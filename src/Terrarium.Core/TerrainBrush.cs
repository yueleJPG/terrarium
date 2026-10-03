namespace Terrarium.Core;

/// <summary>地形笔刷。null 表示"只调肥力、不改地形类型"（也就是种植物 / 除植物）。</summary>
public readonly struct TerrainBrush
{
    /// <summary>要刷成的地形；null = 保持不变。</summary>
    public readonly TerrainType? Type;
    /// <summary>肥力的目标增量（正 = 种植物，负 = 除草）。</summary>
    public readonly float FertilityDelta;
    /// <summary>笔刷半径（世界单位）。</summary>
    public readonly float Radius;

    public TerrainBrush(TerrainType? type, float fertilityDelta, float radius)
    {
        Type = type;
        FertilityDelta = fertilityDelta;
        Radius = radius;
    }

    public static TerrainBrush Terrain(TerrainType t, float radius) => new(t, 0f, radius);
    public static TerrainBrush Plant(float radius) => new(null, 1f, radius);
    public static TerrainBrush Clear(float radius) => new(null, -1f, radius);
    public static TerrainBrush Erase(float radius) => new(TerrainType.Grass, 0f, radius);
}

/// <summary>一键生成的生物群系预设。</summary>
public enum BiomePreset
{
    Lake = 0,       // 湖泊：大片水域把陆地切开
    Mountain = 1,   // 山脉：大量岩石屏障 → 地理隔离最强
    Archipelago = 2,// 群岛：水 + 山，天然的地理隔离实验室
    Jungle = 3,     // 丛林：灌木为主，伏击生态位
    Desert = 4,     // 荒漠：贫瘠空旷，筛选耐饿迁徙
    Riverland = 5,  // 河谷：水网 + 森林，生态位最丰富
}

public static class BiomeTable
{
    public static readonly string[] Cn = { "湖泊", "山脉", "群岛", "丛林", "荒漠", "河谷" };
    public static readonly string[] Desc =
    {
        "大片水域把陆地切成几块，水栖与陆栖分化明显。",
        "大量岩石屏障 —— 地理隔离最强，物种分化最快。",
        "水与山交织，天然的地理隔离实验室。",
        "灌木为主，视野受限，伏击型掠食者占优。",
        "贫瘠空旷，筛选耐饿与长距离迁徙能力。",
        "水网加森林，生态位最丰富，通常物种数最多。",
    };

    /// <summary>把预设写进配置（水域/山地/森林/荒漠/沼泽/沃土占比）。</summary>
    public static void Apply(BiomePreset p, SimConfig cfg)
    {
        switch (p)
        {
            case BiomePreset.Lake:
                cfg.WaterFraction = 0.34f; cfg.MountainFraction = 0.03f;
                cfg.ForestFraction = 0.14f; cfg.DesertFraction = 0.06f;
                cfg.SwampFraction = 0.07f; cfg.FertileFraction = 0.26f;
                break;
            case BiomePreset.Mountain:
                cfg.WaterFraction = 0.08f; cfg.MountainFraction = 0.24f;
                cfg.ForestFraction = 0.18f; cfg.DesertFraction = 0.08f;
                cfg.SwampFraction = 0.02f; cfg.FertileFraction = 0.24f;
                break;
            case BiomePreset.Archipelago:
                cfg.WaterFraction = 0.44f; cfg.MountainFraction = 0.10f;
                cfg.ForestFraction = 0.16f; cfg.DesertFraction = 0.04f;
                cfg.SwampFraction = 0.05f; cfg.FertileFraction = 0.24f;
                break;
            case BiomePreset.Jungle:
                cfg.WaterFraction = 0.14f; cfg.MountainFraction = 0.05f;
                cfg.ForestFraction = 0.44f; cfg.DesertFraction = 0.02f;
                cfg.SwampFraction = 0.09f; cfg.FertileFraction = 0.20f;
                break;
            case BiomePreset.Desert:
                cfg.WaterFraction = 0.05f; cfg.MountainFraction = 0.06f;
                cfg.ForestFraction = 0.05f; cfg.DesertFraction = 0.46f;
                cfg.SwampFraction = 0.01f; cfg.FertileFraction = 0.16f;
                break;
            default: // Riverland
                cfg.WaterFraction = 0.22f; cfg.MountainFraction = 0.09f;
                cfg.ForestFraction = 0.21f; cfg.DesertFraction = 0.06f;
                cfg.SwampFraction = 0.08f; cfg.FertileFraction = 0.28f;
                break;
        }
    }
}
