using System.Runtime.CompilerServices;
using Oranda.Core.Connection;
using Oranda.Platform.Import;
using Oranda.Platform.Storage;

namespace Oranda.Platform.Tests.Import;

// 소유: TSK-03-05. 가져오기 파서·명령줄 해석 단위 시험. 예제 파일은 Samples/ 에 있다.
public class ConnectionImportTests
{
    private static string Sample(string name, [CallerFilePath] string here = "") =>
        Path.Combine(Path.GetDirectoryName(here)!, "Samples", name);

    [Fact]
    public async Task SqlDeveloper_makes_profiles_without_passwords()
    {
        var list = await new SqlDeveloperImporter().ImportAsync(Sample("sqldeveloper-connections.json"), default);

        Assert.Equal(3, list.Count);
        var hr = list[0];
        Assert.Equal("DEV_HR", hr.Profile.Name);
        Assert.Equal("hr", hr.Profile.User);
        Assert.Equal("db-dev.example.com:1521/FREEPDB1", hr.Profile.DataSource);
        Assert.Equal(0, hr.Secret.Length);
        Assert.Contains("비밀번호", hr.Warning);
        Assert.Null(hr.Profile.CredentialRef);

        var sys = list[1];
        Assert.Equal("PRODDB", sys.Profile.DataSource);
        Assert.Equal(AuthMode.SysDba, sys.Profile.Auth);
        Assert.Equal(@"C:\oracle\network\admin", sys.Profile.TnsAdmin);

        Assert.Equal("legacy-host:1521/ORCL", list[2].Profile.DataSource);
        Assert.All(list, i => Assert.Empty(ConnectionProfileValidator.Validate(i.Profile)));
    }

    [Fact]
    public void SqlDeveloper_rejects_wrong_format()
    {
        Assert.Throws<InvalidDataException>(() => SqlDeveloperImporter.Parse("{\"x\":1}"));
        Assert.Throws<InvalidDataException>(() => SqlDeveloperImporter.Parse("not json"));
    }

    [Fact]
    public async Task TnsNames_makes_one_profile_per_alias()
    {
        var path = Sample("tnsnames.ora");
        var list = await new TnsNamesImporter().ImportAsync(path, default);

        Assert.Equal(["ORCL", "ORCL_ALIAS1", "ORCL_ALIAS2", "FREEPDB1"], list.Select(i => i.Profile.Name));
        Assert.All(list, i =>
        {
            Assert.Equal(i.Profile.Name, i.Profile.DataSource);
            Assert.Equal(Path.GetDirectoryName(path), i.Profile.TnsAdmin);
            Assert.Empty(ConnectionProfileValidator.Validate(i.Profile));
        });
    }

    [Fact]
    public async Task Orange_ini_form()
    {
        var list = await new OrangeListImporter().ImportAsync(Sample("orange-ini.lst"), default);

        Assert.Equal(2, list.Count);
        Assert.Equal("개발 HR", list[0].Profile.Name);
        Assert.Equal("hr", list[0].Profile.User);
        Assert.Equal("ORCL", list[0].Profile.DataSource);
        Assert.Contains("비밀번호", list[0].Warning);
        Assert.Equal(AuthMode.SysDba, list[1].Profile.Auth);
    }

    [Fact]
    public async Task Orange_delimited_form()
    {
        var list = await new OrangeListImporter().ImportAsync(Sample("orange-delimited.lst"), default);

        Assert.Equal(2, list.Count);
        Assert.Equal("scott", list[0].Profile.User);
        Assert.Equal("TESTDB", list[0].Profile.DataSource);
        Assert.Contains("비밀번호", list[0].Warning);
        Assert.Equal(AuthMode.SysOper, list[1].Profile.Auth);
        Assert.All(list, i => Assert.Equal(0, i.Secret.Length));
    }

    [Fact]
    public void Importers_declare_extensions()
    {
        Assert.Equal([".json"], new SqlDeveloperImporter().FileExtensions);
        Assert.Equal([".ora"], new TnsNamesImporter().FileExtensions);
        Assert.Equal([".lst"], new OrangeListImporter().FileExtensions);
    }

    [Theory]
    [InlineData("scott@ORCL", "scott", "ORCL", AuthMode.Default)]
    [InlineData("scott@db:1521/svc", "scott", "db:1521/svc", AuthMode.Default)]
    [InlineData("sys@ORCL as sysdba", "sys", "ORCL", AuthMode.SysDba)]
    public void CommandLine_single_token_or_quoted(string line, string user, string source, AuthMode auth)
    {
        var parsed = CommandLineConnect.Parse([line]);
        Assert.Equal(new CommandLineTarget(user, source, auth), parsed);
    }

    [Fact]
    public void CommandLine_split_tokens_and_password_is_dropped()
    {
        Assert.Equal(
            new CommandLineTarget("sys", "ORCL", AuthMode.SysDba),
            CommandLineConnect.Parse(["sys/secret@ORCL", "AS", "SYSDBA"]));
        Assert.Null(CommandLineConnect.Parse([]));
        Assert.Null(CommandLineConnect.Parse(["--help"]));
        Assert.Null(CommandLineConnect.Parse(["@ORCL"]));
    }

    [Fact]
    public void CommandLine_finds_saved_profile_or_builds_new()
    {
        var saved = new ConnectionProfile("개발", "HR", "orcl", Id: Guid.NewGuid());
        var target = CommandLineConnect.Parse(["hr@ORCL"])!;

        Assert.Same(saved, CommandLineConnect.FindProfile([saved], target));
        Assert.Null(CommandLineConnect.FindProfile([], target));
        var fresh = CommandLineConnect.ToProfile(target);
        Assert.Equal("hr@ORCL", fresh.Name);
        Assert.Null(fresh.CredentialRef);
    }
}
