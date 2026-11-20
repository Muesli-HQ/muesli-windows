using System.Text;
using Muesli.Windows.Cotypist;

namespace Muesli.Windows.Tests;

// Ported from macOS CotypistShaperTests (2f10b23d).
public sealed class CotypistShaperTests
{
    private static CotypistCompletion Completion(CotypistStop stop, params (string Text, double LogProb)[] tokens) =>
        new(tokens.Select(token => new CotypistToken(token.Text, token.LogProb)).ToList(), stop);

    private static CotypistShaped Shape(CotypistCompletion raw, bool afterSpace = true, string? heal = null, bool firstWordOnly = false, CotypistShaper.Thresholds? thresholds = null) =>
        CotypistShaper.Shape(raw, afterSpace, heal, firstWordOnly, thresholds);

    [Fact]
    public void AfterASpaceDropsTheLeadingSpaceAndKeepsCompleteWords()
    {
        var shaped = Shape(Completion(CotypistStop.Limit, (" file", -1.0), (" to", -0.5), (" you", -0.4), (" in", -0.3), (" a", -0.2), (" few", -0.2), (" mi", -0.1), ("nutes", -0.1), (".", -0.1), (" Thanks", -0.5)));
        Assert.Equal("file to you in a few", shaped.Text);
        Assert.Equal(-1.0, shaped.FirstWordLogProb);
    }

    [Fact]
    public void LengthAndFrequencySetTheWordLimitAndTheGate()
    {
        var raw = Completion(CotypistStop.EndOfText, (" file", -2.5), (" to", -0.5), (" you", -0.4), (" in", -0.3), (" a", -0.2));
        var phrase = new CotypistShaper.Thresholds(CotypistOptions.FirstWordGate(CotypistFrequency.More, false), MaxWords: CotypistSuggestionLength.Phrase.MaxWords());
        Assert.Equal("file to you", Shape(raw, thresholds: phrase).Text);
        var balanced = new CotypistShaper.Thresholds(CotypistOptions.FirstWordGate(CotypistFrequency.Balanced, false));
        var rejected = Shape(raw, thresholds: balanced);
        Assert.Equal(CotypistRejection.LowConfidence, rejected.Rejection);
        Assert.Equal(-2.5, rejected.FirstWordLogProb);
        Assert.True(CotypistOptions.FirstWordGate(CotypistFrequency.Balanced, true) <= CotypistOptions.FirstWordGate(CotypistFrequency.Balanced, false));
        Assert.True(CotypistOptions.FirstWordGate(CotypistFrequency.Fewer, false) > CotypistOptions.FirstWordGate(CotypistFrequency.More, false));
    }

    [Fact]
    public void NeverShowsAFirstWordTheBudgetCutOff()
    {
        var raw = new CotypistCompletion(Enumerable.Range(0, 10).Select(index => new CotypistToken(index == 0 ? " anti" : "dis", -0.1)).ToList(), CotypistStop.Limit);
        Assert.Equal(CotypistRejection.Empty, Shape(raw).Rejection);
    }

    [Fact]
    public void GatesAndWordBoundaries()
    {
        Assert.Equal(CotypistRejection.LowConfidence, Shape(Completion(CotypistStop.EndOfText, (" payment", -3.5), (" now", -0.5))).Rejection);
        Assert.Equal(CotypistRejection.ContinuesPreviousWord, Shape(Completion(CotypistStop.EndOfText, ("ing", -0.2), (" now", -0.5))).Rejection);
        Assert.Equal("questions.", Shape(Completion(CotypistStop.EndOfText, (" questions", -1.6), (".", -0.2))).Text);
        Assert.Equal(CotypistRejection.HealFailed, Shape(Completion(CotypistStop.HealFailed)).Rejection);
    }

    [Fact]
    public void FirstWordStageShowsOnlyAWordKnownToBeComplete()
    {
        Assert.Equal("help", Shape(Completion(CotypistStop.Limit, (" help", -2.0), (" me", -0.2), (" with", -0.4), (" this", -0.5)), firstWordOnly: true).Text);
        Assert.Equal(CotypistRejection.FirstWordIncomplete, Shape(Completion(CotypistStop.Limit, (" rec", -1.0), ("eiv", -0.1), ("ab", -0.1), ("le", -0.1)), firstWordOnly: true).Rejection);
        // A character split across byte tokens doesn't make a cut-off word look complete.
        Assert.Equal("ordered", Shape(Completion(CotypistStop.Limit, (" ordered", -1.0), (" caf", -0.5), ("é", -0.2), ("t", -0.1))).Text);
        Assert.Equal(CotypistRejection.FirstWordIncomplete, Shape(Completion(CotypistStop.Limit, (" caf", -0.5), ("é", -0.2), ("t", -0.1)), firstWordOnly: true).Rejection);
    }

    [Fact]
    public void HealingShowsTheRestOfTheTypedWordAndIgnoresForcedTokens()
    {
        var shaped = Shape(Completion(CotypistStop.EndOfText, (" rece", -6.0), ("i", -4.0), ("ve", -0.3), (" the", -0.4), (" file", -0.9), (".", -0.2)), afterSpace: false, heal: " recei");
        Assert.Equal("ve the file.", shaped.Text);
        Assert.Equal(-0.3, shaped.FirstWordLogProb);
        Assert.Equal(" works?", Shape(Completion(CotypistStop.EndOfText, (" screen", -5.0), (" works", -1.2), ("?", -0.3)), afterSpace: false, heal: " screen").Text);
    }

    [Fact]
    public void SplitsTheHalfTypedWordAndFlowsScreenText()
    {
        Assert.Equal(("Thanks for the", " quic"), CotypistText.SplitPartialWord("Thanks for the quic"));
        Assert.Equal(("Hi team,\n", "We"), CotypistText.SplitPartialWord("Hi team,\nWe"));
        Assert.Equal(("", "Thou"), CotypistText.SplitPartialWord("Thou"));
        Assert.Null(CotypistText.SplitPartialWord("the q"));
        Assert.Null(CotypistText.SplitPartialWord("the "));

        var ocr = "File\nEdit\nThe quarterly numbers look right\nSend\nThanks, I will forward them to legal today\nOK";
        Assert.Equal("The quarterly numbers look right Thanks, I will forward them to legal today", CotypistText.PromptReadyScreenText(ocr));
        Assert.Equal("Thanks, I will forward them to legal today", CotypistText.PromptReadyScreenText(ocr, 50));
    }

    [Fact]
    public void VocabularyPrefersCoveringTokensAndSetsAsideLineBreaks()
    {
        byte[]? Piece(string text) => Encoding.UTF8.GetBytes(text);
        var vocabulary = new CotypistVocabulary([Piece(" num"), Piece(" numb"), Piece(" number"), Piece(".\n"), null, [0xC3], Piece(" n")]);
        Assert.Equal([1, 2], vocabulary.Allowed(" numb"));
        Assert.Equal([0, 6], vocabulary.Allowed(" numx"));
        Assert.Equal([3], vocabulary.Newlines);
        Assert.Empty(vocabulary.Allowed(""));
        Assert.Equal("�", vocabulary.Pieces[5]);
    }
}

public sealed class CotypistModelCatalogTests
{
    [Fact]
    public void EveryPublishedModelIsPinnedAndHashedAndOneIsRecommended()
    {
        foreach (var model in CotypistModels.All.Where(model => model.Release is not null))
        {
            var release = model.Release!;
            Assert.Equal(40, release.SourceRevision.Split('@')[^1].Length);
            Assert.Equal(64, release.Sha256.Length);
            Assert.True(release.SizeBytes > 0);
            Assert.Contains("/resolve/" + release.SourceRevision.Split('@')[^1] + "/", release.DownloadUrl);
            Assert.EndsWith(".gguf", model.Path);
        }
        Assert.Equal([CotypistModels.Default], CotypistModels.All.Where(model => model.IsRecommended));
        Assert.Null(CotypistModels.Get("gemma-4-e2b-base").Release);
        Assert.Same(CotypistModels.Default, CotypistModels.Get("unknown"));
    }
}

public sealed class CotypistEngineTests
{
    private sealed class CotypistModelFactAttribute : FactAttribute
    {
        public CotypistModelFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("MUESLI_COTYPIST_MODEL") is null)
                Skip = "Set MUESLI_COTYPIST_MODEL to a Cotypist base-model GGUF.";
        }
    }

    [CotypistModelFact]
    public async Task ContinuesTextHealsAHalfTypedWordAndReusesThePromptCache()
    {
        using var engine = new CotypistEngine(Environment.GetEnvironmentVariable("MUESLI_COTYPIST_MODEL")!);
        var first = await engine.CompleteAsync("The capital of France is", 8);
        Assert.NotEmpty(first.Tokens);
        Assert.DoesNotContain('\n', first.Content);

        var healed = await engine.CompleteAsync("Thanks for the", 6, " quic");
        Assert.NotEqual(CotypistStop.HealFailed, healed.Stop);
        Assert.StartsWith(" quic", healed.Content);

        // Qwen 3.5 is hybrid recurrent: like macOS's non-trimmable cache, a diverging prompt starts fresh.
        var again = await engine.CompleteAsync("Thanks for the quick reply. I will", 4);
        Assert.NotEmpty(again.Tokens);
        var shaped = CotypistShaper.Shape(healed, afterSpace: false, heal: " quic", firstWordOnly: false);
        Assert.True(shaped.IsShown ? shaped.Text!.Length > 0 : shaped.Rejection is not null);
        Assert.Throws<OperationCanceledException>(() => engine.CompleteAsync("Hello there", 4, isCurrent: () => false).GetAwaiter().GetResult());
    }
}
