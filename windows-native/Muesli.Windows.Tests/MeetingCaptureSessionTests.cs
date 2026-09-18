using Muesli.Windows.Services;

namespace Muesli.Windows.Tests;

public sealed class MeetingCaptureSessionTests
{
    [Fact]
    public void MergeLiveResultsOffsetsResumeSegmentsAndGapsOntoPriorAudio()
    {
        var prior = new MeetingLiveTranscriptionResult(
            [
                new LiveTranscriptSegment("a", LiveTranscriptChannel.Microphone, 0, 8000, "hello")
            ],
            [
                new LiveTranscriptGap(LiveTranscriptChannel.System, 1000, 2000, "drop")
            ],
            2,
            "live-model",
            LiveTranscriptOwnershipMode.UnifiedLiveAndFinal);
        var current = new MeetingLiveTranscriptionResult(
            [
                new LiveTranscriptSegment("b", LiveTranscriptChannel.System, 0, 4000, "there")
            ],
            [
                new LiveTranscriptGap(LiveTranscriptChannel.Microphone, 500, long.MaxValue, "open")
            ],
            3,
            "live-model",
            LiveTranscriptOwnershipMode.UnifiedLiveAndFinal);

        var merged = MeetingCaptureSession.MergeLiveResults(prior, current);

        Assert.Equal(2, merged.Committed.Count);
        Assert.Equal(8000, merged.Committed[0].EndSample);
        Assert.Equal(8000, merged.Committed[1].StartSample);
        Assert.Equal(12000, merged.Committed[1].EndSample);
        Assert.StartsWith("resume_1_", merged.Committed[1].Id, StringComparison.Ordinal);
        Assert.Equal(5, merged.DroppedPackets);
        var openGap = Assert.Single(merged.Gaps, gap => gap.EndSample == long.MaxValue);
        Assert.Equal(8500, openGap.StartSample);
    }

    [Fact]
    public void CaptureAndCoordinatorCallbacksStayOffTheWpfDispatcher()
    {
        var captureSource = File.ReadAllText(SourcePath("MeetingCaptureSession.cs"));
        var coordinatorSource = File.ReadAllText(SourcePath("MeetingRecordingCoordinator.cs"));

        Assert.DoesNotContain("Dispatcher", captureSource, StringComparison.Ordinal);
        Assert.DoesNotContain("BeginInvoke", captureSource, StringComparison.Ordinal);
        Assert.DoesNotContain("Dispatcher", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("Task.Run(FlushLiveCheckpointAsync)", captureSource, StringComparison.Ordinal);
        Assert.Contains("TryEnqueue", captureSource, StringComparison.Ordinal);
    }

    [Fact]
    public void LiveCheckpointFlushIsCoalescedOntoABackgroundTask()
    {
        var source = File.ReadAllText(SourcePath("MeetingCaptureSession.cs"));
        Assert.Contains("Interlocked.Exchange(ref _liveCheckpointScheduled, 1)", source, StringComparison.Ordinal);
        Assert.Contains("_ = Task.Run(FlushLiveCheckpointAsync)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Application.Current", source, StringComparison.Ordinal);
    }

    private static string SourcePath(string fileName)
    {
        var extra = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "projects",
            "muesli",
            "windows-native",
            "Muesli.Windows",
            "Services",
            fileName);
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory, extra })
        {
            if (File.Exists(start) && start.EndsWith(fileName, StringComparison.OrdinalIgnoreCase))
            {
                return start;
            }

            var directory = new DirectoryInfo(start);
            if (!directory.Exists)
            {
                continue;
            }

            while (directory is not null)
            {
                var candidates = new[]
                {
                    Path.Combine(directory.FullName, "Muesli.Windows", "Services", fileName),
                    Path.Combine(directory.FullName, "windows-native", "Muesli.Windows", "Services", fileName),
                    Path.Combine(directory.FullName, "Muesli.Windows.Platform", "Services", fileName),
                    Path.Combine(directory.FullName, "windows-native", "Muesli.Windows.Platform", "Services", fileName)
                };
                var match = candidates.FirstOrDefault(File.Exists);
                if (match is not null)
                {
                    return match;
                }
                directory = directory.Parent;
            }
        }

        throw new FileNotFoundException($"Could not locate {fileName} from the test output or working directory.");
    }
}
