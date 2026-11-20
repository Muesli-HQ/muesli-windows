using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using Muesli.Windows.Cotypist;
using Muesli.Windows.Services;
using Muesli.Windows.Services.Persistence;

namespace Muesli.Windows.Tests;

public sealed class CotypistPipeTests
{
    private static async Task<string> SendAsync(string name, string line)
    {
        await using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(5_000);
        try
        {
            await client.WriteAsync(Encoding.UTF8.GetBytes(line + "\n"));
            await client.FlushAsync();
            using var reader = new StreamReader(client, Encoding.UTF8);
            return await reader.ReadLineAsync() ?? "";
        }
        catch (IOException)
        {
            return ""; // the server closed a refused connection
        }
    }

    [Fact]
    public async Task ServesTrustedHelpersAndRefusesEveryoneElse()
    {
        using var persistence = MuesliPersistenceStore.OpenInMemory();
        var settings = new MuesliSettings { CotypistDisabledApps = ["notes.exe"] };
        using var service = new CotypistService(() => settings, new CotypistHistoryStore(persistence.Database), _ => { });
        var trusted = true;
        var name = "muesli-cotypist-test-" + Guid.NewGuid().ToString("N");
        using var server = new CotypistPipeServer(service.HandleAsync, _ => trusted, _ => { }, name);

        var reply = JsonNode.Parse(await SendAsync(name, """{"type":"suggest","app":"notes.exe","typed":"Thanks for the"}"""))!;
        Assert.Equal("off in this app", (string?)reply["rejection"]);
        Assert.Equal(0.5, (double?)reply["midWordDelay"]);
        Assert.Equal("unknown request", (string?)JsonNode.Parse(await SendAsync(name, """{"type":"other"}"""))!["rejection"]);

        // An accepted outcome is recorded with no prior suggestion id: no sentence is invented.
        await SendAsync(name, """{"type":"outcome","app":"notes.exe","text":"next word","outcome":"accepted"}""");
        await Task.Delay(300);
        Assert.Equal("", Assert.Single(new CotypistHistoryStore(persistence.Database).Sessions()).Acceptances.Single().ContextBefore);

        trusted = false;
        Assert.Equal("", await SendAsync(name, """{"type":"suggest","app":"other.exe","typed":"hi"}"""));
    }

    [Fact]
    public void AProcessWithoutTheHelperLoadedIsNotTrusted()
    {
        Assert.False(CotypistPipeServer.HasHelperLoaded(Environment.ProcessId, Path.Combine(AppContext.BaseDirectory, CotypistAvailability.HelperFileName)));
        Assert.False(CotypistPipeServer.HasHelperLoaded(-1, "x.dll"));
    }
}
