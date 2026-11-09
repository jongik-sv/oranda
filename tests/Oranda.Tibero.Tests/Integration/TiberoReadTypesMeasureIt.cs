using System.Buffers.Binary;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Reflection;
using Oranda.Tibero.Driver;
using Oranda.Tibero.Execution;
using Oranda.Tibero.Sessions;
using Xunit.Abstractions;

namespace Oranda.Tibero.Tests.Integration;

/// <summary>
/// R1x 측정: 결과 그리드가 Tibero 의 BINARY_FLOAT·BINARY_DOUBLE·LONG·LONG RAW 를 잘못 읽는다. 고치기 전에 드라이버 읽기 API 가 각각 무엇을 돌려주는지 본다.
/// <list type="bullet">
/// <item>측정만 한다: 준비(표 만들기)를 뺀 모든 호출은 호출마다 잡아서 <c>R1X …</c> 줄로 남기고, 단정은 걸지 않는다.</item>
/// <item><c>ORANDA_IT_TIBERO_WRITE=1</c> 일 때만 돈다(<see cref="TiberoItSessions.SkipWrite"/>). 표는 <c>ORT_R1X_*</c> 이고 끝에 ROLLBACK 뒤 DROP … PURGE 로 지운다.</item>
/// <item>LONG·LONG RAW 는 읽기 호출 하나마다 새 리더를 열어(앞 호출이 스트림을 소비하는지 가리기 위해) 재고, 같은 리더에서 연달아 읽는 순서 시험은 따로 한 줄 남긴다.</item>
/// <item>실행: <c>ORANDA_IT_TIBERO_WRITE=1 dotnet test tests/Oranda.Tibero.Tests --filter "FullyQualifiedName~TiberoReadTypesMeasureIt" --logger "console;verbosity=detailed"</c></item>
/// </list>
/// </summary>
[Trait("Category", "Integration")]
public class TiberoReadTypesMeasureIt(ITestOutputHelper output)
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private sealed record FloatCase(string Label, string F, string D);

    private static readonly FloatCase[] FloatCases =
    [
        new("-2.25", "-2.25", "-2.25"),
        new("2.25", "2.25", "2.25"),
        new("-1.5", "-1.5", "-1.5"),
        new("1.5", "1.5", "1.5"),
        new("-0.0", "-0", "-0"),
        new("0", "0", "0"),
        new("-1E-30", "-1E-30", "-1E-30"),
        new("1E30", "1E30", "1E30"),
        new("NaN", "NaN", "NaN"),
        new("max", "3.4028234E38", "1.7976931348623157E308"),
        new("-max", "-3.4028234E38", "-1.7976931348623157E308"),
        new("-1", "-1", "-1"),
        new("123.456", "123.456", "123.456"),
        new("Inf", "Infinity", "Infinity"),
        new("-Inf", "-Infinity", "-Infinity"),
    ];

    private static readonly int[] LongLengths = [0, 1, 10, 4000, 4001, 32767, 40000, 1_000_000];

    private static readonly int[] LongRawLengths = [0, 1, 4, 2000, 2001, 40000, 1_000_000];

    private static readonly int[] LongFetchSizes = [-1, 0, 8192, 65536];

    // =================================================================================================================
    // 1. BINARY_FLOAT / BINARY_DOUBLE
    // =================================================================================================================

    [TiberoWriteFact]
    public async Task R1x_binary_float_and_double_reads()
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
        var t = TiberoItSessions.ObjectName("R1X_FLT");
        await CleanupAsync(s, t);
        try
        {
            await ExecAsync(s, $"CREATE TABLE {t} (ID NUMBER PRIMARY KEY, F BINARY_FLOAT, D BINARY_DOUBLE)");
            await LogDriverSurfaceAsync(s);

            var ids = new List<(int Id, string Label)>();
            var id = 0;
            foreach (var c in FloatCases)
            {
                id++;
                var sql = $"INSERT INTO {t} (ID, F, D) VALUES ({N(id)}, TO_BINARY_FLOAT('{c.F}'), TO_BINARY_DOUBLE('{c.D}'))";
                var result = await TryTextAsync(s, sql);
                Log($"flt insert {c.Label} via TO_BINARY_FLOAT/DOUBLE: {result}");
                if (result.StartsWith("OK", StringComparison.Ordinal))
                {
                    ids.Add((id, c.Label));
                }
            }

            var literal = await TryTextAsync(s, $"INSERT INTO {t} (ID, F, D) VALUES (901, -2.25f, -2.25d)");
            Log($"flt insert literal -2.25f/-2.25d: {literal}");
            if (literal.StartsWith("OK", StringComparison.Ordinal))
            {
                ids.Add((901, "literal -2.25f/-2.25d"));
            }

            var cast = await TryTextAsync(s, $"INSERT INTO {t} (ID, F, D) VALUES (902, CAST(-2.25 AS BINARY_FLOAT), CAST(-2.25 AS BINARY_DOUBLE))");
            Log($"flt insert CAST(-2.25 AS BINARY_FLOAT/DOUBLE): {cast}");
            if (cast.StartsWith("OK", StringComparison.Ordinal))
            {
                ids.Add((902, "CAST -2.25"));
            }

            // 바인드 쓰기(리뷰 지적 5): BinaryFloat/BinaryDouble 종류에 float/double 음수를 그대로 넣으면 드라이버 인코더가 읽기와 대칭으로 틀리는지.
            foreach (var (bindId, value) in new[] { (801, -2.25), (802, 2.25), (803, -1.5) })
            {
                var bound = await TryTextAsync(s, $"INSERT INTO {t} (ID, F, D) VALUES ({N(bindId)}, :C1, :C2)", cmd =>
                {
                    AddParam(s, cmd, "C1", TiberoParamKind.BinaryFloat, (float)value);
                    AddParam(s, cmd, "C2", TiberoParamKind.BinaryDouble, value);
                });
                var text = value.ToString(Inv);
                Log($"flt insert bind {text} kind=BinaryFloat/BinaryDouble: {bound}");
                if (bound.StartsWith("OK", StringComparison.Ordinal))
                {
                    ids.Add((bindId, "bind " + text));
                    // F= 는 F 가 같은 행 수, D= 는 D 가 같은 행 수(TO_CHAR 로 글자 비교)
                    var match = await ProbeAsync(s,
                        $"SELECT TO_CHAR(SUM(CASE WHEN F = TO_BINARY_FLOAT('{text}') THEN 1 ELSE 0 END)), TO_CHAR(SUM(CASE WHEN D = TO_BINARY_DOUBLE('{text}') THEN 1 ELSE 0 END)) "
                        + $"FROM {t} WHERE ID = {N(bindId)}", SqlPair);
                    Log($"flt bind {text} stored equals TO_BINARY_* literal: {match}");
                }
            }

            await LogSchemaAsync(s, t, "flt"); // 행이 있어야 스키마 표를 읽는다(빈 표면 「no row」)

            foreach (var (rowId, label) in ids)
            {
                var where = $"FROM {t} WHERE ID = {N(rowId)}";
                Log($"flt row={label} id={N(rowId)} sql TO_CHAR: " + await ProbeAsync(s, $"SELECT TO_CHAR(F), TO_CHAR(D) {where}", SqlPair));
                Log($"flt row={label} id={N(rowId)} sql DUMP: " + await ProbeAsync(s, $"SELECT DUMP(F), DUMP(D) {where}", SqlPair));
                Log($"flt row={label} id={N(rowId)} sql DUMP(,16): " + await ProbeAsync(s, $"SELECT DUMP(F, 16), DUMP(D, 16) {where}", SqlPair));
                Log($"flt row={label} id={N(rowId)} col=F(BINARY_FLOAT) driver: " + await ProbeAsync(s, $"SELECT F, D {where}", r => NumericCell(s, r, 0)));
                Log($"flt row={label} id={N(rowId)} col=D(BINARY_DOUBLE) driver: " + await ProbeAsync(s, $"SELECT F, D {where}", r => NumericCell(s, r, 1)));
            }
        }
        finally
        {
            await CleanupAsync(s, t);
        }
    }

    // =================================================================================================================
    // 2. LONG
    // =================================================================================================================

    [TiberoWriteFact]
    public async Task R1x_long_reads()
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
        var t = TiberoItSessions.ObjectName("R1X_LONG");
        await CleanupAsync(s, t);
        try
        {
            await ExecAsync(s, $"CREATE TABLE {t} (ID NUMBER PRIMARY KEY, L LONG)");
            await LogLongPropertiesAsync(s, t);

            var ids = new List<int>();
            foreach (var length in LongLengths)
            {
                var text = LongText(length);
                var sql = length == 0
                    ? $"INSERT INTO {t} (ID, L) VALUES (0, NULL)"
                    : $"INSERT INTO {t} (ID, L) VALUES ({N(length)}, :C1)";
                var result = await TryTextAsync(s, sql, length == 0 ? null : cmd => AddParam(s, cmd, "C1", TiberoParamKind.Long, text));
                Log($"long insert length={N(length)} (bind kind=Long): {result}");
                if (result.StartsWith("OK", StringComparison.Ordinal))
                {
                    ids.Add(length);
                }
            }

            await LogSchemaAsync(s, t, "long"); // 행이 있어야 스키마 표를 읽는다

            Log("long lengths via SQL: " + await TryScalarAsync(s, $"SELECT LENGTH(L) FROM {t} WHERE ID = 10"));

            foreach (var length in ids)
            {
                var sql = $"SELECT L FROM {t} WHERE ID = {N(length)}";
                var head = $"long length={N(length)}";
                Log($"{head} IsDBNull: " + await ProbeAsync(s, sql, r => r.IsDBNull(0).ToString()));
                Log($"{head} GetString: " + await ProbeAsync(s, sql, r => StrText(r.GetString(0))));
                Log($"{head} GetValue: " + await ProbeAsync(s, sql, r => Fmt(r.GetValue(0))));
                Log($"{head} GetFieldValue<string>: " + await ProbeAsync(s, sql, r => Fmt(r.GetFieldValue<string>(0))));
                Log($"{head} GetChars(null) length query: " + await ProbeAsync(s, sql, r => N(r.GetChars(0, 0, null, 0, 0))));
                Log($"{head} GetChars chunks of 8192: " + await ProbeAsync(s, sql, ChunkChars));
                Log($"{head} GetBytes(null) length query: " + await ProbeAsync(s, sql, r => N(r.GetBytes(0, 0, null, 0, 0))));
                Log($"{head} GetFieldType/GetDataTypeName: " + await ProbeAsync(s, sql, r => r.GetFieldType(0).FullName + " / " + r.GetDataTypeName(0)));
                Log($"{head} ReadRawBytes: " + await ProbeAsync(s, sql, r => RawText(Drv(s).ReadRawBytes(r, 0))));
                Log($"{head} GetString with SequentialAccess: " + await ProbeAsync(s, sql, r => StrText(r.GetString(0)), CommandBehavior.SequentialAccess));
                Log($"{head} GetChars chunks with SequentialAccess: " + await ProbeAsync(s, sql, ChunkChars, CommandBehavior.SequentialAccess));
                Log($"{head} sequence on one reader GetString,GetValue,GetString,ReadRawBytes: " + await ProbeAsync(s, sql, r =>
                    "1:" + Attempt(() => StrText(r.GetString(0))) + " 2:" + Attempt(() => Fmt(r.GetValue(0))) + " 3:" + Attempt(() => StrText(r.GetString(0)))
                    + " 4:" + Attempt(() => RawText(Drv(s).ReadRawBytes(r, 0)))));
                Log($"{head} sequence on one reader ReadRawBytes,GetString,GetValue: " + await ProbeAsync(s, sql, r =>
                    "1:" + Attempt(() => RawText(Drv(s).ReadRawBytes(r, 0))) + " 2:" + Attempt(() => StrText(r.GetString(0))) + " 3:" + Attempt(() => Fmt(r.GetValue(0)))));
            }
        }
        finally
        {
            await CleanupAsync(s, t);
        }
    }

    // =================================================================================================================
    // 3. LONG RAW
    // =================================================================================================================

    [TiberoWriteFact]
    public async Task R1x_long_raw_reads()
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
        var t = TiberoItSessions.ObjectName("R1X_LRAW");
        await CleanupAsync(s, t);
        try
        {
            await ExecAsync(s, $"CREATE TABLE {t} (ID NUMBER PRIMARY KEY, R LONG RAW)");

            var ids = new List<int>();
            foreach (var length in LongRawLengths)
            {
                var bytes = Pattern(length);
                var sql = length == 0
                    ? $"INSERT INTO {t} (ID, R) VALUES (0, NULL)"
                    : $"INSERT INTO {t} (ID, R) VALUES ({N(length)}, :C1)";
                var result = await TryTextAsync(s, sql, length == 0 ? null : cmd => AddParam(s, cmd, "C1", TiberoParamKind.LongRaw, bytes));
                Log($"lraw insert length={N(length)} (scalar bind kind=LongRaw): {result}");
                if (result.StartsWith("OK", StringComparison.Ordinal))
                {
                    ids.Add(length);
                }
            }

            await LogSchemaAsync(s, t, "lraw"); // 행이 있어야 스키마 표를 읽는다

            Log("lraw RAWTOHEX(R) via SQL: " + Cut(await TryScalarAsync(s, $"SELECT RAWTOHEX(R) FROM {t} WHERE ID = 4"), 200));
            Log("lraw DUMP(R) via SQL: " + Cut(await TryScalarAsync(s, $"SELECT DUMP(R) FROM {t} WHERE ID = 4"), 200));

            foreach (var length in ids)
            {
                var sql = $"SELECT R FROM {t} WHERE ID = {N(length)}";
                var head = $"lraw length={N(length)}";
                var expected = Pattern(Math.Min(length, 4));
                Log($"{head} IsDBNull: " + await ProbeAsync(s, sql, r => r.IsDBNull(0).ToString()));
                Log($"{head} GetValue: " + await ProbeAsync(s, sql, r => Fmt(r.GetValue(0))));
                Log($"{head} GetFieldValue<byte[]>: " + await ProbeAsync(s, sql, r => Fmt(r.GetFieldValue<byte[]>(0))));
                Log($"{head} GetBytes(null) length query: " + await ProbeAsync(s, sql, r => N(r.GetBytes(0, 0, null, 0, 0))));
                Log($"{head} GetBytes chunks of 8192: " + await ProbeAsync(s, sql, ChunkBytes));
                Log($"{head} GetFieldType/GetDataTypeName: " + await ProbeAsync(s, sql, r => r.GetFieldType(0).FullName + " / " + r.GetDataTypeName(0)));
                Log($"{head} ReadRawBytes: " + await ProbeAsync(s, sql, r => RawText(Drv(s).ReadRawBytes(r, 0), expected)));
                Log($"{head} GetValue with SequentialAccess: " + await ProbeAsync(s, sql, r => Fmt(r.GetValue(0)), CommandBehavior.SequentialAccess));
                Log($"{head} GetBytes chunks with SequentialAccess: " + await ProbeAsync(s, sql, ChunkBytes, CommandBehavior.SequentialAccess));
                Log($"{head} sequence on one reader GetValue,ReadRawBytes,GetValue: " + await ProbeAsync(s, sql, r =>
                    "1:" + Attempt(() => Fmt(r.GetValue(0))) + " 2:" + Attempt(() => RawText(Drv(s).ReadRawBytes(r, 0), expected)) + " 3:" + Attempt(() => Fmt(r.GetValue(0)))));
                Log($"{head} sequence on one reader ReadRawBytes,GetValue,GetBytes: " + await ProbeAsync(s, sql, r =>
                    "1:" + Attempt(() => RawText(Drv(s).ReadRawBytes(r, 0), expected)) + " 2:" + Attempt(() => Fmt(r.GetValue(0))) + " 3:" + Attempt(() => ChunkBytes(r))));
            }
        }
        finally
        {
            await CleanupAsync(s, t);
        }
    }

    // =================================================================================================================
    // 읽기 호출 묶음
    // =================================================================================================================

    /// <summary>BINARY_FLOAT·BINARY_DOUBLE 한 칸에 쓸 수 있는 읽기 API 를 전부 불러 "이름=결과" 로 이어 붙인다(호출마다 잡는다).</summary>
    private static string NumericCell(TiberoSession s, DbDataReader r, int i)
    {
        var parts = new List<string>();
        Add(parts, "IsDBNull", () => r.IsDBNull(i).ToString());
        Add(parts, "GetFieldType", () => r.GetFieldType(i).FullName ?? "(null)");
        Add(parts, "GetDataTypeName", () => r.GetDataTypeName(i));
        Add(parts, "GetValue", () => Fmt(r.GetValue(i)));
        Add(parts, "GetFloat", () => Fmt(r.GetFloat(i)));
        Add(parts, "GetDouble", () => Fmt(r.GetDouble(i)));
        Add(parts, "GetFieldValue<float>", () => Fmt(r.GetFieldValue<float>(i)));
        Add(parts, "GetFieldValue<double>", () => Fmt(r.GetFieldValue<double>(i)));
        Add(parts, "GetDecimal", () => Fmt(r.GetDecimal(i)));
        Add(parts, "GetString", () => StrText(r.GetString(i)));
        Add(parts, "GetProviderSpecificValue", () => Fmt(r.GetProviderSpecificValue(i)));
        Add(parts, "ReadRawBytes", () => RawText(Drv(s).ReadRawBytes(r, i)));
        Add(parts, "GetBytes(null)", () => N(r.GetBytes(i, 0, null, 0, 0)));
        Add(parts, "GetBytes(64)", () =>
        {
            var buf = new byte[64];
            var n = r.GetBytes(i, 0, buf, 0, buf.Length);
            return "n=" + N(n) + " hex=" + Convert.ToHexString(buf, 0, (int)Math.Min(n, buf.Length));
        });
        return string.Join(" | ", parts);
    }

    private static string ChunkChars(DbDataReader r)
    {
        var buf = new char[8192];
        long offset = 0;
        var calls = 0;
        long first = -1;
        while (calls < 200)
        {
            var n = r.GetChars(0, offset, buf, 0, buf.Length);
            calls++;
            if (calls == 1)
            {
                first = n;
            }

            if (n <= 0)
            {
                break;
            }

            offset += n;
        }

        return $"total={N(offset)} calls={N(calls)} firstCall={N(first)}";
    }

    private static string ChunkBytes(DbDataReader r)
    {
        var buf = new byte[8192];
        long offset = 0;
        var calls = 0;
        long first = -1;
        while (calls < 200)
        {
            var n = r.GetBytes(0, offset, buf, 0, buf.Length);
            calls++;
            if (calls == 1)
            {
                first = n;
            }

            if (n <= 0)
            {
                break;
            }

            offset += n;
        }

        return $"total={N(offset)} calls={N(calls)} firstCall={N(first)}";
    }

    private static string SqlPair(DbDataReader r) =>
        "F=" + (r.IsDBNull(0) ? "NULL" : r.GetString(0)) + " D=" + (r.IsDBNull(1) ? "NULL" : r.GetString(1));

    // =================================================================================================================
    // 드라이버 표면(스키마·속성·메서드 이름)
    // =================================================================================================================

    private async Task LogSchemaAsync(TiberoSession s, string table, string tag)
    {
        var text = await ProbeAsync(s, $"SELECT * FROM {table}", r =>
        {
            var schema = r.GetSchemaTable();
            if (schema is null)
            {
                return "(no schema table)";
            }

            var lines = new List<string> { "schema columns=" + string.Join(",", schema.Columns.Cast<DataColumn>().Select(c => c.ColumnName)) };
            foreach (DataRow row in schema.Rows)
            {
                var parts = new List<string>();
                foreach (var name in SchemaColumns)
                {
                    parts.Add(name + "=" + (schema.Columns.Contains(name) ? Convert.ToString(row[name], Inv) : "(absent)"));
                }

                var ordinal = schema.Columns.Contains("ColumnOrdinal") ? Convert.ToInt32(row["ColumnOrdinal"], Inv) : lines.Count - 1;
                parts.Add("GetDataTypeName=" + Attempt(() => r.GetDataTypeName(ordinal)));
                parts.Add("GetFieldType=" + Attempt(() => r.GetFieldType(ordinal).FullName ?? "(null)"));
                if (schema.Columns.Contains("ProviderType") && row["ProviderType"] is { } provider and not DBNull)
                {
                    parts.Add("adapterKind=" + (Drv(s).KindOfProviderType(Convert.ToInt32(provider, Inv))?.ToString() ?? "(unknown)"));
                }

                lines.Add(string.Join(" ", parts));
            }

            return string.Join(" ;; ", lines);
        });
        Log($"{tag} schema: {text}");
    }

    private static readonly string[] SchemaColumns =
        ["ColumnName", "ProviderType", "ColumnSize", "NumericPrecision", "NumericScale", "DataType", "DataTypeName", "IsLong", "AllowDBNull"];

    private async Task LogDriverSurfaceAsync(TiberoSession s)
    {
        Log("driver command type: " + DescribeCommandType(s));
        var methods = await ProbeAsync(s, "SELECT 1 FROM DUAL", r =>
            r.GetType().FullName + " methods: " + string.Join(
                ",",
                r.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).Select(m => m.Name).Distinct(StringComparer.Ordinal)));
        Log("driver reader " + methods);
    }

    private static string DescribeCommandType(TiberoSession s)
    {
        try
        {
            using var cmd = s.CreateCommand();
            var props = cmd.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(p => p.Name + ":" + p.PropertyType.Name + (p.CanWrite ? "(rw)" : "(ro)"));
            return cmd.GetType().FullName + " props: " + string.Join(",", props);
        }
        catch (Exception ex)
        {
            return "ERR " + Describe(ex);
        }
    }

    /// <summary>LONG 과 이름이 비슷한 쓸 수 있는 int 명령 속성(InitialLONGFetchSize 같은 것)이 있으면 값을 바꿔 가며 GetString 길이를 잰다. 없으면 없다고 남긴다.</summary>
    private async Task LogLongPropertiesAsync(TiberoSession s, string table)
    {
        Log("long adapter: only SetInitialLobFetchSize (LOB) is exposed by TiberoDriver; no LONG-specific setter in the adapter");
        PropertyInfo[] candidates;
        try
        {
            using var cmd = s.CreateCommand();
            candidates = [.. cmd.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanWrite && p.PropertyType == typeof(int)
                            && (p.Name.Contains("Long", StringComparison.OrdinalIgnoreCase) || p.Name.Contains("Fetch", StringComparison.OrdinalIgnoreCase)))];
        }
        catch (Exception ex)
        {
            Log("long command properties ERR " + Describe(ex));
            return;
        }

        if (candidates.Length == 0)
        {
            Log("long command properties: no writable int property with Long/Fetch in its name");
            return;
        }

        var ins = await TryTextAsync(s, $"INSERT INTO {table} (ID, L) VALUES (7, 'seven77')");
        Log("long property probe row id=7 (7 chars): " + ins);
        foreach (var prop in candidates)
        {
            foreach (var size in LongFetchSizes)
            {
                var text = await ProbeAsync(
                    s,
                    $"SELECT L FROM {table} WHERE ID = 7",
                    r => "GetString=" + Attempt(() => StrText(r.GetString(0))) + " GetValue=" + Attempt(() => Fmt(r.GetValue(0))),
                    CommandBehavior.Default,
                    cmd => prop.SetValue(cmd, size));
                Log($"long command property {prop.Name}={N(size)}: {text}");
            }
        }

        await SilentAsync(s, "ROLLBACK");
    }

    // =================================================================================================================
    // 글자 만들기
    // =================================================================================================================

    private void Log(string text) => output.WriteLine("R1X " + text);

    private static string N(long value) => value.ToString(Inv);

    private static string Flat(string text) => text.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal).Trim();

    private static string Cut(string text, int max) => text.Length <= max ? text : text[..max] + "...";

    private static string Hex(byte[] bytes, int max) => Convert.ToHexString(bytes, 0, Math.Min(max, bytes.Length));

    private static string TailHex(byte[] bytes, int max)
    {
        var take = Math.Min(max, bytes.Length);
        return Convert.ToHexString(bytes, bytes.Length - take, take);
    }

    private static string StrText(string? text) =>
        text is null ? "null" : $"len={N(text.Length)} first20='{Flat(text[..Math.Min(20, text.Length)])}'";

    /// <summary>서버 원시 바이트: 길이·앞 32바이트·뒤 16바이트, 4/8바이트면 부동소수 해석 네 가지, expected 가 있으면 그 바이트열이 어디서 시작하는지.</summary>
    private static string RawText(byte[]? raw, byte[]? expected = null)
    {
        if (raw is null)
        {
            return "null";
        }

        var text = $"len={N(raw.Length)} first32={Hex(raw, 32)} last16={TailHex(raw, 16)}";
        if (raw.Length is 4 or 8)
        {
            text += " " + Interpret(raw);
        }

        if (expected is { Length: > 0 } && raw.Length >= expected.Length)
        {
            text += " patternAt=" + N(raw.AsSpan().IndexOf(expected));
        }

        return text;
    }

    /// <summary>
    /// 4/8바이트 원시값을 네 가지로 읽는다: IEEE 큰/작은 끝, 그리고 "오라클 순서 인코딩"(양수는 부호 비트만, 음수는 전체 비트 뒤집음)을 큰/작은 끝으로.
    /// </summary>
    private static string Interpret(byte[] raw)
    {
        if (raw.Length == 4)
        {
            var be = BinaryPrimitives.ReadUInt32BigEndian(raw);
            var le = BinaryPrimitives.ReadUInt32LittleEndian(raw);
            return $"asFloat[ieeeBE={BitConverter.UInt32BitsToSingle(be).ToString("R", Inv)} ieeeLE={BitConverter.UInt32BitsToSingle(le).ToString("R", Inv)} "
                + $"orderedBE={BitConverter.UInt32BitsToSingle(Unorder(be)).ToString("R", Inv)} orderedLE={BitConverter.UInt32BitsToSingle(Unorder(le)).ToString("R", Inv)}]";
        }

        var be64 = BinaryPrimitives.ReadUInt64BigEndian(raw);
        var le64 = BinaryPrimitives.ReadUInt64LittleEndian(raw);
        return $"asDouble[ieeeBE={BitConverter.UInt64BitsToDouble(be64).ToString("R", Inv)} ieeeLE={BitConverter.UInt64BitsToDouble(le64).ToString("R", Inv)} "
            + $"orderedBE={BitConverter.UInt64BitsToDouble(Unorder(be64)).ToString("R", Inv)} orderedLE={BitConverter.UInt64BitsToDouble(Unorder(le64)).ToString("R", Inv)}]";
    }

    private static uint Unorder(uint v) => (v & 0x80000000u) != 0 ? v ^ 0x80000000u : ~v;

    private static ulong Unorder(ulong v) => (v & 0x8000000000000000ul) != 0 ? v ^ 0x8000000000000000ul : ~v;

    /// <summary>값의 .NET 형식과 값. float/double 은 R·G9·G17 로 모두 보여 준다.</summary>
    private static string Fmt(object? v) => v switch
    {
        null => "null",
        DBNull => "DBNull",
        float f => $"System.Single R={f.ToString("R", Inv)} G9={f.ToString("G9", Inv)} G17={((double)f).ToString("G17", Inv)}",
        double d => $"System.Double R={d.ToString("R", Inv)} G9={d.ToString("G9", Inv)} G17={d.ToString("G17", Inv)}",
        string t => "System.String " + StrText(t),
        byte[] b => $"System.Byte[] len={N(b.Length)} first16={Hex(b, 16)} last16={TailHex(b, 16)}",
        IFormattable f => v.GetType().FullName + " " + f.ToString(null, Inv),
        _ => v.GetType().FullName + " " + v,
    };

    private static string LongText(int length)
    {
        var chars = new char[length];
        for (var i = 0; i < length; i++)
        {
            chars[i] = (char)('a' + (i % 26));
        }

        return new string(chars);
    }

    private static byte[] Pattern(int length)
    {
        var bytes = new byte[length];
        for (var i = 0; i < length; i++)
        {
            bytes[i] = (byte)(i % 251);
        }

        return bytes;
    }

    private static string Describe(Exception ex)
    {
        var error = TiberoErrorClassifier.ToDbError(ex, null);
        var head = error is null
            ? ex.GetType().Name + " " + Flat(ex.Message)
            : "number=" + N(error.Number) + " " + Flat(error.Message);
        return Cut(head, 500);
    }

    private static string Attempt(Func<string> call)
    {
        try
        {
            return call();
        }
        catch (Exception ex)
        {
            return "ERR " + Describe(ex);
        }
    }

    private static void Add(List<string> parts, string name, Func<string> call) => parts.Add(name + "=" + Attempt(call));

    // =================================================================================================================
    // 실행 도우미
    // =================================================================================================================

    private static TiberoDriver Drv(TiberoSession s) =>
        s.Driver ?? throw new InvalidOperationException("이 시험은 실제 드라이버 세션이 필요하다.");

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

    /// <summary>문장을 보내고 "OK rows=n" 또는 "ERR …" 를 돌려준다(던지지 않는다).</summary>
    private static async Task<string> TryTextAsync(TiberoSession s, string sql, Action<DbCommand>? prepare = null)
    {
        try
        {
            var n = await ExecAsync(s, sql, prepare);
            return $"OK rows={N(n)}";
        }
        catch (Exception ex)
        {
            return "ERR " + Describe(ex);
        }
    }

    private static async Task<string> TryScalarAsync(TiberoSession s, string sql)
    {
        return await ProbeAsync(s, sql, r => r.IsDBNull(0) ? "(null)" : Convert.ToString(r.GetValue(0), Inv) ?? "(null)");
    }

    /// <summary>
    /// 새 리더를 열어 첫 행을 읽고 <paramref name="probe"/> 를 돌린다(던지지 않는다: 실패는 "ERR …"). 리더는 호출마다 새로 열려
    /// 앞 호출이 LONG 스트림을 소비해도 영향이 없다.
    /// </summary>
    private static async Task<string> ProbeAsync(
        TiberoSession s,
        string sql,
        Func<DbDataReader, string> probe,
        CommandBehavior behavior = CommandBehavior.Default,
        Action<DbCommand>? prepare = null)
    {
        try
        {
            await s.CommandGate.EnterAsync(CancellationToken.None);
            try
            {
                await using var cmd = s.CreateCommand(sql);
                prepare?.Invoke(cmd);
                await using var reader = await cmd.ExecuteReaderAsync(behavior);
                if (!await reader.ReadAsync())
                {
                    return "(no row)";
                }

                return probe(reader);
            }
            finally
            {
                s.CommandGate.Release();
            }
        }
        catch (Exception ex)
        {
            return "ERR " + Describe(ex);
        }
    }

    private static async Task CleanupAsync(TiberoSession s, string table)
    {
        await SilentAsync(s, "ROLLBACK");
        if (!await SilentAsync(s, $"DROP TABLE {table} CASCADE CONSTRAINTS PURGE"))
        {
            await SilentAsync(s, $"DROP TABLE {table} CASCADE CONSTRAINTS");
        }
    }
}
