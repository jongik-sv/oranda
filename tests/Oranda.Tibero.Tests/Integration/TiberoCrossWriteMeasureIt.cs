using System.Data;
using System.Data.Common;
using System.Globalization;
using Oranda.Core.Execution;
using Oranda.Core.Sessions;
using Oranda.Core.Transfer.Copy.CrossDbms;
using Oranda.Tibero.Driver;
using Oranda.Tibero.Execution;
using Oranda.Tibero.Sessions;
using Xunit.Abstractions;

// 측정 시험: 서버가 거부하는 것도 사실이므로 측정 단계는 던지지 않고 번호·메시지를 줄로 남긴다(표 만들기 같은 준비 단계 실패만 시험을 실패시킨다).
#pragma warning disable CA1031

namespace Oranda.Tibero.Tests.Integration;

/// <summary>
/// 교차 복사(Oracle↔Tibero)의 Tibero 쓰기 쪽 미실측 사항을 서버에서 재는 통합 시험(XC5, docs/design/cross-dbms-copy.md 4장 가정 5개 ·
/// docs/design/tibero-driver-transfer.md 5장). 결과는 <c>XC5 &lt;항목&gt;: &lt;관찰&gt;</c> 줄로 시험 출력에 남는다(서버 반응이 「오류」여도 줄만 남기고 녹색).
/// <list type="bullet">
/// <item>쓰기 시험이라 <c>ORANDA_IT_TIBERO_WRITE=1</c> 일 때만 돌고, 서버·드라이버(<c>ORANDA_IT_TIBERO_*</c>, <c>ORANDA_TIBERO_DRIVER</c>)가 없으면 아무것도 하지 않는다.</item>
/// <item>만드는 표는 모두 <c>ORT_XC5_*</c> 이고 각 시험 끝에 <c>DROP TABLE … PURGE</c> 로 지운다(실패는 무시). 한 시험이 실패해도 나머지는 독립적으로 돈다.</item>
/// <item>실행: <c>ORANDA_IT_TIBERO_WRITE=1 dotnet test tests/Oranda.Tibero.Tests --filter "FullyQualifiedName~TiberoCrossWriteMeasureIt" --logger "console;verbosity=detailed"</c></item>
/// <item>접속 정보(주소·계정·비밀번호)는 어디에도 찍지 않는다.</item>
/// </list>
/// </summary>
[Trait("Category", "Integration")]
public class TiberoCrossWriteMeasureIt(ITestOutputHelper output)
{
    private const string TzType = "TIMESTAMP WITH TIME ZONE";
    private const string LtzType = "TIMESTAMP WITH LOCAL TIME ZONE";
    private const string TzFormatOffset = "YYYY-MM-DD HH24:MI:SS.FF9 TZH:TZM";
    private const string TzFormatRegion = "YYYY-MM-DD HH24:MI:SS.FF9 TZR";
    private const string TsFormat = "YYYY-MM-DD HH24:MI:SS.FF9";

    private static readonly TiberoBulkValueCodec Codec = TiberoBulkValueCodec.Instance;

    private static readonly IReadOnlyDictionary<string, BindValue> NoBinds = new Dictionary<string, BindValue>();

    /// <summary>CrossBindText 가 만드는 모양의 오프셋 글자(양수·음수 분·0·-00:30·인도). 마지막은 NULL.</summary>
    private static readonly TzCase[] OffsetCases =
    [
        new("plus0900", "2026-11-10 09:00:00.123456789 +09:00"),
        new("minus0330", "2026-11-10 09:00:00.123456789 -03:30"),
        new("zero", "2026-11-10 09:00:00.123456789 +00:00"),
        new("minus0030", "2026-11-10 09:00:00.500000000 -00:30"),
        new("plus0530", "2026-11-10 09:00:00.000000001 +05:30"),
        new("null", null),
    ];

    /// <summary>지역 이름 글자(TZR 서식). 마지막 둘은 오프셋 글자를 TZR 서식에 넣는 경우와 없는 지역이다.</summary>
    private static readonly TzCase[] RegionCases =
    [
        new("seoul", "2026-11-10 09:00:00.123456789 Asia/Seoul"),
        new("newyork_summer", "2026-07-10 09:00:00.123456789 America/New_York"),
        new("newyork_winter", "2026-11-10 09:00:00.123456789 America/New_York"),
        new("utc_name", "2026-11-10 09:00:00.123456789 UTC"),
        new("offset_text_with_tzr", "2026-11-10 09:00:00.123456789 +09:00"),
        new("bad_region", "2026-11-10 09:00:00.123456789 Mars/Base"),
    ];

    // ---------------------------------------------------------------------------------------------------------------
    // 1. TZ 쓰기 (D7)
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// 항목 1 — TIMESTAMP WITH TIME ZONE 열에 CrossBindText 모양 글자를 <c>TO_TIMESTAMP_TZ(:C1, 'YYYY-MM-DD HH24:MI:SS.FF9 TZH:TZM')</c> 로 배열·스칼라 바인드해
    /// 넣고, 되읽은 벽시계·오프셋·<c>TZR</c> 표시·<c>SYS_EXTRACT_UTC</c> 를 입력과 비교한다. 지역 이름(<c>Asia/Seoul</c> 등)은 <c>TZR</c> 서식으로 넣어 지역이 남는지 본다.
    /// </summary>
    private static readonly int[] LtzIds = [10, 11, 12, 13, 14, 20, 21, 22, 23, 24];

    private static readonly string[] CharColumns = ["V", "C", "NV"];

    [TiberoWriteFact]
    public Task Tz_write_by_array_and_scalar_bind_keeps_wall_clock_and_offset() => WithSessionAsync(async (_, s, tables) =>
    {
        var t = tables[0];
        await ExecAsync(s, $"CREATE TABLE {t} (ID NUMBER PRIMARY KEY, SRC VARCHAR2(20), TZ TIMESTAMP(9) WITH TIME ZONE)");
        Note("tz.write.expression", TiberoBulkValueCodec.CrossBindExpression("C1", TzType));

        // CrossBindText 가 TiberoBulkTimestampTz 에서 만드는 글자가 위 입력 글자와 같은가(코덱 쪽 바인드 글자 확인).
        foreach (var c in OffsetCases)
        {
            if (c.Text is null)
            {
                continue;
            }

            try
            {
                var made = TiberoBulkValueCodec.CrossBindText(BulkTzOf(c.Text), TzType);
                Note("tz.write.crossbindtext[" + c.Label + "]", string.Equals(made, c.Text, StringComparison.Ordinal) ? "입력 글자와 같음 " + made : "입력 " + c.Text + " ≠ 코덱 " + made);
            }
            catch (Exception ex)
            {
                Note("tz.write.crossbindtext[" + c.Label + "]", "ERR " + Describe(ex));
            }
        }

        var rows = new List<TzRow>();
        await InsertTzAsync(s, t, "array", true, OffsetCases, 1, false, rows);
        await InsertTzAsync(s, t, "scalar", false, OffsetCases, 101, false, rows);
        await InsertTzAsync(s, t, "array", true, RegionCases[..4], 201, true, rows);
        await InsertTzAsync(s, t, "scalar", false, RegionCases, 301, true, rows);

        var shown = await TryMapAsync(s, $"SELECT ID, TO_CHAR(TZ, '{TzFormatOffset}'), TO_CHAR(TZ, '{TzFormatRegion}') FROM {t} ORDER BY ID");
        var utc = await TryMapAsync(s, $"SELECT ID, TO_CHAR(SYS_EXTRACT_UTC(TZ), '{TsFormat}') FROM {t} ORDER BY ID");
        var parts = await TryMapAsync(s,
            $"SELECT ID, EXTRACT(TIMEZONE_HOUR FROM TZ) || ':' || EXTRACT(TIMEZONE_MINUTE FROM TZ), EXTRACT(TIMEZONE_REGION FROM TZ) FROM {t} ORDER BY ID");
        var dump = await TryMapAsync(s, $"SELECT ID, DUMP(TZ, 16) FROM {t} ORDER BY ID");

        foreach (var row in rows)
        {
            var disp = Cell(shown, row.Id, 0);
            var tzr = Cell(shown, row.Id, 1);
            string verdict;
            if (row.Case.Text is null)
            {
                verdict = "NULL 입력";
            }
            else if (disp.StartsWith('(') || disp.StartsWith("ERR", StringComparison.Ordinal))
            {
                verdict = "되읽을 행 없음(삽입 실패)";
            }
            else if (row.Region)
            {
                var wall = disp.StartsWith(row.Case.Text[..29], StringComparison.Ordinal);
                var kept = tzr.EndsWith(row.Case.Text[30..], StringComparison.OrdinalIgnoreCase); // TZR 칸은 「시각 지역」 꼴이다
                verdict = "벽시계 " + (wall ? "일치" : "다름") + ", TZR 지역 " + (kept ? "보존됨" : "보존 안 됨(" + tzr + ")");
            }
            else
            {
                verdict = string.Equals(disp, row.Case.Text, StringComparison.Ordinal) ? "벽시계·오프셋 입력과 일치" : "입력과 다름";
            }

            Note($"tz.write.readback[id={N(row.Id)} {row.Mode}/{row.Case.Label}]",
                $"입력 '{row.Case.Text ?? "<null>"}' → TZH:TZM '{disp}' | TZR '{tzr}' | UTC '{Cell(utc, row.Id, 0)}' | 시·분/지역 '{Cell(parts, row.Id, 0)}'/'{Cell(parts, row.Id, 1)}' | DUMP '{Cell(dump, row.Id, 0)}' → {verdict}");
        }

        // 오프셋 글자: 배열 바인드와 스칼라 바인드가 같은 값을 저장하는가.
        for (var i = 0; i < OffsetCases.Length; i++)
        {
            var same = Cell(shown, 1 + i, 0) == Cell(shown, 101 + i, 0) && Cell(utc, 1 + i, 0) == Cell(utc, 101 + i, 0);
            Note("tz.write.array_vs_scalar[" + OffsetCases[i].Label + "]", same ? "배열 = 스칼라" : "배열 ≠ 스칼라 (" + Cell(shown, 1 + i, 0) + " / " + Cell(shown, 101 + i, 0) + ")");
        }
    }, "XC5_TZW");

    // ---------------------------------------------------------------------------------------------------------------
    // 2. TZ 읽기 모양
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// 항목 2 — 넣은 TZ 값을 XC4 읽기 경로(<c>TiberoBulkReadKind.TimestampTz</c> → <see cref="TiberoBulkTimestampTz"/> → <c>ToPortable</c>)로 읽어
    /// <see cref="PortableTimestampTz"/> 의 벽시계·오프셋·지역이 서버 표시와 같은지 본다. <c>-00:30</c> 은 부호를 읽는 쪽이 놓치는 것으로 알려져 있어 따로 본다.
    /// </summary>
    [TiberoWriteFact]
    public Task Tz_read_through_bulk_reader_matches_server_display() => WithSessionAsync(async (it, s, tables) =>
    {
        var t = tables[0];
        await ExecAsync(s, $"CREATE TABLE {t} (ID NUMBER PRIMARY KEY, SRC VARCHAR2(20), TZ TIMESTAMP(9) WITH TIME ZONE)");
        var rows = new List<TzRow>();
        await InsertTzAsync(s, t, "scalar", false, OffsetCases, 1, false, rows);
        await InsertTzAsync(s, t, "scalar", false, RegionCases, 101, true, rows);

        var shown = await TryMapAsync(s, $"SELECT ID, TO_CHAR(TZ, '{TzFormatOffset}'), TO_CHAR(TZ, '{TzFormatRegion}') FROM {t} ORDER BY ID");
        var service = NewService(it);
        List<object?[]> read;
        try
        {
            read = await StreamAllAsync(service, s, $"SELECT ID, TZ FROM {t} ORDER BY ID");
        }
        catch (Exception ex)
        {
            Note("tz.read.stream", "ERR " + Describe(ex));
            return;
        }

        Note("tz.read.stream", "OK " + N(read.Count) + "행 읽음");
        foreach (var row in read)
        {
            var id = ((TiberoBulkNumber)row[0]!).Text;
            var disp = Cell(shown, int.Parse(id, CultureInfo.InvariantCulture), 0);
            var tzr = Cell(shown, int.Parse(id, CultureInfo.InvariantCulture), 1);
            string line;
            try
            {
                var portable = Codec.ToPortable(row[1], TzType);
                if (portable is PortableTimestampTz p)
                {
                    var mine = Wall(p) + " " + p.Offset;
                    var raw = (TiberoBulkTimestampTz)row[1]!;
                    var back = RoundTripText(p);
                    line = $"읽은 값 UTC {raw.Utc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}.{raw.Nanosecond.ToString("D9", CultureInfo.InvariantCulture)} 오프셋 {raw.Offset.ToString("c", CultureInfo.InvariantCulture)}"
                           + $" → 이식 값 '{mine}' region={p.Region ?? "null"} | 서버 TZH:TZM '{disp}' TZR '{tzr}' → {(string.Equals(mine, disp, StringComparison.Ordinal) ? "일치" : "다름")}"
                           + $" | FromPortable→CrossBindText '{back}' → {(string.Equals(back, disp, StringComparison.Ordinal) ? "서버 표시와 일치" : "서버 표시와 다름")}";
                }
                else
                {
                    line = portable is null ? "NULL → 이식 값 null" : "이식 값 형식 예상 밖: " + portable.GetType().Name;
                }
            }
            catch (Exception ex)
            {
                line = "ToPortable ERR " + Describe(ex);
            }

            Note($"tz.read.portable[id={id}]", line);
        }
    }, "XC5_TZR");

    // ---------------------------------------------------------------------------------------------------------------
    // 3. LTZ (D8)
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// 항목 3 — TIMESTAMP WITH LOCAL TIME ZONE 열에 <c>FROM_TZ(TO_TIMESTAMP(:C1, …), DBTIMEZONE)</c>(형식표 후보 A)와 세션 시간대 기준 후보(B: 그대로 <c>TO_TIMESTAMP</c>,
    /// C: <c>FROM_TZ(…, SESSIONTIMEZONE)</c>)로 같은 벽시계 글자를 넣고, 세션 <c>TIME_ZONE</c> 을 바꿔 가며 되읽어 값이 어떻게 변하는지 본다.
    /// </summary>
    [TiberoWriteFact]
    public Task Ltz_stored_value_depends_on_session_and_database_time_zone() => WithSessionAsync(async (it, s, tables) =>
    {
        var t = tables[0];
        var create = await TryTextAsync(s, $"CREATE TABLE {t} (ID NUMBER PRIMARY KEY, TAG VARCHAR2(40), TL TIMESTAMP(9) WITH LOCAL TIME ZONE)");
        Note("ltz.create", create);
        if (!create.StartsWith("OK", StringComparison.Ordinal))
        {
            return;
        }

        var dbTz = await TryShowAsync(s, "SELECT DBTIMEZONE FROM DUAL");
        Note("ltz.dbtimezone", dbTz);
        Note("ltz.sessiontimezone(initial)", await TryShowAsync(s, "SELECT SESSIONTIMEZONE FROM DUAL"));

        const string text = "2026-11-10 09:00:00.123456789";
        var exprA = TiberoBulkTypes.ShapeOf(LtzType).Wrap("C1");
        var exprB = $"TO_TIMESTAMP(:C1, '{TsFormat}')";
        var exprC = $"FROM_TZ(TO_TIMESTAMP(:C1, '{TsFormat}'), SESSIONTIMEZONE)";
        Note("ltz.write.expressions", $"A={exprA} | B={exprB} | C={exprC}");

        async Task InsertPhaseAsync(string zone, int baseId)
        {
            Note("ltz.session_time_zone(write " + zone + ")", await TryTextAsync(s, $"ALTER SESSION SET TIME_ZONE = '{zone}'"));
            var sqlA = $"INSERT INTO {t} (ID, TAG, TL) VALUES (:C0, 'A@{zone}', {exprA})";
            int[] pairIds = [baseId, baseId + 4];
            string?[] pairTexts = [text, text];
            Note($"ltz.write.A.array({zone})", await TryArrayInsertAsync(s, sqlA, pairIds, pairTexts));
            Note($"ltz.write.A.scalar({zone})", await TryScalarInsertAsync(s, sqlA, baseId + 1, text));
            Note($"ltz.write.B.scalar({zone})", await TryScalarInsertAsync(s, $"INSERT INTO {t} (ID, TAG, TL) VALUES (:C0, 'B@{zone}', {exprB})", baseId + 2, text));
            Note($"ltz.write.C.scalar({zone})", await TryScalarInsertAsync(s, $"INSERT INTO {t} (ID, TAG, TL) VALUES (:C0, 'C@{zone}', {exprC})", baseId + 3, text));
        }

        await InsertPhaseAsync("+09:00", 10);
        await InsertPhaseAsync("-05:00", 20);

        // 세션 시간대를 바꿔 가며 같은 행을 읽는다.
        string[] zones = ["+09:00", "-05:00", "+00:00", "Asia/Seoul"];
        var seen = new Dictionary<string, (Dictionary<string, string?[]> Map, string? Error)>(StringComparer.Ordinal);
        var asTz = new Dictionary<string, (Dictionary<string, string?[]> Map, string? Error)>(StringComparer.Ordinal);
        var service = NewService(it);
        foreach (var zone in zones)
        {
            var setResult = await TryTextAsync(s, $"ALTER SESSION SET TIME_ZONE = '{zone}'");
            Note("ltz.session_time_zone(read " + zone + ")", setResult);
            Note("ltz.sessiontimezone(read " + zone + ")", await TryShowAsync(s, "SELECT SESSIONTIMEZONE FROM DUAL"));
            seen[zone] = await TryMapAsync(s, $"SELECT ID, TO_CHAR(TL, '{TsFormat}') FROM {t} ORDER BY ID");
            asTz[zone] = await TryMapAsync(s, $"SELECT ID, TO_CHAR(CAST(TL AS TIMESTAMP(9) WITH TIME ZONE), '{TzFormatOffset}') FROM {t} ORDER BY ID");
            try
            {
                var raw = await StreamAllAsync(service, s, $"SELECT ID, TL FROM {t} ORDER BY ID");
                Note("ltz.raw_bytes(read " + zone + ")", string.Join(" / ", raw.Select(r =>
                    ((TiberoBulkNumber)r[0]!).Text + "=" + (r[1] is TiberoBulkTimestampLtz l
                        ? l.DbTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "." + l.Nanosecond.ToString("D9", CultureInfo.InvariantCulture)
                        : "<null>"))));
            }
            catch (Exception ex)
            {
                Note("ltz.raw_bytes(read " + zone + ")", "ERR " + Describe(ex));
            }
        }

        foreach (var id in LtzIds)
        {
            var tag = Cell(await TryMapAsync(s, $"SELECT ID, TAG FROM {t} WHERE ID = {N(id)}"), id, 0);
            Note($"ltz.readback[id={N(id)} {tag}]",
                string.Join(" | ", zones.Select(z => $"세션 {z}: {Cell(seen[z], id, 0)} (TZ로 {Cell(asTz[z], id, 0)})")));
        }

        // 요약: 세션 시간대를 바꿨을 때 표시값이 변하는가, 후보 A·B 가 입력 벽시계를 어떻게 저장하는가.
        var a09 = Cell(seen["+09:00"], 11, 0);
        var a05 = Cell(seen["-05:00"], 11, 0);
        var b09 = Cell(seen["+09:00"], 13, 0);
        var b05 = Cell(seen["-05:00"], 23, 0);
        var changes = !string.Equals(a09, a05, StringComparison.Ordinal);
        var summary = $"DBTIMEZONE={dbTz}; 같은 행(id=11)을 세션 +09:00 → -05:00 로 읽으면 {a09} → {a05} ("
            + (changes ? "읽는 세션 시간대에 따라 표시값이 바뀐다" : "읽는 세션 시간대와 무관하게 표시값이 같다") + "). "
            + $"입력 벽시계 09:00:00 이 +09:00 세션에서 읽혀 A(DBTIMEZONE 기준)={a09}, B(세션 시간대 기준, +09:00 세션에서 쓴 id=13)={b09}, "
            + $"-05:00 세션에서 쓴 B(id=23)를 -05:00 세션에서 읽으면 {b05}. "
            + (changes
                ? "의미: LTZ 는 절대 시각을 저장하고 읽는 세션 시간대 벽시계로 보여 준다 → 이식은 원본 절대 시각을 기준으로 정하고, 쓰기는 입력 벽시계가 어느 시간대인지 명시(FROM_TZ)해야 한다."
                : "의미: 표시값이 세션 시간대와 무관하면 DB 시간대 벽시계로 저장된 값을 그대로 보여 주는 것이다 → 이식은 벽시계 글자를 그대로 넣으면 된다.");
        Note("ltz.summary", summary);
        await TryTextAsync(s, "ALTER SESSION SET TIME_ZONE = LOCAL");
    }, "XC5_LTZ");

    // ---------------------------------------------------------------------------------------------------------------
    // 4. NUMBER → BINARY_FLOAT / BINARY_DOUBLE (D6)
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// 항목 4 — BINARY_DOUBLE·BINARY_FLOAT 열에 NUMBER 글자를 <c>:C1</c>(Varchar2 종류 글자 바인드, 감싸지 않음)로 INSERT 했을 때 서버가 암묵 변환하는지, 되읽은 값의 정밀도,
    /// 거절되는 값을 본다. 비교용으로 <c>TO_BINARY_DOUBLE(:C1)</c>·<c>TO_BINARY_FLOAT(:C1)</c> 로 감싼 쓰기와 배열 바인드도 같이 잰다.
    /// </summary>
    [TiberoWriteFact]
    public Task Number_text_into_binary_float_and_double_columns() => WithSessionAsync(async (it, s, tables) =>
    {
        var t = tables[0];
        var create = await TryTextAsync(s, $"CREATE TABLE {t} (ID NUMBER PRIMARY KEY, BD BINARY_DOUBLE, BF BINARY_FLOAT)");
        Note("number_to_binary.create", create);
        if (!create.StartsWith("OK", StringComparison.Ordinal))
        {
            return;
        }

        string[] texts =
        [
            "0.1",
            "15000000000",
            "-123.456",
            "1" + new string('0', 125),
            "123456789.123456789",
            "0." + new string('0', 41) + "1",
            "34028235" + new string('0', 31),
        ];

        // 코덱이 float·double 에서 만드는 NUMBER 글자(교차 쓰기 바인드).
        foreach (var d in new[] { 0.1, 1.5e10, -123.456, 1e125 })
        {
            Note("number_to_binary.crossbindtext(double " + d.ToString("R", CultureInfo.InvariantCulture) + ")", TryBindText(d, "BINARY_DOUBLE"));
        }

        Note("number_to_binary.crossbindtext(float 0.1f)", TryBindText(0.1f, "BINARY_FLOAT"));
        Note("number_to_binary.crossbindtext(double NaN)", TryBindText(double.NaN, "BINARY_DOUBLE"));
        Note("number_to_binary.expression", TiberoBulkValueCodec.CrossBindExpression("C1", "BINARY_DOUBLE"));

        var results = new Dictionary<int, string>();
        for (var i = 0; i < texts.Length; i++)
        {
            results[1000 + i] = await TryScalarInsertAsync(s, $"INSERT INTO {t} (ID, BD) VALUES (:C0, :C1)", 1000 + i, texts[i]);
            results[2000 + i] = await TryScalarInsertAsync(s, $"INSERT INTO {t} (ID, BD) VALUES (:C0, TO_BINARY_DOUBLE(:C1))", 2000 + i, texts[i]);
            results[3000 + i] = await TryScalarInsertAsync(s, $"INSERT INTO {t} (ID, BF) VALUES (:C0, :C1)", 3000 + i, texts[i]);
            results[4000 + i] = await TryScalarInsertAsync(s, $"INSERT INTO {t} (ID, BF) VALUES (:C0, TO_BINARY_FLOAT(:C1))", 4000 + i, texts[i]);
        }

        var ids5 = Enumerable.Range(0, texts.Length).Select(i => 5000 + i).ToArray();
        var ids6 = Enumerable.Range(0, texts.Length).Select(i => 6000 + i).ToArray();
        var arrayBd = await TryArrayInsertAsync(s, $"INSERT INTO {t} (ID, BD) VALUES (:C0, :C1)", ids5, texts);
        var arrayBf = await TryArrayInsertAsync(s, $"INSERT INTO {t} (ID, BF) VALUES (:C0, :C1)", ids6, texts);
        Note("number_to_binary.array.double", arrayBd);
        Note("number_to_binary.array.float", arrayBf);

        var shown = await TryMapAsync(s, $"SELECT ID, TO_CHAR(BD), TO_CHAR(BF) FROM {t} ORDER BY ID");
        var raw = new Dictionary<int, (double? Bd, float? Bf)>();
        string? streamError = null;
        try
        {
            foreach (var row in await StreamAllAsync(NewService(it), s, $"SELECT ID, BD, BF FROM {t} ORDER BY ID"))
            {
                raw[int.Parse(((TiberoBulkNumber)row[0]!).Text, CultureInfo.InvariantCulture)] = (row[1] as double?, row[2] as float?);
            }
        }
        catch (Exception ex)
        {
            streamError = Describe(ex);
            Note("number_to_binary.stream", "ERR " + streamError);
        }

        string DoubleOf(int id, string expected)
        {
            if (streamError is not null)
            {
                return "읽기 ERR";
            }

            if (!raw.TryGetValue(id, out var v) || v.Bd is not { } d)
            {
                return "(값 없음)";
            }

            return "R=" + d.ToString("R", CultureInfo.InvariantCulture) + " TO_CHAR=" + Cell(shown, id, 0) + " parse "
                   + (d == double.Parse(expected, NumberStyles.Float, CultureInfo.InvariantCulture) ? "일치" : "다름");
        }

        string FloatOf(int id, string expected)
        {
            if (streamError is not null)
            {
                return "읽기 ERR";
            }

            if (!raw.TryGetValue(id, out var v) || v.Bf is not { } f)
            {
                return "(값 없음)";
            }

            return "R=" + f.ToString("R", CultureInfo.InvariantCulture) + " TO_CHAR=" + Cell(shown, id, 1) + " parse "
                   + (f == float.Parse(expected, NumberStyles.Float, CultureInfo.InvariantCulture) ? "일치" : "다름");
        }

        for (var i = 0; i < texts.Length; i++)
        {
            var label = texts[i].Length <= 24 ? texts[i] : texts[i][..12] + "…(" + N(texts[i].Length) + "자)";
            Note($"number_to_binary.double[{label}]",
                $"암묵={results[1000 + i]} → {DoubleOf(1000 + i, texts[i])} | TO_BINARY_DOUBLE={results[2000 + i]} → {DoubleOf(2000 + i, texts[i])} | 배열 → {DoubleOf(5000 + i, texts[i])}");
            Note($"number_to_binary.float[{label}]",
                $"암묵={results[3000 + i]} → {FloatOf(3000 + i, texts[i])} | TO_BINARY_FLOAT={results[4000 + i]} → {FloatOf(4000 + i, texts[i])} | 배열 → {FloatOf(6000 + i, texts[i])}");
        }
    }, "XC5_NB");

    // ---------------------------------------------------------------------------------------------------------------
    // 5. CP949 밖 글자 (D13)
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// 항목 5 — VARCHAR2·CHAR·NVARCHAR2 열에 CP949 로 표현하기 어려운 글자(€, 😀, 한자 일부, ꙮ, 태국어 …)를 바인드(Varchar2·NVarchar2 종류)로 넣고 읽는다.
    /// 조용히 <c>?</c> 가 되는지 오류인지, NVARCHAR2 는 보존되는지를 입력·읽은 글자의 코드 포인트와 <c>LENGTH</c>·<c>LENGTHB</c>·<c>DUMP</c> 로 남긴다. VARCHAR2 는 리터럴 INSERT 도 같이 본다.
    /// </summary>
    [TiberoWriteFact]
    public Task Characters_outside_cp949_into_varchar2_char_and_nvarchar2() => WithSessionAsync(async (_, s, tables) =>
    {
        var t = tables[0];
        await ExecAsync(s, $"CREATE TABLE {t} (ID NUMBER PRIMARY KEY, V VARCHAR2(60), C CHAR(20), NV NVARCHAR2(30))");
        Note("cp949.charset", await TryShowAsync(s,
            "SELECT PARAMETER, VALUE FROM NLS_DATABASE_PARAMETERS WHERE PARAMETER IN ('NLS_CHARACTERSET', 'NLS_NCHAR_CHARACTERSET')"));

        (string Label, string Text)[] cases =
        [
            ("hangul_ascii", "한글ABC"),
            ("euro", "€"),
            ("emoji", "\U0001F600"),
            ("hanja_rare", "龘"),
            ("cyrillic_ext", "ꙮ"),
            ("thai", "สวัสดี"),
            ("latin_accent", "é"),
            ("sharp_s", "ẞ"),
            ("mixed", "가€나\U0001F600다"),
        ];

        var plan = new List<(int Id, string Column, string Mode, string Label, string Text)>();
        var inserted = new Dictionary<int, string>();
        for (var i = 0; i < cases.Length; i++)
        {
            var (label, text) = cases[i];
            var literal = text.Replace("'", "''", StringComparison.Ordinal);
            var specs = new (int Id, string Column, string Mode, TiberoParamKind Kind)[]
            {
                (i * 10 + 1, "V", "bind", TiberoParamKind.Varchar2),
                (i * 10 + 2, "C", "bind", TiberoParamKind.Varchar2),
                (i * 10 + 3, "NV", "bind", TiberoParamKind.NVarchar2),
            };
            foreach (var (id, column, mode, kind) in specs)
            {
                plan.Add((id, column, mode, label, text));
                inserted[id] = await TryTextAsync(s, $"INSERT INTO {t} (ID, {column}) VALUES (:C0, :C1)", cmd =>
                {
                    AddParam(s, cmd, "C0", TiberoParamKind.Decimal, (decimal)id);
                    AddParam(s, cmd, "C1", kind, text);
                });
            }

            var literalId = i * 10 + 4;
            plan.Add((literalId, "V", "literal", label, text));
            inserted[literalId] = await TryTextAsync(s, $"INSERT INTO {t} (ID, V) VALUES ({N(literalId)}, '{literal}')");
        }

        var perColumn = new Dictionary<string, (Dictionary<string, string?[]> Map, string? Error)>(StringComparer.Ordinal);
        var perDump = new Dictionary<string, (Dictionary<string, string?[]> Map, string? Error)>(StringComparer.Ordinal);
        foreach (var column in CharColumns)
        {
            perColumn[column] = await TryMapAsync(s, $"SELECT ID, {column}, LENGTH({column}), LENGTHB({column}) FROM {t} WHERE {column} IS NOT NULL ORDER BY ID");
            var dump = await TryMapAsync(s, $"SELECT ID, DUMP({column}, 16) FROM {t} WHERE {column} IS NOT NULL ORDER BY ID");
            perDump[column] = dump.Error is null ? dump : await TryMapAsync(s, $"SELECT ID, DUMP({column}) FROM {t} WHERE {column} IS NOT NULL ORDER BY ID");
        }

        foreach (var (id, column, mode, label, text) in plan)
        {
            var item = $"cp949.{ColumnItem(column)}.{mode}[{label}]";
            var result = inserted[id];
            if (result.StartsWith("ERR", StringComparison.Ordinal))
            {
                Note(item, $"입력 {CodePoints(text)} → 삽입 {result}");
                continue;
            }

            var (map, error) = perColumn[column];
            if (error is not null || !map.TryGetValue(N(id), out var cells))
            {
                Note(item, $"입력 {CodePoints(text)} → 삽입 {result}; 되읽기 {(error is null ? "행 없음" : "ERR " + Cut(error, 120))}");
                continue;
            }

            var read = cells[0] ?? string.Empty;
            string status;
            if (string.Equals(read.TrimEnd(' '), text, StringComparison.Ordinal))
            {
                status = "보존";
            }
            else if (read.Contains('?') || read.Contains('�'))
            {
                status = "? 로 대체됨";
            }
            else
            {
                status = "다른 글자로 바뀜";
            }

            Note(item, $"입력 {CodePoints(text)} → 삽입 {result}; 읽음 {CodePoints(read.TrimEnd(' '))} ({status}); LENGTH={cells[1]}, LENGTHB={cells[2]}; DUMP={Cell(perDump[column], id, 0)}");
        }
    }, "XC5_CP");

    // ---------------------------------------------------------------------------------------------------------------
    // 6. DDL (D19)
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// 항목 6 — CREATE TABLE/CREATE INDEX 로 되는지(D19): <c>VARCHAR2(10 CHAR)</c> 와 BYTE 의 길이 의미(한글 값), <c>DEFAULT SYSTIMESTAMP</c>·<c>DEFAULT SYS_GUID()</c>·<c>DEFAULT 'x'</c>,
    /// <c>CHECK (col &gt; 0)</c>, 일반·UNIQUE·함수 기반(UPPER) 인덱스, <c>COMMENT ON TABLE/COLUMN</c>. 각각 성공/오류(번호·메시지 앞부분)를 줄로 남긴다.
    /// </summary>
    [TiberoWriteFact]
    public Task Ddl_features_for_create_table_and_create_index() =>
        WithSessionAsync(async (_, s, tables) =>
        {
            var (d1, d2a, d2b, d2c, d3, d4) = (tables[0], tables[1], tables[2], tables[3], tables[4], tables[5]);

            // 글자 길이 의미
            var create1 = await TryTextAsync(s, $"CREATE TABLE {d1} (V10C VARCHAR2(10 CHAR), V10B VARCHAR2(10 BYTE), C5C CHAR(5 CHAR), C5B CHAR(5 BYTE))");
            Note("ddl.char_semantics.create", create1);
            if (create1.StartsWith("OK", StringComparison.Ordinal))
            {
                Note("ddl.char_semantics.columns", await TryShowAsync(s,
                    $"SELECT COLUMN_NAME, DATA_LENGTH, CHAR_LENGTH, CHAR_USED FROM USER_TAB_COLUMNS WHERE TABLE_NAME = '{d1}' ORDER BY COLUMN_ID"));
                (string Column, string Value, string Label)[] values =
                [
                    ("V10C", "가나다라마바사아자차", "VARCHAR2(10 CHAR) 에 한글 10자"),
                    ("V10B", "가나다라마", "VARCHAR2(10 BYTE) 에 한글 5자"),
                    ("V10B", "가나다라마바", "VARCHAR2(10 BYTE) 에 한글 6자"),
                    ("C5C", "가나다라마", "CHAR(5 CHAR) 에 한글 5자"),
                    ("C5B", "가나다", "CHAR(5 BYTE) 에 한글 3자"),
                ];
                foreach (var (column, value, label) in values)
                {
                    var insert = await TryTextAsync(s, $"INSERT INTO {d1} ({column}) VALUES ('{value}')");
                    var lengths = insert.StartsWith("OK", StringComparison.Ordinal)
                        ? await TryShowAsync(s, $"SELECT LENGTH({column}), LENGTHB({column}) FROM {d1} WHERE {column} IS NOT NULL AND ROWNUM = 1")
                        : "-";
                    Note("ddl.char_semantics.insert[" + label + "]", insert + "; LENGTH|LENGTHB " + lengths);
                    await TryTextAsync(s, $"DELETE FROM {d1}");
                }
            }

            // 기본값
            await DefaultCaseAsync(s, "systimestamp", d2a, "TS TIMESTAMP DEFAULT SYSTIMESTAMP", "TS", "CASE WHEN TS IS NULL THEN 'NULL' ELSE TO_CHAR(TS, 'YYYY') END");
            await DefaultCaseAsync(s, "sys_guid", d2b, "G RAW(16) DEFAULT SYS_GUID()", "G", "LENGTHB(G) || ' bytes'");
            await DefaultCaseAsync(s, "literal_x", d2c, "X VARCHAR2(10) DEFAULT 'x', Y NUMBER DEFAULT 0 NOT NULL", "X", "X || '/' || TO_CHAR(Y)");

            // CHECK
            var create3 = await TryTextAsync(s, $"CREATE TABLE {d3} (N NUMBER CHECK (N > 0), M NUMBER, CONSTRAINT {TiberoItSessions.ObjectName("XC5_CK1")} CHECK (M < 100))");
            Note("ddl.check.create", create3);
            if (create3.StartsWith("OK", StringComparison.Ordinal))
            {
                Note("ddl.check.insert(N=1)", await TryTextAsync(s, $"INSERT INTO {d3} (N, M) VALUES (1, 1)"));
                Note("ddl.check.insert(N=-1 위반)", await TryTextAsync(s, $"INSERT INTO {d3} (N, M) VALUES (-1, 1)"));
                Note("ddl.check.insert(M=100 위반)", await TryTextAsync(s, $"INSERT INTO {d3} (N, M) VALUES (1, 100)"));
                Note("ddl.check.catalog", await TryShowAsync(s,
                    $"SELECT CONSTRAINT_NAME, CONSTRAINT_TYPE, SEARCH_CONDITION FROM USER_CONSTRAINTS WHERE TABLE_NAME = '{d3}' AND CONSTRAINT_TYPE = 'C' ORDER BY CONSTRAINT_NAME"));
            }

            // 인덱스·주석
            var create4 = await TryTextAsync(s, $"CREATE TABLE {d4} (ID NUMBER, A VARCHAR2(20), B VARCHAR2(20))");
            Note("ddl.index.base_table", create4);
            if (create4.StartsWith("OK", StringComparison.Ordinal))
            {
                var i1 = TiberoItSessions.ObjectName("XC5_I1");
                var i2 = TiberoItSessions.ObjectName("XC5_I2");
                var i3 = TiberoItSessions.ObjectName("XC5_I3");
                var i4 = TiberoItSessions.ObjectName("XC5_I4");
                Note("ddl.index.plain", await TryTextAsync(s, $"CREATE INDEX {i1} ON {d4} (A)"));
                Note("ddl.index.unique", await TryTextAsync(s, $"CREATE UNIQUE INDEX {i2} ON {d4} (B)"));
                Note("ddl.index.function_based(UPPER)", await TryTextAsync(s, $"CREATE INDEX {i3} ON {d4} (UPPER(A))"));
                Note("ddl.index.composite_desc", await TryTextAsync(s, $"CREATE INDEX {i4} ON {d4} (A DESC, B)"));
                Note("ddl.index.unique_enforced(1)", await TryTextAsync(s, $"INSERT INTO {d4} (ID, A, B) VALUES (1, 'a', 'b')"));
                Note("ddl.index.unique_enforced(2 중복)", await TryTextAsync(s, $"INSERT INTO {d4} (ID, A, B) VALUES (2, 'a2', 'b')"));
                Note("ddl.index.catalog", await TryShowAsync(s,
                    $"SELECT INDEX_NAME, UNIQUENESS, INDEX_TYPE FROM USER_INDEXES WHERE TABLE_NAME = '{d4}' ORDER BY INDEX_NAME"));
                Note("ddl.index.columns", await TryShowAsync(s,
                    $"SELECT INDEX_NAME, COLUMN_NAME, COLUMN_POSITION, DESCEND FROM USER_IND_COLUMNS WHERE TABLE_NAME = '{d4}' ORDER BY INDEX_NAME, COLUMN_POSITION"));
                Note("ddl.index.expressions", await TryShowAsync(s,
                    $"SELECT INDEX_NAME, COLUMN_EXPRESSION FROM USER_IND_EXPRESSIONS WHERE TABLE_NAME = '{d4}' ORDER BY INDEX_NAME"));

                Note("ddl.comment.table", await TryTextAsync(s, $"COMMENT ON TABLE {d4} IS '테이블 설명 it''s'"));
                Note("ddl.comment.column", await TryTextAsync(s, $"COMMENT ON COLUMN {d4}.A IS '열 설명'"));
                Note("ddl.comment.table_catalog", await TryShowAsync(s, $"SELECT COMMENTS FROM USER_TAB_COMMENTS WHERE TABLE_NAME = '{d4}'"));
                Note("ddl.comment.column_catalog", await TryShowAsync(s,
                    $"SELECT COLUMN_NAME, COMMENTS FROM USER_COL_COMMENTS WHERE TABLE_NAME = '{d4}' AND COMMENTS IS NOT NULL"));
            }
        }, "XC5_D1", "XC5_D2A", "XC5_D2B", "XC5_D2C", "XC5_D3", "XC5_D4");

    // ---------------------------------------------------------------------------------------------------------------
    // 7. 정리 · 묵시 커밋
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// 항목 7 — <c>DROP TABLE … PURGE</c> 가 되는지, 휴지통(USER_RECYCLEBIN) 잔여 여부(PURGE 없는 DROP 과 대조), CREATE TABLE 직후 DDL 묵시 커밋(앞서 넣은 행이 다른 세션에 보이고
    /// ROLLBACK 으로 안 사라지는지)을 본다.
    /// </summary>
    [TiberoWriteFact]
    public Task Drop_purge_recyclebin_residue_and_ddl_implicit_commit() => WithSessionAsync(async (it, s, tables) =>
    {
        var (p1, p2, p3) = (tables[0], tables[1], tables[2]);
        var other = await it.OpenAsync(SessionPurpose.Monitor);

        // CREATE TABLE 묵시 커밋: P1 에 행을 넣고(미커밋) 다른 표를 만든다.
        await ExecAsync(s, $"CREATE TABLE {p1} (X NUMBER)");
        await ExecAsync(s, $"INSERT INTO {p1} (X) VALUES (1)");
        var before = await TryScalarTextAsync(other, $"SELECT COUNT(*) FROM {p1}");
        var create = await TryTextAsync(s, $"CREATE TABLE {p2} (Y NUMBER)");
        var after = await TryScalarTextAsync(other, $"SELECT COUNT(*) FROM {p1}");
        await TryTextAsync(s, "ROLLBACK");
        var afterRollback = await TryScalarTextAsync(s, $"SELECT COUNT(*) FROM {p1}");
        Note("cleanup.create_table_implicit_commit",
            $"미커밋 INSERT 1행 뒤 다른 세션에서 보임 {before}행 → CREATE TABLE({create}) 뒤 다른 세션에서 보임 {after}행 → ROLLBACK 뒤 {afterRollback}행"
            + (after == "1" && afterRollback == "1" ? " → CREATE TABLE 이 앞 트랜잭션을 커밋함" : " → 묵시 커밋 아님(또는 판정 불가)"));

        // DROP TABLE … PURGE
        var purge = await TryTextAsync(s, $"DROP TABLE {p2} PURGE");
        Note("cleanup.drop_purge", purge);
        Note("cleanup.drop_purge.table_exists", await TryScalarTextAsync(s, $"SELECT COUNT(*) FROM USER_TABLES WHERE TABLE_NAME = '{p2}'"));
        Note("cleanup.drop_purge.recyclebin_rows", await TryScalarTextAsync(s, $"SELECT COUNT(*) FROM USER_RECYCLEBIN WHERE ORIGINAL_NAME = '{p2}'"));

        // PURGE 없는 DROP 은 휴지통에 남는가(인덱스 포함).
        var idx = TiberoItSessions.ObjectName("XC5_PI");
        await ExecAsync(s, $"CREATE TABLE {p3} (A NUMBER, B VARCHAR2(10))");
        Note("cleanup.recyclebin.create_index", await TryTextAsync(s, $"CREATE INDEX {idx} ON {p3} (B)"));
        Note("cleanup.recyclebin.drop_without_purge", await TryTextAsync(s, $"DROP TABLE {p3}"));
        Note("cleanup.recyclebin.after_plain_drop", await TryShowAsync(s,
            $"SELECT ORIGINAL_NAME, TYPE FROM USER_RECYCLEBIN WHERE ORIGINAL_NAME IN ('{p3}', '{idx}') ORDER BY ORIGINAL_NAME"));
        Note("cleanup.recyclebin.purge_table", await TryTextAsync(s, $"PURGE TABLE {p3}"));
        Note("cleanup.recyclebin.after_purge_table", await TryScalarTextAsync(s,
            $"SELECT COUNT(*) FROM USER_RECYCLEBIN WHERE ORIGINAL_NAME IN ('{p3}', '{idx}')"));

        // 정리에 쓰는 형태(CASCADE CONSTRAINTS PURGE)가 휴지통을 안 남기는가.
        await ExecAsync(s, $"CREATE TABLE {p3} (A NUMBER, B VARCHAR2(10))");
        Note("cleanup.cascade_purge", await TryTextAsync(s, $"DROP TABLE {p3} CASCADE CONSTRAINTS PURGE"));
        Note("cleanup.cascade_purge.recyclebin_rows", await TryScalarTextAsync(s, $"SELECT COUNT(*) FROM USER_RECYCLEBIN WHERE ORIGINAL_NAME = '{p3}'"));
        Note("cleanup.recyclebin.total_rows(now)", await TryScalarTextAsync(s, "SELECT COUNT(*) FROM USER_RECYCLEBIN"));
        Note("cleanup.residual.xc5_tables(now)", await TryShowAsync(s,
            @"SELECT TABLE_NAME FROM USER_TABLES WHERE TABLE_NAME LIKE 'ORT\_XC5\_%' ESCAPE '\' ORDER BY TABLE_NAME"));
    }, "XC5_P1", "XC5_P2", "XC5_P3");

    // ---------------------------------------------------------------------------------------------------------------
    // 도우미
    // ---------------------------------------------------------------------------------------------------------------

    private sealed record TzCase(string Label, string? Text);

    private sealed record TzRow(int Id, string Mode, TzCase Case, bool Region);

    /// <summary>시험 한 개의 틀: 쓰기 시험이 꺼졌거나 서버·드라이버가 없으면 아무것도 하지 않는다. 표를 미리 지우고 끝에 다시 지운다.</summary>
    private static async Task WithSessionAsync(Func<TiberoItSessions, TiberoSession, string[], Task> body, params string[] suffixes)
    {
        if (TiberoItSessions.SkipWrite())
        {
            return;
        }

        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        var s = await it.OpenAsync();
        var tables = suffixes.Select(x => TiberoItSessions.ObjectName(x)).ToArray();
        foreach (var table in tables)
        {
            await DropTableAsync(s, table);
        }

        try
        {
            await body(it, s, tables);
        }
        finally
        {
            foreach (var table in tables)
            {
                await DropTableAsync(s, table);
            }
        }
    }

    private void Note(string item, string text) => output.WriteLine("XC5 " + item + ": " + text);

    private static string N(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static TiberoDriver Drv(TiberoSession s) =>
        s.Driver ?? throw new InvalidOperationException("이 시험은 실제 드라이버 세션이 필요하다.");

    private static string Flat(string text) =>
        text.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal).Trim();

    private static string Cut(string text, int max) => text.Length <= max ? text : text[..max] + "...";

    /// <summary>예외를 서버 번호·메시지 앞부분(드라이버 예외가 아니면 형식 이름)으로 적는다.</summary>
    private static string Describe(Exception ex)
    {
        var error = TiberoErrorClassifier.ToDbError(ex, null);
        var head = error is null
            ? ex.GetType().Name + " " + Flat(ex.Message)
            : "number=" + N(error.Number) + " " + Flat(error.Message);
        return Cut(head, 200);
    }

    private static string CodePoints(string text) =>
        text.Length == 0 ? "(빈 글자)" : string.Join(' ', text.EnumerateRunes().Select(r => "U+" + r.Value.ToString("X4", CultureInfo.InvariantCulture)));

    private static string ColumnItem(string column) => column switch
    {
        "V" => "varchar2",
        "C" => "char",
        _ => "nvarchar2",
    };

    private static void AddParam(TiberoSession s, DbCommand cmd, string name, TiberoParamKind kind, object? value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        Drv(s).SetParameterKind(p, kind);
        p.Direction = ParameterDirection.Input;
        p.Value = value ?? DBNull.Value;
        cmd.Parameters.Add(p);
    }

    private static async Task<int> ExecAsync(TiberoSession s, string sql, Action<DbCommand>? prepare = null)
    {
        await s.CommandGate.EnterAsync(CancellationToken.None);
        try
        {
            await using var cmd = s.CreateCommand(sql);
            prepare?.Invoke(cmd);
            return await cmd.ExecuteNonQueryAsync();
        }
        finally
        {
            s.CommandGate.Release();
        }
    }

    private static async Task<bool> SilentAsync(TiberoSession s, string sql)
    {
        try
        {
            await ExecAsync(s, sql);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static async Task DropTableAsync(TiberoSession s, string table)
    {
        await SilentAsync(s, "ROLLBACK");
        if (await SilentAsync(s, $"DROP TABLE {table} CASCADE CONSTRAINTS PURGE"))
        {
            return;
        }

        await SilentAsync(s, $"DROP TABLE {table} CASCADE CONSTRAINTS");
    }

    /// <summary>문장을 보내고 「OK rows=n」 또는 「ERR number=… 메시지」를 돌려준다(던지지 않는다).</summary>
    private static async Task<string> TryTextAsync(TiberoSession s, string sql, Action<DbCommand>? prepare = null)
    {
        try
        {
            return "OK rows=" + N(await ExecAsync(s, sql, prepare));
        }
        catch (Exception ex)
        {
            return "ERR " + Describe(ex);
        }
    }

    private static Task<string> TryScalarInsertAsync(TiberoSession s, string sql, int id, string? text) =>
        TryTextAsync(s, sql, cmd =>
        {
            AddParam(s, cmd, "C0", TiberoParamKind.Decimal, (decimal)id);
            AddParam(s, cmd, "C1", TiberoParamKind.Varchar2, text);
        });

    /// <summary>글자 배열 바인드(기존 배열 쓰기와 같은 모양: ArrayBindCount + 형식 배열 <c>decimal[]</c>·<c>string?[]</c>, Varchar2 종류).</summary>
    private static async Task<string> TryArrayInsertAsync(TiberoSession s, string sql, int[] ids, string?[] texts)
    {
        try
        {
            var k = await ExecAsync(s, sql, cmd =>
            {
                Drv(s).SetArrayBindCount(cmd, ids.Length);
                AddParam(s, cmd, "C0", TiberoParamKind.Decimal, ids.Select(i => (decimal)i).ToArray());
                AddParam(s, cmd, "C1", TiberoParamKind.Varchar2, texts);
            });
            return "OK rows=" + N(k) + (k == ids.Length ? string.Empty : " (보낸 " + N(ids.Length) + "행과 다름: 오류 없이 앞 행만 들어갔을 수 있음)");
        }
        catch (Exception ex)
        {
            return "ERR " + Describe(ex);
        }
    }

    private static async Task<List<string?[]>> RowsAsync(TiberoSession s, string sql)
    {
        var table = await TiberoSessionQuery.RunAsync(s, sql);
        return [.. table.Rows.Select(r => r.Select(v => TiberoSessionQuery.Str(v)).ToArray())];
    }

    /// <summary>조회 결과를 한 줄로(행은 「 / 」, 열은 「 | 」). 실패해도 던지지 않고 「ERR …」.</summary>
    private static async Task<string> TryShowAsync(TiberoSession s, string sql)
    {
        try
        {
            var rows = await RowsAsync(s, sql);
            return rows.Count == 0
                ? "(0행)"
                : Cut(string.Join(" / ", rows.Select(r => string.Join(" | ", r.Select(v => v is null ? "<null>" : Flat(v))))), 900);
        }
        catch (Exception ex)
        {
            return "ERR " + Describe(ex);
        }
    }

    private static async Task<string> TryScalarTextAsync(TiberoSession s, string sql)
    {
        try
        {
            return TiberoSessionQuery.Str(await TiberoSessionQuery.ScalarAsync(s, sql)) ?? "<null>";
        }
        catch (Exception ex)
        {
            return "ERR " + Describe(ex);
        }
    }

    /// <summary>첫 열(보통 ID)을 키로, 나머지 열을 값으로 모은다. 조회가 실패하면 오류 글자를 함께 돌려준다.</summary>
    private static async Task<(Dictionary<string, string?[]> Map, string? Error)> TryMapAsync(TiberoSession s, string sql)
    {
        var map = new Dictionary<string, string?[]>(StringComparer.Ordinal);
        try
        {
            foreach (var row in await RowsAsync(s, sql))
            {
                map[row[0] ?? "<null>"] = row.Skip(1).ToArray();
            }

            return (map, null);
        }
        catch (Exception ex)
        {
            return (map, Describe(ex));
        }
    }

    private static string Cell((Dictionary<string, string?[]> Map, string? Error) result, int id, int column)
    {
        if (result.Error is not null)
        {
            return "ERR(" + Cut(result.Error, 100) + ")";
        }

        return result.Map.TryGetValue(N(id), out var values) && column < values.Length ? values[column] ?? "<null>" : "(행 없음)";
    }

    private static async Task<List<object?[]>> StreamAllAsync(TiberoBulkExecutionService service, TiberoSession s, string sql)
    {
        var rows = new List<object?[]>();
        await foreach (var row in service.StreamRowsAsync(s, new SqlStatement(sql, default, StatementKind.Query), NoBinds, ExecutionOptions.Default, CancellationToken.None))
        {
            rows.Add(row);
        }

        return rows;
    }

    private static TiberoBulkExecutionService NewService(TiberoItSessions it) => new(new AllowAll(), new NoQuestions(), it.Events);

    private static string TryBindText(object value, string bindType)
    {
        try
        {
            return "'" + TiberoBulkValueCodec.CrossBindText(value, bindType) + "'";
        }
        catch (Exception ex)
        {
            return "예외 " + ex.GetType().Name + " " + Cut(Flat(ex.Message), 120);
        }
    }

    /// <summary>
    /// 오프셋 TZ 글자(<c>yyyy-MM-dd HH:mm:ss.fffffffff ±HH:MM</c>) → <see cref="TiberoBulkTimestampTz"/>(UTC = 벽시계 − 오프셋). 코덱의 <c>CrossBindText</c> 가
    /// 같은 글자를 다시 만드는지 보려는 입력 만들기다.
    /// </summary>
    private static TiberoBulkTimestampTz BulkTzOf(string text)
    {
        var local = DateTime.ParseExact(text[..19], "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        var nanos = int.Parse(text.AsSpan(20, 9), NumberStyles.None, CultureInfo.InvariantCulture);
        if (!PortableTimestampTz.TryParseOffsetMinutes(text[30..], out var minutes))
        {
            throw new FormatException("오프셋을 읽을 수 없습니다: " + text[30..]);
        }

        var offset = TimeSpan.FromMinutes(minutes);
        return new TiberoBulkTimestampTz(local - offset, nanos, offset);
    }

    private static string Wall(PortableTimestampTz p) =>
        p.Local.Seconds.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "." + p.Local.Nanos.ToString("D9", CultureInfo.InvariantCulture);

    /// <summary>이식 값 → <c>FromPortable</c> → <c>CrossBindText</c>(쓰기 글자)가 되돌리는 글자. 못 만들면 「ERR …」.</summary>
    private static string RoundTripText(PortableTimestampTz portable)
    {
        try
        {
            return TiberoBulkValueCodec.CrossBindText(Codec.FromPortable(portable, TzType), TzType) ?? "<null>";
        }
        catch (Exception ex)
        {
            return "ERR " + ex.GetType().Name + " " + Cut(Flat(ex.Message), 100);
        }
    }

    /// <summary>
    /// TZ 열에 <paramref name="cases"/> 를 넣는다. 오프셋 글자는 형식표 후보 감싸기(<c>CrossBindExpression</c>), 지역 이름은 <c>TO_TIMESTAMP_TZ(:C1, '… TZR')</c>.
    /// 배열이면 한 번의 배열 INSERT, 아니면 한 행씩 스칼라 INSERT(오류도 행마다 줄로 남긴다).
    /// </summary>
    private async Task InsertTzAsync(TiberoSession s, string table, string mode, bool array, TzCase[] cases, int baseId, bool region, List<TzRow> rows)
    {
        var expression = region ? $"TO_TIMESTAMP_TZ(:C1, '{TzFormatRegion}')" : TiberoBulkValueCodec.CrossBindExpression("C1", TzType);
        var sql = $"INSERT INTO {table} (ID, SRC, TZ) VALUES (:C0, '{mode}', {expression})";
        var kind = region ? "region" : "offset";
        for (var i = 0; i < cases.Length; i++)
        {
            rows.Add(new TzRow(baseId + i, mode, cases[i], region));
        }

        if (array)
        {
            var ids = Enumerable.Range(0, cases.Length).Select(i => baseId + i).ToArray();
            Note($"tz.insert.{mode}.{kind}", await TryArrayInsertAsync(s, sql, ids, [.. cases.Select(c => c.Text)]));
            return;
        }

        for (var i = 0; i < cases.Length; i++)
        {
            Note($"tz.insert.{mode}.{kind}[{cases[i].Label}]", await TryScalarInsertAsync(s, sql, baseId + i, cases[i].Text));
        }
    }

    /// <summary>DEFAULT 한 가지: 표를 만들고(성공/오류), 기본값 열을 비운 채 한 행 넣어 값이 채워지는지 읽는다.</summary>
    private async Task DefaultCaseAsync(TiberoSession s, string label, string table, string columnDef, string column, string readExpression)
    {
        var create = await TryTextAsync(s, $"CREATE TABLE {table} (ID NUMBER, {columnDef})");
        Note($"ddl.default.{label}.create", create);
        if (!create.StartsWith("OK", StringComparison.Ordinal))
        {
            return;
        }

        Note($"ddl.default.{label}.insert", await TryTextAsync(s, $"INSERT INTO {table} (ID) VALUES (1)"));
        Note($"ddl.default.{label}.value", await TryShowAsync(s, $"SELECT {readExpression} FROM {table}"));
        Note($"ddl.default.{label}.catalog", await TryShowAsync(s,
            $"SELECT COLUMN_NAME, DATA_DEFAULT FROM USER_TAB_COLUMNS WHERE TABLE_NAME = '{table}' AND COLUMN_NAME = '{column}'"));
    }

    private sealed class AllowAll : IExecutionPolicy
    {
        public ValueTask<PolicyDecision> EvaluateAsync(PolicyContext ctx, SqlStatement stmt, CancellationToken ct) =>
            ValueTask.FromResult(PolicyDecision.Allowed);
    }

    private sealed class NoQuestions : IExecutionInteraction
    {
        public Task<bool> ConfirmAsync(SqlStatement stmt, PolicyDecision decision, CancellationToken ct) => Task.FromResult(true);

        public Task<IReadOnlyDictionary<string, string>?> AskSubstitutionsAsync(IReadOnlyList<string> names, CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<string, string>?>(null);

        public Task<ScriptErrorAction> AskScriptErrorAsync(SqlStatement stmt, DbError failure, CancellationToken ct) =>
            Task.FromResult(ScriptErrorAction.Stop);
    }
}
