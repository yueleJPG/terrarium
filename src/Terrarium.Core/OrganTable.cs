namespace Terrarium.Core;

/// <summary>
/// 器官的属性表。这张表就是「演化的物理法则」——
/// 改这里的数字，等于改整个世界的选择压力，比改代码逻辑有效得多。
/// 调参时优先动这里。
/// </summary>
public static class OrganTable
{
    public const int Count = 10;
    public const int MaxLevel = 5;

    public static readonly string[] Id =
    {
        "legs", "eye", "fang", "plate", "stomach", "algae", "gland", "gonad", "gill", "spine",
    };

    public static readonly string[] Cn =
    {
        "腿", "眼", "牙", "甲", "胃", "藻胞", "毒腺", "生殖腺", "鳃", "棘",
    };

    public static readonly string[] Desc =
    {
        "提高移动速度。代价：体重上升，且每 tick 消耗能量。",
        "提高视野半径，看得更远才能发现食物和敌人。",
        "提高攻击力，捕食路线的核心器官。",
        "减免受到的伤害。代价：非常重，会显著拖慢速度。",
        "提高能量上限与进食消化效率。",
        "光合自养：每个 tick 被动获得能量。怕暗、体型大时加成更高，但被啃食时损失也更大。",
        "攻击时附加中毒效果，持续掉血。",
        "降低繁殖所需能量阈值，提高后代起始资源。繁殖路线的核心。",
        "可在水域存活，无鳃入水会快速流失能量。",
        "被攻击时反弹一部分伤害给攻击者。",
    };

    /// <summary>1 级时的每 tick 能量开销。</summary>
    public static readonly float[] Upkeep =
    {
        0.0080f, 0.0090f, 0.0120f, 0.0150f, 0.0045f, 0.0020f, 0.0090f, 0.0120f, 0.0070f, 0.0080f,
    };

    /// <summary>
    /// 器官开销随等级的超线性指数。
    ///
    /// 注意：引入 20 点硬上限之后，这一项已经不是"防全能型"的主力了 ——
    /// 硬上限才是。保留一个温和的超线性（1.9 → 1.35）是因为它还能表达
    /// "第 5 级的边际成本比第 1 级高"这件事实，只是不再需要它独自承担全部压力。
    /// </summary>
    public const float UpkeepExponent = 1.35f;

    /// <summary>某个器官在指定等级下的每 tick 开销。</summary>
    public static float UpkeepFor(int organ, int level)
        => level <= 0 ? 0f : Upkeep[organ] * MathF.Pow(level, UpkeepExponent);

    /// <summary>每级对体重的贡献。体重会反过来压低实际速度，并抬高基础代谢。</summary>
    public static readonly float[] Mass =
    {
        0.55f, 0.10f, 0.50f, 2.00f, 0.60f, 0.30f, 0.40f, 0.50f, 0.30f, 1.00f,
    };

    /// <summary>生长一级该器官需要付出的能量。</summary>
    public static readonly float[] GrowCost =
    {
        18f, 14f, 22f, 30f, 12f, 10f, 20f, 26f, 16f, 18f,
    };

    public static int Clamp(int level) => level < 0 ? 0 : (level > MaxLevel ? MaxLevel : level);

    /// <summary>器官等级数组到中文串，用于日志和 UI。</summary>
    public static string Describe(int[] levels)
    {
        var parts = new List<string>();
        for (int i = 0; i < Count; i++)
            if (levels[i] > 0) parts.Add($"{Cn[i]}{levels[i]}");
        return parts.Count == 0 ? "(无器官)" : string.Join(" ", parts);
    }
}

/// <summary>
/// 生活史性状的属性表。
///
/// 这些性状和器官**共用同一个点数预算**，这正是设计意图：
/// "把点数花在身体上（跑得快、咬得狠）还是花在繁殖策略上（活得久、生得多、卵壳硬）"
/// 是每个物种必须回答的问题，而且答案会直接决定它在生态里的位置。
/// </summary>
public static class LifeTable
{
    public const int Count = 5;
    public const int MaxLevel = 5;

    public static readonly string[] Id =
    {
        "lifespan", "repro_interval", "juvenile_vitality", "clutch_size", "egg_shell",
    };

    public static readonly string[] Cn =
    {
        "寿命", "繁殖间隔", "幼体生命力", "窝卵数", "卵壳强度",
    };

    public static readonly string[] Desc =
    {
        "自然寿命上限。活得久意味着更多次繁殖机会，但要点数，也要每 tick 养着。",
        "两次产卵之间的冷却时间。等级越高冷却越短 —— 这是「多生」路线的核心投资。",
        "幼体期的属性保留比例与成熟速度。高投入的后代一出生就更能打，低投入只能靠数量堆。",
        "一次产几颗卵。总投入固定，所以卵越多每颗越弱 —— 这就是 r/K 策略轴。",
        "卵能扛住几次非同族的踩踏。0 级一踩就碎；卵是固定不动的，所以这是卵生路线唯一的防御。",
    };

    /// <summary>1 级时的每 tick 能量开销。</summary>
    public static readonly float[] Upkeep =
    {
        0.0060f, 0.0065f, 0.0050f, 0.0045f, 0.0035f,
    };

    public static float UpkeepFor(int trait, int level)
        => level <= 0 ? 0f : Upkeep[trait] * MathF.Pow(level, OrganTable.UpkeepExponent);

    /// <summary>
    /// 寿命的实际 tick 数。0 级只有基础寿命的四成 —— 不投资就是短命。
    /// </summary>
    public static int LifespanFor(int level, int baseLifespan)
        => (int)(baseLifespan * (0.40f + level * 0.30f));

    /// <summary>
    /// 产卵冷却的实际 tick 数。0 级最长，5 级约为基础值的四成。
    /// </summary>
    public static int ReproIntervalFor(int level, int baseCooldown)
        => (int)(baseCooldown * (1.60f - level * 0.22f));

    /// <summary>幼体属性保留的下限。0 级 = 刚孵化只有成体的 25%，5 级 = 70%。</summary>
    public static float JuvenileFloorFor(int level) => 0.25f + level * 0.09f;

    /// <summary>成熟所需时间的倍率。等级越高成熟越快。</summary>
    public static float MaturityTimeFor(int level) => 1.55f - level * 0.15f;

    /// <summary>一次产几颗卵。</summary>
    public static int ClutchFor(int level) => 1 + level;

    /// <summary>
    /// 卵能扛住几次非同族的踩踏。**这就是玩家说的「踩五次就销毁」那个数字** ——
    /// 它不是全局配置，而是买来的：0 级 1 次，每级 +2，2 级正好 5 次。
    /// </summary>
    public static int EggTrampleLimitFor(int level) => 1 + level * 2;

    public static int Clamp(int level) => level < 0 ? 0 : (level > MaxLevel ? MaxLevel : level);
}

/// <summary>
/// 统一的性状表：10 个器官 + 5 个生活史性状 = 15 项，共用 20 点预算。
///
/// 为什么要有这一层：点数预算必须能同时看见器官和生活史性状，
/// 而它们存在两个不同的数组里。UI 的下拉框、突变算子、点数统计都走这里，
/// 免得三处各写一遍索引换算、改一处漏两处。
/// </summary>
public static class TraitTable
{
    public const int Count = OrganTable.Count + LifeTable.Count;   // 15
    public const int MaxLevel = 5;

    /// <summary>索引 &lt; OrganTable.Count 是器官，否则是生活史性状。</summary>
    public static bool IsOrgan(int i) => i < OrganTable.Count;

    public static string Cn(int i)
        => IsOrgan(i) ? OrganTable.Cn[i] : LifeTable.Cn[i - OrganTable.Count];

    public static string Id(int i)
        => IsOrgan(i) ? OrganTable.Id[i] : LifeTable.Id[i - OrganTable.Count];

    public static string Desc(int i)
        => IsOrgan(i) ? OrganTable.Desc[i] : LifeTable.Desc[i - OrganTable.Count];

    public static int Clamp(int level) => level < 0 ? 0 : (level > MaxLevel ? MaxLevel : level);
}

/// <summary>传感器元数据：给 UI 卡片下拉框用。</summary>
public static class SensorTable
{
    public static readonly string[] Cn =
    {
        "永远是", "我的能量%", "我的生命%", "我的年龄%",
        "最近食物距离", "最近食物方位", "最近敌人距离", "最近敌人方位",
        "同族数量", "周围敌人数量", "周围拥挤度", "我的战力",
        "脚下食物量", "我在水里", "我有鳃", "我正在中毒",
        "我撞到边界", "随机噪声", "我的体重", "昼夜相位",
        "前方障碍距离", "脚下地形代价", "我躲在灌木里", "被围困程度",
        "最近卵距离", "最近敌方卵距离", "最近友方卵距离",
        "视野内卵数量", "视野内敌方卵数量", "视野内幼体数量",
        "我的成熟度", "我是幼体", "产卵已就绪",
    };

    /// <summary>该传感器的取值范围，UI 上用来决定数值输入框的步进和滑块范围。</summary>
    public static readonly (float Min, float Max)[] Range =
    {
        (1f, 1f), (0f, 1f), (0f, 1f), (0f, 1f),
        (0f, 1f), (-1f, 1f), (0f, 1f), (-1f, 1f),
        (0f, 1f), (0f, 1f), (0f, 1f), (0f, 1f),
        (0f, 1f), (0f, 1f), (0f, 1f), (0f, 1f),
        (0f, 1f), (0f, 1f), (0f, 1f), (0f, 1f),
        (0f, 1f), (0f, 1f), (0f, 1f), (0f, 1f),
        (0f, 1f), (0f, 1f), (0f, 1f),
        (0f, 1f), (0f, 1f), (0f, 1f),
        (0f, 1f), (0f, 1f), (0f, 1f),
    };

    public static int Count => Cn.Length;

    /// <summary>
    /// 距离类传感器统一走这里：看不见时返回 1，看见时返回归一化距离。
    /// 三种"最近的卵"都要用它，复制三遍迟早会有一处忘了处理"没找到"而返回 0 ——
    /// 那会让"最近的敌方卵 ≤ 0.1"这种条件在根本没有卵的时候恒为真。
    /// </summary>
    public static float Dist01(bool found, float dist, float vision)
        => !found || vision <= 0.001f ? 1f : Math.Clamp(dist / vision, 0f, 1f);

    /// <summary>方位类传感器用正负比较更自然，UI 上显示"左/正前/右"。</summary>
    public static bool IsBearing(SensorId s)
        => s == SensorId.FoodBearing || s == SensorId.EnemyBearing;
}

/// <summary>动作元数据：给 UI 卡片下拉框用。</summary>
public static class ActionTable
{
    public static readonly string[] Cn =
    {
        "什么都不做", "移动", "冲刺", "攻击目标", "吃脚下的食物",
        "产卵", "原地休息", "生长性状", "退化性状", "转向", "释放信息素",
        "吃卵", "孵卵",
    };

    public static readonly string[] Desc =
    {
        "完全静止，代谢最低。",
        "朝指定方向以基础速度移动，能耗与距离和体重成正比。",
        "朝指定方向冲刺，速度更快但能耗按平方增长。",
        "攻击方向选择器选中的目标（生物或卵），造成伤害并夺取部分能量。",
        "摄食脚下的食物格，把它转化为能量。",
        "能量达到阈值且冷却完毕时，产下一窝卵。窝卵数由性状决定。",
        "原地休息，本 tick 代谢减半。",
        "消耗能量提升指定性状一级（最高 5 级，总点数不能超过预算）。",
        "退化指定性状一级，回收部分能量并腾出点数。",
        "只转向不移动，代价极低。",
        "释放信息素（预留接口）。",
        "吃掉方向选择器选中的卵，把里面的营养转化为能量。",
        "趴在相邻的友方卵上，加速它的孵化。",
    };

    public static int Count => Cn.Length;

    /// <summary>
    /// 可以被随机变异选中的动作。
    ///
    /// 刻意排除还没实装的"释放信息素"：一个什么都不做的动作在演化里是纯中性噪声，
    /// 生物既不会因此变好也不会变差，于是它会在基因组里越积越多，
    /// 玩家打开详情面板看到一堆"释放信息素"的规则却看不出任何行为。
    /// </summary>
    public static readonly ActionId[] MutablePool =
    {
        ActionId.Idle, ActionId.Move, ActionId.Sprint, ActionId.Attack, ActionId.Eat,
        ActionId.Reproduce, ActionId.Rest, ActionId.GrowOrgan, ActionId.ShrinkOrgan, ActionId.Turn,
        ActionId.EatEgg, ActionId.Incubate,
    };

    /// <summary>该动作是否需要"朝哪儿/对谁"这个参数。</summary>
    public static bool NeedsAim(ActionId a)
        => a == ActionId.Move || a == ActionId.Sprint
        || a == ActionId.Attack || a == ActionId.Turn
        || a == ActionId.EatEgg || a == ActionId.Incubate;

    /// <summary>该动作是否需要指定性状。GrowOrgan/ShrinkOrgan 现在管全部 15 项性状。</summary>
    public static bool NeedsOrgan(ActionId a)
        => a == ActionId.GrowOrgan || a == ActionId.ShrinkOrgan;

    /// <summary>该动作是否需要额外的力度/时长参数。</summary>
    public static bool NeedsParam(ActionId a)
        => a == ActionId.Sprint || a == ActionId.EmitPheromone;
}

/// <summary>方向选择器元数据。</summary>
public static class AimTable
{
    public static readonly string[] Cn =
    {
        "正前方", "随机方向", "最近的食物", "最近的敌人",
        "远离最近的敌人", "最近的同族", "远离最近的同族", "世界中心", "保持朝向",
        "最近的卵", "最近的敌方卵", "最近的友方卵", "远离敌方卵",
        "最近的幼体", "最近的敌方幼体", "最近的友方幼体", "最近的同物种",
    };

    public static int Count => Cn.Length;

    /// <summary>该方向是否指向一个"卵"（决定攻击/吃卵作用在什么上）。</summary>
    public static bool TargetsEgg(AimId a)
        => a == AimId.NearestEgg || a == AimId.NearestEnemyEgg || a == AimId.NearestAllyEgg
        || a == AimId.AwayFromEnemyEgg;

    /// <summary>该方向是否指向一个"幼体"。</summary>
    public static bool TargetsJuvenile(AimId a)
        => a == AimId.NearestJuvenile || a == AimId.NearestEnemyJuvenile || a == AimId.NearestAllyJuvenile;
}
