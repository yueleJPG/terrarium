using Terrarium.Core;

namespace Terrarium.App;

/// <summary>
/// 卡片式规则编辑器 —— "像 Scratch 那样给生物定规则"的落地形态。
///
/// 为什么不做像素级积木拖拽：那种编辑器的工程量能占整个项目的 60%（拖拽、吸附、
/// 嵌套、滚动、撤销），而表达能力并不比"下拉框 + 数字输入"强。卡片式可以做到
/// 同样的语义（条件 AND + 动作列表 + 优先级排序），开发量小一个数量级，而且
/// 更容易看懂、更容易改数值做实验。积木视图可以作为将来的皮肤叠加在这一层之上。
///
/// 编辑器始终操作一份基因组副本；只有按下"保存"才会写回生物，
/// 所以边跑模拟边改规则不会互相打架。
/// </summary>
internal sealed class RuleEditorPanel : UserControl
{
    private readonly ComboBox _targetBox = new();
    private readonly Button _btnReload = new();
    private readonly Button _btnAddRule = new();
    private readonly Button _btnApply = new();
    private readonly Button _btnSpawn = new();
    private readonly Button _btnExport = new();
    private readonly Button _btnImport = new();
    private readonly Button _btnBlank = new();
    private readonly Label _hint = new();
    private readonly FlowLayoutPanel _cards = new();

    private Genome _working = new();
    private int _targetId = -1;
    private bool _loading;
    private List<(int Id, string Label)> _targets = new();

    public Func<List<(int Id, string Label)>>? TargetProvider;
    public Func<int, Genome?>? GenomeLoader;
    public Action<int, Genome>? ApplyToCreature;
    public Action<Genome>? SpawnNew;

    public RuleEditorPanel()
    {
        BackColor = Theme.Panel;
        Dock = DockStyle.Fill;

        // ---------- 顶部工具条 ----------
        // 高度要留够两行按钮（y=55 的第二行到 83），否则会被面板裁掉一截
        var top = new Panel { Dock = DockStyle.Top, Height = 92, BackColor = Theme.Panel };

        var lbl = new Label
        {
            Text = "编辑目标",
            ForeColor = Theme.TextDim,
            Font = Theme.UiFontSmall,
            Location = new Point(10, 8),
            AutoSize = true,
        };

        _targetBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _targetBox.Location = new Point(10, 26);
        _targetBox.Width = 200;
        _targetBox.Height = 24;
        _targetBox.FlatStyle = FlatStyle.Flat;
        Theme.StyleInput(_targetBox);
        _targetBox.SelectedIndexChanged += (_, _) => OnTargetChanged();

        _btnReload.Text = "重新载入";
        _btnReload.Location = new Point(216, 25);
        _btnReload.Width = 78;
        Theme.StyleButton(_btnReload);
        _btnReload.Click += (_, _) => OnTargetChanged();

        _btnBlank.Text = "空白基因组";
        _btnBlank.Location = new Point(300, 25);
        _btnBlank.Width = 90;
        Theme.StyleButton(_btnBlank);
        _btnBlank.Click += (_, _) =>
        {
            _targetId = -1;
            _working = Genome.CreateFounder(new Rng(20240501), 0, "人造生物");
            Rebuild();
        };

        _btnApply.Text = "保存到选中";
        _btnApply.Location = new Point(10, 58);
        _btnApply.Width = 108;
        Theme.StyleButton(_btnApply, primary: true);
        _btnApply.Click += (_, _) => DoApply();

        _btnSpawn.Text = "投放一只";
        _btnSpawn.Location = new Point(124, 58);
        _btnSpawn.Width = 96;
        Theme.StyleButton(_btnSpawn);
        _btnSpawn.Click += (_, _) => DoSpawn();

        _btnExport.Text = "导出";
        _btnExport.Location = new Point(226, 58);
        _btnExport.Width = 66;
        Theme.StyleButton(_btnExport);
        _btnExport.Click += (_, _) => DoExport();

        _btnImport.Text = "导入";
        _btnImport.Location = new Point(298, 58);
        _btnImport.Width = 66;
        Theme.StyleButton(_btnImport);
        _btnImport.Click += (_, _) => DoImport();

        top.Controls.AddRange(new Control[]
        {
            lbl, _targetBox, _btnReload, _btnBlank,
            _btnApply, _btnSpawn, _btnExport, _btnImport,
        });

        _hint.Dock = DockStyle.Top;
        _hint.Height = 30;
        _hint.ForeColor = Theme.TextDim;
        _hint.Font = Theme.UiFontSmall;
        _hint.Padding = new Padding(10, 0, 10, 0);
        _hint.Text = "规则自上而下求值，第一条条件全部满足的规则会被执行（优先级 = 顺序）。";

        // ---------- 卡片区 ----------
        _cards.Dock = DockStyle.Fill;
        _cards.FlowDirection = FlowDirection.TopDown;
        _cards.WrapContents = false;
        _cards.AutoScroll = true;
        _cards.BackColor = Theme.Background;
        _cards.Padding = new Padding(8);
        _cards.Resize += (_, _) => { if (!_loading) RelayoutCards(); };

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 44, BackColor = Theme.Panel };
        _btnAddRule.Text = "＋ 添加一条规则";
        _btnAddRule.Location = new Point(10, 8);
        _btnAddRule.Width = 150;
        Theme.StyleButton(_btnAddRule, primary: true);
        _btnAddRule.Click += (_, _) =>
        {
            _working.Rules.Add(new Rule(
                new[] { new Condition(SensorId.Always, CompareOp.Greater, 0f) },
                new[] { new ActionSpec(ActionId.Move, AimId.NearestFood) }));
            Rebuild();
        };
        bottom.Controls.Add(_btnAddRule);

        Controls.Add(_cards);
        Controls.Add(bottom);
        Controls.Add(_hint);
        Controls.Add(top);
    }

    // ==================================================================

    public void RefreshTargets()
    {
        if (TargetProvider is null) return;

        int keepId = CurrentTargetId();
        var list = TargetProvider();
        _targets = list;

        _loading = true;
        _targetBox.Items.Clear();
        foreach (var t in list) _targetBox.Items.Add(t.Label);
        int idx = list.FindIndex(t => t.Id == keepId);
        _targetBox.SelectedIndex = idx >= 0 ? idx : (list.Count > 0 ? 0 : -1);
        _loading = false;

        // 刻意不在这里 Rebuild()：定时刷新下拉列表不应该冲掉玩家正在编辑的内容。
    }

    /// <summary>外部（点击生态箱里的生物）指定当前编辑目标。</summary>
    public void SelectCreature(int id)
    {
        // 生物列表可能没包含这一只（列表只列前若干只），所以先强制刷新一次，
        // 让 MainForm 把"当前选中生物"补进列表头部。
        RefreshTargets();

        int idx = _targets.FindIndex(t => t.Id == id);
        if (idx < 0)
        {
            _hint.Text = $"#{id} 不在候选列表里，已保持当前编辑目标不变。";
            return;
        }

        _loading = true;
        _targetBox.SelectedIndex = idx;
        _loading = false;
        OnTargetChanged();
    }

    private int CurrentTargetId()
    {
        int i = _targetBox.SelectedIndex;
        return i >= 0 && i < _targets.Count ? _targets[i].Id : -1;
    }

    private void OnTargetChanged()
    {
        if (_loading) return;
        _targetId = CurrentTargetId();
        Genome? g = _targetId >= 0 ? GenomeLoader?.Invoke(_targetId) : null;
        _working = g?.Clone() ?? Genome.CreateFounder(new Rng(20240501), 0, "人造生物");
        Rebuild();
    }

    private void DoApply()
    {
        if (_targetId < 0) { DoSpawn(); return; }
        ApplyToCreature?.Invoke(_targetId, _working.Clone());
        _hint.Text = $"已把规则写回 #{_targetId}。它的派生属性（速度/视野/代谢）已同步重算。";
    }

    private void DoSpawn()
    {
        _working.Name = string.IsNullOrWhiteSpace(_working.Name) ? "人造生物" : _working.Name;
        _working.SpeciesId = 0;
        SpawnNew?.Invoke(_working.Clone());
        _hint.Text = "已投放一只人造生物到生态箱。它之后会像其他生物一样突变和繁殖。";
    }

    private void DoExport()
    {
        using var dlg = new SaveFileDialog
        {
            Filter = "基因组 JSON (*.json)|*.json",
            FileName = $"{_working.Name}_{DateTime.Now:MMdd_HHmm}.json",
            InitialDirectory = Path.Combine(AppContext.BaseDirectory, "genomes"),
        };
        try { Directory.CreateDirectory(dlg.InitialDirectory); } catch { /* 忽略 */ }
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        GenomeIO.Save(_working, dlg.FileName);
        _hint.Text = "已导出：" + dlg.FileName;
    }

    private void DoImport()
    {
        using var dlg = new OpenFileDialog { Filter = "基因组 JSON (*.json)|*.json" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            var g = GenomeIO.Load(dlg.FileName);
            if (g is null) { _hint.Text = "读不出来：文件格式不对。"; return; }
            _working = g;
            _targetId = -1;
            Rebuild();
            _hint.Text = $"已载入 {g.Name}（{g.Rules.Count} 条规则，代 {g.Generation}）。按「投放一只」把它放进生态箱。";
        }
        catch (Exception ex)
        {
            _hint.Text = "导入失败：" + ex.Message;
        }
    }

    // ==================================================================
    // 卡片构建
    // ==================================================================

    private void Rebuild()
    {
        _loading = true;
        _cards.SuspendLayout();

        // 必须先取快照再清空。
        // Control.Dispose() 会顺手把自己从父容器的 Controls 里摘掉，
        // 所以 "foreach (var c in _cards.Controls) c.Dispose();" 是在遍历中修改集合，
        // 必然抛 InvalidOperationException，结果是卡片被清空后一张也没重新加回来 ——
        // 界面表现就是规则列表整个空白。
        var old = new List<Control>();
        foreach (Control c in _cards.Controls) old.Add(c);
        _cards.Controls.Clear();
        foreach (Control c in old) c.Dispose();

        for (int r = 0; r < _working.Rules.Count; r++)
            _cards.Controls.Add(BuildRuleCard(r));

        _cards.ResumeLayout();
        _loading = false;
        RelayoutCards();

        _hint.Text = _targetId >= 0
            ? $"正在编辑 #{_targetId} 的基因组：{_working.Rules.Count} 条规则。改完按「保存到选中」。"
            : $"正在编辑一份独立基因组：{_working.Rules.Count} 条规则。按「投放一只」放进生态箱。";
    }

    private void RelayoutCards()
    {
        int w = Math.Max(320, _cards.ClientSize.Width - 26);
        foreach (Control c in _cards.Controls)
        {
            c.Width = w;
            if (c is RuleCard rc) rc.LayoutRows(w);
        }
    }

    private RuleCard BuildRuleCard(int index)
    {
        var card = new RuleCard(this, index, _working.Rules[index]);
        return card;
    }

    // ---- 供 RuleCard 回调 ----

    internal void MoveRule(int index, int delta)
    {
        int j = index + delta;
        if (j < 0 || j >= _working.Rules.Count) return;
        (_working.Rules[index], _working.Rules[j]) = (_working.Rules[j], _working.Rules[index]);
        Rebuild();
    }

    internal void DuplicateRule(int index)
    {
        _working.Rules.Insert(index + 1, _working.Rules[index].Clone());
        Rebuild();
    }

    internal void DeleteRule(int index)
    {
        if (_working.Rules.Count <= 1) return;
        _working.Rules.RemoveAt(index);
        Rebuild();
    }

    internal void AddCondition(int ruleIndex)
    {
        var rule = _working.Rules[ruleIndex];
        if (rule.If.Count >= 4) return;
        rule.If.Add(new Condition(SensorId.EnergyPct, CompareOp.Greater, 0.5f));
        Rebuild();
    }

    internal void RemoveCondition(int ruleIndex, int condIndex)
    {
        var rule = _working.Rules[ruleIndex];
        if (rule.If.Count <= 1) return;
        rule.If.RemoveAt(condIndex);
        Rebuild();
    }

    internal void AddAction(int ruleIndex)
    {
        var rule = _working.Rules[ruleIndex];
        if (rule.Then.Count >= 3) return;
        rule.Then.Add(new ActionSpec(ActionId.Eat));
        Rebuild();
    }

    internal void RemoveAction(int ruleIndex, int actIndex)
    {
        var rule = _working.Rules[ruleIndex];
        if (rule.Then.Count <= 1) return;
        rule.Then.RemoveAt(actIndex);
        Rebuild();
    }

    internal void UpdateCondition(int ruleIndex, int condIndex, SensorId sensor, CompareOp op, float value)
    {
        var rule = _working.Rules[ruleIndex];
        var c = rule.If[condIndex];
        c.Sensor = sensor;
        c.Op = op;
        c.Value = value;
        rule.If[condIndex] = c;
    }

    internal void UpdateAction(int ruleIndex, int actIndex, ActionId action, AimId aim, int organ, float param)
    {
        var rule = _working.Rules[ruleIndex];
        var a = rule.Then[actIndex];
        a.Action = action;
        a.Aim = aim;
        a.Organ = organ;
        a.Param = param;
        rule.Then[actIndex] = a;
    }

    // ==================================================================
    // 单张规则卡片
    // ==================================================================

    private sealed class RuleCard : Panel
    {
        private readonly RuleEditorPanel _owner;
        private readonly int _index;
        private readonly Rule _rule;
        private readonly List<Control> _rows = new();
        private readonly Label _title;

        private const int HeaderH = 30;
        private const int RowH = 28;

        public RuleCard(RuleEditorPanel owner, int index, Rule rule)
        {
            _owner = owner;
            _index = index;
            _rule = rule;

            BackColor = Theme.Panel;
            Margin = new Padding(0, 0, 0, 8);
            Padding = new Padding(8);
            BorderStyle = BorderStyle.FixedSingle;

            _title = new Label
            {
                Text = $"规则 {index + 1}",
                Font = Theme.UiFontBold,
                ForeColor = index == 0 ? Theme.AccentWarm : Theme.Text,
                AutoSize = true,
                Location = new Point(8, 6),
            };
            Controls.Add(_title);

            var btnUp = MiniButton("↑", 8, "上移优先级");
            btnUp.Click += (_, _) => _owner.MoveRule(_index, -1);
            var btnDown = MiniButton("↓", 8, "下移优先级");
            btnDown.Click += (_, _) => _owner.MoveRule(_index, 1);
            var btnDup = MiniButton("复制", 8, "复制这条规则");
            btnDup.Click += (_, _) => _owner.DuplicateRule(_index);
            var btnDel = MiniButton("删除", 8, "删除这条规则");
            btnDel.ForeColor = Theme.Danger;
            btnDel.Click += (_, _) => _owner.DeleteRule(_index);

            Controls.Add(btnUp);
            Controls.Add(btnDown);
            Controls.Add(btnDup);
            Controls.Add(btnDel);
            _headerButtons = new[] { btnUp, btnDown, btnDup, btnDel };

            int y = HeaderH;
            for (int i = 0; i < _rule.If.Count; i++) AddConditionRow(i, ref y);
            AddSmallRow("＋ 条件", ref y, () => _owner.AddCondition(_index));

            for (int i = 0; i < _rule.Then.Count; i++) AddActionRow(i, ref y);
            AddSmallRow("＋ 动作", ref y, () => _owner.AddAction(_index));

            Height = y + 10;
        }

        private readonly Button[] _headerButtons;

        private Button MiniButton(string text, int x, string tip)
        {
            var b = new Button
            {
                Text = text,
                Width = text.Length > 1 ? 44 : 26,
                Height = 22,
                FlatStyle = FlatStyle.Flat,
                BackColor = Theme.PanelAlt,
                ForeColor = Theme.Text,
                Font = Theme.UiFontSmall,
                Cursor = Cursors.Hand,
            };
            b.FlatAppearance.BorderSize = 0;
            var tipObj = new ToolTip();
            tipObj.SetToolTip(b, tip);
            return b;
        }

        private void AddConditionRow(int condIndex, ref int y)
        {
            var row = new Panel { BackColor = Theme.Panel, Location = new Point(8, y), Height = RowH };
            Controls.Add(row);
            _rows.Add(row);

            var prefix = new Label
            {
                Text = condIndex == 0 ? "如果" : "且",
                ForeColor = Theme.TextDim,
                Font = Theme.UiFontSmall,
                AutoSize = true,
                Location = new Point(0, 6),
            };
            row.Controls.Add(prefix);

            var cond = _rule.If[condIndex];

            var sensor = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat };
            Theme.StyleInput(sensor);
            for (int i = 0; i < SensorTable.Count; i++) sensor.Items.Add(SensorTable.Cn[i]);
            sensor.SelectedIndex = (int)cond.Sensor;

            var op = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat };
            Theme.StyleInput(op);
            op.Items.AddRange(new object[] { "<", "≤", ">", "≥" });
            op.SelectedIndex = (int)cond.Op;

            var val = new NumericUpDown
            {
                DecimalPlaces = 2,
                Increment = 0.05m,
                Minimum = -2m,
                Maximum = 2m,
                Value = Math.Clamp((decimal)cond.Value, -2m, 2m),
                BorderStyle = BorderStyle.FixedSingle,
                TextAlign = HorizontalAlignment.Right,
            };
            Theme.StyleInput(val);

            bool meaningful = cond.Sensor != SensorId.Always;
            op.Enabled = meaningful;
            val.Enabled = meaningful;

            var del = MiniButton("×", 0, "删除这个条件");
            del.ForeColor = Theme.Danger;

            sensor.SelectedIndexChanged += (_, _) =>
            {
                var s = (SensorId)sensor.SelectedIndex;
                bool m = s != SensorId.Always;
                op.Enabled = m;
                val.Enabled = m;
                // 换传感器后阈值量纲变了，给一个落在新量程中间的值，
                // 否则很容易留下一条"永远为假"的死规则，玩家还以为程序坏了。
                if (m) val.Value = Math.Clamp((decimal)SuggestValue(s), val.Minimum, val.Maximum);
                Push();
            };
            op.SelectedIndexChanged += (_, _) => Push();
            val.ValueChanged += (_, _) => Push();

            del.Click += (_, _) => _owner.RemoveCondition(_index, condIndex);

            row.Controls.Add(sensor);
            row.Controls.Add(op);
            row.Controls.Add(val);
            row.Controls.Add(del);
            row.Tag = new object[] { prefix, sensor, op, val, del };

            // 必须推进 y，否则所有行会叠在同一个位置上、被卡片高度裁掉 —— 表现就是
            // 卡片里只剩下"规则 N"标题，条件和动作全都不见了。
            y += RowH;
        }

        private static float SuggestValue(SensorId s)
        {
            if (s == SensorId.Always) return 0f;
            var (min, max) = SensorTable.Range[(int)s];
            return (min + max) * 0.5f;
        }

        private void AddActionRow(int actIndex, ref int y)
        {
            var row = new Panel { BackColor = Theme.Panel, Location = new Point(8, y), Height = RowH };
            Controls.Add(row);
            _rows.Add(row);

            var prefix = new Label
            {
                Text = actIndex == 0 ? "那么" : "并且",
                ForeColor = Theme.TextDim,
                Font = Theme.UiFontSmall,
                AutoSize = true,
                Location = new Point(0, 6),
            };
            row.Controls.Add(prefix);

            var spec = _rule.Then[actIndex];

            var action = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat };
            Theme.StyleInput(action);
            foreach (var a in ActionTable.Cn) action.Items.Add(a);
            action.SelectedIndex = (int)spec.Action;

            var aim = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat };
            Theme.StyleInput(aim);
            foreach (var a in AimTable.Cn) aim.Items.Add(a);
            aim.SelectedIndex = (int)spec.Aim;

            var organ = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat };
            Theme.StyleInput(organ);
            foreach (var o in OrganTable.Cn) organ.Items.Add(o);
            organ.SelectedIndex = Math.Clamp(spec.Organ, 0, OrganTable.Count - 1);

            var param = new NumericUpDown
            {
                DecimalPlaces = 2,
                Increment = 0.1m,
                Minimum = 0m,
                Maximum = 2m,
                Value = Math.Clamp((decimal)spec.Param, 0m, 2m),
                BorderStyle = BorderStyle.FixedSingle,
                TextAlign = HorizontalAlignment.Right,
            };
            Theme.StyleInput(param);

            var del = MiniButton("×", 0, "删除这个动作");
            del.ForeColor = Theme.Danger;

            void SyncVisibility()
            {
                var a = (ActionId)action.SelectedIndex;
                aim.Visible = ActionTable.NeedsAim(a);
                organ.Visible = ActionTable.NeedsOrgan(a);
                param.Visible = ActionTable.NeedsParam(a);
            }

            action.SelectedIndexChanged += (_, _) => { SyncVisibility(); Push(); };
            aim.SelectedIndexChanged += (_, _) => Push();
            organ.SelectedIndexChanged += (_, _) => Push();
            param.ValueChanged += (_, _) => Push();
            del.Click += (_, _) => _owner.RemoveAction(_index, actIndex);

            SyncVisibility();

            row.Controls.Add(action);
            row.Controls.Add(aim);
            row.Controls.Add(organ);
            row.Controls.Add(param);
            row.Controls.Add(del);
            row.Tag = new object[] { prefix, action, aim, organ, param, del };
            y += RowH;
        }

        private void AddSmallRow(string text, ref int y, Action onClick)
        {
            var row = new Panel { BackColor = Theme.Panel, Location = new Point(8, y), Height = 24 };
            Controls.Add(row);
            _rows.Add(row);

            var b = new Button
            {
                Text = text,
                Width = 78,
                Height = 22,
                FlatStyle = FlatStyle.Flat,
                BackColor = Theme.PanelAlt,
                ForeColor = Theme.TextDim,
                Font = Theme.UiFontSmall,
                Cursor = Cursors.Hand,
            };
            b.FlatAppearance.BorderSize = 0;
            b.Location = new Point(38, 0);
            b.Click += (_, _) => onClick();
            row.Controls.Add(b);
            y += 24;
            row.Tag = new object[] { b };
        }

        /// <summary>把所有条件/动作的当前控件值写回基因组。</summary>
        private void Push()
        {
            int ci = 0, ai = 0;
            foreach (var row in _rows)
            {
                if (row.Tag is not object[] tags) continue;
                if (tags.Length == 5 && tags[1] is ComboBox s && s.Items.Count == SensorTable.Count)
                {
                    _owner.UpdateCondition(_index, ci,
                        (SensorId)s.SelectedIndex,
                        (CompareOp)((ComboBox)tags[2]).SelectedIndex,
                        (float)((NumericUpDown)tags[3]).Value);
                    ci++;
                }
                else if (tags.Length == 6 && tags[1] is ComboBox a && a.Items.Count == ActionTable.Count)
                {
                    _owner.UpdateAction(_index, ai,
                        (ActionId)a.SelectedIndex,
                        (AimId)((ComboBox)tags[2]).SelectedIndex,
                        ((ComboBox)tags[3]).SelectedIndex,
                        (float)((NumericUpDown)tags[4]).Value);
                    ai++;
                }
            }
        }

        /// <summary>按卡片宽度重新摆放每一行的控件。</summary>
        public void LayoutRows(int cardWidth)
        {
            int w = cardWidth - 20;
            int bx = w - 24;

            // 表头按钮靠右排列
            int hx = w - 4;
            for (int i = _headerButtons.Length - 1; i >= 0; i--)
            {
                var b = _headerButtons[i];
                hx -= b.Width + 4;
                b.Location = new Point(Math.Max(70, hx), 5);
            }

            foreach (var row in _rows)
            {
                if (row.Tag is not object[] tags) continue;

                // 行容器自己也得有宽度！Panel 默认宽度只有 200px，
                // 只设置子控件位置而不设置行宽的话，x 超过 200 的控件
                // （算子、数值、删除按钮）会被行容器整个裁掉 —— 表现为"只有第一个下拉框"。
                row.SetBounds(8, row.Top, w, row.Height);

                if (tags.Length == 5)
                {
                    var prefix = (Label)tags[0];
                    var sensor = (ComboBox)tags[1];
                    var op = (ComboBox)tags[2];
                    var val = (NumericUpDown)tags[3];
                    var del = (Button)tags[4];

                    prefix.Location = new Point(0, 5);
                    int x = prefix.PreferredWidth + 4;
                    int avail = bx - x - 4;
                    sensor.SetBounds(x, 2, (int)(avail * 0.48f), 23);
                    op.SetBounds(x + (int)(avail * 0.48f) + 3, 2, (int)(avail * 0.16f), 23);
                    val.SetBounds(x + (int)(avail * 0.48f) + (int)(avail * 0.16f) + 6, 2,
                                  avail - (int)(avail * 0.48f) - (int)(avail * 0.16f) - 6, 23);
                    del.Location = new Point(bx, 3);
                }
                else if (tags.Length == 6)
                {
                    var prefix = (Label)tags[0];
                    var action = (ComboBox)tags[1];
                    var aim = (ComboBox)tags[2];
                    var organ = (ComboBox)tags[3];
                    var param = (NumericUpDown)tags[4];
                    var del = (Button)tags[5];

                    prefix.Location = new Point(0, 5);
                    int x = prefix.PreferredWidth + 4;
                    int avail = bx - x - 4;

                    int wAction = (int)(avail * 0.34f);
                    int wAim = (int)(avail * 0.30f);
                    int wOrgan = (int)(avail * 0.20f);
                    int wParam = avail - wAction - wAim - wOrgan - 9;
                    if (wParam < 44) wParam = 44;

                    action.SetBounds(x, 2, wAction, 23);
                    int cx = x + wAction + 3;
                    aim.SetBounds(cx, 2, wAim, 23);
                    cx += wAim + 3;
                    organ.SetBounds(cx, 2, wOrgan, 23);
                    cx += wOrgan + 3;
                    param.SetBounds(cx, 2, wParam, 23);
                    del.Location = new Point(bx, 3);
                }
                else if (tags.Length == 1 && tags[0] is Button sb)
                {
                    sb.Location = new Point(38, 0);
                }
            }
        }
    }
}
