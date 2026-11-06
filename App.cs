using Epsilon.Core;
using Epsilon.LinearAlgebra;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.Windowing;
using VectorField.Model;
using VectorField.Rendering;
using VectorField.Simulation;
using VectorField.Ui;

namespace VectorField;

public sealed record AppOptions
{
    public int Width { get; init; } = 1600;
    public int Height { get; init; } = 900;
    public int Preset { get; init; } = 1;
    public string? Formula { get; init; }
    public string? CapturePath { get; init; }
    public int CaptureFrames { get; init; } = 300;
    public Vector2<double>? Mouse { get; init; }
    public HeatmapMode Heatmap { get; init; } = HeatmapMode.Off;
    public bool Arrows { get; init; } = true;
    public bool Particles { get; init; } = true;
    public bool Ui { get; init; } = true;
    public bool Edit { get; init; }
    public bool Clean { get; init; }
    public string? ScriptPath { get; init; }
    public string? RecordDirectory { get; init; }
    public int RecordFps { get; init; } = 25;
}

public sealed partial class App : IDisposable
{
    const double SimulationStep = 1.0 / 60;
    const int MaxSeeds = 24;
    const string Variables = "variables: x, y, t";

    readonly AppOptions options;
    readonly IWindow window;
    VulkanRenderer? renderer;
    IInputContext? input;

    readonly Batch batch = new();
    readonly Camera2D camera = new();
    readonly FieldStats stats = new();
    readonly ParticleSystem particles = new();
    readonly FieldPainter painter = new();
    readonly TextInput editor = new();
    readonly Streamline probeLine = new();
    readonly List<Vector2<double>> seeds = [];
    readonly List<Streamline> seedLines = [];
    readonly CriticalPointFinder critical = new();
    (FieldDefinition, Vector2<double>, double, Vector2<double>, double)? criticalKey;

    FieldDefinition field;
    int preset;
    string? error;
    bool fieldChanged = true;

    bool editing, paused, showArrows, showParticles, showStreamline = true, showCritical = true, showUi;
    HeatmapMode heatmap;
    double time, accumulator, speed = 1;

    Vector2<double> mouse = new(-1, -1);
    bool mouseInWindow, leftDown, dragging;
    Vector2<double> pressPosition, lastMouse;
    Key? repeatKey;
    double repeatDelay;

    (double X, double Y, double W, double H) panel, formulaRow;
    double formulaTextX;
    int formulaScroll;

    string? screenshotRequest;
    int frameNumber;
    double fpsTime, fps;
    int fpsFrames;

    readonly bool profile = Environment.GetEnvironmentVariable("VF_PROFILE") == "1";
    readonly System.Diagnostics.Stopwatch profileClock = new();
    readonly double[] profileTotals = new double[4];

    public App(AppOptions options)
    {
        this.options = options;
        preset = options.Formula is null ? Math.Clamp(options.Preset, 0, Presets.All.Length - 1) : -1;
        field = FieldDefinition.Parse(options.Formula ?? Presets.All[preset].Formula);
        preset = MatchPreset(field.Source);
        heatmap = options.Heatmap;
        showArrows = options.Arrows;
        showParticles = options.Particles;
        showUi = options.Ui;
        if (options.Mouse is { } m)
        {
            mouse = m;
            mouseInWindow = true;
        }
        if (options.Edit) BeginEditing();
        if (options.RecordDirectory is not null)
        {
            Directory.CreateDirectory(options.RecordDirectory);
            scriptTime = -Preroll;
        }
        if (options.ScriptPath is not null) LoadScript(options.ScriptPath);

        window = Window.Create(WindowOptions.DefaultVulkan with
        {
            Size = new Vector2D<int>(options.Width, options.Height),
            Title = "Vector Field · Epsilon × Vulkan",
        });
        window.Load += OnLoad;
        window.Render += OnRender;
        window.Closing += OnClosing;
    }

    public void Run() => window.Run();

    int UiScale => camera.Viewport.Y >= 1300 ? 3 : 2;

    void OnLoad()
    {
        renderer = new VulkanRenderer(window);
        var framebuffer = window.FramebufferSize;
        Console.WriteLine($"Vulkan on {renderer.DeviceName}, {renderer.SampleCount}x MSAA, {framebuffer.X}x{framebuffer.Y}");
        if (Recording) renderer.CaptureCompression = System.IO.Compression.CompressionLevel.Fastest;

        input = window.CreateInput();
        foreach (var keyboard in input.Keyboards)
        {
            keyboard.KeyDown += OnKeyDown;
            keyboard.KeyUp += OnKeyUp;
            keyboard.KeyChar += OnKeyChar;
        }
        foreach (var m in input.Mice)
        {
            m.MouseDown += OnMouseDown;
            m.MouseUp += OnMouseUp;
            m.MouseMove += OnMouseMove;
            m.Scroll += OnScroll;
        }
    }

    void OnClosing()
    {
        input?.Dispose();
        input = null;
        renderer?.Dispose();
        renderer = null;
    }

    void OnRender(double delta)
    {
        if (renderer is null) return;
        var size = window.FramebufferSize;
        if (size.X <= 0 || size.Y <= 0) return;
        camera.Viewport = new Vector2<double>(size.X, size.Y);

        double dt = Recording ? 1.0 / options.RecordFps : Math.Min(delta, 0.1);
        CountFps(delta);
        AdvanceScript(dt);
        RepeatKeys(dt);
        editor.IdleTime += dt;

        profileClock.Restart();
        particles.Resize(Math.Clamp((int)(size.X * size.Y / 650.0), 1200, 6000), camera);
        stats.Update(field, camera, time, fieldChanged);
        if (fieldChanged)
        {
            particles.Respawn(camera);
            fieldChanged = false;
        }

        if (!paused)
        {
            accumulator += dt;
            int steps = 0;
            double baseSpeed = camera.ViewHeight * 0.2 * speed;
            while (accumulator >= SimulationStep && steps < 4)
            {
                particles.Step(field, time, SimulationStep, baseSpeed, stats.Magnitude, camera);
                time += SimulationStep;
                accumulator -= SimulationStep;
                steps++;
            }
            if (steps == 4) accumulator = 0;
        }

        if (showCritical)
        {
            var key = (field, camera.Center, camera.ViewHeight, camera.Viewport, field.DependsOnTime ? time : 0.0);
            if (criticalKey != key)
            {
                critical.Find(field, camera, time, stats.Magnitude);
                criticalKey = key;
            }
        }
        Lap(0);

        BuildScene();
        Lap(1);

        frameNumber++;
        string? capture = screenshotRequest;
        bool finalCapture = options.CapturePath is not null && frameNumber == options.CaptureFrames;
        if (finalCapture) capture = options.CapturePath;
        string? recordPath = NextRecordPath();
        if (recordPath is not null) capture = recordPath;

        bool saved = renderer.Draw(batch, camera.WorldTransform, camera.ScreenTransform, Palette.Background, capture);
        Lap(2);
        if (recordPath is not null)
        {
            if (!saved) throw new InvalidOperationException("This swapchain can't be read back, so nothing can be recorded.");
            if (recordedFrames % 50 == 0) Console.WriteLine($"Recorded {recordedFrames} frames");
        }
        else if (capture is not null)
        {
            Console.WriteLine(saved ? $"Saved {Path.GetFullPath(capture)}" : "This swapchain can't be read back; no screenshot.");
            screenshotRequest = null;
        }
        if (closeAfterFrame || finalCapture || (Recording && !Scripted && recordedFrames >= options.CaptureFrames)) window.Close();
    }

    void CountFps(double delta)
    {
        fpsTime += delta;
        fpsFrames++;
        if (fpsTime >= 0.5)
        {
            fps = fpsFrames / fpsTime;
            if (profile)
            {
                Console.WriteLine($"{fps,5:0} fps | simulate {profileTotals[0] / fpsFrames,5:0.00} ms | build {profileTotals[1] / fpsFrames,5:0.00} ms" +
                    $" | draw+wait {profileTotals[2] / fpsFrames,5:0.00} ms | {batch.Vertices.Length} vertices, {batch.Indices.Length / 3} triangles");
                Array.Clear(profileTotals);
            }
            fpsTime = 0;
            fpsFrames = 0;
        }
    }

    void Lap(int section)
    {
        if (!profile) return;
        profileTotals[section] += profileClock.Elapsed.TotalMilliseconds;
        profileClock.Restart();
    }

    void BuildScene()
    {
        batch.Reset();
        double px = camera.UnitsPerPixel;
        int ui = UiScale;
        double k = ui / 2.0;

        batch.Begin(Space.World, Blend.Alpha, camera.Center, px);
        painter.Heatmap(batch, field, camera, time, heatmap, stats, 9 * k);
        painter.Grid(batch, camera);

        if (showParticles)
        {
            batch.Begin(Space.World, Blend.Additive, camera.Center, px);
            painter.Particles(batch, particles, 2.8 * k);
        }

        batch.Begin(Space.World, Blend.Alpha, camera.Center, px);
        if (showArrows)
            painter.Arrows(batch, field, camera, time, stats, 46 * k, showParticles ? 0.4 : 0.9);

        for (int i = 0; i < seeds.Count; i++)
        {
            seedLines[i].Trace(field, seeds[i], time, px * 2.5, 1500, camera);
            painter.Streamline(batch, seedLines[i], Rgba.Hex(0xFFFFFF, 0.8), 2 * k);
            batch.Disc(seeds[i], px * 3.5 * k, Palette.Accent);
        }

        bool probing = mouseInWindow && !dragging && !OverPanel(mouse);
        int snapped = probing && showCritical ? NearestCritical(mouse, 14 * k) : -1;
        var probePoint = camera.ScreenToWorld(mouse);
        FieldSample probe = default;
        if (probing && snapped < 0)
        {
            probe = field.Sample(probePoint, time);
            if (showStreamline)
            {
                probeLine.Trace(field, probePoint, time, px * 2.5, 1500, camera);
                painter.Streamline(batch, probeLine, Rgba.Hex(0x7DD3FC, 0.9), 2 * k);
            }
        }

        if (showCritical)
            painter.CriticalPoints(batch, critical.Points, k, snapped);
        if (probing && snapped < 0)
            painter.Probe(batch, probePoint, probe.Value, stats);

        batch.Begin(Space.Screen, Blend.Alpha);
        painter.GridLabels(batch, camera, ui);
        if (showCritical && showUi)
            painter.CriticalLabels(batch, critical.Points, camera, ui);
        if (showUi || editing) DrawFormulaPanel();
        if (showUi)
        {
            if (snapped >= 0) DrawTooltip(CriticalLines(critical.Points[snapped]));
            else if (probing) DrawTooltip(ProbeLines(probePoint, probe));
            if (options.Clean)
            {
                DrawBrand();
            }
            else
            {
                DrawHelp();
                DrawStatus();
            }
        }
        batch.End();
    }

    int NearestCritical(Vector2<double> pixel, double radius)
    {
        int best = -1;
        double bestDistance = radius;
        for (int i = 0; i < critical.Points.Count; i++)
        {
            double d = camera.WorldToScreen(critical.Points[i].Position).Distance(pixel);
            if (d <= bestDistance)
            {
                best = i;
                bestDistance = d;
            }
        }
        return best;
    }

    void DrawFormulaPanel()
    {
        int s = UiScale;
        double advance = BitmapFont.Advance * s, lineHeight = BitmapFont.LineHeight * s, pad = 8 * s;
        double x = 12 * s, y = 12 * s;
        int maxChars = Math.Max(16, (int)((camera.Viewport.X * 0.6 - 2 * pad) / advance));

        var lines = new List<(string Text, uint Color)>
        {
            (preset >= 0 ? $"{preset + 1}/{Presets.All.Length}  {Presets.All[preset].Name}" : "Custom field", Palette.TextDim),
            ("", Palette.Text),
            ("∇·F = " + (field.Divergence?.Print() ?? "n/a: no derivative in Epsilon yet"), Palette.Text),
            ("∇×F = " + (field.Curl?.Print() ?? "n/a: no derivative in Epsilon yet"), Palette.Text),
        };
        if (field.Potential is not null)
            lines.Add(("F = ∇φ,  φ = " + field.Potential.Print(), Palette.Good));
        lines.Add(("J = " + (field.Jacobian?.Print() ?? "n/a: no derivative in Epsilon yet"), Palette.Text));
        if (field.CharacteristicPolynomial is not null)
            lines.Add(("det(J - λI) = " + field.CharacteristicPolynomial.Print(), Palette.Text));
        if (showCritical)
            lines.Add((CriticalSummary(), Palette.TextDim));
        if (field.DependsOnTime)
            lines.Add(($"t = {time:0.00}", Palette.TextDim));
        if (editing)
            lines.Add(($"Enter apply · Esc cancel · {Variables}", Palette.TextDim));
        if (error is not null)
            lines.Add((error, Palette.Error));

        const string label = "F(x, y) = ";
        string formula = editing ? editor.Text : field.Source;
        int widest = Math.Max(lines.Max(l => l.Text.Length), label.Length + formula.Length + 1);
        int columns = Math.Min(widest, maxChars);
        double width = columns * advance + 2 * pad;
        double height = lines.Count * lineHeight + 2 * pad - (lineHeight - 9 * s);
        panel = (x, y, width, height);

        batch.Rect(x, y, width, height, Palette.PanelFill);
        batch.RectOutline(x, y, width, height, 1, Palette.PanelBorder);

        double textX = x + pad;
        for (int i = 0; i < lines.Count; i++)
        {
            double rowY = y + pad + i * lineHeight;
            if (i == 1)
            {
                DrawFormulaRow(label, formula, textX, rowY, columns, s);
                continue;
            }
            BitmapFont.Draw(batch, Fit(lines[i].Text, columns), textX, rowY, s, lines[i].Color);
        }
    }

    void DrawFormulaRow(string label, string formula, double x, double y, int columns, int s)
    {
        double advance = BitmapFont.Advance * s;
        int visible = Math.Max(4, columns - label.Length);
        formulaRow = (x - 2 * s, y - 2 * s, columns * advance + 4 * s, 11 * s);
        formulaTextX = x + label.Length * advance;

        BitmapFont.Draw(batch, label, x, y, s, Palette.Accent);
        if (editing)
        {
            batch.Rect(formulaTextX - 2 * s, y - 2 * s, visible * advance + 3 * s, 11 * s, Rgba.Hex(0xFFFFFF, 0.06));
            batch.RectOutline(formulaTextX - 2 * s, y - 2 * s, visible * advance + 3 * s, 11 * s, 1, Rgba.WithAlpha(Palette.Accent, 0.6));

            if (editor.Caret < formulaScroll) formulaScroll = editor.Caret;
            if (editor.Caret > formulaScroll + visible - 1) formulaScroll = editor.Caret - visible + 1;
            formulaScroll = Math.Clamp(formulaScroll, 0, Math.Max(0, formula.Length - visible + 1));
            string shown = formula.Substring(formulaScroll, Math.Min(visible, formula.Length - formulaScroll));
            BitmapFont.Draw(batch, shown, formulaTextX, y, s, Palette.Text);

            if (editor.IdleTime % 1.0 < 0.6)
                batch.Rect(formulaTextX + (editor.Caret - formulaScroll) * advance - s, y - s, s, 9 * s, Palette.Accent);
        }
        else
        {
            formulaScroll = 0;
            BitmapFont.Draw(batch, Fit(formula, visible), formulaTextX, y, s, Palette.Text);
        }
    }

    (string Text, uint Color)[] ProbeLines(Vector2<double> point, FieldSample sample) =>
    [
        ($"x = {Num(point.X)}   y = {Num(point.Y)}", Palette.TextDim),
        ($"F = ({Num(sample.Value.X)}, {Num(sample.Value.Y)})", Palette.Text),
        ($"|F| = {Num(sample.Magnitude)}", Palette.Text),
        ($"∇·F = {Num(Snap(sample.Divergence))}", Palette.Text),
        ($"∇×F = {Num(Snap(sample.Curl))}", Palette.Text),
    ];

    (string Text, uint Color)[] CriticalLines(CriticalPoint p)
    {
        var j = p.Jacobian;
        double scale = Math.Max(Math.Max(Math.Abs(j[0, 0]), Math.Abs(j[0, 1])), Math.Max(Math.Abs(j[1, 0]), Math.Abs(j[1, 1])));
        string J(double v) => Num(Math.Abs(v) < 1e-12 * scale ? 0 : v);
        string X(double v) => Num(Math.Abs(v) < 1e-12 * camera.ViewHeight ? 0 : v);
        string eigenvalues = p.HasRealEigenvalues
            ? $"λ1 = {J(p.First.Re)}   λ2 = {J(p.Second.Re)}"
            : $"λ = {J(p.First.Re)} ± {J(p.First.Im)}i";
        return
        [
            (p.Name, FieldPainter.KindColor(p.Kind)),
            ($"x = {X(p.Position.X)}   y = {X(p.Position.Y)}", Palette.TextDim),
            (eigenvalues, Palette.Text),
            ($"J = [[{J(j[0, 0])}, {J(j[0, 1])}], [{J(j[1, 0])}, {J(j[1, 1])}]]", Palette.Text),
            ($"tr J = {J(p.Trace)}   det J = {Num(Math.Abs(p.Determinant) < 1e-12 * scale * scale ? 0 : p.Determinant)}", Palette.Text),
        ];
    }

    void DrawTooltip((string Text, uint Color)[] lines)
    {
        int s = UiScale;
        double advance = BitmapFont.Advance * s, lineHeight = BitmapFont.LineHeight * s, pad = 6 * s;
        double width = lines.Max(l => l.Text.Length) * advance + 2 * pad;
        double height = lines.Length * lineHeight + 2 * pad - (lineHeight - 9 * s);
        double x = mouse.X + 18 * s, y = mouse.Y + 18 * s;
        if (x + width > camera.Viewport.X - 4) x = mouse.X - 18 * s - width;
        if (y + height > camera.Viewport.Y - 4) y = mouse.Y - 18 * s - height;

        batch.Rect(x, y, width, height, Palette.PanelFill);
        batch.RectOutline(x, y, width, height, 1, Palette.PanelBorder);
        for (int i = 0; i < lines.Length; i++)
            BitmapFont.Draw(batch, lines[i].Text, x + pad, y + pad + i * lineHeight, s, lines[i].Color);
    }

    string CriticalSummary()
    {
        if (!critical.Available) return "critical points: n/a";
        if (critical.NotIsolated) return "critical points: not isolated";
        if (critical.Points.Count == 0) return "critical points: none in view";
        var groups = critical.Points
            .GroupBy(p => p.Kind)
            .OrderBy(g => g.Key)
            .Select(g => $"{g.Count()} {Plural(g.First().Name, g.Count())}");
        return "critical points: " + string.Join(", ", groups);

        static string Plural(string name, int count) =>
            count == 1 || name == "degenerate" ? name
            : name.EndsWith("focus") ? name[..^5] + "foci"
            : name + "s";
    }

    void DrawHelp()
    {
        int s = UiScale;
        double lineHeight = BitmapFont.LineHeight * s, x = 12 * s, y = camera.Viewport.Y - 12 * s - 2 * lineHeight + 3 * s;
        string heat = heatmap switch
        {
            HeatmapMode.Magnitude => "|F|",
            HeatmapMode.Divergence => "div",
            HeatmapMode.Curl => "curl",
            _ => "off",
        };
        BitmapFont.DrawShadowed(batch, "Enter edit   1-9 presets   Space pause   +/- speed   R reset view   Tab hide UI   F12 screenshot",
            x, y, s, Palette.TextDim);
        BitmapFont.DrawShadowed(batch, $"A arrows   P particles   H heatmap: {heat}   S streamline   C critical points   drag pan   click pin   right click clear   wheel zoom",
            x, y + lineHeight, s, Palette.TextDim);
    }

    void DrawStatus()
    {
        int s = UiScale;
        double advance = BitmapFont.Advance * s, lineHeight = BitmapFont.LineHeight * s;
        double right = camera.Viewport.X - 12 * s, y = 12 * s + 8 * s;
        string device = (renderer?.DeviceName ?? "").Replace("(R)", "").Replace("(TM)", "");
        string[] lines =
        [
            "ε Epsilon  ×  Vulkan",
            $"{device} · {fps:0} fps",
            $"{particles.Count} particles · {renderer?.SampleCount}x MSAA",
            (paused ? "PAUSED  " : "") + (speed != 1 ? $"speed ×{speed:0.##}" : ""),
        ];
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            uint color = i == 0 ? Palette.Accent : i == 3 ? Palette.Positive : Palette.TextDim;
            BitmapFont.DrawShadowed(batch, line, right - line.Length * advance, y + i * lineHeight, s, color);
        }
    }

    double Snap(double derivative) => Math.Abs(derivative) < stats.NoiseFloor(camera) ? 0 : derivative;

    static string Fit(string text, int columns) =>
        text.Length <= columns ? text : text[..Math.Max(0, columns - 1)] + "…";

    static string Num(double v)
    {
        if (double.IsNaN(v)) return "undefined";
        if (double.IsInfinity(v)) return v > 0 ? "inf" : "-inf";
        double a = Math.Abs(v);
        if (a != 0 && (a >= 1e5 || a < 1e-3)) return v.ToString("0.###e+0");
        string s = v.ToString("0.000");
        return s == "-0.000" ? "0.000" : s;
    }

    bool OverPanel(Vector2<double> p) =>
        (showUi || editing) && p.X >= panel.X && p.X <= panel.X + panel.W && p.Y >= panel.Y && p.Y <= panel.Y + panel.H;

    bool ApplyFormula(string text)
    {
        try
        {
            field = FieldDefinition.Parse(text);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            error = e.Message;
            return false;
        }
        error = null;
        preset = MatchPreset(field.Source);
        time = 0;
        fieldChanged = true;
        return true;
    }

    static int MatchPreset(string source) =>
        Array.FindIndex(Presets.All, p => p.Formula == source);

    void LoadPreset(int index)
    {
        if (index < 0 || index >= Presets.All.Length) return;
        editing = false;
        ApplyFormula(Presets.All[index].Formula);
    }

    void BeginEditing()
    {
        editing = true;
        error = null;
        editor.Set(field.Source);
    }

    void AddSeed(Vector2<double> point)
    {
        if (seeds.Count == MaxSeeds)
        {
            seeds.RemoveAt(0);
            seedLines.RemoveAt(0);
        }
        seeds.Add(point);
        seedLines.Add(new Streamline());
    }

    static string ScreenshotPath()
    {
        string folder = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) folder = Environment.CurrentDirectory;
        return Path.Combine(folder, $"vectorfield-{DateTime.Now:yyyyMMdd-HHmmss}.png");
    }

    void OnKeyDown(IKeyboard keyboard, Key key, int scancode)
    {
        if (Scripted || repeatKey == key) return;
        bool ctrl = keyboard.IsKeyPressed(Key.ControlLeft) || keyboard.IsKeyPressed(Key.ControlRight);

        if (editing)
        {
            switch (key)
            {
                case Key.Enter or Key.KeypadEnter:
                    if (ApplyFormula(editor.Text)) editing = false;
                    return;
                case Key.Escape:
                    editing = false;
                    error = null;
                    return;
                case Key.V when ctrl:
                    editor.Insert(keyboard.ClipboardText ?? "");
                    return;
                case Key.C when ctrl:
                    keyboard.ClipboardText = editor.Text;
                    return;
            }
            if (EditKey(key, ctrl))
            {
                repeatKey = key;
                repeatDelay = 0.42;
            }
            return;
        }

        switch (key)
        {
            case Key.Enter or Key.KeypadEnter: BeginEditing(); break;
            case >= Key.Number1 and <= Key.Number9: LoadPreset(key - Key.Number1); break;
            case >= Key.Keypad1 and <= Key.Keypad9: LoadPreset(key - Key.Keypad1); break;
            case Key.Space: paused = !paused; break;
            case Key.A: showArrows = !showArrows; break;
            case Key.P: showParticles = !showParticles; break;
            case Key.H: heatmap = (HeatmapMode)(((int)heatmap + 1) % 4); break;
            case Key.S: showStreamline = !showStreamline; break;
            case Key.C when !ctrl:
                showCritical = !showCritical;
                criticalKey = null;
                break;
            case Key.Tab: showUi = !showUi; break;
            case Key.Equal or Key.KeypadAdd: speed = Math.Min(speed * 1.25, 8); break;
            case Key.Minus or Key.KeypadSubtract: speed = Math.Max(speed / 1.25, 0.05); break;
            case Key.F12: screenshotRequest = ScreenshotPath(); break;
            case Key.C when ctrl: keyboard.ClipboardText = field.Source; break;
            case Key.R:
                camera.Reset();
                particles.Respawn(camera);
                break;
        }
    }

    void OnKeyUp(IKeyboard keyboard, Key key, int scancode)
    {
        if (repeatKey == key) repeatKey = null;
    }

    void OnKeyChar(IKeyboard keyboard, char c)
    {
        if (!Scripted && editing && !char.IsControl(c))
            editor.Insert(c.ToString());
    }

    bool EditKey(Key key, bool ctrl)
    {
        switch (key)
        {
            case Key.Backspace: editor.Backspace(ctrl); return true;
            case Key.Delete: editor.Delete(ctrl); return true;
            case Key.Left: editor.Left(ctrl); return true;
            case Key.Right: editor.Right(ctrl); return true;
            case Key.Home: editor.Home(); return false;
            case Key.End: editor.End(); return false;
            default: return false;
        }
    }

    void RepeatKeys(double dt)
    {
        if (repeatKey is not { } key || !editing)
        {
            repeatKey = null;
            return;
        }
        var keyboard = input?.Keyboards.FirstOrDefault();
        bool ctrl = keyboard is not null && (keyboard.IsKeyPressed(Key.ControlLeft) || keyboard.IsKeyPressed(Key.ControlRight));
        repeatDelay -= dt;
        while (repeatDelay <= 0)
        {
            EditKey(key, ctrl);
            repeatDelay += 0.035;
        }
    }

    void OnMouseDown(IMouse m, MouseButton button)
    {
        if (Scripted) return;
        if (button == MouseButton.Left)
        {
            if ((showUi || editing) && Inside(mouse, formulaRow))
            {
                if (!editing) BeginEditing();
                int column = (int)Math.Round((mouse.X - formulaTextX) / (BitmapFont.Advance * UiScale));
                editor.Move(formulaScroll + column);
                return;
            }
            if (OverPanel(mouse)) return;
            leftDown = true;
            dragging = false;
            pressPosition = lastMouse = mouse;
        }
        else if (button == MouseButton.Right)
        {
            seeds.Clear();
            seedLines.Clear();
        }
    }

    void OnMouseUp(IMouse m, MouseButton button)
    {
        if (Scripted || button != MouseButton.Left || !leftDown) return;
        leftDown = false;
        if (!dragging) AddSeed(camera.ScreenToWorld(mouse));
        dragging = false;
    }

    void OnMouseMove(IMouse m, System.Numerics.Vector2 position)
    {
        if (options.Mouse is not null || Scripted) return;
        var size = window.Size;
        var framebuffer = window.FramebufferSize;
        double sx = size.X > 0 ? (double)framebuffer.X / size.X : 1, sy = size.Y > 0 ? (double)framebuffer.Y / size.Y : 1;
        mouse = new Vector2<double>(position.X * sx, position.Y * sy);
        mouseInWindow = mouse.X >= 0 && mouse.Y >= 0 && mouse.X < framebuffer.X && mouse.Y < framebuffer.Y;

        if (!leftDown) return;
        if (!dragging && mouse.Distance(pressPosition) > 4) dragging = true;
        if (dragging) camera.Pan(mouse - lastMouse);
        lastMouse = mouse;
    }

    void OnScroll(IMouse m, ScrollWheel wheel)
    {
        if (Scripted || wheel.Y == 0) return;
        camera.ZoomAt(mouse, Math.Pow(0.88, wheel.Y));
        particles.Respawn(camera, 0.08);
    }

    static bool Inside(Vector2<double> p, (double X, double Y, double W, double H) r) =>
        p.X >= r.X && p.X <= r.X + r.W && p.Y >= r.Y && p.Y <= r.Y + r.H;

    public void Dispose()
    {
        OnClosing();
        window.Dispose();
    }
}
