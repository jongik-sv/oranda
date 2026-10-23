using Avalonia.Controls;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using Oranda.App.Features.MyBatisWorkspace.Editor;
using Oranda.App.Features.MyBatisWorkspace.Hooks;
using Oranda.App.Features.MyBatisWorkspace.Parameters;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Tests.Features.MyBatisWorkspace.Fakes;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.MyBatis;
using Oranda.Language.MyBatis;

namespace Oranda.App.Tests.Features.MyBatisWorkspace;

/// <summary>
/// TSK-03-02 편집기 계산 꾸밈(<see cref="EvalDecorator"/>): 이번 값으로 빠지는 블록 흐림, if·when 여백 켬·끔 점, 계산 못 한 식의 물결과 덮어쓰기.
/// 계산은 실제 계산기(02-02)로 하고, 세션은 가짜(<see cref="FakeMyBatisWorkspaceSession"/>)다.
/// </summary>
public class ParamsRunDecoratorTests
{
    private const string StatementId = "selectEmpList";

    // 세 번째 if 는 산술이 든 식이라 계산기가 계산하지 못한다(handoff-02-02 「계산 못 함」).
    private const string Statement = """
        <select id="selectEmpList" resultType="map">
          SELECT * FROM EMP E
          <where>
            <if test="empNo != null">AND E.EMPNO = #{empNo}</if>
            <if test="ename != null">AND E.ENAME LIKE #{ename}</if>
            <if test="sal * 2 > 100">AND E.SAL > 0</if>
          </where>
        </select>
        """;

    [Fact]
    public Task Off_blocks_are_dimmed_and_if_heads_get_on_and_off_dots() => HeadlessHost.Run(() =>
    {
        using var f = new Fixture();
        f.Eval.SetValue("empNo", new MyBatisScenarioValue(MyBatisScenarioValueKind.Number, "7369"));
        f.Eval.Flush();

        var blocks = f.Eval.Result!.Blocks.Where(b => b.Tag == "if").ToList();
        Assert.Equal(3, blocks.Count);
        Assert.True(blocks[0].IsOn);
        Assert.False(blocks[1].IsOn);
        Assert.Contains(blocks[1].Range, f.Decoration.DimRanges);
        Assert.DoesNotContain(blocks[0].Range, f.Decoration.DimRanges);

        var heads = f.Decoration.Heads.OrderBy(h => h.Line).ToList();
        Assert.Equal(3, heads.Count);
        Assert.Equal([4, 5, 6], heads.Select(h => h.Line));
        Assert.True(heads[0].IsOn);
        Assert.False(heads[1].IsOn);
        Assert.False(heads[0].Unevaluable);
        Assert.True(heads[2].Unevaluable);
    });

    [Fact]
    public Task Unevaluable_test_is_underlined_and_the_margin_dot_overrides_it() => HeadlessHost.Run(() =>
    {
        using var f = new Fixture();
        var issue = Assert.Single(f.Eval.Result!.Issues, i => i.Kind == MyBatisEvalIssueKind.Unevaluable);
        Assert.Contains(issue.Range, f.Decoration.IssueRanges);

        var head = f.Decoration.Heads.Single(h => h.Unevaluable);
        var before = head.IsOn;
        f.Decoration.ToggleHead(head);

        var block = f.Eval.Result!.Blocks.Single(b => b.Range == head.Range);
        Assert.True(block.IsOverridden);
        Assert.Equal(!before, block.IsOn);
        Assert.Equal(!before, f.Eval.Overrides[head.Range]);
        Assert.Equal(!before, f.Eval.Result.Sql.Contains("E.SAL > 0", StringComparison.Ordinal));
        Assert.Equal(!before, f.Decoration.Heads.Single(h => h.Range == head.Range).IsOn); // 바로 다시 그린다

        f.Eval.SetOverride(head.Range, null); // 「계산대로」
        Assert.False(f.Eval.Result!.Blocks.Single(b => b.Range == head.Range).IsOverridden);
    });

    [Fact]
    public Task Editing_the_statement_text_recomputes_after_the_debounce_and_dispose_detaches() => HeadlessHost.Run(async () =>
    {
        using var f = new Fixture();
        var transformers = f.Editor.TextArea.TextView.LineTransformers.Count;
        var margins = f.Editor.TextArea.LeftMargins.Count;

        // 원문 고침: 세션이 고친 요소를 돌려주고 탭 글이 바뀌면 디바운스 뒤 다시 계산한다.
        var edited = Statement.Replace("ename != null", "ename == null", StringComparison.Ordinal);
        f.Session.Elements[StatementId] = Element(edited);
        f.Tab.Document.Text = edited;
        Assert.False(f.Eval.Result!.Blocks.Where(b => b.Tag == "if").ElementAt(1).IsOn); // 아직 옛 계산
        await HeadlessHost.WaitUntil(() => f.Eval.Result!.Blocks.Where(b => b.Tag == "if").ElementAt(1).IsOn);

        f.Decoration.Dispose();
        Assert.Equal(transformers - 1, f.Editor.TextArea.TextView.LineTransformers.Count);
        Assert.Equal(margins - 1, f.Editor.TextArea.LeftMargins.Count);
    });

    private static MyBatisElement Element(string text) => new()
    {
        Kind = MyBatisElementKind.Select,
        Name = "select",
        Id = StatementId,
        Text = text,
    };

    /// <summary>가짜 세션 + 문장 탭 + 창에 올린 편집기 + 실제 계산기로 붙인 꾸밈.</summary>
    private sealed class Fixture : IDisposable
    {
        private readonly Window _window;

        public Fixture()
        {
            var host = new SqlEditorHost();
            var ws = host.EnsureWorkspace();
            Session = new FakeMyBatisWorkspaceSession();
            ws.MyBatis = Session;
            Tab = new SqlEditorViewModel(Guid.NewGuid(), Statement) { Title = StatementId };
            host.AddMyBatisTab(ws, Tab);
            Session.StatementIds[Tab] = StatementId;
            Session.Elements[StatementId] = Element(Statement);
            Session.SelectedStatementId = StatementId;

            var hub = new MyBatisEvalHub(MyBatisEvalHub.Services(new MyBatisEvaluator(), null)) { Host = host, Post = a => a(), Debounce = TimeSpan.FromMilliseconds(20) };
            Editor = new TextEditor { Document = new TextDocument(Statement) };
            _window = new Window { Width = 600, Height = 300, Content = Editor };
            _window.Show();
            Dispatcher.UIThread.RunJobs();

            Decoration = (EvalDecoration)new EvalDecorator(hub).Attach(new MyBatisEditorContext(Session, Tab, Editor));
            Eval = hub.For(Session, StatementId);
            Dispatcher.UIThread.RunJobs();
        }

        public FakeMyBatisWorkspaceSession Session { get; }

        public SqlEditorViewModel Tab { get; }

        public TextEditor Editor { get; }

        public EvalDecoration Decoration { get; }

        public MyBatisStatementEval Eval { get; }

        public void Dispose()
        {
            Decoration.Dispose();
            Eval.Dispose();
            _window.Close();
        }
    }
}
