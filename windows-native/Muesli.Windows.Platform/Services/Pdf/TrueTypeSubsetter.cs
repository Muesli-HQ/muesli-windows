// TrueType subsetter for PDF CIDFontType2 embedding. Glyph ids are retained (CIDToGIDMap /Identity),
// unused glyphs are emptied, and only the tables a PDF consumer needs are kept.
using System.Buffers.Binary;

namespace Muesli.Windows.Services.Pdf;

internal static unsafe class FontSubset
{
    private static ushort U16(byte[] b, int o) => BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(o));
    private static uint U32(byte[] b, int o) => BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(o));

    /// <summary>
    /// Whether the face can go into the PDF as an embedded TrueType subset. It cannot when it has no
    /// 'glyf' outlines (CFF OpenType, bitmap fonts), when it is a variable font (the file holds only the
    /// default instance, while DirectWrite shaped with the selected one), or when its OS/2 fsType bits
    /// forbid embedding or subsetting. Such faces are drawn as outline (Type 3) fonts instead.
    /// </summary>
    public static bool CanEmbed(void* face)
    {
        try
        {
            if (DW.FontTable(face, "fvar") is not null) return false;
            if (DW.FontTable(face, "OS/2") is { Length: >= 10 } os2)
            {
                int fsType = U16(os2, 8);
                if ((fsType & 0x0002) != 0 || (fsType & 0x0100) != 0 || (fsType & 0x0200) != 0) return false;
            }
            var head = DW.FontTable(face, "head"); var maxp = DW.FontTable(face, "maxp");
            var hhea = DW.FontTable(face, "hhea"); var hmtx = DW.FontTable(face, "hmtx");
            if (head is not { Length: >= 54 } || maxp is not { Length: >= 6 } || hhea is not { Length: >= 36 } || hmtx is null) return false;
            if (!DW.BorrowTable(face, "glyf", out _, out _, out var glyfCtx)) return false;
            DW.ReleaseTable(face, glyfCtx);
            if (!DW.BorrowTable(face, "loca", out _, out var locaLength, out var locaCtx)) return false;
            DW.ReleaseTable(face, locaCtx);
            return locaLength >= (U16(maxp, 4) + 1) * (U16(head, 50) != 0 ? 4 : 2);
        }
        catch (InvalidOperationException) { return false; }
    }

    /// <summary>Returns null when the face has no usable 'glyf' outlines; <see cref="CanEmbed"/> predicts that.</summary>
    public static byte[]? Build(void* face, SortedSet<ushort> used, out int[] bbox)
    {
        bbox = [0, 0, 0, 0];
        var head = DW.FontTable(face, "head");
        var maxp = DW.FontTable(face, "maxp");
        var hhea = DW.FontTable(face, "hhea");
        var hmtx = DW.FontTable(face, "hmtx");
        if (head is null || maxp is null || hhea is null || hmtx is null) return null;
        // glyf and loca are tens of megabytes in CJK fonts: read them in place rather than copying.
        if (!DW.BorrowTable(face, "glyf", out var glyfPtr, out var glyfLength, out var glyfCtx)) return null;
        if (!DW.BorrowTable(face, "loca", out var locaPtr, out var locaLength, out var locaCtx)) { DW.ReleaseTable(face, glyfCtx); return null; }
        try { return Build(face, used, head, maxp, hhea, hmtx, glyfPtr, glyfLength, locaPtr, locaLength, out bbox); }
        finally { DW.ReleaseTable(face, glyfCtx); DW.ReleaseTable(face, locaCtx); }
    }

    private static byte[]? Build(void* face, SortedSet<ushort> used, byte[] head, byte[] maxp, byte[] hhea, byte[] hmtx,
        byte* glyfPtr, int glyfLength, byte* locaPtr, int locaLength, out int[] bbox)
    {
        var glyf = new ReadOnlySpan<byte>(glyfPtr, glyfLength);
        int numGlyphs = U16(maxp, 4);
        bool longLoca = U16(head, 50) != 0;
        if (locaLength < (numGlyphs + 1) * (longLoca ? 4 : 2)) { bbox = [0, 0, 0, 0]; return null; }
        uint Loca(int g) => longLoca
            ? BinaryPrimitives.ReadUInt32BigEndian(new ReadOnlySpan<byte>(locaPtr + g * 4, 4))
            : (uint)BinaryPrimitives.ReadUInt16BigEndian(new ReadOnlySpan<byte>(locaPtr + g * 2, 2)) * 2;
        static ushort G16(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadUInt16BigEndian(b.Slice(o));
        bbox = [(short)U16(head, 36), (short)U16(head, 38), (short)U16(head, 40), (short)U16(head, 42)];

        // Composite glyphs pull in their components.
        var keep = new SortedSet<ushort>(used) { 0 };
        var queue = new Queue<ushort>(keep);
        while (queue.Count > 0)
        {
            int g = queue.Dequeue();
            if (g >= numGlyphs) continue;
            int start = (int)Loca(g), end = (int)Loca(g + 1);
            if (end - start < 10 || end > glyf.Length || (short)G16(glyf, start) >= 0) continue;
            int p = start + 10;
            while (p + 4 <= end)
            {
                ushort flags = G16(glyf, p), component = G16(glyf, p + 2);
                if (keep.Add(component)) queue.Enqueue(component);
                p += 4 + ((flags & 0x1) != 0 ? 4 : 2);
                if ((flags & 0x8) != 0) p += 2; else if ((flags & 0x40) != 0) p += 4; else if ((flags & 0x80) != 0) p += 8;
                if ((flags & 0x20) == 0) break;
            }
        }

        var kept = new bool[numGlyphs];
        foreach (var g in keep) if (g < numGlyphs) kept[g] = true;
        var newGlyf = new MemoryStream();
        var newLoca = new byte[(numGlyphs + 1) * 4];
        for (int g = 0; g < numGlyphs; g++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(newLoca.AsSpan(g * 4), (uint)newGlyf.Length);
            if (!kept[g]) continue;
            int start = (int)Loca(g), end = (int)Loca(g + 1);
            if (end <= start || end > glyf.Length) continue;
            newGlyf.Write(glyf.Slice(start, end - start));
            while (newGlyf.Length % 4 != 0) newGlyf.WriteByte(0);
        }
        BinaryPrimitives.WriteUInt32BigEndian(newLoca.AsSpan(numGlyphs * 4), (uint)newGlyf.Length);

        // Zero the metrics of dropped glyphs so the table deflates to almost nothing. The last long
        // metric stays because glyphs past numberOfHMetrics inherit its advance.
        int numHMetrics = U16(hhea, 34);
        var newHmtx = (byte[])hmtx.Clone();
        for (int g = 0; g < numGlyphs; g++)
        {
            if (kept[g]) continue;
            if (g < numHMetrics - 1) newHmtx.AsSpan(g * 4, 4).Clear();
            else if (g >= numHMetrics)
            {
                int o = numHMetrics * 4 + (g - numHMetrics) * 2;
                if (o + 2 <= newHmtx.Length) newHmtx.AsSpan(o, 2).Clear();
            }
        }

        var newHead = (byte[])head.Clone();
        BinaryPrimitives.WriteUInt32BigEndian(newHead.AsSpan(8), 0);  // checkSumAdjustment, patched below
        BinaryPrimitives.WriteUInt16BigEndian(newHead.AsSpan(50), 1); // long loca

        var tables = new SortedDictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["glyf"] = newGlyf.ToArray(), ["head"] = newHead, ["hhea"] = hhea, ["hmtx"] = newHmtx, ["loca"] = newLoca, ["maxp"] = maxp,
        };
        foreach (var tag in new[] { "cvt ", "fpgm", "prep" })
            if (DW.FontTable(face, tag) is { } t) tables[tag] = t;

        return WriteSfnt(tables);
    }

    private static byte[] WriteSfnt(SortedDictionary<string, byte[]> tables)
    {
        int n = tables.Count, entrySelector = 0;
        while (1 << (entrySelector + 1) <= n) entrySelector++;
        int searchRange = (1 << entrySelector) * 16;
        int offset = 12 + n * 16;
        var total = offset + tables.Values.Sum(t => (t.Length + 3) & ~3);
        var o = new byte[total];
        BinaryPrimitives.WriteUInt32BigEndian(o, 0x00010000);
        BinaryPrimitives.WriteUInt16BigEndian(o.AsSpan(4), (ushort)n);
        BinaryPrimitives.WriteUInt16BigEndian(o.AsSpan(6), (ushort)searchRange);
        BinaryPrimitives.WriteUInt16BigEndian(o.AsSpan(8), (ushort)entrySelector);
        BinaryPrimitives.WriteUInt16BigEndian(o.AsSpan(10), (ushort)(n * 16 - searchRange));
        int record = 12, headOffset = 0;
        foreach (var (tag, data) in tables)
        {
            data.CopyTo(o, offset);
            for (int i = 0; i < 4; i++) o[record + i] = (byte)tag[i];
            BinaryPrimitives.WriteUInt32BigEndian(o.AsSpan(record + 4), Checksum(o, offset, (data.Length + 3) & ~3));
            BinaryPrimitives.WriteUInt32BigEndian(o.AsSpan(record + 8), (uint)offset);
            BinaryPrimitives.WriteUInt32BigEndian(o.AsSpan(record + 12), (uint)data.Length);
            if (tag == "head") headOffset = offset;
            record += 16;
            offset += (data.Length + 3) & ~3;
        }
        BinaryPrimitives.WriteUInt32BigEndian(o.AsSpan(headOffset + 8), 0xB1B0AFBA - Checksum(o, 0, o.Length));
        return o;
    }

    private static uint Checksum(byte[] b, int offset, int length)
    {
        uint sum = 0;
        for (int i = offset; i < offset + length; i += 4) sum += U32(b, i);
        return sum;
    }

    /// <summary>PostScript name (name id 6) or null.</summary>
    public static string? PostScriptName(void* face)
    {
        var name = DW.FontTable(face, "name");
        if (name is null || name.Length < 6) return null;
        int count = U16(name, 2), strings = U16(name, 4);
        for (int i = 0; i < count; i++)
        {
            int r = 6 + i * 12;
            if (r + 12 > name.Length || U16(name, r + 6) != 6) continue;
            int platform = U16(name, r), length = U16(name, r + 8), at = strings + U16(name, r + 10);
            if (at + length > name.Length) continue;
            var raw = platform == 3 || platform == 0
                ? System.Text.Encoding.BigEndianUnicode.GetString(name, at, length)
                : System.Text.Encoding.ASCII.GetString(name, at, length);
            var clean = new string(raw.Where(c => char.IsAsciiLetterOrDigit(c) || c == '-').ToArray());
            if (clean.Length > 0) return clean;
        }
        return null;
    }
}
