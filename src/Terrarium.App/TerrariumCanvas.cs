using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Terrarium.Core;

namespace Terrarium.App;

/// <summary>
/// 生态箱视图。自绘 + 双缓冲，全部图形用 GDI+ 画。
///
/// 三条性能策略：
///  1. 地形 + 食物先画进一张 TileW×TileH 的小位图（LockBits 直接写像素），再整张拉伸 ——
///     而不是画上万个矩形。这一步是从"卡"到"流畅"的分界。
///  2. 生物身体是一个按器官构成变形的多边形，用复用的定点缓冲避免每帧分配。
///  3. 器官细节分三级 LOD，按**屏幕半径**而不是缩放倍数切换 ——
///     真正决定"看不看得清"的是它在屏幕上有多大。
///
/// 一条可读性策略：默认镜头是拉近的（显示约 1/9 的世界），
/// 因为全景视角下生物只有 8 像素、器官根本不画，"谁是什么样"完全看不出来。
/// 全局方位靠右下角的小地图给。
/// </summary>
internal sealed class TerrariumCanvas : Control
{
    private SimSnapshot? _snap;
    private Bitmap? _tiles;
    private int[] _tileBuf = Array.Empty<int>();

    private float _zoom = 0.9f;
    private PointF _camWorld = new(1600, 1000);
    private bool _fit;

    private bool _panning;
    private Point _panStart;
    private PointF _panCamStart;
    private bool _minimapDrag;
    private bool _painting;
    /// <summary>当前这一笔是"擦除"还是"绘制"。由按下时哪个键决定，拖拽过程中保持不变。</summary>
    private bool PaintingErase;

    private int _hoverId = -1;
    public int SelectedId = -1;
    public bool FollowSelected;
    public bool ShowVision = true;
    public bool ShowOrgans = true;
    public bool ShowTerritory = true;
    public bool ShowMinimap = true;
    /// <summary>地形编辑模式。打开后左键绘制、右键擦除，且点击不再选中生物。</summary>
    public bool EditMode;

    /// <summary>高亮的物种 Id。-1 = 不高亮（全部正常显示）。</summary>
    public int HighlightSpecies = -1;

    public event Action<int>? CreaturePicked;
    /// <summary>地形绘制回调：(世界坐标, 是否擦除)。由主窗体接上编辑器。</summary>
    public event Action<Vec2, bool>? TerrainPainted;

    /// <summary>鼠标左/右键是否仍按着。主窗体靠它判断一笔什么时候结束。</summary>
    public bool MouseButtonsActive
        => (Control.MouseButtons & (MouseButtons.Left | MouseButtons.Right)) != 0;

    // 复用的 GDI 对象
    private readonly SolidBrush _brush = new(Color.White);
    private readonly Pen _pen = new(Color.White, 1f);
    private readonly Pen _ringPen = new(Color.White, 1.5f);
    private readonly Font _hudFont = new("Consolas", 9f);
    private readonly Font _smallFont = new("Microsoft YaHei UI", 8f);
    private readonly SolidBrush _hudBrush = new(Color.FromArgb(215, 226, 231, 238));
    private readonly SolidBrush _dimBrush = new(Color.FromArgb(170, 150, 160, 175));
        // 按点数缓存的定长缓冲。
    // GDI+ 的 FillPolygon/DrawPolygon 没有 offset+count 重载（只有 FillMode 那个），
    // 所以必须传精确长度的数组；每帧 new 一个会有可观的 GC 压力，这里缓存三档复用。
    private readonly Point[] _poly8 = new Point[8];
    private readonly Point[] _poly14 = new Point[14];
    private readonly Point[] _poly20 = new Point[20];

    private Point[] PolyFor(int n) => n <= 8 ? _poly8 : n <= 14 ? _poly14 : _poly20;

    // 领地图层缓存（避免每帧按物种查色相）
    private readonly Dictionary<int, Color> _speciesColor = new();

    public TerrariumCanvas()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Background;
    }

    // ==================================================================
    // 状态注入
    // ==================================================================

    public void UpdateSnapshot(SimSnapshot snap)
    {
        _snap = snap;
        if (FollowSelected && SelectedId >= 0)
        {
            for (int i = 0; i < snap.CreatureCount; i++)
            {
                if (snap.Creatures[i].Id == SelectedId)
                {
                    _camWorld = new PointF(snap.Creatures[i].Pos.X, snap.Creatures[i].Pos.Y);
                    break;
                }
            }
        }
        Invalidate();
    }

    /// <summary>把镜头对准世界中心并按给定缩放显示（新建生态箱时调用）。</summary>
    public void ResetView(SimSnapshot snap)
    {
        _fit = false;
        _zoom = 0.9f;
        _camWorld = new PointF(snap.WorldWidth * 0.5f, snap.WorldHeight * 0.5f);
        Invalidate();
    }

    // ==================================================================
    // 坐标变换
    // ==================================================================

    private float FitScale()
    {
        if (_snap is null || _snap.WorldWidth <= 0) return 1f;
        return Math.Min(Width / _snap.WorldWidth, Height / _snap.WorldHeight) * 0.97f;
    }

    private float ViewScale => _fit ? FitScale() : _zoom;

    private PointF CamCenter => _fit || _snap is null
        ? new PointF(_snap?.WorldWidth * 0.5f ?? 0f, _snap?.WorldHeight * 0.5f ?? 0f)
        : _camWorld;

    private PointF WorldToScreen(Vec2 p)
    {
        float s = ViewScale;
        PointF c = CamCenter;
        return new PointF((p.X - c.X) * s + Width * 0.5f, (p.Y - c.Y) * s + Height * 0.5f);
    }

    private Vec2 ScreenToWorld(Point p)
    {
        float s = ViewScale;
        PointF c = CamCenter;
        return new Vec2((p.X - Width * 0.5f) / s + c.X, (p.Y - Height * 0.5f) / s + c.Y);
    }

    public void ZoomToFit()
    {
        _fit = true;
        Invalidate();
    }

    private void EnsureFreeCamera()
    {
        if (!_fit) return;
        _zoom = FitScale();
        _camWorld = CamCenter;
        _fit = false;
    }

    // ==================================================================
    // 小地图
    // ==================================================================

    private const int MinimapMargin = 12;

    private Rectangle MinimapRect()
    {
        if (_snap is null) return Rectangle.Empty;
        int w = 232;
        int h = (int)(w * _snap.WorldHeight / MathF.Max(1f, _snap.WorldWidth));
        h = Math.Clamp(h, 60, 200);
        return new Rectangle(Width - w - MinimapMargin, Height - h - MinimapMargin - 18, w, h);
    }

    // ==================================================================
    // 鼠标
    // ==================================================================

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        EnsureFreeCamera();
        Vec2 before = ScreenToWorld(e.Location);
        float factor = e.Delta > 0 ? 1.22f : 1f / 1.22f;
        _zoom = Math.Clamp(_zoom * factor, 0.04f, 30f);
        Vec2 after = ScreenToWorld(e.Location);
        _camWorld = new PointF(_camWorld.X + (before.X - after.X), _camWorld.Y + (before.Y - after.Y));
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();

        // 小地图优先：点它跳转镜头
        if (ShowMinimap && _snap is not null && MinimapRect().Contains(e.Location))
        {
            _minimapDrag = true;
            JumpCameraFromMinimap(e.Location);
            return;
        }

        if (EditMode && TerrainPainted is not null)
        {
            // 编辑模式：左键绘制、右键擦除，并且不选中生物（避免把画地形误当成选生物）
            _painting = true;
            PaintingErase = e.Button == MouseButtons.Right;
            TerrainPainted(ScreenToWorld(e.Location), PaintingErase);
            return;
        }

        if (e.Button == MouseButtons.Middle || e.Button == MouseButtons.Right)
        {
            EnsureFreeCamera();
            _panning = true;
            _panStart = e.Location;
            _panCamStart = _camWorld;
        }
        else if (e.Button == MouseButtons.Left)
        {
            int id = PickAt(e.Location);
            SelectedId = id;
            CreaturePicked?.Invoke(id);
            if (id < 0)
            {
                EnsureFreeCamera();
                _panning = true;
                _panStart = e.Location;
                _panCamStart = _camWorld;
            }
            Invalidate();
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (_painting)
        {
            TerrainPainted?.Invoke(ScreenToWorld(e.Location), PaintingErase);
            return;
        }
        if (_minimapDrag)
        {
            JumpCameraFromMinimap(e.Location);
            return;
        }
        if (_panning)
        {
            float s = ViewScale;
            _camWorld = new PointF(
                _panCamStart.X - (e.X - _panStart.X) / s,
                _panCamStart.Y - (e.Y - _panStart.Y) / s);
            Invalidate();
            return;
        }

        int id = PickAt(e.Location);
        if (id != _hoverId)
        {
            _hoverId = id;
            Cursor = id >= 0 ? Cursors.Hand : Cursors.Cross;
            Invalidate();
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _panning = false;
        _minimapDrag = false;
        _painting = false;
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        int id = PickAt(e.Location);
        if (id >= 0)
        {
            SelectedId = id;
            FollowSelected = true;
            EnsureFreeCamera();
            _zoom = Math.Max(_zoom, 2.2f);
            CreaturePicked?.Invoke(id);
        }
    }

    private void JumpCameraFromMinimap(Point p)
    {
        if (_snap is null) return;
        Rectangle mm = MinimapRect();
        if (mm.Width <= 0) return;
        float fx = Math.Clamp((p.X - mm.Left) / (float)mm.Width, 0f, 1f);
        float fy = Math.Clamp((p.Y - mm.Top) / (float)mm.Height, 0f, 1f);
        EnsureFreeCamera();
        _camWorld = new PointF(fx * _snap.WorldWidth, fy * _snap.WorldHeight);
        FollowSelected = false;
        Invalidate();
    }

    private int PickAt(Point p)
    {
        if (_snap is null) return -1;
        float best = float.MaxValue;
        int bestId = -1;
        for (int i = 0; i < _snap.CreatureCount; i++)
        {
            var c = _snap.Creatures[i];
            PointF sp = WorldToScreen(c.Pos);
            float r = MathF.Max(4f, c.BodyRadius * ViewScale + 3f);
            float dx = sp.X - p.X, dy = sp.Y - p.Y;
            float d = dx * dx + dy * dy;
            if (d <= r * r && d < best)
            {
                best = d;
                bestId = c.Id;
            }
        }
        return bestId;
    }

    // ==================================================================
    // 绘制
    // ==================================================================

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Background);

        if (_snap is null)
        {
            g.DrawString("正在生成生态箱…", Theme.TitleFont, _hudBrush, 20, 20);
            return;
        }

        BuildSpeciesColors();
        // 地形位图只有 TileW×TileH（3200×2000 的世界是 160×100），要放大六倍左右铺满画布。
        // 双线性在这种放大倍率下会糊成一片 —— 必须用最近邻，地形才是清晰可辨的色块。
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        DrawTerrain(g);
        DrawTerritory(g);

        g.SmoothingMode = SmoothingMode.AntiAlias;
        DrawEggs(g);
        DrawCreatures(g);
        DrawSelection(g);

        g.SmoothingMode = SmoothingMode.None;
        DrawHud(g);

        if (ShowMinimap)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            DrawMinimap(g);
        }
    }

    private void BuildSpeciesColors()
    {
        _speciesColor.Clear();
        var snap = _snap!;
        for (int i = 0; i < snap.CreatureCount; i++)
        {
            var c = snap.Creatures[i];
            if (!_speciesColor.ContainsKey(c.SpeciesId))
                _speciesColor[c.SpeciesId] = Theme.FromHue(c.SpeciesHue, 0.78f, 0.96f);
        }
    }

    private Color ColorOf(int speciesId)
        => _speciesColor.TryGetValue(speciesId, out var c) ? c : Theme.Accent;

    /// <summary>把地形 + 食物画进小位图，再整张拉伸。</summary>
    private void DrawTerrain(Graphics g)
    {
        var snap = _snap!;
        int w = snap.TileW, h = snap.TileH;
        if (w <= 0 || h <= 0) return;

        if (_tiles is null || _tiles.Width != w || _tiles.Height != h)
        {
            _tiles?.Dispose();
            _tiles = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            _tileBuf = new int[w * h];
        }

        // 昼夜：夜里整体压暗，但下限不能太低，否则一到夜晚什么都看不见
        float light = Math.Clamp(snap.Light, 0f, 1f);
        float ambient = 0.66f + 0.34f * light;

        for (int i = 0; i < _tileBuf.Length; i++)
        {
            float fert = i < snap.Fertility.Length ? snap.Fertility[i] : 0f;
            var t = i < snap.Terrain.Length ? (TerrainType)snap.Terrain[i] : TerrainType.Grass;
            Color baseC = Theme.TerrainColor(t, fert);

            int r = (int)(baseC.R * ambient);
            int gg = (int)(baseC.G * ambient);
            int b = (int)(baseC.B * ambient);

            float food = i < snap.Food.Length ? snap.Food[i] : 0f;
            if (food > 0.02f && t != TerrainType.Rock)
            {
                float k = Math.Clamp(food, 0f, 1f) * 0.8f;
                r += (int)((Theme.Food.R - r) * k);
                gg += (int)((Theme.Food.G - gg) * k);
                b += (int)((Theme.Food.B - b) * k);
            }

            _tileBuf[i] = unchecked((int)0xFF000000
                | (Math.Clamp(r, 0, 255) << 16)
                | (Math.Clamp(gg, 0, 255) << 8)
                | Math.Clamp(b, 0, 255));
        }

        var data = _tiles.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            if (data.Stride == w * 4)
                Marshal.Copy(_tileBuf, 0, data.Scan0, _tileBuf.Length);
            else
                for (int y = 0; y < h; y++)
                    Marshal.Copy(_tileBuf, y * w, data.Scan0 + y * data.Stride, w);
        }
        finally { _tiles.UnlockBits(data); }

        PointF tl = WorldToScreen(Vec2.Zero);
        float s = ViewScale;
        g.DrawImage(_tiles, new RectangleF(tl.X, tl.Y, snap.WorldWidth * s, snap.WorldHeight * s));
    }

    /// <summary>
    /// 领地叠加层：每个粗格染上占优物种的颜色。
    /// 这是让"物种之间有区分"变得肉眼可见的最有效一层 ——
    /// 即使两只生物长得像，你也能看到它们的势力范围在哪里交界。
    /// </summary>
    private void DrawTerritory(Graphics g)
    {
        var snap = _snap!;
        if (!ShowTerritory || snap.TerritoryW <= 0 || snap.TerritorySpecies.Length == 0) return;

        float cell = snap.TerritoryCell;
        float s = ViewScale;

        for (int ty = 0; ty < snap.TerritoryH; ty++)
        {
            for (int tx = 0; tx < snap.TerritoryW; tx++)
            {
                int idx = ty * snap.TerritoryW + tx;
                if (idx >= snap.TerritorySpecies.Length) continue;
                int species = snap.TerritorySpecies[idx];
                if (species == 0) continue;

                var tl = WorldToScreen(new Vec2(tx * cell, ty * cell));
                float cw = cell * s;
                if (tl.X + cw < 0 || tl.Y + cw < 0 || tl.X > Width || tl.Y > Height) continue;

                Color col = ColorOf(species);
                // 个体越多颜色越实，稀疏占领只是淡淡一层
                int n = idx < snap.TerritoryCount.Length ? snap.TerritoryCount[idx] : 1;
                int alpha = (int)Math.Clamp(8 + n * 3, 8, 30);
                _brush.Color = Color.FromArgb(alpha, col);
                g.FillRectangle(_brush, tl.X, tl.Y, cw + 0.5f, cw + 0.5f);
            }
        }
    }

    /// <summary>
    /// 画卵。
    ///
    /// 画在生物**之前**：卵是静态的、埋在场景里的东西，让生物压在上面才符合直觉，
    /// 而且这样"谁踩在卵上"一眼可见 —— 那正是踩碎机制发生的地方。
    /// </summary>
    private void DrawEggs(Graphics g)
    {
        var snap = _snap!;
        if (snap.EggCount == 0) return;
        float s = ViewScale;

        for (int i = 0; i < snap.EggCount; i++)
        {
            var e = snap.Eggs[i];
            PointF sp = WorldToScreen(e.Pos);
            float r = e.Radius * s;
            if (r < 1.2f) continue;
            if (sp.X < -r - 30 || sp.Y < -r - 30 || sp.X > Width + r + 30 || sp.Y > Height + r + 30) continue;

            bool dimmed = HighlightSpecies >= 0 && e.SpeciesId != HighlightSpecies;
            Color hue = ColorOf(e.SpeciesId);
            if (dimmed) hue = Theme.Blend(hue, Theme.Background, 0.78f);

            // 卵壳：比身体更苍白、更"死物"，一眼能和活物区分开
            Color shell = Theme.Blend(hue, Color.FromArgb(238, 232, 214), 0.52f);
            _brush.Color = shell;
            g.FillEllipse(_brush, sp.X - r, sp.Y - r, r * 2, r * 2);

            _pen.Color = Theme.Blend(shell, Color.Black, 0.35f);
            _pen.Width = Math.Max(1f, r * 0.16f);
            g.DrawEllipse(_pen, sp.X - r, sp.Y - r, r * 2, r * 2);

            // 孵化进度：沿外圈画一段弧。快孵出来时几乎是一整圈。
            if (r >= 3f)
            {
                _pen.Color = Theme.Blend(hue, Color.White, 0.28f);
                _pen.Width = Math.Max(1.2f, r * 0.24f);
                float sweep = Math.Clamp(e.HatchPct, 0f, 1f) * 360f;
                if (sweep > 3f) g.DrawArc(_pen, sp.X - r, sp.Y - r, r * 2, r * 2, -90f, sweep);
            }

            // 已承受的踩踏：画成裂纹数量。于是"这颗卵还能扛几下"是**看得见**的。
            int cracks = e.TrampleLimit - e.Toughness;
            if (cracks > 0 && r >= 3.5f)
            {
                _pen.Color = Color.FromArgb(190, 24, 20, 16);
                _pen.Width = Math.Max(1f, r * 0.15f);
                int shown = Math.Min(cracks, 5);
                for (int k = 0; k < shown; k++)
                {
                    float a = (k / (float)shown) * MathF.Tau + 0.7f;
                    float x1 = sp.X + MathF.Cos(a) * r * 0.18f;
                    float y1 = sp.Y + MathF.Sin(a) * r * 0.18f;
                    float x2 = sp.X + MathF.Cos(a) * r * 0.88f;
                    float y2 = sp.Y + MathF.Sin(a) * r * 0.88f;
                    g.DrawLine(_pen, x1, y1, x2, y2);
                }
            }
        }
    }

    private void DrawCreatures(Graphics g)
    {
        var snap = _snap!;
        float s = ViewScale;

        for (int i = 0; i < snap.CreatureCount; i++)
        {
            var c = snap.Creatures[i];
            PointF sp = WorldToScreen(c.Pos);
            float r = c.BodyRadius * s * c.SizeScale;
            if (sp.X < -r - 40 || sp.Y < -r - 40 || sp.X > Width + r + 40 || sp.Y > Height + r + 40) continue;
            if (r < 0.7f) continue;

            bool dimmed = HighlightSpecies >= 0 && c.SpeciesId != HighlightSpecies;

            Color body = ColorOf(c.SpeciesId);
            // 幼体画得更淡更小 —— 生命阶段必须一眼可辨，否则玩家看不出"成长"这件事在发生
            bool juvenile = c.Maturity < 0.999f;
            if (juvenile) body = Theme.Blend(body, Theme.Background, 0.30f);
            body = Theme.ScaleValue(body, 0.42f + 0.58f * Math.Clamp(c.EnergyPct, 0f, 1f));
            if (dimmed) body = Theme.Blend(body, Theme.Background, 0.72f);

            float fx = MathF.Cos(c.Heading), fy = MathF.Sin(c.Heading);
            float px = -fy, py = fx;

            // 身体：按器官构成变形的多边形（不是千篇一律的圆）
            int n = ShapePoints(r);
            Point[] poly = PolyFor(n);
            BuildBodyPolygon(c, sp, r, fx, fy, px, py, poly, n);

            _brush.Color = body;
            g.FillPolygon(_brush, poly);

            // 生命值描边
            float hp = Math.Clamp(c.HealthPct, 0f, 1f);
            if (r >= 1.6f)
            {
                _ringPen.Color = dimmed
                    ? Color.FromArgb(90, Theme.Blend(Theme.Danger, Theme.Good, hp))
                    : Theme.Blend(Theme.Danger, Theme.Good, hp);
                _ringPen.Width = MathF.Max(1f, r * 0.16f);
                g.DrawPolygon(_ringPen, poly);
            }

            // LOD1：定义轮廓的器官（牙 / 甲 / 棘 / 腿）—— 屏幕够大就画
            if (ShowOrgans && r >= 3.2f && !dimmed)
                DrawSilhouetteOrgans(g, c, sp, r, fx, fy, px, py);

            // LOD2：细节器官（眼 / 藻胞 / 毒腺 / 鳃）
            if (ShowOrgans && r >= 8f && !dimmed)
                DrawFineOrgans(g, c, sp, r, fx, fy, px, py);
        }
    }

    private static int ShapePoints(float r) => r < 4f ? 8 : r < 14f ? 14 : 20;

    /// <summary>
    /// 按器官构成生成身体轮廓。
    ///
    /// 以前所有生物都画成同一个圆，所以"外观上没有差距"。现在每项器官都在形变上留下痕迹：
    ///   腿 → 沿朝向拉长（流线型）      甲 → 侧向压平成多面体
    ///   牙 → 前端推出成楔形            藻胞 → 表面隆起成不规则团块
    ///   胃 → 整体圆润变大
    /// 再叠一个由个体 Id 派生的稳定微扰，于是同物种的个体也不会长得一模一样。
    /// </summary>
    private void BuildBodyPolygon(in RenderCreature c, PointF sp, float r,
                                  float fx, float fy, float px, float py, Point[] poly, int n)
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

        for (int k = 0; k < n; k++)
        {
            float a = k / (float)n * MathF.Tau;
            float ux = MathF.Cos(a), uy = MathF.Sin(a);
            float fwd = ux * fx + uy * fy;        // -1 后 … +1 前
            float side = ux * px + uy * py;

            float rad = r;
            rad *= 1f + elong * fwd;                          // 流线
            rad *= 1f - flat * MathF.Abs(side);               // 侧向压平
            rad *= 1f + wedge * MathF.Max(0f, fwd) * MathF.Max(0f, fwd);  // 前吻楔形
            rad *= 1f + round;                                // 圆润
            if (lumpy > 0f)
            {
                // 稳定哈希噪声：同一只生物每帧形状一致，不会抖
                float hnoise = Hash01(c.Id * 131 + k * 17);
                rad *= 1f + lumpy * (hnoise - 0.5f);
            }
            // 棘：轮廓外凸成星形
            if (spine > 0)
            {
                float spike = MathF.Max(0f, MathF.Sin(a * (3 + spine)));
                rad *= 1f + spine * 0.035f * spike;
            }

            poly[k] = new Point((int)(sp.X + ux * rad), (int)(sp.Y + uy * rad));
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

    /// <summary>LOD1：决定轮廓的器官 —— 牙、甲、棘、腿。屏幕半径 3.2 像素以上就画。</summary>
    private void DrawSilhouetteOrgans(Graphics g, in RenderCreature c, PointF sp, float r,
                                      float fx, float fy, float px, float py)
    {
        int fang = c.OrganLevel((int)OrganType.Fang);
        int plate = c.OrganLevel((int)OrganType.Plate);
        int spine = c.OrganLevel((int)OrganType.Spine);
        int legs = c.OrganLevel((int)OrganType.Legs);

        if (legs > 0)
        {
            _pen.Color = Color.FromArgb(190, 224, 234, 206);
            _pen.Width = MathF.Max(1f, r * 0.16f);
            int shown = Math.Min(legs, 4);
            for (int i = 0; i < shown; i++)
            {
                float off = (i - (shown - 1) * 0.5f) * r * 0.6f;
                float bx = sp.X + fx * off, by = sp.Y + fy * off;
                g.DrawLine(_pen, bx + px * r * 0.75f, by + py * r * 0.75f,
                                bx + px * r * 1.45f, by + py * r * 1.45f);
                g.DrawLine(_pen, bx - px * r * 0.75f, by - py * r * 0.75f,
                                bx - px * r * 1.45f, by - py * r * 1.45f);
            }
        }

        if (spine > 0)
        {
            _pen.Color = Color.FromArgb(215, 236, 242, 248);
            _pen.Width = MathF.Max(1f, r * 0.13f);
            int count = Math.Min(spine + 3, 8);
            for (int i = 0; i < count; i++)
            {
                float a = i / (float)count * MathF.Tau;
                float ca = MathF.Cos(a), sa = MathF.Sin(a);
                g.DrawLine(_pen, sp.X + ca * r * 0.9f, sp.Y + sa * r * 0.9f,
                                sp.X + ca * r * 1.5f, sp.Y + sa * r * 1.5f);
            }
        }

        if (plate > 0)
        {
            _ringPen.Color = Color.FromArgb(205, 158, 180, 208);
            _ringPen.Width = MathF.Max(1.2f, r * (0.13f + plate * 0.045f));
            g.DrawEllipse(_ringPen, sp.X - r * 0.8f, sp.Y - r * 0.8f, r * 1.6f, r * 1.6f);
        }

        if (fang > 0)
        {
            _brush.Color = Color.FromArgb(246, 104, 104);
            float len = r * (0.5f + fang * 0.17f);
            for (int side = -1; side <= 1; side += 2)
            {
                float bx = sp.X + fx * r * 0.6f + px * side * r * 0.4f;
                float by = sp.Y + fy * r * 0.6f + py * side * r * 0.4f;
                var t1 = new PointF(bx + fx * len, by + fy * len);
                var t2 = new PointF(bx + px * side * r * 0.26f, by + py * side * r * 0.26f);
                var t3 = new PointF(bx - px * side * r * 0.26f, by - py * side * r * 0.26f);
                g.FillPolygon(_brush, new[] { t1, t2, t3 });
            }
        }
    }

    /// <summary>LOD2：细节器官 —— 眼、藻胞、毒腺、鳃。屏幕半径 8 像素以上才画。</summary>
    private void DrawFineOrgans(Graphics g, in RenderCreature c, PointF sp, float r,
                                float fx, float fy, float px, float py)
    {
        int eye = c.OrganLevel((int)OrganType.Eye);
        int algae = c.OrganLevel((int)OrganType.Algae);
        int gland = c.OrganLevel((int)OrganType.Gland);
        int gill = c.OrganLevel((int)OrganType.Gill);

        if (eye > 0)
        {
            float er = MathF.Max(1f, r * 0.2f);
            int shown = Math.Min(eye, 3);
            for (int i = 0; i < shown; i++)
            {
                float off = (i - (shown - 1) * 0.5f) * r * 0.52f;
                float ex = sp.X + fx * r * 0.6f + px * off;
                float ey = sp.Y + fy * r * 0.6f + py * off;
                _brush.Color = Color.FromArgb(248, 249, 252);
                g.FillEllipse(_brush, ex - er, ey - er, er * 2f, er * 2f);
                _brush.Color = Color.FromArgb(22, 26, 32);
                g.FillEllipse(_brush, ex - er * 0.45f, ey - er * 0.45f, er * 0.9f, er * 0.9f);
            }
        }

        if (algae > 0)
        {
            float ar = MathF.Max(1f, r * 0.17f);
            int shown = Math.Min(algae + 2, 6);
            for (int i = 0; i < shown; i++)
            {
                float a = i * 2.39f;
                float ax = sp.X + MathF.Cos(a) * r * 0.5f;
                float ay = sp.Y + MathF.Sin(a) * r * 0.5f;
                _brush.Color = Color.FromArgb(Math.Min(255, 150 + algae * 18), 122, 224, 122);
                g.FillEllipse(_brush, ax - ar, ay - ar, ar * 2f, ar * 2f);
            }
        }

        if (gland > 0)
        {
            float gr = MathF.Max(1f, r * 0.19f);
            float gx = sp.X - fx * r * 0.35f;
            float gy = sp.Y - fy * r * 0.35f;
            _brush.Color = Color.FromArgb(205, 192, 112, 238);
            g.FillEllipse(_brush, gx - gr, gy - gr, gr * 2f, gr * 2f);
        }

        if (gill > 0)
        {
            _pen.Color = Color.FromArgb(215, 112, 204, 252);
            _pen.Width = MathF.Max(1f, r * 0.14f);
            g.DrawArc(_pen, sp.X - r * 0.58f, sp.Y - r * 0.58f, r * 1.16f, r * 1.16f, 200f, 140f);
        }
    }

    private void DrawSelection(Graphics g)
    {
        var snap = _snap!;

        if (_hoverId >= 0 && _hoverId != SelectedId)
        {
            for (int i = 0; i < snap.CreatureCount; i++)
            {
                if (snap.Creatures[i].Id != _hoverId) continue;
                PointF sp = WorldToScreen(snap.Creatures[i].Pos);
                float r = snap.Creatures[i].BodyRadius * ViewScale + 3f;
                _ringPen.Color = Color.FromArgb(150, 200, 220, 240);
                _ringPen.Width = 1f;
                g.DrawEllipse(_ringPen, sp.X - r, sp.Y - r, r * 2f, r * 2f);
                break;
            }
        }

        if (SelectedId < 0) return;
        for (int i = 0; i < snap.CreatureCount; i++)
        {
            var c = snap.Creatures[i];
            if (c.Id != SelectedId) continue;

            PointF sp = WorldToScreen(c.Pos);
            float r = c.BodyRadius * ViewScale;

            if (ShowVision)
            {
                float vr = c.Vision * ViewScale;
                _pen.Color = Color.FromArgb(66, 120, 200, 255);
                _pen.Width = 1f;
                _pen.DashStyle = DashStyle.Dash;
                g.DrawEllipse(_pen, sp.X - vr, sp.Y - vr, vr * 2f, vr * 2f);
                _pen.DashStyle = DashStyle.Solid;
            }

            _ringPen.Color = Color.White;
            _ringPen.Width = 2f;
            g.DrawEllipse(_ringPen, sp.X - r - 4f, sp.Y - r - 4f, (r + 4f) * 2f, (r + 4f) * 2f);

            if (c.LastRule >= 0)
            {
                _pen.Color = Theme.AccentWarm;
                _pen.Width = 1.6f;
                g.DrawLine(_pen, sp.X, sp.Y - r - 5f, sp.X, sp.Y - r - 17f);
                _brush.Color = Theme.AccentWarm;
                g.FillEllipse(_brush, sp.X - 2.5f, sp.Y - r - 20f, 5f, 5f);
            }
            return;
        }
        SelectedId = -1;
    }

    private void DrawHud(Graphics g)
    {
        var snap = _snap!;
        string lightTxt = snap.Light > 0.66f ? "白昼" : snap.Light > 0.33f ? "晨昏" : "夜晚";
        string s = $"{snap.Population} 个体   物种 {snap.SpeciesCount}   {lightTxt} {snap.Light * 100:F0}%";
        g.DrawString(s, _hudFont, _hudBrush, 10, 8);

        if (snap.Extinct)
        {
            string t = "种群已灭绝 — 按「新建生态箱」重新开始";
            var sz = g.MeasureString(t, Theme.TitleFont);
            _brush.Color = Color.FromArgb(185, 0, 0, 0);
            g.FillRectangle(_brush, Width / 2f - sz.Width / 2f - 12, 34, sz.Width + 24, sz.Height + 12);
            g.DrawString(t, Theme.TitleFont, Brushes.White, Width / 2f - sz.Width / 2f, 40);
        }

        string hint = TerrainPainted is null
            ? $"缩放 {ViewScale * 100:F0}%   滚轮缩放 · 右键拖动平移 · 双击跟随 · 点生物查看"
            : $"缩放 {ViewScale * 100:F0}%   【地形编辑中】Shift+左键绘制 · 右键擦除";
        g.DrawString(hint, _smallFont, _dimBrush, 10, Height - 20);
    }

    private void DrawMinimap(Graphics g)
    {
        var snap = _snap!;
        if (_tiles is null || snap.WorldWidth <= 0) return;

        Rectangle mm = MinimapRect();
        if (mm.Width <= 8 || mm.Height <= 8) return;

        using (var bg = new SolidBrush(Color.FromArgb(215, 12, 14, 18)))
            g.FillRectangle(bg, mm);

        // 迷你地图是**缩小**（160 宽 → 232 宽其实接近 1:1，但世界尺寸不同时会缩小），
        // 缩小时最近邻会产生锯齿噪点，所以这里保持平滑。
        g.InterpolationMode = InterpolationMode.HighQualityBilinear;
        g.DrawImage(_tiles, mm);

        // 领地色斑（在小地图上更能看清势力范围）
        if (ShowTerritory && snap.TerritoryW > 0)
        {
            float cw = mm.Width / (float)snap.TerritoryW;
            float ch = mm.Height / (float)snap.TerritoryH;
            for (int ty = 0; ty < snap.TerritoryH; ty++)
            {
                for (int tx = 0; tx < snap.TerritoryW; tx++)
                {
                    int idx = ty * snap.TerritoryW + tx;
                    if (idx >= snap.TerritorySpecies.Length) continue;
                    int sp2 = snap.TerritorySpecies[idx];
                    if (sp2 == 0) continue;
                    _brush.Color = Color.FromArgb(70, ColorOf(sp2));
                    g.FillRectangle(_brush, mm.Left + tx * cw, mm.Top + ty * ch, cw + 0.6f, ch + 0.6f);
                }
            }
        }

        // 当前视野框
        float s = ViewScale;
        PointF c = CamCenter;
        float vw = Width / s, vh = Height / s;
        float rx = mm.Left + (c.X - vw / 2f) / snap.WorldWidth * mm.Width;
        float ry = mm.Top + (c.Y - vh / 2f) / snap.WorldHeight * mm.Height;
        float rw = vw / snap.WorldWidth * mm.Width;
        float rh = vh / snap.WorldHeight * mm.Height;

        using (var fp = new Pen(Color.FromArgb(230, 255, 255, 255), 1.4f))
            g.DrawRectangle(fp, rx, ry, Math.Min(rw, mm.Width), Math.Min(rh, mm.Height));

        using (var border = new Pen(Theme.Border, 1f))
            g.DrawRectangle(border, mm);

        g.DrawString("小地图（点击跳转）", _smallFont, _dimBrush, mm.Left, mm.Top - 15);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tiles?.Dispose();
            _brush.Dispose();
            _pen.Dispose();
            _ringPen.Dispose();
            _hudFont.Dispose();
            _smallFont.Dispose();
            _hudBrush.Dispose();
            _dimBrush.Dispose();
        }
        base.Dispose(disposing);
    }
}
