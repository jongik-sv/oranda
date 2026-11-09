using System.Reflection;
using Oranda.App.Shell.Composition;
using Oranda.Core;
using Oranda.Core.Composition;
using Oranda.Core.Composition.Dispatch;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Sessions;
using Oranda.Core.Transfer;
using Oranda.Oracle;
using Oranda.Testing;

namespace Oranda.App.Tests.Shell.Composition;

// 소유: tb-core C7(D-046). 컨테이너가 DBMS 키를 읽어 표에 넣고, 앱이 보는 서비스 형식은 Core 디스패처가 맡는다.
public class DbmsDispatchCompositionTests
{
    /// <summary>
    /// 키 등록이지만 디스패처를 두지 않는 서비스 — 호출하는 쪽이 고르기(<see cref="IDbmsServiceSelector"/>)로 바로 꺼낸다.
    /// C4 계약은 스스로 <c>Dbms</c> 를 밝히고 대부분 세션을 받지 않는다(프로필·버전 글·객체 참조) → 호출 쪽이 <c>Get&lt;T&gt;(profile.Dbms)</c>.
    /// </summary>
    private static readonly Type[] SelectorOnly =
    [
        typeof(IDbErrorClassifier),
        typeof(Oranda.Core.Connection.IConnectionProbe),
        typeof(Oranda.Core.Connection.IConnectionStringFactory),
        typeof(Oranda.Core.Sessions.IServerVersionPolicy),
        typeof(Oranda.Core.Schema.ISchemaStatements),
        typeof(Oranda.Core.Schema.IDdlBulkExporter),
        typeof(Oranda.Core.Schema.IColumnReorderScripts),
        typeof(IImportValueConverter), // 세션을 받지 않는 값 변환 — 가져오기 대화 상자가 세션 DBMS 로 Get<T>
    ];

    private static ServiceContainer Compose() =>
        AppComposition.Create(new AppCompositionOptions { DataDirectory = Path.Combine(Path.GetTempPath(), "oranda-dbms-" + Guid.NewGuid().ToString("N")) });

    [Fact]
    public void Keyed_registration_goes_to_the_dbms_table_not_to_the_service_type()
    {
        using var c = new ServiceContainer();
        c.AddAssembly(typeof(OracleAssembly).Assembly);

        Assert.Null(c.GetService<IImportValueConverter>()); // 디스패처(Core)가 없으면 서비스 형식으로는 보이지 않는다
        var converter = c.DbmsServices.Find<IImportValueConverter>(Dbms.Oracle);
        Assert.NotNull(converter);
        Assert.Equal(typeof(OracleAssembly).Assembly, converter.GetType().Assembly);
        Assert.Same(converter, c.DbmsServices.Find<IImportValueConverter>(Dbms.Oracle)); // 특성 수명(싱글턴) 그대로
        Assert.Same(c.DbmsServices, c.GetService<IDbmsServiceSelector>());
    }

    [Fact]
    public void Resolving_dispatchers_does_not_load_oracle_and_the_first_dispatch_does()
    {
        using var c = Compose();

        var writer = c.GetRequiredService<ITableSpecWriter>();
        var sessions = c.GetRequiredService<ISessionManager>();
        _ = c.GetRequiredService<IExecutionService>();
        Assert.IsType<SessionManagerDispatcher>(sessions);
        Assert.Empty(sessions.Sessions); // 「세션이 있나」만 물어도 Oracle 을 깨우지 않는다(SessionRouter.CurrentOf 의 길)
        Assert.Equal(".xlsx", writer.Extension(TableSpecFormat.Xlsx)); // 정의서 쓰기는 키 없는 Core 서비스 — 디스패처도 Oracle 로드도 없다
        Assert.False(c.DeferredScanned, c.DeferredScanTrigger);

        Assert.NotNull(c.GetRequiredService<IDbmsServiceSelector>().Find<IImportValueConverter>(Dbms.Oracle)); // 처음 DBMS 구현을 고르면 Oracle 이 로드된다

        Assert.True(c.DeferredScanned);
        Assert.Equal(typeof(IDbmsServiceSelector).FullName, c.DeferredScanTrigger);
    }

    [Fact]
    public void Explicit_registration_still_wins_over_the_dispatcher_after_the_deferred_scan()
    {
        using var c = Compose();
        var fake = new FakeExecutionService();
        c.AddInstance<IExecutionService>(fake);

        c.ScanDeferred();

        Assert.Same(fake, c.GetService<IExecutionService>());
        Assert.NotNull(c.DbmsServices.Find<IExecutionService>(Dbms.Oracle));
    }

    [Fact]
    public void After_deferred_scan_runs_once_after_the_scan_and_at_once_when_already_scanned()
    {
        using var c = new ServiceContainer();
        c.AddDeferredAssembly("Oranda.Oracle");
        var calls = 0;
        c.AfterDeferredScan(() => calls++);
        Assert.Equal(0, calls);

        c.ScanDeferred();
        c.ScanDeferred();
        Assert.Equal(1, calls);

        var late = 0;
        c.AfterDeferredScan(() => late++);
        Assert.Equal(1, late);
    }

    [Fact]
    public void Every_keyed_oracle_service_has_a_core_dispatcher()
    {
        var keyed = typeof(OracleAssembly).Assembly.GetTypes()
            .SelectMany(t => t.GetCustomAttributes<OrandaServiceAttribute>(inherit: false))
            .Where(a => a.ForDbms is not null)
            .Select(a => a.ServiceType)
            .Except(SelectorOnly)
            .Distinct()
            .ToList();
        var dispatched = typeof(CoreAssembly).Assembly.GetTypes()
            .SelectMany(t => t.GetCustomAttributes<OrandaServiceAttribute>(inherit: false).Select(a => (Type: t, Attr: a)))
            .Where(x => x.Attr.ForDbms is null && x.Attr.ServiceType.IsAssignableFrom(x.Type))
            .Select(x => x.Attr.ServiceType)
            .ToHashSet();

        Assert.NotEmpty(keyed);
        var missing = keyed.Where(t => !dispatched.Contains(t)).Select(t => t.Name).ToList();
        Assert.True(missing.Count == 0, "디스패처가 없는 키 등록 서비스: " + string.Join(", ", missing));
    }
}
