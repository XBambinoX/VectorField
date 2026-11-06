using Epsilon.LinearAlgebra;
using VectorField.Model;

namespace VectorField.Simulation;

public sealed class Streamline
{
    readonly List<Vector2<double>> backward = [];

    public List<Vector2<double>> Points { get; } = [];

    public int SeedIndex { get; private set; }

    public void Trace(FieldDefinition field, Vector2<double> seed, double time, double step, int maxSteps, Camera2D camera)
    {
        Points.Clear();
        backward.Clear();

        bool closed = Integrate(field, seed, time, step, maxSteps, camera, Points);
        if (!closed)
            Integrate(field, seed, time, -step, maxSteps, camera, backward);

        backward.Reverse();
        backward.Add(seed);
        SeedIndex = backward.Count - 1;
        Points.InsertRange(0, backward);
    }

    static bool Integrate(FieldDefinition field, Vector2<double> seed, double time, double h, int maxSteps,
        Camera2D camera, List<Vector2<double>> output)
    {
        var p = seed;
        double closeDistance = Math.Abs(h) * 0.75;
        for (int i = 0; i < maxSteps; i++)
        {
            if (!Direction(field, p, time, out var k1)) break;
            if (!Direction(field, p + k1 * (h / 2), time, out var k2)) break;
            if (!Direction(field, p + k2 * (h / 2), time, out var k3)) break;
            if (!Direction(field, p + k3 * h, time, out var k4)) break;
            var next = p + (k1 + 2.0 * k2 + 2.0 * k3 + k4) * (h / 6);

            if (!camera.Contains(next, 0.1)) break;
            output.Add(next);
            if (i > 16 && next.Distance(seed) < closeDistance)
            {
                output.Add(seed);
                return true;
            }
            p = next;
        }
        return false;
    }

    static bool Direction(FieldDefinition field, Vector2<double> p, double time, out Vector2<double> direction)
    {
        var v = field.Velocity(p, time);
        double n = v.Norm();
        if (!(n > 1e-12) || !double.IsFinite(n))
        {
            direction = default;
            return false;
        }
        direction = v / n;
        return true;
    }
}
