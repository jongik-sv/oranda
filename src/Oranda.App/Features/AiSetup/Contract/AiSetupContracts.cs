using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Oranda.Core.Ai.Setup;

namespace Oranda.App.Features.AiSetup.Contract;

/// <summary>도우미 이름표(명령 id·창 이름). 바꾸면 매뉴얼 범위표(<c>site/scope.json</c>)도 바꾼다.</summary>
public static class AiSetupIds
{
    /// <summary>도구 메뉴 「AI 연결 도우미…」 명령 id.</summary>
    public const string OpenCommand = "oranda.ai.setup.open";

    /// <summary>도우미 창 형 이름(창 틀 레인이 만든다).</summary>
    public const string WindowName = "AiSetupWindow";

    /// <summary>매뉴얼 쪽 경로.</summary>
    public const string ManualPage = "ai/setup";
}

/// <summary>도우미를 연 곳.</summary>
public enum AiSetupEntry
{
    /// <summary>도구 메뉴.</summary>
    Menu,

    /// <summary>첫 실행 한 번 묻기.</summary>
    FirstRun,

    /// <summary>AI 실패 띠의 단추.</summary>
    FailureBand,
}

/// <summary>도우미 창을 연다. 구현 = 창 틀(<c>Features/AiSetup/Wizard</c>). 이미 열려 있으면 그 창을 앞으로 가져온다.</summary>
public interface IAiSetupLauncher
{
    /// <summary>창을 열고 닫힐 때까지 기다린다. 창을 띄울 주인 창이 없으면 아무것도 하지 않는다.</summary>
    Task OpenAsync(AiSetupEntry entry, CancellationToken ct) => OpenAsync(entry, reason: null, ct);

    /// <summary>
    /// 실패 까닭과 함께 연다(실패 띠). 창 틀은 <see cref="AiSetupContext.Reason"/> 에 넣고, 이어 하기가 없으면 까닭에 맞는 단계를 고를 수 있다
    /// (예 <see cref="AiSetupErrorKind.NotLoggedIn"/> = 갈래 A 의 로그인). 까닭이 null 이면 위 함수와 같다.
    /// </summary>
    Task OpenAsync(AiSetupEntry entry, AiSetupErrorKind? reason, CancellationToken ct);
}

/// <summary>
/// 도우미 단계 하나(화면 + 끝남). 창 틀이 왼쪽 목록·[이전][다음]을 그리고, 단계는 가운데 화면만 맡는다.
/// <see cref="IsComplete"/> 가 바뀌면 <see cref="INotifyPropertyChanged.PropertyChanged"/> 로 알린다(창 틀이 [다음]을 켠다).
/// 창 틀이 단계를 들어갈 때 <see cref="EnterAsync"/>, 떠날 때(다른 단계로·창 닫기) <see cref="LeaveAsync"/> 를 부르고, 창을 닫을 때 <see cref="IDisposable.Dispose"/> 한다.
/// </summary>
public interface IAiSetupStep : INotifyPropertyChanged, IDisposable
{
    /// <summary>이 단계의 id.</summary>
    AiSetupStepId StepId { get; }

    /// <summary>왼쪽 목록·머리에 보일 제목. 기본은 <see cref="AiSetupText.StepTitle"/>.</summary>
    string Title { get; }

    /// <summary>단계가 끝났다([다음] 켜짐).</summary>
    bool IsComplete { get; }

    /// <summary>가운데 화면. 같은 객체를 돌려준다.</summary>
    Control View { get; }

    /// <summary>
    /// 단계가 스스로 다음으로 가자고 할 때(로그인이 끝나 자동 다음 등). 창 틀은 <see cref="IsComplete"/> 가 참일 때만 옮긴다.
    /// </summary>
    event EventHandler? NextRequested;

    /// <summary>단계에 들어왔다(다시 찾기·상태 확인 시작). 실패는 화면에 보이고 예외로 내지 않는다.</summary>
    Task EnterAsync(CancellationToken ct);

    /// <summary>단계를 떠난다(로그인 끊기·확인 멈춤). 여러 번 불려도 된다.</summary>
    Task LeaveAsync(CancellationToken ct);

    /// <summary>
    /// [끝내기]를 눌렀다(마지막 단계에서만, <see cref="LeaveAsync"/> 앞). [닫기]로 닫을 때는 부르지 않는다.
    /// 해 보기의 「고급」(외부 에이전트 연결 창)처럼 끝낼 때만 하는 일을 둔다. 기본은 아무것도 안 함.
    /// </summary>
    Task FinishAsync(CancellationToken ct) => Task.CompletedTask;
}

/// <summary>
/// 단계 만들기. 각 단계 폴더가 하나씩 두고 <c>[OrandaService(typeof(IAiSetupStepFactory))]</c> 로 등록한다.
/// 창 틀은 <c>IEnumerable&lt;IAiSetupStepFactory&gt;</c> 로 모아 <see cref="StepId"/> 로 찾는다(조립 목록 파일 없음).
/// </summary>
public interface IAiSetupStepFactory
{
    /// <summary>만드는 단계의 id. 같은 id 의 팩터리는 하나만 둔다.</summary>
    AiSetupStepId StepId { get; }

    /// <summary>창 하나에 단계 하나를 만든다(창마다 새 객체).</summary>
    IAiSetupStep Create(AiSetupContext context);
}
