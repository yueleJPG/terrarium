namespace Terrarium.Core;

/// <summary>渲染用的生物数据（值拷贝）。器官等级用 3 bit/器官 打包，避免每帧分配数组。</summary>
/// <summary>
/// 卵的渲染数据。卵固定不动，所以只需要位置 + 外观 + 两项状态：
/// 孵化进度（画成一圈进度）和剩余耐久（决定画几个"裂纹"）。
/// </summary>
public struct RenderEgg
{
    public Vec2 Pos;
    public int SpeciesId;
    public float SpeciesHue;
    /// <summary>孵化进度 0..1。</summary>
    public float HatchPct;
    /// <summary>还能扛几次踩踏。</summary>
    public int Toughness;
    public int TrampleLimit;
    public float Radius;
}

public struct RenderCreature
{    public int Id;
    public Vec2 Pos;
    public float Heading;
    public float Hue;
    public float SizeScale;
    public int SpeciesId;
    public float EnergyPct;
    public float HealthPct;
    public float Mass;
    /// <summary>身体半径（世界单位）。渲染尺寸与点击判定都用它。</summary>
    public float BodyRadius;
    /// <summary>视野半径，画选中高亮时显示。</summary>
    public float Vision;
    /// <summary>该个体所在的物种在本局中的稳定色相（由物种序号经黄金角展开得到）。</summary>
    public float SpeciesHue;
    /// <summary>10 个器官 × 3 bit 的等级打包。</summary>
    public uint OrganPacked;
    public int LastRule;
    public bool Alive;
    /// <summary>成熟度 0..1。幼体要画小一号 —— 否则玩家看不出生命阶段。</summary>
    public float Maturity;
    /// <summary>已用点数。详情面板和"看它把点数花在哪了"都要用。</summary>
    public int PointsUsed;

    public int OrganLevel(int index) => (int)((OrganPacked >> (index * 3)) & 0x7u);

    public static uint Pack(int[] organs)
    {
        uint v = 0;
        for (int i = 0; i < OrganTable.Count; i++)
            v |= ((uint)Math.Clamp(organs[i], 0, 7)) << (i * 3);
        return v;
    }
}

/// <summary>
/// 某一 tick 的只读世界快照。UI 只读这个，永远不碰 World 本体 ——
/// 这是模拟线程和 UI 线程之间唯一的交界，避免了绝大部分竞态问题。
/// </summary>
public sealed class SimSnapshot
{
    public int Tick;
    public int Population;
    public int SpeciesCount;
    public RenderCreature[] Creatures = Array.Empty<RenderCreature>();
    public int CreatureCount;
    public RenderEgg[] Eggs = Array.Empty<RenderEgg>();
    public int EggCount;
    public float[] Food = Array.Empty<float>();
    /// <summary>地形（肥力 / 地形类型）在一局之内不会变（除非手绘编辑），由 Simulation 缓存共享。</summary>
    public float[] Fertility = Array.Empty<float>();
    public byte[] Terrain = Array.Empty<byte>();
    public int TileW;
    public int TileH;
    public float TileSize;
    public float WorldWidth;
    public float WorldHeight;
    public float Light;
    /// <summary>地形版本号，每次 Reset / 手绘改地形递增，UI 用它判断要不要重建地形缓存。</summary>
    public int TerrainVersion;

    // 领地图层：把每个粗格染上占优物种的颜色，让"哪块地是谁的"一眼可见
    public int[] TerritorySpecies = Array.Empty<int>();
    public int[] TerritoryCount = Array.Empty<int>();
    public int TerritoryW;
    public int TerritoryH;
    public float TerritoryCell;

    public WorldStats Stats = new();
    public bool Extinct;
}

/// <summary>
/// 顶层门面：把「配置 + 世界 + 历史统计 + 快照」缝在一起，
/// 并负责线程安全。UI 和 headless 实验脚本都只跟这个类打交道。
/// </summary>
public sealed class Simulation
{
    private readonly object _sync = new();

    public SimConfig Config { get; private set; }
    public World World { get; private set; }
    public ulong Seed { get; private set; }

    /// <summary>种群曲线等历史数据（按 HistoryInterval 采样）。只在锁内读写。</summary>
    private readonly List<WorldStats> _history = new();
    public int HistoryInterval { get; set; } = 40;
    public int HistoryCapacity { get; set; } = 3000;

    /// <summary>取最近 max 条统计记录（线程安全拷贝）。</summary>
    public List<WorldStats> RecentHistory(int max = 600)
    {
        lock (_sync)
        {
            int start = Math.Max(0, _history.Count - max);
            return _history.GetRange(start, _history.Count - start);
        }
    }

    public int HistoryCount { get { lock (_sync) return _history.Count; } }

    /// <summary>累计演化过的 tick 数（跨多次 Reset 不重置，用于"总共快进了多久"）。</summary>
    public long TotalTicks { get; private set; }

    private float[]? _cachedFertility;
    private byte[]? _cachedTerrain;
    private int _terrainVersion;

    public Simulation(SimConfig? config = null, ulong seed = 12345)
    {
        Config = config ?? new SimConfig();
        Seed = seed;
        World = new World(Config, seed);
        World.SpawnInitialPopulation(Config.InitialPopulation);
        RecordHistory();
    }

    /// <summary>重建世界（换种子 / 换生态箱尺寸）。</summary>
    public void Reset(ulong seed, int? initialPopulation = null)
    {
        lock (_sync)
        {
            Seed = seed;
            World = new World(Config, seed);
            World.SpawnInitialPopulation(initialPopulation ?? Config.InitialPopulation);
            _cachedFertility = null;
            _cachedTerrain = null;
            _terrainVersion++;
            _history.Clear();
            RecordHistory();
        }
    }

    /// <summary>清空所有生物，只留下空生态箱（给"从零造物"用）。</summary>
    public void ClearPopulation()
    {
        lock (_sync)
        {
            World.Creatures.Clear();
            RecordHistory();
        }
    }

    public void Step()
    {
        lock (_sync) StepUnlocked();
    }

    private void StepUnlocked()
    {
        World.Step();
        TotalTicks++;
        if (World.Tick % HistoryInterval == 0) RecordHistory();
    }

    /// <summary>
    /// 批量快进。每 tick 单独加锁，这样 UI 线程能在 tick 之间插进来取快照，
    /// 不会因为"快进一万年"而整个界面卡死。
    /// </summary>
    public int RunBatch(int ticks, Func<bool>? shouldStop = null, Action<int>? progress = null)
    {
        int done = 0;
        for (int i = 0; i < ticks; i++)
        {
            lock (_sync)
            {
                StepUnlocked();
                if (World.Extinct) { done++; break; }
            }
            done++;
            if (shouldStop != null && shouldStop()) break;
            if (progress != null && (i & 511) == 0) progress(done);
        }
        return done;
    }

    private void RecordHistory()
    {
        _history.Add(World.ComputeStats());
        if (_history.Count > HistoryCapacity) _history.RemoveRange(0, _history.Count - HistoryCapacity);
    }

    /// <summary>取一份不可变快照给 UI 用。</summary>
    public SimSnapshot Snapshot()
    {
        lock (_sync)
        {
            int n = World.Creatures.Count;

            // 地形在一局之内恒定（除非被手绘改动），克隆一次后复用 —— 每帧克隆上万个 float 纯属浪费。
            // TerrainVersion 变了就重新克隆，手绘地形才能立刻反映到画面上。
            if (_cachedFertility is null || _terrainVersion != World.TerrainVersion)
            {
                _cachedFertility = (float[])World.Fertility.Clone();
                _cachedTerrain = (byte[])World.Terrain.Clone();
                _terrainVersion = World.TerrainVersion;
            }

            var snap = new SimSnapshot
            {
                Tick = World.Tick,
                Population = n,
                Extinct = World.Extinct,
                TileW = World.W,
                TileH = World.H,
                TileSize = Config.TileSize,
                WorldWidth = Config.WorldWidth,
                WorldHeight = Config.WorldHeight,
                Light = World.Light,
                TerrainVersion = World.TerrainVersion,
                Stats = World.ComputeStats(),
                Creatures = new RenderCreature[Math.Max(16, n)],
                CreatureCount = n,
                Food = (float[])World.Food.Clone(),
                Fertility = _cachedFertility,
                Terrain = _cachedTerrain ?? Array.Empty<byte>(),
                // 领地数组必须克隆：模拟线程每 30 tick 会 Array.Clear 重写它，
                // UI 直接共享引用会读到"清空到一半"的状态，画面上就是领地整片闪烁。
                TerritorySpecies = (int[])World.TerritorySpecies.Clone(),
                TerritoryCount = (int[])World.TerritoryCount.Clone(),
                TerritoryW = World.TerritoryW,
                TerritoryH = World.TerritoryH,
                TerritoryCell = World.TerritoryCell,
            };
            snap.SpeciesCount = snap.Stats.SpeciesCount;

            for (int i = 0; i < n; i++)
            {
                Creature c = World.Creatures[i];
                snap.Creatures[i] = new RenderCreature
                {
                    Id = c.Id,
                    Pos = c.Pos,
                    Heading = c.Heading,
                    Hue = c.Hue,
                    SizeScale = c.SizeScale,
                    SpeciesId = c.SpeciesId,
                    EnergyPct = c.EnergyPct,
                    HealthPct = c.HealthPct,
                    Mass = c.Mass,
                    BodyRadius = c.BodyRadius,
                    Vision = c.Vision,
                    SpeciesHue = SpeciesPalette.HueFor(c.SpeciesId),
                    OrganPacked = RenderCreature.Pack(c.G.Organs),
                    LastRule = c.LastRule,
                    Alive = c.Alive,
                    Maturity = c.Maturity,
                    PointsUsed = c.G.PointsUsed,
                };
            }

            // ---- 卵 ----
            var eggList = World.Eggs;
            int en = 0;
            for (int i = 0; i < eggList.Count; i++) if (eggList[i].Alive) en++;
            snap.Eggs = new RenderEgg[Math.Max(8, en)];
            snap.EggCount = en;

            int ei = 0;
            for (int i = 0; i < eggList.Count && ei < en; i++)
            {
                Egg e = eggList[i];
                if (!e.Alive) continue;
                snap.Eggs[ei++] = new RenderEgg
                {
                    Pos = e.Pos,
                    SpeciesId = e.SpeciesId,
                    SpeciesHue = SpeciesPalette.HueFor(e.SpeciesId),
                    HatchPct = e.HatchPct,
                    Toughness = e.Toughness,
                    TrampleLimit = e.TrampleLimit,
                    Radius = 5.2f + MathF.Min(4f, e.Energy * 0.05f),
                };
            }

            return snap;
        }
    }

    /// <summary>取某只生物的完整详情（含规则表和实时传感器读数），供检查面板使用。</summary>
    public CreatureDetail? Detail(int creatureId)
    {
        lock (_sync)
        {
            for (int i = 0; i < World.Creatures.Count; i++)
            {
                Creature c = World.Creatures[i];
                if (c.Id != creatureId) continue;

                var d = new CreatureDetail
                {
                    Id = c.Id,
                    SpeciesId = c.SpeciesId,
                    Generation = c.G.Generation,
                    Age = c.Age,
                    Energy = c.Energy,
                    MaxEnergy = c.MaxEnergy,
                    Hp = c.Hp,
                    MaxHp = c.MaxHp,
                    Mass = c.Mass,
                    MaxSpeed = c.MaxSpeed,
                    Vision = c.Vision,
                    Attack = c.Attack,
                    Armor = c.Armor,
                    Upkeep = c.Upkeep,
                    ReproThreshold = c.ReproThreshold,
                    MutationRate = c.G.MutationRate,
                    RuleMutationRate = c.G.RuleMutationRate,
                    Genome = c.G.Clone(),
                    LastRule = c.LastRule,
                    Intent = c.Intent,
                    Alive = c.Alive,
                    CauseOfDeath = c.CauseOfDeath,
                    Position = c.Pos,
                };

                // 用当前状态重建一次感知，让玩家能看到"它此刻看到了什么"
                var sense = World.SenseForDetail(c);
                d.SensorValues = new float[SensorTable.Count];
                for (int s = 0; s < SensorTable.Count; s++)
                    d.SensorValues[s] = c.ReadSensor((SensorId)s, sense, World.Tick, Config);
                d.RuleMatched = new bool[c.G.Rules.Count];
                if (c.LastRule >= 0 && c.LastRule < d.RuleMatched.Length)
                    d.RuleMatched[c.LastRule] = true;

                return d;
            }
            return null;
        }
    }

    /// <summary>把一只生物从基因库里"复活"到世界里（手动造物 / 读档）。</summary>
    public Creature? Inject(Genome g, Vec2? at = null)
    {
        lock (_sync)
        {
            if (World.Creatures.Count >= Config.MaxPopulation) return null;
            var c = World.AddCustom(g, at);
            RecordHistory();
            return c;
        }
    }

    // ---------------- 地形编辑（都是线程安全的薄封装） ----------------

    /// <summary>用笔刷涂抹地形。立刻生效；逃逸距离场会在 EndStroke 统一重算。</summary>
    public int PaintTerrain(Vec2 center, in TerrainBrush brush)
    {
        lock (_sync) return World.Paint(center, brush);
    }

    public void BeginTerrainStroke()
    {
        lock (_sync) World.BeginStroke();
    }

    public void EndTerrainStroke()
    {
        lock (_sync) World.EndStroke();
    }

    public bool UndoTerrain()
    {
        lock (_sync)
        {
            if (!World.CanUndo) return false;
            World.Undo();
            return true;
        }
    }

    public int TerrainUndoDepth { get { lock (_sync) return World.UndoDepth; } }

    /// <summary>把手绘地形存成 .terrarium 地图文件。</summary>
    public void SaveMap(string path, string? name = null)
    {
        lock (_sync) MapIO.Save(World, path, name);
    }

    /// <summary>载入地图。尺寸不符会先把生态箱重建到地图的尺寸。</summary>
    public bool LoadMap(string path)
    {
        var map = MapIO.Load(path);
        if (map is null) return false;

        lock (_sync)
        {
            if (map.W != World.W || map.H != World.H)
            {
                // 尺寸对不上：按地图的瓦片尺寸重建世界，再把地形覆盖上去
                Config.TileSize = map.TileSize;
                Config.WorldWidth = map.WorldWidth;
                Config.WorldHeight = map.WorldHeight;
                World = new World(Config, Seed);
                _cachedFertility = null;
                _cachedTerrain = null;
                _terrainVersion = -1;
            }

            if (!MapIO.Apply(World, map)) return false;
            RecordHistory();
            return true;
        }
    }

    /// <summary>按预设生物群系重新生成地图。会清空撤销历史。</summary>
    public void RegenerateBiome(BiomePreset preset, ulong seed)
    {
        lock (_sync)
        {
            World.Regenerate(preset, seed);
            RecordHistory();
        }
    }

    /// <summary>把编辑好的基因组写回一只活着的生物（规则编辑器保存时调用）。</summary>
    public bool ReplaceGenome(int creatureId, Genome g)
    {
        lock (_sync)
        {
            foreach (var c in World.Creatures)
            {
                if (c.Id != creatureId) continue;
                c.G = g.Clone();
                c.SpeciesId = c.G.SpeciesId;
                // 器官变了，派生属性（速度/视野/代谢/血量上限）必须立刻重算，
                // 否则玩家会看到"改了器官但属性没变"的鬼影。
                c.RecalcDerived(Config);
                c.Hp = MathF.Min(c.Hp, c.MaxHp);
                c.Energy = MathF.Min(c.Energy, c.MaxEnergy);
                return true;
            }
            return false;
        }
    }

    /// <summary>
    /// 重算所有生物的派生属性。
    /// 环境面板改了器官开销 / 代谢之类的参数后必须调用，否则玩家会看到
    /// "拖了滑块但生物属性没变" —— 因为派生属性是缓存的，只在出生和变异时算一次。
    /// </summary>
    public void RefreshDerived()
    {
        lock (_sync)
        {
            foreach (var c in World.Creatures) c.RecalcDerived(Config);
        }
    }

    /// <summary>
    /// 物种清单（含历史图鉴）。
    ///
    /// 每个条目带两个关键信息：一是物种的**行为独特性**（与其它物种的平均指纹距离），
    /// 二是**与它差异最大的那个物种**。有了这两个数，"物种之间有没有区分"
    /// 就从一句主观感受变成了可以看的数字。
    /// </summary>
    public List<SpeciesSummary> SpeciesSummaries(bool includeExtinct = false, int maxExtinct = 40)
    {
        lock (_sync)
        {
            var reg = World.Species;
            var live = new Dictionary<int, SpeciesSummary>();
            var agg = new Dictionary<int, (float mass, float rules, float cann, float[] organs)>();

            foreach (var c in World.Creatures)
            {
                if (!agg.TryGetValue(c.SpeciesId, out var a))
                {
                    a = (0f, 0f, 0f, new float[OrganTable.Count]);
                    agg[c.SpeciesId] = a;
                }
                a.mass += c.Mass;
                a.rules += c.G.Rules.Count;
                a.cann += c.G.Cannibalism;
                for (int i = 0; i < OrganTable.Count; i++) a.organs[i] += c.G.Organs[i];
                agg[c.SpeciesId] = a;
            }

            foreach (var rec in reg.All)
            {
                if (!includeExtinct && !rec.Alive && rec.Population == 0) continue;
                if (rec.FirstTick == 0 && !rec.Alive && rec.PeakPopulation == 0 && rec.Id > 100) continue;

                var s = new SpeciesSummary
                {
                    Id = rec.Id,
                    Name = rec.Name,
                    Hue = rec.Hue,
                    Population = rec.Population,
                    PeakPopulation = rec.PeakPopulation,
                    FirstTick = rec.FirstTick,
                    ExtinctTick = rec.ExtinctTick,
                    LifetimeTicks = rec.LifetimeTicks(World.Tick),
                    Generation = rec.Generation,
                    ParentSpeciesId = rec.ParentSpeciesId,
                    TotalBirths = rec.TotalBirths,
                    Alive = rec.Alive,
                    OrganAverage = new float[OrganTable.Count],
                };

                if (agg.TryGetValue(rec.Id, out var a) && rec.Population > 0)
                {
                    float inv = 1f / rec.Population;
                    s.AverageMass = a.mass * inv;
                    s.AverageRules = a.rules * inv;
                    s.AverageCannibalism = a.cann * inv;
                    for (int i = 0; i < OrganTable.Count; i++) s.OrganAverage[i] = a.organs[i] * inv;
                }
                live[rec.Id] = s;
            }

            // 差异度：和其他存活物种的指纹距离
            var list = live.Values.ToList();
            var aliveRecs = list.Where(s => s.Population > 0)
                                .Select(s => reg.Get(s.Id)).Where(r => r is not null).ToList();

            foreach (var s in list)
            {
                var me = reg.Get(s.Id);
                if (me is null || s.Population == 0) continue;

                float sum = 0f; int n = 0;
                float best = -1f; int bestId = -1;
                foreach (var other in aliveRecs)
                {
                    if (other!.Id == me.Id) continue;
                    float d = reg.DistanceBetween(me, other);
                    sum += d; n++;
                    if (d > best) { best = d; bestId = other.Id; }
                }
                s.Distinctiveness = n > 0 ? sum / n : 0f;
                s.MostDifferentId = bestId;
                s.MostDifferentDistance = MathF.Max(0f, best);
            }

            // 存活优先、按人口降序；历史图鉴按出现顺序倒序补在后面
            return list.OrderByDescending(s => s.Population > 0)
                       .ThenByDescending(s => s.Population)
                       .ThenByDescending(s => s.FirstTick)
                       .Take(200)
                       .ToList();
        }
    }

    /// <summary>
    /// 物种差异矩阵：存活物种两两之间的行为指纹距离。
    /// 返回 (ids, names, matrix)，matrix[i*n+j] 是 i 与 j 的差异度（对角线为 0）。
    /// </summary>
    public (int[] Ids, string[] Names, float[] Matrix) SpeciesDistanceMatrix(int maxSpecies = 10)
    {
        lock (_sync)
        {
            var reg = World.Species;
            var recs = reg.Alive.OrderByDescending(r => r.Population).Take(maxSpecies).ToList();
            int n = recs.Count;
            var ids = new int[n];
            var names = new string[n];
            var m = new float[n * n];
            for (int i = 0; i < n; i++)
            {
                ids[i] = recs[i].Id;
                names[i] = recs[i].Name;
                for (int j = 0; j < n; j++)
                    m[i * n + j] = i == j ? 0f : reg.DistanceBetween(recs[i], recs[j]);
            }
            return (ids, names, m);
        }
    }

    /// <summary>
    /// 卵生统计：场上卵数、累计踩碎 / 被吃 / 孵化、踩踏次数、平均剩余耐久。
    ///
    /// 平均剩余耐久（AvgToughness）是判断「卵壳强度」这个性状有没有被演化利用的指标：
    /// 如果它长期贴近 0，说明全场的卵都是一踩就碎，卵生策略在裸奔。
    /// </summary>
    public (int Eggs, long Crushed, long Eaten, long Hatches, long Tramples, float AvgToughness) EggStats()
    {
        lock (_sync)
        {
            var eggs = World.Eggs;
            float tough = 0f;
            int n = 0;
            for (int i = 0; i < eggs.Count; i++)
                if (eggs[i].Alive) { tough += eggs[i].Toughness; n++; }
            return (n, World.EggsCrushed, World.EggsEaten, World.Hatches, World.Tramples,
                    n > 0 ? tough / n : 0f);
        }
    }

    /// <summary>
    /// 物种与亲缘结构的总体统计。
    ///
    /// <c>AvgSignatureDistance</c> 取的是**个体两两之间**的行为签名距离（采样前 40 只的全部配对），
    /// 而不是物种之间的 —— 因为决定捕食会不会停摆的正是前者：
    /// 亲缘容忍度是拿个体签名去比的，如果个体之间平均只差 5 bit，
    /// 那容忍度设到 5 以上就等于"谁都算同类"，捕食会彻底消失。
    /// </summary>
    public (int Alive, int TotalEver, float AvgSignatureDistance, float AvgCannibalism, float SpeciesDistance)
        SpeciesStats()
    {
        lock (_sync)
        {
            var reg = World.Species;
            var list = World.Creatures;

            int sample = Math.Min(list.Count, 40);
            long sum = 0;
            int pairs = 0;
            for (int i = 0; i < sample; i++)
                for (int j = i + 1; j < sample; j++)
                {
                    sum += GenomeFingerprint.SignatureDistance(list[i].Signature, list[j].Signature);
                    pairs++;
                }

            float cann = 0f;
            for (int i = 0; i < list.Count; i++) cann += list[i].G.Cannibalism;

            // 物种之间的平均差异 —— 这才是"物种到底有没有区分"的量化指标
            var alive = reg.Alive;
            float spSum = 0f; int spN = 0;
            for (int i = 0; i < alive.Count; i++)
                for (int j = i + 1; j < alive.Count; j++)
                {
                    spSum += reg.DistanceBetween(alive[i], alive[j]);
                    spN++;
                }

            return (reg.AliveCount, reg.TotalEver,
                    pairs > 0 ? sum / (float)pairs : 0f,
                    list.Count > 0 ? cann / list.Count : 0f,
                    spN > 0 ? spSum / spN : 0f);
        }
    }

    /// <summary>导出当前最成功物种的代表基因组。</summary>
    public Genome? ChampionGenome()
    {
        lock (_sync)
        {
            if (World.Creatures.Count == 0) return null;
            return World.Creatures
                .GroupBy(c => c.SpeciesId)
                .OrderByDescending(g => g.Count())
                .First()
                .OrderByDescending(c => c.G.Generation)
                .First().G.Clone();
        }
    }
}

/// <summary>检查面板需要的全部信息。</summary>
public sealed class CreatureDetail
{
    public int Id;
    public int SpeciesId;
    public int Generation;
    public float Age;
    public float Energy;
    public float MaxEnergy;
    public float Hp;
    public float MaxHp;
    public float Mass;
    public float MaxSpeed;
    public float Vision;
    public float Attack;
    public float Armor;
    public float Upkeep;
    public float ReproThreshold;
    public float MutationRate;
    public float RuleMutationRate;
    public Genome Genome = new();
    public int LastRule;
    public ActionId Intent;
    public bool Alive;
    public string CauseOfDeath = "";
    public Vec2 Position;
    public float[] SensorValues = Array.Empty<float>();
    public bool[] RuleMatched = Array.Empty<bool>();
}
