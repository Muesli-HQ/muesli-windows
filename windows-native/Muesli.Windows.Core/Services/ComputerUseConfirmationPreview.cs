using System.Text;

namespace Muesli.Windows.Services;

/// <summary>
/// Local, never-persisted confirmation text. The preview is shown to the user and then discarded.
/// </summary>
public static class ComputerUseConfirmationPreview
{
    public static string Build(ComputerUseAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var builder = new StringBuilder();
        builder.AppendLine($"Application: {Safe(action.Target.ApplicationId)}");
        builder.AppendLine($"Browser origin: {Safe(action.Target.BrowserDomain)}");
        builder.AppendLine($"UI Automation target: {Safe(action.Target.AutomationId)}");
        builder.AppendLine($"Action: {action.Kind}");
        builder.AppendLine();
        builder.AppendLine(action.Kind switch
        {
            ComputerUseActionKind.SetText => $"Exact text to enter:\n{Safe(action.Value)}",
            ComputerUseActionKind.BrowserNavigate => $"Exact URL to open:\n{Safe(action.Value)}",
            ComputerUseActionKind.BrowserInvoke => $"Exact data-muesli-target to invoke:\n{Safe(action.Target.AutomationId)}",
            ComputerUseActionKind.InvokeElement => $"Exact AutomationId to invoke:\n{Safe(action.Target.AutomationId)}",
            _ => "No text or navigation value will be sent."
        });
        return builder.ToString();
    }

    private static string Safe(string? value) => string.IsNullOrEmpty(value)
        ? "(none)"
        : new string(value.Select(character =>
            char.IsControl(character) && character is not ('\r' or '\n' or '\t') ? '\uFFFD' : character).ToArray());
}
