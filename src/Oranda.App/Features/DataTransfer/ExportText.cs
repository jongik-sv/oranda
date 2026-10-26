using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Oranda.App.Features.Localization;

namespace Oranda.App.Features.DataTransfer;

/// <summary>내보내기 대화상자가 만드는 글(한/영). 키는 <c>Export.*</c>(Resources/strings-ko·en.json), 없으면 한국어 기본값을 쓴다(<see cref="UiText"/>).</summary>
internal static class ExportText
{
    public static string DefaultBaseName => T("defaultName", ExcelExportPlan.DefaultBaseName);

    public static string InsertFormat => T("format.insert", "INSERT 문(.sql)");

    public static string ScopeLoaded(long rows) => F("scope.loaded", "그리드에 불러온 행({0}행, 다시 실행하지 않음)", N(rows));

    public static string ScopeVisible(long rows) => F("scope.visible", "보이는 행만({0}행, 거르기 적용)", N(rows));

    public static string ScopeFetched(long rows) => F("scope.fetched", "가져온 전체({0}행, 거르기 무시)", N(rows));

    public static string ScopeSelection(long rows, int columns) => F("scope.selection", "선택한 셀({0}행 × {1}열)", N(rows), N(columns));

    public static string ScopeNoSelection => T("scope.noSelection", "선택한 셀(선택 없음)");

    public static string AllRowsUnavailable => T("scope.allUnavailable", "다시 실행할 문장을 알 수 없어 고를 수 없습니다.");

    public static string HintServer => T("hint.server", "질의를 서버에서 다시 실행해 처음부터 끝까지 저장합니다(그리드에 불러온 행만이 아닙니다).");

    public static string HintGrid => T("hint.grid", "서버에 다시 묻지 않고 그리드 값을 화면 순서대로 저장합니다(정렬·숨긴 열·고친 값 반영).");

    public static string PathEmpty => T("path.empty", "저장할 파일 경로를 넣으세요.");

    public static string PathNotRooted => T("path.notRooted", "폴더부터 적은 전체 경로를 넣으세요.");

    public static string PathInvalid => T("path.invalid", "파일 이름에 쓸 수 없는 글자가 있습니다.");

    public static string PathIsFolder => T("path.isFolder", "폴더입니다. 끝에 파일 이름을 붙이세요.");

    public static string PathNoFolder(string folder) => F("path.noFolder", "폴더가 없습니다: {0}", folder);

    public static string PathExists => T("path.exists", "같은 이름의 파일이 있습니다. 내보내면 덮어씁니다.");

    public static string Running(long rows) => F("running", "내보내는 중… {0}행", N(rows));

    public static string Done(long rows, double seconds, string path) =>
        F("done", "{0}행을 내보냈습니다({1}초): {2}", N(rows), seconds.ToString("0.0", CultureInfo.CurrentCulture), path);

    public static string Canceled => T("canceled", "내보내기를 취소했습니다. 저장 중이던 파일은 지웠습니다.");

    public static string Failed(string reason) => F("failed", "내보내기에 실패했습니다: {0}", reason);

    public static string OpenFailed(string path) => F("openFailed", "파일을 기본 앱으로 열지 못했습니다: {0}", path);

    public static string NoResult => T("noResult", "내보낼 결과가 없습니다.");

    public static string NoQuery => T("noQuery", "다시 실행할 질의를 알 수 없습니다.");

    public static string BindMissing(IReadOnlyList<string> names) =>
        F("bindMissing", "바인드 변수 {0} 값이 없습니다 — 바인드 변수 창에서 값을 넣고 다시 실행하세요.", string.Join(", ", names.Select(n => ":" + n)));

    public static string BindInvalid(string name, string? reason) =>
        F("bindInvalid", "바인드 변수 :{0} 값을 읽을 수 없습니다 — 바인드 변수 창에서 고치고 다시 실행하세요. ({1})", name, reason ?? "형식이 맞지 않음");

    public static string NoGrid => T("noGrid", "내보낼 결과 그리드가 없습니다.");

    public static string PickerTitle => T("pickerTitle", "내보낼 파일");

    private static string N(long value) => value.ToString("N0", CultureInfo.CurrentCulture);

    private static string T(string key, string ko) => UiText.Get("Export." + key, ko);

    private static string F(string key, string ko, params object[] args) => string.Format(CultureInfo.CurrentCulture, T(key, ko), args);
}
