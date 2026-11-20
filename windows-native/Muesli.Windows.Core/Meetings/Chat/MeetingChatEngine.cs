using System.Text;

namespace Muesli.Windows.Services;

/// <summary>Live progress for one answer; callbacks may arrive off the UI thread.</summary>
public sealed class MeetingChatProgress
{
    public Action<string>? Phase { get; init; }
    public Action<MeetingChatToolCall, bool>? Activity { get; init; }
    public Action<string>? ActivityOutput { get; init; }
    /// <summary>macOS reasoningSummary(iteration, text): transient display text, never saved.</summary>
    public Func<int, string, Task>? ReasoningSummary { get; init; }
}

/// <summary>
/// Port of the macOS MeetingChatWorker and the answer body of MeetingChatCoordinator.launch (2f10b23d).
/// UI state (selection, composer drafts) stays with the page view model.
/// </summary>
public sealed class MeetingChatEngine(MeetingChatStore store)
{
    private (Guid TurnId, long SourceVersion)? _summaryValidation;

    public MeetingChatStore Store => store;

    public (MeetingChatEvidence Evidence, IReadOnlyList<MeetingChatTurn> History) Evidence(MeetingChatTurn turn, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (history, dependencies) = store.HistoryContext(turn, cancellationToken: cancellationToken);
        // Prior conversation can be transformed without re-reading meetings. Start with no source packet
        // and let the model explicitly search the current scope if it needs new facts. Removing a meeting
        // affects retrieval, not validated history.
        var selection = turn.Scope.Selection;
        var explicitlyEmpty = selection.Kind == MeetingChatSelectionKind.Meetings && selection.Ids.Count == 0;
        if (history.Count > 0 || explicitlyEmpty)
        {
            var eligible = explicitlyEmpty ? 0 : store.SourceChoices().Count(source => InScope(source, turn.Scope));
            var empty = new MeetingChatEvidence(turn.Scope, [], dependencies, new MeetingChatCoverage(eligible, 0, false), new MeetingChatRetrievalMetrics());
            store.AttachEvidence(turn.Id, dependencies, turn.AttemptId);
            return (empty, history);
        }
        var broadIntent = new[] { "recap", "summar", "decisions", "decide", "next steps", "action items", "follow-up" }
            .Any(word => turn.Question.Contains(word, StringComparison.CurrentCultureIgnoreCase));
        // Every question gets lexical relevance first. Broad vocabulary never disables a topic match.
        var attachedContext = selection.Kind == MeetingChatSelectionKind.Meetings && selection.Ids.Count > 0;
        var evidence = store.Retrieve(turn.Question, turn.Scope, history.Select(item => item.Question).ToList(),
            includeScopeContext: attachedContext, cancellationToken: cancellationToken);
        if (broadIntent && !attachedContext)
        {
            var representative = store.Retrieve(turn.Question, turn.Scope, broadRecap: true, cancellationToken: cancellationToken);
            var selected = new List<MeetingChatPassage>();
            var seen = new HashSet<string>();
            int bytes = 0, packetBytes = 0;
            void Take(IEnumerable<MeetingChatPassage> passages, int byteCap, int packetCap)
            {
                foreach (var candidate in passages)
                {
                    var size = Encoding.UTF8.GetByteCount(candidate.Excerpt);
                    if (bytes + size > byteCap) continue;
                    var passage = candidate with { SourceKey = $"S{selected.Count + 1}" };
                    var footprint = MeetingChatPassages.PromptByteCount(passage);
                    if (packetBytes + footprint <= packetCap && seen.Add(passage.Id))
                    {
                        selected.Add(passage); bytes += size; packetBytes += footprint;
                    }
                }
            }
            Take(evidence.Passages, 6_000, 5_000);
            Take(representative.Passages, 12_000, 10_000);
            for (var index = 0; index < selected.Count; index++) selected[index] = selected[index] with { SourceKey = $"S{index + 1}" };
            evidence = new MeetingChatEvidence(turn.Scope, selected,
                selected.Select(p => new MeetingChatDependency(p.MeetingId, p.Revision)).Distinct().ToList(),
                new MeetingChatCoverage(representative.Coverage.EligibleMeetingCount, selected.Select(p => p.MeetingId).Distinct().Count(),
                    representative.Coverage.IsPartialRecap || representative.Passages.Any(p => selected.All(s => s.Id != p.Id))),
                new MeetingChatRetrievalMetrics
                {
                    SourceSnapshotCount = evidence.Metrics.SourceSnapshotCount + representative.Metrics.SourceSnapshotCount,
                    ReindexedMeetingCount = evidence.Metrics.ReindexedMeetingCount + representative.Metrics.ReindexedMeetingCount,
                    DecodedCandidateCount = evidence.Metrics.DecodedCandidateCount + representative.Metrics.DecodedCandidateCount
                });
        }
        store.AttachEvidence(turn.Id, evidence.Dependencies, turn.AttemptId);
        return (evidence, history);
    }

    public (MeetingChatEvidence Evidence, int NextSourceKey, int IncomingCount, int? NextPage) Execute(
        MeetingChatToolCall call, MeetingChatTurn turn, MeetingChatEvidence evidence, int nextSourceKey, bool native = false,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!store.DependenciesAreCurrent(evidence.Dependencies)) throw MeetingChatException.SourceChanged();
        IReadOnlyList<MeetingChatPassage> incoming;
        var incomingIsPartialRecap = false;
        int? nextPage = null;
        switch (call.Kind)
        {
            case MeetingChatToolKind.Search:
                var searched = store.Retrieve(call.Argument, turn.Scope, cancellationToken: cancellationToken);
                incoming = searched.Passages;
                incomingIsPartialRecap = searched.Coverage.IsPartialRecap;
                break;
            case MeetingChatToolKind.Browse:
                var browsed = store.Retrieve(turn.Question, turn.Scope, broadRecap: true, cancellationToken: cancellationToken);
                incoming = browsed.Passages;
                incomingIsPartialRecap = browsed.Coverage.IsPartialRecap;
                break;
            case MeetingChatToolKind.Read:
                // The capability is an issued source marker, never a model-supplied meeting ID.
                var passage = evidence.Passages.FirstOrDefault(p => p.SourceKey == call.Argument);
                if (passage is null || ScopedSource(passage.MeetingId, turn.Scope) is not { } snapshot || snapshot.Revision != passage.Revision)
                    throw MeetingChatException.SourceChanged();
                var sameKind = MeetingChatPassages.Extract(snapshot).Where(p => p.Kind == passage.Kind).ToList();
                var index = sameKind.FindIndex(p => p.Id == passage.Id);
                if (index < 0) throw MeetingChatException.SourceChanged();
                var from = Math.Max(0, index - 2);
                incoming = sameKind.GetRange(from, Math.Min(sameKind.Count - 1, index + 2) - from + 1);
                break;
            default:
                if (!native) throw MeetingChatException.InvalidResponse();
                var source = ScopedSource(call.Argument, turn.Scope) ?? throw MeetingChatException.SourceChanged();
                (incoming, nextPage) = MeetingPage(source, call.Page, cancellationToken);
                incomingIsPartialRecap = nextPage is not null || call.Page > 0;
                break;
        }
        var selected = new List<MeetingChatPassage>();
        var seen = new HashSet<string>();
        var bytes = 0;
        var nextKey = nextSourceKey;
        var omittedForBudget = false;
        // An empty refinement does not erase evidence already gathered.
        foreach (var candidate in incoming.Concat(evidence.Passages))
        {
            if (!seen.Add(candidate.Id)) continue;
            var existing = evidence.Passages.FirstOrDefault(p => p.Id == candidate.Id);
            var keyed = candidate with { SourceKey = existing?.SourceKey ?? $"S{nextKey}" };
            var size = MeetingChatPassages.PromptByteCount(keyed);
            if (bytes + size > (native ? 200_000 : 10_000))
            {
                // A native tool result must never quietly discard earlier pages or claim to have read
                // a full page that the model cannot see.
                if (native) throw MeetingChatException.PromptTooLarge();
                omittedForBudget = true;
                continue;
            }
            if (existing is null) nextKey++;
            bytes += size;
            selected.Add(keyed);
        }
        var result = evidence with
        {
            Passages = selected,
            Dependencies = evidence.Dependencies.Concat(selected.Select(p => new MeetingChatDependency(p.MeetingId, p.Revision))).Distinct().ToList(),
            // A focused search need not match every eligible meeting. Preserve the retriever's
            // completeness assessment and record actual packet eviction.
            Coverage = evidence.Coverage with
            {
                EvidenceMeetingCount = selected.Select(p => p.MeetingId).Distinct().Count(),
                IsPartialRecap = evidence.Coverage.IsPartialRecap || incomingIsPartialRecap || omittedForBudget
            }
        };
        // Attach before the next network call so deletion invalidates every source already disclosed.
        store.AttachEvidence(turn.Id, result.Dependencies, turn.AttemptId);
        return (result, nextKey, incoming.Count, nextPage);
    }

    private MeetingChatSourceSnapshot? ScopedSource(string id, MeetingChatScope scope)
    {
        if (scope.Selection.Kind == MeetingChatSelectionKind.Meetings && !scope.Selection.Ids.Contains(id)) return null;
        var single = new MeetingChatScope(MeetingChatSelection.Meetings([id]), scope.StartDate, scope.EndDateExclusive);
        var snapshot = store.SourceSnapshots(single).FirstOrDefault();
        if (snapshot is null) return null;
        if (scope.Selection.Kind == MeetingChatSelectionKind.Folder && snapshot.FolderId != scope.Selection.FolderId) return null;
        return snapshot;
    }

    /// <summary>Stable pages cover every source kind in original order with exact ranges. Page boundaries
    /// reserve room for issued source markers before serialization.</summary>
    public static (IReadOnlyList<MeetingChatPassage> Passages, int? NextPage) MeetingPage(MeetingChatSourceSnapshot source, int page,
        CancellationToken cancellationToken = default)
    {
        if (page is < 0 or >= 10_000) throw MeetingChatException.InvalidResponse();
        var currentPage = 0;
        var selected = new List<MeetingChatPassage>();
        var bytes = 0;
        foreach (var passage in MeetingChatPassages.Extract(source))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var size = MeetingChatPassages.PromptByteCount(passage) + 16;
            if (size > 40_000) throw MeetingChatException.PromptTooLarge();
            if (bytes + size > 40_000)
            {
                if (currentPage == page) return (selected, page + 1);
                currentPage++;
                bytes = 0;
            }
            if (currentPage == page) selected.Add(passage);
            bytes += size;
        }
        if (currentPage != page) throw MeetingChatException.InvalidResponse();
        return (selected, null);
    }

    public bool CanPublishSummary(MeetingChatTurn turn)
    {
        var current = store.Turns(turn.SessionId).FirstOrDefault(item => item.Id == turn.Id);
        if (current is not { IsPending: true } || current.AttemptId != turn.AttemptId) return false;
        var version = store.SourceMutationVersion();
        if (_summaryValidation == (turn.Id, version)) return true;
        if (!store.DependenciesAreCurrent(store.Dependencies(turn.Id))) return false;
        _summaryValidation = (turn.Id, version);
        return true;
    }

    public (string Question, string Answer)? TitleContext(MeetingChatTurn turn)
    {
        var session = store.Sessions().FirstOrDefault(item => item.Id == turn.SessionId);
        if (session is not { TitleIsAutomatic: true, GeneratedTitleTurnId: null } || !session.Scope.Equals(turn.Scope)
            || turn.Ordinal < session.ContextStartOrdinal) return null;
        var latest = store.Turns(turn.SessionId).LastOrDefault();
        if (latest is null || latest.Id != turn.Id || latest.AttemptId != turn.AttemptId || latest.State != MeetingChatTurnState.Completed
            || latest.OriginalAnswer is not { } answer || !store.DependenciesAreCurrent(store.Dependencies(turn.Id))) return null;
        return (latest.Question, answer);
    }

    /// <summary>The body of macOS MeetingChatCoordinator.launch: memory, tool loop, atomic finish.
    /// Returns the answer when it was saved, so the caller can schedule a title.</summary>
    public async Task<MeetingChatAnswer> AnswerAsync(MeetingChatTurn turn, IMeetingTextGenerator generator, bool isDraft,
        MeetingChatProgress? progress = null, int? contextWindowTokens = null, CancellationToken cancellationToken = default)
    {
        progress?.Phase?.Invoke("Finding meeting context");
        var (evidence, history) = await Task.Run(() => Evidence(turn, cancellationToken), cancellationToken);
        var native = generator.SupportsNativeTools;
        var budget = new MeetingChatContextBudget(generator.Provider, native, contextWindowTokens);
        var saved = store.RollingSummary(turn);
        var plan = budget.PlanHistory(history, saved?.ThroughOrdinal);
        var rollingSummary = saved?.Text;
        if (plan.OlderTurns.Count > 0 || (rollingSummary is not null && MeetingChatContextBudget.EstimateTokens(rollingSummary) > budget.SummaryTokens))
        {
            progress?.Phase?.Invoke("Updating conversation memory");
            rollingSummary = await budget.SummarizeAsync(plan.OlderTurns, rollingSummary, generator, _ =>
                CanPublishSummary(turn) ? Task.CompletedTask : throw MeetingChatException.SourceChanged(), cancellationToken);
            var through = plan.OlderTurns.Count > 0 ? plan.OlderTurns[^1].Ordinal : saved?.ThroughOrdinal ?? -1;
            if (!store.SaveRollingSummary(rollingSummary, history.Where(item => item.Ordinal <= through).ToList(), turn))
                throw MeetingChatException.SourceChanged();
        }
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Phase?.Invoke("Writing answer");
        var nextSourceKey = evidence.Passages.Select(p => int.TryParse(p.SourceKey.AsSpan(1), out var key) ? key : 0).DefaultIfEmpty(0).Max() + 1;
        // macOS MeetingChatClient.run: native function calling where the provider has it, else the
        // provider-neutral JSON tool loop.
        var run = native ? (RunLoop)MeetingChatNativeHarness.RunAsync : MeetingChatClient.RunAsync;
        if (evidence.Coverage.EligibleMeetingCount == 0 && !MeetingChatClient.HasUsableConversation(plan.RecentTurns, rollingSummary))
            run = MeetingChatClient.RunAsync;
        var (answer, finalEvidence) = await run(turn.Question, evidence, plan.RecentTurns, generator,
            (call, current, ct) => Task.Run(() =>
            {
                var (result, next, count, nextPage) = Execute(call, turn, current, nextSourceKey, native, ct);
                nextSourceKey = next;
                progress?.ActivityOutput?.Invoke($"{count} passages returned · {result.Coverage.EvidenceMeetingCount} meetings in the current source packet");
                return new MeetingChatToolResult(result, count, nextPage);
            }, ct),
            (call, completed) =>
            {
                progress?.Activity?.Invoke(call, completed);
                progress?.Phase?.Invoke(completed ? "Writing answer" : call.Title);
            },
            progress?.Phase, rollingSummary, budget, cancellationToken, progress?.ReasoningSummary);
        cancellationToken.ThrowIfCancellationRequested();
        if (!store.FinishTurn(turn.Id, answer.Markdown, answer.Citations, finalEvidence.Dependencies, answer.Coverage, isDraft, turn.AttemptId))
            throw MeetingChatException.SourceChanged();
        return answer;
    }

    /// <summary>A title is optional metadata: any failure keeps the provisional title.</summary>
    public async Task<bool> GenerateTitleAsync(MeetingChatTurn turn, IMeetingTextGenerator generator, CancellationToken cancellationToken = default)
    {
        try
        {
            if (TitleContext(turn) is not { } exchange || turn.AttemptId is not { } attemptId) return false;
            var raw = await generator.GenerateAsync(MeetingChatClient.ConversationTitleRequest(exchange.Question, exchange.Answer), cancellationToken);
            return MeetingChatClient.ValidatedConversationTitle(raw) is { } title && store.UpdateTitleIfAutomatic(turn.Id, attemptId, title);
        }
        catch (Exception error) when (error is not OperationCanceledException) { return false; }
    }

    private delegate Task<(MeetingChatAnswer, MeetingChatEvidence)> RunLoop(
        string question, MeetingChatEvidence initial, IReadOnlyList<MeetingChatTurn> history, IMeetingTextGenerator generator,
        Func<MeetingChatToolCall, MeetingChatEvidence, CancellationToken, Task<MeetingChatToolResult>> execute,
        Action<MeetingChatToolCall, bool>? activity, Action<string>? progress, string? rollingSummary,
        MeetingChatContextBudget budget, CancellationToken cancellationToken, Func<int, string, Task>? reasoningSummary = null);

    public static string FailureMessage(Exception error) => error switch
    {
        MeetingChatException or MeetingChatHarnessException or MeetingNativeToolTransportException => error.Message,
        MeetingChatContextBudget.SummaryException => "Could not fit conversation memory into this model’s context budget. Try again or choose a model with a larger context window. Your conversation is still saved.",
        SummaryProviderException { SafeReason: "missing-connection" or "missing-key" or "unsupported-provider" } => "Configure your meeting AI connection and model in Settings.",
        SummaryProviderException { SafeReason: "not-signed-in" } => "Your ChatGPT connection needs attention. Sign in again in AI Settings, then retry.",
        SummaryProviderException { SafeReason: "empty-response" } => "Meeting AI returned an empty or incomplete response. Please retry.",
        SummaryProviderException { SafeReason: var reason } when reason.StartsWith("http-", StringComparison.Ordinal) =>
            $"Meeting AI returned HTTP {reason[5..]}. Retry or check your connection settings.",
        HttpRequestException => "Could not connect to meeting AI. Check your internet connection and retry.",
        TimeoutException => "The meeting AI connection timed out. Please retry.",
        _ => "Meeting AI request failed. Please retry or check your connection settings."
    };

    private static bool InScope(MeetingChatSourceChoice source, MeetingChatScope scope)
    {
        var selection = scope.Selection;
        if (selection.Kind == MeetingChatSelectionKind.Folder && source.FolderId != selection.FolderId) return false;
        if (selection.Kind == MeetingChatSelectionKind.Meetings && !selection.Ids.Contains(source.Id)) return false;
        return (scope.StartDate is not { } start || source.StartDate >= start) && (scope.EndDateExclusive is not { } end || source.StartDate < end);
    }
}
