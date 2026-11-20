using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace Muesli.Windows.Services;

/// <summary>One meeting from Granola's "Export historical data" CSV (macOS GranolaImportRecord).</summary>
public sealed record GranolaImportRecord(
    string Id,
    string Title,
    DateTimeOffset CreatedAt,
    string Summary,
    string Notes,
    string Transcript,
    string Workspace,
    string OwnerEmail)
{
    public const string MeetingIdPrefix = "meeting-granola-";

    /// <summary>Stable across files and devices; also the Windows meeting id, so a repeat import is a no-op.</summary>
    public string SyncIdentity => MeetingIdPrefix + Id;
}

public sealed record GranolaImportReport(int Imported, int Skipped);

public sealed class GranolaImportException(string message) : Exception(message);

/// <summary>
/// macOS GranolaImport (0504c515, 0c7f5187, eef6ac8a). ZIP entries are read into memory, never extracted.
/// Limits: 50 MB input and uncompressed CSV, 10,000 meetings, 1,000 ZIP entries, 128 columns.
/// </summary>
public static class GranolaImport
{
    public const int MaximumBytes = 50 * 1024 * 1024;
    public const int MaximumMeetings = 10_000;
    public const int MaximumZipEntries = 1_000;
    public const string FolderName = "Granola";
    private static readonly string[] Columns = ["document_id", "user_email", "document_title", "workspace_name", "document_created", "summary", "notes", "transcript"];

    public static IReadOnlyList<GranolaImportRecord> Read(IEnumerable<string> paths)
    {
        var records = new List<GranolaImportRecord>();
        long inputBytes = 0, contentBytes = 0;
        void AppendCsv(byte[] data)
        {
            if (data.Length > MaximumBytes - contentBytes) throw new GranolaImportException("The export exceeds 50 MB of uncompressed CSV data.");
            contentBytes += data.Length;
            records.AddRange(Parse(data));
            if (records.Count > MaximumMeetings) throw new GranolaImportException("Import up to 10,000 meetings at a time.");
        }
        foreach (var path in paths)
        {
            var size = new FileInfo(path).Length;
            if (size > MaximumBytes - inputBytes) throw new GranolaImportException("Choose ZIP or CSV files totaling no more than 50 MB.");
            var data = File.ReadAllBytes(path);
            inputBytes += data.Length;
            switch (Path.GetExtension(path).ToLowerInvariant())
            {
                case ".csv": AppendCsv(data); break;
                case ".zip": ReadZip(data, AppendCsv, () => contentBytes); break;
                default: throw new GranolaImportException("Choose a Granola ZIP or CSV export.");
            }
        }
        if (records.Count == 0) throw new GranolaImportException("This export contains no meetings.");
        return records.OrderByDescending(record => record.CreatedAt).ThenBy(record => record.Id, StringComparer.Ordinal).ToList();
    }

    private static void ReadZip(byte[] data, Action<byte[]> appendCsv, Func<long> contentBytes)
    {
        try
        {
            using var archive = new ZipArchive(new MemoryStream(data, writable: false), ZipArchiveMode.Read);
            if (archive.Entries.Count > MaximumZipEntries) throw new GranolaImportException("Choose a ZIP with no more than 1,000 entries.");
            var csvCount = 0;
            foreach (var entry in archive.Entries)
            {
                var parts = entry.FullName.Split('/');
                if (parts.Contains("__MACOSX") || parts.Any(part => part.StartsWith('.') && part is not ("." or "..")) ||
                    !entry.FullName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)) continue;
                if (entry.FullName.StartsWith('/') || parts.Contains("..") || entry.FullName.Contains('\\') || entry.IsEncrypted)
                    throw new GranolaImportException("The ZIP contains an unsupported CSV entry.");
                if (entry.Length > MaximumBytes - contentBytes()) throw new GranolaImportException("The export exceeds 50 MB of uncompressed CSV data.");
                var csv = new byte[entry.Length];
                using (var stream = entry.Open())
                {
                    stream.ReadExactly(csv);
                    if (stream.ReadByte() != -1) throw new GranolaImportException("A CSV in the ZIP is damaged. Download a new export from Granola.");
                }
                if (Crc32(csv) != entry.Crc32) throw new GranolaImportException("A CSV in the ZIP is damaged. Download a new export from Granola.");
                appendCsv(csv);
                csvCount++;
            }
            if (csvCount == 0) throw new GranolaImportException("This ZIP contains no CSV files.");
        }
        catch (GranolaImportException) { throw; }
        catch (Exception exception) when (exception is InvalidDataException or IOException or NotSupportedException or EndOfStreamException)
        {
            throw new GranolaImportException("Couldn't read this ZIP. Download a new Granola export or choose its CSV files.");
        }
    }

    public static IReadOnlyList<GranolaImportRecord> Parse(byte[] data)
    {
        string text;
        try
        {
            if (data.Length > MaximumBytes) throw new DecoderFallbackException();
            text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(data);
        }
        catch (DecoderFallbackException)
        {
            throw new GranolaImportException("Choose a UTF-8 Granola CSV export no larger than 50 MB.");
        }
        var rows = CsvRows(text);
        if (rows.Count == 0 || rows[0].Distinct().Count() != rows[0].Count || !Columns.All(rows[0].Contains))
            throw new GranolaImportException("This file does not have the expected Granola export columns. Export from Settings → Profile → Export historical data.");
        var header = rows[0];
        var index = header.Select((name, position) => (name, position)).ToDictionary(item => item.name, item => item.position);
        var records = new List<GranolaImportRecord>(rows.Count - 1);
        for (var offset = 1; offset < rows.Count; offset++)
        {
            var row = rows[offset];
            if (row.Count != header.Count) throw new GranolaImportException($"CSV row {offset + 1} has the wrong number of columns.");
            string Value(string key) => row[index[key]];
            if (!Guid.TryParseExact(Value("document_id"), "D", out var id) || ParseDate(Value("document_created")) is not { } created)
                throw new GranolaImportException($"CSV row {offset + 1} has an invalid document ID or creation date. Nothing was imported.");
            if (new[] { Value("summary"), Value("notes"), Value("transcript") }.All(string.IsNullOrWhiteSpace))
                throw new GranolaImportException($"CSV row {offset + 1} has no summary, notes, or transcript.");
            records.Add(new GranolaImportRecord(id.ToString("D"), Value("document_title"), created, Value("summary"), Value("notes"),
                Value("transcript"), Value("workspace_name"), Value("user_email")));
        }
        return records;
    }

    /// <summary>ISO 8601 internet date-time with an explicit offset, with or without fractional seconds.</summary>
    private static DateTimeOffset? ParseDate(string value) =>
        DateTimeOffset.TryParseExact(value, ["yyyy-MM-ddTHH:mm:ssK", "yyyy-MM-ddTHH:mm:ss.FFFFFFFK"], CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var date) && (value.EndsWith('Z') || value.Length > 6 && value[^6] is '+' or '-')
            ? date
            : null;

    /// <summary>RFC 4180 quoting, including multiline fields and CRLF. Cell contents are never evaluated.</summary>
    private static List<List<string>> CsvRows(string input)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var state = 0; // 0 start, 1 unquoted, 2 quoted, 3 closed
        var previousCr = false;
        void FinishField()
        {
            // Bound allocations even for a small malformed file full of commas.
            if (row.Count >= (rows.Count > 0 ? rows[0].Count : 128)) throw new GranolaImportException("The CSV has too many columns.");
            row.Add(field.ToString());
            field.Clear();
            state = 0;
        }
        void FinishRow()
        {
            FinishField();
            if (row is not [""]) rows.Add(row);
            row = [];
            if (rows.Count > MaximumMeetings + 1) throw new GranolaImportException("Import up to 10,000 meetings at a time.");
        }
        var start = input.Length > 0 && input[0] == '﻿' ? 1 : 0;
        for (var i = start; i < input.Length; i++)
        {
            var c = input[i];
            if (c == '\0') throw new GranolaImportException("The CSV contains an unsupported null character.");
            if (state == 2)
            {
                if (c == '"') state = 3; else field.Append(c);
                continue;
            }
            if (previousCr && c == '\n') { previousCr = false; continue; }
            previousCr = false;
            switch (c)
            {
                case '"':
                    if (state == 0) state = 2;
                    else if (state == 3) { field.Append('"'); state = 2; }
                    else throw new GranolaImportException("The CSV contains an unexpected quote.");
                    break;
                case ',': FinishField(); break;
                case '\r' or '\n': FinishRow(); previousCr = c == '\r'; break;
                default:
                    if (state == 3) throw new GranolaImportException("The CSV contains text after a closing quote.");
                    field.Append(c);
                    state = 1;
                    break;
            }
        }
        if (state == 2) throw new GranolaImportException("The CSV ends inside a quoted field.");
        if (state != 0 || row.Count > 0 || field.Length > 0) FinishRow();
        return rows;
    }

    private static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ (0xEDB88320u & (uint)-(int)(crc & 1));
        }
        return ~crc;
    }

    /// <summary>
    /// The import plan (macOS DictationStore.importGranola). Existing ids are skipped and never overwritten.
    /// The "Granola" folder is reused by name at the top level, or created only when a meeting is new.
    /// Duration stays zero; word counts cover transcript and manual notes, not the generated summary.
    /// </summary>
    public static (IReadOnlyList<PersistedMeeting> Meetings, PersistedMeetingFolder? NewFolder, GranolaImportReport Report) Plan(
        IReadOnlyList<GranolaImportRecord> records,
        ISet<string> existingMeetingIds,
        IReadOnlyList<PersistedMeetingFolder> folders)
    {
        if (records.Count > MaximumMeetings) throw new GranolaImportException("Import up to 10,000 meetings at a time.");
        var folder = folders.Where(item => item.ParentId is null && item.Name == FolderName).OrderBy(item => item.Id, StringComparer.Ordinal).FirstOrDefault();
        PersistedMeetingFolder? created = null;
        var meetings = new List<PersistedMeeting>();
        var seen = new HashSet<string>(existingMeetingIds, StringComparer.Ordinal);
        var skipped = 0;
        foreach (var record in records)
        {
            if (!seen.Add(record.SyncIdentity)) { skipped++; continue; }
            folder ??= created = new PersistedMeetingFolder("folder_granola_" + Guid.NewGuid().ToString("N")[..12], FolderName);
            meetings.Add(new PersistedMeeting
            {
                SchemaVersion = 1,
                Id = record.SyncIdentity,
                Title = record.Title,
                TitleIsManual = !string.IsNullOrWhiteSpace(record.Title),
                CreatedAt = record.CreatedAt.LocalDateTime,
                Transcript = record.Transcript,
                Summary = record.Summary,
                ManualNotes = record.Notes,
                FolderId = folder.Id,
                WordCount = WordCount(record.Transcript) + WordCount(record.Notes),
                SessionState = MeetingSessionState.Completed,
            });
        }
        return (meetings, created, new GranolaImportReport(meetings.Count, skipped));
    }

    public const string OriginLabel = "Imported from Granola";
    public const string OriginHelp = "Imported from a Granola CSV. Date is document creation time; duration and audio are unavailable.";

    public static bool IsImported(string meetingId) => meetingId.StartsWith(GranolaImportRecord.MeetingIdPrefix, StringComparison.Ordinal);

    private static int WordCount(string text) => Muesli.Windows.Core.Insights.LibraryMetrics.CountWords(text);
}
