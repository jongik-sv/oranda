using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Oranda.App.Features.Ai.Chat;
using Oranda.App.Features.Ai.Report;
using Oranda.App.Features.Ai.Tuning.Commands;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Ai.Schemas;
using Oranda.Core.Execution;
using Oranda.Core.Extensibility;
using Oranda.Core.Settings;
using Oranda.Core.Text;
using Oranda.Core.Tuning;
using Oranda.Language.Tuning;

namespace Oranda.App.Features.Ai.Tuning;

/// <summary>
/// 「이 문장 튜닝」 실행 흐름(u5): 시작 확인 창의 선택으로 <see cref="AiRequest"/> 를 만들고, <see cref="IAiDriver"/> 사건을 대화 창에 옮기고,
/// 끝의 구조 결과(<see cref="TuningResult"/>)를 반영한다. 반영 규칙은 둘뿐이다.
/// <list type="bullet">
/// <item>재작성 후보 → 차이 미리보기(<c>previewEdit</c>)에서 사용자가 받아들일 때만, 원래 탭은 그대로 두고 「원래 탭명 (튜닝)」 새 탭에 넣는다.</item>
/// <item>인덱스 CREATE 문안 → 새 탭에 글로만 넣는다. 실행하지 않는다.</item>
/// </list>
/// 멈춤은 진행 중인 요청을 취소하고, 이어 말하기는 앞 요청의 <c>ResumeId</c> 로 다음 요청을 보낸다.
/// </summary>
/// <param name="editor">활성 편집기(문장 읽기와 제안 넣기).</param>
/// <param name="driver">AI 호출 경로.</param>
/// <param name="chat">대화 창 모델.</param>
/// <param name="openTab">글을 새 탭으로 연다(제목, 글, 접속 프로필 — 없으면 null). <paramref name="tabs"/> 가 없을 때만 쓴다.</param>
/// <param name="refreshContext">이어 말하기 때 접속의 지금 설정으로 문맥을 다시 만든다(옛 허용 수준으로 새 요청이 열리지 않게). null 을 돌려주면 이어 말하지 않는다.</param>
/// <param name="sessionFactory">도구 호출의 실행 범위를 만든다(문맥, 이 대화의 값 사전). null 이거나 null 을 돌려주면 도구 없이 보낸다.</param>
/// <param name="masker">SQL 글의 리터럴을 가리고 되돌리는 구현. 가림이 필요한데 null 이면 시작하지 않는다.</param>
/// <param name="maskRequired">이 문맥의 접속에서 리터럴 가림이 필요한가(운영 접속이거나 설정이 켜짐). null 이면 가리지 않는다.</param>
/// <param name="tabs">
/// 시작한 탭을 기억해 그 탭 기준으로 제안을 새 「(튜닝)」 탭에 넣고, 인덱스 후보 탭을 대화당 하나만 열고 바꾼다. null 이면 활성 탭의 문장으로 비교하고 (튜닝) 탭·인덱스 후보 탭을 매번 <paramref name="openTab"/> 으로 연다.
/// </param>
/// <param name="showComparison">적용한 제안을 보기만 하는 비교 창(제목, 원본, 적용한 글). null 이면 적용한 카드에 「비교 보기」 가 없다.</param>
/// <param name="previewEdit">
/// 제안을 보이는 비교 창(고칠 수 있음): 제안 편집, 바뀌는 자리의 원래 글 → 받아들이면 고친 최종 글, 거절하면 null. 편집기의 글은 바꾸지 않는다(제안은 새 탭에 들어간다).
/// null 이면 비교 창을 쓸 수 없어 제안을 넣지 않는다.
/// </param>
/// <param name="settings">지금 설정의 <c>ai</c> 섹션. 대화를 시작할 때 보낼 곳(프로필 id·주소·형식·모델)을 기억하고 이어 말할 때 달라졌는지 확인하며, 라우터에 넘기는 <see cref="AiRequest.Expected"/> 를 채운다. null 이면 확인·기대값 없이 보낸다(시험).</param>
/// <param name="confirmImages">가리는 대화에 새 이미지를 보낼 때 띄우는 확인 창(참이면 보냄). 이 대화가 가리는 중인데 null 이면 이미지는 보내지 않는다(확인 없이 나가지 않게).</param>
/// <param name="auditImages">새 이미지를 보낸다는 사실(장수·크기·받는 곳)을 감사 기록에 남긴다. 실패해도 보내기를 막지 않고 대화 창에 알린다(게이트의 감사와 같은 규칙).</param>
/// <param name="generalContext">
/// 일반 대화(튜닝 없이 보내는 글)의 접속 정보를 보낼 때마다 만든다(<see cref="AiGeneralScope"/>): 활성 접속의 지금 설정과 가리는 대화인지.
/// 연결이 없으면 문맥이 null 이고 DB 도구 없이 답한다. 이 인자 자체가 null 이면 항상 연결 없음·가리지 않음으로 본다(시험).
/// </param>
/// <param name="generalSessionFactory">일반 대화의 도구 실행 범위를 만든다(튜닝의 바인드 값을 싣지 않는다). null 이면 <paramref name="sessionFactory"/> 를 쓴다.</param>
/// <param name="confirmText">가리는 일반 대화에서 입력한 글이 가려지지 않고 나간다는 확인 창(대화당 한 번, 참이면 보냄). 가리는 대화인데 null 이면 보내지 않는다(확인 없이 나가지 않게).</param>
/// <param name="readIndexes">표의 실제 기존 인덱스 열 구성(소유자 — 모르면 빈 글, 표). AI 인덱스 후보를 받은 뒤 대조에 쓴다. null 이거나 못 읽으면 대조를 건너뛴다(「확인 못함」).</param>
/// <param name="gate">
/// 도구 호출 관문. 기타 CLI 에는 도구가 없어 AI 명령의 문맥을 프롬프트 전에 앱이 이 관문으로 미리 읽는다(감사·허용 수준·취소·제한 시간은 관문 그대로).
/// null 이면 미리 읽을 길이 없으므로 미리 읽어야 하는 기타 CLI 명령은 보내지 않는다.
/// </param>
public sealed partial class AiTuningFlow(
    IEditorContext editor,
    IAiDriver driver,
    AiChatViewModel chat,
    Action<string, string, Guid?> openTab,
    Func<AiContext, LiteralVault?, AgentSession?>? sessionFactory = null,
    Func<AiContext, CancellationToken, Task<AiContext?>>? refreshContext = null,
    ISqlLiteralMasker? masker = null,
    Func<AiContext, bool>? maskRequired = null,
    IEditorTabs? tabs = null,
    Func<string, string, string, Task>? showComparison = null,
    Func<string, string, CancellationToken, Task<IReadOnlyList<IndexColumnsRow>?>>? readIndexes = null,
    Func<TextEdit, string, CancellationToken, Task<string?>>? previewEdit = null,
    Func<AiSettings?>? settings = null,
    Func<AiImageSendNotice, CancellationToken, Task<bool>>? confirmImages = null,
    Func<AiImageSendNotice, CancellationToken, Task>? auditImages = null,
    Func<CancellationToken, Task<AiGeneralScope>>? generalContext = null,
    Func<AiContext, LiteralVault?, AgentSession?>? generalSessionFactory = null,
    Func<AiImageSendNotice, CancellationToken, Task<bool>>? confirmText = null,
    IAgentGate? gate = null)
{
    /// <summary>도구를 부르는 횟수 예산(프롬프트로 알린다).</summary>
    public const int ToolBudget = 12;

    /// <summary>가림이 필요한데 가림 구현이 없을 때 대화 창에 남기는 안내.</summary>
    public const string MaskerMissingText = "이 접속은 SQL 의 리터럴 값을 가려서 보내야 하는데 가림 기능을 쓸 수 없어 튜닝을 시작하지 않았습니다.";

    /// <summary>이어 말하는 사이 보낼 곳(프로필·주소·형식·모델)이 바뀌었을 때 대화 창에 남기는 안내.</summary>
    public const string ProfileChangedText = "모델이 바뀌어 이어 말할 수 없습니다. 「AI 튜닝」 으로 다시 시작하세요.";

    /// <summary>이어 말하는 사이 보낼 곳이 기타 CLI 로 바뀌었을 때의 안내(기타 CLI 는 튜닝을 거절하므로 실제 출구인 새 대화를 알린다).</summary>
    public const string ProfileChangedToCustomCliText = "모델이 기타 CLI 로 바뀌어 이어 말할 수 없습니다. 기타 CLI 는 튜닝을 지원하지 않으므로 「새 대화」 로 시작하세요.";

    private static string ProfileChangedTextFor(AiConversationTarget? now) =>
        AiTuningCustomCli.IsCustom(now) ? ProfileChangedToCustomCliText : ProfileChangedText;

    /// <summary>이어 말하는 중 예외가 났을 때 화면에 보이는 고정 글(상세는 가린 뒤 추적 기록에만 남긴다).</summary>
    public const string FollowUpFailedText = "이어 말하는 중 오류가 났습니다. 자세한 원인은 로그에 남겼습니다.";

    /// <summary>가리는 대화인데 이미지 확인 창을 열 수 없을 때의 안내(확인 없이 보내지 않는다).</summary>
    public const string ImageConfirmMissingText = "이미지는 가려지지 않아 확인이 필요한데 확인 창을 열 수 없어 보내지 않았습니다.";

    /// <summary>문장을 가리지 못했을 때 대화 창에 남기는 안내.</summary>
    public const string MaskFailedText = "문장의 리터럴 값을 가리지 못해(닫히지 않은 문자열 등) 튜닝을 시작하지 않았습니다. 문장을 고친 뒤 다시 시도하세요.";

    private AiContext? _context;
    private AiConversationTarget? _route; // 대화를 시작할 때의 보낼 곳. 이어 말하기·이미지 경고·라우터 기대값의 기준이다
    private TuningStartChoice? _choice;
    private AiCommandSpec? _command; // 이 대화를 연 AI 명령(튜닝 대화면 null). 수명: 새 튜닝·새 명령 시작이 확정될 때 바뀌고, 일반 대화로 넘어가면 지운다
    private SqlStatement? _target;
    private LiteralVault? _vault;
    private Guid? _startTabId;
    private Guid? _indexTabId;
    private Guid? _tuneTabId;
    private CancellationTokenSource? _running;
    private TuningResult? _lastResult;
    private string _promptStatement = string.Empty; // 대화에 보낸 대상 문장(가린 대화면 가린 글): 레포트용
    private DateTimeOffset _startedAt;
    private readonly List<AiTuningReportTurn> _reportTurns = []; // 차례별 구조 결과(레포트용): 이어 말하기가 앞 차례 결과를 덮지 않게 모은다
    private int _generation;
    private bool _preparing; // 이어 말하기가 확인·감사를 거치는 중(요청이 시작되기 전)이다: 겹쳐 시작하지 않는다

    /// <summary>
    /// 대화 창의 멈춤·이어 말하기를 이 흐름에 잇는다(창당 한 번). <paramref name="followUps"/> 가 거짓이면 이어 말하기는 잇지 않는다
    /// (대화 창 앞에서 보낼 곳을 먼저 확인하는 쪽이 <see cref="FollowUpAsync(ChatFollowUp, CancellationToken)"/> 를 직접 부른다).
    /// </summary>
    public void Attach(bool followUps = true)
    {
        chat.StopRequested += (_, _) => Stop();
        if (!followUps)
        {
            return;
        }

        chat.FollowUpRequested += async (_, followUp) =>
        {
            try
            {
                await FollowUpAsync(followUp).ConfigureAwait(true);
            }
#pragma warning disable CA1031 // 이벤트 처리기라 예외를 밖으로 내면 앱 전역 미처리 예외가 된다: 대화 창에 알린다.
            catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
            {
                System.Diagnostics.Trace.TraceError("이어 말하기 중 예외: {0}", AiErrorText.Redact(ex.GetType().Name + ": " + ex.Message));
                chat.AddAssistantText(FollowUpFailedText);
            }
        };
    }

    /// <summary>
    /// 지금 대화를 연 AI 명령(없으면 null = 튜닝). 대화 창이 튜닝 대화가 아니게 되면(새 대화·일반 대화) 앞 명령을 물려받지 않도록 null 로 본다.
    /// 시험·이어 말하기 확인용.
    /// </summary>
    internal AiCommandSpec? Command => chat.IsTuning ? _command : null;

    /// <summary>마지막으로 반영한 구조 결과(시험·이어 말하기 확인용).</summary>
    public TuningResult? LastResult => _lastResult;

    /// <summary>
    /// 대화가 새로 확정될 때마다 늘어난다(시작이 문장 없음·가림 불가로 거부되면 그대로). 호출하는 쪽이 이 값으로 「이번 시작이 앞 대화를 바꿨는지」 를 안다:
    /// 거부된 시작이 대화에 딸린 상태(접속 프로필 등)를 바꿔 두면 앞 대화의 이어 말하기가 엉뚱한 접속으로 간다.
    /// </summary>
    public int Generation => _generation;

    /// <summary>요청이 진행 중이다(다시 시작하거나 이어 말할 수 없다).</summary>
    public bool IsRunning => _running is not null;

    /// <summary>진행 중인 요청을 멈춘다.</summary>
    /// <summary>「로컬 AI」 로 시작한 실행이 도는 중에 지정이 풀렸거나 보낼 곳이 바뀌어 멈췄을 때의 안내.</summary>
    public const string LocalAiLapsedText = "「로컬 AI」 지정이나 AI 모델이 바뀌어 실행을 멈췄습니다. 값을 가리지 않은 결과를 더 보내지 않습니다.";

    /// <summary>
    /// 「로컬 AI」 로 시작한 실행이 도는 중에 설정이 바뀌어 보낼 곳이 달라졌으면 실행을 멈춘다(D-045). 앞으로의 도구 결과가 가리지 않은 채 나가지 않게 한다.
    /// 설정을 다시 읽는 곳은 여기뿐이고, 게이트는 시작 때 담은 값만 본다. 멈췄으면 true.
    /// </summary>
    public bool StopIfLocalAiLapsed()
    {
        if (!IsRunning || _route is not { LocalAi: true } started || CurrentTarget() is not { } now || started.IsSameAs(now))
        {
            return false;
        }

        Stop();
        chat.AddAssistantText(LocalAiLapsedText);
        return true;
    }

    public void Stop()
    {
        _running?.Cancel();
        if (_preloading is { } preloading)
        {
            _preloadStopRequested = true;
            preloading.Cancel(); // 기타 CLI 명령의 문맥 미리 읽기도 멈춘다
        }
    }

    /// <summary>
    /// 새 튜닝을 시작한다. 시작할 문장이 없으면(커서가 문장 밖) 대화 창에 안내만 남기고 끝낸다.
    /// </summary>
    public async Task StartAsync(TuningStartChoice choice, AiContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(choice);
        ArgumentNullException.ThrowIfNull(context);
        if (IsRunning || _preparing)
        {
            chat.AddAssistantText("튜닝이 진행 중입니다. 멈춤을 누른 뒤 다시 시작하세요.");
            return;
        }

        // 기타 CLI 는 DB 도구가 없어 튜닝에 쓰지 않는다. 아래 _route 와 같은 값으로 판단해, 확인한 뒤 설정이 바뀌어도 어긋나지 않게 한다.
        var route = CurrentTarget();
        if (AiTuningCustomCli.IsCustom(route))
        {
            chat.AddAssistantText(AiTuningCustomCli.TuningText);
            return;
        }

        context = context with { LocalAi = route?.LocalAi == true }; // 값을 가릴지도 실제 전송처(_route)와 같은 값으로 정한다(D-045)

        var statement = editor.GetCurrentStatement();
        if (statement is null)
        {
            chat.AddAssistantText("튜닝할 문장을 찾지 못했습니다. 커서를 문장 안에 두고 다시 시도하세요.");
            return;
        }

        // 가림이 필요하면 대화 하나에 쓸 사전을 만들고 문장을 가린다. 못 하면 원래 글을 보내지 않고 시작을 거부한다.
        if (!TryMaskStatement(context, statement, MaskerMissingText, MaskFailedText, out var vault, out var promptStatement))
        {
            return;
        }

        _command = null; // 튜닝 대화다(앞 명령 대화의 종류가 남지 않게)
        _route = route;
        _choice = choice;
        _target = statement;
        _vault = vault;
        _promptStatement = promptStatement;
        _startedAt = DateTimeOffset.Now;
        _startTabId = tabs is null ? null : editor.GetActiveDocument()?.TabId; // 문장을 읽은 탭: 튜닝하는 사이 다른 탭이 활성이 돼도 이 탭 기준으로 제안을 넣는다
        _indexTabId = null; // 인덱스 후보 탭은 대화 하나에 하나다
        _tuneTabId = null; // 「(튜닝)」 탭도 대화 하나에 하나다
        _generation++;
        _reportTurns.Clear();
        _context = context with { StatementText = statement.Text, AllowRiskyFunctions = choice.RunRiskyFunctions }; // 원문: 범위 검사·추출기가 쓴다(LLM 으로 가지 않는다)
        chat.StartConversation("튜닝 — " + Shorten(promptStatement, 40));
        chat.IsBusy = true;
        try
        {
            await RunAsync(BuildPrompt(choice, promptStatement, vault, JoinSyntax), resumeId: null, userText: null, images: [], ct).ConfigureAwait(true);
        }
        finally
        {
            chat.IsBusy = false;
        }
    }

    /// <summary>
    /// 이 문맥의 접속이 리터럴 가림을 요구하면 대화 하나에 쓸 사전을 만들고 <paramref name="statement"/> 를 가린다(튜닝과 명령이 같이 쓴다).
    /// 가림 구현이 없거나 가리지 못하면 원래 글을 보내지 않도록 <paramref name="missingText"/>·<paramref name="failedText"/> 를 대화 창에 남기고 false.
    /// 가림이 필요 없으면 사전 없이 원래 글로 true.
    /// </summary>
    private bool TryMaskStatement(AiContext context, SqlStatement statement, string missingText, string failedText, out LiteralVault? vault, out string promptStatement)
    {
        vault = null;
        promptStatement = statement.Text;
        if (maskRequired?.Invoke(context) != true)
        {
            return true;
        }

        if (masker is null)
        {
            chat.AddAssistantText(missingText);
            return false;
        }

        try
        {
            vault = masker.CreateVault(statement.Text);
            var masked = masker.Mask(statement.Text, vault);
            if (!masked.Succeeded)
            {
                vault = null;
                chat.AddAssistantText(failedText);
                return false;
            }

            vault.RememberStatement(masked.Text, statement.Text);
            promptStatement = masked.Text;
            return true;
        }
#pragma warning disable CA1031 // 가림 구현의 어떤 실패든 원래 글을 보내는 쪽으로 이어지면 안 된다: 시작을 거부한다.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            vault = null;
            promptStatement = statement.Text;
            chat.AddAssistantText(failedText);
            return false;
        }
    }

    /// <summary>글만 이어 말한다(<see cref="FollowUpAsync(ChatFollowUp, CancellationToken)"/> 의 이미지 없는 형태).</summary>
    public Task FollowUpAsync(string text, CancellationToken ct = default) => FollowUpAsync(new ChatFollowUp(text, []), ct);

    /// <summary>
    /// 앞 대화에 이어 말한다(<see cref="AiChatViewModel.ResumeId"/> 가 없으면 새 대화로 보낸다). 아무것도 보내지 않고 끝나면(보낼 곳이 바뀜·허용 수준 변경·확인 창 취소·
    /// 준비 중 예외 등) 입력칸이 이미 비워졌으므로 글과 첨부를 <see cref="AiChatViewModel.RestoreFollowUp"/> 으로 되돌린다. 요청을 시작한 뒤의 실패는 대화 줄이 남으므로 되돌리지 않는다.
    /// </summary>
    public async Task FollowUpAsync(ChatFollowUp followUp, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(followUp);
        if (!chat.IsTuning)
        {
            await GeneralFollowUpAsync(followUp, ct).ConfigureAwait(true); // 튜닝 없이 시작한 일반 대화
            return;
        }

        if (_preparing)
        {
            chat.AddAssistantText("앞서 보낸 이어 말하기를 준비하는 중입니다. 끝난 뒤 다시 보내세요.");
            chat.RestoreFollowUp(followUp); // 앞 이어 말하기가 확인·감사 중이다: 겹쳐 보내지 않는다
            return;
        }

        string? prompt;
        var conversation = chat.ConversationGeneration;
        var route = _route; // 확인 창에 적는 받는 곳: 기다리는 사이 바뀌지 않았는지 보고, 실제 요청의 기대값도 이것으로 채운다
        _preparing = true;
        chat.IsBusy = true; // 준비하는 동안(확인 창·감사) 새 대화로 바뀌지 않게
        try
        {
            try
            {
                prompt = await PrepareFollowUpAsync(followUp, route, ct).ConfigureAwait(true);
            }
            catch
            {
                chat.RestoreFollowUp(followUp);
                throw;
            }
            finally
            {
                _preparing = false;
            }

            if (prompt is null)
            {
                chat.RestoreFollowUp(followUp);
                return;
            }

            if (conversation != chat.ConversationGeneration)
            {
                chat.RestoreFollowUp(followUp); // 준비하는 사이 대화가 바뀌었다: 옛 대화의 요청을 새 대화로 보내지 않는다
                return;
            }

            await RunAsync(prompt, chat.ResumeId, followUp.Text, followUp.Images, ct, route).ConfigureAwait(true);
        }
        finally
        {
            chat.IsBusy = false;
        }
    }

    /// <summary>이어 말하기를 보내도 되는지 확인하고(보낼 곳·허용 수준·가림·새 이미지 확인) 요청 글을 만든다. 보내면 안 되면 사유를 대화 창에 남기고 null.</summary>
    private async Task<string?> PrepareFollowUpAsync(ChatFollowUp followUp, AiConversationTarget? route, CancellationToken ct)
    {
        var generation = _generation;
        if (_context is null || _choice is null || IsRunning)
        {
            return null;
        }

        if (followUp.Images.Count > AiImageNormalizer.MaxCount)
        {
            chat.AddAssistantText(string.Create(CultureInfo.CurrentCulture, $"이미지는 한 번에 {AiImageNormalizer.MaxCount}장까지 보낼 수 있습니다."));
            return null;
        }

        // 보낼 곳(프로필·주소·형식·모델)이 대화를 시작할 때와 다르면 앞 대화의 ResumeId 를 쓸 수 없고, 앞서 보낸 이미지가 경고 없이 새로운 곳으로 가면 안 된다.
        if (route is { } started && CurrentTarget() is { } now && !started.IsSameAs(now))
        {
            chat.AddAssistantText(ProfileChangedTextFor(now));
            return null;
        }

        // 기타 CLI 는 이미지를 받지 않는다. 입구를 닫아도 설정을 바꾸기 전에 붙인 첨부가 남을 수 있어 보내기 직전에 다시 막는다.
        if (AiTuningCustomCli.IsCustom(route) && followUp.Images.Count > 0)
        {
            chat.AddAssistantText(AiTuningCustomCli.ImagesText);
            return null;
        }

        if (refreshContext is not null)
        {
            var fresh = await refreshContext(_context, ct).ConfigureAwait(true);
            if (fresh is null || fresh.AccessLevel == AiAccessLevel.Off)
            {
                chat.AddAssistantText("이 접속의 AI 허용 수준이 바뀌어 이어 말할 수 없습니다. 「AI 튜닝」 으로 다시 시작하세요.");
                return null;
            }

            fresh = fresh with { LocalAi = _context.LocalAi }; // 보낼 곳이 시작 때와 같음을 위에서 확인했다

            // 이어 말하는 사이 접속이 운영으로 바뀌었는데 이 대화는 사전 없이 시작했다면, 앞서 보낸 글에 원래 값이 이미 실려 있다: 새로 시작하게 한다.
            if (_vault is null && maskRequired?.Invoke(fresh) == true)
            {
                chat.AddAssistantText("이 접속은 이제 리터럴 값을 가려서 보내야 해 이어 말할 수 없습니다. 「AI 튜닝」 으로 다시 시작하세요.");
                return null;
            }

            // 대화마다 고른 값을 이어 말하기에도 쓴다. 명령 대화는 접속의 지금 수준으로 되돌아온 값에 명령의 상한을 다시 건다(안 걸면 이어 말하기에서 벤치마크가 열린다).
            _context = fresh with { AllowRiskyFunctions = _choice!.RunRiskyFunctions, AccessLevel = Cap(Command, fresh.AccessLevel) };
        }

        // 기타 CLI 는 값을 가려 보낼 수 없다: 이어 말하는 사이 접속이 운영으로 바뀌었거나 가림 설정이 켜졌으면 보내지 않는다.
        if (AiTuningCustomCli.IsCustom(route) && MaskingRequiredFor(_context, _vault))
        {
            chat.AddAssistantText(AiTuningCustomCli.MaskingBlockedText);
            return null;
        }

        // 새 이미지를 보낼 때만(글만 보내는 이어 말하기·이미 보낸 이미지는 해당 없음): 가리는 대화면 확인을 거치고, 보낸다는 사실을 감사에 남긴다.
        if (followUp.Images.Count > 0
            && !await ConfirmAndAuditImagesAsync(followUp, route, _context, _vault is not null || maskRequired?.Invoke(_context) == true, ct).ConfigureAwait(true))
        {
            return null;
        }

        // 기다리는 동안(접속 새로 고침·확인 창·감사) 대화가 바뀌었거나 보낼 곳이 달라졌으면 확인한 곳과 다른 곳으로 나가지 않도록 접는다.
        if (generation != _generation || IsRunning || (route is { } confirmed && CurrentTarget() is { } latest && !confirmed.IsSameAs(latest)))
        {
            chat.AddAssistantText(ProfileChangedTextFor(CurrentTarget()));
            return null;
        }

        // 이어 말하기 글은 가리지 않고(사용자가 쓴 글) 자리표시를 그대로 쓰라는 안내만 덧붙인다.
        var text = followUp.Text.Length == 0 ? "Look at the attached image(s) and continue." : followUp.Text;
        var rules = Command is { } command ? CommandRules(command, JoinSyntax) : ResponseStyle + Environment.NewLine + KeepCommentsRule + Environment.NewLine + TuningJoinRule(JoinSyntax);
        return text + Environment.NewLine + Environment.NewLine + rules
            + (_vault is null ? string.Empty : Environment.NewLine + PlaceholderReminder(_vault));
    }

    /// <summary>
    /// 이 요청의 글을 가려 보내야 하는가(<see cref="AiRequest.MaskingRequired"/>). 사전이 있거나 가림 판단을 모르면(판단 쪽이 없음) 참으로 닫는다.
    /// 가림이 필요 없다고 확인된 요청만 거짓이며, 이 값을 읽는 것은 값을 가려 보낼 수 없는 기타 CLI 의 라우터뿐이다.
    /// </summary>
    private bool MaskingRequiredFor(AiContext context, LiteralVault? vault) =>
        vault is not null || maskRequired is null || maskRequired(context);

    /// <summary>이 대화가 지금 보낼 곳(설정을 모르면 null).</summary>
    private AiConversationTarget? CurrentTarget() => settings?.Invoke() is { } current ? AiConversationTarget.From(current) : null;

    /// <summary>설정의 조인 문법. 요청을 만들 때마다 읽으므로 대화 중에 바꾸면 다음 요청부터 따른다. 설정을 모르면 Oracle.</summary>
    private AiJoinSyntax JoinSyntax => settings?.Invoke()?.JoinSyntax ?? AiJoinSyntax.Oracle;

    /// <summary>
    /// 새 이미지의 확인(가리는 대화만)과 감사 기록. 보내도 되면 true. 확인 창을 취소하면 false 이고 아무것도 보내지 않으며 감사에도 남기지 않는다(보내지 않았으므로).
    /// 감사 기록 실패는 보내기를 막지 않는다(게이트의 감사와 같은 규칙).
    /// </summary>
    private async Task<bool> ConfirmAndAuditImagesAsync(ChatFollowUp followUp, AiConversationTarget? route, AiContext context, bool masking, CancellationToken ct)
    {
        var notice = new AiImageSendNotice(
            context.ConnectionName ?? string.Empty, context.Environment ?? Oranda.Core.Connection.ConnectionEnvironment.Development, route?.Label ?? "AI", route?.RecipientUrl,
            followUp.Images.Count, ChatImage.TotalBytes(followUp.Images), masking, context.AccessLevel);
        if (masking)
        {
            if (confirmImages is null)
            {
                chat.AddAssistantText(ImageConfirmMissingText);
                return false;
            }

            if (!await confirmImages(notice, ct).ConfigureAwait(true))
            {
                return false;
            }
        }

        if (auditImages is not null)
        {
            try
            {
                await auditImages(notice, ct).ConfigureAwait(true);
            }
#pragma warning disable CA1031 // 감사 저장 실패가 이어 말하기를 막지 않는다(저장소 계약). 시간 제한으로 취소된 것도 실패다: 호출 쪽이 취소한 것만 밖으로 낸다.
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
#pragma warning restore CA1031
            {
                chat.AddAssistantText("이미지를 보낸다는 기록을 감사 기록에 남기지 못했습니다.");
            }
        }

        return true;
    }

    /// <summary>시작 확인 창의 선택과 대상 문장으로 요청 글을 만든다(문장은 씨앗, 나머지는 도구로 가져오게 한다). <paramref name="vault"/> 가 있으면 문장은 이미 가려진 글이고 자리표시 안내가 붙는다.</summary>
    public static string BuildPrompt(TuningStartChoice choice, string statement, LiteralVault? vault = null, AiJoinSyntax joinSyntax = AiJoinSyntax.Oracle)
    {
        ArgumentNullException.ThrowIfNull(choice);
        var text = new StringBuilder();
        text.AppendLine("Tune this Oracle SQL statement. Use the Oranda tools to inspect the plan, tables, indexes and statistics; do not guess.");
        text.AppendLine(CultureInfo.InvariantCulture, $"Call at most {ToolBudget} tools. Answer in Korean. Return the structured result.");
        text.AppendLine(choice.Benchmark
            ? "You may compare candidates with benchmark_queries and compare_results."
            : "Do not benchmark: do not run candidate statements.");
        text.AppendLine(choice.IndexDdl
            ? IndexRules(choice.Benchmark)
            : "Do not suggest indexes; leave index_suggestions empty.");
        text.AppendLine(ResponseStyle);
        text.AppendLine(KeepCommentsRule);
        text.AppendLine(TuningJoinRule(joinSyntax));
        if (vault is not null)
        {
            text.AppendLine(PlaceholderReminder(vault));
        }

        text.AppendLine();
        text.AppendLine("<statement>");
        text.AppendLine(statement);
        text.AppendLine("</statement>");
        return text.ToString();
    }

    /// <summary>
    /// 응답 규칙(처음 요청과 이어 말하기 모두). 결론 한 줄(굵게) → 원문·제안 수치 표 → 근거 블릿 → 제안 → 뺀 후보 → 다음 → 확인할 것(인용 한 줄).
    /// 대화 창이 결론 줄+표를 결론 상자로, 인용을 주의 상자로 그린다. 본문 SQL 은 구조 결과의 제안 항목에만 둔다.
    /// 도구·함수 이름은 쓰지 않고 사람 말(벤치마크·실행 계획·튜닝 정보)로, 지표 이름도 한국어로 쓴다.
    /// </summary>
    internal const string ResponseStyle =
        "Response style (applies to every answer, including follow-ups; the summary field is this chat answer in Markdown): write in Korean and be brief. "
        + "Use this order and skip a part that has nothing to say. "
        + "1) The conclusion as one bold line, for example **keep_last 재작성 · 37% 빨라짐**; a second plain line may add how it was measured. "
        + "2) Right after it, a Markdown table of the numbers with three columns: 지표 | 원문 | 제안; name the metrics in Korean (경과, 논리 읽기, 결과 행, 계획 해시 and so on, not elapsed or logical reads); put numbers in the table, not in sentences. "
        + "3) ## 근거: 3 to 5 bullets, each about 40 characters on one line, ending in the noun style 「~함」 or 「~음」, quoting plan or statistics numbers. "
        + "4) ## 제안: one line per rewrite or index; put the full SQL only in the structured result, not in the chat text. "
        + "5) ## 뺀 후보: candidates you measured and dropped, one line each with why. "
        + "6) ## 다음: what could be tried next, marked 미측정 if not measured. "
        + "7) One line starting with > that says what the user must check before applying. "
        + "Use bold only for the conclusion line. No [name] tags, no long remarks in parentheses, no preamble or closing remarks, "
        + "do not narrate each tool call and do not repeat yourself. "
        + "Never write tool or function names (get_*, benchmark_queries, explain_plan and so on) in the answer; say what was done in plain words such as 벤치마크, 실행 계획, 튜닝 정보.";

    /// <summary>
    /// 인덱스 제안 규칙(인덱스 DDL 을 켠 요청만): 기존 인덱스를 먼저 확인하고, 변형(modify)을 새 인덱스(create)보다 먼저 고려하며,
    /// 열은 조건·조인 열만 4개 이하로 하고 조회 열을 담는 커버링은 측정 근거가 있을 때만 쓰게 한다.
    /// </summary>
    internal static string IndexRules(bool benchmark) =>
        "Index rules (index_suggestions items are text only and never executed): "
        + "(a) Before suggesting any index, check the existing indexes of that table (tuning information or index information); never suggest from the plan alone. "
        + "(b) If the leading column(s) of an existing index equal the leading column(s) of your candidate, do not create a new index: modify that index (add a column or reorder) with action=modify, "
        + "existing_index as OWNER.NAME and existing_columns as its current columns, and keep the same index name (DROP INDEX then CREATE INDEX in ddl). "
        + "Use action=create only when no existing index of the table fits. "
        + "(c) If an existing index already covers the candidate columns, do not suggest an index; suggest a hint or a rewrite instead. "
        + $"(d) Index columns are the condition and join columns only, at most {IndexAdvisor.MaxColumns}. "
        + "Do not add the selected columns just to make the index covering"
        + (benchmark
            ? "; a covering index is allowed only with benchmark evidence, and the rationale must state that measurement."
            : "; a covering index needs benchmark evidence, which is not available here, so do not suggest one.")
        + " (e) For modify, state in risk which other SQL that uses the existing index may be affected.";

    /// <summary>재작성 SQL 에 원래 주석을 남기라는 규칙(처음 요청과 이어 말하기 모두). 가린 대화면 주석 자리표시를 그대로 두라는 말이 <see cref="PlaceholderReminder"/> 에 붙는다.</summary>
    internal const string KeepCommentsRule =
        "Keep every comment of the original statement in each rewrite's sql, unchanged and next to the code it describes; do not drop, merge or reword comments.";

    /// <summary>
    /// Oracle 조인 문법 규칙의 공통 글(튜닝 <see cref="OracleJoinRule"/>·오류 고치기 <c>FixErrorCommand.FixErrorJoinRule</c> 이 const 조합으로 같이 쓴다): 쉼표 FROM·WHERE 조인 조건·<c>(+)</c>,
    /// 외부 조인 쪽 상수 조건의 <c>(+)</c>. 프롬프트 글만이다(결과가 Oracle 문법인지 검사하거나 바꾸지 않는다).
    /// </summary>
    internal const string OracleJoinSyntaxRule =
        "Oracle join syntax: list the tables in the FROM clause separated by commas, put every join condition in the WHERE clause, and mark an outer join with the (+) operator on the optional side. "
        + "Put (+) on every condition that belongs to the outer join (what was in the ON clause) and refers to the outer-joined table, including conditions against a constant, a literal or a bind variable such as t.status(+) = 'A' or t.k(+) = :v1; if you leave one out the outer join silently turns into an inner join and the result changes. "
        + "Do not put (+) on a filter that was in the WHERE clause and applies after the join, and a condition that only restricts the preserved (non-optional) table inside an ON clause cannot take (+), so keep that join in ANSI syntax. ";

    /// <summary>Oracle 문법으로 쓸 수 없을 때의 ANSI 예외와, 같은 쿼리 블록에 ANSI 와 <c>(+)</c> 를 섞지 말라는 제약(ORA-25156). 두 규칙이 공유한다.</summary>
    internal const string OracleJoinExceptionRule =
        "Use ANSI join syntax only when Oracle syntax cannot express the join, for example FULL OUTER JOIN, a partitioned outer join, LATERAL or APPLY, or a condition that (+) rejects (ORA-01417, ORA-01719, ORA-01799), and then state the reason in that proposal's rationale. "
        + "Never mix ANSI joins and (+) in one query block (ORA-25156): a query block that keeps an ANSI join must not use (+), so write every join of that block in ANSI. ";

    /// <summary>두 규칙이 공유하는 맺음: 가림 자리표시(<c>:vN</c>)와 주석 유지 규칙을 깨지 않는다.</summary>
    internal const string OracleJoinKeepRule =
        "Keep every bind variable token of the statement (such as :1, :name or :v1) exactly as it is; the rule about keeping comments still applies.";

    /// <summary>
    /// 튜닝, 설정이 Oracle 일 때: 제안하는 재작성 SQL 을 Oracle 조인 문법으로 쓴다(처음 요청과 이어 말하기 모두). 원래 문장이 ANSI 조인이어도 바꾸며 결과 집합은 같아야 한다.
    /// 설명 명령은 제안 SQL 이 없어 붙이지 않는다. 오류 고치기는 자기가 쓰거나 고치는 조인만 다루는 <c>FixErrorCommand.FixErrorJoinRule</c> 을 따로 쓴다.
    /// </summary>
    internal const string OracleJoinRule =
        "Write the sql of every rewrite you propose in " + OracleJoinSyntaxRule
        + "Do this even when the original statement uses ANSI joins (JOIN ... ON, LEFT or RIGHT OUTER JOIN): convert them, and the proposed SQL must return the same result set. "
        + OracleJoinExceptionRule
        + OracleJoinKeepRule;

    /// <summary>
    /// ANSI 조인 문법 규칙의 공통 글. <c>(+)</c> 가 붙은 조건은 상수 조건까지 ON 으로 옮기고, <c>(+)</c> 없는 조건은 조인 뒤 필터라 WHERE 에 둔다.
    /// </summary>
    internal const string AnsiJoinSyntaxRule =
        "ANSI join syntax: write every join in the FROM clause with JOIN ... ON, LEFT OUTER JOIN, RIGHT OUTER JOIN, FULL OUTER JOIN or CROSS JOIN, put each join condition in its ON clause and keep filters in the WHERE clause. "
        + "When you convert an Oracle outer join, move every condition that carries (+), including conditions against a constant, a literal or a bind variable such as t.status(+) = 'A' or t.k(+) = :v1, into that join's ON clause without the (+); if you leave one in the WHERE clause the outer join silently turns into an inner join and the result changes. "
        + "A condition on the outer-joined table without (+) is a filter applied after the join, so keep it in the WHERE clause. ";

    /// <summary>ANSI 문법으로 쓸 수 없을 때의 Oracle 예외와, 같은 쿼리 블록에 섞으면 안 되는 조합(쉼표 나열 + ANSI 는 ON 이 앞 표를 못 봄, ANSI + <c>(+)</c> 는 ORA-25156).</summary>
    internal const string AnsiJoinExceptionRule =
        "Use Oracle join syntax only when ANSI syntax cannot be used for the statement, and then state the reason in that proposal's rationale. "
        + "Never mix ANSI joins and (+) in one query block (ORA-25156), and do not put comma-separated tables and ANSI joins in one query block, because an ON clause cannot see a table listed before the comma (ORA-00904). ";

    /// <summary>튜닝, 설정이 ANSI 일 때: 제안하는 재작성 SQL 을 ANSI 조인 문법으로 쓴다. 원래 문장이 Oracle 조인이어도 바꾸며 결과 집합은 같아야 한다.</summary>
    internal const string AnsiJoinRule =
        "Write the sql of every rewrite you propose in " + AnsiJoinSyntaxRule
        + "Do this even when the original statement uses Oracle joins (comma-separated tables or (+)): convert them, and the proposed SQL must return the same result set. "
        + AnsiJoinExceptionRule
        + OracleJoinKeepRule;

    /// <summary>설정의 문법에 맞는 튜닝 조인 규칙.</summary>
    internal static string TuningJoinRule(AiJoinSyntax syntax) => syntax == AiJoinSyntax.Ansi ? AnsiJoinRule : OracleJoinRule;

    /// <summary>
    /// 구조 결과에서 AI 가 빠뜨린 칸(역직렬화가 null 로 두는 문자열·목록·항목)을 빈 값으로 채운다. 스키마가 필수로 요구해도 모델이 칸을 빼면
    /// 뒤의 반영·카드·레포트 코드가 <see cref="NullReferenceException"/> 으로 죽지 않게, 읽는 자리 한 곳에서 막는다.
    /// SQL 이 비어 있는 재작성 후보와 DDL 이 비어 있는 인덱스 후보는 쓸 수 없으므로 뺀다.
    /// </summary>
    internal static TuningResult Sanitize(TuningResult result) => Sanitize(result, out _, out _, out _);

    internal static TuningResult Sanitize(TuningResult result, out int skippedRewrites, out int skippedIndexes) => Sanitize(result, out skippedRewrites, out skippedIndexes, out _);

    /// <summary>
    /// <see cref="Sanitize(TuningResult)"/> 에 쓸 수 없어 건너뛴 재작성·인덱스 후보 수와, 기존 인덱스 이름 없이 DROP 이 든 채 변형으로 남긴 후보 수를 함께 돌려준다.
    /// </summary>
    internal static TuningResult Sanitize(TuningResult result, out int skippedRewrites, out int skippedIndexes, out int modifyWithoutName)
    {
        ArgumentNullException.ThrowIfNull(result);
        var rewriteSource = (result.Rewrites ?? []).ToList();
        var indexSource = (result.IndexSuggestions ?? []).ToList();
        var rewrites = rewriteSource
            .Where(r => r is not null && !string.IsNullOrWhiteSpace(r.Sql))
            .Select(r => r with { Label = string.IsNullOrWhiteSpace(r.Label) ? "(이름 없음)" : r.Label, Rationale = r.Rationale ?? string.Empty })
            .ToList();
        skippedRewrites = rewriteSource.Count - rewrites.Count;
        var indexes = indexSource
            .Where(i => i is not null && !string.IsNullOrWhiteSpace(i.Ddl))
            .Select(i => i with
            {
                Table = i.Table ?? string.Empty,
                Columns = i.Columns ?? [],
                Rationale = i.Rationale ?? string.Empty,
                Action = i.Action ?? TuningIndexSuggestion.ActionCreate,
            })
            // 변형(modify)인데 바꿀 기존 인덱스 이름이 없으면 대화·탭 모두 「새 인덱스」 로 같게 다룬다(대화는 「변형 1」, 탭은 「새 인덱스」 로 어긋나지 않게).
            .Select(i => i.IsModify && string.IsNullOrWhiteSpace(i.ExistingIndex) && !DropsIndex(i.Ddl) ? i with { Action = TuningIndexSuggestion.ActionCreate } : i)
            .ToList();
        skippedIndexes = indexSource.Count - indexes.Count;
        modifyWithoutName = indexes.Count(i => i.IsModify && string.IsNullOrWhiteSpace(i.ExistingIndex));
        var evidence = (result.Evidence ?? []).Where(e => e is not null).ToList();
        return new TuningResult(result.Summary ?? string.Empty, rewrites, indexes, evidence);
    }

    /// <summary>DDL 에 DROP INDEX 가 있다(기존 인덱스를 지우는 변형).</summary>
    private static bool DropsIndex(string ddl) => DropIndex().IsMatch(ddl);

    [GeneratedRegex(@"\bDROP\s+INDEX\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DropIndex();

    /// <summary>카드 없는 재작성 후보 한 줄: 「- `이름` · 근거 · 측정」.</summary>
    private static string RewriteLine(TuningRewrite rewrite) =>
        $"- `{OneLine(rewrite.Label)}` · {OneLine(rewrite.Rationale)}" + (string.IsNullOrWhiteSpace(rewrite.Benchmark) ? string.Empty : " · " + OneLine(rewrite.Benchmark));

    /// <summary>
    /// 벤치마크 글의 첫 「원문 → 제안」 경과(ms·s·초)에서 변화율(예 「−37%」). 벤치마크가 없거나 읽을 수 없으면 null(카드에 안 보인다).
    /// </summary>
    internal static string? ChangeText(string? benchmark)
    {
        if (string.IsNullOrWhiteSpace(benchmark) || ElapsedPair().Match(benchmark) is not { Success: true } m
            || !double.TryParse(m.Groups[1].Value.Replace(",", string.Empty, StringComparison.Ordinal), NumberStyles.Float, CultureInfo.InvariantCulture, out var before)
            || !double.TryParse(m.Groups[3].Value.Replace(",", string.Empty, StringComparison.Ordinal), NumberStyles.Float, CultureInfo.InvariantCulture, out var after))
        {
            return null;
        }

        before *= m.Groups[2].Value == "ms" ? 1 : 1000;
        after *= m.Groups[4].Value == "ms" ? 1 : 1000;
        if (before <= 0)
        {
            return null;
        }

        var percent = (int)Math.Round((after - before) / before * 100, MidpointRounding.AwayFromZero);
        return percent switch
        {
            < 0 => string.Create(CultureInfo.InvariantCulture, $"−{-percent}%"),
            > 0 => string.Create(CultureInfo.InvariantCulture, $"+{percent}%"),
            _ => "0%",
        };
    }

    [GeneratedRegex(@"(\d[\d,]*(?:\.\d+)?)\s*(ms|s|초)\s*(?:→|->)\s*(?:[^\d\s→]{1,8}\s*)?(\d[\d,]*(?:\.\d+)?)\s*(ms|s|초)", RegexOptions.CultureInvariant)]
    private static partial Regex ElapsedPair();

    /// <summary>리터럴 값을 자리표시로 가렸다는 안내. 값은 싣지 않고 접두만 쓴다. 가린 주석이 있으면 주석 자리표시도 그대로 두라고 덧붙인다.</summary>
    private static string PlaceholderReminder(LiteralVault vault, bool statementOnly = false) =>
        (statementOnly
            ? $"Literal values in the statement above and in the tool results were replaced with placeholders such as {vault.Placeholder(1)}; the user's own message was not masked. Use the same placeholders unchanged when you pass SQL to the tools and when you write SQL in your answer; never guess the original values."
            : $"Literal values in the SQL were replaced with placeholders such as {vault.Placeholder(1)}. Use the same placeholders unchanged when you pass SQL to the tools and when you write SQL in your answer; never guess the original values.")
        + (vault.CommentCount == 0
            ? string.Empty
            : $" Comments were replaced with placeholders such as {LiteralVault.CommentPlaceholder(1, lineComment: false)} and {LiteralVault.CommentPlaceholder(1, lineComment: true)}; keep each comment placeholder exactly as written, in the same place, in every SQL you write.");

    private async Task RunAsync(string prompt, string? resumeId, string? userText, IReadOnlyList<ChatImage> images, CancellationToken ct, AiConversationTarget? route = null, AgentSession? prepared = null)
    {
        var context = _context!;
        var conversation = chat.ConversationGeneration;
        var choice = _choice!;
        var vault = _vault; // 결과를 반영하는 동안(미리보기 창) 새 대화가 시작돼도 이 대화의 사전·문장을 쓴다
        var target = _target!;
        var startTab = _startTabId;
        var command = Command; // 결과를 반영하는 동안 새 대화가 시작돼도 이 요청을 연 명령의 규칙을 쓴다
        var userLine = resumeId is null
            ? command?.UserLine ?? "이 문장 튜닝" + (choice.Benchmark ? " · 벤치마크 켬" : string.Empty) + (choice.RunRiskyFunctions ? " · 위험 함수도 실행" : string.Empty)
            : userText ?? prompt;
        chat.BeginTurn(userLine, images);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _running = cts;
        // 명령은 튜닝의 바인드 값을 싣지 않는 쪽(일반 대화와 같은 범위 만들기)을 쓴다: 명령은 문장을 실행하지 않는다.
        var session = prepared ?? (command is null ? sessionFactory : generalSessionFactory ?? sessionFactory)?.Invoke(context, vault); // prepared: 미리 읽는 데 쓴 같은 범위(실행이 끝나면 회수한다)
        var request = new AiRequest(
            prompt, context, resumeId, command is null ? TuningResult.Schema() : command.OutputSchema, choice.ModelId == "default" ? null : choice.ModelId, session,
            choice.EffortId == TuningStartInfo.DefaultModelId ? null : choice.EffortId,
            Images: images.Count == 0 ? null : [.. images.Select(i => i.Attachment)],
            Expected: (route ?? _route)?.ToExpectation(), // 확인 창에 적은 곳과 실제 전송처가 같게: 라우터가 설정의 활성 프로필과 대조한다(profile_changed)
            MaskingRequired: MaskingRequiredFor(context, vault)); // 값을 가려 보낼 수 없는 기타 CLI 의 라우터만 읽는다(알 수 없으면 참으로 닫는다)
        JsonElement? result = null;
        try
        {
            await chat.RunAsync(Capture(driver.RunAsync(request, cts.Token), r => result = r, cts.Token), cts.Token).ConfigureAwait(true);
        }
        finally
        {
            _running = null;
            session?.Revoke();
        }

        if (result is { } structured && !cts.IsCancellationRequested && (command is null || command.OutputSchema is not null))
        {
            await ApplyResultAsync(structured, choice, vault, target, startTab, conversation, command, ct).ConfigureAwait(true);
        }
    }

    /// <summary>사건을 그대로 흘려 보내면서 끝의 구조 결과를 붙잡는다.</summary>
    private static async IAsyncEnumerable<AiEvent> Capture(IAsyncEnumerable<AiEvent> events, Action<JsonElement?> onResult, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var e in events.WithCancellation(ct).ConfigureAwait(false))
        {
            if (e is AiFinal { Result: { } r })
            {
                onResult(r);
            }

            yield return e;
        }
    }

    private async Task ApplyResultAsync(JsonElement structured, TuningStartChoice choice, LiteralVault? vault, SqlStatement target, Guid? startTab, int conversation, AiCommandSpec? command, CancellationToken ct)
    {
        if (conversation != chat.ConversationGeneration)
        {
            return; // 결과를 기다리는 사이 대화가 바뀌었다: 옛 결과의 줄·제안·레포트 재료를 새 대화에 넣지 않는다
        }

        TuningResult result;
        int skippedRewrites, skippedIndexes, modifyWithoutName;
        try
        {
            result = Sanitize(TuningResult.Parse(structured), out skippedRewrites, out skippedIndexes, out modifyWithoutName);
        }
        catch (JsonException ex)
        {
            chat.AddAssistantText((command is null ? "튜닝" : command.WorkName) + " 결과의 모양을 읽지 못했습니다: " + ex.Message);
            return;
        }

        _lastResult = result;

        // 레포트 재료: 결과 그대로(가린 대화의 자리표시도 그대로). 인덱스 후보는 시작 창에서 고른 때만 싣고, 확인에서 빠진 것은 확인 뒤에 걸러 다시 넘긴다.
        var generation = _generation;
        var turn = chat.Entries.OfType<ChatUserEntry>().Count() - 1; // 0 = 처음 튜닝, N = N 번째 이어 말하기
        void ReportTurn(IReadOnlyList<TuningIndexSuggestion> indexes, IReadOnlyList<string> dropped)
        {
            if (command is { ProducesReport: false })
            {
                return; // 레포트는 튜닝(과 레포트를 만든다고 밝힌 명령)만 만든다: 저장 단추는 비활성으로 남는다
            }

            if (generation != _generation || conversation != chat.ConversationGeneration)
            {
                return; // 그 사이 새 대화가 시작됐다
            }

            _reportTurns.RemoveAll(t => t.Index == turn);
            _reportTurns.Add(new AiTuningReportTurn(turn, result with { IndexSuggestions = indexes }, dropped));
            chat.SetReportSource(new AiTuningReportSource(
                _startedAt, _context?.Environment, choice.ModelId == "default" ? null : choice.ModelId, vault is not null, _promptStatement, [.. _reportTurns]));
        }

        if (!choice.IndexDdl || result.IndexSuggestions.Count == 0)
        {
            ReportTurn([], []); // 인덱스 확인이 남았으면 확인 뒤에 한 번에 넘긴다(후보 없는 레포트가 먼저 저장되지 않게)
        }
        if (!string.IsNullOrWhiteSpace(result.Summary))
        {
            chat.AddAssistantText(result.Summary); // 요약이 비면 빈 말풍선을 만들지 않는다
        }

        // 쓸 수 없는 후보(SQL·DDL 이 빠진 것, 빈 항목)는 건너뛰고 나머지는 그대로 보인다. 인덱스를 고르지 않은 대화에는 인덱스 건너뜀을 말하지 않는다.
        var skippedShown = skippedRewrites + (choice.IndexDdl ? skippedIndexes : 0);
        if (skippedShown > 0)
        {
            chat.AddAssistantText(choice.IndexDdl
                ? string.Create(CultureInfo.InvariantCulture, $"AI 가 보낸 후보 중 내용이 빠져 쓸 수 없는 것을 건너뛰었습니다(재작성 {skippedRewrites}개 · 인덱스 {skippedIndexes}개).")
                : string.Create(CultureInfo.InvariantCulture, $"AI 가 보낸 후보 중 내용이 빠져 쓸 수 없는 것을 건너뛰었습니다(재작성 {skippedRewrites}개)."));
        }

        if (modifyWithoutName > 0 && choice.IndexDdl)
        {
            chat.AddAssistantText(string.Create(CultureInfo.InvariantCulture, $"인덱스 변형 후보 {modifyWithoutName}개에 바꿀 기존 인덱스 이름이 없습니다. DDL 의 DROP 대상을 직접 확인하세요."));
        }

        // 편집기·탭으로 가는 글은 자리표시를 원래 값으로 되돌린 뒤에만 넣는다. 되돌리지 못하면(모르는 자리표시) 넣지 않고 알린다.
        var carded = false;
        if (result.Rewrites.Count > 0 && TryRestore(result.Rewrites[0].Sql, $"재작성 후보 「{result.Rewrites[0].Label}」", vault, out var restoredSql))
        {
            // 비교 창을 열었으면 닫은 뒤에도 대화 창 카드에서 같은 제안을 다시 열 수 있게 남긴다(c6). 카드 글은 AI 가 보낸 라벨·근거 그대로다.
            var rewrite = result.Rewrites[0];
            var proposal = rewrite with { Sql = restoredSql };

            // AI 가 원래 주석(가린 대화면 주석 자리표시)을 빠뜨렸으면 제안은 그대로 두고 몇 개 빠졌는지만 알린다.
            if (MissingComments(target.Text, restoredSql) is var missing and > 0)
            {
                chat.AddAssistantText($"주석 {missing}개 빠짐: 재작성 후보 「{rewrite.Label}」 에 원래 문장의 주석 {missing}개가 없습니다. 제안은 그대로 두었으니 비교 창에서 확인하세요.");
            }

            // 받아들인 뒤 그 자리의 글(비교 창에서 고친 것까지)을 기억해 두고 「비교 보기」 로 원본과 견준다.
            string? applied = null;
            async Task<bool?> Propose(CancellationToken token)
            {
                var (answer, finalText) = await ProposeRewriteAsync(proposal, target, startTab, command, token).ConfigureAwait(true);
                if (answer == true)
                {
                    applied = finalText;
                }

                return answer;
            }

            Func<Task>? view = showComparison is null ? null
                : () => showComparison((command?.ProposalLabel ?? ProposalLabelTune) + rewrite.Label + " (적용함)", target.Text, applied ?? MatchTerminator(target.Text, proposal.Sql));
            if (await Propose(ct).ConfigureAwait(true) is { } accepted)
            {
                chat.AddProposal(rewrite.Label, OneLine(rewrite.Rationale), accepted, () => Propose(CancellationToken.None), ChangeText(rewrite.Benchmark), view);
                carded = true;
            }
        }

        // 카드가 없는 후보는 블릿 한 줄씩(「[이름] 긴 글 (수치)」 꼬리표 대신).
        var rest = result.Rewrites.Skip(carded ? 1 : 0).ToList();
        if (rest.Count > 0)
        {
            chat.AddAssistantText((carded ? "## 다른 후보" : "## 재작성 후보") + "\n\n" + string.Join('\n', rest.Select(RewriteLine)));
        }

        if (choice.IndexDdl && result.IndexSuggestions.Count > 0)
        {
            List<TuningIndexSuggestion> restored = [];
            Dictionary<TuningIndexSuggestion, TuningIndexSuggestion> originals = new(ReferenceEqualityComparer.Instance); // 되돌린 후보 → AI 가 보낸 후보(레포트는 자리표시 그대로)
            var notRestored = 0;
            foreach (var suggestion in result.IndexSuggestions)
            {
                if (TryRestore(suggestion.Ddl, $"인덱스 후보 {suggestion.Table}", vault, out var ddl))
                {
                    var candidate = suggestion with { Ddl = ddl };
                    restored.Add(candidate);
                    originals[candidate] = suggestion;
                }
                else
                {
                    notRestored++;
                }
            }

            var review = await AiIndexReview.ReviewAsync(restored, readIndexes, ct).ConfigureAwait(true);
            List<string> reportDropped = [.. review.Dropped];
            if (notRestored > 0)
            {
                reportDropped.Add($"자리표시를 되돌리지 못해 뺀 후보 {notRestored}개");
            }

            ReportTurn([.. review.Kept.Select(k => originals[k.Suggestion])], reportDropped);
            if (conversation != chat.ConversationGeneration)
            {
                return; // 기존 인덱스를 읽는 사이 대화가 바뀌었다: 옛 결과의 후보 탭·줄을 새 대화에 넣지 않는다(레포트 재료는 ReportTurn 이 같은 대조로 막았다)
            }

            if (review.Dropped.Count > 0)
            {
                chat.AddAssistantText(string.Join('\n', review.Dropped));
            }

            if (review.Kept.Count > 0)
            {
                ShowIndexCandidates(BuildIndexText(review.Kept, review.Dropped, vault is not null), CountText(review.Kept));
            }
        }
    }

    /// <summary>대화 한 줄에 쓸 개수 글: 기존 인덱스 변형을 먼저, 새 인덱스를 뒤에(예 「2개(변형 1 · 새 인덱스 1)」).</summary>
    private static string CountText(IReadOnlyList<ReviewedIndex> kept)
    {
        var modify = kept.Count(r => r.Suggestion.IsModify);
        var created = kept.Count - modify;
        var parts = new List<string>();
        if (modify > 0)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"변형 {modify}"));
        }

        if (created > 0)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"새 인덱스 {created}"));
        }

        return string.Create(CultureInfo.InvariantCulture, $"{kept.Count}개({string.Join(" · ", parts)})");
    }

    private const string IndexTabTitle = "AI 인덱스 후보";

    /// <summary>
    /// 인덱스 후보 글을 탭에 넣는다. 대화 하나에 탭 하나만 쓴다: 열려 있으면 그 탭의 글만 바꾸고(활성 탭은 그대로),
    /// 글이 같으면 건드리지 않으며, 사용자가 닫았으면 새로 연다. 실행은 하지 않는다.
    /// </summary>
    private void ShowIndexCandidates(string text, string count)
    {
        if (tabs is null)
        {
            openTab(IndexTabTitle, text, null);
            chat.AddAssistantText($"인덱스 후보 {count}를 새 탭에 넣었습니다. 실행은 하지 않았습니다.");
            return;
        }

        if (_indexTabId is { } existing && tabs.GetText(existing) is { } current)
        {
            if (current == text)
            {
                chat.AddAssistantText("인덱스 후보가 앞서 넣은 탭과 같아 그대로 두었습니다.");
                return;
            }

            if (tabs.ReplaceText(existing, text))
            {
                chat.AddAssistantText($"인덱스 후보 {count}로 「{IndexTabTitle}」 탭의 내용을 바꾸었습니다. 실행은 하지 않았습니다.");
                return;
            }
        }

        _indexTabId = tabs.OpenText(IndexTabTitle, text);
        chat.AddAssistantText($"인덱스 후보 {count}를 새 탭에 넣었습니다. 실행은 하지 않았습니다.");
    }

    /// <summary>
    /// 가린 대화라면 <paramref name="sql"/> 의 자리표시를 되돌린다. 사전이 없으면(가리지 않은 대화) 그대로 통과한다.
    /// 모르는 자리표시(<c>literal_unknown</c>)이거나 되돌리는 중 실패하면 false 이고 대화 창에 알린다.
    /// </summary>
    private bool TryRestore(string sql, string what, LiteralVault? vault, out string restored)
    {
        restored = sql;
        if (vault is null)
        {
            return true;
        }

        try
        {
            var result = masker?.Restore(sql, vault);
            if (result is { Succeeded: true })
            {
                restored = result.Text;
                return true;
            }

            var unknown = result?.UnknownPlaceholder;
            chat.AddAssistantText(unknown is null
                ? $"{what} 의 자리표시를 되돌리지 못해 넣지 않았습니다."
                : $"{what} 에 모르는 자리표시 {unknown} 이 있어 되돌리지 못하고 넣지 않았습니다(literal_unknown).");
        }
#pragma warning disable CA1031 // 되돌림이 어떤 이유로 실패해도 가린 글을 편집기에 넣지 않고 알리기만 한다.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            chat.AddAssistantText($"{what} 의 자리표시를 되돌리지 못해 넣지 않았습니다.");
        }

        return false;
    }

    /// <summary>
    /// 원래 문장의 주석 중 <paramref name="proposal"/>(되돌린 제안)에 없는 수. 버리는 사전 하나로 두 글을 가려 같은 주석이 같은 자리표시가 되게 한 뒤
    /// 자리표시 수를 비교한다(가린 대화·안 가린 대화 공통, 이 사전은 어디로도 보내지 않는다). 셀 수 없으면 0 이다(알리지 않는다).
    /// </summary>
    private int MissingComments(string original, string proposal)
    {
        if (masker is null)
        {
            return 0;
        }

        try
        {
            var scratch = masker.CreateVault(original);
            var before = masker.Mask(original, scratch);
            var after = masker.Mask(proposal, scratch);
            if (!before.Succeeded || !after.Succeeded)
            {
                return 0;
            }

            var kept = LiteralVault.FindCommentPlaceholders(after.Text).CountBy(p => p).ToDictionary();
            return LiteralVault.FindCommentPlaceholders(before.Text).CountBy(p => p)
                .Sum(pair => Math.Max(0, pair.Value - kept.GetValueOrDefault(pair.Key)));
        }
#pragma warning disable CA1031 // 주석 셈은 알림용이다: 어떤 실패든 제안 반영을 막지 않는다.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            return 0;
        }
    }

    private const string TuneSuffix = " (튜닝)";

    private const string ProposalLabelTune = "AI 튜닝 — ";

    /// <summary>반영하기 전에 대상 문장이 바뀌었을 때의 안내(튜닝은 옛 글 그대로, 명령은 <see cref="AiCommandSpec.WorkName"/>).</summary>
    private static string StatementChangedText(AiCommandSpec? command) =>
        command is null
            ? "튜닝하는 사이 문장이 바뀌어 제안을 새 탭에 넣지 않았습니다."
            : $"{command.WorkName} 중에 문장이 바뀌어 제안을 새 탭에 넣지 않았습니다.";

    /// <summary>원래 탭 제목 뒤에 「 (튜닝)」 을 붙인다(이미 그렇게 끝나면 그대로).</summary>
    internal static string TuneTitle(string? original) => SuffixedTitle(original, TuneSuffix);

    /// <summary>원래 탭 제목 뒤에 <paramref name="suffix"/>(앞 공백 포함, 예 「 (수정)」)를 붙인다(이미 그렇게 끝나면 그대로).</summary>
    internal static string SuffixedTitle(string? original, string suffix)
    {
        var title = string.IsNullOrWhiteSpace(original) ? "SQL" : original.Trim();
        return title.EndsWith(suffix.Trim(), StringComparison.Ordinal) ? title : title + suffix;
    }

    /// <summary>이 대화의 도구 수준 상한(명령이 없으면 상한 없음)을 <paramref name="level"/> 에 건다.</summary>
    internal static AiAccessLevel Cap(AiCommandSpec? command, AiAccessLevel level) =>
        command is null ? level : (AiAccessLevel)Math.Min((int)level, (int)command.AccessCeiling);

    /// <summary>
    /// 재작성 제안을 비교 창으로 보인다. 받아들이면 원래 탭은 그대로 두고 「원래 탭명 (튜닝)」 새 탭에 (원래 글에서 그 문장 자리만 바꾼 글을) 넣고 true,
    /// 거절하면 false, 열지 못했으면(탭이 닫힘·문장이 바뀜·비교 창 없음) null. 같은 대화에서 다시 받아들이면 열려 있는 그 탭의 글을 바꾼다.
    /// </summary>
    private async Task<(bool? Answer, string? Applied)> ProposeRewriteAsync(TuningRewrite rewrite, SqlStatement target, Guid? startTab, AiCommandSpec? command, CancellationToken ct)
    {
        string? fullText;
        string? originTitle;
        Guid? profile = null;
        if (tabs is not null && startTab is { } tabId)
        {
            // 시작한 탭의 글로 비교한다(인덱스 후보 탭이 열려 활성 탭이 바뀌어도 문장은 그대로다).
            fullText = tabs.GetText(tabId);
            if (fullText is null)
            {
                chat.AddAssistantText(command is null ? "튜닝을 시작한 탭이 닫혀 제안을 새 탭에 넣지 않았습니다." : $"{command.WorkName} 시작 탭이 닫혀 제안을 새 탭에 넣지 않았습니다.");
                return (null, null);
            }

            // 옛 범위의 글만 같은지 보면 문장 뒤에 이어 친 글(WHERE …)을 놓친다: 그 자리의 문장이 글과 범위까지 같아야 한다.
            var current = tabs.GetStatementAt(tabId, target.Range.Start);
            if (current is null || current.Text != target.Text || current.Range != target.Range)
            {
                chat.AddAssistantText(StatementChangedText(command));
                return (null, null);
            }

            originTitle = tabs.GetTitle(tabId);
        }
        else
        {
            var current = editor.GetCurrentStatement();

            // 튜닝하는 동안 사용자가 문장을 고쳤으면 옛 범위에 덮어쓰지 않는다.
            if (current is null || current.Text != target.Text || current.Range != target.Range)
            {
                chat.AddAssistantText(StatementChangedText(command));
                return (null, null);
            }

            var snapshot = editor.GetActiveDocument();
            fullText = snapshot?.Text;
            originTitle = snapshot?.Title;
            profile = snapshot?.ConnectionProfileId;
        }

        if (previewEdit is null)
        {
            chat.AddAssistantText("비교 창을 쓸 수 없어 제안을 새 탭에 넣지 않았습니다.");
            return (null, null);
        }

        var edit = new TextEdit(target.Range, MatchTerminator(target.Text, rewrite.Sql)) { Description = (command?.ProposalLabel ?? ProposalLabelTune) + rewrite.Label };
        var finalText = await previewEdit(edit, target.Text, ct).ConfigureAwait(true);
        if (finalText is null)
        {
            chat.AddAssistantText("제안을 받아들이지 않았습니다.");
            return (false, null);
        }

        var title = command is null ? TuneTitle(originTitle) : SuffixedTitle(originTitle, command.TabSuffix);
        var text = ApplyToFullText(fullText, target, finalText);
        if (tabs is not null)
        {
            if (_tuneTabId is { } existing && tabs.ReplaceText(existing, text) && tabs.Activate(existing))
            {
                // 같은 대화에서 다시 받아들인 제안: 열려 있는 (튜닝) 탭의 글만 바꾸고 그 탭을 보인다.
            }
            else
            {
                _tuneTabId = tabs.OpenText(title, text, startTab);
            }

            // 원래 탭의 바인드 값을 (튜닝) 탭에 옮긴다(탭을 다시 쓴 경우도 같다). 시작 탭이 닫혔으면 복사하지 않는다.
            if (startTab is { } from && _tuneTabId is { } tuned)
            {
                tabs.CopyBinds(from, tuned);
            }
        }
        else
        {
            openTab(title, text, profile);
        }

        chat.AddAssistantText($"제안을 새 탭 「{title}」 에 넣었습니다. 원래 탭은 그대로입니다.");
        return (true, finalText);
    }

    /// <summary>원래 탭 글에서 <paramref name="target"/> 자리만 <paramref name="replacement"/> 로 바꾼 글. 원래 글을 모르거나 자리가 맞지 않으면 바꾼 문장만.</summary>
    private static string ApplyToFullText(string? fullText, SqlStatement target, string replacement)
    {
        var range = target.Range;
        return fullText is not null && range.End <= fullText.Length && string.CompareOrdinal(fullText, range.Start, target.Text, 0, target.Text.Length) == 0
            ? string.Concat(fullText.AsSpan(0, range.Start), replacement, fullText.AsSpan(range.End))
            : replacement;
    }

    /// <summary>끝의 <c>;</c> 를 원문과 같게 맞춘다(원문 범위가 <c>;</c> 를 포함하지 않으면 뺀다).</summary>
    private static string MatchTerminator(string original, string replacement)
    {
        var text = replacement.Trim();
        var originalHasSemicolon = original.TrimEnd().EndsWith(';');
        var replacementHasSemicolon = text.EndsWith(';');
        return originalHasSemicolon == replacementHasSemicolon ? text
            : originalHasSemicolon ? text + ";"
            : text.TrimEnd(';').TrimEnd();
    }

    /// <summary>
    /// 인덱스 후보 탭 글. 기존 인덱스를 바꾸는 안(modify)을 먼저, 새 인덱스(create)를 뒤에 둔다.
    /// 후보마다 받은 뒤 확인(<see cref="AiIndexReview"/>)의 줄이 붙고, 기존 인덱스가 이미 덮어 뺀 후보는 머리에 한 줄씩 남긴다.
    /// </summary>
    internal static string BuildIndexText(IReadOnlyList<ReviewedIndex> suggestions, IReadOnlyList<string> dropped, bool masked)
    {
        var text = new StringBuilder();
        text.AppendLine("-- AI 가 제안한 인덱스 후보입니다. Oranda 는 실행하지 않았습니다. 검토한 뒤 직접 실행하세요.");
        if (masked)
        {
            text.AppendLine("-- 설명 줄의 :v1 같은 표시는 가려서 보낸 리터럴 값의 자리표시입니다. CREATE 문의 값은 원래대로 되돌렸습니다.");
        }

        foreach (var line in dropped)
        {
            text.AppendLine("-- " + OneLine(line));
        }

        foreach (var reviewed in suggestions.OrderBy(r => r.Suggestion.IsModify ? 0 : 1))
        {
            var s = reviewed.Suggestion;
            text.AppendLine();
            text.AppendLine(s.IsModify && s.ExistingIndex is { Length: > 0 } existing
                ? $"-- 기존 {existing} ({string.Join(", ", s.ExistingColumns ?? [])}) → ({string.Join(", ", s.Columns)}) 로 변형"
                : s.IsModify
                    ? $"-- 변형(기존 인덱스 이름 없음 — DROP 대상을 확인하세요): {s.Table} ({string.Join(", ", s.Columns)})"
                    : $"-- 새 인덱스: {s.Table} ({string.Join(", ", s.Columns)})");
            text.AppendLine("-- " + OneLine(s.Rationale));
            if (!string.IsNullOrWhiteSpace(s.Risk))
            {
                text.AppendLine("-- 위험: " + OneLine(s.Risk));
            }

            foreach (var note in reviewed.Notes)
            {
                text.AppendLine("-- 확인: " + OneLine(note));
            }

            text.AppendLine(s.Ddl.Trim());
        }

        return text.ToString();
    }

    private static string OneLine(string value) => value.ReplaceLineEndings(" ").Trim();

    private static string Shorten(string value, int max)
    {
        var line = OneLine(value);
        return line.Length <= max ? line : line[..max] + "…";
    }
}
