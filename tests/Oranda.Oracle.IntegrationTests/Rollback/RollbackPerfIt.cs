using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Rollback;
using Oranda.Core.Rollback.Capture;
using Oranda.Core.Rollback.Ledger;
using Oranda.Core.Security;
using Oranda.Core.Sessions;
using Oranda.Language.Editing;
using Oranda.Oracle.Execution;
using Oranda.Oracle.IntegrationTests.Integration;
using Oranda.Oracle.IntegrationTests.Metadata;
using Oranda.Oracle.Metadata;
using Oranda.Oracle.Sessions;
using Oranda.Testing;
using Oranda.Testing.Rollback;
using Xunit.Abstractions;

namespace Oranda.Oracle.IntegrationTests.Rollback;

// 소유: rb-m2-oracle RB-90. 이 PC 에서는 컴파일까지만 확인한다. 실행은 CI 의 Oracle 컨테이너에서 한다(통합 시험 작업은 Category=Integration 만 돈다).

/// <summary>
/// 되돌리기 지점 성능(설계 16.4): 수치를 재서 출력하고, 상한 기본값(100,000행)과 「직후 값·RETURNING 1000개씩」 묶음 크기를 다시 정할 근거로 쓴다.
/// 단언은 기능이 맞게 돌았는지(행 수·원복 문장 수)와 넉넉한 상한(시험 하나 8분 제한 안)만 둔다. 수치는 <c>[RB-90]</c> 줄로 남긴다.
/// <list type="bullet">
/// <item>10만 행 UPDATE·DELETE: 원복 스크립트 있음·없음의 실행 시간 비, 원복 문서 크기(JSON, 암호화 뒤), 저장 시간.</item>
/// <item>그리드 한 행 반영(운영 접속): 시점 읽기 + 저장점 + 잠금 읽기 + 직후 값 읽기로 늘어나는 왕복의 시간.</item>
/// <item>층 B 만(개발 접속): 문장당 늘어나는 시간.</item>
/// </list>
/// 저장소는 RB-21(SQLite·금고) 머지 전이라 메모리 저장소와 가짜 금고(같은 AES-GCM 크기)를 쓴다. 저장 시간은 그 뒤 실제 저장소로 다시 잰다.
/// </summary>
[Trait("Category", "Integration")]
[Collection(OracleFreeShared.Name)]
public sealed class RollbackPerfIt(OracleFreeFixture db, ITestOutputHelper output) : IAsyncLifetime
{
    private const int BigRows = 100_000;
    private static readonly Dictionary<string, BindValue> NoBinds = [];
    private readonly string _table = "RBIT_PERF_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
    private readonly FakeEventBus _bus = new();
    private readonly InMemoryRollbackStore _store = new();
    private RollbackLedger _ledger = null!;
    private OracleSessionManager _manager = null!;
    private OracleExecutionService _exec = null!;
    private OracleMetadataProvider _metadata = null!;

    public async Task InitializeAsync()
    {
        _ledger = new RollbackLedger(_store, _bus);
        var metadata = _metadata = new OracleMetadataProvider(new MemoryMetadataCache(), _bus, Timeout.InfiniteTimeSpan);
        // 상한을 표 행 수와 같게 둔다(예상 행 수 > 상한일 때만 만들지 않는다). 기본값이 바뀌어도 이 시험은 10만 행을 다 담는다.
        var engine = new RollbackCaptureEngine(_ledger, () => new RollbackSettings { MaxRowsPerStatement = BigRows }, metadata, _store, events: _bus);
        _manager = new OracleSessionManager(_bus, engine, _ledger);
        _exec = new OracleExecutionService(new ExecutionPolicy(), new RecordingRollbackInteraction(), _bus, null, pageSize: 500,
            analyzer: new DmlShapeAnalyzer());
        await using var setup = await OpenAsync(ConnectionEnvironment.Development);
        await RunAsync(setup, $"CREATE TABLE {_table} (ID NUMBER PRIMARY KEY, NAME VARCHAR2(40), PRICE NUMBER(12,2), UPDATED DATE)", StatementKind.Ddl,
            ChangeRecording.None);
        await LoadAsync(setup);
    }

    public async Task DisposeAsync()
    {
        try
        {
            await using var s = await OpenAsync(ConnectionEnvironment.Development);
            await RunAsync(s, $"DROP TABLE {_table} PURGE", StatementKind.Ddl, ChangeRecording.None);
        }
        finally
        {
            foreach (var s in _manager.Sessions)
            {
                await s.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task Hundred_thousand_row_update_with_and_without_script()
    {
        await using var prod = await OpenAsync(ConnectionEnvironment.Production);
        var update = $"UPDATE {_table} SET PRICE = PRICE + 1, UPDATED = SYSDATE";
        await WarmUpAsync(prod, $"UPDATE {_table} SET PRICE = PRICE + 1, UPDATED = SYSDATE WHERE ID <= 10");

        var without = await TimeAsync(prod, update, ChangeRecording.None, rollbackAfter: true);
        var with = await TimeAsync(prod, update, ChangeRecording.Normal, rollbackAfter: false);
        var save = Stopwatch.StartNew();
        await RunAsync(prod, "COMMIT", StatementKind.TransactionControl, ChangeRecording.Normal);
        save.Stop();

        var (group, json, cipher) = await LatestDocumentAsync();
        Report("update", without.Elapsed, with.Elapsed, save.Elapsed, group, json, cipher); // 수치는 단언보다 먼저 남긴다
        Assert.Equal(BigRows, with.Result.RowsAffected);
        Assert.Equal(RollbackScriptState.Open, with.Result.Rollback?.State);
        Assert.Equal(RollbackScriptState.Confirmed, group.State);
        Assert.Equal(BigRows, group.StepCount);
    }

    [Fact]
    public async Task Hundred_thousand_row_delete_with_and_without_script()
    {
        await using var prod = await OpenAsync(ConnectionEnvironment.Production);
        var delete = $"DELETE FROM {_table}";
        await WarmUpAsync(prod, $"DELETE FROM {_table} WHERE ID <= 10");

        var without = await TimeAsync(prod, delete, ChangeRecording.None, rollbackAfter: true);
        var with = await TimeAsync(prod, delete, ChangeRecording.Normal, rollbackAfter: false);
        var save = Stopwatch.StartNew();
        await RunAsync(prod, "COMMIT", StatementKind.TransactionControl, ChangeRecording.Normal);
        save.Stop();

        var (group, json, cipher) = await LatestDocumentAsync();
        Report("delete", without.Elapsed, with.Elapsed, save.Elapsed, group, json, cipher);
        Assert.Equal(BigRows, with.Result.RowsAffected);
        Assert.Equal(RollbackScriptState.Open, with.Result.Rollback?.State);
        Assert.Equal(RollbackScriptState.Confirmed, group.State);
        Assert.Equal(BigRows, group.StepCount);
    }

    [Fact]
    public async Task Grid_single_row_edit_round_trips_on_production()
    {
        const int edits = 200;
        await using var prod = await OpenAsync(ConnectionEnvironment.Production);
        await PerStatementAsync(prod, 20, ChangeRecording.None, firstId: edits * 2 + 1); // 워밍업(두 모드, 같은 바인드 문장)
        await PerStatementAsync(prod, 20, ChangeRecording.Normal, firstId: edits * 2 + 1);
        var (plain, _) = await PerStatementAsync(prod, edits, ChangeRecording.None, firstId: 1);
        var (captured, last) = await PerStatementAsync(prod, edits, ChangeRecording.Normal, firstId: edits + 1); // 다른 행: 버퍼 캐시 효과를 나눈다
        Emit(Line("grid-row", $"none {Ms(plain)} ms/row, script {Ms(captured)} ms/row, +{Ms(captured - plain)} ms/row ({edits} rows)"));
        Assert.Equal(RollbackScriptState.Open, last.Rollback?.State); // 원복 스크립트를 실제로 만들었다
        Assert.Equal(1, last.Rollback?.StepCount);
        Assert.True(captured < TimeSpan.FromSeconds(2), "한 행 반영이 2초를 넘었다");
    }

    [Fact]
    public async Task Point_only_recording_cost_per_statement_on_development()
    {
        const int statements = 500;
        await using var dev = await OpenAsync(ConnectionEnvironment.Development);
        await PerStatementAsync(dev, 20, ChangeRecording.None, firstId: statements * 2 + 1);
        await PerStatementAsync(dev, 20, ChangeRecording.Normal, firstId: statements * 2 + 1);
        var (plain, _) = await PerStatementAsync(dev, statements, ChangeRecording.None, firstId: 1);
        var (point, last) = await PerStatementAsync(dev, statements, ChangeRecording.Normal, firstId: statements + 1); // 개발 접속: 시점(SCN)만
        Emit(Line("point-only", $"none {Ms(plain)} ms/stmt, scn {Ms(point)} ms/stmt, +{Ms(point - plain)} ms/stmt ({statements} stmts)"));
        Assert.Equal(NotCreatedReason.NotProduction, last.Rollback?.Reason); // 원복 스크립트 없이
        Assert.NotNull(last.Rollback?.BeforePoint); // 시점은 읽었다
        Assert.True(point < TimeSpan.FromSeconds(1));
    }

    /// <summary>
    /// 그리드 한 행의 늘어난 시간이 어디서 오는지(엔진이 문장마다 보내는 것을 하나씩 같은 횟수만큼): 시노님 풀기·표 정보(메타데이터 제공자),
    /// 트리거·CASCADE 찾기, 예상 행 수 <c>COUNT(*)</c>, 시점 읽기, 내부 저장점, 잠금 읽기, 직후 값 읽기.
    /// </summary>
    [Fact]
    public async Task Grid_single_row_cost_breakdown()
    {
        const int n = 200;
        await using var prod = await OpenAsync(ConnectionEnvironment.Production);
        var channel = Oranda.Oracle.Rollback.OracleRollbackChannel.Gated(prod);
        var dialect = Oranda.Oracle.Rollback.OracleRollbackDialect.Instance;
        var table = new Oranda.Core.Metadata.DbObjectRef("SYSTEM", _table, "TABLE");
        var key = new Dictionary<string, BindValue> { ["id"] = new(BindDataType.Number, 1) };
        var named = new Dictionary<string, BindValue>
        {
            ["owner"] = new(BindDataType.Varchar2, "SYSTEM"),
            ["name"] = new(BindDataType.Varchar2, _table),
        };
        var parts = new (string Name, Func<Task> Call)[]
        {
            ("name-step", () => _metadata.GetNameStepAsync(prod, "SYSTEM", _table, CancellationToken.None)),
            ("table-info", () => _metadata.GetTableAsync(prod, table, CancellationToken.None)),
            ("side-effects", () => channel.QueryAsync(dialect.SideEffectsSql!, named, 200, CancellationToken.None)),
            ("count", () => channel.QueryAsync($"SELECT COUNT(*) FROM {_table} WHERE ID = :id", key, 1, CancellationToken.None)),
            ("scn", () => channel.QueryAsync(dialect.PointQueries[0], NoBinds, 1, CancellationToken.None)),
            ("savepoint", () => channel.ExecuteAsync("SAVEPOINT ORANDA$RB", CancellationToken.None)),
            ("lock-read", () => channel.QueryAsync($"SELECT ROWIDTOCHAR(ROWID), ID, PRICE FROM {_table} WHERE ID = :id FOR UPDATE", key, 2,
                CancellationToken.None)),
            ("after-read", () => channel.QueryAsync($"SELECT ROWIDTOCHAR(ROWID), ID, TO_CHAR(PRICE, 'TM9') FROM {_table} WHERE ID = :id", key, 2,
                CancellationToken.None)),
        };
        var line = new List<string>();
        foreach (var (name, call) in parts)
        {
            await call(); // 워밍업(파싱·캐시)
            var sw = Stopwatch.StartNew();
            for (var i = 0; i < n; i++)
            {
                await call();
            }

            sw.Stop();
            line.Add($"{name} {Ms(sw.Elapsed / n)}");
        }

        await RunAsync(prod, "ROLLBACK", StatementKind.TransactionControl, ChangeRecording.None);
        Emit(Line("grid-row-parts", string.Join(", ", line) + $" ms/call ({n} calls each)"));
    }

    /// <summary>그리드 반영처럼 한 행 키 문장을 바인드로 <paramref name="count"/> 번. 문장당 평균 시간과 마지막 결과.</summary>
    private async Task<(TimeSpan PerStatement, ExecutionResult Last)> PerStatementAsync(OracleSession session, int count, ChangeRecording recording,
                                                                                       int firstId)
    {
        var text = $"UPDATE SYSTEM.{_table} SET PRICE = PRICE + 1 WHERE ID = :id"; // 그리드(RowDmlBuilder)처럼 소유자를 붙인다
        ExecutionResult last = null!;
        var sw = Stopwatch.StartNew();
        for (var i = firstId; i < firstId + count; i++)
        {
            var binds = new Dictionary<string, BindValue> { ["id"] = new(BindDataType.Number, i) };
            last = await RunAsync(session, text, StatementKind.Dml, recording, binds, ["id"]);
            Assert.Equal(1, last.RowsAffected);
        }

        sw.Stop();
        await RunAsync(session, "ROLLBACK", StatementKind.TransactionControl, recording);
        return (sw.Elapsed / count, last);
    }

    /// <summary>두 기록 방식 모두 작은 문장으로 한 번씩(파싱·메타데이터 읽기·JIT 를 비교에서 뺀다).</summary>
    private async Task WarmUpAsync(OracleSession session, string small)
    {
        foreach (var recording in new[] { ChangeRecording.None, ChangeRecording.Normal })
        {
            await RunAsync(session, small, StatementKind.Dml, recording);
            await RunAsync(session, "ROLLBACK", StatementKind.TransactionControl, ChangeRecording.None);
        }
    }

    private async Task<(TimeSpan Elapsed, ExecutionResult Result)> TimeAsync(OracleSession session, string text, ChangeRecording recording,
                                                                            bool rollbackAfter)
    {
        var sw = Stopwatch.StartNew();
        var result = await RunAsync(session, text, StatementKind.Dml, recording);
        sw.Stop();
        if (rollbackAfter)
        {
            await RunAsync(session, "ROLLBACK", StatementKind.TransactionControl, ChangeRecording.None);
        }

        return (sw.Elapsed, result);
    }

    private async Task<(RollbackGroup Group, long Json, long Cipher)> LatestDocumentAsync()
    {
        var groups = await _store.SearchAsync(new RollbackQuery(), CancellationToken.None);
        var group = groups.FirstOrDefault(g => g.Tables.Any(t => t.Name == _table));
        Assert.True(group is not null, $"저장된 묶음이 없다(전체 {groups.Count}개)");
        var document = (await _store.LoadDocumentAsync(group.Id, CancellationToken.None))!;
        var json = JsonSerializer.SerializeToUtf8Bytes(document, RollbackJsonContext.Default.RollbackScriptDocument);
        var cipher = await new FakeRollbackVault().ProtectAsync(json, System.Text.Encoding.UTF8.GetBytes($"orb1:{group.Id}:{group.DbKey}"),
            CancellationToken.None);
        return (group, json.LongLength, cipher.LongLength);
    }

    private void Report(string what, TimeSpan without, TimeSpan with, TimeSpan save, RollbackGroup group, long json, long cipher)
    {
        var ratio = without.TotalMilliseconds <= 0 ? double.NaN : with.TotalMilliseconds / without.TotalMilliseconds;
        Emit(Line(what,
            $"rows {BigRows}, none {Ms(without)} ms, script {Ms(with)} ms, ratio {ratio.ToString("0.00", CultureInfo.InvariantCulture)}, "
            + $"commit(in-memory store, RB-21 뒤 SQLite 로 다시) {Ms(save)} ms, steps {group.StepCount}, json {json / 1024} KiB, "
            + $"encrypted(fake vault) {cipher / 1024} KiB"));
    }

    private static string Line(string what, string text) => "[RB-90] " + what + ": " + text;

    /// <summary>시험 출력과 CI 작업 요약(<c>GITHUB_STEP_SUMMARY</c>, 통과한 시험의 출력은 로그에 안 나온다) 둘 다에 남긴다.</summary>
    private void Emit(string line)
    {
        output.WriteLine(line);
        RollbackCaptureIt.WriteSummary(line);
    }

    private static string Ms(TimeSpan t) => t.TotalMilliseconds.ToString("0.0", CultureInfo.InvariantCulture);

    private async Task LoadAsync(OracleSession session)
    {
        await RunAsync(session,
            $"INSERT INTO {_table} SELECT LEVEL, 'item ' || LEVEL, LEVEL / 7, DATE '2026-11-13' FROM DUAL CONNECT BY LEVEL <= {BigRows}",
            StatementKind.Dml, ChangeRecording.None);
        await RunAsync(session, "COMMIT", StatementKind.TransactionControl, ChangeRecording.None);
        Assert.Equal(BigRows, await CountAsync(session));
    }

    private async Task<long> CountAsync(OracleSession session)
    {
        await session.CommandGate.EnterAsync(CancellationToken.None);
        try
        {
            await using var cmd = session.Connection.CreateCommand();
            cmd.CommandText = $"SELECT COUNT(*) FROM {_table}";
            return Convert.ToInt64(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
        }
        finally
        {
            session.CommandGate.Release();
        }
    }

    /// <summary>문장 하나. 2분 안에 끝나지 않으면 드라이버 명령을 취소한다(시험 하나 8분 제한 안에 정리까지). 성공해야 한다.</summary>
    private async Task<ExecutionResult> RunAsync(OracleSession session, string text, StatementKind kind, ChangeRecording recording,
                                                 IReadOnlyDictionary<string, BindValue>? binds = null, IReadOnlyList<string>? bindNames = null)
    {
        var stmt = new SqlStatement(text, default, kind) { Verb = text.Split(' ')[0].ToUpperInvariant(), BindNames = bindNames ?? [] };
        var options = new ExecutionOptions { Recording = recording, PreConfirmed = new HashSet<string>(StringComparer.Ordinal) { text } };
        using var limit = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var result = await _exec.ExecuteAsync(session, stmt, binds ?? NoBinds, options, limit.Token);
        Assert.True(result.Status == ExecutionStatus.Succeeded, $"{text}: {result.Status} {result.Error?.Message}");
        return result;
    }

    private async Task<OracleSession> OpenAsync(ConnectionEnvironment environment)
    {
        var profile = new ConnectionProfile(environment == ConnectionEnvironment.Production ? "prod-example" : "dev-example", "system",
            db.DataSource, Environment: environment);
        using var cred = new SecureCredential(db.SystemPassword);
        return (OracleSession)await _manager.OpenAsync(profile, cred, SessionPurpose.Work, default);
    }
}
