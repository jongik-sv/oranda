namespace Oranda.Core.Ai.Gate;

/// <summary>
/// SQL 글의 리터럴 가림·되돌림(설계 ai-tuning-2). 구현은 <c>Oranda.Language</c> 의 <c>SqlLiteralMasker</c>(렉서 토큰 기준)이고,
/// Core 는 렉서를 참조하지 않으므로 인터페이스만 둔다. 부르는 곳은 둘뿐이다: 앱의 AI 튜닝 흐름(프롬프트 가림, 제안·인덱스 문안 되돌림)과
/// <see cref="IAgentGate"/>(도구 인자 되돌림, 결과의 <see cref="AgentFields.SqlTextFields"/> 가림). 도구 처리자마다 부르지 않는다.
/// 모든 메서드는 실패해도 원래 값을 결과에 싣지 않는다(닫힌 쪽으로 실패).
/// </summary>
public interface ISqlLiteralMasker
{
    /// <summary>
    /// 대화 하나에 쓸 빈 사전을 만든다. 대상 문장 <paramref name="statementText"/> 에 이미 <c>:v1</c> 꼴 바인드가 있으면
    /// 자리표시가 겹치지 않도록 접두를 바꾼다(예: <c>:lit1</c>).
    /// </summary>
    LiteralVault CreateVault(string statementText);

    /// <summary>
    /// 문장 글의 리터럴(문자열·숫자·<c>DATE '…'</c> 묶음)을 자리표시로 바꾸고 주석을 주석 자리표시(<c>--c1</c>·<c>/*c1*/</c>)로 바꾼다(힌트는 유지).
    /// 주석 본문은 LLM 에 보내지 않고 사전(<see cref="LiteralVault.AddComment"/>)에만 둔다.
    /// 가린 글과 원래 글은 <see cref="LiteralVault.RememberStatement"/> 로 짝을 지어 둔다.
    /// 렉서가 읽지 못하는 글(Error 토큰·닫히지 않은 문자열)은 <see cref="SqlMaskResult.Failed"/> 를 돌려준다.
    /// </summary>
    SqlMaskResult Mask(string sql, LiteralVault vault);

    /// <summary>
    /// <see cref="Mask"/> 로 가린 글의 자리표시를 원래 토큰 글로 되돌린다. 바인드 토큰 중 사전에 있는 것만 바꾸고(따옴표로 감싼 <c>':v1'</c> 도 자리표시로 본다)
    /// 주석 자리표시는 같은 종류(줄·블록)의 원래 주석으로 되돌린다. 모르는 번호·종류가 다른 주석 자리표시는 그대로 둔다(실패로 보지 않는다).
    /// 글 전체가 기억한 가린 글과 같으면 원래 글(주석·sql_id 보존)을 쓴다.
    /// 사전의 모양인데 사전에 없는 자리표시가 있으면 <see cref="SqlRestoreResult.UnknownPlaceholder"/> 를 채워 돌려준다.
    /// </summary>
    SqlRestoreResult Restore(string sql, LiteralVault vault);

    /// <summary>
    /// 실행 계획의 술어 칸(<c>access_predicates</c>·<c>filter_predicates</c>)이나 <see cref="AgentFields.SqlTextFields"/> 의 글에서 리터럴과 주석을 <see cref="Mask"/> 와 같은 방식으로 가린다
    /// (같은 사전을 쓰므로 프롬프트에 나온 주석은 같은 자리표시가 된다).
    /// 읽지 못하면 칸 전체를 <see cref="RedactedPlaceholder"/> 로 바꾼다.
    /// </summary>
    string MaskPredicate(string predicate, LiteralVault vault);

    /// <summary>술어를 읽지 못해 칸 전체를 가릴 때 돌려주는 글.</summary>
    const string RedactedPlaceholder = "[가림]";
}

/// <summary><see cref="ISqlLiteralMasker.Mask"/> 결과. 실패하면 글이 없다(원래 값이 결과에 실리지 않게).</summary>
public sealed class SqlMaskResult
{
    private SqlMaskResult(bool succeeded, string text)
    {
        Succeeded = succeeded;
        Text = text;
    }

    /// <summary>가렸다.</summary>
    public bool Succeeded { get; }

    /// <summary>가린 글. 실패하면 빈 글.</summary>
    public string Text { get; }

    public static SqlMaskResult Ok(string maskedText) => new(true, maskedText ?? throw new ArgumentNullException(nameof(maskedText)));

    public static SqlMaskResult Failed { get; } = new(false, string.Empty);

    public override string ToString() => Succeeded ? "Masked(" + Text.Length + ")" : "MaskFailed";
}

/// <summary><see cref="ISqlLiteralMasker.Restore"/> 결과. <see cref="Text"/> 에는 원래 값이 들어 있으므로 LLM 으로 가는 길에 싣지 않는다(문자열로도 내지 않는다).</summary>
public sealed class SqlRestoreResult
{
    private SqlRestoreResult(string text, string? unknownPlaceholder)
    {
        Text = text;
        UnknownPlaceholder = unknownPlaceholder;
    }

    /// <summary>되돌린 글(원래 값 포함). 모르는 자리표시가 있으면 빈 글.</summary>
    public string Text { get; }

    /// <summary>사전의 모양인데 사전에 없는 자리표시(예: <c>:v9</c>). 없으면 null. 우리가 만든 이름이라 값이 아니다.</summary>
    public string? UnknownPlaceholder { get; }

    public bool Succeeded => UnknownPlaceholder is null;

    public static SqlRestoreResult Ok(string restoredText) => new(restoredText ?? throw new ArgumentNullException(nameof(restoredText)), null);

    public static SqlRestoreResult Unknown(string placeholder) => new(string.Empty, placeholder ?? throw new ArgumentNullException(nameof(placeholder)));

    public override string ToString() => Succeeded ? "Restored" : "Unknown(" + UnknownPlaceholder + ")";
}
