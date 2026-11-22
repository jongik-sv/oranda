using Oranda.Core.Ai.Profiles;

namespace Oranda.Core.Ai.Setup;

/// <summary>
/// 갈래 A(회사 Claude 계정)에서 claude 를 찾고·설치하고·로그인하고·시험하는 서비스. 구현은 Platform(<c>Oranda.Platform.Ai.Setup</c>)이 한다.
/// 실패는 예외가 아니라 결과로 돌려준다(취소만 <see cref="OperationCanceledException"/>). 결과 글에 열쇠·주소·계정 비밀을 넣지 않는다.
/// 화면 스레드에서 불러도 되게 모든 일은 비동기이고 오래 걸리는 일은 스레드 밖에서 한다.
/// </summary>
public interface IClaudeSetupService
{
    /// <summary>이 PC 의 claude 를 찾는다(PATH·알려진 설치 폴더). 없으면 null. 실행해 판을 읽지 못하면 <see cref="ClaudeInstallation.Version"/> 만 null.</summary>
    Task<ClaudeInstallation?> FindAsync(CancellationToken ct);

    /// <summary>
    /// 공식 설치 명령을 숨은 창에서 실행한다. 출력은 한 줄씩 <paramref name="progress"/> 로 알린다. 끝나면 다시 찾아 <see cref="ClaudeInstallResult.Installation"/> 을 채운다.
    /// 실패해도 <see cref="ClaudeInstallResult.ManualCommand"/> 는 늘 채운다.
    /// </summary>
    Task<ClaudeInstallResult> InstallAsync(IProgress<string>? progress, CancellationToken ct);

    /// <summary>직접 설치할 때 붙여 넣을 한 줄. 기본은 <see cref="ClaudeInstallCommands.ManualFor"/>.</summary>
    string ManualInstallCommand(AiHostPlatform platform) => ClaudeInstallCommands.ManualFor(platform);

    /// <summary>
    /// 로그인을 뒤에서 시작한다(브라우저가 열림). 시작하며 브라우저 주소를 알면 <see cref="ClaudeLoginStart.BrowserUrl"/> 에 담고,
    /// 나중에 나온 주소는 <paramref name="browserUrl"/> 로 알린다(화면 스레드로 옮기는 것은 부른 쪽 <see cref="Progress{T}"/> 몫).
    /// <see cref="ClaudeLoginStart.Handle"/> 을 닫으면 로그인 프로세스를 끊는다(단계를 떠나거나 창을 닫을 때).
    /// </summary>
    Task<ClaudeLoginStart> StartLoginAsync(IProgress<string>? browserUrl, CancellationToken ct);

    /// <summary>로그인 상태. 몇 초마다 불러도 되게 가볍게 한다. 상태 확인을 못 하는 판이면 <see cref="ClaudeLoginStatus.StatusSupported"/> 가 거짓.</summary>
    Task<ClaudeLoginStatus> GetLoginStatusAsync(CancellationToken ct);

    /// <summary>짧은 질문 한 번으로 연결을 시험한다. DB 를 건드리지 않는다.</summary>
    Task<AiSetupTestResult> TestConnectionAsync(CancellationToken ct);
}

/// <summary>찾은 claude.</summary>
/// <param name="ExecutablePath">실행 파일 전체 경로.</param>
/// <param name="Version">판(<c>2.1.330</c> 처럼 숫자만). 읽지 못하면 null.</param>
public sealed record ClaudeInstallation(string ExecutablePath, string? Version);

/// <summary>설치 결과.</summary>
/// <param name="Success">설치 뒤 claude 를 찾았다.</param>
/// <param name="Installation">설치 뒤 찾은 claude. 실패면 null.</param>
/// <param name="ErrorKind">실패 종류. 성공이면 null.</param>
/// <param name="ManualCommand">직접 설치 한 줄(늘 채움).</param>
/// <param name="Detail">실패 까닭 짧은 원문(진단용, 비밀 없음). 없으면 null.</param>
public sealed record ClaudeInstallResult(bool Success, ClaudeInstallation? Installation, AiSetupErrorKind? ErrorKind, string ManualCommand, string? Detail = null);

/// <summary>로그인 시작 결과. 시작하지 못하면 <see cref="Started"/> 가 거짓이고 <see cref="Handle"/> 은 null.</summary>
/// <param name="Started">로그인 프로세스를 띄웠다.</param>
/// <param name="BrowserUrl">브라우저가 안 열릴 때 복사할 주소. 모르면 null.</param>
/// <param name="Handle">끊기 손잡이. 닫으면 로그인 프로세스를 끝낸다(두 번 닫아도 된다).</param>
/// <param name="ErrorKind">시작하지 못한 까닭.</param>
/// <param name="Exited">로그인 프로세스가 끝나면 종료 코드로 끝나는 일(0 = 정상). 끊어도 끝난다. 모르면 null. 끝남 ≠ 로그인됨이라 화면은 상태 확인으로 다시 본다.</param>
public sealed record ClaudeLoginStart(bool Started, string? BrowserUrl, IAsyncDisposable? Handle, AiSetupErrorKind? ErrorKind = null, Task<int>? Exited = null);

/// <summary>로그인 상태.</summary>
/// <param name="LoggedIn">로그인됨.</param>
/// <param name="AccountLabel">화면에 보일 계정 글(메일 주소 등). 모르면 null. 진단 글에는 넣지 않는다.</param>
/// <param name="StatusSupported">이 claude 판이 상태 확인을 지원한다. 거짓이면 화면은 「로그인 끝났어요」 단추에 기댄다.</param>
/// <param name="ErrorKind">확인 자체가 실패한 까닭(claude 없음 등). 확인했으면 null.</param>
public sealed record ClaudeLoginStatus(bool LoggedIn, string? AccountLabel, bool StatusSupported, AiSetupErrorKind? ErrorKind = null);

/// <summary>연결 시험 결과(갈래 A·B 공용).</summary>
/// <param name="Success">답을 받았다.</param>
/// <param name="Elapsed">걸린 시간(실패여도 채운다).</param>
/// <param name="ErrorKind">실패 종류. 성공이면 null.</param>
/// <param name="Detail">짧은 원문(진단용, 비밀 없음). 화면 글은 <see cref="AiSetupErrorText.For"/> 로 고른다.</param>
public sealed record AiSetupTestResult(bool Success, TimeSpan Elapsed, AiSetupErrorKind? ErrorKind = null, string? Detail = null);

/// <summary>공식 설치 명령.</summary>
public static class ClaudeInstallCommands
{
    /// <summary>Windows PowerShell 한 줄.</summary>
    public const string Windows = "irm https://claude.ai/install.ps1 | iex";

    /// <summary>macOS·Linux 셸 한 줄.</summary>
    public const string Unix = "curl -fsSL https://claude.ai/install.sh | bash";

    /// <summary>운영체제별 직접 설치 한 줄. Windows 면 PowerShell, 그 밖(macOS·Linux)은 셸 명령.</summary>
    public static string ManualFor(AiHostPlatform platform) => platform == AiHostPlatform.Windows ? Windows : Unix;
}
