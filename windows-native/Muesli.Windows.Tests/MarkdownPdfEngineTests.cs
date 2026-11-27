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
