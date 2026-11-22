using System.Globalization;
using System.Text.Json.Nodes;
using Oranda.Core.Ai.Manual;
using Oranda.Core.Ai.Protocol;

namespace Oranda.Agent.Cli;

/// <summary>
/// <c>orandactl docs [검색어...] [--limit N]</c>: 매뉴얼 링크 목록(2단계 F3, E-27). 도구가 아니라 CLI 자체 명령이라 앱 없이 돈다 —
/// 색인은 Core 의 <see cref="OrandaManual"/> 표다. 결과 = 쪽(제목·주소·장·설명)과 검색어에 걸린 h2 의 주소(<c>#앵커</c>).
/// </summary>
internal static class DocsCommand
{
    /// <summary>명령 낱말.</summary>
    public const string Name = "docs";

    /// <summary>사용법(명령 이름 뒤).</summary>
    public const string Usage = "[query...] [--limit N]";

    /// <summary>검색어가 있을 때 기본 쪽 수.</summary>
    public const int DefaultLimit = 10;

    private const int MaxLimit = 100;
    private const string QueryField = "query";
    private const string LimitField = "limit";

    /// <summary><c>docs</c> 뒤 낱말들 → 검색어·쪽 수. 모르는 옵션은 사용법 오류.</summary>
    public static ParsedCommand Parse(IReadOnlyList<string> rest, GlobalOptions globals)
    {
        ArgumentNullException.ThrowIfNull(rest);
        var words = new List<string>();
        int? limit = null;
        for (var i = 1; i < rest.Count; i++)
        {
            var token = rest[i];
            if (token == "--limit")
            {
                if (i + 1 >= rest.Count || !int.TryParse(rest[++i], NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n < 1)
                {
                    throw new BridgeException(AgentErrorCodes.Usage, $"--limit needs a number from 1 to {MaxLimit}. Usage: orandactl {Name} {Usage}");
                }

                limit = Math.Min(n, MaxLimit);
                continue;
            }

            if (token.StartsWith("--", StringComparison.Ordinal))
            {
                throw new BridgeException(AgentErrorCodes.Usage, $"Unknown option '{token}' for 'orandactl {Name}'. Usage: orandactl {Name} {Usage}");
            }

            words.Add(token);
        }

        var arguments = new JsonObject { [QueryField] = string.Join(' ', words) };
        if (limit is { } l)
        {
            arguments[LimitField] = l;
        }

        return new ParsedCommand(CommandKind.Docs, globals, Arguments: arguments);
    }

    /// <summary>
    /// 결과 JSON: <c>{query, site, pages:[{title, url, section, description, headings?:[{title, url}]}], more, hint?}</c>.
    /// 빈 검색어 = 모든 쪽(h2 없이). 쪽이 없으면 <c>hint</c> 로 다르게 찾으라고 알린다(오류가 아니다, 종료 0).
    /// </summary>
    public static JsonObject Run(JsonObject? arguments) => Run(arguments, OrandaManual.All);

    internal static JsonObject Run(JsonObject? arguments, IReadOnlyList<OrandaManual.Entry> entries)
    {
        var query = (string?)arguments?[QueryField] ?? string.Empty;
        var limit = arguments?[LimitField] is JsonValue v && v.TryGetValue<int>(out var l)
            ? l
            : query.Trim().Length == 0 ? entries.Count : DefaultLimit;
        var all = OrandaManual.Find(query, entries, int.MaxValue);
        var result = new JsonObject
        {
            [QueryField] = query,
            ["site"] = OrandaManual.BaseUrl + "/",
            ["pages"] = new JsonArray([.. all.Take(limit).Select(Page)]),
            ["more"] = all.Count > limit,
        };
        if (all.Count == 0)
        {
            result["hint"] = "No page matched every word. Try fewer or other words (Korean titles), or run 'orandactl docs' for every page.";
        }

        return result;
    }

    private static JsonNode Page(OrandaManual.Hit hit)
    {
        var page = new JsonObject
        {
            ["title"] = hit.Entry.Title,
            ["url"] = hit.Entry.Url,
            ["section"] = hit.Entry.Section,
            ["description"] = hit.Entry.Description,
        };
        if (hit.Headings.Count > 0)
        {
            page["headings"] = new JsonArray([.. hit.Headings.Select(h => (JsonNode)new JsonObject
            {
                ["title"] = h,
                ["url"] = hit.Entry.HeadingUrl(h),
            })]);
        }

        return page;
    }
}
