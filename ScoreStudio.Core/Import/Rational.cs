namespace ScoreStudio.Import;

/// <summary>
/// An exact fraction, always reduced and with a positive denominator. Used for musical time so that
/// triplets and other tuplets stay exact (1/3 of a quarter note is not a floating-point approximation).
/// </summary>
public readonly record struct Rational : IComparable<Rational>
{
    public Rational(long numerator, long denominator)
    {
        if (denominator == 0) throw new DivideByZeroException();
        if (denominator < 0) (numerator, denominator) = (-numerator, -denominator);
        var gcd = Gcd(Math.Abs(numerator), denominator);
        Numerator = numerator / gcd;
        Denominator = denominator / gcd;
    }

    public long Numerator { get; }
    public long Denominator { get; }

    public static Rational Zero => new(0, 1);

    public int Sign => Math.Sign(Numerator);

    public static Rational operator +(Rational a, Rational b) =>
        new(checked(a.Numerator * b.Denominator + b.Numerator * a.Denominator), checked(a.Denominator * b.Denominator));

    public static Rational operator -(Rational a, Rational b) =>
        new(checked(a.Numerator * b.Denominator - b.Numerator * a.Denominator), checked(a.Denominator * b.Denominator));

    public static bool operator <(Rational a, Rational b) => (a - b).Sign < 0;
    public static bool operator >(Rational a, Rational b) => (a - b).Sign > 0;

    public static bool operator <=(Rational a, Rational b) => (a - b).Sign <= 0;
    public static bool operator >=(Rational a, Rational b) => (a - b).Sign >= 0;

    public static Rational Max(Rational a, Rational b) => a > b ? a : b;

    public int CompareTo(Rational other) => (this - other).Sign;

    public double ToDouble() => (double)Numerator / Denominator;

    public override string ToString() => Denominator == 1 ? $"{Numerator}" : $"{Numerator}/{Denominator}";

    private static long Gcd(long a, long b)
    {
        while (b != 0) (a, b) = (b, a % b);
        return a == 0 ? 1 : a;
    }
}
