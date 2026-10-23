using System.Diagnostics;
using System.Globalization;
using System.Text;
using Avalonia.Threading;
using Oranda.App.Features.MyBatisWorkspace.Hooks;
using Oranda.App.Features.MyBatisWorkspace.Workspace;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell.Docking;
using Oranda.App.Tests.Features.SqlEditor;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Testing.MyBatis;

namespace Oranda.App.Tests.Features.MyBatisWorkspace;

/// <summary>
/// TSK-03-01 acceptance 의 화면 흐름: 매퍼를 열면 문장 수만큼 항목이 생기고 고르면 원문 탭이 열리며 고치면 수정 표시가 붙는다,
/// 다른 문서로 가면 「문장」·「파라미터 · 시나리오」 창이 숨고 돌아오면 보인다, 1MB 매퍼 열기 1 s 이하(Perf).
/// 세션·복구 세부는 <see cref="WorkspaceSessionTests"/>, 「문장」 창은 WorkspaceStatementsTests, 편집기·강조는 WorkspaceEditorTests·WorkspaceHighlightTests.
/// </summary>
public sealed class WorkspaceUiTests : IDisposable
{
    private const string SqlEditorDoc = "oranda.panel.sqlEditor";
    private const string Statements = "oranda.panel.mybatisStatements";
    private const string Expanded = "oranda.panel.mybatisExpanded";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "oranda-mbui-" + Guid.NewGuid().ToString("N"));

    public WorkspaceUiTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static void Pump()
    {
        for (var i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
        }
    }

    [Fact]
    public Task Opening_a_mapper_lists_its_statements_opens_a_picked_one_and_marks_edits() => Ui.Run(async () =>
    {
        var path = Path.Combine(_root, "EmpMapper.xml");
        File.WriteAllText(path, MapperSamples.EmpMapper);
        var host = new SqlEditorHost { MyBatisHooks = new MyBatisHooks(openHandler: new MyBatisOpenHandler()) };

        await host.OpenWorkspaceFileAsync(path, default);
        var ws = host.ActiveWorkspace!;
        var session = (MyBatisWorkspaceSession)ws.MyBatis!;
        Assert.Equal(6, session.VisibleEntries.Count(e => e.IsStatement));

        var tab = session.OpenStatement("updateEmpSal")!;
        Assert.Same(tab, ws.ActiveTab);
        Assert.Equal(session.Document.Find("updateEmpSal")!.Text, tab.Text);

        tab.Document.Insert(tab.Document.TextLength - "</update>".Length, "  -- 고침\n");
        Assert.True(tab.IsDirty);
        Assert.True(session.Find("updateEmpSal")!.IsModified);
        Assert.StartsWith("* ", ws.DisplayTitle, StringComparison.Ordinal);
    });

    [Fact]
    public Task Statements_and_parameters_tools_hide_on_other_documents_and_come_back_on_the_mapper() => HeadlessHost.Run(async () =>
    {
        using var h = new ShellHarness();
        var docking = h.Get<DockWorkspace>();
        var path = Path.Combine(_root, "EmpMapper.xml");
        File.WriteAllText(path, MapperSamples.EmpMapper);
        var host = new SqlEditorHost { MyBatisHooks = new MyBatisHooks(openHandler: new MyBatisOpenHandler(docking)) };
        var plain = host.NewWorkspace("plain");
        var plainDoc = docking.OpenDocument(SqlEditorDoc, plain)!;
        docking.Activate(plainDoc);
        Pump();
        Assert.DoesNotContain(Statements, docking.VisibleToolIds);
        Assert.DoesNotContain(Expanded, docking.VisibleToolIds);

        await host.OpenWorkspaceFileAsync(path, default);
        var mapperDoc = docking.OpenDocument(SqlEditorDoc, host.ActiveWorkspace!)!;
        docking.Activate(mapperDoc);
        Pump();
        Assert.Contains(Statements, docking.VisibleToolIds);
        Assert.DoesNotContain(Expanded, docking.VisibleToolIds); // 펼친 SQL 은 쿼리 탭 안 분할이다(TSK-03-10)

        for (var round = 0; round < 2; round++)
        {
            docking.Activate(plainDoc);
            Pump();
            Assert.DoesNotContain(Statements, docking.VisibleToolIds);
            Assert.DoesNotContain(Expanded, docking.VisibleToolIds);

            docking.Activate(mapperDoc);
            Pump();
            Assert.Contains(Statements, docking.VisibleToolIds);
            Assert.DoesNotContain(Expanded, docking.VisibleToolIds); // 펼친 SQL 은 쿼리 탭 안 분할이다(TSK-03-10)
        }
    });

    [Fact]
    [Trait("Category", "Perf")]
    public Task Opening_a_1mb_mapper_takes_at_most_one_second() => Ui.Run(async () =>
    {
        var path = Path.Combine(_root, "BigMapper.xml");
        File.WriteAllText(path, BigMapper(1024 * 1024));
        Assert.True(new FileInfo(path).Length >= 1024 * 1024);

        // 첫 실행의 JIT·파서 준비는 재지 않는다(앱도 시작 뒤 첫 열기 전에 다른 xml 을 읽을 수 있다).
        var warm = new SqlEditorHost { MyBatisHooks = new MyBatisHooks(openHandler: new MyBatisOpenHandler()) };
        var warmPath = Path.Combine(_root, "Warm.xml");
        File.WriteAllText(warmPath, MapperSamples.EmpMapper);
        await warm.OpenWorkspaceFileAsync(warmPath, default);

        var host = new SqlEditorHost { MyBatisHooks = new MyBatisHooks(openHandler: new MyBatisOpenHandler()) };
        var sw = Stopwatch.StartNew();
        await host.OpenWorkspaceFileAsync(path, default);
        sw.Stop();

        var session = (MyBatisWorkspaceSession)host.ActiveWorkspace!.MyBatis!;
        Assert.True(session.Entries.Count > 1000, $"문장 {session.Entries.Count}개");
        Assert.Single(host.ActiveWorkspace.Tabs); // 탭은 고른 문장만
        Console.WriteLine($"[perf] 1MB 매퍼 열기 {sw.ElapsedMilliseconds} ms, 요소 {session.Entries.Count}개");
        Assert.True(sw.ElapsedMilliseconds <= 1000, $"1MB 매퍼 열기 {sw.ElapsedMilliseconds} ms");
    });

    [Fact]
    [Trait("Category", "Perf")]
    public Task Opening_a_1mb_mapper_with_the_statements_panel_on_screen_takes_at_most_one_second() => Ui.Run(async () =>
    {
        var path = Path.Combine(_root, "BigMapper.xml");
        File.WriteAllText(path, BigMapper(1024 * 1024));
        var host = new SqlEditorHost { MyBatisHooks = new MyBatisHooks(openHandler: new MyBatisOpenHandler()) };
        var vm = new Oranda.App.Features.MyBatisWorkspace.Statements.MyBatisStatementsViewModel { Host = host };
        var view = new Oranda.App.Features.MyBatisWorkspace.Statements.MyBatisStatementsView { DataContext = vm };
        var window = new Avalonia.Controls.Window { Width = 320, Height = 800, Content = view };
        window.Show();
        Pump();

        // 실제 앱처럼 「문장」 창이 화면에 있는 채로 연다: 세션 + 목록 짓기 + 첫 레이아웃까지 잰다.
        var sw = Stopwatch.StartNew();
        await host.OpenWorkspaceFileAsync(path, default);
        Pump();
        window.UpdateLayout();
        sw.Stop();
        window.Close();

        Assert.Equal(((MyBatisWorkspaceSession)host.ActiveWorkspace!.MyBatis!).Entries.Count, vm.Groups.Sum(g => g.Children.Count));
        Console.WriteLine($"[perf] 1MB 매퍼 열기(「문장」 창 포함) {sw.ElapsedMilliseconds} ms");
        Assert.True(sw.ElapsedMilliseconds <= 1000, $"1MB 매퍼 열기(「문장」 창 포함) {sw.ElapsedMilliseconds} ms");
    });

    /// <summary>시안 selectEmpList 모양 문장을 id 만 바꿔 <paramref name="bytes"/> 이상이 될 때까지 이어 붙인 매퍼.</summary>
    private static string BigMapper(int bytes)
    {
        var body = MapperSamples.EmpMapper;
        int start = body.IndexOf("<select id=\"selectEmpList\"", StringComparison.Ordinal);
        int end = body.IndexOf("</select>", start, StringComparison.Ordinal) + "</select>".Length;
        var statement = body[start..end];
        var sb = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<mapper namespace=\"t.Big\">\n");
        for (int i = 0; sb.Length < bytes; i++)
        {
            sb.Append("  ").Append(statement.Replace("selectEmpList", "select" + i.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)).Append("\n\n");
        }

        return sb.Append("</mapper>\n").ToString();
    }
}
