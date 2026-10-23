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

    /// <summary>본문 폭 상한(DIP). 헤드리스 측정에서 범주별 내용 폭은 428~886 이었고, 대부분 600 안쪽이며 넘는 쪽은 편집기(감사 열 칸)·업데이트(피드 주소 칸)처럼
    /// 입력 칸이 넓게 잡힌 경우였다. 760 이면 이 칸들이 조금 줄어들 뿐 잘리지 않는다. 주 창이 이보다 좁으면 주 창 폭까지만 연다.</summary>
    internal const double MaxBodyWidth = 760;

    /// <summary>작업 영역 높이에 대한 설정 창 높이 비율 상한.</summary>
    private const double WorkAreaHeightRatio = 0.85;

    /// <summary>범주를 하나씩 골라 내용 크기를 재고, 본문 크기를 그중 가장 큰 값으로 고정한다. 창은 내용에 맞추므로 그 뒤로 크기가 바뀌지 않는다.
    /// 폭은 <see cref="MaxBodyWidth"/> 와 주 창 폭 중 작은 값을 넘지 않는다(그 폭을 넘는 미리보기·긴 줄은 옆 스크롤, 안내 글은 줄바꿈).
    /// 창은 열 때 한 번만 커진다. 사용자가 창 크기를 직접 바꾸면 고정을 풀어 본문이 창을 따라간다.</summary>
    private void FitLargestCategory()
    {
        if (Content is not Control root)
        {
            return;
        }

        var margin = root.Margin;
        var marginWidth = margin.Left + margin.Right;
        var marginHeight = margin.Top + margin.Bottom;

        // 작업 영역과 주 창보다 크게 열지 않는다(넘치는 범주는 본문만 스크롤). 폭은 설계 상한도 넘지 않는다.
        var limit = Screens.ScreenFromWindow(this) is { } screen
            ? screen.WorkingArea.Size.ToSize(screen.Scaling)
            : Size.Infinity;
        var limitWidth = Math.Min(limit.Width, MaxBodyWidth + marginWidth);
        var limitHeight = limit.Height * WorkAreaHeightRatio;
        if (Owner is { } owner)
        {
            limitWidth = Math.Min(limitWidth, owner.Bounds.Width);
            limitHeight = Math.Min(limitHeight, owner.Bounds.Height);
        }

        var start = Categories.SelectedIndex;
        double width = 0, height = 0;
        for (var i = 0; i < Categories.ItemCount; i++)
        {
            Categories.SelectedIndex = i;
            UpdateLayout();
            // 폭을 상한으로 막고 재야 줄바꿈 글·옆 스크롤 영역이 상한 안에서 자리를 잡는다(무한 폭이면 한 줄 길이가 그대로 폭이 된다).
            root.Measure(new Size(limitWidth, double.PositiveInfinity));
            width = Math.Max(width, root.DesiredSize.Width - marginWidth);
            height = Math.Max(height, root.DesiredSize.Height - marginHeight);
        }

        Categories.SelectedIndex = start;

        width = Math.Min(width, limitWidth - marginWidth);
        height = Math.Min(height, limitHeight - marginHeight);

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
