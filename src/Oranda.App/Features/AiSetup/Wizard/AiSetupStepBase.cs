using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Oranda.App.Features.AiSetup.Contract;
using Oranda.Core.Ai.Setup;

namespace Oranda.App.Features.AiSetup.Wizard;

/// <summary>
/// 단계 구현을 쉽게 하는 바탕(계약 <see cref="IAiSetupStep"/>). 끝남·알림·자동 다음·화면 한 번 만들기를 맡는다.
/// 화면은 이 객체를 <c>DataContext</c> 로 묶는다(속성이 바뀌면 <see cref="OnPropertyChanged"/>).
/// </summary>
public abstract class AiSetupStepBase : IAiSetupStep
{
    private bool _complete;
    private Control? _view;

    protected AiSetupStepBase(AiSetupContext context)
    {
        Context = context ?? throw new ArgumentNullException(nameof(context));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public event EventHandler? NextRequested;

    public abstract AiSetupStepId StepId { get; }

    public virtual string Title => AiSetupText.StepTitle(StepId, English);

    /// <summary>단계 맥락.</summary>
    public AiSetupContext Context { get; }

    /// <summary>화면 언어가 영어다.</summary>
    public bool English => Context.IsEnglish;

    public bool IsComplete
    {
        get => _complete;
        protected set => Set(ref _complete, value);
    }

    public Control View
    {
        get
        {
            if (_view is null)
            {
                _view = BuildView();
                _view.DataContext = this;
            }

            return _view;
        }
    }

    /// <summary>버렸다(창이 닫힘).</summary>
    protected bool IsDisposed { get; private set; }

    public virtual Task EnterAsync(CancellationToken ct) => Task.CompletedTask;

    public virtual Task LeaveAsync(CancellationToken ct) => Task.CompletedTask;

    public void Dispose()
    {
        if (IsDisposed)
        {
            return;
        }

        IsDisposed = true;
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>가운데 화면을 만든다(한 번).</summary>
    protected abstract Control BuildView();

    /// <summary>정리(확인 주기·프로세스 손잡이 등).</summary>
    protected virtual void Dispose(bool disposing)
    {
    }

    /// <summary>창 틀에 다음으로 가자고 한다(끝남일 때만 옮겨진다).</summary>
    protected void RequestNext() => NextRequested?.Invoke(this, EventArgs.Empty);

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(name);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
