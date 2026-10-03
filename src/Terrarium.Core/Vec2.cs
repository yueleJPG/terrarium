namespace Terrarium.Core;

/// <summary>轻量 2D 向量。刻意用 struct + float，避免堆分配。</summary>
public struct Vec2
{
    public float X;
    public float Y;

    public Vec2(float x, float y) { X = x; Y = y; }

    public static readonly Vec2 Zero = new(0f, 0f);

    public static Vec2 operator +(Vec2 a, Vec2 b) => new(a.X + b.X, a.Y + b.Y);
    public static Vec2 operator -(Vec2 a, Vec2 b) => new(a.X - b.X, a.Y - b.Y);
    public static Vec2 operator *(Vec2 a, float s) => new(a.X * s, a.Y * s);
    public static Vec2 operator *(float s, Vec2 a) => new(a.X * s, a.Y * s);
    public static Vec2 operator /(Vec2 a, float s) => new(a.X / s, a.Y / s);

    public float LengthSq => X * X + Y * Y;
    public float Length => MathF.Sqrt(X * X + Y * Y);

    public Vec2 Normalized()
    {
        float l = Length;
        return l < 1e-6f ? Zero : new Vec2(X / l, Y / l);
    }

    public static float Dist(Vec2 a, Vec2 b)
    {
        float dx = a.X - b.X, dy = a.Y - b.Y;
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    public static float DistSq(Vec2 a, Vec2 b)
    {
        float dx = a.X - b.X, dy = a.Y - b.Y;
        return dx * dx + dy * dy;
    }

    public static Vec2 FromAngle(float radians) => new(MathF.Cos(radians), MathF.Sin(radians));

    public float Angle => MathF.Atan2(Y, X);

    /// <summary>把弧度归一化到 (-pi, pi]。</summary>
    public static float WrapAngle(float a)
    {
        while (a > MathF.PI) a -= MathF.Tau;
        while (a <= -MathF.PI) a += MathF.Tau;
        return a;
    }

    public override string ToString() => $"({X:F1},{Y:F1})";
}
