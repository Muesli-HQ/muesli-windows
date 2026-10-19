using System.Text.Json;
using System.Text.Json.Serialization;

namespace Muesli.Windows.ComputerUse;

/// <summary>
/// Window state sent to the Computer Use planner. Mirrors the macOS `ComputerUseWindowState`
/// (snake_case JSON) so both apps share one planner contract.
/// </summary>
public sealed record CuaRect(double X, double Y, double Width, double Height)
{
    public double CenterX => X + Width / 2;
    public double CenterY => Y + Height / 2;
}

public sealed record CuaElement(
    string ElementId,
    int ElementIndex,
    string Role,
    string Title,
    string Label,
    string Value,
    string Help,
    bool Enabled,
    CuaRect? Frame,
    string Path,
    IReadOnlyList<string> ActionNames,
    int? ProcessId);

public sealed record CuaFocusedElement(string Role, string Title, string Label, string Value, CuaRect? Frame, int? ProcessId);

public sealed record CuaScreenshot(string ScreenshotId, int Width, int Height, CuaRect WindowFrame, double ScaleX, double ScaleY)
{
    /// <summary>Sent to the model as an image input, never inside the JSON prompt.</summary>
    [JsonIgnore] public string? ImageDataUrl { get; init; }
}

public sealed record CuaWindowState(
    string StateId,
    string AppName,
    string AppId,
    long WindowId,
    string WindowTitle,
    CuaRect? WindowFrame,
    CuaScreenshot? Screenshot,
    CuaRect? CursorPosition,
    CuaFocusedElement? FocusedElement,
    string? SelectedText,
    string? DocumentText,
    string? VisibleText,
    IReadOnlyList<CuaElement> Elements,
    DateTimeOffset CapturedAt)
{
    public static string NewStateId() => "s" + Guid.NewGuid().ToString("N")[..10];

    public static CuaWindowState Empty(string reason) => new(NewStateId(), reason, "", 0, "", null, null, null, null, null, null, null,
        Array.Empty<CuaElement>(), DateTimeOffset.UtcNow);
}

public static class CuaJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };
}
