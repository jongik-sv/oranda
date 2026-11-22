using System.Text.Json.Nodes;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Ai.Protocol;
using Oranda.Core.Ai.Schemas;
using Oranda.Core.Composition;
using Oranda.Core.Formatting;
using Oranda.Core.Language;
using Oranda.Core.Storage;

namespace Oranda.Language.Ai.Handlers.External;

/// <summary>
/// <c>check_sql</c>(설계 agent-cli-control §5.3 「언어」): DB 없이 문장 나누기·문법 오류·점검(lint)·바인드 이름.
/// 편집기와 같은 규칙(<see cref="ILanguageService.Split"/>·<see cref="ILanguageService.AnalyzeAsync"/>·<see cref="ILintService.Lint"/>)을 쓴다.
/// <c>connection</c> 은 방언만 고르는 인자인데 Oracle·Tibero 가 같은 문법 규칙을 쓰므로 결과가 달라지지 않는다.
/// </summary>
[OrandaService(typeof(IAppToolHandler))]
internal sealed class CheckSqlHandler(ILanguageService language, ILintService lint, TabTextReader tabs) : IAppToolHandler
{
    public string ToolName => AppToolNames.CheckSql;

    public ValueTask<AgentToolOutput> ExecuteAsync(AppToolInvocation invocation, CancellationToken ct) =>
        AppToolOutputs.Guard(async () =>
        {
            ArgumentNullException.ThrowIfNull(invocation);
            var (text, error) = await LanguageInput.ReadAsync(invocation, tabs, AppToolValues.Scopes.Document, ct).ConfigureAwait(false);
            if (error is not null)
            {
                return error;
            }

            var withLint = AppToolArgs.Flag(invocation.Arguments, AppFields.Lint, true);
            var statements = new JsonArray();
            var binds = new List<string>();
            var index = 0;
            foreach (var stmt in language.Split(text!, SplitOptions.Default))
            {
                statements.Add(new JsonObject
                {
                    ["index"] = index++,
                    ["kind"] = ExternalJson.KindName(stmt.Kind),
                    ["start"] = stmt.Range.Start,
                    ["length"] = stmt.Range.Length,
                    ["line"] = ExternalJson.LineCol(text!, stmt.Range.Start).Line,
                });
                foreach (var name in stmt.BindNames)
                {
                    if (!binds.Contains(name, StringComparer.OrdinalIgnoreCase))
                    {
                        binds.Add(name);
                    }
                }
            }

            var found = new List<Diagnostic>(await language.AnalyzeAsync(text!, ct).ConfigureAwait(false));
            if (withLint)
            {
                found.AddRange(lint.Lint(text!, null, ct));
            }

            var diagnostics = new JsonArray();
            foreach (var d in found.OrderBy(d => d.Range.Start))
            {
                var (line, col) = ExternalJson.LineCol(text!, d.Range.Start);
                var item = new JsonObject
                {
                    ["severity"] = ExternalJson.SeverityName(d.Severity),
                    ["message"] = d.Message,
                    ["line"] = line,
                    ["col"] = col,
                    ["start"] = d.Range.Start,
                    ["length"] = d.Range.Length,
                };
                if (d.Code is { Length: > 0 } code)
                {
                    item["code"] = code;
                }

                diagnostics.Add(item);
            }

            return AgentToolOutput.Ok(new JsonObject
            {
                ["statements"] = statements,
                ["diagnostics"] = diagnostics,
                ["binds"] = new JsonArray([.. binds.Select(b => (JsonNode?)JsonValue.Create(b))]),
            });
        });
}

/// <summary>
/// <c>format_sql</c>: Oranda 포매터로 서식한 글을 돌려준다. 탭은 바꾸지 않는다(바꾸려면 <c>propose_edit</c>).
/// 프로필은 이름 또는 id, 빼면 설정의 기본 프로필(<c>formatterProfileId</c>, 편집기 「서식」 명령과 같은 규칙).
/// </summary>
[OrandaService(typeof(IAppToolHandler))]
internal sealed class FormatSqlHandler(ISqlFormatter formatter, IFormatterProfileStore profiles, ISettingsStore settings, TabTextReader tabs) : IAppToolHandler
{
    public string ToolName => AppToolNames.FormatSql;

    public ValueTask<AgentToolOutput> ExecuteAsync(AppToolInvocation invocation, CancellationToken ct) =>
        AppToolOutputs.Guard(async () =>
        {
            ArgumentNullException.ThrowIfNull(invocation);
            var scope = AppToolArgs.Text(invocation.Arguments, AppFields.Scope) ?? AppToolValues.Scopes.Document;
            if (scope is not (AppToolValues.Scopes.Document or AppToolValues.Scopes.Selection or AppToolValues.Scopes.Statement))
            {
                throw new AppToolArgumentException(AppFields.Scope, "must be document, selection or statement");
            }

            var (text, error) = await LanguageInput.ReadAsync(invocation, tabs, scope, ct).ConfigureAwait(false);
            if (error is not null)
            {
                return error;
            }

            var options = await OptionsAsync(AppToolArgs.Text(invocation.Arguments, AppFields.Profile), ct).ConfigureAwait(false);
            if (options is null)
            {
                return AppToolOutputs.Deny(AgentErrorCodes.InvalidArguments, ExternalToolText.UnknownProfile);
            }

            var result = formatter.Format(text!, options);
            var json = new JsonObject
            {
                ["text"] = result.Text,
                ["changed"] = result.Succeeded && !string.Equals(result.Text, text, StringComparison.Ordinal),
            };
            if (!result.Succeeded)
            {
                json["failure"] = FailureName(result.Failure);
            }

            return AgentToolOutput.Ok(json);
        });

    private async Task<FormatterOptions?> OptionsAsync(string? wanted, CancellationToken ct)
    {
        var all = await profiles.ListAsync(ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(wanted))
        {
            var key = wanted.Trim();
            return all.FirstOrDefault(p => string.Equals(p.Id, key, StringComparison.OrdinalIgnoreCase))?.Options
                ?? all.FirstOrDefault(p => string.Equals(p.Name, key, StringComparison.OrdinalIgnoreCase))?.Options;
        }

        var id = (await settings.LoadAsync(ct).ConfigureAwait(false)).Value.FormatterProfileId;
        return (all.FirstOrDefault(p => p.Id == id) ?? (all.Count > 0 ? all[0] : null))?.Options ?? FormatterOptions.OrandaDefault;
    }

    /// <summary>실패 사유 낱말(snake_case, 예 <c>token_mismatch</c>).</summary>
    private static string FailureName(FormatFailure failure) => failure switch
    {
        FormatFailure.TokenMismatch => "token_mismatch",
        FormatFailure.CommentCountMismatch => "comment_count_mismatch",
        FormatFailure.NotIdempotent => "not_idempotent",
        FormatFailure.MarkupUnsupported => "markup_unsupported",
        FormatFailure.MarkupMismatch => "markup_mismatch",
        _ => "internal_error",
    };
}

/// <summary>언어 도구의 입력 글: <c>sql</c> 이 있으면 그 글, 없으면 <c>tab</c> 의 글(범위 <paramref name="scope"/>).</summary>
internal static class LanguageInput
{
    public static async ValueTask<(string? Text, AgentToolOutput? Error)> ReadAsync(
        AppToolInvocation invocation, TabTextReader tabs, string scope, CancellationToken ct)
    {
        if (AppToolArgs.Text(invocation.Arguments, AgentFields.Sql) is { } sql)
        {
            return sql.Length > AppToolValues.MaxTextChars
                ? throw new AppToolArgumentException(AgentFields.Sql, "is too long")
                : (sql, null);
        }

        return AppToolArgs.Text(invocation.Arguments, AppFields.Tab) is { Length: > 0 } tab
            ? await tabs.ReadAsync(invocation, tab, scope, ct).ConfigureAwait(false)
            : (null, AppToolOutputs.Deny(AgentErrorCodes.InvalidArguments, ExternalToolText.SqlOrTab));
    }
}
