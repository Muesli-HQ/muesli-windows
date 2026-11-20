namespace Muesli.Windows.Services;

/// <summary>
/// macOS DictationAppFormatting (28adabba, 64fdb1c0): output-only rules. They never change the cleanup
/// prompt or infer a tone. Values are the macOS raw strings so an unknown value can't break settings load.
/// </summary>
public sealed record DictationAppFormatting(string LetterCase = DictationAppFormatting.Unchanged, string FinalPeriod = DictationAppFormatting.Keep)
{
    public const string Unchanged = "unchanged";
    public const string Lowercase = "lowercase";
    public const string Keep = "keep";
    public const string Remove = "remove";

    public static DictationAppFormatting Original { get; } = new();
    public static DictationAppFormatting LowercaseChat { get; } = new(Lowercase, Remove);
    public static DictationAppFormatting NoFinalPeriod { get; } = new(Unchanged, Remove);

    public bool IsValid => LetterCase is Unchanged or Lowercase && FinalPeriod is Keep or Remove;
    public bool IsOriginal => this == Original;

    public string Preset =>
        this == Original ? "unchanged"
        : this == LowercaseChat ? "lowercase_chat"
        : this == NoFinalPeriod ? "no_final_period"
        : "custom";

    public string Apply(string text)
    {
        var result = LetterCase == Lowercase ? text.ToLower(System.Globalization.CultureInfo.CurrentCulture) : text;
        if (FinalPeriod != Remove) return result;
        // Only the last ASCII period is a target; trailing whitespace, ellipses, ? and ! are kept.
        var end = result.Length - 1;
        while (end >= 0 && char.IsWhiteSpace(result[end])) end--;
        return end > 0 && result[end] == '.' && result[end - 1] != '.' ? result.Remove(end, 1) : result;
    }
}

/// <summary>One app's formatting snapshot. <see cref="AppId"/> is the process id Muesli records for the paste target.</summary>
public sealed record DictationAppFormattingRule(string AppId, string AppName, DictationAppFormatting Formatting = null!, string? TemplateId = null)
{
    public DictationAppFormatting Formatting { get; init; } = Formatting ?? DictationAppFormatting.Original;

    public static string Apply(string text, string? appId, IReadOnlyList<DictationAppFormattingRule> rules) =>
        Find(appId, rules)?.Formatting.Apply(text) ?? text;

    public static IReadOnlyList<DictationAppFormattingRule> ActiveRules(IEnumerable<DictationAppFormattingRule> rules) =>
        rules.Where(rule => !string.IsNullOrEmpty(rule.AppId) && !rule.Formatting.IsOriginal).ToList();

    /// <summary>
    /// Locked at recording start: a live session can't format text already typed if focus later moves,
    /// so live insertion is skipped only when the starting app has an active rule.
    /// </summary>
    public static bool ShouldBufferStreaming(string? appId, IReadOnlyList<DictationAppFormattingRule> rules) =>
        Find(appId, rules) is { Formatting.IsOriginal: false };

    private static DictationAppFormattingRule? Find(string? appId, IReadOnlyList<DictationAppFormattingRule> rules) =>
        string.IsNullOrEmpty(appId) ? null : rules.FirstOrDefault(rule => string.Equals(rule.AppId, appId, StringComparison.OrdinalIgnoreCase));

    /// <summary>Settings boundary: malformed or unsupported entries are dropped one by one, never the whole list.</summary>
    public static IReadOnlyList<DictationAppFormattingRule> Normalize(IReadOnlyList<DictationAppFormattingRule>? rules) =>
        (rules ?? []).Where(rule => rule is not null && !string.IsNullOrWhiteSpace(rule.AppId) && rule.AppName is not null &&
                (rule.Formatting ?? DictationAppFormatting.Original).IsValid)
            .Select(rule => rule with { Formatting = rule.Formatting ?? DictationAppFormatting.Original })
            .DistinctBy(rule => rule.AppId, StringComparer.OrdinalIgnoreCase)
            .ToList();
}

/// <summary>Reusable starting points. Each app keeps its own formatting snapshot.</summary>
public sealed record DictationFormattingTemplate(string Id, string Name, DictationAppFormatting Formatting = null!)
{
    public DictationAppFormatting Formatting { get; init; } = Formatting ?? DictationAppFormatting.Original;

    public static IReadOnlyList<DictationFormattingTemplate> BuiltIns { get; } =
    [
        new("unchanged", "Original", DictationAppFormatting.Original),
        new("no_final_period", "Casual", DictationAppFormatting.NoFinalPeriod),
        new("lowercase_chat", "Lowercase chat", DictationAppFormatting.LowercaseChat),
    ];

    public const string PreviewText = "Hey Sam, I'll be there at 5. Are you coming? See you soon.";

    public static DictationFormattingTemplate? Selected(DictationAppFormattingRule rule, IReadOnlyList<DictationFormattingTemplate> saved) =>
        saved.FirstOrDefault(template => template.Id == rule.TemplateId && template.Formatting == rule.Formatting)
        ?? BuiltIns.FirstOrDefault(template => template.Formatting == rule.Formatting);

    public static string Describe(DictationAppFormatting formatting) => formatting.Preset switch
    {
        "unchanged" => "Keep the original formatting. Apps using Original do not need a saved rule.",
        "lowercase_chat" => "Lowercase messages with no final period. Keeps questions, exclamations and ellipses.",
        "no_final_period" => "Keep capitalization and drop the final period for a lighter chat style.",
        _ => "Use lowercase while keeping punctuation.",
    };

    public static IReadOnlyList<DictationFormattingTemplate> Normalize(IReadOnlyList<DictationFormattingTemplate>? templates) =>
        (templates ?? []).Where(template => template is not null && !string.IsNullOrWhiteSpace(template.Id) &&
                !string.IsNullOrWhiteSpace(template.Name) && (template.Formatting ?? DictationAppFormatting.Original).IsValid)
            .Select(template => template with { Formatting = template.Formatting ?? DictationAppFormatting.Original })
            .DistinctBy(template => template.Id)
            .ToList();
}

/// <summary>A most-used app offered for customization. Nothing is applied automatically.</summary>
public sealed record DictationFormattingSuggestion(string AppId, string AppName, int Sessions)
{
    // Deliberately narrow: a browser, terminal or unknown app never inherits a messaging rule from its name.
    private static readonly HashSet<string> ChatApps = new(["WhatsApp", "Telegram", "Discord", "Signal"], StringComparer.OrdinalIgnoreCase);

    public bool SuggestsLowercaseChat => ChatApps.Contains(AppId);

    public string Caption => $"{Sessions} dictations · " + (SuggestsLowercaseChat
        ? "Try lowercase chat, with no final period"
        : "Keep capitalization or customize punctuation");

    public static IReadOnlyList<DictationFormattingSuggestion> Ranked(
        IEnumerable<(string AppId, string AppName)> recentDictationApps,
        IEnumerable<DictationAppFormattingRule> excluding,
        int limit = 5)
    {
        var excluded = excluding.Select(rule => rule.AppId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return recentDictationApps
            .Where(app => !string.IsNullOrWhiteSpace(app.AppId) && !excluded.Contains(app.AppId))
            .GroupBy(app => app.AppId, StringComparer.OrdinalIgnoreCase)
            .Select(group => new DictationFormattingSuggestion(group.Key, group.First().AppName, group.Count()))
            .OrderByDescending(suggestion => suggestion.Sessions)
            .ThenBy(suggestion => suggestion.AppId, StringComparer.Ordinal)
            .Take(limit)
            .ToList();
    }
}

/// <summary>macOS MuesliController+DictationFormatting: the settings mutations, pure so they can be tested.</summary>
public static class DictationFormattingEdits
{
    public static IReadOnlyList<DictationAppFormattingRule> AddApp(IReadOnlyList<DictationAppFormattingRule> rules, string appId, string name) =>
        string.IsNullOrWhiteSpace(appId) || rules.Any(rule => string.Equals(rule.AppId, appId, StringComparison.OrdinalIgnoreCase))
            ? rules
            : [.. rules, new DictationAppFormattingRule(appId.Trim(), name)];

    public static IReadOnlyList<DictationAppFormattingRule> RemoveApp(IReadOnlyList<DictationAppFormattingRule> rules, string appId) =>
        rules.Where(rule => !string.Equals(rule.AppId, appId, StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>A custom edit detaches the template; returning to Original removes the rule.</summary>
    public static IReadOnlyList<DictationAppFormattingRule> Update(
        IReadOnlyList<DictationAppFormattingRule> rules, string appId, Func<DictationAppFormatting, DictationAppFormatting> mutate) =>
        rules.Select(rule => string.Equals(rule.AppId, appId, StringComparison.OrdinalIgnoreCase)
                ? rule with { Formatting = mutate(rule.Formatting), TemplateId = null }
                : rule)
            .Where(rule => !string.Equals(rule.AppId, appId, StringComparison.OrdinalIgnoreCase) || !rule.Formatting.IsOriginal)
            .ToList();

    public static IReadOnlyList<DictationAppFormattingRule> ApplyTemplate(
        IReadOnlyList<DictationAppFormattingRule> rules, string appId, DictationFormattingTemplate template) =>
        template.Formatting.IsOriginal
            ? RemoveApp(rules, appId)
            : rules.Select(rule => string.Equals(rule.AppId, appId, StringComparison.OrdinalIgnoreCase)
                ? rule with { Formatting = template.Formatting, TemplateId = template.Id }
                : rule).ToList();

    /// <summary>Returns the new template, or an error the dialog shows (the macOS strings).</summary>
    public static (DictationFormattingTemplate? Template, string? Error) SaveTemplate(
        string name, DictationAppFormattingRule rule, IReadOnlyList<DictationAppFormattingRule> rules, IReadOnlyList<DictationFormattingTemplate> saved)
    {
        name = name.Trim();
        if (name.Length == 0) return (null, "Give this template a name.");
        if (DictationFormattingTemplate.BuiltIns.Concat(saved).Any(template => string.Equals(template.Name, name, StringComparison.OrdinalIgnoreCase)))
            return (null, "A template with that name already exists.");
        var current = rules.FirstOrDefault(candidate => string.Equals(candidate.AppId, rule.AppId, StringComparison.OrdinalIgnoreCase));
        if (rule.Formatting.IsOriginal || current?.Formatting != rule.Formatting)
            return (null, "This app's formatting changed. Close this window and try again.");
        return (new DictationFormattingTemplate("saved_" + Guid.NewGuid().ToString("N"), name, rule.Formatting), null);
    }

    /// <summary>Removing a template keeps the formatting already applied to apps.</summary>
    public static IReadOnlyList<DictationAppFormattingRule> DetachTemplate(IReadOnlyList<DictationAppFormattingRule> rules, string templateId) =>
        rules.Select(rule => rule.TemplateId == templateId ? rule with { TemplateId = null } : rule).ToList();
}
