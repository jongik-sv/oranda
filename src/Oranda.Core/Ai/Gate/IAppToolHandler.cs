using System.Globalization;
using System.Text.Json.Nodes;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Protocol;

namespace Oranda.Core.Ai.Gate;

/// <summary>
/// 앱 도구 처리자(계약 A1, 설계 agent-cli-control §3.2). Host 가 <c>App</c>·<c>Lang</c> 인 도구(<c>AppTools.All</c>)를 처리한다. DB 세션을 받지 않는다.
/// 도구 이름마다 하나를 <c>[OrandaService(typeof(IAppToolHandler))]</c> 로(기존 <c>IAgentToolHandler</c> 와 같은 방식) 등록하고 다리(A4)가 <see cref="ToolName"/> 으로 찾는다.
/// <para>
/// 다리·게이트가 미리 하는 일: 토큰 → 허가, 인자 정리(스키마에 선언된 키만), <c>connection</c> 인자가 있으면 허가 안 접속으로 풀고
/// 정의의 최소 수준·운영 제한 검사, 감사 한 줄, 결과 80,000자 한도, <c>row_values</c> 등 행 값 필드 가림(<see cref="Connection"/> 이 있을 때).
/// </para>
/// <para>
/// 처리자가 하는 일: 탭처럼 <b>대상이 접속을 정하는</b> 도구는 <see cref="AgentGrantView.LevelFor"/>·<see cref="AgentGrantView.CanReadTabText"/>·<see cref="AgentGrantView.IsLocked"/> 로
/// 스스로 판단하고, 행 값 표(<c>row_values</c>)의 민감 열은 <c>***</c> 로 바꾼다(표 위치로는 게이트가 열을 알 수 없다).
/// 실패는 예외 대신 <see cref="AgentToolOutput.Error(string, string)"/>(코드 = <see cref="AgentErrorCodes"/>)로 돌려준다.
/// </para>
/// </summary>
public interface IAppToolHandler
{
    /// <summary><c>AppToolNames</c> 의 값.</summary>
    string ToolName { get; }

    ValueTask<AgentToolOutput> ExecuteAsync(AppToolInvocation invocation, CancellationToken ct);
}

/// <summary>다리가 앱 도구 처리자에게 넘기는 호출 정보.</summary>
/// <param name="Grant">호출한 외부 허가(사본).</param>
/// <param name="Client">이 연결의 클라이언트 이름(hello 의 <c>client.name</c>). 띠 글(「Claude Code 가 편집을 제안했습니다」)에 쓴다.</param>
/// <param name="Arguments">정리된 인자(스키마의 snake_case 이름). 읽기는 <see cref="AppToolArgs"/>.</param>
/// <param name="Connection">
/// <c>connection</c> 인자를 푼 허가 안 접속. 인자가 없고 허가 접속이 하나면 그 접속, 스키마에 <c>connection</c> 이 없거나 인자가 빠지고 접속이 여럿이면 null
/// (그 경우 <c>connection</c> 이 필수인 도구는 다리가 <c>connection_required</c> 로 미리 거부한다).
/// </param>
public sealed record AppToolInvocation(
    AgentGrantView Grant,
    string Client,
    JsonObject Arguments,
    AgentGrantConnection? Connection = null)
{
    /// <summary>이 호출의 수준: <see cref="Connection"/> 이 있으면 그 수준, 없으면 <see cref="AiAccessLevel.Off"/>.</summary>
    public AiAccessLevel Level => Connection?.Level ?? AiAccessLevel.Off;
}

/// <summary>
/// 처리자 공용 인자 읽기. 다리가 스키마 모양을 이미 검사했지만 기본값·범위 자르기는 처리자 몫이라 같은 규칙으로 읽는다.
/// 틀린 값은 <see cref="AppToolArgumentException"/>(코드 <c>invalid_arguments</c>)를 던지므로 처리자는 <see cref="AppToolOutputs.Guard"/> 로 감싼다.
/// </summary>
public static class AppToolArgs
{
    /// <summary>문자열 인자. 없거나 null 이면 null, 문자열이 아니면 예외.</summary>
    public static string? Text(JsonObject args, string name)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (!args.TryGetPropertyValue(name, out var node) || node is null)
        {
            return null;
        }

        return node is JsonValue value && value.TryGetValue<string>(out var text)
            ? text
            : throw new AppToolArgumentException(name, "must be a string");
    }

    /// <summary>꼭 있어야 하는 문자열 인자(빈 글 포함 허용 여부는 <paramref name="allowEmpty"/>).</summary>
    public static string RequiredText(JsonObject args, string name, bool allowEmpty = false)
    {
        var text = Text(args, name) ?? throw new AppToolArgumentException(name, "is required");
        return !allowEmpty && text.Length == 0 ? throw new AppToolArgumentException(name, "must not be empty") : text;
    }

    /// <summary>정수 인자. 없으면 <paramref name="fallback"/>, 범위 밖이면 예외(자르지 않는다 — 스키마와 같은 규칙).</summary>
    public static int Number(JsonObject args, string name, int fallback, int min = int.MinValue, int max = int.MaxValue)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (!args.TryGetPropertyValue(name, out var node) || node is null)
        {
            return fallback;
        }

        // 파이프 JSON(JsonElement)이든 처리자가 C# 값으로 만든 노드(long·int·double·decimal)든 같은 글자 꼴로 읽는다.
        if (node is not JsonValue value
            || value.GetValueKind() != System.Text.Json.JsonValueKind.Number
            || !decimal.TryParse(value.ToJsonString(), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out var number)
            || decimal.Truncate(number) != number)
        {
            throw new AppToolArgumentException(name, "must be an integer");
        }

        return number < min || number > max
            ? throw new AppToolArgumentException(name, $"is out of range ({min.ToString(CultureInfo.InvariantCulture)}..{max.ToString(CultureInfo.InvariantCulture)})")
            : (int)number;
    }

    /// <summary>참/거짓 인자. 없으면 <paramref name="fallback"/>.</summary>
    public static bool Flag(JsonObject args, string name, bool fallback)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (!args.TryGetPropertyValue(name, out var node) || node is null)
        {
            return fallback;
        }

        return node is JsonValue value && value.TryGetValue<bool>(out var flag)
            ? flag
            : throw new AppToolArgumentException(name, "must be true or false");
    }

    /// <summary>문자열 배열 인자. 없으면 빈 목록.</summary>
    public static IReadOnlyList<string> Texts(JsonObject args, string name)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (!args.TryGetPropertyValue(name, out var node) || node is null)
        {
            return [];
        }

        if (node is not JsonArray array)
        {
            throw new AppToolArgumentException(name, "must be an array of strings");
        }

        var list = new List<string>(array.Count);
        foreach (var item in array)
        {
            list.Add(item is JsonValue value && value.TryGetValue<string>(out var text)
                ? text
                : throw new AppToolArgumentException(name, "must be an array of strings"));
        }

        return list;
    }

    /// <summary>
    /// 범위 인자(<c>range</c>): 낱말(<c>document</c>·<c>selection</c>·<c>statement</c>·<c>caret</c>)이면 <see cref="AppTextRange.Keyword"/>,
    /// <c>"시작:길이"</c> 면 <see cref="AppTextRange.Start"/>·<see cref="AppTextRange.Length"/>. 없으면 <paramref name="fallback"/> 낱말.
    /// </summary>
    public static AppTextRange Range(JsonObject args, string name, string fallback)
    {
        var text = Text(args, name) ?? fallback;
        return AppTextRange.TryParse(text, out var range)
            ? range
            : throw new AppToolArgumentException(name, "must be document, selection, statement, caret or start:length");
    }
}

/// <summary><c>range</c> 인자 값. 낱말이거나 위치(UTF-16 시작·길이)다.</summary>
public readonly record struct AppTextRange(string? Keyword, int Start, int Length)
{
    private static readonly string[] Keywords = ["document", "selection", "statement", "caret"];

    public bool IsKeyword => Keyword is not null;

    public static AppTextRange Of(int start, int length) => new(null, start, length);

    /// <summary>낱말(대소문자 무시) 또는 <c>"시작:길이"</c>(둘 다 0 이상 정수)를 읽는다.</summary>
    public static bool TryParse(string? text, out AppTextRange range)
    {
        range = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.Trim();
        foreach (var keyword in Keywords)
        {
            if (string.Equals(keyword, trimmed, StringComparison.OrdinalIgnoreCase))
            {
                range = new AppTextRange(keyword, 0, 0);
                return true;
            }
        }

        var colon = trimmed.IndexOf(':', StringComparison.Ordinal);
        if (colon > 0
            && int.TryParse(trimmed.AsSpan(0, colon), NumberStyles.None, CultureInfo.InvariantCulture, out var start)
            && int.TryParse(trimmed.AsSpan(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var length))
        {
            range = Of(start, length);
            return true;
        }

        return false;
    }

    public override string ToString() => Keyword ?? $"{Start.ToString(CultureInfo.InvariantCulture)}:{Length.ToString(CultureInfo.InvariantCulture)}";
}

/// <summary>인자가 틀렸다. <see cref="AppToolOutputs.Guard"/> 가 <c>invalid_arguments</c> 결과로 바꾼다.</summary>
public sealed class AppToolArgumentException(string argument, string problem)
    : ArgumentException($"The argument '{argument}' {problem}.", argument);

/// <summary>처리자 공용 결과 만들기.</summary>
public static class AppToolOutputs
{
    /// <summary>앱이 쓴 거부. <paramref name="code"/> 는 <see cref="AgentErrorCodes"/> 값.</summary>
    public static AgentToolOutput Deny(string code, string message) => AgentToolOutput.Error(code, message);

    public static AgentToolOutput TabNotFound(string? tab) =>
        Deny(AgentErrorCodes.TabNotFound, tab is null ? "There is no active tab." : "No tab has this id. Call list_tabs.");

    public static AgentToolOutput Locked() =>
        Deny(AgentErrorCodes.Locked, "This tab belongs to a connection that this external connection may not use.");

    public static AgentToolOutput ReadOnly() =>
        Deny(AgentErrorCodes.ReadOnly, "This tab is read-only. Use new_tab to put the text in a new tab.");

    /// <summary>
    /// 처리 본문을 감싸 <see cref="AppToolArgumentException"/> 을 <c>invalid_arguments</c> 결과로 바꾼다. 다른 예외는 그대로 던진다(다리가 <c>tool_exception</c> 으로 감사한다).
    /// </summary>
    public static async ValueTask<AgentToolOutput> Guard(Func<ValueTask<AgentToolOutput>> body)
    {
        ArgumentNullException.ThrowIfNull(body);
        try
        {
            return await body().ConfigureAwait(false);
        }
        catch (AppToolArgumentException ex)
        {
            return Deny(AgentErrorCodes.InvalidArguments, ex.Message);
        }
    }
}
