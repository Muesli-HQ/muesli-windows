namespace Muesli.Windows.Services;

public static class LiveTranscriptCopyContent
{
    // Keep clipboard content identical to LiveTranscriptCopyContent in the macOS app.
    public static string Text(string transcript, string partialYou, string partialOthers) => string.Join("\n",
        new[] { transcript.Trim(), partialOthers.Trim().Length == 0 ? "" : $"Others: {partialOthers.Trim()}",
            partialYou.Trim().Length == 0 ? "" : $"You: {partialYou.Trim()}" }.Where(value => value.Length > 0));
}
