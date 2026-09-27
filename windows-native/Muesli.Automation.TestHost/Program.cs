using System.Diagnostics;
using System.Text;
using System.Text.Json;

Console.InputEncoding = new UTF8Encoding(false);
Console.OutputEncoding = new UTF8Encoding(false);

if (args.Length == 2 && args[0] == "--descendant")
{
    await File.WriteAllTextAsync(args[1], Environment.ProcessId.ToString());
    await Task.Delay(TimeSpan.FromMinutes(2));
    return 0;
}

// Used to prove that even code which runs before stdin is read starts inside the Job Object.
// A vulnerable Start-then-Assign launcher allows this descendant to escape the later assignment.
var immediateSpawnSidecar = Environment.ProcessPath + ".immediate-spawn";
if (File.Exists(immediateSpawnSidecar))
{
    var markerPath = await File.ReadAllTextAsync(immediateSpawnSidecar);
    _ = Process.Start(new ProcessStartInfo
    {
        FileName = Environment.ProcessPath!,
        UseShellExecute = false,
        ArgumentList = { "--descendant", markerPath }
    });
    for (var attempt = 0; attempt < 100 && !File.Exists(markerPath); attempt++)
        await Task.Delay(10);
    await Task.Delay(TimeSpan.FromMinutes(2));
    return 0;
}

var input = await Console.In.ReadToEndAsync();
using var document = JsonDocument.Parse(input);
var meeting = document.RootElement.GetProperty("meeting");
var id = meeting.GetProperty("id").GetString() ?? "";
var title = meeting.GetProperty("title").GetString() ?? "";

if (id.Contains("parent-exit-child", StringComparison.OrdinalIgnoreCase))
{
    var markerPath = Path.Combine(Path.GetTempPath(), $"muesli-child-{id}.txt");
    var child = Process.Start(new ProcessStartInfo
    {
        FileName = Environment.ProcessPath!,
        UseShellExecute = false,
        ArgumentList = { "--descendant", markerPath }
    })!;
    for (var attempt = 0; attempt < 100 && !File.Exists(markerPath); attempt++)
        await Task.Delay(10);
    Console.WriteLine($"CHILD_PID={child.Id}");
    Console.WriteLine($"CHILD_MARKER={markerPath}");
    Console.Out.Flush();
    return 0;
}

if (id.Contains("spawn", StringComparison.OrdinalIgnoreCase))
{
    var markerPath = Path.Combine(Path.GetTempPath(), $"muesli-child-{id}.txt");
    var child = Process.Start(new ProcessStartInfo
    {
        FileName = Environment.ProcessPath!,
        UseShellExecute = false,
        ArgumentList = { "--descendant", markerPath }
    })!;
    for (var attempt = 0; attempt < 100 && !File.Exists(markerPath); attempt++)
        await Task.Delay(10);
    Console.WriteLine($"CHILD_PID={child.Id}");
    Console.WriteLine($"CHILD_MARKER={markerPath}");
    Console.Out.Flush();
    await Task.Delay(TimeSpan.FromMinutes(2));
    return 0;
}

if (id.Contains("timeout", StringComparison.OrdinalIgnoreCase))
{
    await Task.Delay(TimeSpan.FromMinutes(2));
    return 0;
}

if (id.Contains("crash", StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine("password=host-crash-password");
    return 17;
}

if (id.Contains("oversized", StringComparison.OrdinalIgnoreCase))
{
    Console.Out.Write(new string('O', 200_000));
    Console.Error.Write(new string('E', 200_000));
    return 0;
}

if (id.Contains("sensitive-first", StringComparison.OrdinalIgnoreCase))
{
    var transcript = document.RootElement.GetProperty("transcript").GetProperty("data").GetString() ?? "";
    var manual = document.RootElement.GetProperty("notes").GetProperty("manual").GetString() ?? "";
    Console.Out.Write(transcript);
    Console.Error.Write(manual);
    return 0;
}

Console.WriteLine($"TITLE={title}");
Console.WriteLine(input);
Console.WriteLine("api_key=host-secret-123456");
return 0;
