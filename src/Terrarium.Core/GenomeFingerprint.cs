using System.Numerics;

namespace Terrarium.Core;

/// <summary>
/// 基因 + 行为指纹。
///
/// 这是"物种"这个概念的地基。之前 SpeciesId 是每次繁殖按固定概率随机贴的标签，
/// 跟基因差异毫无关系 —— 结果就是物种数变成噪声，同一物种内部可能差异极大，
/// 捕食者在打自己的镜像。玩家反馈"物种没有区分"，根子在这里。
///
/// 指纹由两部分拼成：
///
///   1. 结构特征（46 维）—— 器官等级、各动作被用了几次、各传感器被用了几次、规则数。
///      回答的是"它长什么样、它关心什么"。
///
///   2. 行为探针（32 × 2 维）—— 在 32 个固定的虚拟场景下把它跑一遍，记录
///      它选中了第几条规则、做出了哪一类动作。回答的是"它在什么情况下会做什么"。
///      这一部分才是关键：两条规则表结构完全不同但行为等价的生物，应该算同一个物种。
///
/// 探针同时产出一个 64 位的**行为签名**（32 个场景 × 2 bit）。于是
/// "这两个生物算不算同类"退化成一次 XOR + popcount —— O(1)，
/// 每 tick 对每对邻居做都能承受，而这正是捕食判定需要的东西。
/// </summary>
public static class GenomeFingerprint
{
    public const int Probes = 32;
    public const int OrganDims = OrganTable.Count;      // 10（OrganTable.Count 是 const）
    public const int LifeDims = LifeTable.Count;        // 5（生活史性状）
    // ActionTable.Count / SensorTable.Count 是属性而不是 const，所以这三项只能是 static readonly
    public static readonly int ActionDims = ActionTable.Count;    // 11
    public static readonly int SensorDims = SensorTable.Count;    // 24
    public static readonly int Dims = OrganDims + LifeDims + ActionDims + SensorDims + 1 + Probes * 2;   // 122

    // 探针复用同一个生物实例：每次繁殖都 new 一个 Creature 会带来可观的 GC 压力。
    // 只有在模拟的串行阶段（繁殖）才会调用，不存在并发问题。
    private static Creature? _probe;

    /// <summary>
    /// 计算指纹。<paramref name="vector"/> 非空时必须长度等于 <see cref="Dims"/>，会被填充。
    /// 返回 64 位行为签名。
    /// </summary>
    public static ulong Compute(Genome g, SimConfig cfg, float[]? vector)
    {
        _probe ??= new Creature();
        Creature probe = _probe;
        probe.G = g;
        probe.Poison = 0f;
        probe.RecalcDerived(cfg);

        int vi = 0;

        // ---------- 1. 结构特征 ----------
        for (int i = 0; i < OrganDims; i++)
        {
            float v = g.Organs[i] / (float)OrganTable.MaxLevel;
            if (vector is not null) vector[vi] = v;
            vi++;
        }

        // 生活史性状同样进入指纹：寿命、繁殖间隔、窝卵数、卵壳强度这些
        // 不只是"数值差异"，它们直接决定一个物种在生态里扮演什么角色 ——
        // 一个"多生散养、卵壳很硬"的物种和一个"少生精养"的物种，
        // 哪怕器官完全一样，也是两个不同的生态位。
        for (int i = 0; i < LifeDims; i++)
        {
            float v = g.Life[i] / (float)LifeTable.MaxLevel;
            if (vector is not null) vector[vi] = v;
            vi++;
        }

        Span<int> actionUse = stackalloc int[ActionDims];
        Span<int> sensorUse = stackalloc int[SensorDims];
        int condTotal = 0;
        foreach (var rule in g.Rules)
        {
            for (int a = 0; a < rule.Then.Count; a++)
                actionUse[(int)rule.Then[a].Action]++;
            for (int c = 0; c < rule.If.Count; c++)
            {
                sensorUse[(int)rule.If[c].Sensor]++;
                condTotal++;
            }
        }

        float actionNorm = 1f / MathF.Max(1f, g.Rules.Count);
        for (int i = 0; i < ActionDims; i++)
        {
            if (vector is not null) vector[vi] = Math.Clamp(actionUse[i] * actionNorm, 0f, 1f);
            vi++;
        }

        float sensorNorm = 1f / MathF.Max(1f, condTotal);
        for (int i = 0; i < SensorDims; i++)
        {
            if (vector is not null) vector[vi] = Math.Clamp(sensorUse[i] * sensorNorm, 0f, 1f);
            vi++;
        }

        if (vector is not null) vector[vi] = Math.Clamp(g.Rules.Count / 14f, 0f, 1f);
        vi++;

        // ---------- 2. 行为探针 ----------
        ulong signature = 0;
        for (int p = 0; p < Probes; p++)
        {
            SenseData d = MakeScenario(p, probe);
            int rule = probe.Think(d, p, cfg);

            int cls = 0;                          // 0 = 消极
            float rulePos = 0f;
            if (rule >= 0 && rule < g.Rules.Count)
            {
                Rule r = g.Rules[rule];
                rulePos = (rule + 1f) / (g.Rules.Count + 1f);
                if (r.Then.Count > 0) cls = ActionClass(r.Then[0].Action);
            }

            if (vector is not null)
            {
                vector[vi] = rulePos;
                vector[vi + 1] = cls / 3f;
            }
            vi += 2;

            signature |= (ulong)(uint)cls << (p * 2);
        }

        return signature;
    }

    /// <summary>把动作粗略归成 4 类，正好塞进 2 bit。</summary>
    private static int ActionClass(ActionId a) => a switch
    {
        ActionId.Attack => 3,
        ActionId.Eat or ActionId.Reproduce or ActionId.GrowOrgan or ActionId.ShrinkOrgan => 2,
        ActionId.Move or ActionId.Sprint => 1,
        _ => 0,   // Idle / Rest / Turn / EmitPheromone
    };

    /// <summary>
    /// 第 p 个虚拟场景。用 5 个二元维度的位展开出正好 32 个组合，
    /// 这样签名天然就是 32 × 2 bit = 64 位，不需要额外编码。
    /// </summary>
    private static SenseData MakeScenario(int p, Creature probe)
    {
        bool enemyNear = (p & 1) != 0;
        bool foodNear = (p & 2) != 0;
        bool hungry = (p & 4) != 0;
        bool hurt = (p & 8) != 0;
        bool foodHere = (p & 16) != 0;

        var d = new SenseData
        {
            EffectiveVision = MathF.Max(1f, probe.Vision),
            Light = 0.6f,
            InWater = false,
            InBush = false,
            AtEdge = false,
            TerrainCost = 1f,
            ObstacleAhead = 1f,
            EscapePressure = 0f,
            Noise = (p * 0.03125f) % 1f,
            FoodHere = foodHere ? 0.5f : 0f,
            FoodFound = true,
            FoodDist = foodNear ? probe.Vision * 0.15f : probe.Vision * 0.9f,
            FoodBearing = (p & 2) != 0 ? -0.4f : 0.4f,
            EnemyFound = enemyNear,
            EnemyDist = enemyNear ? probe.Vision * 0.3f : probe.Vision,
            EnemyBearing = enemyNear ? 0.2f : -0.2f,
            AllyFound = false,
            AllyDist = probe.Vision,
            AllyCount = enemyNear ? 0 : 3,
            EnemyCount = enemyNear ? 2 : 0,
            NearbyCount = enemyNear ? 3 : 2,
        };

        probe.Energy = probe.MaxEnergy * (hungry ? 0.25f : 0.85f);
        probe.Hp = probe.MaxHp * (hurt ? 0.35f : 0.95f);
        probe.Age = 0f;
        return d;
    }

    /// <summary>
    /// 各维度的权重。
    ///
    /// 器官被刻意给到 6 倍权重。第一版所有维度等权，结果 10 个器官维度淹没在
    /// 117 维里 —— "牙差 2 级"这种在画面上肉眼可见的形态差异，对指纹距离的
    /// 贡献只有 0.34%，于是物种判定的实际上是行为而不是身体，
    /// 形态差异巨大的捕食型与植食型被算成同一个物种。
    ///
    /// 器官是玩家看得见的东西，也是生态位分化的主轴，理应主导物种判定。
    /// </summary>
    private static readonly float[] Weights = BuildWeights();

    private static float[] BuildWeights()
    {
        var w = new float[Dims];
        int i = 0;
        for (int k = 0; k < OrganDims; k++) w[i++] = 6.0f;      // 器官：形态主轴
        for (int k = 0; k < LifeDims; k++) w[i++] = 4.0f;       // 生活史：生态位主轴
        for (int k = 0; k < ActionDims; k++) w[i++] = 0.6f;
        for (int k = 0; k < SensorDims; k++) w[i++] = 0.5f;
        w[i++] = 0.5f;                                          // 规则数
        for (int k = 0; k < Probes * 2; k++) w[i++] = 1.0f;      // 行为探针
        return w;
    }

    private static readonly float TotalWeight = SumWeights();

    private static float SumWeights()
    {
        float s = 0f;
        for (int i = 0; i < Weights.Length; i++) s += Weights[i];
        return s;
    }

    /// <summary>加权 L1 距离再除以总权重 —— 结果落在 [0,1]，可以直接当作"差异度"看。</summary>
    public static float Distance(float[] a, float[] b)
    {
        int n = Math.Min(Math.Min(a.Length, b.Length), Weights.Length);
        float sum = 0f;
        for (int i = 0; i < n; i++) sum += MathF.Abs(a[i] - b[i]) * Weights[i];
        return n == 0 ? 0f : sum / TotalWeight;
    }

    /// <summary>两个行为签名差了多少 bit。0 = 行为完全一致。</summary>
    public static int SignatureDistance(ulong a, ulong b) => BitOperations.PopCount(a ^ b);
}
