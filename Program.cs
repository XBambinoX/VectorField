using VectorField;

const string Usage = """
    Vector Field: plane vector fields with Epsilon and Vulkan

    Usage: dotnet run -- [options]

      --preset N          start with preset N (1-9)
      --formula "[P, Q]"  start with a field of x, y and the time t
      --size WxH          window size in pixels (default 1600x900)
      --heatmap MODE      off, mag, div or curl (default off)
      --no-arrows         hide the arrow grid
      --no-particles      hide the particles
      --no-ui             hide the interface
      --clean             show the formulas but not the key help and status
      --edit              start with the formula open for editing
      --capture FILE.png  render, save one frame as PNG and exit
      --frames N          the frame to capture or the frames to record (default 300)
      --mouse X,Y         fix the probe at this pixel, ignoring the mouse (for captures)
      --script FILE       play timed commands: preset, formula, heatmap, mouse, move,
                          hide-mouse, edit, clear, type, enter, toggle, end
      --record DIR        save every frame as DIR/frameNNNNN.png at a fixed time step
      --fps N             frames per second of the recording (default 25)
    """;

var options = new AppOptions();
try
{
    for (int i = 0; i < args.Length; i++)
    {
        string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value.");
        options = args[i] switch
        {
            "--preset" => options with { Preset = int.Parse(Next()) - 1 },
            "--formula" => options with { Formula = Next() },
            "--size" => Size(Next()) is var (w, h) ? options with { Width = w, Height = h } : options,
            "--heatmap" => options with { Heatmap = App.ParseHeatmap(Next()) },
            "--no-arrows" => options with { Arrows = false },
            "--no-particles" => options with { Particles = false },
            "--no-ui" => options with { Ui = false },
            "--clean" => options with { Clean = true },
            "--edit" => options with { Edit = true },
            "--capture" => options with { CapturePath = Next() },
            "--frames" => options with { CaptureFrames = Math.Max(1, int.Parse(Next())) },
            "--mouse" => options with { Mouse = App.ParsePoint(Next()) },
            "--script" => options with { ScriptPath = Next() },
            "--record" => options with { RecordDirectory = Next() },
            "--fps" => options with { RecordFps = Math.Clamp(int.Parse(Next()), 1, 120) },
            "-h" or "--help" => throw new OperationCanceledException(),
            _ => throw new ArgumentException($"Unknown option {args[i]}."),
        };
    }
}
catch (OperationCanceledException)
{
    Console.WriteLine(Usage);
    return 0;
}
catch (Exception e) when (e is ArgumentException or FormatException)
{
    Console.Error.WriteLine(e.Message);
    Console.Error.WriteLine(Usage);
    return 2;
}

try
{
    using var app = new App(options);
    app.Run();
    return 0;
}
catch (FormatException e)
{
    Console.Error.WriteLine(e.Message);
    return 2;
}

static (int, int) Size(string text)
{
    var parts = text.Split('x', 'X');
    return parts.Length == 2 ? (int.Parse(parts[0]), int.Parse(parts[1])) : throw new FormatException($"Size {text} is not WxH.");
}
