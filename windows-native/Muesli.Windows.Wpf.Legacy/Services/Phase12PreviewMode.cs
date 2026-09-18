using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Muesli.Windows.Services;

/// <summary>
/// Parse-first, presentation-only entry point for Phase 12 visual checks. This is the
/// only preview composition root: it has no store, filesystem, registry, network,
/// device, logging, native-client, hook, tray, or production lifecycle dependency.
/// </summary>
public sealed record Phase12PreviewMode(string Page, string Case, string Theme, string Size, string? Diagnostic = null)
{
    public sealed record PreviewCase(string Page, string Case, string StateCategory, string Description);

    public static readonly IReadOnlyList<PreviewCase> Cases =
    [
        new("dashboard", "empty", "empty", "Zero history and zero statistics."),
        new("dashboard", "long-text", "long-text", "Presentation-only wrapping stress."),
        new("meetings", "empty", "empty", "No meetings, recordings, or transcripts."),
        new("meetings", "long-text", "long-text", "Presentation-only wrapping stress."),
        new("search", "empty", "empty", "No query results or stored history."),
        new("dictionary", "empty", "empty", "No stored dictionary entries."),
        new("models", "ready", "ready", "Model setup ready presentation."),
        new("models", "downloading", "loading", "Determinate progress and preview-only cancel affordance."),
        new("models", "failure", "failure", "Retry and diagnostics guidance."),
        new("models", "offline", "offline", "Offline/unavailable guidance."),
        new("shortcuts", "conflict", "failure", "Conflict and help guidance."),
        new("settings", "normal", "normal", "Normal settings surface without saved settings."),
        new("settings", "startup-unavailable", "failure", "Windows startup registration unavailable guidance."),
        new("about", "diagnostics", "normal", "Privacy, logs, diagnostics, and update guidance."),
        new("onboarding", "welcome", "empty", "Welcome setup state."),
        new("onboarding", "long-text", "long-text", "Long wrapping setup guidance."),
        new("onboarding", "downloading", "loading", "Determinate setup model preparation."),
        new("onboarding", "failure", "failure", "Model failure and retry guidance."),
        new("onboarding", "offline", "offline", "Offline setup guidance."),
        new("onboarding", "permissions-denied", "failure", "Microphone permissions denied guidance."),
        new("onboarding", "completed", "completed", "Completed setup state."),
        new("tour", "feature-tour", "normal", "Actual feature tour window.")
    ];
    public static readonly IReadOnlyList<string> Themes = ["light", "dark"];
    public static readonly IReadOnlyList<string> Sizes = ["narrow", "normal"];
    public static readonly IReadOnlyList<string> RequiredScales = ["100", "125", "150", "200"];

    // Legacy callers use Scenario; it remains the unqualified state/case name.
    public string Scenario => Case;
    public bool IsValid => Diagnostic is null;
    /// <summary>Preview composition is presentation-only; callers must block production actions.</summary>
    public bool BlocksProductionActions => true;
    public int MatrixCellCount => Cases.Count * Themes.Count * Sizes.Count * RequiredScales.Count;
    public string StateCategory => Cases.FirstOrDefault(candidate => candidate.Page == Page && candidate.Case == Case)?.StateCategory ?? "invalid";
    public string PresentationStatus => StateCategory switch
    {
        "loading" => "Preview-only loading: 42% complete. Cancel is disabled and has no side effects.",
        "failure" => "Preview-only failure: retry and diagnostics are explanatory only; no action is available.",
        "offline" => "Preview-only offline state: no network or model cache was checked.",
        "long-text" => "Preview-only long text: no user history, transcript, or statistics were loaded.",
        "completed" => "Preview-only completed state: no settings or onboarding progress was read or written.",
        _ => "Preview isolation: zero user history, zero statistics, and no production services."
    };
    public string Banner => $"VISUAL VERIFICATION MODE · page: {Page} · case: {Case} ({StateCategory}) · theme: {Theme} · viewport: {Size} · isolated / no user data";
    public string PageStateMessage => $"{StateCategory.ToUpperInvariant()} · {PresentationStatus}";
    public int OnboardingStep => Case switch
    {
        "downloading" or "failure" or "offline" => 2,
        "permissions-denied" => 1,
        "completed" => OnboardingProgress.LastStep,
        _ => 0
    };

    public static bool TryParse(IEnumerable<string> args, out Phase12PreviewMode mode)
    {
        var values = args.ToArray();
        var hasPreview = values.Any(arg => arg.StartsWith("--phase12-preview", StringComparison.OrdinalIgnoreCase) || arg.StartsWith("--phase12-page-case", StringComparison.OrdinalIgnoreCase));
        if (!hasPreview) { mode = default!; return false; }

        var legacy = Option(values, "--phase12-preview", null, out var legacyError);
        var pageCase = Option(values, "--phase12-page-case", null, out var pageCaseError);
        var theme = Option(values, "--phase12-theme", "light", out var themeError)!;
        var size = Option(values, "--phase12-size", "normal", out var sizeError)!;
        var error = legacyError ?? pageCaseError ?? themeError ?? sizeError;
        string page = "onboarding", @case = "welcome";
        if (error is null && legacy is not null && pageCase is not null) error = "Use either --phase12-preview or --phase12-page-case, not both.";
        if (error is null && pageCase is not null)
        {
            var parts = pageCase.Split('/', 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2) error = "--phase12-page-case must be page/case.";
            else { page = parts[0]; @case = parts[1]; }
        }
        else if (error is null && legacy is not null)
        {
            (page, @case) = legacy.ToLowerInvariant() switch
            {
                "empty" => ("onboarding", "welcome"),
                "model-downloading" => ("onboarding", "downloading"),
                "model-failure" => ("onboarding", "failure"),
                "tour" => ("tour", "feature-tour"),
                _ => ("onboarding", legacy.ToLowerInvariant())
            };
        }
        if (error is null && !Cases.Any(candidate => candidate.Page == page && candidate.Case == @case)) error = $"Unsupported Phase 12 preview case '{page}/{@case}'. Expected one of: {string.Join(", ", Cases.Select(candidate => $"{candidate.Page}/{candidate.Case}"))}.";
        if (error is null && !Themes.Contains(theme, StringComparer.Ordinal)) error = $"Unsupported --phase12-theme value '{theme}'.";
        if (error is null && !Sizes.Contains(size, StringComparer.Ordinal)) error = $"Unsupported --phase12-size value '{size}'.";
        mode = new Phase12PreviewMode(page, @case, theme, size, error);
        return true;
    }

    public Window CreateWindow()
    {
        ApplyPreviewTheme();
        if (!IsValid) return CreateDiagnosticWindow();
        Window window = Page switch
        {
            "tour" => new FeatureTourWindow(visualVerification: true) { Title = "Muesli visual verification · feature tour" },
            "onboarding" => new OnboardingWindow(CreateContext(), CreateProgress()) { Title = $"Muesli visual verification · onboarding/{Case}" },
            _ => MainWindow.CreateVisualPreview(this)
        };
        ApplyViewport(window);
        return window;
    }

    private static string? Option(IReadOnlyList<string> args, string name, string? fallback, out string? error)
    {
        var values = args.Where(arg => arg.StartsWith(name, StringComparison.OrdinalIgnoreCase)).ToArray(); error = null;
        if (values.Length == 0) return fallback;
        if (values.Length != 1 || !values[0].StartsWith(name + "=", StringComparison.OrdinalIgnoreCase)) { error = $"{name} must be supplied exactly once as {name}=<value>."; return fallback; }
        var value = values[0][(name.Length + 1)..];
        if (string.IsNullOrWhiteSpace(value)) { error = $"{name} needs a value."; return fallback; }
        return value;
    }

    private OnboardingProgress CreateProgress() => Case switch
    {
        "downloading" or "failure" or "offline" or "permissions-denied" => new() { Step = OnboardingStep, LastStatus = PresentationStatus },
        "completed" => new() { Step = OnboardingProgress.LastStep, MicrophoneVerified = true, ModelsVerified = true, HotkeyVerified = true, PipelineVerified = true, LastStatus = PresentationStatus },
        _ => new() { Step = 0, LastStatus = PresentationStatus }
    };

    private OnboardingContext CreateContext()
    {
        var model = new TranscriptionModelDefinition("preview-local-model", "Preview local model", "Presentation-only model record", "English", "600 MB", NativeAsrModelKind.Whisper, "preview-local-model", "", "", new Dictionary<string, string>());
        var snapshot = Case switch
        {
            "downloading" => new OnboardingModelSnapshot("Preview-only download 42% of 600 MB · no operation started", false, true, false, true, false, false, 42),
            "failure" => new OnboardingModelSnapshot("Preview-only failure: checksum could not be verified. Retry is disabled.", false, false, false, false, true, false),
            "offline" => new OnboardingModelSnapshot("Preview-only offline: connect before preparing this model.", false, false, true, false, false, false),
            "completed" => new OnboardingModelSnapshot("Preview-only ready state.", true, false, false, false, false, true),
            _ => new OnboardingModelSnapshot("Preview-only: not prepared; no cache inspected.", false, false, true, false, false, false)
        };
        return new OnboardingContext(["Preview microphone (not opened)"], ["F8", "F9"], [model], [LiveModelChoice.Off], new OnboardingDraft("", "Preview microphone (not opened)", "F8", model.Id, model.Id, null, false, true, "Top Center", "local"), "http://localhost:11434",
            _ => Task.FromResult(new MicrophoneProbeResult(false, 0, 0, null, "", "Preview only", MicrophoneProbeFailure.Denied, "Preview mode never opens a microphone.")), NoOperation, NoOperation, _ => { }, NoOperation, _ => snapshot, NoOperation, NoOperation, _ => { }, NoOperation, _ => snapshot, _ => Task.FromResult(""), _ => true, false, false, ["Top Center", "Bottom Center"], () => { }, () => { }, () => { }, _ => { }, _ => { }, () => { }, IsPreview: true, PreviewDescription: PresentationStatus);
    }
    private static Task NoOperation(string _, IProgress<ModelDownloadProgress> __, CancellationToken ___) => Task.CompletedTask;

    private void ApplyViewport(Window window)
    {
        var requested = Size == "narrow" ? new System.Windows.Size(780, 600) : new System.Windows.Size(980, 720);
        window.Width = requested.Width; window.Height = requested.Height;
        // After a real HWND/source exists, clamp DIPs to its monitor work area. No viewport DIP is DPI evidence.
        window.ContentRendered += (_, _) => WindowPlacementService.FitToWorkArea(window, requested.Width, requested.Height);
    }
    private void ApplyPreviewTheme()
    {
        var resources = System.Windows.Application.Current.Resources; var light = Theme == "light";
        SetBrush(resources, "BackgroundDeepBrush", light ? "#F6F7F9" : "#111214"); SetBrush(resources, "BackgroundBaseBrush", light ? "#FFFFFF" : "#161719"); SetBrush(resources, "BackgroundRaisedBrush", light ? "#F0F2F5" : "#1C1D20"); SetBrush(resources, "BackgroundHoverBrush", light ? "#E7EBF0" : "#232528"); SetBrush(resources, "SurfacePrimaryBrush", light ? "#FFFFFF" : "#262830"); SetBrush(resources, "SurfaceSelectedBrush", light ? "#E2EAF7" : "#2E3340"); SetBrush(resources, "TextPrimaryBrush", light ? "#1D2430" : "#EBFFFFFF"); SetBrush(resources, "TextSecondaryBrush", light ? "#4C596B" : "#9EFFFFFF"); SetBrush(resources, "TextTertiaryBrush", light ? "#64748B" : "#66FFFFFF"); SetBrush(resources, "BorderBrushSoft", light ? "#2436475A" : "#12FFFFFF"); SetBrush(resources, "BorderBrushMedium", light ? "#3D36475A" : "#1CFFFFFF");
    }
    private static void SetBrush(ResourceDictionary resources, string key, string color) => resources[key] = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(color)!);
    private Window CreateDiagnosticWindow() => new() { Title = "Muesli visual verification · invalid arguments", Width = 720, Height = 360, Content = new TextBlock { Margin = new Thickness(28), TextWrapping = TextWrapping.Wrap, Text = $"VISUAL VERIFICATION MODE\n\n{Diagnostic}\n\nThe app did not continue into production startup. Use --phase12-page-case=<page/case>." } };
}
