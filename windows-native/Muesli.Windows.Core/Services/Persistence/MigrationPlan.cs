namespace Muesli.Windows.Services.Persistence;

/// <summary>
/// The JSON history mapped onto persistence records, with the problems a real history contains
/// already resolved: duplicate ids, blank ids and folder references that point at folders which are
/// no longer there. Building the plan touches nothing; it is the value the import and the
/// verification are both computed from.
/// </summary>
internal sealed record MigrationPlan
{
    public IReadOnlyList<DictationRecord> Dictations { get; init; } = [];
    public IReadOnlyList<MeetingDetail> Meetings { get; init; } = [];
    public IReadOnlyList<FolderRecord> Folders { get; init; } = [];
    public IReadOnlyList<TemplateRecord> Templates { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public string Fingerprint { get; init; } = "";
    public MigrationCounts Counts { get; init; } = MigrationCounts.Empty;

    public HashSet<string> DictationIds { get; init; } = new(StringComparer.Ordinal);
    public HashSet<string> MeetingIds { get; init; } = new(StringComparer.Ordinal);
    public HashSet<string> FolderIds { get; init; } = new(StringComparer.Ordinal);
    public HashSet<string> TemplateIds { get; init; } = new(StringComparer.Ordinal);

    public static MigrationPlan Build(JsonHistorySnapshot snapshot, IReadOnlyList<string> inheritedWarnings)
    {
        var warnings = new List<string>(inheritedWarnings);

        // Folders and templates carry no timestamps in JSON. They take the source file's own
        // modification time so a second run over an unchanged history produces the same fingerprint
        // and is recognised as already done.
        var timestamp = snapshot.SourceTimestampUtc;

        var folders = new List<FolderRecord>();
        var folderIds = new HashSet<string>(StringComparer.Ordinal);
        var duplicateFolders = 0;
        foreach (var folder in snapshot.Folders)
        {
            if (string.IsNullOrWhiteSpace(folder.Id) || !folderIds.Add(folder.Id))
            {
                duplicateFolders++;
                continue;
            }

            folders.Add(new FolderRecord
            {
                Id = folder.Id,
                Name = folder.Name ?? "",
                // ParentId is part of the JSON contract. A null value is an explicit root, not an
                // instruction to retain a value from an existing SQLite row.
                ParentId = string.IsNullOrWhiteSpace(folder.ParentId) ? null : folder.ParentId,
                SortOrder = folders.Count,
                Metadata = "",
                CreatedAtUtc = timestamp,
                UpdatedAtUtc = timestamp
            });
        }

        // JSON is user-editable and older files may contain a reference to a deleted folder or a
        // cycle. Sanitize those references before import, then write parents before children so the
        // foreign-key checks and the search index see a complete tree on the first pass.
        var folderById = folders.ToDictionary(folder => folder.Id, StringComparer.Ordinal);
        var sanitizedFolders = new List<FolderRecord>(folders.Count);
        foreach (var folder in folders)
        {
            var parentId = folder.ParentId;
            if (parentId is not null && !folderById.ContainsKey(parentId))
            {
                warnings.Add($"Folder '{folder.Id}' referenced a missing parent and was moved to the root.");
                parentId = null;
            }

            if (parentId is not null && string.Equals(parentId, folder.Id, StringComparison.Ordinal))
            {
                warnings.Add($"Folder '{folder.Id}' referenced itself and was moved to the root.");
                parentId = null;
            }

            sanitizedFolders.Add(folder with { ParentId = parentId });
        }

        folderById = sanitizedFolders.ToDictionary(folder => folder.Id, StringComparer.Ordinal);
        var cycleBreaks = new HashSet<string>(StringComparer.Ordinal);
        foreach (var folder in sanitizedFolders)
        {
            var path = new HashSet<string>(StringComparer.Ordinal);
            var current = folder.Id;
            while (folderById.TryGetValue(current, out var currentFolder) && currentFolder.ParentId is not null)
            {
                if (!path.Add(current))
                {
                    // The first repeated node is deterministic because the source order is
                    // deterministic. Break the edge at that node and leave the rest of the cycle
                    // attached to it rather than discarding any folder.
                    cycleBreaks.Add(current);
                    break;
                }

                current = currentFolder.ParentId;
            }
        }

        if (cycleBreaks.Count > 0)
        {
            warnings.Add($"{cycleBreaks.Count} folder cycle(s) were broken by moving one folder in each cycle to the root.");
            sanitizedFolders = sanitizedFolders
                .Select(folder => cycleBreaks.Contains(folder.Id) ? folder with { ParentId = null } : folder)
                .ToList();
            folderById = sanitizedFolders.ToDictionary(folder => folder.Id, StringComparer.Ordinal);
        }

        var foldersByParent = sanitizedFolders
            .GroupBy(folder => folder.ParentId ?? "", StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.OrderBy(folder => folder.SortOrder).ToList(), StringComparer.Ordinal);
        var orderedFolders = new List<FolderRecord>(sanitizedFolders.Count);
        var emitted = new HashSet<string>(StringComparer.Ordinal);
        void Emit(string? parentId)
        {
            if (!foldersByParent.TryGetValue(parentId ?? "", out var children))
            {
                return;
            }

            foreach (var child in children)
            {
                if (!emitted.Add(child.Id))
                {
                    continue;
                }

                orderedFolders.Add(child with { SortOrder = orderedFolders.Count });
                Emit(child.Id);
            }
        }

        Emit(null);
        // A defensive fallback keeps malformed input lossless even if a future sanitization rule
        // misses a graph shape: every record is still imported exactly once.
        foreach (var folder in sanitizedFolders.OrderBy(folder => folder.SortOrder))
        {
            if (emitted.Add(folder.Id))
            {
                orderedFolders.Add(folder with { ParentId = null, SortOrder = orderedFolders.Count });
                warnings.Add($"Folder '{folder.Id}' had an unresolved ancestry and was moved to the root.");
            }
        }

        folders = orderedFolders;

        var templates = new List<TemplateRecord>();
        var templateIds = new HashSet<string>(StringComparer.Ordinal);
        var duplicateTemplates = 0;
        foreach (var template in snapshot.Templates)
        {
            if (string.IsNullOrWhiteSpace(template.Id) || !templateIds.Add(template.Id))
            {
                duplicateTemplates++;
                continue;
            }

            templates.Add(new TemplateRecord
            {
                Id = template.Id,
                Name = template.Name ?? "",
                Prompt = template.Prompt ?? "",
                Icon = template.Icon ?? "",
                SortOrder = templates.Count,
                CreatedAtUtc = timestamp,
                UpdatedAtUtc = timestamp
            });
        }

        var dictations = new List<DictationRecord>();
        var dictationIds = new HashSet<string>(StringComparer.Ordinal);
        var duplicateDictations = 0;
        foreach (var dictation in snapshot.Dictations)
        {
            if (string.IsNullOrWhiteSpace(dictation.Id) || !dictationIds.Add(dictation.Id))
            {
                duplicateDictations++;
                continue;
            }

            var text = dictation.Text ?? "";
            var createdAt = PersistenceTime.Normalize(dictation.Timestamp);
            dictations.Add(new DictationRecord
            {
                Id = dictation.Id,
                CreatedAtUtc = createdAt,
                UpdatedAtUtc = createdAt,
                Title = "",
                Text = text,

                // Dictation audio is temporary by policy and is gone by the time it reaches the
                // history file, so there is no path to carry over and nothing is claimed to exist.
                AudioPath = null,
                AudioRetained = false,
                DurationMs = dictation.DurationMs,
                ModelProfile = dictation.ModelProfile ?? "",
                FolderId = null,

                // Derived, because the JSON never stored one. It is a count of the imported text,
                // so it is reproducible from the source rather than invented.
                WordCount = CountWords(text)
            });
        }

        var meetings = new List<MeetingDetail>();
        var meetingIds = new HashSet<string>(StringComparer.Ordinal);
        var duplicateMeetings = 0;
        var orphanedFolderReferences = 0;
        foreach (var snapshotMeeting in snapshot.Meetings)
        {
            var meeting = snapshotMeeting.Meeting;
            if (string.IsNullOrWhiteSpace(meeting.Id) || !meetingIds.Add(meeting.Id))
            {
                duplicateMeetings++;
                continue;
            }

            var folderId = string.IsNullOrWhiteSpace(meeting.FolderId) ? null : meeting.FolderId;
            if (folderId is not null && !folderIds.Contains(folderId))
            {
                // The meeting is kept and unfiled. Dropping it, or writing a reference the schema
                // rejects, would lose the recording over a bookkeeping error.
                orphanedFolderReferences++;
                folderId = null;
            }

            var createdAt = PersistenceTime.Normalize(meeting.CreatedAt);
            meetings.Add(new MeetingDetail
            {
                Meeting = new MeetingRecord
                {
                    Id = meeting.Id,
                    Title = meeting.Title ?? "",
                    TitleIsManual = meeting.TitleIsManual,
                    CreatedAtUtc = createdAt,
                    UpdatedAtUtc = createdAt,
                    DurationMs = meeting.DurationMs,
                    ModelProfile = meeting.ModelProfile ?? "",
                    FolderId = folderId,
                    WordCount = meeting.WordCount,
                    TemplateName = meeting.TemplateName ?? "",
                    SessionState = meeting.SessionState,
                    RecoveredFromInterruption = meeting.RecoveredFromInterruption,
                    HealthWarnings = meeting.HealthWarnings
                        .Where(warning => !string.IsNullOrEmpty(warning))
                        .ToList(),
                    Audio = new MeetingAudioOwnership
                    {
                        SourcePath = meeting.SourcePath ?? "",
                        MicrophoneAudioPath = meeting.MicrophoneAudioPath,
                        SystemAudioPath = meeting.SystemAudioPath,
                        SystemCaptureMode = meeting.SystemCaptureMode ?? "",
                        LivePreviewModelId = meeting.LivePreviewModelId,
                        LiveTranscriptOwnership = meeting.LiveTranscriptOwnership ?? "off",
                        FinalTranscriptOwnerModelId = meeting.FinalTranscriptOwnerModelId ?? "",
                        GapRecoveryModelId = meeting.GapRecoveryModelId
                    },
                    AutomationResultJson = snapshotMeeting.AutomationResultJson,
                    SourceSchemaVersion = snapshotMeeting.OriginalSchemaVersion
                },
                Notes = Notes(meeting),
                Transcripts = Transcripts(meeting),
                SpeakerAliases = Aliases(meeting),
                FollowUps = []
            });
        }

        Warn(warnings, duplicateDictations, "dictations");
        Warn(warnings, duplicateMeetings, "meetings");
        Warn(warnings, duplicateFolders, "folders");
        Warn(warnings, duplicateTemplates, "templates");
        if (orphanedFolderReferences > 0)
        {
            warnings.Add(
                $"{orphanedFolderReferences} meeting(s) referenced a folder that no longer exists and were left unfiled.");
        }

        var folderDigest = PersistenceDigest.OfFolders(folders);
        var templateDigest = PersistenceDigest.OfTemplates(templates);
        var dictationDigest = PersistenceDigest.OfDictations(dictations);
        var meetingDigest = PersistenceDigest.OfMeetings(meetings);

        return new MigrationPlan
        {
            Dictations = dictations,
            Meetings = meetings,
            Folders = folders,
            Templates = templates,
            Warnings = warnings,
            Fingerprint = PersistenceDigest.Combine(
                folderDigest,
                templateDigest,
                dictationDigest,
                meetingDigest),
            Counts = new MigrationCounts(dictations.Count, meetings.Count, folders.Count, templates.Count),
            DictationIds = dictationIds,
            MeetingIds = meetingIds,
            FolderIds = folderIds,
            TemplateIds = templateIds
        };
    }

    /// <summary>
    /// The summary becomes the generated note and <c>ManualNotes</c> the manual one. They are
    /// separate rows because a re-summarization rewrites the first and must not be able to touch the
    /// second.
    /// </summary>
    private static List<MeetingNote> Notes(PersistedMeeting meeting)
    {
        var notes = new List<MeetingNote>(2);
        if (!string.IsNullOrEmpty(meeting.Summary))
        {
            notes.Add(new MeetingNote { MeetingId = meeting.Id, Kind = MeetingNoteKind.Generated, Content = meeting.Summary });
        }

        if (!string.IsNullOrEmpty(meeting.ManualNotes))
        {
            notes.Add(new MeetingNote { MeetingId = meeting.Id, Kind = MeetingNoteKind.Manual, Content = meeting.ManualNotes });
        }

        return notes;
    }

    /// <summary>
    /// JSON kept one transcript, which is the model's own output. It is imported as the raw
    /// transcript, leaving the edited slot free for the first correction the user makes.
    /// </summary>
    private static List<MeetingTranscript> Transcripts(PersistedMeeting meeting) =>
        string.IsNullOrEmpty(meeting.Transcript)
            ? []
            : [new MeetingTranscript { MeetingId = meeting.Id, Kind = MeetingTranscriptKind.Raw, Content = meeting.Transcript }];

    private static List<SpeakerAliasRecord> Aliases(PersistedMeeting meeting) =>
        meeting.SpeakerAliases
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Key))
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new SpeakerAliasRecord
            {
                MeetingId = meeting.Id,
                SpeakerKey = pair.Key,
                Alias = pair.Value ?? ""
            })
            .ToList();

    private static void Warn(List<string> warnings, int count, string entity)
    {
        if (count > 0)
        {
            warnings.Add($"{count} {entity} with a missing or duplicate id were imported once.");
        }
    }

    private static int CountWords(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
}
