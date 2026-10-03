namespace Terrarium.Core;

/// <summary>
/// 物种配色。
///
/// 这里刻意**不用**基因组里那个会漂移的 Hue 基因：
/// 那条色相是独立变异的，结果就是同一物种颜色散开、不同物种可能撞色，
/// 玩家根本没法靠颜色分辨谁是谁（这是实测反馈里"看不出区别"的一部分原因）。
///
/// 改成按物种序号用黄金角展开：相邻序号拿到的色相在圆上尽可能互相远离，
/// 而且只要物种 Id 不变，颜色就永远稳定 —— 你能看着一个物种从头到尾是同一个颜色。
/// </summary>
public static class SpeciesPalette
{
    /// <summary>黄金角：把连续序号映射成在色环上互相远离的色相。</summary>
    private const float GoldenAngle = 0.6180339887f;

    public static float HueFor(int speciesId)
        => (speciesId * GoldenAngle) % 1f;
}
