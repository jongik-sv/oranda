namespace Oranda.Language.Editing;

/// <summary>
/// 「과거 값 결과」 판정 결과. <see cref="Reason"/> 이 비면 단일 표 플래시백 조회 결과이고 <see cref="Table"/> 이 그 표다.
/// </summary>
/// <param name="Kind">플래시백 절 종류</param>
/// <param name="Table">FROM 절 표(판정에 실패하면 null)</param>
/// <param name="Reason">쓸 수 없는 이유(쓸 수 있으면 빈 글)</param>
public sealed record FlashbackResultShape(FlashbackClauseKind Kind, SelectTableRef? Table, string Reason)
{
    public bool IsPastValues => Reason.Length == 0;
}

/// <summary>
/// 실행한 문장이 「과거 값 결과」인가(「이 행 상태로 되돌리기」): 단일 표 SELECT 이고 그 표 뒤에 <c>VERSIONS BETWEEN …</c> 또는 <c>AS OF …</c> 절이 있다.
/// 앱이 만든 질의에 표시를 다는 대신 문장 자체를 본다(사용자가 고친 문장도 같은 규칙으로 판정한다).
/// </summary>
public static class FlashbackResultAnalyzer
{
    public const string NotFlashback = "「이 행 상태로 되돌리기」는 테이블 하나를 VERSIONS BETWEEN … 또는 AS OF … 로 조회한 결과에서 씁니다.";

    public const string MultipleTables = "여러 테이블이 얽힌 결과에서는 쓸 수 없습니다.";

    public const string Remote = "DB 링크 테이블의 플래시백 결과는 되돌릴 수 없습니다.";

    public const string Period = "유효 시간(PERIOD FOR) 플래시백 결과는 되돌릴 수 없습니다.";

    /// <summary>문장 본문을 판정한다.</summary>
    public static FlashbackResultShape Analyze(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var shape = SelectAnalyzer.Analyze(text);
        if (shape.Block != Oranda.Core.Grid.EditBlockReason.None)
        {
            return new FlashbackResultShape(FlashbackClauseKind.None, null, NotFlashback);
        }

        var flashback = shape.Tables.FirstOrDefault(t => t.Flashback != FlashbackClauseKind.None);
        if (flashback is null)
        {
            return new FlashbackResultShape(FlashbackClauseKind.None, null, NotFlashback);
        }

        if (shape.Tables.Count > 1)
        {
            return new FlashbackResultShape(flashback.Flashback, null, MultipleTables);
        }

        if (flashback.IsRemote)
        {
            return new FlashbackResultShape(flashback.Flashback, flashback, Remote);
        }

        return flashback.Flashback == FlashbackClauseKind.Period
            ? new FlashbackResultShape(flashback.Flashback, flashback, Period)
            : new FlashbackResultShape(flashback.Flashback, flashback, string.Empty);
    }
}
