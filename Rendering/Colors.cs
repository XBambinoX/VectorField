namespace VectorField.Rendering;

public static class Rgba
{
    public static uint Pack(double r, double g, double b, double a) =>
        Byte(r) | (Byte(g) << 8) | (Byte(b) << 16) | (Byte(a) << 24);

    public static uint Hex(uint rgb, double alpha = 1) =>
        ((rgb >> 16) & 0xFF) | (rgb & 0xFF00) | ((rgb & 0xFF) << 16) | (Byte(alpha) << 24);

    public static uint WithAlpha(uint color, double alpha) => (color & 0x00FFFFFF) | (Byte(alpha) << 24);

    public static uint Lerp(uint from, uint to, double t)
    {
        uint result = 0;
        for (int shift = 0; shift < 32; shift += 8)
        {
            double a = (from >> shift) & 0xFF, b = (to >> shift) & 0xFF;
            result |= (uint)Math.Round(a + (b - a) * t) << shift;
        }
        return result;
    }

    public static (float R, float G, float B) Unpack(uint color) =>
        ((color & 0xFF) / 255f, ((color >> 8) & 0xFF) / 255f, ((color >> 16) & 0xFF) / 255f);

    static uint Byte(double v) => (uint)Math.Round(Math.Clamp(v, 0, 1) * 255);
}

public static class Palette
{
    public static readonly uint Background = Rgba.Hex(0x090B10);
    public static readonly uint PanelFill = Rgba.Hex(0x0E1117, 0.86);
    public static readonly uint PanelBorder = Rgba.Hex(0xFFFFFF, 0.09);
    public static readonly uint Text = Rgba.Hex(0xE6EDF3);
    public static readonly uint TextDim = Rgba.Hex(0x8B949E);
    public static readonly uint Accent = Rgba.Hex(0x7DD3FC);
    public static readonly uint Good = Rgba.Hex(0x86EFAC);
    public static readonly uint Error = Rgba.Hex(0xF87171);
    public static readonly uint Negative = Rgba.Hex(0x3B82F6);
    public static readonly uint Positive = Rgba.Hex(0xF97316);
    public static readonly uint Attracting = Rgba.Hex(0x4ADE80);
    public static readonly uint Repelling = Rgba.Hex(0xFB7185);
    public static readonly uint Saddle = Rgba.Hex(0xFACC15);
    public static readonly uint Neutral = Rgba.Hex(0x9CA3AF);

    static readonly uint[] Speed =
    [
        Rgba.Hex(0x4C52E0), Rgba.Hex(0x2AA3E8), Rgba.Hex(0x2DD4BF), Rgba.Hex(0xA3E635), Rgba.Hex(0xFACC15),
    ];

    public static uint SpeedColor(double t, double alpha)
    {
        t = Math.Clamp(t, 0, 1) * (Speed.Length - 1);
        int i = Math.Min((int)t, Speed.Length - 2);
        return Rgba.WithAlpha(Rgba.Lerp(Speed[i], Speed[i + 1], t - i), alpha);
    }

    public static uint Diverging(double v, double maxAlpha)
    {
        double a = Math.Pow(Math.Min(Math.Abs(v), 1), 0.8) * maxAlpha;
        return Rgba.WithAlpha(v < 0 ? Negative : Positive, a);
    }
}
