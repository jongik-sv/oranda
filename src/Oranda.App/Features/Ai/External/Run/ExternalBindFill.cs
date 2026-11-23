using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Oranda.App.Features.Ai.External.Editor;
using Oranda.App.Features.Binds;
using Oranda.App.Features.SqlEditor;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Ai.Protocol;
using Oranda.Core.Ai.Schemas;
using Oranda.Core.Execution;

namespace Oranda.App.Features.Ai.External.Run;

/// <summary>바인드 한 줄에 넣을 값(목록 형식에 맞춰 정리한 글). <see cref="NewType"/> 은 목록에 아직 줄이 없을 때 만들 줄의 형식.</summary>
internal sealed record BindFillItem(string Name, string Text, BindDataType? NewType);

/// <summary>검사를 마친 채우기 계획. 거부면 <see cref="Denial"/>.</summary>
internal sealed record BindFillPlan(IReadOnlyList<BindFillItem> Items, AgentToolOutput? Denial)
{
    public static BindFillPlan Refuse(string code, string message) => new([], AppToolOutputs.Deny(code, message));
}

/// <summary>
/// 에이전트가 보낸 바인드 값을 탭의 「바인드 변수」 목록에 넣는다(<c>set_binds</c>·<c>run_tab</c> 의 <c>binds</c>, agent-cli-set-binds 2026-11-23).
/// 사람이 바인드 창에 친 것과 같은 길이다: 줄의 형식(VARCHAR2·NUMBER·DATE …)을 따르고, 칸을 떠날 때처럼 글을 정리한다(<see cref="BindValueFormatter.Normalize"/>, 날짜 <c>20261123</c> → <c>2026-11-23 00:00:00</c>).
/// <list type="bullet">
/// <item>이름 = 탭이 아는 바인드(<see cref="ExternalEditorService.KnownBindNames"/>, <c>get_binds</c> 가 보이는 이름). 앞 <c>:</c> 는 뺀다. 모르는 이름은 거부(오타가 조용히 버려지지 않게).</item>
/// <item>값 = 글·숫자·참거짓·null(NULL). 목록에 줄이 없으면 글 = VARCHAR2, 숫자 = NUMBER, 참거짓 = BOOLEAN 줄을 만든다(<c>query</c> 의 <c>binds</c> 와 같은 규칙).</item>
/// <item>하나라도 맞지 않으면 아무것도 바꾸지 않는다(<see cref="AgentErrorCodes.BindInvalid"/>). 값 글은 거부 글에 싣지 않는다.</item>
/// </list>
/// 검사(<see cref="Plan"/>)와 넣기(<see cref="Apply"/>)를 나눈 까닭: <c>run_tab</c> 은 거부될 수 있는 관문(접속·바쁨·문장 가드)을 다 지난 뒤에만 목록을 바꾼다.
/// </summary>
internal static class ExternalBindFill
{
    /// <summary><c>binds</c> 인자. 없으면 null, 객체가 아니면 <see cref="AppToolArgumentException"/>.</summary>
    public static JsonObject? Arg(JsonObject args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (!args.TryGetPropertyValue(AppFields.Binds, out var node) || node is null)
        {
            return null;
        }

        return node as JsonObject ?? throw new AppToolArgumentException(AppFields.Binds, "must be an object of name to value");
    }

    /// <summary>값을 검사해 계획을 만든다. 목록은 바꾸지 않는다(UI 스레드).</summary>
    public static BindFillPlan Plan(SqlEditorViewModel tab, JsonObject binds)
    {
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentNullException.ThrowIfNull(binds);
        var known = new HashSet<string>(ExternalEditorService.KnownBindNames(tab), StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var items = new List<BindFillItem>();
        foreach (var (rawName, value) in binds)
        {
            var trimmed = rawName.Trim().TrimStart(':').Trim();
            if (trimmed.Length == 0)
            {
                throw new AppToolArgumentException(AppFields.Binds, "has an empty name");
            }

            var name = BindVariableRow.Normalize(trimmed);
            if (!known.Contains(name))
            {
                return BindFillPlan.Refuse(AgentErrorCodes.BindInvalid, ExternalRunText.UnknownBind(name));
            }

            if (!seen.Add(name))
            {
                return BindFillPlan.Refuse(AgentErrorCodes.BindInvalid, ExternalRunText.BindTwice(name));
            }

            var text = Text(value);
            var row = tab.Binds.Find(name);
            var type = row?.Type ?? NewRowType(value);
            var direction = row?.Direction ?? BindDirection.In;
            if (direction == BindDirection.Out || type == BindDataType.RefCursor)
            {
                return BindFillPlan.Refuse(AgentErrorCodes.BindInvalid, ExternalRunText.BindTakesNoValue(name));
            }

            var normalized = BindValueFormatter.Normalize(type, text);
            var input = new BindInput(name) { Type = type, Direction = direction, Text = normalized, IsNull = normalized.Length == 0 };
            if (BindValueFormatter.Validate(type, direction, normalized) is not null || input.ToBindValue(out _) is null)
            {
                return BindFillPlan.Refuse(AgentErrorCodes.BindInvalid, ExternalRunText.BindWrongType(name, TypeName(type), type is BindDataType.Date or BindDataType.Timestamp));
            }

            items.Add(new BindFillItem(name, normalized, row is null ? type : null));
        }

        return new BindFillPlan(items, null);
    }

    /// <summary>
    /// 계획대로 목록에 넣는다(UI 스레드). 줄이 없으면 만들고, 있으면 형식·방향은 그대로 두고 값만 바꾼다. 넣은 이름(<c>:</c> 없이)을 돌려준다.
    /// 사람이 보게 메시지 창에 「[에이전트] …」 한 줄을 남긴다(값은 바인드 창에 보이므로 이름만).
    /// </summary>
    public static IReadOnlyList<string> Apply(SqlEditorViewModel tab, BindFillPlan plan, Action<string>? log = null, string? client = null)
    {
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Denial is not null)
        {
            throw new InvalidOperationException("A refused plan cannot be applied.");
        }

        foreach (var item in plan.Items)
        {
            var row = tab.Binds.Find(item.Name);
            if (row is null)
            {
                row = tab.Binds.Add(item.Name);
                row.Type = item.NewType ?? BindDataType.Varchar2;
            }

            row.Text = item.Text;
        }

        var names = plan.Items.Select(i => i.Name).ToList();
        if (names.Count > 0)
        {
            log?.Invoke(ExternalRunText.LogBindsSet(ExternalRunSupport.ClientName(client), tab.Title, names));
        }

        return names;
    }

    /// <summary>JSON 값 → 바인드 창 글. 글은 그대로, 숫자는 JSON 글자 그대로, 참거짓은 TRUE/FALSE, null 은 빈 글(NULL).</summary>
    private static string Text(JsonNode? value) => value switch
    {
        null => string.Empty,
        JsonValue v when v.GetValueKind() == JsonValueKind.String => v.GetValue<string>(),
        JsonValue v when v.GetValueKind() == JsonValueKind.Number => v.ToJsonString(),
        JsonValue v when v.GetValueKind() == JsonValueKind.True => "TRUE",
        JsonValue v when v.GetValueKind() == JsonValueKind.False => "FALSE",
        JsonValue v when v.GetValueKind() == JsonValueKind.Null => string.Empty,
        _ => throw new AppToolArgumentException(AppFields.Binds, "values must be strings, numbers, true/false or null"),
    };

    /// <summary>목록에 줄이 없을 때 만들 줄의 형식(<c>query</c> 의 <c>binds</c> 와 같은 규칙).</summary>
    private static BindDataType NewRowType(JsonNode? value) => value is JsonValue v
        ? v.GetValueKind() switch
        {
            JsonValueKind.Number => BindDataType.Number,
            JsonValueKind.True or JsonValueKind.False => BindDataType.Boolean,
            _ => BindDataType.Varchar2,
        }
        : BindDataType.Varchar2;

    /// <summary><c>get_binds</c> 의 <c>type</c> 과 같은 글(소문자 형식 이름).</summary>
    private static string TypeName(BindDataType type) => type.ToString().ToLowerInvariant();
}
