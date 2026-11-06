using Epsilon.LinearAlgebra;
using VectorField.Rendering;
using VectorField.Ui;

namespace VectorField;

public sealed partial class App
{
    const double TypingRate = 14;
    const double Preroll = 1.5;

    sealed record ScriptStep(double Time, string Command, string Argument);

    ScriptStep[]? script;
    int scriptNext;
    double scriptTime;
    int recordedFrames;
    Vector2<double> glideFrom, glideTo;
    double glideStart, glideDuration;
    bool gliding;
    string typingText = "";
    double typingStart;
    int typed = -1;
    bool closeAfterFrame;

    bool Recording => options.RecordDirectory is not null;

    bool Scripted => script is not null;

    void LoadScript(string path)
    {
        var steps = File.ReadAllLines(path)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .Select(ParseStep)
            .OrderBy(step => step.Time)
            .ToList();
        if (!steps.Exists(step => step.Command == "end"))
            steps.Add(new ScriptStep((steps.Count > 0 ? steps[^1].Time : 0) + 2, "end", ""));
        script = [.. steps];
        while (scriptNext < script.Length && script[scriptNext].Time <= 0)
            Execute(script[scriptNext++]);
    }

    static ScriptStep ParseStep(string line)
    {
        var parts = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !double.TryParse(parts[0], out double time))
            throw new FormatException($"The script line '{line}' is not 'TIME COMMAND [ARGUMENT]'.");
        return new ScriptStep(time, parts[1], parts.Length > 2 ? parts[2] : "");
    }

    void AdvanceScript(double dt)
    {
        scriptTime += dt;
        if (script is null) return;

        while (scriptNext < script.Length && script[scriptNext].Time <= scriptTime)
            Execute(script[scriptNext++]);

        if (gliding)
        {
            double u = Math.Clamp((scriptTime - glideStart) / glideDuration, 0, 1);
            mouse = Vector2<double>.Lerp(glideFrom, glideTo, u * u * (3 - 2 * u));
            if (u >= 1) gliding = false;
        }

        if (typed >= 0)
        {
            int due = Math.Clamp((int)((scriptTime - typingStart) * TypingRate) + 1, 0, typingText.Length);
            if (due > typed)
            {
                editor.Insert(typingText[typed..due]);
                typed = due;
            }
            if (typed == typingText.Length) typed = -1;
        }
    }

    void Execute(ScriptStep step)
    {
        string argument = step.Argument;
        switch (step.Command)
        {
            case "preset":
                LoadPreset(int.Parse(argument) - 1);
                break;
            case "formula":
                ApplyFormula(argument);
                break;
            case "heatmap":
                heatmap = ParseHeatmap(argument);
                break;
            case "mouse":
                mouse = ParsePoint(argument);
                mouseInWindow = true;
                gliding = false;
                break;
            case "move":
                var parts = argument.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                glideFrom = mouse;
                glideTo = ParsePoint(parts[0]);
                glideStart = step.Time;
                glideDuration = parts.Length > 1 ? Math.Max(double.Parse(parts[1]), 1e-3) : 1;
                gliding = true;
                mouseInWindow = true;
                break;
            case "hide-mouse":
                mouseInWindow = false;
                gliding = false;
                break;
            case "edit":
                BeginEditing();
                break;
            case "clear":
                editor.Set("");
                break;
            case "type":
                typingText = argument;
                typingStart = step.Time;
                typed = 0;
                break;
            case "enter":
                if (editing && ApplyFormula(editor.Text)) editing = false;
                break;
            case "toggle":
                Toggle(argument);
                break;
            case "end":
                closeAfterFrame = true;
                break;
            default:
                throw new FormatException($"Unknown script command '{step.Command}'.");
        }
    }

    void Toggle(string what)
    {
        switch (what)
        {
            case "arrows": showArrows = !showArrows; break;
            case "particles": showParticles = !showParticles; break;
            case "streamline": showStreamline = !showStreamline; break;
            case "ui": showUi = !showUi; break;
            case "critical":
                showCritical = !showCritical;
                criticalKey = null;
                break;
            default: throw new FormatException($"Unknown toggle '{what}'.");
        }
    }

    string? NextRecordPath()
    {
        if (!Recording || scriptTime < 0) return null;
        recordedFrames++;
        return Path.Combine(options.RecordDirectory!, $"frame{recordedFrames:D5}.png");
    }

    void DrawBrand()
    {
        int s = UiScale;
        const string brand = "made with ε Epsilon · symbolic math for .NET";
        double x = camera.Viewport.X - 12 * s - BitmapFont.Measure(brand, s);
        double y = camera.Viewport.Y - 12 * s - 9 * s;
        BitmapFont.DrawShadowed(batch, brand, x, y, s, Palette.Accent);
    }

    internal static Vector2<double> ParsePoint(string text)
    {
        var parts = text.Split(',');
        return parts.Length == 2
            ? new Vector2<double>(double.Parse(parts[0]), double.Parse(parts[1]))
            : throw new FormatException($"The point {text} is not X,Y.");
    }

    internal static HeatmapMode ParseHeatmap(string text) => text switch
    {
        "off" => HeatmapMode.Off,
        "mag" => HeatmapMode.Magnitude,
        "div" => HeatmapMode.Divergence,
        "curl" => HeatmapMode.Curl,
        _ => throw new FormatException($"The heatmap {text} is not off, mag, div or curl."),
    };
}
