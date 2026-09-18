using System.Collections.Concurrent;
using System.IO;

namespace Muesli.Windows.Services;

public enum TranscriptionModelStatus
{
    Missing,
    Downloading,
    Verifying,
    Ready,
    Selected,
    Failed,
    RuntimeUnavailable,
    DeletionFailed
}

public sealed record TranscriptionModelSnapshot(
    TranscriptionModelDefinition Model,
    TranscriptionModelStatus Status,
    string StatusText,
    long DiskSizeBytes,
    string DiskSize,
    string Diagnostics,
    bool IsBusy,
    bool CanPrepare,
    bool CanCancel,
    bool CanRetry,
    bool CanVerify,
    bool CanDelete);

internal static class TranscriptionModelReadiness
{
    public static bool IsVerified(TranscriptionModelDefinition model) =>
        model.Kind == NativeAsrModelKind.Parakeet
            ? NativeParakeetClient.IsModelVerified
            : model.IsCached && NativeOfflineAsrClient.HasValidVerificationStamp(model);
}

internal interface ITranscriptionModelOperations
{
    bool RuntimeAvailable { get; }
    Task PrepareAsync(TranscriptionModelDefinition model, IProgress<ModelDownloadProgress>? progress, CancellationToken cancellationToken);
    Task VerifyAsync(TranscriptionModelDefinition model, IProgress<ModelDownloadProgress>? progress, CancellationToken cancellationToken);
    Task DeleteAsync(TranscriptionModelDefinition model, CancellationToken cancellationToken);
    bool IsVerified(TranscriptionModelDefinition model);
    long DiskSizeBytes(TranscriptionModelDefinition model);
}

internal sealed class NativeTranscriptionModelOperations : ITranscriptionModelOperations
{
    private readonly Func<string, CancellationToken, Task> _releaseModel;

    public NativeTranscriptionModelOperations(Func<string, CancellationToken, Task>? releaseModel = null)
    {
        _releaseModel = releaseModel ?? ((_, _) => Task.CompletedTask);
    }

    public bool RuntimeAvailable => NativeParakeetClient.IsRuntimeAvailable;
    public bool IsVerified(TranscriptionModelDefinition model) => TranscriptionModelReadiness.IsVerified(model);

    public Task PrepareAsync(
        TranscriptionModelDefinition model,
        IProgress<ModelDownloadProgress>? progress,
        CancellationToken cancellationToken) => Task.Run(async () =>
    {
        if (model.Kind == NativeAsrModelKind.Parakeet)
        {
            using var client = new NativeParakeetClient();
            await client.PrepareModelAsync(progress, cancellationToken);
        }
        else
        {
            using var client = new NativeOfflineAsrClient(model);
            await client.PrepareModelAsync(progress, cancellationToken);
        }
    }, cancellationToken);

    public Task VerifyAsync(
        TranscriptionModelDefinition model,
        IProgress<ModelDownloadProgress>? progress,
        CancellationToken cancellationToken) => Task.Run(async () =>
    {
        if (model.Kind == NativeAsrModelKind.Parakeet)
        {
            using var client = new NativeParakeetClient();
            await client.VerifyModelAsync(progress, cancellationToken);
        }
        else
        {
            using var client = new NativeOfflineAsrClient(model);
            await client.VerifyModelAsync(progress, cancellationToken);
        }
    }, cancellationToken);

    public async Task DeleteAsync(TranscriptionModelDefinition model, CancellationToken cancellationToken)
    {
        await _releaseModel(model.Id, cancellationToken);
        await Task.Run(async () =>
        {
            if (model.Kind == NativeAsrModelKind.Parakeet)
            {
                using var client = new NativeParakeetClient();
                await client.DeleteModelAsync(cancellationToken);
            }
            else
            {
                using var client = new NativeOfflineAsrClient(model);
                await client.DeleteModelAsync(cancellationToken);
            }
        }, cancellationToken);
    }

    public long DiskSizeBytes(TranscriptionModelDefinition model)
    {
        if (!Directory.Exists(model.ModelPath))
        {
            return 0;
        }

        return Directory.EnumerateFiles(model.ModelPath, "*", SearchOption.AllDirectories)
            .Select(path => new FileInfo(path))
            .Where(file => file.Exists)
            .Sum(file => file.Length);
    }
}

public sealed class TranscriptionModelLifecycleService : IDisposable
{
    private sealed class OperationState
    {
        public readonly object Gate = new();
        public CancellationTokenSource? Cancellation;
        public TranscriptionModelStatus? Status;
        public string? Detail;
        public Func<CancellationToken, Task>? Retry;
    }

    private readonly ITranscriptionModelOperations _operations;
    private readonly ConcurrentDictionary<string, OperationState> _states = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<IReadOnlySet<string>> _selectedModelIds;
    private bool _disposed;

    public TranscriptionModelLifecycleService(
        Func<IReadOnlySet<string>>? selectedModelIds = null,
        Func<string, CancellationToken, Task>? releaseModel = null)
        : this(new NativeTranscriptionModelOperations(releaseModel), selectedModelIds)
    {
    }

    internal TranscriptionModelLifecycleService(
        ITranscriptionModelOperations operations,
        Func<IReadOnlySet<string>>? selectedModelIds = null)
    {
        _operations = operations;
        _selectedModelIds = selectedModelIds ?? (() => new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    }

    public event EventHandler<string>? ModelChanged;

    public TranscriptionModelSnapshot Snapshot(string modelId)
    {
        var model = TranscriptionModelCatalog.GetRequired(modelId);
        var operation = _states.GetOrAdd(model.Id, _ => new OperationState());
        TranscriptionModelStatus? activeStatus;
        string? detail;
        bool canRetry;
        lock (operation.Gate)
        {
            activeStatus = operation.Status;
            detail = operation.Detail;
            canRetry = operation.Retry is not null && activeStatus is TranscriptionModelStatus.Failed or TranscriptionModelStatus.DeletionFailed;
        }

        var verified = _operations.IsVerified(model);
        var selected = _selectedModelIds().Contains(model.Id);
        var status = activeStatus ?? (!_operations.RuntimeAvailable
            ? TranscriptionModelStatus.RuntimeUnavailable
            : verified
                ? selected ? TranscriptionModelStatus.Selected : TranscriptionModelStatus.Ready
                : TranscriptionModelStatus.Missing);
        var busy = status is TranscriptionModelStatus.Downloading or TranscriptionModelStatus.Verifying;
        var bytes = _operations.DiskSizeBytes(model);
        var statusText = detail ?? StatusLabel(status, selected);
        return new TranscriptionModelSnapshot(
            model,
            status,
            statusText,
            bytes,
            FormatBytes(bytes),
            BuildDiagnostics(model, status, statusText, bytes, selected),
            busy,
            !busy && status != TranscriptionModelStatus.RuntimeUnavailable,
            busy,
            canRetry,
            !busy && model.IsCached,
            !busy && bytes > 0);
    }

    public Task PrepareAsync(
        string modelId,
        IProgress<ModelDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var model = TranscriptionModelCatalog.GetRequired(modelId);
        return StartOperationAsync(
            model,
            TranscriptionModelStatus.Downloading,
            "Preparing download…",
            token => _operations.PrepareAsync(
                model,
                new InlineProgress<ModelDownloadProgress>(value =>
                {
                    UpdateProgress(model.Id, value.DisplayText,
                        value.DisplayText.StartsWith("Verifying", StringComparison.OrdinalIgnoreCase) ||
                        value.DisplayText.Contains("verified", StringComparison.OrdinalIgnoreCase)
                            ? TranscriptionModelStatus.Verifying
                            : TranscriptionModelStatus.Downloading);
                    progress?.Report(value);
                }),
                token),
            cancellationToken,
            deletion: false);
    }

    public Task VerifyAsync(
        string modelId,
        IProgress<ModelDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var model = TranscriptionModelCatalog.GetRequired(modelId);
        return StartOperationAsync(
            model,
            TranscriptionModelStatus.Verifying,
            "Verifying pinned checksums…",
            token => _operations.VerifyAsync(
                model,
                new InlineProgress<ModelDownloadProgress>(value =>
                {
                    UpdateProgress(model.Id, value.DisplayText, TranscriptionModelStatus.Verifying);
                    progress?.Report(value);
                }),
                token),
            cancellationToken,
            deletion: false);
    }

    public Task DeleteAsync(string modelId, CancellationToken cancellationToken = default)
    {
        var model = TranscriptionModelCatalog.GetRequired(modelId);
        return StartOperationAsync(
            model,
            TranscriptionModelStatus.Verifying,
            "Releasing recognizer and deleting files…",
            token => _operations.DeleteAsync(model, token),
            cancellationToken,
            deletion: true);
    }

    public async Task PrepareAllSequentialAsync(
        IProgress<ModelDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        foreach (var model in TranscriptionModelCatalog.Models)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await PrepareAsync(model.Id, progress, cancellationToken);
        }
    }

    public void Cancel(string modelId)
    {
        var model = TranscriptionModelCatalog.GetRequired(modelId);
        if (_states.TryGetValue(model.Id, out var state))
        {
            lock (state.Gate)
            {
                state.Cancellation?.Cancel();
            }
        }
    }

    public Task RetryAsync(string modelId, CancellationToken cancellationToken = default)
    {
        var model = TranscriptionModelCatalog.GetRequired(modelId);
        var state = _states.GetOrAdd(model.Id, _ => new OperationState());
        Func<CancellationToken, Task>? retry;
        lock (state.Gate)
        {
            retry = state.Retry;
        }

        return retry is null
            ? Task.FromException(new InvalidOperationException($"{model.DisplayName} has no failed operation to retry."))
            : retry(cancellationToken);
    }

    private async Task StartOperationAsync(
        TranscriptionModelDefinition model,
        TranscriptionModelStatus runningStatus,
        string detail,
        Func<CancellationToken, Task> operation,
        CancellationToken externalCancellation,
        bool deletion)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var state = _states.GetOrAdd(model.Id, _ => new OperationState());
        CancellationTokenSource linked;
        lock (state.Gate)
        {
            if (state.Cancellation is not null)
            {
                throw new InvalidOperationException($"An operation is already running for {model.DisplayName}.");
            }

            linked = CancellationTokenSource.CreateLinkedTokenSource(externalCancellation);
            state.Cancellation = linked;
            state.Status = runningStatus;
            state.Detail = detail;
            state.Retry = token => StartOperationAsync(model, runningStatus, detail, operation, token, deletion);
        }
        OnModelChanged(model.Id);

        try
        {
            await operation(linked.Token);
            lock (state.Gate)
            {
                state.Status = null;
                state.Detail = null;
                state.Retry = null;
            }
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            lock (state.Gate)
            {
                state.Status = null;
                state.Detail = "Cancelled; safe partial artifacts will be cleaned on retry.";
            }
            throw;
        }
        catch (Exception exception)
        {
            lock (state.Gate)
            {
                state.Status = deletion ? TranscriptionModelStatus.DeletionFailed : TranscriptionModelStatus.Failed;
                state.Detail = exception.Message;
            }
            throw;
        }
        finally
        {
            lock (state.Gate)
            {
                state.Cancellation?.Dispose();
                state.Cancellation = null;
            }
            OnModelChanged(model.Id);
        }
    }

    private void OnModelChanged(string modelId) => ModelChanged?.Invoke(this, modelId);

    private void UpdateProgress(string modelId, string detail, TranscriptionModelStatus status)
    {
        var state = _states.GetOrAdd(modelId, _ => new OperationState());
        lock (state.Gate)
        {
            if (state.Cancellation is null)
            {
                return;
            }
            state.Status = status;
            state.Detail = detail;
        }
        OnModelChanged(modelId);
    }

    private static string StatusLabel(TranscriptionModelStatus status, bool selected) => status switch
    {
        TranscriptionModelStatus.Missing => selected ? "Missing · selected for a role" : "Missing",
        TranscriptionModelStatus.Downloading => "Downloading",
        TranscriptionModelStatus.Verifying => "Verifying",
        TranscriptionModelStatus.Ready => "Ready · checksums verified",
        TranscriptionModelStatus.Selected => "Selected · ready and verified",
        TranscriptionModelStatus.Failed => "Failed",
        TranscriptionModelStatus.RuntimeUnavailable => selected ? "Runtime unavailable · selected for a role" : "Runtime unavailable",
        TranscriptionModelStatus.DeletionFailed => "Deletion failed",
        _ => status.ToString()
    };

    private static string BuildDiagnostics(
        TranscriptionModelDefinition model,
        TranscriptionModelStatus status,
        string statusText,
        long bytes,
        bool selected) => string.Join(
            Environment.NewLine,
            $"Model: {model.DisplayName}",
            $"Model ID: {model.Id}",
            $"Engine family: {model.Kind}",
            $"Status: {status} ({statusText})",
            $"Selected for a role: {selected}",
            $"Installed bytes: {bytes:N0}",
            $"Model directory: {model.ModelPath}",
            $"Archive SHA-256: {model.ArchiveSha256}",
            $"Pinned required files: {model.RequiredFileSha256.Count}");

    internal static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "0 B";
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:0.#} {units[unit]}";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var state in _states.Values)
        {
            lock (state.Gate)
            {
                state.Cancellation?.Cancel();
            }
        }
    }
}

internal sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
