using Oranda.App.Shell.Composition;
using Oranda.Core.Catalog;
using Oranda.Core.Composition;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Sessions;
using Oranda.Tibero;

namespace Oranda.App.Tests.Shell.Composition;

// 소유: tb-skel S3(D-046). 앱 조립이 Oranda.Tibero 를 늦게 읽어 Dbms.Tibero 키 구현을 DBMS 표에 올린다.
public class TiberoCompositionTests
{
    private static ServiceContainer Compose() =>
        AppComposition.Create(new AppCompositionOptions { DataDirectory = Path.Combine(Path.GetTempPath(), "oranda-tibero-" + Guid.NewGuid().ToString("N")) });

    [Fact]
    public void Tibero_assembly_is_deferred_so_startup_does_not_load_it()
    {
        Assert.Contains("Oranda.Tibero", AppComposition.DeferredAssemblies);
        using var c = Compose();
        Assert.False(c.DeferredScanned, c.DeferredScanTrigger);
    }

    [Fact]
    public void A_missing_optional_deferred_assembly_is_skipped_and_the_others_still_register()
    {
        using var c = new ServiceContainer();
        c.AddDeferredAssembly("Oranda.DoesNotExist", optional: true);
        c.AddDeferredAssembly("Oranda.Oracle");

        c.ScanDeferred(); // Oranda.Tibero 가 없는 배포를 흉내 낸다 — 던지지 않는다

        Assert.True(c.DeferredScanned);
        Assert.Contains(Dbms.Oracle, c.DbmsServices.AvailableDbms);
    }

    [Fact]
    public void A_missing_required_deferred_assembly_still_fails_loudly()
    {
        using var c = new ServiceContainer();
        c.AddDeferredAssembly("Oranda.DoesNotExist");

        Assert.Throws<FileNotFoundException>(() => c.ScanDeferred());
    }

    [Fact]
    public void Only_tibero_is_optional_in_the_app_composition()
    {
        Assert.Equal(["Oranda.Oracle", "Oranda.Language", "Oranda.Tibero"], AppComposition.DeferredAssemblies);
    }

    [Fact]
    public void The_first_dbms_pick_loads_tibero_and_finds_every_keyed_service()
    {
        using var c = Compose();
        var selector = c.GetRequiredService<IDbmsServiceSelector>();

        Assert.Contains(Dbms.Tibero, selector.AvailableDbms);
        Assert.Contains(Dbms.Oracle, selector.AvailableDbms);
        Assert.Equal(Dbms.Tibero, selector.Get<IDbErrorClassifier>(Dbms.Tibero).Dbms);
        Assert.Equal(Dbms.Tibero, selector.Get<IConnectionProbe>(Dbms.Tibero).Dbms);
        Assert.Equal(Dbms.Tibero, selector.Get<IConnectionStringFactory>(Dbms.Tibero).Dbms);
        Assert.Equal(Dbms.Tibero, selector.Get<IServerVersionPolicy>(Dbms.Tibero).Dbms);
        Assert.NotNull(selector.Get<ISqlCatalog>(Dbms.Tibero));
        Assert.Equal(typeof(TiberoAssembly).Assembly, selector.Get<IConnectionProbe>(Dbms.Tibero).GetType().Assembly);
    }

    [Fact]
    public void Oracle_keys_stay_with_oracle_after_tibero_registers()
    {
        using var c = Compose();
        var selector = c.GetRequiredService<IDbmsServiceSelector>();

        Assert.Equal(Dbms.Oracle, selector.Get<IDbErrorClassifier>(Dbms.Oracle).Dbms);
        Assert.Equal(Dbms.Oracle, selector.Get<IConnectionProbe>(Dbms.Oracle).Dbms);
        Assert.NotSame(selector.Get<IDbErrorClassifier>(Dbms.Oracle), selector.Get<IDbErrorClassifier>(Dbms.Tibero));
    }

    [Fact]
    public async Task Tibero_probe_without_a_driver_says_so_in_words_a_user_understands()
    {
        if (TiberoDriverInfo.IsLinked)
        {
            return; // 드라이버 있는 빌드는 실제 접속을 시도하므로 여기서는 다루지 않는다.
        }

        using var c = Compose();
        var probe = c.GetRequiredService<IDbmsServiceSelector>().Get<IConnectionProbe>(Dbms.Tibero);
        var profile = new ConnectionProfile("t", "u", "h:8629/d", Dbms: Dbms.Tibero);

        var result = await probe.ProbeAsync(profile, "pw", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("Tibero", result.Message, StringComparison.Ordinal);
        Assert.Contains("드라이버", result.Message, StringComparison.Ordinal);
    }
}
