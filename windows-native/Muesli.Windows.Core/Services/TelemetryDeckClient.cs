using System.Net.Http.Json;
using System.Security.Cryptography;

namespace Muesli.Windows.Services;

public enum ProductEvent
{
    AppLaunched,
    OnboardingCompleted,
    DictationCompleted,
    MeetingCompleted,
    MeetingImported
}

public sealed class TelemetryDeckClient(string profileRoot, string version, bool testMode, Func<bool> isEnabled, HttpClient http)
{
    private const string ProductionAppId = "7F2B7846-1CB5-4FE6-8ABC-56F217B06A86";
    private const string DevAppId = "F6448386-6643-4E85-9354-578087725FB0";
    private readonly string _sessionId = Guid.NewGuid().ToString();
    private readonly Lazy<string> _clientUser = new(() => LoadClientUser(profileRoot));

    public Task SignalAsync(ProductEvent signal) =>
        isEnabled() ? Task.Run(() => SendAsync(signal)) : Task.CompletedTask;

    private async Task SendAsync(ProductEvent signal)
    {
        if (!isEnabled()) return;
        var eventName = signal switch
        {
            ProductEvent.AppLaunched => "app.launched",
            ProductEvent.OnboardingCompleted => "onboarding.completed",
            ProductEvent.DictationCompleted => "dictation.completed",
            ProductEvent.MeetingCompleted => "meeting.completed",
            ProductEvent.MeetingImported => "meeting.imported",
            _ => throw new ArgumentOutOfRangeException(nameof(signal))
        };
        try
        {
            var item = new
            {
                appID = testMode ? DevAppId : ProductionAppId,
                clientUser = _clientUser.Value,
                sessionID = _sessionId,
                type = eventName,
                isTestMode = testMode,
                payload = new Dictionary<string, string>
                {
                    ["TelemetryDeck.Device.platform"] = "Windows",
                    ["TelemetryDeck.AppInfo.version"] = version,
                    ["muesli.channel"] = testMode ? "dev" : "production"
                }
            };
            if (!isEnabled()) return;
            using var response = await http.PostAsJsonAsync("https://nom.telemetrydeck.com/v2/", new[] { item }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException or UnauthorizedAccessException)
        {
            // Telemetry is best effort. Offline or unwritable profiles must still work normally.
        }
    }

    private static string LoadClientUser(string profileRoot)
    {
        var path = Path.Combine(profileRoot, "telemetry-install-id");
        Guid id;
        if (File.Exists(path))
        {
            if (!Guid.TryParseExact(File.ReadAllText(path), "D", out id))
                throw new IOException("Invalid telemetry installation identifier.");
        }
        else
        {
            id = Guid.NewGuid();
            var temporary = path + "." + id;
            File.WriteAllText(temporary, id.ToString());
            try { File.Move(temporary, path); }
            finally { File.Delete(temporary); }
        }
        return Convert.ToHexStringLower(SHA256.HashData(id.ToByteArray()));
    }
}
