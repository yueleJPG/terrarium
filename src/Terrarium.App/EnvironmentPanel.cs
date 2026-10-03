using Terrarium.Core;

namespace Terrarium.App;

/// <summary>
/// 环境面板 —— 生态箱的"旋钮"。
///
/// 这些滑块直接改 SimConfig，而 SimConfig 就是模拟的物理法则。
/// 整个项目最有教育意义的部分之一就是这里：把"食物再生"拉低一点，
/// 几十秒后你会看到种群曲线、平均体型、物种数全都变了。
/// </summary>
internal sealed class EnvironmentPanel : UserControl
{
    private readonly FlowLayoutPanel _flow = new();
    private readonly Label _note = new();
    private readonly Dictionary<string, Label> _valueLabels = new();
    private readonly List<Action> _refreshers = new();

    private SimConfig _cfg = new();
    private Simulation? _sim;

    /// <summary>参数变化后通知外面（需要重算派生属性 / 刷新 UI）。</summary>
    public Action? ConfigChanged;

    public EnvironmentPanel()
    {
        BackColor = Theme.Panel;
        Dock = DockStyle.Fill;

        _note.Dock = DockStyle.Top;
        _note.Height = 44;
        _note.ForeColor = Theme.TextDim;
        _note.Font = Theme.UiFontSmall;
        _note.Padding = new Padding(10, 6, 10, 0);
        _note.Text = "改动立即生效。注意：这些参数改变的是「选择压力」，"
                   + "效果不会马上显现 —— 要等若干代之后才能看到生物的形态和行为跟着变。";

        _flow.Dock = DockStyle.Fill;
        _flow.FlowDirection = FlowDirection.TopDown;
        _flow.WrapContents = false;
        _flow.AutoScroll = true;
        _flow.BackColor = Theme.Background;
        _flow.Padding = new Padding(6);

        Controls.Add(_flow);
        Controls.Add(_note);
    }

    public void Bind(Simulation sim)
    {
        _sim = sim;
        _cfg = sim.Config;
        Rebuild();
    }

    private void Rebuild()
    {
        _flow.SuspendLayout();
        foreach (Control c in _flow.Controls) c.Dispose();
        _flow.Controls.Clear();
        _valueLabels.Clear();
        _refreshers.Clear();

        Section("食物与资源");
        Slider("食物丰饶度", 0.2f, 3.0f, _cfg.FoodRichness, v => _cfg.FoodRichness = v, "F2",
               "直接乘在食物再生速度上。拉高 = 物产丰饶，生物会变大变多。");
        Slider("食物再生速率", 0.0002f, 0.0060f, _cfg.FoodRegen, v => _cfg.FoodRegen = v, "F4",
               "每个地块每 tick 长出的食物量。这是整个模拟最敏感的旋钮。");
        Slider("肥沃地块占比", 0.10f, 0.90f, _cfg.FertileFraction, v => { _cfg.FertileFraction = v; NeedRebuild(); }, "F2",
               "改这个要重开生态箱才生效。实测 0.55 附近捕食最频繁。");
        Slider("光合作用倍率", 0f, 3.0f, _cfg.PhotosynthesisMultiplier, v => _cfg.PhotosynthesisMultiplier = v, "F2",
               "藻胞器官的收益倍率。拉高会让「不用觅食、晒太阳就行」的路线变强。");

        Section("水域（维持多物种共存的关键）");
        Slider("水域占比", 0f, 0.50f, _cfg.WaterFraction, v => { _cfg.WaterFraction = v; NeedRebuild(); }, "F2",
               "水里长满只有带鳃生物能采食的水生植物，切出独立生态位。要重开生效。");
        Slider("水下窒息速率", 0f, 0.10f, _cfg.DrownRate, v => _cfg.DrownRate = v, "F3",
               "无鳃生物在水中的能量流失速度。太低则水域形同虚设，太高则水栖路线无法立足。");
        Slider("水生植物丰饶度", 0f, 2.0f, _cfg.AquaticRichness, v => { _cfg.AquaticRichness = v; NeedRebuild(); }, "F2",
               "水域的食物倍率。要重开生效。");

        Section("代谢与寿命");
        Slider("基础代谢", 0.002f, 0.040f, _cfg.BaseUpkeep, v => { _cfg.BaseUpkeep = v; DerivedRefresh(); }, "F3",
               "所有生物的基础能量消耗。拉高 = 整个世界变得艰难。");
        Slider("运动耗能", 0.0002f, 0.0080f, _cfg.MoveCostPerUnit, v => _cfg.MoveCostPerUnit = v, "F4",
               "移动的开销。拉高会让「跑得快」的代价变大，生物会变慢。");
        Slider("自然寿命", 800f, 12000f, _cfg.Lifespan, v => { _cfg.Lifespan = (int)v; DerivedRefresh(); }, "F0",
               "寿命越长，老个体占着资源越久，演化越慢。缩短寿命能显著加快世代更替。");
        Slider("规则维持成本", 0f, 0.010f, _cfg.RuleUpkeep, v => { _cfg.RuleUpkeep = v; DerivedRefresh(); }, "F4",
               "每条多余规则的每 tick 开销。这是防止基因组膨胀成一堆垃圾规则的闸门。");

        Section("繁殖");
        Slider("繁殖能量阈值基准", 20f, 100f, 72f, _ => { }, "F0", "（当前由器官表决定，此处仅作说明）", enabled: false);
        Slider("繁殖冷却", 10f, 400f, _cfg.ReproCooldown, v => _cfg.ReproCooldown = (int)v, "F0",
               "产下一只后代后要等多少 tick。拉长会放缓种群爆炸的速度。");
        Slider("性状点数预算", 8f, 40f, _cfg.PointBudget, v => _cfg.PointBudget = (int)v, "F0", "15 个性状共享的点数上限，不可超过。调小 = 极端专精，调大 = 全能型重新出现。可以用满也可以用不满 —— 用不满的生物更轻、每 tick 开销更低。");
        Slider("物种分化倍数", 2f, 30f, _cfg.SpeciationFactor, v => _cfg.SpeciationFactor = v, "F1",
               "后代的基因+行为指纹与物种模式标本的距离超过这个值就另立新物种。调低 = 很多细分种，调高 = 少数大类。这是「物种粒度」旋钮。");
        Slider("初始食性宽容度", 0f, 1f, _cfg.InitialCannibalism, v => _cfg.InitialCannibalism = v, "F2",
               "只影响新出生的生物。0 = 有亲缘识别、不碰同类；1 = 谁都能吃。它本身也会演化。");
        Slider("食性宽容开销", 0f, 0.02f, _cfg.CannibalismUpkeep, v => _cfg.CannibalismUpkeep = v, "F4", "食性宽容度每 tick 的开销（乘以宽容度）。没有它，宽容度会无成本涨到 1.0，全世界变成人人相食。");
        Slider("亲缘容忍上限(bit)", 4f, 30f, _cfg.KinToleranceMaxBits, v => _cfg.KinToleranceMaxBits = (int)v, "F0",
               "行为签名允许相差多少 bit 还算同类。签名两两平均差约 20 bit，超过 16 就等于谁都算同类、捕食会停摆。");
        Slider("初始突变率", 0.02f, 1.0f, _cfg.InitialMutationRate, v => _cfg.InitialMutationRate = v, "F3",
               "只影响新出生的生物。注意突变率本身也会演化，所以它多半会自己降下来。");

        Section("搏斗");
        Slider("扑咬加速倍率", 0.8f, 3.5f, _cfg.AttackLungeMultiplier, v => _cfg.AttackLungeMultiplier = v, "F2",
               "追击时的加速。低于 1.0 捕食者永远追不上猎物，捕食行为会彻底消失。");
        Slider("捕食收益（按体重）", 0f, 45f, _cfg.PredationFlatBonus, v => _cfg.PredationFlatBonus = v, "F1",
               "击杀的固定奖励。太低则没人愿意当捕食者，太高则捕食者会吃绝猎物然后自己饿死。");
        Slider("夺取猎物能量比例", 0f, 1.5f, _cfg.PredationGain, v => _cfg.PredationGain = v, "F2", "");
        Slider("攻击距离系数", 0.7f, 2.5f, _cfg.AttackReachFactor, v => _cfg.AttackReachFactor = v, "F2", "攻击距离 = 双方体径之和 × 这个系数。大块头因此天然有更长的攻击范围。");

        Section("世界规模（改动需重开生态箱）");
        Slider("食物格边长", 6f, 40f, _cfg.TileSize, v => { _cfg.TileSize = v; NeedRebuild(); }, "F0", "");
        Slider("人口硬上限", 200f, 3000f, _cfg.MaxPopulation, v => _cfg.MaxPopulation = (int)v, "F0",
               "纯粹是性能保护，不是生态参数。正常情况下种群应该远低于它。");

        _flow.ResumeLayout();
        ResizeAllCards();
    }

    private bool _needRebuild;

    private void NeedRebuild()
    {
        _needRebuild = true;
        ConfigChanged?.Invoke();
    }

    private void DerivedRefresh()
    {
        _sim?.RefreshDerived();
        ConfigChanged?.Invoke();
    }

    private void Section(string title)
    {
        var lbl = new Label
        {
            Text = title,
            Font = Theme.UiFontBold,
            ForeColor = Theme.Accent,
            AutoSize = false,
            Height = 26,
            Width = 400,
            Padding = new Padding(4, 8, 0, 0),
            Margin = new Padding(0, 6, 0, 2),
        };
        _flow.Controls.Add(lbl);
    }

    private void Slider(string name, float min, float max, float value, Action<float> apply,
                        string format, string tip, bool enabled = true)
    {
        var card = new Panel { BackColor = Theme.Panel, Height = 46, Margin = new Padding(0, 0, 0, 4) };
        _flow.Controls.Add(card);

        var nameLbl = new Label
        {
            Text = name,
            ForeColor = enabled ? Theme.Text : Theme.TextDim,
            Font = Theme.UiFontSmall,
            AutoSize = false,
            Location = new Point(8, 4),
            Height = 16,
            Width = 200,
        };
        var valLbl = new Label
        {
            Text = value.ToString(format),
            ForeColor = Theme.AccentWarm,
            Font = Theme.MonoFont,
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleRight,
            Location = new Point(210, 4),
            Height = 16,
            Width = 70,
        };

        var bar = new TrackBar
        {
            Minimum = 0,
            Maximum = 1000,
            TickFrequency = 200,
            SmallChange = 5,
            LargeChange = 50,
            Value = ToSlider(value, min, max),
            Location = new Point(4, 20),
            Height = 24,
            AutoSize = false,
            Enabled = enabled,
            BackColor = Theme.Panel,
        };
        bar.ValueChanged += (_, _) =>
        {
            float v = FromSlider(bar.Value, min, max);
            apply(v);
            valLbl.Text = v.ToString(format);
            if (!_needRebuild) ConfigChanged?.Invoke();
        };

        card.Controls.Add(nameLbl);
        card.Controls.Add(valLbl);
        card.Controls.Add(bar);

        if (!string.IsNullOrEmpty(tip))
        {
            var tt = new ToolTip();
            tt.SetToolTip(nameLbl, tip);
            tt.SetToolTip(card, tip);
        }

        _refreshers.Add(() =>
        {
            nameLbl.Width = Math.Max(120, card.Width - 110);
            valLbl.Location = new Point(card.Width - 78, 4);
            bar.Width = Math.Max(80, card.Width - 16);
        });
    }

    private static int ToSlider(float v, float min, float max)
        => (int)Math.Clamp(MathF.Round((v - min) / MathF.Max(1e-9f, max - min) * 1000f), 0, 1000);

    private static float FromSlider(int i, float min, float max)
        => min + (max - min) * (i / 1000f);

    private void ResizeAllCards()
    {
        foreach (Control c in _flow.Controls) c.Width = Math.Max(280, _flow.ClientSize.Width - 26);
        foreach (var r in _refreshers) r();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        ResizeAllCards();
    }
}
