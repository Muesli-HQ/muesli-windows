using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Muesli.Windows.Services.Persistence;

namespace Muesli.Windows.Services;

/// <summary>
/// Port of macOS MeetingChatStore.swift and MeetingChatRetrieval.swift (2f10b23d) on the shared Windows
/// history connection. Threads, turns and source dependencies are durable; passages are a rebuildable
/// cache reconciled from meeting_chat_dirty_sources before each search. Provider calls never happen here.
/// </summary>
public sealed partial class MeetingChatStore(MuesliDatabase database)
{
    private const int CandidateLimit = 256;

    // ---- Threads ---------------------------------------------------------------------------------

    public MeetingChatSession CreateSession(MeetingChatScope scope, string title, string? anchorMeetingId = null, long? expectedSourceVersion = null)
    {
        var now = DateTimeOffset.UtcNow;
        var session = new MeetingChatSession
        {
            Id = Guid.NewGuid(), Title = title, Scope = scope, CreatedAt = now, UpdatedAt = now, TitleIsAutomatic = true
        };
        var anchor = anchorMeetingId;
        if (anchor is null && scope.Selection.Kind == MeetingChatSelectionKind.Meetings && scope.Selection.Ids.Distinct().Count() == 1)
            anchor = scope.Selection.Ids[0];
        return database.Write(connection =>
        {
            // Validate inside the write transaction: queued setup must not recreate history after a wipe.
            if (expectedSourceVersion is { } expected && SourceVersion(connection) != expected) throw MeetingChatException.SourceChanged();
            Sql.Execute(connection, "INSERT INTO meeting_chat_threads VALUES ($p0, $p1, $p2)",
                Key(session.Id), MeetingChatJson.Encode(session), now.UtcTicks);
            SaveSelectedMeetings(connection, session);
            if (anchor is not null) LinkSession(connection, session.Id, anchor, "anchor");
            session.AnchorMeetingId = Session(connection, session.Id).AnchorMeetingId;
            return session;
        });
    }

    /// <summary>Threads anchored to, scoped to, or with evidence from this meeting. Selection alone is not consultation.</summary>
    public IReadOnlyList<MeetingChatSession> Sessions(string? meetingId = null) => database.Read(connection =>
    {
        var filter = meetingId is null ? "" : """
            WHERE EXISTS (SELECT 1 FROM meeting_chat_thread_meetings sm WHERE sm.session_id = s.id AND sm.meeting_id = $p0)
               OR EXISTS (SELECT 1 FROM meeting_chat_turns t JOIN meeting_chat_dependencies d ON d.turn_id = t.id
                          WHERE t.session_id = s.id AND d.meeting_id = $p0)
            """;
        return Sql.Rows(connection,
            $"SELECT s.body, (SELECT meeting_id FROM meeting_chat_thread_meetings WHERE session_id = s.id AND role = 'anchor') " +
            $"FROM meeting_chat_threads s {filter} ORDER BY s.updated_at_utc DESC, s.id",
            ReadSession, meetingId is null ? [] : [meetingId]);
    });

    public bool HasChatHistory() => database.Read(connection =>
        Sql.Rows(connection, "SELECT 1 FROM meeting_chat_turns LIMIT 1", _ => true).Count > 0);

    public IReadOnlyList<MeetingChatTurn> Turns(Guid sessionId) => database.Read(connection =>
        Sql.Rows(connection, "SELECT body FROM meeting_chat_turns WHERE session_id = $p0 ORDER BY ordinal",
            reader => MeetingChatJson.Decode<MeetingChatTurn>(reader.GetString(0)), Key(sessionId)));

    /// <summary>
    /// Changing scope does not erase conversation. Only current, completed exchanges are reusable and their
    /// inherited dependencies stay deletion-tracked. Historical citations are never returned as fresh evidence.
    /// </summary>
    public (IReadOnlyList<MeetingChatTurn> Turns, IReadOnlyList<MeetingChatDependency> Dependencies) HistoryContext(
        MeetingChatTurn turn, int? byteLimit = null, CancellationToken cancellationToken = default) =>
        database.Read(connection => HistoryContext(connection, turn, byteLimit, cancellationToken));

    private static (IReadOnlyList<MeetingChatTurn>, IReadOnlyList<MeetingChatDependency>) HistoryContext(
        SqliteConnection connection, MeetingChatTurn turn, int? byteLimit, CancellationToken cancellationToken)
    {
        var candidates = Sql.Rows(connection,
            "SELECT body FROM meeting_chat_turns WHERE session_id = $p0 AND ordinal < $p1 AND state = 'completed' ORDER BY ordinal DESC",
            reader => MeetingChatJson.Decode<MeetingChatTurn>(reader.GetString(0)), Key(turn.SessionId), turn.Ordinal);
        var selected = new List<MeetingChatTurn>();
        var dependencies = new HashSet<MeetingChatDependency>();
        var revisions = new Dictionary<string, string?>();
        var bytes = 0;
        foreach (var previous in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var inherited = TurnDependencies(connection, previous.Id);
            var current = true;
            foreach (var dependency in inherited)
            {
                if (!revisions.TryGetValue(dependency.MeetingId, out var revision))
                    revisions[dependency.MeetingId] = revision = Snapshots(connection, Single(dependency.MeetingId)).FirstOrDefault()?.Revision;
                if (revision != dependency.Revision) { current = false; break; }
            }
            if (!current || previous.OriginalAnswer is null) continue;
            var block = MeetingChatHistory.PromptBlock(previous);
            var size = Encoding.UTF8.GetByteCount(block);
            if (byteLimit is { } limit && bytes + size > limit) continue;
            bytes += size;
            previous.Citations = [];
            previous.EditableDraft = null;
            selected.Insert(0, previous);
            dependencies.UnionWith(inherited);
        }
        return (selected, dependencies.ToList());
    }

    public MeetingChatRollingSummary? RollingSummary(MeetingChatTurn turn) => database.Write(connection =>
    {
        var session = Session(connection, turn.SessionId);
        if (session.RollingSummary is not { } summary || summary.ThroughOrdinal >= turn.Ordinal) return null;
        var prefix = HistoryContext(connection, turn, null, default).Item1.Where(previous => previous.Ordinal <= summary.ThroughOrdinal).ToList();
        if (!SameFingerprints(Fingerprints(prefix), summary.CoveredTurnFingerprints) ||
            !DependenciesFor(connection, prefix).SetEquals(summary.Dependencies) ||
            !SourcesMatch(connection, summary.Dependencies))
        {
            session.RollingSummary = null;
            SaveSession(connection, session);
            return null;
        }
        return summary;
    });

    /// <summary>Callers pass exactly the raw exchanges summarized, including an earlier extended prefix.</summary>
    public bool SaveRollingSummary(string text, IReadOnlyList<MeetingChatTurn> coveredTurns, MeetingChatTurn request)
    {
        text = text.Trim();
        if (text.Length == 0 || Encoding.UTF8.GetByteCount(text) > 128_000 || coveredTurns.Count == 0) return false;
        var through = coveredTurns.Max(turn => turn.Ordinal);
        if (through >= request.Ordinal || !coveredTurns.All(turn => turn.SessionId == request.SessionId && turn.State == MeetingChatTurnState.Completed))
            return false;
        return database.Write(connection =>
        {
            if (Turn(connection, request.Id) is not { IsPending: true } current || current.AttemptId != request.AttemptId) return false;
            var session = Session(connection, request.SessionId);
            var prefix = HistoryContext(connection, request, null, default).Item1.Where(turn => turn.Ordinal <= through).ToList();
            var expected = Fingerprints(coveredTurns);
            if (expected.Count != coveredTurns.Count || !SameFingerprints(Fingerprints(prefix), expected)) return false;
            // A slower earlier compaction must never replace a newer prefix.
            if (session.RollingSummary is { } existing && existing.ThroughOrdinal > through) return false;
            var dependencies = DependenciesFor(connection, prefix);
            if (!SourcesMatch(connection, dependencies)) return false;
            session.RollingSummary = new MeetingChatRollingSummary(text, through, expected, dependencies.ToList());
            SaveSession(connection, session);
            return true;
        });
    }

    public void UpdateSession(Guid id, string? title, MeetingChatScope? scope) => database.Write(connection =>
    {
        var session = Session(connection, id);
        if (title is not null) { session.Title = title; session.TitleIsAutomatic = false; }
        if (scope is not null && scope != session.Scope)
        {
            session.Scope = scope;
            session.ContextStartOrdinal = NextOrdinal(connection, id);
            SaveSelectedMeetings(connection, session);
        }
        session.UpdatedAt = DateTimeOffset.UtcNow;
        SaveSession(connection, session);
    });

    public MeetingChatTurn BeginTurn(Guid sessionId, string question, MeetingChatScope scope, string provider, string model, Guid? attemptId = null) =>
        database.Write(connection =>
        {
            var session = Session(connection, sessionId);
            var turn = new MeetingChatTurn
            {
                Id = Guid.NewGuid(), SessionId = sessionId, Ordinal = NextOrdinal(connection, sessionId), Question = question, Scope = scope,
                State = MeetingChatTurnState.Finding, Provider = provider, Model = model, AttemptId = attemptId ?? Guid.NewGuid(),
                ScopeLabel = ScopeLabel(connection, scope)
            };
            Sql.Execute(connection, "INSERT INTO meeting_chat_turns VALUES ($p0, $p1, $p2, $p3, $p4)",
                Key(turn.Id), Key(sessionId), turn.Ordinal, StateKey(turn.State), MeetingChatJson.Encode(turn));
            if (turn.Ordinal == 0 && session.TitleIsAutomatic == true) session.Title = Prefix(question, 64);
            session.UpdatedAt = DateTimeOffset.UtcNow;
            SaveSession(connection, session);
            return turn;
        });

    public void AttachEvidence(Guid turnId, IReadOnlyList<MeetingChatDependency> dependencies, Guid? attemptId = null) => database.Write(connection =>
    {
        if (Turn(connection, turnId) is not { IsPending: true } turn || (attemptId is not null && turn.AttemptId != attemptId))
            throw MeetingChatException.MissingSession();
        if (!SourcesMatch(connection, TurnDependencies(connection, turnId).Concat(dependencies))) throw MeetingChatException.SourceChanged();
        foreach (var dependency in dependencies) SaveDependency(connection, turnId, dependency);
        turn.State = MeetingChatTurnState.Writing;
        SaveTurn(connection, turn);
    });

    public IReadOnlyList<MeetingChatDependency> Dependencies(Guid turnId) => database.Read(connection => TurnDependencies(connection, turnId));

    public bool DependenciesAreCurrent(IReadOnlyList<MeetingChatDependency> dependencies) =>
        database.Read(connection => SourcesMatch(connection, dependencies));

    public bool FinishTurn(Guid turnId, string answer, IReadOnlyList<MeetingChatCitation> citations, IReadOnlyList<MeetingChatDependency> dependencies,
        MeetingChatCoverage? coverage = null, bool isDraft = false, Guid? attemptId = null) => database.Write(connection =>
    {
        if (Turn(connection, turnId) is not { IsPending: true } turn || (attemptId is not null && turn.AttemptId != attemptId) ||
            !SourcesMatch(connection, dependencies)) return false;
        // Persist every consulted source, including those supplied only at completion; otherwise deleting
        // that meeting could leave its derived answer behind.
        var all = TurnDependencies(connection, turnId).Concat(dependencies)
            .Concat(citations.Select(citation => new MeetingChatDependency(citation.MeetingId, citation.Revision))).Distinct().ToList();
        if (!SourcesMatch(connection, all)) return false;
        foreach (var dependency in all) SaveDependency(connection, turnId, dependency);
        turn.State = MeetingChatTurnState.Completed;
        turn.OriginalAnswer = answer;
        turn.Citations = citations.ToList();
        turn.Error = null;
        turn.Coverage = coverage;
        turn.IsDraft = isDraft;
        SaveTurn(connection, turn);
        return true;
    });

    public void SetTurnState(Guid id, MeetingChatTurnState state, string? error = null, Guid? attemptId = null) => database.Write(connection =>
    {
        if (Turn(connection, id) is not { } turn || turn.State == MeetingChatTurnState.SourceDeleted) return;
        if (attemptId is not null && turn.AttemptId != attemptId) return;
        if (state == MeetingChatTurnState.Writing && !turn.IsPending) return;
        turn.State = state;
        turn.Error = error;
        if (state != MeetingChatTurnState.Completed)
        {
            turn.OriginalAnswer = null;
            turn.EditableDraft = null;
            turn.Citations = [];
        }
        SaveTurn(connection, turn);
    });

    public void SaveDraft(Guid turnId, string text) => database.Write(connection =>
    {
        if (Turn(connection, turnId) is not { State: MeetingChatTurnState.Completed } turn) return;
        turn.EditableDraft = text;
        SaveTurn(connection, turn);
    });

    public MeetingChatTurn? RestartTurn(Guid id, string provider, string model) => database.Write(connection =>
    {
        if (Turn(connection, id) is not { } turn || turn.State == MeetingChatTurnState.SourceDeleted) return null;
        // A later summary cannot precede this retried turn; failed turns were never fingerprinted, so
        // the turn trigger alone cannot invalidate it. The raw conversation can rebuild it.
        var session = Session(connection, turn.SessionId);
        if (session.RollingSummary is { } summary && summary.ThroughOrdinal >= turn.Ordinal)
        {
            session.RollingSummary = null;
            SaveSession(connection, session);
        }
        turn.State = MeetingChatTurnState.Finding;
        turn.OriginalAnswer = null;
        turn.EditableDraft = null;
        turn.Citations = [];
        turn.Error = null;
        turn.Provider = provider;
        turn.Model = model;
        turn.Coverage = null;
        turn.AttemptId = Guid.NewGuid();
        Sql.Execute(connection, "DELETE FROM meeting_chat_dependencies WHERE turn_id = $p0", Key(id));
        SaveTurn(connection, turn);
        return turn;
    });

    /// <summary>
    /// Commits a generated title only while this completed attempt is still the newest turn. Manual and
    /// legacy titles are protected, and changed or deleted sources cannot return through a late response.
    /// </summary>
    public bool UpdateTitleIfAutomatic(Guid turnId, Guid attemptId, string title)
    {
        title = title.Trim();
        if (title.Length == 0 || title.Length > 100 || title.Any(character => character is '\n' or '\r' or (char)0x2028 or (char)0x2029)) return false;
        return database.Write(connection =>
        {
            if (Turn(connection, turnId) is not { State: MeetingChatTurnState.Completed } turn || turn.AttemptId != attemptId ||
                turn.Ordinal != NextOrdinal(connection, turn.SessionId) - 1) return false;
            var session = Session(connection, turn.SessionId);
            if (session.TitleIsAutomatic != true || session.GeneratedTitleTurnId is not null || session.Scope != turn.Scope ||
                turn.Ordinal < session.ContextStartOrdinal) return false;
            if (!SourcesMatch(connection, TurnDependencies(connection, turnId))) return false;
            session.Title = title;
            session.GeneratedTitleTurnId = turnId;
            SaveSession(connection, session);
            return true;
        });
    }

    public void DeleteSession(Guid id) => database.Write(connection =>
        Sql.Execute(connection, "DELETE FROM meeting_chat_threads WHERE id = $p0", Key(id)));

    /// <summary>Launch recovery: an answer in flight when the app quit can never publish.</summary>
    public void InterruptPendingTurns() => database.Write(connection => Sql.Execute(connection,
        "UPDATE meeting_chat_turns SET state = 'interrupted', body = json_set(body, '$.state', 'interrupted') WHERE state IN ('finding', 'writing')"));

    public long SourceMutationVersion() => database.Read(SourceVersion);

    public IReadOnlyList<MeetingChatSourceSnapshot> SourceSnapshots(MeetingChatScope scope) => database.Read(connection =>
    {
        var result = new List<MeetingChatSourceSnapshot>();
        string after = "";
        while (Snapshots(connection, scope.Selection, after) is { Count: > 0 } page)
        {
            after = page[^1].MeetingId;
            result.AddRange(page.Where(source =>
                (scope.StartDate is null || source.StartDate >= scope.StartDate) &&
                (scope.EndDateExclusive is null || source.StartDate < scope.EndDateExclusive) &&
                !string.IsNullOrWhiteSpace(source.Transcript + source.ManualNotes + source.GeneratedNotes)));
        }
        return result;
    });

    /// <summary>Picker metadata is available immediately, even while the passage index is cold.</summary>
    public IReadOnlyList<MeetingChatSourceChoice> SourceChoices() => database.Read(connection =>
        Sql.Rows(connection, $"""
            SELECT m.id, m.title, m.created_at_utc, m.folder_id FROM meetings m
            WHERE {FinishedMeeting("m")} AND (
              length(trim(COALESCE({TranscriptOf("m")}, ''))) > 0 OR
              length(trim(COALESCE((SELECT content FROM meeting_notes WHERE meeting_id = m.id AND kind = 'manual'), ''))) > 0 OR
              EXISTS (SELECT 1 FROM meeting_notes WHERE meeting_id = m.id AND kind = 'generated' AND length(trim(content)) > 0
                      AND content NOT GLOB '## Summary failed*' AND content NOT GLOB '## Raw Transcript*'))
            ORDER BY m.created_at_utc DESC, m.id DESC
            """,
            reader => new MeetingChatSourceChoice(reader.GetString(0), reader.GetString(1), Utc(reader.GetInt64(2)), reader.Text(3))));

    // ---- Retrieval -------------------------------------------------------------------------------

    private static readonly HashSet<string> StopWords =
    [
        "what", "when", "where", "who", "how", "did", "do", "we", "i", "the", "a", "an", "is", "was", "were", "about", "and", "to",
        "of", "in", "it", "that", "this", "my", "our", "please", "tell", "me"
    ];

    /// <summary>Background warm-up before the first question.</summary>
    public MeetingChatRetrievalMetrics PrepareIndex(CancellationToken cancellationToken = default) => ReconcileIndex(cancellationToken);

    public MeetingChatEvidence Retrieve(string question, MeetingChatScope scope, IReadOnlyList<string>? priorQuestions = null,
        bool broadRecap = false, bool includeScopeContext = false, CancellationToken cancellationToken = default)
    {
        var metrics = ReconcileIndex(cancellationToken);
        // Warm searches neither load meeting text nor take the writer transaction.
        return database.Read(connection =>
        {
            var (scopeCondition, scopeBindings) = ScopeFilter(scope);
            var eligible = Sql.Rows(connection, $"SELECT s.meeting_id, s.revision FROM meeting_chat_index_state s WHERE {scopeCondition}",
                reader => (Id: reader.GetString(0), Revision: reader.GetString(1)), scopeBindings).ToDictionary(row => row.Id, row => row.Revision);
            var tokens = Words().Matches((question + " " + string.Join(" ", (priorQuestions ?? []).TakeLast(2))).ToLowerInvariant())
                .Select(match => match.Value).Where(token => !StopWords.Contains(token)).ToList();
            const string originalsFirst = "CASE WHEN json_extract(p.body, '$.kind') = 'generatedNotes' THEN 1 ELSE 0 END";
            const string baseJoin = "JOIN meeting_chat_index_state s ON s.meeting_id = p.meeting_id AND s.revision = p.revision";
            var representativeOrder = "ORDER BY ROW_NUMBER() OVER (PARTITION BY p.meeting_id, json_extract(p.body, '$.kind') " +
                $"ORDER BY CAST(json_extract(p.body, '$.range.location') AS INTEGER)), s.start_time_utc DESC, {originalsFirst}, p.id LIMIT {CandidateLimit + 1}";
            List<MeetingChatPassage> candidates;
            if (broadRecap)
            {
                candidates = ReadPassages(connection, $"SELECT p.body FROM meeting_chat_passages p {baseJoin} WHERE {scopeCondition} {representativeOrder}", scopeBindings);
            }
            else if (tokens.Count > 0 && TryFts(connection, tokens, scopeCondition, scopeBindings, baseJoin, originalsFirst) is { } matched)
            {
                candidates = matched;
            }
            else
            {
                var terms = (tokens.Count == 0 ? new List<string> { question.Trim() } : tokens).Where(term => term.Length > 0).ToList();
                var offset = scopeBindings.Length;
                var condition = terms.Count == 0 ? "0" : string.Join(" OR ", terms.Select((_, index) => $"p.text LIKE $p{offset + index} ESCAPE '\\'"));
                var bindings = scopeBindings.Concat(terms.Select(term =>
                    (object?)$"%{term.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_")}%")).ToArray();
                candidates = ReadPassages(connection, $"SELECT p.body FROM meeting_chat_passages p {baseJoin} WHERE {scopeCondition} AND ({condition}) " +
                    $"ORDER BY {originalsFirst}, s.start_time_utc DESC, p.id LIMIT {CandidateLimit + 1}", bindings);
            }
            metrics.DecodedCandidateCount = candidates.Count;
            var candidateLimited = candidates.Count > CandidateLimit;
            candidates = candidates.Take(CandidateLimit).Where(IsContentPassage).ToList();
            var supplyingContext = includeScopeContext && scope.Selection.Kind == MeetingChatSelectionKind.Meetings && scope.Selection.Ids.Count > 0;
            if (supplyingContext)
            {
                // An explicit attachment supplies context even when the question's vocabulary never occurs in
                // the meeting. Lexical matches first, then representative source context.
                var context = ReadPassages(connection, $"SELECT p.body FROM meeting_chat_passages p {baseJoin} WHERE {scopeCondition} {representativeOrder}", scopeBindings);
                metrics.DecodedCandidateCount += context.Count;
                candidateLimited |= context.Count > CandidateLimit;
                var content = context.Take(CandidateLimit).Where(IsContentPassage).ToList();
                var representedKinds = new HashSet<string>();
                var representatives = content.Where(passage => representedKinds.Add($"{passage.MeetingId}:{passage.Kind}"));
                var seen = new HashSet<string>();
                candidates = candidates.Concat(representatives).Concat(content).Where(passage => seen.Add(passage.Id)).ToList();
            }
            var packetLimit = supplyingContext ? 14_000 : 10_000;
            var selected = new List<MeetingChatPassage>();
            var usedBytes = 0;
            var packetBytes = 0;
            foreach (var passage in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var excerptBytes = Encoding.UTF8.GetByteCount(passage.Excerpt);
                if (usedBytes + excerptBytes > 12_000) continue;
                passage.SourceKey = $"S{selected.Count + 1}";
                var footprint = MeetingChatPassages.PromptByteCount(passage);
                if (packetBytes + footprint > packetLimit) continue;
                packetBytes += footprint;
                usedBytes += excerptBytes;
                selected.Add(passage);
            }
            var represented = selected.Select(passage => passage.MeetingId).ToHashSet();
            return new MeetingChatEvidence(scope, selected,
                represented.Order(StringComparer.Ordinal).Select(id => new MeetingChatDependency(id, eligible[id])).ToList(),
                new MeetingChatCoverage(eligible.Count, represented.Count,
                    (broadRecap || supplyingContext) && (candidateLimited || selected.Count < candidates.Count || represented.Count < eligible.Count)),
                metrics);
        });
    }

    private static List<MeetingChatPassage>? TryFts(SqliteConnection connection, List<string> tokens, string scopeCondition, object?[] scopeBindings,
        string baseJoin, string originalsFirst)
    {
        try
        {
            var match = string.Join(" OR ", tokens.Select(token => $"\"{token}\""));
            return ReadPassages(connection, $"SELECT p.body FROM meeting_chat_fts f JOIN meeting_chat_passages p ON p.seq = f.rowid {baseJoin} " +
                $"WHERE {scopeCondition} AND meeting_chat_fts MATCH $p{scopeBindings.Length} ORDER BY bm25(meeting_chat_fts), {originalsFirst} LIMIT {CandidateLimit + 1}",
                [.. scopeBindings, match]);
        }
        catch (SqliteException)
        {
            return null;
        }
    }

    private MeetingChatRetrievalMetrics ReconcileIndex(CancellationToken cancellationToken)
    {
        var metrics = new MeetingChatRetrievalMetrics();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var done = database.Write(connection =>
            {
                var dirty = Sql.Rows(connection, "SELECT meeting_id FROM meeting_chat_dirty_sources ORDER BY meeting_id LIMIT 200", reader => reader.GetString(0));
                if (dirty.Count == 0) return true;
                var snapshots = Snapshots(connection, MeetingChatSelection.Meetings(dirty), limit: dirty.Count);
                metrics.SourceSnapshotCount += snapshots.Count;
                var byId = snapshots.ToDictionary(source => source.MeetingId);
                foreach (var id in dirty)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Sql.Execute(connection, "DELETE FROM meeting_chat_passages WHERE meeting_id = $p0", id);
                    Sql.Execute(connection, "DELETE FROM meeting_chat_index_state WHERE meeting_id = $p0", id);
                    if (byId.TryGetValue(id, out var source))
                    {
                        metrics.ReindexedMeetingCount++;
                        var passages = MeetingChatPassages.Extract(source);
                        foreach (var passage in passages)
                        {
                            var searchable = source.Title + " " + string.Join(" ", source.ParticipantNames) + " " + passage.Excerpt;
                            Sql.Execute(connection, "INSERT INTO meeting_chat_passages (id, meeting_id, revision, text, body) VALUES ($p0, $p1, $p2, $p3, $p4)",
                                passage.Id, id, passage.Revision, searchable, MeetingChatJson.Encode(passage));
                        }
                        if (passages.Count > 0)
                            Sql.Execute(connection, "INSERT INTO meeting_chat_index_state VALUES ($p0, $p1, $p2, $p3)",
                                id, source.Revision, source.StartDate.UtcTicks, source.FolderId);
                    }
                    Sql.Execute(connection, "DELETE FROM meeting_chat_dirty_sources WHERE meeting_id = $p0", id);
                }
                return false;
            });
            if (done) return metrics;
        }
    }

    private static (string Condition, object?[] Bindings) ScopeFilter(MeetingChatScope scope)
    {
        var conditions = new List<string>();
        var bindings = new List<object?>();
        switch (scope.Selection.Kind)
        {
            case MeetingChatSelectionKind.Folder:
                conditions.Add($"s.folder_id = $p{bindings.Count}");
                bindings.Add(scope.Selection.FolderId);
                break;
            case MeetingChatSelectionKind.Meetings:
                var ids = scope.Selection.Ids;
                conditions.Add(ids.Count == 0 ? "0" : $"s.meeting_id IN ({string.Join(", ", ids.Select((_, index) => $"$p{bindings.Count + index}"))})");
                bindings.AddRange(ids);
                break;
        }
        if (scope.StartDate is { } start)
        {
            conditions.Add($"s.start_time_utc >= $p{bindings.Count}");
            bindings.Add(start.UtcTicks);
        }
        if (scope.EndDateExclusive is { } end)
        {
            conditions.Add($"s.start_time_utc < $p{bindings.Count}");
            bindings.Add(end.UtcTicks);
        }
        return (conditions.Count == 0 ? "1" : string.Join(" AND ", conditions), bindings.ToArray());
    }

    /// <summary>A Markdown section heading is navigation, not sufficient evidence; filtered at read time so caches agree.</summary>
    private static bool IsContentPassage(MeetingChatPassage passage) =>
        passage.Kind == MeetingChatSourceKind.Transcript || !MarkdownHeading().IsMatch(passage.Excerpt.Trim());

    private static List<MeetingChatPassage> ReadPassages(SqliteConnection connection, string sql, object?[] bindings) =>
        Sql.Rows(connection, sql, reader => MeetingChatJson.Decode<MeetingChatPassage>(reader.GetString(0)), bindings);

    // ---- Sources ---------------------------------------------------------------------------------

    private static string FinishedMeeting(string alias) =>
        $"{alias}.session_state NOT IN ('Idle', 'Preparing', 'Recording', 'DegradedRecording', 'Stopping', 'Finalizing')";

    private static string TranscriptOf(string alias) =>
        $"COALESCE((SELECT content FROM meeting_transcripts WHERE meeting_id = {alias}.id AND kind = 'edited'), " +
        $"(SELECT content FROM meeting_transcripts WHERE meeting_id = {alias}.id AND kind = 'raw'))";

    private static MeetingChatSelection Single(string meetingId) => MeetingChatSelection.Meetings([meetingId]);

    /// <summary>
    /// The revision hashes content only: folder membership changes retrieval scope, never what an answer
    /// was derived from. A recording still in progress is never a source.
    /// </summary>
    private static List<MeetingChatSourceSnapshot> Snapshots(SqliteConnection connection, MeetingChatSelection selection, string after = "", int limit = 200)
    {
        var clause = $"{FinishedMeeting("m")} AND m.id > $p0";
        var bindings = new List<object?> { after };
        switch (selection.Kind)
        {
            case MeetingChatSelectionKind.Folder:
                clause += " AND m.folder_id = $p1";
                bindings.Add(selection.FolderId);
                break;
            case MeetingChatSelectionKind.Meetings:
                if (selection.Ids.Count == 0) return [];
                clause += $" AND m.id IN ({string.Join(", ", selection.Ids.Select((_, index) => $"$p{index + 1}"))})";
                bindings.AddRange(selection.Ids);
                break;
        }
        bindings.Add(limit);
        return Sql.Rows(connection, $"""
            SELECT m.id, m.title, m.created_at_utc, m.folder_id, COALESCE({TranscriptOf("m")}, ''),
              COALESCE((SELECT content FROM meeting_notes WHERE meeting_id = m.id AND kind = 'manual'), ''),
              COALESCE((SELECT content FROM meeting_notes WHERE meeting_id = m.id AND kind = 'generated'), ''),
              m.session_state, m.source_path,
              COALESCE((SELECT json_group_array(alias) FROM (SELECT alias FROM speaker_aliases WHERE meeting_id = m.id ORDER BY speaker_key)), '[]')
            FROM meetings m WHERE {clause} ORDER BY m.id LIMIT $p{bindings.Count - 1}
            """, reader =>
        {
            var generated = reader.GetString(6);
            var fields = new[]
            {
                reader.GetString(1), reader.GetInt64(2).ToString(CultureInfo.InvariantCulture), "", reader.GetString(4), reader.GetString(5),
                generated, reader.GetString(7), reader.GetString(8), reader.GetString(9)
            };
            var revision = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(fields)));
            var notes = generated.StartsWith("## Summary failed", StringComparison.Ordinal) ||
                        generated.StartsWith("## Raw Transcript", StringComparison.Ordinal) ? "" : generated;
            return new MeetingChatSourceSnapshot(reader.GetString(0), fields[0], Utc(reader.GetInt64(2)), reader.Text(3), fields[3], fields[4], notes,
                JsonSerializer.Deserialize<List<string>>(fields[8]) ?? [], revision);
        }, bindings.ToArray());
    }

    private static bool SourcesMatch(SqliteConnection connection, IEnumerable<MeetingChatDependency> dependencies) =>
        dependencies.All(dependency => Snapshots(connection, Single(dependency.MeetingId)).FirstOrDefault()?.Revision == dependency.Revision);

    // ---- Rows ------------------------------------------------------------------------------------

    private static MeetingChatSession Session(SqliteConnection connection, Guid id) =>
        Sql.Rows(connection,
            "SELECT body, (SELECT meeting_id FROM meeting_chat_thread_meetings WHERE session_id = meeting_chat_threads.id AND role = 'anchor') " +
            "FROM meeting_chat_threads WHERE id = $p0", ReadSession, Key(id)).FirstOrDefault() ?? throw MeetingChatException.MissingSession();

    private static MeetingChatSession ReadSession(SqliteDataReader reader)
    {
        var session = MeetingChatJson.Decode<MeetingChatSession>(reader.GetString(0));
        session.AnchorMeetingId = reader.Text(1);
        return session;
    }

    private static MeetingChatTurn? Turn(SqliteConnection connection, Guid id) =>
        Sql.Rows(connection, "SELECT body FROM meeting_chat_turns WHERE id = $p0",
            reader => MeetingChatJson.Decode<MeetingChatTurn>(reader.GetString(0)), Key(id)).FirstOrDefault();

    private static List<MeetingChatDependency> TurnDependencies(SqliteConnection connection, Guid turnId) =>
        Sql.Rows(connection, "SELECT meeting_id, revision FROM meeting_chat_dependencies WHERE turn_id = $p0",
            reader => new MeetingChatDependency(reader.GetString(0), reader.GetString(1)), Key(turnId));

    private static HashSet<MeetingChatDependency> DependenciesFor(SqliteConnection connection, IEnumerable<MeetingChatTurn> turns) =>
        turns.SelectMany(turn => TurnDependencies(connection, turn.Id)).ToHashSet();

    private static Dictionary<string, string> Fingerprints(IEnumerable<MeetingChatTurn> turns) => turns.ToDictionary(
        turn => Key(turn.Id),
        turn => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new[]
        {
            Key(turn.Id), turn.Ordinal.ToString(CultureInfo.InvariantCulture), turn.AttemptId is { } attempt ? Key(attempt) : "", turn.Question,
            turn.OriginalAnswer ?? ""
        }))));

    private static bool SameFingerprints(IReadOnlyDictionary<string, string> left, IReadOnlyDictionary<string, string> right) =>
        left.Count == right.Count && left.All(pair => right.TryGetValue(pair.Key, out var value) && value == pair.Value);

    private static void SaveDependency(SqliteConnection connection, Guid turnId, MeetingChatDependency dependency) =>
        Sql.Execute(connection, "INSERT OR REPLACE INTO meeting_chat_dependencies VALUES ($p0, $p1, $p2)", Key(turnId), dependency.MeetingId, dependency.Revision);

    private static void LinkSession(SqliteConnection connection, Guid sessionId, string meetingId, string role) =>
        Sql.Execute(connection, "INSERT OR IGNORE INTO meeting_chat_thread_meetings SELECT $p0, id, $p1 FROM meetings WHERE id = $p2",
            Key(sessionId), role, meetingId);

    private static void SaveSelectedMeetings(SqliteConnection connection, MeetingChatSession session)
    {
        Sql.Execute(connection, "DELETE FROM meeting_chat_thread_meetings WHERE session_id = $p0 AND role = 'selected'", Key(session.Id));
        if (session.Scope.Selection.Kind == MeetingChatSelectionKind.Meetings)
            foreach (var id in session.Scope.Selection.Ids.Distinct()) LinkSession(connection, session.Id, id, "selected");
    }

    private static void SaveSession(SqliteConnection connection, MeetingChatSession session) =>
        Sql.Execute(connection, "UPDATE meeting_chat_threads SET body = $p0, updated_at_utc = $p1 WHERE id = $p2",
            MeetingChatJson.Encode(session), session.UpdatedAt.UtcTicks, Key(session.Id));

    private static void SaveTurn(SqliteConnection connection, MeetingChatTurn turn) =>
        Sql.Execute(connection, "UPDATE meeting_chat_turns SET state = $p0, body = $p1 WHERE id = $p2",
            StateKey(turn.State), MeetingChatJson.Encode(turn), Key(turn.Id));

    private static int NextOrdinal(SqliteConnection connection, Guid sessionId) =>
        Sql.Rows(connection, "SELECT COALESCE(MAX(ordinal) + 1, 0) FROM meeting_chat_turns WHERE session_id = $p0",
            reader => reader.GetInt32(0), Key(sessionId))[0];

    private static long SourceVersion(SqliteConnection connection) =>
        Sql.Rows(connection, "SELECT version FROM meeting_chat_source_version WHERE id = 1", reader => reader.GetInt64(0)).FirstOrDefault();

    private static string ScopeLabel(SqliteConnection connection, MeetingChatScope scope)
    {
        var label = scope.Selection.Kind switch
        {
            MeetingChatSelectionKind.All => "All saved meetings",
            MeetingChatSelectionKind.Folder => (Sql.Rows(connection, "SELECT name FROM folders WHERE id = $p0", reader => reader.GetString(0),
                scope.Selection.FolderId).FirstOrDefault() ?? "Folder") + " · This folder only",
            _ => scope.Selection.Ids.Count == 1 ? "1 selected meeting" : $"{scope.Selection.Ids.Count} selected meetings"
        };
        if (scope.StartDate is { } start) label += " · From " + start.ToLocalTime().ToString("MMM d, yyyy", CultureInfo.CurrentCulture);
        if (scope.EndDateExclusive is { } end) label += " through " + end.AddMilliseconds(-1).ToLocalTime().ToString("MMM d, yyyy", CultureInfo.CurrentCulture);
        return label;
    }

    private static string Key(Guid id) => id.ToString();
    private static string StateKey(MeetingChatTurnState state) => JsonNamingPolicy.CamelCase.ConvertName(state.ToString());
    private static DateTimeOffset Utc(long ticks) => new(ticks, TimeSpan.Zero);

    private static string Prefix(string text, int graphemes)
    {
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        var builder = new StringBuilder();
        for (var count = 0; count < graphemes && enumerator.MoveNext(); count++) builder.Append(enumerator.GetTextElement());
        return builder.ToString();
    }

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex Words();

    [GeneratedRegex(@"^#{1,6}\s+[^\r\n]+$")]
    private static partial Regex MarkdownHeading();

    private static class Sql
    {
        public static void Execute(SqliteConnection connection, string sql, params object?[] bindings)
        {
            using var command = Command(connection, sql, bindings);
            command.ExecuteNonQuery();
        }

        public static List<T> Rows<T>(SqliteConnection connection, string sql, Func<SqliteDataReader, T> map, params object?[] bindings)
        {
            using var command = Command(connection, sql, bindings);
            using var reader = command.ExecuteReader();
            var result = new List<T>();
            while (reader.Read()) result.Add(map(reader));
            return result;
        }

        private static SqliteCommand Command(SqliteConnection connection, string sql, object?[] bindings)
        {
            var command = connection.CreateCommand();
            command.CommandText = sql;
            for (var index = 0; index < bindings.Length; index++)
                command.Parameters.AddWithValue($"$p{index}", bindings[index] ?? DBNull.Value);
            return command;
        }
    }
}
