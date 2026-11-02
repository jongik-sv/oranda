using System.Text.Json;
using Oranda.Core.Ai.Contracts;
using Oranda.Platform.Ai.Cli;

namespace Oranda.Platform.Tests.Ai.Cli;

public class ClaudeStreamInputTests
{
    [Fact]
    public void Text_only_request_is_one_user_message_line()
    {
        var line = ClaudeStreamInput.Build("SELECT 1 FROM dual", null);

        Assert.EndsWith("\n", line, StringComparison.Ordinal);
        var root = JsonDocument.Parse(line).RootElement;
        Assert.Equal("user", root.GetProperty("type").GetString());
        Assert.Equal("user", root.GetProperty("message").GetProperty("role").GetString());
        var block = Assert.Single(root.GetProperty("message").GetProperty("content").EnumerateArray());
        Assert.Equal("text", block.GetProperty("type").GetString());
        Assert.Equal("SELECT 1 FROM dual", block.GetProperty("text").GetString());
    }

    [Fact]
    public void Newlines_quotes_korean_and_line_separators_stay_inside_one_line()
    {
        // 줄바꿈 문자(U+2028·U+2029 포함)가 본문에 그대로 남으면 줄 단위로 읽는 쪽이 메시지를 둘로 쪼갠다.
        const string prompt = "첫 줄\r\n둘째 \"줄\"\n셋째\u2028넷째\u2029다섯째\\끝";

        var line = ClaudeStreamInput.Build(prompt, null);

        Assert.Equal(1, line.Count(c => c is '\n' or '\r' or '\u2028' or '\u2029'));
        Assert.Equal(prompt, JsonDocument.Parse(line).RootElement.GetProperty("message").GetProperty("content")[0].GetProperty("text").GetString());
    }

    [Fact]
    public void Images_follow_the_text_and_image_only_requests_have_no_empty_text_block()
    {
        var image = new AiImageAttachment("image/jpeg", [1, 2, 3, 250]);

        var both = JsonDocument.Parse(ClaudeStreamInput.Build("설명해 줘", [image, image])).RootElement.GetProperty("message").GetProperty("content");
        Assert.Equal(["text", "image", "image"], both.EnumerateArray().Select(b => b.GetProperty("type").GetString()));
        Assert.Equal(new byte[] { 1, 2, 3, 250 }, Convert.FromBase64String(both[1].GetProperty("source").GetProperty("data").GetString()!));

        var imageOnly = JsonDocument.Parse(ClaudeStreamInput.Build("  ", [image])).RootElement.GetProperty("message").GetProperty("content");
        Assert.Equal(["image"], imageOnly.EnumerateArray().Select(b => b.GetProperty("type").GetString()));
    }

    [Fact]
    public void Large_image_is_encoded_without_extra_line_breaks()
    {
        var data = new byte[12 * 1024 * 1024];
        new Random(7).NextBytes(data);

        var line = ClaudeStreamInput.Build("x", [new AiImageAttachment("image/png", data)]);

        Assert.Equal(1, line.Count(c => c == '\n'));
        var encoded = JsonDocument.Parse(line).RootElement.GetProperty("message").GetProperty("content")[1].GetProperty("source").GetProperty("data").GetString()!;
        Assert.Equal(data.Length, Convert.FromBase64String(encoded).Length);
    }

    [Theory]
    [InlineData("image/png", 3, null)]
    [InlineData("image/jpeg", 1, null)]
    [InlineData("image/webp", 1, null)]
    [InlineData("image/gif", 1, null)]
    [InlineData("image/bmp", 3, "image_invalid")]
    [InlineData("", 3, "image_invalid")]
    [InlineData("image/png", 0, "image_invalid")]
    public void Validate_accepts_only_supported_non_empty_images(string mediaType, int length, string? code)
    {
        var error = ClaudeStreamInput.Validate("p", [new AiImageAttachment(mediaType, new byte[length])]);

        Assert.Equal(code, error?.Code);
        Assert.Null(ClaudeStreamInput.Validate("p", null));
    }

    [Fact]
    public void Validate_rejects_an_empty_request_but_allows_images_without_text()
    {
        Assert.Equal("prompt_empty", ClaudeStreamInput.Validate("  ", null)?.Code);
        Assert.Equal("prompt_empty", ClaudeStreamInput.Validate("", [])?.Code);
        Assert.Null(ClaudeStreamInput.Validate("", [new AiImageAttachment("image/png", [1])]));
    }
}
