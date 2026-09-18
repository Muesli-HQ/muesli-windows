using System.Text.Json;
using System.Text.Json.Serialization;

namespace Muesli.Windows.Services;

public static class ComputerUseContract
{
    public const int Version = 1;
}

/// <summary>
/// Strict, host-independent validation for planner responses. It rejects coordinates, unapproved
/// applications/domains, malformed JSON, and actions whose locally-derived risk was omitted.
/// </summary>
public static class ComputerUsePlanValidator
{
    public static bool TryParse(string rawJson, string observationId, ComputerUseOptions options, out ComputerUsePlan plan, out string error)
    {
        plan = new ComputerUsePlan(0, string.Empty, false, Array.Empty<ComputerUseAction>());
        error = "Malformed planner response.";
        if (string.IsNullOrWhiteSpace(rawJson) || rawJson.Length > 128 * 1024) return false;
        try
        {
            using var document = JsonDocument.Parse(rawJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !HasExactly(root, "schemaVersion", "observationId", "completed", "actions")) return false;
            if (!root.TryGetProperty("schemaVersion", out var version) || version.GetInt32() != ComputerUseContract.Version ||
                !root.TryGetProperty("observationId", out var observed) || observed.GetString() != observationId ||
                !root.TryGetProperty("completed", out var completed) || completed.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                !root.TryGetProperty("actions", out var actionsJson) || actionsJson.ValueKind != JsonValueKind.Array ||
                actionsJson.GetArrayLength() > 1 || (completed.GetBoolean() ? actionsJson.GetArrayLength() != 0 : actionsJson.GetArrayLength() != 1)) return false;
            var actions = new List<ComputerUseAction>();
            foreach (var item in actionsJson.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || !HasExactly(item, "kind", "target", "value", "risk") ||
                    !item.TryGetProperty("kind", out var kindJson) || kindJson.ValueKind != JsonValueKind.String ||
                    !item.TryGetProperty("risk", out var riskJson) || riskJson.ValueKind != JsonValueKind.String ||
                    !item.TryGetProperty("value", out var valueJson) || valueJson.ValueKind is not (JsonValueKind.String or JsonValueKind.Null) ||
                    !item.TryGetProperty("target", out var targetJson) || targetJson.ValueKind != JsonValueKind.Object ||
                    !HasExactly(targetJson, "applicationId", "automationId", "browserDomain", "x", "y") ||
                    !targetJson.TryGetProperty("applicationId", out var applicationJson) || applicationJson.ValueKind != JsonValueKind.String ||
                    !targetJson.TryGetProperty("automationId", out var automationJson) || automationJson.ValueKind is not (JsonValueKind.String or JsonValueKind.Null) ||
                    !targetJson.TryGetProperty("browserDomain", out var domainJson) || domainJson.ValueKind is not (JsonValueKind.String or JsonValueKind.Null) ||
                    !targetJson.TryGetProperty("x", out var xJson) || xJson.ValueKind != JsonValueKind.Null ||
                    !targetJson.TryGetProperty("y", out var yJson) || yJson.ValueKind != JsonValueKind.Null) return false;
                var action = JsonSerializer.Deserialize<ComputerUseAction>(item.GetRawText(), Options);
                if (action is null || !Enum.IsDefined(action.Kind) || !Enum.IsDefined(action.Risk) || action.Target is null || !ValidateAction(action, options)) return false;
                actions.Add(action with { Risk = EffectiveRisk(action) });
            }
            plan = new ComputerUsePlan(version.GetInt32(), observed.GetString()!, completed.GetBoolean(), actions);
            error = string.Empty;
            return true;
        }
        catch (JsonException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (FormatException) { return false; }
        catch (OverflowException) { return false; }
    }

    private static bool ValidateAction(ComputerUseAction action, ComputerUseOptions options)
    {
        if (string.IsNullOrWhiteSpace(action.Target.ApplicationId) || action.Target.ApplicationId.Length > 128 ||
            !options.AllowedApplications.Contains(action.Target.ApplicationId, StringComparer.OrdinalIgnoreCase)) return false;
        var minimumRisk = DerivedRisk(action);
        if (minimumRisk != ComputerUseRisk.None && action.Risk == ComputerUseRisk.None) return false;
        if (action.Target.X is not null || action.Target.Y is not null) return false;
        if (action.Kind is ComputerUseActionKind.BrowserNavigate or ComputerUseActionKind.BrowserInvoke)
        {
            if (!TryCanonicalHost(action.Target.BrowserDomain, out var targetHost) || !options.AllowedBrowserDomains.Any(d => TryCanonicalHost(d, out var allowed) && allowed == targetHost)) return false;
            if (action.Kind == ComputerUseActionKind.BrowserNavigate)
                return string.IsNullOrEmpty(action.Target.AutomationId) && Uri.TryCreate(action.Value, UriKind.Absolute, out var url) &&
                       url.Scheme == Uri.UriSchemeHttps && url.Port == 443 && string.IsNullOrEmpty(url.UserInfo) && CanonicalHost(url.Host) == targetHost;
            return !string.IsNullOrWhiteSpace(action.Target.AutomationId) && action.Target.AutomationId.Length <= 256 && string.IsNullOrEmpty(action.Value);
        }
        if (!string.IsNullOrEmpty(action.Target.BrowserDomain)) return false;
        if (action.Kind == ComputerUseActionKind.FocusWindow)
            return string.IsNullOrEmpty(action.Target.AutomationId) && string.IsNullOrEmpty(action.Value);
        if (string.IsNullOrWhiteSpace(action.Target.AutomationId) || action.Target.AutomationId.Length > 256) return false;
        return action.Kind switch
        {
            ComputerUseActionKind.InvokeElement => string.IsNullOrEmpty(action.Value),
            ComputerUseActionKind.SetText => action.Value is { Length: > 0 and <= 4096 },
            _ => false
        };
    }

    private static ComputerUseRisk DerivedRisk(ComputerUseAction action)
    {
        if (action.Kind is ComputerUseActionKind.BrowserNavigate or ComputerUseActionKind.BrowserInvoke or ComputerUseActionKind.SetText) return ComputerUseRisk.External;
        if (action.Kind == ComputerUseActionKind.InvokeElement) return ComputerUseRisk.Irreversible;
        var target = action.Target.AutomationId?.ToLowerInvariant() ?? string.Empty;
        if (target.Contains("credential") || target.Contains("password") || target.Contains("signin")) return ComputerUseRisk.Credential;
        if (target.Contains("payment") || target.Contains("purchase") || target.Contains("transfer") || target.Contains("pay")) return ComputerUseRisk.Financial;
        if (target.Contains("delete") || target.Contains("remove") || target.Contains("discard")) return ComputerUseRisk.Destructive;
        if (target.Contains("send") || target.Contains("share") || target.Contains("publish")) return ComputerUseRisk.External;
        return ComputerUseRisk.None;
    }

    private static ComputerUseRisk EffectiveRisk(ComputerUseAction action) =>
        DerivedRisk(action) == ComputerUseRisk.None ? action.Risk : action.Risk == ComputerUseRisk.None ? DerivedRisk(action) : action.Risk;

    private static bool HasExactly(JsonElement objectElement, params string[] names)
    {
        var properties = objectElement.EnumerateObject().ToArray();
        return properties.Length == names.Length && properties.All(property => names.Contains(property.Name, StringComparer.Ordinal)) && names.All(name => objectElement.TryGetProperty(name, out _));
    }

    internal static bool TryCanonicalBrowserHost(string? value, out string host) => TryCanonicalHost(value, out host);
    internal static string CanonicalBrowserHost(string value) => TryCanonicalHost(value, out var host) ? host : string.Empty;

    private static bool TryCanonicalHost(string? value, out string host)
    {
        host = string.Empty;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var source = value.Contains("://", StringComparison.Ordinal) ? value : $"https://{value}";
        return Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && uri.Port == 443 && string.IsNullOrEmpty(uri.UserInfo) &&
               (uri.AbsolutePath is "" or "/") && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment) && (host = CanonicalHost(uri.Host)).Length > 0;
    }

    private static string CanonicalHost(string host)
    {
        try { return new System.Globalization.IdnMapping().GetAscii(host).TrimEnd('.').ToLowerInvariant(); }
        catch (ArgumentException) { return string.Empty; }
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };
}
