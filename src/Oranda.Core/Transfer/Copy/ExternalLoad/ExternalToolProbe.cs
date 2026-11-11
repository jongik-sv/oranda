using System.Globalization;
using System.Text.RegularExpressions;
using Oranda.Core.Composition;

namespace Oranda.Core.Transfer.Copy.ExternalLoad;

/// <summary>도구 찾기가 보는 환경(환경 변수·파일·폴더). 시험은 가짜로 바꾼다. 실패(권한·없음)는 예외 대신 false·빈 목록.</summary>
public interface IExternalToolEnvironment
{
    /// <summary>Windows 이름 규칙(<c>.exe</c>·<c>oci.dll</c>·<c>C:\</c> 아래 찾기)을 쓰는지.</summary>
    bool IsWindows { get; }

    string? GetVariable(string name);

    bool FileExists(string path);

    bool DirectoryExists(string path);

    /// <summary><paramref name="path"/> 바로 아래 폴더 중 <paramref name="searchPattern"/> 에 맞는 것(전체 경로). 못 읽으면 빈 목록.</summary>
    IReadOnlyList<string> GetDirectories(string path, string searchPattern);
}

/// <summary>실제 OS 환경.</summary>
[OrandaService(typeof(IExternalToolEnvironment))]
public sealed class SystemExternalToolEnvironment : IExternalToolEnvironment
{
    public bool IsWindows => OperatingSystem.IsWindows();

    public string? GetVariable(string name) => Environment.GetEnvironmentVariable(name);

    public bool FileExists(string path) => File.Exists(path);

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public IReadOnlyList<string> GetDirectories(string path, string searchPattern)
    {
        try
        {
            return Directory.Exists(path) ? Directory.GetDirectories(path, searchPattern) : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return [];
        }
    }
}

/// <summary>
/// 외부 적재 도구 찾기·확인(XL1, <see cref="IExternalToolProbe"/>). 설정 경로가 비면 차례로 찾는다:
/// PATH → (SQL*Loader) <c>%ORACLE_HOME%\bin</c>·<c>%ORACLE_HOME%</c> → Instant Client 폴더(<c>C:\oracle</c>·<c>C:\</c>·<c>%ProgramFiles%</c>·<c>%LOCALAPPDATA%\Programs</c> 아래
/// <c>instantclient*</c>, PATH 의 <c>oci.dll</c> 폴더 옆 <c>instantclient*</c> — 새 버전 먼저) / (tbLoader) <c>%TB_HOME%\client\bin</c>·<c>%TB_HOME%\bin</c>.
/// 확인은 도구를 인자 없이 실행해(표준 입력은 바로 닫음, 10초 제한) 첫 버전 줄을 읽는다.
/// </summary>
[OrandaService(typeof(IExternalToolProbe))]
public sealed partial class ExternalToolProbe(IExternalProcessRunner runner, IExternalToolEnvironment environment) : IExternalToolProbe
{
    /// <summary>확인 실행 시간 제한.</summary>
    public static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(10);

    /// <summary>못 찾음 안내에 보이는 살펴본 곳 최대 수(넘으면 「외 n곳」).</summary>
    private const int MaxSearchedInMessage = 12;

    private readonly IExternalProcessRunner _runner = runner ?? throw new ArgumentNullException(nameof(runner));
    private readonly IExternalToolEnvironment _environment = environment ?? throw new ArgumentNullException(nameof(environment));

    /// <summary>안내에 쓰는 도구 이름.</summary>
    public static string DisplayName(ExternalLoaderKind kind) =>
        kind == ExternalLoaderKind.SqlLoader ? "SQL*Loader(sqlldr)" : "tbLoader(tbloader)";

    public ExternalToolLocation Locate(ExternalLoaderKind kind, string? configuredPath)
    {
        var exe = ExecutableName(kind);
        var searched = new List<string>();
        var configured = configuredPath?.Trim().Trim('"').Trim();
        if (!string.IsNullOrEmpty(configured))
        {
            searched.Add(configured);
            return new ExternalToolLocation(kind, FromConfigured(configured, exe), FromSettings: true, searched);
        }

        var comparer = _environment.IsWindows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var seen = new HashSet<string>(comparer);
        foreach (var directory in Candidates(kind))
        {
            if (string.IsNullOrWhiteSpace(directory) || !seen.Add(directory))
            {
                continue;
            }

            searched.Add(directory);
            var path = Combine(directory, exe);
            if (_environment.FileExists(path))
            {
                return new ExternalToolLocation(kind, path, FromSettings: false, searched);
            }
        }

        return new ExternalToolLocation(kind, null, FromSettings: false, searched);
    }

    public async Task<ExternalToolInfo> CheckAsync(ExternalLoaderKind kind, string? configuredPath, CancellationToken ct)
    {
        var location = Locate(kind, configuredPath);
        var name = DisplayName(kind);
        if (location.Path is not { } path)
        {
            return new ExternalToolInfo(kind, null, null, null, NotFoundProblem(location));
        }

        ExternalProcessResult result;
        try
        {
            var start = new ExternalProcessStart(
                path,
                [],
                Path.GetTempPath(),
                SqlLoaderCommand.BaseEnvironment,
                StandardInput: null,
                CheckTimeout);
            result = await _runner.RunAsync(start, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ExternalProcessException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new ExternalToolInfo(kind, path, null, null, $"{name} 실행에 실패했습니다: {ex.Message}");
        }

        var output = result.StandardOutput + "\n" + result.StandardError;
        var (line, version) = kind == ExternalLoaderKind.SqlLoader ? ParseSqlLoaderVersion(output) : ParseTbLoaderVersion(output);
        if (line is not null)
        {
            return new ExternalToolInfo(kind, path, version, line, null);
        }

        if (result.TimedOut)
        {
            return new ExternalToolInfo(kind, path, null, null,
                string.Create(CultureInfo.InvariantCulture, $"{name}: {CheckTimeout.TotalSeconds:0}초 안에 끝나지 않아 멈췄습니다(버전 줄 없음)."));
        }

        var first = FirstLine(output);
        var problem = string.Create(CultureInfo.InvariantCulture, $"{name}: 실행했지만 버전 줄을 찾지 못했습니다(종료 코드 {result.ExitCode})")
                      + (first is null ? "." : $": {first}");
        return new ExternalToolInfo(kind, path, null, null, problem);
    }

    /// <summary>
    /// SQL*Loader 버전 줄: <c>SQL*Loader: Release 19.0.0.0.0 - Production on …</c> → 19. <c>Release</c> 가 없으면 <c>Version 23.4…</c> 줄의 앞 수.
    /// <c>SQL*Loader</c> 줄은 있는데 수를 못 읽으면 (그 줄, null).
    /// </summary>
    public static (string? Line, int? Version) ParseSqlLoaderVersion(string? output)
    {
        string? banner = null;
        foreach (var raw in Lines(output))
        {
            if (SqlLoaderRelease().Match(raw) is { Success: true } m)
            {
                return (raw, ParseInt(m.Groups["v"].Value));
            }

            if (banner is null && raw.Contains("SQL*Loader", StringComparison.OrdinalIgnoreCase) && !raw.StartsWith("SQL*Loader-", StringComparison.Ordinal))
            {
                banner = raw;
            }
        }

        if (banner is null)
        {
            return (null, null);
        }

        foreach (var raw in Lines(output))
        {
            if (VersionWord().Match(raw) is { Success: true } v)
            {
                return (banner, ParseInt(v.Groups["v"].Value));
            }
        }

        return (banner, null);
    }

    /// <summary>tbLoader 버전 줄(형식 미확인 — 너그럽게): <c>tbLoader</c> 가 든 첫 줄, 없으면 버전처럼 생긴 수(<c>7.2.1</c>)가 든 첫 줄. 버전 = 그 줄의 첫 정수.</summary>
    public static (string? Line, int? Version) ParseTbLoaderVersion(string? output)
    {
        var lines = Lines(output).ToList();
        var line = lines.FirstOrDefault(l => l.Contains("tbLoader", StringComparison.OrdinalIgnoreCase))
                   ?? lines.FirstOrDefault(l => DottedVersion().IsMatch(l));
        if (line is null)
        {
            return (null, null);
        }

        var number = FirstInteger().Match(line);
        return (line, number.Success ? ParseInt(number.Value) : null);
    }

    private string ExecutableName(ExternalLoaderKind kind)
    {
        var name = kind == ExternalLoaderKind.SqlLoader ? "sqlldr" : "tbloader";
        return _environment.IsWindows ? name + ".exe" : name;
    }

    /// <summary>설정 경로: 실행 파일 자체, (Windows) <c>.exe</c> 를 뺀 이름, 또는 폴더(폴더/실행 파일, 폴더/bin/실행 파일).</summary>
    private string? FromConfigured(string configured, string exe)
    {
        if (_environment.FileExists(configured))
        {
            return configured;
        }

        if (_environment.IsWindows && !configured.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && _environment.FileExists(configured + ".exe"))
        {
            return configured + ".exe";
        }

        if (_environment.DirectoryExists(configured))
        {
            foreach (var candidate in new[] { Combine(configured, exe), Combine(configured, "bin", exe) })
            {
                if (_environment.FileExists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private IEnumerable<string> Candidates(ExternalLoaderKind kind)
    {
        var pathDirectories = PathDirectories();
        foreach (var directory in pathDirectories)
        {
            yield return directory;
        }

        if (kind == ExternalLoaderKind.TbLoader)
        {
            if (Variable("TB_HOME") is { } tbHome)
            {
                yield return Combine(tbHome, "client", "bin");
                yield return Combine(tbHome, "bin");
            }

            yield break;
        }

        if (Variable("ORACLE_HOME") is { } oracleHome)
        {
            yield return Combine(oracleHome, "bin");
            yield return oracleHome; // Instant Client 를 ORACLE_HOME 으로 둔 경우
        }

        foreach (var directory in InstantClientDirectories(pathDirectories))
        {
            yield return directory;
        }
    }

    private List<string> InstantClientDirectories(IReadOnlyList<string> pathDirectories)
    {
        var roots = new List<string>();
        if (_environment.IsWindows)
        {
            roots.Add(@"C:\oracle");
            roots.Add(@"C:\");
            if (Variable("ProgramFiles") is { } programFiles)
            {
                roots.Add(programFiles);
            }

            if (Variable("LOCALAPPDATA") is { } localAppData)
            {
                roots.Add(Combine(localAppData, "Programs"));
            }
        }
        else
        {
            roots.Add("/opt/oracle");
            roots.Add("/usr/local");
            if (Variable("HOME") is { } home)
            {
                roots.Add(home);
            }
        }

        // PATH 에 Instant Client(Basic 만)가 있으면 그 옆 폴더에 Tools 를 따로 풀었을 수 있다.
        var library = _environment.IsWindows ? "oci.dll" : "libclntsh.so";
        foreach (var directory in pathDirectories)
        {
            if (_environment.FileExists(Combine(directory, library)) && Parent(directory) is { Length: > 0 } parent)
            {
                roots.Add(parent);
            }
        }

        var found = new List<string>();
        foreach (var root in roots)
        {
            found.AddRange(_environment.GetDirectories(root, "instantclient*").OrderByDescending(d => VersionKey(FileName(d)), VersionKeyComparer.Instance));
        }

        return found;
    }

    private List<string> PathDirectories()
    {
        var path = Variable("PATH");
        if (path is null)
        {
            return [];
        }

        var separator = _environment.IsWindows ? ';' : ':';
        return path.Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => p.Trim('"'))
            .Where(p => p.Length > 0)
            .ToList();
    }

    private string? Variable(string name) => _environment.GetVariable(name) is { } v && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;

    // 경로 조합·분해는 실행 중인 OS 가 아니라 환경(IsWindows)의 규칙을 따른다. 환경이 이 OS 와 같은 규칙이면(실제 환경은 늘 그렇다)
    // System.IO.Path 를 그대로 쓰고, 다른 OS 를 흉내 낸 환경(예: macOS·Linux 에서 도는 Windows 가짜 환경)일 때만 그 OS 의 구분자로 직접 나눈다.
    private bool NativeRules => _environment.IsWindows == OperatingSystem.IsWindows();

    private char Separator => _environment.IsWindows ? '\\' : '/';

    private bool IsSeparator(char c) => c == '/' || (_environment.IsWindows && c == '\\');

    /// <summary><see cref="Path.Combine(string[])"/> 와 같게 잇는다(앞 조각이 구분자·드라이브 콜론으로 끝나면 덧붙이지 않는다). 뒤 조각은 상대 이름만 온다.</summary>
    private string Combine(string first, params string[] rest)
    {
        if (NativeRules)
        {
            return Path.Combine([first, .. rest]);
        }

        var result = first;
        foreach (var part in rest)
        {
            var joined = result.Length == 0 || IsSeparator(result[^1]) || (_environment.IsWindows && result[^1] == ':');
            result = joined ? result + part : result + Separator + part;
        }

        return result;
    }

    /// <summary>끝 구분자를 뗀 경로의 부모(<see cref="Path.GetDirectoryName(string)"/> 처럼 <c>C:\x</c> → <c>C:\</c>, <c>/x</c> → <c>/</c>). 없으면 null.</summary>
    private string? Parent(string path)
    {
        var trimmed = TrimEndSeparators(path);
        if (NativeRules)
        {
            return Path.GetDirectoryName(trimmed);
        }

        var index = LastSeparator(trimmed);
        if (index < 0)
        {
            return null;
        }

        var parent = trimmed[..index];
        return parent.Length == 0 || (_environment.IsWindows && parent.Length == 2 && parent[1] == ':') ? parent + trimmed[index] : parent;
    }

    /// <summary>끝 구분자를 뗀 경로의 마지막 이름.</summary>
    private string FileName(string path)
    {
        var trimmed = TrimEndSeparators(path);
        return NativeRules ? Path.GetFileName(trimmed) : trimmed[(LastSeparator(trimmed) + 1)..];
    }

    /// <summary>끝 구분자를 뗀다(예전 <c>TrimEnd('\\', '/')</c> 와 같되, 그 OS 의 구분자만).</summary>
    private string TrimEndSeparators(string path) => NativeRules ? path.TrimEnd('\\', '/') : TrimForeign(path);

    private string TrimForeign(string path)
    {
        var end = path.Length;
        while (end > 0 && IsSeparator(path[end - 1]))
        {
            end--;
        }

        return path[..end];
    }

    private int LastSeparator(string path)
    {
        for (var i = path.Length - 1; i >= 0; i--)
        {
            if (IsSeparator(path[i]))
            {
                return i;
            }
        }

        return -1;
    }

    private static string NotFoundProblem(ExternalToolLocation location)
    {
        var name = DisplayName(location.Kind);
        if (location.FromSettings)
        {
            return $"설정한 경로에서 {name} 실행 파일을 찾지 못했습니다: {(location.Searched.Count > 0 ? location.Searched[0] : string.Empty)}";
        }

        var install = location.Kind == ExternalLoaderKind.SqlLoader
            ? "Oracle Instant Client(Basic·Tools)를 설치하거나 설정에 경로를 적으세요."
            : "Tibero 클라이언트를 설치하거나 설정에 경로를 적으세요.";
        var shown = string.Join(", ", location.Searched.Take(MaxSearchedInMessage));
        var more = location.Searched.Count > MaxSearchedInMessage
            ? string.Create(CultureInfo.InvariantCulture, $" 외 {location.Searched.Count - MaxSearchedInMessage}곳")
            : string.Empty;
        return location.Searched.Count == 0
            ? $"{name} 실행 파일을 찾지 못했습니다. {install}"
            : $"{name} 실행 파일을 찾지 못했습니다. {install} 살펴본 곳: {shown}{more}";
    }

    private static IEnumerable<string> Lines(string? output) =>
        (output ?? string.Empty).Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0);

    private static string? FirstLine(string output)
    {
        var line = Lines(output).FirstOrDefault();
        return line is null ? null : line.Length <= 200 ? line : line[..200] + "…";
    }

    private static int? ParseInt(string text) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;

    /// <summary>폴더 이름의 수 목록(<c>instantclient_23_4</c> → [23, 4]).</summary>
    private static int[] VersionKey(string name) =>
        FirstInteger().Matches(name)
            .Select(m => int.TryParse(m.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0)
            .ToArray();

    [GeneratedRegex(@"SQL\*Loader:\s*Release\s+(?<v>\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex SqlLoaderRelease();

    [GeneratedRegex(@"^Version\s+(?<v>\d+)(\.\d+)*", RegexOptions.IgnoreCase)]
    private static partial Regex VersionWord();

    [GeneratedRegex(@"\d+(\.\d+)+")]
    private static partial Regex DottedVersion();

    [GeneratedRegex(@"\d+")]
    private static partial Regex FirstInteger();

    private sealed class VersionKeyComparer : IComparer<int[]>
    {
        public static readonly VersionKeyComparer Instance = new();

        public int Compare(int[]? x, int[]? y)
        {
            x ??= [];
            y ??= [];
            for (var i = 0; i < Math.Max(x.Length, y.Length); i++)
            {
                var a = i < x.Length ? x[i] : -1;
                var b = i < y.Length ? y[i] : -1;
                if (a != b)
                {
                    return a.CompareTo(b);
                }
            }

            return 0;
        }
    }
}
