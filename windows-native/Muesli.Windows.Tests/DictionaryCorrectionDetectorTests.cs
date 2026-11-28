using Muesli.Windows.Services;

namespace Muesli.Windows.Tests;

/// <summary>
/// Ported from macOS <c>DictionaryCorrectionDetectorTests.swift</c> so both apps agree on which
/// edits become dictionary prompts.
/// </summary>
public sealed class DictionaryCorrectionDetectorTests
{
    private static DictionarySuggestion? One(string original, string edited, IReadOnlySet<string>? recognized = null) =>
        DictionaryCorrectionDetector.Suggestions(original, edited, 1, recognized).FirstOrDefault();

    private static DictionarySuggestion? One(string original, string baseline, string current, IReadOnlySet<string>? recognized = null) =>
        DictionaryCorrectionDetector.Suggestions(original, baseline, current, 1, recognized).FirstOrDefault();

    private static IReadOnlyList<DictionarySuggestion> Three(string original, string edited) =>
        DictionaryCorrectionDetector.Suggestions(original, edited, 3);

    private static void AssertSuggestion(DictionarySuggestion? suggestion, string observed, string replacement)
    {
        Assert.NotNull(suggestion);
        Assert.Equal(observed, suggestion!.Observed);
        Assert.Equal(replacement, suggestion.Replacement);
    }

    [Fact]
    public void Surfaces_one_word_and_two_token_to_one_word_corrections_only()
    {
        AssertSuggestion(One(
            "for the dictionary test please write musley in this sentence today",
            "for the dictionary test please write muesli in this sentence today"), "musley", "muesli");
        AssertSuggestion(One(
            "for the dictionary test please write Thoray Pakam in this sentence today",
            "for the dictionary test please write Thoraipakkam in this sentence today"), "Thoray Pakam", "Thoraipakkam");

        var adjacent = Three(
            "for the dictionary test please write Mandavali Dirvaniur in this sentence today",
            "for the dictionary test please write Mandaiveli Thiruvanmiyur in this sentence today");
        Assert.Equal(["Mandavali", "Dirvaniur"], adjacent.Select(item => item.Observed));
        Assert.Equal(["Mandaiveli", "Thiruvanmiyur"], adjacent.Select(item => item.Replacement));

        Assert.Null(One(
            "for the dictionary test please write Thoray I Pakam in this sentence today",
            "for the dictionary test please write Thoraipakkam in this sentence today"));
    }

    [Theory]
    [InlineData("I love museli", "I love muesli", "museli", "muesli")]
    [InlineData("the word newsly is hard to transcribe", "the word muesli is hard to transcribe", "newsly", "muesli")]
    [InlineData("often I try to say Alvar Pet in dictation", "often I try to say Alwarpet in dictation", "Alvar Pet", "Alwarpet")]
    [InlineData("we run kubernetes in production for backend services today", "we run k8s in production for backend services today", "kubernetes", "k8s")]
    [InlineData("Hey Musley, can you see if you're transcribing the word correctly?", "Hey Muesli, can you see if you're transcribing the word correctly?", "Musley", "Muesli")]
    [InlineData("talk to pranav today", "talk to Pranav today", "pranav", "Pranav")]
    [InlineData(
        "No, typically I think the word muzzle is the worst toughest one for it to transcribe because it usually transcribes to the one starting with the w letter N instead of just muzzli.",
        "No, typically I think the word muesli is the worst toughest one for it to transcribe because it usually transcribes to the one starting with the w letter N instead of just muesli.",
        "muzzle", "muesli")]
    [InlineData(
        "Okay, it is able to transcribe the word musley if I say it very explicitly and enunciate the words, but I think as such if I say musley fast it's not able to.",
        "Okay, it is able to transcribe the word muesli if I say it very explicitly and enunciate the words, but I think as such if I say muesli fast it's not able to.",
        "musley", "muesli")]
    [InlineData(
        "See, I typically find the word muesli to be wrongly transcribed almost every time, so I'm just repeating muwsly twice.",
        "See, I typically find the word muesli to be wrongly transcribed almost every time, so I'm just repeating muesli twice.",
        "muwsly", "muesli")]
    [InlineData(
        "So this time if I say Newsly has not transcribed Newsly properly, would you be able to add it to dictionary immediately?",
        "So this time if I say muesli has not transcribed muesli properly, would you be able to add it to dictionary immediately? (still did not trigger the prompt unfortunately)",
        "Newsly", "muesli")]
    public void Detects_corrections(string original, string edited, string observed, string replacement) =>
        AssertSuggestion(One(original, edited), observed, replacement);

    [Theory]
    [InlineData("please route this to sc domain", "please route this to sc-domain")]
    [InlineData("I moved to New York last year and I like it", "I moved to NYC last year and I like it")]
    [InlineData("we discussed north american expansion today", "we discussed NASA expansion today")]
    [InlineData("can it transcribe Thoray I Pakam properly in this sentence", "can it transcribe Thoraipakkam properly in this sentence")]
    [InlineData("ship teh update", "ship the update")]
    [InlineData("please look up spelling correction", "please look file_00000000f4ac61f4841155122554864c-sanitized spelling correction")]
    [InlineData("Please review the Vitruvian design note", "Please review the follow-up design note")]
    [InlineData("Please review the Vitruvian design note", "Please review the k8s design note")]
    [InlineData("Please review the validation design note today", "Please review the v2n design note today")]
    [InlineData("Please review the token design note today", "Please review the t3n design note today")]
    public void Skips_edits_that_are_not_dictionary_corrections(string original, string edited) =>
        Assert.Null(One(original, edited));

    [Fact]
    public void Detects_single_token_acronym_casing_corrections()
    {
        var suggestions = Three("we use api and hsr routing today", "we use API and HSR routing today");
        Assert.Equal(["api", "hsr"], suggestions.Select(item => item.Observed));
        Assert.Equal(["API", "HSR"], suggestions.Select(item => item.Replacement));
    }

    [Fact]
    public void Detects_multiple_proper_noun_corrections_from_one_snapshot()
    {
        var suggestions = Three(
            "Okay, testing the dictionary prompt once again. So let us see if it can transcribe Mailapur, Mandavali, Trivanmir, Teenagar, Alvarped.",
            "Okay, testing the dictionary prompt once again. So let us see if it can transcribe Mylapore, Mandavali, Thiruvanmiyur, TNagar, Alwarpet.");
        Assert.Equal(["Mailapur", "Trivanmir", "Teenagar"], suggestions.Select(item => item.Observed));
        Assert.Equal(["Mylapore", "Thiruvanmiyur", "TNagar"], suggestions.Select(item => item.Replacement));
    }

    [Fact]
    public void Adjacent_word_edits_stay_single_word_suggestions()
    {
        var suggestions = Three(
            "Chennai area names called Mylapore, Mandavali, Dirvaniur.",
            "Chennai area names called Mylapore, Mandaiveli, Thiruvanmiyur.");
        Assert.Equal(["Mandavali", "Dirvaniur"], suggestions.Select(item => item.Observed));
        Assert.All(suggestions, item => Assert.DoesNotContain(" ", item.Replacement));
    }

    [Fact]
    public void Detects_multiple_split_proper_nouns()
    {
        var suggestions = Three(
            "Okay, I am only interested in just like proper noun related corrections, so I don't want any phrasal related changes that is not part of my concern for the dictionary prompts. So let us check. can it transcribe Thoray Pakam, Kara Pakam, Nongam Bakam properly?",
            "Okay, I am only interested in just like proper noun related corrections, so I don't want any phrasal related changes that is not part of my concern for the dictionary prompts. So let us check. can it transcribe Thoraipakkam, Karapakkam, Nungambakkam properly?");
        Assert.Equal(["Thoray Pakam", "Kara Pakam", "Nongam Bakam"], suggestions.Select(item => item.Observed));
        Assert.Equal(["Thoraipakkam", "Karapakkam", "Nungambakkam"], suggestions.Select(item => item.Replacement));
    }

    [Fact]
    public void Detects_corrections_inside_large_snapshots()
    {
        var prefix = string.Join(' ', Enumerable.Range(0, 140).Select(index => $"prefix{index}"));
        var suffix = string.Join(' ', Enumerable.Range(0, 140).Select(index => $"suffix{index}"));
        AssertSuggestion(One($"{prefix} I like museli today {suffix}", $"{prefix} I like muesli today {suffix}"), "museli", "muesli");

        var head = string.Join(' ', Enumerable.Range(0, 40).Select(index => $"prefix{index}"));
        var middle = string.Join(' ', Enumerable.Range(0, 140).Select(index => $"middle{index}"));
        var tail = string.Join(' ', Enumerable.Range(0, 40).Select(index => $"suffix{index}"));
        var far = Three(
            $"{head} I like museli today {middle} I mentioned newsly again {tail}",
            $"{head} I like muesli today {middle} I mentioned muesli again {tail}");
        Assert.Equal(["museli", "newsly"], far.Select(item => item.Observed));
    }

    [Theory]
    [InlineData(100)]
    [InlineData(160)]
    public void Detects_far_apart_corrections_across_repeated_separator_text(int repeats)
    {
        var middle = string.Join(' ', Enumerable.Repeat("and then", repeats));
        var suggestions = Three($"I like museli today {middle} I mentioned newsly again", $"I like muesli today {middle} I mentioned muesli again");
        Assert.Equal(["museli", "newsly"], suggestions.Select(item => item.Observed));
        Assert.Equal(["muesli", "muesli"], suggestions.Select(item => item.Replacement));
    }

    [Fact]
    public void Detects_a_correction_next_to_an_inserted_word()
    {
        var suggestions = Three("I like museli today", "I like fresh muesli today");
        Assert.Equal(["museli"], suggestions.Select(item => item.Observed));
        Assert.Equal(["muesli"], suggestions.Select(item => item.Replacement));
    }

    [Fact]
    public void Requires_shared_dictation_context()
    {
        Assert.True(DictionaryCorrectionDetector.HasSufficientSharedContext(
            "So this time if I say Newsly has not transcribed Newsly properly",
            "So this time if I say muesli has not transcribed muesli properly and then I kept typing"));
        Assert.False(DictionaryCorrectionDetector.HasSufficientSharedContext(
            "please look up spelling correction",
            "please look file_00000000f4ac61f4841155122554864c-sanitized spelling correction"));
        Assert.False(DictionaryCorrectionDetector.HasSufficientSharedContext("hello muesli today", "unrelated editor content"));
        Assert.True(DictionaryCorrectionDetector.HasSufficientSharedContext("Alvar Pet", "Alwarpet"));
    }

    [Fact]
    public void Keeps_changed_english_words_in_recognition_candidates()
    {
        var filler = string.Join(' ', Enumerable.Range(0, 180).Select(index => $"a{index}"));
        var candidates = DictionaryCorrectionDetector.RequiredEnglishWordCandidates($"{filler} internationalization", $"{filler} international");
        Assert.Contains("internationalization", candidates);
        Assert.Contains("international", candidates);
    }

    [Fact]
    public void Skips_common_word_truncations_the_spell_checker_knows()
    {
        Assert.Null(One(
            "I usually use this prompt",
            "I usually use this prompt",
            "I usual use this prompt",
            new HashSet<string> { "usually", "usual" }));
    }

    [Fact]
    public void Ignores_edits_outside_the_pasted_dictation() =>
        Assert.Null(One("hello muesli", "before hello muesli", "before hello muesli!"));

    [Fact]
    public void Re_rendered_or_reordered_text_is_not_a_correction()
    {
        var pasted = "For the trip I need three things:\n1. The passport\n2. The charger\n3. The tickets";
        Assert.False(DictionaryCorrectionDetector.IsSubstitution("things", "three", pasted, pasted.Replace("\n", " ")));
        Assert.False(DictionaryCorrectionDetector.IsSubstitution("three", "trip", pasted, "for the trip I need three things first the passport"));
        Assert.Empty(DictionaryCorrectionDetector.Suggestions(pasted, "For the trip I need things three: 1. The passport 2. The charger 3. The tickets"));
    }

    [Fact]
    public void Real_corrections_are_substitutions()
    {
        Assert.True(DictionaryCorrectionDetector.IsSubstitution("musley", "muesli", "please write musley today", "please write muesli today"));
        Assert.True(DictionaryCorrectionDetector.IsSubstitution("api", "API", "call the api now", "call the API now"));
        Assert.True(DictionaryCorrectionDetector.IsSubstitution("sc domain", "sc-domain", "open sc domain settings", "open sc-domain settings"));
        Assert.True(DictionaryCorrectionDetector.IsSubstitution("musley", "muesli", "musley and musley", "muesli and musley"));
        Assert.True(DictionaryCorrectionDetector.IsSubstitution("musley", "muesli", "musley,now", "muesli,now"));
        Assert.False(DictionaryCorrectionDetector.IsSubstitution("musley", "muesli", "musley,now muesli", "muesli,now musley"));
    }
}

public sealed class DictationCorrectionBaselineTests
{
    // Long enough to keep a run of unchanged words, which the shared-context rule needs.
    private const string Pasted = "I like museli for breakfast every single morning";
    private const string Box = "Shopping list: museli, oats. " + Pasted;
    private const string Edited = "Shopping list: museli, oats. I like muesli for breakfast every single morning";

    [Fact]
    public void Against_the_dictation_alone_a_repeated_word_hides_the_correction() =>
        // What macOS does today: "museli" appears once before and once after, so it is not a substitution.
        Assert.Empty(DictionaryCorrectionDetector.Suggestions(Pasted, Edited, 3));

    [Fact]
    public void Against_the_box_as_pasted_the_correction_is_found()
    {
        var baseline = new DictationCorrectionBaseline(Pasted);
        Assert.Empty(baseline.EditedSnapshots([Box]));
        Assert.Equal(Box, baseline.Before);

        var edited = Assert.Single(baseline.EditedSnapshots([Edited]));
        var suggestion = Assert.Single(DictionaryCorrectionDetector.Suggestions(Pasted, baseline.Before, edited, 3, null));
        Assert.Equal(("museli", "muesli"), (suggestion.Observed, suggestion.Replacement));
    }

    [Fact]
    public void Unrelated_text_never_becomes_the_baseline()
    {
        var baseline = new DictationCorrectionBaseline(Pasted);
        Assert.Empty(baseline.EditedSnapshots(["completely different editor content here"]));
        Assert.Null(baseline.Text);
        Assert.Equal(Pasted, baseline.Before);
    }

    [Fact]
    public void An_empty_box_still_detects_as_before()
    {
        var baseline = new DictationCorrectionBaseline("I love museli");
        Assert.Empty(baseline.EditedSnapshots(["I love museli "]));
        var edited = Assert.Single(baseline.EditedSnapshots(["I love muesli "]));
        Assert.Equal("museli", Assert.Single(DictionaryCorrectionDetector.Suggestions("I love museli", baseline.Before, edited, 3, null)).Observed);
    }
}

public sealed class DictationCorrectionSnapshotStabilizerTests
{
    private static readonly DateTime Start = new(2026, 11, 28, 10, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Quiet = TimeSpan.FromSeconds(1.5);

    [Fact]
    public void Waits_for_a_quiet_window()
    {
        var stabilizer = new DictationCorrectionSnapshotStabilizer();
        Assert.Empty(stabilizer.Observe(["I usually use this prompt"], Start, Quiet));
        Assert.Empty(stabilizer.Observe(["I usually use this prompt"], Start.AddSeconds(1), Quiet));
        Assert.Equal(["I usually use this prompt"], stabilizer.Observe(["I usually use this prompt"], Start.AddSeconds(1.6), Quiet));
    }

    [Fact]
    public void Resets_the_quiet_window_when_the_snapshot_changes()
    {
        var stabilizer = new DictationCorrectionSnapshotStabilizer();
        Assert.Empty(stabilizer.Observe(["I usually"], Start, Quiet));
        Assert.Empty(stabilizer.Observe(["I usual"], Start.AddSeconds(1), Quiet));
        Assert.Empty(stabilizer.Observe(["I usual"], Start.AddSeconds(2.4), Quiet));
        Assert.Equal(["I usual"], stabilizer.Observe(["I usual"], Start.AddSeconds(2.6), Quiet));
    }

    [Fact]
    public void Evaluates_each_stable_snapshot_once()
    {
        var stabilizer = new DictationCorrectionSnapshotStabilizer();
        Assert.Empty(stabilizer.Observe(["muwsly"], Start, Quiet));
        Assert.Equal(["muwsly"], stabilizer.Observe(["muwsly"], Start.AddSeconds(2), Quiet));
        Assert.Empty(stabilizer.Observe(["muwsly"], Start.AddSeconds(3), Quiet));
    }

    [Fact]
    public void Evaluates_later_stable_snapshots()
    {
        var stabilizer = new DictationCorrectionSnapshotStabilizer();
        const string earlier = "I changed punctuation only.";
        const string later = "I changed museli to muesli.";
        Assert.Empty(stabilizer.Observe([earlier], Start, Quiet));
        Assert.Equal([earlier], stabilizer.Observe([earlier], Start.AddSeconds(1.6), Quiet));
        Assert.Empty(stabilizer.Observe([earlier, later], Start.AddSeconds(1.7), Quiet));
        Assert.Equal([later], stabilizer.Observe([earlier, later], Start.AddSeconds(3.3), Quiet));
    }
}

public sealed class DictionarySuggestionLedgerTests
{
    private static readonly MuesliSettings Enabled = new() { EnableDictionaryCorrectionPrompts = true };
    private static readonly DictionarySuggestion Museli = new("museli", "muesli");

    [Fact]
    public void Prompts_are_off_by_default_and_old_settings_files_keep_them_off()
    {
        Assert.False(new MuesliSettings().EnableDictionaryCorrectionPrompts);
        Assert.Equal((null, null), DictionarySuggestionLedger.Add(new MuesliSettings(), Museli, []));
    }

    [Fact]
    public void Key_is_stable_across_whitespace_and_case() =>
        Assert.Equal(DictionarySuggestionLedger.Key(" Museli ", "Muesli"), DictionarySuggestionLedger.Key("museli", " muesli "));

    [Fact]
    public void Add_records_the_suggestion_first_and_prompts()
    {
        var older = new DictionarySuggestion("teh", "Teh");
        var (settings, prompt) = DictionarySuggestionLedger.Add(Enabled with { DictionarySuggestions = [older] }, Museli, []);
        Assert.Equal(Museli, prompt);
        Assert.Equal([Museli, older], settings!.DictionarySuggestions);
    }

    [Fact]
    public void Add_skips_ignored_corrections_and_existing_dictionary_entries()
    {
        var ignored = Enabled with { DismissedDictionarySuggestionKeys = [DictionarySuggestionLedger.Key("Museli", "Muesli")] };
        Assert.Equal((null, null), DictionarySuggestionLedger.Add(ignored, Museli, []));
        var entry = new DictionaryEntryRecord { Id = "d1", Phrase = "Museli", Replacement = "muesli" };
        Assert.Equal((null, null), DictionarySuggestionLedger.Add(Enabled, Museli, [entry]));
    }

    [Fact]
    public void Accept_adds_a_strict_dictionary_entry_once_and_clears_the_suggestion()
    {
        var settings = Enabled with { DictionarySuggestions = [Museli] };
        var (saved, dictionary) = DictionarySuggestionLedger.Accept(settings, Museli, []);
        Assert.Empty(saved.DictionarySuggestions);
        var entry = Assert.Single(dictionary!);
        Assert.Equal(("museli", "muesli", 0.92), (entry.Phrase, entry.Replacement, entry.MatchingThreshold));

        var (_, again) = DictionarySuggestionLedger.Accept(saved, Museli, dictionary!);
        Assert.Null(again);
    }

    [Fact]
    public void Ignore_remembers_the_correction_and_keeps_the_last_200()
    {
        var full = Enabled with
        {
            DictionarySuggestions = [Museli],
            DismissedDictionarySuggestionKeys = Enumerable.Range(0, 200).Select(index => $"k{index}").ToArray()
        };
        var saved = DictionarySuggestionLedger.Ignore(full, Museli);
        Assert.Empty(saved.DictionarySuggestions);
        Assert.Equal(200, saved.DismissedDictionarySuggestionKeys.Count);
        Assert.Equal("museli->muesli", saved.DismissedDictionarySuggestionKeys[^1]);
        Assert.DoesNotContain("k0", saved.DismissedDictionarySuggestionKeys);
    }

    [Fact]
    public void Snapshot_rules_reject_unchanged_oversized_and_unrelated_text()
    {
        const string pasted = "I love museli";
        Assert.Null(DictationCorrectionMonitorPolicy.NormalizedSnapshot("I  love\nmuseli", pasted));
        Assert.Null(DictationCorrectionMonitorPolicy.NormalizedSnapshot(new string('x', 2001), pasted));
        Assert.Null(DictationCorrectionMonitorPolicy.NormalizedSnapshot("I", pasted));
        Assert.Equal("I love muesli", DictationCorrectionMonitorPolicy.NormalizedSnapshot("I love muesli ", pasted));
    }
}
