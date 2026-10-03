using System.Drawing.Drawing2D;
using Terrarium.Core;

namespace Terrarium.App;

/// <summary>一条曲线：取值函数 + 颜色。上下界可以固定，也可以自动。</summary>
internal sealed class ChartSeries
{
    public required string Name;
    public required Func<WorldStats, float> Value;
    public Color Color = Theme.Accent;
    public float FixedMin = float.NaN;
    public float FixedMax = float.NaN;
    public bool Normalize;
}

/// <summary>
/// 极简折线图。刻意不引入任何图表库 —— 整个项目零第三方依赖，
/// 而这里要画的东西（几条随时间变化的曲线）自己画反而更可控。
/// </summary>
internal sealed class ChartPanel : Control
{
    public List<ChartSeries> Series { get; } = new();
    private List<WorldStats> _data = new();

    public string Title = "种群曲线";
    public string EmptyHint = "开始模拟后这里会出现曲线";

    public ChartPanel()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Panel;
    }

    public void SetData(List<WorldStats> data)
    {
        _data = data;
        Invalidate();
    }

    private int LegendHeight => Math.Min(Series.Count, 6) * 14;

    // 绘图区要避开标题和图例，否则图例文字会直接压在标题上
    private Rectangle PlotArea => new(
        52,
        26 + LegendHeight,
        Math.Max(10, Width - 66),
        Math.Max(10, Height - (26 + LegendHeight) - 26));

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Panel);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        using var titleFont = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold);
        using var smallFont = new Font("Microsoft YaHei UI", 7.5f);
        using var textBrush = new SolidBrush(Theme.Text);
        using var dimBrush = new SolidBrush(Theme.TextDim);

        g.DrawString(Title, titleFont, textBrush, 8, 6);

        Rectangle plot = PlotArea;
        using (var gridPen = new Pen(Color.FromArgb(38, Theme.Border)))
        {
            for (int i = 0; i <= 4; i++)
            {
                float y = plot.Top + plot.Height * i / 4f;
                g.DrawLine(gridPen, plot.Left, y, plot.Right, y);
            }
            g.DrawRectangle(gridPen, plot);
        }

        if (_data.Count < 2 || Series.Count == 0)
        {
            g.DrawString(EmptyHint, smallFont, dimBrush, plot.Left + 8, plot.Top + 8);
            return;
        }

        // X 轴按 tick 线性映射
        int t0 = _data[0].Tick;
        int t1 = _data[^1].Tick;
        if (t1 <= t0) t1 = t0 + 1;

        int legendY = 24;
        for (int si = 0; si < Series.Count && si < 6; si++)
        {
            var s = Series[si];
            float min = s.FixedMin, max = s.FixedMax;
            if (s.Normalize || float.IsNaN(min) || float.IsNaN(max))
            {
                min = float.MaxValue;
                max = float.MinValue;
                foreach (var d in _data)
                {
                    float v = s.Value(d);
                    if (v < min) min = v;
                    if (v > max) max = v;
                }
                if (max - min < 1e-6f) max = min + 1f;
                float pad = (max - min) * 0.08f;
                min -= pad;
                max += pad;
                if (min > 0 && s.Normalize) min = 0;
            }

            var pts = new PointF[_data.Count];
            for (int i = 0; i < _data.Count; i++)
            {
                float v = s.Value(_data[i]);
                float nx = (float)(_data[i].Tick - t0) / (t1 - t0);
                float ny = (v - min) / (max - min);
                pts[i] = new PointF(plot.Left + nx * plot.Width, plot.Bottom - ny * plot.Height);
            }

            using var pen = new Pen(s.Color, 1.6f) { LineJoin = LineJoin.Round };
            g.DrawLines(pen, pts);

            var last = pts[^1];
            using var dot = new SolidBrush(s.Color);
            g.FillEllipse(dot, last.X - 2.5f, last.Y - 2.5f, 5f, 5f);

            float cur = s.Value(_data[^1]);
            string label = $"{s.Name} {cur:0.##}";
            g.FillRectangle(dot, 10, legendY + 3, 8, 8);
            g.DrawString(label, smallFont, textBrush, 22, legendY);
            legendY += 14;
        }

        // X 轴刻度
        g.DrawString(t0.ToString(), smallFont, dimBrush, plot.Left, plot.Bottom + 4);
        string right = t1.ToString();
        var rsz = g.MeasureString(right, smallFont);
        g.DrawString(right, smallFont, dimBrush, plot.Right - rsz.Width, plot.Bottom + 4);
        var mid = g.MeasureString("tick", smallFont);
        g.DrawString("tick", smallFont, dimBrush, plot.Left + plot.Width / 2f - mid.Width / 2f, plot.Bottom + 4);
    }
}
