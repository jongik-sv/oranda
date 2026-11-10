using System.Globalization;
using System.Text;

namespace Oranda.Core.Transfer.Copy.ExternalLoad;

/// <summary>
/// tbLoader 제어 파일 글(XL2, Tibero 대상). <see cref="LoaderDataWriter"/> 의 <see cref="LoaderFileFormat.TbLoader"/> 형식과 짝을 이룬다:
/// 필드 <c>,</c> 구분·<c>"</c> 선택 감쌈·역슬래시 이스케이프, 줄 끝 = 0x1E 0x0A, 문자 집합 UTF8, LOB 는 필드에 적은 파일 이름(<c>OUTFILE</c>).
/// <para>
/// tbLoader 는 빈 숫자 필드를 0 으로 넣는다(설명서: NULL 을 넣으려면 문자 형식으로 받아야 한다). 그래서 숫자·구간·이진 실수는 모두 <c>CHAR</c> 로 받고
/// 서버 함수로 바꾼다. 문법에 시간대 시각 형식이 없어 <c>TO_TIMESTAMP_TZ</c> 로 받는다. 근거 = Tibero Utility Guide tbLoader 장, 실서버 확인 전(win144 실측).
/// </para>
/// </summary>
public static class TbLoaderControlFile
{
    /// <summary>tbLoader 적재를 켤지(실측에서 막히면 끈다 — 끄면 사전 점검이 기본 방식으로 바꾼다).</summary>
    public static bool Enabled => true;

    private const char NewLine = '\n';
    private const int MinTextBytes = 4000;
    private const int SmallCharBytes = 100;

    public static string Build(LoaderControlSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (spec.Columns.Count == 0)
        {
            throw new ArgumentException("적재할 열이 없습니다.", nameof(spec));
        }

        var b = new StringBuilder();
        b.Append("LOAD DATA").Append(NewLine);
        b.Append("CHARACTERSET UTF8").Append(NewLine);
        b.Append("INFILE ").Append(SqlLoaderControlFile.PathLiteral(spec.DataFile)).Append(NewLine);
        b.Append("BADFILE ").Append(SqlLoaderControlFile.PathLiteral(spec.BadFile)).Append(NewLine);
        b.Append("DISCARDFILE ").Append(SqlLoaderControlFile.PathLiteral(spec.DiscardFile)).Append(NewLine);
        b.Append("APPEND").Append(NewLine);
        b.Append("INTO TABLE ").Append(SqlLoaderControlFile.QuoteName(spec.Owner)).Append('.').Append(SqlLoaderControlFile.QuoteName(spec.Table)).Append(NewLine);
        b.Append("FIELDS TERMINATED BY ',' OPTIONALLY ENCLOSED BY '\"' ESCAPED BY '\\\\'").Append(NewLine);
        // 줄 끝 = 0x1E(레코드 구분) + 0x0A. 0x1E 는 글자 그대로 적는다(값 안의 줄바꿈이 레코드를 끊지 않게).
        b.Append("LINES TERMINATED BY '\u001E\\n'").Append(NewLine);
        b.Append("TRAILING NULLCOLS").Append(NewLine);
        b.Append('(').Append(NewLine);
        for (var i = 0; i < spec.Columns.Count; i++)
        {
            b.Append("  ").Append(FieldClause(spec.Columns[i]));
            b.Append(i + 1 < spec.Columns.Count ? "," : string.Empty).Append(NewLine);
        }

        b.Append(')').Append(NewLine);
        return b.ToString();
    }

    internal static string FieldClause(LoaderColumn column)
    {
        ArgumentNullException.ThrowIfNull(column);
        if (!LoaderFieldType.TryParse(column.TargetType, out var type))
        {
            throw new ArgumentException(LoaderValueText.UnsupportedType(column.Name, column.TargetType), nameof(column));
        }

        var name = SqlLoaderControlFile.QuoteName(column.Name);
        var inv = CultureInfo.InvariantCulture;
        return type.Kind switch
        {
            LoaderFieldKind.Text => string.Create(inv, $"{name} CHAR({Math.Max(MinTextBytes, 4L * (type.Length ?? MinTextBytes))})"),
            LoaderFieldKind.Number => Expression(name, column.Name, SmallCharBytes * 4, "TO_NUMBER({0})"),
            LoaderFieldKind.Date => $"{name} DATE \"{LoaderValueText.DateMask}\"",
            LoaderFieldKind.Timestamp => $"{name} TIMESTAMP \"{LoaderValueText.TimestampMask}\"",
            LoaderFieldKind.TimestampTz => Expression(name, column.Name, SmallCharBytes, $"TO_TIMESTAMP_TZ({{0}}, '{LoaderValueText.TimestampTzMask}')"),
            LoaderFieldKind.IntervalYm => Expression(name, column.Name, SmallCharBytes, "TO_YMINTERVAL({0})"),
            LoaderFieldKind.IntervalDs => Expression(name, column.Name, SmallCharBytes, "TO_DSINTERVAL({0})"),
            LoaderFieldKind.BinaryFloat => Expression(name, column.Name, SmallCharBytes, "TO_BINARY_FLOAT({0})"),
            LoaderFieldKind.BinaryDouble => Expression(name, column.Name, SmallCharBytes, "TO_BINARY_DOUBLE({0})"),
            LoaderFieldKind.Raw => Expression(name, column.Name, (int)Math.Min(int.MaxValue, 2L * (type.Length ?? 2000)), "HEXTORAW({0})"),
            LoaderFieldKind.Clob or LoaderFieldKind.Blob => $"{name} OUTFILE NULLIF {name} = BLANKS",
            _ => throw new ArgumentException(LoaderValueText.UnsupportedType(column.Name, column.TargetType), nameof(column)),
        };
    }

    /// <summary>문자로 받아 서버 함수로 바꾸는 필드: <c>"COL" CHAR(n) "TO_NUMBER(:COL)"</c>. 바인드 이름은 열 이름 그대로(따옴표 없이 — 설명서 예 <c>":empno"</c>).</summary>
    private static string Expression(string quotedName, string name, int bytes, string format)
    {
        if (!IsSimpleName(name))
        {
            throw new ArgumentException($"열 이름 {name} 은(는) tbLoader 식에 바인드할 수 없는 이름입니다.", nameof(name));
        }

        var expression = string.Format(CultureInfo.InvariantCulture, format, ":" + name);
        return string.Create(CultureInfo.InvariantCulture, $"{quotedName} CHAR({bytes}) \"{expression}\"");
    }

    /// <summary>따옴표 없이 쓸 수 있는 이름(영문 대문자로 시작, 대문자·숫자·_·$·#).</summary>
    internal static bool IsSimpleName(string name) =>
        name.Length > 0 && name[0] is >= 'A' and <= 'Z' && name.All(c => c is (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_' or '$' or '#');

    /// <summary>tbLoader 로 넣을 수 있는 열 이름인가(식으로 받는 형식은 단순한 이름만).</summary>
    public static bool Supports(LoaderColumn column)
    {
        ArgumentNullException.ThrowIfNull(column);
        if (!LoaderFieldType.TryParse(column.TargetType, out var type))
        {
            return false;
        }

        return type.Kind is LoaderFieldKind.Text or LoaderFieldKind.Date or LoaderFieldKind.Timestamp or LoaderFieldKind.Clob or LoaderFieldKind.Blob
               || IsSimpleName(column.Name);
    }
}
