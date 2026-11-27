// Hand-written DirectWrite interop: raw vtable calls, no built-in COM marshalling, no packages.
// Only the system dwrite.dll (in-box since Windows 7; fallback/colour APIs since 8.1) is used.
using System.Runtime.InteropServices;

namespace Muesli.Windows.Services.Pdf;

[StructLayout(LayoutKind.Sequential)]
internal struct TextRange
{
    public uint Start, Length;
    public TextRange(int start, int length) { Start = (uint)start; Length = (uint)length; }
}

internal struct GlyphOffset { public float Advance, Ascender; }

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct DGlyphRun
{
    public void* FontFace; public float EmSize; public uint GlyphCount; public ushort* Indices;
    public float* Advances; public GlyphOffset* Offsets; public int IsSideways; public uint BidiLevel;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct DGlyphRunDesc
{
    public char* Locale; public char* Text; public uint TextLength; public ushort* ClusterMap; public uint TextPosition;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct DColorGlyphRun
{
    public DGlyphRun Run; public DGlyphRunDesc* Desc; public float X, Y; public float R, G, B, A; public ushort PaletteIndex;
}

internal struct DLineMetrics { public uint Length, TrailingWs, NewlineLength; public float Height, Baseline; public int IsTrimmed; }
internal struct DTextMetrics { public float Left, Top, Width, WidthTrailing, Height, LayoutWidth, LayoutHeight; public uint MaxBidiDepth, LineCount; }
internal struct DFontMetrics
{
    public ushort UnitsPerEm, Ascent, Descent; public short LineGap; public ushort CapHeight, XHeight;
    public short UnderlinePos; public ushort UnderlineThick; public short StrikePos; public ushort StrikeThick;
}
internal struct DGlyphMetrics { public int Lsb; public uint AdvanceWidth; public int Rsb, Tsb; public uint AdvanceHeight; public int Bsb, VertOriginY; }

internal struct Point2F { public float X, Y; }

/// <summary>A glyph outline collected from DirectWrite: PDF path operators plus its bounding box, y up.</summary>
internal sealed class GlyphOutline
{
    public readonly System.Text.StringBuilder Path = new();
    public bool Winding = true;
    public int MinX = int.MaxValue, MinY = int.MaxValue, MaxX = int.MinValue, MaxY = int.MinValue;
    public bool IsEmpty => Path.Length == 0 || MinX > MaxX;

    private void Point(Point2F p)
    {
        // DirectWrite's y axis points down; PDF glyph space points up.
        int x = (int)MathF.Round(p.X), y = (int)MathF.Round(-p.Y);
        if (x < MinX) MinX = x; if (x > MaxX) MaxX = x; if (y < MinY) MinY = y; if (y > MaxY) MaxY = y;
        Path.Append(x).Append(' ').Append(y).Append(' ');
    }
    public void Move(Point2F p) { Point(p); Path.Append("m\n"); }
    public void Line(Point2F p) { Point(p); Path.Append("l\n"); }
    public void Curve(Point2F a, Point2F b, Point2F c) { Point(a); Point(b); Point(c); Path.Append("c\n"); }
}

internal interface IGlyphSink
{
    unsafe void OnGlyphRun(float x, float y, DGlyphRun* run, DGlyphRunDesc* desc);
}

internal static unsafe class DW
{
    [DllImport("dwrite.dll", ExactSpelling = true)]
    private static extern int DWriteCreateFactory(int factoryType, Guid* iid, void** factory);

    private static readonly Guid IidFactory = new("b859ee5a-d838-4b5b-a2e8-1adc7d93db48");
    private static readonly Guid IidFactory2 = new("0439fc60-ca44-4994-8dee-3a9af7b732ec");
    private static readonly Guid IidUnknown = new("00000000-0000-0000-C000-000000000046");
    private static readonly Guid IidPixelSnapping = new("eaf3a2da-ecf4-4d24-b644-b34f6842024b");
    private static readonly Guid IidTextRenderer = new("ef8a8135-5cc6-45fe-8825-c5a0724eb819");

    public static readonly void* Factory;
    public static readonly void* Factory2; // null before Windows 8.1
    private static readonly void** RendererObject;

    static DW()
    {
        void* f; var iid = IidFactory;
        Check(DWriteCreateFactory(0 /* shared */, &iid, &f));
        Factory = f;
        void* f2; var iid2 = IidFactory2;
        Factory2 = ((delegate* unmanaged<void*, Guid*, void**, int>)VT(f)[0])(f, &iid2, &f2) >= 0 ? f2 : null;

        // A static, never-freed IDWriteTextRenderer whose methods forward to the IGlyphSink in the drawing context.
        var vt = (void**)NativeMemory.Alloc((nuint)(sizeof(void*) * 10));
        vt[0] = (delegate* unmanaged<void*, Guid*, void**, int>)&QueryInterface;
        vt[1] = (delegate* unmanaged<void*, uint>)&AddRefRelease;
        vt[2] = (delegate* unmanaged<void*, uint>)&AddRefRelease;
        vt[3] = (delegate* unmanaged<void*, void*, int*, int>)&IsPixelSnappingDisabled;
        vt[4] = (delegate* unmanaged<void*, void*, float*, int>)&GetCurrentTransform;
        vt[5] = (delegate* unmanaged<void*, void*, float*, int>)&GetPixelsPerDip;
        vt[6] = (delegate* unmanaged<void*, void*, float, float, int, DGlyphRun*, DGlyphRunDesc*, void*, int>)&DrawGlyphRun;
        vt[7] = (delegate* unmanaged<void*, void*, float, float, void*, void*, int>)&DrawLine;
        vt[8] = (delegate* unmanaged<void*, void*, float, float, void*, void*, int>)&DrawLine;
        vt[9] = (delegate* unmanaged<void*, void*, float, float, void*, int, int, void*, int>)&DrawInlineObject;
        RendererObject = (void**)NativeMemory.Alloc((nuint)sizeof(void*));
        RendererObject[0] = vt;
    }

    public static void** VT(void* p) => *(void***)p;
    public static void Check(int hr) { if (hr < 0) throw new InvalidOperationException($"DirectWrite call failed: 0x{hr:X8}"); }
    public static uint Release(void* p) => p == null ? 0 : ((delegate* unmanaged<void*, uint>)VT(p)[2])(p);
    public static uint AddRef(void* p) => ((delegate* unmanaged<void*, uint>)VT(p)[1])(p);

    // ---- IDWriteFactory ----
    public static void* CreateTextFormat(string family, int weight, int style, float size, string locale)
    {
        void* fmt;
        fixed (char* pf = family) fixed (char* pl = locale)
            Check(((delegate* unmanaged<void*, char*, void*, int, int, int, float, char*, void**, int>)VT(Factory)[15])(
                Factory, pf, null, weight, style, 5, size, pl, &fmt));
        return fmt;
    }

    public static void* CreateTextLayout(string text, void* format, float maxWidth, float maxHeight)
    {
        void* layout;
        fixed (char* pt = text)
            Check(((delegate* unmanaged<void*, char*, uint, void*, float, float, void**, int>)VT(Factory)[18])(
                Factory, pt, (uint)text.Length, format, maxWidth, maxHeight, &layout));
        return layout;
    }

    /// <summary>COLR layers for a glyph run, or null when the run has no colour glyphs.</summary>
    public static void* TranslateColorGlyphRun(float x, float y, DGlyphRun* run, DGlyphRunDesc* desc)
    {
        if (Factory2 == null) return null;
        void* e;
        var hr = ((delegate* unmanaged<void*, float, float, DGlyphRun*, DGlyphRunDesc*, int, void*, uint, void**, int>)VT(Factory2)[28])(
            Factory2, x, y, run, desc, 0, null, 0, &e);
        return hr == 0 ? e : null; // DWRITE_E_NOCOLOR otherwise
    }
    public static bool ColorMoveNext(void* e)
    {
        int has;
        Check(((delegate* unmanaged<void*, int*, int>)VT(e)[3])(e, &has));
        return has != 0;
    }
    public static DColorGlyphRun* ColorCurrent(void* e)
    {
        DColorGlyphRun* r;
        Check(((delegate* unmanaged<void*, DColorGlyphRun**, int>)VT(e)[4])(e, &r));
        return r;
    }

    // ---- IDWriteTextFormat / IDWriteTextLayout ----
    public static void SetTextAlignment(void* l, int a) => Check(((delegate* unmanaged<void*, int, int>)VT(l)[3])(l, a));
    public static void SetReadingDirection(void* l, int d) => Check(((delegate* unmanaged<void*, int, int>)VT(l)[6])(l, d));
    public static void SetFontFamily(void* l, string family, TextRange r)
    {
        fixed (char* p = family) Check(((delegate* unmanaged<void*, char*, TextRange, int>)VT(l)[31])(l, p, r));
    }
    public static void SetFontWeight(void* l, int w, TextRange r) => Check(((delegate* unmanaged<void*, int, TextRange, int>)VT(l)[32])(l, w, r));
    public static void SetFontStyle(void* l, int s, TextRange r) => Check(((delegate* unmanaged<void*, int, TextRange, int>)VT(l)[33])(l, s, r));
    public static void SetUnderline(void* l, bool on, TextRange r) => Check(((delegate* unmanaged<void*, int, TextRange, int>)VT(l)[36])(l, on ? 1 : 0, r));

    public static void Draw(void* layout, IGlyphSink sink)
    {
        var handle = GCHandle.Alloc(sink);
        try
        {
            Check(((delegate* unmanaged<void*, void*, void*, float, float, int>)VT(layout)[58])(
                layout, (void*)GCHandle.ToIntPtr(handle), RendererObject, 0, 0));
        }
        finally { handle.Free(); }
    }

    public static DLineMetrics[] GetLineMetrics(void* layout)
    {
        uint actual;
        var buffer = new DLineMetrics[8];
        int hr;
        fixed (DLineMetrics* p = buffer)
            hr = ((delegate* unmanaged<void*, DLineMetrics*, uint, uint*, int>)VT(layout)[59])(layout, p, (uint)buffer.Length, &actual);
        if (hr < 0)
        {
            buffer = new DLineMetrics[actual];
            fixed (DLineMetrics* p = buffer)
                Check(((delegate* unmanaged<void*, DLineMetrics*, uint, uint*, int>)VT(layout)[59])(layout, p, actual, &actual));
        }
        return buffer.AsSpan(0, (int)actual).ToArray();
    }

    public static DTextMetrics GetMetrics(void* layout)
    {
        DTextMetrics m;
        Check(((delegate* unmanaged<void*, DTextMetrics*, int>)VT(layout)[60])(layout, &m));
        return m;
    }

    // ---- IDWriteFontFace ----
    public static string FaceKey(void* face)
    {
        uint count = 1; void* file;
        Check(((delegate* unmanaged<void*, uint*, void**, int>)VT(face)[4])(face, &count, &file));
        void* key; uint size;
        Check(((delegate* unmanaged<void*, void**, uint*, int>)VT(file)[3])(file, &key, &size));
        var hex = Convert.ToHexString(new ReadOnlySpan<byte>(key, (int)size));
        Release(file);
        var index = ((delegate* unmanaged<void*, uint>)VT(face)[5])(face);
        return hex + ":" + index;
    }
    public static int FaceSimulations(void* face) => ((delegate* unmanaged<void*, int>)VT(face)[6])(face);
    public static DFontMetrics FaceMetrics(void* face)
    {
        DFontMetrics m;
        ((delegate* unmanaged<void*, DFontMetrics*, void>)VT(face)[8])(face, &m);
        return m;
    }
    public static uint DesignAdvance(void* face, ushort gid)
    {
        DGlyphMetrics m;
        Check(((delegate* unmanaged<void*, ushort*, uint, DGlyphMetrics*, int, int>)VT(face)[10])(face, &gid, 1, &m, 0));
        return m.AdvanceWidth;
    }
    public static byte[]? FontTable(void* face, string tag)
    {
        uint t = (uint)(tag[0] | tag[1] << 8 | tag[2] << 16 | tag[3] << 24);
        void* data; uint size; void* ctx; int exists;
        Check(((delegate* unmanaged<void*, uint, void**, uint*, void**, int*, int>)VT(face)[12])(face, t, &data, &size, &ctx, &exists));
        if (exists == 0) return null;
        var copy = new ReadOnlySpan<byte>(data, (int)size).ToArray();
        ((delegate* unmanaged<void*, void*, void>)VT(face)[13])(face, ctx);
        return copy;
    }

    /// <summary>Borrowed view of a font table (a mapping of the font file, no copy). Release with <see cref="ReleaseTable"/>.</summary>
    public static bool BorrowTable(void* face, string tag, out byte* data, out int size, out void* ctx)
    {
        uint t = (uint)(tag[0] | tag[1] << 8 | tag[2] << 16 | tag[3] << 24);
        void* d; uint s; void* c; int exists;
        Check(((delegate* unmanaged<void*, uint, void**, uint*, void**, int*, int>)VT(face)[12])(face, t, &d, &s, &c, &exists));
        data = (byte*)d; size = (int)s; ctx = c;
        return exists != 0;
    }
    public static void ReleaseTable(void* face, void* ctx) => ((delegate* unmanaged<void*, void*, void>)VT(face)[13])(face, ctx);

    // ---- Glyph outlines (IDWriteFontFace::GetGlyphRunOutline into an ID2D1SimplifiedGeometrySink) ----

    private static readonly Guid IidGeometrySink = new("2cd9069e-12e2-11dc-9fed-001143a055f9");
    private static void** _sinkObject;
    [ThreadStatic] private static GlyphOutline? t_outline;

    private static void** SinkObject()
    {
        if (_sinkObject != null) return _sinkObject;
        var vt = (void**)NativeMemory.Alloc((nuint)(sizeof(void*) * 10));
        vt[0] = (delegate* unmanaged<void*, Guid*, void**, int>)&SinkQueryInterface;
        vt[1] = (delegate* unmanaged<void*, uint>)&AddRefRelease;
        vt[2] = (delegate* unmanaged<void*, uint>)&AddRefRelease;
        vt[3] = (delegate* unmanaged<void*, int, void>)&SinkSetFillMode;
        vt[4] = (delegate* unmanaged<void*, int, void>)&SinkIgnore;
        vt[5] = (delegate* unmanaged<void*, Point2F, int, void>)&SinkBeginFigure;
        vt[6] = (delegate* unmanaged<void*, Point2F*, uint, void>)&SinkAddLines;
        vt[7] = (delegate* unmanaged<void*, Point2F*, uint, void>)&SinkAddBeziers;
        vt[8] = (delegate* unmanaged<void*, int, void>)&SinkEndFigure;
        vt[9] = (delegate* unmanaged<void*, int>)&SinkClose;
        var obj = (void**)NativeMemory.Alloc((nuint)sizeof(void*));
        obj[0] = vt;
        return _sinkObject = obj;
    }

    /// <summary>
    /// One glyph's outline at 1000 units per em, y up, as PDF path operators. Works for every outline
    /// format DirectWrite can rasterise (TrueType, CFF, a variable font's selected instance).
    /// </summary>
    public static GlyphOutline Outline(void* face, ushort gid)
    {
        var outline = new GlyphOutline();
        var sink = SinkObject();
        t_outline = outline;
        try
        {
            int hr = ((delegate* unmanaged<void*, float, ushort*, float*, GlyphOffset*, uint, int, int, void*, int>)VT(face)[14])(
                face, 1000f, &gid, null, null, 1, 0, 0, sink);
            if (hr < 0) outline.Path.Clear(); // a glyph that cannot be outlined is drawn empty rather than failing the export
        }
        finally { t_outline = null; }
        return outline;
    }

    [UnmanagedCallersOnly]
    private static int SinkQueryInterface(void* self, Guid* iid, void** result)
    {
        if (*iid == IidUnknown || *iid == IidGeometrySink) { *result = self; return 0; }
        *result = null;
        return unchecked((int)0x80004002);
    }
    [UnmanagedCallersOnly] private static void SinkSetFillMode(void* self, int mode) { if (t_outline is { } o) o.Winding = mode == 1; }
    [UnmanagedCallersOnly] private static void SinkIgnore(void* self, int value) { }
    [UnmanagedCallersOnly] private static void SinkBeginFigure(void* self, Point2F start, int figureBegin) => t_outline?.Move(start);
    [UnmanagedCallersOnly]
    private static void SinkAddLines(void* self, Point2F* points, uint count)
    {
        if (t_outline is not { } o) return;
        for (uint i = 0; i < count; i++) o.Line(points[i]);
    }
    [UnmanagedCallersOnly]
    private static void SinkAddBeziers(void* self, Point2F* points, uint count)
    {
        if (t_outline is not { } o) return;
        for (uint i = 0; i < count; i++) o.Curve(points[i * 3], points[i * 3 + 1], points[i * 3 + 2]);
    }
    [UnmanagedCallersOnly] private static void SinkEndFigure(void* self, int figureEnd) => t_outline?.Path.Append("h\n");
    [UnmanagedCallersOnly] private static int SinkClose(void* self) => 0;

    // ---- IDWriteTextRenderer implementation ----
    [UnmanagedCallersOnly]
    private static int QueryInterface(void* self, Guid* iid, void** result)
    {
        if (*iid == IidUnknown || *iid == IidPixelSnapping || *iid == IidTextRenderer) { *result = self; return 0; }
        *result = null;
        return unchecked((int)0x80004002); // E_NOINTERFACE
    }
    [UnmanagedCallersOnly] private static uint AddRefRelease(void* self) => 1;
    [UnmanagedCallersOnly] private static int IsPixelSnappingDisabled(void* self, void* ctx, int* disabled) { *disabled = 1; return 0; }
    [UnmanagedCallersOnly]
    private static int GetCurrentTransform(void* self, void* ctx, float* m)
    {
        m[0] = 1; m[1] = 0; m[2] = 0; m[3] = 1; m[4] = 0; m[5] = 0;
        return 0;
    }
    [UnmanagedCallersOnly] private static int GetPixelsPerDip(void* self, void* ctx, float* ppd) { *ppd = 1; return 0; }
    [UnmanagedCallersOnly]
    private static int DrawGlyphRun(void* self, void* ctx, float x, float y, int mode, DGlyphRun* run, DGlyphRunDesc* desc, void* effect)
    {
        try
        {
            ((IGlyphSink)GCHandle.FromIntPtr((nint)ctx).Target!).OnGlyphRun(x, y, run, desc);
            return 0;
        }
        catch { return unchecked((int)0x80004005); }
    }
    [UnmanagedCallersOnly] private static int DrawLine(void* self, void* ctx, float x, float y, void* line, void* effect) => 0;
    [UnmanagedCallersOnly]
    private static int DrawInlineObject(void* self, void* ctx, float x, float y, void* obj, int sideways, int rtl, void* effect) => 0;
}
