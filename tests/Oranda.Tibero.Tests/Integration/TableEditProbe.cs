using System.Globalization;
using System.Text;
using Oranda.Core.Execution;
using Oranda.Core.Metadata;
using Oranda.Tibero.Execution;
using Oranda.Tibero.Schema;
using Oranda.Tibero.Sessions;
using Oranda.Tibero.Tests.Schema;
using Xunit.Abstractions;

namespace Oranda.Tibero.Tests.Integration;

/// <summary>
/// 테이블 편집기 실측(<see cref="TableEditProbeIt"/>)의 도구: 문장을 보내 성공·실패(오류 번호·<see cref="DbErrorKind"/>)를 받고 시험 출력에 한 줄로 남긴다.
/// <list type="bullet">
/// <item>출력에는 접속 계정·DB 이름을 싣지 않는다(서버 메시지 안의 계정 이름은 <c>&lt;owner&gt;</c> 로 바꾼다).</item>
/// <item>객체 이름은 늘 <see cref="Prefix"/>(<c>ORT_TE_</c>) 로 시작한다(공유 계정, brief). 지울 때도 그 이름만 지운다.</item>
/// <item>오류 분류는 기존 <see cref="TiberoErrorClassifier.ToDbError"/> 를 그대로 쓴다(분류기가 실측 결과의 기준이다).</item>
/// </list>
/// </summary>
internal sealed class TableEditProbe(TiberoSession session, ITestOutputHelper output, string owner, string dbName)
{
    /// <summary>이 실측이 만드는 객체 이름 앞머리.</summary>
    public const string Prefix = "ORT_TE_";

    private readonly List<(string Value, string Mask)> _hidden = [];

    public TiberoSession Session => session;

    /// <summary>출력에서 가릴 값을 더한다(테이블스페이스 이름 등 회사 환경 이름).</summary>
    public void Hide(string? value, string mask)
    {
        if (!string.IsNullOrEmpty(value))
        {
            _hidden.Add((value, mask));
        }
    }

    /// <summary>접속 계정의 기본 테이블스페이스(출력에서는 <c>&lt;ts&gt;</c> 로 가린다).</summary>
    public async Task<string> DefaultTablespaceAsync()
    {
        var ts = await ScalarAsync("SELECT DEFAULT_TABLESPACE FROM USER_USERS");
        Assert.False(string.IsNullOrEmpty(ts), "default tablespace is unknown");
        Hide(ts, "<ts>");
        return ts!;
    }

    /// <summary>접속 스키마(대문자). 출력에는 싣지 않는다.</summary>
    public string Owner => owner;

    /// <summary>문장 하나의 결과. 실패면 <see cref="Code"/>(<c>TBR-n</c>)·<see cref="Kind"/>·<see cref="Message"/>(계정 이름을 가린 것).</summary>
    public sealed record Outcome(bool Ok, string? Code, DbErrorKind? Kind, string? Message)
    {
        public override string ToString() => Ok ? "OK" : $"{Code} {Kind}: {Message}";
    }

    /// <summary>메모리 캐시·기록 버스를 붙이고 백그라운드 갱신은 끈 메타데이터 제공자(<see cref="TiberoMetadataIt"/> 와 같은 꼴).</summary>
    public static TiberoMetadataProvider NewProvider()
        => new(new MemoryMetadataCache(), new RecordingEventBus(), Timeout.InfiniteTimeSpan, TiberoCatalogQuery.RunAsync);

    /// <summary>문장을 보낸다. 드라이버 예외는 결과로 바꾸고, 그 밖의 예외는 그대로 던진다.</summary>
    public async Task<Outcome> TryAsync(string sql)
    {
        try
        {
            await ExecAsync(sql);
            return new Outcome(true, null, null, null);
        }
        catch (Exception ex) when (TiberoErrorClassifier.ToDbError(ex, sql) is { } error)
        {
            return new Outcome(false, error.Code, error.Kind, Scrub(FirstLine(error.Message)));
        }
    }

    /// <summary>문장을 보내고 「[항목] 결과 :: 문장」 한 줄을 남긴다.</summary>
    public async Task<Outcome> RunAsync(string label, string sql)
    {
        var outcome = await TryAsync(sql);
        Log(label, $"{outcome} :: {Flat(sql)}");
        return outcome;
    }

    /// <summary>
    /// 문장을 보내고 결과가 <paramref name="expected"/>(<c>OK</c> 또는 <c>TBR-n</c>)인지 단언한다(실측 결과를 회귀 시험으로 묶는다).
    /// 오류 분류(<see cref="DbErrorKind"/>)는 출력에만 남긴다 — 분류기를 고치는 레인이 이 시험을 깨지 않게.
    /// </summary>
    public async Task<Outcome> ExpectAsync(string label, string expected, string sql)
    {
        var outcome = await RunAsync(label, sql);
        var actual = outcome.Ok ? "OK" : outcome.Code;
        Assert.True(string.Equals(expected, actual, StringComparison.Ordinal), $"[{label}] expected {expected}, got {Scrub(outcome.ToString())}");
        return outcome;
    }

    /// <summary>성공해야 하는 준비 문장(실패하면 그 결과를 메시지에 싣고 던진다).</summary>
    public async Task MustAsync(string sql)
    {
        var outcome = await TryAsync(sql);
        if (!outcome.Ok)
        {
            throw new InvalidOperationException($"준비 문장 실패: {outcome} :: {Flat(sql)}");
        }
    }

    /// <summary>정리용: 실패해도 무시한다(없는 객체 지우기).</summary>
    public async Task SilentAsync(string sql) => _ = await TryAsync(sql);

    /// <summary>시험 테이블을 지운다(이름은 <see cref="Prefix"/> 로 시작해야 한다).</summary>
    public Task DropTableAsync(string table)
    {
        RequirePrefix(table);
        return SilentAsync($"DROP TABLE {table} CASCADE CONSTRAINTS PURGE");
    }

    /// <summary>질의 결과 행(각 칸은 <see cref="TiberoSessionQuery.Str"/> 문자열, NULL 은 null).</summary>
    public async Task<List<string?[]>> RowsAsync(string sql)
    {
        await session.CommandGate.EnterAsync(CancellationToken.None);
        try
        {
            await using var command = session.CreateCommand(sql);
            await using var reader = await command.ExecuteReaderAsync();
            var rows = new List<string?[]>();
            while (await reader.ReadAsync())
            {
                var row = new string?[reader.FieldCount];
                for (var i = 0; i < row.Length; i++)
                {
                    row[i] = await reader.IsDBNullAsync(i) ? null : TiberoSessionQuery.Str(reader.GetValue(i));
                }

                rows.Add(row);
            }

            return rows;
        }
        finally
        {
            session.CommandGate.Release();
        }
    }

    /// <summary>첫 행 첫 칸(없으면 null).</summary>
    public async Task<string?> ScalarAsync(string sql)
    {
        var rows = await RowsAsync(sql);
        return rows.Count == 0 ? null : rows[0][0];
    }

    /// <summary>뷰·테이블의 열 이름(행 없이 읽는다). 뷰가 없으면 null.</summary>
    public async Task<IReadOnlyList<string>?> ColumnsOfAsync(string view)
    {
        await session.CommandGate.EnterAsync(CancellationToken.None);
        try
        {
            await using var command = session.CreateCommand($"SELECT * FROM {view} WHERE 1 = 0");
            await using var reader = await command.ExecuteReaderAsync();
            return [.. Enumerable.Range(0, reader.FieldCount).Select(reader.GetName)];
        }
        catch (Exception ex) when (TiberoErrorClassifier.IsDriverException(ex))
        {
            return null;
        }
        finally
        {
            session.CommandGate.Release();
        }
    }

    /// <summary>질의 결과를 줄마다 남긴다(칸은 <see cref="Show"/> 로 공백·줄바꿈이 보이게).</summary>
    public async Task<List<string?[]>> DumpAsync(string label, string sql)
    {
        var rows = await RowsAsync(sql);
        Log(label, $"rows={rows.Count} :: {Flat(sql)}");
        foreach (var row in rows)
        {
            Log(label, "  " + string.Join(" | ", row.Select(Show)));
        }

        return rows;
    }

    /// <summary><see cref="DumpAsync"/> 와 같되 질의가 실패하면 오류를 남기고 null.</summary>
    public async Task<List<string?[]>?> TryDumpAsync(string label, string sql)
    {
        try
        {
            return await DumpAsync(label, sql);
        }
        catch (Exception ex) when (TiberoErrorClassifier.ToDbError(ex, sql) is { } error)
        {
            Log(label, $"{error.Code} {error.Kind}: {FirstLine(error.Message)} :: {Flat(sql)}");
            return null;
        }
    }

    /// <summary>기존 Tibero 메타데이터 공급자가 돌려주는 열·제약·인덱스를 남긴다(거짓 차이 정규화 입력).</summary>
    public async Task<TableInfo> DumpTableInfoAsync(string label, string table)
    {
        var info = await NewProvider().GetTableAsync(session, new DbObjectRef(owner, table, "TABLE"), CancellationToken.None);
        foreach (var c in info.Columns)
        {
            Log(label, string.Create(CultureInfo.InvariantCulture,
                $"  col {c.Name} type={c.DataType} len={c.Length} byteLen={c.ByteLength} prec={c.Precision} scale={c.Scale} null={c.IsNullable} virt={c.IsVirtual} ident={c.IsIdentity} default={Show(c.DefaultValue)}"));
        }

        foreach (var k in info.Constraints)
        {
            Log(label, $"  con {k.Name} {k.Kind} cols=[{string.Join(",", k.Columns)}] enabled={k.IsEnabled} cond={Show(k.Condition)} ref={k.ReferencedTable?.Name}");
        }

        foreach (var i in info.Indexes)
        {
            Log(label, $"  idx {i.Name} unique={i.IsUnique} type={i.IndexType} cols=[{string.Join(",", i.Columns.Select(Show))}]");
        }

        return info;
    }

    public void Log(string label, string text) => output.WriteLine($"[{label}] {Scrub(text)}");

    /// <summary>값을 보이게: null → (null), 앞뒤를 ⟨⟩ 로 감싸고 줄바꿈·탭은 \n·\t 로.</summary>
    public static string Show(string? value) =>
        value is null ? "(null)" : "⟨" + value.Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal).Replace("\t", "\\t", StringComparison.Ordinal) + "⟩";

    /// <summary>같은 글자를 <paramref name="count"/> 번 잇는다(식별자 길이 실측).</summary>
    public static string Repeat(string unit, int count) => new StringBuilder(unit.Length * count).Insert(0, unit, count).ToString();

    private static void RequirePrefix(string name)
    {
        if (!name.StartsWith(Prefix, StringComparison.Ordinal) && !name.StartsWith('"' + Prefix, StringComparison.Ordinal))
        {
            throw new ArgumentException("실측 객체 이름은 " + Prefix + " 로 시작해야 한다: " + name, nameof(name));
        }
    }

    private static string FirstLine(string? message) => (message ?? string.Empty).Split('\n')[0].Trim();

    private static string Flat(string sql) => string.Join(" ", sql.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()));

    private string Scrub(string text)
    {
        var result = text;
        if (owner.Length > 0)
        {
            result = result.Replace(owner, "<owner>", StringComparison.OrdinalIgnoreCase);
        }

        if (dbName.Length > 2)
        {
            result = result.Replace(dbName, "<db>", StringComparison.OrdinalIgnoreCase);
        }

        foreach (var (value, mask) in _hidden)
        {
            result = result.Replace(value, mask, StringComparison.OrdinalIgnoreCase);
        }

        return result;
    }

    private async Task ExecAsync(string sql)
    {
        await session.CommandGate.EnterAsync(CancellationToken.None);
        try
        {
            await using var command = session.CreateCommand(sql);
            await command.ExecuteNonQueryAsync();
        }
        finally
        {
            session.CommandGate.Release();
        }
    }
}
