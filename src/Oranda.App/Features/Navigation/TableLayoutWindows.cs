using Avalonia;
using Avalonia.Controls;
using Oranda.App.Features.SqlEditor;

namespace Oranda.App.Features.Navigation;

/// <summary>
/// 떠 있는 테이블 레이아웃 창들의 관리자. 표를 열면(<see cref="Open"/>) 이미 열린 표는 그 탭으로 옮겨 가고, 새 표는 마지막으로 포커스가 있던 창에 탭으로 더하며,
/// 창이 하나도 없을 때만 앱 창이 있는 모니터의 가운데에 새 창을 연다. 앱은 <see cref="Shared"/> 하나를 쓰고 시험은 자기 인스턴스를 만든다.
/// 편집기 탭이 닫히는 것은 편집기 호스트 이벤트를 여기서 한 번만 구독해 해당 표 탭에 전한다. 창이 모두 닫히면 구독을 풀어
/// 오래 사는 호스트가 닫힌 창을 붙들지 않게 한다.
/// </summary>
internal sealed class TableLayoutWindows
{
    // 가장 최근에 포커스를 받은 창이 맨 뒤.
    private readonly List<TableLayoutWindow> _windows = [];
    private SqlEditorHost? _host;

    /// <summary>앱 전체가 쓰는 관리자.</summary>
    internal static TableLayoutWindows Shared { get; } = new();

    /// <summary>떠 있는 창(오래된 포커스 순).</summary>
    internal IReadOnlyList<TableLayoutWindow> Windows => _windows;

    /// <summary>마지막으로 포커스가 있던 창(없으면 null).</summary>
    internal TableLayoutWindow? LastActive => _windows.Count == 0 ? null : _windows[^1];

    /// <summary>위에 있다고 볼 창부터(최근 포커스 순).</summary>
    internal IEnumerable<TableLayoutWindow> WindowsByRecency() => Enumerable.Reverse(_windows).ToList();

    internal void Register(TableLayoutWindow window)
    {
        if (!_windows.Contains(window))
        {
            _windows.Add(window);
        }
    }

    /// <summary>창이 포커스를 받았다(또는 탭을 골랐다): 마지막 포커스 창으로 올린다.</summary>
    internal void Touch(TableLayoutWindow window)
    {
        if (_windows.Remove(window))
        {
            _windows.Add(window);
        }
    }

    internal void Unregister(TableLayoutWindow window)
    {
        _windows.Remove(window);
        if (_windows.Count == 0)
        {
            Watch(null);
        }
    }

    /// <summary>
    /// 표를 연다. 같은 표(접속·소유자·이름)가 이미 있으면 그 탭을 고르고 넣기 대상을 이번 편집기 탭으로 바꾼다.
    /// 아니면 마지막 포커스 창에 새 탭으로 더하고, 창이 없으면 <paramref name="owner"/> 가 있는 모니터 가운데에 새 창을 연다(이미 떠 있는 창은 옮기지 않는다).
    /// 열린 탭의 본문을 돌려준다.
    /// </summary>
    internal TableLayoutView Open(TableLayoutSession session, Window? owner, SqlEditorHost? host)
    {
        ArgumentNullException.ThrowIfNull(session);
        Watch(host);
        foreach (var window in WindowsByRecency())
        {
            if (window.Views.FirstOrDefault(v => v.Session.Key == session.Key) is { } same)
            {
                same.Session.Refresh(session.Model, session.Options); // 다시 읽은 컬럼 목록·설정을 반영한다(탭 상태는 유지)
                same.Session.Retarget(session.Tab);
                window.SelectView(same);
                window.Reveal();
                same.FocusList();
                return same;
            }
        }

        var view = new TableLayoutView(session);
        if (LastActive is { } last)
        {
            last.AddView(view);
            last.Reveal();
            view.FocusList();
            return view;
        }

        var created = new TableLayoutWindow(view, this);
        Place(created, owner, session.Model.Rows.Count);
        if (owner is not null)
        {
            created.Show(owner);
        }
        else
        {
            created.Show();
        }

        return view;
    }

    // 처음 여는 창: 앱 창이 있는 모니터(없으면 주 모니터)의 작업 영역 가운데. 높이 상한도 그 모니터 기준이다.
    private static void Place(TableLayoutWindow window, Window? owner, int columnCount)
    {
        var screen = owner?.Screens.ScreenFromWindow(owner) ?? owner?.Screens.Primary;
        if (screen is null)
        {
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            return;
        }

        // 작업 영역은 화면 좌표(창 위치와 같은 공간)다. 1 DIP 가 화면 좌표로 몇인지는 배율 속성을 가정하지 않고 앱 창에서 직접 잰다(macOS 는 점 단위일 수 있다).
        var area = screen.WorkingArea;
        var unit = UnitsPerDip(owner, screen.Scaling);
        window.Height = TableLayoutWindow.InitialHeight(columnCount, area.Height / unit) + TableLayoutWindow.TabStripHeight;
        var size = new PixelSize((int)Math.Round(window.Width * unit), (int)Math.Round(window.Height * unit));
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Position = TableLayoutDrag.Centered(area, size);
    }

    // 앱 창에서 잰 1 DIP 의 화면 좌표 길이. 창이 아직 화면에 없어 잴 수 없으면 모니터 배율을 쓴다.
    private static double UnitsPerDip(Window? owner, double fallback)
    {
        if (owner is null || !owner.IsVisible)
        {
            return fallback;
        }

        var measured = (owner.PointToScreen(new Point(100, 0)).X - owner.PointToScreen(default).X) / 100.0;
        return measured > 0 ? measured : fallback;
    }

    // 편집기 탭 닫힘 구독은 호스트마다 한 번. 호스트가 바뀌거나(시험) 창이 모두 닫히면 풀린다.
    private void Watch(SqlEditorHost? host)
    {
        if (ReferenceEquals(host, _host))
        {
            return;
        }

        if (_host is not null)
        {
            _host.TabClosed -= OnEditorTabClosed;
        }

        _host = host;
        if (_host is not null)
        {
            _host.TabClosed += OnEditorTabClosed;
        }
    }

    private void OnEditorTabClosed(object? sender, SqlEditorViewModel closed)
    {
        foreach (var window in _windows.ToList())
        {
            foreach (var view in window.Views.Where(v => ReferenceEquals(v.Session.Tab, closed)))
            {
                view.Session.MarkTargetClosed();
            }
        }
    }
}
