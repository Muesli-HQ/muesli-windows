using Muesli.Windows.Services;

namespace Muesli.Windows.Cotypist;

/// <summary>A Cotypist typeahead model. <see cref="Release"/> is null for a model with no published
/// Windows artifact yet ("Coming soon"), exactly like a macOS model without a release.</summary>
public sealed record CotypistModel(string Id, string Label, string Summary, string Logo, bool IsRecommended, CleanupModelDefinition? Release)
{
    public string? Path => Release is null ? null : System.IO.Path.Combine(CotypistModels.CacheDirectory, Id, Release.SourceRevision.Split('@')[^1], Release.FileName);

    public bool IsDownloaded => Path is { } path && File.Exists(path);
}

/// <summary>
/// The macOS Cotypist models (2f10b23d) as base-model GGUF for llama.cpp, each pinned by revision with
/// the host's published SHA-256. macOS ships Muesli's MLX conversions; these are GGUF quantisations of
/// the same unmodified Qwen 3.5 base checkpoints. No trusted base (non-instruct) Gemma 4 E2B GGUF is
/// published, so that card stays "Coming soon" rather than substituting the instruct model.
/// </summary>
public static class CotypistModels
{
    public static string CacheDirectory =>
        Environment.GetEnvironmentVariable("MUESLI_COTYPIST_CACHE") ??
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "muesli", "models", "typeahead");

    public static IReadOnlyList<CotypistModel> All { get; } =
    [
        new("qwen3.5-0.8b-base", "Qwen 3.5 0.8B",
            "As accurate as Gemma on real writing at a ninth of the size, and about twice as fast.", "qwen-logo", true,
            Gguf("qwen3.5-0.8b-base", "ggml-org/Qwen3.5-0.8B-Base-GGUF", "4d656529a7f11288ba2e9c0f98b7d464c7deb850",
                "Qwen3.5-0.8B-Base-Q4_0.gguf", 563_035_840, "0dabf7f08793293d999ea306cee8c9caa3d76099e791ea2b0ce8f555f4e4098d")),
        new("qwen3.5-2b-base", "Qwen 3.5 2B",
            "The most accurate in testing, at about Gemma's speed and a third of its size.", "qwen-logo", false,
            Gguf("qwen3.5-2b-base", "mradermacher/Qwen3.5-2B-Base-GGUF", "85875686059c8887714b9cfbe4f091efc5484a2d",
                "Qwen3.5-2B-Base.Q4_K_M.gguf", 1_274_396_800, "1c42c644e4018f02ddf736e08426aa4597ba76f9de2aa649ef4cd669231743d3")),
        new("gemma-4-e2b-base", "Gemma 4 E2B",
            "Cotypist's first model. Accurate, but the largest and slowest of the three.", "google-logo", false, null)
    ];

    public static CotypistModel Default => All[0];

    public static CotypistModel Get(string? id) => All.FirstOrDefault(model => model.Id == id) ?? Default;

    private static CleanupModelDefinition Gguf(string id, string repository, string revision, string file, long size, string sha256) =>
        new(id, id, repository, file, $"https://huggingface.co/{repository}/resolve/{revision}/{file}", size, sha256,
            "Apache-2.0", "Multilingual", "", $"{repository}@{revision}");
}
