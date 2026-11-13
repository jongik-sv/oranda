using System.Globalization;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Execution.Providers;
using Oranda.Core.Journal;
using Oranda.Core.Rollback;
using Oranda.Core.Rollback.Backup;
using Oranda.Core.Security;
using Oranda.Core.Sessions;
using Oranda.Oracle.Execution;
using Oranda.Oracle.IntegrationTests.Integration;
using Oranda.Oracle.Sessions;
using Oranda.Testing;
using Xunit.Abstractions;

namespace Oranda.Oracle.IntegrationTests.Rollback;

// 소유: rb-m2-oracle RB-44·RB-12. 이 PC 에서는 컴파일까지만 확인한다. 실행은 CI 의 Oracle 컨테이너에서 한다.

/// <summary>
/// 데이터가 사라지는 DDL 앞 백업 테이블(설계 9장, RD-17, 16.2 「CTAS 권유」): 운영 접속 확인 창이 제안을 받고, 고르면 원래 DDL 앞에서 CTAS 가 돈다.
/// CTAS 가 실패하면 원래 DDL 을 보내지 않는다. 개발 접속은 묻지 않으므로 권하지 않는다.
/// </summary>
[Trait("Category", "Integration")]
[Collection(OracleFreeShared.Name)]
public sealed class RollbackBackupIt(OracleFreeFixture db, ITestOutputHelper output) : IAsyncLifetime
{
    private static readonly Dictionary<string, BindValue> NoBinds = [];
    private readonly string _table = "RBIT_BAK_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
    private readonly List<string> _drops = [];
    private readonly FakeEventBus _bus = new();
    private readonly BackupInteraction _ui = new();
    private OracleSessionManager _manager = null!;
    private OracleExecutionService _exec = null!;
    private OracleSession _session = null!;

    public async Task InitializeAsync()
    {
        _manager = new OracleSessionManager(_bus);
        _exec = new OracleExecutionService(new ExecutionPolicy(), _ui, _bus, null, pageSize: 500);
        _session = await OpenAsync(ConnectionEnvironment.Production);
        await ExecAsync($"CREATE TABLE {_table} (ID NUMBER PRIMARY KEY, NAME VARCHAR2(40), MEMO VARCHAR2(40))");
        await ExecAsync($"INSERT INTO {_table} SELECT LEVEL, 'item ' || LEVEL, 'memo ' || LEVEL FROM DUAL CONNECT BY LEVEL <= 30");
        await ExecAsync("COMMIT");
        _drops.Add(_table);
    }

    public async Task DisposeAsync()
    {
        try
        {
            foreach (var table in _drops)
            {
                try
                {
                    await ExecAsync($"DROP TABLE \"{table}\" PURGE");
                }
                catch (global::Oracle.ManagedDataAccess.Client.OracleException)
                {
                    // 시험이 지운 표.
                }
            }
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
    public async Task Truncate_with_backup_copies_rows_first_and_journals_the_backup_name()
    {
        _ui.TakeBackup = true;
        var result = await RunAsync(Truncate());

        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        var offer = Assert.Single(_ui.Offers);
        _drops.Add(offer.BackupName);
        output.WriteLine(offer.CreateSql);
        Assert.Equal(30, await CountAsync($"\"SYSTEM\".\"{offer.BackupName}\""));
        Assert.Equal(0, await CountAsync(_table));
        Assert.Contains(offer.BackupName, result.Rollback!.LogLine, StringComparison.Ordinal);
        var journal = _bus.PublishedOf<ChangeSnapshotCaptured>().Single(e => e.Entry.Name == _table);
        Assert.Contains("백업 테이블 SYSTEM." + offer.BackupName, journal.Entry.StatementSummary, StringComparison.Ordinal);
        Assert.NotNull(journal.Entry.PreviousDdl); // TRUNCATE 도 표 DDL 을 보관한다
    }

    [Fact]
    public async Task Failed_backup_does_not_send_the_ddl()
    {
        var taken = _table + "_X";
        await ExecAsync($"CREATE TABLE {taken} (A NUMBER)");
        _drops.Add(taken);
        _ui.TakeBackup = true;
        _ui.RenameTo = taken; // 이미 있는 이름: ORA-00955

        var result = await RunAsync(Truncate());

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Equal(OracleExecutionService.BackupFailedCode, result.Error!.Code);
        Assert.Equal(30, await CountAsync(_table)); // TRUNCATE 를 보내지 않았다
    }

    [Fact]
    public async Task Drop_column_backup_keeps_the_key_and_the_dropped_column()
    {
        _ui.TakeBackup = true;
        var drop = new SqlStatement($"ALTER TABLE {_table} DROP COLUMN MEMO", default, StatementKind.Ddl)
        {
            Verb = "ALTER",
            ChangeTarget = new ChangeTarget(null, _table, "TABLE", ChangeOperation.Alter) { DataLoss = DataLossKind.DropColumns, DroppedColumns = ["MEMO"] },
        };

        var result = await RunAsync(drop);

        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        var offer = Assert.Single(_ui.Offers);
        _drops.Add(offer.BackupName);
        Assert.Contains("SELECT \"ID\", \"MEMO\" FROM", offer.CreateSql, StringComparison.Ordinal);
        Assert.Equal(30, await CountAsync($"\"SYSTEM\".\"{offer.BackupName}\"", "MEMO LIKE 'memo %'"));
    }

    [Fact]
    public async Task Development_connections_are_not_offered_a_backup()
    {
        await using var dev = await OpenAsync(ConnectionEnvironment.Development);
        _ui.TakeBackup = true;

        var result = await _exec.ExecuteAsync(dev, Truncate(), NoBinds, null, CancellationToken.None);

        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        Assert.Empty(_ui.Offers);
    }

    private SqlStatement Truncate() => new($"TRUNCATE TABLE {_table}", default, StatementKind.Ddl)
    {
        Verb = "TRUNCATE",
        DangerousTarget = _table,
        ChangeTarget = new ChangeTarget(null, _table, "TABLE", ChangeOperation.Truncate) { DataLoss = DataLossKind.Truncate },
    };

    private Task<ExecutionResult> RunAsync(SqlStatement stmt) =>
        _exec.ExecuteAsync(_session, stmt, NoBinds, null, CancellationToken.None).WaitAsync(TimeSpan.FromMinutes(2));

    private async Task<OracleSession> OpenAsync(ConnectionEnvironment environment)
    {
        var profile = new ConnectionProfile(environment == ConnectionEnvironment.Production ? "prod-example" : "dev-example", "system",
            db.DataSource, Environment: environment);
        using var cred = new SecureCredential(db.SystemPassword);
        return (OracleSession)await _manager.OpenAsync(profile, cred, SessionPurpose.Work, default);
    }

    private async Task<long> CountAsync(string table, string? where = null)
    {
        await _session.CommandGate.EnterAsync(CancellationToken.None);
        try
        {
            await using var cmd = _session.Connection.CreateCommand();
            cmd.CommandText = $"SELECT COUNT(*) FROM {table}" + (where is null ? string.Empty : " WHERE " + where);
            return Convert.ToInt64(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
        }
        finally
        {
            _session.CommandGate.Release();
        }
    }

    private async Task ExecAsync(string sql)
    {
        await _session.CommandGate.EnterAsync(CancellationToken.None);
        try
        {
            await using var cmd = _session.Connection.CreateCommand();
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync();
        }
        finally
        {
            _session.CommandGate.Release();
        }
    }

    /// <summary>확인 창: 모든 확인에 진행으로 답하고, 백업 제안이 있으면 고른다(이름을 바꿀 수 있다).</summary>
    private sealed class BackupInteraction : IExecutionInteraction
    {
        public bool TakeBackup { get; set; }

        public string? RenameTo { get; set; }

        public List<BackupOffer> Offers { get; } = [];

        public Task<bool> ConfirmAsync(SqlStatement stmt, PolicyDecision decision, CancellationToken ct) => Task.FromResult(true);

        public Task<ConfirmAnswer> ConfirmChangeAsync(SqlStatement stmt, PolicyDecision decision, ConfirmExtras extras, CancellationToken ct)
        {
            if (extras.Backup is not { } offer || !TakeBackup)
            {
                return Task.FromResult(ConfirmAnswer.Default(extras));
            }

            Offers.Add(offer);
            var chosen = RenameTo is null ? offer : BackupPlanner.Rename(offer, RenameTo)!;
            return Task.FromResult(ConfirmAnswer.Default(extras) with { BackupSql = chosen.CreateSql });
        }

        public Task<IReadOnlyDictionary<string, string>?> AskSubstitutionsAsync(IReadOnlyList<string> names, CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<string, string>?>(null);

        public Task<ScriptErrorAction> AskScriptErrorAsync(SqlStatement stmt, DbError failure, CancellationToken ct) =>
            Task.FromResult(ScriptErrorAction.Stop);
    }
}
