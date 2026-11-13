using Oranda.Core.Grid;
using Oranda.Core.Rollback.Compare;
using Oranda.Language.Editing;

namespace Oranda.App.Features.Flashback;

/// <summary>
/// 「이 행 상태로 되돌리기」 어댑터: 플래시백 결과 행(<c>VERSIONS BETWEEN …</c>·<c>AS OF …</c>) → 화면 3 의 목표 값 입력(<see cref="CompareTargetRow"/>).
/// 표·키·열 대응은 <see cref="FlashbackTarget"/> 를 그대로 쓰고, 플래시백 가상 열(<c>VERSIONS_*</c>·<c>ORA_ROWSCN</c>)은 뺀다(LOB 등 큰 열은 대상이 이미 뺀다).
/// 되돌리는 동작(미리 보기·반영·반대 문장 기록)은 화면 3 이 한다.
/// </summary>
internal static class FlashbackRowRestore
{
    public const string OperationColumn = "VERSIONS_OPERATION";

    /// <summary>플래시백 가상 열인가(<c>VERSIONS_STARTSCN</c>·<c>VERSIONS_OPERATION</c> 등과 <c>ORA_ROWSCN</c>).</summary>
    public static bool IsPseudoColumn(string name) =>
        name.StartsWith("VERSIONS_", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "ORA_ROWSCN", StringComparison.OrdinalIgnoreCase);

    /// <summary>대상의 읽을 열 순서(<see cref="FlashbackTarget.Ordinals"/>)로 읽은 행 → 목표 값 행.</summary>
    public static CompareTargetRow TargetOf(FlashbackTarget target, IReadOnlyList<object?> row)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(row);
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        for (var p = 0; p < target.Names.Count && p < row.Count; p++)
        {
            if (!IsPseudoColumn(target.Names[p]))
            {
                values[target.Names[p]] = row[p] is DBNull ? null : row[p];
            }
        }

        return new CompareTargetRow(values);
    }

    /// <summary>결과 열 가운데 <c>VERSIONS_OPERATION</c> 열 번호. 없으면 -1.</summary>
    public static int OperationOrdinal(IReadOnlyList<ResultColumn> columns)
    {
        ArgumentNullException.ThrowIfNull(columns);
        for (var i = 0; i < columns.Count; i++)
        {
            if (string.Equals(columns[i].Name, OperationColumn, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// 화면 3 시점 줄에 보일 목표 값 이름: VERSIONS 결과는 그 버전의 동작(I·U·D), AS OF 결과는 절 원문.
    /// 지운 행(D)은 그 지우기 직전 모습이라 지금 없으면 INSERT 로 되살리고, 넣은·바꾼 행(I·U)은 그 값으로 UPDATE 한다(지금 없으면 INSERT).
    /// </summary>
    public static string Label(FlashbackResultShape shape, string? operation)
    {
        ArgumentNullException.ThrowIfNull(shape);
        if (shape.Kind == FlashbackClauseKind.Versions)
        {
            var what = operation?.Trim().ToUpperInvariant() switch
            {
                "I" => "넣은 행(I)",
                "U" => "바꾼 행(U)",
                "D" => "지운 행(D)의 마지막 모습",
                _ => "버전 행",
            };
            return "고른 " + what;
        }

        return "고른 행 · " + (shape.Table?.FlashbackText ?? "AS OF");
    }
}
