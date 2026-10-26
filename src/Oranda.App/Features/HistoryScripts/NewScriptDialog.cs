using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Oranda.App.Features.Localization;
using Oranda.App.Shell.Controls;

namespace Oranda.App.Features.HistoryScripts;

/// <summary>「새 스크립트」 창이 부르는 일감: 어느 폴더에, 어떤 이름으로 시작할지, 만들기를 어떻게 하는지.</summary>
/// <param name="Folder">만들 폴더.</param>
/// <param name="SuggestedName">처음 보일 이름.</param>
/// <param name="CanFill">활성 편집기에 글이 있어 「내용으로 채우기」를 켤 수 있는지.</param>
/// <param name="Create">이름과 채우기 여부로 파일을 만든다(창이 「만들기」에서 부른다).</param>
public sealed record NewScriptRequest(string Folder, string SuggestedName, bool CanFill, Func<string, bool, NewScriptResult> Create);

/// <summary>
/// 새 스크립트 이름 입력 창(코드로 만든 작은 대화상자: 다른 입력 창과 같은 <see cref="DialogShell"/> 틀).
/// 이름은 칠 때마다 검사해 오류를 창 안에 보이고, 「만들기」는 오류가 있으면 눌러지지 않는다.
/// 만들기가 실패하면(그 사이 같은 이름이 생기는 등) 창을 닫지 않고 오류 줄에 보인다. Enter = 만들기, Esc = 취소.
/// </summary>
public sealed class NewScriptDialog : Window
{
    private readonly NewScriptRequest _request;
    private readonly TextBlock _message;

    public NewScriptDialog(NewScriptRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        _request = request;

        Title = UiText.Get("Scripts.new.title", "새 스크립트");
        Width = 460;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = false;
        ShowInTaskbar = false;

        FolderText = new TextBlock
        {
            Text = request.Folder,
            Opacity = 0.6,
            TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(FolderText, request.Folder);
        DialogShell.AutomationName(FolderText, UiText.Get("Scripts.new.folder", "폴더"));

        NameBox = new TextBox { Text = request.SuggestedName, VerticalAlignment = VerticalAlignment.Center };
        DialogShell.AutomationName(NameBox, UiText.Get("Scripts.new.name", "이름"));

        FillBox = new CheckBox
        {
            Content = UiText.Get("Scripts.new.fill", "활성 편집기 내용으로 채우기"),
            IsChecked = false, // 기본 꺼짐
            IsEnabled = request.CanFill,
        };
        if (!request.CanFill)
        {
            ToolTip.SetTip(FillBox, UiText.Get("Scripts.new.fillNone", "편집기에 복사할 글이 없습니다"));
            ToolTip.SetShowOnDisabled(FillBox, true);
        }

        // 오류(경고색)와 안내(흐린 글)가 같은 줄을 쓴다. 줄 높이를 잡아 두어 창이 오르내리지 않게 한다.
        _message = new TextBlock { MinHeight = 18, TextWrapping = Avalonia.Media.TextWrapping.Wrap, FontSize = 12 };

        CreateButton = new Button { Content = UiText.Get("Scripts.new.create", "만들기"), IsDefault = true, MinWidth = 80, Classes = { "accent" } };
        CancelButton = new Button { Content = UiText.Get("Shell.dialog.cancel", "취소"), IsCancel = true, MinWidth = 80 };
        CreateButton.Click += (_, _) => TryCreate();
        CancelButton.Click += (_, _) => Close();

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            RowDefinitions = new RowDefinitions("Auto,Auto"),
            RowSpacing = 8,
            ColumnSpacing = 12,
        };
        AddRow(grid, 0, UiText.Get("Scripts.new.folder", "폴더"), FolderText);
        AddRow(grid, 1, UiText.Get("Scripts.new.name", "이름"), NameBox);

        Content = DialogShell.Framed(
            new StackPanel { Spacing = 8, Children = { grid, FillBox, _message } },
            DialogShell.Buttons(CreateButton, CancelButton));

        NameBox.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty)
            {
                Validate();
            }
        };
        Opened += (_, _) =>
        {
            // 확장자 앞까지만 골라 두어 바로 새 이름을 칠 수 있게 한다.
            NameBox.Focus();
            NameBox.SelectionStart = 0;
            NameBox.SelectionEnd = (NameBox.Text ?? string.Empty).Length - Path.GetExtension(NameBox.Text ?? string.Empty).Length;
        };
        Validate();
    }

    public TextBox NameBox { get; }

    public CheckBox FillBox { get; }

    public Button CreateButton { get; }

    public Button CancelButton { get; }

    public TextBlock FolderText { get; }

    /// <summary>창 안 오류·안내 줄의 글.</summary>
    public string MessageText => _message.Text ?? string.Empty;

    /// <summary>오류 줄이 경고색으로 보이는 중인지(안내 줄이면 false).</summary>
    public bool ShowsError { get; private set; }

    /// <summary>만든 파일의 전체 경로(만들지 않고 닫았으면 null).</summary>
    public string? CreatedPath { get; private set; }

    /// <summary>창을 띄워 닫힐 때까지 기다린 뒤 만든 파일의 경로(취소면 null)를 돌려준다.</summary>
    public static async Task<string?> ShowAsync(NewScriptRequest request, Window? owner)
    {
        var dialog = new NewScriptDialog(request);
        await DialogShell.ShowAsync(dialog, owner ?? DialogShell.MainWindow).ConfigureAwait(true);
        return dialog.CreatedPath;
    }

    private static void AddRow(Grid grid, int row, string label, Control field)
    {
        var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, MinWidth = 36 };
        Grid.SetRow(text, row);
        Grid.SetRow(field, row);
        Grid.SetColumn(field, 1);
        grid.Children.Add(text);
        grid.Children.Add(field);
    }

    /// <summary>이름을 검사해 오류·안내 줄과 「만들기」 사용 여부를 맞춘다.</summary>
    private void Validate()
    {
        var input = NameBox.Text ?? string.Empty;
        var check = NewScriptFile.Check(_request.Folder, input);
        CreateButton.IsEnabled = check.Ok;
        if (check.Ok)
        {
            // 확장자를 붙였으면 실제 만들어질 이름을 흐린 글로 알려 준다.
            var extra = string.Equals(check.FileName, input.Trim(), StringComparison.Ordinal)
                ? string.Empty
                : UiText.Format("Scripts.new.willCreate", "{0} 로 만듭니다", check.FileName);
            ShowMessage(extra, error: false);
        }
        else
        {
            // 이름을 아직 안 쳤을 때는 나무라지 않고 안내만 한다.
            ShowMessage(check.Message, error: check.Error != NewScriptError.EmptyName);
        }
    }

    private void TryCreate()
    {
        if (!CreateButton.IsEnabled)
        {
            return;
        }

        var result = _request.Create(NameBox.Text ?? string.Empty, FillBox.IsEnabled && FillBox.IsChecked == true);
        if (result.Ok)
        {
            CreatedPath = result.Path;
            Close();
            return;
        }

        ShowMessage(result.Message, error: true);
    }

    private void ShowMessage(string text, bool error)
    {
        ShowsError = error && text.Length > 0;
        _message.Text = ShowsError ? "⚠ " + text : text;
        if (ShowsError)
        {
            _message.Bind(TextBlock.ForegroundProperty, _message.GetResourceObservable("OrandaDangerForegroundBrush"));
            _message.Opacity = 1;
        }
        else
        {
            _message.ClearValue(TextBlock.ForegroundProperty);
            _message.Opacity = 0.6;
        }
    }
}
