using Avalonia;

namespace Oranda.App.Features.Navigation;

/// <summary>탭을 놓았을 때 할 일.</summary>
internal enum TabDropKind
{
    /// <summary>아무것도 하지 않는다(자기 창의 탭 줄 밖이지만 창 안).</summary>
    Stay,

    /// <summary>같은 창의 탭 줄 안에서 순서를 바꾼다.</summary>
    Reorder,

    /// <summary>다른 창에 합친다(탭 줄 위면 놓은 자리, 그 밖의 창 영역이면 맨 뒤).</summary>
    Merge,

    /// <summary>어느 창 밖에 놓았다: 탭이 둘 이상이면 새 창으로 분리한다.</summary>
    Detach,

    /// <summary>어느 창 밖에 놓았는데 탭이 하나뿐이다: 새 창을 만들지 않고 창을 옮긴다.</summary>
    MoveWindow,
}

/// <summary>놓은 결과. <see cref="Window"/> = 합칠 창 번호(<see cref="TabDropKind.Merge"/>), <see cref="Index"/> = 끼워 넣을 자리(0 ~ 탭 수).</summary>
internal readonly record struct TabDrop(TabDropKind Kind, int Window = -1, int Index = -1);

/// <summary>창 하나의 화면 좌표(픽셀) 배치: 창 전체, 탭 줄, 탭 머리들(왼쪽부터).</summary>
internal readonly record struct TabFrame(PixelRect Window, PixelRect Strip, IReadOnlyList<PixelRect> Tabs);

/// <summary>
/// 탭 끌기의 판정(순수 함수). 포인터 이벤트와 창 좌표는 <see cref="TableLayoutWindow"/> 가 모으고, 무엇을 할지는 여기서만 정한다.
/// 모든 좌표는 화면 픽셀이라 창이 서로 다른 모니터·배율에 있어도 같은 식으로 비교된다.
/// </summary>
internal static class TableLayoutDrag
{
    /// <summary>끌기로 보는 최소 이동 거리(DIP). 이보다 적게 움직인 누르기는 탭 고르기다.</summary>
    internal const double ThresholdDip = 6;

    /// <summary>누른 자리에서 <paramref name="thresholdPixels"/> 이상 움직였는가(가로·세로 어느 쪽이든).</summary>
    internal static bool Exceeds(PixelPoint start, PixelPoint now, double thresholdPixels)
        => Math.Abs(now.X - start.X) >= thresholdPixels || Math.Abs(now.Y - start.Y) >= thresholdPixels;

    /// <summary>
    /// 놓은 자리 <paramref name="point"/> 의 처리. 어느 창의 탭 줄 위면 그 창에 끼운다(자기 창이면 순서 바꾸기, 다른 창이면 합치기).
    /// 탭 줄 밖이라도 다른 창 안이면 그 창 맨 뒤에 합치고(자기 창 안이면 그대로 두고), 모든 창 밖이면 분리한다(탭이 하나뿐이면 창을 옮긴다).
    /// 겹친 창은 목록 앞쪽을 위로 본다(<paramref name="frames"/> 는 위에 있는 창부터).
    /// </summary>
    internal static TabDrop Decide(PixelPoint point, IReadOnlyList<TabFrame> frames, int source, bool onlyTab)
    {
        ArgumentNullException.ThrowIfNull(frames);
        // 위에 있는 창부터 한 창씩 본다: 그 창의 탭 줄이면 끼우고, 탭 줄이 아니어도 그 창 안이면 아래 창을 보지 않고 멈춘다(위 창이 아래 창의 탭 줄을 덮은 경우).
        for (var i = 0; i < frames.Count; i++)
        {
            if (frames[i].Strip.Contains(point))
            {
                var slot = InsertIndex(point, frames[i].Tabs);
                return i == source ? new TabDrop(TabDropKind.Reorder, i, slot) : new TabDrop(TabDropKind.Merge, i, slot);
            }

            if (frames[i].Window.Contains(point))
            {
                // 다른 창의 본문·제목 줄에 놓으면 그 창 맨 뒤에 합친다. 자기 창 안은 그대로 둔다.
                return i == source ? new TabDrop(TabDropKind.Stay) : new TabDrop(TabDropKind.Merge, i, frames[i].Tabs.Count);
            }
        }

        return new TabDrop(onlyTab ? TabDropKind.MoveWindow : TabDropKind.Detach);
    }

    /// <summary>탭 머리들 사이 어느 자리에 끼울지(0 ~ 탭 수). 가로로 각 머리의 가운데 앞이면 그 머리 앞이다.</summary>
    internal static int InsertIndex(PixelPoint point, IReadOnlyList<PixelRect> tabs)
    {
        ArgumentNullException.ThrowIfNull(tabs);
        for (var i = 0; i < tabs.Count; i++)
        {
            if (point.X < tabs[i].X + (tabs[i].Width / 2.0))
            {
                return i;
            }
        }

        return tabs.Count;
    }

    /// <summary>
    /// 같은 창 안에서 <paramref name="from"/> 번 탭을 끼움 자리 <paramref name="slot"/> 에 옮긴 뒤의 번호.
    /// 끼움 자리는 옮기기 전 목록 기준이라 자기보다 뒤 자리면 하나 줄어든다.
    /// </summary>
    internal static int ReorderedIndex(int from, int slot) => slot > from ? slot - 1 : slot;

    /// <summary>
    /// 새로 여는 창을 <paramref name="workArea"/>(그 모니터 작업 영역, 픽셀) 가운데에 놓는 왼쪽 위 좌표.
    /// 창이 작업 영역보다 크면 왼쪽·위 가장자리에 붙인다.
    /// </summary>
    internal static PixelPoint Centered(PixelRect workArea, PixelSize window)
        => new(
            workArea.X + Math.Max(0, (workArea.Width - window.Width) / 2),
            workArea.Y + Math.Max(0, (workArea.Height - window.Height) / 2));

    /// <summary>분리·이동한 창의 왼쪽 위 좌표: 누른 자리(창 왼쪽 위에서 <paramref name="grab"/> 만큼)가 놓은 자리 <paramref name="pointer"/> 에 오게 한다.</summary>
    internal static PixelPoint Dropped(PixelPoint pointer, PixelPoint grab) => new(pointer.X - grab.X, pointer.Y - grab.Y);

    /// <summary>
    /// 분리·이동한 창의 왼쪽 위 좌표를 작업 영역 안에 붙잡는다: 제목 줄이 위 가장자리 밖으로 나가지 않게 하고,
    /// 좌우·아래로는 창이 <paramref name="minVisible"/> 만큼은 보이게 한다.
    /// </summary>
    internal static PixelPoint ClampToArea(PixelPoint position, PixelSize window, PixelRect area, int minVisible = 80)
    {
        var x = Math.Clamp(position.X, area.X - window.Width + minVisible, Math.Max(area.X - window.Width + minVisible, area.Right - minVisible));
        var y = Math.Clamp(position.Y, area.Y, Math.Max(area.Y, area.Bottom - minVisible));
        return new PixelPoint(x, y);
    }
}
