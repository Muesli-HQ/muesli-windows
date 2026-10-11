using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Muesli.Windows.ComputerUse;

/// <summary>One planner tool call: the tool name and its JSON arguments.</summary>
public sealed record CuaToolCall(string Name, JsonObject Arguments)
{
    /// <summary>The planner's id for this call, echoed back with its result.</summary>
    public string? CallId { get; init; }

    public static CuaToolCall Parse(string name, string? argumentsJson, string? callId = null)
    {
        JsonObject arguments;
        try { arguments = JsonNode.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson) as JsonObject ?? new JsonObject(); }
        catch (JsonException) { throw new CuaInvalidToolCallException(name, argumentsJson ?? "", "Arguments are not a JSON object."); }
        return new CuaToolCall(name.Trim(), arguments) { CallId = callId };
    }

    public string? String(string key)
    {
        if (Arguments[key] is not JsonValue value) return null;
        var text = value.TryGetValue<string>(out var s) ? s : value.ToJsonString().Trim('"');
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    public double? Number(string key)
    {
        if (Arguments[key] is not JsonValue value) return null;
        if (value.TryGetValue<double>(out var number)) return number;
        return value.TryGetValue<string>(out var text) && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number) ? number : null;
    }

    public int? Integer(string key) => Number(key) is { } number ? (int)Math.Round(number) : null;

    public IReadOnlyList<string> Strings(string key) =>
        Arguments[key] switch
        {
            JsonArray array => array.Select(item => item?.ToString() ?? "").Where(item => item.Length > 0).ToArray(),
            JsonValue value when value.TryGetValue<string>(out var single) => single.Split('+', ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            _ => Array.Empty<string>(),
        };

    /// <summary>The element reference: element_index, or the number inside element_id ("e12").</summary>
    public int? ElementIndex =>
        Integer("element_index") ?? (String("element_id") is { } id && int.TryParse(id.TrimStart('e', 'E'), out var parsed) ? parsed : null);

    /// <summary>Target app: app_id, or the macOS-compatible app_bundle_id, or app_name.</summary>
    public string? App => String("app_id") ?? String("app_bundle_id") ?? String("app_name");

    public override string ToString() => $"{Name} {Arguments.ToJsonString()}";
}

public sealed class CuaInvalidToolCallException(string name, string arguments, string message) : Exception(message)
{
    public string ToolName { get; } = name;
    public string RawArguments { get; } = arguments;
}

public enum CuaResultStatus { Executed, Failed, Unsupported, NeedsConfirmation, Cancelled }

public sealed record CuaResult(CuaResultStatus Status, string Message)
{
    public static CuaResult Ok(string message) => new(CuaResultStatus.Executed, message);
    public static CuaResult Fail(string message) => new(CuaResultStatus.Failed, message);
    public bool Succeeded => Status == CuaResultStatus.Executed;
}

/// <summary>The desktop Computer Use drives. Calls block; run them off the UI thread.</summary>
public interface ICuaDesktop : IDisposable
{
    /// <summary>Observes the app named by <paramref name="app"/> (id or name), else the current
    /// target, else the window the user is working in.</summary>
    CuaWindowState Observe(string? app, long? windowId, bool includeScreenshot);

    CuaResult Execute(CuaToolCall call, CancellationToken cancellationToken);
}

/// <summary>
/// Windows apps Computer Use must never drive, following OpenAI's Windows computer-use rules:
/// terminals and the Run dialog (no command execution through the UI), password managers,
/// security tools, credential prompts, and AI agent apps.
/// </summary>
public static class CuaSafety
{
    private static readonly HashSet<string> BlockedProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "WindowsTerminal", "wt", "cmd", "powershell", "pwsh", "powershell_ise", "conhost", "OpenConsole", "mintty", "bash", "wsl",
        "regedit", "SecHealthUI", "SecurityHealthHost", "MsMpEng", "CredentialUIBroker", "consent", "LockApp",
        "1Password", "KeePass", "KeePassXC", "Bitwarden", "Dashlane", "LastPass", "Enpass", "RoboForm", "NordPass", "Keeper",
        "ChatGPT", "Codex",
    };

    public static string? BlockReason(string appId, string windowTitle)
    {
        if (BlockedProcesses.Contains(appId))
            return $"Muesli does not control {appId}: terminals, password managers, security tools and sign-in prompts are off limits.";
        if (appId.Equals("explorer", StringComparison.OrdinalIgnoreCase) && windowTitle.Equals("Run", StringComparison.OrdinalIgnoreCase))
            return "Muesli does not use the Windows Run dialog.";
        return null;
    }
}
