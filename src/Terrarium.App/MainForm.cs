using Terrarium.Core;

namespace Terrarium.App;

/// <summary>
/// 主窗口。布局：
///   顶部工具条（播放控制 / 速度 / 新建 / 造物）
///   左侧生态箱视图 + 底部种群曲线 + 右侧选项卡（详情 / 规则 / 物种 / 环境）
/// </summary>
internal sealed class MainForm : Form
{
    private readonly SimulationRunner _runner;
    private readonly TerrariumCanvas _canvas = new();
    private readonly InfoPanel _inspector = new();
    private readonly InfoPanel _species = new();
    private readonly InfoPanel _watch = new();
    private readonly ChartPanel _chart = new();
    private readonly RuleEditorPanel _rules = new();
    private readonly EnvironmentPanel _env = new();
    private readonly TerrainEditorPanel _terrain = new();
    private readonly SpeciesMatrixPanel _speciesMatrix = new();
    private SplitContainer? _speciesTabSplit;
    private readonly System.Windows.Forms.Timer _uiTimer = new();

    private readonly Label _status = new();
    private readonly Button _btnPlay = new();
    private readonly ComboBox _speedBox = new();
    private readonly Button _btnNew = new();
    private readonly Button _btnFit = new();
    private readonly CheckBox _chkFollow = new();
    private readonly CheckBox _chkVision = new();
    private readonly CheckBox _chkOrgans = new();
    private readonly CheckBox _chkTerritory = new();
    private readonly CheckBox _chkMinimap = new();
    private readonly ComboBox _chartMode = new();

    private int _speedIndex = 2;
    private static readonly (string Label, int Value)[] Speeds =
    {
        ("暂停", 0), ("1× 实时", 1), ("2×", 2), ("5×", 5),
        ("20×", 20), ("100×", 100), ("极速", SimulationRunner.MaxSpeed),
    };

    private SimSnapshot? _snap;
    private int _uiTick;
    private readonly SplitContainer _mainSplit;
    private readonly SplitContainer _rightSplit;
    private readonly SplitContainer _leftSplit;

    public MainForm()
    {
        var sim = new Simulation(new SimConfig(), 12345);
        _runner = new SimulationRunner(sim);

        Text = "生态箱 Terrarium — 可编程生物演化模拟";
        BackColor = Theme.Background;
        ForeColor = Theme.Text;
        Font = Theme.UiFont;
        MinimumSize = new Size(1080, 700);
        Size = new Size(1480, 900);
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;

        BuildToolbar();

        // ---------------- 左侧：生态箱 + 曲线 ----------------
        // 注意：SplitterDistance 必须在控件已经完成布局之后再设置，
        // 否则会被静默钳到边界值（右侧面板直接消失）。
        _leftSplit = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            BackColor = Theme.Border,
            SplitterWidth = 4,
            FixedPanel = FixedPanel.Panel2,
        };
        _canvas.Dock = DockStyle.Fill;
        _leftSplit.Panel1.Controls.Add(_canvas);

        var chartHost = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Panel };
        var chartBar = new Panel { Dock = DockStyle.Top, Height = 30, BackColor = Theme.Panel };
        _chartMode.DropDownStyle = ComboBoxStyle.DropDownList;
        _chartMode.FlatStyle = FlatStyle.Flat;
        _chartMode.Items.AddRange(new object[] { "种群 / 物种", "平均器官等级", "体重 / 规则数 / 突变率", "能量 / 战力" });
        _chartMode.SelectedIndex = 0;
        _chartMode.Location = new Point(8, 4);
        _chartMode.Width = 200;
        Theme.StyleInput(_chartMode);
        _chartMode.SelectedIndexChanged += (_, _) => RebuildChartSeries();
        chartBar.Controls.Add(_chartMode);
        _chart.Dock = DockStyle.Fill;
        chartHost.Controls.Add(_chart);
        chartHost.Controls.Add(chartBar);
        _leftSplit.Panel2.Controls.Add(chartHost);

        // ---------------- 右侧：选项卡 + 观察台 ----------------
        _rightSplit = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            BackColor = Theme.Border,
            SplitterWidth = 4,
            FixedPanel = FixedPanel.Panel2,
        };

        var tabs = new TabControl
        {
            Dock = DockStyle.Fill,
            Appearance = TabAppearance.Normal,
            Font = Theme.UiFont,
        };
        AddTab(tabs, "生物详情", _inspector);
        AddTab(tabs, "规则编辑", _rules);
        // 物种页：上面是清单，下面是差异矩阵热力图。
        // 分成两块是因为"有哪些物种"和"它们之间差异有多大"是两个不同的问题。
        var speciesHost = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            BackColor = Theme.Border,
            SplitterWidth = 4,
            FixedPanel = FixedPanel.Panel2,
        };
        _speciesMatrix.Dock = DockStyle.Fill;
        speciesHost.Panel2.Controls.Add(_speciesMatrix);
        _species.Dock = DockStyle.Fill;
        speciesHost.Panel1.Controls.Add(_species);
        AddTab(tabs, "物种", speciesHost);
        _speciesTabSplit = speciesHost;
        AddTab(tabs, "地形", _terrain);
        AddTab(tabs, "环境", _env);
        _rightSplit.Panel1.Controls.Add(tabs);

        var watchHost = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Background };
        var watchTitle = new Label
        {
            Dock = DockStyle.Top,
            Height = 28,
            Text = "  观察台 —— 选中生物的关键数字",
            Font = Theme.UiFontBold,
            ForeColor = Theme.Text,
            TextAlign = ContentAlignment.MiddleLeft,
            BackColor = Theme.Panel,
        };
        _watch = new InfoPanel { Dock = DockStyle.Fill };
        watchHost.Controls.Add(_watch);
        watchHost.Controls.Add(watchTitle);
        _rightSplit.Panel2.Controls.Add(watchHost);

        _mainSplit = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            BackColor = Theme.Border,
            SplitterWidth = 4,
            FixedPanel = FixedPanel.Panel2,
        };
        _mainSplit.Panel1.Controls.Add(_leftSplit);
        _mainSplit.Panel2.Controls.Add(_rightSplit);

        // ---------------- 状态栏 ----------------
        _status.Dock = DockStyle.Bottom;
        _status.Height = 26;
        _status.BackColor = Theme.Panel;
        _status.ForeColor = Theme.TextDim;
        _status.Font = Theme.UiFontSmall;
        _status.TextAlign = ContentAlignment.MiddleLeft;
        _status.Padding = new Padding(10, 0, 0, 0);

        Controls.Add(_mainSplit);
        Controls.Add(_status);
        Controls.Add(BuildToolbarHost());
        // ---------------- 事件接线 ----------------
        _canvas.CreaturePicked += OnCreaturePicked;
        _rules.TargetProvider = BuildTargetList;
        _rules.GenomeLoader = LoadGenome;
        _rules.ApplyToCreature = ApplyGenome;
        _rules.SpawnNew = SpawnNewCreature;
        _env.ConfigChanged = OnConfigChanged;

        // 地形编辑器接线：模式开关 + 画布上的一笔一划。
        // TerrainPainted 是事件（不是普通委托字段），所以只能用 += 订阅；
        // 只在构造函数里接一次，不会重复订阅。
        _terrain.EditModeChanged = () => _canvas.EditMode = _terrain.EditMode;
        _terrain.TerrainChanged = () => _canvas.Invalidate();
        _canvas.TerrainPainted += OnTerrainPainted;

        _uiTimer.Interval = 33; // ~30fps，足够顺滑又不抢模拟线程的 CPU
        _uiTimer.Tick += (_, _) => UiTick();
        _uiTimer.Start();

        // 用户一旦手动拖过分割条，就不再自动套用布局，尊重他的选择
        _mainSplit.SplitterMoved += (_, _) => _userAdjustedLayout = true;
        _leftSplit.SplitterMoved += (_, _) => _userAdjustedLayout = true;
        _rightSplit.SplitterMoved += (_, _) => _userAdjustedLayout = true;

        _layoutReady = true;
        ApplySplitLayout();

        RebuildChartSeries();
        _env.Bind(_runner.Sim);
        _terrain.Bind(_runner.Sim);
        _rules.RefreshTargets();
        UpdateSpeedButtons();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        ApplySplitLayout();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        // 窗口尺寸变化后重新套用一次。用户手动拖过分割条后就尊重他的选择，不再插手。
        if (!_userAdjustedLayout) ApplySplitLayout();
    }

    private bool _userAdjustedLayout;
    /// <summary>
    /// 布局就绪标志。必须要有：Form 在构造函数里设置 MinimumSize / Size 时就会触发
    /// OnResize，而那一刻三个 SplitContainer 字段还是 null —— 直接在 OnResize 里
    /// 访问它们会抛 NullReferenceException 把程序搞崩。
    /// </summary>
    private bool _layoutReady;

    /// <summary>
    /// 只能在窗口已经有真实尺寸之后调用。
    ///
    /// 两个坑都在这里踩过：
    ///  1. 构造期设 SplitterDistance —— 那时容器宽度还是默认值，会被静默钳到边界，
    ///     表现是"右侧面板整个不见了"。
    ///  2. 构造期设 Panel1MinSize/Panel2MinSize —— 两者之和超过容器当前宽度时，
    ///     SplitContainer 会直接抛 InvalidOperationException 把程序干掉。
    /// 所以：先让它自己布局，量到真实尺寸后再算分割位置。
    /// </summary>
    private void ApplySplitLayout()
    {
        if (!_layoutReady) return;

        _mainSplit.PerformLayout();

        int rightW = Math.Clamp(_mainSplit.Width / 3, 330, 640);
        TrySetSplitter(_mainSplit, _mainSplit.Width - rightW - _mainSplit.SplitterWidth);

        _mainSplit.PerformLayout();
        _leftSplit.PerformLayout();
        _rightSplit.PerformLayout();

        int chartH = Math.Clamp(_leftSplit.Height / 4, 130, 260);
        TrySetSplitter(_leftSplit, _leftSplit.Height - chartH - _leftSplit.SplitterWidth);

        if (_speciesTabSplit is not null)
        {
            _speciesTabSplit.PerformLayout();
            TrySetSplitter(_speciesTabSplit, _speciesTabSplit.Height - Math.Clamp(_speciesTabSplit.Height / 2, 140, 300) - _speciesTabSplit.SplitterWidth);
        }

        int watchH = Math.Clamp(_rightSplit.Height / 2, 150, 400);
        TrySetSplitter(_rightSplit, _rightSplit.Height - watchH - _rightSplit.SplitterWidth);
    }
    private static void TrySetSplitter(SplitContainer sc, int distance)
    {
        // 必须先确认 0 < distance < 容器尺寸，否则 SplitContainer 会抛异常直接把程序干掉
        int extent = sc.Orientation == Orientation.Vertical ? sc.Width : sc.Height;
        if (distance <= 0 || distance >= extent) return;
        try { sc.SplitterDistance = distance; }
        catch (InvalidOperationException) { /* 尺寸还不合适，跳过 */ }
        catch (ArgumentException) { /* 同上 */ }
    }

    /// <summary>
    /// 布局诊断：把三个 SplitContainer 的真实尺寸写到文件。
    /// 面板"消失"这类问题光看截图猜不出来，只有把数值打出来才能定位。
    /// 默认不调用，排查布局问题时再打开（见 readonly 字段 _layoutReady 附近注释）。
    /// </summary>
    private void DumpLayout(string tag)
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "layout-debug.txt");
            File.AppendAllText(path,
                $"[{tag}] form={ClientSize.Width}x{ClientSize.Height} " +
                $"main={_mainSplit.Width}x{_mainSplit.Height}/sd{_mainSplit.SplitterDistance} " +
                $"left={_leftSplit.Width}x{_leftSplit.Height}/sd{_leftSplit.SplitterDistance} " +
                $"right={_rightSplit.Width}x{_rightSplit.Height}/sd{_rightSplit.SplitterDistance}" +
                $" canvas={_canvas.Width}x{_canvas.Height} chart={_chart.Width}x{_chart.Height}" +
                $" inspector={_inspector.Width}x{_inspector.Height}\n");
        }
        catch { /* 诊断失败无所谓 */ }
    }

    private void AddTab(TabControl tabs, string title, Control content)
    {
        var page = new TabPage(title)
        {
            BackColor = Theme.Background,
            ForeColor = Theme.Text,
            Padding = new Padding(0),
        };
        content.Dock = DockStyle.Fill;
        page.Controls.Add(content);
        tabs.TabPages.Add(page);
    }

    // ==================================================================
    // 工具条
    // ==================================================================

    private Panel _toolbar = new();

    private void BuildToolbar()
    {
        _toolbar = new Panel { Dock = DockStyle.Top, Height = 42, BackColor = Theme.Panel };

        _btnPlay.Text = "暂停";
        _btnPlay.Location = new Point(8, 7);
        _btnPlay.Width = 70;
        Theme.StyleButton(_btnPlay, primary: true);
        _btnPlay.Click += (_, _) =>
        {
            _runner.Paused = !_runner.Paused;
            UpdateSpeedButtons();
        };

        _speedBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _speedBox.FlatStyle = FlatStyle.Flat;
        foreach (var s in Speeds) _speedBox.Items.Add(s.Label);
        _speedBox.SelectedIndex = _speedIndex;
        _speedBox.Location = new Point(84, 8);
        _speedBox.Width = 90;
        Theme.StyleInput(_speedBox);
        _speedBox.SelectedIndexChanged += (_, _) =>
        {
            _speedIndex = _speedBox.SelectedIndex;
            if (_speedIndex == 0) { _runner.Paused = true; }
            else { _runner.Speed = Speeds[_speedIndex].Value; _runner.Paused = false; }
            UpdateSpeedButtons();
        };

        _btnNew.Text = "新建生态箱";
        _btnNew.Location = new Point(182, 7);
        _btnNew.Width = 96;
        Theme.StyleButton(_btnNew);
        _btnNew.Click += (_, _) => NewWorld();

        _btnFit.Text = "全景";
        _btnFit.Location = new Point(284, 7);
        _btnFit.Width = 56;
        Theme.StyleButton(_btnFit);
        _btnFit.Click += (_, _) => { _canvas.FollowSelected = false; _chkFollow.Checked = false; _canvas.ZoomToFit(); };

        _chkFollow.Text = "跟随";
        _chkFollow.ForeColor = Theme.Text;
        _chkFollow.Font = Theme.UiFontSmall;
        _chkFollow.Location = new Point(348, 10);
        _chkFollow.Width = 56;
        _chkFollow.CheckedChanged += (_, _) => _canvas.FollowSelected = _chkFollow.Checked;

        _chkVision.Text = "视野";
        _chkVision.ForeColor = Theme.Text;
        _chkVision.Font = Theme.UiFontSmall;
        _chkVision.Location = new Point(408, 10);
        _chkVision.Width = 56;
        _chkVision.Checked = true;
        _chkVision.CheckedChanged += (_, _) => _canvas.ShowVision = _chkVision.Checked;

        _chkOrgans.Text = "器官";
        _chkOrgans.ForeColor = Theme.Text;
        _chkOrgans.Font = Theme.UiFontSmall;
        _chkOrgans.Location = new Point(468, 10);
        _chkOrgans.Width = 54;
        _chkOrgans.Checked = true;
        _chkOrgans.CheckedChanged += (_, _) => _canvas.ShowOrgans = _chkOrgans.Checked;

        _chkTerritory.Text = "领地";
        _chkTerritory.ForeColor = Theme.Text;
        _chkTerritory.Font = Theme.UiFontSmall;
        _chkTerritory.Location = new Point(524, 10);
        _chkTerritory.Width = 54;
        _chkTerritory.Checked = true;
        _chkTerritory.CheckedChanged += (_, _) => _canvas.ShowTerritory = _chkTerritory.Checked;

        _chkMinimap.Text = "小地图";
        _chkMinimap.ForeColor = Theme.Text;
        _chkMinimap.Font = Theme.UiFontSmall;
        _chkMinimap.Location = new Point(580, 10);
        _chkMinimap.Width = 66;
        _chkMinimap.Checked = true;
        _chkMinimap.CheckedChanged += (_, _) => _canvas.ShowMinimap = _chkMinimap.Checked;

        var hint = new Label
        {
            Text = "滚轮缩放 · 点生物看它此刻在想什么 · 双击跟随 · 点小地图跳转 · Space 暂停",
            ForeColor = Theme.TextDim,
            Font = Theme.UiFontSmall,
            AutoSize = true,
            Location = new Point(652, 13),
        };

        _toolbar.Controls.AddRange(new Control[]
        {
            _btnPlay, _speedBox, _btnNew, _btnFit,
            _chkFollow, _chkVision, _chkOrgans, _chkTerritory, _chkMinimap, hint,
        });
    }

    private Control BuildToolbarHost() => _toolbar;

    private void UpdateSpeedButtons()
    {
        _btnPlay.Text = _runner.Paused ? "继续" : "暂停";
        if (_runner.Paused && _speedBox.SelectedIndex != 0)
        {
            _speedBox.SelectedIndex = 0;
        }
    }

    private void NewWorld()
    {
        ulong seed = (ulong)Environment.TickCount64;
        _runner.Paused = true;
        var cfg = _runner.Sim.Config.Clone();
        var sim = new Simulation(cfg, seed);
        _runner.ReplaceSimulation(sim);
        _canvas.SelectedId = -1;
        _canvas.ZoomToFit();
        _env.Bind(sim);
        _terrain.Bind(sim);
        _rules.RefreshTargets();
        _snap = null;
        _runner.Paused = false;
        _speedBox.SelectedIndex = _speedIndex == 0 ? 2 : _speedIndex;
    }

    // ==================================================================
    // 每帧刷新
    // ==================================================================

    private void UiTick()
    {
        _uiTick++;
        var sim = _runner.Sim;
        var snap = sim.Snapshot();
        _snap = snap;
        _canvas.UpdateSnapshot(snap);

        var st = snap.Stats;
        int tps = _runner.MeasuredTicksPerSecond;
        string simTime = FormatSimTime(snap.Tick);
        _status.Text = $"  tick {snap.Tick:N0}（模拟时间 {simTime}）   人口 {snap.Population}   "
                     + $"物种 {snap.SpeciesCount}   最深代数 {st.MaxGeneration}   "
                     + $"平均体重 {st.AverageMass:F2}   平均规则数 {st.AverageRuleCount:F1}   "
                     + $"能量 {st.AverageEnergyPct * 100:F0}%   速度 {tps:N0} tick/s"
                     + (snap.Extinct ? "   ⚠ 已灭绝" : "");

        // 详情每 3 帧刷一次就够了，减少字符串分配
        if (_uiTick % 3 == 0)
        {
            RefreshInspector();
            RefreshWatch();
            RefreshSpecies();
            _chart.SetData(sim.RecentHistory(600));
        }

        // 目标列表每 5 秒刷新一次就够了。过于频繁地重建下拉框会打断正在进行的操作。
        if (_uiTick % 150 == 0) _rules.RefreshTargets();

        // 一笔地形画完了就收尾（重算逃逸距离场、刷新撤销计数）
        PollStrokeEnd();
    }

    // ==================================================================
    // 地形编辑
    // ==================================================================

    private bool _strokeActive;

    /// <summary>
    /// 画布上的一次涂抹。
    ///
    /// 一次拖拽 = 一笔 = 一次撤销单位：第一次调用 BeginStroke，鼠标松开后由
    /// PollStrokeEnd 收尾。中间每次都只是往笔刷缓冲里追加差异。
    /// 逃逸距离场（防卡死）要等到笔画结束才重算 —— 拖拽过程中每帧跑一次 BFS 是纯浪费。
    /// </summary>
    private void OnTerrainPainted(Vec2 world, bool erase)
    {
        var sim = _runner.Sim;
        if (!_strokeActive)
        {
            sim.BeginTerrainStroke();
            _strokeActive = true;
        }

        var brush = erase
            ? TerrainBrush.Erase(_terrain.CurrentBrush().Radius)
            : _terrain.CurrentBrush();

        sim.PaintTerrain(world, brush);
    }

    /// <summary>由 UI 定时器检测鼠标是否已松开，来结束当前笔画。</summary>
    private void PollStrokeEnd()
    {
        if (!_strokeActive) return;
        if (_canvas.MouseButtonsActive) return;

        _runner.Sim.EndTerrainStroke();
        _strokeActive = false;
        _terrain.NotifyStrokeEnded();
    }

    private static string FormatSimTime(int ticks)
    {
        int totalSeconds = ticks / SimulationRunner.BaseTicksPerSecond;
        int h = totalSeconds / 3600;
        int m = totalSeconds % 3600 / 60;
        int s = totalSeconds % 60;
        return h > 0 ? $"{h}小时{m}分" : m > 0 ? $"{m}分{s}秒" : $"{s}秒";
    }

    private void OnCreaturePicked(int id)
    {
        if (id >= 0) _rules.SelectCreature(id);
        RefreshInspector();
    }

    private void RefreshInspector()
    {
        _inspector.Begin();
        int id = _canvas.SelectedId;
        if (id < 0)
        {
            _inspector.Title = "生物详情";
            _inspector.Subtitle = "在生态箱里点击一只生物，这里会显示它的身体、器官，"
                                + "以及它此刻感知到的世界和正在执行的规则。";
            _inspector.Footer = "";
            _inspector.Commit();
            return;
        }

        var d = _runner.Sim.Detail(id);
        if (d is null)
        {
            _inspector.Title = $"生物 #{id}";
            _inspector.Subtitle = "它已经死了。";
            _inspector.Commit();
            return;
        }

        _inspector.Title = $"生物 #{d.Id}";
        _inspector.Subtitle = $"物种 #{d.SpeciesId} · 第 {d.Generation} 代 · "
                            + (d.Alive ? "存活" : "已死亡：" + d.CauseOfDeath);

        _inspector.AddBarHeader($"状态   （年龄上限 {_runner.Sim.Config.Lifespan} tick）");
        _inspector.AddBar("能量", d.Energy, d.MaxEnergy, Theme.Good,
            $"{d.Energy:F0}/{d.MaxEnergy:F0}");
        _inspector.AddBar("生命", d.Hp, d.MaxHp, Theme.Danger,
            $"{d.Hp:F0}/{d.MaxHp:F0}");
        _inspector.AddBar("年龄", d.Age, _runner.Sim.Config.Lifespan, Theme.AccentWarm, $"{d.Age:F0}");

        _inspector.AddBarHeader("器官  ——  等级 0-5；越强，每 tick 开销越大");
        for (int i = 0; i < OrganTable.Count; i++)
        {
            int lvl = d.Genome.Organs[i];
            _inspector.AddBar(OrganTable.Cn[i], lvl, OrganTable.MaxLevel,
                lvl > 0 ? Theme.FromHue(i / (float)OrganTable.Count, 0.6f, 0.95f) : Theme.Border,
                lvl.ToString());
        }

        _inspector.AddHeader("派生属性");
        _inspector.Add("体重", $"{d.Mass:F2}");
        _inspector.Add("最大速度", $"{d.MaxSpeed:F2}");
        _inspector.Add("视野半径", $"{d.Vision:F0}");
        _inspector.Add("攻击力", $"{d.Attack:F2}");
        _inspector.Add("减伤", $"{d.Armor * 100:F0}%");
        _inspector.Add("每 tick 开销", $"{d.Upkeep:F4}");
        _inspector.Add("繁殖阈值", $"{d.ReproThreshold:F0}");
        _inspector.Add("突变率", $"{d.MutationRate:F3}");
        _inspector.Add("规则突变率", $"{d.RuleMutationRate:F3}");

        _inspector.AddHeader("它此刻感知到什么");
        for (int i = 0; i < SensorTable.Count; i++)
        {
            var s = (SensorId)i;
            if (s == SensorId.Always) continue;
            float v = d.SensorValues[i];
            _inspector.AddBar(SensorTable.Cn[i], v, 1f, Theme.Accent,
                v.ToString("F2"));
        }

        // 正在执行的规则
        _inspector.AddHeader("正在执行");
        if (d.LastRule >= 0 && d.LastRule < d.Genome.Rules.Count)
            _inspector.Add($"第 {d.LastRule + 1} 条", d.Genome.Rules[d.LastRule].Describe(), Theme.AccentWarm);
        else
            _inspector.Add("无匹配规则", "静止不动（没有任何规则的条件成立）", Theme.TextDim, dim: true);

        _inspector.Footer = "器官数量、规则数量和阈值都会随繁殖变异 —— 你看到的是它这一刻的样子。";
        _inspector.Commit();
    }

    /// <summary>观察台：选中生物的一页速览，跟详情面板互补（这里更紧凑、常驻可见）。</summary>
    private void RefreshWatch()
    {
        _watch.Begin();
        int id = _canvas.SelectedId;
        if (id < 0)
        {
            _watch.Title = "未选中";
            _watch.Subtitle = "点击生态箱里的任意一只生物。";
            _watch.Commit();
            return;
        }

        var d = _runner.Sim.Detail(id);
        if (d is null) { _watch.Title = $"#{id} 已死亡"; _watch.Commit(); return; }

        _watch.Title = $"#{d.Id} · 物种 #{d.SpeciesId} · 第 {d.Generation} 代";
        _watch.Subtitle = $"体重 {d.Mass:F2}   速度 {d.MaxSpeed:F2}   视野 {d.Vision:F0}   开销 {d.Upkeep:F4}/tick";

        _watch.AddBarHeader("状态");
        _watch.AddBar("能量", d.Energy, d.MaxEnergy, Theme.Good, $"{d.Energy:F0}/{d.MaxEnergy:F0}");
        _watch.AddBar("生命", d.Hp, d.MaxHp, Theme.Danger, $"{d.Hp:F0}/{d.MaxHp:F0}");
        _watch.AddBar("年龄", d.Age, _runner.Sim.Config.Lifespan, Theme.AccentWarm, $"{d.Age:F0}");

        _watch.AddBarHeader("器官等级");
        for (int i = 0; i < OrganTable.Count; i++)
        {
            int lvl = d.Genome.Organs[i];
            if (lvl <= 0) continue;
            _watch.AddBar(OrganTable.Cn[i], lvl, OrganTable.MaxLevel,
                Theme.FromHue(i / (float)OrganTable.Count, 0.66f, 0.95f), lvl.ToString());
        }

        _watch.AddHeader("此刻在做");
        if (d.LastRule >= 0 && d.LastRule < d.Genome.Rules.Count)
            _watch.Add($"规则 {d.LastRule + 1}/{d.Genome.Rules.Count}",
                       d.Genome.Rules[d.LastRule].Describe(), Theme.AccentWarm);
        else
            _watch.Add("无匹配规则", "静止", Theme.TextDim, dim: true);

        _watch.Commit();
    }

    private void RefreshSpecies()
    {
        var list = _runner.Sim.SpeciesSummaries();
        var stats = _runner.Sim.SpeciesStats();

        _species.Begin();
        _species.Title = $"物种清单（存活 {stats.Alive} / 历史累计 {stats.TotalEver}）";
        _species.Subtitle = "物种现在由**基因+行为指纹**自动判定：后代的指纹与物种模式标本的距离超过"
                          + "「物种分化阈值」就另立新种。颜色与生态箱里的生物一一对应。";

        _species.AddHeader("总体");
        _species.Add("存活物种数", stats.Alive.ToString());
        _species.Add("历史累计物种", stats.TotalEver.ToString());
        _species.Add("物种间行为签名距离", $"{stats.AvgSignatureDistance:F1} bit",
                     swatch: stats.AvgSignatureDistance > 6f ? Theme.Good : Theme.Danger);
        _species.Add("平均食性宽容度", $"{stats.AvgCannibalism:F2}");
        if (stats.AvgSignatureDistance <= 6f && stats.Alive > 1)
            _species.Add("⚠ 警告", "物种间行为太像，捕食可能停摆", Theme.Danger);

        int show = Math.Min(list.Count, 14);
        for (int i = 0; i < show; i++)
        {
            var s = list[i];
            var col = Theme.FromHue(s.Hue);
            string status = s.Population > 0
                ? $"{s.Population} 个体"
                : $"已灭绝（存活 {s.LifetimeTicks:N0} tick，峰值 {s.PeakPopulation}）";

            _species.AddHeader($"#{s.Id}  {s.Name}  —  {status}");
            _species.Add("行为独特性", $"{s.Distinctiveness:F3}", col);
            if (s.MostDifferentId > 0)
                _species.Add("最不同的物种", $"#{s.MostDifferentId}  ({s.MostDifferentDistance:F3})");
            if (s.OrganAverage.Length > 0 && s.Population > 0)
                _species.Add("器官构成", OrganSummaryOf(s.OrganAverage));
            if (s.Population > 0)
            {
                _species.Add("平均体重", $"{s.AverageMass:F2}");
                _species.Add("平均规则数", $"{s.AverageRules:F1}");
                _species.Add("食性宽容度", $"{s.AverageCannibalism:F2}");
            }
            _species.Add("源自", s.ParentSpeciesId > 0 ? $"#{s.ParentSpeciesId} 分化" : "创始原型", dim: true);
        }
        if (list.Count == 0) _species.Add("（空）", "", dim: true);

        _species.Footer = "「行为独特性」是它与其它存活物种的平均指纹距离。"
                        + "如果所有物种这个值都很低，说明它们只是颜色不同的同一批生物 —— "
                        + "把「物种分化阈值」调低，或者加一道山脉制造地理隔离。";
        _species.Commit();

        var (mids, mnames, mmat) = _runner.Sim.SpeciesDistanceMatrix(9);
        _speciesMatrix.SetMatrix(mids, mnames, mmat, mids.Length);
    }

    private static string OrganSummaryOf(float[] avg)
    {
        var parts = new List<string>();
        for (int i = 0; i < OrganTable.Count; i++)
            if (avg[i] >= 0.15f) parts.Add($"{OrganTable.Cn[i]}{avg[i]:F1}");
        return parts.Count == 0 ? "(全裸)" : string.Join(" ", parts);
    }

    // ==================================================================
    // 曲线系列
    // ==================================================================

    private void RebuildChartSeries()
    {
        _chart.Series.Clear();
        switch (_chartMode.SelectedIndex)
        {
            case 1:
                _chart.Title = "平均器官等级（整个种群）";
                for (int i = 0; i < OrganTable.Count; i++)
                {
                    int oi = i;
                    _chart.Series.Add(new ChartSeries
                    {
                        Name = OrganTable.Cn[i],
                        Color = Theme.FromHue(i / (float)OrganTable.Count, 0.66f, 0.95f),
                        Value = s => s.AverageOrgans.Length > oi ? s.AverageOrgans[oi] : 0f,
                        FixedMin = 0f,
                        FixedMax = OrganTable.MaxLevel,
                    });
                }
                break;

            case 2:
                _chart.Title = "体重 / 规则数 / 突变率（各自归一化）";
                _chart.Series.Add(new ChartSeries { Name = "平均体重", Color = Theme.Accent, Value = s => s.AverageMass, Normalize = true });
                _chart.Series.Add(new ChartSeries { Name = "平均规则数", Color = Theme.AccentWarm, Value = s => s.AverageRuleCount, Normalize = true });
                _chart.Series.Add(new ChartSeries { Name = "平均突变率", Color = Theme.Good, Value = s => s.AverageMutationRate, Normalize = true });
                _chart.Series.Add(new ChartSeries { Name = "最深代数", Color = Theme.FromHue(0.78f), Value = s => s.MaxGeneration, Normalize = true });
                break;

            case 3:
                _chart.Title = "能量 / 战力 / 年龄";
                _chart.Series.Add(new ChartSeries { Name = "平均能量%", Color = Theme.Good, Value = s => s.AverageEnergyPct, FixedMin = 0f, FixedMax = 1f });
                _chart.Series.Add(new ChartSeries { Name = "平均战力", Color = Theme.Danger, Value = s => s.AverageCombatPower, FixedMin = 0f, FixedMax = 1f });
                _chart.Series.Add(new ChartSeries { Name = "平均年龄%", Color = Theme.AccentWarm, Value = s => s.AverageAgePct, FixedMin = 0f, FixedMax = 1f });
                break;

            default:
                _chart.Title = "种群 / 物种数量";
                _chart.Series.Add(new ChartSeries { Name = "人口", Color = Theme.Accent, Value = s => s.Population, Normalize = true });
                _chart.Series.Add(new ChartSeries { Name = "物种数", Color = Theme.AccentWarm, Value = s => s.SpeciesCount, Normalize = true });
                _chart.Series.Add(new ChartSeries { Name = "平均规则数", Color = Theme.Good, Value = s => s.AverageRuleCount, Normalize = true });
                break;
        }
        _chart.SetData(_runner.Sim.RecentHistory(600));
    }

    // ==================================================================
    // 规则编辑器的数据接线
    // ==================================================================

    private List<(int Id, string Label)> BuildTargetList()
    {
        var list = new List<(int, string)>();
        var snap = _snap;
        if (snap is null) return list;

        // 只列出前 150 只，避免下拉框里塞进上千项。
        // 但当前点选的那只一定要在列表里，否则"点生物 -> 规则编辑器跟着切"这条链路会断。
        int picked = _canvas.SelectedId;
        string? pickedLabel = null;

        int n = Math.Min(snap.CreatureCount, 150);
        for (int i = 0; i < n; i++)
        {
            var c = snap.Creatures[i];
            string label = $"#{c.Id}  物种#{c.SpeciesId}  能量{c.EnergyPct * 100:F0}%";
            if (c.Id == picked) pickedLabel = label;
            list.Add((c.Id, label));
        }

        if (picked >= 0 && pickedLabel is null)
        {
            for (int i = 0; i < snap.CreatureCount; i++)
            {
                if (snap.Creatures[i].Id != picked) continue;
                var c = snap.Creatures[i];
                list.Insert(0, (c.Id, $"#{c.Id}  物种#{c.SpeciesId}  能量{c.EnergyPct * 100:F0}%  ← 当前选中"));
                break;
            }
        }
        return list;
    }

    private Genome? LoadGenome(int id)
    {
        var d = _runner.Sim.Detail(id);
        return d?.Genome;
    }

    private void ApplyGenome(int id, Genome g)
    {
        _runner.Sim.ReplaceGenome(id, g);
        RefreshInspector();
    }

    private void SpawnNewCreature(Genome g)
    {
        _runner.Sim.Inject(g);
        _rules.RefreshTargets();
    }

    private void OnConfigChanged()
    {
        // 环境改了参数之后派生属性可能失效，重算一次保证 UI 和模拟一致
        _runner.Sim.RefreshDerived();
    }

    // ==================================================================
    // 键盘
    // ==================================================================

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData)
        {
            case Keys.Space:
                _runner.Paused = !_runner.Paused;
                UpdateSpeedButtons();
                return true;
            case Keys.F:
                _canvas.FollowSelected = false;
                _chkFollow.Checked = false;
                _canvas.ZoomToFit();
                return true;
            case Keys.D1: _speedBox.SelectedIndex = 1; return true;
            case Keys.D2: _speedBox.SelectedIndex = 2; return true;
            case Keys.D3: _speedBox.SelectedIndex = 3; return true;
            case Keys.D4: _speedBox.SelectedIndex = 4; return true;
            case Keys.D5: _speedBox.SelectedIndex = 5; return true;
            case Keys.D6: _speedBox.SelectedIndex = 6; return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _uiTimer.Stop();
        _runner.Dispose();
        base.OnFormClosing(e);
    }
}
