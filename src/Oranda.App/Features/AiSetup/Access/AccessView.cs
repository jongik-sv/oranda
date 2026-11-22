using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;

namespace Oranda.App.Features.AiSetup.Access;

/// <summary>
/// 「AI 를 쓸 접속」 화면: 접속마다 한 줄(이름·환경 표·설명·켬/끔 스위치). 운영 줄은 빨간 테두리와 빨간 환경 표(<c>OrandaEnvProductionBrush</c>).
/// 아래에 건너뜀 안내와 오류 글.
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

        var rows = new ItemsControl { Name = "AccessRows", ItemTemplate = new FuncDataTemplate<AccessRow>((row, _) => RowView(row)) };
        rows.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(AccessStepViewModel.Rows)));

        var skip = new TextBlock { Name = "AccessSkipNotice", TextWrapping = TextWrapping.Wrap };
        skip.Bind(TextBlock.TextProperty, new Binding(nameof(AccessStepViewModel.SkipNoticeText)));
        skip.Bind(IsVisibleProperty, new Binding(nameof(AccessStepViewModel.ShowSkipNotice)));

        var error = new TextBlock { Name = "AccessError", TextWrapping = TextWrapping.Wrap };
        error.Bind(TextBlock.TextProperty, new Binding(nameof(AccessStepViewModel.ErrorText)));
        error.Bind(IsVisibleProperty, new Binding(nameof(AccessStepViewModel.ErrorText)) { Converter = Avalonia.Data.Converters.StringConverters.IsNotNullOrEmpty });
        error.Bind(TextBlock.ForegroundProperty, error.GetResourceObservable("OrandaWarningForegroundBrush"));

        Content = new StackPanel { Spacing = 12, Children = { title, intro, rows, skip, error } };
    }

    private static Border RowView(AccessRow row)
    {
        var name = new TextBlock { Text = row.Name, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        Avalonia.Controls.ToolTip.SetTip(name, row.Name);

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

        var description = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        description.Bind(TextBlock.TextProperty, new Binding(nameof(AccessRow.Description)));
        if (row.IsProduction)
        {
            description.Bind(TextBlock.ForegroundProperty, description.GetResourceObservable("OrandaEnvProductionBrush"));
        }

        var toggle = new ToggleSwitch { OnContent = null, OffContent = null, VerticalAlignment = VerticalAlignment.Center };
        toggle.Bind(ToggleButton.IsCheckedProperty, new Binding(nameof(AccessRow.IsOn)) { Mode = BindingMode.TwoWay });
        toggle.Bind(Avalonia.Automation.AutomationProperties.NameProperty, new Binding(nameof(AccessRow.SwitchName)));

        // 환경 표를 앞에 두고 이름은 남는 폭에서 말줄임한다(긴 이름이 좁은 창 밖으로 나가지 않게).
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 8 };
        head.Children.Add(env);
        Grid.SetColumn(name, 1);
        head.Children.Add(name);
        var text = new StackPanel { Spacing = 2, Children = { head, description } };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12 };
        grid.Children.Add(text);
        Grid.SetColumn(toggle, 1);
        grid.Children.Add(toggle);

        var frame = new Border
        {
            DataContext = row,
            Padding = new Thickness(10, 6),
            Margin = new Thickness(0, 0, 0, 6),
            BorderThickness = row.IsProduction ? new Thickness(3, 1, 1, 1) : new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Child = grid,
            Classes = { row.IsProduction ? "ai-setup-production" : "ai-setup-connection" },
        };
        frame.Bind(Border.BorderBrushProperty, frame.GetResourceObservable(row.IsProduction ? "OrandaEnvProductionBrush" : "OrandaSeparatorBrush"));
        return frame;
    }
}
