using System.Net.Http.Json;
using System.Security.Cryptography;

namespace Muesli.Windows.Services;

/// <summary>
/// Where this build's analytics go. The channel is stamped into the app at build time
/// (<c>-p:MuesliTelemetryChannel=…</c>); a build without a stamp sends nothing, as on macOS.
/// </summary>
public enum TelemetryChannel
{
    Unconfigured,
    Dev,
    Production
}

public static class TelemetryChannelResolver
{
    /// <summary>
    /// Only a build stamped <c>production</c>, running as the shipping package on the production
    /// profile, reports as production. A production-stamped build that is a debug build, a
    /// development slot or an isolated profile is demoted to the development app.
    /// </summary>
    public static TelemetryChannel Resolve(string? stamped, bool debugBuild, bool developmentSlot, bool productionProfile) =>
        stamped?.Trim().ToLowerInvariant() switch
        {
            "dev" => TelemetryChannel.Dev,
            "production" => debugBuild || developmentSlot || !productionProfile
                ? TelemetryChannel.Dev
                : TelemetryChannel.Production,
            _ => TelemetryChannel.Unconfigured
        };
}

// The product event contract is shared with macOS (ProductAnalytics.swift, product.schema 1).
// Events accept these finite values only: content, identifiers, paths, model names typed by the
// user and raw errors cannot be passed through this API.
public enum AnalyticsFeature { Dictation, Meeting, Workflow }

public enum AnalyticsOutcome { Success, Cancelled, Failed, Discarded, Partial }

public enum AnalyticsField { Saved, Delivery, Feature }

public enum AnalyticsValue { Yes, No, Success, Failed, None, Imported }

/// <summary>
/// Sends anonymous usage events to TelemetryDeck over its HTTP ingest API. Best effort: nothing
/// is queued to disk or retried, no caller waits for a send, and no failure reaches a caller.
/// </summary>
public sealed class TelemetryDeckClient(
    string profileRoot,
    string version,
    TelemetryChannel channel,
    Func<bool> isEnabled,
    HttpClient http)
{
    // TelemetryDeck app identifiers are public routing identifiers, not credentials. They are the
    // same apps macOS reports to (scripts/muesli_telemetry_channels.sh in Muesli-HQ/muesli).
    private const string ProductionAppId = "7F2B7846-1CB5-4FE6-8ABC-56F217B06A86";
    private const string DevAppId = "F6448386-6643-4E85-9354-578087725FB0";
    private const string IngestUrl = "https://nom.telemetrydeck.com/v2/";
    private const int MaximumPendingSends = 32;

    /// <summary>The name the TelemetryDeck SDK gives a new session on macOS.</summary>
    public const string SessionStartedEvent = "TelemetryDeck.Session.started";
    public const string OnboardingCompletedEvent = "onboarding.completed";

    private readonly string _sessionId = Guid.NewGuid().ToString();
    // PublicationOnly: a failed read is retried on the next event instead of being cached.
    private readonly Lazy<string> _clientUser =
        new(() => LoadClientUser(profileRoot), LazyThreadSafetyMode.PublicationOnly);
    private int _pending;

    /// <summary>True when this build has a channel and the user has not opted out.</summary>
    public bool IsEnabled
    {
        get
        {
            if (channel == TelemetryChannel.Unconfigured) return false;
            // Reading the preference must never break the feature that reported the event.
            try { return isEnabled(); }
            catch { return false; }
        }
    }

    public Task SessionStarted() => Send(SessionStartedEvent, null);

    public Task OnboardingCompleted() => Send(OnboardingCompletedEvent, null);

    /// <summary>Reports <c>&lt;feature&gt;.started</c> and returns the attempt that reports its one terminal outcome.</summary>
    public AnalyticsAttempt Begin(AnalyticsFeature feature, params (AnalyticsField Field, AnalyticsValue Value)[] fields) =>
        new(this, feature, fields);

    internal Task SendProductEvent(
        AnalyticsFeature feature,
        string kind,
        AnalyticsOutcome? outcome,
        IEnumerable<(AnalyticsField Field, AnalyticsValue Value)> fields)
    {
        var parameters = new Dictionary<string, string> { ["product.schema"] = "1" };
        foreach (var (field, value) in fields) parameters[Wire(field)] = Wire(value);
        if (outcome is { } terminal) parameters["outcome"] = Wire(terminal);
        return Send($"{Wire(feature)}.{kind}", parameters);
    }

    private static string Wire<T>(T value) where T : struct, Enum => value.ToString().ToLowerInvariant();

    private Task Send(string eventName, Dictionary<string, string>? parameters)
    {
        if (!IsEnabled) return Task.CompletedTask;
        if (Interlocked.Increment(ref _pending) > MaximumPendingSends)
        {
            Interlocked.Decrement(ref _pending);
            return Task.CompletedTask;
        }

        return Task.Run(async () =>
        {
            try { await PostAsync(eventName, parameters).ConfigureAwait(false); }
            catch
            {
                // Offline, a slow endpoint or an unwritable profile must never surface anywhere.
            }
            finally { Interlocked.Decrement(ref _pending); }
        });
    }

    private async Task PostAsync(string eventName, Dictionary<string, string>? parameters)
    {
        var test = channel != TelemetryChannel.Production;
        var payload = new Dictionary<string, string>
        {
            ["TelemetryDeck.Device.platform"] = "Windows",
            ["TelemetryDeck.AppInfo.version"] = version,
            ["muesli.channel"] = test ? "dev" : "production"
        };
        if (parameters is not null)
        {
            foreach (var (key, value) in parameters) payload[key] = value;
        }

        var item = new
        {
            appID = test ? DevAppId : ProductionAppId,
            clientUser = _clientUser.Value,
            sessionID = _sessionId,
            type = eventName,
            isTestMode = test,
            payload
        };
        // The user may have opted out while this send waited for a thread.
        if (!IsEnabled) return;
        using var response = await http.PostAsJsonAsync(IngestUrl, new[] { item }).ConfigureAwait(false);
    }

    /// <summary>
    /// A hash of a random identifier that exists only in this profile. It is not derived from the
    /// machine, the Windows account or anything the user entered; TelemetryDeck salts and hashes
    /// it again on arrival.
    /// </summary>
    private static string LoadClientUser(string profileRoot)
    {
        var path = Path.Combine(profileRoot, "telemetry-install-id");
        if (!File.Exists(path) || !Guid.TryParseExact(File.ReadAllText(path).Trim(), "D", out var id))
        {
            // Missing or damaged: start a new anonymous identity instead of going silent.
            id = Guid.NewGuid();
            Directory.CreateDirectory(profileRoot);
            var temporary = path + "." + id.ToString("N");
            File.WriteAllText(temporary, id.ToString());
            try { File.Move(temporary, path, overwrite: true); }
            finally { File.Delete(temporary); }
        }

        return Convert.ToHexStringLower(SHA256.HashData(id.ToByteArray()));
    }
}

/// <summary>
/// One user operation: <c>started</c> when it is created, then exactly one <c>finished</c>. An
/// attempt that began while analytics were off stays silent even if they are turned on meanwhile.
/// </summary>
public sealed class AnalyticsAttempt
{
    private readonly TelemetryDeckClient _client;
    private readonly AnalyticsFeature _feature;
    private readonly (AnalyticsField Field, AnalyticsValue Value)[] _fields;
    private readonly bool _reported;
    private int _finished;

    internal AnalyticsAttempt(
        TelemetryDeckClient client,
        AnalyticsFeature feature,
        (AnalyticsField Field, AnalyticsValue Value)[] fields)
    {
        _client = client;
        _feature = feature;
        _fields = fields;
        _reported = client.IsEnabled;
        if (_reported) Started = client.SendProductEvent(feature, "started", null, fields);
    }

    internal Task Started { get; } = Task.CompletedTask;

    public Task Finish(AnalyticsOutcome outcome, params (AnalyticsField Field, AnalyticsValue Value)[] fields)
    {
        if (Interlocked.Exchange(ref _finished, 1) != 0 || !_reported) return Task.CompletedTask;
        return _client.SendProductEvent(_feature, "finished", outcome, _fields.Concat(fields));
    }
}
