using Android.Content;
using Android.Graphics;
using Android.Views;
using Terrarium.Core;
using AColor = Android.Graphics.Color;
using APaint = Android.Graphics.Paint;
// ImplicitUsings 会带进 System.IO，于是 Path 同时是 System.IO.Path 和 Android.Graphics.Path，
// 编译器判为歧义。取个显式别名，别去关隐式 using（那会连带弄丢一堆别的类型）。
using APath = Android.Graphics.Path;

namespace Terrarium.Android;

/// <summary>
/// 生态箱画布（Android 版）。
///
/// 这是整个移植里唯一真正"重写"的部分 —— 桌面版那 900 行用的是 GDI+
/// （Graphics / Pen / Brush / PointF），Android 这边是 Canvas / Paint / Path。
/// 但**算法全部照搬**：世界→屏幕变换、按器官构成生成身体多边形、
/// 三级 LOD、黄金角物种配色、地形位图 + 最近邻放大。
///
/// 手机上多了三件桌面版没有的事：
///   1. 双指缩放与单指拖动（桌面是滚轮 + 拖拽）
///   2. 触摸热区要放大 —— 手指比鼠标粗得多，点选判定半径必须放宽
///   3. 掉帧比模拟慢更难忍，所以后台线程降优先级、渲染走硬件加速
/// </summary>
internal sealed class TerrariumView : View
{
    private readonly SimRunner _runner;

    // ---- 相机 ----
    private float _zoom = 0.9f;
    private float _camX = 1600f, _camY = 1000f;
    private bool _fit = true;
    private float _worldW = 3200f, _worldH = 2000f;

    // ---- 地形位图缓存 ----
    private Bitmap? _tiles;
    private int[] _tileBuf = Array.Empty<int>();

    // ---- 复用的绘制对象：每帧 new 会在 GC 上抖，手机上尤其明显 ----
    private readonly APaint _fill = new() { AntiAlias = true };
    private readonly APaint _stroke = new() { AntiAlias = true, StrokeCap = APaint.Cap.Round };
    private readonly APaint _text = new() { AntiAlias = true };
    private readonly APaint _bmp = new() { FilterBitmap = false, AntiAlias = false };
    private readonly APath _path = new();
    private readonly Rect _srcRect = new();
    private readonly RectF _dstRect = new();
    private readonly RectF _oval = new();

    private float[] _polyX = new float[32];
    private float[] _polyY = new float[32];

    // ---- 触摸 ----
    private readonly ScaleGestureDetector _scaleDetector;
    private readonly GestureDetector _gestureDetector;

    // ---- 选中 ----
    public int SelectedId { get; set; } = -1;
    private SimSnapshot? _snap;

    public TerrariumView(Context ctx, SimRunner runner) : base(ctx)
    {
        _runner = runner;
        SetBackgroundColor(Palette.Background);
        Focusable = true;

        _scaleDetector = new ScaleGestureDetector(ctx, new ScaleListener(this));
        _gestureDetector = new GestureDetector(ctx, new GestureListener(this));
    }

    // ==================================================================
    // 触摸
    // ==================================================================

    public override bool OnTouchEvent(MotionEvent? e)
    {
        if (e is null) return false;

        _scaleDetector.OnTouchEvent(e);
        _gestureDetector.OnTouchEvent(e);

        // 双指按下时不要再让单指拖动生效，否则缩放会带着画面乱跑
        if (e.PointerCount == 1 && e.ActionMasked == MotionEventActions.Up && !_scaleDetector.IsInProgress)
        {
            float dx = Math.Abs(e.GetX() - _downX);
            float dy = Math.Abs(e.GetY() - _downY);
            if (dx < 14 && dy < 14) PickAt(e.GetX(), e.GetY());   // 手指的热区比鼠标宽得多
        }
        return true;
    }

    private float _downX, _downY;

    private sealed class ScaleListener : ScaleGestureDetector.SimpleOnScaleGestureListener
    {
        private readonly TerrariumView _v;
        public ScaleListener(TerrariumView v) => _v = v;

        public override bool OnScale(ScaleGestureDetector d)
        {
            _v.ApplyZoom(d.ScaleFactor, d.FocusX, d.FocusY);
            return true;
        }
    }

    private sealed class GestureListener : GestureDetector.SimpleOnGestureListener
    {
        private readonly TerrariumView _v;
        public GestureListener(TerrariumView v) => _v = v;

        public override bool OnDown(MotionEvent? e)
        {
            _v._downX = e?.GetX() ?? 0;
            _v._downY = e?.GetY() ?? 0;
            return true;
        }

        public override bool OnScroll(MotionEvent? e1, MotionEvent e2, float dx, float dy)
        {
            _v._fit = false;
            _v.EnsureFreeCamera();
            float s = _v.ViewScale;
            _v._camX += dx / s;
            _v._camY += dy / s;
            _v.ClampCamera();
            _v.Invalidate();
            return true;
        }

        public override bool OnDoubleTap(MotionEvent? e)
        {
            // 双击：在"适应全图"和"贴近观察"之间切换。手机上比找按钮快。
            _v._fit = !_v._fit;
            if (!_v._fit) { _v._zoom = 1.6f; _v.EnsureFreeCamera(); }
            _v.Invalidate();
            return true;
        }
    }

    /// <summary>以手指焦点为中心缩放 —— 这是手机上唯一符合直觉的缩放方式。</summary>
    private void ApplyZoom(float factor, float focusX, float focusY)
    {
        float before = ViewScale;
        float nx = _camX + (focusX - Width * 0.5f) / before;
        float ny = _camY + (focusY - Height * 0.5f) / before;

        _fit = false;
        _zoom = Math.Clamp(_zoom * factor, 0.05f, 6f);

        float after = ViewScale;
        _camX = nx - (focusX - Width * 0.5f) / after;
        _camY = ny - (focusY - Height * 0.5f) / after;
        ClampCamera();
        Invalidate();
    }

    public void ZoomBy(float factor)
    {
        ApplyZoom(factor, Width * 0.5f, Height * 0.5f);
    }

    public void FitAll()
    {
        _fit = true;
        Invalidate();
    }

    // ==================================================================
    // 相机
    // ==================================================================

    private float FitScale()
    {
        if (Width <= 0 || Height <= 0) return 1f;
        float s = Math.Min(Width / _worldW, Height / _worldH);
        return s * 0.99f;
    }

    public float ViewScale => _fit ? FitScale() : _zoom;

    private PointF WorldToScreen(float wx, float wy)
    {
        float s = ViewScale;
        float cx = _fit ? _worldW * 0.5f : _camX;
        float cy = _fit ? _worldH * 0.5f : _camY;
        return new PointF(
            (wx - cx) * s + Width * 0.5f,
            (wy - cy) * s + Height * 0.5f);
    }

    private void EnsureFreeCamera()
    {
        if (_fit) { _camX = _worldW * 0.5f; _camY = _worldH * 0.5f; }
    }

    private void ClampCamera()
    {
        float s = Math.Max(0.0001f, ViewScale);
        float halfW = Width * 0.5f / s;
        float halfH = Height * 0.5f / s;
        _camX = Math.Clamp(_camX, Math.Min(halfW, _worldW * 0.5f), Math.Max(_worldW - halfW, _worldW * 0.5f));
        _camY = Math.Clamp(_camY, Math.Min(halfH, _worldH * 0.5f), Math.Max(_worldH - halfH, _worldH * 0.5f));
    }

    /// <summary>点选最近的一只生物。手指粗，判定半径给到 34 像素。</summary>
    private void PickAt(float sx, float sy)
    {
        var snap = _snap;
        if (snap is null) return;

        int best = -1;
        float bestSq = 34f * 34f;
        for (int i = 0; i < snap.CreatureCount; i++)
        {
            var c = snap.Creatures[i];
            var sp = WorldToScreen(c.Pos.X, c.Pos.Y);
            float dx = sp.X - sx, dy = sp.Y - sy;
            float dsq = dx * dx + dy * dy;
            if (dsq < bestSq) { bestSq = dsq; best = c.Id; }
        }

        SelectedId = best;
        SelectionChanged?.Invoke();
        Invalidate();
    }

    public event Action? SelectionChanged;

    // ==================================================================
    // 绘制
    // ==================================================================

    protected override void OnDraw(Canvas canvas)
    {
        base.OnDraw(canvas);

        // 模拟线程已经死了就没什么好画的了。优先报这个 ——
        // 否则画面上是一帧不动的陈旧快照，看起来像"卡住了"，
        // 而真实原因是后台线程已经退出。
        if (_runner.FatalError is { } fatal)
        {
            DrawFatal(canvas, fatal);
            PostInvalidateDelayed(500);
            return;
        }

        // 这道 try/catch 是给"没有设备可测"这件事兜底的。
        //
        // 这个 APK 是在一台跑不了模拟器、也没有真机的机器上构建的，
        // 渲染层没有任何运行时验证。真要是这里的 Android 绘图 API 用错了，
        // 默认表现是一片黑屏 —— 用户完全无从判断是哪儿出了问题。
        // 把异常直接画在屏幕上，至少能一眼看出原因。
        try
        {
            var snap = _runner.Sim.Snapshot();
            _snap = snap;
            _worldW = snap.WorldWidth > 0 ? snap.WorldWidth : 3200f;
            _worldH = snap.WorldHeight > 0 ? snap.WorldHeight : 2000f;

            DrawTerrain(canvas, snap);
            DrawTerritory(canvas, snap);
            DrawEggs(canvas, snap);
            DrawCreatures(canvas, snap);
            DrawSelection(canvas, snap);
            DrawHud(canvas, snap);

            _lastError = null;
        }
        catch (Exception ex)
        {
            _lastError ??= ex.ToString();   // 只记第一次，否则每帧都覆盖成同一个
            DrawError(canvas, ex);
        }

        // 连续重绘：PostInvalidateOnAnimation 跟着屏幕刷新率走，
        // 比自建定时器省电也更顺滑。暂停时降到低频，别白白烧电池。
        if (!_runner.Paused) PostInvalidateOnAnimation();
        else PostInvalidateDelayed(200);
    }

    private string? _lastError;

    /// <summary>模拟线程崩了。跟 DrawError 的区别是这里没有 Exception 对象，只有文本。</summary>
    private void DrawFatal(Canvas canvas, string text)
    {
        canvas.DrawColor(Palette.Background);
        _text.Color = Palette.Danger;
        _text.TextSize = 26f;
        _text.SetStyle(APaint.Style.Fill);
        canvas.DrawText("模拟线程已停止（把这一屏截图发给开发者）", 20f, 50f, _text);
        DrawWrapped(canvas, text, 92f, 20f, Palette.TextDim);
    }

    /// <summary>
    /// 按屏宽硬折行地画一段文本。Canvas 没有自动换行，中英混排更没法指望它。
    /// 每行字符数按字号和屏宽估，不精确但足够读。
    /// </summary>
    private float DrawWrapped(Canvas canvas, string text, float startY, float size, Color color)
    {
        _text.Color = color;
        _text.TextSize = size;
        float y = startY;
        int per = Math.Max(12, (int)(Width / (size * 0.55f)));
        foreach (var raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;
            for (int i = 0; i < line.Length; i += per)
            {
                if (y > Height - 20f) return y;
                canvas.DrawText(line.Substring(i, Math.Min(per, line.Length - i)), 20f, y, _text);
                y += size + 7f;
            }
        }
        return y;
    }

    /// <summary>把出错位置画在屏幕上。这是无序列表式的最差 UI，但比黑屏强得多。</summary>
    private void DrawError(Canvas canvas, Exception ex)
    {
        canvas.DrawColor(Palette.Background);
        _text.Color = Palette.Danger;
        _text.TextSize = 26f;
        _text.SetStyle(APaint.Style.Fill);
        canvas.DrawText("渲染出错（把这一屏截图发给开发者）", 20f, 50f, _text);

        float y = DrawWrapped(canvas, ex.GetType().Name + ": " + ex.Message, 92f, 20f, Palette.Text);
        if (ex.StackTrace is { } st) DrawWrapped(canvas, st, y + 14f, 17f, Palette.TextDim);
    }

    private void DrawTerrain(Canvas canvas, SimSnapshot snap)
    {
        int w = snap.TileW, h = snap.TileH;
        if (w <= 0 || h <= 0) return;

        if (_tiles is null || _tiles.Width != w || _tiles.Height != h)
        {
            _tiles?.Recycle();
            _tiles = Bitmap.CreateBitmap(w, h, Bitmap.Config.Argb8888!);
            _tileBuf = new int[w * h];
        }

        // 昼夜：夜里整体压暗，但下限不能太低，否则一到夜晚什么都看不见
        float ambient = 0.66f + 0.34f * Math.Clamp(snap.Light, 0f, 1f);
        int foodR = Palette.Food.R, foodG = Palette.Food.G, foodB = Palette.Food.B;

        for (int i = 0; i < _tileBuf.Length; i++)
        {
            float fert = i < snap.Fertility.Length ? snap.Fertility[i] : 0f;
            var t = i < snap.Terrain.Length ? (TerrainType)snap.Terrain[i] : TerrainType.Grass;
            var baseC = Palette.Terrain(t, fert);

            int r = (int)(baseC.R * ambient);
            int g = (int)(baseC.G * ambient);
            int b = (int)(baseC.B * ambient);

            float food = i < snap.Food.Length ? snap.Food[i] : 0f;
            if (food > 0.02f && t != TerrainType.Rock)
            {
                float k = Math.Clamp(food, 0f, 1f) * 0.8f;
                r += (int)((foodR - r) * k);
                g += (int)((foodG - g) * k);
                b += (int)((foodB - b) * k);
            }

            _tileBuf[i] = unchecked((int)0xFF000000
                | (Math.Clamp(r, 0, 255) << 16)
                | (Math.Clamp(g, 0, 255) << 8)
                | Math.Clamp(b, 0, 255));
        }

        _tiles.SetPixels(_tileBuf, 0, w, 0, 0, w, h);

        float s = ViewScale;
        var tl = WorldToScreen(0, 0);
        _srcRect.Set(0, 0, w, h);
        _dstRect.Set(tl.X, tl.Y, tl.X + _worldW * s, tl.Y + _worldH * s);

        // FilterBitmap=false 就是最近邻：地形位图只有 160×100，要放大六倍铺满屏幕，
        // 双线性在这种倍率下会糊成一片。这一条在桌面版上也踩过，两边保持一致。
        _bmp.FilterBitmap = false;
        canvas.DrawBitmap(_tiles, _srcRect, _dstRect, _bmp);
    }

    /// <summary>领地叠加层：每个粗格染上占优物种的颜色。</summary>
    private void DrawTerritory(Canvas canvas, SimSnapshot snap)
    {
        if (!ShowTerritory) return;
        int tw = snap.TerritoryW, th = snap.TerritoryH;
        if (tw <= 0 || th <= 0 || snap.TerritorySpecies.Length < tw * th) return;

        float cell = snap.TerritoryCell;
        float s = ViewScale;
        float alphaScale = Math.Clamp(s / 0.4f, 0f, 1f);

        _fill.Alpha = (int)(26 * alphaScale);
        for (int y = 0; y < th; y++)
        {
            for (int x = 0; x < tw; x++)
            {
                int i = y * tw + x;
                int cnt = snap.TerritoryCount[i];
                if (cnt <= 0) continue;
                int sid = snap.TerritorySpecies[i];
                if (HighlightSpecies >= 0 && sid != HighlightSpecies) continue;

                _fill.Color = Palette.FromHue(SpeciesPalette.HueFor(sid));
                _fill.Alpha = (int)(Math.Min(8 + cnt * 3, 30) * alphaScale);

                var tl = WorldToScreen(x * cell, y * cell);
                _dstRect.Set(tl.X, tl.Y, tl.X + cell * s + 1f, tl.Y + cell * s + 1f);
                canvas.DrawRect(_dstRect, _fill);
            }
        }
        _fill.Alpha = 255;
    }

    private void DrawEggs(Canvas canvas, SimSnapshot snap)
    {
        if (snap.EggCount == 0) return;
        float s = ViewScale;

        for (int i = 0; i < snap.EggCount; i++)
        {
            var e = snap.Eggs[i];
            var sp = WorldToScreen(e.Pos.X, e.Pos.Y);
            float r = e.Radius * s;
            if (r < 1.2f) continue;
            if (sp.X < -r - 30 || sp.Y < -r - 30 || sp.X > Width + r + 30 || sp.Y > Height + r + 30) continue;

            bool dimmed = HighlightSpecies >= 0 && e.SpeciesId != HighlightSpecies;
            var hue = Palette.FromHue(SpeciesPalette.HueFor(e.SpeciesId));
            if (dimmed) hue = Palette.Blend(hue, Palette.Background, 0.78f);

            // 卵壳画得比身体苍白、像死物 —— 一眼能和活物区分
            var shell = Palette.Blend(hue, AColor.Rgb(238, 232, 214), 0.52f);
            _fill.Color = shell;
            canvas.DrawCircle(sp.X, sp.Y, r, _fill);

            _stroke.Color = Palette.Blend(shell, AColor.Black, 0.35f);
            _stroke.StrokeWidth = Math.Max(1f, r * 0.16f);
            _stroke.SetStyle(APaint.Style.Stroke);
            canvas.DrawCircle(sp.X, sp.Y, r, _stroke);

            // 孵化进度弧
            if (r >= 3f)
            {
                _stroke.Color = Palette.Blend(hue, AColor.White, 0.28f);
                _stroke.StrokeWidth = Math.Max(1.2f, r * 0.24f);
                float sweep = Math.Clamp(e.HatchPct, 0f, 1f) * 360f;
                if (sweep > 3f)
                {
                    _oval.Set(sp.X - r, sp.Y - r, sp.X + r, sp.Y + r);
                    canvas.DrawArc(_oval, -90f, sweep, false, _stroke);
                }
            }

            // 已承受的踩踏画成裂纹 —— "这颗卵还能扛几下"是看得见的
            int cracks = e.TrampleLimit - e.Toughness;
            if (cracks > 0 && r >= 3.5f)
            {
                _stroke.Color = AColor.Argb(190, 24, 20, 16);
                _stroke.StrokeWidth = Math.Max(1f, r * 0.15f);
                int shown = Math.Min(cracks, 5);
                for (int k = 0; k < shown; k++)
                {
                    float a = k / (float)shown * MathF.Tau + 0.7f;
                    canvas.DrawLine(
                        sp.X + MathF.Cos(a) * r * 0.18f, sp.Y + MathF.Sin(a) * r * 0.18f,
                        sp.X + MathF.Cos(a) * r * 0.88f, sp.Y + MathF.Sin(a) * r * 0.88f, _stroke);
                }
            }
        }
        _stroke.SetStyle(APaint.Style.Fill);
    }

    private void DrawCreatures(Canvas canvas, SimSnapshot snap)
    {
        float s = ViewScale;

        for (int i = 0; i < snap.CreatureCount; i++)
        {
            var c = snap.Creatures[i];
            var sp = WorldToScreen(c.Pos.X, c.Pos.Y);
            float r = c.BodyRadius * s * c.SizeScale;
            if (sp.X < -r - 40 || sp.Y < -r - 40 || sp.X > Width + r + 40 || sp.Y > Height + r + 40) continue;
            if (r < 0.7f) continue;

            bool dimmed = HighlightSpecies >= 0 && c.SpeciesId != HighlightSpecies;

            var body = Palette.FromHue(SpeciesPalette.HueFor(c.SpeciesId));
            body = Palette.Scale(body, 0.42f + 0.58f * Math.Clamp(c.EnergyPct, 0f, 1f));
            if (c.Maturity < 0.999f) body = Palette.Blend(body, Palette.Background, 0.30f);  // 幼体更淡
            if (dimmed) body = Palette.Blend(body, Palette.Background, 0.72f);

            float fx = MathF.Cos(c.Heading), fy = MathF.Sin(c.Heading);

            int n = ShapePoints(r);
            if (_polyX.Length < n) { _polyX = new float[n]; _polyY = new float[n]; }
            BuildBodyPolygon(c, sp.X, sp.Y, r, fx, fy, n);

            _path.Reset();
            _path.MoveTo(_polyX[0], _polyY[0]);
            for (int k = 1; k < n; k++) _path.LineTo(_polyX[k], _polyY[k]);
            _path.Close();

            _fill.Color = body;
            canvas.DrawPath(_path, _fill);

            // 描边：轮廓深一点，密集时个体之间不会糊在一起
            if (r > 2.2f)
            {
                _stroke.Color = Palette.Blend(body, AColor.Black, 0.42f);
                _stroke.StrokeWidth = Math.Max(0.8f, r * 0.16f);
                _stroke.SetStyle(APaint.Style.Stroke);
                canvas.DrawPath(_path, _stroke);
                _stroke.SetStyle(APaint.Style.Fill);
            }

            // LOD1：牙画成前端的尖，只在够大时画
            if (r > 3.2f)
            {
                int fang = c.OrganLevel((int)OrganType.Fang);
                if (fang > 0)
                {
                    float tip = r * (1f + fang * 0.06f);
                    _path.Reset();
                    _path.MoveTo(sp.X + fx * tip * 1.5f, sp.Y + fy * tip * 1.5f);
                    _path.LineTo(sp.X + fx * r * 0.5f - fy * r * 0.32f, sp.Y + fy * r * 0.5f + fx * r * 0.32f);
                    _path.LineTo(sp.X + fx * r * 0.5f + fy * r * 0.32f, sp.Y + fy * r * 0.5f - fx * r * 0.32f);
                    _path.Close();
                    _fill.Color = Palette.Blend(body, AColor.White, 0.55f);
                    canvas.DrawPath(_path, _fill);
                }
            }
        }
    }

    private static int ShapePoints(float r) => r < 4f ? 8 : r < 14f ? 14 : 20;

    /// <summary>
    /// 按器官构成生成身体轮廓 —— 算法与桌面版逐行一致。
    ///
    /// 这是"外观上有区分"的核心：腿拉长成流线型、甲压平成多面体、牙推出楔形、
    /// 藻胞隆起成团块、棘外凸成星形，再叠一个由个体 Id 派生的稳定微扰。
    /// </summary>
    private void BuildBodyPolygon(in RenderCreature c, float sx, float sy, float r, float fx, float fy, int n)
    {
        int legs = c.OrganLevel((int)OrganType.Legs);
        int fang = c.OrganLevel((int)OrganType.Fang);
        int plate = c.OrganLevel((int)OrganType.Plate);
        int stomach = c.OrganLevel((int)OrganType.Stomach);
        int algae = c.OrganLevel((int)OrganType.Algae);
        int spine = c.OrganLevel((int)OrganType.Spine);

        float elong = legs * 0.055f;
        float flat = plate * 0.035f;
        float wedge = fang * 0.06f;
        float round = stomach * 0.012f;
        float lumpy = algae * 0.05f;

        float px = -fy, py = fx;

        for (int k = 0; k < n; k++)
        {
            float a = k / (float)n * MathF.Tau;
            float ux = MathF.Cos(a), uy = MathF.Sin(a);
            float fwd = ux * fx + uy * fy;
            float side = ux * px + uy * py;

            float rad = r;
            rad *= 1f + elong * fwd;
            rad *= 1f - flat * MathF.Abs(side);
            rad *= 1f + wedge * MathF.Max(0f, fwd) * MathF.Max(0f, fwd);
            rad *= 1f + round;
            if (lumpy > 0f)
            {
                float hnoise = Hash01(c.Id * 131 + k * 17);
                rad *= 1f + lumpy * (hnoise - 0.5f);
            }
            if (spine > 0)
            {
                float spike = MathF.Max(0f, MathF.Sin(a * (3 + spine)));
                rad *= 1f + spine * 0.035f * spike;
            }

            _polyX[k] = sx + ux * rad;
            _polyY[k] = sy + uy * rad;
        }
    }

    private static float Hash01(int v)
    {
        uint h = (uint)v * 2654435761u;
        h ^= h >> 15; h *= 2246822519u;
        h ^= h >> 13; h *= 3266489917u;
        h ^= h >> 16;
        return (h & 0xFFFFFF) / 16777216f;
    }

    private void DrawSelection(Canvas canvas, SimSnapshot snap)
    {
        if (SelectedId < 0) return;
        for (int i = 0; i < snap.CreatureCount; i++)
        {
            var c = snap.Creatures[i];
            if (c.Id != SelectedId) continue;

            var sp = WorldToScreen(c.Pos.X, c.Pos.Y);
            float r = c.BodyRadius * ViewScale * c.SizeScale;
            _stroke.Color = Palette.Accent;
            _stroke.StrokeWidth = Math.Max(1.5f, r * 0.12f);
            _stroke.SetStyle(APaint.Style.Stroke);
            canvas.DrawCircle(sp.X, sp.Y, r + 7f, _stroke);

            // 视野圈：让"它看得见什么"变得可见
            if (ShowVision)
            {
                _stroke.Color = AColor.Argb(70, 96, 190, 255);
                _stroke.StrokeWidth = 1.2f;
                canvas.DrawCircle(sp.X, sp.Y, c.Vision * ViewScale, _stroke);
            }
            _stroke.SetStyle(APaint.Style.Fill);
            break;
        }
    }

    private void DrawHud(Canvas canvas, SimSnapshot snap)
    {
        _text.Color = Palette.Text;
        _text.TextSize = 26f;
        _text.SetStyle(APaint.Style.Fill);

        float top = 8f;
        canvas.DrawText($"{snap.Population} 个体   物种 {snap.SpeciesCount}", 14f, top + 44f, _text);

        if (_runner.MeasuredTicksPerSecond > 0)
        {
            _text.Color = Palette.TextDim;
            _text.TextSize = 20f;
            canvas.DrawText($"{_runner.MeasuredTicksPerSecond} tick/s   第 {snap.Tick} tick",
                14f, top + 74f, _text);
        }

        // 卵的统计：这是这一版新增的东西，必须让它可见
        if (snap.EggCount > 0)
        {
            _text.Color = Palette.Good;
            _text.TextSize = 20f;
            canvas.DrawText($"卵 {snap.EggCount}", 14f, top + 102f, _text);
        }
    }

    // ---- 显示开关 ----
    public bool ShowTerritory { get; set; } = true;
    public bool ShowVision { get; set; } = true;
    public int HighlightSpecies { get; set; } = -1;

    /// <summary>把镜头跳到某个世界坐标（小地图点击用）。</summary>
    public void JumpTo(float wx, float wy)
    {
        _fit = false;
        EnsureFreeCamera();
        _camX = wx;
        _camY = wy;
        ClampCamera();
        Invalidate();
    }
}
