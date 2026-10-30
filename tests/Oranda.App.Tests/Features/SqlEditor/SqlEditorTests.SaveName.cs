using Oranda.App.Features.SqlEditor;

namespace Oranda.App.Tests.Features.SqlEditor;

/// <summary>저장 대화상자의 제안 이름과 돌려받은 경로가 확장자를 겹쳐 갖지 않는지.</summary>
public sealed partial class SqlEditorTests
{
    [Theory]
    [InlineData("findEmp", "findEmp.sql")]
    [InlineData("findEmp.sql", "findEmp.sql")]
    [InlineData("FINDEMP.SQL", "FINDEMP.SQL")]
    [InlineData("pkg.pkb", "pkg.pkb")]
    [InlineData("a/b:c*d", "a_b_c_d.sql")]
    public Task Suggested_name_gets_sql_extension_only_once(string title, string expected) => Ui.Run(async () =>
    {
        var dialogs = new FakeDialogs();
        var host = new SqlEditorHost(dialogs: dialogs);
        var tab = host.NewDocument("select 1");
        tab.Title = title;

        Assert.False(await host.SaveAsAsync(tab, default));
        Assert.Equal(expected, dialogs.LastSuggested);
    });

    [Fact]
    public Task Saved_tab_title_follows_the_chosen_file_name() => Ui.Run(async () =>
    {
        Directory.CreateDirectory(_root);
        var dialogs = new FakeDialogs { SavePath = Path.Combine(_root, "findEmp.sql") };
        var host = new SqlEditorHost(dialogs: dialogs);
        var tab = host.NewDocument("select 1");
        tab.Title = "findEmp.sql";

        Assert.True(await host.SaveAsAsync(tab, default));
        Assert.Equal("findEmp.sql", tab.Title);
    });

    [Theory]
    [InlineData("/d/findEmp.sql.sql", "findEmp.sql", "sql", "/d/findEmp.sql")]
    [InlineData("/d/FINDEMP.SQL.sql", "FINDEMP.SQL", "sql", "/d/FINDEMP.SQL")]
    [InlineData("/d/a.sql.sql", "a", "sql", "/d/a.sql.sql")] // 사용자가 입력한 이름은 건드리지 않는다
    [InlineData("/d/x.md", "x.md", "md", "/d/x.md")]
    public void Doubled_extension_from_picker_is_collapsed_only_when_suggestion_was_kept(string returned, string suggested, string ext, string expected)
        => Assert.Equal(expected, SaveFileNames.CollapseDoubled(returned, suggested, ext));

    [Fact]
    public void Doubled_extension_is_collapsed_when_macos_returns_a_decomposed_korean_name()
    {
        // macOS 저장 대화 상자는 한글 이름을 자모로 나눈 꼴(NFD)로 돌려준다: 제안 「원본.sql」(NFC)과 글자가 달라 보여도 같은 이름이다.
        var returned = "/d/" + "원본.sql.sql".Normalize(System.Text.NormalizationForm.FormD);
        Assert.NotEqual("/d/원본.sql.sql", returned);

        Assert.Equal("/d/원본.sql", SaveFileNames.CollapseDoubled(returned, "원본.sql", "sql"));
        Assert.Equal("/d/작업.workspace.md", SaveFileNames.CollapseDoubled("/d/" + "작업.workspace.md.md".Normalize(System.Text.NormalizationForm.FormD), "작업.workspace.md", "md"));
        Assert.Equal("/d/새 이름.sql", SaveFileNames.CollapseDoubled("/d/" + "새 이름.sql".Normalize(System.Text.NormalizationForm.FormD), "원본.sql", "sql")); // 이름도 NFC 로 맞춘다
    }

    [Theory]
    [InlineData("x", "x.workspace.md")]
    [InlineData("01. 작업", "01. 작업.workspace.md")]
    [InlineData("x.workspace.md", "x.workspace.md")]
    [InlineData("a/b", "a_b.workspace.md")]
    public void Workspace_file_suggestion_is_name_dot_workspace_dot_md(string name, string expected)
        => Assert.Equal(expected, SaveFileNames.SuggestWorkspace(name));
}
