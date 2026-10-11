using System.Text;
using Muesli.Windows.Services;

namespace Muesli.Windows.ComputerUse;

/// <summary>
/// Muesli settings a voice command may change, ported from macOS <c>ComputerUseSettings</c>:
/// only registered choice settings cross the planner boundary, never keys, paths, prompts or
/// free text. Changes go through the app's own load/save so the running app applies them.
/// </summary>
public sealed class CuaMuesliSettings(Func<MuesliSettings> load, Action<MuesliSettings> save)
{
    private sealed record Choice(string Id, string Label, string? Unavailable = null);

    private sealed record Setting(string Id, string Label, Func<MuesliSettings, string> Get,
        Func<MuesliSettings, string, MuesliSettings> Set, Func<IEnumerable<Choice>> Choices);

    private static readonly Choice[] OnOff = [new("on", "On"), new("off", "Off")];

    private static readonly Setting[] Settings =
    [
        new("dictation_model", "Dictation model", s => s.DictationModelId, (s, v) => s with { DictationModelId = v }, Models),
        new("meeting_model", "Meeting transcription model", s => s.FinalMeetingModelId, (s, v) => s with { FinalMeetingModelId = v }, Models),
        new("theme", "App theme", s => s.Theme, (s, v) => s with { Theme = v },
            () => [new("system", "Match Windows"), new("light", "Light"), new("dark", "Dark")]),
        new("indicator_style", "Recording indicator style", s => s.IndicatorStyle, (s, v) => s with { IndicatorStyle = v },
            () => [new("classic", "Classic"), new("minimal", "Minimal")]),
        Toggle("floating_indicator", "Floating recording indicator", s => s.ShowFloatingIndicator, (s, v) => s with { ShowFloatingIndicator = v }),
        Toggle("sounds", "Sound effects", s => s.SoundEnabled, (s, v) => s with { SoundEnabled = v }),
        Toggle("remove_filler_words", "Remove filler words", s => s.RemoveFillerWords, (s, v) => s with { RemoveFillerWords = v }),
        Toggle("double_tap_dictation", "Double-tap hands-free dictation", s => s.EnableDoubleTapDictation, (s, v) => s with { EnableDoubleTapDictation = v }),
        Toggle("meeting_detection", "Automatic meeting detection", s => s.AutoMeetingDetectionEnabled, (s, v) => s with { AutoMeetingDetectionEnabled = v }),
    ];

    private static Setting Toggle(string id, string label, Func<MuesliSettings, bool> get, Func<MuesliSettings, bool, MuesliSettings> set) =>
        new(id, label, s => get(s) ? "on" : "off", (s, v) => set(s, v == "on"), () => OnOff);

    /// <summary>Only downloaded models: local transcription must never switch to a missing model.</summary>
    private static IEnumerable<Choice> Models() =>
        TranscriptionModelCatalog.Models.Select(model =>
            new Choice(model.Id, model.PickerLabel, model.IsCached ? null : "not downloaded; download it in Muesli Settings first"));

    public CuaResult Execute(CuaToolCall call) => call.Name switch
    {
        "get_muesli_settings" => CuaResult.Ok(Describe(load())),
        "set_muesli_setting" => Set(call.String("setting") ?? "", call.String("value") ?? ""),
        _ => new CuaResult(CuaResultStatus.Unsupported, $"{call.Name} is not a settings tool."),
    };

    private static string Describe(MuesliSettings current)
    {
        var text = new StringBuilder("Muesli settings you can change (setting: current value; choices as id = label):\n");
        foreach (var setting in Settings)
        {
            text.Append($"{setting.Id} ({setting.Label}): {setting.Get(current)}; choices: ");
            text.AppendJoin(", ", setting.Choices().Select(choice =>
                $"{choice.Id} = {choice.Label}{(choice.Unavailable is null ? "" : $" [{choice.Unavailable}]")}"));
            text.Append('\n');
        }
        return text.ToString();
    }

    private CuaResult Set(string settingId, string value)
    {
        var setting = Settings.FirstOrDefault(s => s.Id.Equals(settingId, StringComparison.OrdinalIgnoreCase));
        if (setting is null) return CuaResult.Fail($"\"{settingId}\" cannot be changed by voice. Call get_muesli_settings for the list.");
        var choices = setting.Choices().ToArray();
        var choice = choices.FirstOrDefault(c => c.Id.Equals(value, StringComparison.OrdinalIgnoreCase))
            ?? choices.FirstOrDefault(c => c.Label.Equals(value, StringComparison.OrdinalIgnoreCase));
        if (choice is null) return CuaResult.Fail($"\"{value}\" is not a choice for {setting.Label}. Choices: {string.Join(", ", choices.Select(c => c.Id))}. Nothing was changed.");
        if (choice.Unavailable is not null) return CuaResult.Fail($"{choice.Label} is {choice.Unavailable}. Nothing was changed.");

        var current = load();
        if (setting.Get(current) == choice.Id) return CuaResult.Ok($"{setting.Label} is already {choice.Label}.");
        save(setting.Set(current, choice.Id));
        return setting.Get(load()) == choice.Id
            ? CuaResult.Ok($"{setting.Label} changed to {choice.Label}.")
            : CuaResult.Fail($"{setting.Label} did not save. Nothing was changed.");
    }
}
