using System.Text.Json.Nodes;
using Oranda.App.Features.MyBatisWorkspace.Hooks;
using Oranda.App.Features.MyBatisWorkspace.Workspace;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Tests.Features.SqlEditor;
using Oranda.Core.Workspaces;
using Oranda.Language.MyBatis;

namespace Oranda.App.Tests.Features.MyBatisWorkspace;

/// <summary>
/// 문장 탭 들여쓰기(지시 mybatis-indent-1): 탭은 요소의 파일 들여쓰기(여는 태그 줄 앞 공백)를 둘째 줄부터 걷어 낸 글을 보이고(Dedent),
/// 저장 편집·복구 곁 파일에는 다시 붙인 글을 쓴다(Reindent). 왕복이 원문 바이트 그대로 되지 않는 요소는 원문 그대로 보인다.
/// 저장·밖에서 바뀜 병합은 <see cref="SaveMergeTests"/> 의 「들여쓰기」 묶음이 실제 저장 경로로 본다.
/// </summary>
public sealed class MyBatisIndentTests : IDisposable
{
    /// <summary>요소가 4칸 들여 쓰인 매퍼(사용자 신고 모양). shallow 는 덜 들여 쓴 줄이 있어 원문 그대로 보인다.</summary>
    internal const string Indented = """
        <?xml version="1.0" encoding="UTF-8"?>
        <mapper namespace="com.acme.Mo">
            <select id="selectMoSpc" resultType="map">
                select *
                  from MO_SPC
                 where SPC_ID = #{spcId}
            </select>

            <delete id="deleteMoSpc">
                delete from MO_SPC
                 where SPC_ID = #{spcId}
            </delete>

            <update id="shallow">
                update MO_SPC
          set A = 1
            </update>
        </mapper>

        """;

    /// <summary><see cref="Indented"/> 의 문장 id.</summary>
    internal static readonly string[] StatementIds = ["selectMoSpc", "deleteMoSpc", "shallow"];

    private readonly string _root = Path.Combine(Path.GetTempPath(), "oranda-mbindent-" + Guid.NewGuid().ToString("N"));

    public MyBatisIndentTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    // ---- 순수 함수 ------------------------------------------------------------------------------

    [Theory]
    [InlineData("\n", "    ")]
    [InlineData("\r\n", "    ")]
    [InlineData("\r\n", "\t")]
    public void Dedent_and_reindent_round_trip_an_indented_element_keeping_line_ends_and_tabs(string nl, string p)
    {
        var file = $"<delete id=\"d\">{nl}{p}{p}delete from T{nl}{nl}{p}{p} where A = 1{nl}{p}</delete>";

        Assert.True(MyBatisStatementText.TryDedent(file, p, out var tab));

        Assert.Equal($"<delete id=\"d\">{nl}{p}delete from T{nl}{nl}{p} where A = 1{nl}</delete>", tab);
        Assert.Equal(file, MyBatisStatementText.Reindent(tab, p));
    }

    [Fact]
    public void A_lone_carriage_return_is_a_line_end_like_in_apply()
    {
        var file = "<d id=\"d\">\r    a\r\r    b\r  </d>";

        Assert.True(MyBatisStatementText.TryDedent(file, "  ", out var tab));
        Assert.Equal("<d id=\"d\">\r  a\r\r  b\r</d>", tab);
        Assert.Equal("  ", MyBatisStatementText.IndentBefore("<m>\r  <d/>", 6));
    }

    [Fact]
    public void Reindent_skips_empty_lines_and_prefixes_lines_typed_in_the_tab_whatever_their_line_end()
    {
        // 파일은 CRLF 인데 편집기에서 친 줄은 LF 일 수 있다: 줄 끝은 손대지 않고 내용이 있는 줄에만 접두를 붙인다.
        var tab = "<select id=\"s\">\r\n  select 1\r\n\r\n  from DUAL\n\n</select>";

        Assert.Equal("<select id=\"s\">\r\n    select 1\r\n\r\n    from DUAL\n\n  </select>", MyBatisStatementText.Reindent(tab, "  "));
        Assert.Equal(tab, MyBatisStatementText.Dedent(MyBatisStatementText.Reindent(tab, "  "), "  "));
        Assert.Equal("<a/>", MyBatisStatementText.Reindent("<a/>", "  "));
    }

    [Theory]
    [InlineData("<u id=\"u\">\n    a\n  b\n    </u>", "    ")] // 덜 들여 쓴 줄
    [InlineData("<u id=\"u\">\n    a\n  \n  </u>", "  ")] // 접두만 있는 줄(Dedent 하면 빈 줄 → 접두가 다시 붙지 않는다)
    [InlineData("<u id=\"u\">\n    a\n </u>", "  ")] // 닫는 태그가 덜 들여 쓰임
    [InlineData("<u id=\"u\">\n\ta\n</u>", "  ")] // 탭 문자로 들여 썼는데 접두는 공백
    public void TryDedent_keeps_the_original_when_the_round_trip_would_change_bytes(string file, string indent)
    {
        Assert.False(MyBatisStatementText.TryDedent(file, indent, out var tab));
        Assert.Same(file, tab);
    }

    [Fact]
    public void IndentBefore_takes_only_blank_space_on_the_tag_line()
    {
        const string blank = "<m>\n  \t<select/>";
        const string comment = "<m>\n  <!-- c --> <select/>";
        Assert.Equal("  \t", MyBatisStatementText.IndentBefore(blank, blank.IndexOf("<select", StringComparison.Ordinal)));
        Assert.Equal(string.Empty, MyBatisStatementText.IndentBefore(comment, comment.IndexOf("<select", StringComparison.Ordinal)));
        Assert.Equal(string.Empty, MyBatisStatementText.IndentBefore("<select/>", 0));
    }

    // ---- 세션 ---------------------------------------------------------------------------------

    [Fact]
    public Task Opening_an_indented_mapper_shows_open_and_close_tags_in_the_same_column() => Ui.Run(async () =>
    {
        var (_, _, session) = await OpenAsync();

        var delete = session.OpenStatement("deleteMoSpc")!;
        var select = session.OpenStatement("selectMoSpc")!;

        Assert.Equal("<delete id=\"deleteMoSpc\">\n    delete from MO_SPC\n     where SPC_ID = #{spcId}\n</delete>", delete.Text);
        Assert.Equal("<select id=\"selectMoSpc\" resultType=\"map\">\n    select *\n      from MO_SPC\n     where SPC_ID = #{spcId}\n</select>", select.Text);
        Assert.False(delete.IsDirty);
        Assert.False(session.IsDirty);

        // 계산·배지는 탭 글 오프셋을 쓴다: ElementOf 는 탭 글과 같은 원문을, 매번 같은 인스턴스로 준다.
        var element = session.ElementOf("deleteMoSpc")!;
        Assert.Equal(delete.Text, element.Text);
        Assert.Same(element, session.ElementOf("deleteMoSpc"));
        Assert.Equal(session.Document.Find("deleteMoSpc")!.ElementRange, element.ElementRange);
    });

    [Fact]
    public Task Opening_every_tab_and_typing_back_the_same_text_leaves_nothing_to_write() => Ui.Run(async () =>
    {
        var (_, ws, session) = await OpenAsync();
        foreach (var id in StatementIds)
        {
            session.OpenStatement(id);
        }

        var delete = session.OpenStatement("deleteMoSpc")!;
        delete.Document.Replace(0, 0, "x");
        Assert.True(delete.IsDirty);
        delete.Document.Replace(0, 1, string.Empty);

        Assert.False(delete.IsDirty);
        Assert.False(session.IsDirty);
        Assert.Empty(session.PendingEdits());
        Assert.Equal(3, ws.Tabs.Count(t => session.EntryOf(t) is not null));
    });

    [Fact]
    public Task An_element_with_a_less_indented_line_is_shown_as_in_the_file() => Ui.Run(async () =>
    {
        var (_, _, session) = await OpenAsync();
        var baseline = session.Document.Find("shallow")!;

        var tab = session.OpenStatement("shallow")!;

        Assert.Equal(baseline.Text, tab.Text);
        Assert.Equal(string.Empty, session.Find("shallow")!.Indent);
        tab.Document.Replace(tab.Text.IndexOf("A = 1", StringComparison.Ordinal), 5, "A = 2");
        var edit = Assert.IsType<MapperEdit.ReplaceElement>(Assert.Single(session.PendingEdits()));
        Assert.Equal(baseline.Text.Replace("A = 1", "A = 2", StringComparison.Ordinal), edit.NewText);
    });

    [Fact]
    public Task Renaming_an_indented_statement_is_written_as_a_rename_not_a_replace() => Ui.Run(async () =>
    {
        var (_, _, session) = await OpenAsync();
        session.OpenStatement("deleteMoSpc");

        session.RenameStatement("deleteMoSpc", "deleteMoSpcById");

        Assert.Equal(new MapperEdit.RenameElement("deleteMoSpc", "deleteMoSpcById"), Assert.Single(session.PendingEdits()));
    });

    [Fact]
    public Task A_new_statement_takes_the_indent_of_its_insert_place_and_reads_back_the_same_after_save() => Ui.Run(async () =>
    {
        var (_, _, session) = await OpenAsync();
        var tab = session.CreateStatement(MyBatisStatementKind.Delete, "deleteAll");
        Assert.Equal("<delete id=\"deleteAll\">\n  \n</delete>", tab.Text);
        tab.Document.Replace(tab.Text.IndexOf("  \n", StringComparison.Ordinal), 3, "  delete from MO_SPC\n");

        var written = session.Document.Apply(session.PendingEdits());

        // 첫 줄은 Apply 가 AfterId(deleteMoSpc) 들여쓰기로, 둘째 줄부터는 세션이 같은 접두로 붙인다.
        Assert.Contains("    </delete>\n    <delete id=\"deleteAll\">\n      delete from MO_SPC\n    </delete>\n", written, StringComparison.Ordinal);
        session.Rebase(written, ["deleteAll"]);
        Assert.Equal("<delete id=\"deleteAll\">\n  delete from MO_SPC\n</delete>", tab.Text);
        Assert.False(tab.IsDirty);
        Assert.False(session.IsDirty);
    });

    // ---- 복구 곁 파일 -----------------------------------------------------------------------------

    [Fact]
    public Task Recovery_restores_dedented_edits_and_writes_the_same_file_text() => Ui.Run(async () =>
    {
        var participant = new RecoveryStatementsParticipant(Path.Combine(_root, "ws"), Path.Combine(_root, "auto"));
        var hooks = new MyBatisHooks(openHandler: new MyBatisOpenHandler(), recoveryParticipants: [participant]);
        var (_, ws, session) = await OpenAsync(new SqlEditorHost { MyBatisHooks = hooks });
        var tab = session.OpenStatement("deleteMoSpc")!;
        tab.Document.Replace(tab.Text.IndexOf("</delete>", StringComparison.Ordinal), 0, "   and DEL_YN = 'N'\n");
        var closed = session.OpenStatement("selectMoSpc")!;
        closed.Document.Replace(closed.Text.IndexOf("select *", StringComparison.Ordinal), 8, "select 1");
        ws.Host.Close(closed); // 닫힌 탭의 고침은 곁 파일에만 남는다
        var expected = session.PendingEdits();
        var saved = ws.ToWorkspace();
        await participant.SaveAsync(ws, MyBatisRecoveryTarget.Autosave, default);

        var restored = new SqlEditorHost { MyBatisHooks = hooks }.Restore(saved, MyBatisRecoveryTarget.Autosave);

        var back = Assert.IsType<MyBatisWorkspaceSession>(restored.MyBatis);
        Assert.Equal(expected, back.PendingEdits());
        var again = restored.Tabs.Single(t => t.Title == "deleteMoSpc");
        Assert.Equal(tab.Text, again.Text);
        Assert.True(again.IsDirty);
        Assert.Equal(session.Document.Apply(expected), back.Document.Apply(back.PendingEdits()));
    });

    [Fact]
    public Task Recovery_of_an_old_side_file_reads_its_file_form_tab_text_without_doubling_the_indent() => Ui.Run(async () =>
    {
        var participant = new RecoveryStatementsParticipant(Path.Combine(_root, "ws"), Path.Combine(_root, "auto"));
        var hooks = new MyBatisHooks(openHandler: new MyBatisOpenHandler(), recoveryParticipants: [participant]);
        var (_, ws, session) = await OpenAsync(new SqlEditorHost { MyBatisHooks = hooks });
        var deleteTab = session.OpenStatement("deleteMoSpc")!;
        var selectTab = session.OpenStatement("selectMoSpc")!;
        selectTab.Document.Replace(selectTab.Text.IndexOf("select *", StringComparison.Ordinal), 8, "select 1");
        await participant.SaveAsync(ws, MyBatisRecoveryTarget.Autosave, default);

        // 옛 판(1) 기록: 고친 원문·탭 글이 파일 공간(들여쓰기가 남은 원문)이고 접두가 없다.
        var select = session.Document.Find("selectMoSpc")!;
        var delete = session.Document.Find("deleteMoSpc")!;
        var oldSelect = select.Text.Replace("select *", "select 1", StringComparison.Ordinal);
        var side = participant.PathOf(ws.Id, MyBatisRecoveryTarget.Autosave);
        var json = JsonNode.Parse(File.ReadAllText(side))!.AsObject();
        json["Version"] = 1;
        foreach (var e in json["Entries"]!.AsArray())
        {
            var o = e!.AsObject();
            o.Remove("Indent");
            if (o["Text"] is not null)
            {
                o["Text"] = oldSelect;
            }
        }

        File.WriteAllText(side, json.ToJsonString());
        var saved = ws.ToWorkspace();
        saved = saved with
        {
            Tabs = [.. saved.Tabs.Select(t => t.Title switch
            {
                "selectMoSpc" => t with { Content = oldSelect },
                "deleteMoSpc" => t with { Content = delete.Text },
                _ => t,
            })],
        };

        var restored = new SqlEditorHost { MyBatisHooks = hooks }.Restore(saved, MyBatisRecoveryTarget.Autosave);

        var back = Assert.IsType<MyBatisWorkspaceSession>(restored.MyBatis);
        var deleteBack = restored.Tabs.Single(t => t.Title == "deleteMoSpc");
        Assert.Equal(deleteTab.Text, deleteBack.Text); // 고치지 않은 탭은 탭 공간 글로 다시 싣는다
        Assert.False(deleteBack.IsDirty);
        var selectBack = restored.Tabs.Single(t => t.Title == "selectMoSpc");
        Assert.Equal(oldSelect, selectBack.Text); // 고친 옛 탭은 원문 그대로 보인다
        Assert.True(selectBack.IsDirty);
        Assert.Equal(new MapperEdit.ReplaceElement("selectMoSpc", oldSelect), Assert.Single(back.PendingEdits()));
    });

    [Fact]
    public Task Recovery_keeps_the_indent_a_merge_handed_down_even_after_the_edit_is_undone() => Ui.Run(async () =>
    {
        var participant = new RecoveryStatementsParticipant(Path.Combine(_root, "ws"), Path.Combine(_root, "auto"));
        var hooks = new MyBatisHooks(openHandler: new MyBatisOpenHandler(), recoveryParticipants: [participant]);
        var (_, ws, session) = await OpenAsync(new SqlEditorHost { MyBatisHooks = hooks });
        var tab = session.OpenStatement("selectMoSpc")!;
        var before = tab.Text;
        tab.Document.Replace(0, 0, "x");

        // 밖에서 select 줄 앞에만 주석을 더했다: 요소 원문은 같고, 다시 구한 접두는 빈 글이 된다. 내 고침을 남기면 접두를 물려받는다.
        var disk = Indented.Replace("    <select id=", "    <!-- c --> <select id=", StringComparison.Ordinal);
        File.WriteAllText(session.MapperPath, disk);
        session.ApplyDiskText(disk, ["selectMoSpc"]);
        tab.Document.Replace(0, 1, string.Empty);
        Assert.Equal(before, tab.Text);
        Assert.False(session.IsDirty);
        var saved = ws.ToWorkspace();
        await participant.SaveAsync(ws, MyBatisRecoveryTarget.Autosave, default);

        var restored = new SqlEditorHost { MyBatisHooks = hooks }.Restore(saved, MyBatisRecoveryTarget.Autosave);

        var back = Assert.IsType<MyBatisWorkspaceSession>(restored.MyBatis);
        Assert.False(restored.Tabs.Single(t => t.Title == "selectMoSpc").IsDirty);
        Assert.Empty(back.PendingEdits());
    });

    private async Task<(SqlEditorHost Host, SqlWorkspaceViewModel Ws, MyBatisWorkspaceSession Session)> OpenAsync(SqlEditorHost? host = null)
    {
        host ??= new SqlEditorHost { MyBatisHooks = new MyBatisHooks(openHandler: new MyBatisOpenHandler()) };
        var path = Path.Combine(_root, "MoMapper.xml");
        File.WriteAllText(path, Indented);
        await host.OpenWorkspaceFileAsync(path, default);
        var ws = host.ActiveWorkspace!;
        return (host, ws, (MyBatisWorkspaceSession)ws.MyBatis!);
    }
}
