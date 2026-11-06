using Epsilon.LinearAlgebra;
using VectorField.Model;

namespace VectorField.Simulation;

public sealed class ParticleSystem
{
    public const int TrailLength = 24;
    const double MinLife = 2.0, MaxLife = 5.0;

    readonly Random random = new(20261105);
    Vector2<double>[] positions = [];
    Vector2<double>[] trails = [];
    int[] trailCounts = [];
    double[] ages = [];
    double[] lives = [];
    float[] speeds = [];
    int head;

    public int Count { get; private set; }

    public int Head => head;

    public ReadOnlySpan<Vector2<double>> Trail(int particle) => trails.AsSpan(particle * TrailLength, TrailLength);
    public int TrailCount(int particle) => trailCounts[particle];
    public float Speed(int particle) => speeds[particle];

    public double Opacity(int particle)
    {
        double age = ages[particle], life = lives[particle];
        return Math.Clamp(age / 0.5, 0, 1) * Math.Clamp((life - age) / 0.8, 0, 1);
    }

    public void Resize(int count, Camera2D camera)
    {
        if (count == Count) return;
        Count = count;
        positions = new Vector2<double>[count];
        trails = new Vector2<double>[count * TrailLength];
        trailCounts = new int[count];
        ages = new double[count];
        lives = new double[count];
        speeds = new float[count];
        for (int i = 0; i < count; i++)
            Spawn(i, camera, staggered: true);
    }

    public void Respawn(Camera2D camera, double fraction = 1)
    {
        for (int i = 0; i < Count; i++)
            if (fraction >= 1 || random.NextDouble() < fraction)
                Spawn(i, camera, staggered: true);
    }

    public void Step(FieldDefinition field, double time, double dt, double baseSpeed, double magnitudeScale, Camera2D camera)
    {
        head = (head + 1) % TrailLength;
        double half = dt / 2;
        double maxSpeed = camera.ViewHeight * 0.9;
        double colorNorm = 1 / Math.Log(1 + 2.0);

        Vector2<double> Velocity(Vector2<double> v)
        {
            double m = v.Norm();
            if (!(m > 0)) return m == 0 ? Vector2<double>.Zero : v;
            return v * (Math.Min(baseSpeed * Math.Sqrt(m / magnitudeScale), maxSpeed) / m);
        }

        for (int i = 0; i < Count; i++)
        {
            ages[i] += dt;
            var p = positions[i];

            var f1 = field.Velocity(p, time);
            var k1 = Velocity(f1);
            var k2 = Velocity(field.Velocity(p + k1 * half, time + half));
            var k3 = Velocity(field.Velocity(p + k2 * half, time + half));
            var k4 = Velocity(field.Velocity(p + k3 * dt, time + dt));
            var next = p + (k1 + 2.0 * k2 + 2.0 * k3 + k4) * (dt / 6);

            double magnitude = f1.Norm();
            if (ages[i] > lives[i] || !double.IsFinite(next.X) || !double.IsFinite(next.Y) || !camera.Contains(next, 0.05))
            {
                Spawn(i, camera, staggered: false);
                continue;
            }

            positions[i] = next;
            trails[i * TrailLength + head] = next;
            if (trailCounts[i] < TrailLength) trailCounts[i]++;
            speeds[i] = (float)Math.Clamp(Math.Log(1 + magnitude / magnitudeScale) * colorNorm, 0, 1);
        }
    }

    void Spawn(int i, Camera2D camera, bool staggered)
    {
        var min = camera.Min;
        var size = camera.Max - min;
        var p = min + new Vector2<double>(random.NextDouble() * size.X, random.NextDouble() * size.Y);
        positions[i] = p;
        trails[i * TrailLength + head] = p;
        trailCounts[i] = 1;
        lives[i] = MinLife + random.NextDouble() * (MaxLife - MinLife);
        ages[i] = staggered ? random.NextDouble() * lives[i] : 0;
        speeds[i] = 0;
    }
}
