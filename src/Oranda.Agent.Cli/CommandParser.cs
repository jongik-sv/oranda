using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Oranda.Core.Ai.Protocol;
using Oranda.Core.Ai.Schemas;

namespace Oranda.Agent.Cli;

/// <summary>해석한 명령의 종류.</summary>
internal enum CommandKind
{
    /// <summary>사용법을 보인다.</summary>
    Help,

    /// <summary>도구 하나를 부른다(하위 명령·<c>call</c>).</summary>
    Tool,

    /// <summary>도구 목록(<c>tools</c>).</summary>
    Tools,

    /// <summary>모든 연결 파일(<c>status --all</c>). 앱에 붙지 않는다.</summary>
    StatusAll,

    /// <summary>MCP stdio 서버(<c>mcp</c>).</summary>
    Mcp,

    /// <summary>매뉴얼 링크(<c>docs</c>, <see cref="DocsCommand"/>). 앱에 붙지 않는다.</summary>
    Docs,
}

/// <summary>모든 명령에 쓰는 옵션.</summary>
/// <param name="Instance"><c>--instance &lt;pid|label&gt;</c>.</param>
/// <param name="Pipe"><c>--pipe &lt;이름&gt;</c>(통로를 직접 줄 때, 주로 내장 실행).</param>
/// <param name="Text"><c>--text</c>: 사람이 읽는 글로 낸다(E-19).</param>
internal sealed record GlobalOptions(string? Instance, string? Pipe, bool Text);

/// <summary>해석 결과.</summary>
internal sealed record ParsedCommand(CommandKind Kind, GlobalOptions Globals, string? Tool = null, JsonObject? Arguments = null);

/// <summary>
/// 명령줄을 도구 호출 하나로 바꾼다(설계 §4.1·§4.2). 긴 글(SQL·탭 글)은 <c>--file F</c> 또는 <c>-</c>(stdin)로만 받는다 — 셸 따옴표 차이를 피한다.
/// 틀린 명령줄은 <see cref="BridgeException"/>(코드 <c>usage</c>, 종료 2).
/// </summary>
internal static class CommandParser
{
    private const string Stdin = "-";
    private const string FileOption = "--file";
    private const string JsonOption = "--json";

    public static ParsedCommand Parse(IReadOnlyList<string> args, Func<string, string> readFile, Func<string> readStdin)
    {
        ArgumentNullException.ThrowIfNull(args);
        var rest = new List<string>();
        string? instance = null;
        string? pipe = null;
        var text = false;
        var help = false;
        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--instance":
                    instance = ValueAt(args, ref i);
                    break;
                case "--pipe":
                    pipe = ValueAt(args, ref i);
                    break;
                case "--text":
                    text = true;
                    break;
                case "--help" or "-h":
                    help = true;
                    break;
                default:
                    rest.Add(args[i]);
                    break;
            }
        }

        var globals = new GlobalOptions(instance, pipe, text);
        if (help || rest.Count == 0 || rest[0] == "help")
        {
            return new ParsedCommand(CommandKind.Help, globals);
        }

        switch (rest[0])
        {
            case "mcp":
                NoMore(rest, 1, "mcp");
                return new ParsedCommand(CommandKind.Mcp, globals);
            case "tools":
                NoMore(rest, 1, "tools");
                return new ParsedCommand(CommandKind.Tools, globals);
            case "status" when rest.Count == 2 && rest[1] == "--all":
                return new ParsedCommand(CommandKind.StatusAll, globals);
            case "call":
                return ParseCall(rest, globals, readFile, readStdin);
            case DocsCommand.Name:
                return DocsCommand.Parse(rest, globals);
        }

        var spec = CommandTable.Find(rest, out var used)
            ?? throw Usage($"Unknown command '{string.Join(' ', rest.Take(2))}'. Run 'orandactl help'.");
        var arguments = Build(spec, rest.Skip(used).ToList(), readFile, readStdin);
        return new ParsedCommand(CommandKind.Tool, globals, spec.Tool, arguments);
    }

    /// <summary>사용법 글 전부(<c>orandactl help</c>).</summary>
    public static string HelpText()
    {
        var text = new StringBuilder();
        text.AppendLine("Usage: orandactl <command> [options]   (output: one JSON object on stdout; --text for readable text)");
        text.AppendLine("Common options: --instance <pid|label>  --text  --help");
        text.AppendLine();
        foreach (var spec in CommandTable.All)
        {
            text.Append("  orandactl ").Append(spec.Name);
            if (spec.Usage.Length > 0)
            {
                text.Append(' ').Append(spec.Usage);
            }

            text.Append("   -> ").AppendLine(spec.Tool);
        }

        text.AppendLine("  orandactl status --all   -> every connection file (no app call)");
        text.AppendLine("  orandactl call <tool> (--json J | --file F | -)   -> any tool");
        text.AppendLine("  orandactl tools   -> tool names, descriptions and schemas");
        text.AppendLine("  orandactl mcp [--pipe P] [--instance I]   -> MCP stdio server");
        text.AppendLine($"  orandactl {DocsCommand.Name} {DocsCommand.Usage}   -> links to the Oranda manual (no app call)");
        text.AppendLine();
        text.AppendLine("Exit codes: 0 ok (a rejected or stale proposal is ok), 1 tool failed, 2 usage, 3 Oranda unreachable or expired, 4 denied, 5 window not chosen.");
        return text.ToString();
    }

    private static ParsedCommand ParseCall(List<string> rest, GlobalOptions globals, Func<string, string> readFile, Func<string> readStdin)
    {
        if (rest.Count < 2 || rest[1].StartsWith('-'))
        {
            throw Usage("Usage: orandactl call <tool> (--json J | --file F | -).");
        }

        string? body = null;
        for (var i = 2; i < rest.Count; i++)
        {
            var piece = rest[i] switch
            {
                JsonOption => ValueAt(rest, ref i),
                FileOption => ReadFile(readFile, ValueAt(rest, ref i)),
                Stdin => readStdin(),
                _ => throw Usage($"Unexpected '{rest[i]}'. Usage: orandactl call <tool> (--json J | --file F | -)."),
            };
            if (body is not null)
            {
                throw Usage("Give the arguments only once (--json, --file or -).");
            }

            body = piece;
        }

        var arguments = string.IsNullOrWhiteSpace(body) ? [] : ParseObject(body, "arguments");
        return new ParsedCommand(CommandKind.Tool, globals, rest[1], arguments);
    }

    private static JsonObject Build(CommandSpec spec, List<string> tokens, Func<string, string> readFile, Func<string> readStdin)
    {
        var arguments = new JsonObject();
        var positionals = new List<string>();
        string? body = null;
        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (token == Stdin || token == FileOption)
            {
                if (spec.Body == BodyRule.None)
                {
                    throw Usage($"'orandactl {spec.Name}' does not read input. Usage: orandactl {spec.Name} {spec.Usage}".TrimEnd());
                }

                if (body is not null)
                {
                    throw Usage("Give the input only once (--file F or -).");
                }

                body = token == Stdin ? readStdin() : ReadFile(readFile, ValueAt(tokens, ref i));
                continue;
            }

            if (token.StartsWith("--", StringComparison.Ordinal))
            {
                var option = spec.Options.FirstOrDefault(o => o.Name == token)
                    ?? throw Usage($"Unknown option '{token}' for 'orandactl {spec.Name}'. Usage: orandactl {spec.Name} {spec.Usage}".TrimEnd());
                SetOption(arguments, option, option.Kind is ValueKind.Flag or ValueKind.FalseFlag ? null : ValueAt(tokens, ref i));
                continue;
            }

            positionals.Add(token);
        }

        SetPositionals(spec, arguments, positionals);
        SetBody(spec, arguments, body);
        return arguments;
    }

    private static void SetOption(JsonObject arguments, OptionSpec option, string? value)
    {
        switch (option.Kind)
        {
            case ValueKind.Flag:
                arguments[option.Field] = true;
                break;
            case ValueKind.FalseFlag:
                arguments[option.Field] = false;
                break;
            case ValueKind.List:
                var list = arguments[option.Field] as JsonArray ?? [];
                foreach (var item in value!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    list.Add(item);
                }

                arguments[option.Field] = list;
                break;
            default:
                arguments[option.Field] = Convert(option.Kind, value!, option.Name);
                break;
        }
    }

    private static void SetPositionals(CommandSpec spec, JsonObject arguments, List<string> values)
    {
        var index = 0;
        foreach (var positional in spec.Positionals)
        {
            if (positional.Many)
            {
                if (index >= values.Count)
                {
                    throw Usage($"Missing <{positional.Field}>. Usage: orandactl {spec.Name} {spec.Usage}");
                }

                arguments[positional.Field] = new JsonArray([.. values.Skip(index).Select(v => (JsonNode?)JsonValue.Create(v))]);
                index = values.Count;
                continue;
            }

            if (index >= values.Count)
            {
                throw Usage($"Missing <{positional.Field}>. Usage: orandactl {spec.Name} {spec.Usage}");
            }

            arguments[positional.Field] = Convert(positional.Kind, values[index++], "<" + positional.Field + ">");
        }

        if (index < values.Count)
        {
            throw Usage($"Unexpected '{values[index]}'. Usage: orandactl {spec.Name} {spec.Usage}".TrimEnd());
        }
    }

    private static void SetBody(CommandSpec spec, JsonObject arguments, string? body)
    {
        switch (spec.Body)
        {
            case BodyRule.None:
                return;
            case BodyRule.Required when body is null:
                throw Usage($"Give the input with --file F or - (stdin). Usage: orandactl {spec.Name} {spec.Usage}");
            case BodyRule.RequiredUnlessTab when body is null && !arguments.ContainsKey(AppFields.Tab):
                throw Usage($"Give the SQL with --file F or - (stdin), or a tab with --tab. Usage: orandactl {spec.Name} {spec.Usage}");
            case BodyRule.RequiredUnlessTab when body is not null && arguments.ContainsKey(AppFields.Tab):
                throw Usage("Give either the SQL (--file F or -) or --tab, not both.");
            case BodyRule.Variants when body is null:
                throw Usage($"Give the variants as JSON with --file F or - (stdin). Usage: orandactl {spec.Name} {spec.Usage}");
            case BodyRule.Variants:
                MergeVariants(arguments, body!);
                return;
        }

        if (body is not null)
        {
            arguments[spec.BodyField!] = body;
        }
    }

    private static void MergeVariants(JsonObject arguments, string body)
    {
        var node = ParseJson(body, "variants");
        switch (node)
        {
            case JsonArray array:
                arguments[AgentFields.Variants] = array;
                break;
            case JsonObject obj:
                foreach (var (key, value) in obj.ToList())
                {
                    obj.Remove(key);
                    arguments.TryAdd(key, value);
                }

                break;
            default:
                throw Usage("The variants input must be a JSON array or object.");
        }
    }

    private static JsonNode Convert(ValueKind kind, string value, string name)
    {
        switch (kind)
        {
            case ValueKind.Integer:
                return long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number)
                    ? JsonValue.Create(number)
                    : throw Usage($"{name} needs a whole number, not '{value}'.");
            case ValueKind.JsonObject:
                return ParseObject(value, name);
            default:
                return JsonValue.Create(value);
        }
    }

    private static JsonObject ParseObject(string text, string name) =>
        ParseJson(text, name) as JsonObject ?? throw Usage($"{name} must be a JSON object.");

    private static JsonNode ParseJson(string text, string name)
    {
        try
        {
            return JsonNode.Parse(text) ?? throw Usage($"{name} must be JSON.");
        }
        catch (JsonException ex)
        {
            throw Usage($"{name} is not valid JSON: {ex.Message}");
        }
    }

    private static string ReadFile(Func<string, string> readFile, string path)
    {
        try
        {
            return readFile(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw Usage($"Cannot read the file '{path}': {ex.Message}");
        }
    }

    private static string ValueAt(IReadOnlyList<string> tokens, ref int index)
    {
        if (index + 1 >= tokens.Count)
        {
            throw Usage($"{tokens[index]} needs a value.");
        }

        return tokens[++index];
    }

    private static void NoMore(List<string> rest, int count, string name)
    {
        if (rest.Count > count)
        {
            throw Usage($"Unexpected '{rest[count]}' after '{name}'.");
        }
    }

    private static BridgeException Usage(string message) => new(AgentErrorCodes.Usage, message);
}
