using System.Text.Json.Nodes;
using Oranda.Core.Connection;
using Oranda.Platform.Storage;

namespace Oranda.Platform.Tests.Storage;

// 소유: TSK-03-04. connections.json 읽기·쓰기·검증(data-model 2.2).
public sealed class ConnectionStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "oranda-conn-" + Guid.NewGuid().ToString("N"));

    public ConnectionStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private ConnectionStore NewStore() => new(new AppDataPaths(_dir), []);

    private static ConnectionsDocument Doc(params ConnectionProfile[] profiles) => new() { Profiles = profiles };

    private static ConnectionProfile P(string name, string dataSource = "db.example.com:1521/orcl") =>
        new(name, "scott", dataSource);

    [Fact]
    public async Task Missing_file_loads_empty_without_creating_it()
    {
        using var store = NewStore();
        var doc = await store.LoadAsync(CancellationToken.None);
        Assert.Empty(doc.Value.Profiles);
        Assert.False(doc.IsReadOnly);
        Assert.False(File.Exists(Path.Combine(_dir, "connections.json")));
    }

    [Fact]
    public async Task Empty_folders_survive_save_and_reload_and_groups_are_normalized()
    {
        using var store = NewStore();
        await store.SaveAsync(new ConnectionsDocument { Folders = ["빈 폴더", " a / b ", "A/B"], Profiles = [new ConnectionProfile("p", "scott", "db:1521/s", Group: " dev / team ")] }, CancellationToken.None);

        var loaded = (await store.LoadAsync(CancellationToken.None)).Value;
        Assert.Equal(["빈 폴더", "a/b"], loaded.Folders.ToArray());
        Assert.Equal("dev/team", loaded.Profiles[0].Group);
    }

    [Fact]
    public async Task File_written_before_folders_existed_still_loads_with_no_folders()
    {
        File.WriteAllText(
            Path.Combine(_dir, "connections.json"),
            """{"schemaVersion":1,"profiles":[{"name":"old","user":"scott","dataSource":"db:1521/s","group":"ADB"}]}""");
        using var store = NewStore();

        var loaded = (await store.LoadAsync(CancellationToken.None)).Value;
        Assert.Empty(loaded.Folders);
        Assert.Equal("ADB", Assert.Single(loaded.Profiles).Group);
        Assert.Equal(["ADB"], ConnectionFolders.All(loaded).ToArray());
    }

    [Fact]
    public async Task Save_assigns_ids_and_round_trips_all_fields()
    {
        using var store = NewStore();
        var full = new ConnectionProfile(
            "운영 DB",
            "scott",
            "tcps://adb.example.com:1522/svc_high.adb.oraclecloud.com",
            AuthMode.SysDba,
            TnsAdmin: "C:/net",
            WalletLocation: "C:/wallet",
            ConnectTimeoutSeconds: 30,
            Environment: ConnectionEnvironment.Production,
            ReadOnly: true,
            LicensePack: LicensePack.DiagnosticsAndTuning,
            KeepAliveSeconds: 120,
            Nls: new NlsSettings { DateFormat = "YYYY-MM-DD", Sort = "KOREAN_M" },
            CredentialRef: "oranda:conn:abc",
            ProxyUser: "app",
            Color: "#3366CC",
            Group: "고객사",
            LastConnectedAt: new DateTimeOffset(2026, 10, 19, 8, 0, 0, TimeSpan.Zero));
        await store.SaveAsync(Doc(full, P("개발")), CancellationToken.None);

        using var again = NewStore();
        var loaded = (await again.LoadAsync(CancellationToken.None)).Value.Profiles;
        Assert.Equal(2, loaded.Count);
        Assert.All(loaded, p => Assert.NotEqual(Guid.Empty, p.Id));
        Assert.Equal(full with { Id = loaded[0].Id }, loaded[0]);
        Assert.Equal(ConnectionEnvironment.Development, loaded[1].Environment);
    }

    [Fact]
    public async Task Saved_file_uses_camelCase_string_enums_and_has_no_password_field()
    {
        using var store = NewStore();
        await store.SaveAsync(Doc(P("a") with { Environment = ConnectionEnvironment.Test, Auth = AuthMode.SysOper }), CancellationToken.None);
        var json = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(_dir, "connections.json")))!.AsObject();
        Assert.Equal(1, (int)json["schemaVersion"]!);
        var p = json["profiles"]![0]!.AsObject();
        Assert.Equal("Test", (string)p["environment"]!);
        Assert.Equal("SysOper", (string)p["auth"]!);
        Assert.False(p.ContainsKey("password"));
        Assert.False(p.ContainsKey("tnsAdmin"));
    }

    [Theory]
    [InlineData("host:1521/svc;Password=tiger")]
    [InlineData("host:1521/svc;PWD = tiger")]
    [InlineData("scott/tiger@host:1521/svc")]
    [InlineData("scott/tiger@//host/svc")]
    [InlineData("(DESCRIPTION=(ADDRESS=(HOST=h)(PORT=1521))(CONNECT_DATA=(SERVICE_NAME=s)));password=x")]
    public async Task Save_rejects_secrets_in_data_source(string dataSource)
    {
        using var store = NewStore();
        var ex = await Assert.ThrowsAsync<ConnectionValidationException>(() => store.SaveAsync(Doc(P("a", dataSource)), CancellationToken.None));
        Assert.Contains("비밀번호", ex.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_dir, "connections.json")));
    }

    [Theory]
    [InlineData("localhost:1521/FREEPDB1")]
    [InlineData("tcps://adb.example.com:1522/svc.adb.oraclecloud.com")]
    [InlineData("MYDB_HIGH")]
    [InlineData("(DESCRIPTION=(ADDRESS=(PROTOCOL=tcps)(HOST=h)(PORT=1522))(CONNECT_DATA=(SERVICE_NAME=s)))")]
    [InlineData("db.example.com/orcl?connect_timeout=5&transport_connect_timeout=3")]
    public async Task Save_accepts_normal_data_sources(string dataSource)
    {
        using var store = NewStore();
        await store.SaveAsync(Doc(P("a", dataSource)), CancellationToken.None);
        Assert.Single((await store.LoadAsync(CancellationToken.None)).Value.Profiles);
    }

    [Fact]
    public async Task Save_rejects_duplicate_names_ignoring_case_and_padding()
    {
        using var store = NewStore();
        var ex = await Assert.ThrowsAsync<ConnectionValidationException>(() =>
            store.SaveAsync(Doc(P("Dev"), P(" dev ")), CancellationToken.None));
        Assert.Contains("중복", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Save_rejects_blank_name_user_and_data_source_and_bad_color()
    {
        using var store = NewStore();
        var bad = new ConnectionProfile(" ", "", "", Color: "red");
        var ex = await Assert.ThrowsAsync<ConnectionValidationException>(() => store.SaveAsync(Doc(bad), CancellationToken.None));
        Assert.True(ex.Errors.Count >= 4);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-5, 0)]
    [InlineData(1, 60)]
    [InlineData(59, 60)]
    [InlineData(60, 60)]
    [InlineData(300, 300)]
    public async Task Keep_alive_between_1_and_59_is_raised_to_60(int input, int expected)
    {
        using var store = NewStore();
        await store.SaveAsync(Doc(P("a") with { KeepAliveSeconds = input }), CancellationToken.None);
        var loaded = (await store.LoadAsync(CancellationToken.None)).Value.Profiles[0];
        Assert.Equal(expected, loaded.KeepAliveSeconds);
    }

    [Fact]
    public async Task Existing_ids_are_kept()
    {
        using var store = NewStore();
        var id = Guid.NewGuid();
        await store.SaveAsync(Doc(P("a") with { Id = id }), CancellationToken.None);
        Assert.Equal(id, (await store.LoadAsync(CancellationToken.None)).Value.Profiles[0].Id);
    }

    [Fact]
    public async Task Load_keeps_invalid_profile_and_reports_it_in_notice()
    {
        var path = Path.Combine(_dir, "connections.json");
        await File.WriteAllTextAsync(path, """
            { "schemaVersion": 1, "profiles": [
              { "id": "b1f6c2d4-0000-0000-0000-000000000001", "name": "bad", "user": "u", "dataSource": "h/s;password=x" },
              { "id": "b1f6c2d4-0000-0000-0000-000000000002", "name": "ok", "user": "u", "dataSource": "h/s" } ] }
            """);
        using var store = NewStore();
        var doc = await store.LoadAsync(CancellationToken.None);
        Assert.Equal(2, doc.Value.Profiles.Count);
        Assert.Contains("bad", doc.Notice, StringComparison.Ordinal);
        Assert.DoesNotContain("ok,", doc.Notice, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Corrupt_file_is_moved_to_backup_and_default_returned()
    {
        await File.WriteAllTextAsync(Path.Combine(_dir, "connections.json"), "{ not json");
        using var store = NewStore();
        var doc = await store.LoadAsync(CancellationToken.None);
        Assert.Empty(doc.Value.Profiles);
        Assert.NotNull(doc.Notice);
        Assert.Single(Directory.GetFiles(Path.Combine(_dir, "backup"), "connections.json.corrupt-*"));
    }

    [Fact]
    public async Task Future_version_is_read_only_and_not_overwritten()
    {
        var path = Path.Combine(_dir, "connections.json");
        const string future = """{ "schemaVersion": 99, "profiles": [ { "name": "f", "user": "u", "dataSource": "h/s" } ] }""";
        await File.WriteAllTextAsync(path, future);
        using var store = NewStore();
        var doc = await store.LoadAsync(CancellationToken.None);
        Assert.True(doc.IsReadOnly);
        await store.SaveAsync(Doc(P("x")), CancellationToken.None);
        Assert.Equal(future, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public void Validator_flags_user_with_slash_and_accepts_plain_profile()
    {
        Assert.Empty(ConnectionProfileValidator.Validate(P("a")));
        Assert.NotEmpty(ConnectionProfileValidator.Validate(new ConnectionProfile("a", "scott/tiger", "h/s")));
    }
}
