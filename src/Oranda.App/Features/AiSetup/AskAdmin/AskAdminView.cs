using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;

namespace Oranda.App.Features.AiSetup.AskAdmin;

/// <summary>「담당자에게 묻기」 화면: 제목·안내·보낼 글(골라 복사 가능)·「복사」·결과 한 줄.</summary>
internal sealed class AskAdminView : UserControl
{
    public AskAdminView(AskAdminViewModel vm)
    {
        ArgumentNullException.ThrowIfNull(vm);
        DataContext = vm;

        var title = new TextBlock { FontWeight = FontWeight.SemiBold, FontSize = 16, TextWrapping = TextWrapping.Wrap };
        title.Bind(TextBlock.TextProperty, new Binding(nameof(AskAdminViewModel.Title)));
        var intro = new TextBlock { TextWrapping = TextWrapping.Wrap };
        intro.Bind(TextBlock.TextProperty, new Binding(nameof(AskAdminViewModel.Intro)));

        var message = new SelectableTextBlock { Name = "AskAdminMessage", TextWrapping = TextWrapping.Wrap };
        message.Bind(TextBlock.TextProperty, new Binding(nameof(AskAdminViewModel.Message)));
        var box = new Border
        {
            Padding = new Thickness(12),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Child = message,
        };
        box.Bind(Border.BorderBrushProperty, box.GetResourceObservable("OrandaSeparatorBrush"));

        var copyLabel = new TextBlock { TextWrapping = TextWrapping.Wrap };
        copyLabel.Bind(TextBlock.TextProperty, new Binding(nameof(AskAdminViewModel.CopyLabel)));
        var copy = new Button { Name = "AskAdminCopy", Content = copyLabel };
        copy.Classes.Add("accent");
        copy.Bind(Avalonia.Automation.AutomationProperties.NameProperty, new Binding(nameof(AskAdminViewModel.CopyLabel)));
        copy.Click += async (_, _) => await vm.CopyAsync().ConfigureAwait(true);

        var status = new TextBlock { Name = "AskAdminStatus", TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        status.Bind(TextBlock.TextProperty, new Binding(nameof(AskAdminViewModel.StatusText)));

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 12 };
        row.Children.Add(copy);
        Grid.SetColumn(status, 1);
        row.Children.Add(status);

        Content = new StackPanel { Spacing = 12, Children = { title, intro, box, row } };
    }
}
