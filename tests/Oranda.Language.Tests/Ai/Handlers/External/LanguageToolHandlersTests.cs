using System.Text.Json.Nodes;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Formatting;
using Oranda.Core.Storage;
using Oranda.Language.Ai.Handlers.External;
using Oranda.Language.Formatting;
using Oranda.Language.Completion;
using Oranda.Language.Lint;
using Oranda.Testing;

namespace Oranda.Language.Tests.Ai.Handlers.External;

public class LanguageToolHandlersTests
{
    private static CheckSqlHandler Check(params IAppToolHandler[] tabs) =>
        new(new LanguageService(), new LintService((MetadataLookup?)null), new TabTextReader(ExternalFakes.Services(tabs)));

    private static FormatSqlHandler Format(IFormatterProfileStore? profiles = null, InMemorySettingsStore? settings = null, params IAppToolHandler[] tabs) =>
        new(new SqlFormatter(), profiles ?? new FakeFormatterProfiles(), settings ?? new InMemorySettingsStore(), new TabTextReader(ExternalFakes.Services(tabs)));

    [Fact]
    public async Task Check_sql_lists_statements_with_line_kind_and_binds()
    {
        var sql = "SELECT * FROM emp WHERE id = :id;\n\nUPDATE emp SET sal = :sal WHERE id = :id;";

        var output = await Check().ExecuteAsync(ExternalFakes.App(new JsonObject { ["sql"] = sql, ["lint"] = false }), CancellationToken.None);

        Assert.False(output.IsError);
        var statements = output.Result!["statements"]!.AsArray();
        Assert.Equal(2, statements.Count);
        Assert.Equal("query", statements[0]!["kind"]!.GetValue<string>());
        Assert.Equal("dml", statements[1]!["kind"]!.GetValue<string>());
        Assert.Equal(1, statements[0]!["line"]!.GetValue<int>());
        Assert.Equal(3, statements[1]!["line"]!.GetValue<int>());
        Assert.Equal(["id", "sal"], output.Result["binds"]!.AsArray().Select(b => b!.GetValue<string>().ToLowerInvariant()));
        Assert.Empty(output.Result["diagnostics"]!.AsArray());
    }

    [Fact]
    public async Task Check_sql_reports_syntax_error_with_line_and_column()
    {
        var sql = "SELECT 1 FROM dual;\nSELECT FROM WHERE;";

        var output = await Check().ExecuteAsync(ExternalFakes.App(new JsonObject { ["sql"] = sql, ["lint"] = false }), CancellationToken.None);

        var error = Assert.Single(output.Result!["diagnostics"]!.AsArray(), d => d!["severity"]!.GetValue<string>() == "error");
        Assert.Equal(2, error!["line"]!.GetValue<int>());
        Assert.True(error["col"]!.GetValue<int>() >= 1);
        Assert.True(error["start"]!.GetValue<int>() >= sql.IndexOf('\n', StringComparison.Ordinal));
    }

    [Fact]
    public async Task Check_sql_lint_adds_warnings_only_when_asked()
    {
        var sql = "SELECT a FROM emp WHERE b = NULL";

        var on = await Check().ExecuteAsync(ExternalFakes.App(new JsonObject { ["sql"] = sql }), CancellationToken.None);
        var off = await Check().ExecuteAsync(ExternalFakes.App(new JsonObject { ["sql"] = sql, ["lint"] = false }), CancellationToken.None);

        Assert.Contains(on.Result!["diagnostics"]!.AsArray(), d => d!["code"]?.GetValue<string>().StartsWith("LINT.", StringComparison.Ordinal) == true);
        Assert.Empty(off.Result!["diagnostics"]!.AsArray());
    }

    [Fact]
    public async Task Check_sql_reads_the_tab_through_get_tab_and_passes_its_denial_through()
    {
        var tab = FakeGetTab.Returning("SELECT 1 FROM dual");
        var locked = new FakeGetTab(_ => AppToolOutputs.Locked());

        var ok = await Check(tab).ExecuteAsync(ExternalFakes.App(new JsonObject { ["tab"] = "t1" }), CancellationToken.None);
        var denied = await Check(locked).ExecuteAsync(ExternalFakes.App(new JsonObject { ["tab"] = "t9" }), CancellationToken.None);

        Assert.Single(ok.Result!["statements"]!.AsArray());
        Assert.Equal("t1", tab.Calls[0].Arguments["tab"]!.GetValue<string>());
        Assert.Equal("document", tab.Calls[0].Arguments["scope"]!.GetValue<string>());
        Assert.Equal("locked", denied.ErrorCode);
    }

    [Fact]
    public async Task Check_sql_without_sql_or_tab_and_without_tab_reader_is_refused()
    {
        var none = await Check().ExecuteAsync(ExternalFakes.App([]), CancellationToken.None);
        var noReader = await Check().ExecuteAsync(ExternalFakes.App(new JsonObject { ["tab"] = "t1" }), CancellationToken.None);
        var truncated = await Check(FakeGetTab.Returning("SELECT", truncated: true)).ExecuteAsync(ExternalFakes.App(new JsonObject { ["tab"] = "t1" }), CancellationToken.None);
        var badLint = await Check().ExecuteAsync(ExternalFakes.App(new JsonObject { ["sql"] = "SELECT 1 FROM dual", ["lint"] = "yes" }), CancellationToken.None);

        Assert.Equal("invalid_arguments", none.ErrorCode);
        Assert.Equal("not_implemented", noReader.ErrorCode);
        Assert.Equal("result_too_large", truncated.ErrorCode);
        Assert.Equal("invalid_arguments", badLint.ErrorCode);
    }

    [Fact]
    public async Task Format_sql_returns_formatted_text_without_touching_the_tab()
    {
        var output = await Format().ExecuteAsync(ExternalFakes.App(new JsonObject { ["sql"] = "select a,b from t where x=1" }), CancellationToken.None);
        var again = await Format().ExecuteAsync(ExternalFakes.App(new JsonObject { ["sql"] = output.Result!["text"]!.GetValue<string>() }), CancellationToken.None);

        Assert.True(output.Result["changed"]!.GetValue<bool>());
        Assert.Contains("SELECT", output.Result["text"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.False(again.Result!["changed"]!.GetValue<bool>());
        Assert.Null(output.Result["failure"]);
    }

    [Fact]
    public async Task Format_sql_uses_named_profile_and_refuses_unknown_profile()
    {
        var lower = new FormatterProfile { Id = "my", Name = "Mine", Options = FormatterOptions.OrandaDefault with { Case = new CaseOptions { Keyword = LetterCase.Lower } } };
        var profiles = new FakeFormatterProfiles(new FormatterProfile { Id = "oranda-default", Name = "Oranda", Options = FormatterOptions.OrandaDefault }, lower);

        var named = await Format(profiles).ExecuteAsync(ExternalFakes.App(new JsonObject { ["sql"] = "SELECT a FROM t", ["profile"] = "mine" }), CancellationToken.None);
        var unknown = await Format(profiles).ExecuteAsync(ExternalFakes.App(new JsonObject { ["sql"] = "SELECT a FROM t", ["profile"] = "nope" }), CancellationToken.None);

        Assert.Contains("select", named.Result!["text"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal("invalid_arguments", unknown.ErrorCode);
    }

    [Fact]
    public async Task Format_sql_asks_get_tab_for_the_scope_and_refuses_unknown_scope()
    {
        var tab = FakeGetTab.Returning("select 1 from dual");

        var ok = await Format(tabs: tab).ExecuteAsync(ExternalFakes.App(new JsonObject { ["tab"] = "t1", ["scope"] = "statement" }), CancellationToken.None);
        var bad = await Format(tabs: tab).ExecuteAsync(ExternalFakes.App(new JsonObject { ["tab"] = "t1", ["scope"] = "caret" }), CancellationToken.None);

        Assert.False(ok.IsError);
        Assert.Equal("statement", tab.Calls[0].Arguments["scope"]!.GetValue<string>());
        Assert.Equal("invalid_arguments", bad.ErrorCode);
    }

    [Fact]
    public async Task Format_sql_reports_failure_and_returns_input_when_it_cannot_format()
    {
        var sql = "SELECT 'unclosed FROM t";

        var output = await Format().ExecuteAsync(ExternalFakes.App(new JsonObject { ["sql"] = sql }), CancellationToken.None);

        Assert.False(output.IsError);
        Assert.False(output.Result!["changed"]!.GetValue<bool>());
        Assert.Equal(sql, output.Result["text"]!.GetValue<string>());
    }
}
