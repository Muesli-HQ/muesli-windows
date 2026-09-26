using Muesli.Windows.Services;

namespace Muesli.Windows.WinUI.Services;

public sealed class WinUiModelsContext : IDisposable
{
    private readonly WinUiSettingsContext _settingsStore;
    private readonly TranscriptionModelLifecycleService _lifecycle;

    public WinUiModelsContext(WinUiLibraryContext library, WinUiSettingsContext? settings = null)
    {
        _settingsStore = settings ?? new WinUiSettingsContext(library);
        _lifecycle = new TranscriptionModelLifecycleService(SelectedModelIds);
        _lifecycle.ModelChanged += (_, modelId) => ModelChanged?.Invoke(this, modelId);
    }

    public event EventHandler<string>? ModelChanged;

    public MuesliSettings Settings => _settingsStore.Load();

    public IReadOnlyList<TranscriptionModelSnapshot> Snapshots() =>
        TranscriptionModelCatalog.Models.Select(model => _lifecycle.Snapshot(model.Id)).ToList();

    public Task PrepareAsync(
        string modelId,
        IProgress<ModelDownloadProgress>? progress,
        CancellationToken cancellationToken) => _lifecycle.PrepareAsync(modelId, progress, cancellationToken);

    public Task VerifyAsync(
        string modelId,
        IProgress<ModelDownloadProgress>? progress,
        CancellationToken cancellationToken) => _lifecycle.VerifyAsync(modelId, progress, cancellationToken);

    public Task RetryAsync(string modelId, CancellationToken cancellationToken) =>
        _lifecycle.RetryAsync(modelId, cancellationToken);

    public Task DeleteAsync(string modelId, CancellationToken cancellationToken) =>
        _lifecycle.DeleteAsync(modelId, cancellationToken);

    public void Cancel(string modelId) => _lifecycle.Cancel(modelId);

    public void SetActive(string modelId, bool forMeetings)
    {
        var normalized = TranscriptionModelCatalog.GetRequired(modelId).Id;
        var settings = _settingsStore.Load();
        _settingsStore.Save(forMeetings
            ? settings with { FinalMeetingModelId = normalized }
            : settings with { DictationModelId = normalized });
        ModelChanged?.Invoke(this, normalized);
    }

    public bool IsSelected(string modelId) => SelectedModelIds().Contains(modelId);

    // ------------------------------------------------------------------ local cleanup models

    private CleanupModelLifecycleService CleanupLifecycle() =>
        new(_settingsStore.Load().CleanupModelId);

    public IReadOnlyList<CleanupModelSnapshot> CleanupSnapshots() => CleanupLifecycle().Snapshots();

    public Task PrepareCleanupAsync(
        string modelId,
        IProgress<ModelDownloadProgress>? progress,
        CancellationToken cancellationToken) =>
        CleanupLifecycle().PrepareAsync(modelId, progress, cancellationToken);

    public Task VerifyCleanupAsync(string modelId, CancellationToken cancellationToken) =>
        CleanupLifecycle().VerifyAsync(modelId, cancellationToken);

    public Task DeleteCleanupAsync(string modelId, CancellationToken cancellationToken) =>
        CleanupLifecycle().DeleteAsync(modelId, cancellationToken);

    /// <summary>
    /// Records the cleanup model the user chose. Selecting a model never downloads or activates it;
    /// the cleanup stage keeps using whatever is selected until the user changes it.
    /// </summary>
    public void SetCleanupModel(string modelId)
    {
        var normalized = CleanupModelCatalog.Normalize(modelId);
        var settings = _settingsStore.Load();
        _settingsStore.Save(settings with { CleanupModelId = normalized });
        NativeTextCleanupService.Configure(normalized);
        ModelChanged?.Invoke(this, normalized);
    }

    // ------------------------------------------------------------------ execution provider

    public ExecutionProviderStatus ProviderStatus() => ExecutionProviderService.Inspect();

    public void SetExecutionProvider(ExecutionProviderPreference preference)
    {
        var settings = _settingsStore.Load();
        _settingsStore.Save(settings with
        {
            ExecutionProvider = ExecutionProviderService.ToSettingValue(preference)
        });
        // Applied on the next launch: the CPU and CUDA native runtimes cannot be swapped inside a
        // process that has already built a recognizer.
        ModelChanged?.Invoke(this, "execution-provider");
    }

    public Task<ModelOperationResult> PrepareCudaPackAsync(
        IProgress<ModelDownloadProgress>? progress,
        CancellationToken cancellationToken) =>
        new CudaAccelerationPackInstaller().PrepareAsync(progress, cancellationToken);

    public Task<CudaPackVerification> VerifyCudaPackAsync(CancellationToken cancellationToken) =>
        new CudaAccelerationPackInstaller().VerifyAsync(cancellationToken);

    public Task<ModelOperationResult> DeleteCudaPackAsync(CancellationToken cancellationToken) =>
        new CudaAccelerationPackInstaller().DeleteAsync(cancellationToken);

    public void Dispose() => _lifecycle.Dispose();

    private IReadOnlySet<string> SelectedModelIds()
    {
        var settings = _settingsStore.Load();
        return new HashSet<string>(
            new[] { settings.DictationModelId, settings.FinalMeetingModelId }
                .Where(value => !string.IsNullOrWhiteSpace(value)),
            StringComparer.OrdinalIgnoreCase);
    }
}
