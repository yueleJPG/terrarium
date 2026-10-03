using System.Text.Json;
using System.Text.Json.Serialization;

namespace Terrarium.Core;

/// <summary>
/// 基因组存档。刻意用可读 JSON 而不是二进制：
/// 玩家能直接打开文件手改一个阈值，看到生物行为随之改变 ——
/// 这个"可被理解、可被把玩"的特性，是这个项目区别于黑箱模拟的核心价值。
/// </summary>
public static class GenomeIO
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        IncludeFields = true,          // Condition / ActionSpec 是 struct + 公有字段
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string ToJson(Genome g) => JsonSerializer.Serialize(g, Options);

    public static Genome? FromJson(string json) => JsonSerializer.Deserialize<Genome>(json, Options);

    public static void Save(Genome g, string path)
    {
        string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, ToJson(g));
    }

    public static Genome? Load(string path)
        => !File.Exists(path) ? null : FromJson(File.ReadAllText(path));

    /// <summary>物种基因库：一次存一批生物，供玩家收藏和分享。</summary>
    public static void SaveBank(IEnumerable<(string Name, Genome G)> entries, string path)
    {
        var list = entries.Select(e =>
        {
            e.G.Name = e.Name;
            return e.G;
        }).ToList();
        string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(list, Options));
    }

    public static List<Genome> LoadBank(string path)
        => !File.Exists(path)
            ? new List<Genome>()
            : JsonSerializer.Deserialize<List<Genome>>(File.ReadAllText(path), Options) ?? new List<Genome>();

    /// <summary>
    /// 一行式基因组摘要，便于在终端里快速扫视种群分化情况。
    /// </summary>
    public static string Summarize(Genome g)
    {
        string organs = OrganTable.Describe(g.Organs);
        return $"{g.Name} 代{g.Generation} 物种#{g.SpeciesId} [{organs}] 规则{g.Rules.Count} 突变{g.MutationRate:F3}";
    }
}
