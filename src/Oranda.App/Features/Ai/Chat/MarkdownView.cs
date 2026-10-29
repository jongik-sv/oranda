using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Oranda.App.Shell.Icons;

namespace Oranda.App.Features.Ai.Chat;

/// <summary>
/// AI 글을 Markdown 부분 집합으로 그리는 컨트롤(<see cref="MarkdownParser"/> 가 읽은 블록을 그대로 옮긴다).
/// 글은 <see cref="SelectableTextBlock"/> 이라 마우스로 고르고 복사할 수 있다. 색은 기존 리소스 키를 동적으로 묶어 테마를 따른다.
/// <see cref="Markdown"/> 이 바뀌면 통째로 다시 그린다(스트리밍으로 글이 늘 때마다 대화 창이 새 글을 넘겨 준다).
/// 그리다 예외가 나면 일반 글 한 덩어리로 보인다.
/// </summary>
public sealed class MarkdownView : StackPanel
{
    public static readonly StyledProperty<string?> MarkdownProperty = AvaloniaProperty.Register<MarkdownView, string?>(nameof(Markdown));

    private const double CellMaxWidth = 320;

    static MarkdownView()
    {
        MarkdownProperty.Changed.AddClassHandler<MarkdownView>((view, _) => view.Rebuild());
    }

    public MarkdownView()
    {
        Spacing = 6;
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
            foreach (var block in MarkdownParser.Parse(markdown))
            {
                Children.Add(Render(block));
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

    private static Control Render(MarkdownBlock block) => block switch
    {
        MarkdownHeading heading => Heading(heading),
        MarkdownCode code => CodeBlock(code),
        MarkdownList list => List(list),
        MarkdownTable table => Table(table),
        MarkdownParagraph paragraph => TextOf(paragraph.Spans),
        _ => new SelectableTextBlock { TextWrapping = TextWrapping.Wrap },
    };

    private static SelectableTextBlock TextOf(IReadOnlyList<MarkdownSpan> spans, double? fontSize = null, FontWeight? weight = null)
    {
        var block = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap };
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
                run.Bind(TextElement.FontFamilyProperty, block.GetResourceObservable("OrandaCodeFontFamily"));
                run.Bind(TextElement.BackgroundProperty, block.GetResourceObservable("OrandaCodeBackgroundBrush"));
            }

            block.Inlines ??= [];
            block.Inlines.Add(run);
        }

        return block;
    }

    private static SelectableTextBlock Heading(MarkdownHeading heading) =>
        TextOf(heading.Spans, heading.Level switch { 1 => 18, 2 => 16, _ => 15 }, FontWeight.Bold);

    private static Border CodeBlock(MarkdownCode code)
    {
        var text = new SelectableTextBlock
        {
            Text = code.Code,
            TextWrapping = TextWrapping.NoWrap,
            Margin = new Thickness(10, 8, 34, 8),
        };
        text.Bind(TextBlock.FontFamilyProperty, text.GetResourceObservable("OrandaCodeFontFamily"));
        text.Bind(TextBlock.FontSizeProperty, text.GetResourceObservable("OrandaEditorFontSize"));

        var scroll = new ScrollViewer
        {
            Content = text,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, // 세로 휠은 대화 전체가 받는다
        };

        var copy = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 2, 2, 0),
            Opacity = 0.75,
            Content = "복사",
        };
        copy.Classes.Add("toolbar");
        ButtonIcon.SetKey(copy, "copy");
        ButtonIcon.SetIconOnly(copy, true);
        ToolTip.SetTip(copy, "코드를 복사합니다"); // 고정 문구라야 영어 사전(literals-en.json)이 번역한다
        copy.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(copy)?.Clipboard is { } clipboard)
            {
                await clipboard.SetTextAsync(code.Code).ConfigureAwait(true);
            }
        };

        var host = new Grid { Children = { scroll, copy } };
        var border = new Border { CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1), Child = host };
        border.Bind(Border.BackgroundProperty, border.GetResourceObservable("OrandaCodeBackgroundBrush"));
        border.Bind(Border.BorderBrushProperty, border.GetResourceObservable("OrandaSeparatorBrush"));
        return border;
    }

    private static StackPanel List(MarkdownList list)
    {
        var stack = new StackPanel { Spacing = 3 };
        foreach (var item in list.Items)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 6, Margin = new Thickness(item.Depth * 16, 0, 0, 0) };
            row.Children.Add(new TextBlock { Text = item.Marker, MinWidth = 14, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Top });
            var content = TextOf(item.Spans);
            Grid.SetColumn(content, 1);
            row.Children.Add(content);
            stack.Children.Add(row);
        }

        return stack;
    }

    private static ScrollViewer Table(MarkdownTable table)
    {
        var columns = table.Aligns.Count;
        var grid = new Grid();
        for (var c = 0; c < columns; c++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        }

        var rowIndex = 0;
        AddRow(grid, table.Header, table.Aligns, rowIndex++, header: true);
        foreach (var row in table.Rows)
        {
            AddRow(grid, row, table.Aligns, rowIndex++, header: false);
        }

        var border = new Border { BorderThickness = new Thickness(1, 1, 0, 0), Child = grid, HorizontalAlignment = HorizontalAlignment.Left };
        border.Bind(Border.BorderBrushProperty, border.GetResourceObservable("OrandaSeparatorBrush"));
        return new ScrollViewer
        {
            Content = border,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        };
    }

    private static void AddRow(Grid grid, IReadOnlyList<IReadOnlyList<MarkdownSpan>> cells, IReadOnlyList<MarkdownAlign> aligns, int rowIndex, bool header)
    {
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        for (var c = 0; c < aligns.Count; c++)
        {
            var text = TextOf(c < cells.Count ? cells[c] : [], weight: header ? FontWeight.SemiBold : null);
            text.MaxWidth = CellMaxWidth;
            text.TextAlignment = aligns[c] switch { MarkdownAlign.Center => TextAlignment.Center, MarkdownAlign.Right => TextAlignment.Right, _ => TextAlignment.Left };
            var cell = new Border { BorderThickness = new Thickness(0, 0, 1, 1), Padding = new Thickness(6, 3), Child = text };
            cell.Bind(Border.BorderBrushProperty, cell.GetResourceObservable("OrandaSeparatorBrush"));
            if (header)
            {
                cell.Bind(Border.BackgroundProperty, cell.GetResourceObservable("OrandaPanelHeaderBrush"));
            }

            Grid.SetRow(cell, rowIndex);
            Grid.SetColumn(cell, c);
            grid.Children.Add(cell);
        }
    }
}
