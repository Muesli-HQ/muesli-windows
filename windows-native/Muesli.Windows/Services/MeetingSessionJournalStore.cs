using System.Buffers.Binary;
using System.IO;

namespace Muesli.Windows.Services;

public sealed record ActiveMeetingSession(
    string SessionId,
    string Title,
    DateTime StartedAt,
    int? TargetProcessId,
    string? MeetingKey,
    string RemoteAudioMode);

public sealed class MeetingSessionJournalStore
{
    private readonly string _journalPath;
    private readonly string _captureDirectory;
    private readonly AtomicJsonFile _json = new();

    public MeetingSessionJournalStore(string? dataRoot = null, string? captureDirectory = null)
    {
        var root = dataRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "muesli",
            "data");
        _journalPath = Path.Combine(root, "active-meeting-session.json");
        _captureDirectory = captureDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "muesli",
            "captures");
    }

    public void Save(ActiveMeetingSession session) => _json.Save(_journalPath, session);

    public ActiveMeetingSession? Load() =>
        _json.Load<ActiveMeetingSession?>(_journalPath, null).Value;

    public IReadOnlyList<string> DiscoverAudio(ActiveMeetingSession session)
    {
        if (!Directory.Exists(_captureDirectory) || !IsSafeSessionId(session.SessionId))
        {
            return [];
        }

        var prefix = $"meeting-{session.SessionId}-";
        var paths = Directory.EnumerateFiles(_captureDirectory, $"{prefix}*.wav")
            .Where(path => Path.GetFileName(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var path in paths)
        {
            TryRepairInterruptedWave(path);
        }
        return paths;
    }

    public void Clear()
    {
        TryDelete(_journalPath);
        TryDelete($"{_journalPath}.bak");
    }

    internal static bool TryRepairInterruptedWave(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            if (stream.Length < 12)
            {
                return false;
            }

            Span<byte> header = stackalloc byte[12];
            stream.ReadExactly(header);
            if (!header[..4].SequenceEqual("RIFF"u8) || !header[8..12].SequenceEqual("WAVE"u8))
            {
                return false;
            }

            long dataSizeOffset = -1;
            long dataOffset = -1;
            var chunkBuffer = new byte[8];
            while (stream.Position + 8 <= stream.Length)
            {
                Span<byte> chunk = chunkBuffer;
                stream.ReadExactly(chunk);
                var chunkSize = BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..8]);
                if (chunk[..4].SequenceEqual("data"u8))
                {
                    dataSizeOffset = stream.Position - 4;
                    dataOffset = stream.Position;
                    break;
                }
                stream.Position = Math.Min(stream.Length, stream.Position + chunkSize + (chunkSize & 1));
            }

            if (dataOffset < 0 || dataOffset > stream.Length)
            {
                return false;
            }

            var actualDataSize = stream.Length - dataOffset;
            if (actualDataSize > uint.MaxValue || stream.Length - 8 > uint.MaxValue)
            {
                return false;
            }

            Span<byte> size = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(size, checked((uint)(stream.Length - 8)));
            stream.Position = 4;
            stream.Write(size);
            BinaryPrimitives.WriteUInt32LittleEndian(size, checked((uint)actualDataSize));
            stream.Position = dataSizeOffset;
            stream.Write(size);
            stream.Flush(flushToDisk: true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsSafeSessionId(string sessionId) =>
        sessionId.Length == 32 && sessionId.All(Uri.IsHexDigit);

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { }
    }
}
