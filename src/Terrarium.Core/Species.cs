namespace Terrarium.Core;

/// <summary>
/// 一个物种的记录。核心是「模式标本」—— 该物种诞生时那个个体的指纹。
///
/// 为什么不滚动更新成一个平均值：如果中心跟着种群漂移，物种就永远不会分化，
/// 因为中心会一路跟着漂。固定成模式标本之后，一条谱系会随世代累积差异，
/// 迟早越过阈值裂出新种 —— 这是分子钟式的行为，也是真实分类学的做法
/// （每个物种有一个固定的模式标本）。
/// </summary>
public sealed class SpeciesRecord
{
    public int Id;
    public string Name = "原生生物";
    public float Hue;

    /// <summary>模式标本的指纹向量（长度 = GenomeFingerprint.Dims）。</summary>
    public float[] Center = Array.Empty<float>();
    /// <summary>模式标本的行为签名。</summary>
    public ulong Signature;

    /// <summary>模式标本的基因组，用于图鉴回看"这个物种长什么样"。</summary>
    public Genome? TypeSpecimen;

    public int ParentSpeciesId;      // 由哪个物种分化而来，0 = 创始原型
    public int FirstTick;
    public int ExtinctTick = -1;     // -1 = 存活
    public int PeakPopulation;
    public int Population;
    public long TotalBirths;
    public int Generation;

    public bool Alive => ExtinctTick < 0;

    public float LifetimeTicks(int nowTick) => (Alive ? nowTick : ExtinctTick) - FirstTick;
}

/// <summary>
/// 物种登记册：当前存活的 + 历史图鉴。
///
/// 存在的意义有两个：一是给捕食判定和统计提供"物种"这个单位，
/// 二是把演化史沉淀下来 —— 什么时候冒出一个新物种、它活了多久、峰值多少、
/// 长什么样。没有它，物种的更替在画面上是看不出来的（你只会看到颜色变了）。
/// </summary>
public sealed class SpeciesRegistry
{
    private readonly List<SpeciesRecord> _all = new();
    private readonly Dictionary<int, SpeciesRecord> _byId = new();
    private readonly List<SpeciesRecord> _alive = new();

    /// <summary>图鉴上限。超出后淘汰峰值人口最低的已灭绝物种，避免记录无限增长。</summary>
    public int MaxEntries { get; set; } = 400;

    /// <summary>全部物种（含已灭绝），按出现顺序。</summary>
    public IReadOnlyList<SpeciesRecord> All => _all;

    /// <summary>当前存活的物种。</summary>
    public IReadOnlyList<SpeciesRecord> Alive => _alive;

    /// <summary>当前存活的物种数。</summary>
    public int AliveCount => _alive.Count;

    /// <summary>历史累计出现过的物种数（含已被图鉴淘汰的）。</summary>
    public int TotalEver { get; private set; }

    public SpeciesRecord? Get(int id) => _byId.TryGetValue(id, out var r) ? r : null;

    public SpeciesRecord Create(int id, Genome g, ulong signature, float[] center,
                                int tick, int parentId, string? name = null)
    {
        var rec = new SpeciesRecord
        {
            Id = id,
            Name = name ?? (parentId > 0 ? DeriveName(g) : g.Name),
            Hue = SpeciesPalette.HueFor(id),
            Center = (float[])center.Clone(),
            Signature = signature,
            // 直接引用而不是克隆：基因组一旦分配给生物就不会再被变异
            // （变异只发生在刚复制出来的子代上）。每次新物种都克隆一份基因组
            // 在高变异率下会造成巨量分配 —— 实测过一次 4.6 万个物种直接把速度打掉 80%。
            TypeSpecimen = g,
            ParentSpeciesId = parentId,
            FirstTick = tick,
            Generation = g.Generation,
        };
        _all.Add(rec);
        _byId[id] = rec;
        TotalEver++;
        TrimCodex();
        return rec;
    }

    /// <summary>
    /// 图鉴淘汰。
    ///
    /// 关键在**滞后**：只在超出上限两倍时才整理一次，而不是每次创建都检查。
    /// 第一版每创建一个物种就在 400+ 条记录上排一次序，
    /// 结果 4.6 万个物种把整体速度从 1219 tick/s 打到 251 tick/s。
    /// 加滞后之后摊销成本可以忽略（每 400 次创建才排一次）。
    /// </summary>
    private void TrimCodex()
    {
        if (_all.Count <= MaxEntries * 2) return;

        // 只淘汰已灭绝且峰值很低的，绝不动活着的
        _all.Sort((a, b) =>
        {
            if (a.Alive != b.Alive) return a.Alive ? -1 : 1;
            return b.PeakPopulation.CompareTo(a.PeakPopulation);
        });

        for (int i = _all.Count - 1; i >= MaxEntries; i--)
        {
            if (_all[i].Alive) continue;
            _byId.Remove(_all[i].Id);
            _all.RemoveAt(i);
        }
    }

    /// <summary>
    /// 给新物种起个名字：按最突出的器官命名。
    /// 纯装饰，但让图鉴读起来像一本图鉴，而不是一张 id 表。
    /// </summary>
    private static string DeriveName(Genome g)
    {
        int best = -1, bestLevel = 0;
        for (int i = 0; i < OrganTable.Count; i++)
        {
            if (g.Organs[i] > bestLevel) { bestLevel = g.Organs[i]; best = i; }
        }
        if (best < 0 || bestLevel <= 0) return "裸身种";

        string trait = best switch
        {
            (int)OrganType.Legs => "疾行",
            (int)OrganType.Eye => "锐目",
            (int)OrganType.Fang => "掠食",
            (int)OrganType.Plate => "重甲",
            (int)OrganType.Stomach => "大胃",
            (int)OrganType.Algae => "光合",
            (int)OrganType.Gland => "毒腺",
            (int)OrganType.Gonad => "繁育",
            (int)OrganType.Gill => "水栖",
            _ => "棘刺",
        };
        return trait + "种";
    }

    /// <summary>按当前种群刷新每个物种的个体数，并把消失的物种标记为灭绝。</summary>
    public void RefreshPopulation(IReadOnlyList<Creature> creatures, int tick)
    {
        // 只清存活记录的计数，再重建存活列表 —— 图鉴里那几百条历史记录
        // 每 30 tick 全扫一遍是纯浪费（实测物种多的时候这会成为热路径）。
        for (int i = 0; i < _alive.Count; i++) _alive[i].Population = 0;

        for (int i = 0; i < creatures.Count; i++)
        {
            Creature c = creatures[i];
            if (!c.Alive) continue;
            if (_byId.TryGetValue(c.SpeciesId, out var rec)) rec.Population++;
        }

        _alive.Clear();
        for (int i = 0; i < _all.Count; i++)
        {
            SpeciesRecord r = _all[i];
            if (r.Population > r.PeakPopulation) r.PeakPopulation = r.Population;
            if (r.Population > 0)
            {
                r.ExtinctTick = -1;
                _alive.Add(r);
            }
            else if (r.ExtinctTick < 0 && r.FirstTick < tick)
            {
                // 只有在真的存在过之后才算灭绝（新物种在出生那一 tick 还没统计到）
                r.ExtinctTick = tick;
            }
        }
    }

    /// <summary>
    /// 两个物种之间的行为差异度（0 = 几乎一样，1 = 完全不同）。
    /// 物种差异矩阵就是靠它铺出来的。
    /// </summary>
    public float DistanceBetween(SpeciesRecord a, SpeciesRecord b)
        => GenomeFingerprint.Distance(a.Center, b.Center);

    public void Clear()
    {
        _all.Clear();
        _byId.Clear();
        _alive.Clear();
        TotalEver = 0;
    }
}

/// <summary>给 UI 用的物种摘要（含与其他物种的差异度）。</summary>
public sealed class SpeciesSummary
{
    public int Id;
    public string Name = "";
    public float Hue;
    public int Population;
    public int PeakPopulation;
    public int FirstTick;
    public int ExtinctTick = -1;
    public float LifetimeTicks;
    public int Generation;
    public int ParentSpeciesId;
    public long TotalBirths;
    public bool Alive;
    public float[] OrganAverage = Array.Empty<float>();
    public float AverageMass;
    public float AverageRules;
    public float AverageCannibalism;

    /// <summary>和其他物种的平均行为差异度，用来一眼看出"这个物种有多独特"。</summary>
    public float Distinctiveness;
    /// <summary>与它差异最大的那个物种（Id 与差异度）。</summary>
    public int MostDifferentId = -1;
    public float MostDifferentDistance;
}
