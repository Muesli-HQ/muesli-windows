// Markdown -> paginated PDF. DirectWrite does line breaking, bidi, font fallback and shaping;
// this file does the block layout, pagination and the PDF container (Type0 / CIDFontType2 fonts).
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Muesli.Windows.Services.Pdf;

/// <summary>Defaults reproduce the current Windows exporter's page; <see cref="MacParity"/> reproduces the macOS app's.</summary>
internal sealed record PdfOptions(
    bool PageNumbers = true, string Family = "Segoe UI", float Margin = 108, float TopPadding = 20, float BodySize = 11,
    float H1Size = 22, float H2Size = 17, float H3Size = 13, int H3Weight = 700,
    float H1Before = 0, float H2Before = 12, float H3Before = 8, float BlankLine = 8, bool DrawRules = true,
    byte[]? BrandLogo = null, string BrandText = "Exported with muesli", string BrandUrl = "https://muesli.works",
    Func<string, byte[]?>? ImageResolver = null, bool ForceOutlineFonts = false)
{
    /// <summary>
    /// The macOS exporter's layout (MeetingExporter.writePDF + MeetingExportBranding.writeBrandedPDF): US Letter,
    /// 1 inch margins, 13 pt body, 22/17/14 pt headings with 14/12/8 pt above, a blank line is a full body line,
    /// "---" is a gap rather than a drawn line, no page numbers, and the logo + linked label in every bottom margin.
    /// </summary>
    public static PdfOptions MacParity(byte[] logo, Func<string, byte[]?>? images = null) => new(
        PageNumbers: false, Margin: 72, TopPadding: 0, BodySize: 13, H3Size: 14, H3Weight: 600,
        H1Before: 14, H2Before: 12, H3Before: 8, BlankLine: 0, DrawRules: false, BrandLogo: logo, ImageResolver: images);
}

internal static class MarkdownPdf
{
    private static readonly Lazy<byte[]> ExportLogo = new(() =>
    {
        using var stream = typeof(MarkdownPdf).Assembly.GetManifestResourceStream("Muesli.Windows.Platform.Assets.muesli-export-logo.png")
            ?? throw new InvalidOperationException("The bundled export logo is missing.");
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    });

    /// <summary>The meeting export layout shared with the macOS app: its page geometry and the logo + link footer.</summary>
    public static PdfOptions MeetingExportOptions => PdfOptions.MacParity(ExportLogo.Value);

    /// <summary>
    /// Synchronous, needs no UI thread, and returns the same bytes for the same (markdown, createdAt) on one
    /// machine. Failures surface as the exception types the export callers already handle.
    /// </summary>
    public static byte[] Render(string markdown, DateTimeOffset createdAt, PdfOptions? options = null)
    {
        options ??= MeetingExportOptions;
        try
        {
            return new MarkdownPdfEngine(options).Run(markdown, createdAt);
        }
        catch (Exception first) when (first is not OutOfMemoryException && !options.ForceOutlineFonts)
        {
            // Fonts are the one input this engine does not control: any installed font can be picked by
            // fallback. If embedding one of them went wrong, draw every font as outlines instead, which
            // needs nothing from the font file beyond what DirectWrite already rasterises.
            try
            {
                return new MarkdownPdfEngine(options with { ForceOutlineFonts = true }).Run(markdown, createdAt);
            }
            catch (Exception second) when (second is not OutOfMemoryException)
            {
                throw new InvalidOperationException("The PDF could not be rendered.", new AggregateException(first, second));
            }
        }
        catch (Exception ex) when (ex is not (InvalidOperationException or IOException or NotSupportedException or OutOfMemoryException))
        {
            throw new InvalidOperationException("The PDF could not be rendered.", ex);
        }
    }
}

internal readonly record struct Rgb(float R, float G, float B)
{
    public static readonly Rgb Black = new(0, 0, 0), Grey = new(0.62f, 0.62f, 0.62f), Link = new(0.02f, 0.39f, 0.76f);
}

internal sealed record Span(string Text, bool Bold = false, bool Italic = false, bool Code = false, string? Link = null);

internal sealed unsafe class FontRes
{
    public int Index; public void* Face; public DFontMetrics Metrics;
    public readonly SortedSet<ushort> Used = new() { 0 };
    public readonly Dictionary<ushort, string> ToUnicode = new();
    private readonly Dictionary<ushort, int> _widths = new();

    /// <summary>
    /// True when the face cannot be embedded as a TrueType subset (see <see cref="FontSubset.CanEmbed"/>). Its glyphs
    /// are then written as vector outlines in Type 3 fonts, which address glyphs with one byte: each glyph gets a
    /// slot in order of first use, and every 256 slots form one PDF font.
    /// </summary>
    public bool Outline;
    public readonly List<ushort> SlotGlyphs = new();
    private readonly Dictionary<ushort, int> _slots = new();
    public int Slot(ushort gid)
    {
        if (!_slots.TryGetValue(gid, out var slot)) { _slots[gid] = slot = SlotGlyphs.Count; SlotGlyphs.Add(gid); }
        return slot;
    }

    public int Width1000(ushort gid)
    {
        if (!_widths.TryGetValue(gid, out var w))
            _widths[gid] = w = (int)Math.Round(DW.DesignAdvance(Face, gid) * 1000.0 / Metrics.UnitsPerEm);
        return w;
    }
}

internal class GlyphsRec
{
    public FontRes Font = null!; public float Size, X, Y; public ushort[] Gids = []; public float[] Adv = [];
    public GlyphOffset[]? Off; public bool Rtl; public int Sim;
}
internal sealed class LayerRec : GlyphsRec { public Rgb Color; public bool UseForeground; }
internal sealed class RunRec : GlyphsRec
{
    public string Text = ""; public ushort[] Cluster = []; public int TextPos; public List<LayerRec>? Layers;
}

internal sealed class LaidOut
{
    public readonly List<RunRec> Runs = new();
    public readonly List<(int Start, int Length, string Url)> Links = new();
    public DLineMetrics[] Lines = [];
    public float Height, Width;
    public string? LinkAt(int pos)
    {
        foreach (var l in Links) if (pos >= l.Start && pos < l.Start + l.Length) return l.Url;
        return null;
    }
}

internal sealed class PageBuf
{
    public readonly StringBuilder Ops = new();
    public readonly List<(float X0, float Y0, float X1, float Y1, string Url)> Links = new();
}

internal sealed unsafe partial class MarkdownPdfEngine : IGlyphSink
{
    private const float PageW = 612, PageH = 792;
    private readonly float Top, Bottom, Left, ContentW;
    private readonly List<ImageRes> _images = new();
    private static readonly object LogoLock = new();
    private static byte[]? _logoSource;
    private static ImageRes? _logoDecoded;
    private readonly PdfOptions _o;
    private readonly List<PageBuf> _pages = new();
    private readonly List<FontRes> _fonts = new();
    private readonly Dictionary<string, FontRes> _fontsByKey = new();
    private readonly Dictionary<nint, FontRes> _fontsByFace = new();
    private PageBuf _pg = null!;
    private float _y;
    private LaidOut? _sinkTarget;
    private string? _title;

    public MarkdownPdfEngine(PdfOptions o)
    {
        _o = o;
        Top = Left = o.Margin; Bottom = PageH - o.Margin; ContentW = PageW - 2 * o.Margin;
    }

    private ImageRes AddImage(byte[] bytes)
    {
        var image = ImageCodec.Decode(bytes);
        image.Index = _images.Count;
        _images.Add(image);
        return image;
    }

    /// <summary>Draws an image with its top-left corner at (x, yTop) in top-down page coordinates.</summary>
    private static void DrawImage(PageBuf pg, ImageRes image, float x, float yTop, float w, float h, bool translucent = false)
        => pg.Ops.Append("q ").Append(translucent ? "/GS0 gs " : "").Append(N(w)).Append(" 0 0 ").Append(N(h)).Append(' ')
            .Append(N(x)).Append(' ').Append(N(PageH - yTop - h)).Append(" cm /Im").Append(image.Index).Append(" Do Q\n");

    private static string N(float v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    // ---------------------------------------------------------------- shaping via DirectWrite

    private FontRes Font(void* face)
    {
        if (_fontsByFace.TryGetValue((nint)face, out var cached)) return cached;
        var key = DW.FaceKey(face);
        // One variable font file holds every weight and width, so the file key cannot tell a bold heading from
        // regular body text. Outline fonts are drawn from the face object itself, and DirectWrite hands out one
        // face object per instance, so each face of a variable font is its own PDF font.
        if (DW.FontTable(face, "fvar") is not null) key += "@" + ((nint)face).ToString("X");
        if (!_fontsByKey.TryGetValue(key, out var f))
        {
            DW.AddRef(face);
            f = new FontRes { Index = _fonts.Count, Face = face, Metrics = DW.FaceMetrics(face) };
            f.Outline = _o.ForceOutlineFonts || !FontSubset.CanEmbed(face);
            _fonts.Add(f);
            _fontsByKey[key] = f;
        }
        // Keep every face pointer we cache alive so the address cannot be reused by another font.
        if (f.Face != face) DW.AddRef(face);
        _fontsByFace[(nint)face] = f;
        return f;
    }

    private void Fill(GlyphsRec rec, float x, float y, DGlyphRun* run)
    {
        int n = (int)run->GlyphCount;
        rec.Font = Font(run->FontFace); rec.Size = run->EmSize; rec.X = x; rec.Y = y;
        rec.Rtl = (run->BidiLevel & 1) != 0; rec.Sim = DW.FaceSimulations(run->FontFace);
        rec.Gids = new ReadOnlySpan<ushort>(run->Indices, n).ToArray();
        rec.Adv = new ReadOnlySpan<float>(run->Advances, n).ToArray();
        if (run->Offsets != null) rec.Off = new ReadOnlySpan<GlyphOffset>(run->Offsets, n).ToArray();
    }

    void IGlyphSink.OnGlyphRun(float x, float y, DGlyphRun* run, DGlyphRunDesc* desc)
    {
        var rec = new RunRec();
        Fill(rec, x, y, run);
        rec.TextPos = (int)desc->TextPosition;
        rec.Text = new string(desc->Text, 0, (int)desc->TextLength);
        rec.Cluster = new ReadOnlySpan<ushort>(desc->ClusterMap, (int)desc->TextLength).ToArray();

        var layers = DW.TranslateColorGlyphRun(x, y, run, desc);
        if (layers != null)
        {
            rec.Layers = new List<LayerRec>();
            while (DW.ColorMoveNext(layers))
            {
                var c = DW.ColorCurrent(layers);
                var layer = new LayerRec { Color = new Rgb(c->R, c->G, c->B), UseForeground = c->PaletteIndex == 0xFFFF };
                Fill(layer, c->X, c->Y, &c->Run);
                rec.Layers.Add(layer);
            }
            DW.Release(layers);
        }
        _sinkTarget!.Runs.Add(rec);
    }

    private static bool IsRtlParagraph(string s)
    {
        foreach (var c in s)
        {
            if (c is >= '֐' and <= 'ࣿ' or >= 'יִ' and <= '﷿' or >= 'ﹰ' and <= '﻿') return true;
            if (char.IsLetter(c)) return false;
        }
        return false;
    }

    // Han characters are shared between Chinese, Japanese and Korean fonts; the locale picks the regional forms.
    private static string LocaleFor(string s)
    {
        foreach (var c in s)
        {
            if (c is >= '぀' and <= 'ヿ') return "ja-jp";
            if (c is >= '가' and <= '힯' or >= 'ᄀ' and <= 'ᇿ') return "ko-kr";
        }
        foreach (var c in s) if (c is >= '一' and <= '鿿') return "zh-cn";
        return "en-us";
    }

    private LaidOut Layout(IReadOnlyList<Span> spans, float size, float width, bool bold = false, bool italic = false, int align = 0,
        int weight = 0, string? family = null)
    {
        var text = string.Concat(spans.Select(s => s.Text));
        var lo = new LaidOut();
        var format = DW.CreateTextFormat(family ?? _o.Family, weight != 0 ? weight : bold ? 700 : 400, italic ? 2 : 0, size, LocaleFor(text));
        var layout = DW.CreateTextLayout(text, format, Math.Max(width, 1), 100000f);
        try
        {
            if (IsRtlParagraph(text)) DW.SetReadingDirection(layout, 1);
            if (align != 0) DW.SetTextAlignment(layout, align);
            int pos = 0;
            foreach (var s in spans)
            {
                var range = new TextRange(pos, s.Text.Length);
                if (s.Text.Length > 0)
                {
                    if (s.Bold) DW.SetFontWeight(layout, 700, range);
                    if (s.Italic) DW.SetFontStyle(layout, 2, range);
                    if (s.Code) DW.SetFontFamily(layout, "Consolas", range);
                    if (s.Link is not null) { DW.SetUnderline(layout, true, range); lo.Links.Add((pos, s.Text.Length, s.Link)); }
                }
                pos += s.Text.Length;
            }
            _sinkTarget = lo;
            DW.Draw(layout, this);
            lo.Lines = DW.GetLineMetrics(layout);
            var m = DW.GetMetrics(layout);
            lo.Height = m.Height; lo.Width = m.WidthTrailing;
        }
        finally { _sinkTarget = null; DW.Release(layout); DW.Release(format); }
        return lo;
    }

    // ---------------------------------------------------------------- content stream emission

    private static string HexText(string s)
    {
        var sb = new StringBuilder("<FEFF", 6 + s.Length * 4);
        foreach (var c in s) sb.Append(((int)c).ToString("X4"));
        return sb.Append('>').ToString();
    }

    private void EmitRun(PageBuf pg, RunRec r, float dx, float dy, Rgb color)
    {
        if (r.Layers is { } layers)
        {
            // Colour glyph layers carry no text of their own; the whole run is one ActualText span.
            pg.Ops.Append("/Span <</ActualText ").Append(HexText(r.Text)).Append(">> BDC\n");
            foreach (var l in layers) EmitGlyphs(pg, l, dx, dy, l.UseForeground ? color : l.Color, null, null);
            pg.Ops.Append("EMC\n");
        }
        else EmitGlyphs(pg, r, dx, dy, color, r.Text, r.Cluster);
    }

    private void EmitGlyphs(PageBuf pg, GlyphsRec r, float dx, float dy, Rgb c, string? text, ushort[]? cluster)
    {
        var sb = pg.Ops; var f = r.Font; int n = r.Gids.Length; float size = r.Size;
        var px = new float[n]; var py = new float[n];
        float pen = r.X + dx;
        for (int i = 0; i < n; i++)
        {
            float ao = r.Off is null ? 0 : r.Off[i].Advance, so = r.Off is null ? 0 : r.Off[i].Ascender;
            if (r.Rtl) { pen -= r.Adv[i]; px[i] = pen - ao; } else { px[i] = pen + ao; pen += r.Adv[i]; }
            py[i] = r.Y + dy - so;
        }

        bool bold = (r.Sim & 1) != 0, oblique = (r.Sim & 2) != 0;
        string rgb = N(c.R) + " " + N(c.G) + " " + N(c.B);
        sb.Append("BT\n");
        if (!f.Outline) sb.Append("/F").Append(f.Index).Append(' ').Append(N(size)).Append(" Tf\n");
        sb.Append(rgb).Append(" rg\n");
        int outlineFont = -1; // which 256-glyph Type 3 font of an outline face is currently selected
        if (bold) sb.Append("2 Tr ").Append(N(size * 0.035f)).Append(" w ").Append(rgb).Append(" RG\n");
        string skew = oblique ? "0.33" : "0";
        bool open = false; float penX = 0, curY = float.NaN;

        void Flush() { if (open) { sb.Append("] TJ\n"); open = false; } }
        void Show(int i)
        {
            ushort g = r.Gids[i];
            f.Used.Add(g);
            int slot = f.Outline ? f.Slot(g) : 0;
            if (f.Outline && slot >> 8 != outlineFont)
            {
                Flush();
                outlineFont = slot >> 8;
                sb.Append("/F").Append(f.Index).Append('_').Append(outlineFont).Append(' ').Append(N(size)).Append(" Tf\n");
            }
            if (!open || MathF.Abs(py[i] - curY) > 0.004f)
            {
                Flush();
                float tx = MathF.Round(px[i], 2);
                sb.Append("1 0 ").Append(skew).Append(" 1 ").Append(N(tx)).Append(' ').Append(N(PageH - py[i])).Append(" Tm\n[");
                open = true; curY = py[i]; penX = tx;
            }
            else
            {
                float adj = MathF.Round((penX - px[i]) / size * 1000f);
                if (adj != 0) { sb.Append(N(adj)).Append(' '); penX -= adj * size / 1000f; }
            }
            sb.Append('<').Append(f.Outline ? (slot & 0xFF).ToString("X2") : g.ToString("X4")).Append('>');
            penX += f.Width1000(g) * size / 1000f;
        }

        if (text is null || cluster is null)
        {
            for (int i = 0; i < n; i++) Show(i);
        }
        else
        {
            int ti = 0;
            while (ti < text.Length)
            {
                int g0 = cluster[ti], tj = ti + 1;
                while (tj < text.Length && cluster[tj] == g0) tj++;
                int g1 = tj < text.Length ? cluster[tj] : n;
                if (g1 <= g0) g1 = Math.Min(g0 + 1, n);
                var ct = text.Substring(ti, tj - ti);
                ushort first = r.Gids[g0];
                bool oneToOne = g1 - g0 == 1 && first != 0 && (ct.Length == 1 || (ct.Length == 2 && char.IsSurrogatePair(ct, 0)));
                if (oneToOne && (!f.ToUnicode.TryGetValue(first, out var existing) || existing == ct))
                {
                    f.ToUnicode[first] = ct;
                    Show(g0);
                }
                else
                {
                    // Ligatures, conjuncts, reordered marks, contextual forms seen with another meaning:
                    // a per-occurrence ActualText span is exact where a per-glyph ToUnicode entry cannot be.
                    Flush();
                    sb.Append("/Span <</ActualText ").Append(HexText(ct)).Append(">> BDC\n");
                    for (int g = g0; g < g1; g++) Show(g);
                    Flush();
                    sb.Append("EMC\n");
                    if (first != 0) f.ToUnicode.TryAdd(first, ct);
                }
                ti = tj;
            }
        }
        Flush();
        if (bold) sb.Append("0 Tr\n");
        sb.Append("ET\n");
    }

    private void PlaceRun(PageBuf pg, LaidOut lo, RunRec r, float dx, float dy, Rgb color, float lineTop, float lineHeight)
    {
        var link = lo.LinkAt(r.TextPos);
        EmitRun(pg, r, dx, dy, link is null ? color : Rgb.Link);
        if (link is null) return;
        float w = r.Adv.Sum(), x0 = (r.Rtl ? r.X - w : r.X) + dx, x1 = x0 + w, uy = PageH - (r.Y + dy + r.Size * 0.14f);
        pg.Ops.Append("q ").Append(N(Rgb.Link.R)).Append(' ').Append(N(Rgb.Link.G)).Append(' ').Append(N(Rgb.Link.B)).Append(" RG 0.6 w ")
            .Append(N(x0)).Append(' ').Append(N(uy)).Append(" m ").Append(N(x1)).Append(' ').Append(N(uy)).Append(" l S Q\n");
        pg.Links.Add((x0, PageH - (lineTop + lineHeight), x1, PageH - lineTop, link));
    }

    private static int LineOf(LaidOut lo, float baselineY)
    {
        int best = 0; float bestDistance = float.MaxValue, top = 0;
        for (int i = 0; i < lo.Lines.Length; i++)
        {
            float d = MathF.Abs(top + lo.Lines[i].Baseline - baselineY);
            if (d < bestDistance) { bestDistance = d; best = i; }
            top += lo.Lines[i].Height;
        }
        return best;
    }

    private void NewPage() { _pg = new PageBuf(); _pages.Add(_pg); _y = Top; }

    /// <summary>Flows a laid-out block down the page, breaking between lines. Returns where its first line landed.</summary>
    private (PageBuf Page, float Y) Flow(LaidOut lo, float x, Rgb color, float keepWithNext = 0)
    {
        int n = lo.Lines.Length;
        var page = new PageBuf[n]; var dy = new float[n]; var tops = new float[n];
        float top = 0;
        for (int i = 0; i < n; i++)
        {
            float need = lo.Lines[i].Height + (i == 0 ? keepWithNext : 0);
            if (_y + need > Bottom + 0.01f && _y > Top) NewPage();
            page[i] = _pg; dy[i] = _y - top; tops[i] = top;
            _y += lo.Lines[i].Height; top += lo.Lines[i].Height;
        }
        foreach (var r in lo.Runs)
        {
            int i = LineOf(lo, r.Y);
            PlaceRun(page[i], lo, r, x, dy[i], color, dy[i] + tops[i], lo.Lines[i].Height);
        }
        return n == 0 ? (_pg, _y) : (page[0], dy[0]);
    }

    /// <summary>Places a block at a fixed position without pagination (table cells, list markers, footers).</summary>
    private void PlaceAll(LaidOut lo, PageBuf pg, float x, float yTop, Rgb color)
        => PlaceLines(lo, pg, x, yTop, color, 0, lo.Lines.Length);

    /// <summary>Places lines [first, end) of a block so that line <paramref name="first"/> starts at <paramref name="yTop"/>.</summary>
    private void PlaceLines(LaidOut lo, PageBuf pg, float x, float yTop, Rgb color, int first, int end)
    {
        var tops = new float[lo.Lines.Length];
        for (int i = 1; i < tops.Length; i++) tops[i] = tops[i - 1] + lo.Lines[i - 1].Height;
        float shift = first > 0 && first < tops.Length ? yTop - tops[first] : yTop;
        foreach (var r in lo.Runs)
        {
            int i = LineOf(lo, r.Y);
            if (i < first || i >= end) continue;
            PlaceRun(pg, lo, r, x, shift, color, shift + tops[i], lo.Lines[i].Height);
        }
    }

    // ---------------------------------------------------------------- Markdown

    [GeneratedRegex(@"^(\s*)([-*+]|\d{1,9}[.)])\s+(.+)$")] private static partial Regex ListItem();
    [GeneratedRegex(@"^\s*\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)*\|?\s*$")] private static partial Regex TableSeparator();

    internal static List<Span> ParseInline(string s)
    {
        var spans = new List<Span>(); var sb = new StringBuilder(); bool bold = false, italic = false;
        void Flush(bool code = false, string? link = null)
        {
            if (sb.Length == 0) return;
            spans.Add(new Span(sb.ToString(), bold, italic, code, link));
            sb.Clear();
        }
        for (int i = 0; i < s.Length;)
        {
            char c = s[i];
            if (c == '\\' && i + 1 < s.Length && "\\`*_[]()#+-.!|".Contains(s[i + 1])) { sb.Append(s[i + 1]); i += 2; continue; }
            if (c == '`')
            {
                int j = s.IndexOf('`', i + 1);
                if (j > i + 1) { Flush(); sb.Append(s, i + 1, j - i - 1); Flush(code: true); i = j + 1; continue; }
            }
            if (c == '[')
            {
                int j = s.IndexOf("](", i + 1, StringComparison.Ordinal);
                int k = j > i ? s.IndexOf(')', j + 2) : -1;
                if (k > j + 2 && s.AsSpan(i + 1, j - i - 1).IndexOfAny('[', ']') < 0)
                {
                    Flush(); sb.Append(s, i + 1, j - i - 1); Flush(link: s.Substring(j + 2, k - j - 2)); i = k + 1; continue;
                }
            }
            if (c == '*' && i + 1 < s.Length && s[i + 1] == '*')
            {
                if (bold) { Flush(); bold = false; i += 2; continue; }
                if (i + 2 < s.Length && !char.IsWhiteSpace(s[i + 2]) && s.IndexOf("**", i + 2, StringComparison.Ordinal) > 0) { Flush(); bold = true; i += 2; continue; }
            }
            else if (c == '*')
            {
                if (italic && i > 0 && !char.IsWhiteSpace(s[i - 1])) { Flush(); italic = false; i++; continue; }
                if (!italic && i + 1 < s.Length && !char.IsWhiteSpace(s[i + 1]))
                {
                    int close = s.IndexOf('*', i + 1);
                    if (close > i + 1 && !char.IsWhiteSpace(s[close - 1])) { Flush(); italic = true; i++; continue; }
                }
            }
            sb.Append(c); i++;
        }
        Flush();
        if (spans.Count == 0) spans.Add(new Span(" "));
        return spans;
    }

    [GeneratedRegex(@"!\[Image\]\(muesli-note-image:([0-9A-Fa-f-]{36})(?:\?width=([0-9]{1,4}))?\)")] private static partial Regex NoteImage();
    [GeneratedRegex(@"\n*---\n+<img src=""data:image/png;base64,[A-Za-z0-9+/=]+"" alt=""[Mm]uesli logo""[^>]*>\n+\[Exported with [Mm]uesli\]\([^)\n]*\)\s*$")]
    private static partial Regex BrandFooter();

    /// <summary>
    /// The macOS app appends a logo + link footer to exported Markdown. In a PDF that branding lives in the page
    /// margin instead, so the Markdown copy of it is removed from the body (MeetingExportBranding.removingFooter).
    /// </summary>
    internal static string StripBrandFooter(string markdown) => BrandFooter().Replace(markdown, "");

    /// <summary>A line holding inline note images: text around them flows as paragraphs, each image is its own block.</summary>
    private void ImageLine(string line, float body)
    {
        void Text(string s) { if (!string.IsNullOrWhiteSpace(s)) Flow(Layout(ParseInline(s.Trim()), body, ContentW), Left, Rgb.Black); }
        int at = 0;
        foreach (Match m in NoteImage().Matches(line))
        {
            Text(line[at..m.Index]);
            at = m.Index + m.Length;
            ImageRes? image = null;
            try { if (_o.ImageResolver?.Invoke(m.Groups[1].Value) is { } bytes) image = AddImage(bytes); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { image = null; }
            if (image is null) { Text("[Image unavailable on this device]"); continue; }
            float requested = m.Groups[2].Success ? float.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) : ContentW;
            float w = Math.Min(Math.Min(Math.Max(requested, 1), ContentW), image.Width), h = w * image.Height / image.Width;
            if (h > Bottom - Top) { w *= (Bottom - Top) / h; h = Bottom - Top; }
            if (_y + h > Bottom + 0.01f && _y > Top) NewPage();
            DrawImage(_pg, image, Left, _y, w, h);
            _y += h;
        }
        Text(line[at..]);
    }

    private void Rule()
    {
        if (!_o.DrawRules) { _y += 17; return; } // macOS: an empty 4 pt line with 6 pt above and below, nothing drawn
        _y += 8;
        if (_y + 1 > Bottom) NewPage();
        float py = PageH - _y - 0.5f;
        _pg.Ops.Append("q 0.62 0.62 0.62 RG 1 w ").Append(N(Left)).Append(' ').Append(N(py)).Append(" m ")
            .Append(N(Left + ContentW)).Append(' ').Append(N(py)).Append(" l S Q\n");
        _y += 1 + 8;
    }

    private void Table(List<string[]> rows)
    {
        int columns = rows.Max(r => r.Length);
        float colW = ContentW / columns, pad = 4;
        for (int ri = 0; ri < rows.Count; ri++)
        {
            var cells = new LaidOut[columns];
            float rowH = 0;
            for (int ci = 0; ci < columns; ci++)
            {
                cells[ci] = Layout(ParseInline(ci < rows[ri].Length ? rows[ri][ci] : " "), 10, colW - 2 * pad, bold: ri == 0);
                rowH = Math.Max(rowH, cells[ci].Height);
            }
            rowH += 2 * pad;
            bool header = ri == 0;

            void Box(int ci, float height)
            {
                float x = Left + ci * colW;
                _pg.Ops.Append("q ");
                if (header) _pg.Ops.Append("0.95 g ").Append(N(x)).Append(' ').Append(N(PageH - _y - height)).Append(' ').Append(N(colW)).Append(' ').Append(N(height)).Append(" re f ");
                _pg.Ops.Append("0.75 G 0.5 w ").Append(N(x)).Append(' ').Append(N(PageH - _y - height)).Append(' ').Append(N(colW)).Append(' ').Append(N(height)).Append(" re S Q\n");
            }

            if (rowH <= Bottom - Top)
            {
                if (_y + rowH > Bottom && _y > Top) NewPage();
                for (int ci = 0; ci < columns; ci++)
                {
                    Box(ci, rowH);
                    PlaceAll(cells[ci], _pg, Left + ci * colW + pad, _y + pad, Rgb.Black);
                }
                _y += rowH;
                continue;
            }

            // A row taller than a whole page cannot be moved to a fresh page and drawn in one piece: its text
            // would run off the bottom and be lost. Draw it in page-sized slices, breaking between lines.
            var next = new int[columns];
            bool Remaining()
            {
                for (int ci = 0; ci < columns; ci++) if (next[ci] < cells[ci].Lines.Length) return true;
                return false;
            }
            while (Remaining())
            {
                if (_y > Top && Bottom - _y - 2 * pad < 16) NewPage();
                float available = Bottom - _y - 2 * pad, sliceH = 0;
                var end = new int[columns];
                for (int ci = 0; ci < columns; ci++)
                {
                    var cellLines = cells[ci].Lines;
                    int n = next[ci]; float h = 0;
                    while (n < cellLines.Length && h + cellLines[n].Height <= available + 0.01f) h += cellLines[n++].Height;
                    if (n == next[ci] && n < cellLines.Length) h += cellLines[n++].Height; // one line taller than the page
                    end[ci] = n; sliceH = Math.Max(sliceH, h);
                }
                sliceH += 2 * pad;
                for (int ci = 0; ci < columns; ci++)
                {
                    Box(ci, sliceH);
                    PlaceLines(cells[ci], _pg, Left + ci * colW + pad, _y + pad, Rgb.Black, next[ci], end[ci]);
                    next[ci] = end[ci];
                }
                _y += sliceH;
                if (Remaining()) NewPage();
            }
        }
        _y += 4;
    }

    private void Blocks(string markdown)
    {
        var lines = StripBrandFooter(markdown.Replace("\r\n", "\n")).Split('\n', '\r');
        float body = _o.BodySize;
        float blank = _o.BlankLine > 0 ? _o.BlankLine : Layout([new Span(" ")], body, ContentW).Height;
        NewPage();
        _y += _o.TopPadding;
        void Before(float space) { if (_y > Top) _y += space; }
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd();
            if (string.IsNullOrWhiteSpace(line)) { _y += blank; continue; }

            if (line.StartsWith("# ")) { _title ??= line[2..]; Before(_o.H1Before); Flow(Layout(ParseInline(line[2..]), _o.H1Size, ContentW, bold: true), Left, Rgb.Black, 30); continue; }
            if (line.StartsWith("## ")) { Before(_o.H2Before); Flow(Layout(ParseInline(line[3..]), _o.H2Size, ContentW, bold: true), Left, Rgb.Black, 30); continue; }
            if (line.StartsWith("### ")) { Before(_o.H3Before); Flow(Layout(ParseInline(line[4..]), _o.H3Size, ContentW, weight: _o.H3Weight), Left, Rgb.Black, 30); continue; }
            if (line.StartsWith("---") || line is "***" or "___") { Rule(); continue; }
            if (line.Contains("muesli-note-image:") && NoteImage().IsMatch(line)) { ImageLine(line, body); continue; }

            if (line.TrimStart().StartsWith('|') && i + 1 < lines.Length && TableSeparator().IsMatch(lines[i + 1]) && lines[i + 1].Contains('-'))
            {
                var rows = new List<string[]>();
                int first = i;
                for (int j = first; j < lines.Length && lines[j].TrimStart().StartsWith('|'); j++)
                {
                    if (j != first + 1) rows.Add(lines[j].Trim().Trim('|').Split('|').Select(c => c.Trim()).ToArray());
                    i = j;
                }
                Table(rows);
                continue;
            }

            var item = ListItem().Match(line);
            if (item.Success && !(line.StartsWith("**") || (line.StartsWith('*') && line.EndsWith('*') && line.Length > 2)))
            {
                int level = Math.Min(item.Groups[1].Length / 2, 6);
                var marker = item.Groups[2].Value; var itemText = item.Groups[3].Value;
                if (marker.Length == 1) marker = "•";
                if (itemText.StartsWith("[ ] ")) { marker = "☐"; itemText = itemText[4..]; }
                else if (itemText.StartsWith("[x] ", StringComparison.OrdinalIgnoreCase)) { marker = "☑"; itemText = itemText[4..]; }
                float indent = level * 16, gutter = (marker.Length > 2 ? 22 : 16) * body / 11;
                var (page, y) = Flow(Layout(ParseInline(itemText), body, ContentW - indent - gutter), Left + indent + gutter, Rgb.Black);
                PlaceAll(Layout([new Span(marker)], body, gutter + 40), page, Left + indent, y, Rgb.Black);
                continue;
            }

            // The two whole-line conventions of the current exporter, kept for parity.
            if (line.StartsWith("**") && line.EndsWith("**") && line.Length > 4 && !line[2..^2].Contains("**"))
            {
                var parts = line.Trim('*').Split(": ", 2);
                Flow(Layout(parts.Length == 2 ? [new Span(parts[0] + ": ", Bold: true), new Span(parts[1])] : [new Span(parts[0], Bold: true)], body, ContentW), Left, Rgb.Black);
                continue;
            }
            if (line.StartsWith('*') && line.EndsWith('*') && line.Length > 2 && !line[1..^1].Contains('*'))
            {
                Flow(Layout([new Span(line.Trim('*'))], body, ContentW, italic: true), Left, Rgb.Grey);
                continue;
            }

            Flow(Layout(ParseInline(line.TrimStart()), body, ContentW), Left, Rgb.Black);
        }
    }

    // ---------------------------------------------------------------- PDF container

    private static readonly Encoding Latin1 = Encoding.Latin1;

    public byte[] Run(string markdown, DateTimeOffset createdAt)
    {
        try
        {
            Blocks(markdown);
            if (_o.PageNumbers)
                for (int i = 0; i < _pages.Count; i++)
                    PlaceAll(Layout([new Span($"{i + 1} / {_pages.Count}")], 9, ContentW, align: 2), _pages[i], Left, PageH - 72, Rgb.Grey);
            if (_o.BrandLogo is { } logoBytes)
            {
                // Same geometry as the macOS app: an 18 pt logo at 70% opacity at (72, 27) from the bottom-left corner,
                // a 9 pt grey label with its baseline at (96, 33), and a link rectangle (96, 30, label width, 14).
                // The artwork is 1024 px for an 18 pt mark; 128 px (512 dpi) keeps it sharp and the file small.
                // Decoded once per process: the PNG inflate + downscale is the only costly part of the footer.
                ImageRes decoded;
                lock (LogoLock)
                {
                    if (!ReferenceEquals(_logoSource, logoBytes))
                    {
                        var fresh = ImageCodec.Decode(logoBytes);
                        ImageCodec.BoxDownscale(fresh, 128);
                        _logoDecoded = fresh; _logoSource = logoBytes;
                    }
                    decoded = _logoDecoded!;
                }
                var logo = new ImageRes
                {
                    Index = _images.Count, Width = decoded.Width, Height = decoded.Height, Data = decoded.Data,
                    Alpha = decoded.Alpha, ColorSpace = decoded.ColorSpace, IsJpeg = decoded.IsJpeg,
                };
                _images.Add(logo);
                var label = Layout([new Span(_o.BrandText)], 9, 400, family: "Arial"); // metric-compatible with Helvetica
                var grey = new Rgb(0.45f, 0.45f, 0.45f);
                foreach (var pg in _pages)
                {
                    DrawImage(pg, logo, 72, PageH - 27 - 18, 18, 18, translucent: true);
                    PlaceAll(label, pg, 96, PageH - 33 - label.Lines[0].Baseline, grey);
                    pg.Links.Add((96, 30, 96 + label.Width, 44, _o.BrandUrl));
                }
            }
            return Assemble(createdAt);
        }
        finally
        {
            foreach (var face in _fontsByFace.Keys) DW.Release((void*)face);
            foreach (var f in _fonts) if (!_fontsByFace.ContainsKey((nint)f.Face)) DW.Release(f.Face);
        }
    }

    private static byte[] Deflate(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(data);
        return ms.ToArray();
    }

    private static byte[] StreamObject(string extraDict, byte[] data, string? alreadyEncodedWith = null)
    {
        var z = alreadyEncodedWith is null ? Deflate(data) : data;
        var head = Latin1.GetBytes($"<< /Length {z.Length} /Filter {alreadyEncodedWith ?? "/FlateDecode"} {extraDict}>>\nstream\n");
        var tail = Latin1.GetBytes("\nendstream");
        var o = new byte[head.Length + z.Length + tail.Length];
        head.CopyTo(o, 0); z.CopyTo(o, head.Length); tail.CopyTo(o, head.Length + z.Length);
        return o;
    }

    private static string PdfDate(DateTimeOffset d)
    {
        var s = "D:" + d.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        if (d.Offset == TimeSpan.Zero) return s + "Z";
        return s + (d.Offset < TimeSpan.Zero ? "-" : "+") + Math.Abs(d.Offset.Hours).ToString("00") + "'" + Math.Abs(d.Offset.Minutes).ToString("00") + "'";
    }

    private static string SubsetTag(int index)
    {
        var t = new char[6];
        for (int i = 5; i >= 0; i--) { t[i] = (char)('A' + index % 26); index /= 26; }
        return new string(t);
    }

    /// <summary>
    /// Writes a face that cannot be embedded as TrueType (CFF, variable, embedding-restricted) as Type 3
    /// fonts whose glyph procedures are the outlines DirectWrite reports. Text stays real text: it is
    /// selectable and searchable through the same ToUnicode and ActualText data as embedded fonts.
    /// </summary>
    private static void WriteOutlineFonts(FontRes f, StringBuilder fontRefs, Func<int> newObject, List<byte[]?> objects)
    {
        for (int first = 0, part = 0; first < f.SlotGlyphs.Count; first += 256, part++)
        {
            int count = Math.Min(256, f.SlotGlyphs.Count - first);
            var procs = new StringBuilder(); var names = new StringBuilder(); var widths = new StringBuilder();
            int minX = 0, minY = 0, maxX = 0, maxY = 0;
            for (int code = 0; code < count; code++)
            {
                ushort gid = f.SlotGlyphs[first + code];
                int width = f.Width1000(gid);
                var outline = DW.Outline(f.Face, gid);
                // d1 declares an uncoloured glyph, so the text colour set on the page applies to it.
                var proc = outline.IsEmpty
                    ? $"{width} 0 0 0 0 0 d1\n"
                    : $"{width} 0 {outline.MinX} {outline.MinY} {outline.MaxX} {outline.MaxY} d1\n{outline.Path}{(outline.Winding ? "f" : "f*")}\n";
                if (!outline.IsEmpty)
                {
                    minX = Math.Min(minX, outline.MinX); minY = Math.Min(minY, outline.MinY);
                    maxX = Math.Max(maxX, outline.MaxX); maxY = Math.Max(maxY, outline.MaxY);
                }
                int procObject = newObject();
                objects[procObject - 1] = StreamObject("", Latin1.GetBytes(proc));
                procs.Append($"/g{code} {procObject} 0 R ");
                names.Append($"/g{code} ");
                widths.Append(width).Append(' ');
            }

            var cmap = new StringBuilder();
            cmap.Append("/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n")
                .Append("/CMapName /Adobe-Identity-UCS def\n/CMapType 2 def\n1 begincodespacerange\n<00> <FF>\nendcodespacerange\n");
            foreach (var block in OutlineCMapBlocks(f, first, count)) cmap.Append(block);
            cmap.Append("endcmap\nCMapName currentdict /CMap defineresource pop\nend\nend\n");
            int toUnicode = newObject();
            objects[toUnicode - 1] = StreamObject("", Latin1.GetBytes(cmap.ToString()));

            int font = newObject();
            objects[font - 1] = Latin1.GetBytes(
                $"<< /Type /Font /Subtype /Type3 /FontBBox [{minX} {minY} {maxX} {maxY}] /FontMatrix [0.001 0 0 0.001 0 0] " +
                $"/CharProcs << {procs}>> /Encoding << /Type /Encoding /Differences [0 {names}] >> /FirstChar 0 /LastChar {count - 1} " +
                $"/Widths [{widths}] /Resources << >> /ToUnicode {toUnicode} 0 R >>");
            fontRefs.Append($"/F{f.Index}_{part} {font} 0 R ");
        }
    }

    private static IEnumerable<string> OutlineCMapBlocks(FontRes f, int first, int count)
    {
        var entries = new List<string>();
        for (int code = 0; code < count; code++)
        {
            if (!f.ToUnicode.TryGetValue(f.SlotGlyphs[first + code], out var text)) continue;
            var entry = new StringBuilder().Append('<').Append(code.ToString("X2")).Append("> <");
            foreach (var ch in text) entry.Append(((int)ch).ToString("X4"));
            entries.Add(entry.Append(">\n").ToString());
        }
        for (int i = 0; i < entries.Count; i += 100)
        {
            int n = Math.Min(100, entries.Count - i);
            yield return n + " beginbfchar\n" + string.Concat(entries.Skip(i).Take(n)) + "endbfchar\n";
        }
    }

    /// <summary>
    /// A link target as the ASCII URI a PDF annotation requires. PDF objects here are written as Latin-1, so a
    /// raw "https://example.com/会议" would reach the file as "??". A non-ASCII host becomes its IDN (punycode)
    /// form and every other non-ASCII character, space or control character is percent-encoded as UTF-8.
    /// </summary>
    internal static string AsciiUri(string url)
    {
        if (url.Any(c => c > 0x7E) && Uri.TryCreate(url, UriKind.Absolute, out var parsed) && parsed.Scheme is "http" or "https")
        {
            try
            {
                var host = parsed.Host;
                if (host.Any(c => c > 0x7E)) url = url.Replace(host, parsed.IdnHost, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is UriFormatException or InvalidOperationException or ArgumentException) { }
        }

        var ascii = new StringBuilder(url.Length);
        Span<byte> utf8 = stackalloc byte[4];
        foreach (var rune in url.EnumerateRunes())
        {
            if (rune.Value is > 0x20 and < 0x7F) { ascii.Append((char)rune.Value); continue; }
            int n = rune.EncodeToUtf8(utf8);
            for (int i = 0; i < n; i++) ascii.Append('%').Append(utf8[i].ToString("X2"));
        }
        return ascii.ToString();
    }

    private byte[] Assemble(DateTimeOffset createdAt)
    {
        var objects = new List<byte[]?>();
        int New() { objects.Add(null); return objects.Count; }
        void Set(int id, string body) => objects[id - 1] = Latin1.GetBytes(body);

        int catalog = New(), pagesRoot = New(), info = New(), resources = New();

        var fontRefs = new StringBuilder();
        foreach (var f in _fonts)
        {
            if (f.Outline) { WriteOutlineFonts(f, fontRefs, New, objects); continue; }
            var sfnt = FontSubset.Build(f.Face, f.Used, out var bbox)
                ?? throw new NotSupportedException("A font that was expected to embed as TrueType could not be subsetted.");
            var name = SubsetTag(f.Index) + "+" + (FontSubset.PostScriptName(f.Face) ?? "Font" + f.Index);
            float k = 1000f / f.Metrics.UnitsPerEm;
            int file = New(); objects[file - 1] = StreamObject($"/Length1 {sfnt.Length} ", sfnt);
            int descriptor = New();
            Set(descriptor, $"<< /Type /FontDescriptor /FontName /{name} /Flags 4 /FontBBox [{N(bbox[0] * k)} {N(bbox[1] * k)} {N(bbox[2] * k)} {N(bbox[3] * k)}] " +
                $"/ItalicAngle 0 /Ascent {N(f.Metrics.Ascent * k)} /Descent {N(-f.Metrics.Descent * k)} /CapHeight {N(f.Metrics.CapHeight * k)} /StemV 80 /FontFile2 {file} 0 R >>");

            var w = new StringBuilder();
            ushort previous = 0; bool open = false;
            foreach (var g in f.Used)
            {
                if (!open || g != previous + 1) { if (open) w.Append("] "); w.Append(g).Append(" ["); open = true; }
                else w.Append(' ');
                w.Append(f.Width1000(g));
                previous = g;
            }
            if (open) w.Append(']');
            int cidFont = New();
            Set(cidFont, $"<< /Type /Font /Subtype /CIDFontType2 /BaseFont /{name} /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> " +
                $"/FontDescriptor {descriptor} 0 R /DW 1000 /W [{w}] /CIDToGIDMap /Identity >>");

            var cmap = new StringBuilder();
            cmap.Append("/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n")
                .Append("/CMapName /Adobe-Identity-UCS def\n/CMapType 2 def\n1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n");
            var entries = f.ToUnicode.OrderBy(e => e.Key).ToList();
            for (int i = 0; i < entries.Count; i += 100)
            {
                int count = Math.Min(100, entries.Count - i);
                cmap.Append(count).Append(" beginbfchar\n");
                for (int j = i; j < i + count; j++)
                {
                    cmap.Append('<').Append(entries[j].Key.ToString("X4")).Append("> <");
                    foreach (var ch in entries[j].Value) cmap.Append(((int)ch).ToString("X4"));
                    cmap.Append(">\n");
                }
                cmap.Append("endbfchar\n");
            }
            cmap.Append("endcmap\nCMapName currentdict /CMap defineresource pop\nend\nend\n");
            int toUnicode = New(); objects[toUnicode - 1] = StreamObject("", Latin1.GetBytes(cmap.ToString()));

            int type0 = New();
            Set(type0, $"<< /Type /Font /Subtype /Type0 /BaseFont /{name} /Encoding /Identity-H /DescendantFonts [{cidFont} 0 R] /ToUnicode {toUnicode} 0 R >>");
            fontRefs.Append($"/F{f.Index} {type0} 0 R ");
        }
        var imageRefs = new StringBuilder();
        foreach (var image in _images)
        {
            var dict = $"/Type /XObject /Subtype /Image /Width {image.Width} /Height {image.Height} /ColorSpace {image.ColorSpace} /BitsPerComponent 8 ";
            if (image.Alpha is not null)
            {
                int mask = New();
                objects[mask - 1] = StreamObject($"/Type /XObject /Subtype /Image /Width {image.Width} /Height {image.Height} /ColorSpace /DeviceGray /BitsPerComponent 8 ", image.Alpha);
                dict += $"/SMask {mask} 0 R ";
            }
            int imageObject = New();
            objects[imageObject - 1] = image.IsJpeg ? StreamObject(dict, image.Data, "/DCTDecode") : StreamObject(dict, image.Data);
            imageRefs.Append($"/Im{image.Index} {imageObject} 0 R ");
        }
        Set(resources, $"<< /Font << {fontRefs}>> /XObject << {imageRefs}>> /ExtGState << /GS0 << /ca 0.7 /CA 0.7 >> >> >>");

        var kids = new StringBuilder();
        foreach (var pg in _pages)
        {
            int content = New(); objects[content - 1] = StreamObject("", Latin1.GetBytes(pg.Ops.ToString()));
            var annots = new StringBuilder();
            foreach (var l in pg.Links)
            {
                var uri = AsciiUri(l.Url).Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");
                annots.Append($"<< /Type /Annot /Subtype /Link /Rect [{N(l.X0)} {N(l.Y0)} {N(l.X1)} {N(l.Y1)}] /Border [0 0 0] /A << /S /URI /URI ({uri}) >> >> ");
            }
            int page = New();
            Set(page, $"<< /Type /Page /Parent {pagesRoot} 0 R /MediaBox [0 0 {N(PageW)} {N(PageH)}] /Resources {resources} 0 R /Contents {content} 0 R" +
                (annots.Length > 0 ? $" /Annots [{annots}]" : "") + " >>");
            kids.Append(page).Append(" 0 R ");
        }
        Set(pagesRoot, $"<< /Type /Pages /Count {_pages.Count} /Kids [{kids}] >>");
        Set(catalog, $"<< /Type /Catalog /Pages {pagesRoot} 0 R >>");
        var date = PdfDate(createdAt);
        Set(info, $"<< /Title {HexText(_title ?? "Meeting notes")} /Producer (Muesli) /CreationDate ({date}) /ModDate ({date}) >>");

        using var o = new MemoryStream();
        void W(string s) { var b = Latin1.GetBytes(s); o.Write(b); }
        W("%PDF-1.7\n%âãÏÓ\n");
        var offsets = new long[objects.Count];
        for (int i = 0; i < objects.Count; i++)
        {
            offsets[i] = o.Position;
            W($"{i + 1} 0 obj\n"); o.Write(objects[i]!); W("\nendobj\n");
        }
        long xref = o.Position;
        var id = Convert.ToHexString(SHA256.HashData(o.GetBuffer().AsSpan(0, (int)o.Length)).AsSpan(0, 16));
        W($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var off in offsets) W(off.ToString("0000000000") + " 00000 n \n");
        W($"trailer\n<< /Size {objects.Count + 1} /Root {catalog} 0 R /Info {info} 0 R /ID [<{id}> <{id}>] >>\nstartxref\n{xref}\n%%EOF\n");
        return o.ToArray();
    }
}
