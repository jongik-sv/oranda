using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Oranda.App.Features.Localization;
using Oranda.App.Features.MyBatisWorkspace.Hooks;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell.Icons;
using Oranda.App.ViewModels;
using Oranda.Core.Shell;

namespace Oranda.App.Features.MyBatisWorkspace.Save;

/// <summary>
/// 상태 표시줄 10번 구간(design.md 1장): 「EmpMapper.xml · 저장 안 한 문장 2개 · 파일 감시 중 · UTF-8 BOM · CRLF」. 활성 작업 공간이 MyBatis 일 때만 보인다.
/// 밖에서 바뀜 띠가 있으면 앞에 「밖에서 바뀜」 표시를 붙이고(열린 탭이 0개라 꾸밈 띠가 안 보여도 알 수 있게), 누르면 충돌이면 고르기 창, 아니면 알림을 닫는다.
/// 생성자는 등록된 서비스 <see cref="MapperWatchService"/> 만 받는다. 매퍼 문서·편집(Language 형)을 읽는 곳은 <see cref="MapperSaveStatusText"/> 로 나눠
/// 시작 때 셸이 이 형을 훑어도 Language 어셈블리가 올라오지 않게 한다(D-006).
/// </summary>
[OrandaStatusBarItem("oranda.status.mybatis", 10, CollapseRank = 2)]
public sealed class MapperSaveStatusViewModel : ViewModelBase, IStatusBarItem
{
    private readonly MapperWatchService _watch;
    private SqlEditorHost? _host;
    private SqlWorkspaceViewModel? _workspace;
    private IMyBatisWorkspaceSession? _session;
    private MapperWatchState? _state;

    public MapperSaveStatusViewModel(MapperWatchService watch)
    {
        ArgumentNullException.ThrowIfNull(watch);
        _watch = watch;
        watch.Changed += (_, _) => OnSourceChanged();
        if (UiText.Current is { } text)
        {
            text.LanguageChanged += (_, _) => OnSourceChanged();
        }

        Rebind();
    }

    /// <summary>「고르기」 창(시험은 바꿔 끼운다).</summary>
    internal IMergeChoicePresenter Presenter { get; set; } = new MergeChoiceWindowPresenter();

    public bool IsVisible => _session is not null;

    /// <summary>밖에서 바뀜 띠가 있다.</summary>
    public bool HasBanner => _state is { HasBanner: true };

    /// <summary>띠 앞에 붙이는 표시(「밖에서 바뀜」·「파일 없음」·「읽지 못함」). 띠가 없으면 빈 글.</summary>
    public string BannerText => _state?.Banner switch
    {
        MapperBannerKind.Missing => UiText.Get("MyBatis.save.status.missing", "파일 없음"),
        MapperBannerKind.Unreadable => UiText.Get("MyBatis.save.status.unreadable", "읽지 못함"),
        MapperBannerKind.Reloaded or MapperBannerKind.Merged or MapperBannerKind.Conflict => UiText.Get("MyBatis.save.status.external", "밖에서 바뀜"),
        _ => string.Empty,
    };

    /// <summary>「EmpMapper.xml · 저장 안 한 문장 2개 · 파일 감시 중 · UTF-8 BOM · CRLF」.</summary>
    public string Text => _session is null ? string.Empty : MapperSaveStatusText.Build(_session, _state);

    /// <summary>툴팁: 띠가 있으면 띠 글, 없으면 매퍼 파일 경로.</summary>
    public string ToolTipText
    {
        get
        {
            if (_state is { HasBanner: true } state)
            {
                return state.Message;
            }

            return _session is null ? string.Empty : MapperSaveStatusText.PathOf(_session);
        }
    }

    /// <summary>누르기: 충돌이면 차이 고르기 창, 알림만인 띠면 닫기.</summary>
    public async Task ClickAsync()
    {
        if (_state is not { HasBanner: true } state)
        {
            return;
        }

        var action = state.HasConflicts ? MapperBannerAction.Choose : MapperBannerAction.Dismiss;
        await MapperBannerActions.RunAsync(_watch, state, Presenter, action, CancellationToken.None).ConfigureAwait(true);
    }

    // 감시 상태나 글 언어가 바뀌었다. UI 스레드가 아니면 넘긴다.
    private void OnSourceChanged()
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            Rebind();
        }
        else
        {
            Dispatcher.UIThread.Post(Rebind);
        }
    }

    private void OnHostActiveChanged(object? sender, EventArgs e) => Rebind();

    private void OnWorkspaceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SqlWorkspaceViewModel.MyBatis))
        {
            Rebind();
        }
    }

    private void OnSessionChanged(object? sender, EventArgs e) => Raise();

    // 호스트는 첫 화면 뒤에 붙으므로 만들 때는 없을 수 있다: 보일 때마다 확인해 늦게라도 활성 작업 공간 바뀜을 따른다.
    private void Rebind()
    {
        if (_watch.Host is { } host && !ReferenceEquals(host, _host))
        {
            if (_host is not null)
            {
                _host.ActiveWorkspaceChanged -= OnHostActiveChanged;
            }

            _host = host;
            host.ActiveWorkspaceChanged += OnHostActiveChanged;
        }

        var workspace = _host?.ActiveWorkspace;
        if (!ReferenceEquals(workspace, _workspace))
        {
            if (_workspace is not null)
            {
                _workspace.PropertyChanged -= OnWorkspaceChanged;
            }

            _workspace = workspace;
            if (workspace is not null)
            {
                workspace.PropertyChanged += OnWorkspaceChanged;
            }
        }

        var session = workspace?.MyBatis;
        if (!ReferenceEquals(session, _session))
        {
            if (_session is not null)
            {
                _session.StateChanged -= OnSessionChanged;
            }

            _session = session;
            if (session is not null)
            {
                session.StateChanged += OnSessionChanged;
            }
        }

        _state = workspace is null ? null : _watch.StateOf(workspace);
        Raise();
    }

    private void Raise()
    {
        OnPropertyChanged(nameof(IsVisible));
        OnPropertyChanged(nameof(HasBanner));
        OnPropertyChanged(nameof(BannerText));
        OnPropertyChanged(nameof(Text));
        OnPropertyChanged(nameof(ToolTipText));
    }
}

// 매퍼 문서·편집(Language 형)을 읽는 곳. 세션이 있을 때만 부르고, JIT 가 이 메서드를 따로 풀게 인라인하지 않는다.
internal static class MapperSaveStatusText
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static string Build(IMyBatisWorkspaceSession session, MapperWatchState? state)
    {
        var parts = new List<string> { MapperWatchService.FileName(session) };
        int pending = session.PendingEdits().Count;
        parts.Add(pending == 0
            ? UiText.Get("MyBatis.save.status.clean", "저장 안 한 문장 없음")
            : MapperSaveUi.Format("MyBatis.save.status.pending", "저장 안 한 문장 {0}개", pending));
        if (state is not null)
        {
            parts.Add(state.IsWatching
                ? UiText.Get("MyBatis.save.status.watching", "파일 감시 중")
                : UiText.Get("MyBatis.save.status.notWatching", "파일 감시 꺼짐"));
        }

        var format = session.Document.Format;
        parts.Add(format.EncodingName.ToUpperInvariant() + (format.HasBom ? " BOM" : string.Empty));
        parts.Add(format.NewLine switch
        {
            "\r\n" => "CRLF",
            "\n" => "LF",
            "\r" => "CR",
            var other => other,
        });
        return string.Join(" · ", parts);
    }

    public static string PathOf(IMyBatisWorkspaceSession session) => session.MapperPath;
}

/// <summary>10번 구간 View: (경고 아이콘 + 「밖에서 바뀜」) + 글. 띠가 있으면 누를 수 있다.</summary>
public sealed class MapperSaveStatusView : UserControl
{
    private readonly Control? _icon = ShellIcons.Create("warn", 14);
    private readonly TextBlock _banner = new() { VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.SemiBold };
    private readonly TextBlock _text = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly StackPanel _badge = new() { Orientation = Orientation.Horizontal, Spacing = 4 };
    private MapperSaveStatusViewModel? _vm;

    public MapperSaveStatusView()
    {
        if (_icon is not null)
        {
            _badge.Children.Add(_icon);
        }

        _badge.Children.Add(_banner);
        Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _badge, _text } };
        Background = Brushes.Transparent;
        PointerPressed += async (_, e) =>
        {
            if (_vm is { HasBanner: true } vm && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                await vm.ClickAsync();
            }
        };
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null)
            {
                _vm.PropertyChanged -= OnChanged;
            }

            _vm = DataContext as MapperSaveStatusViewModel;
            if (_vm is not null)
            {
                _vm.PropertyChanged += OnChanged;
            }

            Refresh();
        };
    }

    private void OnChanged(object? sender, PropertyChangedEventArgs e) => Refresh();

    private void Refresh()
    {
        bool banner = _vm is { HasBanner: true };
        _badge.IsVisible = banner;
        _banner.Text = _vm?.BannerText ?? string.Empty;
        _text.Text = _vm?.Text ?? string.Empty;
        Cursor = banner ? new Cursor(StandardCursorType.Hand) : null;
        ToolTip.SetTip(this, _vm?.ToolTipText);
        Avalonia.Automation.AutomationProperties.SetName(this, (banner ? _banner.Text + " " : string.Empty) + _text.Text);
    }
}
