using AndroidColor = Android.Graphics.Color;
using CoreTerrain = Terrarium.Core.TerrainType;

namespace Terrarium.Android;

/// <summary>
/// 配色表（Android 版）。
///
/// 这是从 WinForms 那边的 Theme 移植过来的，但**没有**直接把 System.Drawing.Color
/// 的逻辑照搬 —— Android 的 Color 是打包成 int 的，混合要用通道运算。
/// 数值全部保持一致，这样手机上和桌面上的画面是同一张脸。
/// </summary>
internal static class Palette
{
    public static readonly AndroidColor Background = Rgb(16, 18, 22);
    public static readonly AndroidColor Panel = Rgb(26, 29, 35);
    public static readonly AndroidColor Border = Rgb(52, 58, 68);
    public static readonly AndroidColor Text = Rgb(226, 231, 238);
    public static readonly AndroidColor TextDim = Rgb(140, 150, 165);
    public static readonly AndroidColor Accent = Rgb(96, 190, 255);
    public static readonly AndroidColor Danger = Rgb(240, 92, 92);
    public static readonly AndroidColor Good = Rgb(108, 224, 150);
    public static readonly AndroidColor Food = Rgb(126, 226, 108);

    public static AndroidColor Rgb(int r, int g, int b)
        => AndroidColor.Rgb(Clamp255(r), Clamp255(g), Clamp255(b));

    /// <summary>把 [0,1] 的色相展开成饱和明亮的颜色（物种配色用）。</summary>
    public static AndroidColor FromHue(float hue, float sat = 0.72f, float val = 0.95f)
    {
        hue -= MathF.Floor(hue);
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
        return AndroidColor.Rgb((int)(r * 255), (int)(g * 255), (int)(b * 255));
    }

    public static AndroidColor Blend(AndroidColor a, AndroidColor b, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return AndroidColor.Rgb(
            (int)(a.R + (b.R - a.R) * t),
            (int)(a.G + (b.G - a.G) * t),
            (int)(a.B + (b.B - a.B) * t));
    }

    public static AndroidColor Scale(AndroidColor c, float k)
    {
        k = Math.Clamp(k, 0f, 1.4f);
        return AndroidColor.Rgb((int)(c.R * k), (int)(c.G * k), (int)(c.B * k));
    }

    /// <summary>
    /// 地形配色。八种地形必须一眼可分 —— 否则玩家看不出哪里是屏障、哪里是栖息地。
    /// 数值与桌面版逐项一致。
    /// </summary>
    public static AndroidColor Terrain(CoreTerrain t, float fertility)
    {
        float f = Math.Clamp(fertility, 0f, 1.2f);
        switch (t)
        {
            case CoreTerrain.Fertile:
                return Blend(Rgb(60, 82, 38), Rgb(126, 186, 74), f);
            case CoreTerrain.Bush:
                return Blend(Rgb(22, 42, 28), Rgb(44, 96, 52), f);
            case CoreTerrain.ShallowWater:
                return Blend(Rgb(56, 106, 142), Rgb(92, 158, 190), f);
            case CoreTerrain.DeepWater:
                return Blend(Rgb(20, 48, 92), Rgb(34, 78, 136), f);
            case CoreTerrain.Swamp:
                return Blend(Rgb(38, 46, 32), Rgb(74, 88, 54), f);
            case CoreTerrain.Rock:
                return Rgb(104, 108, 116);
            case CoreTerrain.Desert:
                return Blend(Rgb(118, 104, 76), Rgb(158, 142, 104), f);
            default: // Grass
                return Blend(Rgb(38, 50, 34), Rgb(78, 108, 56), f);
        }
    }

    private static int Clamp255(int v) => v < 0 ? 0 : (v > 255 ? 255 : v);
}
