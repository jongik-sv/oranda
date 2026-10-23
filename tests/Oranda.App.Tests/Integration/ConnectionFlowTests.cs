using Oranda.App.Tests.Shell.Headless;
using Xunit.Abstractions;
using System.Text.Json;
using Oranda.App.Features.ConnectionImport;
using Oranda.App.Tests.Features.SqlEditor;
using Oranda.Core.Connection;
using Oranda.Core.Sessions;
using Oranda.Core.Storage;
using Oranda.Platform.Import;

namespace Oranda.App.Tests.Integration;

/// <summary>
/// TSK-11-01 시나리오 S1 「접속 가져오기 → 접속」(TSK-03-05 + 03-0x 접속 관리 + 06-02 세션 상태).
/// 다른 도구 형식 파일 → 가져오기 → 저장 → 비밀번호 입력 접속 → 사용자 확인. 상주 컨테이너 26ai 에 실제로 접속한다.
/// </summary>
[Trait("Category", "Integration")]
[Collection("GridEditStatics")]
[Oranda.App.Tests.TestSupport.ResetUiLanguage]
public sealed class ConnectionFlowTests(ItDb db, ITestOutputHelper output) : IClassFixture<ItDb>
{
    private static string Dir(string tag) => Path.Combine(Path.GetTempPath(), "oranda-itest-" + tag + "-" + Guid.NewGuid().ToString("N")[..8]);

    private string SqlDeveloperJson(params (string Name, string User)[] items) => JsonSerializer.Serialize(new
    {
        connections = items.Select(i => new
        {
            name = i.Name,
            type = "jdbc",
            info = new { user = i.User, password = "must-not-be-kept-9", hostname = db.Host, port = db.Port, serviceName = db.Service, role = "default" },
        }),
    });

    [Fact]
    public Task S1_1_Import_sql_developer_file_then_connect_with_typed_password() => Ui.Run(async () =>
    {
        if (!db.Enabled)
        {
            return;
        }

        using var rig = new FlowRig(db);
        var dir = Dir("s1");
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "connections.json");
            await File.WriteAllTextAsync(path, SqlDeveloperJson(("개발 DB(한글)", db.User)));
            var importer = rig.Container.GetService<IEnumerable<IConnectionImporter>>()!.Single(i => i.SourceName == "SQL Developer");

            var items = await importer.ImportAsync(path, default);
            var summary = await ConnectionImportCommands.SaveAsync(rig.Service, items, default);

            // 정상: 1건 저장, 이름·사용자 유지, 비밀번호는 파일에도 저장소에도 남지 않는다.
            Assert.Equal(1, summary.Added);
            Assert.Contains("비밀번호는 가져오지 않는다", Assert.Single(summary.Warnings), StringComparison.Ordinal);
            var saved = (await rig.Service.LoadAsync(default)).Value.Profiles.Single();
            Assert.Equal("개발 DB(한글)", saved.Name);
            Assert.Equal(db.User, saved.User);
            Assert.Null(saved.CredentialRef);
            var json = await File.ReadAllTextAsync(rig.Container.GetRequiredService<IAppDataPaths>().ConnectionsFile);
            Assert.DoesNotContain("must-not-be-kept-9", json, StringComparison.Ordinal);

            // 되돌림/오류: 잘못된 비밀번호 → ORA-01017, 활성 세션 없음, 상태 표시줄 세션 없음.
            var bad = await rig.TryConnectAsync(saved, "wrong-pw-1");
            Assert.False(bad.IsConnected);
            Assert.Contains("ORA-01017", bad.Message, StringComparison.Ordinal);
            Assert.Null(rig.Service.ActiveSession);
            Assert.False(rig.State.HasSession);

            // 정상: 입력한 비밀번호로 접속 → 시험 사용자, 서버 버전 26ai(23.x).
            var ok = await rig.TryConnectAsync(saved, db.Password);
            Assert.True(ok.IsConnected, ok.Message);
            await HeadlessHost.WaitUntil(() => rig.State.HasSession);
            Assert.Equal(db.User, ok.Session!.Server.SessionUser);
            Assert.StartsWith("23.", ok.ServerVersion, StringComparison.Ordinal);
            output.WriteLine("S1-1 OK: 가져오기→저장→오류 접속→정상 접속, 서버 " + ok.ServerVersion);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    });

    [Fact]
    public Task S1_2_Import_boundaries_duplicate_names_malformed_files_and_placeholder_user() => Ui.Run(async () =>
    {
        if (!db.Enabled)
        {
            return;
        }

        using var rig = new FlowRig(db);
        var dir = Dir("s1b");
        Directory.CreateDirectory(dir);
        try
        {
            var importers = rig.Container.GetService<IEnumerable<IConnectionImporter>>()!.ToList();
            var sqlDev = importers.Single(i => i.SourceName == "SQL Developer");
            var tns = importers.Single(i => i.SourceName == "tnsnames.ora");

            // 경계: 같은 이름을 두 번 가져오면 「이름 (2)」, 한 파일 안의 같은 이름도 구분된다.
            var path = Path.Combine(dir, "c.json");
            await File.WriteAllTextAsync(path, SqlDeveloperJson(("dup", db.User), ("dup", db.User)));
            await ConnectionImportCommands.SaveAsync(rig.Service, await sqlDev.ImportAsync(path, default), default);
            await ConnectionImportCommands.SaveAsync(rig.Service, await sqlDev.ImportAsync(path, default), default);
            var names = (await rig.Service.LoadAsync(default)).Value.Profiles.Select(p => p.Name).Order().ToList();
            Assert.Equal(["dup", "dup (2)", "dup (3)", "dup (4)"], names);

            // 오류: 깨진 JSON → InvalidDataException(앱은 메시지로 보인다), 아무것도 저장되지 않는다.
            var broken = Path.Combine(dir, "broken.json");
            await File.WriteAllTextAsync(broken, "{ not json");
            await Assert.ThrowsAsync<InvalidDataException>(() => sqlDev.ImportAsync(broken, default));
            await Assert.ThrowsAsync<FileNotFoundException>(() => sqlDev.ImportAsync(Path.Combine(dir, "none.json"), default));
            Assert.Equal(4, (await rig.Service.LoadAsync(default)).Value.Profiles.Count);

            // 경계: 빈 목록은 0건.
            var empty = Path.Combine(dir, "empty.json");
            await File.WriteAllTextAsync(empty, "{\"connections\":[]}");
            var none = await ConnectionImportCommands.SaveAsync(rig.Service, await sqlDev.ImportAsync(empty, default), default);
            Assert.Equal(0, none.Added);

            // tnsnames.ora: 별칭 → 프로필, 사용자 이름은 파일에 없으므로 자리 표시 + 경고. 그대로는 접속 못 한다(되돌림: 활성 세션 없음).
            var ora = Path.Combine(dir, "tnsnames.ora");
            await File.WriteAllTextAsync(ora, $"ITEST_ALIAS =\n  (DESCRIPTION = (ADDRESS = (PROTOCOL = TCP)(HOST = {db.Host})(PORT = {db.Port}))\n  (CONNECT_DATA = (SERVICE_NAME = {db.Service})))\n");
            var tnsSummary = await ConnectionImportCommands.SaveAsync(rig.Service, await tns.ImportAsync(ora, default), default);
            Assert.Equal(1, tnsSummary.Added);
            var alias = (await rig.Service.LoadAsync(default)).Value.Profiles.Single(p => p.Name == "ITEST_ALIAS");
            Assert.Equal(dir, alias.TnsAdmin);
            Assert.NotEqual(db.User, alias.User);
            Assert.False((await rig.TryConnectAsync(alias, db.Password)).IsConnected);
            Assert.Null(rig.Service.ActiveSession);

            // 사용자 이름을 고쳐 저장하면 별칭(tnsnames)으로 접속된다.
            var fixedProfile = await rig.Service.SaveProfileAsync(alias with { User = db.User }, null, false, default);
            var viaAlias = await rig.TryConnectAsync(fixedProfile, db.Password);
            Assert.True(viaAlias.IsConnected, viaAlias.Message);
            Assert.Equal(db.User, viaAlias.Session!.Server.SessionUser);
            output.WriteLine("S1-2 OK: 중복 이름 (2)(3)(4), 깨진 파일 오류, 빈 목록, tnsnames 자리 표시 사용자 → 수정 후 별칭 접속");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    });

    [Fact]
    public Task S1_3_Command_line_target_finds_the_saved_profile_and_boundaries() => Ui.Run(async () =>
    {
        if (!db.Enabled)
        {
            return;
        }

        using var rig = new FlowRig(db);
        var saved = await rig.Service.SaveProfileAsync(new ConnectionProfile("cli", db.User, db.DataSource), null, false, default);

        // 정상: user@host:port/svc 는 저장된 프로필에 대응하고, 비밀번호 부분(user/pw@…)은 버려진다.
        var target = CommandLineConnect.Parse([$"{db.User}/secret-pw@{db.DataSource}"])!;
        Assert.Equal(db.User, target.User);
        Assert.Equal(db.DataSource, target.DataSource);
        Assert.Equal(saved.Id, CommandLineConnect.FindProfile((await rig.Service.LoadAsync(default)).Value.Profiles, target)!.Id);

        // 경계: as sysdba 는 다른 인증 방식 → 같은 대상이라도 별도 해석, 접속 대상 없는 인자는 null.
        var sysdba = CommandLineConnect.Parse([$"{db.User}@{db.DataSource}", "as", "sysdba"])!;
        Assert.Equal(AuthMode.SysDba, sysdba.Auth);
        Assert.Null(CommandLineConnect.Parse(["--help"]));
        Assert.Null(CommandLineConnect.Parse(["@only"]));
        Assert.Null(CommandLineConnect.Parse([]));

        // 오류 → 정상: 못 찾는 대상은 null, 찾은 프로필로 접속된다.
        Assert.Null(CommandLineConnect.FindProfile((await rig.Service.LoadAsync(default)).Value.Profiles, new CommandLineTarget("nobody", "nowhere", AuthMode.Default)));
        await rig.ConnectAsync(saved);
        Assert.Equal(db.User, rig.Session.Server.SessionUser);
        output.WriteLine("S1-3 OK: 명령줄 해석·프로필 찾기·접속");
    });
}
