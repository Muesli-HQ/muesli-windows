using System;
using System.IO;
using Muesli.Windows.Services;
using Xunit;

namespace Muesli.Windows.Tests;

/// <summary>MAC-04: a template chosen while recording survives the recorded-meeting merge and is locked at Stop.</summary>
public sealed class RecordingTemplateTests
{
    private static PersistedMeeting Meeting(string template) => new()
    {
        SchemaVersion = AppDataStore.CurrentMeetingSchemaVersion,
        Id = "m1", Title = "Sync", CreatedAt = DateTime.Now, TemplateName = template
    };

    [Fact]
    public void Draft_template_survives_the_recorded_merge()
    {
        Assert.Equal("1:1", MeetingNotesComposer.ApplyRecordedMeeting(Meeting(""), Meeting("1:1")).TemplateName);
        Assert.Equal("Standup", MeetingNotesComposer.ApplyRecordedMeeting(Meeting("Standup"), Meeting("")).TemplateName);
    }

    [Fact]
    public void Stop_locks_the_template_before_capture_stops_and_finalization_uses_it()
    {
        var source = File.ReadAllText(Path.Combine(TestRepositoryLayout.Root, "windows-native", "Muesli.Windows.WinUI",
            "Services", "WinUiMeetingContext.cs"));
        var stop = source.IndexOf("private async Task<PersistedMeeting> StopAndSaveCoreAsync", StringComparison.Ordinal);
        var locked = source.IndexOf("LockRecordingTemplate(activeId)", stop, StringComparison.Ordinal);
        var capture = source.IndexOf("_coordinator.StopAsync(", stop, StringComparison.Ordinal);
        Assert.True(locked > stop && locked < capture);
        Assert.Contains("FinalizeMeetingAsync(persisted, cancellationToken, template)", source, StringComparison.Ordinal);
        // The fallback: the last choice saved this session when the library can't be read at Stop.
        Assert.Contains("var template = _recordingTemplate;", source, StringComparison.Ordinal);
        Assert.Contains("The template is locked once recording stops.", source, StringComparison.Ordinal);
    }
}
