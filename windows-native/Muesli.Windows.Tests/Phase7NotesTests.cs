using System.Net;
using System.Net.Http;
using Muesli.Windows.Services;

namespace Muesli.Windows.Tests;

/// <summary>
/// Meeting-notes providers: contracts, failure handling, disclosure, and persistence.
/// Provider fakes appear only here; production always uses the real HTTP contracts.
/// </summary>
public sealed class Phase7NotesTests
{
    private const string Transcript = "[09:00:00] You: we agreed to ship on Friday and Priya will own the release notes.";

    private static MuesliSettings Settings(string provider, string? endpoint = null, string? model = null) => new()
    {
        MeetingSummaryProvider = provider,
        MeetingSummaryTemplate = "Standard Meeting Notes",
        OllamaEndpoint = endpoint ?? "http://localhost:11434",
        OllamaModel = model ?? "llama3.1:8b"
    };

    // ---- Ollama contract ---------------------------------------------------------

    [Fact]
    public async Task ProviderTitlesPrioritizeWritingAndManualTitlesNeverCallTheProvider()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "{\"message\":{\"content\":\"Release Launch Decision\"}}");
        using var client = new HttpClient(handler);
        var meeting = new PersistedMeeting { Title = "Meeting", Transcript = Transcript, ManualNotes = "Release launch is the priority." };
        Assert.Equal("Release Launch Decision", await MeetingSummaryService.CreateTitleAsync(meeting, Settings("ollama"), httpClient: client));
        using var titleBody = System.Text.Json.JsonDocument.Parse(handler.LastRequestBody!);
        Assert.Contains("3–7 word", titleBody.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
        Assert.Contains(meeting.ManualNotes, handler.LastRequestBody);
        using var failed = new HttpClient(new StubHandler(HttpStatusCode.InternalServerError, "{}"));
        Assert.Equal("My title", await MeetingSummaryService.CreateTitleAsync(meeting with { Title = "My title", TitleIsManual = true }, Settings("ollama"), httpClient: failed));
        Assert.Equal(MeetingTitleService.Generate(Transcript, meeting.CreatedAt, meeting.Title, meeting.ManualNotes),
            await MeetingSummaryService.CreateTitleAsync(meeting, Settings("ollama"), httpClient: failed));
    }

    [Fact]
    public async Task FollowUpSummaryDistinguishesPreviousNotesFromCurrentSpeech()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "{\"message\":{\"content\":\"## Summary\\nRelease now ready\"}}");
        using var client = new HttpClient(handler);
        await MeetingSummaryService.CreateSummaryResultAsync(Transcript, "Follow-up", Settings("ollama"), httpClient: client,
            previousMeetingNotes: "Previously: Priya needed to prepare release notes.");
        using var body = System.Text.Json.JsonDocument.Parse(handler.LastRequestBody!);
        var messages = body.RootElement.GetProperty("messages");
        Assert.Contains("Previously:", messages[0].GetProperty("content").GetString());
        Assert.Contains("do not report prior events as current decisions", messages[0].GetProperty("content").GetString());
        Assert.DoesNotContain("Previously:", messages[1].GetProperty("content").GetString());
    }

    [Fact]
    public async Task WrittenMeetingNotesAreContextForSummaryProvidersAndRemainSeparateFromTranscript()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "{\"message\":{\"content\":\"## Summary\\nRelease moved to Monday\"}}");
        using var client = new HttpClient(handler);
        var written = "Release moved to Monday. Priya owns the revised announcement.";
        var result = await MeetingSummaryService.CreateSummaryResultAsync(
            Transcript, "Release sync", Settings("ollama"), CancellationToken.None, client, manualNotes: written);
        using var body = System.Text.Json.JsonDocument.Parse(handler.LastRequestBody!);
        var prompt = body.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!;
        Assert.Contains(Transcript, prompt, StringComparison.Ordinal);
        Assert.Contains(written, prompt, StringComparison.Ordinal);
        Assert.Contains("high-priority context", prompt, StringComparison.Ordinal);
        Assert.Contains("Monday", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OllamaSuccessUsesTheModelResponseAndReportsNoFallback()
    {
        var handler = new StubHandler(HttpStatusCode.OK,
            "{\"message\":{\"role\":\"assistant\",\"content\":\"## Summary\\n- Ship on Friday\"}}");
        using var client = new HttpClient(handler);

        var result = await MeetingSummaryService.CreateSummaryResultAsync(
            Transcript, "Release sync", Settings("ollama"), CancellationToken.None, client);

        Assert.Equal("ollama", result.Provider);
        Assert.False(result.UsedLocalFallback);
        Assert.Null(result.SafeFailureReason);
        Assert.Contains("Ship on Friday", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OllamaRequestTargetsTheChatEndpointWithStreamingDisabled()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "{\"message\":{\"content\":\"notes\"}}");
        using var client = new HttpClient(handler);

        await MeetingSummaryService.CreateSummaryResultAsync(
            Transcript, "Release sync", Settings("ollama", "http://localhost:11434"), CancellationToken.None, client);

        Assert.Equal("http://localhost:11434/api/chat", handler.LastRequestUri?.ToString());
        Assert.Contains("\"stream\":false", handler.LastRequestBody, StringComparison.Ordinal);
        Assert.Contains("llama3.1:8b", handler.LastRequestBody, StringComparison.Ordinal);
        // A local provider must never attach an Authorization header.
        Assert.Null(handler.LastAuthorizationHeader);
    }

    [Fact]
    public async Task ACustomOllamaEndpointIsHonoured()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "{\"message\":{\"content\":\"notes\"}}");
        using var client = new HttpClient(handler);

        await MeetingSummaryService.CreateSummaryResultAsync(
            Transcript, "t", Settings("ollama", "http://127.0.0.1:9999"), CancellationToken.None, client);

        Assert.Equal("http://127.0.0.1:9999/api/chat", handler.LastRequestUri?.ToString());
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "{}", "model-not-installed")]
    [InlineData(HttpStatusCode.InternalServerError, "{}", "http-500")]
    [InlineData(HttpStatusCode.OK, "not json at all", "malformed-response")]
    [InlineData(HttpStatusCode.OK, "{\"message\":{\"content\":\"\"}}", "empty-response")]
    [InlineData(HttpStatusCode.OK, "{\"unexpected\":true}", "empty-response")]
    public async Task OllamaFailuresFallBackLocallyWithAnHonestReason(
        HttpStatusCode status, string body, string expectedReason)
    {
        using var client = new HttpClient(new StubHandler(status, body));

        var result = await MeetingSummaryService.CreateSummaryResultAsync(
            Transcript, "Release sync", Settings("ollama"), CancellationToken.None, client);

        Assert.True(result.UsedLocalFallback);
        Assert.Equal(expectedReason, result.SafeFailureReason);
        Assert.Equal("ollama", result.Provider);
        // The fallback must be real deterministic notes, not an error string presented as notes.
        Assert.False(string.IsNullOrWhiteSpace(result.Summary));
        Assert.DoesNotContain("http-", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnInvalidOllamaEndpointFailsClosedWithoutAnyRequest()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "{\"message\":{\"content\":\"x\"}}");
        using var client = new HttpClient(handler);

        var result = await MeetingSummaryService.CreateSummaryResultAsync(
            Transcript, "t", Settings("ollama", "not-a-url"), CancellationToken.None, client);

        Assert.True(result.UsedLocalFallback);
        Assert.Equal("invalid-endpoint", result.SafeFailureReason);
        Assert.Null(handler.LastRequestUri);
    }

    [Fact]
    public async Task OllamaTimeoutIsReportedAsTimeoutAndStillProducesNotes()
    {
        using var client = new HttpClient(new ThrowingHandler(new TaskCanceledException("timed out")));

        var result = await MeetingSummaryService.CreateSummaryResultAsync(
            Transcript, "t", Settings("ollama"), CancellationToken.None, client);

        Assert.True(result.UsedLocalFallback);
        Assert.Equal("timeout", result.SafeFailureReason);
        Assert.False(string.IsNullOrWhiteSpace(result.Summary));
    }

    [Fact]
    public async Task CallerCancellationPropagatesInsteadOfSilentlyFallingBack()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        using var client = new HttpClient(new ThrowingHandler(new TaskCanceledException("cancelled")));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            MeetingSummaryService.CreateSummaryResultAsync(
                Transcript, "t", Settings("ollama"), cancelled.Token, client));
    }

    // ---- LM Studio contract (SUM-02) ---------------------------------------------

    [Fact]
    public async Task LmStudioUsesLocalChatCompletionsWithNoCredential()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "{\"choices\":[{\"message\":{\"content\":\"## Summary\\n- Ship on Friday\"}}]}");
        using var client = new HttpClient(handler);
        var settings = Settings("lmstudio") with
        {
            LmStudioEndpoint = "http://localhost:1234",
            LmStudioModel = "qwen2.5-7b-instruct"
        };

        var result = await MeetingSummaryService.CreateSummaryResultAsync(
            Transcript, "Release sync", settings, CancellationToken.None, client);

        Assert.Equal("lmstudio", result.Provider);
        Assert.False(result.UsedLocalFallback);
        Assert.Null(result.SafeFailureReason);
        Assert.Contains("Ship on Friday", result.Summary, StringComparison.Ordinal);
        Assert.Equal("http://localhost:1234/v1/chat/completions", handler.LastRequestUri?.ToString());
        Assert.Contains("\"stream\":false", handler.LastRequestBody, StringComparison.Ordinal);
        Assert.Contains("qwen2.5-7b-instruct", handler.LastRequestBody, StringComparison.Ordinal);
        Assert.Null(handler.LastAuthorizationHeader);
    }

    [Fact]
    public async Task LmStudioRequiresAModelAndFailsClosedWithoutAnyRequest()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "{\"choices\":[{\"message\":{\"content\":\"x\"}}]}");
        using var client = new HttpClient(handler);

        var result = await MeetingSummaryService.CreateSummaryResultAsync(
            Transcript, "t", Settings("lmstudio") with { LmStudioModel = "" }, CancellationToken.None, client);

        Assert.True(result.UsedLocalFallback);
        Assert.Equal("missing-model", result.SafeFailureReason);
        Assert.Equal("lmstudio", result.Provider);
        Assert.Null(handler.LastRequestUri);
    }

    // ---- Custom HTTP contract (SUM-02) -------------------------------------------

    [Fact]
    public async Task CustomLlmPostsToTheConfiguredEndpointWithOptionalBearerKey()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "{\"choices\":[{\"message\":{\"content\":\"notes\"}}]}");
        using var client = new HttpClient(handler);
        var settings = Settings("custom") with
        {
            CustomLlmEndpoint = "http://localhost:8080/v1/chat/completions",
            CustomLlmModel = "my-local-model",
            ResolvedCustomLlmApiKey = "local-test-key"
        };

        var result = await MeetingSummaryService.CreateSummaryResultAsync(
            Transcript, "t", settings, CancellationToken.None, client);

        Assert.Equal("custom", result.Provider);
        Assert.False(result.UsedLocalFallback);
        Assert.Equal("http://localhost:8080/v1/chat/completions", handler.LastRequestUri?.ToString());
        Assert.Equal("Bearer local-test-key", handler.LastAuthorizationHeader);
        Assert.Contains("my-local-model", handler.LastRequestBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CustomLlmBaseUrlReceivesTheChatCompletionsPathAndNoAuthWhenKeyless()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "{\"choices\":[{\"message\":{\"content\":\"notes\"}}]}");
        using var client = new HttpClient(handler);
        var settings = Settings("custom") with
        {
            CustomLlmEndpoint = "http://127.0.0.1:9000",
            CustomLlmModel = "keyless-model",
            ResolvedCustomLlmApiKey = ""
        };

        var result = await MeetingSummaryService.CreateSummaryResultAsync(
            Transcript, "t", settings, CancellationToken.None, client);

        Assert.False(result.UsedLocalFallback);
        Assert.Equal("http://127.0.0.1:9000/v1/chat/completions", handler.LastRequestUri?.ToString());
        Assert.Null(handler.LastAuthorizationHeader);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "{}", "model-not-installed")]
    [InlineData(HttpStatusCode.InternalServerError, "{}", "http-500")]
    [InlineData(HttpStatusCode.OK, "not json", "malformed-response")]
    [InlineData(HttpStatusCode.OK, "{\"choices\":[]}", "empty-response")]
    public async Task CustomLlmFailuresFallBackLocallyWithAnHonestReason(
        HttpStatusCode status, string body, string expectedReason)
    {
        using var client = new HttpClient(new StubHandler(status, body));
        var settings = Settings("custom") with
        {
            CustomLlmEndpoint = "http://localhost:8080/v1/chat/completions",
            CustomLlmModel = "model"
        };

        var result = await MeetingSummaryService.CreateSummaryResultAsync(
            Transcript, "t", settings, CancellationToken.None, client);

        Assert.True(result.UsedLocalFallback);
        Assert.Equal(expectedReason, result.SafeFailureReason);
        Assert.Equal("custom", result.Provider);
        Assert.False(string.IsNullOrWhiteSpace(result.Summary));
    }

    [Fact]
    public async Task CustomLlmFailsClosedForAMissingModelOrEndpoint()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "{}");
        using var client = new HttpClient(handler);

        var missingEndpoint = await MeetingSummaryService.CreateSummaryResultAsync(
            Transcript, "t", Settings("custom") with { CustomLlmEndpoint = "not-a-url", CustomLlmModel = "m" },
            CancellationToken.None, client);
        Assert.Equal("invalid-endpoint", missingEndpoint.SafeFailureReason);

        var missingModel = await MeetingSummaryService.CreateSummaryResultAsync(
            Transcript, "t", Settings("custom") with { CustomLlmEndpoint = "http://localhost:8080", CustomLlmModel = "" },
            CancellationToken.None, client);
        Assert.Equal("missing-model", missingModel.SafeFailureReason);

        Assert.Null(handler.LastRequestUri);
    }

    // ---- disclosure --------------------------------------------------------------

    [Theory]
    [InlineData("local", false)]
    [InlineData("ollama", false)]
    [InlineData("openai", true)]
    [InlineData("openrouter", true)]
    public void OnlyCloudProvidersAreDisclosedAsLeavingTheMachine(string provider, bool leaves) =>
        Assert.Equal(leaves, SummaryProviderDisclosure.LeavesMachine(provider));

    [Fact]
    public void LmStudioAndCustomHttpAreLocalByDefaultButDiscloseRemoteEndpoints()
    {
        Assert.False(SummaryProviderDisclosure.LeavesMachine("lmstudio", null, "http://localhost:1234", null));
        Assert.True(SummaryProviderDisclosure.LeavesMachine("lmstudio", null, "http://192.168.1.50:1234", null));
        Assert.Contains("leaves this machine",
            SummaryProviderDisclosure.DisclosureFor("lmstudio", null, "http://192.168.1.50:1234", null), StringComparison.Ordinal);

        Assert.False(SummaryProviderDisclosure.LeavesMachine("custom", null, null, "http://localhost:8080/v1/chat/completions"));
        Assert.True(SummaryProviderDisclosure.LeavesMachine("custom", null, null, "http://10.0.0.5/v1/chat/completions"));
        Assert.Contains("leaves this machine",
            SummaryProviderDisclosure.DisclosureFor("custom", null, null, "http://10.0.0.5/v1/chat/completions"), StringComparison.Ordinal);

        Assert.Contains("lmstudio", SummaryProviderDisclosure.AvailableIds);
        Assert.Contains("custom", SummaryProviderDisclosure.AvailableIds);
    }

    [Fact]
    public void ARemoteOllamaEndpointIsDisclosedAsLeavingTheMachine()
    {
        Assert.False(SummaryProviderDisclosure.LeavesMachine("ollama", "http://localhost:11434"));
        Assert.False(SummaryProviderDisclosure.LeavesMachine("ollama", "http://127.0.0.1:11434"));
        Assert.True(SummaryProviderDisclosure.LeavesMachine("ollama", "http://192.168.1.50:11434"));
        Assert.Contains("leaves this machine",
            SummaryProviderDisclosure.DisclosureFor("ollama", "http://192.168.1.50:11434"), StringComparison.Ordinal);
    }

    [Fact]
    public void EveryOfferedProviderHasADisclosureAndOnlyCloudOnesRequireAKey()
    {
        Assert.All(SummaryProviderDisclosure.Available, info =>
        {
            Assert.False(string.IsNullOrWhiteSpace(info.Disclosure));
            Assert.Equal(info.SendsTranscriptOffMachine, info.RequiresApiKey);
        });
        Assert.Contains("ollama", SummaryProviderDisclosure.AvailableIds);
    }

    [Fact]
    public void ChatGptSubscriptionIsNotOfferedAndItsBlockerIsRecorded()
    {
        Assert.DoesNotContain(SummaryProviderDisclosure.ChatGptSubscription, SummaryProviderDisclosure.AvailableIds);
        Assert.Contains("no published OAuth client",
            SummaryProviderDisclosure.ChatGptSubscriptionBlocker, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnUnknownProviderResolvesToLocalRatherThanFailing()
    {
        var info = SummaryProviderDisclosure.For("some-provider-we-do-not-have");
        Assert.Equal("local", info.Id);
        Assert.False(info.SendsTranscriptOffMachine);
    }

    // ---- templates and instructions ----------------------------------------------

    [Fact]
    public void EveryBuiltInTemplateProducesNotesAndIsRecognizedAsBuiltIn()
    {
        Assert.NotEmpty(MeetingSummaryService.BuiltInTemplateNames);
        foreach (var template in MeetingSummaryService.BuiltInTemplateNames)
        {
            Assert.True(MeetingSummaryService.IsBuiltInTemplate(template));
            var notes = MeetingSummaryService.CreateSummary(Transcript, "Release sync", template);
            Assert.False(string.IsNullOrWhiteSpace(notes));
        }
    }

    [Fact]
    public void CustomTemplateNamesArePreservedWhileBlankOnesFallBackToABuiltIn()
    {
        // Custom templates are a product feature, so an unrecognised name must survive rather than
        // being silently rewritten to a built-in.
        const string custom = "Board update (custom)";
        Assert.Equal(custom, MeetingSummaryService.NormalizeTemplateName(custom));
        Assert.False(MeetingSummaryService.IsBuiltInTemplate(custom));

        Assert.Contains(MeetingSummaryService.NormalizeTemplateName(null), MeetingSummaryService.BuiltInTemplateNames);
        Assert.Contains(MeetingSummaryService.NormalizeTemplateName("   "), MeetingSummaryService.BuiltInTemplateNames);
    }

    [Fact]
    public void AnUnknownTemplateStillProducesDeterministicLocalNotes()
    {
        var notes = MeetingSummaryService.CreateSummary(Transcript, "Release sync", "Board update (custom)");
        Assert.False(string.IsNullOrWhiteSpace(notes));
        Assert.Equal(notes, MeetingSummaryService.CreateSummary(Transcript, "Release sync", "Board update (custom)"));
    }

    [Fact]
    public async Task CustomSummaryInstructionsReachTheProviderAsTheSystemPrompt()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "{\"message\":{\"content\":\"notes\"}}");
        using var client = new HttpClient(handler);
        var settings = Settings("ollama") with { MeetingSummaryPromptOverride = "Always answer in bullet points only." };

        await MeetingSummaryService.CreateSummaryResultAsync(Transcript, "t", settings, CancellationToken.None, client);

        Assert.Contains("Always answer in bullet points only.", handler.LastRequestBody, StringComparison.Ordinal);
    }

    [Fact]
    public void LocalNotesAreDeterministicForTheSameTranscriptAndTemplate()
    {
        var first = MeetingSummaryService.CreateSummary(Transcript, "Release sync", "Standard Meeting Notes");
        for (var run = 0; run < 5; run++)
        {
            Assert.Equal(first, MeetingSummaryService.CreateSummary(Transcript, "Release sync", "Standard Meeting Notes"));
        }
    }

    [Fact]
    public async Task AnEmptyTranscriptProducesNoNotesAndNeverCallsAProvider()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "{\"message\":{\"content\":\"x\"}}");
        using var client = new HttpClient(handler);

        var result = await MeetingSummaryService.CreateSummaryResultAsync(
            "   ", "t", Settings("ollama"), CancellationToken.None, client);

        Assert.Equal("", result.Summary);
        Assert.Null(handler.LastRequestUri);
    }

    // ---- persistence -------------------------------------------------------------

    [Fact]
    public void OllamaSettingsRoundTripAndDefaultToALocalEndpoint()
    {
        using var directory = new TestDirectory();
        var store = new SettingsStore(directory.File("settings.json"), new InMemorySecretStore());

        var defaults = store.Load();
        Assert.True(SummaryProviderDisclosure.IsLoopbackEndpoint(defaults.OllamaEndpoint));

        store.Save(new MuesliSettings
        {
            MeetingSummaryProvider = "ollama",
            OllamaEndpoint = "http://127.0.0.1:9999",
            OllamaModel = "qwen2.5:14b"
        });
        var loaded = store.Load();

        Assert.Equal("ollama", loaded.MeetingSummaryProvider);
        Assert.Equal("http://127.0.0.1:9999", loaded.OllamaEndpoint);
        Assert.Equal("qwen2.5:14b", loaded.OllamaModel);
        Assert.Equal(MuesliSettings.CurrentSchemaVersion, loaded.SchemaVersion);
    }

    [Fact]
    public void LegacySchema4SettingsMigrateWithLocalOllamaDefaults()
    {
        using var directory = new TestDirectory();
        var path = directory.File("settings.json");
        File.WriteAllText(path,
            "{\"schemaVersion\":1,\"data\":{\"SchemaVersion\":4,\"MeetingSummaryProvider\":\"openai\",\"OpenAIModel\":\"gpt-5.4-mini\"}}");

        var loaded = new SettingsStore(path, new InMemorySecretStore()).Load();

        Assert.Equal("openai", loaded.MeetingSummaryProvider);
        Assert.Equal("gpt-5.4-mini", loaded.OpenAIModel);
        Assert.Equal("http://localhost:11434", loaded.OllamaEndpoint);
        Assert.False(SummaryProviderDisclosure.LeavesMachine("ollama", loaded.OllamaEndpoint));
    }

    [Fact]
    public void ProviderKeysAreNeverWrittenIntoTheSettingsJson()
    {
        using var directory = new TestDirectory();
        var path = directory.File("settings.json");
        var store = new SettingsStore(path, new InMemorySecretStore());

        // Even when a caller hands Save a resolved key, it must be stripped before the file is written.
        store.Save(new MuesliSettings
        {
            MeetingSummaryProvider = "openai",
            ResolvedOpenAIApiKey = "sk-do-not-persist-this-value",
            ResolvedOpenRouterApiKey = "or-do-not-persist-this-value"
        });

        var raw = File.ReadAllText(path);
        Assert.DoesNotContain("sk-do-not-persist-this-value", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("or-do-not-persist-this-value", raw, StringComparison.Ordinal);
    }

    [Fact]
    public void SecretsRoundTripThroughTheCredentialStoreRatherThanTheSettingsFile()
    {
        using var directory = new TestDirectory();
        var path = directory.File("settings.json");
        var secrets = new InMemorySecretStore();
        var store = new SettingsStore(path, secrets);
        store.Save(new MuesliSettings { MeetingSummaryProvider = "openai" });

        store.SaveSecret("muesli-openai", "sk-live-value");
        Assert.True(store.IsSecretConfigured("muesli-openai"));
        Assert.Equal("sk-live-value", store.ReadSecret("muesli-openai"));
        Assert.DoesNotContain("sk-live-value", File.ReadAllText(path), StringComparison.Ordinal);

        store.SaveSecret("muesli-openai", null);
        Assert.False(store.IsSecretConfigured("muesli-openai"));
    }

    // ---- titles ------------------------------------------------------------------

    private static PersistedMeeting Meeting(string title = "Meeting", bool manualTitle = false) => new()
    {
        SchemaVersion = AppDataStore.CurrentMeetingSchemaVersion,
        Id = "meet_1",
        Title = title,
        TitleIsManual = manualTitle,
        Transcript = Transcript,
        Summary = "## Summary\n- generated point",
        TemplateName = "Standard Meeting Notes"
    };

    [Fact]
    public void ATitleIsGeneratedFromTheFirstSubstantiveSentence()
    {
        var title = MeetingTitleService.Generate(Transcript, new DateTime(2026, 1, 1, 9, 0, 0));
        Assert.Contains("ship on Friday", title, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("[09:00:00]", title, StringComparison.Ordinal);
        Assert.DoesNotContain("You:", title, StringComparison.Ordinal);
    }

    [Fact]
    public void GreetingsAndFillerAreSkippedWhenChoosingATitle()
    {
        var title = MeetingTitleService.Generate(
            "[09:00:00] You: Hi.\n[09:00:02] Speaker 1: Um, okay so.\n[09:00:05] You: We need to cut scope for the launch.",
            new DateTime(2026, 1, 1, 9, 0, 0));
        Assert.Contains("cut scope", title, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnEmptyTranscriptFallsBackToADatedTitleRatherThanAnEmptyOne()
    {
        var title = MeetingTitleService.Generate("", new DateTime(2026, 1, 1, 9, 5, 0));
        Assert.Equal("Meeting 2026-01-01 09:05", title);
    }

    [Fact]
    public void GeneratedTitlesAreBoundedAndDeterministic()
    {
        var longLine = "You: " + string.Join(" ", Enumerable.Repeat("discussion", 60));
        var title = MeetingTitleService.Generate(longLine, DateTime.Now);
        Assert.True(title.Length <= MeetingTitleService.MaxTitleLength + 1, $"title was {title.Length} chars");
        Assert.Equal(title, MeetingTitleService.Generate(longLine, DateTime.Now));
    }

    [Fact]
    public void AManualTitleIsNeverOverwrittenByGeneration()
    {
        Assert.Equal("Board sync", MeetingTitleService.Resolve("Board sync", titleIsManual: true, "Generated thing"));
        Assert.Equal("Generated thing", MeetingTitleService.Resolve("Board sync", titleIsManual: false, "Generated thing"));
    }

    [Fact]
    public void EditingTheTitleMarksItManualAndBlankEditsAreRejected()
    {
        var edited = MeetingNotesComposer.ApplyManualTitle(Meeting(), "  Quarterly planning  ");
        Assert.Equal("Quarterly planning", edited.Title);
        Assert.True(edited.TitleIsManual);

        var unchanged = MeetingNotesComposer.ApplyManualTitle(edited, "   ");
        Assert.Equal("Quarterly planning", unchanged.Title);
    }

    // ---- re-summarization preserving manual content ------------------------------

    [Fact]
    public void ReSummarizationReplacesGeneratedNotesButKeepsManualNotes()
    {
        var meeting = MeetingNotesComposer.ApplyManualNotes(Meeting(), "Remember: Priya is on leave next week.");

        var updated = MeetingNotesComposer.ApplyResummarization(
            meeting, "## Summary\n- completely different generated point", "1:1");

        Assert.Equal("## Summary\n- completely different generated point", updated.Summary);
        Assert.Equal("Remember: Priya is on leave next week.", updated.ManualNotes);
        Assert.Equal("1:1", updated.TemplateName);
    }

    [Fact]
    public void ReSummarizationRepeatedManyTimesNeverErodesManualNotes()
    {
        var meeting = MeetingNotesComposer.ApplyManualNotes(Meeting(), "Do not lose me.");
        for (var run = 0; run < 10; run++)
        {
            meeting = MeetingNotesComposer.ApplyResummarization(meeting, $"generated run {run}", "Standard Meeting Notes");
        }
        Assert.Equal("Do not lose me.", meeting.ManualNotes);
        Assert.Equal("generated run 9", meeting.Summary);
    }

    [Fact]
    public void ReSummarizationKeepsAManualTitleButMayReplaceAGeneratedOne()
    {
        var manual = MeetingNotesComposer.ApplyManualTitle(Meeting(), "My chosen title");
        var afterManual = MeetingNotesComposer.ApplyResummarization(manual, "notes", "1:1", "Regenerated title");
        Assert.Equal("My chosen title", afterManual.Title);
        Assert.True(afterManual.TitleIsManual);

        var generated = MeetingNotesComposer.ApplyResummarization(Meeting("Old generated"), "notes", "1:1", "Regenerated title");
        Assert.Equal("Regenerated title", generated.Title);
        Assert.False(generated.TitleIsManual);
    }

    [Fact]
    public void ManualNotesAreRenderedSeparatelyAndNeverMergedIntoGeneratedNotes()
    {
        var meeting = MeetingNotesComposer.ApplyManualNotes(Meeting(), "My own takeaway.");
        var document = MeetingNotesComposer.Document(meeting);

        Assert.Equal("## Summary\n- generated point", document.GeneratedNotes);
        Assert.Equal("My own takeaway.", document.ManualNotes);
        Assert.Contains(MeetingNotesDocument.ManualHeading, document.Rendered, StringComparison.Ordinal);
        Assert.True(
            document.Rendered.IndexOf("generated point", StringComparison.Ordinal) <
            document.Rendered.IndexOf("My own takeaway.", StringComparison.Ordinal));
    }

    [Fact]
    public void ADocumentWithOnlyOneKindOfNotesRendersWithoutStrayHeadings()
    {
        Assert.Equal("just generated", new MeetingNotesDocument("just generated", "").Rendered);
        var manualOnly = new MeetingNotesDocument("", "just mine").Rendered;
        Assert.StartsWith(MeetingNotesDocument.ManualHeading, manualOnly, StringComparison.Ordinal);
        Assert.Contains("just mine", manualOnly, StringComparison.Ordinal);
    }

    [Fact]
    public void MeetingSchema4RoundTripsManualNotesAndTitleOwnership()
    {
        using var directory = new TestDirectory();
        var store = new AppDataStore(directory.Path);
        var meeting = MeetingNotesComposer.ApplyManualTitle(
            MeetingNotesComposer.ApplyManualNotes(Meeting(), "kept"), "Chosen");

        store.SaveMeetings([meeting]);
        var loaded = Assert.Single(store.LoadMeetings());

        Assert.Equal("kept", loaded.ManualNotes);
        Assert.True(loaded.TitleIsManual);
        Assert.Equal("Chosen", loaded.Title);
        Assert.Equal(AppDataStore.CurrentMeetingSchemaVersion, loaded.SchemaVersion);
    }

    [Fact]
    public void LegacyMeetingsWithoutManualNotesMigrateWithoutInventingThem()
    {
        using var directory = new TestDirectory();
        var store = new AppDataStore(directory.Path);
        store.SaveMeetings([new PersistedMeeting
        {
            SchemaVersion = 3,
            Id = "legacy",
            Title = "Legacy meeting",
            Summary = "## Summary\n- old",
            Transcript = "old transcript"
        }]);

        var loaded = Assert.Single(store.LoadMeetings());
        Assert.Equal("", loaded.ManualNotes);
        Assert.False(loaded.TitleIsManual);
        Assert.Equal("Legacy meeting", loaded.Title);
        Assert.Equal("## Summary\n- old", loaded.Summary);
    }

    // ---- fakes -------------------------------------------------------------------

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public Uri? LastRequestUri { get; private set; }
        public string LastRequestBody { get; private set; } = "";
        public string? LastAuthorizationHeader { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri;
            LastAuthorizationHeader = request.Headers.Authorization?.ToString();
            LastRequestBody = request.Content is null
                ? ""
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent(body) };
        }
    }

    private sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw exception;
        }
    }
}
