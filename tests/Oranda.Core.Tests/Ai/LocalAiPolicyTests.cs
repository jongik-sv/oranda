using System.Text.Json;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Connection;
using Oranda.Core.Serialization;
using Oranda.Core.Settings;

namespace Oranda.Core.Tests.Ai;

/// <summary>D-045: 사용자가 「로컬 AI」 로 지정한 프로필에는 비밀번호·주민번호·카드·계좌·주소·인증값 열을 빼고 값을 가리지 않는다.</summary>
public class LocalAiPolicyTests
{
    private const string Url = "http://192.0.2.10:8080/v1";

    private static ConnectionProfile Connection(ConnectionEnvironment env) => new("p", "scott", "db:1521/s", Environment: env);

    [Theory]
    [InlineData(ConnectionEnvironment.Production, false)]
    [InlineData(ConnectionEnvironment.Production, true)]
    [InlineData(ConnectionEnvironment.Development, true)]
    [InlineData(ConnectionEnvironment.Test, false)]
    public void Local_ai_never_masks_whatever_the_connection_or_the_setting_says(ConnectionEnvironment env, bool setting) =>
        Assert.False(AiAccessPolicy.MaskLiterals(Connection(env), setting, localAi: true));

    [Theory]
    [InlineData(AiAccessLevel.Metadata)]
    [InlineData(AiAccessLevel.Plan)]
    [InlineData(AiAccessLevel.Query)]
    [InlineData(AiAccessLevel.QueryRows)]
    public void Local_ai_reads_values_at_the_row_level_and_other_targets_keep_their_level(AiAccessLevel level)
    {
        Assert.Equal(AiAccessLevel.QueryRows, AiAccessPolicy.DataLevel(level, localAi: true));
        Assert.Equal(level, AiAccessPolicy.DataLevel(level, localAi: false));
    }

    [Fact]
    public void A_connection_turned_off_stays_off_for_local_ai() =>
        Assert.Equal(AiAccessLevel.Off, AiAccessPolicy.DataLevel(AiAccessLevel.Off, localAi: true));

    [Theory]
    [InlineData("USER_PASSWORD")]
    [InlineData("LOGIN_PWD")]
    [InlineData("PSWD_HASH")]
    [InlineData("비밀번호")]
    [InlineData("암호")]
    [InlineData("JUMIN_NO")]
    [InlineData("RRN")]
    [InlineData("CUST_SSN")]
    [InlineData("주민번호")]
    [InlineData("CLIENT_SECRET")]
    [InlineData("ACCESS_TOKEN")]
    [InlineData("API_KEY")]
    [InlineData("AUTHKEY")]
    [InlineData("USER_CREDENTIAL")]
    [InlineData("OTP_NO")]
    [InlineData("토큰")]
    [InlineData("인증번호")]
    [InlineData("USER_PW")]
    [InlineData("MBR_PW")]
    [InlineData("PW")]
    [InlineData("USER_PASS")]
    [InlineData("PSWORD")]
    [InlineData("패스워드")]
    [InlineData("비번")]
    [InlineData("RESNO")]
    [InlineData("RES_REG_NO")]
    [InlineData("RSDT_NO")]
    [InlineData("RSDNT_NO")]
    [InlineData("JMN_NO")]
    [InlineData("JUMN_NO")]
    [InlineData("IHIDNUM")]
    [InlineData("CRDNO")]
    [InlineData("ACC_NO")]
    [InlineData("ADRS1")]
    [InlineData("CARD_NO")]
    [InlineData("CRD_NO")]
    [InlineData("카드번호")]
    [InlineData("ACCOUNT_NO")]
    [InlineData("ACCT_NO")]
    [InlineData("ACNT_NO")]
    [InlineData("IBAN")]
    [InlineData("계좌번호")]
    [InlineData("ADDR1")]
    [InlineData("CUST_ADDRESS")]
    [InlineData("주소")]
    [InlineData("SYS_NC00012$")]
    [InlineData("SYS_STUABC")]
    public void Password_resident_card_account_address_and_hidden_columns_stay_hidden(string column) =>
        Assert.True(SensitiveColumns.IsAlwaysHidden(column));

    [Theory]
    [InlineData("DISCARD_YN")]
    [InlineData("BYPASS_YN")]
    [InlineData("COMPASS_DEG")]
    [InlineData("PASSENGER_CNT")]
    public void A_word_that_only_contains_a_fragment_by_accident_is_not_hidden(string column) =>
        Assert.False(SensitiveColumns.IsAlwaysHidden(column));

    [Fact]
    public void Every_always_hidden_fragment_is_also_sensitive_for_other_targets()
    {
        // 로컬 AI 가 다른 대상보다 더 가리는 일이 없게 한다.
        foreach (var fragment in SensitiveColumns.AlwaysHiddenFragments)
        {
            Assert.Contains(fragment, SensitiveColumns.Fragments);
        }
    }

    [Theory]
    [InlineData("PHONE_NO")]
    [InlineData("EMAIL")]
    [InlineData("BIRTH_DT")]
    [InlineData("CUST_NM")]
    public void Other_sensitive_columns_are_sent_to_local_ai_but_stay_sensitive_elsewhere(string column)
    {
        Assert.False(SensitiveColumns.IsAlwaysHidden(column));
        Assert.True(SensitiveColumns.IsSensitive(column));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_column_name_is_not_hidden(string? column) => Assert.False(SensitiveColumns.IsAlwaysHidden(column));

    [Fact]
    public void A_profile_is_local_only_for_the_address_it_was_confirmed_for()
    {
        var profile = new AiLlmProfile { BaseUrl = Url };
        Assert.False(profile.IsLocalAi); // 기본 꺼짐: 주소로 판단하지 않는다

        var local = profile with { LocalAiConfirmedUrl = Url };
        Assert.True(local.IsLocalAi);
        Assert.True((local with { BaseUrl = " " + Url + " " }).IsLocalAi); // 앞뒤 공백은 같은 주소다
        Assert.False((local with { BaseUrl = "http://192.0.2.11:8080/v1" }).IsLocalAi);
        Assert.False((local with { LocalAiConfirmedUrl = "  " }).IsLocalAi);
    }

    [Fact]
    public void Localhost_or_a_private_address_alone_does_not_make_a_profile_local()
    {
        Assert.False(new AiLlmProfile { BaseUrl = "http://localhost:11434/v1" }.IsLocalAi);
        Assert.False(new AiLlmProfile { BaseUrl = "http://192.168.0.10:8080/v1" }.IsLocalAi);
    }

    [Fact]
    public void The_confirmed_address_is_stored_and_the_computed_flag_is_not()
    {
        var settings = new AppSettings { Ai = new AiSettings { Profiles = [new AiLlmProfile { Id = Guid.NewGuid(), BaseUrl = Url, LocalAiConfirmedUrl = Url }] } };

        var json = JsonSerializer.Serialize(settings, OrandaJsonContext.Default.AppSettings);
        var back = JsonSerializer.Deserialize(json, OrandaJsonContext.Default.AppSettings)!;

        Assert.Contains("localAiConfirmedUrl", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("isLocalAi", json, StringComparison.OrdinalIgnoreCase);
        Assert.True(back.Ai.Profiles[0].IsLocalAi);
    }

    [Fact]
    public void A_settings_file_from_before_this_field_reads_as_not_local()
    {
        var back = JsonSerializer.Deserialize("""{ "ai": { "profiles": [ { "baseUrl": "http://192.0.2.10:8080/v1", "model": "m" } ] } }""", OrandaJsonContext.Default.AppSettings)!;

        Assert.Null(back.Ai.Profiles[0].LocalAiConfirmedUrl);
        Assert.False(back.Ai.Profiles[0].IsLocalAi);
    }
}
