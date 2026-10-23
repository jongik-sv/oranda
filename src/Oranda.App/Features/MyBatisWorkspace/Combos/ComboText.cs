using System.Globalization;
using Oranda.App.Features.Localization;

namespace Oranda.App.Features.MyBatisWorkspace.Combos;

/// <summary>
/// 조합 검사 화면 글(한/영). 키는 <c>MyBatis.combos.*</c>(Resources/strings-ko·en.json), 없으면 한국어 기본값을 쓴다(<see cref="UiText"/>).
/// </summary>
internal static class ComboText
{
    public static string On => T("on", "켬");

    public static string Off => T("off", "끔");

    public static string Pass => T("pass", "통과");

    public static string EmptySet => T("emptySet", "통과(주의: 값이 모두 비면 SET 절 없음)");

    public static string Unsupported => T("unsupported", "통과(문법이 모르는 구문)");

    public static string PlanNotApplicable => T("planNotApplicable", "통과(실행 계획 대상 아님)");

    public static string PlanNotApplicableDetail => T("planNotApplicableDetail", "PL/SQL·트랜잭션 제어 문장은 실행 계획을 볼 수 없습니다.");

    public static string PlanDenied => T("planDenied", "통과(실행 계획 거부)");

    public static string PlanError => T("planError", "실행 계획 오류");

    public static string PlanErrorDetail(string message) => F("planErrorDetail", "실행 계획 오류: {0}", message);

    public static string NoSessionNotice => T("noSession", "접속이 없어 구문 분석만 했습니다.");

    public static string EvalError => T("evalError", "계산 오류");

    public static string SyntaxError => T("syntaxError", "구문 오류");

    public static string StrategyAll => T("strategy.all", "전부 (조건 8개 이하)");

    public static string StrategyReduce => T("strategy.reduce", "줄이기 (하나씩 끄기)");

    public static string ModeSyntax => T("mode.syntax", "구문 분석");

    public static string ModePlan => T("mode.plan", "구문 분석 + 실행 계획");

    public static string Hint => T("hint", "MyBatis 문서에서 문장을 고르고 「조합 검사」를 누르세요.");

    public static string Footnote => T("footnote", "조건이 많으면 「하나씩 끄기 + 모두 켬 + 모두 끔」으로 줄입니다(if 10개면 1024개 → 12개). 시나리오가 있으면 그 값 묶음도 함께 검사합니다.");

    public static string Checking => T("checking", "검사하는 중…");

    /// <summary>머리 줄 진행 글(끝난 조합·시나리오 수 / 전체).</summary>
    public static string Progress(int done, int total) => F("progress", "검사 중 {0}/{1}", done, total);

    public static string NoEvaluator => T("noEvaluator", "조합 검사를 할 수 없습니다(MyBatis 계산기가 없습니다).");

    public static string CannotSaveScenario => T("cannotSaveScenario", "시나리오를 저장할 수 없습니다.");

    public static string NoFix => T("noFix", "자동으로 고칠 자리가 없습니다(SET·WHERE 바로 뒤에 <if> 가 이어지는 모양만 고칩니다).");

    public static string FixReadOnly => T("fixReadOnly", "읽기 전용 문장이라 고칠 수 없습니다.");

    public static string SubstitutionPending => T("substitutionPending", "통과(주의: 치환 값 없음)");

    public static string FixGone => T("fixGone", "문장이 바뀌어 고칠 자리가 없습니다.");

    public static string FixSet => T("fix.set", "<set> 으로 고치기");

    public static string FixWhere => T("fix.where", "<where> 로 고치기");

    public static string FixSetDescription => T("fix.setDescription", "SET 뒤의 <if> 들을 <set> 으로 감싸면 MyBatis 가 끝 쉼표와 빈 SET 을 정리합니다.");

    public static string FixWhereDescription => T("fix.whereDescription", "WHERE 뒤의 <if> 들을 <where> 로 감싸면 MyBatis 가 앞 AND·OR 와 빈 WHERE 를 정리합니다.");

    public static string FixPreviewTip => T("fix.previewTip", "미리 보고 적용합니다.");

    public static string PreviewChecking => T("preview.checking", "고친 뒤 결과를 검사하는 중…");

    public static string Combination(int number) => F("combination", "조합 {0}", number);

    public static string Passed(int count) => F("passed", "통과 {0}", count);

    public static string Errors(int count) => F("errors", "오류 {0}", count);

    public static string EmptySetDetail(int line) => F("emptySetDetail", "값이 모두 비면 <set> 이 SET 절을 만들지 않아 실행할 수 없습니다 (줄 {0} 의 <set>).", line);

    public static string EvalErrorDetail(string reason, int line) => F("evalErrorDetail", "계산 오류: {0} (줄 {1})", reason, line);

    public static string PlanChecked(int steps) => F("planChecked", "실행 계획 확인: 단계 {0}개", steps);

    public static string CommaBefore(string word) => F("commaBefore", "{0} 앞 쉼표", word);

    public static string CommaBeforeDetail(string word) => F("commaBeforeDetail", "{0} 앞에 쉼표가 남습니다", word);

    public static string CommaAtEnd => T("commaAtEnd", "끝에 쉼표");

    public static string CommaAtEndDetail => T("commaAtEndDetail", "끝에 쉼표가 남습니다");

    public static string EmptyAfterSet => T("emptyAfterSet", "SET 뒤가 비어 있음");

    public static string EmptyAfterSetDetail => T("emptyAfterSetDetail", "SET 뒤에 바꿀 열이 없습니다");

    public static string RightAfterWhere(string word) => F("rightAfterWhere", "WHERE 바로 뒤 {0}", word);

    public static string RightAfterWhereDetail(string word) => F("rightAfterWhereDetail", "WHERE 바로 뒤에 {0} 가 옵니다", word);

    public static string EmptyAfter(string word) => F("emptyAfter", "{0} 뒤가 비어 있음", word);

    public static string CheckFailed(string message) => F("checkFailed", "검사하지 못했습니다: {0}", message);

    public static string SaveFailed(string message) => F("saveFailed", "시나리오를 저장하지 못했습니다: {0}", message);

    public static string SubstitutionPendingDetail(string name) => F("substitutionPendingDetail", "${{{0}}} 값이 없어 빈 글자로 펼쳤습니다. 시나리오에 값을 넣으면 확인합니다.", name);

    public static string EmptyAfterDetail(string word) => F("emptyAfterDetail", "{0} 뒤에 조건이 없습니다", word);

    public static string SyntaxDetail(string what) => F("syntaxDetail", "구문 오류: {0}", what);

    public static string SyntaxDetailAt(string what, int line, string lineText) => F("syntaxDetailAt", "구문 오류: {0} (줄 {1} 의 {2})", what, line, lineText);

    public static string StatementMissing(string id) => F("statementMissing", "문장 {0} 을(를) 찾을 수 없습니다.", id);

    public static string ScenarioSaved(string name) => F("scenarioSaved", "시나리오 「{0}」 을(를) 저장했습니다.", name);

    public static string ScenarioSavedAdjust(string name) => F("scenarioSavedAdjust", "시나리오 「{0}」 을(를) 저장했습니다. test 식에 맞게 값을 고쳐야 같은 조합이 됩니다.", name);

    public static string Cancelled(int total, int done) => F("cancelled", "취소했습니다(조합 {0}개 중 {1}개 검사).", total, done);

    public static string Finished(int pass, int errors, string tail) => F("finished", "검사를 마쳤습니다: 통과 {0}, 오류 {1}.{2}", pass, errors, tail);

    public static string Heading(string id, int conditions, int combinations) => F("heading", "{0} · 조건 {1}개 → 조합 {2}개", id, conditions, combinations);

    public static string HeadingPending(string id, int conditions) => F("headingPending", "{0} · 조건 {1}개", id, conditions);

    public static string FixHeading(int first, int last) => F("fix.heading", "빠른 고침 · 줄 {0}–{1}", first, last);

    public static string PreviewTitle(string action) => F("preview.title", "미리보기: {0}", action);

    public static string PreviewAllPass(int count) => F("preview.allPass", "고친 뒤: 조합 {0}개가 모두 통과합니다.", count);

    public static string PreviewErrorsLeft(int count, int errors) => F("preview.errorsLeft", "고친 뒤: 조합 {0}개 중 오류 {1}개가 남습니다.", count, errors);

    public static string Badge(int errors) => F("badge", "조합 오류 {0}", errors);

    public static string BadgeTip(int combinations, int errors) => F("badgeTip", "마지막 조합 검사: 조합 {0}개 중 오류 {1}", combinations, errors);

    private static string T(string key, string ko) => UiText.Get("MyBatis.combos." + key, ko);

    private static string F(string key, string ko, params object[] args) => string.Format(CultureInfo.CurrentCulture, T(key, ko), args);
}
