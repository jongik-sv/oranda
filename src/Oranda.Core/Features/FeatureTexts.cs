namespace Oranda.Core.Features;

/// <summary>
/// WP-07 이 쓰는 화면 문구 키(TRD 10.8). 글자는 App 의 <c>Resources/strings-ko.json</c>·<c>strings-en.json</c> 에 계약 Task 가 미리 넣었다
/// (한국어 글자 그대로는 <c>literals-en.json</c> 에도 있다). 기능 Task 는 문구 파일을 고치지 않고 이 키를 <c>IShellText.Lookup</c> 으로 부른다.
/// <c>Shell.</c> 머리를 쓰지 않는 까닭: LocalizationTests 가 코드에서 안 쓰는 <c>Shell.*</c> 키를 실패로 본다.
/// </summary>
public static class FeatureTexts
{
    /// <summary>기능 이름 키 머리(<c>Feature.gridEdit</c>). <see cref="FeatureDefinition.NameKey"/>.</summary>
    public const string FeatureNamePrefix = "Feature.";

    /// <summary>Role 이름 키 머리(<c>Role.sqlDev</c>, 설명은 <c>Role.sqlDev.desc</c>). <see cref="RoleDefinition.NameKey"/>.</summary>
    public const string RoleNamePrefix = "Role.";

    /// <summary>묶음 이름 키 머리(<c>FeatureGroup.DevAssist</c>). <see cref="FeatureCatalog.GroupNameKey"/>.</summary>
    public const string GroupNamePrefix = "FeatureGroup.";

    // ---- 설정 창 「기능」 탭 (TSK-07-04) ----

    /// <summary>기능</summary>
    public const string SettingsTab = "Features.tab";

    /// <summary>Role 목록</summary>
    public const string SettingsRoles = "Features.roles";

    /// <summary>기능 세부</summary>
    public const string SettingsDetails = "Features.details";

    /// <summary>모든 기능 쓰기</summary>
    public const string ModeAll = "Features.mode.all";

    /// <summary>Role 로 고르기</summary>
    public const string ModeRoles = "Features.mode.roles";

    /// <summary>Role 기본</summary>
    public const string StateRoleDefault = "Features.state.roleDefault";

    /// <summary>직접 켬</summary>
    public const string StateEnabled = "Features.state.enabled";

    /// <summary>직접 끔</summary>
    public const string StateDisabled = "Features.state.disabled";

    /// <summary>Role 기본값으로 되돌리기</summary>
    public const string ResetToRoleDefaults = "Features.reset";

    /// <summary>재시작하면 적용됩니다: {0}  ({0} = 기능 이름 목록)</summary>
    public const string RestartBanner = "Features.restartBanner";

    /// <summary>{0} 이(가) 켜져 있어야 합니다  ({0} = 기능 이름)</summary>
    public const string RequiresFeature = "Features.requires";

    /// <summary>조회 전용만 고르면 DML·DDL·PL/SQL 실행을 막습니다</summary>
    public const string ViewerNotice = "Features.viewerNotice";

    /// <summary>조회 전용만 고른 상태에서는 켤 수 없습니다. 다른 Role 을 함께 고르세요.</summary>
    public const string WritesOnlyBlocked = "Features.writesOnlyBlocked";

    /// <summary>Diagnostics·Tuning Pack 보유 선언은 접속 프로필에서 합니다(D-012)</summary>
    public const string PackNotice = "Features.packNotice";

    // ---- 실행 거부 (TSK-07-05) ----

    /// <summary>조회 전용 Role 에서는 {0} 을(를) 실행할 수 없습니다. … ({0} = 문장 종류)</summary>
    public const string ReadOnlyRoleDenied = "Features.readOnlyRoleDenied";

    /// <summary>
    /// <see cref="ReadOnlyRoleDenied"/> 의 한국어 글자. Core 정책 관문(<c>ExecutionPolicy</c>)은 App 문구 표를 못 읽으므로 이 상수를 쓴다
    /// (읽기 전용 프로필 거부 문구와 같은 방식). <c>{0}</c> = 문장 종류.
    /// </summary>
    public const string ReadOnlyRoleDeniedMessage = "조회 전용 Role 에서는 {0} 을(를) 실행할 수 없습니다. 설정 > 기능에서 다른 Role 을 함께 고르면 풀립니다.";

    // ---- 블로커 칸 (TSK-07-03) ----

    /// <summary>Lock 모니터 기능이 꺼져 있습니다. 설정 &gt; 기능에서 켤 수 있습니다.</summary>
    public const string BlockerLockMonitorOff = "Features.blocker.lockMonitorOff";

    /// <summary>설정 열기</summary>
    public const string OpenSettings = "Features.openSettings";

    // ---- 대체 경로 (TSK-07-06) ----

    /// <summary>[읽기 전용] {0}  ({0} = OWNER.NAME)</summary>
    public const string ReadOnlyTabTitle = "Features.readOnlyTitle";

    /// <summary>PL/SQL 편집기 기능이 꺼져 있어 읽기 전용으로 열었습니다</summary>
    public const string PlsqlOffOpenedReadOnly = "Features.plsqlOffReadOnly";

    /// <summary>MyBatis 기능이 꺼져 있어 매퍼를 일반 SQL 탭으로 펼쳐 열었습니다(매퍼 파일에는 저장하지 않습니다)</summary>
    public const string MyBatisOffExpanded = "Features.myBatisOffExpanded";

    // ---- 시작 화면 (TSK-07-06) ----

    /// <summary>작업 공간(.md)이나 SQL 파일을 열면 문장마다 탭이 생깁니다.</summary>
    public const string StartMyBatisOff = "Features.start.myBatisOff";

    /// <summary>지금 {0} 메뉴입니다  ({0} = 기본 Role 이름, D-013)</summary>
    public const string StartDefaultRole = "Features.start.defaultRole";

    /// <summary>바꾸기…  (위 줄의 링크, oranda.tools.roles)</summary>
    public const string StartChangeRole = "Features.start.changeRole";

    /// <summary>위 줄 아래 흐린 설명: 빠진 메뉴를 어디서 켜는지</summary>
    public const string StartDefaultRoleDetail = "Features.start.defaultRoleDetail";

    // ---- 조회 전용 안내 보강 (TSK-07-10) ----

    /// <summary>상태 표시줄 칸: 조회 전용 Role 때문에 쓰기가 막혔다(프로필 읽기 전용은 「읽기 전용」)</summary>
    public const string StatusViewerRole = "Features.status.viewerRole";

    /// <summary>위 칸의 도움말: 무엇 때문이고 풀려면 무엇을 하면 되는지</summary>
    public const string StatusViewerRoleTip = "Features.status.viewerRoleTip";

    /// <summary>Role 로 고르기인데 Role 을 하나도 안 골랐다: 무엇이 켜지고 무엇을 하면 되는지 한 줄</summary>
    public const string NoRoleNotice = "Features.noRoleNotice";

    /// <summary>아무것도 고른 적 없어 기본 Role(SQL 개발자)로 쓰는 중이다(D-013): 무엇을 고를 수 있는지 한 줄</summary>
    public const string DefaultRoleNotice = "Features.defaultRoleNotice";
}
