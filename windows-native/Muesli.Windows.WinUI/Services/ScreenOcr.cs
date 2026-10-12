using System.Drawing.Imaging;
using System.Runtime.InteropServices.WindowsRuntime;
using Muesli.Windows.ComputerUse;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace Muesli.Windows.WinUI.Services;

/// <summary>
/// macOS ScreenContextCapture dictation OCR: renders only the target window (without bringing it
/// forward) and reads its text with the on-device Windows OCR engine. Nothing leaves the machine
/// here; the text joins the app context that hosted cleanup already sends.
/// </summary>
public static class ScreenOcr
{
    public static async Task<string?> ReadWindowAsync(IntPtr window)
    {
        if (window == IntPtr.Zero || Win32Desktop.WindowFrame(window) is not { } frame) return null;
        using var bitmap = Win32Desktop.CaptureWindow(window.ToInt64(), frame);
        if (bitmap is null || Math.Max(bitmap.Width, bitmap.Height) > OcrEngine.MaxImageDimension) return null;
        var engine = OcrEngine.TryCreateFromUserProfileLanguages();
        if (engine is null) return null;

        using var png = new MemoryStream();
        bitmap.Save(png, ImageFormat.Png);
        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(png.ToArray().AsBuffer());
        stream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(stream);
        using var image = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        var result = await engine.RecognizeAsync(image);
        var text = string.Join("\n", result.Lines.Select(line => line.Text)).Trim();
        return text.Length == 0 ? null : text;
    }

    /// <summary>macOS formatForPrompt: OCR text is the last line of the app context.</summary>
    public static string? Append(string? appContext, string? ocrText) =>
        string.IsNullOrWhiteSpace(ocrText) ? appContext : $"{appContext}\nOCR screen text: {ocrText}".TrimStart('\n');
}
