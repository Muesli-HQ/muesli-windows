using System.IO.Compression;
using System.Text;
using Muesli.Windows.Services;

namespace Muesli.Windows.Tests;

/// <summary>MAC-05 (macOS GranolaImportTests): synthetic fixtures only; no real export is bundled.</summary>
public sealed class GranolaImportTests : IDisposable
{
    private const string Header = "document_id,user_email,document_title,workspace_name,document_created,summary,notes,transcript";
    private const string IdA = "8f1c2a3b-4d5e-4f60-8a7b-9c0d1e2f3a4b";
    private const string IdB = "0a1b2c3d-4e5f-4061-8273-94a5b6c7d8e9";
    private readonly string _directory = Directory.CreateTempSubdirectory("granola-test-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static string Csv(params string[] rows) => "﻿" + Header + "\r\n" + string.Join("\r\n", rows) + "\r\n";

    private static readonly string RowA = $"{IdA},owner@example.com,\"Planning, Q4\",Team,2026-10-01T09:30:00.000Z,\"## Summary\nShip it.\",,\"Speaker: \"\"hello\"\" ünïcode\r\nsecond line\"";
    private static readonly string RowB = $"{IdB},owner@example.com,Standup,Team,2026-10-02T08:00:00Z,,typed notes only,";

    private string Write(string name, string text)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllText(path, text, new UTF8Encoding(false));
        return path;
    }

    [Fact]
    public void QuotedMultilineUnicodeFieldsAreKeptExactly()
    {
        var records = GranolaImport.Read([Write("a.csv", Csv(RowA, RowB))]);
        Assert.Equal([IdB, IdA], records.Select(record => record.Id)); // newest first
        var a = records[1];
        Assert.Equal("Planning, Q4", a.Title);
        Assert.Equal("## Summary\nShip it.", a.Summary);
        Assert.Equal("Speaker: \"hello\" ünïcode\r\nsecond line", a.Transcript);
        Assert.Equal("Team", a.Workspace);
        Assert.Equal("meeting-granola-" + IdA, a.SyncIdentity);
        Assert.Equal("typed notes only", records[0].Notes); // notes-only records are accepted
    }

    [Theory]
    [InlineData("not-a-uuid,o,T,W,2026-10-01T09:30:00Z,s,,", "invalid document ID")]
    [InlineData(IdA + ",o,T,W,2026-10-01 09:30,s,,", "invalid document ID or creation date")]
    [InlineData(IdA + ",o,T,W,2026-10-01T09:30:00Z,,,", "no summary, notes, or transcript")]
    [InlineData(IdA + ",o,T,W,2026-10-01T09:30:00Z,s,,,extra", "too many columns")]
    [InlineData(IdA + ",o,T,W,2026-10-01T09:30:00Z,\"open", "ends inside a quoted field")]
    [InlineData(IdA + ",o,T,W,2026-10-01T09:30:00Z,\"s\"x,,", "text after a closing quote")]
    public void AMalformedRowRejectsTheWholeFile(string row, string message)
    {
        var error = Assert.Throws<GranolaImportException>(() => GranolaImport.Read([Write("bad.csv", Csv(RowB, row))]));
        Assert.Contains(message, error.Message);
    }

    [Fact]
    public void MissingColumnsAndNonUtf8AreRejected()
    {
        Assert.Contains("expected Granola export columns", Assert.Throws<GranolaImportException>(() => GranolaImport.Read([Write("x.csv", "a,b\r\n1,2\r\n")])).Message);
        var latin1 = Path.Combine(_directory, "latin1.csv");
        File.WriteAllBytes(latin1, [.. Encoding.UTF8.GetBytes(Header + "\n"), 0xE9, 0xFF]);
        Assert.Contains("UTF-8", Assert.Throws<GranolaImportException>(() => GranolaImport.Read([latin1])).Message);
        Assert.Contains("ZIP or CSV", Assert.Throws<GranolaImportException>(() => GranolaImport.Read([Write("x.txt", "hi")])).Message);
    }

    [Fact]
    public void AZipIsReadInMemoryIgnoringMacMetadata()
    {
        var zip = Path.Combine(_directory, "export.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            void Add(string name, string text)
            {
                using var stream = archive.CreateEntry(name).Open();
                stream.Write(Encoding.UTF8.GetBytes(text));
            }
            Add("granola/part1.csv", Csv(RowA));
            Add("granola/part2.csv", Csv(RowB));
            Add("__MACOSX/granola/._part1.csv", "junk");
            Add("README.txt", "ignored");
        }
        Assert.Equal(2, GranolaImport.Read([zip]).Count);

        var empty = Path.Combine(_directory, "empty.zip");
        using (var archive = ZipFile.Open(empty, ZipArchiveMode.Create)) archive.CreateEntry("notes.txt");
        Assert.Contains("no CSV files", Assert.Throws<GranolaImportException>(() => GranolaImport.Read([empty])).Message);
        Assert.Contains("Couldn't read this ZIP", Assert.Throws<GranolaImportException>(() => GranolaImport.Read([Write("broken.zip", "PK not really")])).Message);
    }

    [Fact]
    public void ThePlanSkipsExistingIdsAndReusesOrCreatesTheGranolaFolder()
    {
        var records = GranolaImport.Read([Write("a.csv", Csv(RowA, RowB))]);
        var (meetings, folder, report) = GranolaImport.Plan(records, new HashSet<string> { "meeting-granola-" + IdA }, []);
        Assert.Equal((1, 1), (report.Imported, report.Skipped));
        Assert.NotNull(folder);
        Assert.Equal("Granola", folder!.Name);
        var b = Assert.Single(meetings);
        Assert.Equal(folder.Id, b.FolderId);
        Assert.Equal(0, b.DurationMs);
        Assert.Equal("typed notes only", b.ManualNotes);
        Assert.Equal(3, b.WordCount); // transcript + manual notes, never the summary
        Assert.True(GranolaImport.IsImported(b.Id));

        var existingFolder = new PersistedMeetingFolder("f1", "Granola");
        var (again, created, _) = GranolaImport.Plan(records, new HashSet<string>(), [new PersistedMeetingFolder("nested", "Granola", "f0"), existingFolder]);
        Assert.Null(created);
        Assert.All(again, meeting => Assert.Equal("f1", meeting.FolderId));

        var (none, noFolder, repeat) = GranolaImport.Plan(records, meetings.Select(item => item.Id).Append("meeting-granola-" + IdA).ToHashSet(), []);
        Assert.Empty(none);
        Assert.Null(noFolder); // no folder for an import with nothing new
        Assert.Equal(2, repeat.Skipped);
    }

    [Fact]
    public void TheLibraryImportIsRepeatableAndNeverOverwritesEdits()
    {
        using var directory = new TestDirectory();
        using var library = new Muesli.Windows.WinUI.Services.WinUiLibraryContext(Muesli.Windows.Core.Profiles.MuesliProfilePaths.Isolated(directory.Path));
        var records = GranolaImport.Read([Write("a.csv", Csv(RowA, RowB))]);
        Assert.Equal(new GranolaImportReport(2, 0), library.ImportGranola(records));
        var folder = Assert.Single(library.History.LoadMeetingFolders(), item => item.Name == "Granola");
        Assert.Equal(2, library.History.LoadMeetings().Count(meeting => meeting.FolderId == folder.Id));
        var id = "meeting-granola-" + IdB;
        Assert.Equal("Team", Muesli.Windows.WinUI.Services.MeetingImports.Find(library.Profile.RootDirectory, id)!.WorkspaceName);

        library.UpdateMeeting(MeetingNotesComposer.ApplyManualNotes(library.FindMeeting(id)!, "my edit"));
        Assert.Equal(new GranolaImportReport(0, 2), library.ImportGranola(records));
        Assert.Equal("my edit", library.FindMeeting(id)!.ManualNotes);
        Assert.Single(library.History.LoadMeetingFolders(), item => item.Name == "Granola");

        Assert.True(library.DeleteMeeting(id));
        Assert.Null(Muesli.Windows.WinUI.Services.MeetingImports.Find(library.Profile.RootDirectory, id));
    }

    [Fact]
    public void ImportedNotesSurviveAutosaveAndTheWordCountIsRefreshed()
    {
        var records = GranolaImport.Read([Write("a.csv", Csv(RowA))]);
        var meeting = GranolaImport.Plan(records, new HashSet<string>(), []).Meetings.Single();
        var saved = MeetingNotesComposer.ApplyManualNotes(meeting, "one two");
        Assert.Equal(meeting.Summary, saved.Summary);
        Assert.Equal(meeting.Transcript, saved.Transcript);
        Assert.Equal(Muesli.Windows.Core.Insights.LibraryMetrics.CountWords(meeting.Transcript) + 2, saved.WordCount);
    }
}
