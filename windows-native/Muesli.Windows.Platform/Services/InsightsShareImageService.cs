using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace Muesli.Windows.Services;

public sealed record InsightsShareCardData(
    string RangeLabel,
    int TotalWords,
    int Meetings,
    int AverageWpm,
    int CurrentStreakDays,
    int ActiveDays,
    int DictationWords,
    int MeetingWords);

/// <summary>Creates a transcript-free, deterministic 1200×630 activity card.</summary>
public static class InsightsShareImageService
{
    public const int Width = 1200;
    public const int Height = 630;

    public static void SavePng(string path, InsightsShareCardData data)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(data);
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temporary = fullPath + $".{Guid.NewGuid():N}.tmp";
        using var bitmap = new Bitmap(Width, Height, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        using var background = new LinearGradientBrush(
            new Rectangle(0, 0, Width, Height),
            Color.FromArgb(17, 20, 28), Color.FromArgb(22, 31, 47), 20f);
        graphics.FillRectangle(background, 0, 0, Width, Height);

        using var accent = new SolidBrush(Color.FromArgb(39, 128, 255));
        using var cyan = new SolidBrush(Color.FromArgb(57, 210, 224));
        using var primary = new SolidBrush(Color.FromArgb(245, 247, 250));
        using var secondary = new SolidBrush(Color.FromArgb(164, 174, 190));
        using var card = new SolidBrush(Color.FromArgb(40, 255, 255, 255));
        using var titleFont = new Font("Segoe UI", 23, FontStyle.Bold, GraphicsUnit.Pixel);
        using var heroFont = new Font("Segoe UI", 82, FontStyle.Bold, GraphicsUnit.Pixel);
        using var metricFont = new Font("Segoe UI", 31, FontStyle.Bold, GraphicsUnit.Pixel);
        using var bodyFont = new Font("Segoe UI", 20, FontStyle.Regular, GraphicsUnit.Pixel);
        using var labelFont = new Font("Segoe UI", 15, FontStyle.Bold, GraphicsUnit.Pixel);

        graphics.FillEllipse(accent, 58, 48, 30, 30);
        graphics.DrawString("muesli", titleFont, primary, 100, 46);
        graphics.DrawString($"ACTIVITY · {data.RangeLabel.ToUpperInvariant()}", labelFont, secondary, 58, 108);
        graphics.DrawString(data.TotalWords.ToString("N0"), heroFont, primary, 52, 142);
        graphics.DrawString("words transcribed", bodyFont, secondary, 62, 242);

        DrawMetric(graphics, card, primary, secondary, metricFont, labelFont, 58, 312, 245, "MEETINGS", data.Meetings.ToString("N0"));
        DrawMetric(graphics, card, primary, secondary, metricFont, labelFont, 319, 312, 245, "AVERAGE PACE", data.AverageWpm <= 0 ? "—" : $"{data.AverageWpm:N0} WPM");
        DrawMetric(graphics, card, primary, secondary, metricFont, labelFont, 580, 312, 245, "CURRENT STREAK", $"{data.CurrentStreakDays:N0} days");
        DrawMetric(graphics, card, primary, secondary, metricFont, labelFont, 841, 312, 301, "ACTIVE DAYS", data.ActiveDays.ToString("N0"));

        var total = Math.Max(1, data.DictationWords + data.MeetingWords);
        var barX = 58;
        var barY = 492;
        var barWidth = 1084;
        using var barBackground = ColorBrush(Color.FromArgb(35, 42, 56));
        graphics.FillRectangle(barBackground, barX, barY, barWidth, 18);
        graphics.FillRectangle(accent, barX, barY, (int)Math.Round(barWidth * data.DictationWords / (double)total), 18);
        graphics.FillRectangle(cyan, barX + (int)Math.Round(barWidth * data.DictationWords / (double)total), barY,
            (int)Math.Round(barWidth * data.MeetingWords / (double)total), 18);
        graphics.DrawString($"Dictation  {data.DictationWords:N0}", bodyFont, accent, 58, 527);
        graphics.DrawString($"Meetings  {data.MeetingWords:N0}", bodyFont, cyan, 355, 527);
        graphics.DrawString("Private, local activity · no transcript text included", bodyFont, secondary, 58, 578);

        bitmap.Save(temporary, ImageFormat.Png);
        File.Move(temporary, fullPath, true);
    }

    private static void DrawMetric(Graphics graphics, Brush card, Brush primary, Brush secondary,
        Font valueFont, Font labelFont, int x, int y, int width, string label, string value)
    {
        using var path = RoundedRectangle(new Rectangle(x, y, width, 128), 18);
        graphics.FillPath(card, path);
        graphics.DrawString(value, valueFont, primary, x + 20, y + 22);
        graphics.DrawString(label, labelFont, secondary, x + 20, y + 82);
    }

    private static GraphicsPath RoundedRectangle(Rectangle rectangle, int radius)
    {
        var diameter = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(rectangle.Left, rectangle.Top, diameter, diameter, 180, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Top, diameter, diameter, 270, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rectangle.Left, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static SolidBrush ColorBrush(Color color) => new(color);
}
