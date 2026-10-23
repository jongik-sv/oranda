using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Oranda.App.Features.Diagnostics;
using Oranda.App.Features.MyBatisWorkspace.Diagnostics;
using Oranda.App.Features.MyBatisWorkspace.Expanded;
using Oranda.App.Features.MyBatisWorkspace.Hooks;
using Oranda.App.Features.MyBatisWorkspace.Parameters;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Tests.Features.MyBatisWorkspace.Fakes;
using Oranda.App.Tests.Features.SqlEditor;
using Oranda.Core.Language;
using Oranda.Core.Text;
using Oranda.Language;
using Oranda.Language.MyBatis;

namespace Oranda.App.Tests.Features.MyBatisWorkspace;

/// <summary>
/// MyBatis 쿼리 탭 문법 오류 밑줄: 원문(XML)에는 일반 L2 분석을 하지 않아 <c>&amp;lt;</c> 같은 엔터티로 생기던 거짓 오류가 없고,
/// 펼친 SQL 의 실제 오류는 대응 원문 줄에 긋는다. 원문을 고치면 옛 밑줄은 바로 지운다. 일반 SQL 탭 분석은 그대로다.
/// </summary>
public sealed class MyBatisDiagnosticsTests
{
    private static readonly ILanguageService Language = new LanguageService();

    // 원문 그대로 L2 로 읽으면 「&lt;」 가 치환 변수 + 문장 끝으로 읽혀 「구문 오류: 'QQQQ'」 가 나던 모양(getCoilPlnPasProc).
    private const string EntityStatement = """
        <select id="s" resultType="map">
        	select *
        	  from TB_COIL INST
        	 where length(PLN_PROC) &lt; (select count(*)*2
        	                               from TB_ROUT ROUT
        	                              where INST.MO_NO = ROUT.MO_NO)
        	   and INST.PROC_CD = #{procCd}
        </select>
        """;

    // 펼친 SQL 에 실제 문법 오류가 있는 모양(FROM 앞 쉼표).
    private const string BrokenStatement = """
        <select id="s" resultType="map">
          select a,
            from tb
          <if test="x != null">
           where x = #{x}
          </if>
        </select>
        """;

    [Fact]
    public Task Entity_in_the_xml_source_no_longer_raises_a_false_syntax_error() => Ui.Run(async () =>
    {
        using var f = new Fixture(EntityStatement);

        // 꾸밈 전: 원문을 SQL 로 읽은 거짓 오류가 있다(고치기 전 동작).
        await f.Diagnostics.AnalyzeNowAsync();
        Assert.Contains(f.Diagnostics.Current, d => d.Severity == DiagnosticSeverity.Error);

        f.Attach();
        await WaitUntil(() => f.Applied >= 1);

        Assert.True(f.Diagnostics.SqlAnalysisOff);
        Assert.DoesNotContain(f.Diagnostics.Current, d => d.Severity == DiagnosticSeverity.Error);
        f.Render("mybatis-diag-entity.png");
    });

    [Fact]
    public Task Real_error_in_the_expanded_sql_is_underlined_on_its_source_line() => Ui.Run(async () =>
    {
        using var f = new Fixture(BrokenStatement);
        f.Attach();
        await WaitUntil(() => f.Applied >= 1);

        var error = Assert.Single(f.Diagnostics.Current, d => d.Severity == DiagnosticSeverity.Error);
        string line = f.Tab.Text.Substring(error.Range.Start, error.Range.Length);
        Assert.StartsWith("펼친 SQL: ", error.Message, StringComparison.Ordinal);
        Assert.True(line is "select a," or "from tb", line);
        f.Render("mybatis-diag-error.png");

        // 원문을 고치면 다음 계산 전에 옛 밑줄을 지운다.
        f.Tab.Document.Insert(0, "\n");
        Assert.Empty(f.Diagnostics.Current);
    });

    [Fact]
    public Task Detaching_clears_the_expanded_sql_underlines_and_keeps_xml_analysis_off() => Ui.Run(async () =>
    {
        using var f = new Fixture(BrokenStatement);
        f.Attach();
        await WaitUntil(() => f.Applied >= 1);
        Assert.NotEmpty(f.Diagnostics.Current);

        f.Detach();

        Assert.Empty(f.Diagnostics.Current);
        Assert.True(f.Diagnostics.SqlAnalysisOff);
        Assert.False(f.Diagnostics.IsPending);
    });

    [Fact]
    public void Error_mapped_onto_a_tag_line_moves_to_the_nearest_sql_line()
    {
        // include 한 줄이 펼친 SQL 두 줄(x, y)이 되고 y 에 오류가 있다: 바로 대응하는 원문 줄은 태그뿐인 include 줄이다.
        const string source = "<select id=\"s\">\n  select\n  <include refid=\"c\"/>\n  from tb\n</select>";
        const string expanded = "select\n  x,\n  y\n  from tb";
        int yLine = 2;
        var map = MyBatisLineMap.Build(source, expanded);
        Assert.Equal(2, (int)Math.Floor(map.ToSource(yLine + 0.5)));

        var found = new[] { new Diagnostic(new TextRange(expanded.IndexOf('y', StringComparison.Ordinal), 1), DiagnosticSeverity.Error, "x") };
        var mapped = Assert.Single(MyBatisSqlDiagnostics.ToSource(source, Result(expanded), found, map));

        string line = source.Substring(mapped.Range.Start, mapped.Range.Length);
        Assert.True(line is "select" or "from tb", line);
        Assert.Equal(DiagnosticSeverity.Error, mapped.Severity);
    }

    [Fact]
    public void One_line_dynamic_tag_with_sql_counts_as_an_sql_line_and_crlf_is_not_underlined()
    {
        const string source = "<select id=\"s\">\r\n  select a from tb where 1 = 1\r\n  <if test=\"x\">and (x = 1</if>\r\n</select>";
        const string expanded = "select a from tb where 1 = 1\r\n  and (x = 1";
        var found = new[] { new Diagnostic(new TextRange(expanded.Length, 0), DiagnosticSeverity.Error, "x") };

        var mapped = Assert.Single(MyBatisSqlDiagnostics.ToSource(source, Result(expanded), found));

        Assert.Equal("<if test=\"x\">and (x = 1</if>", source.Substring(mapped.Range.Start, mapped.Range.Length));
    }

    [Fact]
    public void Errors_are_only_a_note_when_a_value_is_missing()
    {
        // 값 없는 ${sortCol} 은 빈 글이 되어 「ORDER BY」 로 끝난다: 원문 잘못이 아니므로 밑줄 없는 안내로 낮춘다.
        const string source = "<select id=\"s\">\n  select a from tb\n  order by ${sortCol}\n</select>";
        const string expanded = "select a from tb\n  order by ";
        var result = Result(expanded) with { Substitutions = [new MyBatisSubstitution("sortCol", null, new TextRange(source.IndexOf('$', StringComparison.Ordinal), 9))] };
        var found = new[] { new Diagnostic(new TextRange(expanded.Length, 0), DiagnosticSeverity.Error, "x") };

        var mapped = Assert.Single(MyBatisSqlDiagnostics.ToSource(source, result, found));

        Assert.Equal(DiagnosticSeverity.Info, mapped.Severity);
        Assert.Contains("값이 빈 자리가 있어 확인하지 못했습니다", mapped.Message, StringComparison.Ordinal);
    }

    private static EvalResult Result(string sql) => new(sql, [], [], [], []);

    [Fact]
    public Task Plain_sql_tab_keeps_its_own_syntax_analysis() => Ui.Run(async () =>
    {
        var vm = new SqlEditorViewModel(Guid.NewGuid(), "SELECT * FROM WHERE x = 1");
        var view = new SqlEditorView { DataContext = vm };
        var window = new Window { Width = 600, Height = 300, Content = view };
        window.Show();
        var diagnostics = EditorDiagnostics.Attach(view, () => Language);
        await diagnostics.AnalyzeNowAsync();

        Assert.False(diagnostics.SqlAnalysisOff);
        Assert.Single(diagnostics.Current, d => d.Severity == DiagnosticSeverity.Error);
        window.Close();
    });

    private static async Task WaitUntil(Func<bool> done, int timeoutMs = 20000)
    {
        var sw = Stopwatch.StartNew();
        while (!done() && sw.ElapsedMilliseconds < timeoutMs)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }

        // 마지막 계산 뒤 분석이 끝날 틈을 한 번 더 준다.
        for (int i = 0; i < 20; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }
    }

    /// <summary>가짜 세션 + 문장 탭 + 창에 올린 SQL 편집기(일반 진단 표시기 붙음) + 실제 계산기.</summary>
    private sealed class Fixture : IDisposable
    {
        private const string StatementId = "s";
        private readonly Window _window;
        private readonly FakeMyBatisWorkspaceSession _session;
        private readonly MyBatisEvalHub _hub;
        private readonly SqlEditorView _view;
        private MyBatisSqlDiagnostics.Attachment? _decoration;

        public Fixture(string statement)
        {
            var host = new SqlEditorHost();
            var ws = host.EnsureWorkspace();
            _session = new FakeMyBatisWorkspaceSession();
            ws.MyBatis = _session;
            Tab = new SqlEditorViewModel(Guid.NewGuid(), statement) { Title = StatementId };
            host.AddMyBatisTab(ws, Tab);
            _session.StatementIds[Tab] = StatementId;
            _session.Elements[StatementId] = new MyBatisElement { Kind = MyBatisElementKind.Select, Name = "select", Id = StatementId, Text = statement };
            _session.SelectedStatementId = StatementId;

            _hub = new MyBatisEvalHub(MyBatisEvalHub.Services(new MyBatisEvaluator(), null)) { Host = host, Post = a => a(), Debounce = TimeSpan.FromMilliseconds(20) };
            _view = new SqlEditorView { DataContext = Tab };
            _window = new Window { Width = 700, Height = 400, Content = _view };
            _window.Show();
            Dispatcher.UIThread.RunJobs();
            Diagnostics = EditorDiagnostics.Attach(_view, () => Language);
            Eval = _hub.For(_session, StatementId);
        }

        public SqlEditorViewModel Tab { get; }

        public EditorDiagnostics Diagnostics { get; }

        public MyBatisStatementEval Eval { get; }

        /// <summary>꾸밈이 밑줄을 반영한 횟수.</summary>
        public int Applied => _decoration?.Applied ?? 0;

        /// <summary><c>ORANDA_RENDER_DIR</c> 가 있으면 편집기 화면을 PNG 로 뽑는다(렌더 점검용).</summary>
        public void Render(string file)
        {
            var dir = Environment.GetEnvironmentVariable("ORANDA_RENDER_DIR");
            if (string.IsNullOrEmpty(dir))
            {
                return;
            }

            Directory.CreateDirectory(dir);
            Dispatcher.UIThread.RunJobs();
            _window.UpdateLayout();
            using var bmp = new RenderTargetBitmap(new PixelSize(700, 400));
            bmp.Render(_window);
#pragma warning disable CS0618 // 렌더 확인용: 새 오버로드가 없는 헤드리스 비트맵
            bmp.Save(Path.Combine(dir, file));
#pragma warning restore CS0618
        }

        public void Attach() =>
            _decoration = Assert.IsType<MyBatisSqlDiagnostics.Attachment>(
                new MyBatisSqlDiagnostics(_hub, () => Language).Attach(new MyBatisEditorContext(_session, Tab, _view.TextEditorControl)));

        public void Detach()
        {
            _decoration?.Dispose();
            _decoration = null;
        }

        public void Dispose()
        {
            _decoration?.Dispose();
            Eval.Dispose();
            _window.Close();
        }
    }
}
