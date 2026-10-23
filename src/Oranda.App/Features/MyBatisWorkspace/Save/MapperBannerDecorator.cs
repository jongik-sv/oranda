using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using Oranda.App.Features.Localization;
using Oranda.App.Features.MyBatisWorkspace.Editor;
using Oranda.App.Features.MyBatisWorkspace.Hooks;
using Oranda.App.Shell.Icons;
using Oranda.Core.Composition;
using AvDock = Avalonia.Controls.Dock;

namespace Oranda.App.Features.MyBatisWorkspace.Save;

/// <summary>알림 띠·상태 표시줄 「밖에서 바뀜」 표시의 단추 동작(design.md 5.3).</summary>
public enum MapperBannerAction
{
    /// <summary>「차이 보고 고르기」.</summary>
    Choose,

    /// <summary>「파일 것으로」(모든 충돌).</summary>
    UseDisk,

    /// <summary>「내 것으로」(모든 충돌).</summary>
    UseMine,

    /// <summary>알림만인 띠 닫기.</summary>
    Dismiss,
}

internal static class MapperBannerActions
{
    /// <summary>단추 동작 하나를 한다. 고르기 창에서 취소하면 아무것도 바꾸지 않는다.</summary>
    public static async Task RunAsync(MapperWatchService watch, MapperWatchState state, IMergeChoicePresenter presenter, MapperBannerAction action, CancellationToken ct)
    {
        var workspace = state.Workspace;
        switch (action)
        {
            case MapperBannerAction.Choose when state.Session is { } session:
                var answer = await presenter.ChooseAsync(MapperWatchService.FileName(session), state.Conflicts, ct).ConfigureAwait(true);
                if (answer is not null)
                {
                    watch.Resolve(workspace, answer);
                }

                break;
            case MapperBannerAction.UseDisk:
                watch.ResolveAll(workspace, MapperMergeChoice.Disk);
                break;
            case MapperBannerAction.UseMine:
                watch.ResolveAll(workspace, MapperMergeChoice.Mine);
                break;
            case MapperBannerAction.Dismiss:
                watch.Dismiss(workspace);
                break;
        }
    }
}

/// <summary>
/// 문장 편집기 위쪽의 밖에서 바뀜 알림 띠(Order 300, design.md 5.3·9장). 그 작업 공간의 <see cref="MapperWatchState"/> 를 보고
/// 충돌이면 「차이 보고 고르기 · 파일 것으로 · 내 것으로」, 알림만인 띠는 닫기 단추를 보인다. 띠가 없으면 숨는다.
/// 03-01 문장 편집기 View 의 <see cref="MyBatisStatementEditorView.BannerSlot"/>(머리 줄 아래 한 행)에 두어 편집기 글을 가리지 않는다.
/// 그 View 밖이면 편집기 위에 AdornerLayer 로 겹쳐 올린다.
/// </summary>
[OrandaService(typeof(IMyBatisEditorDecorator))]
public sealed class MapperBannerDecorator : IMyBatisEditorDecorator
{
    private readonly MapperWatchService _watch;

    public MapperBannerDecorator(MapperWatchService watch)
    {
        ArgumentNullException.ThrowIfNull(watch);
        _watch = watch;
    }

    public int Order => 300;

    /// <summary>「차이 보고 고르기」 창(시험은 바꿔 끼운다).</summary>
    internal IMergeChoicePresenter Presenter { get; set; } = new MergeChoiceWindowPresenter();

    public IDisposable Attach(MyBatisEditorContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new Attachment(this, context);
    }

    // 문장 편집기 하나에 붙은 띠. 편집기가 시각 트리에서 빠졌다 돌아와도(탭 전환) 따라 붙고 떼며, Dispose 하면 구독을 모두 뗀다.
    private sealed class Attachment : IDisposable
    {
        private readonly MapperBannerDecorator _owner;
        private readonly MyBatisEditorContext _context;
        private readonly MapperBannerBar _bar;
        private readonly CancellationTokenSource _cts = new();
        private MapperWatchState? _state;
        private AdornerLayer? _layer;
        private ContentControl? _slot;
        private bool _disposed;

        public Attachment(MapperBannerDecorator owner, MyBatisEditorContext context)
        {
            _owner = owner;
            _context = context;
            _bar = new MapperBannerBar(RunAsync);
            _owner._watch.Changed += OnWatchChanged;
            _context.Editor.AttachedToVisualTree += OnEditorAttached;
            _context.Editor.DetachedFromVisualTree += OnEditorDetached;
            Follow();
            if (TopLevel.GetTopLevel(_context.Editor) is not null)
            {
                Mount();
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _owner._watch.Changed -= OnWatchChanged;
            _context.Editor.AttachedToVisualTree -= OnEditorAttached;
            _context.Editor.DetachedFromVisualTree -= OnEditorDetached;
            Unmount();
            _bar.State = null;
            _cts.Cancel();
            _cts.Dispose();
        }

        // 상태는 세션이 감시에 올라야 생긴다: 아직 없으면 Changed 가 올 때 잡는다. 한번 잡으면 그 상태 객체를 따라간다
        // (세션이 떨어지며 마지막으로 알리는 때는 이미 Session 이 null 이라 세션으로는 못 거른다).
        private void OnWatchChanged(object? sender, MapperWatchState changed)
        {
            if (!ReferenceEquals(changed, _state))
            {
                Follow();
            }
        }

        private void Follow()
        {
            if (_owner._watch.StateOf(_context.Session) is { } found && !ReferenceEquals(found, _state))
            {
                _state = found;
                _bar.State = found;
            }
        }

        private void OnEditorAttached(object? sender, VisualTreeAttachmentEventArgs e) => Mount();

        private void OnEditorDetached(object? sender, VisualTreeAttachmentEventArgs e) => Unmount();

        private void Mount()
        {
            if (_disposed || _layer is not null || _slot is not null)
            {
                return;
            }

            // 03-01 문장 편집기 View 의 띠 자리(머리 줄과 편집기 사이)에 둔다: 편집기 글을 가리지 않고 아래로 민다.
            if (_context.Editor.FindAncestorOfType<MyBatisStatementEditorView>() is { } view)
            {
                view.BannerSlot.Content = _bar;
                _slot = view.BannerSlot;
                return;
            }

            // 그 View 밖(다른 호스트)이면 편집기 위에 겹쳐 올린다.
            var layer = AdornerLayer.GetAdornerLayer(_context.Editor);
            if (layer is null)
            {
                // 트리에 막 붙은 때는 층이 아직 없을 수 있다: 한 번 미뤄 다시 본다.
                Dispatcher.UIThread.Post(() =>
                {
                    if (!_disposed && _layer is null && TopLevel.GetTopLevel(_context.Editor) is not null)
                    {
                        AttachTo(AdornerLayer.GetAdornerLayer(_context.Editor));
                    }
                });
                return;
            }

            AttachTo(layer);
        }

        private void AttachTo(AdornerLayer? layer)
        {
            if (layer is null)
            {
                return;
            }

            AdornerLayer.SetAdornedElement(_bar, _context.Editor);
            layer.Children.Add(_bar);
            _layer = layer;
        }

        private void Unmount()
        {
            if (_slot is { } slot)
            {
                if (ReferenceEquals(slot.Content, _bar))
                {
                    slot.Content = null;
                }

                _slot = null;
            }

            if (_layer is { } layer)
            {
                layer.Children.Remove(_bar);
                AdornerLayer.SetAdornedElement(_bar, null);
                _layer = null;
            }
        }

        private async Task RunAsync(MapperBannerAction action)
        {
            if (_state is not { } state)
            {
                return;
            }

            try
            {
                await MapperBannerActions.RunAsync(_owner._watch, state, _owner.Presenter, action, _cts.Token).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                // 편집기를 닫으며 취소했다.
            }
        }
    }
}

/// <summary>
/// 띠 컨트롤: 경고 아이콘 + 글(줄바꿈) + 단추. 충돌이면 세 단추, 알림만이면 닫기. <see cref="State"/> 가 없거나 <c>HasBanner</c> 가 거짓이면 숨는다.
/// 단추는 아이콘 + 짧은 글 + 툴팁(.tool 클래스). 어두운 테마에서도 읽히게 바탕·글 색은 모두 테마 리소스 키다.
/// </summary>
internal sealed class MapperBannerBar : Border
{
    private readonly Func<MapperBannerAction, Task> _run;
    private readonly TextBlock _text = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _choose;
    private readonly Button _useDisk;
    private readonly Button _useMine;
    private readonly Button _dismiss;
    private MapperWatchState? _state;

    public MapperBannerBar(Func<MapperBannerAction, Task> run)
    {
        _run = run;
        _choose = Make("diff", "MyBatis.save.banner.choose", "차이 보고 고르기", "양쪽이 고친 문장마다 파일 것과 내 것을 견주어 고릅니다.", MapperBannerAction.Choose);
        _useDisk = Make("refresh", "MyBatis.save.banner.useDisk", "파일 것으로", "양쪽이 고친 문장을 모두 파일 것으로 바꾸고 내 고침은 버립니다.", MapperBannerAction.UseDisk);
        _useMine = Make("docEdit", "MyBatis.save.banner.useMine", "내 것으로", "양쪽이 고친 문장을 모두 내 것으로 남깁니다.", MapperBannerAction.UseMine);
        _dismiss = Make("clear", "MyBatis.save.banner.dismiss", null, "알림을 닫습니다.", MapperBannerAction.Dismiss);
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Top;
        Padding = new Thickness(8, 3);
        BorderThickness = new Thickness(0, 0, 0, 1);
        // 편집기 글 위에 겹쳐 그리므로 불투명 바탕이어야 한다(반투명 BaseLow 는 아래 글이 비친다).
        MapperSaveUi.BindResource(this, BackgroundProperty, "OrandaPanelHeaderBrush");
        MapperSaveUi.BindResource(this, BorderBrushProperty, "OrandaWarningForegroundBrush");

        var icon = ShellIcons.Create("warn");
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Children = { _choose, _useDisk, _useMine, _dismiss } };
        var dock = new DockPanel { LastChildFill = true };
        if (icon is not null)
        {
            icon.Margin = new Thickness(0, 0, 8, 0);
            icon.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(icon, AvDock.Left);
            dock.Children.Add(icon);
        }

        buttons.Margin = new Thickness(8, 0, 0, 0);
        DockPanel.SetDock(buttons, AvDock.Right);
        dock.Children.Add(buttons);
        dock.Children.Add(_text);
        Child = dock;
        Refresh();
    }

    /// <summary>보는 감시 상태. 바꾸면 앞 상태 구독을 떼고 다시 그린다.</summary>
    public MapperWatchState? State
    {
        get => _state;
        set
        {
            if (ReferenceEquals(_state, value))
            {
                return;
            }

            if (_state is not null)
            {
                _state.PropertyChanged -= OnStateChanged;
            }

            _state = value;
            if (value is not null)
            {
                value.PropertyChanged += OnStateChanged;
            }

            Refresh();
        }
    }

    private Button Make(string iconKey, string key, string? fallbackLabel, string fallbackTip, MapperBannerAction action)
    {
        var label = UiText.Get(key, fallbackLabel ?? "닫기");
        var button = MapperSaveUi.ToolButton(iconKey, UiText.Get(key + ".tip", fallbackTip), fallbackLabel is null ? null : label);
        if (fallbackLabel is null)
        {
            Avalonia.Automation.AutomationProperties.SetName(button, label);
        }

        button.Click += async (_, _) => await _run(action).ConfigureAwait(true);
        return button;
    }

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e) => Refresh();

    private void Refresh()
    {
        var state = _state;
        bool shown = state is { HasBanner: true };
        IsVisible = shown;
        _text.Text = state?.Message ?? string.Empty;
        bool conflicts = state is { HasConflicts: true };
        _choose.IsVisible = conflicts;
        _useDisk.IsVisible = conflicts;
        _useMine.IsVisible = conflicts;
        _dismiss.IsVisible = shown && !conflicts && state is { Banner: MapperBannerKind.Reloaded or MapperBannerKind.Merged or MapperBannerKind.Unreadable };
    }
}
