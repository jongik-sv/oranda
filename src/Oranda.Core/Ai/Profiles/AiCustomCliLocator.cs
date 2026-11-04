namespace Oranda.Core.Ai.Profiles;

/// <summary>
/// 기타 CLI 의 실행 파일이 이 컴퓨터에 있는지 찾는다(설정 화면의 설치 표시용). 파일 존재만 보고 프로그램을 실행하지 않는다.
/// 실행할 때 쓰는 탐색과 같은 규칙이다: 이름이면 PATH 와 흔한 설치 위치(Homebrew·npm 전역 등)를, 절대 경로이면 그 파일을 본다.
/// </summary>
public interface IAiCustomCliLocator
{
    /// <summary>
    /// 찾은 실행 파일 경로를 돌려준다. 못 찾거나 쓸 수 없는 파일(Windows 의 <c>.cmd</c>·<c>.bat</c>)이거나 상대 경로이면 null.
    /// </summary>
    /// <param name="executable">실행 파일 이름 또는 절대 경로(명령 문자열의 첫 낱말, <c>AiCustomCliCommand.Executable</c>).</param>
    string? Locate(string executable);
}
