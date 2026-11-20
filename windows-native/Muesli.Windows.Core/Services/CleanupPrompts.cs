using System.Text.RegularExpressions;

namespace Muesli.Windows.Services;

/// <summary>macOS CustomTranscriptCleanupPrompt: a named system prompt for AI transcript cleanup.</summary>
public sealed record CleanupPromptPreset(string Id, string Name, string Prompt);

/// <summary>
/// macOS TranscriptCleanupPrompts and its controller actions: one built-in preset plus the user's
/// own. Creating a preset makes it active; deleting the active one falls back to the default.
/// </summary>
public static class CleanupPrompts
{
    public const string DefaultId = "default";

    public static IReadOnlyList<CleanupPromptPreset> BuiltIns { get; } =
        [new(DefaultId, "Default Cleanup", HostedTranscriptCleanup.DefaultSystemPrompt)];

    public static IReadOnlyList<CleanupPromptPreset> All(IReadOnlyList<CleanupPromptPreset> custom) => [.. BuiltIns, .. custom];

    public static CleanupPromptPreset Resolve(string? id, IReadOnlyList<CleanupPromptPreset> custom) =>
        All(custom).FirstOrDefault(preset => preset.Id == id) ?? BuiltIns[0];

    /// <summary>macOS savePromptEditor's name check, or null when the name can be saved.</summary>
    public static string? NameError(string name, string? excludingId, IReadOnlyList<CleanupPromptPreset> custom)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Enter a preset name.";
        return NameExists(name, excludingId, custom) ? "Use a unique preset name." : null;
    }

    public static string SuggestUniqueName(string baseName, IReadOnlyList<CleanupPromptPreset> custom)
    {
        var name = string.IsNullOrWhiteSpace(baseName) ? "Custom Cleanup" : baseName.Trim();
        if (!NameExists(name, null, custom)) return name;
        for (var suffix = 2; suffix <= 99; suffix++)
            if (!NameExists($"{name} {suffix}", null, custom)) return $"{name} {suffix}";
        return $"{name} {Guid.NewGuid().ToString("N")[..4]}";
    }

    public static MuesliSettings Create(MuesliSettings settings, string name, string prompt)
    {
        var preset = new CleanupPromptPreset(Guid.NewGuid().ToString(), name.Trim(), prompt.Trim());
        return settings with { CustomCleanupPrompts = [.. settings.CustomCleanupPrompts, preset], ActiveCleanupPromptId = preset.Id };
    }

    public static MuesliSettings Update(MuesliSettings settings, string id, string name, string prompt) =>
        settings with
        {
            CustomCleanupPrompts = settings.CustomCleanupPrompts
                .Select(preset => preset.Id == id ? preset with { Name = name.Trim(), Prompt = prompt.Trim() } : preset).ToList()
        };

    public static MuesliSettings Delete(MuesliSettings settings, string id) =>
        settings with
        {
            CustomCleanupPrompts = settings.CustomCleanupPrompts.Where(preset => preset.Id != id).ToList(),
            ActiveCleanupPromptId = settings.ActiveCleanupPromptId == id ? DefaultId : settings.ActiveCleanupPromptId
        };

    private static bool NameExists(string name, string? excludingId, IReadOnlyList<CleanupPromptPreset> custom) =>
        BuiltIns.Any(preset => Normalized(preset.Name) == Normalized(name)) ||
        custom.Any(preset => preset.Id != excludingId && Normalized(preset.Name) == Normalized(name));

    private static string Normalized(string name) => Regex.Replace(name.Trim(), @"\s+", " ").ToLowerInvariant();
}
