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

        var expression = DbmsFeatureKeys.Enabled(DbmsFeature.AwrReport) + " && hasSession";
        Assert.False(keys.Evaluate(expression));
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
    public void Command_enablement_literals_match_the_key_names()
    {
        Assert.Equal("!unsupportedSessionTrace", DbmsFeatureKeys.Enabled(DbmsFeature.SessionTrace));
        Assert.Equal("!unsupportedAsh", DbmsFeatureKeys.Enabled(DbmsFeature.Ash));
        Assert.Equal("!unsupportedAwrReport", DbmsFeatureKeys.Enabled(DbmsFeature.AwrReport));
        Assert.Equal("!unsupportedOptimizerTrace", DbmsFeatureKeys.Enabled(DbmsFeature.OptimizerTrace));
    }
}
