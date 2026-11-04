using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Oranda.App.Features.Ai.Chat;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Connection;

namespace Oranda.App.Features.Ai.Tuning;

/// <summary>
/// 일반 대화가 보낼 때마다 받는 접속 정보.
/// </summary>
/// <param name="Context">활성 접속의 지금 설정(이름·환경·허용 수준). 연결이 없거나 접속을 믿고 읽을 수 없으면 null(DB 도구 없이 답한다).</param>
/// <param name="Masking">
/// 이 대화를 가리는 대화로 본다(운영 접속이거나 설정이 켜짐). 접속이 없어도 전역 가림 설정이 켜져 있으면, 저장소를 읽지 못했거나 접속이 지워져 접속을 알 수 없을 때도 참이다
/// (닫힌 쪽으로: 알 수 없으면 가리는 대화로 본다). 참이면 입력한 글과 이미지가 가려지지 않고 나간다는 확인을 대화당 한 번 받는다.
/// </param>
/// <param name="ConnectionId">접속 id(대화 도중 접속이 바뀌었는지 가리는 기준). 모르면 null.</param>
/// <param name="SqlDocumentActive">
/// 활성 문서가 SQL 작업 공간 문서인가(<c>SessionRouter.SqlDocumentActive</c>). PL/SQL 편집기·객체 상세처럼 SQL 탭이 아닌 문서가 활성이면 false 이고, 이때 편집기 맥락은
/// 활성 문서와 무관하게 마지막 SQL 탭을 돌려주므로 그 탭의 문장은 지금 접속의 것이 아닐 수 있다. 판단할 수 없으면 null — 닫는 쪽(도구 없음)으로 본다.
/// </param>
/// <param name="UnsafeBinding">
/// 보이는 접속은 없지만(<paramref name="Context"/> 가 null) 활성 SQL 탭이 접속이 닫힌 접속에 묶여 있다. 그 접속이 운영이거나 저장소에서 찾을 수 없으면 참이다. 「연결 없음」(탭 자체가 없거나 접속이 묶이지 않음)과 달리 값을 가려 보낼 수 없는 기타 CLI 는 이때 보내지 않는다.
/// 다른 종류의 대화는 이 값을 보지 않는다(글을 가리지 않는 동작은 그대로).
/// </param>
public sealed record AiGeneralScope(AiContext? Context, bool Masking, Guid? ConnectionId = null, bool? SqlDocumentActive = null, bool UnsafeBinding = false);

/// <summary>
/// 일반 대화: 튜닝을 시작하지 않고 대화 창에 쓴 글(과 이미지)을 그대로 AI 에게 보낸다. 튜닝과 같은 드라이버 경로·프로필 선택·이미지 확인·감사를 쓰되,
/// 튜닝 전용 요소(구조 결과 스키마·재작성 제안·인덱스 후보·레포트 재료)는 싣지 않는다.
/// 활성 접속이 있고 허용 수준이 「끔」 이 아니며 활성 SQL 탭에 문장이 있으면, 그 문장을 범위로 튜닝과 같은 게이트·범위 계산(<c>AgentGate</c>)으로 DB 도구를 연다.
/// 그 밖에는 도구 없는 실행 범위(허용 수준 「끔」)로 답하게 한다 — 드라이버 계약이 실행 범위를 늘 요구하므로 도구가 없다는 사실은 「끔」 범위로 표현한다. 새 권한은 더하지 않는다.
/// </summary>
public sealed partial class AiTuningFlow
{
    /// <summary>일반 대화 중 보낼 곳이 바뀌어 새 대화로 이어 가는 안내.</summary>
    public const string GeneralTargetChangedText = "AI 모델이 바뀌어 새 대화로 시작합니다. 앞 대화의 내용은 새 모델로 보내지 않습니다.";

    /// <summary>일반 대화 중 접속이 바뀌어 새 대화로 이어 가는 안내.</summary>
    public const string GeneralConnectionChangedText = "접속이나 가림 설정이 바뀌어 새 대화로 시작합니다. 앞 대화의 내용은 이어 보내지 않습니다.";

    /// <summary>확인·감사를 기다리는 사이 보낼 곳이 바뀌어 보내지 않았을 때의 안내(다시 보내면 새 대화로 시작한다).</summary>
    public const string GeneralSendCancelledText = "보내는 사이 AI 모델이 바뀌어 보내지 않았습니다. 다시 보내면 새 대화로 시작합니다.";

    /// <summary>가림이 필요한 접속인데 가림 기능이 없을 때: DB 도구 없이 답한다.</summary>
    public const string GeneralMaskerMissingText = "이 접속은 SQL 의 리터럴 값을 가려서 보내야 하는데 가림 기능을 쓸 수 없어 DB 도구 없이 답합니다.";

    /// <summary>범위로 삼을 문장을 가리지 못했을 때: DB 도구 없이 답한다.</summary>
    public const string GeneralMaskFailedText = "활성 탭의 문장을 가리지 못해(닫히지 않은 문자열 등) DB 도구 없이 답합니다.";

    /// <summary>가리는 대화인데 입력 글 확인 창을 열 수 없을 때의 안내(확인 없이 보내지 않는다).</summary>
    public const string TextConfirmMissingText = "입력한 글은 가려지지 않아 확인이 필요한데 확인 창을 열 수 없어 보내지 않았습니다.";

    /// <summary>활성 문서가 SQL 탭이 아니거나 다른 접속의 탭이라 그 탭의 문장을 쓰지 않을 때의 안내.</summary>
    public const string GeneralOtherDocumentText = "활성 문서가 지금 접속의 SQL 탭이 아니라서(다른 접속의 탭이거나 PL/SQL 편집기·객체 상세) 그 탭의 문장을 보내지 않고 DB 도구 없이 답합니다.";

    /// <summary>다음 문장의 실제 바인드 이름이 이 대화의 자리표시 접두와 겹쳐 새 대화로 이어 갈 때의 안내.</summary>
    public const string GeneralPrefixChangedText = "문장에 이미 있는 바인드 이름이 자리표시와 겹쳐 새 대화로 시작합니다. 앞 대화의 내용은 이어 보내지 않습니다.";

    /// <summary>활성 SQL 탭에 문장이 없어 도구를 열지 못할 때의 안내.</summary>
    public const string GeneralNoStatementText = "활성 SQL 탭에 문장이 없어 DB 도구 없이 답합니다. 문장 위에 커서를 두고 보내면 그 문장 범위에서 DB 도구를 씁니다.";

    private LiteralVault? _generalVault; // 이 일반 대화의 리터럴 사전(가림이 필요한 접속일 때만)
    private string? _generalSignature; // 이 일반 대화가 시작된 접속(id)과 가림 상태
    private (int Generation, string Signature, AiConversationTarget? Route)? _textConfirmed; // 입력 글 확인을 받은 대화
    private (int Generation, string Key)? _scopeShown; // 대화에 마지막으로 보인 도구 범위 안내

    /// <summary>일반 대화에서 보내기 전에 준비를 마친 한 차례.</summary>
    private sealed record GeneralTurn(string Prompt, AiContext Context, bool Tools, LiteralVault? Vault, AiConversationTarget? Route, int Generation, bool Masking);

    private async Task GeneralFollowUpAsync(ChatFollowUp followUp, CancellationToken ct)
    {
        if (_preparing)
        {
            chat.AddAssistantText("앞서 보낸 글을 준비하는 중입니다. 끝난 뒤 다시 보내세요.");
            chat.RestoreFollowUp(followUp);
            return;
        }

        _command = null; // 튜닝 없이 보내는 일반 대화로 넘어왔다: 앞 명령 대화의 종류(상한·스키마·레포트 조건)가 남지 않게
        GeneralTurn? turn;
        _preparing = true;
        chat.IsBusy = true; // 준비하는 동안 새 대화로 바뀌지 않게
        try
        {
            try
            {
                turn = await PrepareGeneralAsync(followUp, ct).ConfigureAwait(true);
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

            if (turn is null)
            {
                chat.RestoreFollowUp(followUp);
                return;
            }

            await RunGeneralAsync(turn, followUp, ct).ConfigureAwait(true);
        }
        finally
        {
            chat.IsBusy = false;
        }
    }

    /// <summary>접속을 가르는 글: 접속 id(없으면 이름)와 가림 상태. 보낼 곳(프로필 id·주소)은 <see cref="AiConversationTarget"/> 이 따로 가른다.</summary>
    private static string SignatureOf(AiGeneralScope scope) =>
        (scope.ConnectionId is { } id && id != Guid.Empty ? "id:" + id.ToString("N") : "name:" + (scope.Context?.ConnectionName ?? string.Empty))
        + (scope.Masking ? "|mask" : "|plain");

    /// <summary>
    /// 일반 대화 한 차례를 보내도 되는지 확인하고 요청 글을 만든다. 보낼 곳이 바뀌었거나 접속·가림 상태가 바뀌었으면 앞 대화를 이어 보내지 않고 새 대화로 시작한다
    /// (앞 대화의 내용·이미지·도구 결과가 다른 곳으로 가지 않게). 보내면 안 되면 사유를 남기고 null.
    /// </summary>
    private async Task<GeneralTurn?> PrepareGeneralAsync(ChatFollowUp followUp, CancellationToken ct)
    {
        if (IsRunning)
        {
            return null;
        }

        if (followUp.Images.Count > AiImageNormalizer.MaxCount)
        {
            chat.AddAssistantText(string.Create(CultureInfo.CurrentCulture, $"이미지는 한 번에 {AiImageNormalizer.MaxCount}장까지 보낼 수 있습니다."));
            return null;
        }

        var startGeneration = chat.ConversationGeneration;
        var scope = generalContext is null ? new AiGeneralScope(null, false) : await generalContext(ct).ConfigureAwait(true);
        if (startGeneration != chat.ConversationGeneration)
        {
            return null; // 읽는 사이 대화가 바뀌었다: 옛 대화의 글을 새 대화로 보내지 않는다
        }

        // 기타 CLI 는 글만 주고받는다: 이미지·편집기 문장·DB 도구를 쓰지 않고, 값을 가려 보낼 수 없으므로 가림 대상 접속에는 보내지 않는다.
        // 보낼 곳은 아래 요청의 기대값(_route)이 되는 같은 값으로 한 번만 읽어, 판단과 실제 전송처가 어긋나지 않게 한다.
        var now = CurrentTarget();
        var custom = AiTuningCustomCli.IsCustom(now);
        if (custom && !CustomCliGeneralAllowed(followUp, now!, scope))
        {
            return null;
        }

        var connected = scope.Context;
        var tools = !custom && connected is { AccessLevel: not AiAccessLevel.Off };

        // 도구의 범위 = 활성 SQL 탭의 문장(튜닝과 같은 범위 계산을 쓴다). 편집기 맥락은 활성 문서와 무관하게 마지막 SQL 탭을 돌려주므로,
        // 활성 문서가 SQL 탭이 아니거나 그 탭의 접속이 지금 접속(scope)과 다르면 그 문장을 싣지 않고 도구도 닫는다. 판단할 수 없으면 닫는다.
        string? scopeText = null;
        string? closedText = null;
        var otherDocument = false;
        if (tools)
        {
            var (source, found) = AiStatementScope.Resolve(editor, scope.SqlDocumentActive, scope.ConnectionId);
            scopeText = found?.Text;
            otherDocument = source == AiStatementSource.OtherDocument;
            closedText = source switch
            {
                AiStatementSource.OtherDocument => GeneralOtherDocumentText,
                AiStatementSource.NoStatement => GeneralNoStatementText,
                _ => null,
            };
            tools = scopeText is not null;
        }

        if (custom)
        {
            closedText = AiTuningCustomCli.GeneralText;
        }

        // 다음 문장에 진짜 바인드가 있어 이 대화의 자리표시(예 :v1)와 겹치면 앞 사전을 쓸 수 없다: 새 대화로 간다.
        // 새 문장 자체가 어떤 접두를 고를지가 아니라, 새 문장의 바인드가 기존 사전의 접두와 겹치는지만 본다.
        if (tools && scope.Masking && chat.ResumeId is not null && _generalVault is { } existingVault && CollidesWithPrefix(scopeText!, existingVault))
        {
            chat.AddAssistantText(GeneralPrefixChangedText);
            chat.ForgetResume();
        }

        // 새 일반 대화(이어 갈 앞 대화가 없음)면 지금의 보낼 곳·접속을 기준으로 삼는다. 이어 가는 중에 달라졌으면 새 대화로 바꾼다.
        var signature = SignatureOf(scope);
        if (chat.ResumeId is not null)
        {
            if (_route is { } started && now is { } current && !started.IsSameAs(current))
            {
                chat.AddAssistantText(GeneralTargetChangedText);
                chat.ForgetResume();
            }
            else if (_generalSignature is { } before && before != signature)
            {
                chat.AddAssistantText(GeneralConnectionChangedText);
                chat.ForgetResume();
            }
        }

        if (chat.ResumeId is null)
        {
            _route = now;
            _generalSignature = signature;
            _generalVault = null;
        }

        var generation = chat.ConversationGeneration;
        var route = _route;

        // 가리는 접속: 문장도 가려서 보내고 도구 결과도 같은 사전으로 가린다. 가릴 수 없으면 도구를 닫는다.
        LiteralVault? vault = null;
        string? promptStatement = scopeText;
        if (tools && scope.Masking)
        {
            if (masker is null)
            {
                chat.AddAssistantText(GeneralMaskerMissingText);
                tools = false;
            }
            else
            {
                try
                {
                    vault = _generalVault ??= masker.CreateVault(scopeText!); // 접두를 못 고르는 등 사전을 못 만들어도 고정 글로 끝낸다
                    var masked = masker.Mask(scopeText!, vault);
                    if (masked.Succeeded)
                    {
                        vault.RememberStatement(masked.Text, scopeText!);
                        promptStatement = masked.Text;
                    }
                    else
                    {
                        chat.AddAssistantText(GeneralMaskFailedText);
                        tools = false;
                    }
                }
#pragma warning disable CA1031 // 가림 구현의 어떤 실패든 원래 글을 보내는 쪽으로 이어지면 안 된다: 도구를 닫는다.
                catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
                {
                    chat.AddAssistantText(GeneralMaskFailedText);
                    tools = false;
                }
            }
        }

        if (!tools)
        {
            vault = null;
            promptStatement = null; // 도구를 닫았으면(끔·가림 실패·가림 기능 없음) 문장도 보내지 않는다: 가림을 우회하지 않게
        }

        ShowScopeLine(generation, tools, closedText, scopeText);
        var styleReason = custom ? GeneralToolState.CustomCli : tools ? GeneralToolState.Tools : otherDocument ? GeneralToolState.OtherDocument : GeneralToolState.NoTools;

        var context = connected is null ? new AiContext(null, null, AiAccessLevel.Off) : tools ? connected with { StatementText = scopeText } : connected;

        // 가리는 대화: 입력한 글은 가려지지 않고 나간다는 확인을 대화당 한 번 받는다(접속이나 대화가 바뀌면 다시). 이미지는 장마다 따로 확인한다.
        // 글 없이 이미지만 보낼 때는 입력한 글이 없으므로 글 확인은 묻지 않는다(이미지는 아래에서 따로 확인한다).
        if (scope.Masking && followUp.Text.Length > 0 && !await ConfirmTextAsync(context, route, signature, generation, ct).ConfigureAwait(true))
        {
            return null;
        }

        if (followUp.Images.Count > 0 && !await ConfirmAndAuditImagesAsync(followUp, route, context, scope.Masking, ct).ConfigureAwait(true))
        {
            return null;
        }

        // 기다리는 동안 대화가 바뀌었거나 보낼 곳이 달라졌으면 확인한 곳과 다른 곳으로 나가지 않도록 접는다. 다시 보내면 새 대화로 시작한다.
        if (generation != chat.ConversationGeneration)
        {
            return null;
        }

        if (IsRunning || (route is { } confirmed && CurrentTarget() is { } latest && !confirmed.IsSameAs(latest)))
        {
            chat.AddAssistantText(GeneralSendCancelledText);
            chat.ForgetResume();
            return null;
        }

        var text = followUp.Text.Length == 0 ? "Look at the attached image(s) and answer." : followUp.Text;
        var prompt = text + Environment.NewLine + Environment.NewLine + GeneralStyle(styleReason, vault is not null)
            + (promptStatement is null ? string.Empty : Environment.NewLine + Environment.NewLine + "<statement>" + Environment.NewLine + promptStatement + Environment.NewLine + "</statement>")
            + (vault is null ? string.Empty : Environment.NewLine + PlaceholderReminder(vault, statementOnly: true));
        return new GeneralTurn(prompt, context, tools, vault, route, generation, scope.Masking || scope.UnsafeBinding || generalContext is null);
    }

    /// <summary>
    /// 기타 CLI 로 일반 대화를 보내도 되는지 확인한다. 쓸 수 없는 줄·이미지·가림 대상 접속이면 이유를 대화 창에 남기고 false(라우터도 같은 조건으로 닫지만,
    /// 여기서 먼저 막아 가림 확인 창이나 쓸데없는 준비를 거치지 않게 한다). 접속을 알 수 없는 대화(<paramref name="scope"/> 의 <c>Masking</c>·<c>UnsafeBinding</c>)와
    /// 접속 정보를 읽는 쪽이 없는 흐름도 가림 대상으로 본다(닫힌 쪽으로).
    /// </summary>
    private bool CustomCliGeneralAllowed(ChatFollowUp followUp, AiConversationTarget target, AiGeneralScope scope)
    {
        if (AiTuningCustomCli.IsUnavailable(target))
        {
            chat.AddAssistantText(AiTuningCustomCli.UnavailableText);
            return false;
        }

        if (followUp.Images.Count > 0)
        {
            chat.AddAssistantText(AiTuningCustomCli.ImagesText);
            return false;
        }

        if (scope.Masking || scope.UnsafeBinding || generalContext is null)
        {
            chat.AddAssistantText(AiTuningCustomCli.MaskingBlockedText);
            return false;
        }

        return true;
    }

    /// <summary>대화에 어느 문장이 도구의 범위인지(그 문장도 함께 보낸다는 뜻 포함) 한 줄로 보인다. 도구를 닫았으면 그 이유를 보인다. 바뀔 때만.</summary>
    private void ShowScopeLine(int generation, bool tools, string? closedText, string? scopeText)
    {
        var key = tools ? "S:" + scopeText : closedText ?? "off";
        if (_scopeShown is { } shown && shown.Generation == generation && shown.Key == key)
        {
            return;
        }

        _scopeShown = (generation, key);
        if (tools)
        {
            chat.AddAssistantText("DB 도구는 활성 탭의 이 문장 범위에서만 쓰며, 이 문장도 함께 보냅니다(가리는 대화는 값을 가려서): 「" + Shorten(scopeText!, 60) + "」");
        }
        else if (closedText is not null)
        {
            chat.AddAssistantText(closedText);
        }
    }

    /// <summary>가리는 대화에서 입력한 글이 가려지지 않고 나간다는 확인(대화당 1회). 보내도 되면 true.</summary>
    private async Task<bool> ConfirmTextAsync(AiContext context, AiConversationTarget? route, string signature, int generation, CancellationToken ct)
    {
        if (_textConfirmed is { } done && done.Generation == generation && done.Signature == signature
            && (done.Route is null ? route is null : route is not null && done.Route.IsSameAs(route)))
        {
            return true;
        }

        if (confirmText is null)
        {
            chat.AddAssistantText(TextConfirmMissingText);
            return false;
        }

        var notice = new AiImageSendNotice(
            (string.IsNullOrEmpty(context.ConnectionName) ? "(연결 없음)" : context.ConnectionName), context.Environment ?? ConnectionEnvironment.Development, route?.Label ?? "AI", route?.RecipientUrl,
            Count: 0, TotalBytes: 0, Masking: true, context.AccessLevel);
        if (!await confirmText(notice, ct).ConfigureAwait(true))
        {
            return false;
        }

        _textConfirmed = (generation, signature, route);
        return true;
    }

    /// <summary>
    /// 일반 대화 안내(매 차례): 튜닝 보고서 형식을 쓰지 않고, 도구가 있는지 없는지를 알린다.
    /// 도구가 있으면 범위가 활성 탭의 문장뿐임을, 가리는 대화면 자리표시가 도구 결과와 그 문장에만 해당함을 사실대로 적는다(사용자가 쓴 글은 가리지 않는다).
    /// </summary>
    internal static string GeneralStyle(GeneralToolState state, bool masked = false) =>
        "This is a general conversation inside Oranda, an Oracle development tool, not a SQL tuning request: answer the question directly in Korean, briefly, in Markdown; "
        + "do not produce a tuning report, index suggestions or a structured result unless the user asks for one. "
        + state switch
        {
            GeneralToolState.Tools =>
                $"The user's active editor statement is given below as the scope of the Oranda tools: use the tools only for that statement and its related objects, only when the answer needs facts from the connected database (at most {ToolBudget} tool calls); do not guess those facts. If the user asks about any SQL other than that statement, say that the tools cannot see it."
                + (masked ? " Literal values in that statement and in tool results are masked; the user's own message is not masked." : string.Empty),
            GeneralToolState.OtherDocument =>
                "No database tools are available in this conversation: the active document is not an SQL tab of the current connection (another connection's tab, a PL/SQL editor or an object detail), so its statement was not sent. If the question needs live database facts or that statement, say so and tell the user to activate the SQL tab of the current connection and put the cursor on a statement.",
            GeneralToolState.CustomCli =>
                "No database tools exist in this conversation and nothing but the user's message was sent: no editor statement and no database information. If the question needs live database facts or a particular statement, say that you cannot see them and ask the user to paste what is needed into the message.",
            _ =>
                "No database tools are available in this conversation (no connection, access is off, or no statement in the active SQL tab): if the question needs live database facts, say so and tell the user to connect and put the cursor on a statement.",
        };

    /// <summary>일반 대화 프롬프트가 알리는 도구 상태.</summary>
    internal enum GeneralToolState
    {
        /// <summary>활성 탭의 문장 범위에서 도구를 쓴다.</summary>
        Tools,

        /// <summary>연결 없음·허용 수준 끔·문장 없음·가림 실패 등으로 도구가 없다.</summary>
        NoTools,

        /// <summary>활성 문서가 지금 접속의 SQL 탭이 아니라 그 문장을 보내지 않고 도구가 없다.</summary>
        OtherDocument,

        /// <summary>기타 CLI 를 쓴다: 입력한 글만 보내고, 문장·DB 정보·도구가 없다.</summary>
        CustomCli,
    }

    /// <summary>새 문장의 실제 바인드(예 <c>:v1</c>)가 이 사전의 자리표시 접두와 겹친다.</summary>
    private static bool CollidesWithPrefix(string statement, LiteralVault vault) =>
        System.Text.RegularExpressions.Regex.IsMatch(statement, ":" + System.Text.RegularExpressions.Regex.Escape(vault.Prefix) + @"\d+", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private async Task RunGeneralAsync(GeneralTurn turn, ChatFollowUp followUp, CancellationToken ct)
    {
        if (turn.Generation != chat.ConversationGeneration)
        {
            chat.RestoreFollowUp(followUp); // 준비하는 사이 대화가 바뀌었다: 새 대화에 옛 요청의 줄을 만들지 않는다
            return;
        }

        chat.BeginTurn(followUp.Text.Length == 0 ? "(이미지)" : followUp.Text, followUp.Images);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _running = cts;
        AgentSession? session = null;
        try
        {
            session = (turn.Tools ? (generalSessionFactory ?? sessionFactory)?.Invoke(turn.Context, turn.Vault) : null) ?? OffSession(turn.Context);
            var request = new AiRequest(
                turn.Prompt, turn.Context, chat.ResumeId, OutputSchema: null, Model: null, session,
                Images: followUp.Images.Count == 0 ? null : [.. followUp.Images.Select(i => i.Attachment)],
                Expected: turn.Route?.ToExpectation(), // 확인 창에 적은 곳과 실제 전송처가 같게: 라우터가 설정의 활성 프로필과 대조한다
                MaskingRequired: turn.Masking); // 값을 가려 보낼 수 없는 기타 CLI 의 라우터만 읽는다(접속을 알 수 없는 대화는 이미 가리는 대화로 본다)
            await chat.RunAsync(driver.RunAsync(request, cts.Token), cts.Token).ConfigureAwait(true);
        }
        finally
        {
            _running = null;
            session?.Revoke();
        }
    }

    /// <summary>
    /// 도구가 없는 실행 범위: 허용 수준 「끔」 의 빈 접속. 게이트가 도구 목록을 비우고 모든 호출을 거부하므로 어떤 DB 도 건드리지 못한다.
    /// 드라이버 계약이 실행 범위를 늘 요구해서 쓰는 자리 채움이다.
    /// </summary>
    private static AgentSession OffSession(AiContext context) =>
        new(new AgentRunScope(
            Guid.NewGuid().ToString("N"),
            new ConnectionProfile(string.Empty, string.Empty, string.Empty, AiAccess: AiAccessLevel.Off),
            context with { AccessLevel = AiAccessLevel.Off, StatementText = null }));
}
