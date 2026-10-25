namespace Oranda.App.Shell.Features;

/// <summary>WP-07 명령 ID(TSK-07-01 계약). 처리기는 TSK-07-04 의 <c>Shell/Settings/FeatureSettingsCommands.cs</c>.</summary>
public static class FeatureCommandIds
{
    /// <summary>도구 메뉴 「Role·기능 고르기…」. 설정 창을 「기능」 탭으로 연다(RL-12). 블로커 칸의 「설정 열기」·시작 화면 기본 Role 카드의 「바꾸기…」(D-013) 도 이것을 실행한다.</summary>
    public const string Roles = "oranda.tools.roles";
}
