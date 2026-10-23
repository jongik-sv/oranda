using System.Diagnostics;
using System.Globalization;
using System.Text;
using Avalonia.Controls;
using Avalonia.Threading;
using Oranda.App.Features.MyBatisWorkspace.Folder;
using Oranda.App.Features.MyBatisWorkspace.Hooks;
using Oranda.App.Features.MyBatisWorkspace.Statements;
using Oranda.App.Features.MyBatisWorkspace.Workspace;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Tests.Features.SqlEditor;
using Xunit.Abstractions;

namespace Oranda.App.Tests.Features.MyBatisWorkspace.Perf;

/// <summary>
/// TSK-04-02: PRD 6 성능 수치 중 기능 Task 에 시험이 없던 두 항목.
/// 문장 3,000줄 매퍼 열기 1 s 이하(1MB 열기는 WorkspaceUiTests), 매퍼 2,000개 폴더 훑기 중 UI 스레드 막힘 100 ms 이하.
/// 값을 고친 뒤 다시 계산(ParamsRunTests)·조합 256개 검사(ComboCheckTests.Runner)는 각 시험 파일에 있다.
/// </summary>
public sealed class MyBatisWorkspacePerfTests(ITestOutputHelper output) : IDisposable
{
    private const int MapperCount = 2000;
    private const int FolderCount = 20;
    private const int UiBlockLimitMs = 100;
    private const int OpenLimitMs = 1000;
    private const int StatementLines = 3000;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "oranda-mbperf-" + Guid.NewGuid().ToString("N"));

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
    [Trait("Category", "Perf")]
    public Task Scanning_a_folder_of_2000_mappers_blocks_the_ui_thread_at_most_100_ms() => Ui.Run(async () =>
    {
        // 첫 실행의 JIT·감시 준비는 재지 않는다: 작은 폴더를 한 번 열어 데운다(앱도 폴더를 열기 전에 다른 일을 한다).
        var warmDir = Path.Combine(_root, "warm");
        Directory.CreateDirectory(warmDir);
        File.WriteAllText(Path.Combine(warmDir, "WarmMapper.xml"), "<mapper namespace=\"t.Warm\"><select id=\"a\">SELECT 1 FROM dual</select></mapper>");
        using var folder = new MapperFolderService(dataDirectory: null) { DebounceMilliseconds = 600_000 };

        // 단계별 막힘(TSK-03-12): 트리 다시 짓기 = 폴더 사건에서 ViewModel 처리기 앞뒤(구독 순서대로 불린다) 사이 시간.
        long changedAt = 0;
        double rebuildWorst = 0;
        bool measuring = false;
        folder.Changed += (_, _) => changedAt = Stopwatch.GetTimestamp();
        var vm = new MapperFolderTreeViewModel(folder);
        folder.Changed += (_, _) =>
        {
            if (measuring)
            {
                rebuildWorst = Math.Max(rebuildWorst, Stopwatch.GetElapsedTime(changedAt).TotalMilliseconds);
            }
        };
        var view = new MapperFolderTreeView { DataContext = vm };
        var window = new Window { Width = 320, Height = 800, Content = view };
        window.Show();
        Pump();
        Assert.True(await folder.OpenAsync(warmDir, default));
        Pump();
        window.UpdateLayout();
        folder.Close();
        Pump();
        Directory.Delete(warmDir, recursive: true);

        WriteMapperFolder();

        // 훑기 중 UI 스레드가 얼마나 멈추는지 잰다: 다른 스레드가 5 ms 마다 UI 스레드에 일을 던지고, 던진 때부터 실행될 때까지의 지연 최댓값이 막힘이다.
        // 일은 모두 UI 스레드에서 실행되므로 worst 는 같은 스레드에서만 읽고 쓴다.
        using var stop = new CancellationTokenSource();
        long worst = 0;
        var probe = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                long posted = Stopwatch.GetTimestamp();
                await Dispatcher.UIThread.InvokeAsync(() => worst = Math.Max(worst, (long)Stopwatch.GetElapsedTime(posted).TotalMilliseconds));
                await Task.Delay(5);
            }
        });

        long elapsed;
        bool opened;
        double startMs;
        double scanMs;
        double drawMs;
        try
        {
            measuring = true;
            var sw = Stopwatch.StartNew();
            var opening = folder.OpenAsync(_root, default); // 첫 await 까지(감시 걸기·최근 폴더·빈 트리)는 부른 스레드(UI)에서 돈다
            startMs = sw.Elapsed.TotalMilliseconds;
            opened = await opening;
            scanMs = sw.Elapsed.TotalMilliseconds;
            var draw = Stopwatch.StartNew();
            Pump();
            window.UpdateLayout();
            drawMs = draw.Elapsed.TotalMilliseconds;
            elapsed = sw.ElapsedMilliseconds;
            measuring = false;
        }
        finally
        {
            await stop.CancelAsync();
            await probe;
            window.Close();
        }

        Assert.True(opened);
        Assert.Equal(MapperCount, folder.Index!.Files.Count);
        Assert.True(view.IsShowingAll); // 나눠 붙인 최상위 노드가 그리기 안에 다 붙었다
        output.WriteLine($"[perf] 매퍼 {MapperCount}개 폴더 훑기: 전체 {elapsed} ms, UI 스레드 최대 막힘 {worst} ms (상한 {UiBlockLimitMs} ms)");
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[perf] 막힘 단계: 훑기 시작(UI) {startMs:F0} ms / 트리 다시 짓기 최대 {rebuildWorst:F0} ms / 훑기 끝까지 {scanMs:F0} ms / 첫 그리기(나눠 붙이기 합계) {drawMs:F0} ms"));
        Assert.True(worst <= UiBlockLimitMs, $"UI 스레드 최대 막힘 {worst} ms > {UiBlockLimitMs} ms");
    });

    [Fact]
    [Trait("Category", "Perf")]
    public Task Opening_a_mapper_with_a_3000_line_statement_shows_the_list_within_one_second() => Ui.Run(async () =>
    {
        var path = Path.Combine(_root, "LongStatement.xml");
        Directory.CreateDirectory(_root);
        File.WriteAllText(path, LongStatementMapper());
        Assert.True(File.ReadLines(path).Count() >= StatementLines);

        // 첫 실행의 JIT·파서 준비는 재지 않는다.
        var warm = new SqlEditorHost { MyBatisHooks = new MyBatisHooks(openHandler: new MyBatisOpenHandler()) };
        var warmPath = Path.Combine(_root, "Warm.xml");
        File.WriteAllText(warmPath, "<mapper namespace=\"t.Warm\"><select id=\"a\">SELECT 1 FROM dual</select></mapper>");
        await warm.OpenWorkspaceFileAsync(warmPath, default);

        var host = new SqlEditorHost { MyBatisHooks = new MyBatisHooks(openHandler: new MyBatisOpenHandler()) };
        var vm = new MyBatisStatementsViewModel { Host = host };
        var window = new Window { Width = 320, Height = 800, Content = new MyBatisStatementsView { DataContext = vm } };
        window.Show();
        Pump();

        var sw = Stopwatch.StartNew();
        await host.OpenWorkspaceFileAsync(path, default);
        Pump();
        window.UpdateLayout();
        sw.Stop();
        window.Close();

        var session = (MyBatisWorkspaceSession)host.ActiveWorkspace!.MyBatis!;
        Assert.Equal(2, session.Entries.Count);
        Assert.Equal(2, vm.Groups.Sum(g => g.Children.Count));
        output.WriteLine($"[perf] 문장 {StatementLines}줄 매퍼 열기(「문장」 창 포함) {sw.ElapsedMilliseconds} ms (상한 {OpenLimitMs} ms)");
        Assert.True(sw.ElapsedMilliseconds <= OpenLimitMs, $"문장 {StatementLines}줄 매퍼 열기 {sw.ElapsedMilliseconds} ms > {OpenLimitMs} ms");
    });

    // 폴더 20개에 매퍼 100개씩. 매퍼마다 요소 4개(sql 조각·select·insert·update), select 는 자기 매퍼의 조각을 include 한다.
    private void WriteMapperFolder()
    {
        for (var f = 0; f < FolderCount; f++)
        {
            var dir = Path.Combine(_root, "mod" + f.ToString("D2", CultureInfo.InvariantCulture));
            Directory.CreateDirectory(dir);
            for (var m = 0; m < MapperCount / FolderCount; m++)
            {
                var n = f * (MapperCount / FolderCount) + m;
                var ns = "com.acme.m" + n.ToString(CultureInfo.InvariantCulture);
                File.WriteAllText(Path.Combine(dir, "M" + n.ToString("D4", CultureInfo.InvariantCulture) + "Mapper.xml"), $$"""
                    <?xml version="1.0" encoding="UTF-8"?>
                    <!DOCTYPE mapper PUBLIC "-//mybatis.org//DTD Mapper 3.0//EN" "http://mybatis.org/dtd/mybatis-3-mapper.dtd">
                    <mapper namespace="{{ns}}">
                      <sql id="cols">E.EMPNO, E.ENAME, E.SAL</sql>
                      <select id="selectList" resultType="map">
                        SELECT <include refid="cols"/> FROM EMP E
                        <where><if test="ename != null">AND E.ENAME LIKE #{ename}</if></where>
                      </select>
                      <insert id="insertOne">INSERT INTO EMP (EMPNO, ENAME) VALUES (#{empNo}, #{ename})</insert>
                      <update id="updateSal">UPDATE EMP <set><if test="sal != null">SAL = #{sal},</if></set> WHERE EMPNO = #{empNo}</update>
                    </mapper>
                    """);
            }
        }
    }

    // 문장 하나(select)가 3,005줄(<select>·SELECT·<where> 3줄 + if 3줄 × 1,000 + 닫는 2줄): where 안에 if 를 세 줄씩 이어 붙이고, 작은 문장 하나를 더 둔다.
    private static string LongStatementMapper()
    {
        var sb = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<mapper namespace=\"t.Long\">\n");
        sb.Append("  <select id=\"longSelect\" resultType=\"map\">\n    SELECT * FROM T\n    <where>\n");
        for (var i = 0; i < StatementLines / 3; i++)
        {
            sb.Append(CultureInfo.InvariantCulture, $"    <if test=\"p{i} != null\">\n      AND C{i} = #{{p{i}}}\n    </if>\n");
        }

        sb.Append("    </where>\n  </select>\n");
        sb.Append("  <select id=\"small\" resultType=\"map\">SELECT 1 FROM dual</select>\n</mapper>\n");
        return sb.ToString();
    }
}
