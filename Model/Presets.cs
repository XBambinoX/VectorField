namespace VectorField.Model;

public readonly record struct Preset(string Name, string Formula);

public static class Presets
{
    public static readonly Preset[] All =
    [
        new("Vortex", "[-y, x]"),
        new("Damped pendulum", "[y, -sin(x) - 0.2y]"),
        new("Van der Pol oscillator", "[y, (1 - x^2)y - x]"),
        new("Duffing double well", "[y, x - x^3 - 0.25y]"),
        new("Hopf limit cycle", "[x(1 - x^2 - y^2) - y, y(1 - x^2 - y^2) + x]"),
        new("Electric dipole", "[(x - 1)/((x - 1)^2 + y^2) - (x + 1)/((x + 1)^2 + y^2), y/((x - 1)^2 + y^2) - y/((x + 1)^2 + y^2)]"),
        new("Taylor-Green vortices", "[sin(x)cos(y), -cos(x)sin(y)]"),
        new("Complex square z^2 - 4", "[x^2 - y^2 - 4, 2x y]"),
        new("Drifting cells (uses t)", "[sin(y + t), cos(x - t)]"),
    ];
}
