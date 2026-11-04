using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Oranda.Platform.Ai.Api;

namespace Oranda.Platform.Ai.Cli;

/// <summary>
/// 기타 CLI 가 낸 글을 다듬고 구조 결과(JSON)를 찾는다. CLI 마다 출력 형식이 다르므로 형식을 가정하지 않고,
/// 코드 블록(<c>```</c>)과 짝이 맞는 <c>{…}</c> 덩어리를 뒤에서부터 보며 스키마(<see cref="OpenAiSchemaCheck"/>)에 맞는 첫 객체를 고른다.
/// 모든 탐색은 글 길이에 비례하는 시간 안에 끝난다(되돌아가는 정규식·중첩 탐색을 쓰지 않는다).
/// </summary>
internal static class CustomCliOutput
{
    /// <summary>JSON 을 찾을 때 보는 글의 끝부분 길이(뒤쪽에 결과가 오므로 앞부분은 버린다).</summary>
    private const int MaxSearchChars = 1_000_000;

    /// <summary>덩어리 후보를 구문 분석하는 횟수 상한.</summary>
    private const int MaxCandidates = 300;

    /// <summary>구문 분석에 들이는 글자 수 합계 상한(큰 후보를 여러 번 분석하느라 시간이 폭주하지 않게).</summary>
    private const int MaxParsedChars = 4_000_000;

    private static readonly Regex Ansi = new(
        @"\x1B\[[0-?]*[ -/]*[@-~]|\x1B\][^\x07\x1B]*(?:\x07|\x1B\\)|\x1B[@-Z\\-_]",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>색·커서 이동 같은 ANSI 제어열을 지운다.</summary>
    public static string StripAnsi(string text) => Ansi.Replace(text, string.Empty);

    /// <summary>
    /// 글에서 스키마에 맞는 JSON 객체를 찾는다. 맞는 것이 있으면 <c>Result</c>, 없으면 <c>Result</c> 는 null 이고
    /// <c>Issues</c> 에 이유가 담긴다(JSON 자체가 없으면 <c>Found</c> 가 false, 있었지만 스키마에 어긋났으면 가장 뒤 후보의 어긋난 곳).
    /// 스키마 자체를 그대로 되풀이한 객체는 결과로 보지 않는다.
    /// </summary>
    public static (JsonElement? Result, bool Found, IReadOnlyList<string> Issues) FindResult(string text, JsonElement schema)
    {
        var search = text.Length > MaxSearchChars ? text[^MaxSearchChars..] : text;
        var schemaNode = JsonNode.Parse(schema.GetRawText());
        IReadOnlyList<string> lastIssues = [];
        var found = false;
        var attempts = 0;
        long parsed = 0;
        foreach (var candidate in Candidates(search))
        {
            if (attempts++ >= MaxCandidates || (parsed += candidate.Length) > MaxParsedChars)
            {
                break;
            }

            // 중복 키({"a":1,"a":2})는 구문 분석·비교·검증 중 ArgumentException 을 낸다. 그 후보만 건너뛰고 다음 후보로 간다.
            try
            {
                var node = JsonNode.Parse(candidate);
                if (node is not JsonObject || JsonNode.DeepEquals(node, schemaNode))
                {
                    continue;
                }

                found = true;
                var issues = OpenAiSchemaCheck.Validate(schema, node);
                if (issues.Count == 0)
                {
                    using var document = JsonDocument.Parse(node.ToJsonString());
                    return (document.RootElement.Clone(), true, []);
                }

                lastIssues = lastIssues.Count == 0 ? issues : lastIssues;
            }
            catch (Exception ex) when (ex is JsonException or ArgumentException)
            {
                // 이 후보는 쓸 수 없다.
            }
        }

        return (null, found, lastIssues);
    }

    // 뒤에 있는 것부터: 코드 블록 → 짝이 맞는 {…} 덩어리(안쪽 것도 포함, 닫힌 위치가 뒤인 순서).
    private static IEnumerable<string> Candidates(string text)
    {
        var blocks = FencedBlocks(text);
        for (var i = blocks.Count - 1; i >= 0; i--)
        {
            yield return blocks[i];
        }

        var spans = BraceSpans(text);
        for (var i = spans.Count - 1; i >= 0; i--)
        {
            yield return text[spans[i].Start..(spans[i].End + 1)];
        }
    }

    // 줄 단위로 여닫는 줄(``` 뒤에 어떤 언어 이름이 와도 됨)을 맞춘다. 닫히지 않은 블록은 버린다.
    private static List<string> FencedBlocks(string text)
    {
        var blocks = new List<string>();
        var body = new StringBuilder();
        var open = false;
        foreach (var raw in text.Split('\n'))
        {
            var trimmed = raw.Trim();
            if (!open)
            {
                if (trimmed.StartsWith("```", StringComparison.Ordinal))
                {
                    open = true;
                    body.Clear();
                }
            }
            else if (trimmed == "```")
            {
                blocks.Add(body.ToString());
                open = false;
            }
            else
            {
                body.Append(raw.TrimEnd('\r')).Append('\n');
            }
        }

        return blocks;
    }

    // 한 번의 훑기로 짝이 맞는 모든 {…} 의 (시작, 끝)을 닫힌 순서로 모은다. 문자열(따옴표·이스케이프) 안의 괄호는 세지 않는다.
    // 짝이 없는 여는 괄호는 스택에 남을 뿐 뒤의 객체를 가리지 않는다.
    private static List<(int Start, int End)> BraceSpans(string text)
    {
        var spans = new List<(int, int)>();
        var starts = new Stack<int>();
        var inString = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (c == '\\')
                {
                    i++;
                }
                else if (c == '"')
                {
                    inString = false;
                }

                continue;
            }

            switch (c)
            {
                case '"' when starts.Count > 0:
                    inString = true;
                    break;
                case '{':
                    starts.Push(i);
                    break;
                case '}' when starts.Count > 0:
                    spans.Add((starts.Pop(), i));
                    break;
            }
        }

        return spans;
    }
}
