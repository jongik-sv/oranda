using Oranda.App.Features.ConnectionManager;
using Oranda.App.Shell.Composition;
using Oranda.Core.Composition;
using Oranda.Core.Connection;
using Oranda.Core.Sessions;

namespace Oranda.App.Tests.Shell.Composition;

// Tibero 드라이버 지정(결정25) 뒤 실제 앱 조립: 세션 관리자·드라이버 상태가 늘 등록되고, 접속 대화상자는 어셈블리만 있으면 Tibero 를 보여 주며,
// 드라이버를 지정하지 않았으면 상태는 「쓸 수 없음」이라 띠가 막는다.
public class TiberoDriverCompositionTests
{
    private static ServiceContainer Compose() =>
        AppComposition.Create(new AppCompositionOptions { DataDirectory = Path.Combine(Path.GetTempPath(), "oranda-tibero-drv-" + Guid.NewGuid().ToString("N")) });

    [Fact]
    public void Session_manager_and_driver_status_are_registered_for_tibero_without_a_driver()
    {
        using var c = Compose();
        var selector = c.GetRequiredService<IDbmsServiceSelector>();

        Assert.NotNull(selector.Find<ISessionManager>(Dbms.Tibero));
        var status = selector.Find<IDbmsDriverStatus>(Dbms.Tibero);
        Assert.NotNull(status);
        Assert.Equal(Dbms.Tibero, status!.Dbms);
        Assert.Null(selector.Find<IDbmsDriverStatus>(Dbms.Oracle)); // Oracle 은 지정할 드라이버가 없다
    }

    [Fact]
    public void The_connect_dialog_lists_tibero_whenever_the_assembly_is_present()
    {
        using var c = Compose();
        var selector = c.GetRequiredService<IDbmsServiceSelector>();
        var dir = Path.Combine(Path.GetTempPath(), "oranda-tibero-dir-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Assert.DoesNotContain(Dbms.Tibero, DbmsAvailability.Detect(dir, selector));

            File.WriteAllBytes(Path.Combine(dir, DbmsAvailability.TiberoAssembly + ".dll"), []);
            Assert.Contains(Dbms.Tibero, DbmsAvailability.Detect(dir, selector));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void The_driver_path_source_is_resolvable_so_the_tibero_session_manager_gets_the_settings_path()
    {
        using var c = Compose();

        var source = c.GetRequiredService<IDriverPathSource>();

        Assert.Null(source.GetPath(Dbms.Oracle));
    }
}
