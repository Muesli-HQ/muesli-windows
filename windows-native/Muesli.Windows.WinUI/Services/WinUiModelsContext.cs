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
