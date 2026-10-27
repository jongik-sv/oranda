using System.Globalization;
using System.Text;
using Oranda.Core.Metadata;
using Oranda.Language.Editing;

namespace Oranda.App.Features.Flashback;

/// <summary>
/// 「변경 추적하기」(TSK-13-31)가 새 SQL 탭에 넣는 글. 사람이 읽고 고쳐 다시 돌릴 수 있게 키 값을 바인드가 아니라 리터럴로 쓴다.
/// 기간은 묻지 않고 설정 「변경 추적 기간」(TSK-13-44)을 따른다: 기본 최근 N일(<c>VERSIONS BETWEEN TIMESTAMP SYSDATE - N AND MAXVALUE</c>),
/// 또는 가능한 전체 기간(<c>VERSIONS BETWEEN SCN MINVALUE AND MAXVALUE</c> — 운영 DB 에서 ORA-08180 이 날 수 있다).
/// 끝은 <c>SYSDATE</c> 가 아니라 <c>MAXVALUE</c>: 시각→SCN 대응이 몇 초 늦어 <c>AND SYSDATE</c> 는 방금 커밋한 변경을 빠뜨린다(26ai·11.2 확인).
/// </summary>
internal static class FlashbackTrackSql
{
    /// <summary>기본 기간(일). 사용자가 운영 DB 에서 돌아감을 확인한 값(<see cref="Oranda.Core.Settings.GridSettings.DefaultFlashbackTrackDays"/> 와 같음).</summary>
    public const decimal DefaultDays = 1.5m;

    /// <summary>표 정의가 바뀐(마지막 DDL) 시각 뒤 이만큼 지나서부터 읽는다: 그 앞 시각은 SCN 대응이 DDL 앞으로 잡혀 ORA-01466 이 난다.</summary>
    public const int DdlMarginSeconds = 10;

    private const string SettingPath = "설정 > 결과 그리드 「변경 추적 기간」";

    /// <summary>설정의 기간: 최근 N일이면 N(범위 안으로, 소수 둘째 자리), 가능한 전체 기간이면 null.</summary>
    public static decimal? DaysFrom(Oranda.Core.Settings.GridSettings grid)
    {
        ArgumentNullException.ThrowIfNull(grid);
        if (grid.FlashbackTrackRange == Oranda.Core.Settings.FlashbackTrackRange.All)
        {
            return null;
        }

        var days = double.IsFinite(grid.FlashbackTrackDays) ? grid.FlashbackTrackDays : Oranda.Core.Settings.GridSettings.DefaultFlashbackTrackDays;
        return Math.Round((decimal)Math.Clamp(days, Oranda.Core.Settings.GridSettings.MinFlashbackTrackDays, Oranda.Core.Settings.GridSettings.MaxFlashbackTrackDays), 2);
    }

    /// <summary>
    /// 한 줄 주석(대상 표·키·기간·한계)과 변경 이력 질의 한 문장. 키 값이 NULL 이면 <see cref="ArgumentException"/>.
    /// <paramref name="currentUser"/> 가 표 소유자와 다르면 주석에 FLASHBACK 권한 안내를 덧붙인다.
    /// <paramref name="days"/> 는 최근 N일(null = 가능한 전체 기간). <paramref name="lastDdl"/>(표 정의가 마지막으로 바뀐 시각, 서버 시각)을 알면
    /// 최근 N일의 시작을 그 <see cref="DdlMarginSeconds"/>초 뒤보다 앞으로 잡지 않는다.
    /// </summary>
    public static string Build(DbObjectRef table, IReadOnlyList<string> columns, IReadOnlyList<string> keyColumns, IReadOnlyList<object?> keyValues,
                               string? currentUser = null, decimal? days = DefaultDays, DateTime? lastDdl = null)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(keyColumns);
        ArgumentNullException.ThrowIfNull(keyValues);
        if (columns.Count == 0 || keyColumns.Count == 0 || keyColumns.Count != keyValues.Count)
        {
            throw new ArgumentException("열과 키 열·키 값의 수가 맞아야 합니다.");
        }

        var conditions = new List<string>(keyColumns.Count);
        var shown = new List<string>(keyColumns.Count);
        for (var i = 0; i < keyColumns.Count; i++)
        {
            if (keyValues[i] is null or DBNull)
            {
                throw new ArgumentException($"키 {keyColumns[i]} 값이 NULL 입니다.", nameof(keyValues));
            }

            var rowId = IsRowId(keyColumns[i]);
            var literal = Literal(keyValues[i]!, rowId);
            conditions.Add($"{Column(keyColumns[i])} = {literal}");
            shown.Add($"{keyColumns[i]} = {literal}");
        }

        if (days is { } d && (d < (decimal)Oranda.Core.Settings.GridSettings.MinFlashbackTrackDays || d > (decimal)Oranda.Core.Settings.GridSettings.MaxFlashbackTrackDays))
        {
            throw new ArgumentOutOfRangeException(nameof(days), days, "기간(일)이 범위를 벗어났습니다.");
        }

        var inv = CultureInfo.InvariantCulture;
        string range;
        var sb = new StringBuilder();
        sb.Append("-- ").Append(OneLine(RowDmlBuilder.QualifiedName(table))).Append(" 의 변경 이력(키: ").Append(OneLine(string.Join(", ", shown))).Append("). ");
        if (days is { } n)
        {
            var since = "SYSDATE - " + n.ToString("0.##", inv);
            range = "TIMESTAMP " + since;
            sb.Append("기간: 최근 ").Append(n.ToString("0.##", inv)).Append("일(").Append(since).Append(")은 고쳐 다시 돌리거나 ").Append(SettingPath)
              .Append("에서 바꿀 수 있습니다. undo 보존 기간과 표 정의 변경 시점까지만 보입니다. ORA-08180·ORA-30052 가 나면 기간을 줄이세요.");
            if (lastDdl is { } ddl)
            {
                var at = ddl.ToString("yyyy-MM-dd HH:mm:ss", inv);
                range = $"TIMESTAMP GREATEST({since}, TO_DATE('{at}', 'YYYY-MM-DD HH24:MI:SS') + INTERVAL '{DdlMarginSeconds.ToString(inv)}' SECOND)";
                sb.Append(" 표 정의가 마지막으로 바뀐 시각(").Append(at).Append(") ").Append(DdlMarginSeconds.ToString(inv)).Append("초 뒤부터 읽습니다(그 앞은 ORA-01466).");
            }
        }
        else
        {
            range = "SCN MINVALUE";
            sb.Append("기간: 가능한 전체 기간(SCN MINVALUE). DB 에 따라 ORA-08180 이 날 수 있으며 ").Append(SettingPath)
              .Append("에서 최근 N일로 바꿀 수 있습니다. undo 보존 기간과 표 정의 변경 시점까지만 보입니다.");
        }

        if (currentUser is not null && !string.Equals(table.Owner, currentUser, StringComparison.OrdinalIgnoreCase))
        {
            sb.Append(" 다른 스키마 표는 FLASHBACK 권한이 있어야 합니다.");
        }

        sb.Append('\n');
        sb.Append("SELECT VERSIONS_STARTTIME, VERSIONS_ENDTIME, VERSIONS_OPERATION, RAWTOHEX(VERSIONS_XID) AS VERSIONS_XID, ")
          .AppendJoin(", ", columns.Select(Column)).Append('\n')
          .Append("FROM ").Append(RowDmlBuilder.QualifiedName(table)).Append(" VERSIONS BETWEEN ").Append(range).Append(" AND MAXVALUE\n")
          .Append("WHERE ").AppendJoin(" AND ", conditions).Append('\n')
          .Append("ORDER BY VERSIONS_STARTSCN DESC NULLS LAST");
        return sb.ToString();
    }

    private static bool IsRowId(string name) => string.Equals(name, RowDmlBuilder.RowIdKey, StringComparison.OrdinalIgnoreCase);

    private static string Column(string name) => IsRowId(name) ? RowDmlBuilder.RowIdKey : RowDmlBuilder.QuoteIdentifier(name);

    private static string OneLine(string text) => text.ReplaceLineEndings(" ");

    /// <summary>키 값 리터럴: 문자 = 따옴표 이스케이프, 날짜·타임스탬프 = TO_DATE·TO_TIMESTAMP, ROWID = CHARTOROWID, 숫자 = 그대로.</summary>
    internal static string Literal(object value, bool rowId)
    {
        var inv = CultureInfo.InvariantCulture;
        return value switch
        {
            string s when rowId => $"CHARTOROWID({Quote(s)})",
            string s => Quote(s),
            decimal d => d.ToString(inv),
            int or long or short or byte or sbyte or ushort or uint or ulong => Convert.ToString(value, inv)!,
            double or float => Convert.ToDouble(value, inv).ToString("R", inv),
            DateTime dt => dt.Ticks % TimeSpan.TicksPerSecond == 0
                ? $"TO_DATE('{dt.ToString("yyyy-MM-dd HH:mm:ss", inv)}', 'YYYY-MM-DD HH24:MI:SS')"
                : $"TO_TIMESTAMP('{dt.ToString("yyyy-MM-dd HH:mm:ss.ffffff", inv)}', 'YYYY-MM-DD HH24:MI:SS.FF6')",
            DateTimeOffset o => $"TO_TIMESTAMP_TZ('{o.ToString("yyyy-MM-dd HH:mm:ss.ffffff zzz", inv)}', 'YYYY-MM-DD HH24:MI:SS.FF6 TZH:TZM')",
            byte[] b => $"HEXTORAW('{Convert.ToHexString(b)}')",
            _ => throw new NotSupportedException($"리터럴로 쓸 수 없는 키 값 형식입니다: {value.GetType().Name}"),
        };
    }

    private static string Quote(string s) => "'" + s.Replace("'", "''", StringComparison.Ordinal) + "'";
}
