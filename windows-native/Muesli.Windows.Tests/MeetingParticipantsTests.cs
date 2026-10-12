using System.Net;
using Muesli.Windows.Services;

namespace Muesli.Windows.Tests;

public sealed class MeetingParticipantsTests
{
    [Fact]
    public void PeopleArePerMeetingDeduplicatedAndRemovedWithTheMeeting()
    {
        var root = Directory.CreateTempSubdirectory("muesli-people").FullName;
        try
        {
            MeetingParticipants.Add(root, "m1", "  Ada   Lovelace ", "ada@example.com");
            MeetingParticipants.Add(root, "m1", "Ada L", "ADA@example.com");
            var people = MeetingParticipants.Add(root, "m1", "", "grace@example.com");
            Assert.Equal(["Ada Lovelace", "grace@example.com"], people.Select(person => person.Name));
            Assert.Equal("grace", people[1].DisplayName);
            Assert.Empty(MeetingParticipants.Load(root, "m2"));

            Assert.Single(MeetingParticipants.Remove(root, "m1", people[0].Id));
            MeetingParticipants.RemoveMeeting(root, "m1");
            Assert.Empty(MeetingParticipants.Load(root, "m1"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void PromptRosterSkipsEmailsAndEscapesNames()
    {
        Assert.Equal(["Ada", "B<b>"], MeetingParticipants.NamesForPrompt(["Ada", "ada", "x@y.com", "123", "B<b>"]));
        Assert.Equal("- <participant_name>B&lt;b&gt;</participant_name>", MeetingParticipants.PromptLine("B<b>"));
        Assert.Equal(200, MeetingParticipants.NamesForPrompt([new string('a', 500)])[0].Length);
    }

    [Fact]
    public async Task HostedSummaryReceivesRoster()
    {
        string? body = null;
        using var client = new HttpClient(new CaptureHandler(request => body = request));
        var settings = new MuesliSettings { MeetingSummaryProvider = "ollama", OllamaEndpoint = "http://localhost:11434", OllamaModel = "llama3" };
        await MeetingSummaryService.CreateSummaryResultAsync("We shipped it.", "Sync", settings, httpClient: client,
            participantNames: ["Ada Lovelace"]);
        using var json = System.Text.Json.JsonDocument.Parse(body!);
        var messages = json.RootElement.GetProperty("messages");
        Assert.Contains("A participant roster may also be provided.", messages[0].GetProperty("content").GetString());
        Assert.Contains("<participant_name>Ada Lovelace</participant_name>", messages[1].GetProperty("content").GetString());
    }

    private sealed class CaptureHandler(Action<string> capture) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            capture(await request.Content!.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"message":{"content":"## Summary\nShipped."}}""") };
        }
    }
}
