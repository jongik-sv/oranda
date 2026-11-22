using Oranda.Core.Ai.Setup;
using Oranda.Core.Settings;

namespace Oranda.Core.Tests.Ai.Setup;

/// <summary><c>.oranda-ai</c> 읽기(<see cref="AiSetupFile"/>): 정상·열쇠 칸 거절·잘못된 주소·모르는 판.</summary>
public class AiSetupFileTests
{
    private const string Good = """
        { "format": "oranda-ai", "version": 1, "name": "사내 AI", "baseUrl": "https://ai.example.com/v1", "apiFormat": "openai", "model": "big-model", "localAi": false }
        """;

    [Fact]
    public void A_good_file_becomes_a_profile_draft()
    {
        var result = AiSetupFile.Parse(Good);

        Assert.True(result.Success);
        Assert.Null(result.Error);
        var p = result.Profile!;
        Assert.Equal(Guid.Empty, p.Id);
        Assert.Equal("사내 AI", p.Name);
        Assert.Equal(AiProfilePreset.Custom, p.Preset);
        Assert.Equal(AiApiFormat.OpenAi, p.Format);
        Assert.Equal("https://ai.example.com/v1", p.BaseUrl);
        Assert.Equal("big-model", p.Model);
        Assert.False(p.IsLocalAi);
    }

    [Fact]
    public void Local_ai_is_on_only_when_the_file_says_true_and_missing_name_uses_the_host()
    {
        var local = AiSetupFile.Parse("""{ "format": "oranda-ai", "version": 1, "baseUrl": "https://ai.example.com/v1", "apiFormat": "Anthropic", "localAi": true }""").Profile!;
        Assert.True(local.IsLocalAi);
        Assert.Equal(AiApiFormat.Anthropic, local.Format);
        Assert.Equal("ai.example.com", local.Name);
        Assert.Equal("", local.Model);

        var text = AiSetupFile.Parse("""{ "format": "oranda-ai", "version": 1, "baseUrl": "https://ai.example.com/v1", "apiFormat": "openai", "localAi": "true" }""").Profile!;
        Assert.False(text.IsLocalAi);
    }

    [Theory]
    [InlineData("apiKey")]
    [InlineData("key")]
    [InlineData("api_key")]
    [InlineData("token")]
    [InlineData("accessToken")]
    [InlineData("password")]
    [InlineData("clientSecret")]
    [InlineData("Authorization")]
    public void A_file_with_a_secret_looking_field_is_refused(string field)
    {
        var json = $$"""{ "format": "oranda-ai", "version": 1, "baseUrl": "https://ai.example.com/v1", "apiFormat": "openai", "{{field}}": "sk-test-not-real" }""";

        var result = AiSetupFile.Parse(json);

        Assert.False(result.Success);
        Assert.Equal(AiSetupFileError.ContainsSecret, result.Error);
    }

    [Fact]
    public void Token_count_fields_are_not_secrets()
    {
        var json = """{ "format": "oranda-ai", "version": 1, "baseUrl": "https://ai.example.com/v1", "apiFormat": "openai", "maxOutputTokens": 4000, "max_input_tokens": 9000 }""";

        Assert.True(AiSetupFile.Parse(json).Success);
    }

    [Fact]
    public void A_secret_hidden_in_a_nested_object_is_refused_too()
    {
        var json = """{ "format": "oranda-ai", "version": 1, "baseUrl": "https://ai.example.com/v1", "apiFormat": "openai", "extra": [{ "headers": { "x-api-key": "sk" } }] }""";

        Assert.Equal(AiSetupFileError.ContainsSecret, AiSetupFile.Parse(json).Error);
    }

    [Theory]
    [InlineData("ftp://ai.example.com/v1")]
    [InlineData("https://user:pw@ai.example.com/v1")]
    [InlineData("https://ai.example.com/v1?k=1")]
    [InlineData("not a url")]
    [InlineData("")]
    public void A_bad_address_is_refused(string url)
    {
        var json = $$"""{ "format": "oranda-ai", "version": 1, "baseUrl": "{{url}}", "apiFormat": "openai" }""";

        Assert.Equal(AiSetupFileError.BadUrl, AiSetupFile.Parse(json).Error);
    }

    [Theory]
    [InlineData("""{ "format": "oranda-ai", "version": 2, "baseUrl": "https://ai.example.com/v1", "apiFormat": "openai" }""", AiSetupFileError.UnsupportedVersion)]
    [InlineData("""{ "format": "oranda-ai", "version": "1", "baseUrl": "https://ai.example.com/v1", "apiFormat": "openai" }""", AiSetupFileError.UnsupportedVersion)]
    [InlineData("""{ "format": "oranda-ai", "baseUrl": "https://ai.example.com/v1", "apiFormat": "openai" }""", AiSetupFileError.UnsupportedVersion)]
    [InlineData("""{ "format": "other", "version": 1 }""", AiSetupFileError.WrongFormat)]
    [InlineData("""{ "format": "oranda-ai", "version": 1, "baseUrl": "https://ai.example.com/v1", "apiFormat": "gemini" }""", AiSetupFileError.BadApiFormat)]
    [InlineData("""[1, 2]""", AiSetupFileError.NotReadable)]
    [InlineData("""{ not json""", AiSetupFileError.NotReadable)]
    [InlineData("", AiSetupFileError.NotReadable)]
    public void Wrong_files_say_why(string json, AiSetupFileError expected)
    {
        var result = AiSetupFile.Parse(json);

        Assert.Equal(expected, result.Error);
        Assert.Null(result.Profile);
        Assert.False(string.IsNullOrWhiteSpace(AiSetupFile.ErrorText(expected, english: false)));
        Assert.False(string.IsNullOrWhiteSpace(AiSetupFile.ErrorText(expected, english: true)));
    }

    [Fact]
    public void A_too_large_file_is_not_read() =>
        Assert.Equal(AiSetupFileError.NotReadable, AiSetupFile.Parse(Good + new string(' ', AiSetupFile.MaxLength)).Error);
}
