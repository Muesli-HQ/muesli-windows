using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Muesli.Windows.Services;
using Windows.System;
using Microsoft.UI.Text;
using System.Text.RegularExpressions;

namespace Muesli.Windows.WinUI.Controls;

/// <summary>Native selection, rich formatting and undo, with Markdown as the only persisted value.</summary>
public sealed class MarkdownNotesEditor : RichEditBox
{
    public static readonly DependencyProperty MarkdownProperty = DependencyProperty.Register(nameof(Markdown), typeof(string),
        typeof(MarkdownNotesEditor), new PropertyMetadata("", MarkdownChanged));
    public string Markdown { get => (string)GetValue(MarkdownProperty); set => SetValue(MarkdownProperty, value); }
    private bool _applying;
    private string _published = "";

    public MarkdownNotesEditor()
    {
        FontSize = 16; Padding = new Thickness(24, 20, 24, 20); AcceptsReturn = true;
        TextWrapping = TextWrapping.Wrap;
        TextChanged += (_, _) => Publish();
        PreviewKeyDown += ContinueList;
        LostFocus += (_, _) => Publish();
    }

    private static void MarkdownChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var editor = (MarkdownNotesEditor)sender;
        var markdown = (string?)args.NewValue ?? "";
        if (markdown == editor._published) return;
        editor.Render(markdown);
    }

    private void Render(string markdown)
    {
        _applying = true;
        try
        {
            var lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').Select(MeetingNotesRichText.ParseLine).ToArray();
            var text = string.Join("\r", lines.Select(line => string.Concat(line.Runs.Select(run => run.Text))));
            Document.SetText(TextSetOptions.None, text);
            Document.GetRange(0, text.Length).CharacterFormat.SetClone(Document.GetDefaultCharacterFormat());
            var position = 0;
            foreach (var line in lines)
            {
                var length = line.Runs.Sum(run => run.Text.Length);
                var paragraph = Document.GetRange(position, position + length);
                paragraph.ParagraphFormat.SpaceAfter = line.Heading > 0 ? 14 : 8;
                foreach (var run in line.Runs)
                {
                    var range = Document.GetRange(position, position + run.Text.Length);
                    range.CharacterFormat.Size = line.Heading > 0 ? 26 - (line.Heading - 1) : 16;
                    range.CharacterFormat.Bold = run.Bold || line.Heading > 0 ? FormatEffect.On : FormatEffect.Off;
                    range.CharacterFormat.Italic = run.Italic ? FormatEffect.On : FormatEffect.Off;
                    range.CharacterFormat.Name = run.Code ? "Consolas" : FontFamily.Source;
                    if (run.Link is { } link) range.Link = $"\"{link.Replace("\"", "") }\"";
                    position += run.Text.Length;
                }
                position++;
            }
            Document.Selection.SetRange(0, 0);
            _published = markdown;
        }
        finally { _applying = false; }
    }

    private void Publish()
    {
        if (_applying || IsReadOnly) return;
        try { PromoteTypedMarkdown(); }
        catch (RegexMatchTimeoutException) { /* Preserve literal writing if a paragraph exceeds the formatting budget. */ }
        Document.GetText(TextGetOptions.None, out var full);
        if (full.EndsWith('\r')) full = full[..^1]; // TOM supplies one implicit final paragraph.
        var lines = new List<string>();
        var start = 0;
        foreach (var text in full.Split('\r'))
        {
            var runs = new List<MeetingNoteRun>();
            var heading = text.Length > 0 ? Math.Clamp(27 - (int)Document.GetRange(start, start + 1).CharacterFormat.Size, 1, 6) : 0;
            if (text.Length == 0 || Document.GetRange(start, start + 1).CharacterFormat.Size < 21) heading = 0;
            for (var pos = start; pos < start + text.Length;)
            {
                var range = Document.GetRange(pos, pos);
                range.MoveEnd(TextRangeUnit.CharacterFormat, 1);
                var end = Math.Clamp(range.EndPosition, pos + 1, start + text.Length);
                range.SetRange(pos, end);
                range.GetText(TextGetOptions.None, out var value);
                var format = range.CharacterFormat;
                var link = range.Link.Trim('"');
                runs.Add(new(value, heading == 0 && format.Bold == FormatEffect.On, format.Italic == FormatEffect.On,
                    format.Name == "Consolas", link.Length == 0 ? null : link));
                pos = end;
            }
            lines.Add(MeetingNotesRichText.SerializeLine(new(heading, runs)));
            start += text.Length + 1;
        }
        var markdown = string.Join("\n", lines);
        if (markdown == _published) return;
        _published = markdown;
        _applying = true;
        try { Markdown = markdown; }
        finally { _applying = false; }
    }

    public void Format(string marker)
    {
        if (!IsEnabled || IsReadOnly) return;
        var selection = Document.Selection;
        _applying = true;
        Document.BeginUndoGroup();
        try
        {
            if (marker == "**") selection.CharacterFormat.Bold = FormatEffect.Toggle;
            else if (marker == "_") selection.CharacterFormat.Italic = FormatEffect.Toggle;
            else
            {
                var range = selection.GetClone();
                range.Expand(TextRangeUnit.Paragraph);
                if (marker == "# ")
                {
                    var heading = range.CharacterFormat.Size < 21;
                    range.CharacterFormat.Size = heading ? 26 : 16;
                    range.CharacterFormat.Bold = heading ? FormatEffect.On : FormatEffect.Off;
                }
                else
                {
                    range.GetText(TextGetOptions.None, out var text);
                    var prefix = marker == "- " ? "• " : "☐ ";
                    var lines = text.Split('\r');
                    var position = range.StartPosition;
                    var edits = new List<(int Start, int Remove)>();
                    for (var i = 0; i < lines.Length; i++)
                    {
                        if (i < lines.Length - 1 || lines[i].Length > 0 || lines.Length == 1)
                            edits.Add((position, lines[i].StartsWith("• ") || lines[i].StartsWith("☐ ") || lines[i].StartsWith("☑ ") ? 2 : 0));
                        position += lines[i].Length + 1;
                    }
                    // Replace only markers, so bold, links and other body formatting survive.
                    foreach (var edit in edits.AsEnumerable().Reverse())
                        Document.GetRange(edit.Start, edit.Start + edit.Remove).SetText(TextSetOptions.None, prefix);
                    if (edits.Count == 1)
                    {
                        var caret = edits[0].Start + lines[0].Length - edits[0].Remove + prefix.Length;
                        selection.SetRange(caret, caret);
                    }
                }
            }
        }
        finally { Document.EndUndoGroup(); _applying = false; }
        Publish(); Focus(FocusState.Programmatic);
    }

    private void PromoteTypedMarkdown()
    {
        var selection = Document.Selection;
        if (selection.StartPosition != selection.EndPosition) return;
        var paragraph = selection.GetClone(); paragraph.Expand(TextRangeUnit.Paragraph);
        paragraph.GetText(TextGetOptions.None, out var raw);
        raw = raw.TrimEnd('\r');
        var start = paragraph.StartPosition;
        var caret = selection.StartPosition;
        _applying = true;
        try
        {
            foreach (Match match in Regex.Matches(raw, @"(?<!\\)(?<marker>\*\*|__|`|(?<!\*)\*(?!\*)|(?<!\w)_(?!_))(?<body>.+?)(?<!\\)\k<marker>", RegexOptions.None, TimeSpan.FromMilliseconds(100)).Cast<Match>().Reverse())
            {
                var marker = match.Groups["marker"].Value;
                var body = Document.GetRange(start + match.Groups["body"].Index, start + match.Groups["body"].Index + match.Groups["body"].Length);
                if (marker == "`") body.CharacterFormat.Name = "Consolas";
                else if (marker.Length == 2) body.CharacterFormat.Bold = FormatEffect.On;
                else body.CharacterFormat.Italic = FormatEffect.On;
                var trailing = start + match.Index + match.Length - marker.Length;
                Document.GetRange(trailing, trailing + marker.Length).SetText(TextSetOptions.None, "");
                Document.GetRange(start + match.Index, start + match.Index + marker.Length).SetText(TextSetOptions.None, "");
                if (caret >= trailing + marker.Length) caret -= marker.Length;
                if (caret >= start + match.Index + marker.Length) caret -= marker.Length;
            }
            var parsed = MeetingNotesRichText.ParseLine(raw);
            var prefix = raw.StartsWith("- [ ] ") ? (6, "☐ ") : raw.StartsWith("- [x] ") || raw.StartsWith("- [X] ") ? (6, "☑ ")
                : raw.StartsWith("- ") ? (2, "• ") : (0, "");
            if (parsed.Heading > 0)
            {
                var length = parsed.Heading + 1;
                Document.GetRange(start, start + length).SetText(TextSetOptions.None, "");
                paragraph = Document.GetRange(start, start); paragraph.Expand(TextRangeUnit.Paragraph);
                paragraph.CharacterFormat.Size = 27 - parsed.Heading;
                paragraph.CharacterFormat.Bold = FormatEffect.On;
                paragraph.ParagraphFormat.SpaceAfter = 14;
                caret = Math.Max(start, caret - length);
            }
            else if (prefix.Item1 > 0)
            {
                Document.GetRange(start, start + prefix.Item1).SetText(TextSetOptions.None, prefix.Item2);
                caret = Math.Max(start, caret + prefix.Item2.Length - prefix.Item1);
            }
            selection.SetRange(caret, caret);
        }
        finally { _applying = false; }
    }

    private void ContinueList(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key != VirtualKey.Enter || IsReadOnly || !IsEnabled) return;
        var selection = Document.Selection;
        var line = selection.GetClone(); line.Expand(TextRangeUnit.Paragraph);
        line.GetText(TextGetOptions.None, out var text);
        var marker = text.StartsWith("• ") ? "• " : text.StartsWith("☐ ") || text.StartsWith("☑ ") ? "☐ " : null;
        if (marker is null) return;
        args.Handled = true;
        Document.BeginUndoGroup();
        try
        {
            if (text.TrimEnd('\r').Length == 2)
            {
                Document.GetText(TextGetOptions.None, out var full);
                var hasFollowingParagraph = line.EndPosition < full.Length - 1;
                var caret = line.StartPosition;
                line.SetText(TextSetOptions.None, hasFollowingParagraph ? "\r" : "");
                selection.SetRange(caret, caret);
            }
            else
            {
                var caret = selection.StartPosition;
                selection.SetText(TextSetOptions.None, "\r" + marker);
                selection.SetRange(caret + marker.Length + 1, caret + marker.Length + 1);
            }
        }
        finally { Document.EndUndoGroup(); }
        Publish();
    }
}
