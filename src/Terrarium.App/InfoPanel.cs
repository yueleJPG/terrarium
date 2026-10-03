using System.Drawing.Drawing2D;

namespace Terrarium.App;

/// <summary>一行键值对。Color 非空时会画一个色块，用于物种颜色标识。</summary>
internal sealed class InfoRow
{
    public string Key = "";
    public string Value = "";
    public Color? Swatch;
    public bool Header;
    public bool Dim;
}

/// <summary>一条横向条形图（器官等级 / 传感器读数都用它）。</summary>
internal sealed class InfoBar
{
    public string Label = "";
    public float Value;
    public float Max = 1f;
    public Color Color = Theme.Accent;
    public string Text = "";

    /// <summary>为 true 时这条不是数据，而是一个分组小标题。</summary>
    public bool IsHeader;

    /// <summary>超过这个比例时整条变亮，用来强调"这条规则正在触发"。</summary>
    public bool Highlight;
}

/// <summary>
/// 通用信息面板：标题 + 键值表 + 条形图列表，全部自绘。
/// 生物详情和物种详情共用它，避免写两套布局代码。
/// </summary>
internal sealed class InfoPanel : Control
{
    public string Title = "";
    public string Subtitle = "";
    public string Footer = "";
    public List<InfoRow> Rows { get; } = new();
    public List<InfoBar> Bars { get; } = new();
    public string BarsTitle = "";
    public string RowsTitle = "";

    private const int Pad = 10;

    private int _scroll;
    private int _contentH;

    public InfoPanel()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        BackColor = Theme.Panel;
    }

    // 详情面板的内容（状态 + 10 个器官 + 派生属性 + 19 个传感器 + 当前规则）
    // 必然高过面板本身，所以必须能滚。鼠标滚轮只会送给有焦点的控件，
    // 而这里没有任何输入框，所以鼠标移入就取焦点是安全且符合直觉的。
    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        Focus();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        int max = Math.Max(0, _contentH - Height);
        if (max <= 0) return;
        _scroll = Math.Clamp(_scroll - e.Delta, 0, max);
        Invalidate();
    }

    /// <summary>算出内容总高度，用来决定能不能滚、滚到哪。</summary>
    private int MeasureContent(Graphics g, Font smallFont)
    {
        int h = Pad;
        if (!string.IsNullOrEmpty(Title)) h += 22;
        if (!string.IsNullOrEmpty(Subtitle))
            h += 15 * WrapByWidth(g, Subtitle, smallFont, Width - Pad * 2).Count + 3;

        if (Bars.Count > 0)
        {
            foreach (var b in Bars) h += b.IsHeader ? 22 : 17;
            h += 6;
        }
        if (Rows.Count > 0)
        {
            foreach (var r in Rows) h += r.Header ? 24 : 16;
        }
        return h + 16;
    }

    public void Begin()
    {
        Rows.Clear();
        Bars.Clear();
    }

    public void Add(string key, string value, Color? swatch = null, bool dim = false)
        => Rows.Add(new InfoRow { Key = key, Value = value, Swatch = swatch, Dim = dim });

    public void AddHeader(string text) => Rows.Add(new InfoRow { Key = text, Header = true });

    public void AddBar(string label, float value, float max, Color color, string text = "", bool highlight = false)
        => Bars.Add(new InfoBar { Label = label, Value = value, Max = max, Color = color, Text = text, Highlight = highlight });

    /// <summary>
    /// 在条形图里插入一个分组小标题。
    /// 刻意不用单个 BarsTitle 字段 —— 一个面板里通常有两组条形图（状态一组、器官一组），
    /// 用一个字段装标题的话后一组会把前一组的标题覆盖掉。
    /// </summary>
    public void AddBarHeader(string text)
        => Bars.Add(new InfoBar { Label = text, IsHeader = true });

    public void Commit()
    {
        // 切换到另一只生物时把滚动位置归零，否则会停在上一只的中间位置，很困惑。
        // 不能放在 Begin() 里 —— 那个每帧都会被调用，会导致完全滚不动。
        if (_scrollKey != Title)
        {
            _scroll = 0;
            _scrollKey = Title;
        }
        Invalidate();
    }

    private string _scrollKey = "";

    /// <summary>按可用宽度把文本硬折行（中文没有空格，只能逐字符量宽）。</summary>
    private static List<string> WrapByWidth(Graphics g, string text, Font font, int maxWidth)
    {
        var lines = new List<string>();
        if (maxWidth <= 20) { lines.Add(text); return lines; }

        var sb = new System.Text.StringBuilder();
        foreach (char ch in text)
        {
            if (ch == '\n')
            {
                lines.Add(sb.ToString());
                sb.Clear();
                continue;
            }
            sb.Append(ch);
            if (g.MeasureString(sb.ToString(), font).Width > maxWidth)
            {
                // 回退一个字符，把它挪到下一行
                sb.Length--;
                lines.Add(sb.ToString());
                sb.Clear();
                sb.Append(ch);
            }
            if (lines.Count > 8) break;
        }
        if (sb.Length > 0) lines.Add(sb.ToString());
        return lines;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Panel);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        using var titleFont = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold);
        using var font = new Font("Microsoft YaHei UI", 8.6f);
        using var smallFont = new Font("Microsoft YaHei UI", 8f);
        using var mono = new Font("Consolas", 8.6f);
        using var textBrush = new SolidBrush(Theme.Text);
        using var dimBrush = new SolidBrush(Theme.TextDim);
        using var accentBrush = new SolidBrush(Theme.Accent);

        _contentH = MeasureContent(g, smallFont);
        int maxScroll = Math.Max(0, _contentH - Height);
        if (_scroll > maxScroll) _scroll = maxScroll;

        // 内容整体上移 _scroll；起始为负值，GDI 会自动裁掉视口外的部分
        int y = Pad - _scroll;
        if (!string.IsNullOrEmpty(Title))
        {
            g.DrawString(Title, titleFont, textBrush, Pad, y);
            y += 22;
        }
        if (!string.IsNullOrEmpty(Subtitle))
        {
            // 中文没有空格，不能按单词折行 —— 只能按字符宽度硬折，
            // 否则长句会直接画到面板外面去。
            foreach (string line in WrapByWidth(g, Subtitle, smallFont, Width - Pad * 2))
            {
                g.DrawString(line, smallFont, dimBrush, Pad, y);
                y += 15;
                if (y > Height - 20) break;
            }
            y += 3;
        }

        int left = Pad;
        int right = Width - Pad - 16;   // 给右侧滚动条留位置
        int keyW = Math.Min(112, Math.Max(70, Width / 3));

        // ---- 条形图区 ----
        if (Bars.Count > 0)
        {
            if (!string.IsNullOrEmpty(BarsTitle))
            {
                g.DrawString(BarsTitle, smallFont, dimBrush, left, y);
                y += 16;
            }
            foreach (var b in Bars)
            {
                if (y > Height - 12) break;

                if (b.IsHeader)
                {
                    y += 4;
                    using var hp = new Pen(Color.FromArgb(44, Theme.Border));
                    g.DrawLine(hp, left, y + 8, right, y + 8);
                    g.DrawString(b.Label, smallFont, dimBrush, left, y - 3);
                    y += 18;
                    continue;
                }

                g.DrawString(b.Label, font, b.Highlight ? accentBrush : dimBrush, left, y);

                int barX = left + keyW;
                int barW = Math.Max(20, right - barX - 46);
                var rect = new Rectangle(barX, y + 1, barW, 11);

                using (var bg = new SolidBrush(Color.FromArgb(30, 34, 40)))
                    g.FillRectangle(bg, rect);

                float frac = b.Max <= 0 ? 0 : Math.Clamp(b.Value / b.Max, 0f, 1f);
                var fill = new Rectangle(rect.X, rect.Y, (int)(rect.Width * frac), rect.Height);
                using (var fb = new SolidBrush(b.Highlight ? Theme.AccentWarm : b.Color))
                    g.FillRectangle(fb, fill);

                if (b.Highlight)
                {
                    using var hp = new Pen(Theme.AccentWarm, 1.4f);
                    g.DrawRectangle(hp, rect);
                }

                string txt = string.IsNullOrEmpty(b.Text) ? b.Value.ToString("0.##") : b.Text;
                g.DrawString(txt, mono, textBrush, barX + barW + 6, y);
                y += 17;
            }
            y += 6;
        }

        // ---- 键值区 ----
        if (Rows.Count > 0)
        {
            if (!string.IsNullOrEmpty(RowsTitle))
            {
                g.DrawString(RowsTitle, smallFont, dimBrush, left, y);
                y += 16;
            }
            foreach (var r in Rows)
            {
                if (y > Height - 12) break;

                if (r.Header)
                {
                    y += 4;
                    using var hp = new Pen(Color.FromArgb(50, Theme.Border));
                    g.DrawLine(hp, left, y + 8, right, y + 8);
                    g.DrawString(r.Key, font, accentBrush, left, y - 2);
                    y += 20;
                    continue;
                }

                int x = left;
                if (r.Swatch.HasValue)
                {
                    using var sb = new SolidBrush(r.Swatch.Value);
                    g.FillRectangle(sb, x, y + 2, 9, 9);
                    x += 13;
                }

                float keyTextW = g.MeasureString(r.Key, font).Width;
                float valW = g.MeasureString(r.Value, mono).Width;
                float availForValue = right - (x + keyTextW + 8);

                if (valW <= availForValue || availForValue < 40)
                {
                    // 常规情形：左边键、右边值，两栏对齐
                    g.DrawString(r.Key, font, r.Dim ? dimBrush : textBrush, x, y);
                    g.DrawString(r.Value, mono, r.Dim ? dimBrush : textBrush, right - valW, y);
                    y += 16;
                }
                else
                {
                    // 值太长（比如整条规则的自然语言描述）：键单独一行，值折行画在下面，
                    // 否则两者会直接叠在一起看不清。
                    g.DrawString(r.Key, font, dimBrush, x, y);
                    y += 15;
                    foreach (string line in WrapByWidth(g, r.Value, mono, right - x - 10))
                    {
                        g.DrawString(line, mono, r.Dim ? dimBrush : textBrush, x + 10, y);
                        y += 14;
                        if (y > Height - 12) break;
                    }
                }
            }
        }

        // 页脚只在内容放得下时画。放不下时它会被溢出的内容压住，反而变成一团糊字。
        if (!string.IsNullOrEmpty(Footer) && Height > 24 && maxScroll <= 0)
        {
            var sz = g.MeasureString(Footer, smallFont);
            g.DrawString(Footer, smallFont, dimBrush, Pad, Height - sz.Height - 6);
        }

        // 滚动条：内容放不下时在右边缘画一条，让玩家知道下面还有东西
        if (maxScroll > 0)
        {
            int barX = Width - 6;
            using (var track = new SolidBrush(Color.FromArgb(26, 29, 35)))
                g.FillRectangle(track, barX, 0, 5, Height);

            float frac = Height / (float)_contentH;
            int thumbH = Math.Max(24, (int)(Height * frac));
            int thumbY = (int)((Height - thumbH) * (_scroll / (float)maxScroll));
            using var thumb = new SolidBrush(Color.FromArgb(90, Theme.Border));
            g.FillRectangle(thumb, barX, thumbY, 5, thumbH);
        }
    }
}
