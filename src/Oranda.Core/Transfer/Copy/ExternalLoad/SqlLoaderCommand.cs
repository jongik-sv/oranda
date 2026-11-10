using System.Globalization;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace Oranda.Core.Transfer.Copy.ExternalLoad;

/// <summary>비밀번호를 SQL*Loader 에 넘기는 길(조정 결정 89). 어느 쪽이든 명령줄·로그·예외 글에는 넣지 않는다.</summary>
public enum LoaderPasswordVia
{
    /// <summary>
    /// parfile·명령줄에 <c>userid</c> 를 두지 않고, 도구가 묻는 <c>Username:</c> 에 <c>사용자/"비밀번호"@접속</c> 전체를 표준 입력으로 준다(파일에 비밀번호 없음).
    /// <c>userid=사용자@host:port/service</c> 처럼 비밀번호만 빼면 sqlldr 가 EZConnect 의 <c>/</c> 를 사용자/비밀번호 구분자로 읽어 ORA-01017 이 난다(win144 #181, 결정 94).
    /// </summary>
    StandardInput,

    /// <summary>parfile 에 <c>userid=사용자/"비밀번호"@접속</c> 을 두고, 그 파일을 현재 사용자만 읽게 만든다. 실행이 끝나면(성공·실패·취소) 지운다.</summary>
    PrivateParfile,
}

/// <summary>
/// SQL*Loader 실행 한 번의 재료. 경로는 전체 경로(작업 폴더 안 — <see cref="LoaderWorkspace"/>). <see cref="WorkingDirectory"/> 는 데이터 폴더여야 한다
/// (LOB 필드에 적은 상대 경로를 도구가 작업 폴더 기준으로 연다). <see cref="Direct"/> 는 제어 파일의 값과 같게 둔다(명령줄 값이 제어 파일을 이긴다).
/// </summary>
public sealed record SqlLoaderRunSpec(
    string ExecutablePath,
    string WorkingDirectory,
    string ParFile,
    string ControlFile,
    string LogFile,
    string BadFile,
    ExternalLoadLogin Login,
    int MaxErrors,
    bool Direct,
    LoaderPasswordVia PasswordVia = LoaderPasswordVia.StandardInput)
{
    /// <summary>버린 레코드 파일. null = 제어 파일의 값.</summary>
    public string? DiscardFile { get; init; }

    /// <summary>실행 시간 제한. null = 없음(취소로만 끝낸다).</summary>
    public TimeSpan? Timeout { get; init; }
}

/// <summary>
/// 만든 SQL*Loader 실행. <see cref="WriteParFile"/> 로 parfile 을 쓰고(비밀 parfile 이면 현재 사용자만 읽는 파일), <see cref="Start"/> 를
/// <see cref="IExternalProcessRunner"/> 로 실행한 뒤 <c>finally</c> 에서 <see cref="DeleteParFile"/> 를 부른다(작업 폴더 정리도 지우지만 먼저 지운다).
/// 비밀번호는 <see cref="Start"/> 의 표준 입력(<see cref="LoaderPasswordVia.StandardInput"/>)이나 parfile 파일 안에만 있다.
/// <see cref="LoggableParFileText"/>·<see cref="Describe"/>·<see cref="ToString"/> 에는 없다.
/// </summary>
public sealed class SqlLoaderInvocation
{
    private readonly string _parFileText;

    private readonly string? _loggableStandardInput;

    internal SqlLoaderInvocation(ExternalProcessStart start, string parFile, string parFileText, string loggableParFileText, LoaderPasswordVia passwordVia,
                                 string? loggableStandardInput = null)
    {
        Start = start;
        ParFile = parFile;
        _parFileText = parFileText;
        LoggableParFileText = loggableParFileText;
        PasswordVia = passwordVia;
        _loggableStandardInput = loggableStandardInput;
    }

    /// <summary>실행 정보(인자 = <c>parfile=경로</c> 하나). 표준 입력 방식이면 <see cref="ExternalProcessStart.StandardInput"/> 에 비밀번호 줄이 있다 — 로그에 쓰지 않는다(<see cref="ExternalProcessStart"/> 의 ToString 은 가린다).</summary>
    public ExternalProcessStart Start { get; }

    /// <summary>parfile 전체 경로.</summary>
    public string ParFile { get; }

    public LoaderPasswordVia PasswordVia { get; }

    /// <summary>로그에 써도 되는 parfile 글(비밀 parfile 이면 비밀번호 자리를 <c>********</c> 로).</summary>
    public string LoggableParFileText { get; }

    /// <summary>로그용 설명: 실행 파일·인자 줄과 parfile 글(비밀번호 없음).</summary>
    public string Describe() =>
        Start.FileName + " " + string.Join(' ', Start.Arguments) + SqlLoaderControlFile.NewLine + LoggableParFileText
        + (_loggableStandardInput is null ? string.Empty : "(표준 입력) " + _loggableStandardInput);

    public override string ToString() => Describe();

    /// <summary>
    /// parfile 을 쓴다(UTF-8, BOM 없음). 비밀 parfile 이면 새 파일을 현재 사용자만 읽고 쓰게 만든다(Windows = 상속 없는 ACL, 그 밖 = 0600).
    /// 이미 있으면 먼저 지운다. 디스크·권한 오류는 예외(글에 비밀번호 없음).
    /// </summary>
    public void WriteParFile()
    {
        if (File.Exists(ParFile))
        {
            File.Delete(ParFile);
        }

        if (PasswordVia == LoaderPasswordVia.PrivateParfile)
        {
            LoaderPrivateFile.Write(ParFile, _parFileText);
        }
        else
        {
            File.WriteAllText(ParFile, _parFileText, SqlLoaderControlFile.FileEncoding);
        }
    }

    /// <summary>parfile 을 지운다. 예외를 내지 않는다(실패하면 false — 작업 폴더 정리가 한 번 더 지운다). 없으면 true.</summary>
    public bool DeleteParFile()
    {
        try
        {
            File.Delete(ParFile);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

/// <summary>
/// SQL*Loader 명령 만들기(XL1). 파일을 쓰지 않고 실행 정보와 parfile 글을 돌려준다(<see cref="SqlLoaderInvocation.WriteParFile"/> 가 쓴다).
/// parfile: <c>userid</c>·<c>control</c>·<c>log</c>·<c>bad</c>(·<c>discard</c>)·<c>direct</c>·<c>errors</c>. 인자는 <c>parfile=경로</c> 하나.
/// 환경: <c>NLS_LANG=AMERICAN_AMERICA.AL32UTF8</c>(로그가 영어 — <see cref="SqlLoaderLog"/> 가 읽는다, 파일 문자 집합 UTF-8), <c>NLS_NUMERIC_CHARACTERS=".,"</c>,
/// 접속 정보에 있으면 <c>TNS_ADMIN</c>.
/// </summary>
public static class SqlLoaderCommand
{
    public const string ParFileName = "load.par";

    /// <summary>로그용 글에서 비밀번호 자리.</summary>
    public const string PasswordMask = "********";

    /// <summary>도구 실행 공통 환경(도구 확인도 쓴다).</summary>
    public static IReadOnlyDictionary<string, string?> BaseEnvironment { get; } = new Dictionary<string, string?>(StringComparer.Ordinal)
    {
        ["NLS_LANG"] = "AMERICAN_AMERICA.AL32UTF8",
        ["NLS_NUMERIC_CHARACTERS"] = ".,",
    };

    /// <summary>실행 한 번을 만든다. 사용자 이름·접속 주소·비밀번호·경로가 parfile 문법에 맞지 않으면 <see cref="ArgumentException"/>(글에 비밀번호 없음).</summary>
    public static SqlLoaderInvocation Build(SqlLoaderRunSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(spec.Login);
        ArgumentException.ThrowIfNullOrWhiteSpace(spec.ExecutablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(spec.WorkingDirectory);
        if (spec.MaxErrors < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(spec), "허용 오류 수는 0 이상이어야 합니다.");
        }

        var login = spec.Login;
        var password = login.Password ?? string.Empty;
        if (password.Contains('\n', StringComparison.Ordinal) || password.Contains('\r', StringComparison.Ordinal) || password.Contains('"', StringComparison.Ordinal))
        {
            // 값은 넣지 않는다. 비밀번호는 어느 길이든 userid 안에서 큰따옴표로 감싼다.
            throw new ArgumentException("비밀번호에 외부 도구로 넘길 수 없는 문자(줄바꿈·큰따옴표)가 있습니다.", nameof(spec));
        }

        var isPrivate = spec.PasswordVia == LoaderPasswordVia.PrivateParfile;
        var realText = ParFileText(spec, isPrivate ? UserId(login, password) : null);
        var loggable = isPrivate ? ParFileText(spec, UserId(login, PasswordMask)) : realText;

        var environment = new Dictionary<string, string?>(BaseEnvironment, StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(login.TnsAdmin))
        {
            environment["TNS_ADMIN"] = login.TnsAdmin.Trim();
        }

        var start = new ExternalProcessStart(
            spec.ExecutablePath,
            ["parfile=" + spec.ParFile],
            spec.WorkingDirectory,
            environment,
            isPrivate ? null : UserId(login, password) + "\n",
            spec.Timeout);
        return new SqlLoaderInvocation(start, spec.ParFile, realText, loggable, spec.PasswordVia, isPrivate ? null : UserId(login, PasswordMask));
    }

    /// <summary>
    /// <c>userid</c> 값: <c>사용자[/"비밀번호"][@접속]</c>. 접속 주소에 공백·괄호·<c>=</c>·<c>,</c> 가 있으면(기술자 등) 큰따옴표로 감싸고 줄바꿈은 공백으로 바꾼다.
    /// <paramref name="password"/> 가 null 이면 비밀번호를 두지 않는다(도구가 묻는다).
    /// </summary>
    internal static string UserId(ExternalLoadLogin login, string? password)
    {
        var user = login.User?.Trim() ?? string.Empty;
        if (user.Length == 0 || user.IndexOfAny(['/', '@', '"', '\'', ' ', '\t', '\r', '\n', '=', ',']) >= 0)
        {
            throw new ArgumentException($"외부 도구에 넘길 수 없는 사용자 이름입니다: {user}", nameof(login));
        }

        var b = new StringBuilder(user);
        if (password is not null)
        {
            b.Append("/\"").Append(password).Append('"');
        }

        var connect = string.Join(' ', (login.Connect ?? string.Empty).Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
        if (connect.Length > 0)
        {
            if (connect.Contains('"', StringComparison.Ordinal))
            {
                throw new ArgumentException("접속 주소에 큰따옴표가 있어 외부 도구에 넘길 수 없습니다.", nameof(login));
            }

            b.Append('@').Append(connect.IndexOfAny([' ', '(', ')', '=', ',']) >= 0 ? "\"" + connect + "\"" : connect);
        }

        return b.ToString();
    }

    /// <summary>parfile 값의 경로. 공백·괄호·<c>=</c>·<c>,</c>·<c>'</c> 가 있으면 큰따옴표로 감싼다. <c>"</c>·줄바꿈이 있으면 <see cref="ArgumentException"/>.</summary>
    internal static string ParPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (path.IndexOfAny(['"', '\r', '\n']) >= 0)
        {
            throw new ArgumentException($"경로에 큰따옴표나 줄바꿈이 있어 parfile 에 적을 수 없습니다: {path}", nameof(path));
        }

        return path.IndexOfAny([' ', '(', ')', '=', ',', '\'']) >= 0 ? "\"" + path + "\"" : path;
    }

    /// <summary>parfile 글. <paramref name="userId"/> 가 null 이면 userid 줄을 두지 않는다(도구가 <c>Username:</c> 으로 묻고 표준 입력이 답한다).</summary>
    private static string ParFileText(SqlLoaderRunSpec spec, string? userId)
    {
        var nl = SqlLoaderControlFile.NewLine;
        var b = new StringBuilder();
        if (userId is not null)
        {
            b.Append("userid=").Append(userId).Append(nl);
        }

        b.Append("control=").Append(ParPath(spec.ControlFile)).Append(nl);
        b.Append("log=").Append(ParPath(spec.LogFile)).Append(nl);
        b.Append("bad=").Append(ParPath(spec.BadFile)).Append(nl);
        if (!string.IsNullOrWhiteSpace(spec.DiscardFile))
        {
            b.Append("discard=").Append(ParPath(spec.DiscardFile)).Append(nl);
        }

        b.Append("direct=").Append(spec.Direct ? "true" : "false").Append(nl);
        b.Append(CultureInfo.InvariantCulture, $"errors={spec.MaxErrors}").Append(nl);
        return b.ToString();
    }
}

/// <summary>현재 사용자만 읽고 쓰는 새 파일(비밀 parfile). Windows = 상속을 끊고 현재 사용자 SID 만 허용하는 ACL, 그 밖 = 0600.</summary>
internal static class LoaderPrivateFile
{
    public static void Write(string path, string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(text);
        var bytes = SqlLoaderControlFile.FileEncoding.GetBytes(text);
        try
        {
            if (OperatingSystem.IsWindows())
            {
                WriteWindows(path, bytes);
            }
            else
            {
                WriteUnix(path, bytes);
            }
        }
        finally
        {
            Array.Clear(bytes);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void WriteWindows(string path, byte[] bytes)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new InvalidOperationException("현재 사용자 SID 를 읽지 못해 비밀 parfile 을 만들 수 없습니다.");
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
        using var stream = new FileInfo(path).Create(
            FileMode.CreateNew,
            FileSystemRights.Read | FileSystemRights.Write | FileSystemRights.Synchronize,
            FileShare.None,
            4096,
            FileOptions.None,
            security);
        stream.Write(bytes);
    }

    [UnsupportedOSPlatform("windows")]
    private static void WriteUnix(string path, byte[] bytes)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        };
        using var stream = new FileStream(path, options);
        stream.Write(bytes);
    }
}
