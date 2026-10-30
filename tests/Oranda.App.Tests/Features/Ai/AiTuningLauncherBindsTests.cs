using Oranda.App.Features.Ai.Tuning;
using Oranda.App.Features.Binds;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Settings;
using Oranda.Core.Text;

namespace Oranda.App.Tests.Features.Ai;

/// <summary>AI 튜닝 시작 때 담는 바인드 값: 시작 탭 목록에서 대상 문장이 쓰는 이름만, 비밀로 보이는 이름은 뺀다(로컬 실행에만 쓴다).</summary>
public class AiTuningLauncherBindsTests
{
    private static SqlStatement Statement(params string[] binds) =>
        new("SELECT 1 FROM dual", new TextRange(0, 18), StatementKind.Query) { Verb = "SELECT", BindNames = binds };

    [Fact]
    public void Start_binds_keep_only_the_names_the_statement_uses_and_drop_secret_looking_names()
    {
        var list = new BindVariableStore();
        list.Add("WORD_DH").Text = "20261030";
        list.Add("OTHER").Text = "x";
        list.Add("USER_PWD").Text = "hunter2";

        var binds = AiTuningLauncher.StartBinds(Statement("word_dh", "USER_PWD", "PLANT_CD"), list);

        Assert.Equal(["word_dh"], binds.Keys);
        Assert.Equal("20261030", binds["WORD_DH"].Value);
    }

    [Fact]
    public void Follow_ups_keep_the_start_tab_values_after_another_tab_becomes_active_or_the_list_changes()
    {
        var startTab = new BindVariableStore();
        startTab.Add("WORD_DH").Text = "20261030";
        var otherTab = new BindVariableStore();
        otherTab.Add("WORD_DH").Text = "19990101";
        var binds = new TuningStartBinds();

        binds.Capture(Statement("WORD_DH"), startTab);
        startTab.Find("WORD_DH")!.Text = "20270101"; // 시작 뒤 목록을 고치거나 다른 탭(otherTab)이 활성이 돼도

        Assert.Equal("20261030", binds.Current["WORD_DH"].Value); // 이어 말하기는 시작 때 담은 값을 쓴다
        Assert.Equal("19990101", otherTab.Find("WORD_DH")!.Text);
    }

    [Fact]
    public void A_rejected_new_start_restores_the_previous_conversation_values()
    {
        var first = new BindVariableStore();
        first.Add("WORD_DH").Text = "20261030";
        var second = new BindVariableStore();
        second.Add("WORD_DH").Text = "19990101";
        var binds = new TuningStartBinds();
        binds.Capture(Statement("WORD_DH"), first);

        var previous = binds.Capture(Statement("WORD_DH"), second);
        binds.Restore(previous); // 문장 없음·가림 불가로 시작이 거부됐다

        Assert.Equal("20261030", binds.Current["WORD_DH"].Value);
    }

    [Fact]
    public void Start_dialog_says_bind_values_are_used_only_for_local_runs()
    {
        var info = AiTuningLauncher.BuildStartInfo(null, new AiContext("dev", ConnectionEnvironment.Development, AiAccessLevel.Query), new AiSettings());

        Assert.Contains("바인드 값(로컬 실행에만 씀)", info.NotSent, StringComparison.Ordinal);
    }

    [Fact]
    public void Start_binds_are_empty_without_a_statement_or_a_list()
    {
        Assert.Empty(AiTuningLauncher.StartBinds(null, new BindVariableStore()));
        Assert.Empty(AiTuningLauncher.StartBinds(Statement("A"), null));
    }
}
