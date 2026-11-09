using Oranda.Core.Catalog;
using Oranda.Core.Catalog.Providers;
using Oranda.Core.Connection;
using Oranda.Core.Sessions;
using Oranda.Testing;

namespace Oranda.Core.Tests.Catalog.Providers;

// 소유: TSK-09-01(라이선스 대조), P0-b. Oracle.IntegrationTests SessionMonitorLogicTests 의 EnsureLicense 시험을 옮겼다(DB 없음).
public class CatalogLicenseGateTests
{
    [Theory]
    [InlineData(LicensePack.None, null, CatalogLicense.None, true)]
    [InlineData(LicensePack.None, null, CatalogLicense.Diagnostics, false)]
    [InlineData(LicensePack.Diagnostics, null, CatalogLicense.Diagnostics, true)]
    [InlineData(LicensePack.Diagnostics, "NONE", CatalogLicense.Diagnostics, false)]
    [InlineData(LicensePack.Diagnostics, null, CatalogLicense.Tuning, false)]
    [InlineData(LicensePack.DiagnosticsAndTuning, "DIAGNOSTIC+TUNING", CatalogLicense.Tuning, true)]
    public void License_tagged_query_needs_declared_pack(LicensePack held, string? serverAccess, CatalogLicense need, bool allowed)
    {
        var session = new FakeSession(TestData.Profile(licensePack: held), TestData.Server() with { ManagementPackAccess = serverAccess }, SessionPurpose.Monitor);

        var ex = Record.Exception(() => CatalogLicenseGate.Ensure(session, need));

        Assert.Equal(allowed, ex is null);
        if (!allowed)
        {
            Assert.IsType<UnauthorizedAccessException>(ex);
        }
    }

    [Fact]
    public void Deferred_binds_are_built_once_on_first_read_and_trimmed_text_drops_blanks()
    {
        var built = 0;
        var binds = CatalogBinds.Defer(() =>
        {
            built++;
            return [CatalogBinds.Trimmed("a", "  x "), CatalogBinds.Trimmed("b", "   "), CatalogBinds.Text("c", " y ")];
        });

        Assert.Equal(0, built);
        Assert.Equal(3, binds.Count);
        Assert.Equal(["x", null, " y "], binds.Select(b => b.Value));
        Assert.Equal(1, built);
        Assert.Equal(CatalogBindKind.Int64, CatalogBinds.Long("n", null).Kind);
    }

    [Fact]
    public void Dictionary_prefix_follows_the_dictionary_privilege_only_when_asked()
    {
        var dba = TestData.Server();
        var all = dba with { Capabilities = dba.Capabilities with { CanQueryDictionary = false } };
        var options = new CatalogQueryOptions { DictionaryPrefix = true };

        Assert.Equal("SELECT * FROM dba_users", options.Apply("SELECT * FROM @PFX@_users", dba));
        Assert.Equal("SELECT * FROM all_users", options.Apply("SELECT * FROM @PFX@_users", all));
        Assert.Equal("SELECT * FROM @PFX@_users", CatalogQueryOptions.Default.Apply("SELECT * FROM @PFX@_users", dba));
    }
}
