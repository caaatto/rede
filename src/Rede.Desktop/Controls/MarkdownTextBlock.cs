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

    // M2: Add regex timeout to prevent ReDoS on adversarial input
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);
    private static readonly Regex CodeBlockRegex = new(@"```([\s\S]*?)```", RegexOptions.Compiled, RegexTimeout);

    private static readonly Regex InlinePattern = new(
        @"(`[^`]+`)" +                          // group 1: inline code
        @"|(?<!\w)\*([^*]+?)\*(?!\w)" +          // group 2: *bold*
        @"|(?<!\w)_([^_]+?)_(?!\w)" +            // group 3: _italic_
        @"|(?<!\w)-([^-]+?)-(?!\w)",             // group 4: -strikethrough-
        RegexOptions.Compiled, RegexTimeout);

    private const int MaxRenderLength = 8192; // M10: Limit text length before regex processing

    private void UpdateInlines()
    {
        Inlines?.Clear();
        var text = Markdown;
        if (string.IsNullOrEmpty(text))
            return;

        // M10: Truncate before regex processing to prevent UI freezes
        if (text.Length > MaxRenderLength)
            text = text[..MaxRenderLength] + "…";

        Inlines ??= new InlineCollection();
        try { ParseMarkdown(text, Inlines); }
        catch (RegexMatchTimeoutException) { Inlines.Add(new Run(text)); }
    }

    private void ParseMarkdown(string text, InlineCollection inlines)
    {
        // Split by code blocks (```)
        var codeBlockParts = CodeBlockRegex.Split(text);
        var codeBlockMatches = CodeBlockRegex.Matches(text);
        int matchIdx = 0;

        for (int i = 0; i < codeBlockParts.Length; i++)
        {
            var part = codeBlockParts[i];

            if (i > 0 && matchIdx < codeBlockMatches.Count)
            {
                var match = codeBlockMatches[matchIdx];
                if (match.Groups[1].Value == part)
                {
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

    private static void ParseInlineMarkdown(string text, InlineCollection inlines)
    {
        int lastEnd = 0;
        foreach (Match match in InlinePattern.Matches(text))
        {
            if (match.Index > lastEnd)
                inlines.Add(new Run(text[lastEnd..match.Index]));

            if (match.Groups[1].Success)
            {
                // Inline code: `code`
                var code = match.Groups[1].Value[1..^1];
                var span = new Span();
                span.FontFamily = new FontFamily("Courier New, monospace");
                span.Background = new SolidColorBrush(Color.Parse("#1a1a28"));
                span.Foreground = new SolidColorBrush(Color.Parse("#2dd4bf"));
                span.Inlines?.Add(new Run(code));
                inlines.Add(span);
            }
            else if (match.Groups[2].Success)
            {
                // Bold: *text*
                var span = new Span();
                span.FontWeight = FontWeight.Bold;
                span.Inlines?.Add(new Run(match.Groups[2].Value));
                inlines.Add(span);
            }
            else if (match.Groups[3].Success)
            {
                // Italic: _text_
                var span = new Span();
                span.FontStyle = FontStyle.Italic;
                span.Inlines?.Add(new Run(match.Groups[3].Value));
                inlines.Add(span);
            }
            else if (match.Groups[4].Success)
            {
                // Strikethrough: -text-
                var span = new Span();
                span.TextDecorations = new TextDecorationCollection
                {
                    new TextDecoration { Location = TextDecorationLocation.Strikethrough }
                };
                span.Inlines?.Add(new Run(match.Groups[4].Value));
                inlines.Add(span);
            }

            lastEnd = match.Index + match.Length;
        }

        if (lastEnd < text.Length)
            inlines.Add(new Run(text[lastEnd..]));
    }
}
