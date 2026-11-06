using Epsilon.LinearAlgebra;

namespace VectorField.Model;

public sealed class Camera2D
{
    public const double DefaultViewHeight = 8;

    public Vector2<double> Center { get; set; } = Vector2<double>.Zero;

    public double ViewHeight { get; private set; } = DefaultViewHeight;

    public Vector2<double> Viewport { get; set; } = new(1, 1);

    public double UnitsPerPixel => ViewHeight / Viewport.Y;
    public double ViewWidth => Viewport.X * UnitsPerPixel;
    public Vector2<double> Min => Center - new Vector2<double>(ViewWidth, ViewHeight) / 2;
    public Vector2<double> Max => Center + new Vector2<double>(ViewWidth, ViewHeight) / 2;

    public void Reset()
    {
        Center = Vector2<double>.Zero;
        ViewHeight = DefaultViewHeight;
    }

    public Vector2<double> ScreenToWorld(Vector2<double> pixel) =>
        Center + new Vector2<double>(pixel.X - Viewport.X / 2, Viewport.Y / 2 - pixel.Y) * UnitsPerPixel;

    public Vector2<double> WorldToScreen(Vector2<double> point)
    {
        var d = (point - Center) / UnitsPerPixel;
        return new Vector2<double>(Viewport.X / 2 + d.X, Viewport.Y / 2 - d.Y);
    }

    public void Pan(Vector2<double> pixels) =>
        Center -= new Vector2<double>(pixels.X, -pixels.Y) * UnitsPerPixel;

    public void ZoomAt(Vector2<double> pixel, double factor)
    {
        var before = ScreenToWorld(pixel);
        ViewHeight = Math.Clamp(ViewHeight * factor, 1e-4, 1e5);
        Center += before - ScreenToWorld(pixel);
    }

    public bool Contains(Vector2<double> point, double margin)
    {
        var min = Min;
        var max = Max;
        double mx = (max.X - min.X) * margin, my = (max.Y - min.Y) * margin;
        return point.X >= min.X - mx && point.X <= max.X + mx && point.Y >= min.Y - my && point.Y <= max.Y + my;
    }

    public Matrix4x4<float> WorldTransform
    {
        get
        {
            float hw = (float)(ViewWidth / 2), hh = (float)(ViewHeight / 2);
            return Matrix4x4<float>.OrthographicOffCenter(-hw, hw, hh, -hh, -1f, 1f, DepthRange.ZeroToOne);
        }
    }

    public Matrix4x4<float> ScreenTransform =>
        Matrix4x4<float>.OrthographicOffCenter(0f, (float)Viewport.X, 0f, (float)Viewport.Y, -1f, 1f, DepthRange.ZeroToOne);
}
