using System;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace Rede.Desktop.Controls;

public class MarkdownTextBlock : SelectableTextBlock
{
    public static readonly StyledProperty<string?> MarkdownProperty =
        AvaloniaProperty.Register<MarkdownTextBlock, string?>(nameof(Markdown));

    public string? Markdown
    {
        get => GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    static MarkdownTextBlock()
    {
        MarkdownProperty.Changed.AddClassHandler<MarkdownTextBlock>((tb, _) => tb.UpdateInlines());
    }

    private static readonly Regex CodeBlockRegex = new(@"```([\s\S]*?)```", RegexOptions.Compiled);
    private static readonly Regex InlineCodeRegex = new(@"`([^`]+)`", RegexOptions.Compiled);
    private static readonly Regex BoldRegex = new(@"\*\*(.+?)\*\*", RegexOptions.Compiled);
    private static readonly Regex ItalicRegex = new(@"(?<!\*)\*(?!\*)(.+?)(?<!\*)\*(?!\*)", RegexOptions.Compiled);
    private static readonly Regex StrikethroughRegex = new(@"~~(.+?)~~", RegexOptions.Compiled);

    private void UpdateInlines()
    {
        Inlines?.Clear();
        var text = Markdown;
        if (string.IsNullOrEmpty(text))
            return;

        Inlines ??= new InlineCollection();
        ParseMarkdown(text, Inlines);
    }

    private void ParseMarkdown(string text, InlineCollection inlines)
    {
        // First split by code blocks (```)
        var codeBlockParts = CodeBlockRegex.Split(text);
        var codeBlockMatches = CodeBlockRegex.Matches(text);
        int matchIdx = 0;

        for (int i = 0; i < codeBlockParts.Length; i++)
        {
            var part = codeBlockParts[i];

            // Check if this part was a code block capture group
            if (i > 0 && matchIdx < codeBlockMatches.Count)
            {
                var match = codeBlockMatches[matchIdx];
                if (match.Groups[1].Value == part)
                {
                    // This is code block content
                    var codeContent = part.Trim();
                    if (codeContent.Length > 0)
                    {
                        var span = new Span();
                        span.FontFamily = new FontFamily("Courier New, monospace");
                        span.Background = new SolidColorBrush(Color.Parse("#1a1a28"));
                        span.Foreground = new SolidColorBrush(Color.Parse("#2dd4bf"));
                        span.Inlines?.Add(new Run(codeContent));
                        inlines.Add(span);
                    }
                    matchIdx++;
                    continue;
                }
            }

            if (string.IsNullOrEmpty(part)) continue;
            ParseInlineMarkdown(part, inlines);
        }
    }

    private void ParseInlineMarkdown(string text, InlineCollection inlines)
    {
        // Build a combined regex to match all inline patterns at once
        // Order matters: bold before italic (** vs *)
        var pattern = @"(`[^`]+`)|(\*\*(.+?)\*\*)|((?<!\*)\*(?!\*)(.+?)(?<!\*)\*(?!\*))|(~~(.+?)~~)";
        var regex = new Regex(pattern, RegexOptions.Compiled);

        int lastEnd = 0;
        foreach (Match match in regex.Matches(text))
        {
            // Add plain text before match
            if (match.Index > lastEnd)
                inlines.Add(new Run(text[lastEnd..match.Index]));

            if (match.Groups[1].Success)
            {
                // Inline code: `code`
                var code = match.Groups[1].Value[1..^1]; // strip backticks
                var span = new Span();
                span.FontFamily = new FontFamily("Courier New, monospace");
                span.Background = new SolidColorBrush(Color.Parse("#1a1a28"));
                span.Foreground = new SolidColorBrush(Color.Parse("#2dd4bf"));
                span.Inlines?.Add(new Run(code));
                inlines.Add(span);
            }
            else if (match.Groups[2].Success)
            {
                // Bold: **text**
                var span = new Span();
                span.FontWeight = FontWeight.Bold;
                span.Inlines?.Add(new Run(match.Groups[3].Value));
                inlines.Add(span);
            }
            else if (match.Groups[4].Success)
            {
                // Italic: *text*
                var span = new Span();
                span.FontStyle = FontStyle.Italic;
                span.Inlines?.Add(new Run(match.Groups[5].Value));
                inlines.Add(span);
            }
            else if (match.Groups[6].Success)
            {
                // Strikethrough: ~~text~~
                var span = new Span();
                span.TextDecorations = new TextDecorationCollection
                {
                    new TextDecoration { Location = TextDecorationLocation.Strikethrough }
                };
                span.Inlines?.Add(new Run(match.Groups[7].Value));
                inlines.Add(span);
            }

            lastEnd = match.Index + match.Length;
        }

        // Add remaining plain text
        if (lastEnd < text.Length)
            inlines.Add(new Run(text[lastEnd..]));
    }
}
