using System.Globalization;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Rollback;
using Oranda.Core.Rollback.Capture;
using Oranda.Core.Rollback.Ledger;
using Oranda.Core.Rollback.Script;
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

// 소유: rb-m2-oracle RB-12. 이 PC 에서는 컴파일까지만 확인한다. 실행은 CI 의 Oracle 컨테이너에서 한다.

/// <summary>
/// 원장(RB-04)·엔진(RB-02)·분석기(RB-30)·스크립트(RB-03)와 Oracle 공급자를 한 줄로(설계 7.3·16.2): 트랜잭션 끝을 아는 자리마다 묶음 상태가 맞는가.
/// <list type="bullet">
/// <item>편집기의 <c>COMMIT</c> 문장 → 확정. 원복 스크립트 글(역순)을 그대로 실행하면 돌아온다.</item>
/// <item><c>ROLLBACK TO SAVEPOINT</c> → 그 뒤 문장만 무효.</item>
/// <item>PL/SQL 안 COMMIT·ROLLBACK → 미커밋 참 → 거짓이라 「모름」 → 표본 판정으로 커밋·롤백.</item>
/// <item>DDL 앞 암시적 커밋(성공한 DDL·실패한 DDL) → 판정.</item>
/// </list>
/// </summary>
[Trait("Category", "Integration")]
[Collection(OracleFreeShared.Name)]
public sealed class RollbackLedgerIt(OracleFreeFixture db, ITestOutputHelper output) : IAsyncLifetime
{
    private static readonly Dictionary<string, BindValue> NoBinds = [];
    private readonly string _table = "RBIT_LED_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
    private readonly FakeEventBus _bus = new();
    private readonly InMemoryRollbackStore _store = new();
    private RollbackLedger _ledger = null!;
    private OracleSessionManager _manager = null!;
    private OracleExecutionService _exec = null!;
    private OracleSession _session = null!;

    public async Task InitializeAsync()
    {
        _ledger = new RollbackLedger(_store, _bus);
        var metadata = new OracleMetadataProvider(new MemoryMetadataCache(), _bus, Timeout.InfiniteTimeSpan);
        var engine = new RollbackCaptureEngine(_ledger, () => new RollbackSettings(), metadata, _store);
        _manager = new OracleSessionManager(_bus, engine, _ledger);
        _exec = new OracleExecutionService(new ExecutionPolicy(), new RecordingRollbackInteraction(), _bus, null, pageSize: 500,
            analyzer: new DmlShapeAnalyzer());
        var profile = new ConnectionProfile("prod-example", "system", db.DataSource, Environment: ConnectionEnvironment.Production);
        using var cred = new SecureCredential(db.SystemPassword);
        _session = (OracleSession)await _manager.OpenAsync(profile, cred, SessionPurpose.Work, default);
        await RunAsync($"CREATE TABLE {_table} (ID NUMBER PRIMARY KEY, NAME VARCHAR2(40), PRICE NUMBER(10,2))", StatementKind.Ddl);
        await RunAsync($"INSERT INTO {_table} SELECT LEVEL, 'item ' || LEVEL, LEVEL * 100 FROM DUAL CONNECT BY LEVEL <= 5", StatementKind.Dml);
        await RunAsync("COMMIT", StatementKind.TransactionControl);
    }

    public async Task DisposeAsync()
    {
        try
        {
            await RunAsync("ROLLBACK", StatementKind.TransactionControl);
            await RunAsync($"DROP TABLE {_table} PURGE", StatementKind.Ddl);
            await RunAsync($"DROP TABLE {_table}_D PURGE", StatementKind.Ddl);
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
    public async Task Commit_statement_confirms_the_group_and_its_script_restores_the_rows()
    {
        await RunAsync($"UPDATE {_table} SET PRICE = PRICE + 1 WHERE ID <= 2", StatementKind.Dml);
        await RunAsync($"DELETE FROM {_table} WHERE ID = 5", StatementKind.Dml);
        await RunAsync("COMMIT", StatementKind.TransactionControl);

        var group = await LatestGroupAsync();
        Assert.Equal(RollbackScriptState.Confirmed, group.State);
        Assert.NotNull(group.BasePoint);
        Assert.NotNull(group.CommitPoint);
        var document = (await _store.LoadDocumentAsync(group.Id, CancellationToken.None))!;
        var script = RollbackScriptWriter.Write(group, document);
        output.WriteLine(script);

        foreach (var step in document.Statements.OrderByDescending(s => s.Seq).SelectMany(s => s.Steps))
        {
            Assert.Equal(ExecutionStatus.Succeeded, (await RunAsync(step.Sql, StatementKind.Dml, ChangeRecording.None)).Status);
        }

        Assert.Equal(100m, await PriceAsync(1));
        Assert.Equal(200m, await PriceAsync(2));
        Assert.Equal(500m, await PriceAsync(5));
    }

    [Fact]
    public async Task Rollback_to_savepoint_invalidates_only_the_later_statements()
    {
        await RunAsync($"UPDATE {_table} SET PRICE = 1 WHERE ID = 1", StatementKind.Dml);
        await RunAsync("SAVEPOINT SP1", StatementKind.TransactionControl);
        await RunAsync($"UPDATE {_table} SET PRICE = 2 WHERE ID = 2", StatementKind.Dml);
        await RunAsync("ROLLBACK TO SAVEPOINT SP1", StatementKind.TransactionControl);
        await RunAsync("COMMIT", StatementKind.TransactionControl);

        var group = await LatestGroupAsync();
        Assert.Equal(RollbackScriptState.Confirmed, group.State);
        Assert.Equal([false, true], group.Statements.OrderBy(s => s.Seq).Select(s => s.Invalidated));
        Assert.Equal(200m, await PriceAsync(2)); // 서버도 되돌렸다
    }

    /// <summary>
    /// PL/SQL 블록도 원복 없이 시점만 남긴 문장으로 묶음에 들어가므로(RD-20) 커밋으로 판정된 묶음은 「불완전」이다(원장 11.2: 원복이 없는 문장이 섞임).
    /// 롤백으로 판정되면 「무효」. 판정 자체(끝 종류)가 이 시험의 대상이다.
    /// </summary>
    [Theory]
    [InlineData("BEGIN COMMIT; END;", TransactionEndKind.Commit, RollbackScriptState.Incomplete)]
    [InlineData("BEGIN ROLLBACK; END;", TransactionEndKind.Rollback, RollbackScriptState.Invalid)]
    public async Task Commit_or_rollback_inside_plsql_is_probed(string block, TransactionEndKind kind, RollbackScriptState expected)
    {
        await RunAsync($"UPDATE {_table} SET PRICE = 7 WHERE ID = 3", StatementKind.Dml);
        await RunAsync(block, StatementKind.PlSql);

        var group = await LatestGroupAsync();
        output.WriteLine($"{block} → {group.State} ({group.EndKind}, {group.EndSource})");
        Assert.Equal(TransactionEndSource.PlSql, group.EndSource);
        Assert.Equal(kind, group.EndKind);
        Assert.Equal(expected, group.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ddl_after_a_change_commits_implicitly_and_is_probed(bool failingDdl)
    {
        await RunAsync($"UPDATE {_table} SET PRICE = 8 WHERE ID = 4", StatementKind.Dml);
        var ddl = failingDdl
            ? $"CREATE TABLE {_table} (X NUMBER)" // ORA-00955: 이름이 이미 있다
            : $"CREATE TABLE {_table}_D (X NUMBER)";
        var result = await RunAsync(ddl, StatementKind.Ddl);
        output.WriteLine($"{ddl} → {result.Status} {result.Error?.Message}");

        var group = await LatestGroupAsync();
        output.WriteLine($"→ {group.State} ({group.EndKind}, {group.EndSource})");
        Assert.Equal(TransactionEndSource.Ddl, group.EndSource);
        Assert.Equal(RollbackScriptState.Confirmed, group.State); // 실패한 DDL 도 앞에서 커밋했다(실측 대상)
        Assert.False(_session.HasPendingTransaction);
    }

    private async Task<RollbackGroup> LatestGroupAsync()
    {
        var groups = await _store.SearchAsync(new RollbackQuery { Table = null }, CancellationToken.None);
        return groups.First(g => g.Tables.Any(t => t.Name == _table) && g.EndKind is not null);
    }

    private async Task<ExecutionResult> RunAsync(string text, StatementKind kind, ChangeRecording recording = ChangeRecording.Normal)
    {
        var stmt = new SqlStatement(text, default, kind) { Verb = text.Split(' ')[0].ToUpperInvariant() };
        var options = new ExecutionOptions { Recording = recording, PreConfirmed = new HashSet<string>(StringComparer.Ordinal) { text } };
        return await _exec.ExecuteAsync(_session, stmt, NoBinds, options, CancellationToken.None).WaitAsync(TimeSpan.FromMinutes(2));
    }

    private async Task<decimal> PriceAsync(int id)
    {
        await _session.CommandGate.EnterAsync(CancellationToken.None);
        try
        {
            await using var cmd = _session.Connection.CreateCommand();
            cmd.CommandText = $"SELECT TO_CHAR(PRICE, 'TM9') FROM {_table} WHERE ID = {id}";
            return decimal.Parse((string)(await cmd.ExecuteScalarAsync())!, CultureInfo.InvariantCulture);
        }
        finally
        {
            _session.CommandGate.Release();
        }
    }
}
