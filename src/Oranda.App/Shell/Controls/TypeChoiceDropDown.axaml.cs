using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Oranda.App.Shell.Controls;

/// <summary>
/// 객체 유형 체크 드롭다운. DataContext 는 <see cref="TypeChoiceSet"/>. 열면 「전체 선택」 에 포커스가 가고,
/// Space 로 체크(체크 상자 기본), ↑↓·Tab 으로 옮기고, Esc 로 닫으면 포커스가 단추로 돌아온다.
/// </summary>
public partial class TypeChoiceDropDown : UserControl
{
    public TypeChoiceDropDown()
    {
        InitializeComponent();
        DropFlyout.Opened += (_, _) => Dispatcher.UIThread.Post(() => AllBox.Focus(NavigationMethod.Tab));
        List.AddHandler(KeyDownEvent, OnListKeyDown, RoutingStrategies.Tunnel);
    }

    /// <summary>펼침 단추.</summary>
    internal Button DropButton => Toggle;

    /// <summary>펼친 목록(시험이 체크 상자를 찾는다).</summary>
    internal Panel ListPanel => List;

    internal Flyout DropFlyout => (Flyout)Toggle.Flyout!;

    public bool IsDropDownOpen => DropFlyout.IsOpen;

    public void Open() => DropFlyout.ShowAt(Toggle);

    public void Close()
    {
        DropFlyout.Hide();
        Toggle.Focus();
    }

    /// <summary>목록의 체크 상자(「전체 선택」 먼저, 그다음 유형 순서).</summary>
    internal IReadOnlyList<CheckBox> CheckBoxes => [.. List.GetVisualDescendants().OfType<CheckBox>()];

    private void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
            return;
        }

        if (e.Key is not (Key.Down or Key.Up))
        {
            return;
        }

        var boxes = CheckBoxes;
        var at = -1;
        for (var i = 0; i < boxes.Count; i++)
        {
            if (boxes[i].IsFocused)
            {
                at = i;
                break;
            }
        }

        var next = Math.Clamp(at + (e.Key == Key.Down ? 1 : -1), 0, boxes.Count - 1);
        if (boxes.Count > 0)
        {
            e.Handled = true;
            boxes[next].Focus(NavigationMethod.Directional);
            boxes[next].BringIntoView();
        }
    }
}
