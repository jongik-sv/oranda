using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell.Icons;

namespace Oranda.App.Features.Ai.Chat;

/// <summary>
/// AI 글을 Markdown 부분 집합으로 그리는 컨트롤(<see cref="MarkdownParser"/> 가 읽은 블록을 옮긴다, 규격 = 시안 Markdown 요소표).
/// 제목은 작은 굵은 회색 글, 블릿은 촘촘하게, 표는 숫자 칸을 고정폭·오른쪽 정렬로, <c>&gt;</c> 인용은 주의 상자로 그린다.
/// 첫 「굵은 한 줄 + 바로 뒤 표」 는 결론 상자(옅은 파랑 머리 + 표) 하나로 묶는다.
/// 글은 <see cref="SelectableTextBlock"/> 이라 마우스로 고르고 복사할 수 있다. 색은 기존 리소스 키를 동적으로 묶어 테마를 따른다.
/// <see cref="Markdown"/> 이 바뀌면 통째로 다시 그린다(스트리밍으로 글이 늘 때마다 대화 창이 새 글을 넘겨 준다).
/// SQL 코드 블록은 닫는 울타리까지 온 뒤에만 편집기와 같은 색으로 칠한다(<see cref="SqlInlineText"/>, 스트리밍 중인 블록은 일반 글).
/// 그리다 예외가 나면 일반 글 한 덩어리로 보인다.
/// </summary>
public sealed partial class MarkdownView : StackPanel
{
    public static readonly StyledProperty<string?> MarkdownProperty = AvaloniaProperty.Register<MarkdownView, string?>(nameof(Markdown));

    private const double CellMaxWidth = 320;

    /// <summary>제목·표 머리·부제처럼 한 단계 낮춘 글의 불투명도(테마 글색을 그대로 옅게).</summary>
    private const double MutedOpacity = 0.72;

    static MarkdownView()
    {
        MarkdownProperty.Changed.AddClassHandler<MarkdownView>((view, _) => view.Rebuild());
    }

    public MarkdownView()
    {
        Spacing = 12;
    }

    /// <summary>그릴 Markdown 글.</summary>
    public string? Markdown
    {
        get => GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    private void Rebuild()
    {
        Children.Clear();
        var markdown = Markdown;
        if (string.IsNullOrEmpty(markdown))
        {
            return;
        }

        try
        {
            var blocks = MarkdownParser.Parse(markdown);
            var conclusion = ConclusionAt(blocks);
            for (var i = 0; i < blocks.Count; i++)
            {
                if (i == conclusion)
                {
                    Children.Add(Conclusion((MarkdownParagraph)blocks[i], (MarkdownTable)blocks[i + 1]));
                    i++;
                    continue;
                }

                Children.Add(Render(blocks[i]));
            }
        }
#pragma warning disable CA1031 // 그리는 중 어떤 실패든 대화 창을 죽이지 않고 일반 글로 보인다.
        catch (Exception)
#pragma warning restore CA1031
        {
            Children.Clear();
            Children.Add(new SelectableTextBlock { Text = markdown, TextWrapping = TextWrapping.Wrap });
        }
    }

    /// <summary>결론 상자로 묶을 문단의 자리: 첫 줄이 모두 굵은 첫 문단이고 바로 뒤가 표. 없으면 -1.</summary>
    internal static int ConclusionAt(IReadOnlyList<MarkdownBlock> blocks)
    {
        var first = blocks.ToList().FindIndex(b => b is MarkdownParagraph);
        return first >= 0 && first + 1 < blocks.Count && blocks[first + 1] is MarkdownTable && IsBoldLine(Lines(((MarkdownParagraph)blocks[first]).Spans)[0])
            ? first
            : -1;
    }

    /// <summary>문단 글 토막을 줄(<c>\n</c>) 단위로 나눈다. 꾸밈은 줄마다 그대로 남는다.</summary>
    internal static List<List<MarkdownSpan>> Lines(IReadOnlyList<MarkdownSpan> spans)
    {
        var lines = new List<List<MarkdownSpan>> { new() };
        foreach (var span in spans)
        {
            var parts = span.Text.Split('\n');
            for (var p = 0; p < parts.Length; p++)
            {
                if (p > 0)
                {
                    lines.Add([]);
                }

                if (parts[p].Length > 0)
                {
                    lines[^1].Add(span with { Text = parts[p] });
                }
            }
        }

        return lines;
    }

    private static bool IsBoldLine(IReadOnlyList<MarkdownSpan> line) =>
        line.Any(s => !string.IsNullOrWhiteSpace(s.Text)) && line.All(s => string.IsNullOrWhiteSpace(s.Text) || s.Style.HasFlag(MarkdownStyle.Bold));

    /// <summary>숫자 칸인가(141.5 ms · 2,237 · −37% · 2회 · 817 같은 값). 표에서 고정폭·오른쪽 정렬로 그린다.</summary>
    internal static bool IsNumeric(string text) => NumericCell().IsMatch(text.Trim());

    [GeneratedRegex(@"^[+\-−]?\d[\d,]*(?:\.\d+)?\s*(?:%|ms|s|초|분|회|행|건|개|블록|자|배|x|KB|MB|GB)?$", RegexOptions.CultureInvariant)]
    private static partial Regex NumericCell();

    private static Control Render(MarkdownBlock block) => block switch
    {
        MarkdownHeading heading => Heading(heading),
        MarkdownCode code => CodeBlock(code),
        MarkdownList list => List(list),
        MarkdownTable table => Table(table),
        MarkdownQuote quote => Caution(quote),
        MarkdownRule => Rule(),
        MarkdownParagraph paragraph => TextOf(paragraph.Spans),
        _ => new SelectableTextBlock { TextWrapping = TextWrapping.Wrap },
    };

    private static SelectableTextBlock TextOf(IReadOnlyList<MarkdownSpan> spans, double? fontSize = null, FontWeight? weight = null)
    {
        var block = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap, LineSpacing = 3 };
        if (fontSize is { } size)
        {
            block.FontSize = size;
        }

        if (weight is { } w)
        {
            block.FontWeight = w;
        }

        foreach (var span in spans)
        {
            var run = new Run(span.Text);
            if (span.Style.HasFlag(MarkdownStyle.Bold))
            {
                run.FontWeight = FontWeight.Bold;
            }

            if (span.Style.HasFlag(MarkdownStyle.Italic))
            {
                run.FontStyle = FontStyle.Italic;
            }

            if (span.Style.HasFlag(MarkdownStyle.Code))
            {
                // 인라인 코드: 고정폭 · 옅은 회색 바탕(글 선택·복사가 되도록 Run 으로 둔다 — Run 바탕은 모서리를 둥글게 할 수 없다).
                run.Bind(TextElement.FontFamilyProperty, block.GetResourceObservable("OrandaCodeFontFamily"));
                run.Bind(TextElement.BackgroundProperty, block.GetResourceObservable("OrandaPanelHeaderBrush"));
            }

            if (span.Style.HasFlag(MarkdownStyle.Link))
            {
                run.TextDecorations = Avalonia.Media.TextDecorations.Underline;
                run.Bind(TextElement.ForegroundProperty, block.GetResourceObservable("OrandaIconBlueBrush"));
            }

            block.Inlines ??= [];
            block.Inlines.Add(run);
        }

        return block;
    }

    private static SelectableTextBlock SmallText(IReadOnlyList<MarkdownSpan> spans, FontWeight? weight = null)
    {
        var text = TextOf(spans, weight: weight);
        text.Bind(TextBlock.FontSizeProperty, text.GetResourceObservable("OrandaFontSizeSmall"));
        return text;
    }

    /// <summary>제목: 12px 굵게 · 회색 · 밑줄 없음(1 단계만 본문보다 조금 크게). 바로 아래 블록에 붙도록 아래 여백을 줄인다.</summary>
    private static SelectableTextBlock Heading(MarkdownHeading heading)
    {
        if (heading.Level == 1)
        {
            return TextOf(heading.Spans, 14.5, FontWeight.Bold);
        }

        var text = SmallText(heading.Spans, FontWeight.Bold);
        text.Opacity = MutedOpacity;
        text.Margin = new Thickness(0, 0, 0, -8);
        return text;
    }

    /// <summary><c>&gt;</c> 인용 = 주의 상자: 노랑 바탕 · 노랑 테두리 · 경고 아이콘 · 한 줄 글(시안 「적용 전 확인」).</summary>
    private static Border Caution(MarkdownQuote quote)
    {
        var stack = new StackPanel { Spacing = 4 };
        foreach (var block in quote.Blocks)
        {
            stack.Children.Add(block is MarkdownParagraph p ? SmallText(p.Spans) : Render(block));
        }

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 8 };
        if (ShellIcons.Create("warn", 14) is { } icon)
        {
            icon.VerticalAlignment = VerticalAlignment.Top;
            icon.Margin = new Thickness(0, 2, 0, 0);
            grid.Children.Add(icon);
        }

        Grid.SetColumn(stack, 1);
        grid.Children.Add(stack);
        var border = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 8), Child = grid, Classes = { "caution" } };
        border.Bind(Border.BackgroundProperty, border.GetResourceObservable("OrandaDiffChangedBrush"));
        border.Bind(Border.BorderBrushProperty, border.GetResourceObservable("OrandaIconAmberBrush"));
        return border;
    }

    private static Border Rule()
    {
        var line = new Border { Height = 1, Margin = new Thickness(0, 4) };
        line.Bind(Border.BackgroundProperty, line.GetResourceObservable("OrandaSeparatorBrush"));
        return line;
    }

    private static bool IsSql(string language) =>
        language.Trim().ToUpperInvariant() is "SQL" or "PLSQL" or "PL/SQL" or "ORACLE";

    /// <summary>코드 블록: 머리 줄(언어 이름·「복사」 글 단추)과 가로로 스크롤되는 본문. SQL 은 닫힌 블록만 색을 칠한다.</summary>
    private static Border CodeBlock(MarkdownCode code)
    {
        var text = new SelectableTextBlock
        {
            TextWrapping = TextWrapping.NoWrap,
            Margin = new Thickness(10, 8, 10, 10),
            LineSpacing = 2,
        };
        if (code.Closed && IsSql(code.Language))
        {
            SqlInlineText.SetSql(text, code.Code);
        }
        else
        {
            text.Text = code.Code;
        }

        text.Bind(TextBlock.FontFamilyProperty, text.GetResourceObservable("OrandaCodeFontFamily"));
        text.Bind(TextBlock.FontSizeProperty, text.GetResourceObservable("OrandaFontSizeSmall"));

        var scroll = new ScrollViewer
        {
            Content = text,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, // 세로 휠은 대화 전체가 받는다
        };

        var copy = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Content = "복사",
            Padding = new Thickness(8, 2),
            Margin = new Thickness(0, 0, 2, 0),
        };
        copy.Classes.Add("toolbar");
        copy.Bind(TemplatedControl.ForegroundProperty, copy.GetResourceObservable("OrandaIconBlueBrush"));
        copy.Bind(TemplatedControl.FontSizeProperty, copy.GetResourceObservable("OrandaFontSizeSmall"));
        ToolTip.SetTip(copy, "코드를 복사합니다"); // 고정 문구라야 영어 사전(literals-en.json)이 번역한다
        copy.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(copy)?.Clipboard is { } clipboard)
            {
                await clipboard.SetTextAsync(code.Code).ConfigureAwait(true);
            }
        };

        var language = new TextBlock { Text = code.Language.Trim().ToLowerInvariant(), VerticalAlignment = VerticalAlignment.Center, Opacity = MutedOpacity, Margin = new Thickness(10, 0, 0, 0) };
        language.Bind(TextBlock.FontSizeProperty, language.GetResourceObservable("OrandaFontSizeSmall"));
        var bar = new Border { BorderThickness = new Thickness(0, 0, 0, 1), MinHeight = 26, Child = new Grid { Children = { language, copy } } };
        bar.Bind(Border.BorderBrushProperty, bar.GetResourceObservable("OrandaSeparatorBrush"));
        bar.Bind(Border.BackgroundProperty, bar.GetResourceObservable("OrandaToolbarBackgroundBrush"));
        DockPanel.SetDock(bar, Avalonia.Controls.Dock.Top);

        var border = new Border { CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), ClipToBounds = true, Child = new DockPanel { Children = { bar, scroll } } };
        border.Bind(Border.BackgroundProperty, border.GetResourceObservable("OrandaCodeBackgroundBrush"));
        border.Bind(Border.BorderBrushProperty, border.GetResourceObservable("OrandaSeparatorBrush"));
        return border;
    }

    /// <summary>블릿 목록: 항목 사이 2px.</summary>
    private static StackPanel List(MarkdownList list)
    {
        var stack = new StackPanel { Spacing = 2 };
        foreach (var item in list.Items)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 6, Margin = new Thickness(2 + (item.Depth * 18), 0, 0, 0) };
            row.Children.Add(new TextBlock { Text = item.Marker, MinWidth = 12, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Opacity = item.Marker == "•" ? 0.6 : 0.85 });
            var content = TextOf(item.Spans);
            Grid.SetColumn(content, 1);
            row.Children.Add(content);
            stack.Children.Add(row);
        }

        return stack;
    }

    /// <summary>결론 상자: 옅은 파랑 머리(「결론」 · 굵은 줄 · 부제 줄) 아래에 수치 표를 붙인 테두리 하나.</summary>
    private static Border Conclusion(MarkdownParagraph paragraph, MarkdownTable table)
    {
        var lines = Lines(paragraph.Spans);
        var head = new StackPanel { Spacing = 2 };
        var label = new TextBlock { Text = "결론", FontWeight = FontWeight.Bold };
        label.Bind(TextBlock.FontSizeProperty, label.GetResourceObservable("OrandaFontSizeSmall"));
        label.Bind(TextBlock.ForegroundProperty, label.GetResourceObservable("OrandaIconBlueBrush"));
        head.Children.Add(label);
        head.Children.Add(TextOf(lines[0], 14.5, FontWeight.Bold));
        foreach (var line in lines.Skip(1).Where(l => l.Count > 0))
        {
            var sub = SmallText(line);
            sub.Opacity = 0.8;
            head.Children.Add(sub);
        }

        var headBorder = new Border { Padding = new Thickness(12, 8), BorderThickness = new Thickness(0, 0, 0, 1), Child = head };
        headBorder.Bind(Border.BackgroundProperty, headBorder.GetResourceObservable("OrandaGridCurrentRowBrush"));
        headBorder.Bind(Border.BorderBrushProperty, headBorder.GetResourceObservable("OrandaSeparatorBrush"));
        DockPanel.SetDock(headBorder, Avalonia.Controls.Dock.Top);

        var grid = TableGrid(table, padding: 10, stretch: true);
        var box = new Border
        {
            CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), ClipToBounds = true, Classes = { "conclusion" },
            Child = new DockPanel { Children = { headBorder, Scroll(grid, grid, inset: 0) } },
        };
        box.Bind(Border.BorderBrushProperty, box.GetResourceObservable("OrandaSeparatorBrush"));
        return box;
    }

    /// <summary>표: 테두리 하나 · 머리 회색 · 줄무늬 · 숫자 칸 고정폭 오른쪽 정렬. 대화 폭에 맞추고, 그래도 넓으면 가로로 스크롤된다.</summary>
    private static MarkdownTableScroll Table(MarkdownTable table)
    {
        var grid = TableGrid(table, padding: 8, stretch: false);
        var border = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), ClipToBounds = true, Child = grid, HorizontalAlignment = HorizontalAlignment.Left };
        border.Bind(Border.BorderBrushProperty, border.GetResourceObservable("OrandaSeparatorBrush"));
        return Scroll(border, grid, inset: 2);
    }

    /// <summary>표를 감싸는 가로 스크롤 뷰. 보이는 폭(테두리 <paramref name="inset"/> 뺀 값)을 표 판에 넘겨 칸 폭을 맞추게 한다.</summary>
    private static MarkdownTableScroll Scroll(Control content, MarkdownTablePanel table, double inset) => new(table, inset)
    {
        Content = content,
        HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
    };

    /// <summary>표 칸 판: 숫자 칸은 한 줄(줄바꿈 없음), 글 칸은 좁으면 줄바꿈된다(<see cref="MarkdownTablePanel"/>).</summary>
    private static MarkdownTablePanel TableGrid(MarkdownTable table, double padding, bool stretch)
    {
        var columns = table.Aligns.Count;
        var numeric = new bool[columns];
        for (var c = 0; c < columns; c++)
        {
            var cells = table.Rows.Select(r => c < r.Count ? MarkdownParser.ToPlainText(r[c]).Trim() : string.Empty).Where(t => t.Length > 0 && t is not "-" and not "—").ToList();
            numeric[c] = cells.Count > 0 && cells.All(IsNumeric);
        }

        // 결론 상자 안의 표는 상자 폭을 채운다(첫 칸이 남는 폭을 받는다).
        var grid = new MarkdownTablePanel(columns, numeric.Select(n => !n).ToArray(), stretchFirst: stretch);

        var rowIndex = 0;
        AddRow(grid, table.Header, table.Aligns, numeric, rowIndex++, header: true, padding);
        foreach (var row in table.Rows)
        {
            AddRow(grid, row, table.Aligns, numeric, rowIndex++, header: false, padding);
        }

        return grid;
    }

    private static void AddRow(MarkdownTablePanel grid, IReadOnlyList<IReadOnlyList<MarkdownSpan>> cells, IReadOnlyList<MarkdownAlign> aligns, bool[] numeric, int rowIndex, bool header, double padding)
    {
        for (var c = 0; c < aligns.Count; c++)
        {
            var spans = c < cells.Count ? cells[c] : [];
            var text = header ? SmallText(spans, FontWeight.SemiBold) : TextOf(spans);
            text.MaxWidth = CellMaxWidth;
            if (numeric[c])
            {
                text.TextWrapping = TextWrapping.NoWrap; // 숫자는 한 줄로(칸이 좁으면 글 칸이 줄거나 표가 스크롤된다)
            }

            text.TextAlignment = aligns[c] switch
            {
                MarkdownAlign.Center => TextAlignment.Center,
                MarkdownAlign.Right => TextAlignment.Right,
                _ => numeric[c] ? TextAlignment.Right : TextAlignment.Left,
            };
            if (header)
            {
                text.Opacity = MutedOpacity;
            }
            else if (numeric[c])
            {
                text.Bind(TextBlock.FontFamilyProperty, text.GetResourceObservable("OrandaCodeFontFamily"));
            }

            var cell = new Border { Padding = new Thickness(padding, header ? 5 : 4), Child = text };
            if (header)
            {
                cell.BorderThickness = new Thickness(0, 0, 0, 1);
                cell.Bind(Border.BorderBrushProperty, cell.GetResourceObservable("OrandaSeparatorBrush"));
                cell.Bind(Border.BackgroundProperty, cell.GetResourceObservable("OrandaToolbarBackgroundBrush"));
            }
            else if (rowIndex % 2 == 0)
            {
                cell.Bind(Border.BackgroundProperty, cell.GetResourceObservable("OrandaRowAltBrush")); // 줄무늬(머리글 다음 둘째 줄부터)
            }

            grid.Children.Add(cell);
        }
    }
}
