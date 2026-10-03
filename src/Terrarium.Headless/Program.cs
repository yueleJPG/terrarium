using System.Diagnostics;
using System.Globalization;
using System.Text;
using Terrarium.Core;

namespace Terrarium.Headless;

/// <summary>
/// 无头实验跑者。这是调参的主力工具 ——
/// 生态参数的平衡几乎不可能靠看画面调出来，只能靠跑几百次实验看曲线。
///
/// 用法示例：
///   Terrarium.Headless --pop 300 --ticks 60000 --every 5000
///   Terrarium.Headless --repeat 8 --ticks 40000         多组种子做对照
///   Terrarium.Headless --ticks 200000 --csv run.csv
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch { /* 某些终端不支持 */ }

        var opt = CliOptions.Parse(args);
        if (opt.Help) { CliOptions.PrintHelp(); return 0; }
        if (opt.MapTest) return RunMapTest();

        if (opt.Repeat > 1) return RunRepeat(opt);
        return RunSingle(opt);
    }

    // ==================================================================

    private static SimConfig BuildConfig(CliOptions o)
    {
        var cfg = new SimConfig
        {
            InitialPopulation = o.Population,
            FoodRichness = o.FoodRichness,
            WaterEnabled = o.Water,
            WaterFraction = o.WaterFraction,
            DayLength = o.DayLength,
            FoodRegen = o.FoodRegen,
            InitialMutationRate = o.MutationRate,
            InitialRuleMutationRate = o.RuleMutationRate,
            MaxPopulation = o.MaxPopulation,
            Lifespan = o.Lifespan,
        };
        if (o.NoAlgae) cfg.PhotosynthesisMultiplier = 0f;
        if (o.NoCollision) cfg.BodyCollision = false;
        if (o.NoPath) cfg.Pathfinding = false;
        cfg.FertileFraction = o.FertileFraction;
        cfg.MountainFraction = o.MountainFraction;
        cfg.ForestFraction = o.ForestFraction;
        cfg.DesertFraction = o.DesertFraction;
        cfg.SwampFraction = o.SwampFraction;
        cfg.SpeciationFactor = o.SpeciationFactor;
        cfg.KinToleranceMaxBits = o.KinToleranceMaxBits;
        cfg.InitialCannibalism = o.InitialCannibalism;
        cfg.AttackLungeMultiplier = o.Lunge;
        return cfg;
    }

    private static int RunSingle(CliOptions o)
    {
        var cfg = BuildConfig(o);
        var sim = new Simulation(cfg, o.Seed);
        long lastHatch = 0, lastCrush = 0;
        var sw = Stopwatch.StartNew();

        Console.WriteLine("=== 生态箱 headless 实验 ===");
        Console.WriteLine($"种子               : {o.Seed}");
        Console.WriteLine($"初始人口           : {o.Population}");
        Console.WriteLine($"世界               : {cfg.WorldWidth:0}x{cfg.WorldHeight:0}  格子 {cfg.TileSize:0}  食物再生 {cfg.FoodRegen * cfg.FoodRichness:0.00000}");

        // 地形构成：这是理解"为什么食物供给是这个量级"的唯一依据。
        // 换地形参数后如果不看这个，调参就是在瞎猜。
        var w0 = new World(cfg, o.Seed);
        var comp = new List<string>();
        for (int i = 0; i < TerrainTable.Count; i++)
        {
            float frac = w0.TerrainFraction((TerrainType)i);
            if (frac >= 0.005f) comp.Add($"{TerrainTable.Cn[i]} {frac * 100:F1}%");
        }
        Console.WriteLine($"地形构成           : {string.Join("  ", comp)}");
        Console.WriteLine($"可通行地块占比     : {(1f - w0.TerrainFraction(TerrainType.Rock)) * 100:F1}%");
        Console.WriteLine($"目标 tick          : {o.Ticks}   报告间隔 {o.Every}");
        Console.WriteLine();
        Console.WriteLine(Pad("tick", 9) + Pad("人口", 7) + Pad("存活种", 8) + Pad("累计种", 8) + Pad("个体签名距", 11) + Pad("食性", 7) + Pad("物种差异", 9)
                        + Pad("代数max", 9) + Pad("能量%", 8) + Pad("体重", 7)
                        + Pad("规则", 6) + Pad("突变率", 8) + Pad("掠食%", 8) + Pad("牙", 6) + Pad("点数", 6) + Pad("幼体%", 7) + Pad("卵", 7) + Pad("孵化", 7) + Pad("碎卵", 7) + Pad("出生", 9) + Pad("寿终", 9) + Pad("猎杀", 9));
        Console.WriteLine(new string('-', 136));

        var csv = o.Csv is null ? null : new StringBuilder();
        csv?.AppendLine("tick,population,species,gen_max,gen_avg,avg_energy,avg_mass,avg_rules,avg_mutation,avg_combat,kills,births,deaths");

        long prevBirths = 0, prevKills = 0, prevStarve = 0, prevOld = 0;

        void Report()
        {
            WorldStats s = sim.World.ComputeStats();
            var w = sim.World;
            long dBirth = w.TotalBirths - prevBirths;
            long dKill = w.TotalKills - prevKills;
            long dStarve = w.TotalStarvations - prevStarve;
            long dOld = w.TotalOldAges - prevOld;
            prevBirths = w.TotalBirths; prevKills = w.TotalKills;
            prevStarve = w.TotalStarvations; prevOld = w.TotalOldAges;

            var sp = sim.SpeciesStats();
            var es = sim.EggStats();
            long dHatch = es.Hatches - lastHatch; lastHatch = es.Hatches;
            long dCrush = es.Crushed - lastCrush; lastCrush = es.Crushed;
            Console.WriteLine(Pad(s.Tick.ToString(), 9) + Pad(s.Population.ToString(), 7)
                            + Pad(sp.Alive.ToString(), 8) + Pad(sp.TotalEver.ToString(), 8)
                            + Pad(sp.AvgSignatureDistance.ToString("F1"), 11) + Pad(sp.AvgCannibalism.ToString("F2"), 7) + Pad(sp.SpeciesDistance.ToString("F3"), 9)
                            + Pad(s.MaxGeneration.ToString(), 9)
                            + Pad((s.AverageEnergyPct * 100f).ToString("F1"), 8)
                            + Pad(s.AverageMass.ToString("F2"), 7)
                            + Pad(s.AverageRuleCount.ToString("F2"), 6)
                            + Pad(s.AverageMutationRate.ToString("F3"), 8)
                            + Pad((s.PredatorFraction * 100f).ToString("F0"), 8) + Pad(s.AverageFang.ToString("F2"), 6)
                            + Pad(s.AveragePoints.ToString("F1"), 6)
                            + Pad((s.JuvenileFraction * 100f).ToString("F0"), 7)
                            + Pad(es.Eggs.ToString(), 7) + Pad(dHatch.ToString(), 7) + Pad(dCrush.ToString(), 7)
                            + Pad(dBirth.ToString(), 9)
                            + Pad(dOld.ToString(), 9) + Pad(dKill.ToString(), 9));
            csv?.AppendLine(string.Join(',',
                s.Tick, s.Population, s.SpeciesCount, s.MaxGeneration,
                s.AverageGeneration.ToString("F2", CultureInfo.InvariantCulture),
                s.AverageEnergyPct.ToString("F4", CultureInfo.InvariantCulture),
                s.AverageMass.ToString("F4", CultureInfo.InvariantCulture),
                s.AverageRuleCount.ToString("F4", CultureInfo.InvariantCulture),
                s.AverageMutationRate.ToString("F5", CultureInfo.InvariantCulture),
                s.AverageCombatPower.ToString("F5", CultureInfo.InvariantCulture),
                w.TotalKills, w.TotalBirths, w.TotalDeaths));
        }

        Report();

        while (sim.World.Tick < o.Ticks && !sim.World.Extinct)
        {
            int chunk = Math.Min(o.Every, o.Ticks - sim.World.Tick);
            sim.RunBatch(chunk);
            Report();
        }

        sw.Stop();
        Console.WriteLine();
        Console.WriteLine($"寻路：调用 {sim.World.PathCalls:N0} 次，被预算拒绝 {sim.World.PathDenied:N0} 次，展开节点 {sim.World.PathNodes:N0} 个");
        Console.WriteLine($"完成：{sim.World.Tick} tick，用时 {sw.Elapsed.TotalSeconds:F1}s "
                        + $"（{sim.World.Tick / Math.Max(0.001, sw.Elapsed.TotalSeconds):F0} tick/s）");

        if (sim.World.Extinct)
            Console.WriteLine("!! 种群灭绝：所有生物都死光了。要么降低代谢开销，要么提高食物再生。");

        PrintSpeciesProfile(sim, o);
        ExportArtifacts(sim, o);

        if (o.Csv is not null)
        {
            File.WriteAllText(o.Csv, csv!.ToString());
            Console.WriteLine($"CSV 已写出: {Path.GetFullPath(o.Csv)}");
        }
        return sim.World.Extinct ? 2 : 0;
    }

    // ==================================================================

    private static void PrintSpeciesProfile(Simulation sim, CliOptions o)
    {
        var world = sim.World;
        if (world.Creatures.Count == 0) return;

        var groups = world.Creatures.GroupBy(c => c.SpeciesId)
                          .OrderByDescending(g => g.Count())
                          .Take(o.Profile)
                          .ToList();

        Console.WriteLine();
        Console.WriteLine($"--- 现存物种 TOP {groups.Count}（共 {world.Creatures.GroupBy(c => c.SpeciesId).Count()} 个物种） ---");
        Console.WriteLine(Pad("物种", 7) + Pad("数量", 7) + Pad("代数", 7) + Pad("平均体重", 10)
                        + Pad("掠食%", 8) + Pad("牙", 6) + Pad("点数", 6) + Pad("幼体%", 7) + Pad("卵", 7) + Pad("孵化", 7) + Pad("碎卵", 7) + Pad("突变率", 8) + "器官构成（平均等级）");
        Console.WriteLine(new string('-', 96));

        foreach (var g in groups)
        {
            var list = g.ToList();
            float mass = list.Average(c => c.Mass);
            float combat = list.Average(c => c.CombatPower);
            float mut = list.Average(c => c.G.MutationRate);
            int gen = (int)list.Average(c => c.G.Generation);

            var organs = new List<string>();
            for (int i = 0; i < OrganTable.Count; i++)
            {
                float avg = (float)list.Average(c => c.G.Organs[i]);
                if (avg >= 0.15f) organs.Add($"{OrganTable.Cn[i]}{avg:F1}");
            }

            Console.WriteLine(Pad("#" + g.Key, 7) + Pad(list.Count.ToString(), 7) + Pad(gen.ToString(), 7)
                            + Pad(mass.ToString("F2"), 10) + Pad(combat.ToString("F2"), 7) + Pad(mut.ToString("F3"), 8)
                            + (organs.Count == 0 ? "(全裸)" : string.Join(" ", organs)));
        }

        // 展示最大物种里"代数最深"个体的规则表 —— 这是"演化出了什么策略"的直接证据
        var top = groups.FirstOrDefault();
        if (top is null) return;

        var champion = top.OrderByDescending(c => c.G.Generation).First();
        Console.WriteLine();
        Console.WriteLine($"--- 样本规则表：物种 #{champion.SpeciesId} 第 {champion.G.Generation} 代 ---");
        for (int i = 0; i < champion.G.Rules.Count; i++)
            Console.WriteLine($"  {i + 1,2}. {champion.G.Rules[i].Describe()}");

        Console.WriteLine();
        Console.WriteLine($"  平均规则数 {top.Average(c => c.G.Rules.Count):F1}"
                        + $"   平均突变率 {top.Average(c => c.G.MutationRate):F3}"
                        + $"   平均规则突变率 {top.Average(c => c.G.RuleMutationRate):F3}");
    }

    private static void ExportArtifacts(Simulation sim, CliOptions o)
    {
        var world = sim.World;
        if (world.Creatures.Count == 0) return;

        // 导出"最成功物种"的代表个体（按人口数排序）
        var topGroup = world.Creatures.GroupBy(c => c.SpeciesId).OrderByDescending(g => g.Count()).First();
        var champion = topGroup.OrderByDescending(c => c.G.Generation).First();

        string path = o.Export ?? Path.Combine(AppContext.BaseDirectory, "genomes", $"champion_{o.Seed}.json");
        GenomeIO.Save(champion.G, path);
        Console.WriteLine();
        Console.WriteLine($"冠军基因组已导出: {Path.GetFullPath(path)}");
        Console.WriteLine($"  {GenomeIO.Summarize(champion.G)}");
    }

    // ==================================================================

    private static int RunRepeat(CliOptions o)
    {
        Console.WriteLine($"=== 多种子对照实验：{o.Repeat} 组 × {o.Ticks} tick ===");
        Console.WriteLine(Pad("种子", 8) + Pad("末人口", 9) + Pad("物种", 7) + Pad("体重", 8)
                        + Pad("规则", 7) + Pad("突变率", 9) + Pad("战力", 8)
                        + Pad("出生", 9) + Pad("猎杀", 9) + "主要器官");
        Console.WriteLine(new string('-', 118));

        var survivors = 0;
        for (int r = 0; r < o.Repeat; r++)
        {
            ulong seed = o.Seed + (ulong)(r * 1013);
            var cfg = BuildConfig(o);
            var sim = new Simulation(cfg, seed);
            sim.RunBatch(o.Ticks);
            var s = sim.World.ComputeStats();
            long kills = sim.World.TotalKills;
            long births = sim.World.TotalBirths;

            string organs = "—";
            if (sim.World.Creatures.Count > 0)
            {
                var top = sim.World.Creatures.GroupBy(c => c.SpeciesId).OrderByDescending(g => g.Count()).First().ToList();
                var parts = new List<string>();
                for (int i = 0; i < OrganTable.Count; i++)
                {
                    float avg = (float)top.Average(c => c.G.Organs[i]);
                    if (avg >= 0.2f) parts.Add($"{OrganTable.Cn[i]}{avg:F1}");
                }
                organs = parts.Count == 0 ? "(全裸)" : string.Join(" ", parts);
            }

            if (s.Population > 0) survivors++;

            Console.WriteLine(Pad(seed.ToString(), 8) + Pad(s.Population.ToString(), 9) + Pad(s.SpeciesCount.ToString(), 7)
                            + Pad(s.AverageMass.ToString("F2"), 8) + Pad(s.AverageRuleCount.ToString("F2"), 7)
                            + Pad(s.AverageMutationRate.ToString("F3"), 9) + Pad(s.AverageCombatPower.ToString("F2"), 8)
                            + Pad(births.ToString(), 9) + Pad(kills.ToString(), 9) + organs);
        }

        Console.WriteLine();
        Console.WriteLine($"存活 {survivors}/{o.Repeat} 组。"
                        + (survivors == o.Repeat ? " 生态稳定。" : " 有灭绝，说明参数过于严苛。"));
        return survivors == o.Repeat ? 0 : 3;
    }

    // ==================================================================

    /// <summary>
    /// 地图存档的自检。
    ///
    /// 为什么必须自动化：地图存档这条链路横跨 手绘改动 → 打包 → gzip/base64 →
    /// 写文件 → 读文件 → 解包 → 装回 World → 重算逃逸距离场 → 刷新缓存版本号。
    /// 任何一环错了，界面上都只表现为"载入之后地形没变"或者"变了一半"，
    /// 靠手点对话框根本测不干净。
    /// </summary>
    private static int RunMapTest()
    {
        Console.WriteLine("=== 地图存档自检 ===");
        var cfg = new SimConfig();
        var sim = new Simulation(cfg, 424242);
        var w = sim.World;

        int n = w.W * w.H;
        var before = (byte[])w.Terrain.Clone();
        var fertBefore = (float[])w.Fertility.Clone();

        string path = Path.Combine(Path.GetTempPath(), $"terrarium_selftest_{Guid.NewGuid():N}.terrarium");

        // 1. 存原始地图
        sim.SaveMap(path, "selftest");
        long size = new FileInfo(path).Length;
        Console.WriteLine($"存盘成功，大小 {size / 1024.0:F1} KB");

        // 2. 把地形改得面目全非（画一大片岩石）
        sim.BeginTerrainStroke();
        for (int x = 200; x < 1400; x += 60)
            for (int y = 200; y < 1200; y += 60)
                sim.PaintTerrain(new Vec2(x, y), TerrainBrush.Terrain(TerrainType.Rock, 70f));
        sim.EndTerrainStroke();

        int changed = 0;
        for (int i = 0; i < n; i++) if (w.Terrain[i] != before[i]) changed++;
        Console.WriteLine($"已涂抹 {changed} 个地块变成岩石（作为被改动的对照）");
        if (changed < 100) { Console.WriteLine("!! 涂抹没有生效，测试无效"); return 2; }

        // 3. 载入回来，逐字节比对
        if (!sim.LoadMap(path)) { Console.WriteLine("!! 载入失败"); return 2; }

        int mismatchTerrain = 0, mismatchFert = 0;
        for (int i = 0; i < n; i++)
        {
            if (w.Terrain[i] != before[i]) mismatchTerrain++;
            if (MathF.Abs(w.Fertility[i] - fertBefore[i]) > 1e-5f) mismatchFert++;
        }

        // 4. 逃逸距离场也必须被重算（否则生物会沿已经不存在的路走）
        bool escapeRebuilt = w.EscapeDist.Length == n;
        int blockedTiles = 0;
        for (int i = 0; i < n; i++) if (w.EscapeDist[i] > 0) blockedTiles++;

        Console.WriteLine($"地形不一致地块 : {mismatchTerrain} / {n}");
        Console.WriteLine($"肥力不一致地块 : {mismatchFert} / {n}");
        Console.WriteLine($"逃逸场已重算   : {escapeRebuilt}（被围困地块 {blockedTiles}）");

        try { File.Delete(path); } catch { /* 清理失败无所谓 */ }

        bool pass = mismatchTerrain == 0 && mismatchFert == 0 && escapeRebuilt;
        Console.WriteLine(pass ? "==> 通过：地图可以完整往返" : "==> 失败：地图往返有损");
        return pass ? 0 : 2;
    }

    // ==================================================================

    private static string Pad(string s, int width) => s.PadRight(width);
}

/// <summary>
/// 极简命令行解析。刻意不引入任何命令行库 —— 整个项目零第三方依赖，
/// 这样自包含单文件发布不会有任何意外。
/// </summary>
internal sealed class CliOptions
{
    public ulong Seed = 12345;
    public int Population = 420;
    public int Ticks = 60_000;
    public int Every = 5_000;
    public int Repeat = 1;
    public int Profile = 6;
    public int MaxPopulation = 2600;
    public int Lifespan = 6400;
    public int DayLength = 1500;
    public float FoodRichness = 1.0f;
    public float FoodRegen = 0.00055f;
    public float SpeciationFactor = 3.5f;
    public int KinToleranceMaxBits = 18;
    public float InitialCannibalism = 0.5f;
    public float FertileFraction = 0.22f;
    public float MountainFraction = 0.07f;
    public float ForestFraction = 0.16f;
    public float DesertFraction = 0.12f;
    public float SwampFraction = 0.05f;
    public float WaterFraction = 0.18f;
    public float MutationRate = 0.35f;
    public float RuleMutationRate = 0.18f;
    public bool Water = true;
    public bool NoAlgae = false;
    public bool NoCollision = false;
    public bool NoPath = false;
    public bool MapTest = false;
    public float Lunge = 1.45f;
    public string? Csv;
    public string? Export;
    public bool Help;

    public static CliOptions Parse(string[] args)
    {
        var o = new CliOptions();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            string? Next() => i + 1 < args.Length ? args[++i] : null;

            switch (a)
            {
                case "-h": case "--help": o.Help = true; break;
                case "--seed": o.Seed = ulong.Parse(Next() ?? "12345"); break;
                case "--pop": o.Population = int.Parse(Next() ?? "300"); break;
                case "--ticks": o.Ticks = int.Parse(Next() ?? "60000"); break;
                case "--every": o.Every = Math.Max(1, int.Parse(Next() ?? "5000")); break;
                case "--repeat": o.Repeat = Math.Max(1, int.Parse(Next() ?? "1")); break;
                case "--profile": o.Profile = Math.Max(1, int.Parse(Next() ?? "6")); break;
                case "--maxpop": o.MaxPopulation = int.Parse(Next() ?? "1400"); break;
                case "--lifespan": o.Lifespan = int.Parse(Next() ?? "6400"); break;
                case "--daylength": o.DayLength = int.Parse(Next() ?? "1500"); break;
                case "--richness": o.FoodRichness = float.Parse(Next() ?? "1", CultureInfo.InvariantCulture); break;
                case "--regen": o.FoodRegen = float.Parse(Next() ?? "0.00070", CultureInfo.InvariantCulture); break;
                case "--fertile": o.FertileFraction = float.Parse(Next() ?? "0.22", CultureInfo.InvariantCulture); break;
                case "--mountain": o.MountainFraction = float.Parse(Next() ?? "0.07", CultureInfo.InvariantCulture); break;
                case "--forest": o.ForestFraction = float.Parse(Next() ?? "0.16", CultureInfo.InvariantCulture); break;
                case "--desert": o.DesertFraction = float.Parse(Next() ?? "0.12", CultureInfo.InvariantCulture); break;
                case "--swamp": o.SwampFraction = float.Parse(Next() ?? "0.05", CultureInfo.InvariantCulture); break;
                case "--nocol": o.NoCollision = true; break;
                case "--nopath": o.NoPath = true; break;
                case "--maptest": o.MapTest = true; break;
                case "--lunge": o.Lunge = float.Parse(Next() ?? "1.45", CultureInfo.InvariantCulture); break;
                case "--foodregen": o.FoodRegen = float.Parse(Next() ?? "0.0008", CultureInfo.InvariantCulture); break;
                case "--spec": o.SpeciationFactor = float.Parse(Next() ?? "3.5", CultureInfo.InvariantCulture); break;
                case "--kinbits": o.KinToleranceMaxBits = int.Parse(Next() ?? "18"); break;
                case "--cann": o.InitialCannibalism = float.Parse(Next() ?? "0.5", CultureInfo.InvariantCulture); break;
                case "--water": o.WaterFraction = float.Parse(Next() ?? "0.15", CultureInfo.InvariantCulture); break;
                case "--nowater": o.Water = false; break;
                case "--noalgae": o.NoAlgae = true; break;
                case "--mut": o.MutationRate = float.Parse(Next() ?? "0.35", CultureInfo.InvariantCulture); break;
                case "--rulemut": o.RuleMutationRate = float.Parse(Next() ?? "0.18", CultureInfo.InvariantCulture); break;
                case "--csv": o.Csv = Next(); break;
                case "--export": o.Export = Next(); break;
                default:
                    Console.WriteLine($"未知参数: {a}");
                    break;
            }
        }
        return o;
    }

    public static void PrintHelp()
    {
        Console.WriteLine("""
        Terrarium.Headless —— 生态箱无头演化实验

          --seed N        随机种子（默认 12345）
          --pop N         初始人口（默认 300）
          --ticks N       模拟总 tick 数（默认 60000）
          --every N       报告间隔（默认 5000）
          --repeat K      用 K 个种子各跑一遍做对照
          --profile N     打印人口最多的 N 个物种（默认 6）
          --maxpop N      人口硬上限（默认 1400）
          --lifespan N    自然寿命 tick（默认 6400）
          --daylength N   昼夜周期 tick（默认 1500）
          --richness X    食物丰饶度倍率（默认 1.0）
          --regen X       食物再生速率（默认 0.0022）
          --fertile X     肥沃地块占比（默认 0.55）
          --water X       水域占比（默认 0.15）
          --nowater       关闭水域
          --noalgae       关闭光合作用（做对照实验用）
          --mut X         初始突变率（默认 0.35）
          --rulemut X     初始规则突变率（默认 0.18）
          --csv PATH      把种群曲线导出成 CSV
          --export PATH   把最成功物种的代表基因组导出成 JSON
        """);
    }
}
