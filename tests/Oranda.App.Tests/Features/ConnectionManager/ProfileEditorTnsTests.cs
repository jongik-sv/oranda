using Oranda.App.Features.ConnectionManager;
using Oranda.Core.Connection;

namespace Oranda.App.Tests.Features.ConnectionManager;

/// <summary>TNS 디렉터리를 넣으면 tnsnames.ora 의 별칭을 제안하고 미리 채운다(접속 방식·별칭·이름·접속 요약).</summary>
public sealed class ProfileEditorTnsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("oranda-tns-").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    private string Folder(string name, string tnsnames, string fileName = "TNSNAMES.ORA")
    {
        var dir = Path.Combine(_dir, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, fileName), tnsnames);
        return dir;
    }

    private const string Two = """
        # 시험용
        TESTB =
          (DESCRIPTION =
            (ADDRESS = (PROTOCOL = TCP)(HOST = hostb)(PORT = 1522))
            (CONNECT_DATA = (SERVICE_NAME = svcb)))
        testa = (DESCRIPTION = (ADDRESS = (PROTOCOL = TCP)(HOST = hosta)(PORT = 1521)) (CONNECT_DATA = (SID = orcl)))
        """;

    [Fact]
    public void Tns_folder_lists_aliases_switches_to_tns_mode_and_alias_fills_name_and_summary()
    {
        var vm = ProfileEditorViewModel.ForNew();
        Assert.Equal(ConnectionMode.HostPortService, vm.Mode);

        vm.TnsAdmin = Folder("two", Two); // 대문자 TNSNAMES.ORA 도 찾는다

        Assert.Equal(ConnectionMode.TnsAlias, vm.Mode);
        Assert.Equal(["testa", "TESTB"], vm.TnsAliases);
        Assert.Equal(string.Empty, vm.TnsAlias); // 둘 이상이면 고르게 둔다
        Assert.Contains("2", vm.TnsAliasDetail, StringComparison.Ordinal);

        vm.TnsAlias = "TESTB";
        Assert.Equal("TESTB", vm.Name);
        Assert.Equal("→ hostb:1522/svcb", vm.TnsAliasDetail);

        vm.TnsAlias = "testa"; // 별칭으로 채운 이름은 별칭을 따라간다
        Assert.Equal("testa", vm.Name);
        Assert.Equal("→ hosta:1521:orcl", vm.TnsAliasDetail);

        vm.Name = "내 접속";
        vm.TnsAlias = "TESTB"; // 사람이 고친 이름은 그대로
        Assert.Equal("내 접속", vm.Name);
        Assert.Contains("TESTB", vm.ConnectionStringPreview, StringComparison.Ordinal);
    }

    [Fact]
    public void Single_alias_is_filled_in_and_host_mode_with_a_host_is_kept()
    {
        var one = Folder("one", "ONLY = (DESCRIPTION = (ADDRESS = (HOST = h1)(PORT = 1521)) (CONNECT_DATA = (SERVICE_NAME = s1)))", "tnsnames.ora");

        var vm = ProfileEditorViewModel.ForNew();
        vm.TnsAdmin = one;
        Assert.Equal("ONLY", vm.TnsAlias);
        Assert.Equal("ONLY", vm.Name);

        var withHost = ProfileEditorViewModel.ForNew();
        withHost.Host = "db.example";
        withHost.TnsAdmin = one; // 이미 호스트를 넣었으면 접속 방식은 바꾸지 않는다(TNS 디렉터리는 지갑·tcps 에도 쓴다)
        Assert.Equal(ConnectionMode.HostPortService, withHost.Mode);
        Assert.Equal(["ONLY"], withHost.TnsAliases);
    }

    [Fact]
    public void Missing_file_or_unknown_alias_is_explained_and_loading_a_profile_does_not_rename_it()
    {
        var empty = Path.Combine(_dir, "empty");
        Directory.CreateDirectory(empty);
        var vm = ProfileEditorViewModel.ForNew();
        vm.Mode = ConnectionMode.TnsAlias;
        vm.TnsAdmin = empty;
        Assert.Empty(vm.TnsAliases);
        Assert.NotEmpty(vm.TnsAliasDetail);

        vm.TnsAdmin = Folder("two", Two);
        vm.TnsAlias = "NOPE";
        Assert.NotEmpty(vm.TnsAliasDetail);
        Assert.DoesNotContain("→", vm.TnsAliasDetail, StringComparison.Ordinal);

        var loaded = ProfileEditorViewModel.From(new ConnectionProfile("사내 테스트", "scott", "TESTB", TnsAdmin: Path.Combine(_dir, "two")));
        Assert.Equal("사내 테스트", loaded.Name);
        Assert.Equal(["testa", "TESTB"], loaded.TnsAliases);
        Assert.Equal("→ hostb:1522/svcb", loaded.TnsAliasDetail);
        Assert.False(loaded.IsDirty);
    }
}
