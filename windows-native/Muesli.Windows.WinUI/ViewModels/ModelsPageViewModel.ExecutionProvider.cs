using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Muesli.Windows.Core.Contracts;
using Muesli.Windows.Services;

namespace Muesli.Windows.WinUI.ViewModels;

/// <summary>One selectable execution provider.</summary>
public sealed record ProviderOption(
    string Value,
    string Label,
    string Description,
    bool IsEnabled);

/// <summary>
/// One cleanup model as the Cleanup category renders it. Every field is read from the real
/// installer/snapshot; nothing is inferred from a file name alone.
/// </summary>
public sealed partial class CleanupModelItem : ObservableObject
{
    private readonly CleanupModelSnapshot _snapshot;

    public CleanupModelItem(CleanupModelSnapshot snapshot)
    {
        _snapshot = snapshot;
        Id = snapshot.Model.Id;
        DisplayName = snapshot.Model.DisplayName;
        Summary = snapshot.Model.Summary;
        License = snapshot.Model.License;
        LanguageSupport = snapshot.Model.LanguageSupport;
        DownloadLabel = FormatBytes(snapshot.Model.SizeBytes);
    }

    public string Id { get; }
    public string DisplayName { get; }
    public string Summary { get; }
    public string License { get; }
    public string LanguageSupport { get; }
    public string DownloadLabel { get; }

    public bool IsInstalled => _snapshot.IsInstalled;
    public bool IsVerified => _snapshot.IsVerified;
    public bool IsSelected => _snapshot.IsSelected;
    public string StatusText => _snapshot.StatusText;
    public string InstalledLabel => string.IsNullOrWhiteSpace(_snapshot.DiskSize)
        ? ""
        : $"Installed · {_snapshot.DiskSize}";
    public bool HasInstalledSize => !string.IsNullOrWhiteSpace(InstalledLabel);
    public bool CanPrepare => !_snapshot.IsInstalled || !_snapshot.IsVerified;
    public bool CanCancel => IsBusy;
    public bool CanVerify => _snapshot.IsInstalled;
    public bool CanDelete => _snapshot.IsInstalled && !_snapshot.IsSelected;
    public bool CanSelect => _snapshot.IsVerified && !_snapshot.IsSelected;
    public bool IsActive => _snapshot.IsSelected;

    public bool HasActions => CanPrepare || CanVerify || CanDelete || CanSelect || CanCancel;
    public string AccessibleName => IsActive ? $"{DisplayName}. Active." : DisplayName;

    public string InstallButtonLabel => _snapshot.IsInstalled ? "Repair" : "Download";

    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial int? ProgressPercent { get; set; }
    public string ProgressLabel => ProgressPercent is int value ? $"{value}%" : StatusText;
    public double ProgressValue => ProgressPercent ?? 0;
    public bool IsProgressIndeterminate => IsBusy && ProgressPercent is null;

    public string StateNote => IsSelected
        ? "Selected for cleanup. These files stay on disk while the model is in use."
        : IsInstalled && !IsVerified
            ? "Installed but not verified. Verify to confirm it matches the published SHA-256 before selecting it."
            : "";

    public bool HasStateNote => !string.IsNullOrWhiteSpace(StateNote);

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1_073_741_824 => $"{bytes / 1_073_741_824.0:0.0} GB",
        >= 1_048_576 => $"{bytes / 1_048_576.0:0} MB",
        _ => $"{bytes} B"
    };
}

/// <summary>
/// Execution-provider control and cleanup-model lifecycle for the Models page. Kept in its own
/// partial file so the family-card logic in <see cref="ModelsPageViewModel"/> stays readable.
/// </summary>
public partial class ModelsPageViewModel
{
    private readonly Dictionary<string, CleanupModelItem> _cleanupById = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _cudaPackCancellation;

    [ObservableProperty]
    public partial IReadOnlyList<ProviderOption> ProviderOptions { get; private set; } = [];

    /// <summary>DirectML is deliberately not selectable; its status is reported in diagnostics.</summary>
    public string DirectMlStatusLine => ExecutionProviderService.DirectMlUnsupportedReason;

    private bool _providerInitialized;

    [ObservableProperty] public partial string SelectedProviderValue { get; set; } = "auto";
    [ObservableProperty] public partial bool IsProviderBusy { get; private set; }
    public bool CanChangeProvider => !IsProviderBusy;

    partial void OnIsProviderBusyChanged(bool value) => OnPropertyChanged(nameof(CanChangeProvider));
    [ObservableProperty] public partial bool IsRestartRequired { get; private set; }
    [ObservableProperty] public partial string RestartRequiredMessage { get; private set; } = "";
    [ObservableProperty] public partial bool IsCudaPackInstalled { get; private set; }
    [ObservableProperty] public partial string AccelerationStatusLine { get; private set; } = "";
    [ObservableProperty] public partial string AccelerationDetailText { get; private set; } = "";
    [ObservableProperty] public partial string CudaPackStatusLine { get; private set; } = "";
    [ObservableProperty] public partial bool IsAccelerationDetailsOpen { get; set; }
    [ObservableProperty] public partial IReadOnlyList<CleanupModelItem> CleanupItems { get; private set; } = [];
    public bool HasCleanupItems => CleanupItems.Count > 0;

    partial void OnCleanupItemsChanged(IReadOnlyList<CleanupModelItem> value) =>
        OnPropertyChanged(nameof(HasCleanupItems));

    public bool CanInstallCudaPack => true;
    public bool CanDeleteCudaPack => IsCudaPackInstalled;
    public string InstallCudaPackLabel => IsCudaPackInstalled ? "Reinstall" : "Install";

    partial void OnSelectedProviderValueChanged(string value)
    {
        if (_disposed || !_providerInitialized) return;
        var preference = ExecutionProviderService.Parse(value);
        if (ExecutionProviderService.ToSettingValue(preference) ==
            ExecutionProviderService.ToSettingValue(ExecutionProviderService.Preference))
        {
            RefreshAcceleration();
            return;
        }

        _models.SetExecutionProvider(preference);
        RefreshAcceleration();
        ShowStatus(
            ExecutionProviderService.RestartRequired(preference)
                ? $"Execution provider set to {ExecutionProviderService.LabelFor(preference)}. Restart Muesli to apply it."
                : $"Execution provider set to {ExecutionProviderService.LabelFor(preference)}.");
    }

    [RelayCommand]
    private async Task InstallCudaPackAsync(CancellationToken cancellationToken)
    {
        if (IsProviderBusy) return;
        IsProviderBusy = true;
        _cudaPackCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            var progress = new Progress<ModelDownloadProgress>(value =>
            {
                ShowStatus(value.DisplayText, ModelStatusTone.Informational);
                RefreshAcceleration();
            });
            var result = await _models.PrepareCudaPackAsync(progress, _cudaPackCancellation.Token);
            ShowStatus(result.Text);
        }
        catch (OperationCanceledException)
        {
            ShowStatus("CUDA acceleration pack installation cancelled.", ModelStatusTone.Informational);
        }
        catch (Exception exception)
        {
            ShowStatus($"CUDA acceleration pack failed: {exception.Message}", ModelStatusTone.Error);
        }
        finally
        {
            _cudaPackCancellation?.Dispose();
            _cudaPackCancellation = null;
            IsProviderBusy = false;
            RefreshAcceleration();
        }
    }

    [RelayCommand]
    private void CancelCudaPack() => _cudaPackCancellation?.Cancel();

    [RelayCommand]
    private async Task VerifyCudaPackAsync()
    {
        try
        {
            var verification = await _models.VerifyCudaPackAsync(CancellationToken.None);
            ShowStatus(verification.Diagnostic,
                verification.IsValid ? ModelStatusTone.Success : ModelStatusTone.Error);
        }
        catch (Exception exception)
        {
            ShowStatus($"CUDA acceleration pack verification failed: {exception.Message}", ModelStatusTone.Error);
        }
        finally
        {
            RefreshAcceleration();
        }
    }

    [RelayCommand]
    private async Task DeleteCudaPackAsync()
    {
        if (!IsCudaPackInstalled) return;
        var choice = await _dialogs.ConfirmAsync(
            "Delete the downloaded CUDA acceleration pack? The packaged CPU runtime keeps working, and the pack can be installed again later.",
            "Delete CUDA acceleration pack",
            CancellationToken.None);
        if (choice != AppDialogChoice.Primary) return;
        try
        {
            await _models.DeleteCudaPackAsync(CancellationToken.None);
            ShowStatus("CUDA acceleration pack deleted.");
        }
        catch (Exception exception)
        {
            ShowStatus($"Could not delete the CUDA acceleration pack: {exception.Message}", ModelStatusTone.Error);
        }
        finally
        {
            RefreshAcceleration();
        }
    }

    /// <summary>
    /// Discards the cached GPU verdict so the next transcription re-runs the real warm-up
    /// qualification. Used after a driver or GPU change.
    /// </summary>
    [RelayCommand]
    private void RequalifyGpu()
    {
        ExecutionProviderService.ResetQualification();
        ShowStatus("GPU qualification cleared. The next transcription re-runs a real warm-up inference on the GPU provider.");
        RefreshAcceleration();
    }

    [RelayCommand]
    private async Task RestartAsync()
    {
        try
        {
            var reason = await global::Windows.ApplicationModel.Core.CoreApplication.RequestRestartAsync("");
            if (reason == global::Windows.ApplicationModel.Core.AppRestartFailureReason.RestartPending)
            {
                ShowStatus("Restarting Muesli.");
            }
            else
            {
                ShowStatus($"Muesli could not restart automatically ({reason}). Quit Muesli from the tray and start it again.", ModelStatusTone.Error);
            }
        }
        catch (Exception exception)
        {
            ShowStatus($"Muesli could not restart automatically: {exception.Message}. Quit Muesli from the tray and start it again.", ModelStatusTone.Error);
        }
    }

    private void RefreshAcceleration()
    {
        if (_disposed) return;
        var status = _models.ProviderStatus();
        var telemetry = ExecutionProviderService.Snapshot();

        _providerInitialized = false;
        var options = new List<ProviderOption>
        {
            new("auto", "Automatic (recommended)",
                "Uses measured GPU acceleration where it is faster and falls back to CPU for the same model.",
                true),
            new("cpu", "CPU only",
                "Uses the packaged CPU runtime for every transcription path.",
                true)
        };
        var cudaAvailable = status.CudaPackVerified &&
                            status.NvidiaRuntimeComplete &&
                            status.NvidiaAdapterDetected &&
                            status.CudaDriverPresent;
        if (cudaAvailable || status.Requested == ExecutionProviderPreference.Cuda)
        {
            options.Add(new ProviderOption(
                "cuda",
                cudaAvailable ? "NVIDIA CUDA" : "NVIDIA CUDA (unavailable)",
                cudaAvailable
                    ? "Uses the verified CUDA acceleration pack and the installed CUDA 12 / cuDNN 9 runtime."
                    : "The saved CUDA preference is retained, but Muesli will use CPU until the runtime requirements are satisfied.",
                cudaAvailable));
        }

        ProviderOptions = options;
        SelectedProviderValue = ExecutionProviderService.ToSettingValue(status.Requested);
        _providerInitialized = true;
        IsCudaPackInstalled = status.CudaPackInstalled;
        IsRestartRequired = status.RestartRequired;
        RestartRequiredMessage = status.RestartRequired
            ? "The requested execution provider differs from the runtime already loaded in this process. Restart Muesli to switch."
            : "";

        var active = status.LoadedRuntime switch
        {
            "cuda" => "NVIDIA CUDA",
            "cpu" => "CPU",
            _ => "Unavailable"
        };
        // A CUDA-capable runtime may be loaded while Automatic still chooses CPU for the
        // selected model. Report runtime loading separately from measured inference.
        var offlineProvider = ExecutionProviderService.ActiveProvider(ExecutionProviderRole.OfflineTranscription) switch
        {
            "cuda" => "NVIDIA CUDA",
            "cpu" => "CPU",
            _ => "not measured"
        };
        AccelerationStatusLine = $"Requested: {status.RequestedLabel} · Runtime loaded: {active}";
        CudaPackStatusLine = status.CudaPackInstalled
            ? status.CudaPackVerified
                ? $"CUDA acceleration pack {CudaAccelerationPack.PackVersion} installed and SHA-256 verified."
                : "CUDA acceleration pack is installed but failed SHA-256 verification."
            : $"CUDA acceleration pack not installed ({CudaAccelerationPack.SizeLabel} download from the pinned sherpa-onnx 1.13.4 release archive).";

        AccelerationDetailText = string.Join(
            Environment.NewLine,
            $"Requested provider: {status.RequestedLabel}",
            $"Loaded runtime: {active}",
            $"Last offline inference provider: {offlineProvider}",
            $"Adapter: {status.AdaptersLabel}",
            $"Runtime: {status.LoadedRuntimeLabel} · sherpa-onnx {CudaAccelerationPack.SherpaRuntimeVersion} · ONNX Runtime {CudaAccelerationPack.OnnxRuntimeVersion}",
            status.NvidiaRuntimeComplete
                ? $"CUDA/cuDNN: CUDA {CudaAccelerationPack.CudaVersion} / cuDNN {CudaAccelerationPack.CudnnVersion} dependencies resolved"
                : $"CUDA/cuDNN: missing {string.Join(", ", status.MissingNvidiaFiles)}",
            $"Provider status: {status.Summary}",
            status.RestartRequired ? $"Restart required: {RestartRequiredMessage}" : "",
            $"DirectML: not available · {status.DirectMlReason}",
            $"Last model load: {(telemetry.LastModelLoadMs is long load ? $"{load} ms" : "not measured")}",
            telemetry.LastWarmInferenceMs is long warm && telemetry.LastWarmAudioMs is int audio and > 0
                ? $"Warm transcription: {warm} ms for {audio} ms audio · real-time factor {warm / (double)audio:0.000}"
                : "Warm transcription: not measured yet",
            telemetry.LastFallbackReason is { Length: > 0 } fallback ? $"Fallback: {fallback}" : "Fallback: none",
            telemetry.LastGpuEvidence is { Length: > 0 } evidence ? $"GPU evidence: {evidence}" : "GPU evidence: none",
            status.RuntimeDiagnostic);

        OnPropertyChanged(nameof(CanDeleteCudaPack));
        OnPropertyChanged(nameof(InstallCudaPackLabel));
    }

    [RelayCommand]
    private void SelectCleanupModel(CleanupModelItem? item)
    {
        if (item is null || !item.CanSelect) return;
        _models.SetCleanupModel(item.Id);
        ReloadCleanup();
        ShowStatus($"{item.DisplayName} is now used for local cleanup.");
    }

    [RelayCommand]
    private async Task PrepareCleanupAsync(CleanupModelItem? item, CancellationToken cancellationToken)
    {
        if (item is null || item.IsBusy) return;
        await RunCleanupOperationAsync(
            item,
            (progress, token) => _models.PrepareCleanupAsync(item.Id, progress, token),
            cancellationToken);
    }

    [RelayCommand]
    private async Task VerifyCleanupAsync(CleanupModelItem? item, CancellationToken cancellationToken)
    {
        if (item is null || item.IsBusy) return;
        await RunCleanupOperationAsync(
            item,
            (_, token) => _models.VerifyCleanupAsync(item.Id, token),
            cancellationToken);
    }

    [RelayCommand]
    private async Task DeleteCleanupAsync(CleanupModelItem? item, CancellationToken cancellationToken)
    {
        if (item is null || !item.CanDelete) return;
        var choice = await _dialogs.ConfirmAsync(
            $"Delete the local files for {item.DisplayName}? You can download them again later.",
            "Delete cleanup model",
            cancellationToken);
        if (choice != AppDialogChoice.Primary) return;
        await RunCleanupOperationAsync(
            item,
            (_, token) => _models.DeleteCleanupAsync(item.Id, token),
            cancellationToken);
    }

    private async Task RunCleanupOperationAsync(
        CleanupModelItem item,
        Func<IProgress<ModelDownloadProgress>, CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        item.IsBusy = true;
        try
        {
            var progress = new Progress<ModelDownloadProgress>(value =>
            {
                item.ProgressPercent = value.Percent;
                ShowStatus($"{item.DisplayName}: {value.DisplayText}", ModelStatusTone.Informational);
                ReloadCleanup();
            });
            await operation(progress, cancellationToken);
            ShowStatus($"{item.DisplayName} is ready.");
        }
        catch (OperationCanceledException)
        {
            ShowStatus($"{item.DisplayName} operation cancelled.", ModelStatusTone.Informational);
        }
        catch (Exception exception)
        {
            ShowStatus($"{item.DisplayName} failed: {exception.Message}", ModelStatusTone.Error);
        }
        finally
        {
            item.IsBusy = false;
            item.ProgressPercent = null;
            ReloadCleanup();
        }
    }

    private void ReloadCleanup()
    {
        if (_disposed) return;
        var snapshots = _models.CleanupSnapshots();
        var ordered = new List<CleanupModelItem>(snapshots.Count);
        foreach (var snapshot in snapshots)
        {
            if (!_cleanupById.TryGetValue(snapshot.Model.Id, out var item))
            {
                item = new CleanupModelItem(snapshot);
                _cleanupById[snapshot.Model.Id] = item;
            }

            // Rebuild the immutable snapshot state in place while keeping the observable instance,
            // so progress and busy state survive a reload.
            var replacement = new CleanupModelItem(snapshot) { IsBusy = item.IsBusy, ProgressPercent = item.ProgressPercent };
            _cleanupById[snapshot.Model.Id] = replacement;
            ordered.Add(replacement);
        }

        CleanupItems = ordered;
    }
}
