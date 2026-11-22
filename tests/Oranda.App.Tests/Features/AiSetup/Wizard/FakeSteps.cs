using System.ComponentModel;
using Avalonia.Controls;
using Oranda.App.Features.AiSetup.Contract;
using Oranda.Core.Ai.Setup;

namespace Oranda.App.Tests.Features.AiSetup.Wizard;

/// <summary>창 틀 시험용 가짜 단계. 끝남을 시험이 직접 바꾸고, 들어감·떠남·끝내기·버림 횟수를 센다.</summary>
internal sealed class FakeStep(AiSetupStepId id, AiSetupContext context) : IAiSetupStep
{
    private bool _complete;

    public AiSetupStepId StepId { get; } = id;

    public AiSetupContext Context { get; } = context;

    public string Title => AiSetupText.StepTitle(StepId, Context.IsEnglish);

    public bool IsComplete
    {
        get => _complete;
        set
        {
            if (_complete == value)
            {
                return;
            }

            _complete = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsComplete)));
        }
    }

    public Control View { get; } = new TextBlock { Text = "fake " + id };

    public int Enters { get; private set; }

    public int Leaves { get; private set; }

    public int Finishes { get; private set; }

    public bool Disposed { get; private set; }

    /// <summary>들어올 때 저절로 끝남으로 둔다(설치되어 있음 같은 경우).</summary>
    public bool CompleteOnEnter { get; set; }

    /// <summary>들어갈 때 다음으로 가자고 한다(이미 설치됨 → 자동 다음).</summary>
    public bool RequestNextOnEnter { get; set; }

    /// <summary>있으면 들어가기가 이것이 끝날 때까지 멈춘다(느린 확인).</summary>
    public TaskCompletionSource? EnterGate { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public event EventHandler? NextRequested;

    public void RequestNext() => NextRequested?.Invoke(this, EventArgs.Empty);

    public async Task EnterAsync(CancellationToken ct)
    {
        Enters++;
        if (EnterGate is { } gate)
        {
            await gate.Task;
        }

        if (CompleteOnEnter)
        {
            IsComplete = true;
        }

        if (RequestNextOnEnter)
        {
            RequestNext();
        }
    }

    public Task LeaveAsync(CancellationToken ct)
    {
        Leaves++;
        return Task.CompletedTask;
    }

    public Task FinishAsync(CancellationToken ct)
    {
        Finishes++;
        return Task.CompletedTask;
    }

    public void Dispose() => Disposed = true;
}

/// <summary>가짜 단계 팩터리. 만든 단계를 모두 기억한다.</summary>
internal sealed class FakeStepFactory(AiSetupStepId id) : IAiSetupStepFactory
{
    public AiSetupStepId StepId { get; } = id;

    public List<FakeStep> Created { get; } = [];

    public Action<FakeStep>? OnCreate { get; set; }

    public FakeStep Last => Created[^1];

    public IAiSetupStep Create(AiSetupContext context)
    {
        var step = new FakeStep(StepId, context);
        OnCreate?.Invoke(step);
        Created.Add(step);
        return step;
    }

    /// <summary>모든 단계의 가짜 팩터리(빠진 단계를 고를 수 있음).</summary>
    public static Dictionary<AiSetupStepId, FakeStepFactory> All(params AiSetupStepId[] except) =>
        Enum.GetValues<AiSetupStepId>().Where(s => !except.Contains(s)).ToDictionary(s => s, s => new FakeStepFactory(s));
}
