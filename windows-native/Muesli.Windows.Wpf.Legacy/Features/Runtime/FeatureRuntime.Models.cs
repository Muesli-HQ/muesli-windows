using System.Diagnostics;
using System.IO;
using System.Windows;
using Muesli.Windows.Services;

namespace Muesli.Windows;

public sealed partial class FeatureRuntime
{
private void RefreshRuntimeDiagnostics_Click(object sender, RoutedEventArgs e) =>
    _ = RefreshRuntimeDiagnosticsAsync();

private void SetModelsCategory_Click(object sender, RoutedEventArgs e)
{
    if (sender is not System.Windows.Controls.Button { Tag: string category } ||
        string.Equals(_modelsCategory, category, StringComparison.Ordinal))
    {
        return;
    }

    _modelsCategory = category;
    OnPropertyChanged(nameof(SelectedModelsCategory));
    OnPropertyChanged(nameof(ShowDictationModelsCategory));
    OnPropertyChanged(nameof(ShowStreamingModelsCategory));
    OnPropertyChanged(nameof(ShowCleanupModelsCategory));
}

private void ClearModelCache_Click(object sender, RoutedEventArgs e) => _ = ClearModelCacheAsync();

private async Task ClearModelCacheAsync()
{
    var result = System.Windows.MessageBox.Show(
        _shell.Window,
        "Delete downloaded local model files from the Muesli cache? They will download again when needed.",
        "Clear model cache",
        MessageBoxButton.YesNo,
        MessageBoxImage.Warning);
    if (result != MessageBoxResult.Yes)
    {
        return;
    }
    try
    {
        foreach (var model in TranscriptionModels)
        {
            if (_modelLifecycle.Snapshot(model.Id).DiskSizeBytes > 0)
            {
                await _modelLifecycle.DeleteAsync(model.Id);
            }
        }
        foreach (var model in StreamingModelCatalog.Models)
        {
            if (_streamingModelLifecycle.Snapshot(model.Id).DiskSizeBytes > 0)
            {
                await _streamingModelLifecycle.DeleteAsync(model.Id);
            }
        }
        NativeDiarizationClient.ClearModelCache();
        NativeTextCleanupService.ClearModelCache();
        DictationStatus = "Model cache cleared";
        await RefreshRuntimeDiagnosticsAsync();
    }
    catch (Exception exception)
    {
        DictationStatus = $"Could not clear model cache: {ConciseUiError(exception)}";
        _logService.Error("Could not clear model cache.", exception);
    }
}
private void OnTranscriptionModelChanged(object? sender, string modelId)
{
    if (!Dispatcher.CheckAccess())
    {
        Dispatcher.BeginInvoke(() => OnTranscriptionModelChanged(sender, modelId));
        return;
    }

    OnPropertyChanged(nameof(SelectedModelCacheStatus));
    OnPropertyChanged(nameof(FinalMeetingModelStatus));
    RefreshModelItems();
}

private void RefreshModelItems()
{
    foreach (var item in TranscriptionModelItems)
    {
        item.Apply(_modelLifecycle.Snapshot(item.Id));
    }
    OnPropertyChanged(nameof(SelectedModelCacheStatus));
    OnPropertyChanged(nameof(FinalMeetingModelStatus));
}

private void OnStreamingModelChanged(object? sender, string modelId)
{
    if (!Dispatcher.CheckAccess())
    {
        Dispatcher.BeginInvoke(() => OnStreamingModelChanged(sender, modelId));
        return;
    }
    RefreshStreamingModelItems();
}

private void RefreshStreamingModelItems()
{
    foreach (var item in StreamingModelItems) item.Apply(_streamingModelLifecycle.Snapshot(item.Id));
    OnPropertyChanged(nameof(LiveMeetingModelStatus));
}

private void PrepareStreamingModelItem_Click(object sender, RoutedEventArgs e) =>
    _ = PrepareStreamingModelItemAsync(sender);

private async Task PrepareStreamingModelItemAsync(object sender)
{
    if (sender is FrameworkElement { DataContext: StreamingModelItem item })
        await RunStreamingModelOperationAsync(item, progress => _streamingModelLifecycle.PrepareAsync(item.Id, progress));
}

private void CancelStreamingModelItem_Click(object sender, RoutedEventArgs e)
{
    if (sender is FrameworkElement { DataContext: StreamingModelItem item }) _streamingModelLifecycle.Cancel(item.Id);
}

private void RetryStreamingModelItem_Click(object sender, RoutedEventArgs e) =>
    _ = RetryStreamingModelItemAsync(sender);

private async Task RetryStreamingModelItemAsync(object sender)
{
    if (sender is FrameworkElement { DataContext: StreamingModelItem item })
        await RunStreamingModelOperationAsync(item, _ => _streamingModelLifecycle.RetryAsync(item.Id));
}

private void VerifyStreamingModelItem_Click(object sender, RoutedEventArgs e) =>
    _ = VerifyStreamingModelItemAsync(sender);

private async Task VerifyStreamingModelItemAsync(object sender)
{
    if (sender is FrameworkElement { DataContext: StreamingModelItem item })
        await RunStreamingModelOperationAsync(item, progress => _streamingModelLifecycle.VerifyAsync(item.Id, progress));
}

private void DeleteStreamingModelItem_Click(object sender, RoutedEventArgs e) =>
    _ = DeleteStreamingModelItemAsync(sender);

private async Task DeleteStreamingModelItemAsync(object sender)
{
    if (sender is not FrameworkElement { DataContext: StreamingModelItem item }) return;
    if (System.Windows.MessageBox.Show(_shell.Window, $"Delete {item.DisplayName}? The live role remains selected but will fail closed until prepared again.", "Delete live model", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
        await RunStreamingModelOperationAsync(item, _ => _streamingModelLifecycle.DeleteAsync(item.Id));
}

private void StreamingModelDiagnosticsItem_Click(object sender, RoutedEventArgs e)
{
    if (sender is FrameworkElement { DataContext: StreamingModelItem item })
        System.Windows.MessageBox.Show(_shell.Window, item.Diagnostics, $"{item.DisplayName} diagnostics", MessageBoxButton.OK, MessageBoxImage.Information);
}

private async Task RunStreamingModelOperationAsync(StreamingModelItem item, Func<IProgress<ModelDownloadProgress>, Task> operation)
{
    try
    {
        var progress = new Progress<ModelDownloadProgress>(value => { item.ProgressText = value.DisplayText; DictationStatus = value.DisplayText; });
        await operation(progress);
        DictationStatus = $"{item.DisplayName}: {_streamingModelLifecycle.Snapshot(item.Id).StatusText}. Live selection was unchanged.";
    }
    catch (OperationCanceledException) { DictationStatus = $"{item.DisplayName} operation cancelled"; }
    catch (Exception exception)
    {
        DictationStatus = $"{item.DisplayName}: {ConciseUiError(exception)}";
        _logService.Error($"Streaming model operation failed. model={item.Id}", exception);
    }
finally { RefreshStreamingModelItems(); }
}
private void DownloadSelectedModel_Click(object sender, RoutedEventArgs e) =>
    _ = DownloadSelectedModelAsync();

private void PrepareModelItem_Click(object sender, RoutedEventArgs e) =>
    _ = PrepareModelItemAsync(sender);

private async Task PrepareModelItemAsync(object sender)
{
    if (sender is FrameworkElement { DataContext: TranscriptionModelItem item })
    {
        await RunModelItemOperationAsync(item, progress => _modelLifecycle.PrepareAsync(item.Id, progress));
    }
}

private void CancelModelItem_Click(object sender, RoutedEventArgs e)
{
    if (sender is FrameworkElement { DataContext: TranscriptionModelItem item })
    {
        _modelLifecycle.Cancel(item.Id);
        DictationStatus = $"Cancelling {item.DisplayName}…";
    }
}

private void RetryModelItem_Click(object sender, RoutedEventArgs e) =>
    _ = RetryModelItemAsync(sender);

private async Task RetryModelItemAsync(object sender)
{
    if (sender is FrameworkElement { DataContext: TranscriptionModelItem item })
    {
        await RunModelItemOperationAsync(item, _ => _modelLifecycle.RetryAsync(item.Id));
    }
}

private void VerifyModelItem_Click(object sender, RoutedEventArgs e) =>
    _ = VerifyModelItemAsync(sender);

private async Task VerifyModelItemAsync(object sender)
{
    if (sender is FrameworkElement { DataContext: TranscriptionModelItem item })
    {
        await RunModelItemOperationAsync(item, progress => _modelLifecycle.VerifyAsync(item.Id, progress));
    }
}

private void DeleteModelItem_Click(object sender, RoutedEventArgs e) =>
    _ = DeleteModelItemAsync(sender);

private async Task DeleteModelItemAsync(object sender)
{
    if (sender is not FrameworkElement { DataContext: TranscriptionModelItem item })
    {
        return;
    }

    var confirmation = System.Windows.MessageBox.Show(
        _shell.Window,
        $"Delete the downloaded {item.DisplayName} files? Role selections will be preserved, but transcription using this model will fail closed until you prepare it again.",
        "Delete transcription model",
        MessageBoxButton.YesNo,
        MessageBoxImage.Warning);
    if (confirmation != MessageBoxResult.Yes)
    {
        return;
    }

    await RunModelItemOperationAsync(item, _ => _modelLifecycle.DeleteAsync(item.Id));
}

private void ModelDiagnosticsItem_Click(object sender, RoutedEventArgs e)
{
    if (sender is FrameworkElement { DataContext: TranscriptionModelItem item })
    {
        System.Windows.MessageBox.Show(_shell.Window, item.Diagnostics, $"{item.DisplayName} diagnostics", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}

private async Task RunModelItemOperationAsync(
    TranscriptionModelItem item,
    Func<IProgress<ModelDownloadProgress>, Task> operation)
{
    try
    {
        var progress = new Progress<ModelDownloadProgress>(value =>
        {
            item.ProgressText = value.DisplayText;
            DictationStatus = value.DisplayText;
        });
        await operation(progress);
        DictationStatus = $"{item.DisplayName}: {_modelLifecycle.Snapshot(item.Id).StatusText}";
    }
    catch (OperationCanceledException)
    {
        DictationStatus = $"{item.DisplayName} operation cancelled";
    }
    catch (Exception exception)
    {
        var error = ConciseUiError(exception);
        DictationStatus = $"{item.DisplayName}: {error}";
        _logService.Error($"Transcription model operation failed. model={item.Id}", exception);
    }
    finally
    {
        RefreshModelItems();
        await RefreshRuntimeDiagnosticsAsync();
    }
}
private void DownloadAllModels_Click(object sender, RoutedEventArgs e) => _ = DownloadAllModelsAsync();

private async Task DownloadAllModelsAsync()
{
    try
    {
        var progress = new Progress<ModelDownloadProgress>(value => DictationStatus = value.DisplayText);
        await _modelLifecycle.PrepareAllSequentialAsync(progress);

        DictationStatus = $"All {TranscriptionModels.Count} transcription models are ready";
        _toastNotificationService.Show("Models ready", $"{TranscriptionModels.Count} native transcription models verified", ToastState.Success, 4200);
        OnPropertyChanged(nameof(SelectedModelCacheStatus));
        await RefreshRuntimeDiagnosticsAsync();
    }
    catch (Exception exception)
    {
        var error = ConciseUiError(exception);
        DictationStatus = $"Model setup stopped: {error}";
        _logService.Error("Prepare all transcription models failed.", exception);
        _toastNotificationService.Show("Model setup stopped", error, ToastState.Error, 5200);
    }
}
private async Task DownloadSelectedModelAsync()
{
    var selected = SelectedTranscriptionModel;
    try
    {
        DictationStatus = $"Preparing {selected.DisplayName}";
        _toastNotificationService.Show("Preparing transcription", selected.DisplayName, ToastState.Transcribing, 0);
        var progress = new Progress<ModelDownloadProgress>(value => DictationStatus = value.DisplayText);
        await _modelLifecycle.PrepareAsync(selected.Id, progress);
        DictationStatus = $"{selected.DisplayName} downloaded and verified; role selections were unchanged";
        _toastNotificationService.Show("Transcription ready", $"{selected.DisplayName} verified", ToastState.Success, 3600);
        OnPropertyChanged(nameof(SelectedModelCacheStatus));
        await RefreshRuntimeDiagnosticsAsync();
    }
    catch (Exception exception)
    {
        var error = ConciseUiError(exception);
        DictationStatus = $"{selected.DisplayName} download failed: {error}";
        _logService.Error($"{selected.DisplayName} model download failed.", exception);
        _toastNotificationService.Show("Model download failed", error, ToastState.Error, 5200);
    }
}
private void RunBenchmark_Click(object sender, RoutedEventArgs e) => _ = RunBenchmarkAsync();

private async Task RunBenchmarkAsync()
{
    try
    {
        BenchmarkSummary = "Running benchmark...";
        DictationStatus = "Running transcription benchmark";
        _toastNotificationService.Show("Running benchmark", "Using captured local audio", ToastState.Transcribing, 0);
        var report = await _transcriptionBenchmarkService.RunAsync();
        BenchmarkSummary = string.IsNullOrWhiteSpace(report.LaunchRecommendation)
            ? report.Summary
            : $"{report.Summary}{Environment.NewLine}{report.LaunchRecommendation}";
        DictationStatus = "Benchmark complete";
        _toastNotificationService.Show("Benchmark complete", report.Summary, ToastState.Success, 5200);
    }
    catch (Exception exception)
    {
        var error = ConciseUiError(exception);
        BenchmarkSummary = $"Benchmark failed: {error}";
        DictationStatus = $"Benchmark failed: {error}";
        _logService.Error("Transcription benchmark failed.", exception);
        _toastNotificationService.Show("Benchmark failed", error, ToastState.Error, 5200);
    }
}
private void OpenModelCache_Click(object sender, RoutedEventArgs e)
{
    try
    {
        _runtimeDiagnosticsService.OpenModelCacheDirectory();
    }
    catch (Exception exception)
    {
        DictationStatus = $"Could not open model cache: {ConciseUiError(exception)}";
        _logService.Error("Could not open model cache.", exception);
    }
}
private void OpenCleanupModelCache_Click(object sender, RoutedEventArgs e)
{
    try
    {
        NativeTextCleanupService.OpenModelCacheDirectory();
    }
    catch (Exception exception)
    {
        DictationStatus = $"Could not open cleanup cache: {ConciseUiError(exception)}";
        _logService.Error("Could not open cleanup model cache.", exception);
    }
}
private void OpenTranscriptionModelCache_Click(object sender, RoutedEventArgs e)
{
    try
    {
        NativeTranscriptionClient.OpenModelCacheDirectory();
    }
    catch (Exception exception)
    {
        DictationStatus = $"Could not open transcription cache: {ConciseUiError(exception)}";
        _logService.Error("Could not open transcription model cache.", exception);
    }
}
private void OpenDiarizationModelCache_Click(object sender, RoutedEventArgs e)
{
    try
    {
        NativeDiarizationClient.OpenModelCacheDirectory();
    }
    catch (Exception exception)
    {
        DictationStatus = $"Could not open speaker label cache: {ConciseUiError(exception)}";
        _logService.Error("Could not open diarization model cache.", exception);
    }
}

private async Task RefreshRuntimeDiagnosticsAsync()
{
    try
    {
        RuntimeDiagnostics = "Checking runtime...";
        SetupReadiness = "Checking setup...";
        RuntimeSetupStatus = "";
        var diagnostics = await _runtimeDiagnosticsService.InspectAsync(
            EnableLocalCleanup,
            SelectedTranscriptionModel.Id,
            SelectedFinalMeetingModel.Id,
            SelectedLiveMeetingModel.Id);
        RuntimeDiagnostics = diagnostics.Summary;
        ModelCacheDirectory = diagnostics.ModelCacheDirectory;
        ModelCacheSize = diagnostics.ModelCacheSize;
        ApplyRuntimeDiagnostics(diagnostics);
        _logService.Info($"Runtime diagnostics refreshed. {SetupReadiness.Replace(Environment.NewLine, " | ")}");
        _logService.Info($"Runtime diagnostics details. {diagnostics.Summary.Replace(Environment.NewLine, " | ")}");
    }
    catch (Exception exception)
    {
        var failureStatus = RuntimeStatusMapper.Map(null, exception);
        RuntimeDiagnostics = "Setup check failed. Open logs for details.";
        SetupReadiness = "Setup check failed. Open logs for details.";
        RuntimeSetupStatus = failureStatus.Readiness;
        NativeRuntimeStatus = failureStatus.RuntimeStatus;
        SpeakerDiarizationStatusLabel = "Unknown (diagnostics failed)";
        DictationModelRuntimeStatus = SelectedModelCacheStatus;
        QwenCleanupRuntimeStatus = NativeTextCleanupService.Status(EnableLocalCleanup);
        GpuRuntimeStatus = failureStatus.ProviderStatus;
        DiarizationDependencyStatus = "Unknown (diagnostics failed)";
        DiarizationTokenStatus = "Not required";
        _logService.Error("Runtime diagnostics failed.", exception);
    }
}

private async Task SwitchDictationModelAsync(string modelId)
{
    try
    {
        await _dictationCoordinator.SwitchModelAsync(modelId);
        _logService.Info($"Dictation model role changed. model={modelId}; activation=selection-only; downloadStarted=false");
    }
    catch (Exception exception)
    {
        _logService.Error($"Could not switch the dictation model role to {modelId}.", exception);
        DictationStatus = $"Could not switch dictation model: {ConciseUiError(exception)}";
    }
}

private async Task SwitchFinalMeetingModelAsync(string modelId)
{
    try
    {
        await _meetingTranscriptionClient.SwitchModelAsync(modelId);
        _logService.Info($"Final meeting model role changed. model={modelId}; activation=selection-only; downloadStarted=false");
    }
    catch (Exception exception)
    {
        _logService.Error($"Could not switch the final meeting model role to {modelId}.", exception);
        DictationStatus = $"Could not switch final meeting model: {ConciseUiError(exception)}";
    }
}

private async Task ReleaseTranscriptionModelAsync(string modelId, CancellationToken cancellationToken)
{
    await _dictationCoordinator.ReleaseModelAsync(modelId, cancellationToken);
    await _meetingTranscriptionClient.ReleaseModelAsync(modelId, cancellationToken);
}

private void SetLazyTranscriptionReadiness()
{
    if (!_dictationCoordinator.IsModelReady)
    {
        DictationStatus = $"{_dictationCoordinator.ModelDisplayName} needs preparation in Models";
        _logService.Info($"Dictation model is not ready. model={_dictationCoordinator.ModelId}; automaticDownload=false");
        return;
    }

    DictationStatus = "Ready";
    _logService.Info(
        $"Transcription model verified; native session warmup deferred until capture starts. engine={_dictationCoordinator.EngineId}; model={_dictationCoordinator.ModelId}");
}

private void ApplyRuntimeDiagnostics(RuntimeDiagnostics diagnostics)
{
    var status = RuntimeStatusMapper.Map(diagnostics);
    NativeRuntimeStatus = status.RuntimeStatus;
    SpeakerDiarizationStatusLabel = diagnostics.DiarizationStatus;
    DictationModelRuntimeStatus = SelectedModelCacheStatus;
    OnPropertyChanged(nameof(DictationModelStatusLabel));
    OnPropertyChanged(nameof(SelectedModelCacheStatus));
    QwenCleanupRuntimeStatus = diagnostics.QwenCleanupStatus;
    OnPropertyChanged(nameof(QwenCleanupStatusLabel));
    GpuRuntimeStatus = status.ProviderStatus;
    DiarizationDependencyStatus = SpeakerDiarizationStatusLabel;
    DiarizationTokenStatus = "Not required";
    RuntimeSetupStatus = status.Readiness;
    SetupReadiness = BuildSetupReadiness(diagnostics);
}

private string BuildSetupReadiness(RuntimeDiagnostics diagnostics)
{
    var lines = new List<string>();
    lines.Add(diagnostics.RuntimeReady
        ? "Native transcription runtime is ready."
        : "Native transcription runtime needs attention.");
    lines.Add(diagnostics.ModelReady
        ? $"{ActiveModelLabel} is cached for offline use."
        : $"{ActiveModelLabel} is not ready. Prepare it explicitly from Models.");
    lines.Add($"Execution provider: {diagnostics.Acceleration} (selected automatically). ");
    lines.Add("Native speaker diarization uses ONNX models and downloads them on first meeting transcription if missing.");
    lines.Add(EnableLocalCleanup
        ? $"Local Qwen cleanup: {NativeTextCleanupService.Status(true)}."
        : "Local Qwen cleanup is disabled. Turn it on in Settings after placing a GGUF model in the native-cleanup cache.");
    return string.Join(Environment.NewLine, lines);
}

public string ActiveModelLabel => SelectedTranscriptionModel.DisplayName;
public string SelectedModelDescription => SelectedTranscriptionModel.Summary;
public string SelectedModelLanguages => SelectedTranscriptionModel.Languages;
public string SelectedModelDownloadSize => SelectedTranscriptionModel.SizeLabel;
public string SelectedModelCacheStatus => _isVisualPreview ? "Preview-only: no model cache inspected." : _modelLifecycle.Snapshot(SelectedTranscriptionModel.Id).StatusText;

public TranscriptionModelDefinition SelectedTranscriptionModel
{
    get => _selectedTranscriptionModel;
    set
    {
        var next = TranscriptionModelCatalog.Get(value?.Id);
        if (!SetField(ref _selectedTranscriptionModel, next)) return;
        _ = SwitchDictationModelAsync(next.Id);
        OnPropertyChanged(nameof(ActiveModelLabel));
        OnPropertyChanged(nameof(SelectedModelDescription));
        OnPropertyChanged(nameof(SelectedModelLanguages));
        OnPropertyChanged(nameof(SelectedModelDownloadSize));
        OnPropertyChanged(nameof(SelectedModelCacheStatus));
        DictationModelRuntimeStatus = _modelLifecycle.Snapshot(next.Id).StatusText;
        DictationStatus = TranscriptionModelReadiness.IsVerified(next)
            ? $"{next.DisplayName} selected"
            : $"{next.DisplayName} selected — prepare the model before transcription";
        SaveSettings();
        _ = RefreshRuntimeDiagnosticsAsync();
    }
}

public TranscriptionModelDefinition SelectedFinalMeetingModel
{
    get => _selectedFinalMeetingModel;
    set
    {
        var next = TranscriptionModelCatalog.Get(value?.Id);
        if (!SetField(ref _selectedFinalMeetingModel, next)) return;
        _ = SwitchFinalMeetingModelAsync(next.Id);
        OnPropertyChanged(nameof(FinalMeetingModelStatus));
        OnPropertyChanged(nameof(FinalTranscriptOwnerLabel));
        OnPropertyChanged(nameof(GapRecoveryOwnerLabel));
        SaveSettings();
        RefreshModelItems();
    }
}

public string FinalMeetingModelStatus => _isVisualPreview ? "Preview-only: no model cache inspected." : _modelLifecycle.Snapshot(SelectedFinalMeetingModel.Id).StatusText;

public LiveModelChoice SelectedLiveMeetingModel
{
    get => _selectedLiveMeetingModel;
    set
    {
        var next = LiveMeetingModels.FirstOrDefault(choice => choice.Id == value?.Id) ?? LiveModelChoice.Off;
        if (!SetField(ref _selectedLiveMeetingModel, next)) return;
        OnPropertyChanged(nameof(LiveMeetingModelStatus));
        OnPropertyChanged(nameof(LivePreviewOwnerLabel));
        OnPropertyChanged(nameof(FinalTranscriptOwnerLabel));
        OnPropertyChanged(nameof(GapRecoveryOwnerLabel));
        SaveSettings();
        RefreshStreamingModelItems();
    }
}

public string SelectedLiveTranscriptOwnership
{
    get => _selectedLiveTranscriptOwnership;
    set
    {
        var next = LiveTranscriptOwnershipModes.Contains(value) ? value : LiveTranscriptOwnershipDescriptor.PreviewOnlyDisplayName;
        if (!SetField(ref _selectedLiveTranscriptOwnership, next)) return;
        OnPropertyChanged(nameof(LivePreviewOwnerLabel));
        OnPropertyChanged(nameof(FinalTranscriptOwnerLabel));
        OnPropertyChanged(nameof(GapRecoveryOwnerLabel));
        SaveSettings();
    }
}

public bool ShowLiveWaveformOnHover
{
    get => _showLiveWaveformOnHover;
    set { if (SetField(ref _showLiveWaveformOnHover, value)) SaveSettings(); }
}

private LiveTranscriptOwnershipMode SelectedOwnershipMode =>
    LiveTranscriptOwnershipDescriptor.ModeFromDisplayName(SelectedLiveTranscriptOwnership);

private LiveTranscriptOwnershipDescriptor OwnershipDescriptor => LiveTranscriptOwnershipDescriptor.Create(
    SelectedLiveMeetingModel.Id,
    SelectedLiveMeetingModel.Label,
    SelectedOwnershipMode,
    SelectedFinalMeetingModel.DisplayName);

public string LiveMeetingModelStatus => SelectedLiveMeetingModel.Id is null
    ? "Off by default · select a verified live model explicitly"
    : _streamingModelLifecycle.Snapshot(SelectedLiveMeetingModel.Id).StatusText;
public string LivePreviewOwnerLabel => OwnershipDescriptor.LivePreviewOwner;
public string FinalTranscriptOwnerLabel => OwnershipDescriptor.FinalTranscriptOwner;
public string GapRecoveryOwnerLabel => OwnershipDescriptor.GapRecoveryOwner;
}
