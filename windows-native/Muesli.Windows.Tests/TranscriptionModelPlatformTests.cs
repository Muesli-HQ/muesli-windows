namespace Muesli.Windows.Tests;

public sealed class TranscriptionModelPlatformTests
{
    [Fact]
    public void CatalogPinsArchiveAndEveryRequiredFileAndRejectsUnknownRuntimeIds()
    {
        Assert.Equal(12, TranscriptionModelCatalog.Models.Count);
        Assert.All(TranscriptionModelCatalog.Models, model =>
        {
            Assert.Matches("^[A-F0-9]{64}$", model.ArchiveSha256);
            Assert.NotEmpty(model.RequiredFileSha256);
            Assert.All(model.RequiredFileSha256, file =>
            {
                Assert.False(Path.IsPathRooted(file.Key));
                Assert.DoesNotContain("..", file.Key, StringComparison.Ordinal);
                Assert.Matches("^[A-F0-9]{64}$", file.Value);
            });
        });
        Assert.Throws<ArgumentOutOfRangeException>(() => TranscriptionModelCatalog.GetRequired("coreml-only"));
    }

    [Fact]
    public void LegacySingleSelectionMigratesToBothOfflineRolesAndIsRemovedFromJson()
    {
        using var directory = new TestDirectory();
        var path = directory.File("settings.json");
        File.WriteAllText(path, "{\"SchemaVersion\":1,\"TranscriptionModelId\":\"whisper-small-en\"}");
        var store = new SettingsStore(path, new InMemorySecretStore());

        var settings = store.Load();

        Assert.Equal(MuesliSettings.CurrentSchemaVersion, settings.SchemaVersion);
        Assert.Equal("whisper-small-en", settings.DictationModelId);
        Assert.Equal("whisper-small-en", settings.FinalMeetingModelId);
        Assert.Null(settings.LiveMeetingModelId);
        var persisted = File.ReadAllText(path);
        Assert.DoesNotContain("TranscriptionModelId", persisted, StringComparison.Ordinal);
        Assert.Contains("dictationModelId", persisted, StringComparison.Ordinal);
        Assert.Contains("finalMeetingModelId", persisted, StringComparison.Ordinal);
    }

    [Fact]
    public void IndependentRoleSelectionsRoundTripWithoutLiveFallback()
    {
        using var directory = new TestDirectory();
        var path = directory.File("settings.json");
        var store = new SettingsStore(path, new InMemorySecretStore());
        store.Save(new MuesliSettings
        {
            DictationModelId = "whisper-tiny-en",
            FinalMeetingModelId = "qwen3-asr-0.6b-int8",
            LiveMeetingModelId = "unsupported-streaming"
        });

        var settings = store.Load();

        Assert.Equal("whisper-tiny-en", settings.DictationModelId);
        Assert.Equal("qwen3-asr-0.6b-int8", settings.FinalMeetingModelId);
        Assert.Null(settings.LiveMeetingModelId);
        Assert.DoesNotContain("unsupported-streaming", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public void EarlierWindowsProfilePreservesItsExplicitAsrEngineDuringRoleMigration()
    {
        using var directory = new TestDirectory();
        var path = directory.File("settings.json");
        File.WriteAllText(
            path,
            "{\"AsrEngine\":\"parakeet-v3\",\"ModelProfile\":\"large-v3\",\"DictationModelProfile\":\"medium\"}");
        var store = new SettingsStore(path, new InMemorySecretStore());

        var settings = store.Load();

        Assert.Equal("parakeet-v3", settings.DictationModelId);
        Assert.Equal("parakeet-v3", settings.FinalMeetingModelId);
        var persisted = File.ReadAllText(path);
        Assert.DoesNotContain("AsrEngine", persisted, StringComparison.Ordinal);
        Assert.DoesNotContain("ModelProfile", persisted, StringComparison.Ordinal);
        Assert.DoesNotContain("DictationModelProfile", persisted, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ClientSwitchRoutesToTheRequestedModelAndDisposesThePreviousRecognizer()
    {
        var factory = new FakeSessionFactory();
        using var client = new NativeTranscriptionClient(
            TranscriptionModelCatalog.GetRequired("parakeet-v3"), factory, _ => true);

        var first = await client.TranscribeAsync([]);
        await client.SwitchModelAsync("sensevoice-small-int8");
        var second = await client.TranscribeAsync([]);

        Assert.Equal("parakeet-v3", first.Text);
        Assert.Equal("sensevoice-small-int8", second.Text);
        Assert.True(factory.Sessions[0].Disposed);
        Assert.False(factory.Sessions[1].Disposed);
        Assert.Equal("sensevoice-small-int8", client.ModelId);
    }

    [Fact]
    public async Task SwitchWaitsForActiveInferenceBeforeDisposingItsRecognizer()
    {
        var inference = new TaskCompletionSource<TranscriptionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new FakeSessionFactory(inference);
        using var client = new NativeTranscriptionClient(
            TranscriptionModelCatalog.GetRequired("parakeet-v3"), factory, _ => true);
        var active = client.TranscribeAsync([]);
        await factory.Created.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var switching = client.SwitchModelAsync("whisper-tiny-en");
        Assert.False(switching.IsCompleted);
        Assert.False(factory.Sessions[0].Disposed);
        inference.SetResult(new TranscriptionResult("finished"));
        await active;
        await switching;

        Assert.True(factory.Sessions[0].Disposed);
        Assert.Equal("whisper-tiny-en", client.ModelId);
    }

    [Fact]
    public async Task DefaultSessionPoolSharesOneRecognizerUntilEveryRoleReleasesIt()
    {
        var sessions = new List<FakeSession>();
        var factory = new NativeTranscriptionModelSessionFactory(model =>
        {
            var session = new FakeSession(model.Id, null);
            sessions.Add(session);
            return session;
        });
        var model = TranscriptionModelCatalog.GetRequired("parakeet-v3");
        using var dictation = new NativeTranscriptionClient(model, factory, _ => true);
        using var meeting = new NativeTranscriptionClient(model, factory, _ => true);

        await dictation.InitializeAsync();
        await meeting.InitializeAsync();

        Assert.Single(sessions);
        Assert.Equal(1, factory.ActiveSessionCount);
        await dictation.ReleaseModelAsync(model.Id);
        Assert.False(sessions[0].Disposed);
        Assert.Equal(1, factory.ActiveSessionCount);
        await meeting.ReleaseModelAsync(model.Id);
        Assert.True(sessions[0].Disposed);
        Assert.Equal(0, factory.ActiveSessionCount);
    }

    [Theory]
    [InlineData("cpu", 16, null, 6)]
    [InlineData("cpu", 4, null, 4)]
    [InlineData("cuda", 16, null, 4)]
    [InlineData("cpu", 16, "2", 2)]
    [InlineData("cpu", 16, "99", 32)]
    public void ParakeetThreadPolicyAvoidsCpuOversubscription(
        string provider,
        int processorCount,
        string? configured,
        int expected)
    {
        Assert.Equal(expected, NativeParakeetClient.RecommendedThreadCount(provider, processorCount, configured));
    }

    [Fact]
    public async Task LifecyclePrepareDoesNotChangeRoleSelectionAndReportsReady()
    {
        var operations = new FakeModelOperations();
        var selected = new HashSet<string>(["parakeet-v3"], StringComparer.OrdinalIgnoreCase);
        using var lifecycle = new TranscriptionModelLifecycleService(operations, () => selected);

        await lifecycle.PrepareAsync("whisper-tiny-en");

        Assert.Equal(["whisper-tiny-en"], operations.Prepared);
        Assert.Equal(["parakeet-v3"], selected);
        Assert.Equal(TranscriptionModelStatus.Ready, lifecycle.Snapshot("whisper-tiny-en").Status);
        Assert.Equal(TranscriptionModelStatus.Selected, lifecycle.Snapshot("parakeet-v3").Status);
    }

    [Fact]
    public async Task LifecycleCancellationRecoversAndCanRetry()
    {
        var operations = new FakeModelOperations
        {
            Prepare = async (_, token) => await Task.Delay(Timeout.InfiniteTimeSpan, token)
        };
        using var lifecycle = new TranscriptionModelLifecycleService(operations);
        var prepare = lifecycle.PrepareAsync("whisper-small-en");
        await operations.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        lifecycle.Cancel("whisper-small-en");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => prepare);

        Assert.Equal(TranscriptionModelStatus.Missing, lifecycle.Snapshot("whisper-small-en").Status);
        operations.Prepare = (_, _) => Task.CompletedTask;
        await lifecycle.RetryAsync("whisper-small-en");
        Assert.Equal(TranscriptionModelStatus.Ready, lifecycle.Snapshot("whisper-small-en").Status);
    }

    [Fact]
    public async Task LifecycleRejectsConcurrentWorkForOneModelButAllowsRecoveryAfterFailure()
    {
        var block = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var operations = new FakeModelOperations
        {
            Prepare = async (_, _) => await block.Task
        };
        using var lifecycle = new TranscriptionModelLifecycleService(operations);
        var first = lifecycle.PrepareAsync("sensevoice-small-int8");
        await operations.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAsync<InvalidOperationException>(() => lifecycle.VerifyAsync("sensevoice-small-int8"));
        block.SetException(new IOException("network interrupted"));
        await Assert.ThrowsAsync<IOException>(() => first);
        Assert.Equal(TranscriptionModelStatus.Failed, lifecycle.Snapshot("sensevoice-small-int8").Status);

        operations.Prepare = (_, _) => Task.CompletedTask;
        await lifecycle.RetryAsync("sensevoice-small-int8");
        Assert.Equal(TranscriptionModelStatus.Ready, lifecycle.Snapshot("sensevoice-small-int8").Status);
    }

    [Fact]
    public async Task DeleteFailureHasDistinctStatusAndRetryCanRecover()
    {
        var operations = new FakeModelOperations { Verified = true, DeleteFailure = new IOException("file locked") };
        using var lifecycle = new TranscriptionModelLifecycleService(operations);

        await Assert.ThrowsAsync<IOException>(() => lifecycle.DeleteAsync("qwen3-asr-0.6b-int8"));
        Assert.Equal(TranscriptionModelStatus.DeletionFailed, lifecycle.Snapshot("qwen3-asr-0.6b-int8").Status);
        operations.DeleteFailure = null;
        await lifecycle.RetryAsync("qwen3-asr-0.6b-int8");
        Assert.Equal(TranscriptionModelStatus.Missing, lifecycle.Snapshot("qwen3-asr-0.6b-int8").Status);
    }

    private sealed class FakeSessionFactory : ITranscriptionModelSessionFactory
    {
        private readonly TaskCompletionSource<TranscriptionResult>? _inference;
        public FakeSessionFactory(TaskCompletionSource<TranscriptionResult>? inference = null) => _inference = inference;
        public List<FakeSession> Sessions { get; } = [];
        public TaskCompletionSource Created { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ITranscriptionModelSession Create(TranscriptionModelDefinition model)
        {
            var session = new FakeSession(model.Id, Sessions.Count == 0 ? _inference : null);
            Sessions.Add(session);
            Created.TrySetResult();
            return session;
        }
    }

    private sealed class FakeSession(string modelId, TaskCompletionSource<TranscriptionResult>? inference) : ITranscriptionModelSession
    {
        public bool Disposed { get; private set; }
        public Task<ModelOperationResult> InitializeAsync() => Task.FromResult(new ModelOperationResult("ready", "fake"));
        public Task<TranscriptionResult> TranscribeAsync(byte[] audioBytes) =>
            inference?.Task ?? Task.FromResult(new TranscriptionResult(modelId));
        public Task<TranscriptionResult> TranscribeFileAsync(
            string title,
            string filePath,
            IProgress<TranscriptionProgress>? progress = null,
            CancellationToken cancellationToken = default) => TranscribeAsync([]);
        public void Dispose() => Disposed = true;
    }

    private sealed class FakeModelOperations : ITranscriptionModelOperations
    {
        public bool RuntimeAvailable { get; set; } = true;
        public bool Verified { get; set; }
        public List<string> Prepared { get; } = [];
        public Func<TranscriptionModelDefinition, CancellationToken, Task>? Prepare { get; set; }
        public Exception? DeleteFailure { get; set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task PrepareAsync(TranscriptionModelDefinition model, IProgress<ModelDownloadProgress>? progress, CancellationToken cancellationToken)
        {
            Prepared.Add(model.Id);
            Started.TrySetResult();
            if (Prepare is not null) await Prepare(model, cancellationToken);
            Verified = true;
        }

        public Task VerifyAsync(TranscriptionModelDefinition model, IProgress<ModelDownloadProgress>? progress, CancellationToken cancellationToken)
        {
            Verified = true;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(TranscriptionModelDefinition model, CancellationToken cancellationToken)
        {
            if (DeleteFailure is not null) return Task.FromException(DeleteFailure);
            Verified = false;
            return Task.CompletedTask;
        }

        public bool IsVerified(TranscriptionModelDefinition model) => Verified;
        public long DiskSizeBytes(TranscriptionModelDefinition model) => Verified ? 1024 : 0;
    }
}
