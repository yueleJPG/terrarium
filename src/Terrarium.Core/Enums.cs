namespace Terrarium.Core;

/// <summary>
/// 器官类型。每个器官都是「能量开销 ↔ 功能」的交换，并且互相耦合。
/// 索引顺序是存档格式的一部分，不要重排，只能在末尾追加。
/// </summary>
public enum OrganType
{
    /// <summary>腿：提高速度，但增加体重。</summary>
    Legs = 0,
    /// <summary>眼：提高视野半径，决定能否"看见"目标。</summary>
    Eye = 1,
    /// <summary>牙：提高攻击力。</summary>
    Fang = 2,
    /// <summary>甲：减伤，但很重（拖慢速度）。</summary>
    Plate = 3,
    /// <summary>胃：提高最大能量与消化效率。</summary>
    Stomach = 4,
    /// <summary>藻胞：光合自养，不用觅食也能缓慢获得能量；代价是怕黑、被啃时损失更大。</summary>
    Algae = 5,
    /// <summary>毒腺：攻击附带持续伤害。</summary>
    Gland = 6,
    /// <summary>生殖腺：降低繁殖阈值、提高后代初始资源。</summary>
    Gonad = 7,
    /// <summary>鳃：可在水域生存而不窒息。</summary>
    Gill = 8,
    /// <summary>棘：被攻击时反伤。</summary>
    Spine = 9,
}

/// <summary>
/// 生活史性状。与器官共用同一个点数预算 —— 这是"把点数花在身体上还是花在繁殖策略上"
/// 这个根本取舍的载体。
///
/// 为什么要把这几项从全局配置里搬出来：第一版里「寿命」和「繁殖间隔」是写死的全局常量
/// （3600 / 80），所有生物完全一样，基因组里根本没有这两项 —— 也就是说它们**完全不可演化**。
/// 生态里最要紧的几个轴（活多久、多久生一次、投多少资源给后代）全都是死数，
/// 演化自然只能在一个很窄的空间里打转。
/// </summary>
public enum LifeTrait
{
    /// <summary>寿命：自然寿命上限。</summary>
    Lifespan = 0,
    /// <summary>繁殖间隔：两次产卵之间的冷却。</summary>
    ReproInterval = 1,
    /// <summary>幼体生命力：幼体期的属性保留比例与成熟速度。</summary>
    JuvenileVitality = 2,
    /// <summary>窝卵数：一次产几颗卵。投入总量固定，所以卵越多每颗越弱 —— 天然的 r/K 策略轴。</summary>
    ClutchSize = 3,
    /// <summary>卵壳强度：卵能扛住几次非同族的踩踏。0 级一踩就碎，这是卵生策略的防御投资。</summary>
    EggShell = 4,
}

/// <summary>比较算子。规则条件形如 Sensor Op Value。</summary>
public enum CompareOp
{
    Less = 0,
    LessEqual = 1,
    Greater = 2,
    GreaterEqual = 3,
}

/// <summary>
/// 传感器池：生物能"感知"到的标量。全部归一化到大致 [0,1] 或 [-1,1]，
/// 这样变异时的数值微扰有一个统一的尺度，不会因为量纲不同而失效。
/// </summary>
public enum SensorId
{
    /// <summary>恒为 1 —— 用来表达"其他条件都满足就做这件事"。</summary>
    Always = 0,
    /// <summary>能量 / 能量上限。</summary>
    EnergyPct = 1,
    /// <summary>生命值百分比。</summary>
    HealthPct = 2,
    /// <summary>年龄 / 寿命上限。</summary>
    AgePct = 3,
    /// <summary>最近食物距离 / 视野半径（看不见时为 1）。</summary>
    FoodDistance = 4,
    /// <summary>最近食物方位，-1=正左后方，0=正前方，1=正右后方。</summary>
    FoodBearing = 5,
    /// <summary>最近敌人距离 / 视野半径（看不见时为 1）。</summary>
    EnemyDistance = 6,
    /// <summary>最近敌人方位。</summary>
    EnemyBearing = 7,
    /// <summary>同族数量 / 10，封顶 1。</summary>
    AllyCount = 8,
    /// <summary>视野内敌人数量 / 10，封顶 1。</summary>
    Threat = 9,
    /// <summary>视野内生物总数 / 20，封顶 1 —— 用来表达"拥挤"。</summary>
    Crowding = 10,
    /// <summary>自己的搏斗能力（牙 + 棘 + 体型换算）/ 归一化。</summary>
    CombatPower = 11,
    /// <summary>自己当前所在格子的食物量 0..1。</summary>
    FoodHere = 12,
    /// <summary>自己是否泡在水里（0/1）。</summary>
    InWater = 13,
    /// <summary>自己是否有鳃（0/1）。</summary>
    HasGill = 14,
    /// <summary>自己是否在承受毒伤（0/1）。</summary>
    Poisoned = 15,
    /// <summary>是否撞到世界边界（0/1）。</summary>
    AtEdge = 16,
    /// <summary>纯噪声 0..1 —— 给演化一个"随机行为"的出口，往往会被淘汰或利用。</summary>
    Noise = 17,
    /// <summary>自己躯体质量 / 4，封顶 1。</summary>
    Mass = 18,
    /// <summary>当前 tick 数的周期分量，用于昼夜节律。</summary>
    DayPhase = 19,
    /// <summary>正前方到最近不可通行地形的距离，归一化。1 = 前方畅通，0 = 紧贴障碍。
    /// 演化要靠它学会避障 —— 没有这个传感器，生物会一头撞进岩壁卡死。</summary>
    ObstacleAhead = 20,
    /// <summary>脚下地形的通行代价（1.0 基准 → 归一化）。沼泽 / 灌木会明显偏高。</summary>
    TerrainCost = 21,
    /// <summary>自己是否躲在灌木丛里（0/1）。灌木同时会砍半视野。</summary>
    InBush = 22,
    /// <summary>被不可通行地形围困的程度 0..1。0 = 周围开阔，1 = 陷在死角里。</summary>
    EscapePressure = 23,

    // ---- 卵与幼体（有了卵生机制之后才存在的东西）----
    /// <summary>最近的卵距离（不分敌友）/ 视野半径，看不见时为 1。</summary>
    EggDistance = 24,
    /// <summary>最近的**敌方**卵距离。卵是固定不动的，是幼体最脆弱的那段时间。</summary>
    EnemyEggDistance = 25,
    /// <summary>最近的**友方**卵距离。用它来写"回防自己的卵"。</summary>
    AllyEggDistance = 26,
    /// <summary>视野内卵的总数 / 10，封顶 1。</summary>
    EggCount = 27,
    /// <summary>视野内敌方卵数量 / 10，封顶 1。</summary>
    EnemyEggCount = 28,
    /// <summary>视野内幼体数量 / 10，封顶 1。</summary>
    JuvenileCount = 29,
    /// <summary>自己的成熟度 0..1。0 = 刚孵化，1 = 成体。</summary>
    Maturity = 30,
    /// <summary>自己是不是幼体（0/1）。</summary>
    IsJuvenile = 31,
    /// <summary>产卵冷却已完成的百分比 0..1。1 = 随时可以产卵。</summary>
    LayReady = 32,
}

/// <summary>方向选择器。动作需要"朝哪儿"时从这些候选里挑一个。</summary>
public enum AimId
{
    Forward = 0,
    Random = 1,
    NearestFood = 2,
    NearestEnemy = 3,
    AwayFromEnemy = 4,
    NearestAlly = 5,
    AwayFromAlly = 6,
    WorldCenter = 7,
    CurrentHeading = 8,

    // ---- 卵与幼体 ----
    /// <summary>最近的卵，不分敌友。</summary>
    NearestEgg = 9,
    /// <summary>最近的敌方卵 —— 去踩碎它、吃掉它。</summary>
    NearestEnemyEgg = 10,
    /// <summary>最近的友方卵 —— 回去守着自己的后代。</summary>
    NearestAllyEgg = 11,
    /// <summary>远离最近的敌方卵（护卵时站位用）。</summary>
    AwayFromEnemyEgg = 12,
    /// <summary>最近的幼体，不分敌友。</summary>
    NearestJuvenile = 13,
    /// <summary>最近的敌方幼体 —— 幼体比成体好杀，是最划算的猎物。</summary>
    NearestEnemyJuvenile = 14,
    /// <summary>最近的友方幼体。</summary>
    NearestAllyJuvenile = 15,
    /// <summary>最近的**同物种**个体。
    /// 它和「最近的同族」不是一回事：同族还要行为签名足够接近，同物种只看物种 Id。</summary>
    NearestSameSpecies = 16,
}

/// <summary>
/// 动作池：生物对外部世界能做的事。
/// 关键约束 —— 规则只能通过这些动作与世界交互，不允许任意脚本。
/// 正因为它是封闭枚举，规则图才能被安全地随机变异、交叉和可视化。
/// </summary>
public enum ActionId
{
    /// <summary>什么都不做（省下运动开销）。</summary>
    Idle = 0,
    /// <summary>朝指定方向以基础速度移动。</summary>
    Move = 1,
    /// <summary>朝指定方向冲刺（更快，但能耗按二次方增长）。</summary>
    Sprint = 2,
    /// <summary>攻击指定目标。目标由方向选择器决定，可以是生物，也可以是卵。</summary>
    Attack = 3,
    /// <summary>吃脚下的食物。</summary>
    Eat = 4,
    /// <summary>产卵：能量足够且冷却完毕时，产下一窝卵。</summary>
    Reproduce = 5,
    /// <summary>原地休息，把代谢降到最低。</summary>
    Rest = 6,
    /// <summary>生长指定器官一级。</summary>
    GrowOrgan = 7,
    /// <summary>退化指定器官一级，把资源回收成能量。</summary>
    ShrinkOrgan = 8,
    /// <summary>转向指定方位（不移动，便宜）。</summary>
    Turn = 9,
    /// <summary>吐出一口信息素（预留接口）。</summary>
    EmitPheromone = 10,

    // ---- 卵相关 ----
    /// <summary>吃掉目标卵，把里面的营养转化为能量。</summary>
    EatEgg = 11,
    /// <summary>趴在相邻的友方卵上加速孵化。</summary>
    Incubate = 12,
}
