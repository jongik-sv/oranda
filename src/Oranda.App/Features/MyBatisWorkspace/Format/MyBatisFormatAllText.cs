using System.Globalization;
using Oranda.App.Features.Localization;

namespace Oranda.App.Features.MyBatisWorkspace.Format;

/// <summary>
/// 매퍼 전체 포맷·그 되돌리기 알림 글(한/영). 키는 <c>MyBatis.formatAll.*</c>(Resources/strings-ko·en.json), 없으면 한국어 기본값을 쓴다(<see cref="UiText"/>).
/// </summary>
internal static class MyBatisFormatAllText
{
    public static string NothingToFormat => T("nothing", "포맷할 쿼리가 없습니다.");

    public static string MapperChanged => T("mapperChanged", "포맷하는 사이 매퍼가 바뀌어 적용하지 않았습니다.");

    public static string ReadOnly => T("readOnly", "읽기 전용(같은 id 가 여럿)");

    public static string ChangedWhileFormatting => T("changedWhileFormatting", "포맷하는 사이 글이 바뀌었습니다");

    public static string KeptHeading => T("keptHeading", "원문을 유지한 쿼리:");

    public static string NothingToUndo => T("nothingToUndo", "되돌릴 전체 포맷이 없습니다.");

    public static string SkippedHeading => T("skippedHeading", "그 사이 고쳐서 건너뛴 쿼리:");

    public static string Summary(int formatted, int already, int kept) =>
        F("summary", "매퍼 전체 포맷을 마쳤습니다: {0}개 정리, {1}개는 이미 정리됨, {2}개는 원문 유지.", formatted, already, kept);

    /// <summary>목록 한 줄(글자가 없어 번역 키를 두지 않는다).</summary>
    public static string Reason(string id, string reason) => $"· {id}: {reason}";

    public static string More(int count) => F("more", "외 {0}개가 더 있습니다.", count);

    public static string Undone(int reverted, int skipped) =>
        F("undone", "전체 포맷을 되돌렸습니다: {0}개 되돌림, {1}개는 그 사이 고쳐서 건너뜀.", reverted, skipped);

    private static string T(string key, string ko) => UiText.Get("MyBatis.formatAll." + key, ko);

    private static string F(string key, string ko, params object[] args) => string.Format(CultureInfo.CurrentCulture, T(key, ko), args);
}
