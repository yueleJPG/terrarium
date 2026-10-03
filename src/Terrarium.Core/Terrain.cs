namespace Terrarium.Core;

/// <summary>
/// 地形类型。每个地块一个字节。
///
/// 这张表是"空间结构"的载体，也是让物种真正分化出来的引擎：
/// 岩石把世界切成互不连通的区域 → 区域内的种群独立演化 → 地理隔离产生新物种。
/// 在均质地图上，竞争排斥原理决定了最终只会剩一个赢家（这是实测到的现象）。
///
/// 索引顺序是存档格式的一部分，只能在末尾追加。
/// </summary>
public enum TerrainType : byte
{
    /// <summary>草地：基准地形，食物中等，通行顺畅。</summary>
    Grass = 0,
    /// <summary>沃土 / 花丛：食物最丰富的地块，是领地争夺的焦点。</summary>
    Fertile = 1,
    /// <summary>灌木丛：通行变慢、视野减半 → 天然的伏击与藏身之处。</summary>
    Bush = 2,
    /// <summary>浅水：可涉水通过，只是慢一点。它是水陆之间的走廊而不是屏障。</summary>
    ShallowWater = 3,
    /// <summary>深水：只有带鳃的生物能待，其他生物会持续流失能量。</summary>
    DeepWater = 4,
    /// <summary>沼泽：又慢又费能量，筛选"腿 + 鳃"的组合。</summary>
    Swamp = 5,
    /// <summary>岩石 / 山：不可通行。地理隔离的主要来源。</summary>
    Rock = 6,
    /// <summary>荒漠：空旷贫瘠，筛选耐饿与迁徙能力。</summary>
    Desert = 7,
}

/// <summary>地形属性表。改这里的数字等于改整个世界的物理法则。</summary>
public static class TerrainTable
{
    public const int Count = 8;

    public static readonly string[] Cn =
    {
        "草地", "沃土", "灌木丛", "浅水", "深水", "沼泽", "岩石", "荒漠",
    };

    public static readonly string[] Desc =
    {
        "基准地形，食物中等。",
        "食物最丰富的地块，领地争夺的焦点。",
        "通行缓慢、视野减半 —— 天然的伏击与藏身之处。",
        "可以涉水通过，只是慢一点。水陆之间的走廊。",
        "只有带鳃的生物能长时间停留，其他生物会持续流失能量。",
        "又慢又费能量，筛选腿与鳃的组合。",
        "不可通行。把世界切成互不连通的区域，是地理隔离的主要来源。",
        "空旷贫瘠，筛选耐饿与迁徙能力。",
    };

    /// <summary>能否通行。岩石是唯一的硬屏障。</summary>
    public static readonly bool[] Passable =
    {
        true, true, true, true, true, true, false, true,
    };

    /// <summary>移动的额外能量开销倍率。</summary>
    public static readonly float[] MoveCost =
    {
        1.00f, 1.00f, 1.60f, 1.30f, 1.00f, 1.90f, 99f, 1.05f,
    };

    /// <summary>该地形的食物上限基准（再乘以地块自己的斑块噪声）。</summary>
    public static readonly float[] FoodCapacity =
    {
        0.35f, 0.95f, 0.55f, 0.70f, 0.85f, 0.50f, 0.00f, 0.06f,
    };

    /// <summary>视野倍率。灌木丛砍一半，于是"藏身"和"伏击"成为可行的策略。</summary>
    public static readonly float[] VisionMult =
    {
        1.00f, 1.00f, 0.50f, 1.00f, 1.00f, 0.70f, 1.00f, 1.00f,
    };

    /// <summary>是否算水域（用于"在水里 / 有鳃"这类判定与渲染）。</summary>
    public static readonly bool[] WaterTile =
    {
        false, false, false, true, true, true, false, false,
    };

    /// <summary>是否会淹死无鳃生物。只有深水会 —— 浅水刻意做成可通行的走廊。</summary>
    public static readonly bool[] Drowns =
    {
        false, false, false, false, true, false, false, false,
    };

    public static bool IsPassable(TerrainType t) => Passable[(int)t];
    public static bool IsWater(TerrainType t) => WaterTile[(int)t];
    public static bool DrownsAt(TerrainType t) => Drowns[(int)t];
    public static float MoveCostOf(TerrainType t) => MoveCost[(int)t];
    public static float VisionOf(TerrainType t) => VisionMult[(int)t];
}
