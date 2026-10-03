namespace Terrarium.Core;

/// <summary>
/// 全部可调参数。这是「生态箱的旋钮」——
/// UI 上的环境面板直接绑定这些字段，headless 的实验脚本也改这些字段。
/// 想改变选择压力就去改这里，而不是去改模拟逻辑。
/// </summary>
public sealed class SimConfig
{
    // ---------- 世界 ----------
    // 长宽比刻意接近常见窗口比例：这样"全景"视图能铺满画面，
    // 而不是在左右留下两条大黑边。
    public float WorldWidth { get; set; } = 3200f;
    public float WorldHeight { get; set; } = 2000f;
    public float TileSize { get; set; } = 20f;

    /// <summary>人口硬上限，纯粹为了保护性能，不是生态参数。</summary>
    public int MaxPopulation { get; set; } = 2600;

    public int InitialPopulation { get; set; } = 420;

    // ---------- 食物经济 ----------
    /// <summary>每 tile 每 tick 的食物再生量（乘以肥力）。这是最敏感的一个旋钮。</summary>
    public float FoodRegen { get; set; } = 0.00065f;

    /// <summary>UI 上的"丰饶度"倍率，用来快速做对照实验。</summary>
    public float FoodRichness { get; set; } = 1.0f;

    /// <summary>1 单位食物折算多少能量。</summary>
    public float FoodEnergy { get; set; } = 42f;

    /// <summary>
    /// 肥沃地块占比（斑块噪声的高分位比例）。
    /// 注意：这个值调低并不一定会加强竞争 —— 实测 0.55 反而比 0.22 的捕食更频繁，
    /// 因为种群密度本身才是竞争强度的主要来源。别凭直觉调它，要跑 --repeat 对照。
    /// </summary>
    public float FertileFraction { get; set; } = 0.22f;

    /// <summary>山地（不可通行岩石）占比。这是地理隔离与物种分化的主要来源。</summary>
    public float MountainFraction { get; set; } = 0.07f;

    /// <summary>灌木丛（森林）占比。通行慢、视野减半，制造伏击生态位。</summary>
    public float ForestFraction { get; set; } = 0.16f;

    /// <summary>荒漠占比。贫瘠空旷，筛选耐饿与迁徙能力。</summary>
    public float DesertFraction { get; set; } = 0.12f;

    /// <summary>沼泽占比（最湿的低地）。</summary>
    public float SwampFraction { get; set; } = 0.05f;

    // ---------- 代谢 ----------
    public float BaseUpkeep { get; set; } = 0.010f;

    /// <summary>
    /// 每条"多余"规则（超过 RuleFreeAllowance 条之后）的每 tick 维持开销。
    ///
    /// 没有这个成本，规则数会一路膨胀到十几条，其中大半是永不触发的垃圾规则在
    /// 做中性漂移 —— 基因组会变得又长又没意义，玩家打开详情面板看到一堆噪声。
    /// 生物学上也站得住脚：神经组织是昂贵的。有了它，规则会被精简到真正有用的那几条。
    /// </summary>
    public float RuleUpkeep { get; set; } = 0.0030f;
    public int RuleFreeAllowance { get; set; } = 3;

    public float MoveCostPerUnit { get; set; } = 0.0022f;
    public float SprintMultiplier { get; set; } = 1.75f;
    public float SprintExtraCost { get; set; } = 1.6f;
    public float RestUpkeepFactor { get; set; } = 0.45f;
    public float IdleUpkeepFactor { get; set; } = 0.25f;

    /// <summary>自然寿命的**基础值**（tick）。实际寿命 = 基础值 × 寿命性状的倍率 ——
    /// 以前它是所有生物共用的死数，现在只是 0 级性状的参照点。
    /// 这个值同时决定了世代更替速度：寿命太长会让老个体占着资源不开花，演化变慢。</summary>
    public int Lifespan { get; set; } = 3600;

    // ==================================================================
    // 点数预算：本作最核心的一条约束
    // ==================================================================

    /// <summary>
    /// 性状点数总预算。15 个性状共享这 20 点，**不可超过**。
    ///
    /// 可以用满也可以用不满 —— 用不满的生物更轻、每 tick 开销更低，
    /// 于是"躺平省钱"从一种隐性行为变成了一张明牌。
    ///
    /// 这是全局可调参数（环境面板里能改）。调它等于调整整个世界的特化压力：
    /// 调小 → 极端专精，调大 → 全能型重新出现。
    /// </summary>
    public int PointBudget { get; set; } = 20;

    // ==================================================================
    // 卵生
    // ==================================================================

    /// <summary>基础孵化时间（tick）。</summary>
    public int EggHatchTicks { get; set; } = 240;

    /// <summary>两次被踩之间的最小间隔。没有它，一只生物站在卵上就能一 tick 踩满次数。</summary>
    public int EggTrampleCooldown { get; set; } = 18;

    /// <summary>产卵时投入的能量占自身能量的比例（这是总投入，会平摊到整窝卵上）。</summary>
    public float EggInvestment { get; set; } = 0.55f;

    /// <summary>每 tick「孵卵」动作能替相邻友方卵省下多少孵化时间。</summary>
    public int EggIncubateSpeedup { get; set; } = 4;

    /// <summary>吃一颗卵能拿到它多少比例的能量。</summary>
    public float EggEatGain { get; set; } = 0.85f;

    /// <summary>全场卵的数量上限。卵固定不动，没有上限会无限堆积并拖垮空间索引 ——
    /// 和当初"4.6 万个物种把速度打掉 80%"是同一类坑。</summary>
    public int MaxEggs { get; set; } = 4000;

    /// <summary>踩踏判定半径系数（乘生物体径）。</summary>
    public float EggTrampleRadiusFactor { get; set; } = 0.9f;

    // ==================================================================
    // 幼体
    // ==================================================================

    /// <summary>基础成熟时间（tick）。实际值 = 基础值 × 幼体生命力性状的倍率。</summary>
    public int MaturityTicks { get; set; } = 700;

    /// <summary>幼体能不能繁殖。</summary>
    public bool JuvenileCanReproduce { get; set; }

    /// <summary>幼体的每 tick 开销系数（小身体代谢低）。</summary>
    public float JuvenileUpkeepFactor { get; set; } = 0.65f;

    /// <summary>尸体能量折算成食物归还到地块的比例（只是能量转移，不是净注入）。</summary>
    public float CorpseConversion { get; set; } = 0.45f;
    /// <summary>尸体按体重的净注入量。这是全系统唯一的净注入项（除了植物），必须很小。</summary>
    public float CorpseMassBonus { get; set; } = 1.0f;

    // ---------- 繁殖 ----------
    public float ReproCostFactor { get; set; } = 0.68f;
    public float ChildEnergyFactor { get; set; } = 0.46f;
    /// <summary>产卵冷却的**基础值**。实际冷却 = 基础值 × 繁殖间隔性状的倍率。</summary>
    public int ReproCooldown { get; set; } = 80;

    /// <summary>
    /// 物种分化阈值：以「平均亲子指纹距离」为单位的**倍数**。
    ///
    /// 为什么是倍数而不是绝对值：指纹距离的尺度强烈依赖变异强度、规则数、器官数。
    /// 第一版用绝对阈值 0.055，结果 6 万 tick 里裂出 28597 个物种 ——
    /// 几乎每次繁殖都算新种，"物种"彻底失去意义。
    ///
    /// 改成"子代必须比一次典型突变偏离 8 倍才算新种"之后，阈值会自动跟着
    /// 变异强度、世界参数、规则复杂度一起缩放，不再需要手工重调。
    /// 这就是「物种粒度」旋钮：调小 = 很多细分种，调大 = 少数大类。
    /// </summary>
    public float SpeciationFactor { get; set; } = 3.5f;

    /// <summary>亲缘容忍度的 bit 上限。行为签名两两平均差约 20 bit，
    /// 阈值超过 ~16 就等于"谁都算同类"，捕食会停摆。</summary>
    public int KinToleranceMaxBits { get; set; } = 18;

    /// <summary>初始食性宽容度。0.5 左右是个中性起点，让演化自己选方向。</summary>
    public float InitialCannibalism { get; set; } = 0.30f;

    /// <summary>
    /// 食性宽容度的每 tick 开销（乘以宽容度）。
    ///
    /// 没有这项，宽容度会无成本地涨到 1.0 —— 因为"谁都能吃"显然更划算，
    /// 于是全世界变成人人相食的绞肉机，亲缘结构彻底消失。
    /// 生物学上也说得通：广食性需要更强的消化系统与免疫系统。
    /// </summary>
    public float CannibalismUpkeep { get; set; } = 0.030f;

    /// <summary>物种图鉴最多保留多少条记录。超出后淘汰峰值人口最低的已灭绝物种。</summary>
    public int MaxCodexEntries { get; set; } = 400;

    /// <summary>物种统计（领地、种群计数）的刷新间隔，单位 tick。</summary>
    public int SpeciesRefreshInterval { get; set; } = 30;

    // ---------- 搏斗 ----------
    /// <summary>攻击距离 / 自身体径。1.0 表示"够到自己身体边缘"，1.2 留一点余量。
    /// 大块头因此天然拥有更长的攻击范围 —— 这是体重的一项真实收益。</summary>
    public float AttackReachFactor { get; set; } = 1.25f;

    /// <summary>攻击判定的最小距离下限，防止极小体型导致完全够不到。</summary>
    public float MinAttackReach { get; set; } = 12f;
    public float AttackDamageScale { get; set; } = 0.62f;
    /// <summary>每次攻击 tick 的能量开销（乘以牙等级的加成）。攻击必须真的贵，
    /// 否则"见谁咬谁"没有代价，种群会退化成绞肉机。</summary>
    public float AttackCost { get; set; } = 0.045f;
    /// <summary>够不着时扑咬的加速倍率 —— 必须明显大于 1，否则追击永远追不上逃跑，
    /// 捕食行为就演化不出来（这是让"搏斗"真正发生的关键参数）。</summary>
    public float AttackLungeMultiplier { get; set; } = 1.60f;
    /// <summary>击杀后夺取猎物的能量比例。捕食是**转移**而不是创造。</summary>
    public float PredationGain { get; set; } = 0.55f;
    /// <summary>击杀的额外奖励，按猎物**体重的平方根**换算，代表"那具身体的肉"。
    /// 刻意压得很小：这一项是净注入，给大了就会让"多杀"变成压倒性最优解。</summary>
    public float PredationFlatBonus { get; set; } = 4.0f;
    public float PoisonPerLevel { get; set; } = 0.9f;
    public float PoisonDamage { get; set; } = 0.055f;
    public float PoisonDecay { get; set; } = 0.972f;

    // ---------- 性能 ----------
    /// <summary>并行感知-决策的最小个体数。太少时并行的调度开销大于收益。</summary>
    public int ParallelSenseThreshold { get; set; } = 96;

    /// <summary>并行度上限。0 = 交给运行时决定（通常等于逻辑核心数）。</summary>
    public int MaxParallelism { get; set; } = 0;

    // ---------- 碰撞与寻路 ----------
    /// <summary>是否让生物拥有实体碰撞体积（互相推开）。关掉就退回"点实体"模型。</summary>
    public bool BodyCollision { get; set; } = true;

    /// <summary>单个 tick 里身体被推开的位移上限。防止密集重叠时一次性弹飞，造成画面抖动。</summary>
    public float MaxSeparationPerTick { get; set; } = 1.6f;

    /// <summary>身体重叠每单位带来的额外能量开销 —— 拥挤的代谢代价。</summary>
    public float CrowdCost { get; set; } = 0.0022f;

    /// <summary>局部 A* 寻路的每 tick 调用预算。真正做寻路的个体数量被它限制住，
    /// 否则每个生物每 tick 跑一次 A* 会直接吃光全部算力。</summary>
    public int PathfindBudgetPerTick { get; set; } = 220;

    /// <summary>每 tick 可展开的节点总数上限。只限调用次数是不够的 ——
    /// 单次最坏几百个节点，在几千生物的场景下仍能把一帧打爆。</summary>
    public int PathfindNodeBudgetPerTick { get; set; } = 6000;

    /// <summary>局部 A* 的单次最大搜索格数。超过就放弃，退回直线转向 + 贴墙滑行。</summary>
    public int PathfindMaxNodes { get; set; } = 180;

    /// <summary>路径缓存有效期（tick）。避免同一个目标每 tick 重算。</summary>
    public int PathfindCooldown { get; set; } = 40;

    /// <summary>是否启用局部 A* 寻路。关掉就只剩逃逸距离场 + 贴墙滑行（用于 A/B 对照）。</summary>
    public bool Pathfinding { get; set; } = true;

    // ---------- 环境 ----------
    public int DayLength { get; set; } = 1500;
    public bool WaterEnabled { get; set; } = true;
    /// <summary>
    /// 水域占比。0.18 是实测最平衡的值：物种能稳定维持在 4–8 个、捕食频繁。
    /// 调到 0 也能跑，但长期演化必然塌缩成单一物种。
    /// </summary>
    public float WaterFraction { get; set; } = 0.18f;
    public float DrownRate { get; set; } = 0.030f;
    /// <summary>地形摩擦（沼泽/灌木/荒漠的额外代价）有多强。0 = 只影响移动，不影响代谢。</summary>
    public float TerrainFriction { get; set; } = 0.8f;
    /// <summary>
    /// 水域的食物丰饶度倍率。
    ///
    /// 这是"维持多物种共存"的关键设计。只把水做成一种惩罚（淹死）是不够的 ——
    /// 那样所有生物都会躲开水，水域等于浪费的地图。让水里长满只有带鳃生物才能
    /// 安全采食的水生植物，就切出了一个真实的生态位：水栖食草者、陆栖食草者、
    /// 以及各自水域里的掠食者可以长期共存，而不是演到最后只剩一个物种。
    /// </summary>
    public float AquaticRichness { get; set; } = 0.95f;
    /// <summary>光照低于此值时藻胞不再光合。</summary>
    public float AlgaeLightMinimum { get; set; } = 0.22f;
    public float PhotosynthesisMultiplier { get; set; } = 1.0f;

    // ---------- 初始化 ----------
    public float InitialMutationRate { get; set; } = 0.35f;
    public float InitialRuleMutationRate { get; set; } = 0.18f;

    /// <summary>连续 N tick 人口为 0 则判定灭绝。</summary>
    public int ExtinctionGraceTicks { get; set; } = 40;

    public SimConfig Clone() => (SimConfig)MemberwiseClone();
}
