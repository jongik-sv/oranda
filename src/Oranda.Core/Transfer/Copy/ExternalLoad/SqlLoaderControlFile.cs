using System.Globalization;
using System.Text;

namespace Oranda.Core.Transfer.Copy.ExternalLoad;

/// <summary>
/// SQL*Loader 제어 파일 하나의 재료. <see cref="DataFile"/>·<see cref="BadFile"/>·<see cref="DiscardFile"/> 은 전체 경로(작은따옴표가 있으면 쓸 수 없다).
/// <see cref="Direct"/> = 직접 경로(이 실행이 만든 테이블에만 쓴다. 이미 있던 테이블은 기존 방식 — 트리거·제약이 앱의 INSERT 와 같게 동작).
/// <see cref="MaxErrors"/> = 허용 오류 수(<c>ERRORS</c>, 넘으면 도구가 멈춘다).
/// </summary>
public sealed record LoaderControlSpec(
    string Owner,
    string Table,
    IReadOnlyList<LoaderColumn> Columns,
    string DataFile,
    string BadFile,
    string DiscardFile,
    int MaxErrors,
    bool Direct)
{
    /// <summary>기존 경로(conventional) 한 번에 보내는 행 수(<c>ROWS</c>). null = 도구 기본값.</summary>
    public int? Rows { get; init; }
}

/// <summary>
/// SQL*Loader 제어 파일 글(XL1). <see cref="LoaderDataWriter"/> 가 만든 데이터 파일 형식과 짝을 이룬다:
/// 필드 <c>,</c> 구분·<c>"</c> 선택 감쌈, 레코드 끝 <c>X'1E0A'</c>, 문자 집합 AL32UTF8, LOB 는 필드에 적은 파일 이름(LOBFILE).
/// 파일은 <see cref="FileEncoding"/>(UTF-8, BOM 없음)로 쓴다 — 도구는 NLS_LANG(<see cref="SqlLoaderCommand"/> 가 AL32UTF8 로 둔다) 문자 집합으로 읽는다.
/// </summary>
public static class SqlLoaderControlFile
{
    public const string FileName = "load.ctl";

    /// <summary>선언 길이를 모르는 문자 필드의 길이(바이트). 아는 열은 선언 길이 × 4(UTF-8 한 글자 최대 4바이트).</summary>
    public const int MinTextBytes = 4000;

    /// <summary>NUMBER 필드 길이. 이식 숫자 글자는 400자를 넘지 않는다(<c>PortableNumber</c>).</summary>
    public const int NumberBytes = 400;

    /// <summary>LOB 파일 이름을 담는 FILLER 필드 길이.</summary>
    public const int LobFileNameBytes = 512;

    private const int SmallCharBytes = 100;

    /// <summary>기존 경로의 바인드 배열·읽기 버퍼 크기(바이트, 20MB).</summary>
    public const int BindSize = 20 * 1024 * 1024;

    /// <summary>direct path 열 배열 한 번의 행 수(<c>COLUMNARRAYROWS</c>, 도구 기본 5,000).</summary>
    public const int DirectColumnArrayRows = 10_000;

    /// <summary>direct path 서버로 보내는 스트림 버퍼(<c>STREAMSIZE</c>, 바이트, 도구 기본 256,000).</summary>
    public const int DirectStreamSize = 4 * 1024 * 1024;

    public static Encoding FileEncoding { get; } = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>제어 파일 줄바꿈(Windows 는 CRLF).</summary>
    internal static string NewLine => OperatingSystem.IsWindows() ? "\r\n" : "\n";

    /// <summary>
    /// 제어 파일 글. 열 형식이 외부 도구가 다루지 않는 형식이거나(사전 점검이 <see cref="LoaderValueText.Supports"/> 로 거른다),
    /// 이름에 <c>"</c>·경로에 <c>'</c> 가 있으면 <see cref="ArgumentException"/>.
    /// </summary>
    public static string Build(LoaderControlSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(spec.Columns);
        if (spec.Columns.Count == 0)
        {
            throw new ArgumentException("적재할 열이 없습니다.", nameof(spec));
        }

        if (spec.MaxErrors < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(spec), "허용 오류 수는 0 이상이어야 합니다.");
        }

        var nl = NewLine;
        var b = new StringBuilder();
        b.Append(CultureInfo.InvariantCulture, $"OPTIONS (DIRECT={(spec.Direct ? "TRUE" : "FALSE")}, ERRORS={spec.MaxErrors}");
        if (!spec.Direct && spec.Rows is { } rows and > 0)
        {
            // 기본 BINDSIZE(1MB)면 ROWS 를 크게 해도 바인드 배열이 1MB 에서 잘린다(로그 「Bind array … maximum of 1048576 bytes」): 함께 키운다.
            b.Append(CultureInfo.InvariantCulture, $", ROWS={rows}, BINDSIZE={BindSize}, READSIZE={BindSize}");
        }
        else if (spec.Direct)
        {
            // direct path 는 BINDSIZE·ROWS 가 아니라 열 배열 행 수·스트림 크기·읽기 버퍼가 속도를 정한다(#184 로그: Read buffer 1MB 기본값). 결정 97.
            b.Append(CultureInfo.InvariantCulture, $", COLUMNARRAYROWS={DirectColumnArrayRows}, STREAMSIZE={DirectStreamSize}, READSIZE={BindSize}, MULTITHREADING=TRUE");
        }

        b.Append(')').Append(nl);
        b.Append("LOAD DATA").Append(nl);
        b.Append("CHARACTERSET AL32UTF8").Append(nl);
        b.Append("INFILE ").Append(PathLiteral(spec.DataFile)).Append(" \"str X'1E0A'\"").Append(nl);
        b.Append("BADFILE ").Append(PathLiteral(spec.BadFile)).Append(nl);
        b.Append("DISCARDFILE ").Append(PathLiteral(spec.DiscardFile)).Append(nl);
        b.Append("APPEND").Append(nl);
        b.Append("INTO TABLE ").Append(QuoteName(spec.Owner)).Append('.').Append(QuoteName(spec.Table)).Append(nl);
        if (spec.Direct)
        {
            // 직접 경로가 끈 제약(CHECK·FK 등)을 적재 뒤 다시 켠다.
            b.Append("REENABLE DISABLED_CONSTRAINTS").Append(nl);
        }

        b.Append("FIELDS TERMINATED BY ',' OPTIONALLY ENCLOSED BY '\"'").Append(nl);
        b.Append("TRAILING NULLCOLS").Append(nl);
        b.Append('(').Append(nl);

        var names = new HashSet<string>(spec.Columns.Select(c => c.Name), StringComparer.Ordinal);
        for (var i = 0; i < spec.Columns.Count; i++)
        {
            b.Append("  ").Append(FieldClause(spec.Columns[i], i, names));
            b.Append(i + 1 < spec.Columns.Count ? "," : string.Empty).Append(nl);
        }

        b.Append(')').Append(nl);
        return b.ToString();
    }

    /// <summary>열 하나의 필드 정의(LOB 은 FILLER 필드와 LOBFILE 열 두 줄).</summary>
    internal static string FieldClause(LoaderColumn column, int index, ISet<string> names)
    {
        ArgumentNullException.ThrowIfNull(column);
        if (!LoaderFieldType.TryParse(column.TargetType, out var type))
        {
            throw new ArgumentException(LoaderValueText.UnsupportedType(column.Name, column.TargetType), nameof(column));
        }

        var name = QuoteName(column.Name);
        var inv = CultureInfo.InvariantCulture;
        switch (type.Kind)
        {
            case LoaderFieldKind.Text:
                // 선언 길이 × 4(UTF-8 한 글자 최대 4바이트). 예전처럼 최소 4,000 바이트로 두면 기존 경로의 바인드 배열이 한 번에 100행 남짓만 담아 느렸다(win144 #182).
                return string.Create(inv, $"{name} CHAR({(type.Length is { } n and > 0 ? 4L * n : MinTextBytes)})");
            case LoaderFieldKind.Number:
                return string.Create(inv, $"{name} CHAR({NumberBytes})");
            case LoaderFieldKind.Date:
                return $"{name} DATE \"{LoaderValueText.DateMask}\"";
            case LoaderFieldKind.Timestamp:
                return $"{name} TIMESTAMP \"{LoaderValueText.TimestampMask}\"";
            case LoaderFieldKind.TimestampTz:
                return $"{name} TIMESTAMP WITH TIME ZONE \"{LoaderValueText.TimestampTzMask}\"";
            case LoaderFieldKind.IntervalYm:
                return SqlString(name, SmallCharBytes, "TO_YMINTERVAL");
            case LoaderFieldKind.IntervalDs:
                return SqlString(name, SmallCharBytes, "TO_DSINTERVAL");
            case LoaderFieldKind.BinaryFloat:
                return SqlString(name, SmallCharBytes, "TO_BINARY_FLOAT");
            case LoaderFieldKind.BinaryDouble:
                return SqlString(name, SmallCharBytes, "TO_BINARY_DOUBLE");
            case LoaderFieldKind.Raw:
                return SqlString(name, (int)Math.Min(int.MaxValue, 2L * (type.Length ?? 2000)), "HEXTORAW");
            case LoaderFieldKind.Clob:
            case LoaderFieldKind.Blob:
            {
                var filler = FillerName(index, names);
                return string.Create(
                    inv,
                    $"{filler} FILLER CHAR({LobFileNameBytes}),{NewLine}  {name} LOBFILE({filler}) TERMINATED BY EOF NULLIF {filler}=BLANKS");
            }

            default:
                throw new ArgumentException(LoaderValueText.UnsupportedType(column.Name, column.TargetType), nameof(column));
        }
    }

    /// <summary>큰따옴표로 감싼 이름. 이름에 <c>"</c> 가 있으면(Oracle 식별자에 올 수 없다) <see cref="ArgumentException"/>.</summary>
    internal static string QuoteName(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (name.Contains('"', StringComparison.Ordinal))
        {
            throw new ArgumentException($"이름에 큰따옴표가 있어 제어 파일에 적을 수 없습니다: {name}", nameof(name));
        }

        return "\"" + name + "\"";
    }

    /// <summary>작은따옴표로 감싼 경로. 경로에 <c>'</c> 가 있으면 <see cref="ArgumentException"/>(도구의 이스케이프 규칙이 OS 마다 달라 피한다).</summary>
    internal static string PathLiteral(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (path.Contains('\'', StringComparison.Ordinal) || path.Contains('\n', StringComparison.Ordinal) || path.Contains('\r', StringComparison.Ordinal))
        {
            throw new ArgumentException($"경로에 작은따옴표나 줄바꿈이 있어 제어 파일에 적을 수 없습니다(다른 작업 폴더를 쓰세요): {path}", nameof(path));
        }

        return "'" + path + "'";
    }

    /// <summary>SQL 문자열을 단 필드: <c>"COL" CHAR(100) "TO_YMINTERVAL(:\"COL\")"</c>(SQL 문자열 안의 큰따옴표는 역슬래시로 이스케이프).</summary>
    private static string SqlString(string quotedName, int bytes, string function)
    {
        var bind = ":\\\"" + quotedName[1..^1] + "\\\"";
        return string.Create(CultureInfo.InvariantCulture, $"{quotedName} CHAR({bytes}) \"{function}({bind})\"");
    }

    /// <summary>LOB 파일 이름 FILLER 필드 이름: <c>"F$LOB&lt;열 순번&gt;"</c>, 실제 열 이름과 겹치면 뒤에 <c>$</c> 를 더한다.</summary>
    private static string FillerName(int index, ISet<string> names)
    {
        var name = "F$LOB" + (index + 1).ToString(CultureInfo.InvariantCulture);
        while (names.Contains(name))
        {
            name += "$";
        }

        names.Add(name);
        return "\"" + name + "\"";
    }
}
