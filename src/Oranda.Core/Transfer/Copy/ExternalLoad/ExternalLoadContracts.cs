namespace Oranda.Core.Transfer.Copy.ExternalLoad;

/// <summary>
/// 외부 적재 도구(조정 결정 88, XL1·XL2). 도구는 앱에 들어 있지 않다(라이선스): 사용자가 Oracle Instant Client(Basic + Tools)·Tibero 클라이언트를 깔고
/// 설정에 경로를 적거나(<see cref="ExternalToolPaths"/>) 비워 두면 찾는다(<see cref="IExternalToolProbe.Locate"/>).
/// </summary>
public enum ExternalLoaderKind
{
    /// <summary>Oracle SQL*Loader(<c>sqlldr</c>). Oracle 대상.</summary>
    SqlLoader,

    /// <summary>Tibero tbLoader(<c>tbloader</c>). Tibero 대상.</summary>
    TbLoader,
}

/// <summary>설정의 도구 경로(<c>drivers.sqlLoaderPath</c>·<c>drivers.tbLoaderPath</c>). null·빈 값 = 찾기.</summary>
public sealed record ExternalToolPaths(string? SqlLoader, string? TbLoader)
{
    public static ExternalToolPaths None { get; } = new(null, null);

    public string? For(ExternalLoaderKind kind) => kind == ExternalLoaderKind.SqlLoader ? SqlLoader : TbLoader;
}

/// <summary>
/// 도구 위치 찾기 결과. <see cref="Path"/> 가 null 이면 못 찾았다. <see cref="Searched"/> = 살펴본 곳(설정 경로·PATH·ORACLE_HOME/bin·Instant Client 폴더·TB_HOME/client/bin 순, 안내에 보인다).
/// <see cref="FromSettings"/> = 설정에 적은 경로를 썼다(그 경로에 파일이 없으면 찾지 않고 null — 적은 값이 틀렸다고 알린다).
/// </summary>
public sealed record ExternalToolLocation(ExternalLoaderKind Kind, string? Path, bool FromSettings, IReadOnlyList<string> Searched);

/// <summary>
/// 「도구 확인」 결과. <see cref="Version"/> = 버전 줄에서 읽은 주 버전(예 SQL*Loader <c>Release 19.0.0.0.0</c> → 19), 못 읽으면 null.
/// <see cref="Problem"/> 이 null 이면 쓸 수 있다. 아니면 사용자에게 보이는 이유(못 찾음·실행 실패·버전 줄 없음).
/// </summary>
public sealed record ExternalToolInfo(ExternalLoaderKind Kind, string? Path, int? Version, string? VersionLine, string? Problem)
{
    public bool Usable => Problem is null && Path is not null;
}

/// <summary>도구 찾기·확인. 앱 설정 화면(「도구 확인」)·사전 점검·실행기가 쓴다.</summary>
public interface IExternalToolProbe
{
    /// <summary>설정 경로가 있으면 그것(파일이 없으면 null), 없으면 정해진 곳을 차례로 찾는다. 프로세스를 띄우지 않는다.</summary>
    ExternalToolLocation Locate(ExternalLoaderKind kind, string? configuredPath);

    /// <summary>찾은 도구를 인자 없이 실행해 버전 줄을 읽는다(짧은 시간 제한, 표준 입력은 바로 닫음). 예외를 내지 않고 <see cref="ExternalToolInfo.Problem"/> 으로 알린다(취소는 예외).</summary>
    Task<ExternalToolInfo> CheckAsync(ExternalLoaderKind kind, string? configuredPath, CancellationToken ct);
}

/// <summary>
/// 외부 프로세스 하나 실행(Platform 이 구현, Core 는 프로세스를 직접 띄우지 않는다). <see cref="StandardInput"/> 은 쓰고 바로 닫는다(비밀번호를 명령줄에 두지 않으려고 쓴다).
/// <see cref="Environment"/> 의 값이 null 이면 그 변수를 지운다.
/// </summary>
public sealed record ExternalProcessStart(
    string FileName,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string?> Environment,
    string? StandardInput,
    TimeSpan? Timeout = null)
{
    // 기본 출력(ToString·로그·디버거)에 표준 입력(비밀번호)과 환경 변수 값이 나오지 않게 한다.
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append("FileName = ").Append(FileName)
            .Append(", Arguments = [").AppendJoin(' ', Arguments).Append(']')
            .Append(", WorkingDirectory = ").Append(WorkingDirectory)
            .Append(", Environment = [").AppendJoin(", ", Environment.Keys).Append(']')
            .Append(", StandardInput = ").Append(StandardInput is null ? "null" : "(가림)")
            .Append(", Timeout = ").Append(Timeout?.ToString("c", System.Globalization.CultureInfo.InvariantCulture) ?? "null");
        return true;
    }
}

/// <summary>끝난 프로세스. 출력은 끝부분만 남긴다(각 64KB). <see cref="TimedOut"/> = 시간 제한으로 끝냈다(프로세스 트리를 끝냈다).</summary>
public sealed record ExternalProcessResult(int ExitCode, string StandardOutput, string StandardError, bool TimedOut = false);

/// <summary>
/// 외부 프로세스 실행기. 취소되면 프로세스 트리 전체를 끝낸 뒤(<c>Kill(entireProcessTree: true)</c>, 끝날 때까지 기다림) <see cref="OperationCanceledException"/>.
/// 실행 파일을 못 띄우면 <see cref="ExternalProcessException"/>.
/// </summary>
public interface IExternalProcessRunner
{
    Task<ExternalProcessResult> RunAsync(ExternalProcessStart start, CancellationToken ct);
}

/// <summary>실행 파일을 띄우지 못했다(없음·권한·형식). 메시지는 사용자에게 보인다(비밀 없음).</summary>
public sealed class ExternalProcessException : Exception
{
    public ExternalProcessException(string message)
        : base(message)
    {
    }

    public ExternalProcessException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// 외부 도구가 대상에 접속할 정보. <see cref="Connect"/> = 접속 프로필의 접속 주소 그대로(EZConnect·TNS 별칭·기술자, Tibero 는 <c>host:port/db</c>).
/// 비밀번호는 표준 입력이나 현재 사용자만 읽는 임시 parfile 로만 넘기고(조정 결정 89) 로그·명령줄·예외 글에 넣지 않는다. <see cref="ToString"/> 은 비밀번호를 보이지 않는다.
/// </summary>
public sealed class ExternalLoadLogin(string user, string connect, string password, string? tnsAdmin)
{
    public string User { get; } = user;

    public string Connect { get; } = connect;

    public string Password { get; } = password;

    public string? TnsAdmin { get; } = tnsAdmin;

    public override string ToString() => $"{User}@{Connect}";
}

/// <summary>
/// 실행 한 번의 외부 도구 준비물(<see cref="CopySessions.ExternalLoad"/>). 앱이 실행 때 넣는다. null = 외부 도구를 쓸 수 없음(사전 점검이 기본 방식으로 바꾸고 알린다).
/// <see cref="TargetLogin"/> = 대상 접속 정보(실행 때 받은 비밀번호를 다시 쓴다, 새로 묻지 않음 — 못 주면 null). <see cref="WorkRoot"/> = 임시 폴더의 부모(null = 사용자 임시 폴더).
/// </summary>
public sealed record CopyExternalLoad(IExternalToolProbe Probe, IExternalProcessRunner Runner, ExternalToolPaths Paths,
                                      Func<CancellationToken, Task<ExternalLoadLogin?>> TargetLogin)
{
    public string? WorkRoot { get; init; }

    /// <summary>비밀번호를 넘기는 길(조정 결정 89). 기본 = 표준 입력, 도구가 콘솔에서만 읽으면 현재 사용자만 읽는 임시 parfile(SQL*Loader 만).</summary>
    public LoaderPasswordVia PasswordVia { get; init; }
}
