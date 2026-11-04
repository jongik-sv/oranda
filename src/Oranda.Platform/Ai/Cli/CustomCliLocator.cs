using Oranda.Core.Ai.Contracts;
using Oranda.Core.Ai.Profiles;
using Oranda.Core.Composition;

namespace Oranda.Platform.Ai.Cli;

/// <summary>
/// 기타 CLI 실행 파일 찾기(<see cref="IAiCustomCliLocator"/>). 설정 화면의 설치 표시와 <see cref="CustomCliDriver"/> 가 같은 탐색을 쓰도록 한 곳에 둔다:
/// 이름은 <see cref="SystemCliExecutable.Locate"/>(PATH + 흔한 설치 위치), 절대 경로는 파일 존재를 본다. 프로그램을 실행하지 않는다.
/// </summary>
[OrandaService(typeof(IAiCustomCliLocator))]
internal sealed class CustomCliLocator : IAiCustomCliLocator
{
    private readonly Func<string?> _pathVariable;
    private readonly Func<string, bool> _fileExists;
    private readonly string _home;

    public CustomCliLocator()
        : this(() => Environment.GetEnvironmentVariable("PATH"), File.Exists, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))
    {
    }

    /// <summary>시험용: PATH·파일 존재·홈 폴더를 바꿔 넣는다.</summary>
    internal CustomCliLocator(Func<string?> pathVariable, Func<string, bool> fileExists, string home)
    {
        _pathVariable = pathVariable ?? throw new ArgumentNullException(nameof(pathVariable));
        _fileExists = fileExists ?? throw new ArgumentNullException(nameof(fileExists));
        _home = home;
    }

    public string? Locate(string executable) => Resolve(executable, "기타 CLI").Path;

    /// <summary>
    /// 찾은 경로, 못 찾았으면 이유(<c>cli_not_found</c>·<c>cli_unsupported</c>).
    /// <paramref name="pathOverride"/> 는 명령에 <c>PATH=</c> 로 적은 값이며, 있으면 부모의 PATH 대신 그 값으로 찾는다(실행 때 자식이 보는 PATH 와 맞춘다).
    /// </summary>
    internal (string? Path, AiError? Error) Resolve(string executable, string displayName, string? pathOverride = null)
    {
        ArgumentNullException.ThrowIfNull(executable);
        if (string.IsNullOrWhiteSpace(executable))
        {
            return (null, new AiError($"{displayName} 의 실행 파일 이름이 비어 있습니다.", "cli_not_found"));
        }

        // 배치 파일은 찾기 전에 이름으로 거부한다: Windows 의 PATH 탐색은 이름.exe 를 붙여 찾으므로 x.cmd 는 cli_not_found 로 새고,
        // 끝의 점·공백(a.bat.)은 파일 시스템이 떼어 같은 파일로 열기 때문에 확장자 판정에서도 뗀다.
        if (IsBatchName(executable))
        {
            return (null, new AiError($"{displayName} 의 배치 파일은 인자가 깨져 지원하지 않습니다.", "cli_unsupported"));
        }

        if (System.IO.Path.IsPathRooted(executable) || executable.StartsWith('/'))
        {
            if (!_fileExists(executable))
            {
                return (null, new AiError($"{displayName} 의 실행 파일({executable})이 없습니다. 경로를 확인하세요.", "cli_not_found"));
            }

            return (executable, null);
        }

        // 폴더 구분자가 든 상대 경로는 명령 분해 단계에서 이미 거부한다. 여기서도 PATH 탐색으로 흘리지 않는다.
        if (executable.Contains('/') || executable.Contains('\\'))
        {
            return (null, new AiError($"{displayName} 의 실행 파일은 이름만 쓰거나 절대 경로여야 합니다.", "cli_not_found"));
        }

        // Windows 는 이름.exe 를 찾으므로 확장자를 붙여 쓴 이름은 떼어 준다.
        var bare = OperatingSystem.IsWindows() && executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? executable[..^4] : executable;
        return SystemCliExecutable.Locate(bare, displayName, AbsoluteEntries(pathOverride ?? _pathVariable()), _fileExists, _home);
    }

    private static bool IsBatchName(string executable) =>
        ClaudeExecutable.IsBatchFile(OperatingSystem.IsWindows() ? executable.TrimEnd('.', ' ') : executable);

    // PATH 의 상대 항목(".", "bin" 등)은 건너뛴다. 상대 경로로 풀린 실행 파일은 자식의 작업 폴더 기준으로 해석되어
    // 작업 폴더에 같은 이름 파일이 있으면 그것이 실행될 수 있다(Windows 의 CreateProcess 는 PATH 보다 작업 폴더를 먼저 본다). 실행에는 항상 전체 경로를 넘긴다.
    private static string? AbsoluteEntries(string? pathVariable) =>
        pathVariable is null
            ? null
            : string.Join(System.IO.Path.PathSeparator, pathVariable.Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(entry => System.IO.Path.IsPathRooted(entry) && !entry.StartsWith('~')));
}
