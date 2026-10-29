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
    [InlineData(ConnectionEnvironment.Production, AiAccessLevel.Query)]
    public void Unset_access_uses_environment_default(ConnectionEnvironment env, AiAccessLevel expected) =>
        Assert.Equal(expected, AiAccessPolicy.Effective(Profile(env)));

    [Fact]
    public void Production_is_capped_at_query_and_a_stored_query_rows_shrinks_to_plan()
    {
        var query = Profile(ConnectionEnvironment.Production, AiAccessLevel.Query);
        Assert.Equal(AiAccessLevel.Query, AiAccessPolicy.MaxLevel(ConnectionEnvironment.Production));
        Assert.Equal(AiAccessLevel.Query, AiAccessPolicy.Effective(query));
        Assert.True(AiAccessPolicy.Allows(query, AiAccessLevel.Query));
        Assert.False(AiAccessPolicy.Allows(query, AiAccessLevel.QueryRows));
        Assert.Empty(AiAccessPolicy.Validate(query));

        // 행 값은 운영으로 못 보낸다: 한 칸(Query)이 아니라 Plan 으로 줄인다.
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
    [InlineData(ConnectionEnvironment.Production, null, true)]
    [InlineData(ConnectionEnvironment.Production, false, false)]
    [InlineData(ConnectionEnvironment.Production, true, true)]
    [InlineData(ConnectionEnvironment.Test, false, false)]
    public void Explain_plan_follows_profile_then_environment(ConnectionEnvironment env, bool? flag, bool expected) =>
        Assert.Equal(expected, AiAccessPolicy.ExplainPlanEnabled(Profile(env, explain: flag)));

    [Theory]
    [InlineData(ConnectionEnvironment.Production, false, true)]
    [InlineData(ConnectionEnvironment.Production, true, true)]
    [InlineData(ConnectionEnvironment.Development, false, false)]
    [InlineData(ConnectionEnvironment.Test, true, true)]
    public void Literal_masking_is_forced_on_production_and_follows_the_setting_elsewhere(ConnectionEnvironment env, bool setting, bool expected) =>
        Assert.Equal(expected, AiAccessPolicy.MaskLiterals(Profile(env), setting));

    [Fact]
    public void Sql_text_fields_name_the_plan_predicate_and_statement_fields()
    {
        Assert.Equal(
            ["access", "filter", "access_predicates", "filter_predicates", "sql_text"],
            Oranda.Core.Ai.Schemas.AgentFields.SqlTextFields);
    }

    [Fact]
    public void Name_confirmation_follows_effective_level_and_environment_changes()
    {
        var defaultProd = Profile(ConnectionEnvironment.Production);
        var offProd = Profile(ConnectionEnvironment.Production, AiAccessLevel.Off);
        var planProd = Profile(ConnectionEnvironment.Production, AiAccessLevel.Plan);

        // 환경 기본값(운영 Query)은 묻지 않는다: 새 운영 접속 저장, 기본값을 그대로 둔 편집, 같은 값을 직접 고른 경우.
        Assert.False(AiAccessPolicy.NeedsNameConfirmation(Profile(ConnectionEnvironment.Development), defaultProd));
        Assert.False(AiAccessPolicy.NeedsNameConfirmation(defaultProd, defaultProd with { Comment = "x" }));
        Assert.False(AiAccessPolicy.NeedsNameConfirmation(defaultProd, Profile(ConnectionEnvironment.Production, AiAccessLevel.Query)));

        // 사용자가 직접 수준을 올릴 때만 묻는다.
        Assert.True(AiAccessPolicy.NeedsNameConfirmation(offProd, planProd));
        Assert.True(AiAccessPolicy.NeedsNameConfirmation(planProd, planProd with { AiAccess = AiAccessLevel.Query }));
        Assert.False(AiAccessPolicy.NeedsNameConfirmation(planProd, planProd with { AiAccess = AiAccessLevel.Metadata }));
        Assert.False(AiAccessPolicy.NeedsNameConfirmation(defaultProd, offProd));
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
    public void Ai_settings_default_to_claude_code_keep_an_explicit_none_and_tolerate_null_section()
    {
        var absent = JsonSerializer.Deserialize("""{ "schemaVersion": 1 }""", OrandaJsonContext.Default.AppSettings)!;
        Assert.Equal(AiDriverKind.ClaudeCode, absent.Ai.Driver);

        // 이미 저장된 설정에서 「없음」 으로 명시한 값은 유지된다.
        var none = JsonSerializer.Deserialize("""{ "ai": { "driver": "None" } }""", OrandaJsonContext.Default.AppSettings)!;
        Assert.Equal(AiDriverKind.None, none.Ai.Driver);

        var nullSection = JsonSerializer.Deserialize("""{ "schemaVersion": 1, "ai": null }""", OrandaJsonContext.Default.AppSettings)!;
        Assert.Equal(new AiSettings(), nullSection.Ai);

        var set = JsonSerializer.Deserialize("""{ "ai": { "driver": "ClaudeCode", "model": "sonnet" } }""", OrandaJsonContext.Default.AppSettings)!;
        Assert.Equal(AiDriverKind.ClaudeCode, set.Ai.Driver);
        Assert.Equal("sonnet", set.Ai.Model);
    }
}
