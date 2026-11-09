using System.Reflection;
using Oranda.App.Shell;
using Oranda.App.Shell.Features;
using Oranda.App.Tests.TestSupport;
using Oranda.Core.Composition;
using Oranda.Core.Composition.Dispatch;
using Oranda.Core.Connection;
using Oranda.Core.Monitoring;
using Oranda.Core.Plans;

namespace Oranda.App.Tests.Shell;

// 소유: Tibero U1. 영역별 서비스 구현이 없는 DBMS 의 명령을 비활성 + 이유로 보이는 부품(DbmsServiceKeys).
[Collection("DbmsFeatureKeysSerial")]
public class DbmsServiceKeysTests
{
    private static string? Phrase(string key) => key switch
    {
        "Menu.disabled.noImplOn.Tibero" => "Tibero 에서는 아직 지원하지 않음",
        _ => null,
    };

    private static TableProvider Provider(DbmsServiceTable table) => new(table);

    private static DbmsServiceTable TiberoWithMonitorOnly()
        => new DbmsServiceTable().Add(typeof(IMonitorService), Dbms.Tibero, () => DispatchProxy.Create<IMonitorService, NullProxy>());

    [Fact]
    public void Missing_implementation_sets_the_key_while_Oracle_and_no_session_stay_enabled()
    {
        var keys = new ContextKeyService();
        var provider = Provider(TiberoWithMonitorOnly());

        DbmsServiceKeys.Apply(keys, provider, Dbms.Tibero);
        Assert.True(keys.Evaluate(DbmsServiceKeys.Enabled(DbmsServiceArea.Monitor))); // 구현이 있는 영역
        Assert.False(keys.Evaluate(DbmsServiceKeys.Enabled(DbmsServiceArea.Tuning)));
        Assert.False(keys.Evaluate(DbmsServiceKeys.Enabled(DbmsServiceArea.Metadata)));
        Assert.False(keys.Evaluate(DbmsServiceKeys.Enabled(DbmsServiceArea.DataTransfer)));

        // Oracle 은 선택기에 그 서비스가 없어도(시험 선택기) 구현이 있는 것으로 본다. 접속이 없을 때도 같다.
        DbmsServiceKeys.Apply(keys, OracleDbmsServices.Provider, Dbms.Oracle);
        Assert.All(Enum.GetValues<DbmsServiceArea>(), a => Assert.True(keys.Evaluate(DbmsServiceKeys.Enabled(a))));
        DbmsServiceKeys.Apply(keys, provider, null);
        Assert.All(Enum.GetValues<DbmsServiceArea>(), a => Assert.True(keys.Evaluate(DbmsServiceKeys.Enabled(a))));

        // 선택기가 아예 없는 공급자는 비-Oracle DBMS 에서 모두 구현 없음.
        DbmsServiceKeys.Apply(keys, null, Dbms.Tibero);
        Assert.All(Enum.GetValues<DbmsServiceArea>(), a => Assert.False(keys.Evaluate(DbmsServiceKeys.Enabled(a))));
        DbmsServiceKeys.Apply(keys, null, null);
    }

    [Fact]
    public void An_area_with_several_services_needs_all_of_them()
    {
        var table = new DbmsServiceTable().Add(typeof(IExecutionPlanService), Dbms.Tibero, () => DispatchProxy.Create<IExecutionPlanService, NullProxy>());
        Assert.True(DbmsServiceKeys.HasImplementation(Provider(table), Dbms.Tibero, DbmsServiceArea.ExecutionPlan));
        Assert.False(DbmsServiceKeys.HasImplementation(Provider(table), Dbms.Tibero, DbmsServiceArea.Metadata));
    }

    [Fact]
    public void Menu_reason_names_the_active_dbms_and_falls_back_to_the_default_wording()
    {
        var keys = new ContextKeyService();
        keys.Set("hasSession", true);
        DbmsServiceKeys.Apply(keys, Provider(TiberoWithMonitorOnly()), Dbms.Tibero);

        var expression = "hasSession && " + DbmsServiceKeys.Enabled(DbmsServiceArea.Tuning);
        Assert.False(keys.Evaluate(expression));
        Assert.Equal("Tibero 에서는 아직 지원하지 않음", ContextKeyReasons.Explain(expression, keys.Evaluate, DbmsServiceKeys.WrapPhrase(Phrase, () => Dbms.Tibero)));
        Assert.Equal("Tibero 에서는 아직 지원하지 않음", ContextKeyReasons.Explain(expression, keys.Evaluate, DbmsServiceKeys.WrapPhrase(_ => null, () => Dbms.Tibero)));
        Assert.Equal("x", DbmsServiceKeys.WrapPhrase(_ => "x")("Menu.disabled.hasSession")); // 다른 키는 그대로

        // 단추·패널용 이유: 구현이 있으면 null.
        Assert.Null(DbmsServiceKeys.Reason(Provider(TiberoWithMonitorOnly()), Dbms.Tibero, DbmsServiceArea.Monitor, Phrase));
        Assert.Null(DbmsServiceKeys.Reason(null, Dbms.Oracle, DbmsServiceArea.Tuning, Phrase));
        Assert.Equal("Tibero 에서는 아직 지원하지 않음", DbmsServiceKeys.Reason(Provider(TiberoWithMonitorOnly()), Dbms.Tibero, DbmsServiceArea.Tuning, Phrase));
        DbmsServiceKeys.Apply(keys, null, null);
    }

    [Fact]
    public void Area_table_covers_every_service_wrapped_by_a_core_dispatcher()
    {
        var dispatched = typeof(MonitorServiceDispatcher).Assembly.GetTypes()
            .Where(t => t.Namespace == typeof(MonitorServiceDispatcher).Namespace && t.IsPublic)
            .SelectMany(t => t.GetCustomAttributes<OrandaServiceAttribute>(inherit: false))
            .Select(a => a.ServiceType)
            .ToHashSet();
        Assert.NotEmpty(dispatched);

        var known = DbmsServiceKeys.Areas.Values.SelectMany(v => v).Concat(DbmsServiceKeys.NotAnArea).ToHashSet();
        Assert.Empty(dispatched.Except(known)); // 새 디스패처는 영역 표(Areas)나 NotAnArea 에 넣어야 한다
        Assert.Empty(DbmsServiceKeys.Areas.Values.SelectMany(v => v).Except(dispatched)); // 표에 있는 형식은 디스패처가 있어야 한다
        Assert.Equal(Enum.GetValues<DbmsServiceArea>().Length, DbmsServiceKeys.Areas.Count);
    }

    [Fact]
    public void Command_enablement_literals_match_the_key_names()
    {
        Assert.Equal("!noImplMonitor", DbmsServiceKeys.Enabled(DbmsServiceArea.Monitor));
        Assert.Equal("!noImplExecutionPlan", DbmsServiceKeys.Enabled(DbmsServiceArea.ExecutionPlan));
        Assert.Equal("!noImplTuning", DbmsServiceKeys.Enabled(DbmsServiceArea.Tuning));
        Assert.Equal("!noImplMetadata", DbmsServiceKeys.Enabled(DbmsServiceArea.Metadata));
        Assert.Equal("!noImplDataTransfer", DbmsServiceKeys.Enabled(DbmsServiceArea.DataTransfer));
        Assert.Equal("!noImplDatabaseInfo", DbmsServiceKeys.Enabled(DbmsServiceArea.DatabaseInfo));
    }

    private sealed class TableProvider(IDbmsServiceSelector selector) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType == typeof(IDbmsServiceSelector) ? selector : null;
    }

    public class NullProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => null;
    }
}
