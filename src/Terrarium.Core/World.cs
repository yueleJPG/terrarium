namespace Terrarium.Core;

/// <summary>
/// 生态箱本身：地形、食物、生物容器、空间索引，以及 tick 主循环。
///
/// 设计要点：
///  - 完全无 UI 依赖 —— 可以 headless 跑 1000 倍速做演化实验。
///  - 完全确定性 —— 同种子 + 同 tick 数 = 同一个世界，存档能原样复现。
///  - 单线程 —— World 只允许被一个线程驱动，UI 通过快照读取。
/// </summary>
public sealed class World
{
    public SimConfig Cfg { get; }
    public Rng Rng { get; }

    public int W { get; private set; }          // 宽（tile 数）
    public int H { get; private set; }          // 高（tile 数）
    public float[] Food { get; private set; } = Array.Empty<float>();
    /// <summary>每个地块的食物容量上限（由地形基准 × 斑块噪声决定）。</summary>
    public float[] Fertility { get; private set; } = Array.Empty<float>();
    /// <summary>每个地块的地形类型。</summary>
    public byte[] Terrain { get; private set; } = Array.Empty<byte>();

    /// <summary>地形改动计数。UI 靠它判断地形缓存是否失效（手绘地形时必须自增）。</summary>
    public int TerrainVersion { get; private set; }

    /// <summary>逃逸距离场：每个不可通行地块指向"最近的可通行方向"，用于防止生物卡死。</summary>
    public sbyte[] EscapeDirX { get; private set; } = Array.Empty<sbyte>();
    public sbyte[] EscapeDirY { get; private set; } = Array.Empty<sbyte>();
    /// <summary>到最近可通行地块的步数（-1 表示本身就是可通行的）。</summary>
    public short[] EscapeDist { get; private set; } = Array.Empty<short>();

    public List<Creature> Creatures { get; } = new();

    /// <summary>
    /// 场上的卵。它是这个生态里第一种**固定不动、不会反击**的资源 ——
    /// 于是"抢劫卵"成了一个不需要搏斗就能吃饱的生态位，
    /// 而"护卵"则成了群居策略第一个真正有意义的理由。
    /// </summary>
    public List<Egg> Eggs { get; } = new();
    public int NextEggId { get; private set; } = 1;

    public int Tick { get; private set; }
    public int NextCreatureId { get; private set; } = 1;
    public int NextSpeciesId { get; private set; } = 1;

    /// <summary>累计寻路统计：真正做了多少次、被预算拒绝多少次、展开多少节点。</summary>
    public long PathCalls, PathDenied, PathNodes;

    // ---- 卵生统计 ----
    /// <summary>累计踩踏次数（含攻击卵）。</summary>
    public long Tramples;
    /// <summary>被踩碎 / 被吃掉的卵。</summary>
    public long EggsCrushed, EggsEaten;
    /// <summary>成功孵化的卵。</summary>
    public long Hatches;

    /// <summary>物种登记册：当前存活的 + 历史图鉴。</summary>
    public SpeciesRegistry Species { get; } = new();

    /// <summary>指纹计算用的复用缓冲，避免每次繁殖都分配一个 117 维数组。</summary>
    private float[] _fpScratch = Array.Empty<float>();

    /// <summary>
    /// 突变步长（父代 vs 子代指纹距离）的滑动平均。物种分化阈值以它为基准缩放，
    /// 从而自动适应变异强度与基因组复杂度。
    /// </summary>
    private float _mutationStepEma;

    /// <summary>
    /// 两个生物算不算同类（因而不互相捕食）。
    ///
    /// 用的是观察者自己的容忍度：行为签名差多少 bit 以内还算同类。
    /// 这是一次 XOR + popcount，O(1) —— 每 tick 对每对邻居做都完全承受得起，
    /// 而这正是捕食判定最需要的东西。
    /// </summary>
    public static bool IsKin(in Creature observer, in Creature other)
        => GenomeFingerprint.SignatureDistance(observer.Signature, other.Signature) <= observer.KinThresholdBits;
    public bool Extinct { get; private set; }

    /// <summary>本 tick 出生 / 死亡 / 猎杀计数。</summary>
    public int Births, Deaths, Kills, Starvations, OldAges;

    /// <summary>从世界建立至今的累计计数 —— 判断"捕食到底有没有发生"必须看累计值。</summary>
    public long TotalBirths, TotalDeaths, TotalKills, TotalStarvations, TotalOldAges;

    // ---------------- 空间哈希网格 ----------------
    private int _cw, _ch;
    private float _cell;
    private int[] _head = Array.Empty<int>();
    private int[] _next = Array.Empty<int>();
    // 卵自己的空间哈希。用同一套网格参数，但独立的链表头 ——
    // 混进生物那条链会让"索引 i 到底是生物还是卵"变成一堆隐式约定。
    private int[] _eggHead = Array.Empty<int>();
    private int[] _eggNext = Array.Empty<int>();
    private SenseData[] _senseBuf = Array.Empty<SenseData>();
    private int[] _ruleBuf = Array.Empty<int>();
    private ParallelOptions? _parallelOptions;
    private readonly PathFinder _pathFinder;
    private readonly List<Creature> _newborns = new();
    private readonly List<int> _dead = new();

    public World(SimConfig cfg, ulong seed)
    {
        Cfg = cfg;
        Rng = new Rng(seed);
        GenerateTerrain(seed);
        BuildGrid();
        _pathFinder = new PathFinder(this);
    }

    // ==================================================================
    // 地形生成
    // ==================================================================

    private void GenerateTerrain(ulong seed)
    {
        W = Math.Max(8, (int)MathF.Round(Cfg.WorldWidth / Cfg.TileSize));
        H = Math.Max(8, (int)MathF.Round(Cfg.WorldHeight / Cfg.TileSize));
        int n = W * H;

        Food = new float[n];
        Fertility = new float[n];
        Terrain = new byte[n];
        EscapeDirX = new sbyte[n];
        EscapeDirY = new sbyte[n];
        EscapeDist = new short[n];

        // 三个噪声：
        //   高程 —— 低频，决定大块地理（山脉 / 湖泊 / 平原）
        //   湿度 —— 中低频，决定植被带（森林 / 沼泽 / 荒漠）
        //   斑块 —— 高频，决定局部食物富集点
        // 空间异质性是关键：均质世界里竞争排斥必然收敛到单一物种。
        var elev = new float[n];
        var moist = new float[n];
        var patch = new float[n];
        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < W; x++)
            {
                float wx = x * Cfg.TileSize;
                float wy = y * Cfg.TileSize;
                int i = y * W + x;
                elev[i] = Fbm(wx * 0.0016f, wy * 0.0016f, 4, seed * 2654435761UL + 17UL);
                moist[i] = Fbm(wx * 0.0027f, wy * 0.0027f, 3, seed * 40503UL + 91UL);
                patch[i] = Fbm(wx * 0.0085f, wy * 0.0085f, 2, seed * 7919UL + 233UL);
            }
        }

        // 全部用分位数取阈值：这样"水域占比 / 山地占比 / 森林占比"是精确可控的，
        // 不受噪声分布形状的影响（凭直觉设绝对阈值是调不准的）。
        float seaLevel = Quantile(elev, Clamp01(Cfg.WaterFraction));
        float rockLevel = Quantile(elev, 1f - Clamp01(Cfg.MountainFraction));
        float swampMoist = Quantile(moist, 1f - Clamp01(Cfg.SwampFraction));
        float bushMoist = Quantile(moist, 1f - Clamp01(Cfg.SwampFraction + Cfg.ForestFraction));
        float desertMoist = Quantile(moist, Clamp01(Cfg.DesertFraction));
        float fertilePatch = Quantile(patch, 1f - Clamp01(Cfg.FertileFraction));
        // 海平面往下 0.035 以内算浅水，更深算深水。
        // "浅水可涉水 / 深水需鳃"这个梯度很重要：浅水是水陆之间的走廊，
        // 深水才是屏障。全是屏障的水域只是一个惩罚区，不是栖息地。
        float deepLevel = seaLevel - 0.035f;

        var counts = new int[TerrainTable.Count];

        for (int i = 0; i < n; i++)
        {
            float e = elev[i], m = moist[i], p = patch[i];
            TerrainType t;

            if (Cfg.WaterEnabled && e < seaLevel)
            {
                t = e < deepLevel ? TerrainType.DeepWater : TerrainType.ShallowWater;
            }
            else if (e > rockLevel)
            {
                t = TerrainType.Rock;
            }
            else if (m > swampMoist && e < seaLevel + 0.08f)
            {
                t = TerrainType.Swamp;
            }
            else if (m > bushMoist)
            {
                t = TerrainType.Bush;
            }
            else if (m < desertMoist)
            {
                t = TerrainType.Desert;
            }
            else if (p > fertilePatch)
            {
                t = TerrainType.Fertile;
            }
            else
            {
                t = TerrainType.Grass;
            }

            Terrain[i] = (byte)t;
            counts[(int)t]++;

            // 食物容量 = 地形基准 × 局部斑块调制。
            // 水域再乘一个"水生植物丰饶度"，让它成为独立生态位而不是惩罚区。
            float cap = TerrainTable.FoodCapacity[(int)t] * (0.55f + 0.45f * p);
            if (TerrainTable.IsWater(t)) cap *= Cfg.AquaticRichness;
            Fertility[i] = Math.Clamp(cap, 0f, 1.2f);
            Food[i] = Fertility[i] * 0.55f;
        }

        LastTerrainCounts = counts;
        RebuildEscapeField();
        TerrainVersion++;
    }

    /// <summary>上一次地形生成/编辑后的各类地形地块数，用于日志和调参。</summary>
    public int[] LastTerrainCounts { get; private set; } = new int[TerrainTable.Count];

    public float TerrainFraction(TerrainType t)
        => W * H == 0 ? 0f : LastTerrainCounts[(int)t] / (float)(W * H);

    // ==================================================================
    // 领地统计
    //
    // 存在的理由很直接：生物外观再像，"哪块地是谁的"也是肉眼分不清的。
    // 把每个粗格染上占优物种的颜色，物种边界就一眼可见 ——
    // 这是让"物种之间有区分"变得可见的最有效手段。
    // ==================================================================

    /// <summary>领地图层的格子边长（世界单位）。</summary>
    public const float TerritoryCell = 128f;

    public int TerritoryW { get; private set; }
    public int TerritoryH { get; private set; }
    /// <summary>每格占优物种 Id（0 = 无主）。</summary>
    public int[] TerritorySpecies { get; private set; } = Array.Empty<int>();
    /// <summary>占优物种在该格的个体数，用来决定染色深浅。</summary>
    public int[] TerritoryCount { get; private set; } = Array.Empty<int>();

    private readonly Dictionary<long, int> _territoryScratch = new();

    public void UpdateTerritory()
    {
        TerritoryW = Math.Max(1, (int)MathF.Ceiling(Cfg.WorldWidth / TerritoryCell));
        TerritoryH = Math.Max(1, (int)MathF.Ceiling(Cfg.WorldHeight / TerritoryCell));
        int cells = TerritoryW * TerritoryH;
        if (TerritorySpecies.Length != cells)
        {
            TerritorySpecies = new int[cells];
            TerritoryCount = new int[cells];
        }
        Array.Clear(TerritorySpecies);
        Array.Clear(TerritoryCount);

        _territoryScratch.Clear();
        for (int i = 0; i < Creatures.Count; i++)
        {
            Creature c = Creatures[i];
            if (!c.Alive) continue;
            int tx = (int)Math.Clamp(c.Pos.X / TerritoryCell, 0, TerritoryW - 1);
            int ty = (int)Math.Clamp(c.Pos.Y / TerritoryCell, 0, TerritoryH - 1);
            long key = ((long)(ty * TerritoryW + tx) << 32) | (uint)c.SpeciesId;
            _territoryScratch.TryGetValue(key, out int n);
            _territoryScratch[key] = n + 1;
        }

        foreach (var kv in _territoryScratch)
        {
            int cell = (int)(kv.Key >> 32);
            int species = (int)(kv.Key & 0xFFFFFFFF);
            if (kv.Value > TerritoryCount[cell])
            {
                TerritoryCount[cell] = kv.Value;
                TerritorySpecies[cell] = species;
            }
        }
    }

    private static float Clamp01(float v) => Math.Clamp(v, 0f, 0.95f);

    // ==================================================================
    // 地形编辑
    //
    // 撤销用"地块差异栈"而不是整图快照：一笔画过去可能只改了 200 个地块，
    // 存整张图（上万个 float）纯属浪费。差异栈还能精确回放。
    // ==================================================================

    private readonly Stack<List<(int Index, byte Terrain, float Fertility, float Food)>> _undoStack = new();
    private List<(int Index, byte Terrain, float Fertility, float Food)>? _activeStroke;

    public bool CanUndo => _undoStack.Count > 0;
    public int UndoDepth => _undoStack.Count;

    /// <summary>笔画开始。一次拖拽 = 一笔 = 一次撤销单位。</summary>
    public void BeginStroke()
    {
        _activeStroke ??= new List<(int, byte, float, float)>(512);
    }

    /// <summary>笔画结束：统一重算逃逸距离场并刷新版本号。</summary>
    public void EndStroke()
    {
        if (_activeStroke is null) return;
        if (_activeStroke.Count > 0) _undoStack.Push(_activeStroke);
        _activeStroke = null;
        RebuildEscapeField();
        RecountTerrain();
        TerrainVersion++;
    }

    public void Undo()
    {
        if (_undoStack.Count == 0) return;
        var stroke = _undoStack.Pop();
        // 倒序回放，保证同一地块被多次涂改时回到最初的值
        for (int i = stroke.Count - 1; i >= 0; i--)
        {
            var d = stroke[i];
            Terrain[d.Index] = d.Terrain;
            Fertility[d.Index] = d.Fertility;
            Food[d.Index] = d.Food;
        }
        RebuildEscapeField();
        RecountTerrain();
        TerrainVersion++;
    }

    /// <summary>
    /// 用笔刷涂抹一圈地块。立刻改 Terrain/Fertility，
    /// 但逃逸距离场与版本号留到 EndStroke 统一更新 —— 一次拖拽里每帧重算 BFS 是纯浪费。
    /// </summary>
    public int Paint(Vec2 center, in TerrainBrush brush)
    {
        BeginStroke();
        int changed = 0;
        float r2 = brush.Radius * brush.Radius;

        int tx0 = (int)Math.Clamp((center.X - brush.Radius) / Cfg.TileSize, 0, W - 1);
        int tx1 = (int)Math.Clamp((center.X + brush.Radius) / Cfg.TileSize, 0, W - 1);
        int ty0 = (int)Math.Clamp((center.Y - brush.Radius) / Cfg.TileSize, 0, H - 1);
        int ty1 = (int)Math.Clamp((center.Y + brush.Radius) / Cfg.TileSize, 0, H - 1);

        for (int ty = ty0; ty <= ty1; ty++)
        {
            for (int tx = tx0; tx <= tx1; tx++)
            {
                float cx = (tx + 0.5f) * Cfg.TileSize;
                float cy = (ty + 0.5f) * Cfg.TileSize;
                float dx = cx - center.X, dy = cy - center.Y;
                if (dx * dx + dy * dy > r2) continue;

                int idx = ty * W + tx;
                RecordTile(idx);

                if (brush.Type.HasValue)
                {
                    var t = brush.Type.Value;
                    Terrain[idx] = (byte)t;
                    // 换地形时把肥力重置成该地形的容量基准，否则会出现"岩石上长满草"
                    float patch = 0.55f + 0.45f * PatchNoise(tx, ty);
                    float cap = TerrainTable.FoodCapacity[(int)t] * patch;
                    if (TerrainTable.IsWater(t)) cap *= Cfg.AquaticRichness;
                    Fertility[idx] = Math.Clamp(cap, 0f, 1.2f);
                    if (Food[idx] > Fertility[idx]) Food[idx] = Fertility[idx];
                }

                if (brush.FertilityDelta != 0f)
                {
                    float cap = Math.Clamp(Fertility[idx] + brush.FertilityDelta * 0.25f, 0f, 1.2f);
                    Fertility[idx] = cap;
                    Food[idx] = MathF.Min(Food[idx] + MathF.Max(0f, brush.FertilityDelta) * 0.15f, cap);
                }

                changed++;
            }
        }
        return changed;
    }

    private void RecordTile(int idx)
    {
        if (_activeStroke is null) return;
        // 同一笔里同一地块只记第一次，撤销时才回到笔前状态
        int from = Math.Max(0, _activeStroke.Count - 48);
        for (int i = _activeStroke.Count - 1; i >= from; i--)
            if (_activeStroke[i].Index == idx) return;

        _activeStroke.Add((idx, Terrain[idx], Fertility[idx], Food[idx]));
    }

    /// <summary>小尺度斑块噪声：地形编辑后肥力仍然有自然的疏密变化。</summary>
    private float PatchNoise(int tx, int ty) => ValueNoise(tx * 0.35f, ty * 0.35f, 0x5EEDu);

    private void RecountTerrain()
    {
        var counts = new int[TerrainTable.Count];
        for (int i = 0; i < Terrain.Length; i++) counts[Terrain[i]]++;
        LastTerrainCounts = counts;
    }

    /// <summary>用预设生物群系重新生成整张地图。种子相同则结果相同。</summary>
    public void Regenerate(BiomePreset preset, ulong seed)
    {
        BiomeTable.Apply(preset, Cfg);
        _undoStack.Clear();
        GenerateTerrain(seed);
    }

    /// <summary>载入地图之后的收尾：重算逃逸距离场、刷新计数与缓存版本号、清空撤销历史。</summary>
    public void OnMapInstalled()
    {
        _undoStack.Clear();
        RebuildEscapeField();
        RecountTerrain();
        TerrainVersion++;
        UpdateTerritory();

        // 地形换了之后旧的绕行路径全部作废，否则生物会沿着已经不存在的路走
        for (int i = 0; i < Creatures.Count; i++)
        {
            Creatures[i].PathCount = 0;
            Creatures[i].PathIndex = 0;
            Creatures[i].PathTimer = 0;
        }
    }

    /// <summary>
    /// 重算逃逸距离场：从所有可通行地块出发做一次多源 BFS，
    /// 于是每个被岩石包围的位置都能查表得到"往哪走能出去"。
    ///
    /// 这是"不做逐个体 A* 也能不卡死"的关键：预计算一次 O(地块数)，
    /// 之后每个生物每 tick 只要查一次表。地形改动时才需要重算。
    /// </summary>
    public void RebuildEscapeField()
    {
        int n = W * H;
        if (EscapeDist.Length != n)
        {
            EscapeDist = new short[n];
            EscapeDirX = new sbyte[n];
            EscapeDirY = new sbyte[n];
        }

        var queue = new int[n];
        int head = 0, tail = 0;

        for (int i = 0; i < n; i++)
        {
            EscapeDirX[i] = 0;
            EscapeDirY[i] = 0;
            if (TerrainTable.IsPassable((TerrainType)Terrain[i]))
            {
                EscapeDist[i] = -1;      // 本身就是可通行的
                queue[tail++] = i;
            }
            else
            {
                EscapeDist[i] = short.MaxValue;
            }
        }

        // 8 邻域 BFS：得到"朝最近开阔地"的方向
        int[] dx = { 1, -1, 0, 0, 1, 1, -1, -1 };
        int[] dy = { 0, 0, 1, -1, 1, -1, 1, -1 };

        while (head < tail)
        {
            int cur = queue[head++];
            int cx = cur % W, cy = cur / W;
            short d = EscapeDist[cur];
            if (d < 0) d = 0;

            for (int k = 0; k < 8; k++)
            {
                int nx = cx + dx[k], ny = cy + dy[k];
                if (nx < 0 || ny < 0 || nx >= W || ny >= H) continue;
                int ni = ny * W + nx;
                if (EscapeDist[ni] <= d + 1) continue;

                EscapeDist[ni] = (short)(d + 1);
                // 指向"从邻居走回开阔地"的第一步方向
                EscapeDirX[ni] = (sbyte)(-dx[k]);
                EscapeDirY[ni] = (sbyte)(-dy[k]);
                queue[tail++] = ni;
            }
        }

        // 整张地图全是岩石的极端情况：把距离归零，避免后续溢出
        for (int i = 0; i < n; i++)
            if (EscapeDist[i] == short.MaxValue) EscapeDist[i] = 0;
    }

    private static float Quantile(float[] data, float q)
    {
        var copy = (float[])data.Clone();
        Array.Sort(copy);
        int idx = (int)Math.Clamp(MathF.Round(q * (copy.Length - 1)), 0, copy.Length - 1);
        return copy[idx];
    }

    private static float Hash2(int x, int y, ulong seed)
    {
        ulong h = seed;
        h ^= (ulong)(uint)x * 0x9E3779B97F4A7C15UL;
        h ^= (ulong)(uint)y * 0xC2B2AE3D27D4EB4FUL;
        h ^= h >> 29; h *= 0xBF58476D1CE4E5B9UL;
        h ^= h >> 32;
        return (h & 0xFFFFFF) / 16777215f;
    }

    private static float ValueNoise(float x, float y, ulong seed)
    {
        int xi = (int)MathF.Floor(x), yi = (int)MathF.Floor(y);
        float xf = x - xi, yf = y - yi;
        // smoothstep 插值，避免网格状条纹
        float u = xf * xf * (3f - 2f * xf);
        float v = yf * yf * (3f - 2f * yf);

        float a = Hash2(xi, yi, seed);
        float b = Hash2(xi + 1, yi, seed);
        float c = Hash2(xi, yi + 1, seed);
        float d = Hash2(xi + 1, yi + 1, seed);

        return (a * (1 - u) + b * u) * (1 - v) + (c * (1 - u) + d * u) * v;
    }

    private static float Fbm(float x, float y, int octaves, ulong seed)
    {
        float sum = 0f, amp = 0.5f, freq = 1f, norm = 0f;
        for (int i = 0; i < octaves; i++)
        {
            sum += ValueNoise(x * freq, y * freq, seed + (ulong)i * 7919UL) * amp;
            norm += amp;
            amp *= 0.5f;
            freq *= 2.07f;
        }
        return sum / norm;
    }

    // ==================================================================
    // 世界操作
    // ==================================================================

    public void SpawnInitialPopulation(int count)
    {
        int archetypes = Genome.ArchetypeNames.Length;
        Species.Clear();
        _fpScratch = new float[GenomeFingerprint.Dims];
        _mutationStepEma = 0f;
        Species.MaxEntries = Cfg.MaxCodexEntries;

        // 每个生态原型是一个"物种"，而不是每个个体一个物种 ——
        // 否则开局就是 420 个物种、420 个个体，物种面板和捕食判定全都失去意义。
        var bases = new Genome[archetypes];
        for (int a = 0; a < archetypes; a++)
        {
            int sid = NextSpeciesId++;
            bases[a] = Genome.CreateArchetype(a, Rng, sid);
            bases[a].MutationRate = Cfg.InitialMutationRate;
            bases[a].RuleMutationRate = Cfg.InitialRuleMutationRate;
            bases[a].Cannibalism = Cfg.InitialCannibalism;
            bases[a].Signature = GenomeFingerprint.Compute(bases[a], Cfg, null);

            // 给每个原型建一条物种记录，模式标本就是原型本身
            GenomeFingerprint.Compute(bases[a], Cfg, _fpScratch);
            Species.Create(sid, bases[a], bases[a].Signature, _fpScratch, 0, 0, bases[a].Name);
        }

        for (int i = 0; i < count; i++)
        {
            int a = i % archetypes;
            Genome g = bases[a].Clone();
            // 给同物种的个体之间一点初始差异，否则自然选择一开始就无材可选。
            // 只做轻微抖动：不碰规则结构，只微调器官和阈值。
            float mr = g.MutationRate, rr = g.RuleMutationRate;
            g.MutationRate = 0.05f;
            g.RuleMutationRate = 0f;
            Mutator.Mutate(g, Rng, Cfg);
            g.MutationRate = mr;
            g.RuleMutationRate = rr;
            g.Signature = GenomeFingerprint.Compute(g, Cfg, null);
            g.SpeciesId = bases[a].SpeciesId;
            g.Generation = 0;
            Spawn(g, RandomLandPosition());
        }

        Species.RefreshPopulation(Creatures, 0);
    }

    public Vec2 RandomLandPosition()
    {
        for (int attempt = 0; attempt < 60; attempt++)
        {
            var p = new Vec2(Rng.Range(0f, Cfg.WorldWidth), Rng.Range(0f, Cfg.WorldHeight));
            TerrainType t = TerrainAt(p);
            if (TerrainTable.IsPassable(t) && !TerrainTable.DrownsAt(t)) return p;
        }
        // 兜底：全图扫描找一个可站的地方，别把生物塞进岩石里
        for (int i = 0; i < Terrain.Length; i++)
        {
            var t = (TerrainType)Terrain[i];
            if (!TerrainTable.IsPassable(t) || TerrainTable.DrownsAt(t)) continue;
            return new Vec2(((i % W) + 0.5f) * Cfg.TileSize, ((i / W) + 0.5f) * Cfg.TileSize);
        }
        return new Vec2(Cfg.WorldWidth * 0.5f, Cfg.WorldHeight * 0.5f);
    }

    public Creature Spawn(Genome genome, Vec2 pos)
    {
        var c = new Creature
        {
            Id = NextCreatureId++,
            G = genome,
            Pos = pos,
            Heading = Rng.Range(0f, MathF.Tau),
            SpeciesId = genome.SpeciesId,
        };
        // 每个个体都带一份指纹：突变步长的校准要靠它（父代 vs 子代），
        // UI 也要用它显示"这个个体偏离物种模式标本多远"。
        float[] fp = new float[GenomeFingerprint.Dims];
        c.Signature = GenomeFingerprint.Compute(genome, Cfg, fp);
        c.Fingerprint = fp;
        genome.Signature = c.Signature;

        c.RecalcDerived(Cfg);
        c.Energy = c.MaxEnergy * 0.55f;
        c.Hp = c.MaxHp;
        Creatures.Add(c);
        return c;
    }

    public int TileAt(Vec2 p)
    {
        int tx = (int)Math.Clamp(p.X / Cfg.TileSize, 0, W - 1);
        int ty = (int)Math.Clamp(p.Y / Cfg.TileSize, 0, H - 1);
        return ty * W + tx;
    }

    public TerrainType TerrainAt(int tile) => (TerrainType)Terrain[tile];
    public TerrainType TerrainAt(Vec2 p) => TerrainAt(TileAt(p));

    public bool IsPassableAt(Vec2 p) => TerrainTable.IsPassable(TerrainAt(p));
    public bool IsPassableTile(int tile) => TerrainTable.IsPassable(TerrainAt(tile));

    /// <summary>该位置是否是水域（含浅水）。渲染与"我有鳃"类判定用。</summary>
    public bool IsWater(Vec2 p) => TerrainTable.IsWater(TerrainAt(p));

    /// <summary>该位置会不会淹死无鳃生物。只有深水会。</summary>
    public bool DrownsAt(Vec2 p) => TerrainTable.DrownsAt(TerrainAt(p));

    public float FoodAt(Vec2 p) => Food[TileAt(p)];

    /// <summary>把世界坐标钳到最近的可通行位置上（用于把生物从岩石里推出来）。</summary>
    public Vec2 NudgeToPassable(Vec2 p)
    {
        if (IsPassableAt(p)) return p;
        int tile = TileAt(p);
        if (EscapeDist.Length != Terrain.Length) return p;

        int ex = EscapeDirX[tile], ey = EscapeDirY[tile];
        if (ex == 0 && ey == 0) return p;

        int tx = tile % W, ty = tile / W;
        tx = Math.Clamp(tx + ex * 1, 0, W - 1);
        ty = Math.Clamp(ty + ey * 1, 0, H - 1);
        return new Vec2((tx + 0.5f) * Cfg.TileSize, (ty + 0.5f) * Cfg.TileSize);
    }

    /// <summary>昼夜相位：0 = 深夜，1 = 正午。</summary>
    public float Light
    {
        get
        {
            float phase = (Tick % Cfg.DayLength) / (float)Cfg.DayLength;
            return (MathF.Sin(phase * MathF.Tau - MathF.PI * 0.5f) + 1f) * 0.5f;
        }
    }

    // ==================================================================
    // 主循环
    // ==================================================================

    public void Step()
    {
        Births = Deaths = Kills = Starvations = OldAges = 0;
        _newborns.Clear();
        _dead.Clear();

        BuildGrid();
        // 卵必须先处理：踩踏/孵化会**压实 Eggs 列表**，而卵的空间哈希是按旧下标建的。
        // 处理完必须重建一次卵网格，否则后面几千次感知会读到已经失效的下标
        // （实测过：直接崩在 ScanEggs 的 Eggs[node] 上，而且是并行段里十条线程一起崩）。
        TickEggs();
        BuildEggGrid();
        RegrowFood();
        // 寻路预算按 tick 重置：总开销有硬上限，与生物数量无关
        _pathFinder.BeginTick(Cfg.PathfindBudgetPerTick, Cfg.PathfindNodeBudgetPerTick);

        float light = Light;
        int n = Creatures.Count;

        // ------------------------------------------------------------------
        // 阶段一：感知 + 决策（可并行）
        //
        // 这一阶段是纯只读的：Sense 只读世界状态，Think 只读基因组，
        // 它们唯一的写操作是写自己那只生物的 LastRule/Intent 字段。
        // 所以可以按个体切开并行跑，而且结果与串行完全一致 —— 确定性不破。
        // 这是整个模拟里最热的一段（环扫描全在这里），8 核能拿到接近线性的收益。
        // ------------------------------------------------------------------
        if (_senseBuf.Length < n)
        {
            _senseBuf = new SenseData[Math.Max(64, n * 2)];
            _ruleBuf = new int[Math.Max(64, n * 2)];
        }

        var senseBuf = _senseBuf;
        var ruleBuf = _ruleBuf;
        int tick = Tick;                 // 捕获到本地，避免并行体内读到变化的字段
        float ambientLight = light;
        var creatures = Creatures;       // List<T> 在并行期间不会被改动，索引读取是安全的

        if (n >= Cfg.ParallelSenseThreshold)
        {
            // ParallelOptions 缓存成字段：每 tick new 一个会白白增加 GC 压力。
            // MaxDegreeOfParallelism 的合法值是 -1（交给运行时）或 >=1，
            // 传 0 会直接抛 ArgumentOutOfRangeException 把程序干掉。
            _parallelOptions ??= new ParallelOptions
            {
                MaxDegreeOfParallelism = Cfg.MaxParallelism > 0 ? Cfg.MaxParallelism : -1,
            };
            var options = _parallelOptions;

            Parallel.For(0, n, options, i =>
            {
                Creature c = creatures[i];
                if (!c.Alive) { ruleBuf[i] = -1; return; }
                SenseData d = Sense(c, i, ambientLight);
                senseBuf[i] = d;
                ruleBuf[i] = c.Think(d, tick, Cfg);
            });
        }
        else
        {
            // 个体太少时并行的调度开销反而更大，直接串行
            for (int i = 0; i < n; i++)
            {
                Creature c = creatures[i];
                if (!c.Alive) { ruleBuf[i] = -1; continue; }
                SenseData d = Sense(c, i, ambientLight);
                senseBuf[i] = d;
                ruleBuf[i] = c.Think(d, tick, Cfg);
            }
        }

        // ------------------------------------------------------------------
        // 阶段二：执行（必须串行）
        //
        // 动作会修改位置、食物存量、生老病死，全是有副作用的。
        // 轮转起始位置，消除"列表靠前的个体永远先动手"这一系统性偏差。
        // ------------------------------------------------------------------
        int offset = n > 0 ? Tick % n : 0;

        for (int k = 0; k < n; k++)
        {
            int i = (k + offset) % n;
            Creature c = creatures[i];
            if (!c.Alive) continue;

            int ruleIndex = ruleBuf[i];
            if (ruleIndex < 0 || ruleIndex >= c.G.Rules.Count) continue;

            SenseData d = senseBuf[i];
            Rule rule = c.G.Rules[ruleIndex];
            for (int a = 0; a < rule.Then.Count; a++)
            {
                if (!c.Alive) break;
                Execute(c, rule.Then[a], d, i);
            }
        }

        // 顺序有讲究：
        //   代谢必须在碰撞分离之前 —— 否则被挤开的位移会算进下一 tick 的开销判定里，
        //   造成"明明没动却掉能量"的鬼影。
        Metabolize(light);

        // 手绘地形可能把岩石画到生物脚下，必须把它们推出来，否则永久卡死
        UnstickFromTerrain();

        // 生物碰撞体积：互相推开 + 拥挤的代谢代价
        if (Cfg.BodyCollision) SeparateBodies();

        CollectDead();

        for (int i = 0; i < _newborns.Count; i++)
        {
            if (Creatures.Count >= Cfg.MaxPopulation) break;
            Creatures.Add(_newborns[i]);
        }

        Tick++;

        // 领地图层与物种统计不需要每 tick 重算 —— 它们是给人看的视图，不是模拟输入。
        // 每 30 tick 更新一次，成本摊薄到可以忽略。
        if ((Tick % Cfg.SpeciesRefreshInterval) == 0)
        {
            UpdateTerritory();
            Species.RefreshPopulation(Creatures, Tick);
        }

        TotalBirths += Births;
        TotalDeaths += Deaths;
        TotalKills += Kills;
        TotalStarvations += Starvations;
        TotalOldAges += OldAges;

        PathCalls += _pathFinder.BudgetUsed;
        PathDenied += _pathFinder.BudgetDenied;
        PathNodes += _pathFinder.NodesUsed;

        if (Creatures.Count == 0) Extinct = true;
    }

    /// <summary>跑若干 tick。返回实际执行的 tick 数。</summary>
    public int Run(int ticks, Action<int, World>? onTick = null)
    {
        int done = 0;
        for (int t = 0; t < ticks; t++)
        {
            Step();
            done++;
            if (Extinct) break;
            if (onTick != null && (t & 127) == 0) onTick(Tick, this);
        }
        return done;
    }

    private void RegrowFood()
    {
        float rate = Cfg.FoodRegen * Cfg.FoodRichness;
        float[] food = Food;
        float[] fert = Fertility;
        for (int i = 0; i < food.Length; i++)
        {
            float cap = fert[i];
            if (cap <= 0.001f) continue;
            float f = food[i];
            if (f >= cap) continue;
            // logistic 再生：空地块长得快，接近上限时变慢 —— 形成"斑块被吃秃再慢慢恢复"
            food[i] = f + rate * (0.25f + f) * (1f - f / cap);
        }
    }

    // ==================================================================
    // 感知
    // ==================================================================

    private SenseData Sense(Creature c, int selfIndex, float light)
    {
        TerrainType here = TerrainAt(TileAt(c.Pos));
        int tile = TileAt(c.Pos);

        var d = new SenseData
        {
            Light = light,
            InWater = TerrainTable.IsWater(here),
            InBush = here == TerrainType.Bush,
            TerrainCost = TerrainTable.MoveCostOf(here),
            // 地形遮蔽：灌木丛里视野砍半。这条让"伏击"和"藏身"成为可行的生态位，
            // 而且成本极低 —— 只是把有效视野乘一个系数，不需要真正的视线遮挡计算。
            EffectiveVision = c.Vision * TerrainTable.VisionOf(here),
            FoodHere = FoodAt(c.Pos),
            Noise = Hash2(c.Id, Tick, 0xA5A5A5A5UL),
        };

        // 被岩石围困的程度：距离场步数越大说明陷得越深
        short esc = tile < EscapeDist.Length ? EscapeDist[tile] : (short)0;
        d.EscapePressure = esc <= 0 ? 0f : Math.Clamp(esc / 6f, 0f, 1f);

        d.ObstacleAhead = ProbeObstacleAhead(c);

        float margin = 4f;
        d.AtEdge = c.Pos.X < margin || c.Pos.Y < margin
                || c.Pos.X > Cfg.WorldWidth - margin || c.Pos.Y > Cfg.WorldHeight - margin;

        FindNearestFood(c, ref d);
        ScanCreatures(c, selfIndex, ref d);
        ScanEggs(c, ref d);
        return d;
    }

    /// <summary>
    /// 扫描视野内的卵。
    ///
    /// 单独一趟而不是并进 ScanCreatures：卵和生物存在两个不同的空间哈希里，
    /// 并且卵的敌友判定要靠**它亲代的**物种与签名（亲代可能早就死了），
    /// 混在一起写会让那段本来就很密的邻居循环更难读。
    /// </summary>
    private void ScanEggs(Creature self, ref SenseData d)
    {
        d.EggFound = d.EnemyEggFound = d.AllyEggFound = false;
        d.EggCount = d.EnemyEggCount = 0;
        if (Eggs.Count == 0) return;

        int cx0 = (int)Math.Clamp(self.Pos.X / _cell, 0, _cw - 1);
        int cy0 = (int)Math.Clamp(self.Pos.Y / _cell, 0, _ch - 1);
        int maxRing = (int)MathF.Ceiling(d.EffectiveVision / _cell);
        if (maxRing < 1) maxRing = 1;
        int countRing = (int)MathF.Ceiling(120f / _cell);

        float visionSq = d.EffectiveVision * d.EffectiveVision;
        float bestEgg = float.MaxValue, bestEnemyEgg = float.MaxValue, bestAllyEgg = float.MaxValue;

        for (int ring = 0; ring <= maxRing; ring++)
        {
            for (int dy = -ring; dy <= ring; dy++)
            {
                int cy = cy0 + dy;
                if (cy < 0 || cy >= _ch) continue;
                for (int dx = -ring; dx <= ring; dx++)
                {
                    if (ring > 0 && Math.Abs(dx) != ring && Math.Abs(dy) != ring) continue;
                    int cx = cx0 + dx;
                    if (cx < 0 || cx >= _cw) continue;

                    int node = _eggHead[cy * _cw + cx];
                    while (node >= 0)
                    {
                        Egg e = Eggs[node];
                        node = _eggNext[node];
                        if (!e.Alive) continue;

                        float ddx = e.Pos.X - self.Pos.X;
                        float ddy = e.Pos.Y - self.Pos.Y;
                        float dsq = ddx * ddx + ddy * ddy;
                        if (dsq > visionSq) continue;

                        // 敌友看**卵的归属**，不是看现在谁站在旁边
                        bool allyEgg = e.SpeciesId == self.SpeciesId
                                    && GenomeFingerprint.SignatureDistance(self.Signature, e.Signature) <= self.KinThresholdBits;

                        d.EggCount++;
                        if (!allyEgg) d.EnemyEggCount++;

                        if (dsq < bestEgg)
                        {
                            bestEgg = dsq;
                            d.EggFound = true;
                            d.NextEggPos = e.Pos;
                            d.NextEggIndex = node;
                            d.EggDist = MathF.Sqrt(dsq);
                        }
                        if (!allyEgg && dsq < bestEnemyEgg)
                        {
                            bestEnemyEgg = dsq;
                            d.EnemyEggFound = true;
                            d.NextEnemyEggPos = e.Pos;
                            d.NextEnemyEggIndex = node;
                            d.EnemyEggDist = MathF.Sqrt(dsq);
                        }
                        else if (allyEgg && dsq < bestAllyEgg)
                        {
                            bestAllyEgg = dsq;
                            d.AllyEggFound = true;
                            d.NextAllyEggPos = e.Pos;
                            d.NextAllyEggIndex = node;
                            d.AllyEggDist = MathF.Sqrt(dsq);
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// 沿朝向探测最近的不可通行地形，返回归一化距离（1 = 探测距离内畅通）。
    ///
    /// 这是生物"学会避障"的唯一信息来源。没有它，面对岩石生物只有两个选择：
    /// 一头撞上去卡死，或者靠随机转向乱撞。有了它，一条
    /// 「当 前方障碍距离 &lt; 0.3 → 转向 随机方向」的规则就能让演化自己解决问题。
    /// </summary>
    private float ProbeObstacleAhead(Creature c)
    {
        const float probe = 90f;
        const int steps = 5;
        const float stepLen = probe / steps;
        float fx = MathF.Cos(c.Heading), fy = MathF.Sin(c.Heading);

        for (int i = 1; i <= steps; i++)
        {
            float t = stepLen * i;
            float px = c.Pos.X + fx * t;
            float py = c.Pos.Y + fy * t;
            if (px < 0f || py < 0f || px >= Cfg.WorldWidth || py >= Cfg.WorldHeight)
                return t / probe;
            if (!IsPassableAt(new Vec2(px, py)))
                return t / probe;
        }
        return 1f;
    }

    /// <summary>
    /// 只读地为某只生物重建一次感知数据，供 UI 检查面板显示"它此刻看到了什么"。
    /// 严禁修改任何世界状态（包括不要碰 RNG）。
    /// </summary>
    public SenseData SenseForDetail(Creature c)
    {
        int idx = Creatures.IndexOf(c);
        BuildGrid();
        return Sense(c, idx, Light);
    }

    /// <summary>用扩张方环搜索食物格，找到最近一环里的最优格就停下。</summary>
    private void FindNearestFood(Creature c, ref SenseData d)
    {
        // 快速路径：脚下就有食物时，最近的食物就是脚下这一格。
        // 这既符合直觉，又省掉了绝大多数生物每 tick 上百次的内存探测。
        if (d.FoodHere > 0.08f)
        {
            d.FoodFound = true;
            d.NextFoodPos = c.Pos;
            d.FoodDist = 0f;
            d.FoodBearing = 0f;
            return;
        }

        int tx0 = (int)Math.Clamp(c.Pos.X / Cfg.TileSize, 0, W - 1);
        int ty0 = (int)Math.Clamp(c.Pos.Y / Cfg.TileSize, 0, H - 1);
        int maxRing = (int)MathF.Ceiling(d.EffectiveVision / Cfg.TileSize);
        if (maxRing < 1) maxRing = 1;

        float bestScore = -1f;
        float bestDistSq = 0f;
        int bestTile = -1;

        for (int ring = 0; ring <= maxRing; ring++)
        {
            bool any = false;
            for (int dy = -ring; dy <= ring; dy++)
            {
                int yy = ty0 + dy;
                if (yy < 0 || yy >= H) continue;
                int span = ring;
                for (int dx = -span; dx <= span; dx++)
                {
                    // 只扫方环本身，不重复扫内部
                    if (ring > 0 && Math.Abs(dx) != ring && Math.Abs(dy) != ring) continue;
                    int xx = tx0 + dx;
                    if (xx < 0 || xx >= W) continue;

                    int idx = yy * W + xx;
                    float f = Food[idx];
                    if (f < 0.08f) continue;

                    float cx = (xx + 0.5f) * Cfg.TileSize;
                    float cy = (yy + 0.5f) * Cfg.TileSize;
                    float distSq = (cx - c.Pos.X) * (cx - c.Pos.X) + (cy - c.Pos.Y) * (cy - c.Pos.Y);
                    if (distSq > d.EffectiveVision * d.EffectiveVision) continue;

                    any = true;
                    // 近处优先，但食物量多也有加分
                    float score = f / (1f + MathF.Sqrt(distSq) / Cfg.TileSize);
                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestDistSq = distSq;
                        bestTile = idx;
                    }
                }
            }

            // 这一环已经找到目标，不必再往外找（更远的环不可能"更近"）
            if (any && bestTile >= 0) break;
        }

        if (bestTile >= 0)
        {
            int bx = bestTile % W;
            int by = bestTile / W;
            var target = new Vec2((bx + 0.5f) * Cfg.TileSize, (by + 0.5f) * Cfg.TileSize);
            d.FoodFound = true;
            d.NextFoodPos = target;
            d.FoodDist = MathF.Sqrt(bestDistSq);
            d.FoodBearing = Bearing(c.Heading, c.Pos, target);
        }
    }

    /// <summary>用扩张方环扫空间哈希，统计视野内的同族/敌人，并记录最近的一个敌人与同族。</summary>
    private void ScanCreatures(Creature self, int selfIndex, ref SenseData d)
    {
        int cx0 = (int)Math.Clamp(self.Pos.X / _cell, 0, _cw - 1);
        int cy0 = (int)Math.Clamp(self.Pos.Y / _cell, 0, _ch - 1);
        int maxRing = (int)MathF.Ceiling(d.EffectiveVision / _cell);
        if (maxRing < 1) maxRing = 1;

        // "周围密度"类传感器只统计 120 单位内，不必扫满整个视野。
        // 多数个体在几环内就能同时找到敌人和同族，于是可以提前收工。
        int countRing = (int)MathF.Ceiling(120f / _cell);

        float visionSq = d.EffectiveVision * d.EffectiveVision;
        float bestEnemySq = float.MaxValue;
        float bestAllySq = float.MaxValue;
        // 幼体与同物种是这一版新增的三条感知通道，各自独立取最近
        float bestJuvSq = float.MaxValue;
        float bestEnemyJuvSq = float.MaxValue;
        float bestAllyJuvSq = float.MaxValue;
        float bestSameSq = float.MaxValue;

        for (int ring = 0; ring <= maxRing; ring++)
        {
            if (ring > countRing && d.EnemyFound && d.AllyFound) break;

            for (int dy = -ring; dy <= ring; dy++)
            {
                int cy = cy0 + dy;
                if (cy < 0 || cy >= _ch) continue;
                for (int dx = -ring; dx <= ring; dx++)
                {
                    if (ring > 0 && Math.Abs(dx) != ring && Math.Abs(dy) != ring) continue;
                    int cx = cx0 + dx;
                    if (cx < 0 || cx >= _cw) continue;

                    int node = _head[cy * _cw + cx];
                    while (node >= 0)
                    {
                        if (node != selfIndex && node < Creatures.Count)
                        {
                            Creature o = Creatures[node];
                            if (o.Alive)
                            {
                                float ddx = o.Pos.X - self.Pos.X;
                                float ddy = o.Pos.Y - self.Pos.Y;
                                float dsq = ddx * ddx + ddy * ddy;
                                if (dsq <= visionSq)
                                {
                                    // 同族判定用**混合规则**：必须既属于同一物种、行为又足够接近。
                                    //
                                    // 为什么不能只用签名距离：捕食会驱赶整个种群收敛到同一个最优行为，
                                    // 于是签名距离趋近 0，"谁都是同类"→ 捕食停摆 → 世界变得死气沉沉。
                                    // 实测过一次，个体签名距掉到 0.0 bit，猎杀数断崖式下跌。
                                    //
                                    // 加回物种这一条之后：不同物种永远是猎物（鲁棒），
                                    // 而同物种内部是否相残则由可演化的食性宽容度决定（有戏）。
                                    bool ally = o.SpeciesId == self.SpeciesId && IsKin(self, o);
                                    if (ring <= countRing) d.NearbyCount++;

                                    // ---- 幼体：按血量外的"成熟度"分档，因为它们是最划算的猎物 ----
                                    if (o.IsJuvenile)
                                    {
                                        if (ring <= countRing) d.JuvenileCount++;
                                        if (dsq < bestJuvSq)
                                        {
                                            bestJuvSq = dsq;
                                            d.JuvenileFound = true;
                                            d.NextJuvenilePos = o.Pos;
                                            d.JuvenileDist = MathF.Sqrt(dsq);
                                        }
                                        if (!ally && dsq < bestEnemyJuvSq)
                                        {
                                            bestEnemyJuvSq = dsq;
                                            d.EnemyJuvenileFound = true;
                                            d.NextEnemyJuvenilePos = o.Pos;
                                            d.EnemyJuvenileDist = MathF.Sqrt(dsq);
                                        }
                                        else if (ally && dsq < bestAllyJuvSq)
                                        {
                                            bestAllyJuvSq = dsq;
                                            d.AllyJuvenileFound = true;
                                            d.NextAllyJuvenilePos = o.Pos;
                                            d.AllyJuvenileDist = MathF.Sqrt(dsq);
                                        }
                                    }

                                    // ---- 同物种：只看物种 Id，不看行为签名 ----
                                    if (o.SpeciesId == self.SpeciesId && dsq < bestSameSq)
                                    {
                                        bestSameSq = dsq;
                                        d.SameSpeciesFound = true;
                                        d.NextSameSpeciesPos = o.Pos;
                                        d.SameSpeciesDist = MathF.Sqrt(dsq);
                                    }

                                    if (ally)
                                    {
                                        if (ring <= countRing) d.AllyCount++;
                                        if (dsq < bestAllySq)
                                        {
                                            bestAllySq = dsq;
                                            d.AllyFound = true;
                                            d.NextAllyPos = o.Pos;
                                            d.AllyDist = MathF.Sqrt(dsq);
                                            d.AllyBearing = Bearing(self.Heading, self.Pos, o.Pos);
                                        }
                                    }
                                    else
                                    {
                                        if (ring <= countRing) d.EnemyCount++;
                                        if (dsq < bestEnemySq)
                                        {
                                            bestEnemySq = dsq;
                                            d.EnemyFound = true;
                                            d.NextEnemyPos = o.Pos;
                                            d.NextEnemyIndex = node;
                                            d.EnemyDist = MathF.Sqrt(dsq);
                                            d.EnemyBearing = Bearing(self.Heading, self.Pos, o.Pos);
                                        }
                                    }
                                }
                            }
                        }
                        node = _next[node];
                    }
                }
            }
        }
    }

    /// <summary>相对方位：-1 = 正左后方，0 = 正前方，1 = 正右后方。</summary>
    private static float Bearing(float heading, Vec2 from, Vec2 to)
    {
        float ang = MathF.Atan2(to.Y - from.Y, to.X - from.X);
        return Vec2.WrapAngle(ang - heading) / MathF.PI;
    }

    private void BuildGrid()
    {
        _cell = 72f;   // 必须 >= 最大体径的 2 倍，否则 3×3 邻域会漏掉重叠对
        _cw = Math.Max(1, (int)MathF.Ceiling(Cfg.WorldWidth / _cell));
        _ch = Math.Max(1, (int)MathF.Ceiling(Cfg.WorldHeight / _cell));
        int cells = _cw * _ch;
        if (_head.Length != cells) _head = new int[cells];

        int need = Math.Max(64, Cfg.MaxPopulation + 64);
        if (_next.Length < need) _next = new int[need];

        Array.Fill(_head, -1);
        for (int i = 0; i < Creatures.Count; i++)
        {
            Creature c = Creatures[i];
            int cx = (int)Math.Clamp(c.Pos.X / _cell, 0, _cw - 1);
            int cy = (int)Math.Clamp(c.Pos.Y / _cell, 0, _ch - 1);
            int ci = cy * _cw + cx;
            _next[i] = _head[ci];
            _head[ci] = i;
        }
        BuildEggGrid();
    }

    /// <summary>把场上的卵挂进空间哈希。和生物网格共用格子尺寸，每 tick 重建一次。</summary>
    private void BuildEggGrid()
    {
        int cells = _cw * _ch;
        if (_eggHead.Length != cells) _eggHead = new int[cells];
        if (_eggNext.Length < Eggs.Count + 16) _eggNext = new int[Math.Max(64, Eggs.Count * 2)];

        Array.Fill(_eggHead, -1, 0, cells);
        for (int i = 0; i < Eggs.Count; i++)
        {
            Egg e = Eggs[i];
            if (!e.Alive) continue;
            int cx = (int)Math.Clamp(e.Pos.X / _cell, 0, _cw - 1);
            int cy = (int)Math.Clamp(e.Pos.Y / _cell, 0, _ch - 1);
            int ci = cy * _cw + cx;
            _eggNext[i] = _eggHead[ci];
            _eggHead[ci] = i;
        }
    }

    // ==================================================================
    // 执行动作
    // ==================================================================

    private void Execute(Creature c, ActionSpec spec, in SenseData d, int selfIndex)
    {
        switch (spec.Action)
        {
            case ActionId.Idle:
                break;

            case ActionId.Move:
                MoveWithAim(c, spec.Aim, d, 1f, 1f);
                break;

            case ActionId.Sprint:
                MoveWithAim(c, spec.Aim, d, Cfg.SprintMultiplier, Cfg.SprintExtraCost);
                break;

            case ActionId.Turn:
                TurnToward(c, ResolveAim(spec.Aim, c, d), 0.55f);
                break;

            case ActionId.Attack:
                // 攻击可以作用于生物，也可以作用于卵 —— 由方向选择器决定目标是谁。
                // 这就是"攻击视野内最近的敌方卵"能写出来的原因。
                if (AimTable.TargetsEgg(spec.Aim)) DoAttackEgg(c, spec.Aim, d);
                else DoAttack(c, d, selfIndex);
                break;

            case ActionId.Eat:
                DoEat(c, d);
                break;

            case ActionId.EatEgg:
                DoEatEgg(c, d);
                break;

            case ActionId.Incubate:
                DoIncubate(c, d);
                break;

            case ActionId.Reproduce:
                LayEggs(c);
                break;

            case ActionId.Rest:
                break; // 代谢阶段统一处理

            case ActionId.GrowOrgan:
                DoGrow(c, spec.Organ, grow: true);
                break;

            case ActionId.ShrinkOrgan:
                DoGrow(c, spec.Organ, grow: false);
                break;

            case ActionId.EmitPheromone:
                // MVP-2：信息素场。当前为无操作，但保留在动作池里，
                // 这样演化出的规则不会因为动作缺失而失效。
                break;
        }
    }

    private Vec2 ResolveAim(AimId aim, Creature c, in SenseData d)
    {
        switch (aim)
        {
            case AimId.Forward:
            case AimId.CurrentHeading:
                return Vec2.FromAngle(c.Heading);

            case AimId.Random:
                return Vec2.FromAngle(Rng.Range(0f, MathF.Tau));

            case AimId.NearestFood:
                return d.FoodFound
                    ? (d.NextFoodPos - c.Pos).Normalized()
                    : Vec2.FromAngle(c.Heading);

            case AimId.NearestEnemy:
                return d.EnemyFound
                    ? (d.NextEnemyPos - c.Pos).Normalized()
                    : Vec2.FromAngle(Rng.Range(0f, MathF.Tau));

            case AimId.AwayFromEnemy:
                return d.EnemyFound
                    ? (c.Pos - d.NextEnemyPos).Normalized()
                    : Vec2.FromAngle(c.Heading);

            case AimId.NearestAlly:
                return d.AllyFound
                    ? (d.NextAllyPos - c.Pos).Normalized()
                    : Vec2.FromAngle(c.Heading);

            case AimId.AwayFromAlly:
                return d.AllyFound
                    ? (c.Pos - d.NextAllyPos).Normalized()
                    : Vec2.FromAngle(c.Heading);

            case AimId.WorldCenter:
                return (new Vec2(Cfg.WorldWidth * 0.5f, Cfg.WorldHeight * 0.5f) - c.Pos).Normalized();

            // ---- 卵 ----
            case AimId.NearestEgg:
                return d.EggFound ? (d.NextEggPos - c.Pos).Normalized() : Vec2.FromAngle(c.Heading);

            case AimId.NearestEnemyEgg:
                return d.EnemyEggFound ? (d.NextEnemyEggPos - c.Pos).Normalized() : Vec2.FromAngle(c.Heading);

            case AimId.NearestAllyEgg:
                return d.AllyEggFound ? (d.NextAllyEggPos - c.Pos).Normalized() : Vec2.FromAngle(c.Heading);

            case AimId.AwayFromEnemyEgg:
                return d.EnemyEggFound ? (c.Pos - d.NextEnemyEggPos).Normalized() : Vec2.FromAngle(c.Heading);

            // ---- 幼体 ----
            case AimId.NearestJuvenile:
                return d.JuvenileFound ? (d.NextJuvenilePos - c.Pos).Normalized() : Vec2.FromAngle(c.Heading);

            case AimId.NearestEnemyJuvenile:
                return d.EnemyJuvenileFound ? (d.NextEnemyJuvenilePos - c.Pos).Normalized() : Vec2.FromAngle(c.Heading);

            case AimId.NearestAllyJuvenile:
                return d.AllyJuvenileFound ? (d.NextAllyJuvenilePos - c.Pos).Normalized() : Vec2.FromAngle(c.Heading);

            // ---- 同物种：只看物种 Id，不看行为签名 ----
            case AimId.NearestSameSpecies:
                return d.SameSpeciesFound ? (d.NextSameSpeciesPos - c.Pos).Normalized() : Vec2.FromAngle(c.Heading);

            default:
                return Vec2.FromAngle(c.Heading);
        }
    }

    /// <summary>
    /// 带绕障的移动。规则的 aim 给了目标点，但直线上可能有岩石 ——
    /// 这时才向 A* 要一步绕行方向。
    ///
    /// 成本控制的三道闸门（顺序很重要，越靠前越便宜）：
    ///   1. 直线可视性采样（几十次数组查表）—— 挡住绝大多数请求
    ///   2. 路径缓存（同目标 + 未过期直接复用）
    ///   3. 全局调用预算与节点预算 —— 保证总开销有硬上限
    /// </summary>
    private void MoveWithAim(Creature c, AimId aim, in SenseData d, float speedMultiplier, float costMultiplier)
    {
        Vec2 dir = ResolveAim(aim, c, d);

        // 免费闸门：感知阶段已经算过「前方障碍距离」（沿朝向探 90 单位）。
        // 它等于 1 就说明前方一路畅通，直线可视性检查根本不必做。
        // 这一条把 LineClear 的调用量砍掉八成 —— 而 LineClear 才是真正的开销大头，
        // 它每 tick 被每个移动动作各调一次，而 A* 每 tick 只触发十几次。
        if (Cfg.Pathfinding && (d.ObstacleAhead < 1f || c.HasWaypoint))
        {
            Vec2 target = ResolveAimTarget(aim, c, d, dir);
            Vec2 steer = SteerWithPath(c, target);
            if (steer.LengthSq > 1e-8f) dir = steer;
        }

        MoveCreature(c, dir, speedMultiplier, costMultiplier);
    }

    /// <summary>把方向型 aim 折算成一个"目标点"，供寻路使用。</summary>
    private Vec2 ResolveAimTarget(AimId aim, Creature c, in SenseData d, Vec2 dir)
    {
        switch (aim)
        {
            case AimId.NearestFood when d.FoodFound: return d.NextFoodPos;
            case AimId.NearestEnemy when d.EnemyFound: return d.NextEnemyPos;
            case AimId.NearestAlly when d.AllyFound: return d.NextAllyPos;
            // 卵是固定不动的，所以"走向卵"特别适合寻路 —— 目标不会跑
            case AimId.NearestEgg when d.EggFound: return d.NextEggPos;
            case AimId.NearestEnemyEgg when d.EnemyEggFound: return d.NextEnemyEggPos;
            case AimId.NearestAllyEgg when d.AllyEggFound: return d.NextAllyEggPos;
            case AimId.NearestJuvenile when d.JuvenileFound: return d.NextJuvenilePos;
            case AimId.NearestEnemyJuvenile when d.EnemyJuvenileFound: return d.NextEnemyJuvenilePos;
            case AimId.NearestAllyJuvenile when d.AllyJuvenileFound: return d.NextAllyJuvenilePos;
            case AimId.NearestSameSpecies when d.SameSpeciesFound: return d.NextSameSpeciesPos;
            // 其余（正前方 / 随机 / 远离某物 / 世界中心）没有"要到达的点"，
            // 用一个前方远点代表方向 —— 直线检查对它天然通畅，等于不做寻路。
            default: return c.Pos + dir * (Cfg.TileSize * 4f);
        }
    }

    /// <summary>返回绕行方向；直线通畅或没找到路时返回零向量（表示沿用原方向）。</summary>
    private Vec2 SteerWithPath(Creature c, Vec2 target)
    {
        const float waypointReach = 16f;
        const float targetMoveTolerance = 90f;

        // 1) 已有一段缓存路径且仍然有效：沿它走，走到一个就前进一个。
        //    这是把寻路开销压下来的关键 —— 只返回下一步的话，生物每走一格就要重算一次。
        if (c.HasWaypoint)
        {
            bool targetMoved = Vec2.DistSq(target, c.PathGoal) > targetMoveTolerance * targetMoveTolerance;
            if (c.PathTimer > 0 && !targetMoved)
            {
                Vec2 wp = c.PathBuf![c.PathIndex];
                while (Vec2.DistSq(c.Pos, wp) < waypointReach * waypointReach)
                {
                    c.PathIndex++;
                    if (c.PathIndex >= c.PathCount) break;
                    wp = c.PathBuf[c.PathIndex];
                }
                if (c.PathIndex < c.PathCount)
                    return (wp - c.Pos).Normalized();
            }
            c.PathCount = 0;
            c.PathIndex = 0;
        }

        // 2) 直线通畅就不寻路（绝大多数情况在这里返回）
        if (_pathFinder.LineClear(c.Pos, target)) return Vec2.Zero;

        // 3) 预算闸门
        if (!_pathFinder.TryConsumeBudget()) return Vec2.Zero;

        // 4) 要一段完整路径并跟随
        c.PathBuf ??= new Vec2[8];
        int n = _pathFinder.FindPath(c.Pos, target, c.PathBuf, c.PathBuf.Length);
        if (n <= 0) return Vec2.Zero;

        c.PathCount = n;
        c.PathIndex = 0;
        c.PathGoal = target;
        c.PathTimer = Cfg.PathfindCooldown * 4;

        Vec2 first = c.PathBuf[0];
        return (first - c.Pos).Normalized();
    }

    private void TurnToward(Creature c, Vec2 dir, float maxTurn)
    {
        if (dir.LengthSq < 1e-8f) return;
        float target = MathF.Atan2(dir.Y, dir.X);
        float delta = Vec2.WrapAngle(target - c.Heading);
        c.Heading = Vec2.WrapAngle(c.Heading + Math.Clamp(delta, -maxTurn, maxTurn));
    }

    private void MoveCreature(Creature c, Vec2 dir, float speedMultiplier, float costMultiplier)
    {
        TurnToward(c, dir, 0.35f);
        float speed = c.MaxSpeed * speedMultiplier;
        float dx = MathF.Cos(c.Heading) * speed;
        float dy = MathF.Sin(c.Heading) * speed;

        Vec2 want = new(
            Math.Clamp(c.Pos.X + dx, 1f, Cfg.WorldWidth - 1f),
            Math.Clamp(c.Pos.Y + dy, 1f, Cfg.WorldHeight - 1f));

        Vec2 got = want;

        if (!IsPassableAt(want))
        {
            // 岩石碰撞：先试"只走 X"或"只走 Y"实现贴墙滑行。
            // 直接硬停会让生物顶在墙上原地抖，看起来像卡死；
            // 滑行则让它们自然地沿墙绕过去。
            Vec2 slideX = new(want.X, c.Pos.Y);
            Vec2 slideY = new(c.Pos.X, want.Y);
            if (IsPassableAt(slideX)) got = slideX;
            else if (IsPassableAt(slideY)) got = slideY;
            else got = c.Pos;   // 完全堵死：原地不动，靠「前方障碍距离」传感器让演化学会转向
        }

        float actualDist = Vec2.Dist(c.Pos, got);
        c.Pos = got;

        // 移动开销按地形代价缩放（沼泽、灌木明显更贵）
        TerrainType t = TerrainAt(TileAt(c.Pos));
        float terrainCost = MathF.Min(TerrainTable.MoveCostOf(t), 3f);
        c.Energy -= Cfg.MoveCostPerUnit * actualDist * c.Mass * costMultiplier * terrainCost;
    }

    /// <summary>
    /// 把误入岩石的生物推出来。
    ///
    /// 正常移动不可能进岩石（MoveCreature 会挡住），但手绘地形可能把岩石直接画在某只生物脚下，
    /// 这时如果没有这一步，它就会永久卡死在里面 —— 而且玩家完全看不出发生了什么。
    /// 这里用逃逸距离场查表往外挪，O(1)。
    /// </summary>
    private void UnstickFromTerrain()
    {
        for (int i = 0; i < Creatures.Count; i++)
        {
            Creature c = Creatures[i];
            if (!c.Alive) continue;
            if (IsPassableAt(c.Pos)) continue;
            c.Pos = NudgeToPassable(c.Pos);
        }
    }

    /// <summary>
    /// 生物碰撞体积：把互相重叠的身体推开。
    ///
    /// 用空间哈希的 3×3 邻域枚举，每对重叠各处理两次（从 a 看一次、从 b 看一次），
    /// 每次只推一半 —— 等价于一次 Gauss-Seidel 松弛，天然收敛且不需要去重。
    ///
    /// 拥挤同时要花能量：这给了"扎堆"一个真实的代谢代价，
    /// 于是群居和分散都会成为需要权衡的策略，而不是单纯的人多力量大。
    /// </summary>
    private void SeparateBodies()
    {
        if (_head.Length == 0) return;

        for (int i = 0; i < Creatures.Count; i++)
        {
            Creature a = Creatures[i];
            if (!a.Alive) continue;

            int cx = (int)Math.Clamp(a.Pos.X / _cell, 0, _cw - 1);
            int cy = (int)Math.Clamp(a.Pos.Y / _cell, 0, _ch - 1);

            for (int gy = cy - 1; gy <= cy + 1; gy++)
            {
                if (gy < 0 || gy >= _ch) continue;
                for (int gx = cx - 1; gx <= cx + 1; gx++)
                {
                    if (gx < 0 || gx >= _cw) continue;

                    int node = _head[gy * _cw + gx];
                    while (node >= 0)
                    {
                        if (node != i && node < Creatures.Count)
                        {
                            Creature b = Creatures[node];
                            if (b.Alive && b.Id > a.Id)   // 只从 Id 小的一侧推，避免重复计算
                                ResolveOverlap(a, b);
                        }
                        node = _next[node];
                    }
                }
            }
        }
    }

    private void ResolveOverlap(Creature a, Creature b)
    {
        float dx = b.Pos.X - a.Pos.X;
        float dy = b.Pos.Y - a.Pos.Y;
        float minDist = a.BodyRadius + b.BodyRadius;
        float dsq = dx * dx + dy * dy;
        if (dsq >= minDist * minDist) return;

        float dist = MathF.Sqrt(dsq);
        float overlap;
        float nx, ny;
        if (dist < 1e-4f)
        {
            // 完全重合：用一个确定性的方向分开，别除以零
            overlap = minDist;
            nx = 1f; ny = 0f;
        }
        else
        {
            overlap = minDist - dist;
            nx = dx / dist; ny = dy / dist;
        }

        // 按体重反比分配位移：重的推不动
        float total = a.Mass + b.Mass;
        float shareA = b.Mass / total;
        float shareB = a.Mass / total;

        const float relax = 0.5f;
        float maxPush = Cfg.MaxSeparationPerTick;

        float pushA = MathF.Min(overlap * shareA * relax, maxPush);
        float pushB = MathF.Min(overlap * shareB * relax, maxPush);

        Vec2 na = new(a.Pos.X - nx * pushA, a.Pos.Y - ny * pushA);
        Vec2 nb = new(b.Pos.X + nx * pushB, b.Pos.Y + ny * pushB);

        if (IsPassableAt(na)) a.Pos = na;
        if (IsPassableAt(nb)) b.Pos = nb;

        // 拥挤的代谢代价
        float cost = overlap * Cfg.CrowdCost;
        a.Energy -= cost;
        b.Energy -= cost;
    }

    private void DoAttack(Creature c, in SenseData d, int selfIndex)
    {
        if (!d.EnemyFound)
        {
            c.Energy -= Cfg.AttackCost * 0.3f; // 挥空也有代价
            return;
        }

        c.Energy -= Cfg.AttackCost * (1f + c.Attack * 0.2f);

        if (d.NextEnemyIndex < 0 || d.NextEnemyIndex >= Creatures.Count) return;
        Creature target = Creatures[d.NextEnemyIndex];
        if (!target.Alive) return;

        // 攻击距离由双方体径决定 —— 大块头因此天然有更长的攻击范围，
        // 这是体重除了"耐打"之外的又一项真实收益。
        float reach = MathF.Max(Cfg.MinAttackReach, (c.BodyRadius + target.BodyRadius) * Cfg.AttackReachFactor);

        if (d.EnemyDist > reach)
        {
            // 够不着就扑咬：必须比对手的逃跑速度更快，否则捕食永远无法成功。
            // 这一条是"搏斗"能不能演化出来的分水岭。
            MoveCreature(c, (d.NextEnemyPos - c.Pos).Normalized(), Cfg.AttackLungeMultiplier, 1.25f);
            return;
        }

        float raw = 0.35f + c.Attack * Cfg.AttackDamageScale;
        float applied = raw * (1f - target.Armor);
        target.Hp -= applied;

        // 撕咬过程中的即时收益（大头在击杀）
        c.Energy = MathF.Min(c.MaxEnergy, c.Energy + applied * 0.30f);

        // 毒腺
        int gland = c.G.Organs[(int)OrganType.Gland];
        if (gland > 0) target.Poison += gland * Cfg.PoisonPerLevel;

        // 棘：反伤
        if (target.SpineReflect > 0f)
        {
            float back = applied * target.SpineReflect;
            c.Hp -= back;
            if (c.Hp <= 0f) KillCreature(c, "被反伤致死");
        }

        if (target.Hp <= 0f)
        {
            // ------------------------------------------------------------------
            // 捕食的能量收益。
            //
            // 这里**必须**基本保持守恒：捕食只在系统内部转移能量，不创造能量。
            // 整个食物链的总能量来自植物（全图每秒才产 ~100 能量），
            // 捕食者能分到的份额不可能超过这个数。
            //
            // 第一版写的是 `Energy*0.7 + Mass*18` —— 那个按体重的固定奖励是凭空造能量。
            // 体重 5 的猎物一次送 40 能量，而 6.6 次/秒的击杀频率等于每秒往系统里
            // 注入 263 能量，是植物产量的三倍。后果就是"多杀"永远压倒性最优、
            // 食草生态位彻底不存在、全体演化成战力 0.94 的绞肉机。
            //
            // 现在只剩一小截按体重的奖励（代表那具身体的肉），小到不至于扭转选择压力。
            // ------------------------------------------------------------------
            float gain = target.Energy * Cfg.PredationGain
                       + MathF.Sqrt(target.Mass) * Cfg.PredationFlatBonus;
            c.Energy = MathF.Min(c.MaxEnergy + gain, c.Energy + gain);
            KillCreature(target, "被猎杀");
            Kills++;
        }
    }

    private void DoEat(Creature c, in SenseData d)
    {
        if (d.FoodHere <= 0.001f) return;
        int idx = TileAt(c.Pos);
        float bite = MathF.Min(Food[idx], c.BiteRate);
        if (bite <= 0f) return;
        Food[idx] -= bite;
        c.Energy = MathF.Min(c.MaxEnergy, c.Energy + bite * Cfg.FoodEnergy * c.DigestEff);
    }

    /// <summary>
    /// 攻击卵。走和踩踏同一套次数机制 —— 卵没有血量，只有"还能扛几下"。
    /// 目标由方向选择器决定：写"最近的敌方卵"就砸敌方卵，写"最近的友方卵"就砸自己人的卵
    /// （听起来荒谬，但那正是演化该有的自由 —— 同类相食本来就是可演化策略）。
    /// </summary>
    private void DoAttackEgg(Creature c, AimId aim, in SenseData d)
    {
        int idx = aim switch
        {
            AimId.NearestAllyEgg => d.AllyEggFound ? d.NextAllyEggIndex : -1,
            AimId.NearestEnemyEgg => d.EnemyEggFound ? d.NextEnemyEggIndex : -1,
            _ => d.EggFound ? d.NextEggIndex : -1,
        };
        if (idx < 0 || idx >= Eggs.Count) return;

        Egg e = Eggs[idx];
        if (!e.Alive) return;

        float reach = c.AttackReachSelf + 12f;
        float ddx = e.Pos.X - c.Pos.X;
        float ddy = e.Pos.Y - c.Pos.Y;
        if (ddx * ddx + ddy * ddy > reach * reach) return;

        c.Energy = MathF.Max(0f, c.Energy - Cfg.AttackCost);
        e.TrampleCount++;
        Tramples++;
        if (e.TrampleCount >= e.TrampleLimit)
        {
            e.Alive = false;
            EggsCrushed++;
        }
    }

    /// <summary>
    /// 产卵：繁殖从"直接造一个个体"改成"产下一窝卵"。
    ///
    /// 这一步带来三个真实后果，全都不是修饰：
    ///  1. 繁殖被**延迟**了 —— 种群增长变慢，生态节奏整体拉长；
    ///  2. 卵固定不动且不反击，成了生态里第一种"不用搏斗就能吃"的资源，
    ///     于是"抢劫卵"和"护卵"变成两个全新的生态位；
    ///  3. 窝卵数把 r/K 策略轴明牌化：投入总量固定，卵越多每颗越弱。
    /// </summary>
    private void LayEggs(Creature c)
    {
        if (c.ReproTimer > 0) return;
        if (c.Energy < c.ReproThreshold) return;
        if (c.IsJuvenile && !Cfg.JuvenileCanReproduce) return;
        if (Eggs.Count >= Cfg.MaxEggs) return;

        float total = c.Energy * Cfg.EggInvestment;
        if (total < 6f) return;

        c.Energy -= total;
        c.ReproTimer = c.ReproInterval;

        int clutch = Math.Max(1, c.ClutchSize);
        if (Eggs.Count + clutch > Cfg.MaxEggs) clutch = Cfg.MaxEggs - Eggs.Count;
        if (clutch <= 0) { c.Energy += total; return; }

        float per = total / clutch;

        // 整窝的基因组是**同一个**：一次产卵 = 一次减数分裂 + 一次变异，
        // 所以同窝后代互为近乎克隆的兄弟姐妹。这让"窝"成为一个真实的亲缘单位，
        // 也让护卵行为有明确的受益对象。
        Genome child = Mutator.Reproduce(c.G, Rng, Cfg);
        child.Name = c.G.Name;

        if (_fpScratch.Length != GenomeFingerprint.Dims) _fpScratch = new float[GenomeFingerprint.Dims];
        GenomeFingerprint.Compute(child, Cfg, _fpScratch);

        SpeciesRecord? parent = Species.Get(c.G.SpeciesId);
        float dist = parent is null ? float.MaxValue : GenomeFingerprint.Distance(_fpScratch, parent.Center);

        if (c.Fingerprint.Length == GenomeFingerprint.Dims)
        {
            float step = GenomeFingerprint.Distance(_fpScratch, c.Fingerprint);
            _mutationStepEma = _mutationStepEma <= 0f
                ? step
                : _mutationStepEma * 0.998f + step * 0.002f;
        }

        float effectiveThreshold = MathF.Max(1e-5f, _mutationStepEma * Cfg.SpeciationFactor);
        bool isNewSpecies = parent is null || dist > effectiveThreshold;

        if (isNewSpecies)
        {
            int id = NextSpeciesId++;
            SpeciesRecord rec = Species.Create(id, child, child.Signature, _fpScratch, Tick, c.SpeciesId);
            rec.TotalBirths = 1;
            child.SpeciesId = id;
        }
        else
        {
            child.SpeciesId = parent!.Id;
            parent.TotalBirths++;
        }

        // 卵的孵化时间：总投入越少（窝越大）孵得越久 —— 小卵需要更长的发育期
        int hatch = (int)(Cfg.EggHatchTicks * (0.75f + 1.1f / clutch));

        for (int k = 0; k < clutch; k++)
        {
            // 卵散在亲代周围。散开是为了避免"五颗卵叠在一个点上"被一次踩踏全灭。
            float ang = Rng.Range(0f, MathF.Tau);
            float r = Rng.Range(6f, 22f);
            var e = new Egg
            {
                Id = NextEggId++,
                Pos = new Vec2(
                    Math.Clamp(c.Pos.X + MathF.Cos(ang) * r, 1f, Cfg.WorldWidth - 1f),
                    Math.Clamp(c.Pos.Y + MathF.Sin(ang) * r, 1f, Cfg.WorldHeight - 1f)),
                Energy = per,
                SpeciesId = child.SpeciesId,
                Signature = child.Signature,
                Genome = child,
                ParentId = c.Id,
                HatchTimer = hatch,
                HatchTotal = hatch,
                TrampleLimit = Math.Max(1, c.EggTrampleLimit),
            };
            Eggs.Add(e);
        }

        Births += clutch;
    }

    /// <summary>
    /// 卵的每 tick 处理：踩踏判定 → 孵化倒计时 → 清理。
    ///
    /// 踩踏是这一版新加的销毁机制，刻意**不用血量**：卵不会反击，也不需要被"战斗"，
    /// 它只是挡在路上了。能扛几次由亲代的「卵壳强度」性状买来（0 级 1 次，每级 +2）。
    /// </summary>
    private void TickEggs()
    {
        if (Eggs.Count == 0) return;

        for (int i = 0; i < Eggs.Count; i++)
        {
            Egg e = Eggs[i];
            if (!e.Alive) continue;

            // ---- 踩踏：任何非同族生物压上来都算一次 ----
            if (e.TrampleCooldown > 0) e.TrampleCooldown--;

            float reach = Cfg.TileSize * Cfg.EggTrampleRadiusFactor;
            int cx = (int)Math.Clamp(e.Pos.X / _cell, 0, _cw - 1);
            int cy = (int)Math.Clamp(e.Pos.Y / _cell, 0, _ch - 1);
            bool crushed = false;

            for (int oy = -1; oy <= 1 && !crushed; oy++)
            {
                int gy = cy + oy;
                if (gy < 0 || gy >= _ch) continue;
                for (int ox = -1; ox <= 1 && !crushed; ox++)
                {
                    int gx = cx + ox;
                    if (gx < 0 || gx >= _cw) continue;

                    int node = _head[gy * _cw + gx];
                    while (node >= 0)
                    {
                        Creature t = Creatures[node];
                        node = _next[node];
                        if (!t.Alive || t.Id == e.ParentId) continue;

                        float rr = reach + t.BodyRadius;
                        float ddx = t.Pos.X - e.Pos.X;
                        float ddy = t.Pos.Y - e.Pos.Y;
                        if (ddx * ddx + ddy * ddy > rr * rr) continue;

                        // 同族不踩。判定用**卵记录下来的亲代签名** ——
                        // 亲代可能早就死了，但敌友不能因此失效。
                        if (t.SpeciesId == e.SpeciesId
                            && GenomeFingerprint.SignatureDistance(t.Signature, e.Signature) <= t.KinThresholdBits)
                            continue;

                        if (e.TrampleCooldown > 0) continue;   // 同一只生物不能一 tick 踩满
                        e.TrampleCount++;
                        e.TrampleCooldown = Cfg.EggTrampleCooldown;
                        Tramples++;

                        if (e.TrampleCount >= e.TrampleLimit)
                        {
                            e.Alive = false;
                            EggsCrushed++;
                            crushed = true;
                        }
                        break;
                    }
                }
            }
            if (crushed) continue;

            // ---- 孵化 ----
            if (--e.HatchTimer <= 0) HatchEgg(e);
        }

        // 清理：就地压实，避免每 tick 分配新列表
        int w = 0;
        for (int i = 0; i < Eggs.Count; i++)
            if (Eggs[i].Alive) Eggs[w++] = Eggs[i];
        if (w < Eggs.Count) Eggs.RemoveRange(w, Eggs.Count - w);
    }

    /// <summary>卵孵出幼体。</summary>
    private void HatchEgg(Egg e)
    {
        e.Alive = false;
        if (Creatures.Count + _newborns.Count >= Cfg.MaxPopulation) return;

        Genome g = e.Genome?.Clone() ?? Genome.CreateFounder(Rng, e.SpeciesId);
        g.SpeciesId = e.SpeciesId;
        g.Signature = e.Signature;
        g.SpeciesId = e.SpeciesId;

        var baby = new Creature
        {
            Id = NextCreatureId++,
            G = g,
            Pos = e.Pos,
            Heading = Rng.Range(0f, MathF.Tau),
            SpeciesId = e.SpeciesId,
            Age = 0f,
            Maturity = 0f,
        };
        float[] fp = new float[GenomeFingerprint.Dims];
        baby.Signature = GenomeFingerprint.Compute(g, Cfg, fp);
        baby.Fingerprint = fp;
        g.Signature = baby.Signature;

        baby.RecalcDerived(Cfg);
        baby.Maturity = 0f;
        // 幼体一出生只有成体的一部分血量和体径 —— 这正是"幼体脆弱"的物理体现
        baby.BodyRadius = baby.BaseBodyRadius * baby.JuvenileFloor;
        baby.AttackReachSelf = baby.BodyRadius * Cfg.AttackReachFactor;
        baby.Upkeep = baby.BaseUpkeep * Cfg.JuvenileUpkeepFactor;
        baby.Energy = MathF.Min(e.Energy, baby.MaxEnergy);
        baby.Hp = baby.MaxHp * baby.JuvenileFloor;
        _newborns.Add(baby);
        Hatches++;
    }

    /// <summary>吃卵：卵是场上唯一"不用搏斗就能吃"的资源。</summary>
    private void DoEatEgg(Creature c, in SenseData d)
    {
        if (!d.EggFound) return;
        if (d.NextEggIndex < 0 || d.NextEggIndex >= Eggs.Count) return;

        Egg e = Eggs[d.NextEggIndex];
        if (!e.Alive) return;

        // 必须真的够得着，否则规则会变成"隔着半张地图吃卵"
        float reach = c.AttackReachSelf + 10f;
        float ddx = e.Pos.X - c.Pos.X;
        float ddy = e.Pos.Y - c.Pos.Y;
        if (ddx * ddx + ddy * ddy > reach * reach) return;

        e.Alive = false;
        c.Energy = MathF.Min(c.MaxEnergy, c.Energy + e.Energy * Cfg.EggEatGain);
        EggsEaten++;
    }

    /// <summary>孵卵：趴在相邻的友方卵上，替它省下孵化时间。</summary>
    private void DoIncubate(Creature c, in SenseData d)
    {
        if (!d.AllyEggFound) return;
        if (d.NextAllyEggIndex < 0 || d.NextAllyEggIndex >= Eggs.Count) return;

        Egg e = Eggs[d.NextAllyEggIndex];
        if (!e.Alive) return;

        float reach = c.AttackReachSelf + 12f;
        float ddx = e.Pos.X - c.Pos.X;
        float ddy = e.Pos.Y - c.Pos.Y;
        if (ddx * ddx + ddy * ddy > reach * reach) return;

        e.HatchTimer = Math.Max(1, e.HatchTimer - Cfg.EggIncubateSpeedup);
    }

    /// <summary>
    /// 生长/退化性状。现在管的是全部 15 项（10 器官 + 5 生活史），
    /// 而且生长要受点数预算约束 —— 这是硬上限真正落地的地方。
    /// </summary>
    private void DoGrow(Creature c, int trait, bool grow)
    {
        if (trait < 0 || trait >= TraitTable.Count) return;

        if (grow)
        {
            int lvl = c.G.TraitAt(trait);
            if (lvl >= TraitTable.MaxLevel) return;
            // 点数用光了就长不了 —— 必须先从别处退化
            if (c.G.PointsUsed >= Cfg.PointBudget) return;

            float cost = TraitGrowCost(trait) * (1f + lvl * 0.35f);
            if (c.Energy < cost * 1.4f) return; // 留点余粮，别把自己饿死
            c.Energy -= cost;
            c.G.SetTrait(trait, lvl + 1);
        }
        else
        {
            int lvl = c.G.TraitAt(trait);
            if (lvl <= 0) return;
            c.G.SetTrait(trait, lvl - 1);
            c.Energy += TraitGrowCost(trait) * 0.35f * lvl;
        }
        c.RecalcDerived(Cfg);
    }

    private static float TraitGrowCost(int trait)
        => trait < OrganTable.Count ? OrganTable.GrowCost[trait] : 16f;

    // ==================================================================
    // 代谢与死亡
    // ==================================================================

    private void Metabolize(float light)
    {
        for (int i = 0; i < Creatures.Count; i++)
        {
            Creature c = Creatures[i];
            if (!c.Alive) continue;

            float factor = 1f;
            if (c.Intent == ActionId.Rest) factor = Cfg.RestUpkeepFactor;
            else if (c.Intent == ActionId.Idle) factor = Cfg.IdleUpkeepFactor;

            c.Energy -= c.Upkeep * factor;

            // 藻胞：光合自养。夜里有 AlgaeLightMinimum 的截止，形成昼夜节律。
            if (c.AlgaeGain > 0f && light >= Cfg.AlgaeLightMinimum)
                c.Energy += c.AlgaeGain * light;

            // 无鳃入深水会持续流失能量。浅水刻意不淹 ——
            // 否则水域就只是惩罚区，而不是"可涉水的走廊 + 需要鳃的栖息地"这种有梯度的空间。
            TerrainType here = TerrainAt(TileAt(c.Pos));
            if (c.G.Organs[(int)OrganType.Gill] == 0 && TerrainTable.DrownsAt(here))
                c.Energy -= Cfg.DrownRate;

            // 地形通行代价（沼泽、灌木会持续消耗额外能量）
            float terrainCost = TerrainTable.MoveCostOf(here);
            if (terrainCost > 1f)
                c.Energy -= c.Upkeep * (terrainCost - 1f) * Cfg.TerrainFriction;

            // 中毒
            if (c.Poison > 0.01f)
            {
                c.Hp -= c.Poison * Cfg.PoisonDamage;
                c.Poison *= Cfg.PoisonDecay;
                if (c.Hp <= 0f)
                {
                    KillCreature(c, "中毒身亡");
                    continue;
                }
            }

            if (c.ReproTimer > 0) c.ReproTimer--;
            if (c.PathTimer > 0) c.PathTimer--;
            c.Age += 1f;

            // ------------------------------------------------------------------
            // 成熟度：幼体期是这一版新增的生命阶段。
            //
            // 每 tick 更新一次而不是在 RecalcDerived 里算 —— 后者只在出生和
            // 生长性状时调用，而成熟度是连续变化的。这里一次乘加，代价可以忽略，
            // 但它是"幼体更小更慢更弱"能同时体现在画面、碰撞和战斗上的唯一开关。
            // ------------------------------------------------------------------
            if (c.Maturity < 1f)
            {
                c.Maturity = Math.Clamp(c.Age / c.MaturityAge, 0f, 1f);
                float k = c.JuvenileFloor + (1f - c.JuvenileFloor) * c.Maturity;
                c.BodyRadius = c.BaseBodyRadius * k;
                c.AttackReachSelf = c.BodyRadius * Cfg.AttackReachFactor;
                c.Upkeep = c.BaseUpkeep * (Cfg.JuvenileUpkeepFactor + (1f - Cfg.JuvenileUpkeepFactor) * c.Maturity);
            }

            if (c.Energy <= 0f)
            {
                Starvations++;
                KillCreature(c, "饿死");
            }
            else if (c.Age >= c.Lifespan)
            {
                OldAges++;
                KillCreature(c, "寿终");
            }
        }
    }

    private void KillCreature(Creature c, string cause)
    {
        if (!c.Alive) return;
        c.Alive = false;
        c.CauseOfDeath = cause;
        Deaths++;

        // 物质循环：尸体回到食物池，供养下一代和食腐者。
        // 注意 `Mass * 1f` 那一项是唯一的净注入（能量转食物那部分只是转移），
        // 所以要压得很小 —— 否则死亡本身就成了一个能量来源。
        int idx = TileAt(c.Pos);
        float corpseFood = (MathF.Max(0f, c.Energy) * Cfg.CorpseConversion + c.Mass * Cfg.CorpseMassBonus)
                         / Cfg.FoodEnergy;
        Food[idx] = MathF.Min(2.0f, Food[idx] + corpseFood);
    }

    private void CollectDead()
    {
        for (int i = Creatures.Count - 1; i >= 0; i--)
            if (!Creatures[i].Alive) Creatures.RemoveAt(i);
    }

    /// <summary>手工添加一只自定义生物（UI 的"造物"功能）。人口满时返回 null。</summary>
    public Creature? AddCustom(Genome g, Vec2? at = null)
    {
        if (g.SpeciesId == 0) g.SpeciesId = NextSpeciesId++;
        if (Creatures.Count >= Cfg.MaxPopulation) return null;
        return Spawn(g, at ?? RandomLandPosition());
    }

    /// <summary>种群统计快照。</summary>
    public WorldStats ComputeStats()
    {
        var s = new WorldStats { Tick = Tick, Population = Creatures.Count, Births = Births, Deaths = Deaths, Kills = Kills };
        if (Creatures.Count == 0) return s;

        var species = new HashSet<int>();
        float energy = 0, mass = 0, mutation = 0, rules = 0, age = 0, combat = 0;
        float genSum = 0;
        int genMax = 0;
        int predators = 0;
        int points = 0;
        int juveniles = 0;
        float fangSum = 0;
        var organSum = new float[OrganTable.Count];

        for (int i = 0; i < Creatures.Count; i++)
        {
            Creature c = Creatures[i];
            species.Add(c.SpeciesId);
            energy += c.EnergyPct;
            mass += c.Mass;
            mutation += c.G.MutationRate;
            rules += c.G.Rules.Count;
            age += c.Age / Math.Max(1, c.Lifespan);
            combat += c.CombatPower;
            genSum += c.G.Generation;
            if (c.G.Generation > genMax) genMax = c.G.Generation;

            // "掠食者"的判据：牙 ≥ 2。这是判断食物链有没有退化成绞肉机的关键指标 ——
            // 如果这个比例长期接近 1，说明食草生态位根本不存在。
            int fang = c.G.Organs[(int)OrganType.Fang];
            fangSum += fang;
            if (fang >= 2) predators++;
            points += c.G.PointsUsed;
            if (c.IsJuvenile) juveniles++;

            for (int o = 0; o < OrganTable.Count; o++) organSum[o] += c.G.Organs[o];
        }

        float inv = 1f / Creatures.Count;
        s.SpeciesCount = species.Count;
        s.AverageEnergyPct = energy * inv;
        s.AverageMass = mass * inv;
        s.AverageMutationRate = mutation * inv;
        s.AverageRuleCount = rules * inv;
        s.AverageAgePct = age * inv;
        s.AverageCombatPower = combat * inv;
        s.AverageGeneration = genSum * inv;
        s.MaxGeneration = genMax;
        s.PredatorFraction = predators * inv;
        s.AveragePoints = points * inv;
        s.JuvenileFraction = juveniles * inv;
        s.AverageFang = fangSum * inv;
        s.AverageOrgans = new float[OrganTable.Count];
        for (int o = 0; o < OrganTable.Count; o++) s.AverageOrgans[o] = organSum[o] * inv;
        return s;
    }
}

/// <summary>某个时刻的世界统计，用于画曲线和判断演化是否在发生。</summary>
public sealed class WorldStats
{
    public int Tick;
    public int Population;
    public int SpeciesCount;
    public int Births;
    public int Deaths;
    public int Kills;
    public float AverageEnergyPct;
    public float AverageMass = 1f;
    public float AverageMutationRate;
    public float AverageRuleCount;
    public float AverageAgePct;
    public float AverageCombatPower;
    /// <summary>平均已用点数。用来验证点数预算是否真的在起作用 ——
    /// 如果它长期贴着上限，说明约束生效；如果离上限很远，说明上限形同虚设。</summary>
    public float AveragePoints;
    /// <summary>幼体占比。</summary>
    public float JuvenileFraction;
    /// <summary>牙 ≥ 2 的个体占比。食物链有没有退化成绞肉机就看这个。</summary>
    public float PredatorFraction;
    /// <summary>平均牙等级。</summary>
    public float AverageFang;
    /// <summary>平均代数 —— 演化是否真在推进，看这个最直接。</summary>
    public float AverageGeneration;
    /// <summary>种群中最深的代数。</summary>
    public int MaxGeneration;
    public float[] AverageOrgans = new float[OrganTable.Count];
}
