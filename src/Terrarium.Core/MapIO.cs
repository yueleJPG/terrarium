using System.IO.Compression;
using System.Text.Json;

namespace Terrarium.Core;

/// <summary>
/// 生态箱地图存档。
///
/// 为什么必须能存：地形编辑器让玩家可以手绘地貌 —— 挖一条河、立一道山脊、
/// 种一片林子。如果这些不能保存，那所有手工劳动在关掉程序的一瞬间就没了，
/// 编辑器也就失去了意义。
///
/// 格式选择：
///  - JSON + 三个 gzip + base64 的二进制块。人可读的元数据（尺寸、配置）在外面，
///    大块数组压在里面 —— 兼顾"能看懂"和"体积小"。
///  - 16000 地块的 Terrain + Fertility + Food 原始约 144 KB，gzip 之后通常 20–50 KB。
///  - 不存生物：生物靠"地图 + 种子"就能精确重放（本项目是确定性的），
///    存下来只会让文件变大且容易和版本脱节。
/// </summary>
public sealed class TerrariumMap
{
    public int Version { get; set; } = 1;
    public string Name { get; set; } = "未命名生态箱";

    public int W { get; set; }
    public int H { get; set; }
    public float TileSize { get; set; }
    public float WorldWidth { get; set; }
    public float WorldHeight { get; set; }

    /// <summary>gzip + base64 的地形字节数组。</summary>
    public string Terrain { get; set; } = "";
    public string Fertility { get; set; } = "";
    public string Food { get; set; } = "";

    // 影响地形生成与水域富饶度的配置快照，载入时一并恢复
    public float WaterFraction { get; set; }
    public float MountainFraction { get; set; }
    public float ForestFraction { get; set; }
    public float DesertFraction { get; set; }
    public float SwampFraction { get; set; }
    public float FertileFraction { get; set; }
    public float AquaticRichness { get; set; }
    public bool WaterEnabled { get; set; }
}

public static class MapIO
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
    };

    public static void Save(World world, string path, string? name = null)
    {
        var map = new TerrariumMap
        {
            Name = name ?? "未命名生态箱",
            W = world.W,
            H = world.H,
            TileSize = world.Cfg.TileSize,
            WorldWidth = world.Cfg.WorldWidth,
            WorldHeight = world.Cfg.WorldHeight,
            Terrain = Pack(world.Terrain),
            Fertility = PackFloats(world.Fertility),
            Food = PackFloats(world.Food),
            WaterFraction = world.Cfg.WaterFraction,
            MountainFraction = world.Cfg.MountainFraction,
            ForestFraction = world.Cfg.ForestFraction,
            DesertFraction = world.Cfg.DesertFraction,
            SwampFraction = world.Cfg.SwampFraction,
            FertileFraction = world.Cfg.FertileFraction,
            AquaticRichness = world.Cfg.AquaticRichness,
            WaterEnabled = world.Cfg.WaterEnabled,
        };

        string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(map, Options));
    }

    public static TerrariumMap? Load(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<TerrariumMap>(File.ReadAllText(path), Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>把地图装回 World。尺寸不一致时返回 false（调用方需要先重建世界）。</summary>
    public static bool Apply(World world, TerrariumMap map)
    {
        if (map.W != world.W || map.H != world.H) return false;

        byte[]? terrain = UnpackBytes(map.Terrain, world.W * world.H);
        float[]? fert = UnpackFloats(map.Fertility, world.W * world.H);
        float[]? food = UnpackFloats(map.Food, world.W * world.H);
        if (terrain is null || fert is null || food is null) return false;

        Array.Copy(terrain, world.Terrain, terrain.Length);
        Array.Copy(fert, world.Fertility, fert.Length);
        Array.Copy(food, world.Food, food.Length);

        // 配置也跟着恢复，否则"手绘过地形的地图 + 旧的水域参数"会对不上
        world.Cfg.WaterFraction = map.WaterFraction;
        world.Cfg.MountainFraction = map.MountainFraction;
        world.Cfg.ForestFraction = map.ForestFraction;
        world.Cfg.DesertFraction = map.DesertFraction;
        world.Cfg.SwampFraction = map.SwampFraction;
        world.Cfg.FertileFraction = map.FertileFraction;
        world.Cfg.AquaticRichness = map.AquaticRichness;
        world.Cfg.WaterEnabled = map.WaterEnabled;

        world.OnMapInstalled();
        return true;
    }

    // ==================================================================
    // 打包
    // ==================================================================

    private static string Pack(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            gz.Write(data, 0, data.Length);
        return Convert.ToBase64String(ms.ToArray());
    }

    private static string PackFloats(float[] data)
    {
        var bytes = new byte[data.Length * sizeof(float)];
        Buffer.BlockCopy(data, 0, bytes, 0, bytes.Length);
        return Pack(bytes);
    }

    private static byte[]? UnpackBytes(string b64, int expected)
    {
        var raw = Unpack(b64);
        return raw is null || raw.Length < expected ? null : raw;
    }

    private static float[]? UnpackFloats(string b64, int expected)
    {
        var raw = Unpack(b64);
        if (raw is null || raw.Length < expected * sizeof(float)) return null;
        var data = new float[expected];
        Buffer.BlockCopy(raw, 0, data, 0, expected * sizeof(float));
        return data;
    }

    private static byte[]? Unpack(string b64)
    {
        if (string.IsNullOrEmpty(b64)) return null;
        try
        {
            byte[] compressed = Convert.FromBase64String(b64);
            using var ms = new MemoryStream(compressed);
            using var gz = new GZipStream(ms, CompressionMode.Decompress);
            using var outMs = new MemoryStream();
            gz.CopyTo(outMs);
            return outMs.ToArray();
        }
        catch (Exception)
        {
            // 文件损坏就当作读不出来，不要让整个程序崩掉
            return null;
        }
    }
}
