using System.Text.Json;
using System.Text.Json.Serialization;
using Muesli.Windows.Services;

namespace Muesli.Windows.Core.Services;

/// <summary>
/// Versioned, current-user-only named-pipe contract between the WinUI owner and the WPF indicator
/// companion. WinUI sends complete, idempotent <see cref="IndicatorSnapshot"/> messages down the
/// pipe; the companion sends <see cref="IndicatorCommand"/> messages back up. Neither side ever
/// closes over product state — commands carry a session identifier so stale messages from a dead
/// companion are ignored.
/// </summary>
public static class IndicatorProtocol
{
    public const int CurrentVersion = 1;

    /// <summary>Prefix for the per-instance pipe name; the caller appends its unique identity.</summary>
    public const string PipeNamePrefix = "Muesli.Indicator";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    /// <summary>
    /// Builds the pipe name for one WinUI↔companion pair. The instance id is owned by the WinUI
    /// process (a per-launch token) so a second Muesli instance cannot connect to the first's
    /// companion.
    /// </summary>
    public static string PipeNameFor(string instanceId)
    {
        var token = new string((instanceId ?? "").Where(char.IsLetterOrDigit).ToArray());
        if (token.Length == 0) token = "default";
        return $"{PipeNamePrefix}.{token}";
    }

    /// <summary>One-way snapshot pipe (WinUI → companion).</summary>
    public static string SnapshotPipeName(string instanceId) => PipeNameFor(instanceId) + ".down";

    /// <summary>One-way command pipe (companion → WinUI).</summary>
    public static string CommandPipeName(string instanceId) => PipeNameFor(instanceId) + ".up";

    public static bool IsSupportedVersion(int version) => version == CurrentVersion;

    /// <summary>True when a command carried a session id older than the live session.</summary>
    public static bool IsStaleSession(long commandSession, long liveSession) => commandSession != liveSession;

    /// <summary>
    /// True when <paramref name="current"/> changes anything a renderer must rebuild or reposition
    /// for. Every snapshot field except <see cref="IndicatorSnapshot.Amplitude"/> participates.
    /// </summary>
    /// <remarks>
    /// The WinUI owner sends amplitude-only frames — <c>latest with { Amplitude = … }</c> — at up to
    /// 30 fps while recording. Those frames are structurally identical to the previous frame and
    /// must not trigger a content rebuild, a reposition, or animation-timer churn. Treating them as
    /// structural is the confirmed cause of the waveform snapping and the drag redraw trails: a
    /// full render per sample destroyed the bars, reset their heights, restarted the animation
    /// timer and recomputed the window position while the pointer was still dragging it.
    /// </remarks>
    public static bool IsStructuralChange(IndicatorSnapshot previous, IndicatorSnapshot current) =>
        previous.Version != current.Version ||
        previous.SessionId != current.SessionId ||
        !string.Equals(previous.State, current.State, StringComparison.Ordinal) ||
        !string.Equals(previous.Owner, current.Owner, StringComparison.Ordinal) ||
        previous.Visible != current.Visible ||
        !string.Equals(previous.HotkeyLabel, current.HotkeyLabel, StringComparison.Ordinal) ||
        !string.Equals(previous.Message, current.Message, StringComparison.Ordinal) ||
        !string.Equals(previous.RecordingColorHex, current.RecordingColorHex, StringComparison.Ordinal) ||
        previous.MeetingPaused != current.MeetingPaused ||
        previous.HandsFree != current.HandsFree ||
        !string.Equals(previous.IndicatorAnchor, current.IndicatorAnchor, StringComparison.Ordinal) ||
        previous.SavedLeft != current.SavedLeft ||
        previous.SavedTop != current.SavedTop ||
        !string.Equals(previous.Theme, current.Theme, StringComparison.Ordinal) ||
        previous.HighContrast != current.HighContrast;

    public static string SerializeSnapshot(IndicatorSnapshot snapshot) =>
        JsonSerializer.Serialize(snapshot, Json);

    public static IndicatorSnapshot? DeserializeSnapshot(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var snapshot = JsonSerializer.Deserialize<IndicatorSnapshot>(json, Json);
            return snapshot is null || !IsSupportedVersion(snapshot.Version) ? null : snapshot;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string SerializeCommand(IndicatorCommand command) =>
        JsonSerializer.Serialize(command, Json);

    public static IndicatorCommand? DeserializeCommand(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var command = JsonSerializer.Deserialize<IndicatorCommand>(json, Json);
            return command is null || !IsSupportedVersion(command.Version) ? null : command;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>Well-known command type identifiers sent from the companion to the WinUI owner.</summary>
public static class IndicatorCommandType
{
    public const string Ready = "ready";
    public const string Stop = "stop";
    public const string Cancel = "cancel";
    public const string Pause = "pause";
    public const string Resume = "resume";
    public const string Drag = "drag";
    public const string HoverEnter = "hover-enter";
    public const string HoverExit = "hover-exit";
    public const string Heartbeat = "heartbeat";
    public const string Exit = "exit";
    public const string MeetingNotificationAction = "meeting-notification-action";
    public const string MeetingNotificationDismiss = "meeting-notification-dismiss";
    public const string MeetingNotificationAutoDismiss = "meeting-notification-auto-dismiss";
}

/// <summary>The owner labels WinUI publishes so the companion can render meeting vs dictation controls.</summary>
public static class IndicatorOwnerKind
{
    public const string None = "none";
    public const string Dictation = "dictation";
    public const string Meeting = "meeting";
    public const string ComputerUse = "computer-use";
}

/// <summary>
/// One complete, idempotent indicator frame. WinUI sends the whole frame after every state change
/// and again after every (re)connect, so the companion never needs to replay or infer state.
/// </summary>
public sealed record IndicatorSnapshot
{
    public int Version { get; init; } = IndicatorProtocol.CurrentVersion;

    /// <summary>Monotonic session/state identifier; a command quoting an older value is stale.</summary>
    public long SessionId { get; init; }

    /// <summary>Visual state name: <c>idle</c>, <c>preparing</c>, <c>recording</c>, <c>transcribing</c>, <c>success</c>, <c>error</c>.</summary>
    public string State { get; init; } = "idle";

    /// <summary>Owner: <see cref="IndicatorOwnerKind"/>.</summary>
    public string Owner { get; init; } = IndicatorOwnerKind.None;

    public bool Visible { get; init; }
    public string HotkeyLabel { get; init; } = "";
    public string Message { get; init; } = "";

    /// <summary>Recording accent in <c>RRGGBB</c> form (the user-configurable recording colour).</summary>
    public string RecordingColorHex { get; init; } = "1e1e2e";

    public bool MeetingPaused { get; init; }

    /// <summary>
    /// True when double-tap (hands-free) dictation is enabled, so the idle hover copy can mention
    /// it. Matches the macOS reference's "Hold … or double-tap for hands-free" hint.
    /// </summary>
    public bool HandsFree { get; init; }

    public string IndicatorAnchor { get; init; } = "Middle Right";
    public double? SavedLeft { get; init; }
    public double? SavedTop { get; init; }

    public string Theme { get; init; } = "dark";
    public bool HighContrast { get; init; }

    /// <summary>Live microphone RMS level in [0,1]; the companion maps it to dB and smooths it at 30 fps.</summary>
    public float Amplitude { get; init; }

    public IndicatorMeetingNotification? MeetingNotification { get; init; }

    [JsonIgnore]
    public bool IsActive =>
        State is "preparing" or "recording" or "transcribing";
}

/// <summary>A product command marshalled from the companion back to the WinUI owner. Never executed in the WPF process.</summary>
public sealed record IndicatorCommand
{
    public int Version { get; init; } = IndicatorProtocol.CurrentVersion;
    public long SessionId { get; init; }
    public string Type { get; init; } = "";
    public string? NotificationPromptId { get; init; }
    public string? NotificationAction { get; init; }

    /// <summary>Repaired custom centre after a drag completes, in DIPs.</summary>
    public double? DragLeft { get; init; }
    public double? DragTop { get; init; }
    /// <summary>Screen-pixel bounds used to place the meeting transcript beside the companion.</summary>
    public int? IndicatorX { get; init; }
    public int? IndicatorY { get; init; }
    public int? IndicatorWidth { get; init; }
    public int? IndicatorHeight { get; init; }
}

public sealed record IndicatorMeetingNotification(
    string PromptId,
    string Title,
    string Subtitle,
    string Platform,
    string Glyph,
    string AccentHex,
    string ShortLabel,
    string ActionLabel,
    bool HasSplitAction,
    Muesli.Windows.Services.MeetingJoinDefaultAction DefaultAction,
    double DismissAfterSeconds,
    MeetingNotificationAction SingleAction = MeetingNotificationAction.StartTranscribing);
