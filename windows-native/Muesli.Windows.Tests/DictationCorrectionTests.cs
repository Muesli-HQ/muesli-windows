using Muesli.Windows.Services;
using Muesli.Windows.Services.Persistence;

namespace Muesli.Windows.Tests;

public sealed class DictationCorrectionTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void IncidentalSelectionWhitespaceStaysInHistoryButNotInTheDictionary(bool sqlite, bool remember)
    {
        using var f = new HistoryFixture(sqlite);
        const string text = "Vatsan,\r\n\t  Vatsan \u00a0!";
        f.Seed(text);
        var start = text.IndexOf('\r');
        var selection = new DictationCorrectionSelection("d", text, start, text.Length - start - 1);
        var trimmed = DictationCorrectionService.TrimSelection(selection);
        Assert.Equal("Vatsan", trimmed.Original);
        Assert.Equal(text.LastIndexOf("Vatsan", StringComparison.Ordinal), trimmed.Start);
        Assert.Null(DictationCorrectionService.ValidateSelection(selection));
        Assert.True(f.Service.Save(selection, "  Vathsan  ", remember).Succeeded);
        f.Reopen();
        Assert.Equal("Vatsan,\r\n\t  Vathsan \u00a0!", Assert.Single(f.History.LoadDictations()).Text);
        if (remember)
        {
            var entry = Assert.Single(f.History.LoadDictionary());
            Assert.Equal("Vatsan", entry.Phrase);
            Assert.Equal("Vathsan", entry.Replacement);
        }
        else Assert.Empty(f.History.LoadDictionary());
    }

    [Fact]
    public void CaptureTrimsAfterMappingDisplayLineEndingsAndKeepsUnicodeIntact()
    {
        const string source = "👩🏽‍💻\r\n  cafe\u0301 \t";
        var displayed = source.Replace("\r\n", "\r");
        var start = displayed.IndexOf('\r');
        var selected = DictationCorrectionService.CaptureSelection("d", source, displayed, start, displayed.Length - start);
        Assert.Equal("cafe\u0301", selected.Original);
        Assert.Equal(source.IndexOf("cafe", StringComparison.Ordinal), selected.Start);
        Assert.Null(DictationCorrectionService.ValidateSelection(selected));
        Assert.Equal(0, DictationCorrectionService.TrimSelection(new("d", " \t\u00a0", 0, 3)).Length);
    }

    [Theory]
    [InlineData("\r\n", "\r")]
    [InlineData("\r\n", "\n")]
    [InlineData("\n", "\r")]
    [InlineData("\r", "\r\n")]
    public void DisplayLineEndingsMapToTheSameSavedOccurrence(string sourceBreak, string displayBreak)
    {
        var source = "  👩🏽‍💻 Vatsan" + sourceBreak + "Vatsan!  ";
        var displayed = "  👩🏽‍💻 Vatsan" + displayBreak + "Vatsan!  ";
        var selection = DictationCorrectionService.CaptureSelection("d", source, displayed,
            displayed.LastIndexOf("Vatsan", StringComparison.Ordinal), 6);
        Assert.Equal(source.LastIndexOf("Vatsan", StringComparison.Ordinal), selection.Start);
        Assert.Equal("Vatsan", selection.Original);
        Assert.Null(DictationCorrectionService.ValidateSelection(selection));
        Assert.NotNull(DictationCorrectionService.ValidateSelection(
            DictationCorrectionService.CaptureSelection("d", source, displayed + "changed", 0, 6)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SelectedOccurrencePersistsAndFeedsTheExistingPipeline(bool sqlite)
    {
        using var fixture = new HistoryFixture(sqlite);
        const string text = "  👩🏽‍💻 Vatsan, meet Vatsan!\r\nKeep café.  ";
        fixture.Seed(text);
        var selection = new DictationCorrectionSelection("d", text, text.LastIndexOf("Vatsan", StringComparison.Ordinal), 6);
        var result = fixture.Service.Save(selection, "Vathsan", true);
        Assert.True(result.Succeeded, result.Message);
        fixture.Reopen();
        var saved = Assert.Single(fixture.History.LoadDictations());
        Assert.Equal("  👩🏽‍💻 Vatsan, meet Vathsan!\r\nKeep café.  ", saved.Text);
        Assert.Equal(fixture.Original with { Text = saved.Text }, saved);
        var entry = Assert.Single(fixture.History.LoadDictionary());
        Assert.Equal("Vatsan", entry.Phrase);
        Assert.Equal("Vathsan", entry.Replacement);
        Assert.Equal(DictionaryCorrectionService.MaximumThreshold, entry.MatchingThreshold);
        Assert.Single(((ILibrarySearchAdapter)fixture.History).Search(new SearchQuery { Text = "Vathsan" }));
        var output = await new TranscriptionPipelineService(new NativeTextCleanupService()).PrepareDictationTextAsync(
            "Ask Vatsan, not Vatsanville.", false, false, fixture.History.LoadDictionary());
        Assert.Equal("Ask Vathsan, not Vatsanville.", output);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AbandonedEditorCancellationAndOneOffEdits(bool sqlite)
    {
        using var fixture = new HistoryFixture(sqlite);
        const string text = "cafe\u0301 / cafe\u0301; 👩🏽‍💻!";
        fixture.Seed(text);
        var selection = new DictationCorrectionSelection("d", text, 0, 5);
        // Opening/abandoning an editor only captures a value; no persistence occurs.
        Assert.Null(DictationCorrectionService.ValidateSelection(selection));
        Assert.Equal(text, Assert.Single(fixture.History.LoadDictations()).Text);
        Assert.Empty(fixture.History.LoadDictionary());
        Assert.False(fixture.Service.Save(selection, "café", true, cancellationToken: new CancellationToken(true)).Succeeded);
        Assert.Equal(text, Assert.Single(fixture.History.LoadDictations()).Text);
        Assert.True(fixture.Service.Save(selection, "café", false).Succeeded);
        fixture.Reopen();
        Assert.Equal("café / cafe\u0301; 👩🏽‍💻!", Assert.Single(fixture.History.LoadDictations()).Text);
        Assert.Empty(fixture.History.LoadDictionary());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DuplicatesConflictsAndChangedConfirmationNeverSilentlyOverwrite(bool sqlite)
    {
        using var f = new HistoryFixture(sqlite);
        f.Seed("Vatsan Vatsan Vatsan");
        var originalEntry = new DictionaryEntryRecord { Id = "existing", Phrase = "vatsan", Replacement = "Other", MatchingThreshold = .95 };
        f.History.SaveDictionary([originalEntry]);
        var selection = new DictationCorrectionSelection("d", f.Original.Text, 0, 6);
        var conflict = f.Service.Save(selection, "Vathsan", true);
        Assert.Equal(DictationCorrectionOutcome.Conflict, conflict.Outcome);
        Assert.Equal(f.Original, Assert.Single(f.History.LoadDictations()));
        Assert.Equal(originalEntry, Assert.Single(f.History.LoadDictionary()));
        var changedEntry = originalEntry with { Replacement = "Another" };
        f.History.SaveDictionary([changedEntry]);
        var recheck = f.Service.Save(selection, "Vathsan", true, conflict.Conflicts);
        Assert.Equal(DictationCorrectionOutcome.Conflict, recheck.Outcome);
        Assert.True(f.Service.Save(selection, "Vathsan", true, recheck.Conflicts).Succeeded);
        var entry = Assert.Single(f.History.LoadDictionary());
        Assert.Equal(originalEntry with { Replacement = "Vathsan" }, entry);
        var current = Assert.Single(f.History.LoadDictations()).Text;
        Assert.True(f.Service.Save(new("d", current, current.IndexOf("Vatsan", StringComparison.Ordinal), 6), "Vathsan", true).Succeeded);
        Assert.Equal(entry, Assert.Single(f.History.LoadDictionary()));
        // A conflict may also be resolved as a one-off, keeping the existing dictionary mapping.
        current = Assert.Single(f.History.LoadDictations()).Text;
        Assert.True(f.Service.Save(new("d", current, current.IndexOf("Vatsan", StringComparison.Ordinal), 6), "Different", false).Succeeded);
        Assert.Equal(entry, Assert.Single(f.History.LoadDictionary()));
    }

    [Fact]
    public void MatcherEquivalentLegacyDuplicatesAreCollapsedAndCombiningMarksAreWholeTokens()
    {
        using var f = new HistoryFixture(false);
        f.Seed("cafe\u0301 noir");
        f.History.SaveDictionary([
            new() { Id = "a", Phrase = "cafe\u0301  noir", Replacement = "coffee" },
            new() { Id = "b", Phrase = "CAFE\u0301,NOIR", Replacement = "coffee" }]);
        Assert.True(f.Service.Save(new("d", f.Original.Text, 0, f.Original.Text.Length), "coffee", true).Succeeded);
        Assert.Single(f.History.LoadDictionary());
        Assert.Equal("coffee!", DictionaryCorrectionService.Apply("cafe\u0301 noir!", f.History.LoadDictionary()));
    }

    [Theory]
    [InlineData("hello", 0, 0, "hi", false)]
    [InlineData("hello", 0, 5, "", false)]
    [InlineData("hello", 0, 5, "hello", false)]
    [InlineData("hello", 0, 5, " hello ", false)]
    [InlineData("   ", 0, 3, "hi", false)]
    [InlineData("hello", 0, 5, "hi\nthere", false)]
    [InlineData("👩🏽‍💻", 0, 2, "person", false)]
    [InlineData("cafe\u0301", 0, 4, "café", false)]
    [InlineData("Vatsanville", 0, 6, "Vathsan", true)]
    [InlineData("hello,", 0, 6, "hi", true)]
    [InlineData("!!!", 0, 3, "hi", true)]
    public void InvalidInputDoesNotWrite(string text, int start, int length, string replacement, bool remember)
    {
        using var f = new HistoryFixture(false);
        f.Seed(text);
        Assert.Equal(DictationCorrectionOutcome.Invalid, f.Service.Save(new("d", text, start, length), replacement, remember).Outcome);
        Assert.Equal(f.Original, Assert.Single(f.History.LoadDictations()));
        Assert.Empty(f.History.LoadDictionary());
    }

    [Fact]
    public void WholeEmojiAndPunctuationCanBeEditedOnce()
    {
        using var f = new HistoryFixture(false);
        f.Seed("Hi 👩🏽‍💻!");
        Assert.True(f.Service.Save(new("d", f.Original.Text, 3, "👩🏽‍💻!".Length), "🧑🏽‍💻?", false).Succeeded);
        Assert.Equal("Hi 🧑🏽‍💻?", Assert.Single(f.History.LoadDictations()).Text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StaleAndDeletedDictationsAreNotOverwrittenOrResurrected(bool sqlite)
    {
        using var f = new HistoryFixture(sqlite);
        f.Seed("Vatsan");
        var selection = new DictationCorrectionSelection("d", "Vatsan", 0, 6);
        ((IDictationTextEditor)f.History).TryUpdateDictationText("d", "Vatsan", "newer");
        Assert.Equal(DictationCorrectionOutcome.Stale, f.Service.Save(selection, "Vathsan", true).Outcome);
        f.History.SaveDictations([], afterExplicitDeletion: true);
        Assert.Equal(DictationCorrectionOutcome.Stale, f.Service.Save(selection, "Vathsan", true).Outcome);
        Assert.Empty(f.History.LoadDictations());
        Assert.Empty(f.History.LoadDictionary());
    }

    [Fact]
    public void SqlitePreservesRecordingMetadataAndUpdatesDerivedWordCount()
    {
        using var f = new HistoryFixture(true);
        f.Seed("Vatsan");
        var sqlite = (SqliteLibraryHistoryAdapter)f.History;
        var record = sqlite.Store.Dictations.Find("d")! with { AudioPath = "owned.wav", AudioRetained = true, Title = "My title" };
        sqlite.Store.Dictations.Upsert(record);
        Assert.True(f.Service.Save(new("d", "Vatsan", 0, 6), "Vathsan Prahalad", false).Succeeded);
        var updated = sqlite.Store.Dictations.Find("d")!;
        Assert.Equal(record with { Text = updated.Text, WordCount = 2, UpdatedAtUtc = updated.UpdatedAtUtc }, updated);
    }

    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(true, true, false, false)]
    [InlineData(false, true, true, false)]
    [InlineData(true, true, true, false)]
    [InlineData(false, true, false, true)]
    [InlineData(true, true, false, true)]
    public void SaveFailuresReportRollbackOrPartialPersistence(bool sqlite, bool dictionaryFailure, bool committedBeforeFailure, bool rollbackFailure)
    {
        using var f = new HistoryFixture(sqlite);
        f.Seed("Vatsan");
        var failing = new FailingHistory(f.History)
        {
            FailHistory = !dictionaryFailure, FailDictionary = dictionaryFailure,
            CommitBeforeFailure = committedBeforeFailure, FailRollback = rollbackFailure
        };
        var result = new DictationCorrectionService(failing).Save(new("d", "Vatsan", 0, 6), "Vathsan", true);
        Assert.False(result.Succeeded);
        Assert.Equal(rollbackFailure ? DictationCorrectionOutcome.PartialFailure : DictationCorrectionOutcome.Failed, result.Outcome);
        Assert.Equal(rollbackFailure ? "Vathsan" : "Vatsan", Assert.Single(f.History.LoadDictations()).Text);
        Assert.Empty(f.History.LoadDictionary());
        if (rollbackFailure) Assert.Contains("dictation correction was saved", result.Message);
        else Assert.Contains("Both stores are unchanged", result.Message);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void FailedDictionaryRollbackAndUnreadableStateNeverReportSuccess(bool sqlite, bool unreadable)
    {
        using var f = new HistoryFixture(sqlite);
        f.Seed("Vatsan");
        var failing = new FailingHistory(f.History)
        {
            FailDictionary = true, CommitBeforeFailure = true,
            FailDictionaryRollback = true, FailReadback = unreadable
        };
        var result = new DictationCorrectionService(failing).Save(new("d", "Vatsan", 0, 6), "Vathsan", true);
        Assert.Equal(DictationCorrectionOutcome.PartialFailure, result.Outcome);
        Assert.False(result.Succeeded);
        Assert.Equal("Vatsan", Assert.Single(f.History.LoadDictations()).Text);
        Assert.Equal("Vathsan", Assert.Single(f.History.LoadDictionary()).Replacement);
        Assert.Contains(unreadable ? "could not be verified" : "dictionary could not be restored", result.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HistoryFailureAfterCommitIsCompensatedWithoutTouchingOtherRows(bool sqlite)
    {
        using var f = new HistoryFixture(sqlite);
        f.Seed("Vatsan");
        var other = f.Original with { Id = "other", Text = "Keep this row" };
        f.History.AppendDictation(other);
        var failing = new FailingHistory(f.History) { FailHistory = true, CommitBeforeFailure = true };
        var result = new DictationCorrectionService(failing).Save(new("d", "Vatsan", 0, 6), "Vathsan", true);
        Assert.Equal(DictationCorrectionOutcome.Failed, result.Outcome);
        Assert.Equal(f.Original, f.History.LoadDictations().Single(row => row.Id == "d"));
        Assert.Equal(other, f.History.LoadDictations().Single(row => row.Id == "other"));
        Assert.Empty(f.History.LoadDictionary());
    }

    private sealed class HistoryFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "muesli-correction-" + Guid.NewGuid().ToString("N"));
        private readonly bool _sqlite;
        public ILibraryHistoryAdapter History { get; private set; }
        public DictationCorrectionService Service => new(History);
        public PersistedDictation Original { get; private set; } = null!;
        public HistoryFixture(bool sqlite)
        {
            _sqlite = sqlite;
            if (sqlite) new PersistenceCutover(_root).EnsureMigrated();
            History = Open();
        }
        private ILibraryHistoryAdapter Open() => _sqlite ? SqliteLibraryHistoryAdapter.Open(_root) : new JsonLibraryHistoryAdapter(new AppDataStore(_root));
        public void Seed(string text)
        {
            Original = new("d", new DateTime(2026, 11, 18, 10, 0, 0, DateTimeKind.Utc), text, 2345, "test-model");
            History.AppendDictation(Original);
        }
        public void Reopen() { (History as IDisposable)?.Dispose(); History = Open(); }
        public void Dispose()
        {
            (History as IDisposable)?.Dispose();
            Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), Path.GetFullPath(_root), StringComparison.OrdinalIgnoreCase);
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }
    }

    private sealed class FailingHistory(ILibraryHistoryAdapter inner) : ILibraryHistoryAdapter, IDictationTextEditor
    {
        public bool FailHistory { get; init; }
        public bool FailDictionary { get; init; }
        public bool CommitBeforeFailure { get; init; }
        public bool FailRollback { get; init; }
        public bool FailDictionaryRollback { get; init; }
        public bool FailReadback { get; init; }
        private int _dictionaryWrites;
        private int _historyWrites;
        public bool TryUpdateDictationText(string id, string expectedText, string replacementText)
        {
            if (FailRollback && replacementText == "Vatsan") throw new IOException("injected");
            if (FailHistory && _historyWrites++ == 0)
            {
                if (CommitBeforeFailure) ((IDictationTextEditor)inner).TryUpdateDictationText(id, expectedText, replacementText);
                throw new IOException("injected");
            }
            return ((IDictationTextEditor)inner).TryUpdateDictationText(id, expectedText, replacementText);
        }
        public void SaveDictionary(IEnumerable<DictionaryEntryRecord> entries)
        {
            if (FailDictionaryRollback && _dictionaryWrites > 0) throw new IOException("injected");
            if (FailDictionary && _dictionaryWrites++ == 0)
            {
                if (CommitBeforeFailure) inner.SaveDictionary(entries);
                throw new IOException("injected");
            }
            inner.SaveDictionary(entries);
        }
        public string? LastWarning => inner.LastWarning;
        public IReadOnlyList<PersistedDictation> LoadDictations() => inner.LoadDictations();
        public void AppendDictation(PersistedDictation d) => inner.AppendDictation(d);
        public void SaveDictations(IEnumerable<PersistedDictation> d, bool afterExplicitDeletion = false) => inner.SaveDictations(d, afterExplicitDeletion);
        public IReadOnlyList<DictionaryEntryRecord> LoadDictionary() =>
            FailReadback && _dictionaryWrites > 0 ? throw new IOException("injected") : inner.LoadDictionary();
        public IReadOnlyList<PersistedMeeting> LoadMeetings() => inner.LoadMeetings();
        public void SaveMeetings(IEnumerable<PersistedMeeting> d, bool afterExplicitDeletion = false) => inner.SaveMeetings(d, afterExplicitDeletion);
        public IReadOnlyList<PersistedMeetingFolder> LoadMeetingFolders() => inner.LoadMeetingFolders();
        public void SaveMeetingFolders(IEnumerable<PersistedMeetingFolder> d) => inner.SaveMeetingFolders(d);
        public IReadOnlyList<PersistedMeetingTemplate> LoadMeetingTemplates() => inner.LoadMeetingTemplates();
        public void SaveMeetingTemplates(IEnumerable<PersistedMeetingTemplate> d) => inner.SaveMeetingTemplates(d);
    }
}
