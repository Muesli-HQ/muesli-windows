using System.IO;

namespace Muesli.Windows.Services;

internal interface ITranscriptionModelSession : IDisposable
{
    Task<ModelOperationResult> InitializeAsync();
    Task<TranscriptionResult> TranscribeAsync(byte[] audioBytes);

    /// <summary>
    /// Transcribes a media file. <paramref name="progress"/> and <paramref name="cancellationToken"/>
    /// are honoured at every point the work can actually be subdivided; see the implementations for
    /// which stages are interruptible.
    /// </summary>
    Task<TranscriptionResult> TranscribeFileAsync(
        string title,
        string filePath,
        IProgress<TranscriptionProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

internal interface ITranscriptionModelSessionFactory
{
    ITranscriptionModelSession Create(TranscriptionModelDefinition model);
}

internal sealed class NativeTranscriptionModelSessionFactory : ITranscriptionModelSessionFactory
{
    private readonly object _gate = new();
    private readonly Dictionary<string, SharedSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<TranscriptionModelDefinition, ITranscriptionModelSession> _createSession;

    public static NativeTranscriptionModelSessionFactory Shared { get; } = new();

    public NativeTranscriptionModelSessionFactory()
        : this(model => model.Kind == NativeAsrModelKind.Parakeet
            ? new NativeParakeetClient()
            : new NativeOfflineAsrClient(model))
    {
    }

    internal NativeTranscriptionModelSessionFactory(
        Func<TranscriptionModelDefinition, ITranscriptionModelSession> createSession)
    {
        _createSession = createSession;
    }

    public ITranscriptionModelSession Create(TranscriptionModelDefinition model)
    {
        var key = SessionKey(model);
        lock (_gate)
        {
            if (!_sessions.TryGetValue(key, out var shared))
            {
                shared = new SharedSession(_createSession(model));
                _sessions.Add(key, shared);
            }

            shared.ReferenceCount++;
            return new SharedSessionLease(this, key, shared);
        }
    }

    internal int ActiveSessionCount
    {
        get
        {
            lock (_gate)
            {
                return _sessions.Count;
            }
        }
    }

    private void Release(string key, SharedSession shared)
    {
        ITranscriptionModelSession? sessionToDispose = null;
        lock (_gate)
        {
            if (!_sessions.TryGetValue(key, out var current) || !ReferenceEquals(current, shared))
            {
                return;
            }

            current.ReferenceCount--;
            if (current.ReferenceCount == 0)
            {
                _sessions.Remove(key);
                sessionToDispose = current.Session;
            }
        }

        // Native recognizer disposal can be expensive, so do it after releasing the pool lock.
        sessionToDispose?.Dispose();
    }

    private static string SessionKey(TranscriptionModelDefinition model) => string.Join('|',
        model.Id,
        model.ModelPath,
        Environment.GetEnvironmentVariable("MUESLI_ASR_PROVIDER")?.Trim() ?? "",
        Environment.GetEnvironmentVariable("MUESLI_PARAKEET_PROVIDER")?.Trim() ?? "",
        Environment.GetEnvironmentVariable("MUESLI_PARAKEET_THREADS")?.Trim() ?? "",
        Environment.ProcessorCount.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private sealed class SharedSession(ITranscriptionModelSession session)
    {
        public ITranscriptionModelSession Session { get; } = session;
        public int ReferenceCount { get; set; }
    }

    private sealed class SharedSessionLease(
        NativeTranscriptionModelSessionFactory owner,
        string key,
        SharedSession shared) : ITranscriptionModelSession
    {
        private int _disposed;

        public Task<ModelOperationResult> InitializeAsync() => Session.InitializeAsync();

        public Task<TranscriptionResult> TranscribeAsync(byte[] audioBytes) =>
            Session.TranscribeAsync(audioBytes);

        public Task<TranscriptionResult> TranscribeFileAsync(
            string title,
            string filePath,
            IProgress<TranscriptionProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            Session.TranscribeFileAsync(title, filePath, progress, cancellationToken);

        private ITranscriptionModelSession Session
        {
            get
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                return shared.Session;
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.Release(key, shared);
            }
        }
    }
}

/// <summary>
/// Owns exactly one role-specific recognizer. Model selection is instance scoped:
/// callers must explicitly create one client for dictation or final-meeting work.
/// </summary>
public sealed class NativeTranscriptionClient : IDisposable
{
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly ITranscriptionModelSessionFactory _sessionFactory;
    private readonly Func<TranscriptionModelDefinition, bool> _isReady;
    private TranscriptionModelDefinition _model;
    private ITranscriptionModelSession? _session;
    private bool _disposed;
    private readonly Action _languageChanged;

    public NativeTranscriptionClient(string? modelId = null)
        : this(TranscriptionModelCatalog.GetRequired(modelId ?? TranscriptionModelCatalog.DefaultModelId),
            NativeTranscriptionModelSessionFactory.Shared)
    {
    }

    internal NativeTranscriptionClient(
        TranscriptionModelDefinition model,
        ITranscriptionModelSessionFactory sessionFactory,
        Func<TranscriptionModelDefinition, bool>? isReady = null)
    {
        _model = model;
        _sessionFactory = sessionFactory;
        _isReady = isReady ?? TranscriptionModelReadiness.IsVerified;
        // A language change must not leave a recognizer running with the old language; the next
        // transcription rebuilds the session from the new selection.
        _languageChanged = () => _reconfigurePending = true;
        TranscriptionLanguageSelection.Changed += _languageChanged;
    }

    private volatile bool _reconfigurePending;

    public string EngineId => $"native-sherpa-onnx/{_model.Kind.ToString().ToLowerInvariant()}";
    public string ModelId => _model.Id;
    public string ModelDisplayName => _model.DisplayName;
    public TranscriptionModelDefinition SelectedModel => _model;
    public bool IsSelectedModelReady => TranscriptionModelReadiness.IsVerified(_model);

    public async Task SwitchModelAsync(string modelId, CancellationToken cancellationToken = default)
    {
        var next = TranscriptionModelCatalog.GetRequired(modelId);
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            if (_model.Id.Equals(next.Id, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            DisposeSession();
            _model = next;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task ReleaseModelAsync(string modelId, CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            if (_model.Id.Equals(modelId, StringComparison.OrdinalIgnoreCase))
            {
                DisposeSession();
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public Task<ModelOperationResult> InitializeAsync() => UseSessionAsync(session => session.InitializeAsync());

    public Task<TranscriptionResult> TranscribeAsync(byte[] audioBytes) =>
        UseSessionAsync(session => session.TranscribeAsync(audioBytes));

    public Task<TranscriptionResult> TranscribeFileAsync(
        string title,
        string filePath,
        IProgress<TranscriptionProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        UseSessionAsync(
            session => session.TranscribeFileAsync(title, filePath, progress, cancellationToken),
            cancellationToken);

    private async Task<T> UseSessionAsync<T>(
        Func<ITranscriptionModelSession, Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        // Waiting behind another transcription is itself part of the wait the user sees, so it has
        // to be cancellable too.
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            if (!_isReady(_model))
            {
                throw new InvalidOperationException(
                    $"{_model.DisplayName} is not downloaded and verified. Prepare it from Models first.");
            }

            // A language change invalidates the pooled recognizer so it is rebuilt with the new
            // selection instead of transcribing with the previous language.
            if (_reconfigurePending)
            {
                DisposeSession();
                _reconfigurePending = false;
            }

            _session ??= _sessionFactory.Create(_model);
            return await operation(_session);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private void DisposeSession()
    {
        _session?.Dispose();
        _session = null;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public static long ModelCacheSizeBytes() => TranscriptionModelCatalog.CacheSizeBytes();

    public static void OpenModelCacheDirectory() => TranscriptionModelCatalog.OpenCacheDirectory();

    public static void ClearAllModelCaches()
    {
        NativeParakeetClient.ClearModelCache();
        if (Directory.Exists(TranscriptionModelCatalog.ModelCacheDirectory))
        {
            Directory.Delete(TranscriptionModelCatalog.ModelCacheDirectory, recursive: true);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _operationGate.Wait();
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            TranscriptionLanguageSelection.Changed -= _languageChanged;
            DisposeSession();
        }
        finally
        {
            _operationGate.Release();
            _operationGate.Dispose();
        }
    }
}
