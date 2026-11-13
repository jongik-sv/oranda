using System.Data;
using Oranda.Core.Connection;
using Oranda.Core.Events;
using Oranda.Core.Execution;
using Oranda.Core.Journal;
using Oranda.Core.Rollback;
using Oranda.Core.Sessions;
using Oranda.Testing.Rollback;
using Oranda.Tibero.Execution;
using Oranda.Tibero.Sessions;
using Oranda.Tibero.Tests.Driver;

namespace Oranda.Tibero.Tests.Rollback;

/// <summary>데이터가 사라지는 DDL 앞 백업 테이블 권유와 CTAS(RB-14, 설계 9장, RD-17, 가짜 드라이버).</summary>
[Collection("FakeDriver")]
public sealed class TiberoBackupOfferTests : IDisposable
{
    private static readonly ServerInfo Server = new("6", "i", "h", null, "D", null, false, 7,
        new ServerCapabilities(false, false, true, false, false, Dbms.Tibero), DbUniqueName: "D", SessionUser: "U", Dbms: Dbms.Tibero);

    private static readonly SqlStatement Truncate = new("TRUNCATE TABLE SAMPLE.PRODUCTS", default, StatementKind.Ddl)
    {
        Verb = "TRUNCATE",
        ChangeTarget = new ChangeTarget("SAMPLE", "PRODUCTS", "TABLE", ChangeOperation.Truncate) { DataLoss = DataLossKind.Truncate },
    };

    private static readonly IReadOnlyDictionary<string, BindValue> NoBinds = new Dictionary<string, BindValue>();

    private readonly EventBus _bus = new();
    private readonly List<string> _sent = [];

    public TiberoBackupOfferTests() => FakeDriverHost.Reset();

    public void Dispose() => FakeDriverHost.Reset();

    [Fact]
    public async Task Production_truncate_offers_a_backup_and_the_chosen_ctas_runs_before_the_ddl()
    {
        Serve(failCtas: false);
        var interaction = new RecordingRollbackInteraction();
        var service = new TiberoExecutionService(new ConfirmAll(), interaction, _bus, null, dbmsOutput: false);
        await using var session = NewSession(ConnectionEnvironment.Production);
        interaction.Answer = null;

        // 첫 실행으로 제안을 받아 그 CTAS 를 고른 답을 정한다.
        var offer = await TiberoExecutionService.OfferBackupAsync(session, Truncate, CancellationToken.None);
        Assert.NotNull(offer);
        Assert.StartsWith("ORB_PRODUCTS_", offer.BackupName, StringComparison.Ordinal);
        interaction.Answer = new ConfirmAnswer(true, false, offer.CreateSql);
        _sent.Clear();

        var result = await service.ExecuteAsync(session, Truncate, NoBinds, null, CancellationToken.None);

        Assert.Equal(ExecutionStatus.Succeeded, result.Status);
        Assert.Equal(offer.Table, Assert.Single(interaction.Asked).Extras.Backup!.Table);
        var ctas = _sent.FindIndex(t => t.StartsWith("CREATE TABLE", StringComparison.Ordinal));
        Assert.True(ctas >= 0 && ctas < _sent.IndexOf(Truncate.Text));
        Assert.Contains("백업 테이블", result.Rollback!.LogLine, StringComparison.Ordinal);
        Assert.False(session.CommandGate.IsHeld);
    }

    [Fact]
    public async Task Failed_ctas_does_not_send_the_ddl()
    {
        Serve(failCtas: true);
        var interaction = new RecordingRollbackInteraction();
        var service = new TiberoExecutionService(new ConfirmAll(), interaction, _bus, null, dbmsOutput: false);
        await using var session = NewSession(ConnectionEnvironment.Test);
        var offer = await TiberoExecutionService.OfferBackupAsync(session, Truncate, CancellationToken.None);
        interaction.Answer = new ConfirmAnswer(true, false, offer!.CreateSql);
        _sent.Clear();

        var result = await service.ExecuteAsync(session, Truncate, NoBinds, null, CancellationToken.None);

        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.Equal(TiberoExecutionService.BackupFailedCode, result.Error!.Code);
        Assert.DoesNotContain(Truncate.Text, _sent);
    }

    [Fact]
    public async Task Cancel_after_the_ctas_ran_does_not_send_the_ddl_but_reports_the_backup_table()
    {
        using var cts = new CancellationTokenSource();
        Serve(failCtas: false, afterCtas: cts.Cancel);
        var interaction = new RecordingRollbackInteraction();
        var service = new TiberoExecutionService(new ConfirmAll(), interaction, _bus, null, dbmsOutput: false);
        await using var session = NewSession(ConnectionEnvironment.Production);
        var offer = await TiberoExecutionService.OfferBackupAsync(session, Truncate, CancellationToken.None);
        interaction.Answer = new ConfirmAnswer(true, false, offer!.CreateSql);
        _sent.Clear();

        var result = await service.ExecuteAsync(session, Truncate, NoBinds, null, cts.Token);

        Assert.Equal(ExecutionStatus.Cancelled, result.Status);
        Assert.DoesNotContain(Truncate.Text, _sent);
        Assert.Contains(offer.BackupName, result.Rollback!.LogLine, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Development_connection_and_ddl_without_data_loss_are_not_offered_and_read_nothing()
    {
        Serve(failCtas: false);
        await using var dev = NewSession(ConnectionEnvironment.Development);
        await using var prod = NewSession(ConnectionEnvironment.Production);

        Assert.Null(await TiberoExecutionService.OfferBackupAsync(dev, Truncate, CancellationToken.None));
        Assert.Null(await TiberoExecutionService.OfferBackupAsync(prod, new SqlStatement("CREATE TABLE X (A NUMBER)", default, StatementKind.Ddl), CancellationToken.None));
        Assert.Empty(_sent);
    }

    private TiberoSession NewSession(ConnectionEnvironment environment)
    {
        var driver = FakeDriverHost.Driver;
        var connection = driver.CreateConnection("Data Source=x");
        connection.Open();
        var profile = new ConnectionProfile("prod-example", "u", "h:1/d", Dbms: Dbms.Tibero, Environment: environment);
        return new TiberoSession(Guid.NewGuid(), profile, Server, SessionPurpose.Work, "D/-/U", connection, null, _bus, driver: driver);
    }

    /// <summary>현재 스키마 질의 = (SAMPLE, U), CTAS 는 실패(선택), 그 밖은 영향 행 0.</summary>
    private void Serve(bool failCtas, Action? afterCtas = null) =>
        FakeDriverHost.SetHandler(cmd =>
        {
            lock (_sent)
            {
                _sent.Add(cmd.CommandText);
            }

            if (cmd.CommandText.Contains("SYS_CONTEXT", StringComparison.Ordinal))
            {
                var table = new DataTable();
                table.Columns.Add("A", typeof(string));
                table.Columns.Add("B", typeof(string));
                table.Rows.Add("SAMPLE", "U");
                return table;
            }

            if (failCtas && cmd.CommandText.StartsWith("CREATE TABLE", StringComparison.Ordinal))
            {
                throw FakeDriverHost.ServerError(-8053, "TBR-8053: Not enough privilege.");
            }

            if (cmd.CommandText.StartsWith("CREATE TABLE", StringComparison.Ordinal))
            {
                afterCtas?.Invoke();
            }

            return 0;
        });

    private sealed class ConfirmAll : IExecutionPolicy
    {
        public ValueTask<PolicyDecision> EvaluateAsync(PolicyContext ctx, SqlStatement stmt, CancellationToken ct) =>
            ValueTask.FromResult(new PolicyDecision(PolicyDecisionKind.Confirm));
    }
}
