using Avalonia;
using Avalonia.Controls;

namespace Oranda.App.Shell.Settings;

/// <summary>설정 창. 열 때 모든 범주 중 가장 큰 쪽에 맞춘 뒤 크기를 고정하고(범주를 바꾸거나 미리보기가 바뀌어도 창이 출렁이지 않게),
/// 작업 영역보다 크면 본문만 스크롤한다.</summary>
public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is SettingsViewModel vm)
            {
                vm.CloseRequested += (_, _) => Close();
            }
        };
        Opened += (_, _) => FitLargestCategory();
    }

    /// <summary>범주를 하나씩 골라 내용 크기를 재고, 본문 크기를 그중 가장 큰 값으로 고정한다. 창은 내용에 맞추므로 그 뒤로 크기가 바뀌지 않는다.
    /// 창은 열 때 한 번만 커진다. 사용자가 창 크기를 직접 바꾸면 고정을 풀어 본문이 창을 따라간다.</summary>
    private void FitLargestCategory()
    {
        if (Content is not Control root)
        {
            return;
        }

        var start = Categories.SelectedIndex;
        double width = 0, height = 0;
        for (var i = 0; i < Categories.ItemCount; i++)
        {
            Categories.SelectedIndex = i;
            UpdateLayout();
            root.Measure(Size.Infinity);
            width = Math.Max(width, root.DesiredSize.Width - root.Margin.Left - root.Margin.Right);
            height = Math.Max(height, root.DesiredSize.Height - root.Margin.Top - root.Margin.Bottom);
        }

        Categories.SelectedIndex = start;

        // 작업 영역과 주 창보다 크게 열지 않는다(넘치는 범주는 본문만 스크롤).
        var limit = Screens.ScreenFromWindow(this) is { } screen
            ? screen.WorkingArea.Size.ToSize(screen.Scaling)
            : Size.Infinity;
        if (Owner is { } owner)
        {
            limit = new Size(Math.Min(limit.Width, owner.Bounds.Width), Math.Min(limit.Height, owner.Bounds.Height));
        }

        width = Math.Min(width, limit.Width - root.Margin.Left - root.Margin.Right);
        height = Math.Min(height, limit.Height - root.Margin.Top - root.Margin.Bottom);

        root.Width = width;
        root.Height = height;
        var centered = false;
        Resized += (_, e) =>
        {
            if (e.Reason == WindowResizeReason.User)
            {
                root.Width = double.NaN;
                root.Height = double.NaN;
            }
            else if (!centered && Owner is Window owner && owner.FrameSize is { } ownerFrame && FrameSize is { } frame)
            {
                // 커진 만큼 왼쪽 위가 그대로라 주 창 가운데에서 벗어난다: 한 번 다시 가운데로.
                centered = true;
                var offset = PixelPoint.FromPoint(
                    new Point((ownerFrame.Width - frame.Width) / 2, (ownerFrame.Height - frame.Height) / 2), owner.DesktopScaling);
                Position = owner.Position + offset;
            }
        };
    }
}
