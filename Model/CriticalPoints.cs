using Epsilon.LinearAlgebra;

namespace VectorField.Model;

public enum CriticalKind { Saddle, StableNode, UnstableNode, StableFocus, UnstableFocus, Center, Degenerate }

public readonly record struct Eigenvalue(double Re, double Im);

public readonly record struct CriticalPoint(Vector2<double> Position, CriticalKind Kind, Matrix2x2<double> Jacobian,
    Eigenvalue First, Eigenvalue Second)
{
    public bool HasRealEigenvalues => First.Im == 0;

    public double Trace => Jacobian.Trace();

    public double Determinant => Jacobian.Determinant();

    public string Name => Kind switch
    {
        CriticalKind.Saddle => "saddle",
        CriticalKind.StableNode => "stable node",
        CriticalKind.UnstableNode => "unstable node",
        CriticalKind.StableFocus => "stable focus",
        CriticalKind.UnstableFocus => "unstable focus",
        CriticalKind.Center => "center",
        _ => "degenerate",
    };

    public Vector2<double>? Eigenvector(double lambda)
    {
        var j = Jacobian;
        var first = new Vector2<double>(j[0, 1], lambda - j[0, 0]);
        var second = new Vector2<double>(lambda - j[1, 1], j[1, 0]);
        var v = first.NormSquared() >= second.NormSquared() ? first : second;
        double scale = Math.Max(Math.Abs(lambda), Math.Max(Math.Abs(j[0, 0]) + Math.Abs(j[0, 1]), Math.Abs(j[1, 0]) + Math.Abs(j[1, 1])));
        return v.Norm() > 1e-9 * scale ? v.Normalize() : null;
    }
}

public sealed class CriticalPointFinder
{
    const int SeedColumns = 20, SeedRows = 12, MaxIterations = 40, MaxPoints = 48;

    readonly List<CriticalPoint> points = [];

    public IReadOnlyList<CriticalPoint> Points => points;

    public bool Available { get; private set; }

    public bool NotIsolated { get; private set; }

    public void Find(FieldDefinition field, Camera2D camera, double time, double magnitudeScale)
    {
        points.Clear();
        NotIsolated = false;
        Available = field.Jacobian is not null;
        if (!Available) return;

        var min = camera.Min;
        var size = camera.Max - min;
        double merge = camera.UnitsPerPixel * 3;
        double tolerance = 1e-10 * magnitudeScale;
        double maxStep = size.Norm() * 0.25;

        for (int j = 0; j < SeedRows; j++)
        {
            for (int i = 0; i < SeedColumns; i++)
            {
                var seed = min + new Vector2<double>((i + 0.5) / SeedColumns * size.X, (j + 0.5) / SeedRows * size.Y);
                if (!Newton(field, seed, time, tolerance, maxStep, camera, out var point, out var jacobian)) continue;
                if (!camera.Contains(point, 0)) continue;
                if (points.Exists(p => p.Position.Distance(point) < merge)) continue;
                if (points.Count == MaxPoints)
                {
                    points.Clear();
                    NotIsolated = true;
                    return;
                }
                points.Add(Classify(point, jacobian));
            }
        }
    }

    static bool Newton(FieldDefinition field, Vector2<double> seed, double time, double tolerance, double maxStep,
        Camera2D camera, out Vector2<double> point, out Matrix2x2<double> jacobian)
    {
        point = seed;
        for (int k = 0; k < MaxIterations; k++)
        {
            if (!field.Linearize(point, time, out var value, out jacobian)) return false;
            double residual = value.Norm();
            if (!double.IsFinite(residual) || !IsFinite(jacobian)) return false;
            if (residual <= tolerance) return true;
            if (!jacobian.TryInverse(out var inverse)) return false;

            var step = inverse * value;
            double length = step.Norm();
            if (!double.IsFinite(length)) return false;
            if (length > maxStep) step *= maxStep / length;
            point -= step;
            if (!camera.Contains(point, 0.5)) return false;
        }
        jacobian = default;
        return false;
    }

    static CriticalPoint Classify(Vector2<double> point, Matrix2x2<double> j)
    {
        double trace = j.Trace(), determinant = j.Determinant();
        double scale = Math.Max(Math.Max(Math.Abs(j[0, 0]), Math.Abs(j[0, 1])), Math.Max(Math.Abs(j[1, 0]), Math.Abs(j[1, 1])));
        double tiny = 1e-9 * scale;
        double half = trace / 2;
        double discriminant = half * half - determinant;
        if (discriminant < 0 && -discriminant <= tiny * scale) discriminant = 0;

        Eigenvalue first, second;
        if (discriminant >= 0)
        {
            double root = Math.Sqrt(discriminant);
            first = new Eigenvalue(half + root, 0);
            second = new Eigenvalue(half - root, 0);
        }
        else
        {
            double root = Math.Sqrt(-discriminant);
            first = new Eigenvalue(half, root);
            second = new Eigenvalue(half, -root);
        }

        CriticalKind kind =
            scale == 0 || Math.Abs(determinant) <= tiny * scale ? CriticalKind.Degenerate
            : determinant < 0 ? CriticalKind.Saddle
            : Math.Abs(trace) <= tiny ? CriticalKind.Center
            : discriminant >= 0 ? (trace < 0 ? CriticalKind.StableNode : CriticalKind.UnstableNode)
            : trace < 0 ? CriticalKind.StableFocus : CriticalKind.UnstableFocus;

        return new CriticalPoint(point, kind, j, first, second);
    }

    static bool IsFinite(Matrix2x2<double> m) =>
        double.IsFinite(m[0, 0]) && double.IsFinite(m[0, 1]) && double.IsFinite(m[1, 0]) && double.IsFinite(m[1, 1]);
}
