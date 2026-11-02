using Oranda.Core.Ai.Profiles;
using Oranda.Core.Settings;

namespace Oranda.Core.Tests.Ai.Profiles;

/// <summary>기준 URL 정규화: 끝의 / 와 /v1 유무와 무관하게 같은 결과이고, 쓰는 곳별 URL 이 고정된다. 시험 주소는 자리표시자만 쓴다.</summary>
public class AiProfileUrlTests
{
    [Theory]
    [InlineData("http://llm.example:8080")]
    [InlineData("http://llm.example:8080/")]
    [InlineData("http://llm.example:8080/v1")]
    [InlineData("http://llm.example:8080/v1/")]
    [InlineData("  http://llm.example:8080/v1/  ")]
    public void Cli_base_url_is_the_anthropic_root_whatever_the_user_typed(string typed) =>
        Assert.Equal("http://llm.example:8080", AiProfileUrl.CliBaseUrl(typed));

    [Theory]
    [InlineData("https://api.z.ai/api/anthropic", "https://api.z.ai/api/anthropic")]
    [InlineData("https://api.z.ai/api/anthropic/", "https://api.z.ai/api/anthropic")]
    [InlineData("https://api.z.ai/api/anthropic/v1", "https://api.z.ai/api/anthropic")]
    public void Anthropic_base_keeps_a_path_prefix(string typed, string expected) =>
        Assert.Equal(expected, AiProfileUrl.BaseFor(AiApiFormat.Anthropic, typed));

    [Theory]
    [InlineData("http://llm.example:8080", "http://llm.example:8080/v1")]
    [InlineData("http://llm.example:8080/", "http://llm.example:8080/v1")]
    [InlineData("http://llm.example:8080/v1", "http://llm.example:8080/v1")]
    [InlineData("http://llm.example:8080/v1/", "http://llm.example:8080/v1")]
    [InlineData("https://generativelanguage.googleapis.com/v1beta/openai", "https://generativelanguage.googleapis.com/v1beta/openai")]
    [InlineData("https://generativelanguage.googleapis.com/v1beta/openai/", "https://generativelanguage.googleapis.com/v1beta/openai")]
    public void OpenAi_base_adds_v1_only_when_there_is_no_path(string typed, string expected) =>
        Assert.Equal(expected, AiProfileUrl.BaseFor(AiApiFormat.OpenAi, typed));

    [Fact]
    public void A_host_named_loopback_is_sent_to_localhost_so_the_request_and_the_warning_agree() =>
        Assert.Equal("http://localhost:1", AiProfileUrl.BaseFor(AiApiFormat.Anthropic, "http://loopback:1"));

    [Fact]
    public void Request_and_models_urls_are_fixed_per_format()
    {
        Assert.Equal("http://llm.example:8080/v1/chat/completions", AiProfileUrl.RequestUrl(AiApiFormat.OpenAi, "http://llm.example:8080", "chat/completions"));
        Assert.Equal("http://llm.example:8080/v1/chat/completions", AiProfileUrl.RequestUrl(AiApiFormat.OpenAi, "http://llm.example:8080/v1/", "/chat/completions"));
        Assert.Equal("http://llm.example:8080/v1/messages", AiProfileUrl.RequestUrl(AiApiFormat.Anthropic, "http://llm.example:8080/v1", "messages"));
        Assert.Equal("http://llm.example:8080/v1/models", AiProfileUrl.ModelsUrl(AiApiFormat.Anthropic, "http://llm.example:8080/"));
        Assert.Equal("http://llm.example:8080/v1/models", AiProfileUrl.ModelsUrl(AiApiFormat.OpenAi, "http://llm.example:8080"));
        Assert.Equal("https://generativelanguage.googleapis.com/v1beta/openai/models", AiProfileUrl.ModelsUrl(AiApiFormat.OpenAi, "https://generativelanguage.googleapis.com/v1beta/openai"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("llm.example:8080")]
    [InlineData("ftp://llm.example/v1")]
    [InlineData("not a url")]
    [InlineData("http://user:secret@llm.example:8080")]
    [InlineData("http://llm.example:8080/v1?api-version=1")]
    [InlineData("http://llm.example:8080/v1#frag")]
    public void Invalid_addresses_give_null(string? typed)
    {
        Assert.Null(AiProfileUrl.BaseFor(AiApiFormat.Anthropic, typed));
        Assert.Null(AiProfileUrl.BaseFor(AiApiFormat.OpenAi, typed));
        Assert.Null(AiProfileUrl.ModelsUrl(AiApiFormat.OpenAi, typed));
    }

    [Theory]
    [InlineData("http://llm.example:8080", true)]
    [InlineData("http://192.0.2.10:8080/v1", true)]
    [InlineData("https://llm.example", false)]
    [InlineData("http://localhost:11434/v1", false)]
    [InlineData("http://127.0.0.1:1234/v1", false)]
    [InlineData("http://[::1]:1234", false)]
    [InlineData("http://127.5.6.7:1234", false)]
    [InlineData("http://loopback:1", false)] // Uri 가 loopback 을 localhost 로 바꿔 요청도 이 PC 로 나간다
    [InlineData("http://myhost.localhost:1", false)]
    [InlineData("", false)]
    public void Plain_text_remote_warning_applies_only_to_non_local_http(string url, bool expected) =>
        Assert.Equal(expected, AiProfileUrl.IsPlainTextRemote(url));
}
