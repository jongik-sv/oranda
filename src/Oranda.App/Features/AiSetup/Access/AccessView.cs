using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;

namespace Oranda.App.Features.AiSetup.Access;

/// <summary>
/// 도우미 「AI 를 쓸 접속」 단계 화면: 제목·처음 값 안내·<see cref="AccessTableView"/>(설정 「AI」 탭 ①과 같은 컨트롤)·같은 표라는 파란 띠·건너뜀 안내.
/// </summary>
internal sealed class AccessView : UserControl
{
    public AccessView(AccessStepViewModel vm)
    {
        ArgumentNullException.ThrowIfNull(vm);
        DataContext = vm;

        var title = new TextBlock { FontWeight = FontWeight.SemiBold, FontSize = 16, TextWrapping = TextWrapping.Wrap };
        title.Bind(TextBlock.TextProperty, new Binding(nameof(AccessStepViewModel.Title)));
        var intro = new TextBlock { TextWrapping = TextWrapping.Wrap };
        intro.Bind(TextBlock.TextProperty, new Binding(nameof(AccessStepViewModel.Intro)));

        var skip = new TextBlock { Name = "AccessSkipNotice", TextWrapping = TextWrapping.Wrap };
        skip.Bind(TextBlock.TextProperty, new Binding(nameof(AccessStepViewModel.SkipNoticeText)));
        skip.Bind(IsVisibleProperty, new Binding(nameof(AccessStepViewModel.ShowSkipNotice)));

        Content = new StackPanel { Spacing = 12, Children = { title, intro, new AccessTableView(vm), AccessTableView.Band(nameof(AccessStepViewModel.SharedNote)), skip } };
    }
}

/// <summary>
/// 「AI 가 볼 수 있는 접속」 표(ai-settings-one-place 2026-11-24): 접속마다 이름·환경 표·수준 단추 5개(끔·메타데이터·계획·조회 실행·조회+행 값).
/// 고른 단추는 파랗게 채운다. 운영 줄은 「조회+행 값」 이 점선(고를 수 없음)이고 옆에 「운영은 「조회 실행」까지」, 운영 수준을 직접 올리면 이름 칸이 나온다.
/// 표 아래 한 줄 뜻풀이와 오류 글. 설정 「AI」 탭 ①과 도우미 단계가 이 컨트롤을 함께 쓴다(같은 값·같은 글).
/// </summary>
internal sealed class AccessTableView : UserControl
{
    /// <summary>고른 수준 단추에 붙이는 클래스.</summary>
    internal const string SelectedClass = "ai-access-selected";

    /// <summary>고를 수 없는 수준 단추(운영의 조회+행 값)에 붙이는 클래스.</summary>
    internal const string UnavailableClass = "ai-access-unavailable";

    public AccessTableView(AccessStepViewModel vm)
    {
        ArgumentNullException.ThrowIfNull(vm);
        DataContext = vm;

        var rows = new ItemsControl { Name = "AccessRows", ItemTemplate = new FuncDataTemplate<AccessRow>((row, _) => RowView(row)) };
        rows.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(AccessStepViewModel.Rows)));

        var legend = new TextBlock { Name = "AccessLegend", TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        legend.Classes.Add("dim");
        legend.Bind(TextBlock.TextProperty, new Binding(nameof(AccessStepViewModel.Legend)));

        var none = new TextBlock { TextWrapping = TextWrapping.Wrap };
        none.Bind(TextBlock.TextProperty, new Binding(nameof(AccessStepViewModel.SkipNoticeText)));
        none.Bind(IsVisibleProperty, new Binding(nameof(AccessStepViewModel.HasNoConnections)));

        var error = new TextBlock { Name = "AccessError", TextWrapping = TextWrapping.Wrap };
        error.Bind(TextBlock.TextProperty, new Binding(nameof(AccessStepViewModel.ErrorText)));
        error.Bind(IsVisibleProperty, new Binding(nameof(AccessStepViewModel.ErrorText)) { Converter = Avalonia.Data.Converters.StringConverters.IsNotNullOrEmpty });
        error.Bind(TextBlock.ForegroundProperty, error.GetResourceObservable("OrandaWarningForegroundBrush"));

        Content = new StackPanel { Spacing = 8, Children = { rows, none, legend, error } };
    }

    /// <summary>파란 안내 띠(옅은 파랑 바탕·파랑 테두리). <paramref name="property"/> = 글 속성 이름.</summary>
    internal static Border Band(string property)
    {
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap };
        text.Bind(TextBlock.TextProperty, new Binding(property));
        var band = new Border { Padding = new Thickness(12, 8), CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), Child = text, Classes = { "ai-info-band" } };
        band.Bind(Border.BackgroundProperty, band.GetResourceObservable("OrandaTableSelectionBrush"));
        band.Bind(Border.BorderBrushProperty, band.GetResourceObservable("OrandaButtonAccentBrush"));
        return band;
    }

    private static Border RowView(AccessRow row)
    {
        var name = new TextBlock { Text = row.Name, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        ToolTip.SetTip(name, row.Name);

        var envText = new TextBlock { FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        envText.Bind(TextBlock.TextProperty, new Binding(nameof(AccessRow.EnvironmentLabel)));
        var env = new Border { Padding = new Thickness(6, 1), CornerRadius = new CornerRadius(3), VerticalAlignment = VerticalAlignment.Center, Child = envText };
        if (row.IsProduction)
        {
            env.Bind(Border.BackgroundProperty, env.GetResourceObservable("OrandaEnvProductionBrush"));
            envText.Bind(TextBlock.ForegroundProperty, envText.GetResourceObservable("OrandaEnvProductionForegroundBrush"));
        }
        else
        {
            env.BorderThickness = new Thickness(1);
            env.Bind(Border.BorderBrushProperty, env.GetResourceObservable("OrandaSeparatorBrush"));
        }

        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 8 };
        head.Children.Add(env);
        Grid.SetColumn(name, 1);
        head.Children.Add(name);

        // 단추와 운영 한도 글을 한 줄에 두되, 좁으면 다음 줄로 넘긴다(좁은 도우미 창에서도 잘리지 않게).
        var buttonLine = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 4, LineSpacing = 4 };
        foreach (var level in row.Buttons)
        {
            buttonLine.Children.Add(LevelButton(level));
        }

        var limit = new TextBlock { Text = row.LimitNote, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, IsVisible = row.IsProduction, Margin = new Thickness(6, 0, 0, 0), TextWrapping = TextWrapping.Wrap };
        limit.Classes.Add("dim");
        limit.Bind(TextBlock.TextProperty, new Binding(nameof(AccessRow.LimitNote)));
        buttonLine.Children.Add(limit);

        var description = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        description.Classes.Add("dim");
        description.Bind(TextBlock.TextProperty, new Binding(nameof(AccessRow.Description)));

        var confirm = new TextBox { Name = "AccessConfirmName", MaxWidth = 320, HorizontalAlignment = HorizontalAlignment.Left };
        confirm.Bind(TextBox.TextProperty, new Binding(nameof(AccessRow.ConfirmName)) { Mode = BindingMode.TwoWay });
        confirm.Bind(TextBox.PlaceholderTextProperty, new Binding(nameof(AccessRow.ConfirmPlaceholder)));
        confirm.Bind(IsVisibleProperty, new Binding(nameof(AccessRow.NeedsName)));
        confirm.Bind(Avalonia.Automation.AutomationProperties.NameProperty, new Binding(nameof(AccessRow.ConfirmPlaceholder)));

        var frame = new Border
        {
            DataContext = row,
            Padding = new Thickness(10, 6),
            Margin = new Thickness(0, 0, 0, 6),
            BorderThickness = row.IsProduction ? new Thickness(3, 1, 1, 1) : new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Child = new StackPanel { Spacing = 4, Children = { head, buttonLine, description, confirm } },
            Classes = { row.IsProduction ? "ai-setup-production" : "ai-setup-connection" },
        };
        frame.Bind(Border.BorderBrushProperty, frame.GetResourceObservable(row.IsProduction ? "OrandaEnvProductionBrush" : "OrandaSeparatorBrush"));
        return frame;
    }

    private static Button LevelButton(AccessLevelButton level)
    {
        var button = new Button
        {
            DataContext = level,
            Padding = new Thickness(10, 3),
            MinWidth = 0,
            FontSize = 12,
            IsEnabled = level.IsEnabled,
            Command = level.Row.ChooseCommand,
            CommandParameter = level.Level,
        };
        button.Bind(ContentProperty, new Binding(nameof(AccessLevelButton.Label)));
        button.Bind(Avalonia.Automation.AutomationProperties.NameProperty, new Binding(nameof(AccessLevelButton.AutomationName)));
        if (!level.IsEnabled)
        {
            // 고를 수 없는 수준: 점선 테두리(운영의 조회+행 값).
            button.Classes.Add(UnavailableClass);
            button.Background = Brushes.Transparent;
            button.BorderThickness = new Thickness(1);
            button.Bind(BorderBrushProperty, button.GetResourceObservable("OrandaSeparatorBrush"));
        }

        void Mark()
        {
            button.Classes.Set(SelectedClass, level.IsSelected);
            button.Classes.Set("accent", level.IsSelected);
        }

        Mark();
        level.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AccessLevelButton.IsSelected))
            {
                Mark();
            }
        };
        return button;
    }
}
