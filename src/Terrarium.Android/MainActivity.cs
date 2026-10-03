using Android.App;
using Android.OS;
using Android.Views;
using Android.Widget;
using Android.Content.PM;
// 注意：在 namespace Terrarium.Android 里写 Android.Util.X 会被解析成
// Terrarium.Android.Util.X 而找不到。用 using 引进来就没这个问题 ——
// using 指令是在编译单元（全局）层级解析的。
using Android.Util;
using Terrarium.Core;
using AColor = Android.Graphics.Color;

namespace Terrarium.Android;

/// <summary>
/// 主界面。
///
/// 桌面版有 3473 行界面代码：六个标签页、规则编辑器、地形编辑器、物种矩阵、
/// 环境滑块、迷你地图…… 手机上不可能也不应该照搬。
/// 这一版只保留**看**的核心：世界视图 + 播放控制 + 个体观察。
/// 编辑类功能（规则、地形、群系）留在桌面版。
///
/// 布局全部用代码搭，不用 XML —— 少一层资源编译，改起来也直观。
/// </summary>
[Activity(
    Label = "生态箱 Terrarium",
    MainLauncher = true,
    Icon = "@mipmap/ic_launcher",
    Theme = "@android:style/Theme.Material.NoActionBar.Fullscreen",
    ScreenOrientation = ScreenOrientation.SensorLandscape,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.KeyboardHidden)]
public sealed class MainActivity : Activity
{
    private SimRunner _runner = null!;
    private TerrariumView _view = null!;
    private TextView _status = null!;
    private TextView _info = null!;
    private Button _playBtn = null!;
    private Button _speedBtn = null!;

    private int _speedIdx = 2;   // 默认 4×

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        Simulation sim;
        try
        {
            var cfg = new SimConfig();
            sim = new Simulation(cfg);
            sim.Reset(seed: 20240613);
        }
        catch (Exception ex)
        {
            // 建世界就崩的话，后面什么都做不了。直接把原因贴出来，
            // 而不是让应用在启动瞬间静静退出（那看起来就像"装了没用"）。
            var tv = new TextView(this);
            tv.SetTextColor(Palette.Danger);
            tv.SetTextSize(ComplexUnitType.Sp, 12f);
            tv.SetPadding(24, 40, 24, 24);
            tv.Text = "初始化生态箱失败：\n\n" + ex;
            SetContentView(tv);
            return;
        }

        _runner = new SimRunner(sim);

        var root = new LinearLayout(this)
        {
            Orientation = Orientation.Vertical,
        };
        root.SetBackgroundColor(Palette.Background);

        root.AddView(BuildToolbar(), new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

        _view = new TerrariumView(this, _runner);
        _view.SelectionChanged += UpdateInfo;
        root.AddView(_view, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, 0, 1f));

        _info = new TextView(this);
        _info.SetTextColor(Palette.Text);
        _info.SetTextSize(ComplexUnitType.Sp, 11f);
        _info.SetPadding(20, 12, 20, 12);
        _info.SetBackgroundColor(Palette.Panel);
        _info.Text = "点一下任意一只生物，这里会显示它此刻在想什么。双指缩放，单指拖动，双击适应全图。";
        root.AddView(_info, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

        SetContentView(root);

        // 状态条每秒刷一次就够了 —— 挂在渲染循环里会让 TextView 每帧重排
        var handler = new Handler(Looper.MainLooper!);
        handler.PostDelayed(new Java.Lang.Runnable(TickStatus), 500);
    }

    private LinearLayout BuildToolbar()
    {
        var bar = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        bar.SetBackgroundColor(Palette.Panel);
        bar.SetPadding(8, 6, 8, 6);

        _playBtn = MakeButton("暂停", v =>
        {
            _runner.Paused = !_runner.Paused;
            _playBtn.Text = _runner.Paused ? "继续" : "暂停";
        });
        bar.AddView(_playBtn);

        _speedBtn = MakeButton($"{SimRunner.SpeedSteps[_speedIdx]}×", v =>
        {
            _speedIdx = (_speedIdx + 1) % SimRunner.SpeedSteps.Length;
            _runner.Speed = SimRunner.SpeedSteps[_speedIdx];
            _speedBtn.Text = $"{_runner.Speed}×";
        });
        bar.AddView(_speedBtn);

        bar.AddView(MakeButton("缩小", v => _view.ZoomBy(1f / 1.45f)));
        bar.AddView(MakeButton("放大", v => _view.ZoomBy(1.45f)));
        bar.AddView(MakeButton("全景", v => _view.FitAll()));
        bar.AddView(MakeButton("新建", v => NewWorld()));

        bar.AddView(MakeButton("领地", v =>
        {
            _view.ShowTerritory = !_view.ShowTerritory;
            ((Button)v!).Text = _view.ShowTerritory ? "领地" : "领地✕";
            _view.Invalidate();
        }));

        bar.AddView(MakeButton("视野", v =>
        {
            _view.ShowVision = !_view.ShowVision;
            ((Button)v!).Text = _view.ShowVision ? "视野" : "视野✕";
            _view.Invalidate();
        }));

        _status = new TextView(this);
        _status.SetTextColor(Palette.TextDim);
        _status.SetTextSize(ComplexUnitType.Sp, 11f);
        _status.Gravity = GravityFlags.CenterVertical | GravityFlags.End;
        _status.SetPadding(16, 0, 8, 0);
        bar.AddView(_status, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));

        return bar;
    }

    private Button MakeButton(string label, Action<View?> onClick)
    {
        var b = new Button(this) { Text = label };
        b.SetTextColor(Palette.Text);
        b.SetTextSize(ComplexUnitType.Sp, 11f);
        b.SetBackgroundColor(Palette.Border);
        b.SetPadding(14, 4, 14, 4);
        b.Click += (s, e) => onClick(s as View);
        var lp = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent);
        lp.RightMargin = 6;
        b.LayoutParameters = lp;
        return b;
    }

    private void NewWorld()
    {
        var cfg = new SimConfig();
        var sim = new Simulation(cfg);
        // 用时间派生种子，每次"新建"都是一个新的生态箱
        sim.Reset((ulong)DateTime.UtcNow.Ticks);
        _runner.ReplaceSimulation(sim);
        _view.SelectedId = -1;
        _view.FitAll();
        UpdateInfo();
    }

    /// <summary>物种名缓存。每秒钟跟着状态条刷一次 —— 每次点选都去查一遍太贵。</summary>
    private readonly Dictionary<int, string> _speciesNames = new();

    private void TickStatus()
    {
        var sim = _runner.Sim;
        var snap = sim.Snapshot();
        _status.Text = $"{snap.Population} 个体 · 物种 {snap.SpeciesCount} · " +
                       $"{_runner.MeasuredTicksPerSecond} tick/s · 第 {snap.Tick} tick";

        try
        {
            _speciesNames.Clear();
            foreach (var s in sim.SpeciesSummaries())
                _speciesNames[s.Id] = s.Name;
        }
        catch (Exception) { /* 物种表正在被模拟线程改，这一秒就先不更新，下秒再来 */ }

        var handler = new Handler(Looper.MainLooper!);
        handler.PostDelayed(new Java.Lang.Runnable(TickStatus), 900);
    }

    /// <summary>
    /// 点选生物后的信息条。桌面版这里是一整块面板（器官雷达图、传感器条、
    /// 命中的规则、体节剖面），手机上压成几行 —— 只留"它此刻在想什么"最关键的几项。
    /// </summary>
    private void UpdateInfo()
    {
        int id = _view.SelectedId;
        if (id < 0)
        {
            _info.Text = "点一下任意一只生物，这里会显示它此刻在想什么。双指缩放，单指拖动，双击适应全图。";
            return;
        }

        var d = _runner.Sim.Detail(id);
        if (d is null || !d.Alive)
        {
            _info.Text = "这只生物已经不在了。";
            return;
        }

        _speciesNames.TryGetValue(d.SpeciesId, out var sname);
        sname ??= $"物种 {d.SpeciesId}";

        var sb = new System.Text.StringBuilder();
        sb.Append(sname).Append("  #").Append(d.Id)
          .Append("   第 ").Append(d.Generation).Append(" 代")
          .Append("   年龄 ").Append(d.Age.ToString("0"))
          .Append("   突变 ").Append(d.MutationRate.ToString("0.000"));

        sb.Append('\n');
        sb.Append("能量 ").Append((d.Energy / Math.Max(1f, d.MaxEnergy) * 100f).ToString("0")).Append('%')
          .Append("   生命 ").Append((d.Hp / Math.Max(1f, d.MaxHp) * 100f).ToString("0")).Append('%')
          .Append("   质量 ").Append(d.Mass.ToString("0.0"))
          .Append("   速度 ").Append(d.MaxSpeed.ToString("0"))
          .Append("   视野 ").Append(d.Vision.ToString("0"))
          .Append("   攻击 ").Append(d.Attack.ToString("0.0"))
          .Append("   护甲 ").Append(d.Armor.ToString("0.0"));

        // 器官与生活史：只列非零项。15 项全列在手机上读不过来，
        // 而"它把点数花在哪了"恰恰只能从非零项看出来。
        sb.Append('\n').Append("点数 ").Append(d.Genome.PointsUsed).Append("   ");
        bool any = false;
        for (int i = 0; i < TraitTable.Count; i++)
        {
            int v = d.Genome.TraitAt(i);
            if (v <= 0) continue;
            if (any) sb.Append(' ');
            sb.Append(TraitTable.Cn(i)).Append(v);
            any = true;
        }
        if (!any) sb.Append("（裸身：一点都没花）");

        var intent = (int)d.Intent;
        sb.Append('\n').Append("此刻在做：")
          .Append(intent >= 0 && intent < ActionTable.Cn.Length ? ActionTable.Cn[intent] : intent.ToString());
        if (d.LastRule >= 0) sb.Append("（规则 #").Append(d.LastRule).Append(" 命中）");

        _info.Text = sb.ToString();
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        _runner.Dispose();
    }
}
