using System.Drawing;
using Muesli.Windows.Services;

namespace Muesli.Windows.Tests;

public sealed class InsightsShareImageTests
{
    [Fact]
    public void ShareCardIsExactSocialImageSizeAndCanBeAtomicallyReplaced()
    {
        using var directory = new TestDirectory();
        var path = directory.File("activity.png");
        var data = new InsightsShareCardData(
            "12 months", 945700, 208, 154, 3, 134, 98400, 847300);

        InsightsShareImageService.SavePng(path, data);
        InsightsShareImageService.SavePng(path, data with { TotalWords = 945701 });

        using var image = Image.FromFile(path);
        Assert.Equal(InsightsShareImageService.Width, image.Width);
        Assert.Equal(InsightsShareImageService.Height, image.Height);
        Assert.Equal(System.Drawing.Imaging.ImageFormat.Png.Guid, image.RawFormat.Guid);
        Assert.DoesNotContain(Directory.EnumerateFiles(directory.Path), candidate =>
            candidate.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase));
    }
}
