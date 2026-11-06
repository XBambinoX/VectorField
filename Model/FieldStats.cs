using Epsilon.LinearAlgebra;

namespace VectorField.Model;

public sealed class FieldStats
{
    const int Columns = 48, Rows = 27;

    readonly double[] magnitudes = new double[Columns * Rows];
    readonly double[] divergences = new double[Columns * Rows];
    readonly double[] curls = new double[Columns * Rows];

    public double Magnitude { get; private set; } = 1;

    public double Divergence { get; private set; } = 1;

    public double Curl { get; private set; } = 1;

    public void Update(FieldDefinition field, Camera2D camera, double time, bool reset)
    {
        var min = camera.Min;
        var size = camera.Max - min;
        int nm = 0, nd = 0, nc = 0;
        for (int j = 0; j < Rows; j++)
        {
            for (int i = 0; i < Columns; i++)
            {
                var p = min + new Vector2<double>((i + 0.5) / Columns * size.X, (j + 0.5) / Rows * size.Y);
                var s = field.Sample(p, time);
                double m = s.Magnitude;
                if (double.IsFinite(m)) magnitudes[nm++] = m;
                if (double.IsFinite(s.Divergence)) divergences[nd++] = Math.Abs(s.Divergence);
                if (double.IsFinite(s.Curl)) curls[nc++] = Math.Abs(s.Curl);
            }
        }

        double blend = reset ? 1 : 0.12;
        Magnitude = Smooth(Magnitude, Percentile(magnitudes, nm, 0.75), blend);

        double floor = NoiseFloor(camera);
        Divergence = Smooth(Divergence, Math.Max(Percentile(divergences, nd, 0.95), floor * 1e3), blend);
        Curl = Smooth(Curl, Math.Max(Percentile(curls, nc, 0.95), floor * 1e3), blend);
    }

    public double NoiseFloor(Camera2D camera) => 1e-9 * Magnitude / camera.ViewHeight;

    static double Smooth(double current, double target, double blend)
    {
        if (!(target > 1e-12)) target = 1e-12;
        return current + (target - current) * blend;
    }

    static double Percentile(double[] values, int count, double q)
    {
        if (count == 0) return 0;
        int k = (int)Math.Clamp(q * (count - 1), 0, count - 1);
        int lo = 0, hi = count - 1;
        while (lo < hi)
        {
            double pivot = values[(lo + hi) >>> 1];
            int i = lo, j = hi;
            while (i <= j)
            {
                while (values[i] < pivot) i++;
                while (values[j] > pivot) j--;
                if (i <= j)
                {
                    (values[i], values[j]) = (values[j], values[i]);
                    i++;
                    j--;
                }
            }
            if (k <= j) hi = j;
            else if (k >= i) lo = i;
            else return values[k];
        }
        return values[k];
    }
}
