using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Muesli.Windows.Services;

namespace Muesli.Windows.Tests;

/// <summary>
/// The Anthropic notes provider and hosted dictation (OpenAI Realtime, OpenRouter), ported from
/// macOS. Every request goes to a fake handler or a fake socket; nothing here reaches a real service.
/// </summary>
public sealed class HostedProviderTests
{
    private const string Transcript = "[00:00:01] You: We agreed to ship on Friday.";

    // ─── Anthropic meeting notes ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task AnthropicRequestFollowsTheMessagesApiAndReadsOnlyTextBlocks()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK,
            """{"stop_reason":"end_turn","content":[{"type":"thinking","thinking":""},{"type":"text","text":"## Summary"},{"type":"text","text":"Ship Friday."}]}""");
        var result = await MeetingSummaryService.CreateSummaryResultAsync(
            Transcript, "Planning",
            new MuesliSettings { MeetingSummaryProvider = "anthropic", ResolvedAnthropicApiKey = "  sk-ant-test  " },
            httpClient: new HttpClient(handler), manualNotes: "typed note");

        Assert.False(result.UsedLocalFallback);
        Assert.Equal("anthropic", result.Provider);
        Assert.Equal("## Summary\nShip Friday.", result.Summary);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://api.anthropic.com/v1/messages", request.Uri);
        Assert.Equal("sk-ant-test", request.Headers["x-api-key"]);
        Assert.Equal("2023-06-01", request.Headers["anthropic-version"]);
        Assert.False(request.Headers.ContainsKey("Authorization"));

        using var body = JsonDocument.Parse(request.Body);
        var root = body.RootElement;
        Assert.Equal("claude-sonnet-5-5", root.GetProperty("model").GetString());
        Assert.Equal(AnthropicMessagesClient.MaxOutputTokens, root.GetProperty("max_tokens").GetInt32());
        Assert.Contains("meeting notes", root.GetProperty("system").GetString(), StringComparison.OrdinalIgnoreCase);
        var message = Assert.Single(root.GetProperty("messages").EnumerateArray());
        Assert.Equal("user", message.GetProperty("role").GetString());
        Assert.Contains("ship on Friday", message.GetProperty("content").GetString(), StringComparison.Ordinal);
        Assert.Contains("typed note", message.GetProperty("content").GetString(), StringComparison.Ordinal);
        // Thinking, sampling and effort are left to the model's defaults for notes.
        Assert.False(root.TryGetProperty("output_config", out _));
        Assert.False(root.TryGetProperty("thinking", out _));
        Assert.False(root.TryGetProperty("temperature", out _));
    }

    [Fact]
    public async Task AnthropicUsesTheChosenModelAndAsksForLowEffortOnTitles()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK,
            """{"stop_reason":"end_turn","content":[{"type":"text","text":"Friday Ship Plan"}]}""");
        var title = await MeetingSummaryService.CreateTitleAsync(
            new PersistedMeeting { Title = "Meeting", Transcript = Transcript, CreatedAt = new DateTime(2026, 11, 15) },
            new MuesliSettings
            {
                MeetingSummaryProvider = "anthropic", ResolvedAnthropicApiKey = "sk-ant-test", AnthropicModel = "claude-opus-5-5"
            },
            httpClient: new HttpClient(handler));

        Assert.Equal("Friday Ship Plan", title);
        using var body = JsonDocument.Parse(Assert.Single(handler.Requests).Body);
        Assert.Equal("claude-opus-5-5", body.RootElement.GetProperty("model").GetString());
        Assert.Equal("low", body.RootElement.GetProperty("output_config").GetProperty("effort").GetString());
    }

    [Fact]
    public async Task AnthropicWithoutAKeyFallsBackToLocalNotesWithoutCallingOut()
    {
        var saved = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null);
        try
        {
            var handler = new RecordingHandler(HttpStatusCode.OK, "{}");
            var result = await MeetingSummaryService.CreateSummaryResultAsync(
                Transcript, "Planning", new MuesliSettings { MeetingSummaryProvider = "anthropic" },
                httpClient: new HttpClient(handler));

            Assert.True(result.UsedLocalFallback);
            Assert.Equal("missing-key", result.SafeFailureReason);
            Assert.Contains("## Summary", result.Summary, StringComparison.Ordinal);
            Assert.Empty(handler.Requests);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", saved);
        }
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, """{"type":"error","error":{"type":"authentication_error","message":"invalid x-api-key sk-ant-leak"}}""", "http-401")]
    [InlineData(HttpStatusCode.TooManyRequests, "{}", "http-429")]
    [InlineData(HttpStatusCode.OK, """{"stop_reason":"max_tokens","content":[{"type":"text","text":"Half a summ"}]}""", "output-limit-reached")]
    [InlineData(HttpStatusCode.OK, """{"stop_reason":"refusal","content":[]}""", "refused")]
    [InlineData(HttpStatusCode.OK, """{"stop_reason":"end_turn","content":[{"type":"thinking","thinking":""}]}""", "empty-response")]
    [InlineData(HttpStatusCode.OK, "not json", "malformed-response")]
    public async Task AnthropicFailuresFallBackToLocalNotesWithAReasonThatCarriesNoProviderText(
        HttpStatusCode status, string responseBody, string expectedReason)
    {
        var result = await MeetingSummaryService.CreateSummaryResultAsync(
            Transcript, "Planning",
            new MuesliSettings { MeetingSummaryProvider = "anthropic", ResolvedAnthropicApiKey = "sk-ant-test" },
            httpClient: new HttpClient(new RecordingHandler(status, responseBody)));

        Assert.True(result.UsedLocalFallback);
        Assert.Equal("anthropic", result.Provider);
        Assert.Equal(expectedReason, result.SafeFailureReason);
        Assert.Contains("## Summary", result.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("Half a summ", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void AnthropicIsDisclosedAsAnUploadThatNeedsAKeyAndCountsAsAModelProvider()
    {
        var info = SummaryProviderDisclosure.For("anthropic");
        Assert.Equal(SummaryProviderDisclosure.Anthropic, info.Id);
        Assert.True(info.RequiresApiKey);
        Assert.True(info.SendsTranscriptOffMachine);
        Assert.True(SummaryProviderDisclosure.LeavesMachine("anthropic"));
        Assert.Contains("Anthropic", SummaryProviderDisclosure.DisclosureFor("anthropic"), StringComparison.Ordinal);
        Assert.Contains(SummaryProviderDisclosure.Anthropic, SummaryProviderDisclosure.AvailableIds);
        Assert.True(MeetingSummaryService.IsModelProvider("Anthropic"));
        Assert.Equal(AnthropicMessagesClient.DefaultModel, AnthropicMessagesClient.Models[0].Id);
        Assert.Equal(AnthropicMessagesClient.DefaultModel, AnthropicMessagesClient.NormalizeModel("  "));
    }

    [Fact]
    public void TheAnthropicKeyLivesInTheSecretStoreAndNeverInTheSettingsFile()
    {
        var directory = Directory.CreateTempSubdirectory("muesli-hosted-");
        try
        {
            var path = Path.Combine(directory.FullName, "windows-settings.json");
            var secrets = new InMemorySecretStore();
            var store = new SettingsStore(path, secrets);
            store.SaveSecret(SettingsStore.AnthropicSecretKey, " sk-ant-secret ");
            store.Save(new MuesliSettings
            {
                MeetingSummaryProvider = "anthropic", AnthropicModel = " claude-haiku-5-5 ", ResolvedAnthropicApiKey = "sk-ant-secret"
            });

            Assert.DoesNotContain("sk-ant-secret", File.ReadAllText(path), StringComparison.Ordinal);
            var loaded = new SettingsStore(path, secrets).Load();
            Assert.Equal("sk-ant-secret", loaded.ResolvedAnthropicApiKey);
            Assert.Equal("claude-haiku-5-5", loaded.AnthropicModel);
            Assert.Equal("anthropic", loaded.MeetingSummaryProvider);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    // ─── Dictation provider: settings and the check before recording ─────────────────────────

    [Fact]
    public void DictationStaysLocalUnlessAHostedProviderWasChosen()
    {
        Assert.Equal(DictationProviders.Local, new MuesliSettings().DictationProvider);
        Assert.Equal(DictationProviders.Local, DictationProviders.Normalize(null));
        Assert.Equal(DictationProviders.Local, DictationProviders.Normalize("something-else"));
        Assert.Equal(DictationProviders.OpenAI, DictationProviders.Normalize(" OpenAI "));
        Assert.Equal(DictationProviders.OpenRouter, DictationProviders.Normalize("openRouter"));
        Assert.False(DictationProviders.IsHosted("local"));

        // Keys saved for meeting notes must not turn hosted dictation on by themselves.
        var withKeys = new MuesliSettings { ResolvedOpenAIApiKey = "sk-openai", ResolvedOpenRouterApiKey = "sk-or" };
        Assert.Null(HostedDictationSessions.Create(withKeys));
        Assert.Null(HostedDictationSessions.BlockingMessage(withKeys));

        Assert.Contains("No audio leaves", DictationProviders.Disclosure("local"), StringComparison.Ordinal);
        Assert.Contains("sent to OpenAI", DictationProviders.Disclosure("openai"), StringComparison.Ordinal);
        Assert.Contains("sent to OpenRouter", DictationProviders.Disclosure("openrouter"), StringComparison.Ordinal);
        // The fallback is only promised when the local model is there to provide it.
        Assert.Contains("transcribed on this PC", DictationProviders.Disclosure("openai"), StringComparison.Ordinal);
        foreach (var hosted in new[] { "openai", "openrouter" })
        {
            var withoutModel = DictationProviders.Disclosure(hosted, fallbackModelInstalled: false);
            Assert.Contains("sent to", withoutModel, StringComparison.Ordinal);
            Assert.Contains("that dictation is lost", withoutModel, StringComparison.Ordinal);
            Assert.DoesNotContain("transcribed on this PC", withoutModel, StringComparison.Ordinal);
        }
        Assert.Equal(DictationProviders.Disclosure("local"), DictationProviders.Disclosure("local", fallbackModelInstalled: false));
    }

    [Fact]
    public void ASettingsFileFromBeforeHostedDictationLoadsAsLocal()
    {
        var directory = Directory.CreateTempSubdirectory("muesli-hosted-");
        try
        {
            var path = Path.Combine(directory.FullName, "windows-settings.json");
            File.WriteAllText(path, """{"SchemaVersion":11,"Hotkey":"F9","DictationModelId":"parakeet-v3"}""");
            var loaded = new SettingsStore(path, new InMemorySecretStore()).Load();
            Assert.Equal("F9", loaded.Hotkey);
            Assert.Equal(DictationProviders.Local, loaded.DictationProvider);
            Assert.Equal("", loaded.OpenAIDictationModel);
            Assert.Equal("", loaded.OpenRouterDictationModel);
            Assert.Equal("", loaded.AnthropicModel);

            File.WriteAllText(path, """{"SchemaVersion":11,"DictationProvider":null,"OpenRouterDictationModel":null,"AnthropicModel":null}""");
            loaded = new SettingsStore(path, new InMemorySecretStore()).Load();
            Assert.Equal(DictationProviders.Local, loaded.DictationProvider);
            Assert.Equal("", loaded.OpenRouterDictationModel);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void AMissingKeyOrModelIsReportedBeforeRecordingStarts()
    {
        Assert.Null(HostedDictationActivationPolicy.BlockingMessage("local", "", "", ""));
        Assert.Contains("OpenAI API key", HostedDictationActivationPolicy.BlockingMessage("openai", "  ", "sk-or", "a/b"), StringComparison.Ordinal);
        Assert.Null(HostedDictationActivationPolicy.BlockingMessage("openai", "sk-openai", "", ""));
        Assert.Contains("OpenRouter API key", HostedDictationActivationPolicy.BlockingMessage("openrouter", "sk-openai", "", "a/b"), StringComparison.Ordinal);
        Assert.Contains("transcription model", HostedDictationActivationPolicy.BlockingMessage("openrouter", "", "sk-or", " "), StringComparison.Ordinal);
        Assert.Null(HostedDictationActivationPolicy.BlockingMessage("openrouter", "", "sk-or", "a/b"));

        var settings = new MuesliSettings { DictationProvider = "openrouter", ResolvedOpenRouterApiKey = "sk-or" };
        Assert.NotNull(HostedDictationSessions.BlockingMessage(settings));
        Assert.Null(HostedDictationSessions.BlockingMessage(settings with { OpenRouterDictationModel = "a/b" }));
    }

    // ─── OpenRouter speech-to-text ───────────────────────────────────────────────────────────

    [Fact]
    public async Task OpenRouterPostsTheWholeWavAsBase64AndReadsTheText()
    {
        using var wav = new TempWav([82, 73, 70, 70, 1, 2, 3, 4, 255]);
        var handler = new RecordingHandler(HttpStatusCode.OK, """{"text":"  hello from openrouter \n","usage":{}}""");
        using var session = HostedDictationSessions.Create(
            new MuesliSettings
            {
                DictationProvider = "openrouter", ResolvedOpenRouterApiKey = " sk-or-test ", OpenRouterDictationModel = " openai/whisper-1 "
            },
            openRouterClient: new OpenRouterTranscriptionClient(new HttpClient(handler)))!;
        Assert.False(session.AcceptsLiveAudio);
        session.Append([0.5f]);

        var result = await session.FinishAsync(wav.Path, CancellationToken.None);
        Assert.Equal("hello from openrouter", result.Text);
        Assert.Equal("openrouter-stt", result.Backend);
        Assert.Equal("openai/whisper-1", result.Model);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://openrouter.ai/api/v1/audio/transcriptions", request.Uri);
        Assert.Equal("Bearer sk-or-test", request.Headers["Authorization"]);
        Assert.Equal("Muesli Windows", request.Headers["X-OpenRouter-Title"]);
        Assert.StartsWith("application/json", request.ContentType, StringComparison.Ordinal);
        using var body = JsonDocument.Parse(request.Body);
        Assert.Equal("openai/whisper-1", body.RootElement.GetProperty("model").GetString());
        var audio = body.RootElement.GetProperty("input_audio");
        Assert.Equal("wav", audio.GetProperty("format").GetString());
        Assert.Equal(Convert.ToBase64String(File.ReadAllBytes(wav.Path)), audio.GetProperty("data").GetString());
    }

    [Theory]
    [InlineData(HttpStatusCode.PaymentRequired, """{"error":{"message":"Insufficient   credits\n for this request"}}""", "http-402", "Insufficient credits for this request")]
    [InlineData(HttpStatusCode.InternalServerError, "<html>", "http-500", "HTTP 500")]
    [InlineData(HttpStatusCode.OK, """{"text":"   "}""", "empty-transcript", "empty transcript")]
    [InlineData(HttpStatusCode.OK, """{"result":"x"}""", "empty-transcript", "empty transcript")]
    [InlineData(HttpStatusCode.OK, "not json", "malformed-response", "could not be read")]
    public async Task OpenRouterFailuresAreReportedWithAFixedCategory(
        HttpStatusCode status, string responseBody, string category, string messagePart)
    {
        using var wav = new TempWav([1, 2, 3]);
        var client = new OpenRouterTranscriptionClient(new HttpClient(new RecordingHandler(status, responseBody)));
        var error = await Assert.ThrowsAsync<HostedDictationException>(
            () => client.TranscribeAsync(wav.Path, "sk-or-test", "a/b", CancellationToken.None));
        Assert.Equal(category, error.Category);
        Assert.Contains(messagePart, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-or-test", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenRouterTimesOutAsAFailureButCancellationStaysCancellation()
    {
        using var wav = new TempWav([1, 2, 3]);
        var never = new RecordingHandler(HttpStatusCode.OK, "{}") { Delay = Timeout.InfiniteTimeSpan };
        var client = new OpenRouterTranscriptionClient(new HttpClient(never));

        var timedOut = await Assert.ThrowsAsync<HostedDictationException>(
            () => client.TranscribeAsync(wav.Path, "sk-or", "a/b", CancellationToken.None, TimeSpan.FromMilliseconds(50)));
        Assert.Equal("timeout", timedOut.Category);

        using var cancel = new CancellationTokenSource();
        var pending = client.TranscribeAsync(wav.Path, "sk-or", "a/b", cancel.Token);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);

        using var session = new OpenRouterHostedDictationSession("sk-or", "a/b", client);
        var finishing = session.FinishAsync(wav.Path, CancellationToken.None);
        session.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => finishing);

        // Nothing is sent without a key or a model.
        var unsent = new RecordingHandler(HttpStatusCode.OK, """{"text":"x"}""");
        var guarded = new OpenRouterTranscriptionClient(new HttpClient(unsent));
        Assert.Equal("missing-key", (await Assert.ThrowsAsync<HostedDictationException>(
            () => guarded.TranscribeAsync(wav.Path, " ", "a/b", CancellationToken.None))).Category);
        Assert.Equal("missing-model", (await Assert.ThrowsAsync<HostedDictationException>(
            () => guarded.TranscribeAsync(wav.Path, "sk-or", " ", CancellationToken.None))).Category);
        Assert.Empty(unsent.Requests);
    }

    // ─── Hosted result, fallback to the local model ──────────────────────────────────────────

    [Fact]
    public async Task AHostedTranscriptIsMarkedHostedAndTheLocalModelIsNotUsed()
    {
        var localCalls = 0;
        var result = await HostedDictationTranscriber.TranscribeAsync(
            new StubSession(() => Task.FromResult(new HostedDictationResult("hosted words", "openai-realtime", "gpt-live-transcribe", "wss://x"))),
            "recording.wav",
            () => { localCalls++; return Task.FromResult(new TranscriptionResult("local words")); },
            _ => { },
            CancellationToken.None);

        Assert.Equal("hosted words", result.Text);
        Assert.Equal(0, localCalls);
        Assert.True(HostedDictationTranscriber.IsHostedResult(result));
        Assert.False(HostedDictationTranscriber.IsFallbackResult(result));
        // The coordinator appends its own diagnostics below the first line.
        var annotated = result with { Diagnostic = result.Diagnostic + Environment.NewLine + "Latency trace: abc" };
        Assert.Equal("openai-realtime:gpt-live-transcribe", HostedDictationTranscriber.ModelLabel(annotated));
        Assert.False(HostedDictationTranscriber.IsHostedResult(new TranscriptionResult("x", "ASR during capture: 3 chunks")));
    }

    [Fact]
    public async Task AHostedFailureTranscribesTheSameRecordingLocallyAndLogsOnlyACategory()
    {
        var log = new List<string>();
        string? fallbackPath = null;
        var result = await HostedDictationTranscriber.TranscribeAsync(
            new StubSession(() => throw new HostedDictationException("OpenRouter transcription failed (HTTP 500): secret detail", "http-500")),
            "recording.wav",
            () => { fallbackPath = "recording.wav"; return Task.FromResult(new TranscriptionResult("local words", "Local diagnostic")); },
            log.Add,
            CancellationToken.None);

        Assert.Equal("local words", result.Text);
        Assert.Equal("recording.wav", fallbackPath);
        Assert.True(HostedDictationTranscriber.IsFallbackResult(result));
        Assert.False(HostedDictationTranscriber.IsHostedResult(result));
        Assert.Contains("Local diagnostic", result.Diagnostic, StringComparison.Ordinal);
        var line = Assert.Single(log);
        Assert.Contains("reason=http-500", line, StringComparison.Ordinal);
        Assert.DoesNotContain("secret detail", line, StringComparison.Ordinal);
        Assert.DoesNotContain("local words", line, StringComparison.Ordinal);

        // An unexpected exception type falls back too.
        var other = await HostedDictationTranscriber.TranscribeAsync(
            new StubSession(() => throw new InvalidOperationException("boom")), "recording.wav",
            () => Task.FromResult(new TranscriptionResult("local again")), _ => { }, CancellationToken.None);
        Assert.Equal("local again", other.Text);
    }

    [Fact]
    public async Task CancellingDoesNotFallBackAndAMissingLocalModelSurfacesTheHostedError()
    {
        var localCalls = 0;
        Task<TranscriptionResult> Local() { localCalls++; return Task.FromResult(new TranscriptionResult("local")); }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => HostedDictationTranscriber.TranscribeAsync(
            new StubSession(() => throw new OperationCanceledException()), "recording.wav", Local, _ => { }, CancellationToken.None));

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAsync<HostedDictationException>(() => HostedDictationTranscriber.TranscribeAsync(
            new StubSession(() => throw new HostedDictationException("failed", "network")), "recording.wav", Local, _ => { }, cancelled.Token));
        Assert.Equal(0, localCalls);

        Assert.False(HostedDictationFallbackPolicy.ShouldFallback(new TaskCanceledException(), cancellationRequested: false));
        Assert.False(HostedDictationFallbackPolicy.ShouldFallback(new HttpRequestException(), cancellationRequested: true));
        Assert.True(HostedDictationFallbackPolicy.ShouldFallback(new HttpRequestException(), cancellationRequested: false));

        var noLocal = await Assert.ThrowsAsync<HostedDictationException>(() => HostedDictationTranscriber.TranscribeAsync(
            new StubSession(() => throw new HostedDictationException("OpenAI did not finish the transcript in time.", "timeout")),
            "recording.wav", localFallback: null, _ => { }, CancellationToken.None));
        Assert.Equal("timeout", noLocal.Category);
    }

    // ─── OpenAI Realtime transcription ───────────────────────────────────────────────────────

    [Fact]
    public void ThePcmEncoderResamplesTo24KhzIdenticallyAcrossBufferBoundaries()
    {
        var samples = Enumerable.Range(0, 1600).Select(index => MathF.Sin(index * 0.05f) * 0.8f).ToArray();
        var whole = new OpenAIRealtimePcmEncoder().Encode(samples);
        // 16 kHz -> 24 kHz is three outputs for every two inputs; the last input waits for its successor.
        Assert.Equal((samples.Length - 1) * 3 / 2 * 2 + 2, whole.Length);

        var chunked = new OpenAIRealtimePcmEncoder();
        var pieces = new List<byte>();
        var offset = 0;
        foreach (var size in new[] { 1, 7, 160, 333, 1099 })
        {
            pieces.AddRange(chunked.Encode(samples.AsSpan(offset, size)));
            offset += size;
        }

        Assert.Equal(whole, pieces.ToArray());
        Assert.Empty(new OpenAIRealtimePcmEncoder().Encode([]));

        // Full scale maps to the 16-bit limits, little-endian, and out-of-range input is clamped.
        var extremes = new OpenAIRealtimePcmEncoder().Encode([1f, 1f, -1f, -1f, 2f, 2f]);
        Assert.Equal(short.MaxValue, BitConverter.ToInt16(extremes, 0));
        Assert.Equal(short.MinValue, BitConverter.ToInt16(extremes, 6));
        Assert.Equal(short.MaxValue, BitConverter.ToInt16(extremes, extremes.Length - 2));
    }

    [Fact]
    public async Task OpenAIRealtimeConfiguresATranscriptionSessionStreamsAudioCommitsAndReturnsTheTranscript()
    {
        var socket = new FakeSocket();
        using var session = HostedDictationSessions.Create(
            new MuesliSettings { DictationProvider = "openai", ResolvedOpenAIApiKey = " sk-openai-test " },
            socketFactory: () => socket)!;
        Assert.True(session.AcceptsLiveAudio);

        // Audio captured before the service is ready is kept and sent once it is.
        session.Append(Enumerable.Repeat(0.25f, 1600).ToArray());
        socket.Receive("""{"type":"session.created","session":{}}""");
        var update = await socket.NextSentAsync();
        using (var document = JsonDocument.Parse(update))
        {
            var root = document.RootElement;
            Assert.Equal("session.update", root.GetProperty("type").GetString());
            var sessionJson = root.GetProperty("session");
            Assert.Equal("transcription", sessionJson.GetProperty("type").GetString());
            var input = sessionJson.GetProperty("audio").GetProperty("input");
            Assert.Equal("audio/pcm", input.GetProperty("format").GetProperty("type").GetString());
            Assert.Equal(24000, input.GetProperty("format").GetProperty("rate").GetInt32());
            Assert.Equal("gpt-live-transcribe", input.GetProperty("transcription").GetProperty("model").GetString());
            Assert.Equal(JsonValueKind.Null, input.GetProperty("turn_detection").ValueKind);
        }

        Assert.Equal("wss://api.openai.com/v1/realtime?intent=transcription", socket.Endpoint!.AbsoluteUri);
        Assert.Equal("sk-openai-test", socket.BearerToken);

        socket.Receive("""{"type":"session.updated"}""");
        var append = await socket.NextSentAsync();
        using (var document = JsonDocument.Parse(append))
        {
            Assert.Equal("input_audio_buffer.append", document.RootElement.GetProperty("type").GetString());
            var pcm = Convert.FromBase64String(document.RootElement.GetProperty("audio").GetString()!);
            Assert.Equal(new OpenAIRealtimePcmEncoder().Encode(Enumerable.Repeat(0.25f, 1600).ToArray()), pcm);
        }

        session.Append(Enumerable.Repeat(-0.5f, 320).ToArray());
        Assert.Contains("input_audio_buffer.append", await socket.NextSentAsync(), StringComparison.Ordinal);

        var finishing = session.FinishAsync("unused.wav", CancellationToken.None);
        Assert.Equal(OpenAIRealtimeProtocol.Commit, await socket.NextSentAsync());
        socket.Receive("""{"type":"input_audio_buffer.committed","item_id":"item_1"}""");
        socket.Receive("""{"type":"conversation.item.input_audio_transcription.delta","item_id":"item_1","content_index":0,"delta":"Hello "}""");
        socket.Receive("""{"type":"conversation.item.input_audio_transcription.completed","item_id":"item_1","content_index":0,"transcript":" Hello from OpenAI. "}""");

        var result = await finishing.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("Hello from OpenAI.", result.Text);
        Assert.Equal("openai-realtime", result.Backend);
        Assert.Equal("gpt-live-transcribe", result.Model);
        Assert.True(socket.Aborted);
    }

    [Fact]
    public async Task OpenAIRealtimeUsesTheChosenModelAndFallsBackToDeltasWhenCompletedHasNoTranscript()
    {
        var socket = new FakeSocket { AutoHandshake = true };
        using var session = new OpenAIRealtimeDictationSession("sk-openai", "gpt-transcribe", () => socket);
        Assert.Contains("\"model\":\"gpt-transcribe\"", await socket.NextSentAsync(), StringComparison.Ordinal);
        session.Append(new float[160]);
        await socket.NextSentAsync();

        var finishing = session.FinishAsync("unused.wav", CancellationToken.None);
        await socket.NextSentAsync();
        socket.Receive("""{"type":"conversation.item.input_audio_transcription.delta","item_id":"a","delta":"streamed "}""");
        socket.Receive("""{"type":"conversation.item.input_audio_transcription.delta","item_id":"a","delta":"text"}""");
        socket.Receive("""{"type":"conversation.item.input_audio_transcription.completed","item_id":"a"}""");
        Assert.Equal("streamed text", (await finishing.WaitAsync(TimeSpan.FromSeconds(10))).Text);
    }

    [Theory]
    [InlineData("""{"type":"error","error":{"message":"Incorrect API key provided"}}""", "server-error", "Incorrect API key provided")]
    [InlineData("""{"type":"conversation.item.input_audio_transcription.failed","item_id":"a","error":{"code":"audio_unintelligible"}}""", "transcription-failed", "audio_unintelligible")]
    [InlineData("""{"type":"conversation.item.input_audio_transcription.completed","item_id":"a","transcript":"  "}""", "empty-transcript", "empty transcript")]
    [InlineData("not json", "malformed-response", "could not be read")]
    public async Task OpenAIRealtimeFailuresAfterTheCommitAreReported(string serverMessage, string category, string messagePart)
    {
        var socket = new FakeSocket { AutoHandshake = true };
        using var session = new OpenAIRealtimeDictationSession("sk-openai", null, () => socket);
        await socket.NextSentAsync();
        var finishing = session.FinishAsync("unused.wav", CancellationToken.None);
        Assert.Equal(OpenAIRealtimeProtocol.Commit, await socket.NextSentAsync());
        socket.Receive(serverMessage);

        var error = await Assert.ThrowsAsync<HostedDictationException>(() => finishing.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(category, error.Category);
        Assert.Contains(messagePart, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenAIRealtimeReportsConnectionFailuresTimeoutsAndAClosedSocket()
    {
        // The connection is refused: the failure reaches Finish, with no commit sent.
        var refused = new FakeSocket { ConnectError = new System.Net.WebSockets.WebSocketException("handshake 401") };
        using (var session = new OpenAIRealtimeDictationSession("sk-openai", null, () => refused))
        {
            var error = await Assert.ThrowsAsync<HostedDictationException>(
                () => session.FinishAsync("unused.wav", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal("network", error.Category);
            Assert.Empty(refused.Sent);
        }

        // The service never confirms the session.
        var silent = new FakeSocket();
        using (var session = new OpenAIRealtimeDictationSession(
                   "sk-openai", null, () => silent, readyTimeout: TimeSpan.FromMilliseconds(80)))
        {
            var error = await Assert.ThrowsAsync<HostedDictationException>(
                () => session.FinishAsync("unused.wav", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal("timeout", error.Category);
        }

        // The transcript never arrives after the commit.
        var stalled = new FakeSocket { AutoHandshake = true };
        using (var session = new OpenAIRealtimeDictationSession(
                   "sk-openai", null, () => stalled, finalTimeout: TimeSpan.FromMilliseconds(80)))
        {
            var error = await Assert.ThrowsAsync<HostedDictationException>(
                () => session.FinishAsync("unused.wav", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal("timeout", error.Category);
        }

        // The service hangs up while the transcript is awaited.
        var closing = new FakeSocket { AutoHandshake = true };
        using (var session = new OpenAIRealtimeDictationSession("sk-openai", null, () => closing))
        {
            await closing.NextSentAsync();
            var finishing = session.FinishAsync("unused.wav", CancellationToken.None);
            await closing.NextSentAsync();
            closing.Close();
            var error = await Assert.ThrowsAsync<HostedDictationException>(() => finishing.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal("connection-closed", error.Category);
        }

        // No key: nothing is connected.
        var unused = new FakeSocket();
        using (var session = new OpenAIRealtimeDictationSession("  ", null, () => unused))
        {
            var error = await Assert.ThrowsAsync<HostedDictationException>(
                () => session.FinishAsync("unused.wav", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal("missing-key", error.Category);
            Assert.Null(unused.Endpoint);
        }
    }

    [Fact]
    public async Task OpenAIRealtimeCancellationIsNotAFailureAndTooMuchQueuedAudioIs()
    {
        var socket = new FakeSocket { AutoHandshake = true };
        using (var session = new OpenAIRealtimeDictationSession("sk-openai", null, () => socket))
        {
            await socket.NextSentAsync();
            var finishing = session.FinishAsync("unused.wav", CancellationToken.None);
            await socket.NextSentAsync();
            session.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => finishing.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.True(socket.Aborted);
        }

        // The service never becomes ready while audio keeps arriving: past the limit the session
        // stops holding audio and reports that the network could not keep up.
        var slow = new FakeSocket();
        using (var session = new OpenAIRealtimeDictationSession("sk-openai", null, () => slow))
        {
            var second = new float[16_000];
            for (var index = 0; index <= OpenAIRealtimeDictationSession.MaxQueuedSamples / second.Length; index++) session.Append(second);
            slow.Receive("""{"type":"session.created"}""");
            await slow.NextSentAsync();
            slow.Receive("""{"type":"session.updated"}""");
            var error = await Assert.ThrowsAsync<HostedDictationException>(
                () => session.FinishAsync("unused.wav", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20)));
            Assert.Equal("audio-backpressure", error.Category);
            Assert.DoesNotContain(OpenAIRealtimeProtocol.Commit, slow.Sent);
        }
    }

    // ─── Connect OpenRouter (browser sign-in) ────────────────────────────────────────────────

    [Fact]
    public void OpenRouterAuthorizationUrlCarriesTheCallbackAndAnS256Challenge()
    {
        var (verifier, challenge) = OpenRouterAuthFlow.CreatePkce();
        Assert.Equal(43, verifier.Length);
        Assert.Equal(43, challenge.Length);
        Assert.NotEqual(verifier, challenge);
        Assert.NotEqual(verifier, OpenRouterAuthFlow.CreatePkce().Verifier);

        var uri = OpenRouterAuthFlow.BuildAuthorizationUri(new Uri("http://localhost:51234/muesli/openrouter/oauth/abc"), challenge).AbsoluteUri;
        Assert.StartsWith("https://openrouter.ai/auth?callback_url=", uri, StringComparison.Ordinal);
        Assert.Contains("callback_url=http%3A%2F%2Flocalhost%3A51234%2Fmuesli%2Fopenrouter%2Foauth%2Fabc", uri, StringComparison.Ordinal);
        Assert.Contains($"code_challenge={challenge}", uri, StringComparison.Ordinal);
        Assert.Contains("code_challenge_method=S256", uri, StringComparison.Ordinal);

        Assert.Equal(
            "https://openrouter.ai/keys/" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("sk-or-x"))),
            OpenRouterAuthFlow.ManageKeyUri("sk-or-x").AbsoluteUri);
    }

    [Fact]
    public void OpenRouterCallbackIsAcceptedOnlyOnThisSignInsPath()
    {
        const string path = "/muesli/openrouter/oauth/secret";
        Assert.Equal(
            new OpenRouterAuthFlow.Callback(OpenRouterAuthFlow.CallbackKind.AuthorizationCode, "abc 123"),
            OpenRouterAuthFlow.ParseCallback($"GET {path}?code=abc%20123 HTTP/1.1\r\nHost: localhost\r\n\r\n", path));
        Assert.Equal(
            new OpenRouterAuthFlow.Callback(OpenRouterAuthFlow.CallbackKind.ProviderError, "access_denied"),
            OpenRouterAuthFlow.ParseCallback($"GET {path}?error=access_denied HTTP/1.1", path));
        Assert.Equal(OpenRouterAuthFlow.CallbackKind.MissingCode, OpenRouterAuthFlow.ParseCallback($"GET {path} HTTP/1.1", path).Kind);
        Assert.Equal(OpenRouterAuthFlow.CallbackKind.MissingCode, OpenRouterAuthFlow.ParseCallback($"GET {path}?code= HTTP/1.1", path).Kind);
        Assert.Equal(OpenRouterAuthFlow.CallbackKind.Invalid, OpenRouterAuthFlow.ParseCallback($"POST {path}?code=abc HTTP/1.1", path).Kind);
        Assert.Equal(OpenRouterAuthFlow.CallbackKind.Invalid, OpenRouterAuthFlow.ParseCallback("garbage", path).Kind);
        // A favicon, a port scanner or a guess at another path neither completes nor fails the sign-in.
        Assert.Equal(OpenRouterAuthFlow.CallbackKind.Unrelated, OpenRouterAuthFlow.ParseCallback("GET /favicon.ico HTTP/1.1", path).Kind);
        Assert.Equal(OpenRouterAuthFlow.CallbackKind.Unrelated, OpenRouterAuthFlow.ParseCallback("GET /muesli/openrouter/oauth/other?code=abc HTTP/1.1", path).Kind);
    }

    [Fact]
    public async Task OpenRouterSignInWaitsForTheBrowserCallbackAndExchangesTheCodeForAKey()
    {
        var exchange = new RecordingHandler(HttpStatusCode.OK, """{"key":" sk-or-v1-new ","user_id":"user_1"}""");
        var flow = new OpenRouterAuthFlow(new HttpClient(exchange));
        Uri? opened = null;
        string? strayStatus = null;
        string? approvedPage = null;

        var key = await flow.SignInAsync(uri =>
        {
            opened = uri;
            // Stands in for the browser: OpenRouter redirects it to the loopback callback address.
            var callback = Uri.UnescapeDataString(uri.Query.Split("callback_url=")[1].Split('&')[0]);
            _ = Task.Run(async () =>
            {
                using var browser = new HttpClient();
                strayStatus = ((int)(await browser.GetAsync(new Uri(new Uri(callback), "/favicon.ico"))).StatusCode).ToString();
                approvedPage = await (await browser.GetAsync(callback + "?code=auth-code-1")).Content.ReadAsStringAsync();
            });
            return Task.FromResult(true);
        }).WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal("sk-or-v1-new", key);
        Assert.Equal("404", strayStatus);
        // The page is served before the key exchange, so it reports approval and not a finished connection.
        Assert.Contains(OpenRouterAuthFlow.ApprovedPageTitle, approvedPage, StringComparison.Ordinal);
        Assert.DoesNotContain("connected", approvedPage, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("https://openrouter.ai/auth?", opened!.AbsoluteUri, StringComparison.Ordinal);
        Assert.Contains("callback_url=http%3A%2F%2Flocalhost%3A", opened.AbsoluteUri, StringComparison.Ordinal);

        var request = Assert.Single(exchange.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://openrouter.ai/api/v1/auth/keys", request.Uri);
        Assert.False(request.Headers.ContainsKey("Authorization"));
        using var body = JsonDocument.Parse(request.Body);
        Assert.Equal("auth-code-1", body.RootElement.GetProperty("code").GetString());
        Assert.Equal("S256", body.RootElement.GetProperty("code_challenge_method").GetString());
        // The verifier sent to the exchange is the one whose hash went to the browser.
        var verifier = body.RootElement.GetProperty("code_verifier").GetString()!;
        var challenge = Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Assert.Contains($"code_challenge={challenge}", opened.AbsoluteUri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenRouterSignInReportsARefusalATimeoutACancellationAndABadExchange()
    {
        static Func<Uri, Task<bool>> Browser(string query) => uri =>
        {
            var callback = Uri.UnescapeDataString(uri.Query.Split("callback_url=")[1].Split('&')[0]);
            _ = Task.Run(async () =>
            {
                using var browser = new HttpClient();
                await browser.GetAsync(callback + query);
            });
            return Task.FromResult(true);
        };

        var unused = new RecordingHandler(HttpStatusCode.OK, """{"key":"sk-or"}""");
        var flow = new OpenRouterAuthFlow(new HttpClient(unused));
        var refused = await Assert.ThrowsAsync<OpenRouterAuthException>(
            () => flow.SignInAsync(Browser("?error=access_denied%3Cscript%3E")).WaitAsync(TimeSpan.FromSeconds(20)));
        Assert.Contains("not approved (access_deniedscript)", refused.Message, StringComparison.Ordinal);

        var timedOut = await Assert.ThrowsAsync<OpenRouterAuthException>(
            () => flow.SignInAsync(_ => Task.FromResult(true), callbackTimeout: TimeSpan.FromMilliseconds(100)).WaitAsync(TimeSpan.FromSeconds(20)));
        Assert.Contains("timed out", timedOut.Message, StringComparison.Ordinal);

        using var cancel = new CancellationTokenSource();
        var pending = flow.SignInAsync(_ => Task.FromResult(true), cancel.Token);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(20)));
        // No browser could be opened: fail at once instead of waiting out the callback timeout.
        foreach (var launch in new Func<Uri, Task<bool>>[] { _ => Task.FromResult(false), _ => throw new InvalidOperationException("no browser") })
        {
            var noBrowser = await Assert.ThrowsAsync<OpenRouterAuthException>(
                () => flow.SignInAsync(launch).WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Contains("Could not open the browser", noBrowser.Message, StringComparison.Ordinal);
        }

        Assert.Empty(unused.Requests);

        var rejected = await Assert.ThrowsAsync<OpenRouterAuthException>(
            () => new OpenRouterAuthFlow(new HttpClient(new RecordingHandler(HttpStatusCode.Forbidden, """{"error":"sk-or-leak"}""")))
                .ExchangeAsync("code", "verifier", CancellationToken.None));
        Assert.Equal("OpenRouter key exchange failed (HTTP 403)", rejected.Message);

        foreach (var payload in new[] { "{}", """{"key":"  "}""", "not json" })
        {
            var invalid = await Assert.ThrowsAsync<OpenRouterAuthException>(
                () => new OpenRouterAuthFlow(new HttpClient(new RecordingHandler(HttpStatusCode.OK, payload)))
                    .ExchangeAsync("code", "verifier", CancellationToken.None));
            Assert.Contains("invalid response", invalid.Message, StringComparison.Ordinal);
        }
    }

    // ─── Fakes ───────────────────────────────────────────────────────────────────────────────

    private sealed record RecordedRequest(
        HttpMethod Method, string Uri, Dictionary<string, string> Headers, string ContentType, string Body);

    private sealed class RecordingHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];
        public TimeSpan Delay { get; init; } = TimeSpan.Zero;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new RecordedRequest(
                request.Method,
                request.RequestUri!.AbsoluteUri,
                request.Headers.ToDictionary(header => header.Key, header => string.Join(",", header.Value), StringComparer.OrdinalIgnoreCase),
                request.Content?.Headers.ContentType?.ToString() ?? "",
                request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken)));
            if (Delay != TimeSpan.Zero) await Task.Delay(Delay, cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class StubSession(Func<Task<HostedDictationResult>> finish) : IHostedDictationSession
    {
        public string Provider => DictationProviders.OpenRouter;
        public bool AcceptsLiveAudio => false;
        public void Append(float[] samples) { }
        public Task<HostedDictationResult> FinishAsync(string recordedWavPath, CancellationToken cancellationToken) => finish();
        public void Cancel() { }
        public void Dispose() { }
    }

    /// <summary>Stands in for the Realtime service: records what is sent and replays scripted server events.</summary>
    private sealed class FakeSocket : IRealtimeSocket
    {
        private readonly Channel<string> _incoming = Channel.CreateUnbounded<string>();
        private readonly Channel<string> _sent = Channel.CreateUnbounded<string>();
        private readonly List<string> _allSent = [];

        /// <summary>Answers the connection with session.created and the session.update with session.updated.</summary>
        public bool AutoHandshake { get; init; }
        public Exception? ConnectError { get; init; }
        public Uri? Endpoint { get; private set; }
        public string? BearerToken { get; private set; }
        public bool Aborted { get; private set; }
        public IReadOnlyList<string> Sent { get { lock (_allSent) return _allSent.ToArray(); } }

        public void Receive(string message) => _incoming.Writer.TryWrite(message);
        public void Close() => _incoming.Writer.TryComplete();
        public Task<string> NextSentAsync() => _sent.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        public Task ConnectAsync(Uri endpoint, string bearerToken, CancellationToken cancellationToken)
        {
            if (ConnectError is not null) return Task.FromException(ConnectError);
            Endpoint = endpoint;
            BearerToken = bearerToken;
            if (AutoHandshake) Receive("""{"type":"session.created"}""");
            return Task.CompletedTask;
        }

        public Task SendTextAsync(string message, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Aborted) return Task.FromException(new System.Net.WebSockets.WebSocketException("aborted"));
            lock (_allSent) _allSent.Add(message);
            _sent.Writer.TryWrite(message);
            if (AutoHandshake && message.Contains("session.update", StringComparison.Ordinal)) Receive("""{"type":"session.updated"}""");
            return Task.CompletedTask;
        }

        public async Task<string?> ReceiveTextAsync(CancellationToken cancellationToken)
        {
            try
            {
                return await _incoming.Reader.ReadAsync(cancellationToken);
            }
            catch (ChannelClosedException)
            {
                return null;
            }
        }

        public void Abort()
        {
            Aborted = true;
            _incoming.Writer.TryComplete();
        }

        public void Dispose() => Abort();
    }

    private sealed class TempWav : IDisposable
    {
        public TempWav(byte[] bytes)
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"muesli-hosted-{Guid.NewGuid():N}.wav");
            File.WriteAllBytes(Path, bytes);
        }

        public string Path { get; }
        public void Dispose() => File.Delete(Path);
    }
}
