using System.IO.Compression;
using System.Text.RegularExpressions;
using Muesli.Windows.Services;
using Muesli.Windows.Services.Pdf;

namespace Muesli.Windows.Tests;

/// <summary>
/// The in-house PDF engine: DirectWrite shapes the text, the engine writes the PDF. These tests read the
/// produced file directly (page objects, link annotations, inflated content streams) rather than trusting size.
/// </summary>
public sealed class MarkdownPdfEngineTests
{
    private static readonly DateTimeOffset Created = new(2026, 9, 26, 9, 0, 0, TimeSpan.Zero);

    private const string Sample =
        "# Release sync 发布同步\n\n**Date:** 2026-09-26 09:00\n\n---\n\n## Summary\n- Ship on **Friday**\n- [x] 完成测试\n\n" +
        "## Transcript\n\n[09:00:00] You: we agreed to ship on Friday.\n[09:00:12] Speaker 2: हमें एक और दिन चाहिए।\n[09:00:20] Speaker 3: سأرسل الملخص.";

    [Fact]
    public void TheSameMarkdownAndDateGiveIdenticalBytes()
    {
        var first = MarkdownPdf.Render(Sample, Created);
        var second = MarkdownPdf.Render(Sample, Created);

        Assert.Equal("%PDF-", Encoding.ASCII.GetString(first, 0, 5));
        Assert.Equal(first, second);
        Assert.NotEqual(first, MarkdownPdf.Render(Sample, Created.AddDays(1)));
    }

    [Fact]
    public void EveryPageCarriesTheLogoAndTheLinkedFooter()
    {
        var lines = Enumerable.Range(1, 180).Select(i => $"Line {i}: The team reviewed export branding and confirmed the next milestone.");
        var pdf = Latin1(MarkdownPdf.Render("## Review\n" + string.Join("\n", lines), Created));

        var pages = Regex.Matches(pdf, @"/Type /Page[^s]").Count;
        Assert.True(pages > 1, $"Expected several pages, found {pages}.");
        Assert.Equal(pages, Regex.Matches(pdf, @"/URI \(https://muesli\.works\)").Count);
        // One logo image (plus its soft mask), drawn once on each page at 70% opacity.
        Assert.Equal(2, Regex.Matches(pdf, "/Subtype /Image").Count);
        Assert.Contains("/ca 0.7", pdf, StringComparison.Ordinal);
    }

    [Fact]
    public void TheMarkdownBrandingFooterStaysOutOfThePdfBody()
    {
        const string footer = "\n\n---\n\n<img src=\"data:image/png;base64,iVBORw0KGgo=\" alt=\"muesli logo\" width=\"24\" height=\"24\" />\n\n[Exported with muesli](https://muesli.works)";

        Assert.Equal("# Title\n\nBody", MarkdownPdfEngine.StripBrandFooter("# Title\n\nBody" + footer));
        Assert.Equal("# Title\n\nBody", MarkdownPdfEngine.StripBrandFooter("# Title\n\nBody"));
        Assert.Equal(MarkdownPdf.Render("# Title\n\nBody", Created), MarkdownPdf.Render("# Title\n\nBody" + footer, Created));
    }

    [Fact]
    public void InlineMarkdownIsParsedRatherThanPrinted()
    {
        var spans = MarkdownPdfEngine.ParseInline("Ship on **Friday** if *clean*, see [the list](https://example.com) and 2 * 3 = 6");

        Assert.DoesNotContain(spans, s => s.Text.Contains("**", StringComparison.Ordinal));
        Assert.Contains(spans, s => s is { Text: "Friday", Bold: true });
        Assert.Contains(spans, s => s is { Text: "clean", Italic: true });
        Assert.Contains(spans, s => s is { Text: "the list", Link: "https://example.com" });
        Assert.Contains(spans, s => s.Text.Contains("2 * 3 = 6", StringComparison.Ordinal));
    }

    [Fact]
    public void ComplexScriptClustersCarryTheirSourceTextForCopyAndSearch()
    {
        var pdf = MarkdownPdf.Render("हिन्दी क्ष", Created, new PdfOptions(PageNumbers: false));
        var content = string.Concat(InflatedStreams(pdf));

        // A conjunct (consonant + virama U+094D + consonant) is drawn with reordered or fused glyphs, so a
        // per-glyph ToUnicode entry cannot describe it: the cluster must carry its text in an ActualText span.
        Assert.Matches(@"/ActualText <FEFF(?:[0-9A-F]{4})*094D(?:[0-9A-F]{4})*>", content);
    }

    [Fact]
    public void FontsThatCannotBeEmbeddedAreDrawnAsOutlinesAndStayRealText()
    {
        // ForceOutlineFonts sends every font down the path taken by CFF, variable and
        // embedding-restricted fonts, so that path is covered whatever fonts this PC has.
        var options = new PdfOptions(PageNumbers: false, ForceOutlineFonts: true);
        var bytes = MarkdownPdf.Render(Sample, Created, options);
        var pdf = Latin1(bytes);

        Assert.Contains("/Subtype /Type3", pdf, StringComparison.Ordinal);
        Assert.DoesNotContain("/FontFile2", pdf, StringComparison.Ordinal);
        Assert.Contains("/ToUnicode", pdf, StringComparison.Ordinal);
        Assert.Equal(bytes, MarkdownPdf.Render(Sample, Created, options));

        var streams = InflatedStreams(bytes).ToList();
        // Glyph procedures are filled paths; the page still shows text operators and conjunct text.
        Assert.Contains(streams, s => s.Contains(" d1\n", StringComparison.Ordinal) && s.TrimEnd().EndsWith('f'));
        Assert.Contains(streams, s => s.Contains(" Tf\n", StringComparison.Ordinal) && s.Contains("/ActualText", StringComparison.Ordinal));
    }

    [Fact]
    public void AVariableFontExportsInsteadOfFailing()
    {
        // Bahnschrift ships with Windows as a variable font; its file only holds the default instance, so
        // it must not be embedded as TrueType. Where it is missing, DirectWrite substitutes another font.
        var bytes = MarkdownPdf.Render("# Variable\n\nBody **bold** text", Created, new PdfOptions(PageNumbers: false, Family: "Bahnschrift"));

        Assert.Equal("%PDF-", Encoding.ASCII.GetString(bytes, 0, 5));
        var installed = File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "bahnschrift.ttf"));
        if (installed) Assert.Contains("/Subtype /Type3", Latin1(bytes), StringComparison.Ordinal);
    }

    [Fact]
    public void AMoreThan256GlyphOutlineFontIsSplitAcrossSeveralFonts()
    {
        var text = string.Concat(Enumerable.Range(0x4E00, 600).Select(c => (char)c));
        var pdf = Latin1(MarkdownPdf.Render(text, Created, new PdfOptions(PageNumbers: false, ForceOutlineFonts: true)));

        Assert.True(Regex.Matches(pdf, "/Subtype /Type3").Count >= 3, "600 distinct glyphs need at least three one-byte fonts.");
    }

    [Theory]
    [InlineData("https://muesli.works", "https://muesli.works")]
    [InlineData("https://example.com/会议?q=é x", "https://example.com/%E4%BC%9A%E8%AE%AE?q=%C3%A9%20x")]
    [InlineData("https://münchen.example/a", "https://xn--mnchen-3ya.example/a")]
    [InlineData("mailto:名前@example.com", "mailto:%E5%90%8D%E5%89%8D@example.com")]
    public void LinkTargetsAreWrittenAsAsciiUris(string url, string expected)
    {
        Assert.Equal(expected, MarkdownPdfEngine.AsciiUri(url));

        var pdf = Latin1(MarkdownPdf.Render($"See [the notes]({url}) today", Created, new PdfOptions(PageNumbers: false)));
        Assert.Contains($"/URI ({expected})", pdf, StringComparison.Ordinal);
    }

    [Fact]
    public void ATableRowTallerThanAPageContinuesOnTheNextPageInsteadOfBeingClipped()
    {
        var longCell = string.Join(" ", Enumerable.Range(1, 900).Select(i => $"word{i}"));
        var options = new PdfOptions(PageNumbers: false); // 108 pt margins, no footer
        var bytes = MarkdownPdf.Render($"| Topic | Notes |\n|---|---|\n| Long | {longCell} |\n| After | done |", Created, options);

        Assert.True(Regex.Matches(Latin1(bytes), @"/Type /Page[^s]").Count > 1, "The tall row should spill onto further pages.");
        var baselines = InflatedStreams(bytes)
            .SelectMany(s => Regex.Matches(s, @" ([\d.]+) Tm").Select(m => double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)))
            .ToList();
        Assert.NotEmpty(baselines);
        // Every line of text sits inside the page's content area: nothing is drawn into or below the bottom margin.
        Assert.All(baselines, y => Assert.InRange(y, 108, 792 - 108));
    }

    [Fact]
    public void TwoWeightsOfOneVariableFontAreSeparateFonts()
    {
        if (!File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "bahnschrift.ttf"))) return;

        var options = new PdfOptions(PageNumbers: false, Family: "Bahnschrift");
        var bytes = MarkdownPdf.Render("# Heading in bold\n\nBody in regular", Created, options);

        // Bold and regular come from the same font file; sharing one PDF font would draw both with one weight.
        Assert.True(Regex.Matches(Latin1(bytes), "/Subtype /Type3").Count >= 2);
        Assert.Equal(bytes, MarkdownPdf.Render("# Heading in bold\n\nBody in regular", Created, options));
    }

    [Theory]
    [InlineData("وافق الفريق على إصدار النسخة الجديدة يوم الجمعة، لا إله إلا الله")]
    [InlineData("הצוות הסכים לשחרר את הגרסה החדשה ביום שישי")]
    [InlineData("Mixed العربية and עברית with 2026 inside")]
    public void RightToLeftRunsKeepAnAscendingClusterMap(string text)
    {
        // The engine derives each cluster's glyphs from the cluster map. DirectWrite numbers glyphs in
        // logical order for both directions, so the map never descends; this pins that down.
        var runs = MarkdownPdfEngine.ShapeForInspection(text);

        Assert.Contains(runs, r => r.Rtl);
        foreach (var run in runs)
        {
            Assert.Equal(run.Text.Length, run.ClusterMap.Length);
            for (int i = 1; i < run.ClusterMap.Length; i++)
                Assert.True(run.ClusterMap[i] >= run.ClusterMap[i - 1], $"Cluster map descends at {i} in an {(run.Rtl ? "RTL" : "LTR")} run.");
            Assert.All(run.ClusterMap, glyph => Assert.InRange(glyph, 0, run.GlyphCount - 1));
        }
    }

    [Fact]
    public void RightToLeftTextCarriesItsSourceTextInLogicalOrder()
    {
        // Lam + alef (U+0644 U+0627). A font may draw them as one ligature glyph, which needs an ActualText span
        // holding both letters in logical order, or as two contextual glyphs, each mapped to its own letter.
        var content = string.Concat(InflatedStreams(MarkdownPdf.Render("لا إله", Created, new PdfOptions(PageNumbers: false))));

        bool ligature = content.Contains("/ActualText <FEFF06440627>", StringComparison.Ordinal);
        bool separate = Regex.IsMatch(content, @"<[0-9A-F]{4}> <0644>") && Regex.IsMatch(content, @"<[0-9A-F]{4}> <0627>");
        Assert.True(ligature || separate, "Neither a lam-alef ActualText span nor per-letter ToUnicode entries were written.");
        Assert.DoesNotContain("<FEFF06270644>", content, StringComparison.Ordinal); // never reversed
    }

    [Theory]
    [InlineData("See [docs](https://en.wikipedia.org/wiki/Function_(mathematics)) now", "https://en.wikipedia.org/wiki/Function_(mathematics)", " now")]
    [InlineData("See [docs](https://example.com/a) (later)", "https://example.com/a", " (later)")]
    [InlineData(@"See [docs](https://example.com/a\)b) now", "https://example.com/a)b", " now")]
    public void LinkTargetsMayContainBalancedOrEscapedParentheses(string markdown, string target, string after)
    {
        var spans = MarkdownPdfEngine.ParseInline(markdown);

        Assert.Contains(spans, s => s.Text == "docs" && s.Link == target);
        Assert.Equal(after, spans[^1].Text);
    }

    [Fact]
    public void AnEscapedPipeStaysInsideItsTableCell()
    {
        Assert.Equal(["Command", "`a|b`", "x | y"], MarkdownPdfEngine.SplitTableRow(@"| Command | `a\|b` | x \| y |"));
        Assert.Equal(["a", "", "c"], MarkdownPdfEngine.SplitTableRow("| a |  | c |"));
        Assert.Equal(["a", "b"], MarkdownPdfEngine.SplitTableRow("a | b"));
    }

    [Fact]
    public void ColourEmojiLayersAreDrawnInsideOneTextSpan()
    {
        var bytes = MarkdownPdf.Render("Party 🎉 time", Created, new PdfOptions(PageNumbers: false));
        var content = string.Concat(InflatedStreams(bytes));

        // U+1F389 as a surrogate pair; its colour layers are glyph runs inside that span.
        Assert.Contains("/ActualText <FEFFD83CDF89>", content, StringComparison.Ordinal);
        // Any translucent layer must have a matching graphics state; opaque-only emoji declare none.
        foreach (Match m in Regex.Matches(content, @"/GA(\d+) gs"))
            Assert.Contains($"/GA{m.Groups[1].Value} << /ca", Latin1(bytes), StringComparison.Ordinal);
    }

    private static string Latin1(byte[] bytes) => Encoding.Latin1.GetString(bytes);

    private static IEnumerable<string> InflatedStreams(byte[] pdf)
    {
        var text = Latin1(pdf);
        foreach (Match m in Regex.Matches(text, @"<< /Length (\d+) /Filter /FlateDecode [^>]*>>\nstream\n"))
        {
            int start = m.Index + m.Length, length = int.Parse(m.Groups[1].Value);
            using var z = new ZLibStream(new MemoryStream(pdf, start, length), CompressionMode.Decompress);
            using var o = new MemoryStream();
            string? inflated = null;
            try { z.CopyTo(o); inflated = Latin1(o.ToArray()); } catch (InvalidDataException) { }
            if (inflated is not null) yield return inflated;
        }
    }
}
