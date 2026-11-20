using Muesli.Windows.Services;

namespace Muesli.Windows.Tests;

/// <summary>Cases from macOS DictionaryCorrectionDetectorTests, which the Windows port must match.</summary>
public sealed class DictionaryCorrectionDetectorTests
{
    private const string Lead = "for the dictionary test please write ";
    private const string Tail = " in this sentence today";

    public static TheoryData<string, string, int, string[], string[]> Cases => new()
    {
        { Lead + "musley" + Tail, Lead + "muesli" + Tail, 1, ["musley"], ["muesli"] },
        { Lead + "Thoray Pakam" + Tail, Lead + "Thoraipakkam" + Tail, 1, ["Thoray Pakam"], ["Thoraipakkam"] },
        { Lead + "Mandavali Dirvaniur" + Tail, Lead + "Mandaiveli Thiruvanmiyur" + Tail, 3, ["Mandavali", "Dirvaniur"], ["Mandaiveli", "Thiruvanmiyur"] },
        { Lead + "Thoray I Pakam" + Tail, Lead + "Thoraipakkam" + Tail, 1, [], [] },
        { "the word newsly is hard to transcribe", "the word muesli is hard to transcribe", 1, ["newsly"], ["muesli"] },
        { "often I try to say Alvar Pet in dictation", "often I try to say Alwarpet in dictation", 1, ["Alvar Pet"], ["Alwarpet"] },
        { "please route this to sc domain", "please route this to sc-domain", 1, [], [] },
        { "I moved to New York last year and I like it", "I moved to NYC last year and I like it", 1, [], [] },
        { "we use api and hsr routing today", "we use API and HSR routing today", 3, ["api", "hsr"], ["API", "HSR"] },
        { "we discussed north american expansion today", "we discussed NASA expansion today", 1, [], [] },
        { "we run kubernetes in production for backend services today", "we run k8s in production for backend services today", 1, ["kubernetes"], ["k8s"] },
        {
            "No, typically I think the word muzzle is the worst toughest one for it to transcribe because it usually transcribes to the one starting with the w letter N instead of just muzzli.",
            "No, typically I think the word muesli is the worst toughest one for it to transcribe because it usually transcribes to the one starting with the w letter N instead of just muesli.",
            1, ["muzzle"], ["muesli"]
        },
        {
            "Okay, testing the dictionary prompt once again. So let us see if it can transcribe Mailapur, Mandavali, Trivanmir, Teenagar, Alvarped.",
            "Okay, testing the dictionary prompt once again. So let us see if it can transcribe Mylapore, Mandavali, Thiruvanmiyur, TNagar, Alwarpet.",
            3, ["Mailapur", "Trivanmir", "Teenagar"], ["Mylapore", "Thiruvanmiyur", "TNagar"]
        },
        {
            "Chennai area names called Mylapore, Mandavali, Dirvaniur.",
            "Chennai area names called Mylapore, Mandaiveli, Thiruvanmiyur.",
            3, ["Mandavali", "Dirvaniur"], ["Mandaiveli", "Thiruvanmiyur"]
        },
        {
            "Okay, I am only interested in just like proper noun related corrections, so I don't want any phrasal related changes that is not part of my concern for the dictionary prompts. So let us check. can it transcribe Thoray Pakam, Kara Pakam, Nongam Bakam properly?",
            "Okay, I am only interested in just like proper noun related corrections, so I don't want any phrasal related changes that is not part of my concern for the dictionary prompts. So let us check. can it transcribe Thoraipakkam, Karapakkam, Nungambakkam properly?",
            3, ["Thoray Pakam", "Kara Pakam", "Nongam Bakam"], ["Thoraipakkam", "Karapakkam", "Nungambakkam"]
        },
        {
            "So this time if I say Newsly has not transcribed Newsly properly, would you be able to add it to dictionary immediately?",
            "So this time if I say muesli has not transcribed muesli properly, would you be able to add it to dictionary immediately? (still did not trigger the prompt unfortunately)",
            1, ["Newsly"], ["muesli"]
        },
        { "I like museli today", "I like fresh muesli today", 3, ["museli"], ["muesli"] },
        { "Hey Musley, can you see if you're transcribing the word correctly?", "Hey Muesli, can you see if you're transcribing the word correctly?", 1, ["Musley"], ["Muesli"] },
        { "talk to pranav today", "talk to Pranav today", 1, ["pranav"], ["Pranav"] },
        { "ship teh update", "ship the update", 1, [], [] },
        { "please look up spelling correction", "please look file_00000000f4ac61f4841155122554864c-sanitized spelling correction", 1, [], [] },
        { "Please review the Vitruvian design note", "Please review the follow-up design note", 1, [], [] },
        { "Please review the Vitruvian design note", "Please review the k8s design note", 1, [], [] },
        { "Please review the validation design note today", "Please review the v2n design note today", 1, [], [] },
        { "Please review the token design note today", "Please review the t3n design note today", 1, [], [] },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void MatchesMacOsDetector(string original, string edited, int max, string[] observed, string[] replacement)
    {
        var suggestions = DictionaryCorrectionDetector.Suggestions(original, edited, maxSuggestions: max);
        Assert.Equal(observed, suggestions.Select(item => item.Observed));
        Assert.Equal(replacement, suggestions.Select(item => item.Replacement));
    }

    [Fact]
    public void LargeSnapshotsAndAppContext()
    {
        var prefix = string.Join(' ', Enumerable.Range(0, 40).Select(index => $"prefix{index}"));
        var middle = string.Join(' ', Enumerable.Range(0, 140).Select(index => $"middle{index}"));
        var found = DictionaryCorrectionDetector.Suggestions(
            $"{prefix} I like museli today {middle} I mentioned newsly again",
            $"{prefix} I like muesli today {middle} I mentioned muesli again",
            "Notepad|notepad", 3);
        Assert.Equal(["museli", "newsly"], found.Select(item => item.Observed));
        Assert.All(found, item => Assert.Equal("Notepad", item.AppName));

        var repeated = string.Join(' ', Enumerable.Repeat("and then", 160));
        Assert.Equal(2, DictionaryCorrectionDetector.Suggestions(
            $"I like museli today {repeated} I mentioned newsly again",
            $"I like muesli today {repeated} I mentioned muesli again", maxSuggestions: 3).Count);
    }

    [Fact]
    public void BaselineAndEnglishWordRules()
    {
        Assert.Empty(DictionaryCorrectionDetector.Suggestions("hello muesli", "before hello muesli", "before hello muesli!", "", 1, null));
        Assert.Empty(DictionaryCorrectionDetector.Suggestions(
            "I usually use this prompt", "I usually use this prompt", "I usual use this prompt", "", 1, new HashSet<string> { "usually", "usual" }));

        var filler = string.Join(' ', Enumerable.Range(0, 180).Select(index => $"a{index}"));
        var candidates = DictionaryCorrectionDetector.RequiredEnglishWordCandidates($"{filler} internationalization", $"{filler} international");
        Assert.Contains("internationalization", candidates);
        Assert.Contains("international", candidates);
    }

    [Fact]
    public void SharedContext()
    {
        Assert.True(DictionaryCorrectionDetector.HasSufficientSharedContext(
            "So this time if I say Newsly has not transcribed Newsly properly",
            "So this time if I say muesli has not transcribed muesli properly and then I kept typing"));
        Assert.False(DictionaryCorrectionDetector.HasSufficientSharedContext("hello muesli today", "unrelated editor content"));
        Assert.True(DictionaryCorrectionDetector.HasSufficientSharedContext("Alvar Pet", "Alwarpet"));
    }

    [Fact]
    public void StabilizerWaitsForQuietAndEvaluatesOnce()
    {
        var stabilizer = new DictationCorrectionSnapshotStabilizer();
        var start = new DateTime(2026, 10, 12, 12, 0, 0);
        var quiet = TimeSpan.FromSeconds(1.5);
        Assert.Empty(stabilizer.Observe(["I usually"], start, quiet));
        Assert.Empty(stabilizer.Observe(["I usual"], start.AddSeconds(1), quiet));
        Assert.Empty(stabilizer.Observe(["I usual"], start.AddSeconds(2.4), quiet));
        Assert.Equal(["I usual"], stabilizer.Observe(["I usual"], start.AddSeconds(2.6), quiet));
        Assert.Empty(stabilizer.Observe(["I usual"], start.AddSeconds(4), quiet));
    }

    [Fact]
    public void BookCountsRepeatsHonoursIgnoreAndAccepts()
    {
        var settings = new MuesliSettings { EnableDictionaryCorrectionPrompts = true };
        var seen = new DictionarySuggestion("museli", "muesli") { AppContext = "Notepad|notepad" };

        Assert.Null(DictionarySuggestionBook.Add(new MuesliSettings(), [], seen).Prompt);

        var (once, prompt) = DictionarySuggestionBook.Add(settings, [], seen);
        Assert.NotNull(prompt);
        var (twice, _) = DictionarySuggestionBook.Add(once, [], seen);
        Assert.Equal(2, Assert.Single(twice.DictionarySuggestions).OccurrenceCount);
        Assert.Equal("Seen 2x | Notepad", twice.DictionarySuggestions[0].Detail);

        var ignored = DictionarySuggestionBook.Dismiss(twice, seen);
        Assert.Empty(ignored.DictionarySuggestions);
        Assert.Null(DictionarySuggestionBook.Add(ignored, [], seen).Prompt);

        var (accepted, words) = DictionarySuggestionBook.Accept(twice, [], seen);
        Assert.Empty(accepted.DictionarySuggestions);
        var word = Assert.Single(words);
        Assert.Equal(("museli", "muesli", 0.92), (word.Phrase, word.Replacement, word.MatchingThreshold));
        Assert.Null(DictionarySuggestionBook.Add(accepted, words, seen).Prompt);
    }

    [Fact]
    public void WindowsSpellCheckerKnowsEnglishWords()
    {
        var recognized = WindowsSpellChecker.Recognized(["usually", "musley"]);
        Assert.Contains("usually", recognized);
        Assert.DoesNotContain("musley", recognized);
    }
}
