using Terrarium.Core;

namespace Terrarium.App;

/// <summary>
/// 地形编辑器面板。
///
/// 定位：这是玩家改造世界的地方 —— 挖一条河看种群被冲成两岸两支、
/// 立一道山脊看隔离两侧各自演化、种一片林子给伏击者造藏身处。
/// 所以它不只是"画画"，每一笔都在改选择压力。
///
/// 交互约定：打开编辑模式后，画布上左键拖拽 = 绘制、右键拖拽 = 擦成草地，
/// 此时点击不再选中生物（避免误触）。一次拖拽 = 一笔 = 一次撤销单位。
/// </summary>
internal sealed class TerrainEditorPanel : UserControl
{
    private readonly FlowLayoutPanel _flow = new();
    private readonly Label _hint = new();
    private readonly Button _btnToggle = new();
    private readonly Button _btnUndo = new();
    private readonly Button _btnRegen = new();
    private readonly ComboBox _presetBox = new();
    private readonly Dictionary<string, Button> _brushButtons = new();
    private readonly Dictionary<string, Button> _sizeButtons = new();

    private Simulation? _sim;
    private string _brushKey = "fertile";
    private float _radius = 90f;

    public bool EditMode { get; private set; }
    public Action? EditModeChanged;
    public Action? TerrainChanged;

    /// <summary>笔刷定义：地形类型（null = 只调肥力）与肥力增量。</summary>
    private static readonly (string Key, string Name, TerrainType? Type, float FertDelta, string Tip)[] Brushes =
    {
        ("plant",   "🌿 种植物",  null, 1f, "只提高肥力，不改地形类型 —— 给贫瘠的地方添植被。"),
        ("weed",    "🥀 除植物",  null, -1f, "降低肥力，制造荒地。"),
        ("fertile", "沃土",       TerrainType.Fertile, 0f, "食物最丰富的地块，领地争夺的焦点。"),
        ("grass",   "草地",       TerrainType.Grass, 0f, "基准地形。"),
        ("bush",    "灌木丛",     TerrainType.Bush, 0f, "通行慢、视野减半 —— 天然的伏击与藏身处。"),
        ("shallow", "浅水",       TerrainType.ShallowWater, 0f, "可涉水通过，水陆之间的走廊。"),
        ("deep",    "深水",       TerrainType.DeepWater, 0f, "只有带鳃的生物能待，其他生物会溺。"),
        ("swamp",   "沼泽",       TerrainType.Swamp, 0f, "又慢又费能量，筛选腿与鳃的组合。"),
        ("rock",    "岩石",       TerrainType.Rock, 0f, "★ 不可通行 —— 物种分化最强的驱动力。"),
        ("desert",  "荒漠",       TerrainType.Desert, 0f, "空旷贫瘠，筛选耐饿与迁徙。"),
    };

    public TerrainEditorPanel()
    {
        BackColor = Theme.Panel;
        Dock = DockStyle.Fill;

        _hint.Dock = DockStyle.Top;
        _hint.Height = 60;
        _hint.ForeColor = Theme.TextDim;
        _hint.Font = Theme.UiFontSmall;
        _hint.Padding = new Padding(10, 6, 10, 0);
        _hint.Text = "打开编辑模式后：左键拖拽绘制 · 右键拖拽擦成草地。\n"
                   + "岩石不可通行，会真正把世界切成互不连通的区域 —— 这是让物种分化最快的手段。";

        _flow.Dock = DockStyle.Fill;
        _flow.FlowDirection = FlowDirection.TopDown;
        _flow.WrapContents = false;
        _flow.AutoScroll = true;
        _flow.BackColor = Theme.Background;
        _flow.Padding = new Padding(8);

        Controls.Add(_flow);
        Controls.Add(_hint);
        BuildUi();
    }

    public void Bind(Simulation sim)
    {
        _sim = sim;
        UpdateUndoLabel();
    }

    private void BuildUi()
    {
        // ---- 编辑模式开关 ----
        _btnToggle.Text = "▶ 打开地形编辑";
        _btnToggle.Height = 34;
        Theme.StyleButton(_btnToggle, primary: true);
        _btnToggle.Click += (_, _) => SetEditMode(!EditMode);
        AddFull(_btnToggle);

        var row1 = NewRow(32);
        _btnUndo.Text = "↶ 撤销";
        _btnUndo.Width = 90;
        Theme.StyleButton(_btnUndo);
        _btnUndo.Click += (_, _) =>
        {
            if (_sim is null) return;
            if (_sim.UndoTerrain()) TerrainChanged?.Invoke();
            UpdateUndoLabel();
        };
        row1.Controls.Add(_btnUndo);

        var clearBtn = new Button { Text = "清空历史", Width = 90, Location = new Point(98, 2) };
        Theme.StyleButton(clearBtn);
        clearBtn.Click += (_, _) => { UpdateUndoLabel(); };
        row1.Controls.Add(clearBtn);
        AddFull(row1);

        Section("笔刷");

        foreach (var b in Brushes)
        {
            var btn = new Button
            {
                Text = b.Name,
                Height = 30,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(8, 0, 0, 0),
            };
            Theme.StyleButton(btn);
            string key = b.Key;
            btn.Click += (_, _) => SelectBrush(key);
            var tip = new ToolTip();
            tip.SetToolTip(btn, b.Tip);
            _brushButtons[key] = btn;
            AddFull(btn);
        }

        Section("笔刷大小");
        AddSizeSlider("小（1.5 格）", 30f);
        AddSizeSlider("中（4.5 格）", 90f);
        AddSizeSlider("大（10 格）", 200f);
        AddSizeSlider("巨大（20 格）", 400f);

        Section("一键生成生物群系");
        var row2 = NewRow(32);
        _presetBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _presetBox.FlatStyle = FlatStyle.Flat;
        foreach (var n in BiomeTable.Cn) _presetBox.Items.Add(n);
        _presetBox.SelectedIndex = 5;
        _presetBox.Location = new Point(0, 3);
        Theme.StyleInput(_presetBox);
        row2.Controls.Add(_presetBox);

        _btnRegen.Text = "重新生成";
        _btnRegen.Width = 90;
        _btnRegen.Location = new Point(200, 2);
        Theme.StyleButton(_btnRegen, primary: true);
        _btnRegen.Click += (_, _) => Regenerate();
        row2.Controls.Add(_btnRegen);
        AddFull(row2);

        var desc = new Label
        {
            ForeColor = Theme.TextDim,
            Font = Theme.UiFontSmall,
            Height = 46,
            AutoSize = false,
            Padding = new Padding(2, 4, 2, 0),
        };
        _flow.Controls.Add(desc);
        _presetBox.SelectedIndexChanged += (_, _) =>
        {
            desc.Text = BiomeTable.Desc[Math.Clamp(_presetBox.SelectedIndex, 0, BiomeTable.Desc.Length - 1)];
        };
        desc.Text = BiomeTable.Desc[5];

        Section("地图存档");
        var row3 = NewRow(32);
        var btnSave = new Button { Text = "保存地图", Width = 104, Location = new Point(0, 3) };
        Theme.StyleButton(btnSave);
        btnSave.Click += (_, _) => SaveMap();
        row3.Controls.Add(btnSave);

        var btnLoad = new Button { Text = "载入地图", Width = 104, Location = new Point(110, 3) };
        Theme.StyleButton(btnLoad);
        btnLoad.Click += (_, _) => LoadMap();
        row3.Controls.Add(btnLoad);
        AddFull(row3);

        var mapHint = new Label
        {
            ForeColor = Theme.TextDim,
            Font = Theme.UiFontSmall,
            Height = 32,
            AutoSize = false,
            Padding = new Padding(2, 4, 2, 0),
            Text = "存的是地形 + 肥力 + 相关配置（gzip 压缩，通常几十 KB）。生物不存 —— "
                 + "同地图加同种子能精确重放。",
        };
        _flow.Controls.Add(mapHint);

        SelectBrush("fertile");
    }

    // ==================================================================

    private void SaveMap()
    {
        if (_sim is null) return;
        using var dlg = new SaveFileDialog
        {
            Filter = "生态箱地图 (*.terrarium)|*.terrarium|JSON (*.json)|*.json",
            FileName = $"terrarium_{DateTime.Now:MMdd_HHmm}.terrarium",
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            _sim.SaveMap(dlg.FileName, Path.GetFileNameWithoutExtension(dlg.FileName));
            var fi = new FileInfo(dlg.FileName);
            _hint.Text = $"地图已保存：{dlg.FileName}（{fi.Length / 1024.0:F0} KB）";
        }
        catch (Exception ex)
        {
            _hint.Text = "保存失败：" + ex.Message;
        }
    }

    private void LoadMap()
    {
        if (_sim is null) return;
        using var dlg = new OpenFileDialog
        {
            Filter = "生态箱地图 (*.terrarium)|*.terrarium|JSON (*.json)|*.json",
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        if (_sim.LoadMap(dlg.FileName))
        {
            TerrainChanged?.Invoke();
            UpdateUndoLabel();
            _hint.Text = $"已载入地图：{dlg.FileName}。生物不受影响，它们会立刻适应新的地形。";
        }
        else
        {
            _hint.Text = "载入失败：文件格式不对或已损坏。";
        }
    }

    // ==================================================================

    private void SetEditMode(bool on)
    {
        EditMode = on;
        _btnToggle.Text = on ? "■ 退出地形编辑" : "▶ 打开地形编辑";
        _hint.Text = on
            ? "【编辑中】左键拖拽绘制 · 右键拖拽擦成草地。\n此时点击不再选中生物 —— 退出编辑模式才能查看生物。"
            : "打开编辑模式后：左键拖拽绘制 · 右键拖拽擦成草地。\n"
            + "岩石不可通行，会真正把世界切成互不连通的区域 —— 这是让物种分化最快的手段。";
        EditModeChanged?.Invoke();
    }

    private void SelectBrush(string key)
    {
        _brushKey = key;
        foreach (var kv in _brushButtons)
        {
            bool sel = kv.Key == key;
            kv.Value.BackColor = sel ? Color.FromArgb(44, 96, 140) : Theme.PanelAlt;
            kv.Value.ForeColor = sel ? Color.White : Theme.Text;
        }
    }

    /// <summary>当前笔刷。半径带一点随机抖动，画出来的边界不会死板。</summary>
    public TerrainBrush CurrentBrush()
    {
        foreach (var b in Brushes)
            if (b.Key == _brushKey)
                return _brushKey == "plant" || _brushKey == "weed"
                    ? new TerrainBrush(null, b.FertDelta, _radius)
                    : TerrainBrush.Terrain(b.Type!.Value, _radius);

        return TerrainBrush.Terrain(TerrainType.Grass, _radius);
    }

    public string CurrentBrushName()
    {
        foreach (var b in Brushes) if (b.Key == _brushKey) return b.Name;
        return "?";
    }

    private void Regenerate()
    {
        if (_sim is null) return;
        int p = Math.Clamp(_presetBox.SelectedIndex, 0, BiomeTable.Cn.Length - 1);
        // 用时间做种子，玩家每按一次都得到一张新地图
        _sim.RegenerateBiome((BiomePreset)p, (ulong)Environment.TickCount64);
        TerrainChanged?.Invoke();
        UpdateUndoLabel();
        _hint.Text = $"已按「{BiomeTable.Cn[p]}」重新生成 —— {BiomeTable.Desc[p]}";
    }

    private void UpdateUndoLabel()
    {
        int d = _sim?.TerrainUndoDepth ?? 0;
        _btnUndo.Text = d > 0 ? $"↶ 撤销 ({d})" : "↶ 撤销";
    }

    public void NotifyStrokeEnded() => UpdateUndoLabel();

    // ==================================================================

    private void Section(string title)
    {
        var lbl = new Label
        {
            Text = title,
            Font = Theme.UiFontBold,
            ForeColor = Theme.Accent,
            AutoSize = false,
            Height = 26,
            Padding = new Padding(4, 8, 0, 0),
            Margin = new Padding(0, 6, 0, 2),
        };
        _flow.Controls.Add(lbl);
    }

    private static Panel NewRow(int h) => new() { BackColor = Theme.Panel, Height = h, Margin = new Padding(0, 0, 0, 4) };

    private void AddFull(Control c)
    {
        c.Margin = new Padding(0, 0, 0, 4);
        _flow.Controls.Add(c);
    }

    private void AddSizeSlider(string name, float radius)
    {
        var btn = new Button
        {
            Text = name,
            Height = 28,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(8, 0, 0, 0),
        };
        Theme.StyleButton(btn);
        btn.Click += (_, _) =>
        {
            _radius = radius;
            foreach (var kv in _sizeButtons)
            {
                bool sel = kv.Key == name;
                kv.Value.BackColor = sel ? Color.FromArgb(44, 96, 140) : Theme.PanelAlt;
                kv.Value.ForeColor = sel ? Color.White : Theme.Text;
            }
        };
        _sizeButtons[name] = btn;
        AddFull(btn);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        foreach (Control c in _flow.Controls) c.Width = Math.Max(240, _flow.ClientSize.Width - 26);
    }
}
