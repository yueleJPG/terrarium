namespace Terrarium.Core;

/// <summary>
/// 生物在某一 tick 感知到的世界。由 World 计算好后交给 Creature 决策，
/// 这样"决策"就是纯函数式的：给定感知 → 定行为，便于复现和调试。
/// </summary>
public struct SenseData
{
    public bool FoodFound;
    public float FoodDist;
    public float FoodBearing;
    public float FoodHere;
    public Vec2 NextFoodPos;

    public bool EnemyFound;
    public float EnemyDist;
    public float EnemyBearing;
    public Vec2 NextEnemyPos;
    public int NextEnemyIndex;

    public bool AllyFound;
    public float AllyDist;
    public float AllyBearing;
    public Vec2 NextAllyPos;

    public int AllyCount;
    public int EnemyCount;
    public int NearbyCount;

    // ---- 卵：三档（任意 / 敌方 / 友方），因为规则里这三种写法都有用 ----
    public bool EggFound;
    public float EggDist;
    public Vec2 NextEggPos;
    public int NextEggIndex;

    public bool EnemyEggFound;
    public float EnemyEggDist;
    public Vec2 NextEnemyEggPos;
    public int NextEnemyEggIndex;

    public bool AllyEggFound;
    public float AllyEggDist;
    public Vec2 NextAllyEggPos;
    public int NextAllyEggIndex;

    public int EggCount;
    public int EnemyEggCount;

    // ---- 幼体 ----
    public bool JuvenileFound;
    public float JuvenileDist;
    public Vec2 NextJuvenilePos;

    public bool EnemyJuvenileFound;
    public float EnemyJuvenileDist;
    public Vec2 NextEnemyJuvenilePos;

    public bool AllyJuvenileFound;
    public float AllyJuvenileDist;
    public Vec2 NextAllyJuvenilePos;

    public int JuvenileCount;

    /// <summary>最近的同物种个体（只看物种 Id，不看行为签名）。</summary>
    public bool SameSpeciesFound;
    public float SameSpeciesDist;
    public Vec2 NextSameSpeciesPos;

    public float Light;
    public bool InWater;
    public bool InBush;
    public bool AtEdge;
    public float Noise;

    /// <summary>地形遮蔽后的有效视野半径（灌木丛里会砍半）。所有距离传感器都按它归一化。</summary>
    public float EffectiveVision;
    /// <summary>正前方到最近不可通行地形的距离，归一化到 0..1（1 = 前方畅通）。</summary>
    public float ObstacleAhead;
    /// <summary>脚下地形的移动代价。</summary>
    public float TerrainCost;
    /// <summary>被不可通行地形围困的程度 0..1。</summary>
    public float EscapePressure;
}

/// <summary>
/// 一个生物个体。只持有状态和派生属性，不含世界逻辑 ——
/// 所有与世界交互的行为都由 World 执行，保证 tick 顺序完全确定。
/// </summary>
public sealed class Creature
{
    public int Id;
    public Vec2 Pos;
    public float Heading;

    public float Energy;
    public float Hp;
    public float Age;
    public float Poison;

    public int SpeciesId;
    public int ReproTimer;

    public Genome G = new();

    public bool Alive = true;
    public string CauseOfDeath = "";

    /// <summary>上一 tick 触发的规则下标，UI 要用来显示"它此刻在想什么"。</summary>
    public int LastRule = -1;

    // ---------------- 派生属性（由 RecalcDerived 计算） ----------------
    public float Mass;
    public float MaxSpeed;
    public float Vision;
    public float Attack;
    public float Armor;
    public float Upkeep;
    public float MaxEnergy;
    public float MaxHp;
    public float ReproThreshold;
    public float BiteRate;
    public float DigestEff;
    public float AlgaeGain;
    public float SpineReflect;
    public float Hue;
    public float SizeScale;

    /// <summary>
    /// 身体半径（世界单位）。
    ///
    /// 刻意把体重的影响放大：体重 1.3 → 半径约 8；体重 6.5 → 半径约 21，差 2.6 倍。
    /// 之前半径只有 5–11，在全景视角下折合 2.5–5 像素，"谁大谁小"根本看不出来 ——
    /// 这是玩家反馈"外观和体型上没有差距"的最直接原因。
    /// 顺带让身体占据真实面积，为碰撞体积留出物理意义。
    /// </summary>
    public float BodyRadius;
    /// <summary>成体体径。幼体在此基础上按成熟度缩小 —— 画面上和碰撞上都要小一号。</summary>
    public float BaseBodyRadius;
    /// <summary>攻击能够到的距离，由双方体径决定（+ 一点余量）。</summary>
    public float AttackReachSelf;

    // ---------------- 生活史（由点数买来的性状推导） ----------------
    /// <summary>自然寿命上限（tick）。由「寿命」性状决定，不再是全局常量。</summary>
    public int Lifespan = 3600;
    /// <summary>两次产卵之间的冷却（tick）。由「繁殖间隔」性状决定。</summary>
    public int ReproInterval = 80;
    /// <summary>成熟所需的年龄（tick）。由「幼体生命力」性状决定。</summary>
    public float MaturityAge = 700f;
    /// <summary>刚孵化时的属性保留比例。由「幼体生命力」性状决定。</summary>
    public float JuvenileFloor = 0.25f;
    /// <summary>一次产几颗卵。由「窝卵数」性状决定。</summary>
    public int ClutchSize = 1;
    /// <summary>产的卵能扛几次非同族踩踏。由「卵壳强度」性状决定。</summary>
    public int EggTrampleLimit = 1;

    /// <summary>成熟度 0..1。0 = 刚孵化，1 = 成体。每 tick 更新一次。</summary>
    public float Maturity = 1f;
    /// <summary>成体每 tick 开销。幼体开销按成熟度在它与 JuvenileUpkeepFactor 之间插值。</summary>
    public float BaseUpkeep;

    /// <summary>还没成年。</summary>
    public bool IsJuvenile => Maturity < 1f;

    /// <summary>64 位行为签名（从基因组复制）。捕食判定用的"算不算同类"就是拿它做 XOR + popcount。</summary>
    public ulong Signature;

    /// <summary>
    /// 能容忍多少 bit 的行为差异还算同类。由 <see cref="Genome.Cannibalism"/> 换算。
    /// 0 = 只有行为完全一致才算同类（谁都能吃）；越大越宽容（越和平）。
    /// </summary>
    public int KinThresholdBits;

    /// <summary>
    /// 自己的指纹向量（长度 = GenomeFingerprint.Dims）。
    ///
    /// 存一份是为了算「突变步长」—— 子代与父代指纹的距离。
    /// 物种分化阈值要以突变步长为基准缩放，而**不能**用"子代 vs 物种中心"，
    /// 因为后者会随物种年龄增长，形成负反馈把分化彻底锁死（实测过：只剩 1 个物种）。
    /// 代价约 500 字节/个体，几千个体也就是 1MB 出头。
    /// </summary>
    public float[] Fingerprint = Array.Empty<float>();

    /// <summary>与所属物种模式标本的指纹距离。UI 用它显示"这个个体偏离物种多远"。</summary>
    public float SpeciesDivergence;

    // ---------------- 寻路缓存 ----------------
    /// <summary>缓存的绕行路点序列（起点→终点方向）。懒分配：大多数生物从不寻路。</summary>
    public Vec2[]? PathBuf;
    public int PathCount;
    public int PathIndex;
    /// <summary>缓存的剩余有效期。避免同一个目标每 tick 重算 A*。</summary>
    public int PathTimer;
    /// <summary>上次寻路时想去的目标点，目标没变且缓存没过期就复用整段路径。</summary>
    public Vec2 PathGoal;

    public bool HasWaypoint => PathBuf is not null && PathIndex < PathCount;

    /// <summary>本 tick 的意图（仅第一个动作，用于 UI 显示"它此刻在做什么"）。</summary>
    public ActionId Intent = ActionId.Idle;
    public float IntentParam;
    public int IntentOrgan;

    public void RecalcDerived(SimConfig cfg)
    {
        float mass = 1f;
        for (int i = 0; i < OrganTable.Count; i++)
            mass += G.Organs[i] * OrganTable.Mass[i];
        Mass = mass;

        int legs = G.Organs[(int)OrganType.Legs];
        int eye = G.Organs[(int)OrganType.Eye];
        int fang = G.Organs[(int)OrganType.Fang];
        int plate = G.Organs[(int)OrganType.Plate];
        int stomach = G.Organs[(int)OrganType.Stomach];
        int algae = G.Organs[(int)OrganType.Algae];
        int spine = G.Organs[(int)OrganType.Spine];

        // ------------------------------------------------------------------
        // 器官的"零级基线"必须很差，否则不带器官就是免费的最优解。
        //
        // 第一版的基线是：视野 45、速度 1.0、能量上限 70 —— 这些其实完全够用，
        // 于是演化一路退化成"什么器官都不长"的极简生物（实测过：体重 1.18、
        // 只剩 眼0.5、牙 0.00），世界变成一个毫无区分的单质。
        //
        // 真实生物拿掉器官是会残疾的，不是省钱的。把零级基线压到几乎不可用之后，
        // 演化**必须**投资器官，而器官开销是超线性的 —— 于是"要什么"成了真正的取舍，
        // 这才谈得上生态位。
        // ------------------------------------------------------------------
        float speedBase = 0.40f + legs * 0.95f;     // 无腿 0.40：几乎挪不动
        MaxSpeed = speedBase * (2.2f / (1.0f + mass));

        Vision = 14f + eye * 46f;                   // 无眼 14 单位：近似全盲
        Attack = fang * 3.2f;
        Armor = MathF.Min(0.45f, plate * 0.09f);
        SpineReflect = spine * 0.10f;

        MaxEnergy = 28f + stomach * 20f;            // 无胃 28：一次进食就撑爆
        MaxHp = 22f + mass * 7f + plate * 5f;

        float organUpkeep = 0f;
        for (int i = 0; i < OrganTable.Count; i++)
            organUpkeep += OrganTable.UpkeepFor(i, G.Organs[i]);

        // 生活史性状也要养着 —— 活得久、生得勤、卵壳硬，全都是要花能量的。
        // 如果它们免费，"把点数花在繁殖上"就会变成无脑最优解。
        float lifeUpkeep = 0f;
        for (int i = 0; i < LifeTable.Count; i++)
            lifeUpkeep += LifeTable.UpkeepFor(i, G.Life[i]);

        // 神经系统的维持成本：规则越多越费能量。
        // 没有这一项，规则数会无成本地膨胀成一堆永不触发的垃圾。
        int extraRules = Math.Max(0, G.Rules.Count - cfg.RuleFreeAllowance);
        float brainUpkeep = extraRules * cfg.RuleUpkeep;

        // 食性宽容度：谁都能吃显然更划算，所以必须给它一项开销，
        // 否则宽容度会无成本涨到 1.0、全世界变成人人相食的绞肉机、亲缘结构彻底消失。
        // 生物学上也站得住：广食性需要更强的消化系统与免疫系统。
        float cannibalismUpkeep = G.Cannibalism * cfg.CannibalismUpkeep;

        // 基础代谢随体重上升，但不完全线性 ——
        // 这让"大型化"有收益也有代价，而不是单纯的大就是好。
        Upkeep = cfg.BaseUpkeep * (0.5f + 0.5f * MathF.Pow(mass, 1.35f))
               + organUpkeep + lifeUpkeep + brainUpkeep + cannibalismUpkeep;

        // 繁殖阈值必须低于能量上限，否则生物永远无法繁殖（这是最容易犯的错）。
        // 胃抬高上限 → 更容易攒够；生殖腺直接压低阈值 → 这是"繁殖路线"的核心投资。
        ReproThreshold = MathF.Max(24f, 60f - G.Organs[(int)OrganType.Gonad] * 8f);
        BiteRate = 0.15f + stomach * 0.075f;        // 无胃 0.15：吃得极慢
        DigestEff = 1.0f + stomach * 0.11f;
        AlgaeGain = algae * 0.0050f * (0.8f + 0.2f * mass) * cfg.PhotosynthesisMultiplier;

        // 体径：体重的影响被刻意放大，让"大块头"和"小不点"一眼能分出来。
        BaseBodyRadius = (4.5f + 1.7f * MathF.Min(mass, 13f)) * Math.Clamp(G.SizeScale, 0.65f, 1.55f);
        BodyRadius = BaseBodyRadius;
        AttackReachSelf = BodyRadius * cfg.AttackReachFactor;

        // ==================================================================
        // 生活史性状 —— 这些以前是全局死数，现在是被点数买来的
        // ==================================================================
        int tLife = G.Life[(int)LifeTrait.Lifespan];
        int tRepro = G.Life[(int)LifeTrait.ReproInterval];
        int tJuv = G.Life[(int)LifeTrait.JuvenileVitality];
        int tClutch = G.Life[(int)LifeTrait.ClutchSize];
        int tShell = G.Life[(int)LifeTrait.EggShell];

        Lifespan = LifeTable.LifespanFor(tLife, cfg.Lifespan);
        ReproInterval = LifeTable.ReproIntervalFor(tRepro, cfg.ReproCooldown);
        MaturityAge = MathF.Max(60f, cfg.MaturityTicks * LifeTable.MaturityTimeFor(tJuv));
        JuvenileFloor = LifeTable.JuvenileFloorFor(tJuv);
        ClutchSize = LifeTable.ClutchFor(tClutch);
        EggTrampleLimit = LifeTable.EggTrampleLimitFor(tShell);

        // 幼体的基础代谢更低（小身体），但成熟之后恢复全额。
        // 这一项在 Metabolize 里按当前成熟度插值，不在这里定死。
        BaseUpkeep = Upkeep;
        Upkeep *= cfg.JuvenileUpkeepFactor + (1f - cfg.JuvenileUpkeepFactor) * Maturity;

        // 亲缘容忍度：食性宽容度越高，能算作"同类因而不吃"的范围越小。
        // 上限取 cfg.KinToleranceMaxBits —— 经验上行为签名两两相差约 20 bit，
        // 阈值超过 ~16 就等于"谁都算同类"，捕食会彻底停摆。
        KinThresholdBits = (int)MathF.Round((1f - Math.Clamp(G.Cannibalism, 0f, 1f)) * cfg.KinToleranceMaxBits);
        Signature = G.Signature;

        Hue = G.Hue;
        SizeScale = G.SizeScale;

        if (Energy > MaxEnergy) Energy = MaxEnergy;
        if (Hp > MaxHp) Hp = MaxHp;
    }

    public float EnergyPct => MaxEnergy <= 0f ? 0f : Energy / MaxEnergy;
    public float HealthPct => MaxHp <= 0f ? 0f : Hp / MaxHp;

    /// <summary>综合搏斗能力，归一化到 0..1，供规则判断"打不打得过"。</summary>
    public float CombatPower
        => Math.Clamp((Attack + SpineReflect * 12f + Mass * 1.5f) / 22f, 0f, 1f);

    /// <summary>读取一个传感器的当前值。全部归一化到统一尺度，变异才有意义。</summary>
    public float ReadSensor(SensorId s, in SenseData d, int tick, SimConfig cfg)
    {
        switch (s)
        {
            case SensorId.Always: return 1f;
            case SensorId.EnergyPct: return Math.Clamp(EnergyPct, 0f, 1f);
            case SensorId.HealthPct: return Math.Clamp(HealthPct, 0f, 1f);
            case SensorId.AgePct: return Math.Clamp(Age / cfg.Lifespan, 0f, 1f);

            case SensorId.FoodDistance:
                return d.FoodFound ? Math.Clamp(d.FoodDist / MathF.Max(1f, d.EffectiveVision), 0f, 1f) : 1f;
            case SensorId.FoodBearing: return d.FoodFound ? d.FoodBearing : 0f;

            case SensorId.EnemyDistance:
                return d.EnemyFound ? Math.Clamp(d.EnemyDist / MathF.Max(1f, d.EffectiveVision), 0f, 1f) : 1f;
            case SensorId.EnemyBearing: return d.EnemyFound ? d.EnemyBearing : 0f;

            case SensorId.AllyCount: return Math.Clamp(d.AllyCount / 10f, 0f, 1f);
            case SensorId.Threat: return Math.Clamp(d.EnemyCount / 10f, 0f, 1f);
            case SensorId.Crowding: return Math.Clamp(d.NearbyCount / 20f, 0f, 1f);
            case SensorId.CombatPower: return CombatPower;

            case SensorId.FoodHere: return Math.Clamp(d.FoodHere, 0f, 1f);
            case SensorId.InWater: return d.InWater ? 1f : 0f;
            case SensorId.HasGill: return G.Organs[(int)OrganType.Gill] > 0 ? 1f : 0f;
            case SensorId.Poisoned: return Poison > 0.05f ? 1f : 0f;
            case SensorId.AtEdge: return d.AtEdge ? 1f : 0f;
            case SensorId.Noise: return d.Noise;
            case SensorId.Mass: return Math.Clamp(Mass / 4f, 0f, 1f);
            case SensorId.DayPhase: return d.Light;

            case SensorId.ObstacleAhead: return Math.Clamp(d.ObstacleAhead, 0f, 1f);
            case SensorId.TerrainCost: return Math.Clamp((d.TerrainCost - 1f) / 1.2f, 0f, 1f);
            case SensorId.InBush: return d.InBush ? 1f : 0f;
            case SensorId.EscapePressure: return Math.Clamp(d.EscapePressure, 0f, 1f);

            // ---- 卵与幼体 ----
            case SensorId.EggDistance: return SensorTable.Dist01(d.EggFound, d.EggDist, d.EffectiveVision);
            case SensorId.EnemyEggDistance: return SensorTable.Dist01(d.EnemyEggFound, d.EnemyEggDist, d.EffectiveVision);
            case SensorId.AllyEggDistance: return SensorTable.Dist01(d.AllyEggFound, d.AllyEggDist, d.EffectiveVision);
            case SensorId.EggCount: return Math.Clamp(d.EggCount / 10f, 0f, 1f);
            case SensorId.EnemyEggCount: return Math.Clamp(d.EnemyEggCount / 10f, 0f, 1f);
            case SensorId.JuvenileCount: return Math.Clamp(d.JuvenileCount / 10f, 0f, 1f);
            case SensorId.Maturity: return Math.Clamp(Maturity, 0f, 1f);
            case SensorId.IsJuvenile: return IsJuvenile ? 1f : 0f;
            case SensorId.LayReady: return ReproTimer <= 0 ? 1f : 1f - ReproTimer / (float)Math.Max(1, ReproInterval);

            default: return 0f;
        }
    }

    /// <summary>
    /// 决策：从上往下求值，第一条条件全部满足的规则触发。
    /// 返回触发的规则下标（-1 表示没有任何规则匹配，生物将静止）。
    /// 注意：只负责"选中哪条规则"，具体动作由 World 按规则里的动作列表逐个执行。
    /// </summary>
    public int Think(in SenseData d, int tick, SimConfig cfg)
    {
        IntentOrgan = 0;
        IntentParam = 0f;

        var rules = G.Rules;
        for (int r = 0; r < rules.Count; r++)
        {
            Rule rule = rules[r];
            bool all = true;
            for (int c = 0; c < rule.If.Count; c++)
            {
                Condition cond = rule.If[c];
                float v = ReadSensor(cond.Sensor, d, tick, cfg);
                if (!cond.Eval(v)) { all = false; break; }
            }
            if (!all) continue;

            LastRule = r;
            if (rule.Then.Count > 0)
            {
                Intent = rule.Then[0].Action;
                IntentOrgan = rule.Then[0].Organ;
                IntentParam = rule.Then[0].Param;
            }
            else
            {
                Intent = ActionId.Idle;
            }
            return r;
        }

        LastRule = -1;
        Intent = ActionId.Idle;
        return -1;
    }

    /// <summary>取出生效的规则对象（可能为空）。</summary>
    public Rule? ActiveRule
        => LastRule >= 0 && LastRule < G.Rules.Count ? G.Rules[LastRule] : null;

    /// <summary>供 UI 使用：当前意图的 aim 选择器。</summary>
    public AimId IntentAim
    {
        get
        {
            var r = ActiveRule;
            return r != null && r.Then.Count > 0 ? r.Then[0].Aim : AimId.Forward;
        }
    }
}
