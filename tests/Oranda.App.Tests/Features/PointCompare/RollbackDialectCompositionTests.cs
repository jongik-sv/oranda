using Oranda.App.Features;
using Oranda.App.Features.PointCompare;
using Oranda.App.Features.Rollback;
using Oranda.App.Shell.Composition;
using Oranda.Core.Connection;
using Oranda.Core.Grid;
using Oranda.Core.Journal;
using Oranda.Core.Metadata;
using Oranda.Core.Rollback;
using Oranda.Core.Rollback.Compare;
using Oranda.Testing;

namespace Oranda.App.Tests.Features.PointCompare;

// 소유: fbfix-1113 FX-02·FX-03. 실제 앱 조립(AppComposition·지연 어셈블리 적재)에서 롤백 방언이 DBMS 키로 꺼내지고,
// 화면 3 런처가 「이 DBMS 에서는 시점 비교를 쓸 수 없습니다」로 막히지 않는다. 가짜 선택기·DbOverride 를 쓰지 않는다
// (그 이음매 때문에 Oracle 방언 키 등록이 빠진 것을 시험이 못 잡았다).
public class RollbackDialectCompositionTests
{
    private static readonly DbObjectRef Emp = new("SCOTT", "EMP", "TABLE");

    private static ServiceContainer Compose() =>
        AppComposition.Create(new AppCompositionOptions { DataDirectory = Path.Combine(Path.GetTempPath(), "oranda-fx02-" + Guid.NewGuid().ToString("N")) });

    private static FakeSession Session(Dbms dbms) => new(TestData.Profile() with { Dbms = dbms });

    [Theory]
    [InlineData(Dbms.Oracle)]
    [InlineData(Dbms.Tibero)]
    public void Real_composition_gives_the_rollback_dialect_for_the_session_dbms(Dbms dbms)
    {
        using var c = Compose();

        var dialect = DbmsServices.For<IRollbackDialect>(c, Session(dbms));

        Assert.NotNull(dialect);
        Assert.Equal(dbms, dialect.Dbms);
    }

    [Theory]
    [InlineData(Dbms.Oracle)]
    [InlineData(Dbms.Tibero)]
    public async Task Launcher_opens_screen_3_in_the_real_composition(Dbms dbms)
    {
        using var c = Compose();
        PointCompareViewModel? shown = null;
        PointCompareLauncher.ShowOverride = vm =>
        {
            shown = vm;
            return Task.CompletedTask;
        };
        try
        {
            var args = new PointCompareArgs(Guid.NewGuid(), null, Emp, ["EMPNO"], [[7369m]], 10);
            CompareTargetRow[] targets = [new(new Dictionary<string, object?> { ["EMPNO"] = 7369m, ["SAL"] = 800m })];

            var why = await PointCompareLauncher.OpenAsync(c, args, null, default, Session(dbms), targets: targets, targetLabel: "플래시백 결과 행");

            Assert.Null(why);
            Assert.NotNull(shown);
        }
        finally
        {
            PointCompareLauncher.ShowOverride = null;
        }
    }

    [Fact]
    public void Real_composition_resolves_the_services_rollback_screens_look_up()
    {
        // FX-03: Rollback·PointCompare·Flashback·ChangeJournal 이 GetService 로 찾는 형식(없으면 기능이 조용히 꺼진다).
        using var c = Compose();

        Assert.NotNull(c.GetService<IRollbackStore>());
        Assert.NotNull(c.GetService<IRollbackLedger>());
        Assert.NotNull(c.GetService<IDmlAnalyzer>());
        Assert.NotNull(c.GetService<IChangeJournalStore>());
        Assert.NotNull(c.GetService<IRowEditor>());
        Assert.NotNull(DbmsServices.For<Oranda.Core.Catalog.ISqlCatalog>(c, Session(Dbms.Oracle)));
        Assert.NotNull(DbmsServices.For<Oranda.Core.Catalog.ISqlCatalog>(c, Session(Dbms.Tibero)));
    }
}
