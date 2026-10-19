using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Oranda.App.Shell;
using Oranda.App.Shell.Composition;
using Oranda.App.ViewModels;
using Oranda.Core.Events;
using Oranda.Core.Shell;

namespace Oranda.App.Features.Execution;

/// <summary>
/// 상태 표시줄 구간(ui-shell.md 1.2)의 공통 바탕. 값은 <see cref="ExecutionState"/> 에서 읽는다.
/// 생성자는 이벤트 버스·문맥 키만 받는다(등록된 서비스라 시작 때 Oracle·Language 어셈블리를 깨우지 않는다).
/// </summary>
public abstract class ExecutionStatusItemViewModel : ViewModelBase, IStatusBarItem
{
    protected ExecutionStatusItemViewModel(IEventBus? events, ContextKeyService? keys, ServiceContainer? container)
    {
        State = ExecutionState.For(events);
        State.UseKeys(keys);
        ExecutionCommands.Install(container);
        State.PropertyChanged += OnStateChanged;
    }

    public ExecutionState State { get; }

    public abstract string Text { get; }

    /// <summary>글 앞의 점·아이콘 색 리소스 키. 없으면 점을 그리지 않는다.</summary>
    public virtual string? DotBrushKey => null;

    public virtual bool IsVisible => Text.Length > 0;

    public string ToolTip => Text;

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        OnPropertyChanged(nameof(Text));
        OnPropertyChanged(nameof(ToolTip));
        OnPropertyChanged(nameof(DotBrushKey));
        OnPropertyChanged(nameof(IsVisible));
    }
}

/// <summary>2번 구간: 연결됨 / 실행 중(경과 시간) / 취소 중 / 끊김.</summary>
[OrandaStatusBarItem("oranda.status.session", 2, CollapseRank = 0)]
public sealed class SessionStatusViewModel : ExecutionStatusItemViewModel
{
    public SessionStatusViewModel(IEventBus? events = null, ContextKeyService? keys = null, ServiceContainer? container = null)
        : base(events, keys, container)
    {
    }

    public override string Text => State.SessionText;

    public override string? DotBrushKey => State.RunState == SessionRunState.Broken ? "OrandaEnvProductionBrush" : null;
}

/// <summary>3번 구간: 미커밋(주황 점 + 「미커밋」), 자동 커밋이 켜져 있으면 「자동 커밋」.</summary>
[OrandaStatusBarItem("oranda.status.transaction", 3, CollapseRank = 0)]
public sealed class TransactionStatusViewModel : ExecutionStatusItemViewModel
{
    public TransactionStatusViewModel(IEventBus? events = null, ContextKeyService? keys = null, ServiceContainer? container = null)
        : base(events, keys, container)
    {
    }

    public override string Text => State.TransactionText;

    public override string? DotBrushKey => State.TxnPending ? "OrandaUncommittedBrush" : null;
}

/// <summary>4번 구간: 마지막 실행의 행 수·소요 시간·오류 번호.</summary>
[OrandaStatusBarItem("oranda.status.lastRun", 4, CollapseRank = 3)]
public sealed class LastRunStatusViewModel : ExecutionStatusItemViewModel
{
    public LastRunStatusViewModel(IEventBus? events = null, ContextKeyService? keys = null, ServiceContainer? container = null)
        : base(events, keys, container)
    {
    }

    public override string Text => State.HasSession ? State.LastRunText : string.Empty;
}

/// <summary>6번 구간: 「읽기 전용」(읽기 전용 프로필).</summary>
[OrandaStatusBarItem("oranda.status.protection", 6, CollapseRank = 0)]
public sealed class ProtectionStatusViewModel : ExecutionStatusItemViewModel
{
    public ProtectionStatusViewModel(IEventBus? events = null, ContextKeyService? keys = null, ServiceContainer? container = null)
        : base(events, keys, container)
    {
    }

    public override string Text => State.HasSession && State.ReadOnly ? "읽기 전용" : string.Empty;

    public override string? DotBrushKey => Text.Length > 0 ? "OrandaEnvTestBrush" : null;
}

/// <summary>상태 표시줄 구간 View 의 공통 몸통: 점(선택) + 글. 도움말 풍선에 전체 글을 둔다.</summary>
public abstract class ExecutionStatusItemView : UserControl
{
    private readonly Border _dot = new() { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _text = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private ExecutionStatusItemViewModel? _vm;

    protected ExecutionStatusItemView()
    {
        Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { _dot, _text } };
        DataContextChanged += (_, _) => Rebind();
    }

    private void Rebind()
    {
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnChanged;
        }

        _vm = DataContext as ExecutionStatusItemViewModel;
        if (_vm is not null)
        {
            _vm.PropertyChanged += OnChanged;
        }

        Refresh();
    }

    private void OnChanged(object? sender, PropertyChangedEventArgs e) => Refresh();

    private void Refresh()
    {
        _text.Text = _vm?.Text ?? string.Empty;
        ToolTip.SetTip(this, _vm?.ToolTip);
        Avalonia.Automation.AutomationProperties.SetName(this, _vm?.Text ?? string.Empty);
        if (_vm?.DotBrushKey is { } key)
        {
            _dot.IsVisible = true;
            _dot.Bind(Border.BackgroundProperty, _dot.GetResourceObservable(key));
        }
        else
        {
            _dot.IsVisible = false;
        }
    }
}

public sealed class SessionStatusView : ExecutionStatusItemView
{
}

public sealed class TransactionStatusView : ExecutionStatusItemView
{
}

public sealed class LastRunStatusView : ExecutionStatusItemView
{
}

public sealed class ProtectionStatusView : ExecutionStatusItemView
{
}
