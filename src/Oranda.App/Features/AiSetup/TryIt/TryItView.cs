using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Layout;
using Avalonia.Media;
using Oranda.Core.Connection;

namespace Oranda.App.Features.AiSetup.TryIt;

/// <summary>
/// 「해 보기」 화면: 제목·안내, (여럿이면) 접속 고르기, 연습 문장 상자, 「연습 탭 열기」·「SQL 설명 받아 보기」, 상태 한 줄,
/// 답 뒤에는 답 머리글·자주 쓰는 3기능·고급 확인란.
/// </summary>
internal sealed class TryItView : UserControl
{
    public TryItView(TryItViewModel vm)
    {
        ArgumentNullException.ThrowIfNull(vm);
        DataContext = vm;

        var title = Bound(new TextBlock { FontWeight = FontWeight.SemiBold, FontSize = 16, TextWrapping = TextWrapping.Wrap }, nameof(vm.Title));
        var intro = Bound(new TextBlock { TextWrapping = TextWrapping.Wrap }, nameof(vm.Intro));

        var pickLabel = Bound(new TextBlock { TextWrapping = TextWrapping.Wrap }, nameof(vm.PickLabel));
        var pick = new ComboBox
        {
            Name = "TryItConnection",
            MinWidth = 200,
            ItemTemplate = new FuncDataTemplate<ConnectionProfile>((p, _) => new TextBlock { Text = p?.Name, TextTrimming = TextTrimming.CharacterEllipsis }),
        };
        pick.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(vm.Connections)));
        pick.Bind(SelectingItemsControl.SelectedItemProperty, new Binding(nameof(vm.SelectedConnection)) { Mode = BindingMode.TwoWay });
        pick.Bind(Avalonia.Automation.AutomationProperties.NameProperty, new Binding(nameof(vm.PickLabel)));
        var pickRow = new StackPanel { Spacing = 4, Children = { pickLabel, pick } };
        pickRow.Bind(IsVisibleProperty, new Binding(nameof(vm.ShowPicker)));

        var sql = new SelectableTextBlock { Text = TryItViewModel.PracticeSql, FontFamily = new FontFamily("Cascadia Mono, Consolas, Menlo, DejaVu Sans Mono, monospace") };
        var sqlBox = new Border { Padding = new Thickness(10, 6), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Child = sql };
        sqlBox.Bind(Border.BackgroundProperty, sqlBox.GetResourceObservable("OrandaCodeBackgroundBrush"));
        sqlBox.Bind(Border.BorderBrushProperty, sqlBox.GetResourceObservable("OrandaSeparatorBrush"));

        var open = LabelButton("TryItOpenTab", nameof(vm.OpenTabLabel));
        open.Bind(IsEnabledProperty, new Binding(nameof(vm.CanOpenTab)));
        open.Click += (_, _) => vm.OpenTab();
        var explain = LabelButton("TryItExplain", nameof(vm.ExplainLabel));
        explain.Classes.Add("accent");
        explain.Bind(IsEnabledProperty, new Binding(nameof(vm.CanExplain)));
        explain.Click += async (_, _) => await vm.ExplainAsync().ConfigureAwait(true);
        var buttons = new WrapPanel { ItemSpacing = 8, LineSpacing = 8, Children = { open, explain } };

        var status = Bound(new TextBlock { Name = "TryItStatus", TextWrapping = TextWrapping.Wrap }, nameof(vm.StatusText));
        status.Bind(IsVisibleProperty, new Binding(nameof(vm.StatusText)) { Converter = StringConverters.IsNotNullOrEmpty });

        var tryPanel = new StackPanel { Spacing = 10, Children = { pickRow, sqlBox, buttons, status } };
        tryPanel.Bind(IsVisibleProperty, new Binding(nameof(vm.IsSkipped)) { Converter = BoolConverters.Not });

        // 답 뒤
        var answer = Bound(new SelectableTextBlock { Name = "TryItAnswer", TextWrapping = TextWrapping.Wrap, MaxLines = 6, TextTrimming = TextTrimming.CharacterEllipsis }, nameof(vm.AnswerText));
        var note = Bound(new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12 }, nameof(vm.AnswerNote));
        var tips = new ItemsControl { Name = "TryItTips", ItemTemplate = new FuncDataTemplate<TryItTip>((tip, _) => TipView(tip)) };
        tips.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(vm.Tips)));
        var advanced = new CheckBox { Name = "TryItAlsoAgent" };
        advanced.Bind(ContentControl.ContentProperty, new Binding(nameof(vm.AlsoExternalAgentLabel)) { Converter = WrapText.Instance });
        advanced.Bind(ToggleButton.IsCheckedProperty, new Binding(nameof(vm.AlsoExternalAgent)) { Mode = BindingMode.TwoWay });
        advanced.Bind(Avalonia.Automation.AutomationProperties.NameProperty, new Binding(nameof(vm.AlsoExternalAgentLabel)));
        var donePanel = new StackPanel { Spacing = 10, Children = { answer, note, tips, advanced } };
        donePanel.Bind(IsVisibleProperty, new Binding(nameof(vm.ShowTips)));

        Content = new StackPanel { Spacing = 12, Children = { title, intro, tryPanel, donePanel } };
    }

    private static Border TipView(TryItTip tip)
    {
        var name = new TextBlock { Text = tip.Title, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
        var where = new TextBlock { Text = tip.Where, TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        var frame = new Border
        {
            Padding = new Thickness(10, 6),
            Margin = new Thickness(0, 0, 0, 6),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Child = new StackPanel { Spacing = 2, Children = { name, where } },
        };
        frame.Bind(Border.BorderBrushProperty, frame.GetResourceObservable("OrandaSeparatorBrush"));
        return frame;
    }

    private static T Bound<T>(T block, string property)
        where T : TextBlock
    {
        block.Bind(TextBlock.TextProperty, new Binding(property));
        return block;
    }

    private static Button LabelButton(string name, string property)
    {
        var label = new TextBlock { TextWrapping = TextWrapping.Wrap }; // 좁은 창에서 단추 글이 잘리지 않게 줄을 바꾼다
        label.Bind(TextBlock.TextProperty, new Binding(property));
        var button = new Button { Name = name, Content = label };
        button.Bind(Avalonia.Automation.AutomationProperties.NameProperty, new Binding(property));
        return button;
    }

    /// <summary>확인란 글이 좁은 폭에서 잘리지 않게 줄바꿈 글 상자로 바꾼다.</summary>
    private sealed class WrapText : IValueConverter
    {
        public static WrapText Instance { get; } = new();

        public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
            new TextBlock { Text = value as string, TextWrapping = TextWrapping.Wrap };

        public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) => throw new NotSupportedException();
    }
}
