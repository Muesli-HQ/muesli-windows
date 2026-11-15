using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Muesli.Windows.Services;

public enum ClaudeCodeStatus
{
    /// <summary>No Claude Code installation was found on this PC.</summary>
    Unavailable,
    SignedIn,
    SignedOut,
    /// <summary>The CLI is installed but did not say whether it is signed in.</summary>
    Unknown
}

/// <summary>
/// Meeting notes through the Claude Code installation already on this PC, a port of the macOS
/// <c>ClaudeCodeSummarizer</c>. Muesli runs the user's own <c>claude</c> in print mode with tools,
/// slash commands, MCP servers and session history switched off, hands it the prompt on standard
/// input, and reads one JSON result. It uses whatever account Claude Code is signed in to.
/// </summary>
public static partial class ClaudeCodeSummarizer
{
    public static readonly TimeSpan SummaryTimeout = TimeSpan.FromSeconds(300);
    public static readonly TimeSpan TitleTimeout = TimeSpan.FromSeconds(120);
    private const int MaxInputBytes = 10_000_000;
    private const int MaxOutputChars = 5_000_000;

    /// <summary>The macOS model choices; an empty id follows Claude Code's own setting.</summary>
    public static IReadOnlyList<(string Id, string Label)> Models { get; } =
    [
        ("", "Follow Claude Code settings"),
        ("sonnet", "Claude Sonnet"),
        ("opus", "Claude Opus"),
        ("haiku", "Claude Haiku")
    ];

    [GeneratedRegex(@"^[A-Za-z0-9._:\-\[\]]{1,100}$")]
    private static partial Regex ModelPattern();

    /// <summary>
    /// Finds the Claude Code CLI: the configured path if it exists, otherwise every PATH folder and
    /// the usual install folders, without going through a shell.
    /// </summary>
    public static string? LocateExecutable(string? configuredPath = null)
    {
        var configured = Environment.ExpandEnvironmentVariables(configuredPath?.Trim() ?? "");
        if (configured.Length > 0)
        {
            return Path.IsPathFullyQualified(configured) && File.Exists(configured) ? configured : null;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var folders = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Concat(
            [
                Path.Combine(home, ".local", "bin"),
                Path.Combine(home, ".claude", "local"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "claude")
            ]);
        foreach (var folder in folders)
        {
            foreach (var name in new[] { "claude.exe", "claude.cmd" })
            {
                try
                {
                    var candidate = Path.Combine(folder, name);
                    if (File.Exists(candidate)) return candidate;
                }
                catch (ArgumentException)
                {
                }
            }
        }
        return null;
    }

    /// <summary>The arguments of one notes run. The prompt is never an argument; it goes on stdin.</summary>
    public static IReadOnlyList<string> BuildArguments(string? model)
    {
        var arguments = new List<string>
        {
            "-p", "--output-format", "json", "--no-session-persistence", "--disable-slash-commands",
            "--strict-mcp-config", "--setting-sources", "user"
        };
        var trimmed = model?.Trim() ?? "";
        if (trimmed.Length > 0 && ModelPattern().IsMatch(trimmed))
        {
            arguments.Add("--model");
            arguments.Add(trimmed);
        }
        arguments.Add("--tools");
        arguments.Add("");
        return arguments;
    }

    /// <summary><c>claude auth status</c>: exit 0 is signed in, 1 is signed out.</summary>
    public static async Task<ClaudeCodeStatus> CheckStatusAsync(string? configuredPath = null, CancellationToken cancellationToken = default)
    {
        var executable = LocateExecutable(configuredPath);
        if (executable is null) return ClaudeCodeStatus.Unavailable;
        try
        {
            var run = await RunProcessAsync(executable, ["auth", "status"], "", TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            return run.TimedOut ? ClaudeCodeStatus.Unknown : run.ExitCode switch
            {
                0 => ClaudeCodeStatus.SignedIn,
                1 => ClaudeCodeStatus.SignedOut,
                _ => ClaudeCodeStatus.Unknown
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return ClaudeCodeStatus.Unknown;
        }
    }

    /// <summary>Runs one prompt and returns Claude Code's answer text.</summary>
    public static async Task<string> RunAsync(
        string instructions, string input, string? model, string? configuredPath, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var executable = LocateExecutable(configuredPath) ?? throw new SummaryProviderException("claude-code-not-found");
        // Windows caps a command line at 32,767 characters and a .cmd launcher re-parses its
        // arguments, so the instructions travel with the transcript on standard input.
        var prompt = $"{instructions}\n\n{input}";
        if (Encoding.UTF8.GetByteCount(prompt) > MaxInputBytes) throw new SummaryProviderException("input-too-large");
        var run = await RunProcessAsync(executable, BuildArguments(model), prompt, timeout, cancellationToken).ConfigureAwait(false);
        if (run.TimedOut) throw new SummaryProviderException("timeout");
        return ParseResult(run.Output, run.ExitCode);
    }

    /// <summary>Reads the <c>"type":"result"</c> object of a print-mode run.</summary>
    public static string ParseResult(string output, int exitCode)
    {
        JsonElement? result = null;
        foreach (var candidate in Candidates(output))
        {
            if (candidate.ValueKind == JsonValueKind.Object &&
                candidate.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == "result")
            {
                result = candidate;
            }
        }
        if (result is not { } found) throw new SummaryProviderException(exitCode == 0 ? "malformed-response" : "claude-code-failed");

        var subtype = found.TryGetProperty("subtype", out var subtypeValue) && subtypeValue.ValueKind == JsonValueKind.String
            ? subtypeValue.GetString() ?? "" : "";
        var isError = found.TryGetProperty("is_error", out var errorValue) && errorValue.ValueKind == JsonValueKind.True;
        if (isError || exitCode != 0 || subtype.StartsWith("error_", StringComparison.Ordinal) || subtype is not ("" or "success"))
            throw new SummaryProviderException("claude-code-failed");

        var text = found.TryGetProperty("result", out var resultValue) && resultValue.ValueKind == JsonValueKind.String
            ? resultValue.GetString()?.Trim() ?? "" : "";
        return text.Length == 0 ? throw new SummaryProviderException("empty-response") : text;
    }

    // One JSON value, an array of events, or one event per line.
    private static IEnumerable<JsonElement> Candidates(string output)
    {
        var trimmed = output.Trim();
        if (trimmed.Length == 0) yield break;
        JsonElement? whole = null;
        try { whole = JsonDocument.Parse(trimmed).RootElement.Clone(); }
        catch (JsonException) { }
        if (whole is { } root)
        {
            if (root.ValueKind == JsonValueKind.Array) foreach (var item in root.EnumerateArray()) yield return item;
            else yield return root;
            yield break;
        }
        foreach (var line in trimmed.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            JsonElement? parsed = null;
            try { parsed = JsonDocument.Parse(line).RootElement.Clone(); }
            catch (JsonException) { }
            if (parsed is { } element) yield return element;
        }
    }

    private sealed record ProcessRun(int ExitCode, string Output, bool TimedOut);

    private static async Task<ProcessRun> RunProcessAsync(
        string executable, IReadOnlyList<string> arguments, string standardInput, TimeSpan timeout, CancellationToken cancellationToken)
    {
        // An empty private folder as the working directory: no project files, settings or history
        // of another project can reach the run.
        var workingDirectory = Path.Combine(Path.GetTempPath(), $"muesli-claude-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workingDirectory);
        try
        {
            var start = new ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);

            using var process = Process.Start(start) ?? throw new SummaryProviderException("claude-code-failed");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeout);
            try
            {
                var output = ReadCappedAsync(process.StandardOutput, deadline.Token);
                var error = ReadCappedAsync(process.StandardError, deadline.Token);
                await process.StandardInput.WriteAsync(standardInput.AsMemory(), deadline.Token).ConfigureAwait(false);
                process.StandardInput.Close();
                await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
                _ = await error.ConfigureAwait(false);
                return new ProcessRun(process.ExitCode, await output.ConfigureAwait(false), false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (Exception) { }
                if (cancellationToken.IsCancellationRequested) throw;
                return new ProcessRun(-1, "", true);
            }
            catch (IOException)
            {
                try { process.Kill(entireProcessTree: true); } catch (Exception) { }
                throw new SummaryProviderException("claude-code-failed");
            }
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new SummaryProviderException("claude-code-not-found");
        }
        finally
        {
            try { Directory.Delete(workingDirectory, recursive: true); } catch (Exception) { }
        }
    }

    private static async Task<string> ReadCappedAsync(StreamReader reader, CancellationToken token)
    {
        var builder = new StringBuilder();
        var buffer = new char[8192];
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) > 0)
        {
            if (builder.Length + read > MaxOutputChars) throw new IOException("Claude Code produced too much output.");
            builder.Append(buffer, 0, read);
        }
        return builder.ToString();
    }
}
