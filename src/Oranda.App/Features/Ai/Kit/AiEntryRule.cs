using Oranda.App.Features.Ai.Agent;
using Oranda.Core.Ai.Access;
using Oranda.Core.Shell;

namespace Oranda.App.Features.Ai.Kit;

/// <summary>입구 단추 하나의 상태: 켜짐 여부와 툴팁 글(꺼졌으면 까닭).</summary>
/// <param name="IsEnabled">누를 수 있다.</param>
/// <param name="ToolTip">툴팁. 꺼졌으면 까닭, 켜졌으면 단추 설명(없으면 null).</param>
public sealed record AiEntryState(bool IsEnabled, string? ToolTip)
{
    /// <summary>꺼진 까닭(켜졌으면 null).</summary>
    public string? DisabledReason => IsEnabled ? null : ToolTip;
}

/// <summary>
/// 일하는 자리의 「AI」 입구 단추 규칙(설계 §2): 켜짐 = <c>aiConfigured &amp;&amp; aiAccessOn</c> + 화면별 조건(필요하면 최소 허용 수준).
/// 꺼지면 첫 까닭 하나를 툴팁에 쓴다. 까닭의 차례 = AI 모델 설정 → 접속 허용 수준 → 기능의 최소 수준 → 화면 조건.
/// </summary>
public static class AiEntryRule
{
    /// <summary>메뉴·단추 활성 식(문맥 키). 화면 조건은 각 기능이 <c>&amp;&amp;</c> 로 덧붙인다.</summary>
    public const string Enablement = AiContextKeys.ConfiguredKey + " && " + AiContextKeys.AccessOnKey;

    /// <summary>AI 모델이 「선택 안 함」 이다.</summary>
    public const string NotConfiguredText = "AI 모델이 설정되지 않았습니다. 설정 › AI 모델에서 고르세요.";

    /// <summary>접속의 AI 허용 수준이 「끔」 이다.</summary>
    public const string AccessOffText = "이 접속은 AI 허용 수준이 「끔」 입니다. 접속 편집에서 바꾸세요.";

    /// <summary>기능에 필요한 최소 수준보다 낮다.</summary>
    public static string LevelTooLowText(AiAccessLevel required) =>
        "이 기능은 AI 허용 수준 「" + AiContextBundle.LevelLabel(required) + "」 이상에서 씁니다. 접속 편집에서 바꾸세요.";

    /// <summary>설정·접속 사실과 화면 조건으로 판단한다.</summary>
    /// <param name="aiConfigured">AI 모델이 골라져 있다.</param>
    /// <param name="aiAccessOn">접속의 허용 수준이 「끔」 이 아니다.</param>
    /// <param name="screenReason">화면 조건이 맞지 않는 까닭(맞으면 null).</param>
    /// <param name="enabledTip">켜졌을 때의 툴팁.</param>
    public static AiEntryState Evaluate(bool aiConfigured, bool aiAccessOn, string? screenReason = null, string? enabledTip = null)
    {
        if (!aiConfigured)
        {
            return new AiEntryState(false, NotConfiguredText);
        }

        if (!aiAccessOn)
        {
            return new AiEntryState(false, AccessOffText);
        }

        return string.IsNullOrWhiteSpace(screenReason) ? new AiEntryState(true, enabledTip) : new AiEntryState(false, screenReason);
    }

    /// <summary>최소 허용 수준이 있는 기능(예: 락 풀이 = 계획 이상).</summary>
    public static AiEntryState Evaluate(bool aiConfigured, AiAccessLevel level, AiAccessLevel required, string? screenReason = null, string? enabledTip = null)
    {
        var basic = Evaluate(aiConfigured, level != AiAccessLevel.Off, screenReason: null, enabledTip);
        if (!basic.IsEnabled)
        {
            return basic;
        }

        if (level < required)
        {
            return new AiEntryState(false, LevelTooLowText(required));
        }

        return Evaluate(true, true, screenReason, enabledTip);
    }

    /// <summary>문맥 키로 판단한다(메뉴와 같은 키).</summary>
    public static AiEntryState Evaluate(IContextKeys keys, string? screenReason = null, string? enabledTip = null)
    {
        ArgumentNullException.ThrowIfNull(keys);
        return Evaluate(keys.Evaluate(AiContextKeys.ConfiguredKey), keys.Evaluate(AiContextKeys.AccessOnKey), screenReason, enabledTip);
    }
}

/// <summary>
/// 입구 단추 모델: 이름 · 켜짐 · 툴팁(꺼진 까닭) · 누르면 할 일. 화면이 문맥이 바뀔 때(고른 줄·접속·설정) <see cref="Refresh"/> 를 부른다.
/// 누르는 동안 다시 누르지 못한다.
/// </summary>
/// <param name="label">단추 글(「AI 풀이」).</param>
/// <param name="evaluate">지금 상태(<see cref="AiEntryRule.Evaluate(bool, bool, string?, string?)"/>).</param>
/// <param name="run">누르면 할 일(띠 요청 또는 채팅창 새 대화).</param>
public sealed partial class AiEntryButton(string label, Func<AiEntryState> evaluate, Func<Task> run) : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    private readonly Func<AiEntryState> _evaluate = evaluate ?? throw new ArgumentNullException(nameof(evaluate));
    private readonly Func<Task> _run = run ?? throw new ArgumentNullException(nameof(run));
    private AiEntryState _state = evaluate();
    private bool _busy;

    public string Label { get; } = label;

    public bool IsEnabled => _state.IsEnabled && !_busy;

    public string? ToolTip => _state.ToolTip;

    /// <summary>상태를 다시 계산한다.</summary>
    public void Refresh()
    {
        _state = _evaluate();
        OnPropertyChanged(nameof(IsEnabled));
        OnPropertyChanged(nameof(ToolTip));
        RunCommand.NotifyCanExecuteChanged();
    }

    [CommunityToolkit.Mvvm.Input.RelayCommand(CanExecute = nameof(IsEnabled))]
    private async Task RunAsync()
    {
        Refresh();
        if (!IsEnabled)
        {
            return;
        }

        _busy = true;
        Refresh();
        try
        {
            await _run().ConfigureAwait(true);
        }
        finally
        {
            _busy = false;
            Refresh();
        }
    }
}
