using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Muesli.Windows.Services;

/// <summary>
/// The execution provider a user asked for. <see cref="Auto"/> and <see cref="Cpu"/> are always
/// offered; <see cref="Cuda"/> is offered when a verified acceleration pack and a matching NVIDIA
/// runtime are present; <see cref="DirectMl"/> is parsed so an obsolete setting is reported
/// truthfully, but it is never offered as a working provider — see
/// <see cref="ExecutionProviderService.DirectMlUnsupportedReason"/>.
/// </summary>
public enum ExecutionProviderPreference
{
    Auto,
    Cpu,
    Cuda,
    DirectMl
}

/// <summary>Which part of the product is asking for a provider.</summary>
public enum ExecutionProviderRole
{
    OfflineTranscription,
    LiveTranscription,
    Diarization,
    TextCleanup
}

/// <summary>One provider the engine clients should try, in order.</summary>
public sealed record ExecutionProviderRequest(string Provider, bool IsGpu, string Source)
{
    public string Label => Provider.Equals("cuda", StringComparison.OrdinalIgnoreCase)
        ? "NVIDIA CUDA"
        : "CPU";
}

public sealed record ProviderAttempt(
    string Role,
    string Provider,
    string ModelId,
    bool Succeeded,
    long ElapsedMs,
    string Detail);

/// <summary>
/// What the product actually observed, as opposed to what it requested. Every field is written
/// only from a real recognizer construction, a real warm-up decode, or a real profile verdict.
/// </summary>
public sealed record ProviderTelemetry(
    string? LastActiveProvider,
    string? LastFallbackReason,
    long? LastModelLoadMs,
    long? LastWarmInferenceMs,
    int? LastWarmAudioMs,
    string? LastGpuEvidence,
    IReadOnlyList<ProviderAttempt> RecentAttempts);

/// <summary>
/// Full execution-provider picture for the Models diagnostics UI.
/// </summary>
public sealed record ExecutionProviderStatus(
    ExecutionProviderPreference Requested,
    string RequestedLabel,
    string LoadedRuntime,
    string LoadedRuntimeLabel,
    bool CudaPackInstalled,
    bool CudaPackVerified,
    string? CudaPackDirectory,
    bool NvidiaRuntimeComplete,
    IReadOnlyList<string> MissingNvidiaFiles,
    bool NvidiaAdapterDetected,
    bool CudaDriverPresent,
    bool DirectMlAvailable,
    string DirectMlReason,
    bool RestartRequired,
    IReadOnlyList<GpuAdapterInfo> Adapters,
    string RuntimeDiagnostic,
    string Summary)
{
    public string AdaptersLabel => Adapters.Count == 0
        ? "No display adapters reported"
        : string.Join(" · ", Adapters.Where(adapter => !adapter.IsSoftwareAdapter).Select(adapter => adapter.ToString()));

    public bool HasNvidiaAdapter => NvidiaAdapterDetected;
}

/// <summary>
/// The single place that decides which on-device execution provider Muesli may use. Dictation,
/// meeting finalization, imported media, live Nemotron transcription, and diarization all resolve
/// their provider through here, so a policy change cannot leave one path pinned to a stale choice.
///
/// Auto never trusts DLL presence: a GPU provider is only offered after a real warm-up inference
/// through this exact model has produced a positive ONNX Runtime profile verdict on this machine.
/// When the GPU attempt fails, the same selected model is retried on CPU and the fallback reason is
/// recorded for the Models diagnostics panel and the log.
/// </summary>
public static class ExecutionProviderService
{
    public const string CudaProvider = "cuda";
    public const string CpuProvider = "cpu";

    public const string DirectMlUnsupportedReason =
        "DirectML is not available: the pinned sherpa-onnx 1.13.4 Windows build was compiled without " +
        "SHERPA_ONNX_ENABLE_DIRECTML, and requesting provider=directml logs " +
        "\"DirectML is for Windows only. Fallback to cpu!\" and silently runs on CPU. Muesli will not " +
        "label CPU inference as DirectML.";

    // ONNX Runtime's Chrome-trace profile spells each node's execution provider with varying
    // whitespace, so the evidence counts the provider names rather than a formatted fragment.
    private const string CudaNodeMarker = "CUDAExecutionProvider";
    private const string CpuNodeMarker = "CPUExecutionProvider";
    private const string DirectMlNodeMarker = "DmlExecutionProvider";

    private static readonly object Gate = new();
    private static ExecutionProviderPreference _preference = ExecutionProviderPreference.Auto;
    private static readonly List<ProviderAttempt> Attempts = [];
    private static string? _activeOffline;
    private static string? _activeLive;
    private static string? _activeDiarization;
    private static string? _lastFallbackReason;
    private static long? _lastModelLoadMs;
    private static long? _lastWarmInferenceMs;
    private static int? _lastWarmAudioMs;
    private static string? _lastGpuEvidence;
    private static int _attemptSequence;
    private static readonly object QualificationGate = new();
    private const int QualificationCacheSchemaVersion = 2;
    private static Dictionary<string, ProviderEvidence> _cudaEvidenceByKey =
        new(StringComparer.OrdinalIgnoreCase);
    private static bool _cudaEvidenceLoaded;
    private static string? _qualificationCachePathOverride;

    /// <summary>
    /// The active preference for this process. It is set once during bootstrap, before any native
    /// sherpa library is loaded, because the CPU and CUDA builds cannot be mixed in one process.
    /// </summary>
    public static ExecutionProviderPreference Preference
    {
        get
        {
            lock (Gate)
            {
                return _preference;
            }
        }
    }

    /// <summary>Applies a preference before the native runtime is loaded. Never throws.</summary>
    public static void Configure(ExecutionProviderPreference preference)
    {
        lock (Gate)
        {
            _preference = preference;
        }
    }

    public static void Configure(string? settingValue) => Configure(Parse(settingValue));

    public static ExecutionProviderPreference Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "cpu" => ExecutionProviderPreference.Cpu,
        "cuda" or "gpu" => ExecutionProviderPreference.Cuda,
        "directml" or "dml" => ExecutionProviderPreference.DirectMl,
        _ => ExecutionProviderPreference.Auto
    };

    public static string ToSettingValue(ExecutionProviderPreference preference) => preference switch
    {
        ExecutionProviderPreference.Cpu => "cpu",
        ExecutionProviderPreference.Cuda => "cuda",
        ExecutionProviderPreference.DirectMl => "directml",
        _ => "auto"
    };

    public static string LabelFor(ExecutionProviderPreference preference) => preference switch
    {
        ExecutionProviderPreference.Cpu => "CPU only",
        ExecutionProviderPreference.Cuda => "NVIDIA CUDA",
        ExecutionProviderPreference.DirectMl => "DirectML",
        _ => "Automatic"
    };

    /// <summary>
    /// Ordered providers for a role. The last entry is always CPU, so an engine client can always
    /// fall back without changing the model or the language.
    ///
    /// A role-specific environment override (used by the release qualification scripts) still wins
    /// over the persisted preference; it is never silently ignored.
    ///
    /// Automatic is measured, not assumed: <paramref name="modelKind"/> lets the measured
    /// <see cref="TranscriptionProviderPolicy"/> keep architectures where CUDA ties or regresses on
    /// CPU instead of paying a GPU model load for no benefit.
    /// </summary>
    public static IReadOnlyList<ExecutionProviderRequest> PreferredProviders(
        ExecutionProviderRole role,
        NativeAsrModelKind? modelKind = null,
        bool streaming = false)
    {
        if (role == ExecutionProviderRole.TextCleanup)
        {
            // Cleanup runs on the LLamaSharp backend, not the sherpa runtime; it resolves its own
            // acceleration below.
            return [new ExecutionProviderRequest(CpuProvider, false, "cleanup-backend")];
        }

        var overrideValue = ExplicitProviderOverride(role);
        var preference = overrideValue is null ? Preference : Parse(overrideValue);
        var result = new List<ExecutionProviderRequest>();
        var cudaCapable = NativeSherpaRuntime.IsCudaCapable;
        var allowCuda = preference is ExecutionProviderPreference.Auto or ExecutionProviderPreference.Cuda;
        if (allowCuda && preference == ExecutionProviderPreference.Auto)
        {
            var beneficial = modelKind is { } kind
                ? TranscriptionProviderPolicy.AutoShouldAttemptGpu(kind)
                : streaming && TranscriptionProviderPolicy.AutoShouldAttemptGpuForStreaming;
            if (!beneficial)
            {
                RecordAutoCpuDecision(role, modelKind, streaming);
                allowCuda = false;
            }
        }

        if (allowCuda && cudaCapable)
        {
            result.Add(new ExecutionProviderRequest(
                CudaProvider,
                true,
                NativeSherpaRuntime.CudaRuntimeDirectory ?? "cuda-bundle"));
        }

        result.Add(new ExecutionProviderRequest(CpuProvider, false, "packaged-cpu"));
        return result;
    }

    private static void RecordAutoCpuDecision(ExecutionProviderRole role, NativeAsrModelKind? kind, bool streaming)
    {
        var note = kind is { } value
            ? TranscriptionProviderPolicy.MeasurementNote(value)
            : streaming
                ? "Measured: live streaming keeps CPU for now; see docs/WINDOWS_GPU_QUALIFICATION.md."
                : "Measured: automatic kept CPU for this architecture.";
        if (string.IsNullOrWhiteSpace(note))
        {
            return;
        }

        lock (Gate)
        {
            _lastFallbackReason = $"Automatic chose CPU: {note}";
        }
    }

    /// <summary>The role-specific provider environment override, or null when unset.</summary>
    public static string? ExplicitProviderOverride(ExecutionProviderRole role)
    {
        var name = role switch
        {
            ExecutionProviderRole.OfflineTranscription => "MUESLI_ASR_PROVIDER",
            ExecutionProviderRole.LiveTranscription => "MUESLI_LIVE_PROVIDER",
            ExecutionProviderRole.Diarization => "MUESLI_DIARIZATION_PROVIDER",
            _ => null
        };
        var value = name is null ? null : Environment.GetEnvironmentVariable(name);
        value ??= Environment.GetEnvironmentVariable("MUESLI_PARAKEET_PROVIDER");
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    /// <summary>
    /// True when the active provider came from the user's persisted setting rather than an
    /// environment override. Diagnostics uses this to avoid attributing an override to the UI.
    /// </summary>
    public static bool IsProviderForcedByEnvironment(ExecutionProviderRole role) =>
        ExplicitProviderOverride(role) is not null;

    /// <summary>
    /// Why a GPU provider is not in the candidate list, or "" when it is. Shown verbatim in
    /// diagnostics and written to the log when Auto ends up on CPU.
    /// </summary>
    public static string ExplainWhyNoGpu(ExecutionProviderRole role)
    {
        if (role == ExecutionProviderRole.TextCleanup)
        {
            return "";
        }

        var preference = Preference;
        if (preference == ExecutionProviderPreference.Cpu)
        {
            return "";
        }

        if (preference == ExecutionProviderPreference.DirectMl)
        {
            return DirectMlUnsupportedReason;
        }

        var environment = ProbeEnvironment();
        if (environment.PackInstalled && !environment.PackVerified)
        {
            return "The NVIDIA CUDA acceleration pack is installed but did not pass SHA-256 verification.";
        }

        if (!environment.PackInstalled)
        {
            return "The NVIDIA CUDA acceleration pack is not installed. Muesli is using the packaged CPU provider.";
        }

        if (!environment.NvidiaAdapterDetected || !environment.CudaDriverPresent)
        {
            return "No NVIDIA GPU with a CUDA-capable driver was detected on this PC.";
        }

        if (environment.MissingNvidiaFiles.Any())
        {
            return "The CUDA acceleration pack is verified, but required CUDA 12 / cuDNN 9 components are missing: " +
                   string.Join(", ", environment.MissingNvidiaFiles) + ".";
        }

        if (!NativeSherpaRuntime.IsCudaCapable)
        {
            return "The CUDA acceleration pack is present but the CUDA sherpa runtime could not be loaded.";
        }

        return "The NVIDIA CUDA acceleration pack is installed and verified.";
    }

    private readonly record struct ProviderEnvironment(
        bool PackInstalled,
        bool PackVerified,
        IReadOnlyList<string> MissingNvidiaFiles,
        bool NvidiaAdapterDetected,
        bool CudaDriverPresent,
        IReadOnlyList<GpuAdapterInfo> Adapters);

    /// <summary>
    /// Filesystem, DXGI, and NVIDIA-driver facts only. Deliberately does not call
    /// <see cref="Inspect"/> so the diagnostic text builder cannot recurse.
    /// </summary>
    private static ProviderEnvironment ProbeEnvironment()
    {
        var packInstalled = Directory.Exists(CudaAccelerationPack.InstallDirectory);
        var packVerified = CudaAccelerationPack.IsInstalled;
        var missingNvidia = packVerified
            ? NativeSherpaRuntime.ResolveMissingNvidiaDependencies(CudaAccelerationPack.InstallDirectory)
            : NativeSherpaRuntime.NvidiaDependencyFileNames.ToArray();
        var adapters = OperatingSystem.IsWindows()
            ? GpuInventory.Adapters
            : [];
        return new ProviderEnvironment(
            packInstalled,
            packVerified,
            missingNvidia,
            adapters.Any(adapter => adapter.VendorId == 0x10DE),
            OperatingSystem.IsWindows() && GpuInventory.IsNvidiaCudaDriverPresent(),
            adapters);
    }

    // ---------------------------------------------------------------- active-provider telemetry

    public static void RecordActive(
        ExecutionProviderRole role,
        string provider,
        string modelId,
        long modelLoadMs,
        ProviderEvidence? evidence = null)
    {
        lock (Gate)
        {
            var confirmed = evidence?.ConfirmedProvider ?? provider;
            switch (role)
            {
                case ExecutionProviderRole.OfflineTranscription:
                    _activeOffline = confirmed;
                    break;
                case ExecutionProviderRole.LiveTranscription:
                    _activeLive = confirmed;
                    break;
                case ExecutionProviderRole.Diarization:
                    _activeDiarization = confirmed;
                    break;
            }

            if (modelLoadMs > 0)
            {
                _lastModelLoadMs = modelLoadMs;
            }

            if (evidence is not null)
            {
                _lastGpuEvidence = evidence.Detail;
            }

            AddAttempt(role, provider, modelId, true, modelLoadMs,
                evidence is null ? $"Active provider: {confirmed}." : evidence.Detail);
        }
    }

    public static void RecordFailure(
        ExecutionProviderRole role,
        string provider,
        string modelId,
        long elapsedMs,
        string detail)
    {
        lock (Gate)
        {
            AddAttempt(role, provider, modelId, false, elapsedMs, detail);
        }
    }

    public static void RecordFallback(
        ExecutionProviderRole role,
        string modelId,
        string requestedProvider,
        string reason)
    {
        lock (Gate)
        {
            _lastFallbackReason = $"{LabelForProvider(requestedProvider)} unavailable for {modelId}: {reason}";
            AddAttempt(role, requestedProvider, modelId, false, 0, reason);
        }
    }

    public static void RecordWarmInference(long inferenceMs, int audioMs)
    {
        lock (Gate)
        {
            _lastWarmInferenceMs = inferenceMs;
            _lastWarmAudioMs = audioMs;
        }
    }

    public static string? ActiveProvider(ExecutionProviderRole role)
    {
        lock (Gate)
        {
            return role switch
            {
                ExecutionProviderRole.OfflineTranscription => _activeOffline,
                ExecutionProviderRole.LiveTranscription => _activeLive,
                ExecutionProviderRole.Diarization => _activeDiarization,
                _ => null
            };
        }
    }

    public static ProviderTelemetry Snapshot()
    {
        lock (Gate)
        {
            return new ProviderTelemetry(
                _activeOffline ?? _activeLive ?? _activeDiarization,
                _lastFallbackReason,
                _lastModelLoadMs,
                _lastWarmInferenceMs,
                _lastWarmAudioMs,
                _lastGpuEvidence,
                Attempts.ToArray());
        }
    }

    public static void ResetTelemetry()
    {
        lock (Gate)
        {
            Attempts.Clear();
            _activeOffline = null;
            _activeLive = null;
            _activeDiarization = null;
            _lastFallbackReason = null;
            _lastModelLoadMs = null;
            _lastWarmInferenceMs = null;
            _lastWarmAudioMs = null;
            _lastGpuEvidence = null;
        }
    }

    private static void AddAttempt(
        ExecutionProviderRole role,
        string provider,
        string modelId,
        bool succeeded,
        long elapsedMs,
        string detail)
    {
        Attempts.Add(new ProviderAttempt(role.ToString(), provider, modelId, succeeded, elapsedMs, detail));
        // Diagnostics keep the tail only; a long meeting must not grow this without bound.
        if (Attempts.Count > 32)
        {
            Attempts.RemoveRange(0, Attempts.Count - 32);
        }

        _attemptSequence++;
    }

    private static string LabelForProvider(string provider) => provider.Equals(CudaProvider, StringComparison.OrdinalIgnoreCase)
        ? "NVIDIA CUDA"
        : "CPU";

    // ----------------------------------------------------------------------------- status

    public static ExecutionProviderStatus Inspect()
    {
        var preference = Preference;
        var environment = ProbeEnvironment();
        var loadedRuntime = NativeSherpaRuntime.SelectedRuntime;
        var loadedLabel = loadedRuntime switch
        {
            "cuda" => "NVIDIA CUDA",
            "cpu" => "CPU",
            "not initialized" => "Not initialized",
            _ => "Unavailable"
        };
        var packInstalled = environment.PackInstalled;
        var missingNvidia = environment.MissingNvidiaFiles;
        var adapters = environment.Adapters;
        var nvidiaAdapter = environment.NvidiaAdapterDetected;
        var cudaDriver = environment.CudaDriverPresent;
        var restartRequired = RestartRequired(preference);

        var summary = preference switch
        {
            ExecutionProviderPreference.Cpu =>
                "CPU only was requested. Muesli uses the packaged CPU runtime for every transcription path.",
            ExecutionProviderPreference.DirectMl =>
                DirectMlUnsupportedReason,
            ExecutionProviderPreference.Cuda when loadedRuntime == "cuda" =>
                "NVIDIA CUDA was requested and the CUDA sherpa runtime is loaded. GPU inference is verified per model by a warm-up inference.",
            ExecutionProviderPreference.Cuda =>
                "NVIDIA CUDA was requested but the CUDA sherpa runtime is not loaded. " + ExplainWhyNoGpu(ExecutionProviderRole.OfflineTranscription),
            _ when loadedRuntime == "cuda" =>
                "Automatic: a verified CUDA runtime is available. GPU is attempted first only for architectures where the shipping benchmark measured a real win (Whisper, Parakeet transducer); other models stay on CPU, and CPU is always the fallback for the same model.",
            _ =>
                "Automatic: " + (ExplainWhyNoGpu(ExecutionProviderRole.OfflineTranscription) is { Length: > 0 } reason
                    ? reason
                    : "Muesli uses the packaged CPU runtime.")
        };

        return new ExecutionProviderStatus(
            preference,
            LabelFor(preference),
            loadedRuntime,
            loadedLabel,
            packInstalled,
            environment.PackVerified,
            packInstalled ? CudaAccelerationPack.InstallDirectory : null,
            missingNvidia.Count == 0,
            missingNvidia,
            nvidiaAdapter,
            cudaDriver,
            DirectMlAvailable: false,
            DirectMlUnsupportedReason,
            restartRequired,
            adapters,
            NativeSherpaRuntime.Diagnostic,
            summary);
    }

    /// <summary>
    /// True when moving to <paramref name="candidate"/> needs a process restart because the native
    /// sherpa runtime for this process has already been selected.
    /// </summary>
    public static bool RestartRequired(ExecutionProviderPreference candidate)
    {
        if (!NativeSherpaRuntime.HasLoadedRuntime)
        {
            return false;
        }

        var loaded = NativeSherpaRuntime.SelectedRuntime;
        var wouldLoad = WouldLoad(candidate);
        if (loaded is "unavailable" or "not initialized" || wouldLoad is "unavailable" or "not initialized")
        {
            return false;
        }

        return !string.Equals(loaded, wouldLoad, StringComparison.OrdinalIgnoreCase);
    }

    private static string WouldLoad(ExecutionProviderPreference candidate)
    {
        if (candidate == ExecutionProviderPreference.Cpu)
        {
            return "cpu";
        }

        return NativeSherpaRuntime.ResolveCudaBundleDirectory() is not null ? "cuda" : "cpu";
    }

    // ------------------------------------------------------------------------ warm-up probing

    /// <summary>
    /// Starts a provider attempt. For a GPU provider on the first attempt in this process the
    /// returned provider string enables ONNX Runtime profiling, so the warm-up inference can prove
    /// which provider actually executed the graph instead of trusting the request.
    /// </summary>
    public static ProviderWarmUp BeginWarmUp(string provider, string roleKey)
    {
        if (provider.Equals(CpuProvider, StringComparison.OrdinalIgnoreCase))
        {
            return new ProviderWarmUp(provider, roleKey, null);
        }

        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "muesli",
            "diagnostics");
        try
        {
            Directory.CreateDirectory(directory);
            var configPath = Path.Combine(directory, $"ort-profile-{Environment.ProcessId}-{Interlocked.Increment(ref _attemptSequence)}.conf");
            var prefix = Path.Combine(directory, $"ort-profile-{Environment.ProcessId}-{Interlocked.Increment(ref _attemptSequence)}");
            File.WriteAllText(configPath, $"ProfilingFilePrefix={prefix}");
            return new ProviderWarmUp(provider, roleKey, new ProfileSession(configPath, prefix));
        }
        catch
        {
            // If the diagnostic directory cannot be written, fall back to a plain provider request.
            // The model construction itself is still the acceptance gate.
            return new ProviderWarmUp(provider, roleKey, null);
        }
    }

    internal sealed record ProfileSession(string ConfigPath, string Prefix);

    /// <summary>
    /// Feeds the recognizer's result back into the provider opinion. Never throws.
    /// </summary>
    public static ProviderEvidence ReadEvidence(string provider, IReadOnlyList<string> profilePrefixes)
    {
        if (provider.Equals(CpuProvider, StringComparison.OrdinalIgnoreCase))
        {
            return new ProviderEvidence(CpuProvider, CpuProvider, false, 0, 0, "CPU provider requested.");
        }

        var cudaNodes = 0;
        var cpuNodes = 0;
        var dmlNodes = 0;
        foreach (var prefix in profilePrefixes)
        {
            foreach (var path in EnumerateProfileFiles(prefix))
            {
                try
                {
                    var text = File.ReadAllText(path);
                    cudaNodes += CountOccurrences(text, CudaNodeMarker);
                    cpuNodes += CountOccurrences(text, CpuNodeMarker);
                    dmlNodes += CountOccurrences(text, DirectMlNodeMarker);
                }
                catch
                {
                    // A profile that cannot be read simply contributes no evidence.
                }
            }
        }

        if (cudaNodes > 0)
        {
            return new ProviderEvidence(
                provider,
                CudaProvider,
                true,
                cudaNodes,
                cpuNodes,
                $"CUDA verified by warm-up inference: {cudaNodes} graph nodes ran on CUDAExecutionProvider ({cpuNodes} on CPU).");
        }

        if (dmlNodes > 0)
        {
            return new ProviderEvidence(provider, "directml", true, dmlNodes, cpuNodes,
                $"DirectML verified by warm-up inference: {dmlNodes} graph nodes ran on DmlExecutionProvider.");
        }

        return new ProviderEvidence(
            provider,
            CpuProvider,
            false,
            cudaNodes,
            cpuNodes,
            cpuNodes > 0
                ? $"Requested {provider} but the warm-up profile shows only CPUExecutionProvider ({cpuNodes} nodes); treating the session as CPU."
                : $"Requested {provider} but no warm-up profile was produced ({DescribeProfileLookup(profilePrefixes)}); treating the session as CPU.");
    }

    private static string DescribeProfileLookup(IReadOnlyList<string> prefixes)
    {
        if (prefixes.Count == 0)
        {
            return "no probe";
        }

        var prefix = prefixes[0];
        var directory = Path.GetDirectoryName(prefix) ?? "";
        if (!Directory.Exists(directory))
        {
            return $"looked for {Path.GetFileName(prefix)}_*.json in missing directory {directory}";
        }

        var names = Directory.EnumerateFiles(directory)
            .Select(Path.GetFileName)
            .Take(8)
            .ToArray();
        return $"looked for {Path.GetFileName(prefix)}_*.json in {directory}; present: {string.Join(", ", names)}";
    }

    private static IEnumerable<string> EnumerateProfileFiles(string prefix)
    {
        var directory = Path.GetDirectoryName(prefix);
        var name = Path.GetFileName(prefix);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            yield break;
        }

        foreach (var path in Directory.EnumerateFiles(directory, $"{name}_*.json"))
        {
            yield return path;
        }
    }

    internal static void CleanupProfile(ProfileSession? session)
    {
        if (session is null)
        {
            return;
        }

        try
        {
            if (File.Exists(session.ConfigPath))
            {
                File.Delete(session.ConfigPath);
            }

            foreach (var file in EnumerateProfileFiles(session.Prefix).ToArray())
            {
                try { File.Delete(file); } catch { }
            }
        }
        catch
        {
            // Profile cleanup is best-effort.
        }
    }

    // ------------------------------------------------------------------ durable GPU qualification

    /// <summary>
    /// A CUDA verdict that was already proven by a real warm-up inference on this machine for this
    /// exact acceleration pack, ONNX Runtime build and NVIDIA driver. Reusing it avoids paying a
    /// second model load on every launch while keeping the acceptance gate honest.
    /// </summary>
    public static bool TryGetQualifiedCudaEvidence(string qualificationKey, out ProviderEvidence evidence)
    {
        lock (QualificationGate)
        {
            EnsureQualificationLoaded();
            var key = NormalizeQualificationKey(qualificationKey);
            if (_cudaEvidenceByKey.TryGetValue(key, out var cached) && cached.ConfirmedGpuInference)
            {
                evidence = cached;
                return true;
            }

            evidence = ProviderEvidenceRejected with
            {
                Detail = $"No CUDA warm-up inference has been performed for {key} yet."
            };
            return false;
        }
    }

    public static void StoreQualifiedCudaEvidence(string qualificationKey, ProviderEvidence evidence)
    {
        lock (QualificationGate)
        {
            EnsureQualificationLoaded();
            if (!evidence.ConfirmedGpuInference)
            {
                return;
            }

            var key = NormalizeQualificationKey(qualificationKey);
            _cudaEvidenceByKey[key] = evidence;

            try
            {
                var path = QualificationCachePath;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var entries = _cudaEvidenceByKey.ToDictionary(
                    pair => pair.Key,
                    pair => new PersistedQualification(
                        pair.Value.RequestedProvider,
                        pair.Value.ConfirmedProvider,
                        pair.Value.ConfirmedGpuInference,
                        pair.Value.GpuNodeCount,
                        pair.Value.CpuNodeCount,
                        pair.Value.Detail),
                    StringComparer.OrdinalIgnoreCase);
                var serialized = JsonSerializer.Serialize(new PersistedQualificationStore(
                    QualificationCacheSchemaVersion,
                    QualificationFingerprint(),
                    entries));
                var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
                try
                {
                    File.WriteAllText(temporaryPath, serialized);
                    File.Move(temporaryPath, path, overwrite: true);
                }
                finally
                {
                    try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch { }
                }
            }
            catch
            {
                // A cache that cannot be written only means the next launch re-probes.
            }
        }
    }

    /// <summary>
    /// Forgets a cached GPU verdict so the next transcription runs a fresh warm-up qualification.
    /// Exposed in the Models diagnostics panel for driver or GPU changes.
    /// </summary>
    public static void ResetQualification()
    {
        lock (QualificationGate)
        {
            _cudaEvidenceByKey = new Dictionary<string, ProviderEvidence>(StringComparer.OrdinalIgnoreCase);
            _cudaEvidenceLoaded = true;
            try
            {
                if (File.Exists(QualificationCachePath))
                {
                    File.Delete(QualificationCachePath);
                }
            }
            catch
            {
                // A cache that cannot be deleted only means the next launch reuses the old verdict.
            }
        }
    }

    private static readonly ProviderEvidence ProviderEvidenceRejected =
        new(CudaProvider, CpuProvider, false, 0, 0, "No CUDA warm-up inference has been performed yet.");

    private static string QualificationCachePath => Path.Combine(
        _qualificationCachePathOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "muesli"),
        "provider-qualification.json");

    internal static void SetQualificationCacheDirectoryForTests(string? directory)
    {
        lock (QualificationGate)
        {
            _qualificationCachePathOverride = directory;
            _cudaEvidenceByKey = new Dictionary<string, ProviderEvidence>(StringComparer.OrdinalIgnoreCase);
            _cudaEvidenceLoaded = false;
        }
    }

    private static void EnsureQualificationLoaded()
    {
        if (_cudaEvidenceLoaded)
        {
            return;
        }

        _cudaEvidenceLoaded = true;
        _cudaEvidenceByKey = LoadPersistedQualifications();
    }

    private static Dictionary<string, ProviderEvidence> LoadPersistedQualifications()
    {
        try
        {
            var path = QualificationCachePath;
            if (!File.Exists(path))
            {
                return new Dictionary<string, ProviderEvidence>(StringComparer.OrdinalIgnoreCase);
            }

            var persisted = JsonSerializer.Deserialize<PersistedQualificationStore>(File.ReadAllText(path));
            if (persisted is null ||
                persisted.SchemaVersion != QualificationCacheSchemaVersion ||
                !persisted.Fingerprint.Equals(QualificationFingerprint(), StringComparison.Ordinal))
            {
                return new Dictionary<string, ProviderEvidence>(StringComparer.OrdinalIgnoreCase);
            }

            return persisted.Entries
                .Where(pair => pair.Value.ConfirmedGpuInference)
                .ToDictionary(
                    pair => NormalizeQualificationKey(pair.Key),
                    pair => new ProviderEvidence(
                        pair.Value.RequestedProvider,
                        pair.Value.ConfirmedProvider,
                        true,
                        pair.Value.GpuNodeCount,
                        pair.Value.CpuNodeCount,
                        pair.Value.Detail + " (reused for this model from this machine's earlier qualification)"),
                    StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, ProviderEvidence>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static string NormalizeQualificationKey(string qualificationKey) =>
        string.IsNullOrWhiteSpace(qualificationKey)
            ? throw new ArgumentException("A model or role qualification key is required.", nameof(qualificationKey))
            : qualificationKey.Trim().ToLowerInvariant();

    private static string QualificationFingerprint()
    {
        var ort = "";
        try
        {
            var ortPath = Path.Combine(CudaAccelerationPack.InstallDirectory, "onnxruntime.dll");
            if (File.Exists(ortPath))
            {
                ort = FileVersionInfo.GetVersionInfo(ortPath).FileVersion ?? "";
            }
        }
        catch
        {
            // A missing version simply produces a fingerprint that will never match a cached one.
        }

        return string.Join(
            "|",
            CudaAccelerationPack.PackVersion,
            CudaAccelerationPack.ArchiveSha256,
            ort,
            NvidiaDriverVersion() ?? "driver-unknown");
    }

    private static string? NvidiaDriverVersion()
    {
        if (DriverVersionProbed)
        {
            return _driverVersion;
        }

        DriverVersionProbed = true;
        try
        {
            var startInfo = new ProcessStartInfo("nvidia-smi", "--query-gpu=driver_version --format=csv,noheader")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(5000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return null;
            }

            _driverVersion = output
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault() ?? "";
        }
        catch
        {
            _driverVersion = "";
        }

        return string.IsNullOrWhiteSpace(_driverVersion) ? null : _driverVersion;
    }

    private static bool DriverVersionProbed;
    private static string? _driverVersion;

    private sealed record PersistedQualificationStore(
        [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
        [property: JsonPropertyName("fingerprint")] string Fingerprint,
        [property: JsonPropertyName("entries")] Dictionary<string, PersistedQualification> Entries);

    private sealed record PersistedQualification(
        [property: JsonPropertyName("requestedProvider")] string RequestedProvider,
        [property: JsonPropertyName("confirmedProvider")] string ConfirmedProvider,
        [property: JsonPropertyName("confirmedGpuInference")] bool ConfirmedGpuInference,
        [property: JsonPropertyName("gpuNodeCount")] int GpuNodeCount,
        [property: JsonPropertyName("cpuNodeCount")] int CpuNodeCount,
        [property: JsonPropertyName("detail")] string Detail);

    private static int CountOccurrences(string text, string marker)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(marker))
        {
            return 0;
        }

        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(marker, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += marker.Length;
        }

        return count;
    }
}

/// <summary>The provider string to configure, plus the profiling session used to verify it.</summary>
public sealed class ProviderWarmUp
{
    private readonly string _provider;
    private readonly string _roleKey;
    private readonly ExecutionProviderService.ProfileSession? _session;
    private bool _completed;

    internal ProviderWarmUp(string provider, string roleKey, ExecutionProviderService.ProfileSession? session)
    {
        _provider = provider;
        _roleKey = roleKey;
        _session = session;
    }

    public string Provider => _provider;

    /// <summary>
    /// The value for the recognizer's Provider option. GPU attempts carry a profiling config so a
    /// warm-up decode can prove which provider executed the graph.
    /// </summary>
    public string ProviderString => _session is null
        ? _provider
        : $"{_provider}:{_session.ConfigPath}";

    public bool IsProbing => _session is not null;

    /// <summary>
    /// Reads the ONNX Runtime profile produced by the probe recognizer. The caller has already run
    /// the warm-up inference and released the probe session, which is what flushes the profile.
    /// </summary>
    public ProviderEvidence Complete()
    {
        _completed = true;
        if (_session is null)
        {
            return new ProviderEvidence(_provider, _provider, _provider != CpuProviderName, 0, 0,
                _provider == CpuProviderName
                    ? $"CPU provider requested for {_roleKey}. No GPU inference is claimed."
                    : $"No profile probe was available for {_provider} ({_roleKey}); the model construction itself was the acceptance gate.");
        }

        var evidence = ExecutionProviderService.ReadEvidence(_provider, [_session.Prefix]);
        ExecutionProviderService.CleanupProfile(_session);
        return evidence;
    }

    public void Abandon(Exception exception)
    {
        if (_completed)
        {
            return;
        }

        _completed = true;
        ExecutionProviderService.CleanupProfile(_session);
    }

    private const string CpuProviderName = "cpu";
}

/// <summary>
/// The verified outcome of a warm-up inference. <see cref="ConfirmedProvider"/> is what the
/// product may truthfully report as active.
/// </summary>
public sealed record ProviderEvidence(
    string RequestedProvider,
    string ConfirmedProvider,
    bool ConfirmedGpuInference,
    int GpuNodeCount,
    int CpuNodeCount,
    string Detail);
