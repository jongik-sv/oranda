using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Oranda.Core.Execution;
using Oranda.Core.Execution.Providers;
using Oranda.Tibero.Driver;
using Oranda.Tibero.Results;
using Oranda.Tibero.Sessions;

namespace Oranda.Tibero.Execution;

/// <summary>
/// 대량 읽기(<see cref="TiberoBulkExecutionService.StreamRowsAsync"/>)의 드라이버 쪽(TB1 설계 1.1·1.2·3.5): 읽기 바인드, 열 계획(스키마 표 ProviderType),
/// 행 읽기. 세션 명령 잠금·바쁨 표시는 호출한 쪽이 쥔다.
/// <para>
/// 드라이버의 <c>*Async</c> 는 동기로 돌므로(실측 4장) 실행과 읽기 묶음 하나(<see cref="BatchRows"/> 행 또는 LOB·RAW 누적 <see cref="BatchBytes"/> 바이트)를
/// 한 번의 <c>Task.Run</c> 으로 돌린다. FetchSize 는 효과가 없어(values.md 1장) 묶음이 곧 한 번에 읽는 양이다. 취소는 드라이버 호출마다
/// <see cref="CommandCancellation.CancelOnToken"/> 으로 묶고(호출이 끝나면 푼다), 행 사이·묶음 사이에 토큰을 본다.
/// </para>
/// <para>
/// 값은 드라이버 형식이 아니다(결정 25): NUMBER = <see cref="TiberoBulkNumber"/>, TIMESTAMP = <see cref="TiberoBulkTimestamp"/>, LOB 은 끝까지 읽은 string·byte[].
/// 원본 세션 값이 대상 세션 명령으로 넘어가도 연결이 섞이지 않는다.
/// </para>
/// </summary>
internal static class TiberoBulkReader
{
    /// <summary>읽기 묶음 하나의 최대 행 수.</summary>
    internal const int BatchRows = 500;

    /// <summary>읽기 묶음 하나의 LOB·RAW 누적 바이트 상한(CLOB 은 문자 × 2). 넘으면 그 행까지로 묶음을 끝낸다.</summary>
    internal const long BatchBytes = 16L * 1024 * 1024;

    /// <summary>decimal 이 손실 없이 담는 유효 자릿수(결정 40). 넘는 NUMBER 키는 글자로 보낸다.</summary>
    private const int MaxDecimalDigits = 28;

    private const int ClobChunk = 8192;

    private const int BlobChunk = 81920;

    private const string CancelledMessage = "대량 읽기를 취소했습니다.";

    /// <summary>
    /// 질의를 실행하고 행을 낸다. 명령 잠금을 쥔 채로 부른다. 리더·명령 정리는 여기서 한다(조기 중단·예외 포함).
    /// 취소(토큰 취소, 또는 취소된 토큰 뒤의 TBR-12040)는 <see cref="OperationCanceledException"/>, 드라이버 예외는 <see cref="TiberoSession.ReportFailure"/> 에 알린 뒤 그대로 올린다.
    /// </summary>
    public static async IAsyncEnumerable<object?[]> ReadAsync(TiberoSession session, SqlStatement query,
                                                              IReadOnlyDictionary<string, BindValue> binds,
                                                              [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(binds);
        var driver = session.Driver ?? throw new InvalidOperationException("세션에 Tibero 드라이버 어댑터가 없어 대량 읽기를 할 수 없습니다.");
        DbCommand? cmd = null;
        DbDataReader? reader = null;
        try
        {
            // 명령·바인드 만들기는 서버를 부르지 않는다. 값이 없는 바인드는 여기서 InvalidOperationException(보내지 않음).
            cmd = session.CreateCommand(query.Text);
            Bind(driver, cmd, query, binds);
            var command = cmd;

            TiberoBulkReadKind[] plan;
            try
            {
                plan = await Task.Run(() =>
                {
                    reader = Execute(command, ct);
                    return PlanOf(reader);
                }, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && TiberoExecutionService.IsCancellation(ex, ct))
            {
                throw new OperationCanceledException(CancelledMessage, ex, ct); // TBR-12040 → 취소(복사가 실패로 적지 않게)
            }
            catch (Exception ex) when (TiberoErrorClassifier.IsDriverException(ex) && Report(session, ex))
            {
                throw; // 도달하지 않는다(필터가 false)
            }

            var open = reader!;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                Batch batch;
                try
                {
                    batch = await Task.Run(() => ReadBatch(command, open, driver, plan, ct), CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException && TiberoExecutionService.IsCancellation(ex, ct))
                {
                    throw new OperationCanceledException(CancelledMessage, ex, ct);
                }
                catch (Exception ex) when (TiberoErrorClassifier.IsDriverException(ex) && Report(session, ex))
                {
                    throw;
                }

                foreach (var row in batch.Rows)
                {
                    ct.ThrowIfCancellationRequested();
                    yield return row;
                }

                if (batch.Done)
                {
                    yield break;
                }
            }
        }
        finally
        {
            // 닫기도 드라이버 호출(커서 닫기)이라 스레드 풀에서 한다. 닫기 실패는 삼킨다(SafeDisposeAsync).
            var r = reader;
            var c = cmd;
            await Task.Run(async () =>
            {
                await TiberoExecutionService.SafeDisposeAsync(r).ConfigureAwait(false);
                await TiberoExecutionService.SafeDisposeAsync(c).ConfigureAwait(false);
            }, CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 읽기 바인드(TB1 설계 3.5): 문장의 바인드 이름(<see cref="SqlStatement.BindNames"/>, 비었으면 <paramref name="binds"/> 의 키)마다 매개변수 하나.
    /// 값이 없는 이름이면 <see cref="InvalidOperationException"/>.
    /// </summary>
    internal static void Bind(TiberoDriver driver, DbCommand cmd, SqlStatement query, IReadOnlyDictionary<string, BindValue> binds)
    {
        ArgumentNullException.ThrowIfNull(driver);
        ArgumentNullException.ThrowIfNull(cmd);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(binds);
        var lookup = new Dictionary<string, BindValue>(binds, StringComparer.OrdinalIgnoreCase);
        var names = query.BindNames.Count > 0 ? query.BindNames : [.. binds.Keys];
        foreach (var name in names)
        {
            if (!lookup.TryGetValue(name, out var bind))
            {
                throw new InvalidOperationException($"바인드 변수 :{name} 의 값이 없습니다.");
            }

            cmd.Parameters.Add(CreateParameter(driver, cmd, name, bind));
        }
    }

    /// <summary>
    /// 바인드 하나 → 매개변수. <see cref="TiberoBulkNumber"/> = Decimal 종류(28자리 안이면 decimal, 아니면 고정 소수점 글자),
    /// <see cref="TiberoBulkTimestamp"/> = TimeStamp 종류(초 + 나노초 그대로 — <see cref="TiberoBindBuilder"/> 는 DateTime 틱(100ns)만 넣어 TIMESTAMP(8·9) 키가 안 맞는다).
    /// 그 밖(기간 DateTime·시퀀스 개수·글자 키·RAW byte[])은 <see cref="TiberoBindBuilder.Create"/> 그대로.
    /// </summary>
    internal static DbParameter CreateParameter(TiberoDriver driver, DbCommand cmd, string name, BindValue bind)
    {
        ArgumentNullException.ThrowIfNull(bind);
        return bind switch
        {
            { Direction: BindDirection.In, Value: TiberoBulkNumber n } => Input(driver, cmd, name, TiberoParamKind.Decimal, NumberParameterValue(n)),
            { Direction: BindDirection.In, Value: TiberoBulkTimestamp t } =>
                Input(driver, cmd, name, TiberoParamKind.TimeStamp, driver.CreateTimeStamp(t.Seconds, t.Nanosecond)),
            _ => TiberoBindBuilder.Create(driver, cmd, name, bind),
        };
    }

    /// <summary>NUMBER 키 값: 유효 자리(부호·소수점 뺀 숫자 글자)가 28 이하이고 decimal 로 그대로 읽히면 decimal, 아니면 글자.</summary>
    internal static object NumberParameterValue(TiberoBulkNumber number)
    {
        ArgumentNullException.ThrowIfNull(number);
        var text = number.Text;
        var digits = text.Count(char.IsAsciiDigit);
        return digits <= MaxDecimalDigits
               && decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var d)
               && d.ToString(CultureInfo.InvariantCulture) == text
            ? d
            : text;
    }

    private static DbParameter Input(TiberoDriver driver, DbCommand cmd, string name, TiberoParamKind kind, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        driver.SetParameterKind(p, kind);
        p.Direction = ParameterDirection.Input;
        p.Value = value;
        return p;
    }

    private static DbDataReader Execute(DbCommand cmd, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using (cmd.CancelOnToken(ct))
        {
            return cmd.ExecuteReader();
        }
    }

    /// <summary>
    /// 열마다 읽는 법(TB1 설계 1.1 열 판정): 스키마 표 ProviderType(+IsRowID), 그 열의 ProviderType 을 모르면 <c>GetDataTypeName</c> 이름.
    /// 모르는 형식이면 <see cref="NotSupportedException"/>(열 이름·형식). 문자로 가정하지 않는다(숫자 열 GetString 은 빈 글자).
    /// </summary>
    internal static TiberoBulkReadKind[] PlanOf(DbDataReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var schema = TiberoColumns.SchemaOrNull(reader.GetSchemaTable);
        var kinds = new TiberoBulkReadKind[reader.FieldCount];
        for (var i = 0; i < kinds.Length; i++)
        {
            var name = reader.GetName(i);
            kinds[i] = TiberoColumns.ProviderTypeOf(schema, i) is { } providerType
                ? TiberoBulkTypes.ReadKindOf(providerType, IsRowId(schema!, i), name)
                : TiberoBulkTypes.ReadKindOf(reader.GetDataTypeName(i), name);
        }

        return kinds;
    }

    private static bool IsRowId(DataTable schema, int ordinal)
    {
        if (!schema.Columns.Contains("IsRowID"))
        {
            return false;
        }

        return schema.Rows[ordinal]["IsRowID"] switch
        {
            bool b => b,
            string s => bool.TryParse(s, out var parsed) && parsed,
            _ => false,
        };
    }

    /// <summary>묶음 하나: <see cref="BatchRows"/> 행 또는 LOB·RAW 누적 <see cref="BatchBytes"/> 바이트까지. 리더가 끝나면 <see cref="Batch.Done"/>.</summary>
    private static Batch ReadBatch(DbCommand cmd, DbDataReader reader, TiberoDriver driver, TiberoBulkReadKind[] plan, CancellationToken ct)
    {
        var rows = new List<object?[]>();
        long bytes = 0;
        using (cmd.CancelOnToken(ct))
        {
            while (rows.Count < BatchRows && bytes < BatchBytes)
            {
                ct.ThrowIfCancellationRequested();
                if (!reader.Read())
                {
                    return new Batch(rows, true);
                }

                var row = new object?[plan.Length];
                for (var i = 0; i < plan.Length; i++)
                {
                    if (reader.IsDBNull(i))
                    {
                        continue;
                    }

                    var value = ReadValue(reader, driver, i, plan[i]);
                    row[i] = value;
                    bytes += value switch
                    {
                        byte[] b => b.Length,
                        string s when plan[i] is TiberoBulkReadKind.Clob or TiberoBulkReadKind.Long => 2L * s.Length, // LONG 도 본문 전체(최대 2GB)
                        _ => 0,
                    };
                }

                rows.Add(row);
            }
        }

        return new Batch(rows, false);
    }

    /// <summary>값 하나(TB1 설계 1.2 표). NULL 은 호출한 쪽이 먼저 본다.</summary>
    internal static object? ReadValue(DbDataReader reader, TiberoDriver driver, int i, TiberoBulkReadKind kind) => kind switch
    {
        TiberoBulkReadKind.Number => ReadNumber(reader, driver, i),
        TiberoBulkReadKind.BinaryFloat => ReadBinaryFloat<float>(reader, driver, i, 4),
        TiberoBulkReadKind.BinaryDouble => ReadBinaryFloat<double>(reader, driver, i, 8),
        TiberoBulkReadKind.Text => reader.GetString(i), // CHAR 끝 공백 그대로
        TiberoBulkReadKind.Long => ReadLong(reader, i),
        TiberoBulkReadKind.Clob => ReadClob(reader, driver, i),
        TiberoBulkReadKind.Blob => ReadBlob(reader, driver, i),
        TiberoBulkReadKind.Raw => driver.ReadRawBytes(reader, i),
        TiberoBulkReadKind.LongRaw => ReadLongRaw(reader, i),
        TiberoBulkReadKind.Date => DateTime.SpecifyKind(
            reader.GetValue(i) is DateTime date ? date : Convert.ToDateTime(reader.GetValue(i), CultureInfo.InvariantCulture), DateTimeKind.Unspecified),
        TiberoBulkReadKind.Timestamp => driver.ReadRawBytes(reader, i) is { } ts ? TiberoBulkTimestamp.FromBytes(ts) : null,
        TiberoBulkReadKind.TimestampTz => driver.ReadRawBytes(reader, i) is { } tz ? TiberoBulkTimestampTz.FromBytes(tz) : null,
        TiberoBulkReadKind.TimestampLtz => driver.ReadRawBytes(reader, i) is { } ltz ? TiberoBulkTimestampLtz.FromBytes(ltz) : null,
        TiberoBulkReadKind.IntervalYm => driver.ReadRawBytes(reader, i) is { } ym ? TiberoBulkIntervalYm.FromBytes(ym) : null,
        TiberoBulkReadKind.IntervalDs => driver.ReadRawBytes(reader, i) is { } ds ? TiberoBulkIntervalDs.FromBytes(ds) : null,
        _ => throw new NotSupportedException($"데이터 복사가 지원하지 않는 열 형식입니다: {reader.GetName(i)} ({kind})"),
    };

    /// <summary>
    /// BINARY_FLOAT/DOUBLE: 원시 바이트(4·8바이트)를 직접 푼다. 드라이버 GetValue/GetFloat/GetDouble 은 음수에서 틀린 값을 준다(R1x 실측).
    /// 바이트 길이가 열 형식과 맞지 않으면 열 이름과 함께 <see cref="FormatException"/>.
    /// </summary>
    private static T ReadBinaryFloat<T>(DbDataReader reader, TiberoDriver driver, int i, int length)
        where T : struct
    {
        var raw = driver.ReadRawBytes(reader, i);
        if (raw is null || raw.Length != length)
        {
            throw new FormatException($"BINARY_FLOAT/BINARY_DOUBLE 열 {reader.GetName(i)} 의 원시 바이트가 {length}바이트가 아닙니다: {(raw is null ? "null" : raw.Length.ToString(CultureInfo.InvariantCulture))}");
        }

        return (T)TiberoWire.DecodeBinaryFloat(raw);
    }

    /// <summary>LONG: GetValue 로 읽는다(GetString 은 값이 있어도 빈 글자, ReadRawBytes 는 72바이트 서술자 — R1x 실측).</summary>
    private static string ReadLong(DbDataReader reader, int i) => reader.GetValue(i) switch
    {
        string s => s,
        var v => Convert.ToString(v, CultureInfo.InvariantCulture) ?? string.Empty,
    };

    /// <summary>
    /// LONG RAW: GetValue 가 값을 준다(ReadRawBytes 는 72바이트 서술자, GetBytes 조각은 쓰레기 — R1x 실측). 실제 드라이버는 <c>sbyte[]</c> 를 준다(win144 #86).
    /// CLR 은 <c>sbyte[]</c> 객체를 <c>byte[]</c> 패턴에도 맞춰 주므로 형식을 정확히 비교한다(그대로 내면 받는 쪽이 <c>byte[]</c> 가 아닌 값을 받는다).
    /// </summary>
    private static byte[] ReadLongRaw(DbDataReader reader, int i)
    {
        var value = reader.GetValue(i);
        if (value.GetType() == typeof(sbyte[]))
        {
            return TiberoWire.ToBytes((sbyte[])value);
        }

        return value.GetType() == typeof(byte[])
            ? (byte[])value
            : throw new FormatException($"LONG RAW 열 {reader.GetName(i)} 의 값이 바이트 배열이 아닙니다: {value.GetType().Name}");
    }

    /// <summary>NUMBER: 원시 바이트 → 고정 소수점(38자리·1E±125 손실 없음). 바이트를 못 풀 때만 GetValue 값의 invariant 글자.</summary>
    private static TiberoBulkNumber ReadNumber(DbDataReader reader, TiberoDriver driver, int i)
    {
        if (driver.ReadRawBytes(reader, i) is { } raw && FromBytesOrNull(raw) is { } number)
        {
            return number;
        }

        var value = reader.GetValue(i);
        var text = value switch
        {
            double d => d.ToString("R", CultureInfo.InvariantCulture),
            float f => f.ToString("R", CultureInfo.InvariantCulture),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
        };

        try
        {
            return TiberoBulkNumber.Parse(text);
        }
        catch (FormatException ex)
        {
            throw new FormatException($"NUMBER 열 {reader.GetName(i)} 의 값을 고정 소수점 글자로 읽지 못했습니다: {text}", ex);
        }
    }

    private static TiberoBulkNumber? FromBytesOrNull(byte[] raw)
    {
        try
        {
            return TiberoBulkNumber.FromBytes(raw);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>CLOB·NCLOB 끝까지. 끝은 Read 가 0 을 줄 때로 본다(임시 NCLOB 은 Length 가 0 일 수 있다, values.md 4장).</summary>
    private static string? ReadClob(DbDataReader reader, TiberoDriver driver, int i)
    {
        using var lob = driver.ReadClob(reader, i);
        if (lob is null)
        {
            return null;
        }

        var text = new StringBuilder((int)Math.Clamp(lob.Length / 2, 0, 1 << 20));
        var buffer = new char[ClobChunk];
        int n;
        while ((n = lob.Read(buffer, 0, buffer.Length)) > 0)
        {
            text.Append(buffer, 0, n);
        }

        return text.ToString();
    }

    /// <summary>BLOB 끝까지(Read 가 0 을 줄 때까지).</summary>
    private static byte[]? ReadBlob(DbDataReader reader, TiberoDriver driver, int i)
    {
        using var lob = driver.ReadBlob(reader, i);
        if (lob is null)
        {
            return null;
        }

        using var bytes = new MemoryStream((int)Math.Clamp(lob.Length, 0, 1 << 20));
        var buffer = new byte[BlobChunk];
        int n;
        while ((n = lob.Read(buffer, 0, buffer.Length)) > 0)
        {
            bytes.Write(buffer, 0, n);
        }

        return bytes.ToArray();
    }

    /// <summary>예외 필터용: 끊김이면 세션 상태를 바꾸고, 잡지는 않는다.</summary>
    private static bool Report(TiberoSession session, Exception ex)
    {
        session.ReportFailure(ex);
        return false;
    }

    /// <summary>읽은 묶음: 행들과 리더가 끝났는지.</summary>
    private sealed record Batch(List<object?[]> Rows, bool Done);
}
