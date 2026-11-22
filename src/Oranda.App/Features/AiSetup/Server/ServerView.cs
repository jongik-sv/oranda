using System;
using System.Linq;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Oranda.Core.Ai.Setup;

namespace Oranda.App.Features.AiSetup.Server;

/// <summary>
/// 「회사 AI 서버」 화면. 파일 길 = 큰 끌어다 놓기 자리 + 「파일 고르기」 + 읽은 값 요약. 주소 길 = 주소 칸 + 「모델 새로고침」 + 모델 목록.
/// 두 길 모두 열쇠 칸(가림 글자)과 「연결하기」. 로컬 AI·형식·토큰 칸은 없다.
/// </summary>
internal sealed class ServerView : UserControl
{
    private readonly ServerStepViewModel _vm;
    private readonly TextBox _key;

    public ServerView(ServerStepViewModel vm)
    {
        ArgumentNullException.ThrowIfNull(vm);
        _vm = vm;
        DataContext = vm;

        var title = Bound(new TextBlock { FontWeight = FontWeight.SemiBold, FontSize = 16, TextWrapping = TextWrapping.Wrap }, nameof(vm.Title));
        var intro = Bound(new TextBlock { TextWrapping = TextWrapping.Wrap }, nameof(vm.Intro));

        // 파일 길
        var dropText = Bound(new TextBlock { TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center }, nameof(vm.DropText));
        var pick = LabelButton("ServerPickFile", nameof(vm.PickLabel));
        pick.HorizontalAlignment = HorizontalAlignment.Center;
        pick.Click += async (_, _) => await PickFileAsync().ConfigureAwait(true);
        var drop = new Border
        {
            Name = "ServerDropZone",
            MinHeight = 110,
            Padding = new Thickness(16),
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(6),
            Background = Brushes.Transparent,
            Child = new StackPanel { Spacing = 10, VerticalAlignment = VerticalAlignment.Center, Children = { dropText, pick } },
        };
        drop.Bind(Border.BorderBrushProperty, drop.GetResourceObservable("OrandaSeparatorBrush"));
        DragDrop.SetAllowDrop(drop, true);
        drop.AddHandler(DragDrop.DragOverEvent, OnDragOver);
        drop.AddHandler(DragDrop.DropEvent, OnDrop);

        var summary = Bound(new SelectableTextBlock { Name = "ServerFileSummary", TextWrapping = TextWrapping.Wrap }, nameof(vm.FileSummary));
        summary.Bind(IsVisibleProperty, new Binding(nameof(vm.FileLoaded)));
        var addressOnly = LabelButton("ServerAddressOnly", nameof(vm.AddressOnlyLabel));
        addressOnly.Classes.Add("link");
        addressOnly.Click += (_, _) => vm.UseAddressOnly();
        var filePanel = new StackPanel { Spacing = 10, Children = { drop, summary, addressOnly } };
        filePanel.Bind(IsVisibleProperty, new Binding(nameof(vm.IsFileMode)));

        // 주소 길
        var addressLabel = Bound(new TextBlock { TextWrapping = TextWrapping.Wrap }, nameof(vm.AddressLabel));
        var address = new TextBox { Name = "ServerAddress" };
        address.Bind(TextBox.TextProperty, new Binding(nameof(vm.Address)) { Mode = BindingMode.TwoWay });
        address.Bind(TextBox.PlaceholderTextProperty, new Binding(nameof(vm.AddressPlaceholder)));
        address.Bind(Avalonia.Automation.AutomationProperties.NameProperty, new Binding(nameof(vm.AddressLabel)));
        var refresh = LabelButton("ServerRefreshModels", nameof(vm.RefreshLabel));
        refresh.Click += async (_, _) => await vm.RefreshModelsAsync().ConfigureAwait(true);
        var models = new ComboBox { Name = "ServerModels", MinWidth = 200, HorizontalAlignment = HorizontalAlignment.Stretch };
        models.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(vm.Models)));
        models.Bind(Avalonia.Controls.Primitives.SelectingItemsControl.SelectedItemProperty, new Binding(nameof(vm.SelectedModel)) { Mode = BindingMode.TwoWay });
        models.Bind(Avalonia.Automation.AutomationProperties.NameProperty, new Binding(nameof(vm.ModelLabel)));
        var modelRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        modelRow.Children.Add(models);
        Grid.SetColumn(refresh, 1);
        modelRow.Children.Add(refresh);
        var backToFile = LabelButton("ServerBackToFile", nameof(vm.BackToFileLabel));
        backToFile.Classes.Add("link");
        backToFile.Click += (_, _) => vm.UseFile();
        var addressPanel = new StackPanel { Spacing = 6, Children = { addressLabel, address, Bound(new TextBlock { TextWrapping = TextWrapping.Wrap }, nameof(vm.ModelLabel)), modelRow, backToFile } };
        addressPanel.Bind(IsVisibleProperty, new Binding(nameof(vm.IsAddressMode)));

        var warning = Bound(new TextBlock { Name = "ServerPlainTextWarning", TextWrapping = TextWrapping.Wrap }, nameof(vm.PlainTextWarning));
        warning.Bind(IsVisibleProperty, new Binding(nameof(vm.ShowPlainTextWarning)));
        warning.Bind(TextBlock.ForegroundProperty, warning.GetResourceObservable("OrandaWarningForegroundBrush"));

        // 열쇠 + 연결하기
        var keyLabel = Bound(new TextBlock { TextWrapping = TextWrapping.Wrap }, nameof(vm.KeyLabel));
        _key = new TextBox { Name = "ServerKey", PasswordChar = '•' };
        _key.Bind(Avalonia.Automation.AutomationProperties.NameProperty, new Binding(nameof(vm.KeyLabel)));
        vm.KeyProvider = () => _key.Text;
        vm.KeyConsumed += OnKeyConsumed;
        var connect = LabelButton("ServerConnect", nameof(vm.ConnectLabel));
        connect.Classes.Add("accent");
        connect.HorizontalAlignment = HorizontalAlignment.Left;
        connect.Bind(IsEnabledProperty, new Binding(nameof(vm.CanConnect)));
        connect.Click += async (_, _) => await vm.ConnectAsync().ConfigureAwait(true);

        var status = Bound(new TextBlock { Name = "ServerStatus", TextWrapping = TextWrapping.Wrap }, nameof(vm.StatusText));
        var error = Bound(new TextBlock { Name = "ServerError", TextWrapping = TextWrapping.Wrap }, nameof(vm.ErrorText));
        error.Bind(IsVisibleProperty, new Binding(nameof(vm.ErrorText)) { Converter = StringConverters.IsNotNullOrEmpty });
        error.Bind(TextBlock.ForegroundProperty, error.GetResourceObservable("OrandaWarningForegroundBrush"));

        Content = new StackPanel
        {
            Spacing = 12,
            Children = { title, intro, filePanel, addressPanel, warning, new StackPanel { Spacing = 4, Children = { keyLabel, _key } }, connect, status, error },
        };
    }

    private void OnKeyConsumed(object? sender, EventArgs e) => _key.Text = string.Empty;

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

    private static void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.TryGetFiles() is { Length: > 0 } ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        if (e.DataTransfer.TryGetFiles()?.Select(f => f.TryGetLocalPath()).FirstOrDefault(p => !string.IsNullOrWhiteSpace(p)) is { } path)
        {
            await _vm.LoadFileAsync(path, CancellationToken.None).ConfigureAwait(true);
        }
    }

    private async System.Threading.Tasks.Task PickFileAsync()
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
        {
            return;
        }

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = _vm.PickLabel,
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Oranda AI") { Patterns = ["*" + AiSetupFile.Extension] }, FilePickerFileTypes.All],
        }).ConfigureAwait(true);
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
        {
            await _vm.LoadFileAsync(path, CancellationToken.None).ConfigureAwait(true);
        }
    }
}
