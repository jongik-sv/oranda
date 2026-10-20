using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Oranda.Core.Connection;

namespace Oranda.App.Features.SessionMonitor;

/// <summary>프로필 고르기 목록의 한 줄. 접속 관리자 저장소의 프로필 그대로(폴더 = <see cref="ConnectionProfile.Group"/>).</summary>
public sealed record MonitorProfileRow(ConnectionProfile Profile)
{
    public string Folder => string.IsNullOrWhiteSpace(Profile.Group) ? "(폴더 없음)" : Profile.Group;

    public string Account => $"{Profile.User}@{Profile.DataSource}";

    public bool IsProduction => Profile.Environment == ConnectionEnvironment.Production;
}

/// <summary>모니터를 열 접속 프로필을 고르는 모달 창(코드로 만든다). 비밀번호 묻기는 열기 단계에서 기존 비밀번호 대화상자가 맡는다.</summary>
public sealed class MonitorProfilePickerWindow : Window
{
    private readonly ListBox _list;

    public MonitorProfilePickerWindow(IReadOnlyList<ConnectionProfile> profiles, Guid? current, string monitorName)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        var rows = profiles
            .OrderBy(p => string.IsNullOrWhiteSpace(p.Group) ? 1 : 0)
            .ThenBy(p => p.Group, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(p => new MonitorProfileRow(p))
            .ToList();
        _list = new ListBox
        {
            ItemsSource = rows,
            MinWidth = 520,
            MaxHeight = 320,
            ItemTemplate = new FuncDataTemplate<MonitorProfileRow>((row, _) => BuildRow(row), supportsRecycling: false),
            SelectedItem = rows.FirstOrDefault(r => r.Profile.Id == current) ?? rows.FirstOrDefault(),
        };
        Avalonia.Automation.AutomationProperties.SetName(_list, "접속 목록");
        var ok = new Button { Content = "열기", MinWidth = 80, IsDefault = true };
        var cancel = new Button { Content = "취소", MinWidth = 80, IsCancel = true };
        ok.Click += (_, _) => Accept();
        cancel.Click += (_, _) => Close();
        _list.DoubleTapped += (_, _) => Accept();
        Title = monitorName + " 접속 고르기";
        SizeToContent = SizeToContent.WidthAndHeight;
        MinWidth = 360;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 10,
            Children =
            {
                new TextBlock
                {
                    Text = monitorName + "에 쓸 접속을 고르세요. 모니터 전용 세션만 새로 열며 작업 접속은 그대로입니다. 저장된 비밀번호가 없으면 다음에 묻습니다.",
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 520,
                },
                _list,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { ok, cancel },
                },
            },
        };
        Opened += (_, _) => _list.Focus();
    }

    /// <summary>고른 프로필. 취소하거나 창을 닫았으면 null.</summary>
    public ConnectionProfile? Picked { get; private set; }

    /// <summary>목록에서 고른 줄을 확정하고 닫는다(시험도 부른다).</summary>
    public void Accept()
    {
        if (_list.SelectedItem is MonitorProfileRow row)
        {
            Picked = row.Profile;
            Close();
        }
    }

    /// <summary>창을 모달로 보이고 고른 프로필을 돌려준다. 소유 창이 안 보이면 독립 창으로 띄운다.</summary>
    public static async Task<ConnectionProfile?> PickAsync(Window owner, IReadOnlyList<ConnectionProfile> profiles, Guid? current, string monitorName)
    {
        ArgumentNullException.ThrowIfNull(owner);
        var window = new MonitorProfilePickerWindow(profiles, current, monitorName);
        if (owner.IsVisible)
        {
            await window.ShowDialog(owner).ConfigureAwait(true);
        }
        else
        {
            var closed = new TaskCompletionSource();
            window.Closed += (_, _) => closed.TrySetResult();
            window.Show();
            await closed.Task.ConfigureAwait(true);
        }

        return window.Picked;
    }

    private static Grid BuildRow(MonitorProfileRow? row)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("120,160,*,Auto"), Margin = new Thickness(2, 3) };
        if (row is null)
        {
            return grid;
        }

        grid.Children.Add(new TextBlock { Text = row.Folder, Opacity = 0.65, TextTrimming = TextTrimming.CharacterEllipsis });
        grid.Children.Add(Cell(new TextBlock { Text = row.Profile.Name, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis }, 1));
        grid.Children.Add(Cell(new TextBlock { Text = row.Account, TextTrimming = TextTrimming.CharacterEllipsis }, 2));
        if (row.IsProduction)
        {
            grid.Children.Add(Cell(
                new Border
                {
                    Background = new SolidColorBrush(Color.Parse("#C42B1C")),
                    CornerRadius = new CornerRadius(3),
                    Padding = new Thickness(6, 0),
                    Margin = new Thickness(8, 0, 0, 0),
                    Child = new TextBlock { Text = "운영", Foreground = Brushes.White },
                },
                3));
        }

        return grid;
    }

    private static Control Cell(Control control, int column)
    {
        Grid.SetColumn(control, column);
        return control;
    }
}
