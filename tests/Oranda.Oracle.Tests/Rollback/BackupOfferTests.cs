using Oracle.ManagedDataAccess.Client;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Journal;
using Oranda.Core.Sessions;
using Oranda.Oracle.Execution;
using Oranda.Oracle.Sessions;
using Oranda.Testing;

namespace Oranda.Oracle.Tests.Rollback;

// 소유: rb-m2-oracle RB-44. 백업 테이블 권유를 어디서 하는가(RD-17). DB 없음: CTAS 생성·실패 시 DDL 을 보내지 않음은 RB-12 통합 시험.
public sealed class BackupOfferTests
{
    private static readonly SqlStatement Truncate = new("TRUNCATE TABLE SAMPLE.PRODUCTS", default, StatementKind.Ddl)
    {
        ChangeTarget = new ChangeTarget("SAMPLE", "PRODUCTS", "TABLE", ChangeOperation.Truncate) { DataLoss = DataLossKind.Truncate },
    };

    private static async Task<(Core.Rollback.BackupOffer? Offer, bool GateFree)> OfferAsync(ConnectionEnvironment environment, bool readOnly = false)
    {
        await using var connection = new OracleConnection("User Id=u;Password=p;Data Source=localhost:1/x");
        var info = new ServerInfo("19.0.0.0.0", "ORCL", "localhost", "ORCLPDB1", "ORCL", "ORCLPDB1", true, 1,
            new ServerCapabilities(false, true, true, false, false));
        var profile = new ConnectionProfile("prod-example", "scott", "localhost:1521/ORCLPDB1", Environment: environment, ReadOnly: readOnly);
        await using var session = new OracleSession(Guid.NewGuid(), profile, info, SessionPurpose.Work, "K", connection, new FakeEventBus(),
            closeConnection: () => ValueTask.CompletedTask);

        var offer = await OracleExecutionService.OfferBackupAsync(session, Truncate, CancellationToken.None);
        var free = session.CommandGate.TryEnter();
        if (free)
        {
            session.CommandGate.Release();
        }

        return (offer, free);
    }

    [Theory]
    [InlineData(ConnectionEnvironment.Development, false)] // 확인 창이 없는 접속(D-065)
    [InlineData(ConnectionEnvironment.Production, true)]   // 읽기 전용: 정책이 DDL 을 거부한다
    public async Task Not_offered_where_no_confirmation_is_asked(ConnectionEnvironment environment, bool readOnly)
    {
        var (offer, free) = await OfferAsync(environment, readOnly);
        Assert.Null(offer);
        Assert.True(free);
    }

    [Fact]
    public async Task Dictionary_read_failure_means_no_offer_and_the_statement_goes_on()
    {
        // 열지 않은 연결: 현재 스키마 읽기가 실패한다(권한 없음과 같은 갈래). 예외 없이 권하지 않는다.
        var (offer, free) = await OfferAsync(ConnectionEnvironment.Production);
        Assert.Null(offer);
        Assert.True(free);
    }

    [Fact]
    public async Task Statements_without_data_loss_are_not_offered()
    {
        await using var connection = new OracleConnection("User Id=u;Password=p;Data Source=localhost:1/x");
        var info = new ServerInfo("19.0.0.0.0", "ORCL", "localhost", "ORCLPDB1", "ORCL", "ORCLPDB1", true, 1,
            new ServerCapabilities(false, true, true, false, false));
        var profile = new ConnectionProfile("prod-example", "scott", "localhost:1521/ORCLPDB1", Environment: ConnectionEnvironment.Production);
        await using var session = new OracleSession(Guid.NewGuid(), profile, info, SessionPurpose.Work, "K", connection, new FakeEventBus(),
            closeConnection: () => ValueTask.CompletedTask);
        var drop = new SqlStatement("DROP TABLE SAMPLE.PRODUCTS", default, StatementKind.Ddl)
        {
            ChangeTarget = new ChangeTarget("SAMPLE", "PRODUCTS", "TABLE", ChangeOperation.Drop),
        };

        Assert.Null(await OracleExecutionService.OfferBackupAsync(session, drop, CancellationToken.None));
    }
}
