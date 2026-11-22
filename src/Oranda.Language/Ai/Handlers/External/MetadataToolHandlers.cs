using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Ai.Protocol;
using Oranda.Core.Ai.Schemas;
using Oranda.Core.Composition;
using Oranda.Core.Metadata;

namespace Oranda.Language.Ai.Handlers.External;

/// <summary>
/// <c>find_objects</c>(설계 §5.3 「메타데이터」): 이름 패턴(<c>%</c>·<c>_</c>)으로 객체를 찾는다. 객체 목록 캐시를 쓰고 상태(INVALID)만 서버에 묻는다.
/// 표·뷰 주석은 결과에 나온 소유자(최대 <see cref="MaxCommentOwners"/>명)만 읽는다.
/// </summary>
[OrandaService(typeof(IAgentToolHandler))]
internal sealed class FindObjectsHandler(IMetadataProvider metadata) : ExternalDbHandlerBase
{
    private const int MaxCommentOwners = 10;

    private static readonly string[] CommentTypes = ["TABLE", "VIEW", "MATERIALIZED VIEW"];

    public override string ToolName => AppToolNames.FindObjects;

    protected override async Task<JsonNode> RunAsync(AgentToolInvocation inv, CancellationToken ct)
    {
        var args = inv.Arguments;
        var pattern = ExternalObjectResolver.Unquote(AppToolArgs.RequiredText(args, AppFields.NameLike).Trim());
        var owner = AppToolArgs.Text(args, AppFields.Owner) is { Length: > 0 } o ? ExternalObjectResolver.Unquote(o.Trim()) : null;
        var types = AppToolArgs.Texts(args, AppFields.Types).Select(t => t.Trim().ToUpperInvariant()).Where(t => t.Length > 0).Distinct().ToList();
        var limit = AppToolArgs.Number(args, AppFields.Limit, AppToolValues.DefaultRows, 1, AppToolValues.MaxRows);

        var query = new ObjectQuery { Owner = owner, ObjectTypes = types, NamePattern = pattern, Limit = limit + 1 };
        var found = await metadata.ListObjectsAsync(inv.Db, query, ct).ConfigureAwait(false);
        var truncated = found.Count > limit;
        var shown = found.Take(limit).ToList();

        var invalid = shown.Count == 0 ? new HashSet<DbObjectRef>() : await InvalidAsync(inv, query, ct).ConfigureAwait(false);
        var comments = await CommentsAsync(inv, shown, ct).ConfigureAwait(false);

        var objects = new JsonArray();
        foreach (var item in shown)
        {
            var json = new JsonObject
            {
                ["owner"] = item.Owner,
                ["name"] = item.Name,
                ["type"] = item.ObjectType,
            };
            if (invalid is not null)
            {
                json["status"] = invalid.Contains(item with { SubName = null }) ? "INVALID" : "VALID";
            }

            if (comments.TryGetValue((item.Owner, item.Name), out var comment) && CommentTypes.Contains(item.ObjectType, StringComparer.Ordinal))
            {
                json["comment"] = comment;
            }

            objects.Add(json);
        }

        return new JsonObject { ["objects"] = objects, ["truncated"] = truncated };
    }

    /// <summary>같은 조건의 INVALID 객체. 권한·서버 오류로 읽지 못하면 null(상태 칸을 뺀다).</summary>
    private async Task<HashSet<DbObjectRef>?> InvalidAsync(AgentToolInvocation inv, ObjectQuery query, CancellationToken ct)
    {
        try
        {
            var invalid = await metadata.ListObjectsAsync(inv.Db, query with { InvalidOnly = true, Limit = 1000 }, ct).ConfigureAwait(false);
            return [.. invalid.Select(o => o with { SubName = null })];
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    private async Task<Dictionary<(string Owner, string Name), string>> CommentsAsync(AgentToolInvocation inv, IReadOnlyList<DbObjectRef> shown, CancellationToken ct)
    {
        var result = new Dictionary<(string, string), string>();
        var owners = shown.Where(o => CommentTypes.Contains(o.ObjectType, StringComparer.Ordinal)).Select(o => o.Owner).Distinct(StringComparer.Ordinal).Take(MaxCommentOwners);
        foreach (var owner in owners)
        {
            try
            {
                foreach (var (name, comment) in await metadata.ListTableCommentsAsync(inv.Db, owner, ct).ConfigureAwait(false))
                {
                    if (!string.IsNullOrWhiteSpace(comment))
                    {
                        result[(owner, name)] = comment;
                    }
                }
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                // 주석은 덧붙이는 정보다. 읽지 못해도 목록은 준다.
            }
        }

        return result;
    }
}

/// <summary><c>list_schemas</c>: 접속에서 보이는 스키마(소유자) 이름.</summary>
[OrandaService(typeof(IAgentToolHandler))]
internal sealed class ListSchemasHandler(IMetadataProvider metadata) : ExternalDbHandlerBase
{
    public override string ToolName => AppToolNames.ListSchemas;

    protected override async Task<JsonNode> RunAsync(AgentToolInvocation inv, CancellationToken ct)
    {
        var schemas = await metadata.ListSchemasAsync(inv.Db, ct).ConfigureAwait(false);
        return new JsonObject { ["schemas"] = new JsonArray([.. schemas.Select(s => (JsonNode?)JsonValue.Create(s))]) };
    }
}

/// <summary>
/// <c>get_ddl</c>: DBMS_METADATA DDL. 글은 <c>sql_text</c> 칸에 넣는다 — 게이트가 리터럴 가림이 필요한 실행에서 이 칸을 가린다(<see cref="AgentFields.SqlTextFields"/>).
/// </summary>
[OrandaService(typeof(IAgentToolHandler))]
internal sealed class GetDdlHandler(IMetadataProvider metadata) : ExternalDbHandlerBase
{
    public override string ToolName => AppToolNames.GetDdl;

    protected override async Task<JsonNode> RunAsync(AgentToolInvocation inv, CancellationToken ct)
    {
        var target = await ExternalObjects.RequireAsync(metadata, inv, ct).ConfigureAwait(false);
        var ddl = await metadata.GetDdlAsync(inv.Db, target.Ref, new DdlOptions(), ct).ConfigureAwait(false);
        return new JsonObject
        {
            ["owner"] = target.Owner,
            ["name"] = target.Name,
            ["type"] = target.Type,
            ["sql_text"] = ddl,
        };
    }
}

/// <summary>
/// <c>get_source</c>: PL/SQL 원문(all_source). 패키지·타입은 명세 뒤에 본문을 붙인다. <c>member</c> 를 주면 그 이름의 프로시저·함수 부분(명세 선언과 본문 구현, 겹지정 모두)만.
/// </summary>
[OrandaService(typeof(IAgentToolHandler))]
internal sealed class GetSourceHandler(IMetadataProvider metadata) : ExternalDbHandlerBase
{
    private static readonly string[] SourceTypes = ["PACKAGE", "PACKAGE BODY", "PROCEDURE", "FUNCTION", "TYPE", "TYPE BODY", "TRIGGER", "JAVA SOURCE"];

    public override string ToolName => AppToolNames.GetSource;

    protected override async Task<JsonNode> RunAsync(AgentToolInvocation inv, CancellationToken ct)
    {
        var target = await ExternalObjects.RequireAsync(metadata, inv, ct).ConfigureAwait(false);
        var type = target.Types.Contains("PACKAGE", StringComparer.Ordinal) ? "PACKAGE"
            : target.Types.Contains("TYPE", StringComparer.Ordinal) ? "TYPE"
            : target.Types.FirstOrDefault(t => SourceTypes.Contains(t, StringComparer.Ordinal));
        if (type is null)
        {
            throw new ExternalToolException(AgentErrorCodes.InvalidArguments, ExternalToolText.NotPlSql);
        }

        var text = await metadata.GetSourceAsync(inv.Db, new DbObjectRef(target.Owner, target.Name, type), ct).ConfigureAwait(false);
        var body = type + " BODY";
        if (target.Types.Contains(body, StringComparer.Ordinal))
        {
            var bodyText = await metadata.GetSourceAsync(inv.Db, new DbObjectRef(target.Owner, target.Name, body), ct).ConfigureAwait(false);
            text = text.TrimEnd() + "\n/\n\n" + bodyText.TrimEnd() + "\n/\n";
        }

        IReadOnlyList<string> members = type switch
        {
            "PACKAGE" => [.. (await metadata.GetProceduresAsync(inv.Db, new DbObjectRef(target.Owner, target.Name, type), ct).ConfigureAwait(false))
                .Select(p => p.Name).Distinct(StringComparer.Ordinal)],
            "PROCEDURE" or "FUNCTION" => [target.Name],
            _ => [],
        };

        if (AppToolArgs.Text(inv.Arguments, AppFields.Member) is { Length: > 0 } member)
        {
            text = MemberText(text, ExternalObjectResolver.Unquote(member.Trim()))
                ?? throw new ExternalToolException(AgentErrorCodes.InvalidArguments, ExternalToolText.MemberNotFound);
        }

        return new JsonObject
        {
            ["owner"] = target.Owner,
            ["name"] = target.Name,
            ["type"] = type,
            ["sql_text"] = text,
            ["members"] = new JsonArray([.. members.Select(m => (JsonNode?)JsonValue.Create(m))]),
        };
    }

    /// <summary>
    /// 원문에서 멤버 <paramref name="member"/> 의 선언·구현 부분을 모은다. 머리(<c>PROCEDURE 이름</c>·<c>FUNCTION 이름</c>) 뒤에 <c>IS</c>/<c>AS</c> 보다 <c>;</c> 가 먼저 오면 선언이고,
    /// 아니면 <c>END 이름;</c> 까지가 구현이다. 없으면 null.
    /// </summary>
    internal static string? MemberText(string text, string member)
    {
        var name = Regex.Escape(member);
        var headers = Regex.Matches(text, $@"\b(PROCEDURE|FUNCTION)\s+""?{name}""?(?=[\s(;]|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var pieces = new List<string>();
        foreach (Match header in headers)
        {
            var start = LineStart(text, header.Index);
            var semicolon = text.IndexOf(';', header.Index);
            var isAs = Regex.Match(text[header.Index..], @"\b(IS|AS)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            int end;
            if (semicolon >= 0 && (!isAs.Success || semicolon < header.Index + isAs.Index))
            {
                end = semicolon + 1;
            }
            else
            {
                var close = Regex.Match(text[header.Index..], $@"\bEND\s+""?{name}""?\s*;", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                end = close.Success ? header.Index + close.Index + close.Length : text.Length;
            }

            pieces.Add(text[start..end].TrimEnd());
        }

        return pieces.Count == 0 ? null : string.Join("\n\n", pieces) + "\n";
    }

    private static int LineStart(string text, int index)
    {
        var newline = index == 0 ? -1 : text.LastIndexOf('\n', index - 1);
        return newline + 1;
    }
}

/// <summary><c>resolve_names</c>: 이름을 DB 처럼 푼다(현재 스키마·동의어). 없는 객체와 볼 수 없는 객체는 둘 다 <c>found:false</c>.</summary>
[OrandaService(typeof(IAgentToolHandler))]
internal sealed class ResolveNamesHandler(IMetadataProvider metadata) : ExternalDbHandlerBase
{
    private const int MaxNames = 50;

    public override string ToolName => AppToolNames.ResolveNames;

    protected override async Task<JsonNode> RunAsync(AgentToolInvocation inv, CancellationToken ct)
    {
        var names = AppToolArgs.Texts(inv.Arguments, AppFields.Names);
        if (names.Count is 0 or > MaxNames)
        {
            throw new AppToolArgumentException(AppFields.Names, $"must have 1..{MaxNames} names");
        }

        var result = new JsonArray();
        foreach (var input in names)
        {
            ExternalObject? found;
            try
            {
                found = await ExternalObjectResolver.ResolveAsync(metadata, inv.Db, input, DefaultSchema(inv), ct).ConfigureAwait(false);
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                found = null; // 권한·사전 오류도 「찾지 못함」과 같게 보인다.
            }

            var item = new JsonObject { ["input"] = input, ["found"] = found is not null };
            if (found is not null)
            {
                item["owner"] = found.Owner;
                item["name"] = found.Name;
                item["type"] = found.Type;
                item["via_synonym"] = found.ViaSynonym;
            }

            result.Add(item);
        }

        return new JsonObject { ["names"] = result };
    }
}

/// <summary><c>get_procedure_args</c>: 프로시저·함수·패키지 멤버의 인자(all_arguments). 겹지정은 <c>overload</c> 로 가른다.</summary>
[OrandaService(typeof(IAgentToolHandler))]
internal sealed class GetProcedureArgsHandler(IMetadataProvider metadata) : ExternalDbHandlerBase
{
    private static readonly string[] CallableTypes = ["PACKAGE", "PROCEDURE", "FUNCTION"];

    public override string ToolName => AppToolNames.GetProcedureArgs;

    protected override async Task<JsonNode> RunAsync(AgentToolInvocation inv, CancellationToken ct)
    {
        var target = await ExternalObjects.RequireAsync(metadata, inv, ct).ConfigureAwait(false);
        var type = CallableTypes.FirstOrDefault(t => target.Types.Contains(t, StringComparer.Ordinal))
            ?? throw new ExternalToolException(AgentErrorCodes.InvalidArguments, ExternalToolText.NoArguments);

        var members = new JsonArray();
        foreach (var p in await metadata.GetProceduresAsync(inv.Db, new DbObjectRef(target.Owner, target.Name, type), ct).ConfigureAwait(false))
        {
            var item = new JsonObject { ["member"] = p.Name };
            if (p.Overload is { } overload)
            {
                item["overload"] = overload;
            }

            item["args"] = new JsonArray([.. p.Arguments.OrderBy(a => a.Position).Select(a => (JsonNode?)new JsonObject
            {
                ["name"] = a.Name,
                ["type"] = a.TypeName ?? a.DataType,
                ["in_out"] = a.Direction switch
                {
                    ArgumentDirection.Out => "OUT",
                    ArgumentDirection.InOut => "IN OUT",
                    _ => "IN",
                },
                ["default"] = a.HasDefault,
            })]);
            if (p.IsFunction && p.ReturnType is { Length: > 0 } returns)
            {
                item["returns"] = returns;
            }

            members.Add(item);
        }

        return new JsonObject { ["owner"] = target.Owner, ["name"] = target.Name, ["type"] = type, ["members"] = members };
    }
}

/// <summary>메타 도구의 <c>object</c> 인자 풀기.</summary>
internal static class ExternalObjects
{
    /// <summary>
    /// <c>object</c> 인자를 실제 객체로 푼다. 게이트가 범위 검사에 쓴 이름(<c>OWNER.NAME</c>)으로 바꿔 넘겼으면 그 이름을 그대로 푼다.
    /// 없으면 게이트의 범위 밖 거부와 같은 코드(<c>scope</c>)로 거부한다(존재 여부를 알리지 않는다).
    /// </summary>
    public static async Task<ExternalObject> RequireAsync(IMetadataProvider metadata, AgentToolInvocation inv, CancellationToken ct)
    {
        var name = inv.Resolved.TryGetValue(AgentFields.ObjectName, out var resolved)
            ? resolved.Owner + "." + resolved.Name
            : AppToolArgs.RequiredText(inv.Arguments, AgentFields.ObjectName);
        var schema = string.IsNullOrWhiteSpace(inv.Session.Context.CurrentSchema) ? null : inv.Session.Context.CurrentSchema;
        return await ExternalObjectResolver.ResolveAsync(metadata, inv.Db, name, schema, ct).ConfigureAwait(false)
            ?? throw new ExternalToolException(AgentErrorCodes.Scope, ExternalToolText.ObjectNotFound);
    }
}
