namespace Terrarium.Core;

/// <summary>
/// 变异引擎 —— 整个项目的灵魂。
///
/// 三层算子，按频率从高到低：
///   1. 参数微调（高频）：改规则里的阈值、方向、力度、器官等级。负责"精修"。
///   2. 规则变异（中频）：换传感器/换算子/换动作/插入/删除/交换顺序。负责"想出办法"。
///   3. 结构变异（低频）：器官的出现与退化、体型漂移、新物种分家。负责"改变身体"。
///
/// 另外，突变率本身也是基因（元演化）：稳定环境会把它压到很低，
/// 剧变环境里高突变率的支系会胜出。这是整个模拟里最迷人的现象之一。
/// </summary>
public static class Mutator
{
    /// <summary>器官等级变异概率系数。</summary>
    private const float OrganSigma = 0.10f;
    private const float ValueSigmaFraction = 0.11f;
    private const float ParamSigma = 0.12f;
    private const float MetaSigma = 0.28f;

    private const float MinMutationRate = 0.02f;
    private const float MaxMutationRate = 1.20f;

    /// <summary>对基因组做一次完整变异（原地修改）。</summary>
    public static void Mutate(Genome g, Rng rng, SimConfig cfg)
    {
        float mr = g.MutationRate;
        float rr = g.RuleMutationRate;

        MutateTraits(g, rng, mr, cfg);
        MutateRulesContent(g, rng, mr);
        MutateRuleStructure(g, rng, rr);
        MutateMeta(g, rng);
        MutateAppearance(g, rng);
    }

    /// <summary>由父代产生一个带变异的后代。物种归属由 World 依据指纹距离判定，不在这里决定。</summary>
    public static Genome Reproduce(Genome parent, Rng rng, SimConfig cfg)
    {
        Genome child = parent.Clone();
        child.Generation = parent.Generation + 1;
        Mutate(child, rng, cfg);

        // 变异之后必须重算行为签名 —— 否则子代的"算不算同类"判定会停留在父代，
        // 亲缘识别就完全失效了。
        child.Signature = GenomeFingerprint.Compute(child, cfg, null);
        child.SpeciesId = parent.SpeciesId;
        return child;
    }

    // ------------------------------------------------------------------
    // 1. 参数与内容变异
    // ------------------------------------------------------------------

    /// <summary>
    /// 性状变异。**全部 15 项**（10 器官 + 5 生活史）共用同一个点数池，
    /// 而且这里必须守住那条硬上限。
    ///
    /// 关键设计：净增只占一部分，剩下的变异是**点数转移**（从别处扣一点加到这里）。
    /// 为什么一定要有转移：如果只有净增/净减，那么"想变强"就只能等净增，
    /// 而点数用满 20 之后净增被彻底堵死 —— 演化会僵在第一个用满点数的局部最优上。
    /// 有了转移，生物可以在点数总量不变的前提下不断重新分配，
    /// 这才是"特化方向"能被持续搜索的前提。
    /// </summary>
    private static void MutateTraits(Genome g, Rng rng, float mr, SimConfig cfg)
    {
        int budget = Math.Max(1, cfg.PointBudget);

        for (int i = 0; i < TraitTable.Count; i++)
        {
            if (!rng.Chance(mr * OrganSigma)) continue;

            int cur = g.TraitAt(i);

            if (rng.Chance(0.30f))
            {
                // ---- 点数转移：从随机另一项扣一点，加到这里 ----
                int src = rng.NextInt(TraitTable.Count);
                if (src == i || g.TraitAt(src) <= 0) continue;
                if (cur >= TraitTable.MaxLevel) continue;

                g.SetTrait(src, g.TraitAt(src) - 1);
                g.SetTrait(i, cur + 1);
                continue;
            }

            // 偏向生长：新生性状比退化更常见，否则演化会一路简化到只剩一张嘴。
            bool grow = rng.Chance(0.58f);

            if (grow)
            {
                // 点数用光了就长不了 —— 这是硬上限真正生效的地方
                if (g.PointsUsed >= budget) continue;
                if (cur >= TraitTable.MaxLevel) continue;
                g.SetTrait(i, cur + 1);
            }
            else
            {
                if (cur <= 0) continue;
                g.SetTrait(i, cur - 1);
            }
        }
    }

    private static void MutateRulesContent(Genome g, Rng rng, float mr)
    {
        foreach (var rule in g.Rules)
        {
            for (int i = 0; i < rule.If.Count; i++)
            {
                Condition c = rule.If[i];

                if (rng.Chance(mr * 0.45f)) TuneConditionValue(ref c, rng);
                if (rng.Chance(mr * 0.06f)) c.Op = RandomOp(rng);
                if (rng.Chance(mr * 0.05f))
                {
                    c.Sensor = RandomSensor(rng);
                    // 换了传感器，阈值量纲也变了，顺手重新采一个合理值
                    c.Value = RandomSensorValue(c.Sensor, rng);
                }

                rule.If[i] = c;
            }

            for (int i = 0; i < rule.Then.Count; i++)
            {
                ActionSpec a = rule.Then[i];

                if (rng.Chance(mr * 0.05f))
                {
                    a.Action = RandomAction(rng);
                    NormalizeAction(ref a, rng);
                }
                if (rng.Chance(mr * 0.06f) && ActionTable.NeedsAim(a.Action))
                    a.Aim = RandomAim(rng);
                if (rng.Chance(mr * 0.05f) && ActionTable.NeedsOrgan(a.Action))
                    a.Organ = rng.NextInt(OrganTable.Count);
                if (rng.Chance(mr * 0.30f) && ActionTable.NeedsParam(a.Action))
                    a.Param = Math.Clamp(a.Param + rng.Gaussian() * ParamSigma, 0f, 2f);

                rule.Then[i] = a;
            }
        }
    }

    private static void MutateRuleStructure(Genome g, Rng rng, float rr)
    {
        // 插入一条全新规则（可能出现在任意优先级位置）
        if (rng.Chance(rr * 0.55f) && g.Rules.Count < 14)
        {
            int at = rng.NextInt(g.Rules.Count + 1);
            g.Rules.Insert(at, RandomRule(rng));
        }

        // 删除一条规则（至少保留一条，否则生物彻底失去行为）
        if (rng.Chance(rr * 0.35f) && g.Rules.Count > 1)
            g.Rules.RemoveAt(rng.NextInt(g.Rules.Count));

        // 交换两条规则的优先级 —— 这一条经常产生巨大的行为变化
        if (rng.Chance(rr * 0.45f) && g.Rules.Count >= 2)
        {
            int a = rng.NextInt(g.Rules.Count);
            int b = rng.NextInt(g.Rules.Count);
            (g.Rules[a], g.Rules[b]) = (g.Rules[b], g.Rules[a]);
        }

        // 复制一条规则再做微扰（基因重复，是"增加复杂度"的主要途径）
        if (rng.Chance(rr * 0.22f) && g.Rules.Count < 14)
        {
            Rule copy = g.Rules[rng.NextInt(g.Rules.Count)].Clone();
            for (int i = 0; i < copy.If.Count; i++)
            {
                Condition c = copy.If[i];
                if (rng.Chance(0.6f)) TuneConditionValue(ref c, rng);
                copy.If[i] = c;
            }
            g.Rules.Insert(rng.NextInt(g.Rules.Count + 1), copy);
        }

        // 给某条规则增删一个条件
        if (rng.Chance(rr * 0.30f) && g.Rules.Count > 0)
        {
            Rule r = g.Rules[rng.NextInt(g.Rules.Count)];
            if (rng.Chance(0.5f) && r.If.Count < 4)
                r.If.Add(RandomCondition(rng));
            else if (r.If.Count > 1)
                r.If.RemoveAt(rng.NextInt(r.If.Count));
        }

        // 给某条规则增删一个动作
        if (rng.Chance(rr * 0.30f) && g.Rules.Count > 0)
        {
            Rule r = g.Rules[rng.NextInt(g.Rules.Count)];
            if (rng.Chance(0.5f) && r.Then.Count < 3)
                r.Then.Add(RandomActionSpec(rng));
            else if (r.Then.Count > 1)
                r.Then.RemoveAt(rng.NextInt(r.Then.Count));
        }
    }

    private static void MutateMeta(Genome g, Rng rng)
    {
        // 对数空间微扰，避免乘法漂移到 0 或爆炸
        if (rng.Chance(0.16f))
            g.MutationRate = Math.Clamp(g.MutationRate * MathF.Exp(rng.Gaussian() * MetaSigma * 0.5f),
                                        MinMutationRate, MaxMutationRate);
        if (rng.Chance(0.16f))
            g.RuleMutationRate = Math.Clamp(g.RuleMutationRate * MathF.Exp(rng.Gaussian() * MetaSigma * 0.5f),
                                            MinMutationRate, MaxMutationRate);

        // 食性宽容度：一步可以走得不小，因为它是一条策略性的开关，
        // 微调到小数点后三位没有意义。
        if (rng.Chance(0.10f))
            g.Cannibalism = Math.Clamp(g.Cannibalism + rng.Gaussian() * 0.12f, 0f, 1f);
    }

    private static void MutateAppearance(Genome g, Rng rng)
    {
        if (rng.Chance(0.10f)) g.Hue = (g.Hue + rng.Gaussian() * 0.02f + 1f) % 1f;
        if (rng.Chance(0.08f))
            g.SizeScale = Math.Clamp(g.SizeScale * MathF.Exp(rng.Gaussian() * 0.05f), 0.65f, 1.55f);
    }

    // ------------------------------------------------------------------
    // 工具
    // ------------------------------------------------------------------

    private static void TuneConditionValue(ref Condition c, Rng rng)
    {
        if (c.Sensor == SensorId.Always) return;
        var (min, max) = SensorTable.Range[(int)c.Sensor];
        float span = max - min;
        float sigma = MathF.Max(0.015f, span * ValueSigmaFraction);
        float v = c.Value + rng.Gaussian() * sigma;
        // 允许一点点越界（饱和阈值也是一种有效策略），但不要跑飞
        c.Value = Math.Clamp(v, min - span * 0.15f, max + span * 0.15f);
    }

    private static CompareOp RandomOp(Rng rng) => (CompareOp)rng.NextInt(4);

    private static SensorId RandomSensor(Rng rng) => (SensorId)rng.NextInt(SensorTable.Count);

    private static float RandomSensorValue(SensorId s, Rng rng)
    {
        if (s == SensorId.Always) return 0f;
        var (min, max) = SensorTable.Range[(int)s];
        return rng.Range(min, max);
    }

    private static ActionId RandomAction(Rng rng) => ActionTable.MutablePool[rng.NextInt(ActionTable.MutablePool.Length)];

    private static AimId RandomAim(Rng rng) => (AimId)rng.NextInt(AimTable.Count);

    private static int RandomOrgan(Rng rng) => rng.NextInt(OrganTable.Count);

    /// <summary>换动作后，把不适用的参数补成合法值。</summary>
    private static void NormalizeAction(ref ActionSpec a, Rng rng)
    {
        if (ActionTable.NeedsAim(a.Action) && a.Aim == AimId.Forward && rng.Chance(0.6f))
            a.Aim = RandomAim(rng);
        if (ActionTable.NeedsOrgan(a.Action) && (a.Organ < 0 || a.Organ >= OrganTable.Count))
            a.Organ = RandomOrgan(rng);
        if (ActionTable.NeedsParam(a.Action) && a.Param <= 0f)
            a.Param = rng.Range(0.3f, 1.2f);
    }

    private static Condition RandomCondition(Rng rng)
    {
        var s = RandomSensor(rng);
        return new Condition(s, RandomOp(rng), RandomSensorValue(s, rng));
    }

    private static ActionSpec RandomActionSpec(Rng rng)
    {
        var a = new ActionSpec(RandomAction(rng));
        NormalizeAction(ref a, rng);
        return a;
    }

    private static Rule RandomRule(Rng rng)
    {
        int condCount = 1 + rng.NextInt(2);
        var conds = new List<Condition>(condCount);
        for (int i = 0; i < condCount; i++) conds.Add(RandomCondition(rng));

        int actCount = 1 + rng.NextInt(2);
        var acts = new List<ActionSpec>(actCount);
        for (int i = 0; i < actCount; i++) acts.Add(RandomActionSpec(rng));

        return new Rule(conds, acts);
    }

    // ------------------------------------------------------------------
    // 交叉（MVP-3 的有性生殖预留）
    // ------------------------------------------------------------------

    /// <summary>
    /// 双亲各取一部分规则。按"规则签名"做同源对齐，而不是简单切一半 ——
    /// 简单切一半会把规则列表切得面目全非，后代行为基本等于随机。
    /// </summary>
    public static Genome Crossover(Genome a, Genome b, Rng rng)
    {
        Genome child = rng.Chance(0.5f) ? a.Clone() : b.Clone();

        // 器官取平均后各自取整，保留双亲的体型折中
        for (int i = 0; i < OrganTable.Count; i++)
        {
            int mid = (int)MathF.Round((a.Organs[i] + b.Organs[i]) * 0.5f);
            child.Organs[i] = OrganTable.Clamp(mid);

            int midLife = (int)MathF.Round((a.Life[i] + b.Life[i]) * 0.5f);
            child.Life[i] = OrganTable.Clamp(midLife);
        }

        // 规则：交叉装填 —— 按对齐槽位交替取双亲的规则
        var mixed = new List<Rule>();
        int max = Math.Max(a.Rules.Count, b.Rules.Count);
        for (int i = 0; i < max; i++)
        {
            var src = rng.Chance(0.5f) ? a : b;
            if (i < src.Rules.Count) mixed.Add(src.Rules[i].Clone());
            else if (i < a.Rules.Count) mixed.Add(a.Rules[i].Clone());
            else if (i < b.Rules.Count) mixed.Add(b.Rules[i].Clone());
            if (mixed.Count >= 14) break;
        }
        if (mixed.Count > 0) child.Rules = mixed;

        child.MutationRate = (a.MutationRate + b.MutationRate) * 0.5f;
        child.RuleMutationRate = (a.RuleMutationRate + b.RuleMutationRate) * 0.5f;
        child.Cannibalism = (a.Cannibalism + b.Cannibalism) * 0.5f;
        return child;
    }
}
