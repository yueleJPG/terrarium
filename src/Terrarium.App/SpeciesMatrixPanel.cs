using System.Drawing.Drawing2D;
using Terrarium.Core;

namespace Terrarium.App;

/// <summary>
/// 物种差异矩阵。
///
/// 把"物种之间到底有没有区分"从一个主观感受变成一张可以看的图：
/// 每格是两种生物在**基因+行为指纹**上的距离，越亮 = 差异越大。
///
/// 为什么要单独做这个可视化：玩家反馈过"物种没有区分"，但当时的物种只是随机标签，
/// 看名字看不出任何东西。现在有了真实的指纹距离，一张热力图就能一眼判断：
/// 如果整张图都很暗，说明这些"物种"其实只是颜色不同的同一批生物，
/// 不管名字差多远都没意义。
/// </summary>
internal sealed class SpeciesMatrixPanel : Control
{
    private int[] _ids = Array.Empty<int>();
    private string[] _names = Array.Empty<string>();
    private float[] _matrix = Array.Empty<float>();
    private int _n;

    private readonly Font _titleFont = new("Microsoft YaHei UI", 9f, FontStyle.Bold);
    private readonly Font _labelFont = new("Microsoft YaHei UI", 7.5f);
    private readonly Font _smallFont = new("Microsoft YaHei UI", 7f);
    private readonly SolidBrush _textBrush = new(Theme.Text);
    private readonly SolidBrush _dimBrush = new(Theme.TextDim);
    private readonly StringFormat _rightAlign = new() { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center };

    public SpeciesMatrixPanel()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Panel;
    }

    public void SetMatrix(int[] ids, string[] names, float[] matrix, int n)
    {
        _ids = ids;
        _names = names;
        _matrix = matrix;
        _n = n;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Panel);
        g.SmoothingMode = SmoothingMode.None;

        g.DrawString("物种差异矩阵（指纹距离，越亮差异越大）", _titleFont, _textBrush, 8, 6);

        if (_n < 2)
        {
            g.DrawString("存活物种少于 2 个，还没有可比的对象。", _smallFont, _dimBrush, 8, 28);
            g.DrawString("把「物种分化倍数」调小，或者用「地形」页加一道山脉制造地理隔离。",
                _smallFont, _dimBrush, 8, 44);
            return;
        }

        // 行标签宽 + 列头高
        int labelW = 54;
        int top = 34;
        int avail = Math.Min(Width - labelW - 12, Height - top - 34);
        int cell = Math.Max(8, avail / _n);
        int ox = labelW + 6, oy = top + cell;

        // 归一化基准：用最大距离，这样对比度始终拉满
        float maxD = 1e-4f;
        for (int i = 0; i < _n; i++)
            for (int j = 0; j < _n; j++)
                if (i != j && _matrix[i * _n + j] > maxD) maxD = _matrix[i * _n + j];

        for (int i = 0; i < _n; i++)
        {
            // 行标签
            g.DrawString($"#{_ids[i]}", _smallFont, _dimBrush,
                new RectangleF(2, oy + i * cell, labelW, cell), _rightAlign);

            for (int j = 0; j < _n; j++)
            {
                var rect = new Rectangle(ox + j * cell, oy + i * cell, cell - 1, cell - 1);
                if (i == j)
                {
                    using var diag = new SolidBrush(Color.FromArgb(26, 30, 36));
                    g.FillRectangle(diag, rect);
                    continue;
                }

                float t = Math.Clamp(_matrix[i * _n + j] / maxD, 0f, 1f);
                // 暗蓝 → 青 → 黄 → 橙，差异越大越暖
                Color c = t < 0.4f
                    ? Theme.Blend(Color.FromArgb(24, 32, 48), Color.FromArgb(52, 130, 150), t / 0.4f)
                    : Theme.Blend(Color.FromArgb(52, 130, 150), Color.FromArgb(240, 170, 70), (t - 0.4f) / 0.6f);
                using var b = new SolidBrush(c);
                g.FillRectangle(b, rect);
            }
        }

        // 列头
        for (int j = 0; j < _n; j++)
            g.DrawString($"{_ids[j]}", _smallFont, _dimBrush, ox + j * cell + 1, top + 6);

        // 图例
        int ly = oy + _n * cell + 8;
        if (ly + 20 < Height)
        {
            g.DrawString("相似", _smallFont, _dimBrush, labelW, ly);
            int bx = labelW + 26;
            for (int k = 0; k < 24; k++)
            {
                float t = k / 23f;
                Color c = t < 0.4f
                    ? Theme.Blend(Color.FromArgb(24, 32, 48), Color.FromArgb(52, 130, 150), t / 0.4f)
                    : Theme.Blend(Color.FromArgb(52, 130, 150), Color.FromArgb(240, 170, 70), (t - 0.4f) / 0.6f);
                using var b = new SolidBrush(c);
                g.FillRectangle(b, bx + k * 5, ly + 1, 5, 9);
            }
            g.DrawString("差异大", _smallFont, _dimBrush, bx + 122, ly);
            g.DrawString($"最大 {maxD:F3}", _smallFont, _dimBrush, bx + 162, ly);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _titleFont.Dispose();
            _labelFont.Dispose();
            _smallFont.Dispose();
            _textBrush.Dispose();
            _dimBrush.Dispose();
            _rightAlign.Dispose();
        }
        base.Dispose(disposing);
    }
}
