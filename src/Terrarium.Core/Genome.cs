namespace Terrarium.Core;

/// <summary>
/// 单条条件：传感器 与 阈值 的比较。
/// 条件是"可演化的数字"，阈值本身参与变异 —— 所以"血量低于三成才逃"
/// 这种策略不需要玩家设计，它会被演化找出来。
/// </summary>
public struct Condition
{
    public SensorId Sensor;
    public CompareOp Op;
    public float Value;

    public Condition(SensorId sensor, CompareOp op, float value)
    {
        Sensor = sensor;
        Op = op;
        Value = value;
    }

    public bool Eval(float sensorValue) => Op switch
    {
        CompareOp.Less => sensorValue < Value,
        CompareOp.LessEqual => sensorValue <= Value,
        CompareOp.Greater => sensorValue > Value,
        CompareOp.GreaterEqual => sensorValue >= Value,
        _ => false,
    };

    public override string ToString()
    {
        string op = Op switch
        {
            CompareOp.Less => "<",
            CompareOp.LessEqual => "≤",
            CompareOp.Greater => ">",
            _ => "≥",
        };
        // "永远是" 的阈值没有意义，不显示出来干扰阅读
        if (Sensor == SensorId.Always) return SensorTable.Cn[(int)Sensor];
        return $"{SensorTable.Cn[(int)Sensor]} {op} {Value:0.##}";
    }
}

/// <summary>单个动作实例：动作 + 方向 + 器官 + 力度参数。</summary>
public struct ActionSpec
{
    public ActionId Action;
    public AimId Aim;
    public int Organ;
    public float Param;

    public ActionSpec(ActionId action, AimId aim = AimId.Forward, int organ = 0, float param = 0f)
    {
        Action = action;
        Aim = aim;
        Organ = organ;
        Param = param;
    }

    public override string ToString()
    {
        string s = ActionTable.Cn[(int)Action];
        if (ActionTable.NeedsAim(Action)) s += $" → {AimTable.Cn[(int)Aim]}";
        if (ActionTable.NeedsOrgan(Action))
        {
            int o = Math.Clamp(Organ, 0, OrganTable.Count - 1);
            s += $" [{OrganTable.Cn[o]}]";
        }
        if (ActionTable.NeedsParam(Action)) s += $" ({Param:0.##})";
        return s;
    }
}

/// <summary>
/// 一条规则：若干条件（AND 连接）+ 若干动作。
/// 求值顺序 = 列表顺序 = 优先级，第一条条件全部满足的规则触发，其余忽略。
/// 这是经典的 subsumption 架构：比"所有规则都执行"更好调试、更好演化，
/// 也让玩家能从视觉上理解"它为什么这么做"。
/// </summary>
public sealed class Rule
{
    public List<Condition> If { get; set; } = new();
    public List<ActionSpec> Then { get; set; } = new();

    public Rule() { }

    public Rule(IEnumerable<Condition> conds, IEnumerable<ActionSpec> acts)
    {
        If = new List<Condition>(conds);
        Then = new List<ActionSpec>(acts);
    }

    public Rule Clone()
    {
        var r = new Rule();
        r.If.AddRange(If);
        r.Then.AddRange(Then);
        return r;
    }

    public string Describe()
    {
        string cond = If.Count == 0 ? "(无条件)" : string.Join(" 且 ", If.Select(c => c.ToString()));
        string act = Then.Count == 0 ? "(无动作)" : string.Join(" + ", Then.Select(a => a.ToString()));
        return $"当 {cond} → {act}";
    }
}

/// <summary>
/// 基因组：一个生物的全部可遗传信息。
/// 刻意做成「器官等级数组 + 规则列表 + 几个元基因」这样扁平的结构，
/// 因为扁平结构才能被安全地随机变异、交叉、序列化成 JSON 给人手改。
/// </summary>
public sealed class Genome
{
    /// <summary>各器官等级，按 OrganType 索引，0..OrganTable.MaxLevel。</summary>
    public int[] Organs { get; set; } = new int[OrganTable.Count];

    /// <summary>各生活史性状等级，按 LifeTrait 索引。</summary>
    public int[] Life { get; set; } = new int[LifeTable.Count];

    // ==================================================================
    // 点数预算：器官与生活史性状共用一个池子
    //
    // 为什么要有硬上限：第一版靠超线性开销压制"什么都点满"，但那是个经济补丁 ——
    // 只要食物够多，全场还是会长成一模一样的全能型。硬上限是从结构上禁止这件事：
    // 15 个性状里你最多只能点满 4 个，剩下的必须保持低等级。
    // 于是"我是谁"不再由环境施舍，而是由这套点数分配直接定义。
    // ==================================================================

    /// <summary>统一读写性状：索引 &lt; OrganTable.Count 落到器官，否则落到生活史性状。</summary>
    public int TraitAt(int i)
        => i < OrganTable.Count ? Organs[i] : Life[i - OrganTable.Count];

    public void SetTrait(int i, int level)
    {
        level = TraitTable.Clamp(level);
        if (i < OrganTable.Count) Organs[i] = level;
        else Life[i - OrganTable.Count] = level;
    }

    /// <summary>已经用掉多少点。</summary>
    public int PointsUsed
    {
        get
        {
            int n = 0;
            for (int i = 0; i < Organs.Length; i++) n += Organs[i];
            for (int i = 0; i < Life.Length; i++) n += Life[i];
            return n;
        }
    }

    /// <summary>规则列表，顺序即优先级。</summary>
    public List<Rule> Rules { get; set; } = new();

    // ---- 元基因：让"进化能力"本身也参与进化 ----
    // 稳定环境里这两条会演化到很低（保守），剧变环境里高的支系会胜出。
    public float MutationRate { get; set; } = 0.35f;
    public float RuleMutationRate { get; set; } = 0.18f;

    /// <summary>
    /// 食性宽容度（0..1），可演化基因。
    ///
    /// 0 = 只吃和自己行为差别很大的东西（有亲缘识别，不碰同类）
    /// 1 = 谁都能吃（同类相食）
    ///
    /// 为什么要它：单体物种群里会出现"没有敌人可打 → 捕食消失 → 更加单调"的死结。
    /// 有了这个基因，这个死结就变成**可演化的**：只要出现一个高宽容度的突变体，
    /// 它就能靠吃邻居活下去，多态性自己就回来了。
    /// 这比硬编码一个逃生口优雅得多，而且同类相食本来就是真实存在的策略。
    /// </summary>
    public float Cannibalism { get; set; } = 0.5f;

    /// <summary>
    /// 64 位行为签名（32 个探针场景 × 2 bit）。由 <see cref="GenomeFingerprint"/> 算出。
    /// 捕食判定用的"算不算同类"就是对这个值做 XOR + popcount —— O(1)。
    /// 变异之后必须重算，否则子代的行为判定会停留在父代。
    /// </summary>
    public ulong Signature { get; set; }

    // ---- 表型装饰（不参与物理，只影响观感，但也会漂移） ----
    public float Hue { get; set; }
    public float SizeScale { get; set; } = 1.0f;

    // ---- 谱系信息 ----
    public int Generation { get; set; }
    public int SpeciesId { get; set; }
    public string Name { get; set; } = "原生生物";

    public Genome Clone()
    {
        var g = new Genome
        {
            Organs = (int[])Organs.Clone(),
            Life = (int[])Life.Clone(),
            Rules = new List<Rule>(Rules.Count),
            MutationRate = MutationRate,
            RuleMutationRate = RuleMutationRate,
            Cannibalism = Cannibalism,
            Signature = Signature,
            Hue = Hue,
            SizeScale = SizeScale,
            Generation = Generation,
            SpeciesId = SpeciesId,
            Name = Name,
        };
        foreach (var r in Rules) g.Rules.Add(r.Clone());
        return g;
    }

    public int OrganLevel(OrganType t) => Organs[(int)t];

    /// <summary>
    /// 创始基因组：一只"什么都不会但至少能活"的原生生物。
    /// 器官只给最低限度的腿/眼/胃，其余全为 0 ——
    /// 牙、甲、藻胞、毒腺、鳃、棘都等着被演化"发现"。
    /// </summary>
    public static Genome CreateFounder(Rng rng, int speciesId, string name = "原生生物")
    {
        var g = new Genome
        {
            SpeciesId = speciesId,
            Name = name,
            Hue = rng.NextFloat(),
            Generation = 0,
        };
        g.Organs[(int)OrganType.Legs] = 1;
        g.Organs[(int)OrganType.Eye] = 1;
        g.Organs[(int)OrganType.Stomach] = 1;
        g.Life[(int)LifeTrait.Lifespan] = 1;
        g.Life[(int)LifeTrait.ReproInterval] = 1;
        g.Life[(int)LifeTrait.JuvenileVitality] = 1;
        g.Life[(int)LifeTrait.ClutchSize] = 1;

        // 一条朴素但完整的生存策略：先吃脚下 → 有危险就跑 → 攒够就产卵 → 否则去觅食
        g.Rules.Add(new Rule(
            new[] { new Condition(SensorId.FoodHere, CompareOp.Greater, 0.12f) },
            new[] { new ActionSpec(ActionId.Eat) }));

        g.Rules.Add(new Rule(
            new[]
            {
                new Condition(SensorId.EnemyDistance, CompareOp.Less, 0.22f),
                new Condition(SensorId.HealthPct, CompareOp.Less, 0.70f),
            },
            new[] { new ActionSpec(ActionId.Move, AimId.AwayFromEnemy) }));

        g.Rules.Add(new Rule(
            new[] { new Condition(SensorId.EnergyPct, CompareOp.Greater, 0.82f) },
            new[] { new ActionSpec(ActionId.Reproduce) }));

        // 敌方卵是最好赚的一顿饭：它不会跑，而且比自己弱得多
        g.Rules.Add(new Rule(
            new[]
            {
                new Condition(SensorId.EnemyEggDistance, CompareOp.Less, 0.35f),
                new Condition(SensorId.HealthPct, CompareOp.Greater, 0.55f),
            },
            new[] { new ActionSpec(ActionId.EatEgg, AimId.NearestEnemyEgg) }));

        g.Rules.Add(new Rule(
            new[] { new Condition(SensorId.FoodDistance, CompareOp.Less, 1.0f) },
            new[] { new ActionSpec(ActionId.Move, AimId.NearestFood) }));

        g.Rules.Add(new Rule(
            new[] { new Condition(SensorId.Always, CompareOp.Greater, 0f) },
            new[] { new ActionSpec(ActionId.Move, AimId.Random) }));

        return g;
    }

    // ==================================================================
    // 初始生态原型
    // ==================================================================

    /// <summary>
    /// 初始种群不是一群一模一样的原生生物，而是若干个生态原型。
    ///
    /// 理由很实在：如果所有人都从同一套"吃—走—生"规则出发，演化就得靠随机
    /// 规则变异从零"发明"出捕食、装甲、光合这些策略，搜索空间太大，
    /// 几十万 tick 都未必撞得出来。给一组起点不同的原型，
    /// 世界从第 0 tick 就有真实的生态位竞争，演化也立刻有原材料可用。
    /// </summary>
    public static readonly string[] ArchetypeNames =
    {
        "食草者", "疾行者", "掠食者", "甲士", "光合者", "群居者",
    };

    /// <summary>
    /// 分配生活史性状。参数顺序：寿命 / 繁殖间隔 / 幼体生命力 / 窝卵数 / 卵壳强度。
    ///
    /// 六个原型的生活史策略是**故意拉开**的，这样世界从第 0 tick 就同时存在
    /// "少生精养"和"多生散养"两种对策，演化立刻有原材料可比。
    /// 每个原型的点数都明显低于 20 点预算 —— 留出余地让后代去填。
    /// </summary>
    private static void SetLife(Genome g, int lifespan, int reproInterval, int juvenile, int clutch, int shell)
    {
        g.Life[(int)LifeTrait.Lifespan] = lifespan;
        g.Life[(int)LifeTrait.ReproInterval] = reproInterval;
        g.Life[(int)LifeTrait.JuvenileVitality] = juvenile;
        g.Life[(int)LifeTrait.ClutchSize] = clutch;
        g.Life[(int)LifeTrait.EggShell] = shell;
    }

    public static Genome CreateArchetype(int index, Rng rng, int speciesId)
    {
        var g = new Genome
        {
            SpeciesId = speciesId,
            Name = ArchetypeNames[Math.Clamp(index, 0, ArchetypeNames.Length - 1)],
            Hue = (index / (float)ArchetypeNames.Length + rng.Range(-0.03f, 0.03f) + 1f) % 1f,
            Generation = 0,
            MutationRate = 0.35f,
            RuleMutationRate = 0.18f,
        };

        switch (Math.Clamp(index, 0, ArchetypeNames.Length - 1))
        {
            // 0 —— 食草者：均衡的通用觅食者，生态基准
            case 0:
                g.Organs[(int)OrganType.Legs] = 1;
                g.Organs[(int)OrganType.Eye] = 1;
                g.Organs[(int)OrganType.Stomach] = 1;
                SetLife(g, 2, 1, 1, 1, 0);
                g.Rules.Add(Rule_IfFoodHere(0.12f));
                g.Rules.Add(new Rule(
                    new[]
                    {
                        new Condition(SensorId.EnemyDistance, CompareOp.Less, 0.25f),
                        new Condition(SensorId.HealthPct, CompareOp.Less, 0.70f),
                    },
                    new[] { new ActionSpec(ActionId.Move, AimId.AwayFromEnemy) }));
                g.Rules.Add(Rule_IfEnergyHigh(0.82f));
                g.Rules.Add(Rule_SeekFood());
                g.Rules.Add(Rule_Wander());
                break;

            // 1 —— 疾行者：跑得快、看得远，靠速度活命
            case 1:
                g.Organs[(int)OrganType.Legs] = 3;
                g.Organs[(int)OrganType.Eye] = 2;
                g.Organs[(int)OrganType.Stomach] = 1;
                SetLife(g, 1, 1, 0, 1, 0);
                g.Rules.Add(Rule_IfFoodHere(0.15f));
                g.Rules.Add(new Rule(
                    new[]
                    {
                        new Condition(SensorId.EnemyDistance, CompareOp.Less, 0.55f),
                        new Condition(SensorId.HealthPct, CompareOp.Less, 0.92f),
                    },
                    new[] { new ActionSpec(ActionId.Sprint, AimId.AwayFromEnemy, 0, 0.9f) }));
                g.Rules.Add(Rule_IfEnergyHigh(0.80f));
                g.Rules.Add(Rule_SeekFood());
                g.Rules.Add(Rule_Wander());
                break;

            // 2 —— 掠食者：牙 + 腿 + 眼，把别人的身体当饭吃
            case 2:
                g.Organs[(int)OrganType.Legs] = 2;
                g.Organs[(int)OrganType.Eye] = 2;
                g.Organs[(int)OrganType.Fang] = 2;
                g.Organs[(int)OrganType.Stomach] = 1;
                SetLife(g, 1, 0, 2, 0, 1);
                g.Rules.Add(Rule_IfFoodHere(0.25f));
                g.Rules.Add(Rule_IfEnergyHigh(0.86f));
                g.Rules.Add(Rule_RaidEnemyEggs(0.45f));
                g.Rules.Add(new Rule(
                    new[]
                    {
                        new Condition(SensorId.EnemyDistance, CompareOp.Less, 0.90f),
                        new Condition(SensorId.EnergyPct, CompareOp.Less, 0.78f),
                    },
                    new[] { new ActionSpec(ActionId.Attack, AimId.NearestEnemy) }));
                g.Rules.Add(Rule_SeekFood());
                g.Rules.Add(Rule_Wander());
                break;

            // 3 —— 甲士：重装甲、带棘，站着不走硬碰硬
            case 3:
                g.Organs[(int)OrganType.Legs] = 1;
                g.Organs[(int)OrganType.Eye] = 1;
                g.Organs[(int)OrganType.Plate] = 3;
                g.Organs[(int)OrganType.Spine] = 2;
                g.Organs[(int)OrganType.Stomach] = 1;
                SetLife(g, 3, 0, 1, 0, 2);
                g.Rules.Add(Rule_IfFoodHere(0.15f));
                g.Rules.Add(new Rule(
                    new[]
                    {
                        new Condition(SensorId.EnemyDistance, CompareOp.Less, 0.40f),
                        new Condition(SensorId.HealthPct, CompareOp.Less, 0.95f),
                    },
                    new[] { new ActionSpec(ActionId.Attack, AimId.NearestEnemy) }));
                g.Rules.Add(Rule_IfEnergyHigh(0.80f));
                g.Rules.Add(Rule_SeekFood());
                g.Rules.Add(new Rule(
                    new[] { new Condition(SensorId.Always, CompareOp.Greater, 0f) },
                    new[] { new ActionSpec(ActionId.Rest) }));
                break;

            // 4 —— 光合者：不觅食，靠藻胞晒太阳，白天增生夜里硬扛
            case 4:
                g.Organs[(int)OrganType.Legs] = 1;
                g.Organs[(int)OrganType.Eye] = 1;
                g.Organs[(int)OrganType.Algae] = 3;
                g.Organs[(int)OrganType.Stomach] = 1;
                SetLife(g, 3, 1, 1, 0, 0);
                g.Rules.Add(Rule_IfFoodHere(0.20f));
                g.Rules.Add(Rule_IfEnergyHigh(0.85f));
                g.Rules.Add(new Rule(
                    new[]
                    {
                        new Condition(SensorId.DayPhase, CompareOp.Greater, 0.62f),
                        new Condition(SensorId.EnergyPct, CompareOp.Less, 0.95f),
                    },
                    new[] { new ActionSpec(ActionId.Rest) }));
                g.Rules.Add(Rule_SeekFood());
                g.Rules.Add(Rule_Wander());
                break;

            // 5 —— 群居者：跟着同族走，靠数量分摊风险
            default:
                g.Organs[(int)OrganType.Legs] = 1;
                g.Organs[(int)OrganType.Eye] = 2;
                g.Organs[(int)OrganType.Stomach] = 1;
                g.Organs[(int)OrganType.Gonad] = 2;
                SetLife(g, 1, 2, 0, 3, 2);
                g.Rules.Add(Rule_IfFoodHere(0.12f));
                g.Rules.Add(Rule_GuardAllyEggs(0.55f));
                g.Rules.Add(new Rule(
                    new[] { new Condition(SensorId.EnemyDistance, CompareOp.Less, 0.30f) },
                    new[] { new ActionSpec(ActionId.Move, AimId.AwayFromEnemy) }));
                g.Rules.Add(Rule_IfEnergyHigh(0.74f));
                g.Rules.Add(Rule_SeekFood());
                g.Rules.Add(new Rule(
                    new[]
                    {
                        new Condition(SensorId.AllyCount, CompareOp.GreaterEqual, 0.10f),
                        new Condition(SensorId.Crowding, CompareOp.Less, 0.35f),
                    },
                    new[] { new ActionSpec(ActionId.Move, AimId.NearestAlly) }));
                g.Rules.Add(Rule_Wander());
                break;
        }

        return g;
    }

    // ---- 规则小工厂：让上面的原型定义读起来像自然语言 ----

    private static Rule Rule_IfFoodHere(float threshold)
        => new(new[] { new Condition(SensorId.FoodHere, CompareOp.Greater, threshold) },
               new[] { new ActionSpec(ActionId.Eat) });

    private static Rule Rule_IfEnergyHigh(float threshold)
        => new(new[] { new Condition(SensorId.EnergyPct, CompareOp.Greater, threshold) },
               new[] { new ActionSpec(ActionId.Reproduce) });

    private static Rule Rule_SeekFood()
        => new(new[] { new Condition(SensorId.FoodDistance, CompareOp.Less, 1.0f) },
               new[] { new ActionSpec(ActionId.Move, AimId.NearestFood) });

    private static Rule Rule_Wander()
        => new(new[] { new Condition(SensorId.Always, CompareOp.Greater, 0f) },
               new[] { new ActionSpec(ActionId.Move, AimId.Random) });

    /// <summary>抢劫：视野里有敌方卵就去踩碎它换一顿饭。卵不会跑，是最划算的猎物。</summary>
    private static Rule Rule_RaidEnemyEggs(float range)
        => new(new[]
            {
                new Condition(SensorId.EnemyEggDistance, CompareOp.Less, range),
                new Condition(SensorId.HealthPct, CompareOp.Greater, 0.55f),
            },
            new[] { new ActionSpec(ActionId.EatEgg, AimId.NearestEnemyEgg) });

    /// <summary>护卵：守在自己人的卵旁边，顺手把摸过来的东西打掉。</summary>
    private static Rule Rule_GuardAllyEggs(float range)
        => new(new[] { new Condition(SensorId.AllyEggDistance, CompareOp.Less, range) },
               new[]
               {
                   new ActionSpec(ActionId.Incubate, AimId.NearestAllyEgg),
                   new ActionSpec(ActionId.Attack, AimId.NearestEnemy),
               });
}
