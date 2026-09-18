using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Muesli.Windows.Services;

public static class MeetingSummaryService
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(90)
    };

    private static readonly string[] BuiltIns =
    [
        "Auto",
        "Standard Meeting Notes",
        "1:1",
        "Customer Discovery",
        "Stand-up",
        "Weekly Team Meeting",
        "Hiring Interview",
        "Sales Call",
        "Product Review",
        "Incident Review"
    ];

    private static readonly Dictionary<string, string> TemplateAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["auto"] = "Auto",
        ["standard"] = "Standard Meeting Notes",
        ["standard meeting notes"] = "Standard Meeting Notes",
        ["1 to 1"] = "1:1",
        ["1:1"] = "1:1",
        ["one on one"] = "1:1",
        ["customer"] = "Customer Discovery",
        ["customer discovery"] = "Customer Discovery",
        ["standup"] = "Stand-up",
        ["stand-up"] = "Stand-up",
        ["weekly team meeting"] = "Weekly Team Meeting",
        ["hiring"] = "Hiring Interview",
        ["hiring interview"] = "Hiring Interview",
        ["sales"] = "Sales Call",
        ["sales call"] = "Sales Call",
        ["product review"] = "Product Review",
        ["incident"] = "Incident Review",
        ["incident review"] = "Incident Review"
    };

    private static readonly Regex TimestampPattern = new(@"^\[(?:\d{2}:)?\d{2}:\d{2}\]\s+(?<speaker>[^:]+):\s*(?<text>.+)$", RegexOptions.Compiled);
    private static readonly Regex LegacyPattern = new(@"^\[(?<speaker>[^\]]+)\]\s*(?<text>.+)$", RegexOptions.Compiled);
    private static readonly Regex WhitespacePattern = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex ActionPattern = new(@"\b(action item|todo|to do|follow up|follow-up|next step|can you|please|i will|i'll|we need to|need to|send|schedule|review|prepare|investigate|owner)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DecisionPattern = new(@"\b(decided|agreed|approved|resolved|move forward|ship|selected|chose|choose|plan is|going with|we'll use|we will use)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex QuestionPattern = new(@"\?|(\bquestion\b|\bunclear\b|\bnot sure\b|\bneed to know\b|\bfigure out\b)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex RiskPattern = new(@"\b(blocker|blocked|risk|issue|concern|dependency|delay|problem|broken|escalate)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PainPattern = new(@"\b(problem|pain|manual|slow|friction|difficult|hard|broken|can't|cannot|missing|annoying)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex WorkflowPattern = new(@"\b(currently|today|right now|workflow|process|using|use today|manually|today they|current setup)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex BuyingSignalPattern = new(@"\b(budget|timeline|quarter|urgent|priority|decision|buy|purchase|pilot|contract|renewal|security review|procurement)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PositiveSignalPattern = new(@"\b(strong|good|great|clear|confident|thoughtful|impressive|well|solid)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex NegativeSignalPattern = new(@"\b(concern|gap|weak|missing|lacking|unclear|unsure|risk)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex YesterdayPattern = new(@"\b(yesterday|since last|completed|finished|shipped|wrapped up)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TodayPattern = new(@"\b(today|next|planning|focus|working on|going to)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ImpactPattern = new(@"\b(impact|affected|severity|outage|customer impact|downtime)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex FillerOnlyPattern = new(@"^(uh|um|hmm|mm|yeah|yep|nope|okay|ok|right|got it|sounds good|sure|thanks|thank you)[.!]*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex HeadingPattern = new(@"^##+\s+(?<heading>.+)$", RegexOptions.Multiline | RegexOptions.Compiled);

    public static IReadOnlyList<string> BuiltInTemplateNames => BuiltIns;

    public static string NormalizeTemplateName(string? template)
    {
        if (string.IsNullOrWhiteSpace(template))
        {
            return "Standard Meeting Notes";
        }

        var trimmed = template.Trim();
        if (TemplateAliases.TryGetValue(trimmed, out var normalized))
        {
            return normalized;
        }

        return trimmed;
    }

    public static bool IsBuiltInTemplate(string? template)
    {
        var normalized = NormalizeTemplateName(template);
        return BuiltIns.Contains(normalized, StringComparer.OrdinalIgnoreCase);
    }

    public static string CreateSummary(string transcript, string template = "Standard Meeting Notes")
    {
        return CreateSummary(transcript, "", template);
    }

    public static string CreateSummary(string transcript, string meetingTitle, string template)
    {
        if (string.IsNullOrWhiteSpace(transcript))
        {
            return "";
        }

        var resolvedTemplate = ResolveTemplate(meetingTitle, transcript, template);
        var turns = ParseTurns(transcript);
        var model = BuildSummaryModel(turns);
        var usedContent = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var builder = new StringBuilder();
        AppendParagraphSection(builder, "Summary", BuildSummaryParagraph(model));
        RememberContent(usedContent, BuildSummaryParagraph(model));

        switch (resolvedTemplate)
        {
            case "1:1":
                AppendBulletSection(builder, "Wins / Progress", TakeUnique(model.Updates, usedContent, 4));
                AppendBulletSection(builder, "Concerns / Support Needed", TakeUnique(Merge(model.Risks, model.OpenQuestions), usedContent, 4));
                AppendBulletSection(builder, "Decisions / Commitments", TakeUnique(Merge(model.Decisions, model.ActionItems), usedContent, 5), checklist: true);
                AppendBulletSection(builder, "Action Items", TakeUnique(model.ActionItems, usedContent, 5), checklist: true);
                AppendBulletSection(builder, "Follow-ups", TakeUnique(model.Risks, usedContent, 4));
                break;

            case "Customer Discovery":
                AppendBulletSection(builder, "Customer Context", TakeUnique(model.CustomerContext, usedContent, 4));
                AppendBulletSection(builder, "Pain Points", TakeUnique(model.PainPoints, usedContent, 5));
                AppendBulletSection(builder, "Current Workflow", TakeUnique(model.CurrentWorkflow, usedContent, 4));
                AppendBulletSection(builder, "Buying Signals", TakeUnique(model.BuyingSignals, usedContent, 4));
                AppendBulletSection(builder, "Action Items", TakeUnique(model.ActionItems, usedContent, 5), checklist: true);
                break;

            case "Stand-up":
                AppendBulletSection(builder, "Yesterday", TakeUnique(model.Yesterday, usedContent, 4));
                AppendBulletSection(builder, "Today", TakeUnique(model.Today, usedContent, 4));
                AppendBulletSection(builder, "Blockers", TakeUnique(model.Risks, usedContent, 4));
                AppendBulletSection(builder, "Coordination Notes", TakeUnique(model.Decisions, usedContent, 4));
                AppendBulletSection(builder, "Action Items", TakeUnique(model.ActionItems, usedContent, 5), checklist: true);
                break;

            case "Weekly Team Meeting":
                AppendBulletSection(builder, "Key Points", TakeUnique(model.KeyPoints, usedContent, 5));
                AppendBulletSection(builder, "Decisions", TakeUnique(model.Decisions, usedContent, 4));
                AppendBulletSection(builder, "Risks / Follow-ups", TakeUnique(Merge(model.Risks, model.OpenQuestions), usedContent, 5));
                AppendBulletSection(builder, "Action Items", TakeUnique(model.ActionItems, usedContent, 5), checklist: true);
                break;

            case "Hiring Interview":
                AppendBulletSection(builder, "Candidate Signals", TakeUnique(model.KeyPoints, usedContent, 5));
                AppendBulletSection(builder, "Strengths", TakeUnique(model.Strengths, usedContent, 4));
                AppendBulletSection(builder, "Concerns", TakeUnique(Merge(model.Concerns, model.OpenQuestions), usedContent, 4));
                AppendBulletSection(builder, "Decision / Recommendation", TakeUnique(model.Decisions, usedContent, 3));
                AppendBulletSection(builder, "Follow-ups", TakeUnique(model.ActionItems, usedContent, 4), checklist: true);
                break;

            case "Sales Call":
                AppendBulletSection(builder, "Customer Needs", TakeUnique(Merge(model.CustomerContext, model.PainPoints), usedContent, 5));
                AppendBulletSection(builder, "Objections / Risks", TakeUnique(Merge(model.Concerns, model.Risks), usedContent, 4));
                AppendBulletSection(builder, "Commitments", TakeUnique(model.Decisions, usedContent, 5), checklist: true);
                AppendBulletSection(builder, "Next Steps", TakeUnique(model.ActionItems, usedContent, 5), checklist: true);
                break;

            case "Product Review":
                AppendBulletSection(builder, "Feedback", TakeUnique(Merge(model.KeyPoints, model.PainPoints), usedContent, 5));
                AppendBulletSection(builder, "Decisions", TakeUnique(model.Decisions, usedContent, 4));
                AppendBulletSection(builder, "Risks / Open Questions", TakeUnique(Merge(model.Risks, model.OpenQuestions), usedContent, 5));
                AppendBulletSection(builder, "Action Items", TakeUnique(model.ActionItems, usedContent, 5), checklist: true);
                break;

            case "Incident Review":
                AppendBulletSection(builder, "Timeline / Signals", TakeUnique(Merge(model.Updates, model.KeyPoints), usedContent, 5));
                AppendBulletSection(builder, "Impact", TakeUnique(Merge(model.Impact, model.Risks), usedContent, 4));
                AppendBulletSection(builder, "Decisions", TakeUnique(model.Decisions, usedContent, 4));
                AppendBulletSection(builder, "Follow-ups", TakeUnique(Merge(model.ActionItems, model.OpenQuestions), usedContent, 5), checklist: true);
                break;

            default:
                AppendBulletSection(builder, "Key Points", TakeUnique(model.KeyPoints, usedContent, 5));
                AppendBulletSection(builder, "Decisions", TakeUnique(model.Decisions, usedContent, 4));
                AppendBulletSection(builder, "Action Items", TakeUnique(model.ActionItems, usedContent, 5), checklist: true);
                AppendBulletSection(builder, "Open Questions", TakeUnique(model.OpenQuestions, usedContent, 4));
                AppendBulletSection(builder, "Risks / Follow-ups", TakeUnique(model.Risks, usedContent, 4));
                break;
        }

        return builder.ToString().Trim();
    }

    public static async Task<string> CreateSummaryAsync(
        string transcript,
        string meetingTitle,
        MuesliSettings settings,
        CancellationToken cancellationToken = default)
    {
        return (await CreateSummaryResultAsync(transcript, meetingTitle, settings, cancellationToken)).Summary;
    }

    public static async Task<SummaryGenerationResult> CreateSummaryResultAsync(
        string transcript,
        string meetingTitle,
        MuesliSettings settings,
        CancellationToken cancellationToken = default,
        HttpClient? httpClient = null)
    {
        if (string.IsNullOrWhiteSpace(transcript))
        {
            return new SummaryGenerationResult("", "local", false, null);
        }

        var provider = settings.MeetingSummaryProvider.Trim().ToLowerInvariant();
        try
        {
            var summary = provider switch
            {
                "openai" => await SummarizeWithOpenAIAsync(transcript, meetingTitle, settings, httpClient ?? Http, cancellationToken),
                "openrouter" => await SummarizeWithOpenRouterAsync(transcript, meetingTitle, settings, httpClient ?? Http, cancellationToken),
                "ollama" => await SummarizeWithOllamaAsync(transcript, meetingTitle, settings, httpClient ?? Http, cancellationToken),
                _ => CreateLocalSummary(transcript, meetingTitle, settings)
            };
            return new SummaryGenerationResult(
                summary,
                provider is "openai" or "openrouter" or "ollama" ? provider : "local",
                false,
                null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var fallback = CreateLocalSummary(transcript, meetingTitle, settings);
            var reason = exception is SummaryProviderException providerException
                ? providerException.SafeReason
                : exception is TaskCanceledException ? "timeout"
                : exception is JsonException ? "malformed-response" : "provider-error";
            return new SummaryGenerationResult(fallback, provider, true, reason);
        }
    }

    private static string CreateLocalSummary(string transcript, string meetingTitle, MuesliSettings settings)
    {
        var promptOverride = settings.MeetingSummaryPromptOverride?.Trim() ?? "";
        if (promptOverride.Length == 0)
        {
            return CreateSummary(transcript, meetingTitle, EffectiveLocalTemplate(settings, meetingTitle, transcript));
        }

        return TryCreateCustomPromptSummary(transcript, meetingTitle, promptOverride, out var customSummary)
            ? customSummary
            : CreateSummary(transcript, meetingTitle, "Standard Meeting Notes");
    }

    private static string EffectiveLocalTemplate(MuesliSettings settings, string meetingTitle, string transcript)
    {
        var selected = NormalizeTemplateName(settings.MeetingSummaryTemplate);
        return ResolveTemplate(meetingTitle, transcript, selected);
    }

    private static string EffectiveSystemPrompt(MuesliSettings settings)
    {
        var promptOverride = settings.MeetingSummaryPromptOverride?.Trim() ?? "";
        return promptOverride.Length > 0
            ? promptOverride
            : SummaryInstructions(NormalizeTemplateName(settings.MeetingSummaryTemplate));
    }

    private static async Task<string> SummarizeWithOpenAIAsync(
        string transcript,
        string meetingTitle,
        MuesliSettings settings,
        HttpClient httpClient,
        CancellationToken cancellationToken)
    {
        var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            apiKey = settings.ResolvedOpenAIApiKey;
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new SummaryProviderException("missing-key");
        }

        var model = string.IsNullOrWhiteSpace(settings.OpenAIModel) ? "gpt-5.4-mini" : settings.OpenAIModel;
        var body = new
        {
            model,
            input = new object[]
            {
                new { role = "system", content = EffectiveSystemPrompt(settings) },
                new { role = "user", content = SummaryUserPrompt(transcript, meetingTitle) }
            },
            reasoning = new { effort = "low" },
            text = new { verbosity = "low" },
            max_output_tokens = 2500
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/responses");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = JsonContent(body);

        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new SummaryProviderException($"http-{(int)response.StatusCode}");
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        using var document = JsonDocument.Parse(json);
        var text = ExtractOpenAIText(document.RootElement);
        return string.IsNullOrWhiteSpace(text)
            ? throw new SummaryProviderException("empty-response")
            : text.Trim();
    }

    /// <summary>
    /// Ollama's local chat contract: POST {endpoint}/api/chat with stream disabled, and read
    /// message.content. No credential is involved, so nothing here may be stored or logged as one.
    /// </summary>
    private static async Task<string> SummarizeWithOllamaAsync(
        string transcript,
        string meetingTitle,
        MuesliSettings settings,
        HttpClient httpClient,
        CancellationToken cancellationToken)
    {
        var endpoint = string.IsNullOrWhiteSpace(settings.OllamaEndpoint)
            ? "http://localhost:11434"
            : settings.OllamaEndpoint.Trim();
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var baseUri) ||
            (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new SummaryProviderException("invalid-endpoint");
        }

        var model = string.IsNullOrWhiteSpace(settings.OllamaModel) ? "llama3.1:8b" : settings.OllamaModel.Trim();
        var body = new
        {
            model,
            stream = false,
            messages = new object[]
            {
                new { role = "system", content = EffectiveSystemPrompt(settings) },
                new { role = "user", content = SummaryUserPrompt(transcript, meetingTitle) }
            }
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, "/api/chat"));
        request.Content = JsonContent(body);

        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            // A stopped daemon and a missing model are the two failures users actually hit.
            throw new SummaryProviderException(
                response.StatusCode == System.Net.HttpStatusCode.NotFound
                    ? "model-not-installed"
                    : $"http-{(int)response.StatusCode}");
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        using var document = JsonDocument.Parse(json);
        var text = document.RootElement.TryGetProperty("message", out var message) &&
                   message.TryGetProperty("content", out var content)
            ? content.GetString()
            : null;
        return string.IsNullOrWhiteSpace(text)
            ? throw new SummaryProviderException("empty-response")
            : text.Trim();
    }

    private static async Task<string> SummarizeWithOpenRouterAsync(
        string transcript,
        string meetingTitle,
        MuesliSettings settings,
        HttpClient httpClient,
        CancellationToken cancellationToken)
    {
        var apiKey = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            apiKey = settings.ResolvedOpenRouterApiKey;
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new SummaryProviderException("missing-key");
        }

        var model = string.IsNullOrWhiteSpace(settings.OpenRouterModel)
            ? "stepfun/step-3.5-flash:free"
            : settings.OpenRouterModel;
        var body = new
        {
            model,
            messages = new object[]
            {
                new { role = "system", content = EffectiveSystemPrompt(settings) },
                new { role = "user", content = SummaryUserPrompt(transcript, meetingTitle) }
            },
            max_tokens = 2500
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://openrouter.ai/api/v1/chat/completions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Headers.TryAddWithoutValidation("X-OpenRouter-Title", "Muesli Windows");
        request.Content = JsonContent(body);

        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new SummaryProviderException($"http-{(int)response.StatusCode}");
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        using var document = JsonDocument.Parse(json);
        var text = ExtractOpenRouterText(document.RootElement);
        return string.IsNullOrWhiteSpace(text)
            ? throw new SummaryProviderException("empty-response")
            : text.Trim();
    }

    private static StringContent JsonContent<T>(T value)
    {
        return new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");
    }

    private static string SummaryInstructions(string template)
    {
        if (!string.IsNullOrWhiteSpace(template) && template.Contains('\n'))
        {
            return template;
        }

        var resolved = NormalizeTemplateName(template);
        if (resolved.Equals("Auto", StringComparison.OrdinalIgnoreCase))
        {
            return """
            You are a meeting notes assistant. Infer the best note structure from the transcript and produce polished markdown notes.
            Do not invent facts. Keep the notes structured, concise, and useful after the meeting.

            Always include:
            - ## Summary
            - one or more meeting-specific sections
            - ## Action Items

            Prefer short bullet points. Include owners only when they are clearly stated.
            """;
        }

        return resolved switch
        {
            "Standard Meeting Notes" => """
                You are a meeting notes assistant. Produce concise, polished markdown notes.
                Do not invent facts. Prefer clean bullets over transcript excerpts.

                Use this structure exactly:

                ## Summary
                One short paragraph covering the purpose and outcome.

                ## Key Points
                - Most important discussion points

                ## Decisions
                - Confirmed choices or conclusions

                ## Action Items
                - [ ] Follow-ups, owners, and timing only if stated

                ## Open Questions
                - Open issues still unresolved

                ## Risks / Follow-ups
                - Risks, blockers, or coordination items
                """,
            "1:1" => """
                Produce polished markdown notes for a 1:1.
                Do not invent facts.

                Use this structure exactly:

                ## Summary
                Briefly summarize the tone, focus, and outcome.

                ## Wins / Progress
                - Positive updates or momentum

                ## Concerns / Support Needed
                - Challenges, blockers, coaching needs, or asks

                ## Decisions / Commitments
                - Agreements or commitments made

                ## Action Items
                - [ ] Follow-ups with owners only if stated

                ## Follow-ups
                - Anything that should be revisited later
                """,
            "Customer Discovery" => """
                Produce polished markdown notes for a customer discovery meeting.
                Do not invent facts.

                Use this structure exactly:

                ## Summary
                One short paragraph about the customer context and main outcome.

                ## Customer Context
                - Relevant company, role, or situation details

                ## Pain Points
                - Explicit pains, frustrations, or unmet needs

                ## Current Workflow
                - How the customer handles this today

                ## Buying Signals
                - Timing, urgency, budget, decision process, or next-step signals

                ## Action Items
                - [ ] Follow-ups with owners only if stated
                """,
            "Stand-up" => """
                Produce polished markdown notes for a stand-up.
                Do not invent facts.

                Use this structure exactly:

                ## Summary
                A short paragraph about the overall progress and risks.

                ## Yesterday
                - Completed work or recent progress

                ## Today
                - Planned work or priorities

                ## Blockers
                - Risks, blockers, or dependencies

                ## Coordination Notes
                - Cross-team asks or important alignment points

                ## Action Items
                - [ ] Follow-ups with owners only if stated
                """,
            "Weekly Team Meeting" => """
                Produce polished markdown notes for a weekly team meeting.
                Do not invent facts.

                Use this structure exactly:

                ## Summary
                A concise paragraph on the most important weekly outcomes.

                ## Key Points
                - Important updates across workstreams

                ## Decisions
                - Decisions made or confirmed

                ## Risks / Follow-ups
                - Issues, blockers, or unresolved items

                ## Action Items
                - [ ] Tasks with owners only if stated
                """,
            "Hiring Interview" => """
                Produce polished markdown notes for a hiring interview.
                Do not invent facts.

                Use this structure exactly:

                ## Summary
                Briefly summarize the candidate and how the conversation went.

                ## Candidate Signals
                - Notable examples, experience, or evidence

                ## Strengths
                - Strong positive signals

                ## Concerns
                - Gaps, risks, or unclear areas

                ## Decision / Recommendation
                - Hiring recommendation or next interview decision

                ## Follow-ups
                - [ ] Remaining interview steps or checks
                """,
            "Sales Call" => """
                Produce polished markdown notes for a sales call.
                Do not invent facts.

                Use this structure exactly:

                ## Summary
                A concise paragraph on customer need, fit, and momentum.

                ## Customer Needs
                - What the buyer needs or wants to solve

                ## Objections / Risks
                - Concerns, objections, or blockers

                ## Commitments
                - Promises or agreed follow-ups

                ## Next Steps
                - [ ] Specific next steps with owners only if stated
                """,
            "Product Review" => """
                Produce polished markdown notes for a product review.
                Do not invent facts.

                Use this structure exactly:

                ## Summary
                A short paragraph covering the product area reviewed and the outcome.

                ## Feedback
                - Main feedback themes

                ## Decisions
                - Decisions, prioritization, or scope calls

                ## Risks / Open Questions
                - What still needs attention

                ## Action Items
                - [ ] Follow-ups with owners only if stated
                """,
            "Incident Review" => """
                Produce polished markdown notes for an incident review.
                Do not invent facts.

                Use this structure exactly:

                ## Summary
                A short paragraph covering what happened and the current state.

                ## Timeline / Signals
                - Key incident milestones or observed signals

                ## Impact
                - Customer, product, or operational impact

                ## Decisions
                - Confirmed remediation or policy decisions

                ## Follow-ups
                - [ ] Follow-up actions and owners only if stated
                """,
            _ => SummaryInstructions("Standard Meeting Notes")
        };
    }

    private static string ResolveTemplate(string meetingTitle, string transcript, string template)
    {
        var normalized = NormalizeTemplateName(template);
        if (!normalized.Equals("Auto", StringComparison.OrdinalIgnoreCase))
        {
            return normalized;
        }

        var combined = $"{meetingTitle} {transcript}";
        if (Regex.IsMatch(combined, @"\b(1:1|1 to 1|one on one)\b", RegexOptions.IgnoreCase))
            return "1:1";
        if (Regex.IsMatch(combined, @"\b(standup|stand-up|daily sync)\b", RegexOptions.IgnoreCase))
            return "Stand-up";
        if (Regex.IsMatch(combined, @"\b(customer discovery|discovery|prospect|customer call)\b", RegexOptions.IgnoreCase))
            return "Customer Discovery";
        if (Regex.IsMatch(combined, @"\b(hiring|candidate|interview loop)\b", RegexOptions.IgnoreCase))
            return "Hiring Interview";
        if (Regex.IsMatch(combined, @"\b(sales|deal|pipeline|close)\b", RegexOptions.IgnoreCase))
            return "Sales Call";
        if (Regex.IsMatch(combined, @"\b(product review|design review|roadmap review)\b", RegexOptions.IgnoreCase))
            return "Product Review";
        if (Regex.IsMatch(combined, @"\b(incident|postmortem|outage|sev)\b", RegexOptions.IgnoreCase))
            return "Incident Review";
        if (Regex.IsMatch(combined, @"\b(weekly|staff meeting|team meeting)\b", RegexOptions.IgnoreCase))
            return "Weekly Team Meeting";
        return "Standard Meeting Notes";
    }

    private static bool TryCreateCustomPromptSummary(string transcript, string meetingTitle, string customPrompt, out string summary)
    {
        var headings = HeadingPattern.Matches(customPrompt)
            .Select(match => match.Groups["heading"].Value.Trim())
            .Where(heading => heading.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (headings.Count == 0)
        {
            summary = "";
            return false;
        }

        var turns = ParseTurns(transcript);
        var model = BuildSummaryModel(turns);
        var usedContent = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var builder = new StringBuilder();

        foreach (var heading in headings)
        {
            AppendCustomSection(builder, heading, model, usedContent);
        }

        summary = builder.ToString().Trim();
        return summary.Length > 0;
    }

    private static SummaryModel BuildSummaryModel(List<MeetingTurn> turns)
    {
        if (turns.Count == 0)
        {
            return new SummaryModel([], [], [], [], [], [], [], [], [], [], [], [], [], [], []);
        }

        var keyPoints = CollectDistinct(turns,
            turn => !FillerOnlyPattern.IsMatch(turn.Text) &&
                    !ActionPattern.IsMatch(turn.Text) &&
                    !DecisionPattern.IsMatch(turn.Text) &&
                    !QuestionPattern.IsMatch(turn.Text),
            7,
            includeSpeaker: false);
        var decisions = CollectDistinct(turns, turn => DecisionPattern.IsMatch(turn.Text), 5, includeSpeaker: true);
        var actionItems = CollectDistinct(turns, turn => ActionPattern.IsMatch(turn.Text), 6, includeSpeaker: true, checklistTone: true);
        var openQuestions = CollectDistinct(turns, turn => QuestionPattern.IsMatch(turn.Text), 5, includeSpeaker: true);
        var risks = CollectDistinct(turns, turn => RiskPattern.IsMatch(turn.Text), 5, includeSpeaker: true);
        var customerContext = CollectDistinct(turns, turn => Regex.IsMatch(turn.Text, @"\b(company|team|role|customer|users?|market|segment)\b", RegexOptions.IgnoreCase), 4, includeSpeaker: true);
        var painPoints = CollectDistinct(turns, turn => PainPattern.IsMatch(turn.Text), 5, includeSpeaker: true);
        var currentWorkflow = CollectDistinct(turns, turn => WorkflowPattern.IsMatch(turn.Text), 4, includeSpeaker: true);
        var buyingSignals = CollectDistinct(turns, turn => BuyingSignalPattern.IsMatch(turn.Text), 4, includeSpeaker: true);
        var strengths = CollectDistinct(turns, turn => PositiveSignalPattern.IsMatch(turn.Text), 4, includeSpeaker: true);
        var concerns = CollectDistinct(turns, turn => NegativeSignalPattern.IsMatch(turn.Text), 4, includeSpeaker: true);
        var yesterday = CollectDistinct(turns, turn => YesterdayPattern.IsMatch(turn.Text), 4, includeSpeaker: true);
        var today = CollectDistinct(turns, turn => TodayPattern.IsMatch(turn.Text), 4, includeSpeaker: true);
        var updates = CollectDistinct(turns,
            turn => !FillerOnlyPattern.IsMatch(turn.Text) &&
                    !ActionPattern.IsMatch(turn.Text),
            5,
            includeSpeaker: true);
        var impact = CollectDistinct(turns, turn => ImpactPattern.IsMatch(turn.Text), 4, includeSpeaker: true);

        return new SummaryModel(
            keyPoints,
            decisions,
            actionItems,
            openQuestions,
            risks,
            customerContext,
            painPoints,
            currentWorkflow,
            buyingSignals,
            strengths,
            concerns,
            yesterday,
            today,
            updates,
            impact);
    }

    private static string BuildSummaryParagraph(SummaryModel model)
    {
        var parts = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in new[] { model.KeyPoints, model.Risks, model.Decisions, model.ActionItems })
        {
            foreach (var raw in source)
            {
                var part = EnsureSentence(RemoveMarkdownSpeakerPrefix(raw));
                var key = CanonicalizeText(part);
                if (key.Length == 0 || !seen.Add(key))
                {
                    continue;
                }

                parts.Add(part);
                if (parts.Count == 3)
                {
                    break;
                }
            }

            if (parts.Count == 3)
            {
                break;
            }
        }

        if (parts.Count == 0)
        {
            return "No concise summary could be generated from the transcript.";
        }

        return string.Join(" ", parts);
    }

    private static void AppendCustomSection(StringBuilder builder, string heading, SummaryModel model, HashSet<string> usedContent)
    {
        var normalized = heading.Trim().ToLowerInvariant();
        if (normalized.Contains("summary") || normalized.Contains("overview"))
        {
            var paragraph = BuildSummaryParagraph(model);
            AppendParagraphSection(builder, heading, paragraph);
            RememberContent(usedContent, paragraph);
            return;
        }

        var (items, checklist) = normalized switch
        {
            var value when value.Contains("customer context") => (TakeUnique(model.CustomerContext, usedContent, 4), false),
            var value when value.Contains("pain") || value.Contains("need") => (TakeUnique(Merge(model.PainPoints, model.KeyPoints), usedContent, 5), false),
            var value when value.Contains("workflow") || value.Contains("process") => (TakeUnique(model.CurrentWorkflow, usedContent, 4), false),
            var value when value.Contains("buy") || value.Contains("signal") => (TakeUnique(model.BuyingSignals, usedContent, 4), false),
            var value when value.Contains("strength") => (TakeUnique(model.Strengths, usedContent, 4), false),
            var value when value.Contains("concern") || value.Contains("objection") => (TakeUnique(Merge(model.Concerns, model.Risks), usedContent, 4), false),
            var value when value.Contains("risk") || value.Contains("follow-up") || value.Contains("follow up") => (TakeUnique(Merge(model.Risks, model.OpenQuestions), usedContent, 5), false),
            var value when value.Contains("decision") || value.Contains("commitment") || value.Contains("recommendation") => (TakeUnique(Merge(model.Decisions, model.ActionItems), usedContent, 5), normalized.Contains("commitment")),
            var value when value.Contains("action") || value.Contains("next step") || value.Contains("follow-up") => (TakeUnique(model.ActionItems, usedContent, 5), true),
            var value when value.Contains("question") => (TakeUnique(model.OpenQuestions, usedContent, 4), false),
            var value when value.Contains("yesterday") => (TakeUnique(model.Yesterday, usedContent, 4), false),
            var value when value.Contains("today") => (TakeUnique(model.Today, usedContent, 4), false),
            var value when value.Contains("blocker") => (TakeUnique(model.Risks, usedContent, 4), false),
            var value when value.Contains("impact") => (TakeUnique(Merge(model.Impact, model.Risks), usedContent, 4), false),
            var value when value.Contains("timeline") || value.Contains("signal") => (TakeUnique(Merge(model.Updates, model.KeyPoints), usedContent, 5), false),
            _ => (TakeUnique(model.KeyPoints, usedContent, 5), false)
        };

        AppendBulletSection(builder, heading, items, checklist);
    }

    private static List<MeetingTurn> ParseTurns(string transcript)
    {
        var turns = new List<MeetingTurn>();
        var dedupe = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rawLine in transcript.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                continue;
            }

            if (TryParseTurn(trimmed, out var parsed))
            {
                if (dedupe.Add($"{parsed.Speaker}|{parsed.CanonicalText}"))
                {
                    turns.Add(parsed);
                }

                continue;
            }

            foreach (var sentence in SplitIntoSentences(trimmed))
            {
                var canonical = CanonicalizeText(sentence);
                if (canonical.Length == 0 || FillerOnlyPattern.IsMatch(canonical))
                {
                    continue;
                }

                if (dedupe.Add($"|{canonical}"))
                {
                    turns.Add(new MeetingTurn("", sentence.Trim(), canonical));
                }
            }
        }

        return turns;
    }

    private static bool TryParseTurn(string line, out MeetingTurn turn)
    {
        var match = TimestampPattern.Match(line);
        if (!match.Success)
        {
            match = LegacyPattern.Match(line);
        }

        if (!match.Success)
        {
            turn = new MeetingTurn("", "", "");
            return false;
        }

        var speaker = match.Groups["speaker"].Value.Trim();
        var text = match.Groups["text"].Value.Trim();
        var canonical = CanonicalizeText(text);
        if (canonical.Length == 0 || FillerOnlyPattern.IsMatch(canonical))
        {
            turn = new MeetingTurn("", "", "");
            return false;
        }

        turn = new MeetingTurn(speaker, text, canonical);
        return true;
    }

    private static IEnumerable<string> SplitIntoSentences(string text)
    {
        return Regex.Split(WhitespacePattern.Replace(text, " "), @"(?<=[.!?])\s+")
            .Select(part => part.Trim())
            .Where(part => part.Length > 0);
    }

    private static List<string> CollectDistinct(
        IEnumerable<MeetingTurn> turns,
        Func<MeetingTurn, bool> predicate,
        int maxItems,
        bool includeSpeaker,
        bool checklistTone = false)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var items = new List<string>();

        foreach (var turn in turns)
        {
            if (!predicate(turn))
            {
                continue;
            }

            var rendered = FormatTurn(turn, includeSpeaker, checklistTone);
            var key = CanonicalizeText(RemoveMarkdownSpeakerPrefix(rendered));
            if (key.Length == 0 || !seen.Add(key))
            {
                continue;
            }

            items.Add(rendered);
            if (items.Count >= maxItems)
            {
                break;
            }
        }

        return items;
    }

    private static string FormatTurn(MeetingTurn turn, bool includeSpeaker, bool checklistTone)
    {
        var text = ShortenForNote(turn.Text, checklistTone ? 170 : 190);
        if (includeSpeaker && !string.IsNullOrWhiteSpace(turn.Speaker) && !turn.Speaker.Equals("System audio", StringComparison.OrdinalIgnoreCase))
        {
            return $"**{turn.Speaker}:** {text}";
        }

        return text;
    }

    private static string ShortenForNote(string text, int maxLength)
    {
        var cleaned = WhitespacePattern.Replace(text, " ").Trim().Trim('-', '•', ':', ';');
        cleaned = Regex.Replace(cleaned, @"^(we need to|need to|can you|please)\s+", "", RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned, @"^(i will|i'll)\s+", "", RegexOptions.IgnoreCase);
        cleaned = UppercaseFirstLetter(cleaned);
        if (cleaned.Length <= maxLength)
        {
            return EnsureSentence(cleaned);
        }

        var shortened = cleaned[..maxLength].TrimEnd();
        var lastSpace = shortened.LastIndexOf(' ');
        if (lastSpace > maxLength / 2)
        {
            shortened = shortened[..lastSpace];
        }

        return EnsureSentence(shortened + "...");
    }

    private static string UppercaseFirstLetter(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        return char.ToUpperInvariant(text[0]) + text[1..];
    }

    private static string CanonicalizeText(string text)
    {
        var cleaned = WhitespacePattern.Replace(text, " ").Trim().Trim('-', '•');
        cleaned = Regex.Replace(cleaned, @"[^\w\s]", "");
        return cleaned.Trim();
    }

    private static string RemoveMarkdownSpeakerPrefix(string text)
    {
        return Regex.Replace(text, @"^\*\*[^*]+:\*\*\s*", "").Trim();
    }

    private static string EnsureSentence(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "";
        }

        var trimmed = text.Trim();
        if (trimmed.EndsWith(".") || trimmed.EndsWith("!") || trimmed.EndsWith("?"))
        {
            return trimmed;
        }

        return trimmed + ".";
    }

    private static List<string> Merge(IEnumerable<string> primary, IEnumerable<string> secondary)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var merged = new List<string>();

        foreach (var item in primary.Concat(secondary))
        {
            var key = CanonicalizeText(RemoveMarkdownSpeakerPrefix(item));
            if (key.Length == 0 || !seen.Add(key))
            {
                continue;
            }

            merged.Add(item);
        }

        return merged;
    }

    private static List<string> TakeUnique(IEnumerable<string> items, HashSet<string> usedContent, int maxItems)
    {
        var selected = new List<string>();
        foreach (var item in items)
        {
            var key = CanonicalizeText(RemoveMarkdownSpeakerPrefix(item));
            if (key.Length == 0 || usedContent.Contains(key))
            {
                continue;
            }

            selected.Add(item);
            usedContent.Add(key);
            if (selected.Count >= maxItems)
            {
                break;
            }
        }

        return selected;
    }

    private static void RememberContent(HashSet<string> usedContent, string text)
    {
        var key = CanonicalizeText(RemoveMarkdownSpeakerPrefix(text));
        if (key.Length > 0)
        {
            usedContent.Add(key);
        }
    }

    private static void AppendParagraphSection(StringBuilder builder, string heading, string paragraph)
    {
        if (builder.Length > 0)
        {
            builder.AppendLine();
        }

        builder.AppendLine($"## {heading}");
        builder.AppendLine(string.IsNullOrWhiteSpace(paragraph) ? "None noted." : paragraph.Trim());
    }

    private static void AppendBulletSection(StringBuilder builder, string heading, List<string> items, bool checklist = false)
    {
        if (builder.Length > 0)
        {
            builder.AppendLine();
        }

        builder.AppendLine($"## {heading}");
        if (items.Count == 0)
        {
            builder.AppendLine("None noted.");
            return;
        }

        foreach (var item in items)
        {
            builder.AppendLine(checklist ? $"- [ ] {item}" : $"- {item}");
        }
    }

    private static string SummaryUserPrompt(string transcript, string meetingTitle)
    {
        return $"Meeting title: {meetingTitle}{Environment.NewLine}{Environment.NewLine}Raw transcript:{Environment.NewLine}{transcript}";
    }

    private static string ExtractOpenAIText(JsonElement root)
    {
        if (root.TryGetProperty("output_text", out var outputText) &&
            outputText.ValueKind == JsonValueKind.String)
        {
            return outputText.GetString() ?? "";
        }

        if (!root.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array)
        {
            return "";
        }

        foreach (var item in output.EnumerateArray())
        {
            if (!item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var entry in content.EnumerateArray())
            {
                if (entry.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                {
                    return text.GetString() ?? "";
                }
            }
        }

        return "";
    }

    private static string ExtractOpenRouterText(JsonElement root)
    {
        if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array)
        {
            return "";
        }

        var first = choices.EnumerateArray().FirstOrDefault();
        if (first.ValueKind == JsonValueKind.Undefined ||
            !first.TryGetProperty("message", out var message) ||
            !message.TryGetProperty("content", out var content))
        {
            return "";
        }

        if (content.ValueKind == JsonValueKind.String)
        {
            return content.GetString() ?? "";
        }

        if (content.ValueKind != JsonValueKind.Array)
        {
            return "";
        }

        var parts = new List<string>();
        foreach (var part in content.EnumerateArray())
        {
            if (part.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
            {
                parts.Add(text.GetString() ?? "");
            }
        }

        return string.Join(Environment.NewLine, parts.Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    private sealed record MeetingTurn(string Speaker, string Text, string CanonicalText);

    private sealed record SummaryModel(
        List<string> KeyPoints,
        List<string> Decisions,
        List<string> ActionItems,
        List<string> OpenQuestions,
        List<string> Risks,
        List<string> CustomerContext,
        List<string> PainPoints,
        List<string> CurrentWorkflow,
        List<string> BuyingSignals,
        List<string> Strengths,
        List<string> Concerns,
        List<string> Yesterday,
        List<string> Today,
        List<string> Updates,
        List<string> Impact);
}

public sealed record SummaryGenerationResult(
    string Summary,
    string Provider,
    bool UsedLocalFallback,
    string? SafeFailureReason);

public sealed class SummaryProviderException : Exception
{
    public SummaryProviderException(string safeReason)
        : base("The selected summary provider could not produce notes.")
    {
        SafeReason = safeReason;
    }

    public string SafeReason { get; }
}
