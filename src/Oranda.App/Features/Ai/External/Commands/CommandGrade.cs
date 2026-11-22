using Oranda.Core.Ai.Schemas;

namespace Oranda.App.Features.Ai.External.Commands;

/// <summary>
/// 밖의 에이전트가 <c>run_command</c> 로 앱 명령을 부를 때의 안전 등급(2단계 계획 §3.3).
/// 계약 글(<c>now</c>·<c>confirm</c>·<c>deny</c>)은 <see cref="CommandGradeNames.ToWire"/> 한 곳에서만 만든다.
/// </summary>
public enum CommandGrade
{
    /// <summary>바로 실행: 화면만 바뀜·창을 열기만 하고 끝은 사람이 함·새 탭·읽기 화면.</summary>
    Now,

    /// <summary>띠 한 번: 디스크에 씀·탭/작업 공간 닫기(고친 글 잃을 수 있음)·연결 끊기.</summary>
    Confirm,

    /// <summary>거부: 글 바꿈·DB 실행/변경·커밋·세션 죽이기·클립보드·앱 종료·AI 띄우기·비밀번호·표에 없는 명령.</summary>
    Deny,
}

/// <summary>등급 ↔ 계약 글(<see cref="AppToolValues.CommandGrades"/>).</summary>
public static class CommandGradeNames
{
    private const string NowText = AppToolValues.CommandGrades.Now;
    private const string ConfirmText = AppToolValues.CommandGrades.Confirm;
    private const string DenyText = AppToolValues.CommandGrades.Deny;

    public static string ToWire(this CommandGrade grade) => grade switch
    {
        CommandGrade.Now => NowText,
        CommandGrade.Confirm => ConfirmText,
        _ => DenyText,
    };

    /// <summary>계약 글 → 등급. 모르는 글이면 null(거르기 인자 검사용).</summary>
    public static CommandGrade? FromWire(string? text) => text switch
    {
        NowText => CommandGrade.Now,
        ConfirmText => CommandGrade.Confirm,
        DenyText => CommandGrade.Deny,
        _ => null,
    };
}
