using System;

namespace SlidecastExtender.Core;

/// <summary>
/// World position. Units are FFXIV world units (1 unit ≈ 1 m); the exact scale relative to
/// the wire encoding (short ticks) is a <c>WirePackets</c> concern.
/// </summary>
public readonly record struct Position(float X, float Y, float Z)
{
    public static readonly Position Origin = new(0f, 0f, 0f);

    public float DistanceTo(Position other) =>
        MathF.Sqrt(
            (X - other.X) * (X - other.X) +
            (Y - other.Y) * (Y - other.Y) +
            (Z - other.Z) * (Z - other.Z));

    public bool IsCloseTo(Position other, float eps = 0.01f) => DistanceTo(other) <= eps;

    public static Position operator +(Position a, Position b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Position operator -(Position a, Position b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static Position operator *(Position a, float s) => new(a.X * s, a.Y * s, a.Z * s);
    public static Position operator *(float s, Position a) => a * s;

    public static Position operator /(Position a, float s) =>
        s == 0f ? Position.Origin : new(a.X / s, a.Y / s, a.Z / s);

    public override string ToString() => $"({X:0.000}, {Y:0.000}, {Z:0.000})";
}
