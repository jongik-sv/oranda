using Oranda.App.Shell;
using Oranda.App.Shell.Features;
using Oranda.Core.Connection;

namespace Oranda.App.Tests.Shell;

// 소유: Tibero T1 A5. 미지원 기능을 비활성 + 이유로 보이는 공용 부품(DbmsFeatureKeys).
[Collection("DbmsFeatureKeysSerial")]
public class DbmsFeatureKeysTests
{
    private static string? Phrase(string key) => key switch
    {
        "Menu.disabled.unsupportedOn.Tibero" => "Tibero 에서는 지원하지 않음",
        _ => null,
    };

    [Fact]
    public void Oracle_and_no_session_keep_every_feature_enabled()
    {
        var keys = new ContextKeyService();

        DbmsFeatureKeys.Apply(keys, Dbms.Oracle);
        Assert.All(Enum.GetValues<DbmsFeature>(), f => Assert.True(keys.Evaluate(DbmsFeatureKeys.Enabled(f))));

        DbmsFeatureKeys.Apply(keys, null);
        Assert.All(Enum.GetValues<DbmsFeature>(), f => Assert.True(keys.Evaluate(DbmsFeatureKeys.Enabled(f))));
    }

    [Fact]
    public void Tibero_disables_the_unsupported_features_and_the_menu_reason_names_the_dbms()
    {
        var keys = new ContextKeyService();
        keys.Set("hasSession", true);
        DbmsFeatureKeys.Apply(keys, Dbms.Tibero);

        var expression = DbmsFeatureKeys.Enabled(DbmsFeature.Ash) + " && hasSession"; // 아직 Tibero = false 인 기능
        Assert.False(keys.Evaluate(expression));

        // 실측으로 켠 기능은 Tibero 에서도 켜진다(AwrReport = 계획 이력, P5b TPR 실측).
        Assert.True(keys.Evaluate(DbmsFeatureKeys.Enabled(DbmsFeature.AwrReport) + " && hasSession"));
        var reason = ContextKeyReasons.Explain(expression, keys.Evaluate, DbmsFeatureKeys.WrapPhrase(Phrase, () => Dbms.Tibero));
        Assert.Equal("Tibero 에서는 지원하지 않음", reason);

        // 접속이 Oracle 로 바뀌면 다시 켜진다.
        DbmsFeatureKeys.Apply(keys, Dbms.Oracle);
        Assert.True(keys.Evaluate(expression));
        DbmsFeatureKeys.Apply(keys, null);
    }

    [Fact]
    public void Other_phrase_keys_pass_through_and_missing_text_falls_back_to_the_core_wording()
    {
        var keys = new ContextKeyService();
        DbmsFeatureKeys.Apply(keys, Dbms.Tibero);

        Assert.Equal("x", DbmsFeatureKeys.WrapPhrase(_ => "x")("Menu.disabled.hasSession"));
        Assert.Equal(DbmsSupport.NotSupportedText(Dbms.Tibero), DbmsFeatureKeys.WrapPhrase(_ => null, () => Dbms.Tibero)(DbmsFeatureKeys.PhrasePrefix + "Ash"));
        DbmsFeatureKeys.Apply(keys, null);
    }

    [Fact]
    public void Reason_is_null_when_supported_and_names_the_dbms_otherwise()
    {
        Assert.Null(DbmsFeatureKeys.Reason(Dbms.Oracle, DbmsFeature.Ash));
        Assert.Null(DbmsFeatureKeys.Reason(null, DbmsFeature.Ash));
        Assert.Equal("Tibero 에서는 지원하지 않음", DbmsFeatureKeys.Reason(Dbms.Tibero, DbmsFeature.Ash, Phrase));
        Assert.Equal("Tibero 에서는 지원하지 않음", DbmsFeatureKeys.Reason(Dbms.Tibero, DbmsFeature.Ash));
    }

    [Fact]
    public void Trace_features_stay_off_on_tibero_and_on_for_oracle()
    {
        var keys = new ContextKeyService();
        DbmsFeature[] traces = [DbmsFeature.SessionTrace, DbmsFeature.Tkprof, DbmsFeature.OptimizerTrace];
        try
        {
            DbmsFeatureKeys.Apply(keys, Dbms.Tibero); // 10053·DBMS_MONITOR·TKPROF 가 Tibero 에 없다(P5c) — 켜려면 실측 근거가 필요
            Assert.All(traces, f => Assert.False(keys.Evaluate(DbmsFeatureKeys.Enabled(f))));
            DbmsFeatureKeys.Apply(keys, Dbms.Oracle);
            Assert.All(traces, f => Assert.True(keys.Evaluate(DbmsFeatureKeys.Enabled(f))));
        }
        finally
        {
            DbmsFeatureKeys.Apply(keys, null);
        }
    }

    [Theory]
    [InlineData(DbmsFeature.SharedCursorReasons)]
    [InlineData(DbmsFeature.PlanControls)]
    [InlineData(DbmsFeature.Deadlocks)]
    [InlineData(DbmsFeature.LiteralGroups)]
    [InlineData(DbmsFeature.SqlMonitorReportHtml)]
    public void Whole_feature_gaps_are_supported_on_oracle_and_explain_themselves_on_tibero(DbmsFeature feature)
    {
        Assert.Null(DbmsFeatureKeys.Reason(Dbms.Oracle, feature));
        Assert.Equal(DbmsSupport.NotSupportedText(Dbms.Tibero), DbmsFeatureKeys.Reason(Dbms.Tibero, feature));
        Assert.Equal("!unsupported" + feature, DbmsFeatureKeys.Enabled(feature));
    }

    [Fact]
    public void Command_enablement_literals_match_the_key_names()
    {
        Assert.Equal("!unsupportedSessionTrace", DbmsFeatureKeys.Enabled(DbmsFeature.SessionTrace));
        Assert.Equal("!unsupportedAsh", DbmsFeatureKeys.Enabled(DbmsFeature.Ash));
        Assert.Equal("!unsupportedAwrReport", DbmsFeatureKeys.Enabled(DbmsFeature.AwrReport));
        Assert.Equal("!unsupportedOptimizerTrace", DbmsFeatureKeys.Enabled(DbmsFeature.OptimizerTrace));
    }
}
