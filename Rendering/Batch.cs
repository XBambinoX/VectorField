using System.Runtime.InteropServices;
using Epsilon.LinearAlgebra;

namespace VectorField.Rendering;

[StructLayout(LayoutKind.Sequential)]
public readonly struct Vertex(float x, float y, uint color)
{
    public readonly float X = x;
    public readonly float Y = y;
    public readonly uint Color = color;
}

public enum Space { World, Screen }

public enum Blend { Alpha, Additive }

public readonly record struct DrawCall(Space Space, Blend Blend, uint FirstIndex, uint IndexCount);

public sealed class Batch
{
    Vertex[] vertices = new Vertex[1 << 16];
    uint[] indices = new uint[1 << 17];
    readonly List<DrawCall> calls = [];
    int vertexCount, indexCount, callStart;
    Space space;
    Blend blend;
    double originX, originY;

    public double PixelSize { get; private set; } = 1;

    public ReadOnlySpan<Vertex> Vertices => vertices.AsSpan(0, vertexCount);
    public ReadOnlySpan<uint> Indices => indices.AsSpan(0, indexCount);
    public IReadOnlyList<DrawCall> Calls => calls;

    public void Reset()
    {
        vertexCount = indexCount = callStart = 0;
        calls.Clear();
    }

    public void Begin(Space space, Blend blend, Vector2<double> origin = default, double pixelSize = 1)
    {
        End();
        this.space = space;
        this.blend = blend;
        originX = origin.X;
        originY = origin.Y;
        PixelSize = pixelSize;
    }

    public void End()
    {
        if (indexCount > callStart)
            calls.Add(new DrawCall(space, blend, (uint)callStart, (uint)(indexCount - callStart)));
        callStart = indexCount;
    }

    public uint AddVertex(double x, double y, uint color)
    {
        if (vertexCount == vertices.Length)
            Array.Resize(ref vertices, vertices.Length * 2);
        vertices[vertexCount] = new Vertex((float)(x - originX), (float)(y - originY), color);
        return (uint)vertexCount++;
    }

    public uint AddVertex(Vector2<double> p, uint color) => AddVertex(p.X, p.Y, color);

    public void AddTriangle(uint a, uint b, uint c)
    {
        if (indexCount + 3 > indices.Length)
            Array.Resize(ref indices, indices.Length * 2);
        indices[indexCount] = a;
        indices[indexCount + 1] = b;
        indices[indexCount + 2] = c;
        indexCount += 3;
    }

    public void Triangle(Vector2<double> a, Vector2<double> b, Vector2<double> c, uint color) =>
        AddTriangle(AddVertex(a, color), AddVertex(b, color), AddVertex(c, color));

    public void Quad(Vector2<double> a, Vector2<double> b, Vector2<double> c, Vector2<double> d, uint color)
    {
        uint i0 = AddVertex(a, color), i1 = AddVertex(b, color), i2 = AddVertex(c, color), i3 = AddVertex(d, color);
        AddTriangle(i0, i1, i2);
        AddTriangle(i0, i2, i3);
    }

    public void Rect(double x, double y, double width, double height, uint color)
    {
        uint i0 = AddVertex(x, y, color), i1 = AddVertex(x + width, y, color);
        uint i2 = AddVertex(x + width, y + height, color), i3 = AddVertex(x, y + height, color);
        AddTriangle(i0, i1, i2);
        AddTriangle(i0, i2, i3);
    }

    public void RectOutline(double x, double y, double width, double height, double thickness, uint color)
    {
        Rect(x, y, width, thickness, color);
        Rect(x, y + height - thickness, width, thickness, color);
        Rect(x, y + thickness, thickness, height - 2 * thickness, color);
        Rect(x + width - thickness, y + thickness, thickness, height - 2 * thickness, color);
    }

    public void Line(Vector2<double> a, Vector2<double> b, double halfWidth, uint color)
    {
        var d = b - a;
        double length = d.Norm();
        if (!(length > 0)) return;
        var n = new Vector2<double>(-d.Y, d.X) * (halfWidth / length);
        Quad(a + n, b + n, b - n, a - n, color);
    }

    public void Polyline(ReadOnlySpan<Vector2<double>> points, double halfWidth, uint color)
    {
        int n = points.Length;
        if (n < 2) return;
        uint first = (uint)vertexCount;
        for (int i = 0; i < n; i++)
        {
            var offset = Normal(points, i) * halfWidth;
            AddVertex(points[i] + offset, color);
            AddVertex(points[i] - offset, color);
        }
        StitchRibbon(first, n);
    }

    public void Ribbon(ReadOnlySpan<Vector2<double>> points, ReadOnlySpan<float> halfWidths, ReadOnlySpan<uint> colors)
    {
        int n = points.Length;
        if (n < 2) return;
        uint first = (uint)vertexCount;
        for (int i = 0; i < n; i++)
        {
            var offset = Normal(points, i) * halfWidths[i];
            AddVertex(points[i] + offset, colors[i]);
            AddVertex(points[i] - offset, colors[i]);
        }
        StitchRibbon(first, n);
    }

    void StitchRibbon(uint first, int n)
    {
        for (int i = 0; i < n - 1; i++)
        {
            uint a = first + (uint)(2 * i);
            AddTriangle(a, a + 1, a + 2);
            AddTriangle(a + 1, a + 3, a + 2);
        }
    }

    static Vector2<double> Normal(ReadOnlySpan<Vector2<double>> points, int i)
    {
        var tangent = points[Math.Min(i + 1, points.Length - 1)] - points[Math.Max(i - 1, 0)];
        double length = tangent.Norm();
        return length > 0 ? new Vector2<double>(-tangent.Y, tangent.X) / length : Vector2<double>.Zero;
    }

    public void Arrow(Vector2<double> center, double angle, double length, double shaftHalfWidth,
        double headLength, double headHalfWidth, uint color, double anchor = 0.5)
    {
        var frame = Matrix3x3<double>.Compose(center, angle, Vector2<double>.One);
        double tail = -length * anchor, tip = length * (1 - anchor), neck = tip - Math.Min(headLength, length * 0.6);

        if (neck > tail)
            Quad(frame.TransformPoint(new(tail, -shaftHalfWidth)), frame.TransformPoint(new(neck, -shaftHalfWidth)),
                 frame.TransformPoint(new(neck, shaftHalfWidth)), frame.TransformPoint(new(tail, shaftHalfWidth)), color);
        Triangle(frame.TransformPoint(new(neck, -headHalfWidth)), frame.TransformPoint(new(tip, 0)),
                 frame.TransformPoint(new(neck, headHalfWidth)), color);
    }

    public void Disc(Vector2<double> center, double radius, uint color, int segments = 20)
    {
        uint c = AddVertex(center, color);
        uint first = (uint)vertexCount;
        var step = Matrix2x2<double>.Rotation(2 * Math.PI / segments);
        var r = new Vector2<double>(radius, 0);
        for (int i = 0; i < segments; i++)
        {
            AddVertex(center + r, color);
            r = step * r;
        }
        for (int i = 0; i < segments; i++)
            AddTriangle(c, first + (uint)i, first + (uint)((i + 1) % segments));
    }

    public void Ring(Vector2<double> center, double inner, double outer, uint color, int segments = 28)
    {
        uint first = (uint)vertexCount;
        var step = Matrix2x2<double>.Rotation(2 * Math.PI / segments);
        var r = Vector2<double>.UnitX;
        for (int i = 0; i < segments; i++)
        {
            AddVertex(center + r * outer, color);
            AddVertex(center + r * inner, color);
            r = step * r;
        }
        for (int i = 0; i < segments; i++)
        {
            uint a = first + (uint)(2 * i), b = first + (uint)(2 * ((i + 1) % segments));
            AddTriangle(a, a + 1, b);
            AddTriangle(a + 1, b + 1, b);
        }
    }
}
