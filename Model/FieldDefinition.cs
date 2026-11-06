using Epsilon.Calculus;
using Epsilon.Core;
using Epsilon.LinearAlgebra;

namespace VectorField.Model;

public readonly record struct FieldSample(Vector2<double> Value, double Divergence, double Curl)
{
    public double Magnitude => Value.Norm();
}

public sealed class FieldDefinition
{
    static readonly string[] Variables = ["x", "y", "t"];
    static readonly string[] Plane = ["x", "y"];
    static readonly string[] Space = ["x", "y", "z"];

    readonly Action<double[], double[]> velocity;
    readonly Action<double[], double[]> derived;
    readonly Action<double[], double[]>? linearization;
    readonly double[] input = new double[3];
    readonly double[] velocityOut = new double[2];
    readonly double[] derivedOut = new double[4];
    readonly double[] linearizationOut = new double[6];

    public string Source { get; }

    public Vector<Expr> Field { get; }

    public Expr? Divergence { get; }

    public Expr? Curl { get; }

    public Expr? Potential { get; }

    public Matrix<Expr>? Jacobian { get; }

    public Expr? CharacteristicPolynomial { get; }

    public bool DependsOnTime { get; }

    FieldDefinition(string source, Vector<Expr> field, Expr? divergence, Expr? curl, Expr? potential,
        Matrix<Expr>? jacobian, Expr? characteristicPolynomial,
        Action<double[], double[]> velocity, Action<double[], double[]> derived, Action<double[], double[]>? linearization)
    {
        Source = source;
        Field = field;
        Divergence = divergence;
        Curl = curl;
        Potential = potential;
        Jacobian = jacobian;
        CharacteristicPolynomial = characteristicPolynomial;
        DependsOnTime = field.GetVariables().Contains("t");
        this.velocity = velocity;
        this.derived = derived;
        this.linearization = linearization;
    }

    public static FieldDefinition Parse(string text)
    {
        string source = text.Trim();
        if (source.Length == 0)
            throw new FormatException("Type a field such as [-y, x].");
        if (!source.StartsWith('['))
            source = "[" + source + "]";

        Vector<Expr> parsed = Vector<Expr>.Parse(source, Variables);
        if (parsed.Length != 2)
            throw new FormatException($"A plane field has 2 components, this one has {parsed.Length}.");

        Vector<Expr> field = parsed.Simplify(SimplifyMode.Generic);

        Expr? divergence = TryDerive(() => field.Divergence(Plane));
        Expr? curl = TryDerive(() => Vector.Create<Expr>(field[0], field[1], 0).Curl(Space)[2]);
        Expr? potential = curl is not null && IsZero(curl) ? TryDerive(() => field.Potential(Plane)) : null;
        Matrix<Expr>? jacobian = TryDerive(() => field.Jacobian(Plane));
        Expr? characteristic = jacobian is null ? null : TryDerive(() => jacobian.CharacteristicPolynomial("λ"));

        Action<double[], double[]> velocity;
        try
        {
            velocity = Expr.Compile([field[0], field[1]], Variables);
        }
        catch (ArgumentException e)
        {
            throw new FormatException(e.Message, e);
        }

        Action<double[], double[]> derived = Expr.Compile([field[0], field[1], divergence ?? 0, curl ?? 0], Variables);
        Action<double[], double[]>? linearization = jacobian is null
            ? null
            : Expr.Compile([field[0], field[1], jacobian[0, 0], jacobian[0, 1], jacobian[1, 0], jacobian[1, 1]], Variables);

        return new FieldDefinition(source, field, divergence, curl, potential, jacobian, characteristic,
            velocity, derived, linearization);
    }

    public Vector2<double> Velocity(Vector2<double> point, double time)
    {
        input[0] = point.X;
        input[1] = point.Y;
        input[2] = time;
        velocity(input, velocityOut);
        return new Vector2<double>(velocityOut[0], velocityOut[1]);
    }

    public FieldSample Sample(Vector2<double> point, double time)
    {
        input[0] = point.X;
        input[1] = point.Y;
        input[2] = time;
        derived(input, derivedOut);
        return new FieldSample(new Vector2<double>(derivedOut[0], derivedOut[1]),
            Divergence is null ? double.NaN : derivedOut[2],
            Curl is null ? double.NaN : derivedOut[3]);
    }

    public bool Linearize(Vector2<double> point, double time, out Vector2<double> value, out Matrix2x2<double> jacobian)
    {
        if (linearization is null)
        {
            value = default;
            jacobian = default;
            return false;
        }
        input[0] = point.X;
        input[1] = point.Y;
        input[2] = time;
        linearization(input, linearizationOut);
        value = new Vector2<double>(linearizationOut[0], linearizationOut[1]);
        jacobian = new Matrix2x2<double>(linearizationOut[2], linearizationOut[3], linearizationOut[4], linearizationOut[5]);
        return true;
    }

    static bool IsZero(Expr e) => e.Print() == "0";

    static T? TryDerive<T>(Func<T> derive) where T : class
    {
        try
        {
            return derive();
        }
        catch (Exception e) when (e is NotSupportedException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }
}
