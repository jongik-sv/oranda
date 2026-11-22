using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Layout;
using Avalonia.Media;

namespace Oranda.App.Features.AiSetup.Wizard;

/// <summary>상태 칸 모양.</summary>
public enum AiSetupStatusKind
{
    /// <summary>아직 안 함(흐린 테두리).</summary>
    Idle,

    /// <summary>기다리는 중.</summary>
    Wait,

    /// <summary>됨.</summary>
    Ok,

    /// <summary>안 됨.</summary>
    Bad,
}

/// <summary>도우미 단계 화면 조각(상태 칸·안내 글·복사 줄). 색은 기존 브러시만 쓴다(운영 빨강은 쓰지 않는다).</summary>
public static class AiSetupUi
{
    /// <summary>상태 칸 클래스(시험이 찾는다).</summary>
    public const string StatusClass = "ai-setup-status";

    private static readonly IValueConverter IconOf = new FuncValueConverter<AiSetupStatusKind, string>(k => k switch
    {
        AiSetupStatusKind.Ok => "✓",
        AiSetupStatusKind.Bad => "!",
        AiSetupStatusKind.Wait => "…",
        _ => "○",
    });

    /// <summary>
    /// 상태 칸: 왼쪽 표시(✓·!·…) + 굵은 제목 + 흐린 설명. 세 값은 DataContext 의 속성 경로로 묶는다.
    /// </summary>
    public static Border Status(string kindPath, string titlePath, string detailPath)
    {
        var icon = new TextBlock { FontWeight = FontWeight.Bold, FontSize = 15, Width = 22, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Top };
        icon.Bind(TextBlock.TextProperty, new Binding(kindPath) { Converter = IconOf });
        var title = new TextBlock { FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
        title.Bind(TextBlock.TextProperty, new Binding(titlePath));
        var detail = Caption(string.Empty);
        detail.Bind(TextBlock.TextProperty, new Binding(detailPath));
        detail.Bind(Visual.IsVisibleProperty, new Binding(detailPath) { Converter = StringConverters.IsNotNullOrEmpty });
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,8,*") };
        var texts = new StackPanel { Spacing = 2, Children = { title, detail } };
        Grid.SetColumn(texts, 2);
        grid.Children.Add(icon);
        grid.Children.Add(texts);
        var box = new Border { Padding = new Thickness(12, 10), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Child = grid };
        box.Classes.Add(StatusClass);
        box.Bind(Border.BorderBrushProperty, box.GetResourceObservable("OrandaSeparatorBrush"));
        box.DataContextChanged += (_, _) => Recolor(box, icon);
        box.PropertyChanged += (_, e) =>
        {
            if (e.Property == Visual.IsVisibleProperty)
            {
                Recolor(box, icon);
            }
        };
        icon.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBlock.TextProperty)
            {
                Recolor(box, icon);
            }
        };
        return box;
    }

    /// <summary>흐린 안내 글(줄바꿈).</summary>
    public static TextBlock Caption(string text)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        block.Classes.Add("panel-caption");
        return block;
    }

    /// <summary>보통 글(줄바꿈).</summary>
    public static TextBlock Text(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap };

    /// <summary>단추(화면 읽기 이름 = 글).</summary>
    public static Button Button(string text, bool accent = false)
    {
        var button = new Button { Content = text, HorizontalAlignment = HorizontalAlignment.Left };
        if (accent)
        {
            button.Classes.Add("accent");
        }

        Avalonia.Automation.AutomationProperties.SetName(button, text);
        return button;
    }

    /// <summary>
    /// 고정폭 한 줄(명령·주소) + 「복사」. 글은 DataContext 의 <paramref name="textPath"/> 로 묶고, 복사는 <paramref name="copy"/> 가 한다.
    /// 긴 글은 줄을 바꿔 창 밖으로 나가지 않는다.
    /// </summary>
    public static Grid CopyLine(string textPath, string copyLabel, Action copy)
    {
        var code = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Consolas, Menlo, monospace"), VerticalAlignment = VerticalAlignment.Center };
        code.Bind(TextBlock.TextProperty, new Binding(textPath));
        var codeBox = new Border { Padding = new Thickness(8, 6), CornerRadius = new CornerRadius(6), Child = code };
        codeBox.Bind(Border.BackgroundProperty, codeBox.GetResourceObservable("OrandaCodeBackgroundBrush"));
        var button = Button(copyLabel);
        button.VerticalAlignment = VerticalAlignment.Center;
        button.Click += (_, _) => copy();
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,8,Auto") };
        Grid.SetColumn(button, 2);
        grid.Children.Add(codeBox);
        grid.Children.Add(button);
        return grid;
    }

    private static void Recolor(Border box, TextBlock icon)
    {
        var key = icon.Text switch
        {
            "✓" => "OrandaSuccessForegroundBrush",
            "!" => "OrandaWarningForegroundBrush", // 운영 빨강과 헷갈리지 않게 경고색
            "…" => "OrandaButtonAccentBrush",
            _ => "OrandaSeparatorBrush",
        };
        icon.Bind(TextBlock.ForegroundProperty, icon.GetResourceObservable(key));
        box.Bind(Border.BorderBrushProperty, box.GetResourceObservable(key));
    }
}
