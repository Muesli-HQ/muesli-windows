using System;
using System.IO;
using System.Xml.Linq;
using System.Linq;
using Xunit;

namespace Muesli.Windows.Tests;

/// <summary>MAC-12 (macOS a5ef96d9, 2dd3d4b3): a long meeting title never widens the page or grows the header.</summary>
public sealed class MeetingTitleLayoutTests
{
    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine([TestRepositoryLayout.Root, "windows-native", "Muesli.Windows.WinUI", .. parts]));

    [Fact]
    public void Title_editor_is_one_line_at_a_fixed_height_inside_a_star_column()
    {
        var page = XDocument.Parse(Read("Pages", "MeetingDetailPage.xaml"));
        var style = page.Descendants().Single(e => e.Name.LocalName == "Style" &&
            (string?)e.Attributes().FirstOrDefault(a => a.Name.LocalName == "Key") == "MeetingTitleEditorStyle");
        string? Setter(string property) => style.Elements().FirstOrDefault(e => (string?)e.Attribute("Property") == property)?.Attribute("Value")?.Value;
        Assert.Equal("NoWrap", Setter("TextWrapping"));
        Assert.Equal("40", Setter("Height"));
        Assert.Null(Setter("MinHeight"));

        var block = page.Descendants().Single(e => (string?)e.Attributes().FirstOrDefault(a => a.Name.LocalName == "Name") == "TitleBlock");
        Assert.Equal("0", (string?)block.Attribute("MinWidth"));
        Assert.Null(block.Attribute("Grid.Column"));
    }

    [Fact]
    public void Meeting_lists_sort_typed_timestamps_without_parsing()
    {
        var library = Read("Services", "WinUiLibraryContext.cs");
        Assert.DoesNotContain("DateTime.Parse", library, StringComparison.Ordinal);
        Assert.DoesNotContain("DateTimeOffset.Parse", library, StringComparison.Ordinal);
        Assert.Contains(".OrderByDescending(item => item.CreatedAt)", library, StringComparison.Ordinal);
    }
}
