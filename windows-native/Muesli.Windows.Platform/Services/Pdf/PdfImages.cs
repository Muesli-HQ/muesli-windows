// Image XObjects without any imaging library: PNG is decoded by hand (zlib inflate + unfilter) so its
// alpha channel can become a soft mask; JPEG is embedded as-is (/DCTDecode) after reading its size.
using System.Buffers.Binary;
using System.IO.Compression;

namespace Muesli.Windows.Services.Pdf;

internal sealed class ImageRes
{
    public int Index, Width, Height;
    public byte[] Data = [];          // raw samples (PNG) or the JPEG file
    public bool IsJpeg;
    public string ColorSpace = "/DeviceRGB";
    public byte[]? Alpha;             // 8-bit soft mask, PNG only
}

internal static class ImageCodec
{
    public static ImageRes Decode(byte[] bytes)
    {
        if (bytes.Length > 8 && bytes[0] == 0x89 && bytes[1] == 'P') return Png(bytes);
        if (bytes.Length > 4 && bytes[0] == 0xFF && bytes[1] == 0xD8) return Jpeg(bytes);
        throw new NotSupportedException("Only PNG and JPEG images can be embedded.");
    }

    private static ImageRes Jpeg(byte[] b)
    {
        for (int p = 2; p + 9 < b.Length;)
        {
            if (b[p] != 0xFF) { p++; continue; }
            int marker = b[p + 1];
            if (marker is 0xD8 or 0x01 or >= 0xD0 and <= 0xD7 or 0xFF) { p += marker == 0xFF ? 1 : 2; continue; }
            int length = BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(p + 2));
            if (marker is >= 0xC0 and <= 0xCF and not (0xC4 or 0xC8 or 0xCC))
            {
                int components = b[p + 9];
                if (components is not (1 or 3)) throw new NotSupportedException("CMYK JPEG images are not supported.");
                return new ImageRes
                {
                    IsJpeg = true, Data = b, ColorSpace = components == 1 ? "/DeviceGray" : "/DeviceRGB",
                    Height = BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(p + 5)), Width = BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(p + 7)),
                };
            }
            p += 2 + length;
        }
        throw new NotSupportedException("The JPEG image has no frame header.");
    }

    private static ImageRes Png(byte[] b)
    {
        int width = 0, height = 0, depth = 0, type = 0, interlace = 0;
        byte[]? palette = null, transparency = null;
        using var idat = new MemoryStream();
        for (int p = 8; p + 12 <= b.Length;)
        {
            int length = (int)BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(p));
            var tag = System.Text.Encoding.ASCII.GetString(b, p + 4, 4);
            var data = b.AsSpan(p + 8, length);
            switch (tag)
            {
                case "IHDR":
                    width = (int)BinaryPrimitives.ReadUInt32BigEndian(data); height = (int)BinaryPrimitives.ReadUInt32BigEndian(data[4..]);
                    depth = data[8]; type = data[9]; interlace = data[12];
                    break;
                case "PLTE": palette = data.ToArray(); break;
                case "tRNS": transparency = data.ToArray(); break;
                case "IDAT": idat.Write(data); break;
            }
            p += 12 + length;
        }
        if (depth != 8 || interlace != 0 || width == 0) throw new NotSupportedException("Only 8-bit non-interlaced PNG images are supported.");
        int channels = type switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => throw new NotSupportedException("Unknown PNG colour type.") };

        int stride = width * channels;
        var raw = new byte[(stride + 1) * height];
        idat.Position = 0;
        using (var z = new ZLibStream(idat, CompressionMode.Decompress)) z.ReadExactly(raw);

        var pixels = new byte[stride * height];
        for (int y = 0; y < height; y++)
        {
            int filter = raw[y * (stride + 1)];
            var src = raw.AsSpan(y * (stride + 1) + 1, stride);
            var dst = pixels.AsSpan(y * stride, stride);
            var up = y > 0 ? pixels.AsSpan((y - 1) * stride, stride) : default;
            for (int x = 0; x < stride; x++)
            {
                int a = x >= channels ? dst[x - channels] : 0, c = y > 0 && x >= channels ? up[x - channels] : 0, u = y > 0 ? up[x] : 0;
                int predicted = filter switch
                {
                    0 => 0, 1 => a, 2 => u, 3 => (a + u) >> 1,
                    4 => Paeth(a, u, c),
                    _ => throw new NotSupportedException("Unknown PNG filter."),
                };
                dst[x] = (byte)(src[x] + predicted);
            }
        }

        int count = width * height;
        var image = new ImageRes { Width = width, Height = height };
        switch (type)
        {
            case 0: image.Data = pixels; image.ColorSpace = "/DeviceGray"; break;
            case 2: image.Data = pixels; break;
            case 3:
                image.Data = new byte[count * 3];
                if (transparency is not null) { image.Alpha = new byte[count]; Array.Fill(image.Alpha, (byte)255); }
                for (int i = 0; i < count; i++)
                {
                    int index = pixels[i];
                    palette.AsSpan(index * 3, 3).CopyTo(image.Data.AsSpan(i * 3));
                    if (transparency is not null && index < transparency.Length) image.Alpha![i] = transparency[index];
                }
                break;
            case 4:
                image.Data = new byte[count]; image.Alpha = new byte[count]; image.ColorSpace = "/DeviceGray";
                for (int i = 0; i < count; i++) { image.Data[i] = pixels[i * 2]; image.Alpha[i] = pixels[i * 2 + 1]; }
                break;
            case 6:
                image.Data = new byte[count * 3]; image.Alpha = new byte[count];
                for (int i = 0; i < count; i++)
                {
                    pixels.AsSpan(i * 4, 3).CopyTo(image.Data.AsSpan(i * 3));
                    image.Alpha[i] = pixels[i * 4 + 3];
                }
                break;
        }
        return image;
    }

    /// <summary>Shrinks a decoded RGB(A) PNG by an integer factor so its width is about <paramref name="target"/> pixels (alpha-weighted box filter).</summary>
    public static void BoxDownscale(ImageRes image, int target)
    {
        int f = image.Width / target;
        if (image.IsJpeg || f < 2 || image.ColorSpace != "/DeviceRGB") return;
        int w = image.Width / f, h = image.Height / f;
        var rgb = new byte[w * h * 3];
        var alpha = image.Alpha is null ? null : new byte[w * h];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            long r = 0, g = 0, b = 0, a = 0;
            for (int yy = 0; yy < f; yy++)
            for (int xx = 0; xx < f; xx++)
            {
                int i = (y * f + yy) * image.Width + x * f + xx;
                int weight = image.Alpha is null ? 255 : image.Alpha[i];
                r += image.Data[i * 3] * weight; g += image.Data[i * 3 + 1] * weight; b += image.Data[i * 3 + 2] * weight; a += weight;
            }
            int o = y * w + x;
            if (a > 0) { rgb[o * 3] = (byte)(r / a); rgb[o * 3 + 1] = (byte)(g / a); rgb[o * 3 + 2] = (byte)(b / a); }
            if (alpha is not null) alpha[o] = (byte)(a / (f * f));
        }
        image.Data = rgb; image.Alpha = alpha; image.Width = w; image.Height = h;
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }
}
