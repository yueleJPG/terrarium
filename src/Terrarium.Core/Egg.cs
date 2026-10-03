namespace Terrarium.Core;

/// <summary>
/// 卵：介于"亲代的一次投资"和"一个独立的新个体"之间的东西。
///
/// 为什么它必须是一个**世界实体**而不是一个计时器：
///  - 它固定不动，所以是生态里第一种"不会跑的猎物" —— 这本身就创造了一个新生态位；
///  - 它需要被感知（进空间索引），否则"攻击最近的敌方卵"这种规则无从写起；
///  - 它需要归属（记住亲代的物种与行为签名），因为亲代很可能在它孵化前就死了，
///    但敌友判定不能因此失效。
///
/// 销毁机制刻意做成**踩踏**而不是血量：卵不会反击也不需要被"战斗"，
/// 它只是挡在路上了。能扛几次踩踏由「卵壳强度」这个性状买来 ——
/// 这是卵生路线唯一的防御投资，也是"花点数买什么"的一个真实选项。
/// </summary>
public sealed class Egg
{
    public int Id;
    public Vec2 Pos;

    /// <summary>孵化时交给幼体的起始能量。</summary>
    public float Energy;

    /// <summary>亲代的物种 Id 与行为签名 —— 敌友判定靠它们，而不是靠"亲代还活着"。</summary>
    public int SpeciesId;
    public ulong Signature;
    public int ParentId;

    /// <summary>
    /// 里面那个体的基因组。
    ///
    /// 必须是真家伙，不能靠"物种模式标本"临时重建 —— 第一版就是这么写的，
    /// 结果每一只孵出来的个体都退回祖先，**世代数永远停在 0、突变率永远是原型的 0.35**，
    /// 整个演化信号被抹掉了（实测：4 万 tick 后代数还是 0）。
    ///
    /// 同一窝的卵共享同一个引用，所以内存开销是"每窝一份"而不是"每颗一份"。
    /// </summary>
    public Genome? Genome;

    /// <summary>剩余孵化时间。</summary>
    public int HatchTimer;
    /// <summary>总孵化时间，UI 画进度条用。</summary>
    public int HatchTotal;

    /// <summary>能扛住几次非同族踩踏。由亲代的「卵壳强度」性状决定，不是全局配置。</summary>
    public int TrampleLimit;
    public int TrampleCount;
    /// <summary>两次被踩之间的最小间隔，防止一只生物站在上面一 tick 踩满。</summary>
    public int TrampleCooldown;

    public bool Alive = true;

    public float HatchPct => HatchTotal <= 0 ? 1f : 1f - HatchTimer / (float)HatchTotal;

    /// <summary>剩余能扛几次。</summary>
    public int Toughness => TrampleLimit - TrampleCount;
}
