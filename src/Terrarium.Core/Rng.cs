namespace Terrarium.Core;

/// <summary>
/// 确定性伪随机数发生器 (xorshift128+)。
///
/// 这是整个项目"可复现"的基石：同一种子 + 同一 tick 数 = 完全相同的世界。
/// 不要用 System.Random —— 它的序列在不同 .NET 版本间不保证稳定，
/// 而我们要能把一个演化了几十万 tick 的存档原样复现出来。
/// 非线程安全：只允许模拟线程持有和调用。
/// </summary>
public sealed class Rng
{
    private ulong _s0;
    private ulong _s1;
    private float _spare;
    private bool _hasSpare;

    public Rng(ulong seed)
    {
        ulong z = seed;
        _s0 = SplitMix64(ref z);
        _s1 = SplitMix64(ref z);
        if (_s0 == 0 && _s1 == 0) _s1 = 0x9E3779B97F4A7C15UL;
    }

    private static ulong SplitMix64(ref ulong z)
    {
        z += 0x9E3779B97F4A7C15UL;
        ulong x = z;
        x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
        x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
        return x ^ (x >> 31);
    }

    public ulong NextULong()
    {
        ulong x = _s0;
        ulong y = _s1;
        _s0 = y;
        x ^= x << 23;
        _s1 = x ^ y ^ (x >> 17) ^ (y >> 26);
        return _s1 + y;
    }

    /// <summary>[0,1) 均匀分布。</summary>
    public float NextFloat() => (NextULong() >> 40) * (1.0f / 16777216.0f);

    /// <summary>[a,b) 均匀分布。</summary>
    public float Range(float a, float b) => a + (b - a) * NextFloat();

    /// <summary>[0,max) 整数。</summary>
    public int NextInt(int maxExclusive)
        => maxExclusive <= 0 ? 0 : (int)(NextULong() % (ulong)maxExclusive);

    /// <summary>以概率 p 返回 true。</summary>
    public bool Chance(float p) => NextFloat() < p;

    /// <summary>标准正态分布 (Box-Muller)，用于连续性状的微扰。</summary>
    public float Gaussian()
    {
        if (_hasSpare) { _hasSpare = false; return _spare; }
        float u, v, s;
        do
        {
            u = NextFloat() * 2f - 1f;
            v = NextFloat() * 2f - 1f;
            s = u * u + v * v;
        } while (s >= 1f || s <= 1e-9f);
        float mul = MathF.Sqrt(-2f * MathF.Log(s) / s);
        _spare = v * mul;
        _hasSpare = true;
        return u * mul;
    }

    /// <summary>从数组中随机取一个元素。</summary>
    public T Pick<T>(IReadOnlyList<T> list) => list[NextInt(list.Count)];
}
