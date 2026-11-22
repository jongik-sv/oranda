namespace Oranda.Core.Ai.Setup;

/// <summary>AI 연결 도우미의 갈래(방법). 설정 파일에는 이름 문자열로 저장한다. 새 값은 맨 끝에 더한다.</summary>
public enum AiSetupPath
{
    /// <summary>갈래 A: 회사 Claude 계정(추천).</summary>
    ClaudeAccount,

    /// <summary>갈래 B: 회사 AI 서버.</summary>
    CompanyServer,

    /// <summary>갈래 C: 잘 모르겠어요(담당자에게 묻기).</summary>
    NotSure,
}

/// <summary>도우미 단계. 설정 파일에는 이름 문자열로 저장한다. 새 값은 맨 끝에 더한다.</summary>
public enum AiSetupStepId
{
    /// <summary>방법 고르기.</summary>
    Choose,

    /// <summary>설치(갈래 A).</summary>
    Install,

    /// <summary>로그인(갈래 A).</summary>
    Login,

    /// <summary>연결 시험(갈래 A·B).</summary>
    Test,

    /// <summary>회사 AI 서버(갈래 B).</summary>
    Server,

    /// <summary>AI 를 쓸 접속(갈래 A·B).</summary>
    Access,

    /// <summary>해 보기(갈래 A·B 끝).</summary>
    TryIt,

    /// <summary>담당자에게 묻기(갈래 C 끝).</summary>
    AskAdmin,
}

/// <summary>
/// 도우미의 이동 규칙(화면 없는 순수 모델). 지금 단계·끝난 단계·[다음]·[이전]을 정한다.
/// 갈래를 바꾸면 방법 고르기 뒤 단계의 「끝남」을 모두 비운다. 이어 하기는 <see cref="Restore"/> 로 되살린다(모르는 값 = 처음부터).
/// 스레드 안전하지 않다(화면 스레드에서만 쓴다).
/// </summary>
public sealed class AiSetupFlow
{
    private static readonly IReadOnlyList<AiSetupStepId> ChooseOnly = [AiSetupStepId.Choose];

    private static readonly IReadOnlyList<AiSetupStepId> ClaudeAccountSteps =
        [AiSetupStepId.Choose, AiSetupStepId.Install, AiSetupStepId.Login, AiSetupStepId.Test, AiSetupStepId.Access, AiSetupStepId.TryIt];

    private static readonly IReadOnlyList<AiSetupStepId> CompanyServerSteps =
        [AiSetupStepId.Choose, AiSetupStepId.Server, AiSetupStepId.Test, AiSetupStepId.Access, AiSetupStepId.TryIt];

    private static readonly IReadOnlyList<AiSetupStepId> NotSureSteps = [AiSetupStepId.Choose, AiSetupStepId.AskAdmin];

    private readonly HashSet<AiSetupStepId> _completed = [];

    /// <summary>갈래별 단계 순서(handoff 「흐름」). 갈래를 고르기 전(null)·모르는 값 = 방법 고르기 하나.</summary>
    public static IReadOnlyList<AiSetupStepId> StepsFor(AiSetupPath? path) => path switch
    {
        AiSetupPath.ClaudeAccount => ClaudeAccountSteps,
        AiSetupPath.CompanyServer => CompanyServerSteps,
        AiSetupPath.NotSure => NotSureSteps,
        _ => ChooseOnly,
    };

    /// <summary>
    /// 이어 하기 값으로 되살린다. 갈래와 단계가 둘 다 있고 그 갈래에 그 단계가 있으면 그 단계에서 시작하고 앞 단계는 끝난 것으로 둔다
    /// (지금 단계는 다시 확인하도록 끝나지 않음). 그 밖(없음·모르는 값·갈래에 없는 단계)은 처음부터다.
    /// </summary>
    public static AiSetupFlow Restore(AiSetupPath? path, AiSetupStepId? step)
    {
        var flow = new AiSetupFlow();
        if (path is not { } chosen || step is not { } at || !Enum.IsDefined(chosen) || !Enum.IsDefined(at))
        {
            return flow;
        }

        var steps = StepsFor(chosen);
        var index = IndexOf(steps, at);
        if (index < 0)
        {
            return flow;
        }

        flow.Path = chosen;
        for (var i = 0; i < index; i++)
        {
            flow._completed.Add(steps[i]);
        }

        flow.Current = at;
        return flow;
    }

    /// <summary>지금 단계·갈래·끝남이 바뀌었다.</summary>
    public event EventHandler? Changed;

    /// <summary>고른 갈래. 고르기 전 = null.</summary>
    public AiSetupPath? Path { get; private set; }

    /// <summary>지금 갈래의 단계 순서.</summary>
    public IReadOnlyList<AiSetupStepId> Steps => StepsFor(Path);

    /// <summary>지금 단계.</summary>
    public AiSetupStepId Current { get; private set; } = AiSetupStepId.Choose;

    /// <summary>끝난 단계 모음.</summary>
    public IReadOnlySet<AiSetupStepId> Completed => _completed;

    /// <summary>지금 단계의 자리(0 부터).</summary>
    public int CurrentIndex => IndexOf(Steps, Current);

    /// <summary>[이전] 켜짐: 첫 단계가 아니다.</summary>
    public bool CanGoBack => CurrentIndex > 0;

    /// <summary>[다음] 켜짐: 지금 단계가 끝났고 뒤 단계가 있다.</summary>
    public bool CanGoNext => IsComplete(Current) && CurrentIndex < Steps.Count - 1;

    /// <summary>마지막 단계다(갈래를 고른 뒤에만 참).</summary>
    public bool IsLastStep => Path is not null && CurrentIndex == Steps.Count - 1;

    /// <summary>[끝내기] 켜짐: 마지막 단계가 끝났다.</summary>
    public bool CanFinish => IsLastStep && IsComplete(Current);

    /// <summary>그 단계가 끝났는가.</summary>
    public bool IsComplete(AiSetupStepId step) => _completed.Contains(step);

    /// <summary>
    /// 갈래를 고른다. 방법 고르기 단계는 끝난 것으로 둔다. 다른 갈래로 바꾸면 방법 고르기 뒤 단계의 「끝남」을 모두 비우고 지금 단계를 방법 고르기로 둔다.
    /// 같은 갈래면 끝남·지금 단계를 그대로 둔다(되살린 뒤 같은 갈래를 다시 넣어도 이어 하기가 깨지지 않는다).
    /// </summary>
    public void ChoosePath(AiSetupPath path)
    {
        if (!Enum.IsDefined(path))
        {
            throw new ArgumentOutOfRangeException(nameof(path), path, "모르는 갈래");
        }

        if (Path == path)
        {
            if (_completed.Add(AiSetupStepId.Choose))
            {
                OnChanged();
            }

            return;
        }

        _completed.Clear();
        Path = path;
        _completed.Add(AiSetupStepId.Choose);
        Current = AiSetupStepId.Choose;
        OnChanged();
    }

    /// <summary>지금 갈래의 단계 하나의 끝남을 바꾼다(단계 화면의 끝남 알림을 옮긴다). 갈래에 없는 단계는 무시한다.</summary>
    public void SetComplete(AiSetupStepId step, bool complete)
    {
        if (IndexOf(Steps, step) < 0 || IsComplete(step) == complete)
        {
            return;
        }

        if (complete)
        {
            _completed.Add(step);
        }
        else
        {
            _completed.Remove(step);
        }

        OnChanged();
    }

    /// <summary>다음 단계로 간다. <see cref="CanGoNext"/> 가 거짓이면 아무것도 안 하고 false.</summary>
    public bool GoNext()
    {
        if (!CanGoNext)
        {
            return false;
        }

        Current = Steps[CurrentIndex + 1];
        OnChanged();
        return true;
    }

    /// <summary>앞 단계로 간다. 끝남은 그대로 둔다. 첫 단계면 false.</summary>
    public bool GoBack()
    {
        if (!CanGoBack)
        {
            return false;
        }

        Current = Steps[CurrentIndex - 1];
        OnChanged();
        return true;
    }

    private static int IndexOf(IReadOnlyList<AiSetupStepId> steps, AiSetupStepId step)
    {
        for (var i = 0; i < steps.Count; i++)
        {
            if (steps[i] == step)
            {
                return i;
            }
        }

        return -1;
    }

    private void OnChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
