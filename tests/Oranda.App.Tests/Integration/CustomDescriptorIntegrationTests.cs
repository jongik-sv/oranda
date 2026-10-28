using Oranda.App.Features.ConnectionManager;
using Oranda.Oracle.Connection;

namespace Oranda.App.Tests.Integration;

/// <summary>
/// 사용자 지정 칸에 여러 줄 tnsnames.ora 항목(별칭·주석·들여쓰기 포함)을 넣고 실제 서버(상주 컨테이너)에 접속한다.
/// 시험 사용자는 <see cref="ItDb"/> 가 만들고 지운다. ORANDA_IT_ADMIN_PASSWORD 가 없으면 아무것도 하지 않는다.
/// </summary>
[Trait("Category", "Integration")]
public sealed class CustomDescriptorIntegrationTests(ItDb db) : IClassFixture<ItDb>
{
    [Fact]
    public async Task Multi_line_tnsnames_entry_connects()
    {
        if (!db.Enabled)
        {
            return;
        }

        var vm = ProfileEditorViewModel.ForNew();
        vm.User = db.User;
        vm.Mode = ConnectionMode.EasyConnect;
        vm.EasyConnect =
            "# 시험 서버\n"
            + "IT_DB =\n"
            + "  (DESCRIPTION =\n"
            + "    (ADDRESS_LIST =\n"
            + $"      (ADDRESS = (PROTOCOL = TCP)(HOST = {db.Host})(PORT = {db.Port}))\n"
            + "    )\n"
            + "    (CONNECT_DATA =\n"
            + $"      (SERVICE_NAME = {db.Service})\n"
            + "    )\n"
            + "  )\n";
        var profile = vm.TryBuild(out var errors);
        Assert.True(profile is not null, string.Join(" / ", errors));
        Assert.Contains('\n', profile.DataSource);

        await using var conn = profile.CreateConnection(db.Password);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "select user from dual";
        Assert.Equal(db.User, (string?)await cmd.ExecuteScalarAsync());
    }
}
