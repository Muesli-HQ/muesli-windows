using System.IO;
using Microsoft.Win32;

namespace Muesli.Windows.Services;

public static class StartupRegistrationService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "Muesli";

    public static bool IsEnabled()
    {
        return !string.IsNullOrWhiteSpace(GetRegisteredCommand());
    }

    public static string? GetRegisteredCommand()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(AppName) as string;
    }

    public static bool IsRegisteredForBackgroundLaunch()
    {
        return IsCommandForExecutable(GetRegisteredCommand(), Environment.ProcessPath);
    }

    public static string DescribeState()
    {
        var command = GetRegisteredCommand();
        if (string.IsNullOrWhiteSpace(command)) return "Disabled in Windows";
        return command.Contains("--background", StringComparison.OrdinalIgnoreCase)
            ? "Enabled for background launch"
            : "Enabled, but needs background-launch repair";
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                        ?? Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        if (key is null)
        {
            throw new InvalidOperationException("Could not open the Windows startup registry key.");
        }

        if (!enabled)
        {
            key.DeleteValue(AppName, throwOnMissingValue: false);
            return;
        }

        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            throw new InvalidOperationException("Could not determine the Muesli executable path.");
        }

        key.SetValue(AppName, BuildBackgroundCommand(executablePath), RegistryValueKind.String);
    }

    internal static bool IsCommandForExecutable(string? command, string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(command) || string.IsNullOrWhiteSpace(executablePath))
        {
            return false;
        }

        return string.Equals(
            command.Trim(),
            BuildBackgroundCommand(executablePath),
            StringComparison.OrdinalIgnoreCase);
    }

    internal static string BuildBackgroundCommand(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            throw new ArgumentException("The Muesli executable path is required.", nameof(executablePath));
        }

        return $"\"{Path.GetFullPath(executablePath)}\" --background";
    }
}
