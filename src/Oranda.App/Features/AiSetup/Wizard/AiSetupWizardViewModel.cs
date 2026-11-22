using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Oranda.App.Features.AiSetup.Contract;
using Oranda.Core.Ai.Setup;
using Oranda.Core.Settings;

namespace Oranda.App.Features.AiSetup.Wizard;

/// <summary>왼쪽 단계 목록 한 줄의 상태.</summary>
public enum AiSetupRailState
{
    /// <summary>아직 안 함.</summary>
    Pending,

    /// <summary>지금 단계.</summary>
    Current,

    /// <summary>끝남.</summary>
    Done,
}

/// <summary>왼쪽 단계 목록 한 줄(번호·제목·상태).</summary>
public sealed record AiSetupRailItem(AiSetupStepId StepId, int Number, string Title, AiSetupRailState State)
{
    /// <summary>동그라미 안 글: 끝남 = ✓, 그 밖 = 번호.</summary>
    public string Badge => State == AiSetupRailState.Done ? "✓" : Number.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// 도우미 창 틀의 뷰모델. 이동 규칙은 계약 <see cref="AiSetupFlow"/> 가 정하고, 여기는 단계 화면 만들기·들어감/떠남·이어 하기 저장을 맡는다.
/// 단계는 처음 들어갈 때 팩터리로 만들어 창이 닫힐 때까지 쓴다(갈래를 바꾸면 방법 고르기 뒤 단계는 버리고 새로 만든다).
/// 팩터리가 없는 단계는 「준비 중」 자리 표시(<see cref="AiSetupPlaceholderStep"/>)다. 화면 스레드에서만 쓴다.
/// </summary>
public sealed class AiSetupWizardViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly AiSetupContext _context;
    private readonly Dictionary<AiSetupStepId, IAiSetupStepFactory> _factories = [];
    private readonly Dictionary<AiSetupStepId, IAiSetupStep> _steps = [];
    private readonly AiSetupFlow _flow;
    private IAiSetupStep _current;
    private IReadOnlyList<AiSetupRailItem> _rail = [];
    private Task _queue = Task.CompletedTask;
    private bool _busy;
    private bool _started;
    private bool _closing;
    private bool _closed;

    public AiSetupWizardViewModel(AiSetupContext context, IEnumerable<IAiSetupStepFactory> factories)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(factories);
        _context = context;
        foreach (var factory in factories)
        {
            _factories.TryAdd(factory.StepId, factory);
        }

        _flow = RestoreFlow(context);
        if (_flow.Path is { } path)
        {
            context.ChosenPath = path;
        }

        context.ChosenPathChanged += OnChosenPathChanged;
        _flow.Changed += OnFlowChanged;
        _current = StepFor(_flow.Current);
        Refresh();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>[끝내기]로 다 끝났다(창이 닫는다).</summary>
    public event EventHandler? Finished;

    /// <summary>단계들이 받은 맥락(창이 클립보드 등을 채운다).</summary>
    public AiSetupContext Context => _context;

    /// <summary>화면 언어가 영어다.</summary>
    public bool IsEnglish => _context.IsEnglish;

    /// <summary>지금 갈래의 단계 목록.</summary>
    public IReadOnlyList<AiSetupRailItem> Rail => _rail;

    /// <summary>지금 단계.</summary>
    public IAiSetupStep CurrentStep => _current;

    /// <summary>지금 단계 화면.</summary>
    public Control CurrentView => _current.View;

    /// <summary>지금 단계 제목.</summary>
    public string CurrentTitle => _current.Title;

    /// <summary>[이전] 켜짐.</summary>
    public bool CanGoBack => !_busy && _flow.CanGoBack;

    /// <summary>[다음](마지막이면 [끝내기]) 켜짐.</summary>
    public bool CanGoNext => !_busy && (_flow.CanGoNext || _flow.CanFinish);

    /// <summary>마지막 단계다.</summary>
    public bool IsLastStep => _flow.IsLastStep;

    /// <summary>[다음] 단추 글.</summary>
    public string NextText => IsLastStep ? AiSetupText.Finish(IsEnglish) : AiSetupText.Next(IsEnglish);

    /// <summary>아래 왼쪽 「2 / 6 단계」.</summary>
    public string ProgressText => AiSetupWizardText.Progress(_flow.CurrentIndex + 1, _flow.Steps.Count, IsEnglish);

    /// <summary>줄 세운 일(이동·닫기·끝내기)이 모두 끝나는 일(시험용).</summary>
    internal Task Idle => _queue;

    /// <summary>첫 단계에 들어간다. 창을 띄운 뒤 한 번 부른다(두 번째부터는 아무것도 안 함).</summary>
    public Task StartAsync(CancellationToken ct = default)
    {
        if (_started)
        {
            return _queue;
        }

        _started = true;
        return Enqueue(() => BusyAsync(() => EnterCurrentAsync(ct)));
    }

    /// <summary>[다음]. 마지막 단계면 끝내기(<see cref="Finished"/>)이고 true 를 돌려준다. 이동·닫는 중이거나 못 움직이면 false.</summary>
    public async Task<bool> NextAsync(CancellationToken ct = default)
    {
        if (_busy || _closing || !(_flow.CanGoNext || _flow.CanFinish))
        {
            return false;
        }

        var finish = _flow.CanFinish;
        await Enqueue(() => finish ? FinishCoreAsync(ct) : MoveCoreAsync(() => _flow.GoNext(), ct)).ConfigureAwait(true);
        return true;
    }

    /// <summary>[이전]. 끝남은 그대로 둔다.</summary>
    public Task BackAsync(CancellationToken ct = default)
    {
        if (_busy || _closing || !_flow.CanGoBack)
        {
            return Task.CompletedTask;
        }

        return Enqueue(() => MoveCoreAsync(() => _flow.GoBack(), ct));
    }

    /// <summary>
    /// [닫기]·창 닫기: 하던 이동·끝내기를 기다린 뒤 지금 단계를 떠나고 갈래·단계를 이어 하기 칸에 저장하고 모든 단계를 버린다.
    /// 여러 번 불러도 한 번만 한다(같은 일을 돌려준다). 끝내기로 이미 닫혔으면 아무것도 안 한다.
    /// </summary>
    public Task CloseAsync(CancellationToken ct = default)
    {
        if (_closing)
        {
            return _queue;
        }

        _closing = true;
        return Enqueue(async () =>
        {
            if (_closed)
            {
                return;
            }

            _closed = true;
            try
            {
                await LeaveQuietlyAsync(_current, ct).ConfigureAwait(true);
                var path = _flow.Path;
                var step = path is null ? (AiSetupStepId?)null : _flow.Current;
                var profile = path == AiSetupPath.CompanyServer ? _context.CreatedProfileId : null;
                await SaveResumeAsync(path, step, profile, ct).ConfigureAwait(true);
            }
            finally
            {
                DisposeSteps();
            }
        });
    }

    public void Dispose()
    {
        _closing = true;
        _closed = true;
        _context.ChosenPathChanged -= OnChosenPathChanged;
        _flow.Changed -= OnFlowChanged;
        DisposeSteps();
    }

    /// <summary>일을 앞 일 뒤에 줄 세운다. 앞 일이 없으면 바로(첫 await 까지 같은 호출 안에서) 돈다. 예외는 돌려준 일에만 남는다.</summary>
    private Task Enqueue(Func<Task> work)
    {
        var previous = _queue;
        var next = RunAfterAsync(previous, work);
        _queue = next.ContinueWith(_ => { }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return next;
    }

    private static async Task RunAfterAsync(Task previous, Func<Task> work)
    {
        await previous.ConfigureAwait(true);
        await work().ConfigureAwait(true);
    }

    private async Task BusyAsync(Func<Task> work)
    {
        SetBusy(true);
        try
        {
            await work().ConfigureAwait(true);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task FinishCoreAsync(CancellationToken ct)
    {
        if (_closed || !_flow.CanFinish)
        {
            return;
        }

        _closing = true;
        _closed = true;
        SetBusy(true);
        try
        {
            try
            {
                await _current.FinishAsync(ct).ConfigureAwait(true);
            }
            catch (Exception)
            {
                // 끝낼 때 하는 일(다른 창 열기 등)이 실패하거나 취소돼도 도우미는 닫는다.
            }

            await LeaveQuietlyAsync(_current, CancellationToken.None).ConfigureAwait(true);
            await SaveResumeAsync(null, null, null, CancellationToken.None).ConfigureAwait(true);
        }
        finally
        {
            DisposeSteps();
            SetBusy(false);
        }

        Finished?.Invoke(this, EventArgs.Empty);
    }

    private Task MoveCoreAsync(Func<bool> move, CancellationToken ct)
    {
        if (_closed)
        {
            return Task.CompletedTask;
        }

        return BusyAsync(async () =>
        {
            await LeaveQuietlyAsync(_current, ct).ConfigureAwait(true);
            if (_closed)
            {
                return;
            }

            move();
            await EnterCurrentAsync(ct).ConfigureAwait(true);
        });
    }

    private async Task EnterCurrentAsync(CancellationToken ct)
    {
        if (_closed)
        {
            return;
        }

        _current = StepFor(_flow.Current);

        // 들어가는 단계의 끝남은 그 단계가 다시 정한다(이어 하기로 되살린 앞 단계도 마찬가지).
        _flow.SetComplete(_current.StepId, _current.IsComplete);
        Refresh();
        try
        {
            await _current.EnterAsync(ct).ConfigureAwait(true);
        }
        catch (Exception)
        {
            // 계약: 단계 실패는 화면에 보인다. 예외가 새도 창 틀은 멈추지 않는다.
        }
    }

    private static async Task LeaveQuietlyAsync(IAiSetupStep step, CancellationToken ct)
    {
        try
        {
            await step.LeaveAsync(ct).ConfigureAwait(true);
        }
        catch (Exception)
        {
            // 떠날 때 정리 실패는 무시한다(창은 닫혀야 한다).
        }
    }

    private async Task SaveResumeAsync(AiSetupPath? path, AiSetupStepId? step, Guid? profile, CancellationToken ct)
    {
        try
        {
            await _context.Settings.UpdateAsync(
                s => s with { Ai = s.Ai with { Setup = s.Ai.Setup with { LastPath = path, LastStep = step, LastProfileId = profile } } },
                ct).ConfigureAwait(true);
        }
        catch (Exception)
        {
            // 이어 하기 저장이 안 돼도(읽기 전용 설정 등) 창은 닫는다. 다음에 처음부터 한다.
        }
    }

    private IAiSetupStep StepFor(AiSetupStepId id)
    {
        if (_steps.TryGetValue(id, out var step))
        {
            return step;
        }

        step = null;
        if (_factories.TryGetValue(id, out var factory))
        {
            try
            {
                step = factory.Create(_context);
            }
            catch (Exception)
            {
                // 단계를 못 만들면 자리 표시로 넘어갈 수 있게 한다.
            }
        }

        step ??= new AiSetupPlaceholderStep(id, _context.IsEnglish);
        step.PropertyChanged += OnStepPropertyChanged;
        step.NextRequested += OnStepNextRequested;
        _steps[id] = step;
        return step;
    }

    private void OnStepPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not IAiSetupStep step || !_steps.TryGetValue(step.StepId, out var known) || !ReferenceEquals(known, step))
        {
            return;
        }

        var all = string.IsNullOrEmpty(e.PropertyName);
        if (all || e.PropertyName == nameof(IAiSetupStep.IsComplete))
        {
            _flow.SetComplete(step.StepId, step.IsComplete);
        }

        if (all || e.PropertyName == nameof(IAiSetupStep.Title))
        {
            Refresh();
        }
    }

    private void OnStepNextRequested(object? sender, EventArgs e)
    {
        if (_closing || sender is not IAiSetupStep step)
        {
            return;
        }

        // 들어가는 중(EnterAsync 안)에 온 요청도 버리지 않게 줄 세우고, 차례가 오면 다시 본다.
        _ = Enqueue(() => ReferenceEquals(step, _current) && step.IsComplete && _flow.CanGoNext
            ? MoveCoreAsync(() => _flow.GoNext(), CancellationToken.None)
            : Task.CompletedTask);
    }

    private void OnChosenPathChanged(object? sender, EventArgs e)
    {
        if (_closing || _context.ChosenPath is not { } path || _flow.Path == path)
        {
            return;
        }

        _ = Enqueue(() => ApplyPathAsync(path));
    }

    /// <summary>갈래가 바뀌었다: 방법 고르기 뒤 단계를 버리고(다시 들어가면 새로 만든다) 흐름을 새 갈래로. 방법 고르기 밖이면 거기로 돌아간다.</summary>
    private async Task ApplyPathAsync(AiSetupPath path)
    {
        if (_closed || _flow.Path == path || _context.ChosenPath != path)
        {
            return;
        }

        if (_current.StepId != AiSetupStepId.Choose)
        {
            await LeaveQuietlyAsync(_current, CancellationToken.None).ConfigureAwait(true);
        }

        foreach (var id in _steps.Keys.Where(k => k != AiSetupStepId.Choose).ToList())
        {
            Forget(id);
        }

        var wasChoose = _current.StepId == AiSetupStepId.Choose;
        _flow.ChoosePath(path);
        if (!wasChoose)
        {
            await EnterCurrentAsync(CancellationToken.None).ConfigureAwait(true);
        }
    }

    private void OnFlowChanged(object? sender, EventArgs e) => Refresh();

    private void Forget(AiSetupStepId id)
    {
        var step = _steps[id];
        _steps.Remove(id);
        step.PropertyChanged -= OnStepPropertyChanged;
        step.NextRequested -= OnStepNextRequested;
        step.Dispose();
    }

    private void DisposeSteps()
    {
        foreach (var id in _steps.Keys.ToList())
        {
            Forget(id);
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoNext));
    }

    private void Refresh()
    {
        var steps = _flow.Steps;
        var rail = new List<AiSetupRailItem>(steps.Count);
        for (var i = 0; i < steps.Count; i++)
        {
            var id = steps[i];
            var state = id == _flow.Current ? AiSetupRailState.Current : _flow.IsComplete(id) ? AiSetupRailState.Done : AiSetupRailState.Pending;
            var title = _steps.TryGetValue(id, out var made) ? made.Title : AiSetupText.StepTitle(id, IsEnglish);
            rail.Add(new AiSetupRailItem(id, i + 1, title, state));
        }

        if (!rail.SequenceEqual(_rail))
        {
            _rail = rail;
            OnPropertyChanged(nameof(Rail));
        }

        OnPropertyChanged(nameof(CurrentStep));
        OnPropertyChanged(nameof(CurrentView));
        OnPropertyChanged(nameof(CurrentTitle));
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoNext));
        OnPropertyChanged(nameof(IsLastStep));
        OnPropertyChanged(nameof(NextText));
        OnPropertyChanged(nameof(ProgressText));
    }

    private static AiSetupFlow RestoreFlow(AiSetupContext context)
    {
        var saved = context.Settings.Current.Ai.Setup;
        var flow = AiSetupFlow.Restore(saved.LastPath, saved.LastStep);
        if (flow.Path is not null)
        {
            if (flow.Path == AiSetupPath.CompanyServer && saved.LastProfileId is { } id && context.Settings.Current.Ai.Profiles.Any(p => p.Id == id))
            {
                context.CreatedProfileId = id;
            }

            return flow;
        }

        // 이어 하기가 없으면 실패 까닭에 맞는 단계부터(실패 띠: claude 없음 = 설치, 로그인 안 함 = 로그인).
        return context.Reason switch
        {
            AiSetupErrorKind.CliNotFound => AiSetupFlow.Restore(AiSetupPath.ClaudeAccount, AiSetupStepId.Install),
            AiSetupErrorKind.NotLoggedIn => AiSetupFlow.Restore(AiSetupPath.ClaudeAccount, AiSetupStepId.Login),
            _ => flow,
        };
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
