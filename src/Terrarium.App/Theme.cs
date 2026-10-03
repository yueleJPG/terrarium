using System.Drawing.Drawing2D;

namespace Terrarium.App;

/// <summary>统一的深色配色与色彩工具。整个 UI 只从这里取颜色，改风格只改这一个文件。</summary>
internal static class Theme
{
    public static readonly Color Background = Color.FromArgb(16, 18, 22);
    public static readonly Color Panel = Color.FromArgb(26, 29, 35);
    public static readonly Color PanelAlt = Color.FromArgb(33, 37, 44);
    public static readonly Color Border = Color.FromArgb(52, 58, 68);
    public static readonly Color Text = Color.FromArgb(226, 231, 238);
    public static readonly Color TextDim = Color.FromArgb(140, 150, 165);
    public static readonly Color Accent = Color.FromArgb(96, 190, 255);
    public static readonly Color AccentWarm = Color.FromArgb(255, 176, 84);
    public static readonly Color Danger = Color.FromArgb(240, 92, 92);
    public static readonly Color Good = Color.FromArgb(108, 224, 150);

    // --- 生态箱地形 ---
    public static readonly Color LandPoor = Color.FromArgb(40, 45, 40);
    public static readonly Color LandRich = Color.FromArgb(74, 88, 52);
    public static readonly Color WaterDeep = Color.FromArgb(26, 54, 92);
    public static readonly Color WaterShallow = Color.FromArgb(42, 96, 142);
    public static readonly Color Food = Color.FromArgb(126, 226, 108);

    /// <summary>
    /// 地形配色。八种地形必须在画面上**一眼可分** ——
    /// 否则玩家看不出哪里是屏障、哪里是栖息地，地理结构就白做了。
    /// 色相刻意拉开：草地=黄绿、沃土=亮绿、灌木=墨绿、浅水=青蓝、深水=深蓝、
    /// 沼泽=暗橄榄、岩石=中性灰、荒漠=沙黄。
    /// </summary>
    public static Color TerrainColor(Terrarium.Core.TerrainType t, float fertility)
    {
        float f = Math.Clamp(fertility, 0f, 1.2f);
        switch (t)
        {
            case Terrarium.Core.TerrainType.Fertile:
                return Blend(Color.FromArgb(60, 82, 38), Color.FromArgb(126, 186, 74), f);
            case Terrarium.Core.TerrainType.Bush:
                return Blend(Color.FromArgb(22, 42, 28), Color.FromArgb(44, 96, 52), f);
            case Terrarium.Core.TerrainType.ShallowWater:
                return Blend(Color.FromArgb(56, 106, 142), Color.FromArgb(92, 158, 190), f);
            case Terrarium.Core.TerrainType.DeepWater:
                return Blend(Color.FromArgb(20, 48, 92), Color.FromArgb(34, 78, 136), f);
            case Terrarium.Core.TerrainType.Swamp:
                return Blend(Color.FromArgb(38, 46, 32), Color.FromArgb(74, 88, 54), f);
            case Terrarium.Core.TerrainType.Rock:
                return Color.FromArgb(104, 108, 116);
            case Terrarium.Core.TerrainType.Desert:
                return Blend(Color.FromArgb(118, 104, 76), Color.FromArgb(158, 142, 104), f);
            default: // Grass
                return Blend(Color.FromArgb(38, 50, 34), Color.FromArgb(78, 108, 56), f);
        }
    }

    public static readonly Font UiFont = new("Microsoft YaHei UI", 9f);
    public static readonly Font UiFontSmall = new("Microsoft YaHei UI", 8.25f);
    public static readonly Font UiFontBold = new("Microsoft YaHei UI", 9f, FontStyle.Bold);
    public static readonly Font MonoFont = new("Consolas", 9f);
    public static readonly Font TitleFont = new("Microsoft YaHei UI", 10.5f, FontStyle.Bold);

    /// <summary>HSV → RGB。hue 在 [0,1)。</summary>
    public static Color FromHue(float hue, float sat = 0.72f, float val = 0.95f)
    {
        hue = hue - MathF.Floor(hue);
        float h6 = hue * 6f;
        int i = (int)h6;
        float f = h6 - i;
        float p = val * (1f - sat);
        float q = val * (1f - sat * f);
        float t = val * (1f - sat * (1f - f));
        float r, g, b;
        switch (i % 6)
        {
            case 0: r = val; g = t; b = p; break;
            case 1: r = q; g = val; b = p; break;
            case 2: r = p; g = val; b = t; break;
            case 3: r = p; g = q; b = val; break;
            case 4: r = t; g = p; b = val; break;
            default: r = val; g = p; b = q; break;
        }
        return Color.FromArgb(255, (int)(r * 255), (int)(g * 255), (int)(b * 255));
    }

    /// <summary>按比例缩放明度，用来表达"能量高低"。</summary>
    public static Color ScaleValue(Color c, float k)
    {
        k = Math.Clamp(k, 0f, 1.4f);
        return Color.FromArgb(
            c.A,
            Math.Clamp((int)(c.R * k), 0, 255),
            Math.Clamp((int)(c.G * k), 0, 255),
            Math.Clamp((int)(c.B * k), 0, 255));
    }

    public static Color Blend(Color a, Color b, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return Color.FromArgb(
            (int)(a.A + (b.A - a.A) * t),
            (int)(a.R + (b.R - a.R) * t),
            (int)(a.G + (b.G - a.G) * t),
            (int)(a.B + (b.B - a.B) * t));
    }

    public static void StyleButton(Button b, bool primary = false)
    {
        b.FlatStyle = FlatStyle.Flat;
        b.FlatAppearance.BorderSize = 0;
        b.BackColor = primary ? Color.FromArgb(44, 96, 140) : PanelAlt;
        b.ForeColor = Text;
        b.Font = UiFont;
        b.Cursor = Cursors.Hand;
        b.Height = 28;
        b.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(56, 118, 170) : Color.FromArgb(46, 52, 62);
        b.FlatAppearance.MouseDownBackColor = primary ? Color.FromArgb(36, 82, 122) : Color.FromArgb(38, 43, 52);
    }

    public static void StyleInput(Control c)
    {
        c.BackColor = Color.FromArgb(20, 22, 27);
        c.ForeColor = Text;
        c.Font = UiFont;
    }
}
