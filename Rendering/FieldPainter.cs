using Epsilon.LinearAlgebra;
using VectorField.Model;
using VectorField.Simulation;
using VectorField.Ui;

namespace VectorField.Rendering;

public enum HeatmapMode { Off, Magnitude, Divergence, Curl }

public sealed class FieldPainter
{
    readonly Vector2<double>[] trailPoints = new Vector2<double>[ParticleSystem.TrailLength];
    readonly float[] trailWidths = new float[ParticleSystem.TrailLength];
    readonly uint[] trailColors = new uint[ParticleSystem.TrailLength];
    readonly List<uint> meshRow = [];
    readonly List<(double X, double Y, double W, double H)> occupied = [];

    public void Heatmap(Batch batch, FieldDefinition field, Camera2D camera, double time, HeatmapMode mode, FieldStats stats, double cellPixels)
    {
        if (mode == HeatmapMode.Off) return;
        var min = camera.Min;
        var size = camera.Max - min;
        int columns = Math.Max(2, (int)Math.Ceiling(camera.Viewport.X / cellPixels));
        int rows = Math.Max(2, (int)Math.Ceiling(camera.Viewport.Y / cellPixels));

        double reference = mode switch
        {
            HeatmapMode.Divergence => stats.Divergence,
            HeatmapMode.Curl => stats.Curl,
            _ => stats.Magnitude,
        };
        double logNorm = 1 / Math.Log(1 + 3.0);

        meshRow.Clear();
        for (int j = 0; j <= rows; j++)
        {
            for (int i = 0; i <= columns; i++)
            {
                var p = min + new Vector2<double>((double)i / columns * size.X, (double)j / rows * size.Y);
                var s = field.Sample(p, time);
                uint color = mode switch
                {
                    HeatmapMode.Divergence => Diverging(s.Divergence / reference),
                    HeatmapMode.Curl => Diverging(s.Curl / reference),
                    _ => double.IsFinite(s.Magnitude)
                        ? Palette.SpeedColor(Math.Log(1 + s.Magnitude / reference) * logNorm, 0.30)
                        : Rgba.Hex(0xFFFFFF, 0.25),
                };
                meshRow.Add(batch.AddVertex(p, color));
            }
        }

        int stride = columns + 1;
        for (int j = 0; j < rows; j++)
        {
            for (int i = 0; i < columns; i++)
            {
                uint a = meshRow[j * stride + i], b = a + 1, c = meshRow[(j + 1) * stride + i], d = c + 1;
                batch.AddTriangle(a, b, d);
                batch.AddTriangle(a, d, c);
            }
        }

        static uint Diverging(double v) => double.IsFinite(v) ? Palette.Diverging(v, 0.55) : Rgba.Hex(0xFFFFFF, 0.25);
    }

    public void Grid(Batch batch, Camera2D camera)
    {
        double step = GridStep(camera);
        var min = camera.Min;
        var max = camera.Max;
        double hw = batch.PixelSize * 0.5;
        uint minor = Rgba.Hex(0xFFFFFF, 0.045), axis = Rgba.Hex(0xFFFFFF, 0.22);

        for (double x = Math.Ceiling(min.X / step) * step; x <= max.X; x += step)
            batch.Line(new(x, min.Y), new(x, max.Y), hw, Math.Abs(x) < step * 1e-6 ? axis : minor);
        for (double y = Math.Ceiling(min.Y / step) * step; y <= max.Y; y += step)
            batch.Line(new(min.X, y), new(max.X, y), hw, Math.Abs(y) < step * 1e-6 ? axis : minor);
    }

    public void GridLabels(Batch batch, Camera2D camera, int scale)
    {
        double step = GridStep(camera);
        int decimals = Math.Max(0, (int)-Math.Floor(Math.Log10(step) + 1e-9));
        var min = camera.Min;
        var max = camera.Max;
        uint color = Rgba.Hex(0xFFFFFF, 0.38);
        double charHeight = 7 * scale, pad = 4 * scale;

        var origin = camera.WorldToScreen(Vector2<double>.Zero);
        double axisY = Math.Clamp(origin.Y + pad, pad, camera.Viewport.Y - charHeight - pad * 6);
        double axisX = Math.Clamp(origin.X + pad, pad, camera.Viewport.X - pad * 12);

        for (double x = Math.Ceiling(min.X / step) * step; x <= max.X; x += step)
        {
            if (Math.Abs(x) < step * 1e-6) continue;
            var s = camera.WorldToScreen(new(x, 0));
            BitmapFont.Draw(batch, Format(x, decimals), s.X + pad * 0.5, axisY, scale, color);
        }
        for (double y = Math.Ceiling(min.Y / step) * step; y <= max.Y; y += step)
        {
            if (Math.Abs(y) < step * 1e-6) continue;
            var s = camera.WorldToScreen(new(0, y));
            BitmapFont.Draw(batch, Format(y, decimals), axisX, s.Y + pad * 0.5, scale, color);
        }

        static string Format(double v, int decimals)
        {
            string s = v.ToString("F" + decimals);
            return s.TrimStart('-').Trim('0', '.').Length == 0 ? "0" : s;
        }
    }

    static double GridStep(Camera2D camera)
    {
        double raw = 110 * camera.UnitsPerPixel;
        double power = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        double m = raw / power;
        return (m < 1.5 ? 1 : m < 3.5 ? 2 : m < 7.5 ? 5 : 10) * power;
    }

    public void Arrows(Batch batch, FieldDefinition field, Camera2D camera, double time, FieldStats stats, double spacingPixels, double alpha)
    {
        double spacing = spacingPixels * camera.UnitsPerPixel;
        var min = camera.Min;
        var max = camera.Max;
        double px = batch.PixelSize;
        double logNorm = 1 / Math.Log(1 + 3.0);

        for (double y = Math.Floor(min.Y / spacing) * spacing + spacing / 2; y <= max.Y + spacing; y += spacing)
        {
            for (double x = Math.Floor(min.X / spacing) * spacing + spacing / 2; x <= max.X + spacing; x += spacing)
            {
                var p = new Vector2<double>(x, y);
                var v = field.Velocity(p, time);
                double m = v.Norm();
                if (!double.IsFinite(m) || m < 1e-300) continue;

                double t = Math.Clamp(Math.Log(1 + m / stats.Magnitude) * logNorm, 0, 1);
                double length = spacing * (0.28 + 0.52 * t);
                double angle = Math.Atan2(v.Y, v.X);
                batch.Arrow(p, angle, length, px * 0.9, px * 7, px * 3.6, Palette.SpeedColor(t, alpha));
            }
        }
    }

    public void Particles(Batch batch, ParticleSystem particles, double widthPixels)
    {
        int length = ParticleSystem.TrailLength;
        double px = batch.PixelSize;
        for (int i = 0; i < particles.Count; i++)
        {
            int count = particles.TrailCount(i);
            if (count < 2) continue;
            double opacity = particles.Opacity(i);
            if (opacity <= 0.01) continue;

            var trail = particles.Trail(i);
            uint color = Palette.SpeedColor(particles.Speed(i), 1);
            int oldest = particles.Head - (count - 1);
            for (int k = 0; k < count; k++)
            {
                double u = (double)k / (count - 1);
                trailPoints[k] = trail[((oldest + k) % length + length) % length];
                trailWidths[k] = (float)(px * widthPixels * 0.5 * (0.25 + 0.75 * u));
                trailColors[k] = Rgba.WithAlpha(color, 0.95 * opacity * u * u);
            }
            batch.Ribbon(trailPoints.AsSpan(0, count), trailWidths.AsSpan(0, count), trailColors.AsSpan(0, count));
        }
    }

    public void Streamline(Batch batch, Streamline line, uint color, double widthPixels)
    {
        var points = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(line.Points);
        if (points.Length < 2) return;
        double px = batch.PixelSize;
        batch.Polyline(points, px * widthPixels * 0.5, color);

        double spacing = px * 90, travelled = spacing * 0.5;
        for (int i = 1; i < points.Length; i++)
        {
            var d = points[i] - points[i - 1];
            travelled += d.Norm();
            if (travelled < spacing) continue;
            travelled = 0;
            batch.Arrow(points[i], Math.Atan2(d.Y, d.X), px * 9, 0, px * 9, px * 4.5, color, anchor: 0.5);
        }
    }

    public void Probe(Batch batch, Vector2<double> point, Vector2<double> value, FieldStats stats)
    {
        double m = value.Norm();
        double px = batch.PixelSize;
        batch.Disc(point, px * 4.5, Rgba.Hex(0x000000, 0.6));
        batch.Disc(point, px * 3, Palette.Text);
        if (!double.IsFinite(m) || m < 1e-300) return;

        double t = Math.Clamp(Math.Log(1 + m / stats.Magnitude) / Math.Log(3), 0, 1);
        double length = px * (60 + 90 * t);
        double angle = Math.Atan2(value.Y, value.X);
        batch.Arrow(point, angle, length + px * 4, px * 4, px * 22, px * 11, Rgba.Hex(0x000000, 0.65), anchor: 0);
        batch.Arrow(point, angle, length, px * 2, px * 18, px * 8, Palette.Text, anchor: 0);
    }

    public static uint KindColor(CriticalKind kind) => kind switch
    {
        CriticalKind.Saddle => Palette.Saddle,
        CriticalKind.StableNode or CriticalKind.StableFocus => Palette.Attracting,
        CriticalKind.UnstableNode or CriticalKind.UnstableFocus => Palette.Repelling,
        CriticalKind.Center => Palette.Accent,
        _ => Palette.Neutral,
    };

    public void CriticalPoints(Batch batch, IReadOnlyList<CriticalPoint> points, double scale, int highlighted)
    {
        double px = batch.PixelSize * scale;
        uint shadow = Rgba.Hex(0x000000, 0.65);
        for (int i = 0; i < points.Count; i++)
        {
            var p = points[i];
            if (p.HasRealEigenvalues && p.Kind is CriticalKind.Saddle or CriticalKind.StableNode or CriticalKind.UnstableNode)
            {
                EigenDirection(batch, p, p.First.Re, px, shadow);
                if (p.First.Re != p.Second.Re)
                    EigenDirection(batch, p, p.Second.Re, px, shadow);
            }
        }

        for (int i = 0; i < points.Count; i++)
        {
            var p = points[i];
            var c = p.Position;
            uint color = KindColor(p.Kind);
            if (i == highlighted)
            {
                batch.Ring(c, px * 12, px * 15, shadow);
                batch.Ring(c, px * 12.75, px * 14.25, Palette.Text);
            }
            switch (p.Kind)
            {
                case CriticalKind.Center:
                    batch.Ring(c, px * 4.5, px * 8.5, shadow);
                    batch.Ring(c, px * 5.5, px * 7.5, color);
                    batch.Disc(c, px * 2, color);
                    break;
                case CriticalKind.StableFocus or CriticalKind.UnstableFocus:
                    batch.Ring(c, px * 7.5, px * 10.5, shadow);
                    batch.Ring(c, px * 8.5, px * 9.5, color);
                    batch.Disc(c, px * 6, shadow);
                    batch.Disc(c, px * 4.5, color);
                    break;
                default:
                    batch.Disc(c, px * 6.5, shadow);
                    batch.Disc(c, px * 5, color);
                    break;
            }
        }
    }

    static void EigenDirection(Batch batch, CriticalPoint point, double lambda, double px, uint shadow)
    {
        if (point.Eigenvector(lambda) is not { } v) return;
        uint color = lambda < 0 ? Palette.Attracting : lambda > 0 ? Palette.Repelling : Palette.Neutral;
        double length = px * 34;
        for (int side = 0; side < 2; side++)
        {
            var d = side == 0 ? v : -v;
            double angle = Math.Atan2(d.Y, d.X);
            if (lambda < 0)
            {
                var tail = point.Position + d * length;
                batch.Arrow(tail, angle + Math.PI, length - px * 7, px * 2.4, px * 11, px * 6, shadow, anchor: 0);
                batch.Arrow(tail, angle + Math.PI, length - px * 8, px * 1.2, px * 9, px * 4.5, color, anchor: 0);
            }
            else
            {
                batch.Arrow(point.Position, angle, length + px, px * 2.4, px * 11, px * 6, shadow, anchor: 0);
                batch.Arrow(point.Position, angle, length, px * 1.2, px * 9, px * 4.5, color, anchor: 0);
            }
        }
    }

    public void CriticalLabels(Batch batch, IReadOnlyList<CriticalPoint> points, Camera2D camera, int scale)
    {
        if (points.Count > 16) return;
        occupied.Clear();
        double marker = 7 * scale, gap = 9 * scale, height = 9 * scale;
        foreach (var p in points)
        {
            var s = camera.WorldToScreen(p.Position);
            occupied.Add((s.X - marker, s.Y - marker, 2 * marker, 2 * marker));
        }

        foreach (var p in points)
        {
            var s = camera.WorldToScreen(p.Position);
            double width = BitmapFont.Measure(p.Name, scale);
            (double X, double Y)[] candidates =
            [
                (s.X + gap, s.Y - gap - height), (s.X + gap, s.Y + gap),
                (s.X - gap - width, s.Y - gap - height), (s.X - gap - width, s.Y + gap),
            ];
            foreach (var (x, y) in candidates)
            {
                var box = (x, y, width, height);
                if (x < 0 || y < 0 || x + width > camera.Viewport.X || y + height > camera.Viewport.Y) continue;
                if (occupied.Exists(o => Overlaps(o, box))) continue;
                occupied.Add((x - gap, y - scale, width + 2 * gap, height + 2 * scale));
                BitmapFont.DrawShadowed(batch, p.Name, x, y, scale, Rgba.WithAlpha(KindColor(p.Kind), 0.95));
                break;
            }
        }
    }

    static bool Overlaps((double X, double Y, double W, double H) a, (double X, double Y, double W, double H) b) =>
        a.X < b.X + b.W && b.X < a.X + a.W && a.Y < b.Y + b.H && b.Y < a.Y + a.H;
}
