using System.IO;

namespace Muesli.Windows.Services;

public sealed class RuntimeDiagnosticsService
{
    public string ModelCacheDirectory => Path.Combine(
        MuesliPathService.UserProfileDirectory,
        ".cache",
        "muesli");

    public Task<RuntimeDiagnostics> InspectAsync(
        bool enableLocalCleanup,
        string dictationModelId,
        string finalMeetingModelId,
        string? liveMeetingModelId)
    {
        Directory.CreateDirectory(ModelCacheDirectory);
        Directory.CreateDirectory(NativeParakeetClient.ModelCacheDirectory);
        Directory.CreateDirectory(TranscriptionModelCatalog.ModelCacheDirectory);
        Directory.CreateDirectory(StreamingModelCatalog.ModelCacheDirectory);
        Directory.CreateDirectory(NativeTextCleanupService.ModelCacheDirectory);

        var provider = NativeSherpaRuntime.SelectedRuntime;
        var acceleration = provider.Equals("cuda", StringComparison.OrdinalIgnoreCase)
            ? "GPU (CUDA)"
            : provider.Equals("cpu", StringComparison.OrdinalIgnoreCase)
                ? "CPU"
                : "Unavailable";
        var dictationModel = TranscriptionModelCatalog.GetRequired(dictationModelId);
        var finalMeetingModel = TranscriptionModelCatalog.GetRequired(finalMeetingModelId);
        var dictationReady = TranscriptionModelReadiness.IsVerified(dictationModel);
        var finalMeetingReady = TranscriptionModelReadiness.IsVerified(finalMeetingModel);
        var dictationStatus = dictationReady ? "Ready · checksums verified" : "Missing or unverified";
        var finalMeetingStatus = finalMeetingReady ? "Ready · checksums verified" : "Missing or unverified";
        var diarizationStatus = NativeDiarizationClient.Status;
        var cleanupStatus = NativeTextCleanupService.Status(enableLocalCleanup);
        var transcriptionBytes = NativeTranscriptionClient.ModelCacheSizeBytes();
        var cleanupBytes = NativeTextCleanupService.ModelCacheSizeBytes();
        var streamingModel = StreamingModelCatalog.Get(liveMeetingModelId);
        var streamingBytes = StreamingModelCatalog.Models.Sum(model => new StreamingModelInstaller(model).DiskSizeBytes());
        var streamingStatus = streamingModel is null
            ? "Off by default"
            : new StreamingModelInstaller(streamingModel).IsVerified
                ? $"{streamingModel.DisplayName} ({streamingModel.Id}) · Ready · checksums verified"
                : $"{streamingModel.DisplayName} ({streamingModel.Id}) · Missing or unverified";
        var totalBytes = transcriptionBytes + streamingBytes + cleanupBytes + NativeDiarizationClient.ModelCacheSizeBytes();
        var runtimeReady = NativeParakeetClient.IsRuntimeAvailable;

        var packageTruth = NativeSherpaRuntime.IsCudaCapable
            ? "CUDA is active from an externally staged matching bundle. The public Wave 0 package still does not ship NVIDIA libraries."
            : PublicNativePackageContract.PublicPackageDisclosure;
        var detail = string.Join(
            Environment.NewLine,
            $"Dictation model: {dictationModel.DisplayName} ({dictationModel.Id}) · {dictationStatus}",
            $"Final meeting/import model: {finalMeetingModel.DisplayName} ({finalMeetingModel.Id}) · {finalMeetingStatus}",
            $"Live meeting model: {streamingStatus}",
            $"Runtime available: {(runtimeReady ? "yes" : "no")}",
            $"Dictation model verified: {(dictationReady ? "yes" : "no")}",
            $"Final meeting model verified: {(finalMeetingReady ? "yes" : "no")}",
            $"Execution provider: {provider}",
            $"Device: {(provider.Equals("cuda", StringComparison.OrdinalIgnoreCase) ? "cuda" : "cpu")}",
            "Compute type: native ONNX",
            $"Package truth: {packageTruth}",
            $"Sherpa runtime diagnostic: {NativeSherpaRuntime.Diagnostic}",
            $"Speaker diarization: {diarizationStatus}",
            $"Local cleanup: {cleanupStatus}");

        return Task.FromResult(new RuntimeDiagnostics(
            Runtime: $"Native Sherpa ONNX {provider}",
            RuntimeReady: runtimeReady,
            ModelReady: dictationReady,
            FinalMeetingModelReady: finalMeetingReady,
            DictationModelName: dictationModel.DisplayName,
            FinalMeetingModelName: finalMeetingModel.DisplayName,
            Acceleration: acceleration,
            DictationModelStatus: dictationStatus,
            DiarizationStatus: diarizationStatus,
            QwenCleanupStatus: cleanupStatus,
            ModelCacheDirectory: ModelCacheDirectory,
            ModelCacheBytes: totalBytes,
            Detail: detail));
    }

    public void OpenModelCacheDirectory()
    {
        Directory.CreateDirectory(ModelCacheDirectory);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = ModelCacheDirectory,
            UseShellExecute = true
        });
    }

    public void ClearModelCache()
    {
        NativeTranscriptionClient.ClearAllModelCaches();
        NativeDiarizationClient.ClearModelCache();
        NativeTextCleanupService.ClearModelCache();
    }
}

public sealed record RuntimeDiagnostics(
    string Runtime,
    bool RuntimeReady,
    bool ModelReady,
    bool FinalMeetingModelReady,
    string DictationModelName,
    string FinalMeetingModelName,
    string Acceleration,
    string DictationModelStatus,
    string DiarizationStatus,
    string QwenCleanupStatus,
    string ModelCacheDirectory,
    long ModelCacheBytes,
    string Detail)
{
    public RuntimeDiagnostics(
        string Runtime,
        bool RuntimeReady,
        bool ModelReady,
        string Acceleration,
        string DictationModelStatus,
        string DiarizationStatus,
        string QwenCleanupStatus,
        string ModelCacheDirectory,
        long ModelCacheBytes,
        string Detail)
        : this(
            Runtime,
            RuntimeReady,
            ModelReady,
            ModelReady,
            "Transcription model",
            "Final meeting model",
            Acceleration,
            DictationModelStatus,
            DiarizationStatus,
            QwenCleanupStatus,
            ModelCacheDirectory,
            ModelCacheBytes,
            Detail)
    {
    }

    public string ModelCacheSize => FormatBytes(ModelCacheBytes);

    public string Summary => string.Join(
        Environment.NewLine,
        $"Runtime: {Runtime}",
        $"Dictation: {DictationModelName} ({(ModelReady ? "verified" : "missing or unverified")})",
        $"Final meetings/imports: {FinalMeetingModelName} ({(FinalMeetingModelReady ? "verified" : "missing or unverified")})",
        $"Readiness: {(RuntimeReady && ModelReady ? "Ready" : ModelReady ? "Runtime unavailable" : "Model preparation required")}",
        $"Acceleration: {Acceleration}",
        $"Model cache: {ModelCacheDirectory} ({ModelCacheSize})",
        Detail);

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1_073_741_824 => $"{bytes / 1_073_741_824.0:0.0} GB",
        >= 1_048_576 => $"{bytes / 1_048_576.0:0.0} MB",
        >= 1024 => $"{bytes / 1024.0:0.0} KB",
        _ => $"{bytes} B"
    };
}
