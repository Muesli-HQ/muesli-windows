namespace Muesli.Windows.Services;

public static class RuntimeStatusMapper
{
    public static RuntimeUiStatus Map(RuntimeDiagnostics? diagnostics, Exception? failure = null)
    {
        if (failure is not null || diagnostics is null)
        {
            return new RuntimeUiStatus(
                "Diagnostics failed",
                "Diagnostics failed",
                "Unknown",
                "Setup check failed. Open logs for details.");
        }

        var runtime = diagnostics.RuntimeReady ? "Runtime available" : "Runtime unavailable";
        var model = diagnostics.ModelReady ? "Model verified" : "Model missing or unverified";
        var modelName = diagnostics.DictationModelName;
        var readiness = diagnostics.RuntimeReady && diagnostics.ModelReady
            ? $"{modelName} is ready on {diagnostics.Acceleration}."
            : diagnostics.ModelReady
                ? $"{modelName} is downloaded, but the native runtime is unavailable."
                : $"{modelName} is missing or has not passed verification.";
        return new RuntimeUiStatus(runtime, model, diagnostics.Acceleration, readiness);
    }
}

public sealed record RuntimeUiStatus(
    string RuntimeStatus,
    string ModelStatus,
    string ProviderStatus,
    string Readiness);
