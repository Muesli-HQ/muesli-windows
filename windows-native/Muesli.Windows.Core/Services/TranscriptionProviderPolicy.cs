namespace Muesli.Windows.Services;

/// <summary>
/// Measured GPU-benefit policy. Automatic mode only attempts a GPU provider for the architectures
/// where the shipping benchmark actually showed a meaningful warm-inference win; architectures that
/// merely tie (or regress) stay on CPU unless the user explicitly asks for NVIDIA CUDA.
///
/// Evidence: <c>qualification/gpu-qualification/BENCHMARK-SUMMARY.md</c> and
/// <c>docs/WINDOWS_GPU_QUALIFICATION.md</c>. Re-measure with
/// <c>scripts/benchmark-gpu-transcription.ps1</c> before changing these answers.
/// </summary>
public static class TranscriptionProviderPolicy
{
    /// <summary>
    /// True when Automatic should try CUDA first for this offline architecture. A false answer is a
    /// measured decision, not a limitation: the same model still runs on CPU and an explicit
    /// CUDA selection is still honoured.
    /// </summary>
    public static bool AutoShouldAttemptGpu(NativeAsrModelKind kind) => kind switch
    {
        // Measured 1.45x-3.3x faster warm inference on the qualification machine.
        NativeAsrModelKind.Whisper => true,
        // Measured 1.9x-2.3x faster warm inference.
        NativeAsrModelKind.ParakeetTransducer => true,
        // Legacy Parakeet v3 measured a tie (RTF 0.060 vs 0.063) with a 1.3 s slower model load.
        NativeAsrModelKind.Parakeet => false,
        // SenseVoice measured a tie (RTF 0.0201 vs 0.0210) with a 0.9 s slower model load.
        NativeAsrModelKind.SenseVoice => false,
        // Cohere measured a tie (RTF 0.174 vs 0.179) with a 4.4 s slower model load.
        NativeAsrModelKind.CohereTranscribe => false,
        // Qwen3-ASR measured 1.8x slower on CUDA, and the CUDA transcript differed from CPU.
        NativeAsrModelKind.Qwen3Asr => false,
        _ => false
    };

    /// <summary>
    /// Live Nemotron streaming keeps CPU under Automatic. Measured on the qualification machine:
    /// CPU real-time factor 0.759 versus CUDA 0.904 (CUDA slower), and the CUDA run produced a
    /// different committed transcript. An explicit CUDA selection is still honoured.
    /// </summary>
    public static bool AutoShouldAttemptGpuForStreaming => false;

    public static string MeasurementNote(NativeAsrModelKind kind) => kind switch
    {
        NativeAsrModelKind.Whisper =>
            "Measured: Whisper warm inference is 1.45x-3.3x faster on CUDA on the qualification machine.",
        NativeAsrModelKind.ParakeetTransducer =>
            "Measured: Parakeet transducer warm inference is 1.9x-2.3x faster on CUDA on the qualification machine.",
        NativeAsrModelKind.Parakeet =>
            "Measured: Parakeet v3 is a CPU/CUDA tie, so Automatic keeps CPU and avoids the slower CUDA model load.",
        NativeAsrModelKind.SenseVoice =>
            "Measured: SenseVoice is a CPU/CUDA tie, so Automatic keeps CPU and avoids the slower CUDA model load.",
        NativeAsrModelKind.CohereTranscribe =>
            "Measured: Cohere Transcribe is a CPU/CUDA tie, so Automatic keeps CPU and avoids the slower CUDA model load.",
        NativeAsrModelKind.Qwen3Asr =>
            "Measured: Qwen3-ASR is 1.8x slower on CUDA and its CUDA transcript differed from CPU, so Automatic keeps CPU.",
        _ => ""
    };
}
