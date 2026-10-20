using System.Text.RegularExpressions;

namespace Oranda.Language.Editing;

/// <summary>플래시백 조회가 실패했을 때 ORA 번호별 이유 한 줄(TSK-13-05).</summary>
public static partial class FlashbackErrorAdvisor
{
    /// <summary>오류 글에서 ORA 번호를 찾아 이유를 돌려준다. 플래시백과 관련 없는 오류면 null.</summary>
    public static string? Explain(string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return null;
        }

        foreach (Match m in OraCode().Matches(message))
        {
            if (Reason(m.Groups[1].Value) is { } why)
            {
                return why;
            }
        }

        return null;
    }

    /// <summary>표 정의가 바뀌어 그 시점 값을 읽을 수 없는 오류(ORA-01466)인지.</summary>
    public static bool IsDefinitionChanged(string? message) =>
        message is not null && message.Contains("ORA-01466", StringComparison.Ordinal);

    private static string? Reason(string code) =>
        code switch
        {
            "01555" => "undo 보관 기간(undo_retention)이 지나 그 시점의 값을 undo 에서 읽을 수 없습니다(ORA-01555). 더 짧은 시간을 넣어 보세요.",
            "08180" => "요청한 시각이 너무 오래전이라 그 시각에 맞는 SCN 을 찾을 수 없습니다(ORA-08180). 더 짧은 시간을 넣어 보세요.",
            "01466" => "그 시각 뒤에 표 정의가 바뀌어(DDL, 표 생성 포함) 그 시점의 값을 읽을 수 없습니다(ORA-01466).",
            "01031" => "FLASHBACK 권한이 부족합니다(ORA-01031). 표 소유자에게 FLASHBACK 또는 FLASHBACK ANY TABLE 권한을 받아야 합니다.",
            "30052" => "VERSIONS 의 시작 시각이 undo 보관 범위를 벗어났습니다(ORA-30052). 더 짧은 시간을 넣어 보세요.",
            "00942" => "표나 뷰가 없거나 읽을 권한이 없습니다(ORA-00942).",
            _ => null,
        };

    [GeneratedRegex(@"ORA-(\d{5})", RegexOptions.CultureInvariant)]
    private static partial Regex OraCode();
}
