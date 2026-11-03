using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Ai.Schemas;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Extensibility;

namespace Oranda.App.Features.Ai.Tuning.Commands;

/// <summary>
/// 「AI 튜닝」 밖의 AI 명령(SQL 설명·실행 계획 해설·오류 고치기) 하나의 계약. 명령은 이 형을 구현해 <c>Tuning/Commands/*</c> 에 두고,
/// 가림·보낼 곳 확인·수준 상한·이어 말하기·제안 반영은 <see cref="AiTuningFlow"/> 가 공통으로 한다. 명령이 정하는 것은 아래뿐이다.
/// <list type="bullet">
/// <item><see cref="PrepareAsync"/>: 무엇을 보낼지(대상 문장·<c>sql_id</c>)를 정하거나 보내지 않을 이유를 돌려준다.</item>
/// <item><see cref="BuildInstruction"/>·<see cref="ResponseRules"/>: 프롬프트 글. 문장 블록·자리표시 안내는 흐름이 붙인다.</item>
/// <item><see cref="AccessCeiling"/>: 도구 수준 상한. 이어 말하기 때마다 흐름이 다시 건다.</item>
/// <item><see cref="OutputSchema"/>·<see cref="TabSuffix"/>·<see cref="ProposalLabel"/>·<see cref="ProducesReport"/>: 끝에 무엇을 하는지.</item>
/// </list>
/// 이 계약이 머지된 뒤 바꾸면 다른 PC 가 다시 작업해야 하므로, 명령 쪽 필요는 이 형의 virtual 멤버로 받는다.
/// </summary>
public abstract class AiCommandSpec
{
    /// <summary>이 명령의 명령 id(<see cref="AiCommands"/>).</summary>
    public abstract string Id { get; }

    /// <summary>대화 제목의 앞부분(「SQL 설명」). 뒤에 대상 문장 한 줄이 붙는다.</summary>
    public abstract string ConversationTitle { get; }

    /// <summary>
    /// 안내 글에 쓰는 작업 이름(「오류 고치기」). 구조 결과를 읽지 못했을 때나 반영 전에 문장·탭이 바뀌었을 때의 흐름 안내에 들어가므로,
    /// 이 글들을 바꾸려고 흐름 파일을 고칠 필요가 없다. 기본은 <see cref="ConversationTitle"/>.
    /// </summary>
    public virtual string WorkName => ConversationTitle;

    /// <summary>첫 차례에 사용자 줄로 보이는 글(「이 문장 튜닝」 에 해당).</summary>
    public abstract string UserLine { get; }

    /// <summary>
    /// 이 명령에서 도구가 쓸 수 있는 최고 수준. 흐름이 접속의 실효 수준과 이 값의 최솟값을 문맥에 싣고, 이어 말하기로 문맥을 새로 읽을 때마다 다시 건다
    /// (게이트는 <c>AgentSession.Level</c> 로 이 값과 프로필 수준의 최솟값을 쓰므로 게이트를 고치지 않고 도구가 닫힌다).
    /// </summary>
    public abstract AiAccessLevel AccessCeiling { get; }

    /// <summary>
    /// 구조 결과의 스키마. null 이면 자유 글 답만 내고 제안 반영은 없다. 값이 있으면 <see cref="TuningResult"/> 모양(<c>summary</c>·<c>rewrites</c>)으로
    /// 받아 기존 비교 창 → 새 탭 경로로 반영하고, 인덱스 경로는 꺼진다.
    /// </summary>
    public virtual JsonElement? OutputSchema => null;

    /// <summary>
    /// 응답 규칙. 처음 요청과 모든 이어 말하기 뒤에 붙는다(튜닝의 <c>ResponseStyle</c> 에 해당). 재작성을 내는 명령은 원래 주석을 지키라는 규칙도 여기에 넣는다.
    /// </summary>
    public abstract string ResponseRules { get; }

    /// <summary>제안을 받아들일 때 열 새 탭 이름 뒤에 붙는 글(원래 탭명 + 이 글). <see cref="OutputSchema"/> 가 있을 때만 쓴다.</summary>
    public virtual string TabSuffix => " (수정)";

    /// <summary>비교 창 설명의 앞글(뒤에 후보 이름이 붙는다). <see cref="OutputSchema"/> 가 있을 때만 쓴다.</summary>
    public virtual string ProposalLabel => "AI 제안 — ";

    /// <summary>레포트 재료를 만드는가. 튜닝만 참이어야 하므로 기본은 거짓이다(저장 단추 비활성).</summary>
    public virtual bool ProducesReport => false;

    /// <summary>문장을 가릴 수 없을 때(가림 기능 없음) 대화 창에 남기는 글. null 이면 공통 글.</summary>
    public virtual string? MaskerMissingText => null;

    /// <summary>문장을 가리지 못했을 때(닫히지 않은 문자열 등) 대화 창에 남기는 글. null 이면 공통 글.</summary>
    public virtual string? MaskFailedText => null;

    /// <summary>대화 창 첫 줄에 보낸 내용을 알린 뒤 덧붙일 글(가림 여부별). 없으면 null.</summary>
    public virtual string? SentNote(bool masked) => null;

    /// <summary>
    /// 시작 준비: 대상 문장과 문맥을 정한다. 보내면 안 되면 <see cref="AiCommandPreparation.Refuse"/> 로 이유를 돌려주고 흐름은 시작하지 않는다.
    /// 활성 문서가 지금 접속의 SQL 탭이 아닌 경우는 이 호출 전에 공통으로 걸러진다.
    /// </summary>
    public abstract Task<AiCommandPreparation> PrepareAsync(AiCommandStartEnv env, CancellationToken ct);

    /// <summary>
    /// 처음 요청의 지시 글. 문장 블록(<c>&lt;statement&gt;</c>)·자리표시 안내·<see cref="ResponseRules"/> 는 흐름이 뒤에 붙이므로 쓰지 않는다.
    /// 가리는 대화에서 원래 글이나 서버 값이 새지 않도록 <see cref="AiCommandPromptInput"/> 의 가린 글만 쓴다.
    /// </summary>
    public abstract string BuildInstruction(AiCommandPromptInput input);
}

/// <summary>시작 준비가 돌려주는 대상.</summary>
/// <param name="Statement">AI 로 보낼 원래 문장(가리기 전). 가림이 필요하면 흐름이 가린다.</param>
/// <param name="SqlId">도구 범위에 넣을 <c>sql_id</c>(게이트가 <c>IsKnownSqlId</c> 로 허용). 없으면 null.</param>
/// <param name="TabId">제안을 새 탭에 넣을 때 기준으로 삼는 원래 탭. null 이면 활성 탭.</param>
/// <param name="State">프롬프트 생성에 되돌려 받을 명령 전용 값(오류 정보 등). 흐름은 열어 보지 않는다.</param>
public sealed record AiCommandTarget(SqlStatement Statement, string? SqlId = null, Guid? TabId = null, object? State = null);

/// <summary>시작 준비의 결과: 보낼 대상 또는 보내지 않는 이유.</summary>
public sealed class AiCommandPreparation
{
    private AiCommandPreparation(AiCommandTarget? target, string? refusal)
    {
        Target = target;
        RefusalText = refusal;
    }

    /// <summary>보낼 대상. 거부면 null.</summary>
    public AiCommandTarget? Target { get; }

    /// <summary>보내지 않는 이유(대화 창에 그대로 보인다). 보낼 대상이 있으면 null.</summary>
    public string? RefusalText { get; }

    public static AiCommandPreparation Ready(AiCommandTarget target) =>
        new(target ?? throw new ArgumentNullException(nameof(target)), null);

    public static AiCommandPreparation Refuse(string text) =>
        new(null, string.IsNullOrWhiteSpace(text) ? throw new ArgumentException("거부 이유가 비었습니다.", nameof(text)) : text);
}

/// <summary>시작 준비가 읽을 수 있는 환경(읽기 전용 사실).</summary>
/// <param name="Services">앱 서비스(실행 상태·계획 창 등을 읽을 때).</param>
/// <param name="Editor">활성 편집기.</param>
/// <param name="Profile">저장소의 최신 접속 프로필.</param>
/// <param name="Context">실효 허용 수준이 담긴 문맥(상한은 아직 걸리지 않았다).</param>
/// <param name="CursorSource">활성 SQL 탭에서 커서 문장을 찾은 결과(<see cref="AiStatementSource.OtherDocument"/> 는 이 앞에서 걸러진다).</param>
/// <param name="CursorStatement">커서 위치 문장. 없으면 null.</param>
/// <param name="ActiveTabId">활성 문서의 탭 id. 알 수 없으면 null.</param>
/// <param name="Masking">
/// 이 시점에 읽은 가림 여부(운영 접속이거나 설정이 켜짐). 참고용이다: 시작 준비가 기다리는 사이 설정이 바뀔 수 있고 흐름은 시작할 때 다시 판단하므로,
/// 프롬프트에 무엇을 넣을지는 <see cref="AiCommandPromptInput.Masked"/> 로만 정한다(<see cref="BuildInstruction"/> 에서 오류 메시지 같은 서버 값을 넣을 때 포함).
/// </param>
public sealed record AiCommandStartEnv(
    IServiceProvider Services,
    IEditorContext Editor,
    ConnectionProfile Profile,
    AiContext Context,
    AiStatementSource CursorSource,
    SqlStatement? CursorStatement,
    Guid? ActiveTabId,
    bool Masking);

/// <summary>프롬프트 생성이 받는 값. 가린 대화에서는 <see cref="PromptStatement"/> 만 AI 로 간다.</summary>
/// <param name="Target">시작 준비가 정한 대상.</param>
/// <param name="PromptStatement">AI 로 보낼 문장(가린 대화면 가린 글).</param>
/// <param name="Vault">가린 대화의 사전. 가리지 않으면 null.</param>
/// <param name="Masker">가림 구현. 없을 수 있다.</param>
/// <param name="Context">이 대화의 문맥(상한이 걸린 뒤).</param>
public sealed record AiCommandPromptInput(AiCommandTarget Target, string PromptStatement, LiteralVault? Vault, ISqlLiteralMasker? Masker, AiContext Context)
{
    /// <summary>리터럴을 가려서 보내는 대화다.</summary>
    public bool Masked => Vault is not null;

    /// <summary>
    /// 원래 문장의 <paramref name="originalOffset"/> 위치를 가린 글의 위치로 바꾼다. 가리지 않는 대화면 그대로 돌려준다.
    /// 앞부분을 가릴 수 없거나(문자열 한가운데 등) 앞부분의 가림이 전체의 가림과 어긋나거나 범위를 벗어나면 null(호출한 쪽이 위치를 빼고 보낸다).
    /// 리터럴 바로 뒤에 글자가 붙은 글은 가림 구현이 자리표시 뒤에 공백을 끼우므로 자리표시 경계에서 1자 어긋날 수 있다(오류 위치 안내로는 충분하다).
    /// 버리는 사전으로 앞부분만 가리므로 대화의 사전에는 아무것도 더하지 않는다.
    /// </summary>
    public int? MaskedOffset(int originalOffset)
    {
        var original = Target.Statement.Text;
        if (originalOffset < 0 || originalOffset > original.Length)
        {
            return null;
        }

        if (!Masked)
        {
            return originalOffset;
        }

        if (Masker is null)
        {
            return null;
        }

        try
        {
            // 앞부분만 가린 글이 전체를 가린 글의 앞부분과 같을 때만 믿는다: 리터럴 뒤에 글자가 붙거나 DATE 키워드·주석 한가운데서 자르면
            // 앞부분과 전체의 가림 모양이 달라 위치가 어긋나므로 그때는 null(위치를 빼고 보낸다).
            var prefix = Masker.Mask(original[..originalOffset], Masker.CreateVault(original));
            var whole = Masker.Mask(original, Masker.CreateVault(original));
            return prefix.Succeeded && whole.Succeeded && whole.Text.StartsWith(prefix.Text, StringComparison.Ordinal) ? prefix.Text.Length : null;
        }
#pragma warning disable CA1031 // 위치 환산은 알림용이다: 어떤 실패든 위치를 빼고 보내는 쪽으로 닫는다.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            return null;
        }
    }
}
