using System.Text.Json;
using Oranda.Core.Ai.Access;
using Oranda.Core.Connection;
using Oranda.Core.Serialization;
using Oranda.Core.Settings;

namespace Oranda.Core.Tests.Ai;

public class AiAccessPolicyTests
{
    private static ConnectionProfile Profile(ConnectionEnvironment env, AiAccessLevel? access = null, bool? explain = null) =>
        new("p", "scott", "db:1521/s", Environment: env, AiAccess: access, AllowExplainPlan: explain);

    [Theory]
    [InlineData(ConnectionEnvironment.Development, AiAccessLevel.Plan)]
    [InlineData(ConnectionEnvironment.Test, AiAccessLevel.Plan)]
    [InlineData(ConnectionEnvironment.Production, AiAccessLevel.Off)]
    public void Unset_access_uses_environment_default(ConnectionEnvironment env, AiAccessLevel expected) =>
        Assert.Equal(expected, AiAccessPolicy.Effective(Profile(env)));

    [Fact]
    public void Production_is_capped_at_plan_and_never_reaches_query()
    {
        var prod = Profile(ConnectionEnvironment.Production, AiAccessLevel.QueryRows);
        Assert.Equal(AiAccessLevel.Plan, AiAccessPolicy.Effective(prod));
        Assert.False(AiAccessPolicy.Allows(prod, AiAccessLevel.Query));
        Assert.True(AiAccessPolicy.Allows(prod, AiAccessLevel.Plan));
        Assert.Equal(AiAccessLevel.Plan, AiAccessPolicy.Clamp(prod).AiAccess);
        Assert.Single(AiAccessPolicy.Validate(prod));
    }

    [Fact]
    public void Development_allows_every_level_and_off_allows_nothing()
    {
        var dev = Profile(ConnectionEnvironment.Development, AiAccessLevel.QueryRows);
        Assert.True(AiAccessPolicy.Allows(dev, AiAccessLevel.QueryRows));
        Assert.Empty(AiAccessPolicy.Validate(dev));

        var off = Profile(ConnectionEnvironment.Development, AiAccessLevel.Off);
        Assert.False(AiAccessPolicy.Allows(off, AiAccessLevel.Metadata));
        Assert.False(AiAccessPolicy.Allows(off, AiAccessLevel.Off));
    }

    [Theory]
    [InlineData(ConnectionEnvironment.Development, null, true)]
    [InlineData(ConnectionEnvironment.Production, null, false)]
    [InlineData(ConnectionEnvironment.Production, true, true)]
    [InlineData(ConnectionEnvironment.Test, false, false)]
    public void Explain_plan_follows_profile_then_environment(ConnectionEnvironment env, bool? flag, bool expected) =>
        Assert.Equal(expected, AiAccessPolicy.ExplainPlanEnabled(Profile(env, explain: flag)));

    [Fact]
    public void Name_confirmation_follows_effective_level_and_environment_changes()
    {
        var offProd = Profile(ConnectionEnvironment.Production);
        var planProd = Profile(ConnectionEnvironment.Production, AiAccessLevel.Plan);
        Assert.True(AiAccessPolicy.NeedsNameConfirmation(offProd, planProd));
        Assert.False(AiAccessPolicy.NeedsNameConfirmation(planProd, planProd with { AiAccess = AiAccessLevel.Metadata }));
        Assert.False(AiAccessPolicy.NeedsNameConfirmation(Profile(ConnectionEnvironment.Development), Profile(ConnectionEnvironment.Development, AiAccessLevel.Plan)));

        // 개발에서 Plan 으로 켜 둔 접속의 라벨만 운영으로 바꿔도 묻는다.
        var devPlan = Profile(ConnectionEnvironment.Development, AiAccessLevel.Plan);
        Assert.True(AiAccessPolicy.NeedsNameConfirmation(devPlan, devPlan with { Environment = ConnectionEnvironment.Production }));
        Assert.False(AiAccessPolicy.NeedsNameConfirmation(devPlan, devPlan with { Environment = ConnectionEnvironment.Production, AiAccess = AiAccessLevel.Off }));
    }

    [Fact]
    public void Undefined_level_values_are_treated_as_off()
    {
        var weird = Profile(ConnectionEnvironment.Development, (AiAccessLevel)9);
        Assert.Equal(AiAccessLevel.Off, AiAccessPolicy.Effective(weird));
        Assert.Single(AiAccessPolicy.Validate(weird));
    }

    [Fact]
    public void Old_connection_json_reads_with_null_ai_fields_and_omits_them_when_writing()
    {
        var doc = JsonSerializer.Deserialize(
            """{"schemaVersion":1,"profiles":[{"name":"old","user":"scott","dataSource":"db:1521/s"}]}""",
            OrandaJsonContext.Default.ConnectionsDocument)!;
        var p = Assert.Single(doc.Profiles);
        Assert.Null(p.AiAccess);
        Assert.Null(p.AllowExplainPlan);
        Assert.DoesNotContain("aiAccess", JsonSerializer.Serialize(doc, OrandaJsonContext.Default.ConnectionsDocument), StringComparison.Ordinal);
    }

    [Fact]
    public void Ai_settings_default_to_no_driver_and_tolerate_null_section()
    {
        var absent = JsonSerializer.Deserialize("""{ "schemaVersion": 1 }""", OrandaJsonContext.Default.AppSettings)!;
        Assert.Equal(AiDriverKind.None, absent.Ai.Driver);

        var nullSection = JsonSerializer.Deserialize("""{ "schemaVersion": 1, "ai": null }""", OrandaJsonContext.Default.AppSettings)!;
        Assert.Equal(new AiSettings(), nullSection.Ai);

        var set = JsonSerializer.Deserialize("""{ "ai": { "driver": "ClaudeCode", "model": "sonnet" } }""", OrandaJsonContext.Default.AppSettings)!;
        Assert.Equal(AiDriverKind.ClaudeCode, set.Ai.Driver);
        Assert.Equal("sonnet", set.Ai.Model);
    }
}
