using System.Net;
using System.Text.Json;
using Muesli.Windows.Services;

namespace Muesli.Windows.Tests;

/// <summary>
/// The privacy contract of usage analytics. Every client here talks to a recording handler;
/// no test can reach TelemetryDeck.
/// </summary>
public sealed class TelemetryDeckClientTests
{
    private const string ProductionAppId = "7F2B7846-1CB5-4FE6-8ABC-56F217B06A86";
    private const string DevAppId = "F6448386-6643-4E85-9354-578087725FB0";

    private static readonly string[] AllowedSignalKeys =
        ["appID", "clientUser", "sessionID", "type", "isTestMode", "payload"];

    private static readonly string[] AllowedPayloadKeys =
    [
        "TelemetryDeck.Device.platform", "TelemetryDeck.AppInfo.version", "muesli.channel",
        "product.schema", "outcome", "saved", "delivery", "feature"
    ];

    private static readonly string[] AllowedPayloadValues =
    [
        "Windows", "0.3.0", "dev", "production", "1",
        "success", "cancelled", "failed", "discarded", "partial", "yes", "no", "none", "imported"
    ];

    [Fact]
    public async Task EverySignalCarriesOnlyAllowListedKeysAndFiniteValues()
    {
        using var directory = new TestDirectory();
        var handler = new RecordingHandler();
        var client = Client(directory, handler, TelemetryChannel.Production);

        await client.SessionStarted();
        await client.OnboardingCompleted();
        var dictation = client.Begin(AnalyticsFeature.Dictation);
        await dictation.Started;
        await dictation.Finish(
            AnalyticsOutcome.Partial,
            (AnalyticsField.Saved, AnalyticsValue.Yes),
            (AnalyticsField.Delivery, AnalyticsValue.Failed));
        var meeting = client.Begin(AnalyticsFeature.Meeting);
        await meeting.Started;
        await meeting.Finish(AnalyticsOutcome.Success, (AnalyticsField.Saved, AnalyticsValue.Yes));
        var import = client.Begin(AnalyticsFeature.Workflow, (AnalyticsField.Feature, AnalyticsValue.Imported));
        await import.Started;
        await import.Finish(AnalyticsOutcome.Failed);

        var signals = handler.Signals();
        Assert.Equal(
            [
                "TelemetryDeck.Session.started", "onboarding.completed",
                "dictation.started", "dictation.finished",
                "meeting.started", "meeting.finished",
                "workflow.started", "workflow.finished"
            ],
            signals.Select(signal => signal.GetProperty("type").GetString()!).ToArray());
        Assert.All(handler.Requests, request =>
            Assert.Equal("https://nom.telemetrydeck.com/v2/", request.Url));
        foreach (var signal in signals)
        {
            Assert.All(signal.EnumerateObject(), property => Assert.Contains(property.Name, AllowedSignalKeys));
            Assert.All(signal.GetProperty("payload").EnumerateObject(), property =>
            {
                Assert.Contains(property.Name, AllowedPayloadKeys);
                Assert.Contains(property.Value.GetString(), AllowedPayloadValues);
            });
        }

        var finished = signals[3].GetProperty("payload");
        Assert.Equal("1", finished.GetProperty("product.schema").GetString());
        Assert.Equal("partial", finished.GetProperty("outcome").GetString());
        Assert.Equal("yes", finished.GetProperty("saved").GetString());
        Assert.Equal("failed", finished.GetProperty("delivery").GetString());
        Assert.Equal("imported", signals[7].GetProperty("payload").GetProperty("feature").GetString());
        // The two events macOS sends outside the product schema stay outside it here too.
        Assert.False(signals[0].GetProperty("payload").TryGetProperty("product.schema", out _));
        Assert.False(signals[1].GetProperty("payload").TryGetProperty("product.schema", out _));
    }

    [Fact]
    public async Task ProductionAndDevelopmentBuildsReportToSeparateApps()
    {
        using var directory = new TestDirectory();
        var production = new RecordingHandler();
        await Client(directory, production, TelemetryChannel.Production).SessionStarted();
        var dev = new RecordingHandler();
        await Client(directory, dev, TelemetryChannel.Dev).SessionStarted();

        var live = Assert.Single(production.Signals());
        Assert.Equal(ProductionAppId, live.GetProperty("appID").GetString());
        Assert.False(live.GetProperty("isTestMode").GetBoolean());
        Assert.Equal("production", live.GetProperty("payload").GetProperty("muesli.channel").GetString());

        var test = Assert.Single(dev.Signals());
        Assert.Equal(DevAppId, test.GetProperty("appID").GetString());
        Assert.True(test.GetProperty("isTestMode").GetBoolean());
        Assert.Equal("dev", test.GetProperty("payload").GetProperty("muesli.channel").GetString());
    }

    [Fact]
    public async Task ABuildWithoutAChannelSendsNothingAndCreatesNoIdentity()
    {
        using var directory = new TestDirectory();
        var handler = new RecordingHandler();
        var client = Client(directory, handler, TelemetryChannel.Unconfigured);

        await client.SessionStarted();
        await client.OnboardingCompleted();
        await client.Begin(AnalyticsFeature.Dictation).Finish(AnalyticsOutcome.Success);

        Assert.False(client.IsEnabled);
        Assert.Empty(handler.Requests);
        Assert.False(File.Exists(directory.File("telemetry-install-id")));
    }

    [Fact]
    public async Task OptingOutStopsEverySignalIncludingTheEndOfAnOperationAlreadyUnderWay()
    {
        using var directory = new TestDirectory();
        var handler = new RecordingHandler();
        var enabled = true;
        var client = Client(directory, handler, TelemetryChannel.Production, () => enabled);

        var underWay = client.Begin(AnalyticsFeature.Meeting);
        await underWay.Started;
        enabled = false;
        await underWay.Finish(AnalyticsOutcome.Success);
        await client.SessionStarted();
        var whileOff = client.Begin(AnalyticsFeature.Dictation);
        await whileOff.Started;
        enabled = true;
        // An operation that began while analytics were off never reports, even once they are on.
        await whileOff.Finish(AnalyticsOutcome.Success);

        Assert.Equal(["meeting.started"], handler.Signals().Select(signal => signal.GetProperty("type").GetString()!).ToArray());
    }

    [Fact]
    public async Task AnOperationReportsExactlyOneOutcome()
    {
        using var directory = new TestDirectory();
        var handler = new RecordingHandler();
        var attempt = Client(directory, handler, TelemetryChannel.Dev).Begin(AnalyticsFeature.Dictation);
        await attempt.Started;

        await attempt.Finish(AnalyticsOutcome.Cancelled);
        await attempt.Finish(AnalyticsOutcome.Success);

        var signals = handler.Signals();
        Assert.Equal(2, signals.Count);
        Assert.Equal("cancelled", signals[1].GetProperty("payload").GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task AFailingNetworkOrPreferenceReadNeverReachesTheCaller()
    {
        using var directory = new TestDirectory();
        var offline = Client(directory, new RecordingHandler(new HttpRequestException("offline")), TelemetryChannel.Production);
        await offline.SessionStarted();
        await offline.Begin(AnalyticsFeature.Meeting).Finish(AnalyticsOutcome.Failed);

        var handler = new RecordingHandler();
        var unreadable = Client(directory, handler, TelemetryChannel.Production,
            () => throw new IOException("settings are locked"));
        await unreadable.SessionStarted();

        Assert.False(unreadable.IsEnabled);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task TheUserIdentifierIsAStableHashOfARandomProfileValue()
    {
        using var directory = new TestDirectory();
        var first = new RecordingHandler();
        await Client(directory, first, TelemetryChannel.Dev).SessionStarted();
        var second = new RecordingHandler();
        await Client(directory, second, TelemetryChannel.Dev).SessionStarted();

        var stored = File.ReadAllText(directory.File("telemetry-install-id"));
        var user = first.Signals()[0].GetProperty("clientUser").GetString()!;
        Assert.True(Guid.TryParseExact(stored, "D", out _));
        Assert.Equal(64, user.Length);
        Assert.DoesNotContain(stored, first.Requests[0].Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Environment.MachineName, first.Requests[0].Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Environment.UserName, first.Requests[0].Body, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(user, second.Signals()[0].GetProperty("clientUser").GetString());
        // A new launch is a new session for the same anonymous user.
        Assert.NotEqual(
            first.Signals()[0].GetProperty("sessionID").GetString(),
            second.Signals()[0].GetProperty("sessionID").GetString());

        using var other = new TestDirectory();
        var elsewhere = new RecordingHandler();
        await Client(other, elsewhere, TelemetryChannel.Dev).SessionStarted();
        Assert.NotEqual(user, elsewhere.Signals()[0].GetProperty("clientUser").GetString());
    }

    [Fact]
    public async Task ADamagedIdentityFileIsReplacedInsteadOfSilencingAnalytics()
    {
        using var directory = new TestDirectory();
        File.WriteAllText(directory.File("telemetry-install-id"), "not an identifier");
        var handler = new RecordingHandler();

        await Client(directory, handler, TelemetryChannel.Dev).SessionStarted();

        Assert.Single(handler.Requests);
        Assert.True(Guid.TryParseExact(File.ReadAllText(directory.File("telemetry-install-id")), "D", out _));
    }

    [Theory]
    [InlineData(null, false, false, true, TelemetryChannel.Unconfigured)]
    [InlineData("", false, false, true, TelemetryChannel.Unconfigured)]
    [InlineData("preview", false, false, true, TelemetryChannel.Unconfigured)]
    [InlineData("dev", false, false, true, TelemetryChannel.Dev)]
    [InlineData("production", false, false, true, TelemetryChannel.Production)]
    [InlineData(" Production ", false, false, true, TelemetryChannel.Production)]
    [InlineData("production", true, false, true, TelemetryChannel.Dev)]
    [InlineData("production", false, true, true, TelemetryChannel.Dev)]
    [InlineData("production", false, false, false, TelemetryChannel.Dev)]
    public void OnlyAStampedShippingBuildOnTheProductionProfileReportsAsProduction(
        string? stamped, bool debugBuild, bool developmentSlot, bool productionProfile, TelemetryChannel expected) =>
        Assert.Equal(expected, TelemetryChannelResolver.Resolve(stamped, debugBuild, developmentSlot, productionProfile));

    [Fact]
    public void OnlyThePublicPackagerStampsTheProductionChannel()
    {
        var packager = File.ReadAllText(TestRepositoryLayout.Combine("scripts", "package-winui-msix.ps1"));
        Assert.Contains("if ($Store -or $Publisher) {", packager, StringComparison.Ordinal);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(packager, "MuesliTelemetryChannel=production"));
        foreach (var launcher in new[] { "run-windows.ps1", "run-windows-dev.ps1", "run-winui-preview.ps1" })
        {
            Assert.DoesNotContain(
                "MuesliTelemetryChannel",
                File.ReadAllText(TestRepositoryLayout.Combine("scripts", launcher)),
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public void AnUnreadableOrNewerSettingsFileNeverTurnsAnalyticsBackOn()
    {
        using var directory = new TestDirectory();
        var path = directory.File("settings.json");

        File.WriteAllText(path, "{ this is not json");
        Assert.False(new SettingsStore(path, new InMemorySecretStore()).Load().ShareUsageAnalytics);

        // The backup is the save before the latest one, so it can predate an opt-out.
        var store = new SettingsStore(path, new InMemorySecretStore());
        File.Delete(path);
        store.Save(store.Load() with { UserName = "first save" });
        store.Save(store.Load() with { ShareUsageAnalytics = false });
        File.WriteAllText(path, "{ damaged after the opt-out");
        var recovered = new SettingsStore(path, new InMemorySecretStore()).Load();
        Assert.False(recovered.ShareUsageAnalytics);

        File.WriteAllText(path, $$"""{"schemaVersion":{{MuesliSettings.CurrentSchemaVersion + 1}},"shareUsageAnalytics":false}""");
        Assert.False(new SettingsStore(path, new InMemorySecretStore()).Load().ShareUsageAnalytics);
    }

    private static TelemetryDeckClient Client(
        TestDirectory directory,
        RecordingHandler handler,
        TelemetryChannel channel,
        Func<bool>? isEnabled = null) =>
        new(directory.Path, "0.3.0", channel, isEnabled ?? (() => true), new HttpClient(handler));

    private sealed class RecordingHandler(Exception? failure = null) : HttpMessageHandler
    {
        private readonly object _gate = new();
        private readonly List<(string Url, string Body)> _requests = [];

        public IReadOnlyList<(string Url, string Body)> Requests
        {
            get { lock (_gate) return _requests.ToArray(); }
        }

        public List<JsonElement> Signals() => Requests
            .SelectMany(request => JsonDocument.Parse(request.Body).RootElement.EnumerateArray().ToArray())
            .ToList();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (failure is not null) throw failure;
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            lock (_gate) _requests.Add((request.RequestUri!.ToString(), body));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("OK") };
        }
    }
}
