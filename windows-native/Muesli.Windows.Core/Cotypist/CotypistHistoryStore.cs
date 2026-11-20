using Muesli.Windows.Services.Persistence;

namespace Muesli.Windows.Cotypist;

public enum CotypistOutcome
{
    /// <summary>Tab took all of it (possibly after some words were taken one at a time).</summary>
    Accepted,
    /// <summary>Some words were taken one at a time, then the rest was typed over.</summary>
    Partial,
    /// <summary>Typed over without taking any of it.</summary>
    Ignored,
    /// <summary>Gone without typing over it: a click, a focus change, switching apps.</summary>
    Dismissed
}

/// <summary>What the helper reported about one shown suggestion (macOS CotypistSuggestionEvent).</summary>
public sealed record CotypistSuggestionEvent(
    DateTimeOffset Date,
    string AppId,
    string AppName,
    string Trigger,
    string SuggestionText,
    string AcceptedText,
    CotypistOutcome Outcome,
    double? FirstWordLogProb,
    int? FirstMs,
    int? FullMs,
    string ContextSource,
    string Model,
    string ContextBefore = "")
{
    public bool IsTaken => Outcome is CotypistOutcome.Accepted or CotypistOutcome.Partial;
}

public sealed record CotypistAcceptance(string ContextBefore, string Text);

/// <summary>A run of Cotypist use in one app, for the timeline.</summary>
public sealed record CotypistSession(
    long Id,
    DateTimeOffset StartedAt,
    DateTimeOffset LastActivityAt,
    string TargetAppName,
    string TargetAppId,
    IReadOnlyList<CotypistAcceptance> Acceptances,
    int WordsAccepted,
    int SuggestionsShown,
    int SuggestionsAccepted);

public sealed record CotypistInsightsTotals(int WordsAccepted, int SuggestionsAccepted, double? AcceptanceRate, int RateDays);

/// <summary>
/// Port of the macOS DictationStore Cotypist history (2f10b23d). Local only: the user's sentence and
/// the app are kept only for suggestions they took; untaken rows are pruned after 90 days and past
/// the newest 5,000; removing a session hard-deletes it and its suggestions.
/// </summary>
public sealed class CotypistHistoryStore(MuesliDatabase database)
{
    public static readonly TimeSpan UnacceptedRetention = TimeSpan.FromDays(90);
    public const int UnacceptedLimit = 5_000;
    public static readonly TimeSpan SessionGap = TimeSpan.FromMinutes(10);

    /// <summary>An acceptance joins the app's open session or starts one; a suggestion that wasn't
    /// taken only counts toward an open session. Returns the session id.</summary>
    public long? Record(CotypistSuggestionEvent suggestion) => database.Write(connection =>
    {
        var taken = suggestion.IsTaken;
        long? sessionId = null;
        using (var open = Db.Command(connection, """
                   SELECT id FROM cotypist_sessions WHERE target_app_id = $app AND last_activity_at_utc >= $since
                   ORDER BY last_activity_at_utc DESC LIMIT 1;
                   """).Bind("$app", suggestion.AppId).Bind("$since", suggestion.Date - SessionGap))
        {
            if (open.ExecuteScalar() is long id) sessionId = id;
        }
        if (sessionId is null && taken)
        {
            sessionId = Db.Command(connection, """
                    INSERT INTO cotypist_sessions (started_at_utc, last_activity_at_utc, target_app_name, target_app_id)
                    VALUES ($at, $at, $name, $app) RETURNING id;
                    """).Bind("$at", suggestion.Date).Bind("$name", suggestion.AppName).Bind("$app", suggestion.AppId).ScalarInt64();
        }
        if (sessionId is { } session)
            Db.Command(connection, "UPDATE cotypist_sessions SET last_activity_at_utc = $at WHERE id = $id;")
                .Bind("$at", suggestion.Date).Bind("$id", session).Execute();

        var insert = Db.Command(connection, """
                INSERT INTO cotypist_suggestions
                (created_at_utc, session_id, target_app_name, target_app_id, trigger, suggestion_text, accepted_text, outcome,
                 words_suggested, words_accepted, first_word_logprob, first_ms, full_ms, context_source, model, context_before)
                VALUES ($at, $session, $name, $app, $trigger, $text, $accepted, $outcome, $suggested, $acceptedWords,
                        $logprob, $firstMs, $fullMs, $source, $model, $before);
                """)
            .Bind("$at", suggestion.Date).Bind("$session", sessionId)
            // Which app the user typed in, and their sentence, are kept only for suggestions they took.
            .Bind("$name", taken ? suggestion.AppName : "").Bind("$app", taken ? suggestion.AppId : "")
            .Bind("$trigger", suggestion.Trigger).Bind("$text", suggestion.SuggestionText).Bind("$accepted", suggestion.AcceptedText)
            .Bind("$outcome", suggestion.Outcome.ToString().ToLowerInvariant())
            .Bind("$suggested", CountWords(suggestion.SuggestionText)).Bind("$acceptedWords", CountWords(suggestion.AcceptedText))
            .Bind("$firstMs", (long?)suggestion.FirstMs).Bind("$fullMs", (long?)suggestion.FullMs)
            .Bind("$source", suggestion.ContextSource).Bind("$model", suggestion.Model).Bind("$before", taken ? suggestion.ContextBefore : "");
        insert.Parameters.AddWithValue("$logprob", (object?)suggestion.FirstWordLogProb ?? DBNull.Value);
        insert.Execute();
        Prune(connection, suggestion.Date);
        return sessionId;
    });

    /// <summary>Retention for suggestions that weren't taken; run at startup and daily.</summary>
    public void Prune(DateTimeOffset now) => database.Write(connection => Prune(connection, now));

    private static void Prune(Microsoft.Data.Sqlite.SqliteConnection connection, DateTimeOffset now)
    {
        Db.Command(connection, "DELETE FROM cotypist_suggestions WHERE accepted_text = '' AND created_at_utc < $cutoff;")
            .Bind("$cutoff", now - UnacceptedRetention).Execute();
        Db.Command(connection, """
                DELETE FROM cotypist_suggestions WHERE accepted_text = '' AND id NOT IN
                    (SELECT id FROM cotypist_suggestions WHERE accepted_text = '' ORDER BY id DESC LIMIT $limit);
                """).Bind("$limit", UnacceptedLimit).Execute();
    }

    public IReadOnlyList<CotypistSession> Sessions() => database.Read(connection =>
    {
        var ids = new List<long>();
        using (var command = Db.Command(connection, "SELECT id FROM cotypist_sessions ORDER BY started_at_utc DESC;"))
        using (var reader = command.ExecuteReader())
            while (reader.Read()) ids.Add(reader.GetInt64(0));
        return (IReadOnlyList<CotypistSession>)ids.Select(id => Session(connection, id)!).ToList();
    });

    public CotypistSession? Session(long id) => database.Read(connection => Session(connection, id));

    private static CotypistSession? Session(Microsoft.Data.Sqlite.SqliteConnection connection, long id)
    {
        CotypistSession? session = null;
        using (var command = Db.Command(connection, """
                   SELECT s.id, s.started_at_utc, s.last_activity_at_utc, s.target_app_name, s.target_app_id,
                          COUNT(e.id), COALESCE(SUM(CASE WHEN e.accepted_text <> '' THEN 1 ELSE 0 END), 0),
                          COALESCE(SUM(e.words_accepted), 0)
                   FROM cotypist_sessions s LEFT JOIN cotypist_suggestions e ON e.session_id = s.id
                   WHERE s.id = $id GROUP BY s.id;
                   """).Bind("$id", id))
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read()) return null;
            session = new CotypistSession(reader.GetInt64(0), PersistenceTime.FromStorage(reader.GetInt64(1)), PersistenceTime.FromStorage(reader.GetInt64(2)),
                reader.GetString(3), reader.GetString(4), [], (int)reader.GetInt64(7), (int)reader.GetInt64(5), (int)reader.GetInt64(6));
        }
        var acceptances = new List<CotypistAcceptance>();
        using (var command = Db.Command(connection, "SELECT context_before, accepted_text FROM cotypist_suggestions WHERE session_id = $id AND accepted_text <> '' ORDER BY id;").Bind("$id", id))
        using (var reader = command.ExecuteReader())
            while (reader.Read()) acceptances.Add(new CotypistAcceptance(reader.GetString(0), reader.GetString(1)));
        return session with { Acceptances = acceptances };
    }

    /// <summary>Removes a session and every suggestion recorded in it, text included.</summary>
    public void DeleteSession(long id) => database.Write(connection =>
    {
        Db.Command(connection, "DELETE FROM cotypist_suggestions WHERE session_id = $id;").Bind("$id", id).Execute();
        Db.Command(connection, "DELETE FROM cotypist_sessions WHERE id = $id;").Bind("$id", id).Execute();
    });

    /// <summary>Words written and suggestions taken since <paramref name="since"/>. Untaken suggestions
    /// are pruned by age and count, so the rate counts only the span whose untaken history is complete.</summary>
    public CotypistInsightsTotals? InsightsTotals(DateTimeOffset? since, DateTimeOffset now) => database.Read(connection =>
    {
        var rateStart = now - UnacceptedRetention;
        if (since is { } start && start > rateStart) rateStart = start;
        using (var oldest = Db.Command(connection, """
                   SELECT MIN(created_at_utc) FROM cotypist_suggestions WHERE accepted_text = '' HAVING COUNT(*) >= $limit;
                   """).Bind("$limit", UnacceptedLimit))
        {
            if (oldest.ExecuteScalar() is long ticks && PersistenceTime.FromStorage(ticks) > rateStart) rateStart = PersistenceTime.FromStorage(ticks);
        }
        using var command = Db.Command(connection, """
                SELECT COALESCE(SUM(CASE WHEN accepted_text <> '' AND created_at_utc >= $since THEN words_accepted END), 0),
                       COUNT(CASE WHEN accepted_text <> '' AND created_at_utc >= $since THEN 1 END),
                       COUNT(CASE WHEN accepted_text <> '' AND created_at_utc >= $rate THEN 1 END),
                       COUNT(CASE WHEN created_at_utc >= $rate THEN 1 END)
                FROM cotypist_suggestions;
                """).Bind("$since", since ?? DateTimeOffset.MinValue).Bind("$rate", rateStart);
        using var reader = command.ExecuteReader();
        reader.Read();
        var accepted = (int)reader.GetInt64(1);
        if (accepted == 0) return null;
        var shown = reader.GetInt64(3);
        return new CotypistInsightsTotals((int)reader.GetInt64(0), accepted, shown > 0 ? reader.GetInt64(2) / (double)shown : null,
            (int)Math.Round((now - rateStart).TotalDays));
    });

    private static int CountWords(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
}
