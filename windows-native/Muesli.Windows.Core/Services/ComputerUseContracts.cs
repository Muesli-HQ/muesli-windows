namespace Muesli.Windows.Services;

/// <summary>
/// Framework-neutral Computer Use contract. The planner and Windows UI Automation adapters are
/// free to live in different hosts, but they exchange only these records and interfaces.
/// </summary>
public sealed record ComputerUseActivationToken
{
    internal ComputerUseActivationToken(Guid value) => Value = value;
    internal Guid Value { get; }
}

public sealed record ExplicitVoiceCommand
{
    internal ExplicitVoiceCommand(string text, DateTimeOffset issuedAtUtc)
    {
        Text = text;
        IssuedAtUtc = issuedAtUtc;
    }

    public string Text { get; }
    public DateTimeOffset IssuedAtUtc { get; }
}

public enum ComputerUsePlannerProvider { OpenAI }
public enum ComputerUseRunStatus { Disabled, Rejected, Completed, Cancelled, TimedOut, Failed, StaleObservation, ActionLimitReached }
public enum ComputerUseStage { AcquiringObservation, Planning, AwaitingConfirmation, Executing, Completed, Failed }
public enum ComputerUseActionKind { FocusWindow, InvokeElement, SetText, BrowserNavigate, BrowserInvoke }
public enum ComputerUseRisk { None, Destructive, External, Financial, Credential, Irreversible }

public sealed record ComputerUsePrivacyOptions
{
    public bool IncludeScreenshots { get; init; }
    public bool UserApprovedScreenCapture { get; init; }
    public bool IncludeWindowText { get; init; }
    public bool IncludeBrowserPageText { get; init; }
}

public sealed record ComputerUseOptions
{
    public bool Enabled { get; init; }
    public ComputerUsePlannerProvider Provider { get; init; } = ComputerUsePlannerProvider.OpenAI;
    public string Model { get; init; } = string.Empty;
    public TimeSpan PlannerTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan OverallTimeout { get; init; } = TimeSpan.FromMinutes(2);
    public TimeSpan ConfirmationTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan PerActionTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public int MaximumActionCount { get; init; } = 5;
    public IReadOnlyList<string> AllowedApplications { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> AllowedBrowserDomains { get; init; } = Array.Empty<string>();
    public ComputerUsePrivacyOptions Privacy { get; init; } = new();
}

public sealed record ComputerUseTarget(string ApplicationId, string? AutomationId, string? BrowserDomain, int? X, int? Y);
public sealed record ComputerUseAction(ComputerUseActionKind Kind, ComputerUseTarget Target, string? Value, ComputerUseRisk Risk);
public sealed record ComputerUsePlan(int SchemaVersion, string ObservationId, bool Completed, IReadOnlyList<ComputerUseAction> Actions);
public sealed record ComputerUseObservation(
    string Id,
    string Fingerprint,
    string ApplicationId,
    bool ContainsSecrets,
    bool ContainsPasswordField,
    bool ContainsClipboardData,
    VirtualScreenBounds VirtualScreen,
    IReadOnlyList<ComputerUseElement> Elements)
{
    /// <summary>Opaque, local-only binding for an explicitly supported browser page; never sent as page content.</summary>
    public string? BrowserContextFingerprint { get; init; }
}

public sealed record ComputerUseElement(string AutomationId, string ControlType, bool IsPassword, bool IsEnabled, int Left, int Top, int Width, int Height);
public sealed record VirtualScreenBounds(int Left, int Top, int Width, int Height)
{
    public bool Contains(int x, int y) => x >= Left && y >= Top && x < Left + Width && y < Top + Height;
}

public sealed record ComputerUsePlanningRequest(
    int SchemaVersion,
    ComputerUsePlannerProvider Provider,
    string Model,
    string Command,
    ComputerUseObservation Observation,
    IReadOnlyList<string> AllowedApplications,
    IReadOnlyList<string> AllowedBrowserDomains);

public sealed record ComputerUseExecutionResult(bool Succeeded, string? Error = null);
public sealed record ComputerUseTraceEntry(int Index, ComputerUseActionKind Kind, string ApplicationId, ComputerUseRisk Risk, string Outcome, string? Error, DateTimeOffset AtUtc);
public sealed record ComputerUseRunResult(Guid RunId, ComputerUseRunStatus Status, DateTimeOffset StartedAtUtc, DateTimeOffset CompletedAtUtc, IReadOnlyList<ComputerUseTraceEntry> Trace, string? Error);
public sealed record ComputerUseStatus(ComputerUseStage Stage, string Message, int ActionNumber, int ActionCount);

public interface IComputerUseObservationSource
{
    Task<ComputerUseObservation> AcquireAsync(ComputerUsePrivacyOptions privacy, CancellationToken cancellationToken);
}

public interface IComputerUsePlannerProvider
{
    Task<string> PlanAsync(ComputerUsePlanningRequest request, CancellationToken cancellationToken);
}

public interface IComputerUseActionExecutor
{
    Task<ComputerUseExecutionResult> ExecuteAsync(ComputerUseAction action, ComputerUseObservation observation, CancellationToken cancellationToken);
}

public interface IComputerUseConfirmation
{
    Task<bool> ConfirmAsync(ComputerUseAction action, CancellationToken cancellationToken);
}

public interface IComputerUseStatusSink
{
    void Publish(ComputerUseStatus status);
}

public interface IBrowserAutomationAdapter
{
    Task<ComputerUseExecutionResult> NavigateAsync(string domain, string value, string expectedContextFingerprint, CancellationToken cancellationToken);
    Task<ComputerUseExecutionResult> InvokeAsync(string domain, string targetId, string expectedContextFingerprint, CancellationToken cancellationToken);
}

public interface IApprovedLocalUiAutomationAdapter
{
    Task<ComputerUseExecutionResult> ExecuteAsync(ComputerUseAction action, CancellationToken cancellationToken);
}

public interface IComputerUseActionVisualizer
{
    Task ShowAsync(ComputerUseAction action, ComputerUseElement? resolvedTarget, CancellationToken cancellationToken);
}

public sealed class NullComputerUseStatusSink : IComputerUseStatusSink
{
    public static NullComputerUseStatusSink Instance { get; } = new();
    public void Publish(ComputerUseStatus status) { }
}

public sealed class NullComputerUseActionVisualizer : IComputerUseActionVisualizer
{
    public static NullComputerUseActionVisualizer Instance { get; } = new();
    public Task ShowAsync(ComputerUseAction action, ComputerUseElement? resolvedTarget, CancellationToken cancellationToken) => Task.CompletedTask;
}
