using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Connection;

namespace Oranda.Core.Tests.Ai;

/// <summary>
/// 「AI 가 볼 수 있는 접속」 공유 값(ai-settings-one-place 2026-11-24): 안의 AI = 접속 실효 수준, 밖의 에이전트 = min(접속 수준, 외부 상한),
/// 고를 수 있는 수준, 저장 꼴(기본값이면 null), 운영 수준을 직접 올릴 때의 이름 재입력.
/// </summary>
public class AiConnectionAccessTests
{
    private static ConnectionProfile Dev(AiAccessLevel? level = null) =>
        new("dev-erp", "scott", "db.example.com:1521/DEV", Id: Guid.NewGuid(), AiAccess: level);

    private static ConnectionProfile Prd(AiAccessLevel? level = null) =>
        new("prd-erp", "scott", "db.example.com:1521/PRD", Id: Guid.NewGuid(), Environment: ConnectionEnvironment.Production, AiAccess: level);

    [Fact]
    public void Both_sides_read_the_same_connection_level_and_external_is_capped()
    {
        var dev = AiConnectionAccess.Of(Dev(AiAccessLevel.QueryRows));
        Assert.Equal(AiAccessLevel.QueryRows, dev.Level);
        Assert.Equal(AiAccessLevel.QueryRows, dev.ExternalLevel);
        Assert.False(dev.ExternalCapped);
        Assert.False(dev.ShowsExternalCap);
        Assert.False(dev.FollowsDefault);

        var prd = AiConnectionAccess.Of(Prd());
        Assert.True(prd.FollowsDefault);
        Assert.Equal(AiAccessLevel.Query, prd.Level); // 운영 기본 = 조회 실행
        Assert.Equal(AiAccessLevel.Query, prd.MaxLevel);
        Assert.Equal(AgentGrantPolicy.DefaultCap(ConnectionEnvironment.Production), prd.ExternalCap);
        Assert.Equal(prd.ExternalCap < AiAccessLevel.Query ? prd.ExternalCap : AiAccessLevel.Query, prd.ExternalLevel); // min(접속, 상한)
        Assert.True(prd.ShowsExternalCap); // 운영은 상한을 보인다

        var off = AiConnectionAccess.Of(Prd(AiAccessLevel.Off));
        Assert.Equal((AiAccessLevel.Off, AiAccessLevel.Off), (off.Level, off.ExternalLevel));
    }

    [Fact]
    public void A_default_production_connection_gives_query_inside_and_the_capped_value_outside()
    {
        // 시안(2026-11-24): 운영 처음 값 = 「조회 실행」, 밖의 에이전트 = min(조회 실행, 외부 상한).
        // 운영 실행 스위치(AgentGrantPolicy.ProductionRuns, D5)가 켜져 있으면 상한 = 조회+행 값이라 밖도 「조회 실행」(상한 표시 없음),
        // 끄면 상한 = 메타데이터라 밖은 「메타데이터(외부 상한)」.
        var prd = AiConnectionAccess.Of(Prd());
        Assert.Equal(AiAccessLevel.Query, prd.Level);
        if (AgentGrantPolicy.ProductionRuns)
        {
            Assert.Equal(AiAccessLevel.QueryRows, prd.ExternalCap);
            Assert.Equal(AiAccessLevel.Query, prd.ExternalLevel);
            Assert.False(prd.ExternalCapped);
        }
        else
        {
            Assert.Equal(AiAccessLevel.Metadata, prd.ExternalCap);
            Assert.Equal(AiAccessLevel.Metadata, prd.ExternalLevel);
            Assert.True(prd.ExternalCapped);
        }

        // 상한이 메타데이터인 경우의 값은 스위치와 관계없이 같은 계산으로 나온다.
        Assert.Equal(AiAccessLevel.Metadata, AgentGrantPolicy.LevelFor(Prd(), AiAccessLevel.Metadata));
    }

    [Fact]
    public void External_level_equals_the_grant_policy_for_every_level_and_environment()
    {
        foreach (var environment in Enum.GetValues<ConnectionEnvironment>())
        {
            foreach (var level in Enum.GetValues<AiAccessLevel>())
            {
                var profile = Dev(level) with { Environment = environment };
                var access = AiConnectionAccess.Of(profile);
                Assert.Equal(AgentGrantPolicy.LevelFor(profile, AgentGrantPolicy.DefaultCap(environment)), access.ExternalLevel);
                Assert.Equal(AiAccessPolicy.Effective(profile), access.Level);
                Assert.True(access.ExternalLevel <= access.Level);
            }
        }
    }

    [Fact]
    public void Choices_stop_at_the_environment_maximum()
    {
        Assert.Equal([AiAccessLevel.Off, AiAccessLevel.Metadata, AiAccessLevel.Plan, AiAccessLevel.Query, AiAccessLevel.QueryRows], AiConnectionAccessRules.Choices(ConnectionEnvironment.Development));
        Assert.Equal([AiAccessLevel.Off, AiAccessLevel.Metadata, AiAccessLevel.Plan, AiAccessLevel.Query], AiConnectionAccessRules.Choices(ConnectionEnvironment.Production));
        Assert.Equal(AiConnectionAccessRules.Choices(ConnectionEnvironment.Production), AiConnectionAccess.Of(Prd()).Choices);
    }

    [Fact]
    public void Apply_stores_null_for_the_environment_default_and_keeps_an_unchanged_profile()
    {
        var dev = Dev(AiAccessLevel.Off);
        Assert.Null(AiConnectionAccessRules.Apply(dev, AiAccessLevel.Plan).AiAccess); // 개발 기본 = 계획
        Assert.Equal(AiAccessLevel.QueryRows, AiConnectionAccessRules.Apply(dev, AiAccessLevel.QueryRows).AiAccess);
        Assert.Equal(AiAccessLevel.Off, AiConnectionAccessRules.Apply(Dev(), AiAccessLevel.Off).AiAccess);

        var explicitPlan = Dev(AiAccessLevel.Plan);
        Assert.Same(explicitPlan, AiConnectionAccessRules.Apply(explicitPlan, AiAccessLevel.Plan)); // 같은 실효 수준이면 그대로
    }

    [Fact]
    public void Raising_production_explicitly_needs_the_name_like_the_connection_editor()
    {
        var off = Prd(AiAccessLevel.Off);
        Assert.Equal(AiAccessChange.Saved, AiConnectionAccessRules.Check(off, AiAccessLevel.Query, null)); // 기본값으로 = null 저장, 묻지 않음(도우미 켬과 같다)
        Assert.Equal(AiAccessChange.NeedsNameConfirmation, AiConnectionAccessRules.Check(off, AiAccessLevel.Plan, null));
        Assert.Equal(AiAccessChange.NeedsNameConfirmation, AiConnectionAccessRules.Check(off, AiAccessLevel.Plan, "prd"));
        Assert.Equal(AiAccessChange.Saved, AiConnectionAccessRules.Check(off, AiAccessLevel.Plan, "prd-erp"));
        Assert.Equal(AiAccessChange.Saved, AiConnectionAccessRules.Check(Prd(), AiAccessLevel.Metadata, null)); // 내리기는 묻지 않는다
        Assert.Equal(AiAccessChange.AboveMaximum, AiConnectionAccessRules.Check(off, AiAccessLevel.QueryRows, "prd-erp"));
        Assert.Equal(AiAccessChange.AboveMaximum, AiConnectionAccessRules.Check(Dev(), (AiAccessLevel)42, null));
        Assert.Equal(AiAccessChange.Saved, AiConnectionAccessRules.Check(Dev(), AiAccessLevel.QueryRows, null)); // 개발은 묻지 않는다
    }
}
