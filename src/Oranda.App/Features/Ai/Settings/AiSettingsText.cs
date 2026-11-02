using Oranda.App.Shell;
using Oranda.Core.Ai.Profiles;
using Oranda.Core.Events;
using Oranda.Core.Security;
using Oranda.App.Shell.Settings;

namespace Oranda.App.Features.Ai.Settings;

/// <summary>
/// 뷰모델이 만드는 글(요약·안내·오류)의 한/영 선택. 정적 <c>LiteralTranslator.IsEnglish</c> 대신 설정 창이 넘긴 <see cref="IShellText"/> 를 본다
/// (언어를 바꾸는 시험과 병렬로 돌아도 흔들리지 않게). 글 공급자가 없으면 한국어다.
/// </summary>
internal sealed class AiSettingsText(IShellText? text)
{
    public bool IsEnglish => text?.Language == "en";

    public string T(string ko, string en) => IsEnglish ? en : ko;
}

/// <summary>
/// 프로필 관리에 필요한 서비스 묶음. 설정 창이 앱 서비스에서 모아 페이지에 넘긴다.
/// 키 저장소·서버 조회가 없으면(null) 해당 동작은 비활성이고 모델명은 직접 입력한다.
/// </summary>
/// <param name="Settings">설정 읽기·쓰기. 관리 창은 이것으로 프로필 목록을 즉시 기록한다.</param>
/// <param name="Credentials">API 키 저장소. null 이면 키가 필요한 프로필은 저장할 수 없다.</param>
/// <param name="Probe">모델 목록·연결 테스트(구현은 Platform). null 이면 두 단추가 비활성이다.</param>
/// <param name="Bus">저장 뒤 <c>SettingsChanged("ai")</c> 를 낼 버스. 설정 저장소는 <c>ai</c> 섹션 변경을 알리지 않는다.</param>
/// <param name="Text">한/영 글.</param>
public sealed record AiProfileServices(
    SettingsService Settings,
    ICredentialStore? Credentials = null,
    IAiProfileProbe? Probe = null,
    IEventBus? Bus = null,
    IShellText? Text = null);
