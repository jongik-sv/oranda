using System.Globalization;
using System.Text.RegularExpressions;

namespace Oranda.Core.Transfer.Copy.ExternalLoad;

/// <summary>도구가 거절한 레코드 하나. <see cref="Record"/> = 데이터 파일의 레코드 번호(1부터, <see cref="LoaderDataWriter.ReadKeys(IReadOnlySet{long})"/> 로 키를 찾는다).</summary>
public sealed record LoaderRejected(long Record, string Error);

/// <summary>
/// SQL*Loader 로그를 읽은 결과. <see cref="Loaded"/> = 적재 행 수(요약이 없으면 null — 도중에 멈춤), <see cref="Rejected"/> = 데이터 오류로 거절,
/// <see cref="Discarded"/> = WHEN 조건으로 버림, <see cref="AllNull"/> = 모든 필드가 NULL 이라 버림, <see cref="Rejects"/> = 거절 레코드(앞쪽 최대 <see cref="SqlLoaderLog.MaxRejects"/> 개),
/// <see cref="Fatal"/> = <c>SQL*Loader-nnn</c> 줄과 그에 붙은 <c>ORA-</c> 줄, <see cref="ErrorLimitHit"/> = 허용 오류 수를 넘어 멈춤,
/// <see cref="UnusableIndexes"/> = 직접 경로 적재 뒤 쓸 수 없게 된 인덱스(호출 측은 하나라도 있으면 실패로 본다), <see cref="Read"/> = 읽은 논리 레코드 수.
/// </summary>
public sealed record LoaderLogResult(
    long? Loaded,
    long Rejected,
    long Discarded,
    long AllNull,
    IReadOnlyList<LoaderRejected> Rejects,
    IReadOnlyList<string> Fatal,
    bool ErrorLimitHit,
    IReadOnlyList<string> UnusableIndexes,
    long? Read)
{
    /// <summary>로그가 없거나 비었을 때.</summary>
    public static LoaderLogResult Empty { get; } = new(null, 0, 0, 0, [], [], false, [], null);

    /// <summary>요약 줄이 있고, 거절·버림·치명 오류·쓸 수 없는 인덱스가 없다.</summary>
    public bool Clean => Loaded is not null && Rejected == 0 && Discarded == 0 && AllNull == 0 && Fatal.Count == 0 && !ErrorLimitHit && UnusableIndexes.Count == 0;
}

/// <summary>SQL*Loader 종료 코드(EX_SUCC 0·EX_FAIL 1·EX_WARN 2·EX_FTL 3, Windows·Unix 같음).</summary>
public enum SqlLoaderExit
{
    /// <summary>0: 모든 행 적재.</summary>
    Success,

    /// <summary>1: 명령줄·문법 오류, 로그인 실패, 파일 오류 등.</summary>
    Failure,

    /// <summary>2: 일부 행 거절·버림, 또는 직접 경로 도중 멈춤.</summary>
    Warning,

    /// <summary>3: OS 오류 등 치명적.</summary>
    Fatal,

    /// <summary>그 밖의 코드.</summary>
    Unknown,
}

/// <summary>
/// SQL*Loader 로그 읽기(XL1). <c>NLS_LANG=AMERICAN_…</c> 로 실행한 영어 로그를 읽는다(19c·23ai 형식). 표준 출력도 같은 규칙으로 읽을 수 있다(로그 파일을 만들기 전에 멈춘 경우).
/// </summary>
public static partial class SqlLoaderLog
{
    /// <summary>거절 레코드를 이만큼만 모은다(개수는 요약 줄로 센다).</summary>
    public const int MaxRejects = 1000;

    /// <summary>치명 오류 줄을 이만큼만 모은다.</summary>
    public const int MaxFatal = 50;

    public static SqlLoaderExit ClassifyExit(int exitCode) => exitCode switch
    {
        0 => SqlLoaderExit.Success,
        1 => SqlLoaderExit.Failure,
        2 => SqlLoaderExit.Warning,
        3 => SqlLoaderExit.Fatal,
        _ => SqlLoaderExit.Unknown,
    };

    public static LoaderLogResult Parse(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return LoaderLogResult.Empty;
        }

        var lines = text.Split('\n');
        long? loaded = null;
        long? read = null;
        long rejected = 0;
        long discarded = 0;
        long allNull = 0;
        var errorLimit = false;
        var rejects = new List<LoaderRejected>();
        var fatal = new List<string>();
        var unusable = new List<string>();

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            if (RejectLine().Match(trimmed) is { Success: true } reject)
            {
                // 다음 빈 줄까지가 그 레코드의 오류 글(ORA- 줄 또는 도구 글). 빈 줄이 빠져도 다음 부분(레코드·요약·한도 줄)에서 멈춘다.
                var message = new List<string>();
                while (i + 1 < lines.Length && lines[i + 1].Trim() is { Length: > 0 } next && !StartsSection(next))
                {
                    message.Add(next);
                    i++;
                }

                if (rejects.Count < MaxRejects)
                {
                    var error = message.Count > 0 ? string.Join(' ', message) : trimmed;
                    var column = reject.Groups["column"];
                    rejects.Add(new LoaderRejected(
                        long.Parse(reject.Groups["record"].Value, NumberStyles.None, CultureInfo.InvariantCulture),
                        column.Success ? $"열 {Unquote(column.Value)}: {error}" : error));
                }

                continue;
            }

            if (UnusableIndexLine().IsMatch(trimmed))
            {
                // "index SCOTT.PK_EMP was made unusable due to:" + 다음 ORA- 줄.
                var entry = trimmed;
                if (i + 1 < lines.Length && lines[i + 1].Trim() is { } cause && cause.StartsWith("ORA-", StringComparison.Ordinal))
                {
                    entry += " " + cause;
                    i++;
                }

                unusable.Add(entry);
                continue;
            }

            if (FatalLine().IsMatch(trimmed))
            {
                if (IndexUnusableMessage().IsMatch(trimmed))
                {
                    unusable.Add(trimmed);
                }

                if (fatal.Count < MaxFatal)
                {
                    fatal.Add(trimmed);
                }

                // 붙은 ORA- 줄(로그인 실패 등)도 함께.
                while (i + 1 < lines.Length && lines[i + 1].Trim() is { } ora && ora.StartsWith("ORA-", StringComparison.Ordinal))
                {
                    if (fatal.Count < MaxFatal)
                    {
                        fatal.Add(ora);
                    }

                    i++;
                }

                continue;
            }

            if (trimmed.StartsWith("ORA-", StringComparison.Ordinal) || trimmed.StartsWith("TNS-", StringComparison.Ordinal))
            {
                // 거절 레코드·인덱스에 붙지 않은 오류 줄(접속 실패 「SQL*Loader-128」 뒤 빈 줄 다음의 ORA-01017·ORA-12514 등)도 원인으로 모은다.
                if (fatal.Count < MaxFatal)
                {
                    fatal.Add(trimmed);
                }

                continue;
            }

            if (trimmed.Contains("MAXIMUM ERROR COUNT EXCEEDED", StringComparison.OrdinalIgnoreCase))
            {
                errorLimit = true;
                continue;
            }

            if (Count(LoadedLine(), trimmed) is { } l)
            {
                loaded = (loaded ?? 0) + l;
            }
            else if (Count(DataErrorLine(), trimmed) is { } r)
            {
                rejected += r;
            }
            else if (Count(WhenLine(), trimmed) is { } d)
            {
                discarded += d;
            }
            else if (Count(AllNullLine(), trimmed) is { } n)
            {
                allNull += n;
            }
            else if (Count(TotalReadLine(), trimmed) is { } t)
            {
                read = t;
            }
        }

        return new LoaderLogResult(loaded, rejected, discarded, allNull, rejects, fatal, errorLimit, unusable, read);
    }

    /// <summary>거절 레코드의 오류 글이 아니라 로그의 다음 부분이 시작하는 줄.</summary>
    private static bool StartsSection(string line) =>
        RecordLine().IsMatch(line)
        || line.StartsWith("Table ", StringComparison.Ordinal)
        || line.Contains("MAXIMUM ERROR COUNT EXCEEDED", StringComparison.OrdinalIgnoreCase)
        || line.StartsWith("Total logical records", StringComparison.Ordinal)
        || UnusableIndexLine().IsMatch(line)
        || FatalLine().IsMatch(line);

    private static long? Count(Regex regex, string line) =>
        regex.Match(line) is { Success: true } m
        && long.TryParse(m.Groups["n"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n)
            ? n
            : null;

    private static string Unquote(string name) => name.Length >= 2 && name[0] == '"' && name[^1] == '"' ? name[1..^1] : name;

    [GeneratedRegex(@"^Record\s+(?<record>\d+):\s+Rejected\s+-\s+Error on table\s+(?<table>.+?)(?:,\s+column\s+(?<column>.+?))?\.?$")]
    private static partial Regex RejectLine();

    [GeneratedRegex(@"^\s*Record\s+\d+:")]
    private static partial Regex RecordLine();

    [GeneratedRegex(@"^index\s+\S+\s+was made unusable", RegexOptions.IgnoreCase)]
    private static partial Regex UnusableIndexLine();

    [GeneratedRegex(@"^SQL\*Loader-\d+:")]
    private static partial Regex FatalLine();

    [GeneratedRegex(@"index\b.*\bunusable", RegexOptions.IgnoreCase)]
    private static partial Regex IndexUnusableMessage();

    [GeneratedRegex(@"^(?<n>\d+)\s+Rows?\s+successfully loaded\.")]
    private static partial Regex LoadedLine();

    [GeneratedRegex(@"^(?<n>\d+)\s+Rows?\s+not loaded due to data errors\.")]
    private static partial Regex DataErrorLine();

    [GeneratedRegex(@"^(?<n>\d+)\s+Rows?\s+not loaded because all WHEN clauses were failed\.")]
    private static partial Regex WhenLine();

    [GeneratedRegex(@"^(?<n>\d+)\s+Rows?\s+not loaded because all fields were null\.")]
    private static partial Regex AllNullLine();

    [GeneratedRegex(@"^Total logical records read:\s+(?<n>\d+)")]
    private static partial Regex TotalReadLine();
}
