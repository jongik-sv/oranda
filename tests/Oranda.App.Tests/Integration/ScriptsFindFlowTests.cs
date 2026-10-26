using System.Diagnostics;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Oranda.App.Features.HistoryScripts;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Tests.Shell.Headless;
using Xunit.Abstractions;

namespace Oranda.App.Tests.Integration;

// 소유: TSK-10-03. 스크립트 검색(TSK-10-01)·새 스크립트(TSK-10-02)를 한 흐름으로 잇는 통합 확인과 5,000개 파일 검색 시간.
// 실제 파일 시스템(임시 폴더)과 실제 ScriptsView 를 쓴다. ORANDA_RENDER_DIR 가 있으면 단계별 라이트·다크 PNG 를 남긴다.

/// <summary>스크립트 창 통합 흐름: 폴더 둘 연결 → 이름·내용 검색 → 결과에서 새 스크립트 → 만든 파일이 다시 검색에 잡힌다.</summary>
public sealed class ScriptsFindFlowTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "oranda-itest-scripts-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
            {
                foreach (var d in Directory.EnumerateDirectories(_dir, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(d, FileAttributes.Directory); // 숨김 속성을 풀어야 지울 수 있는 환경이 있다
                }

                Directory.Delete(_dir, recursive: true);
            }
        }
        catch (IOException)
        {
            // 임시 폴더 정리 실패는 무시한다.
        }
    }

    private string Write(string rel, string text, string encoding = "utf-8")
    {
        var path = Path.Combine(_dir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, FileEncoding.Encode(text, encoding));
        return path;
    }

    /// <summary>폴더 둘: 하위 폴더·숨김 폴더·비스크립트 파일·여러 인코딩을 섞는다.</summary>
    private (string Work, string Shared) Seed()
    {
        Write(Path.Combine("work", "월마감_집계.sql"), "-- 월 마감\nselect * from closing where ym = :ym;");
        Write(Path.Combine("work", "dev", "proc_emp_load.sql"), "create or replace procedure proc_emp_load is\nbegin\n  insert into emp_hist select * from EMP;\nend;");
        Write(Path.Combine("work", "dev", "pkg", "pkg_pay.pkb"), "-- 급여\nselect a.b(1) from dual; -- 정규식 문자 a.b(\n", "utf-16");
        Write(Path.Combine("work", "dev", "사원.sql"), "select 이름 from 사원 where 부서 = '영업';", "cp949");
        Write(Path.Combine("work", "notes.txt"), "emp 메모 — 글 파일도 내용 검색 대상이다");
        Write(Path.Combine("work", "image.png"), "emp"); // 스크립트가 아닌 확장자: 이름·내용 모두 빠진다
        var hidden = Path.Combine("work", OperatingSystem.IsWindows() ? "secret" : ".secret");
        Write(Path.Combine(hidden, "emp_secret.sql"), "select * from emp;");
        if (OperatingSystem.IsWindows())
        {
            File.SetAttributes(Path.Combine(_dir, hidden), FileAttributes.Directory | FileAttributes.Hidden);
        }

        Write(Path.Combine("shared", "공용_emp_조회.sql"), "SELECT * FROM Emp WHERE deptno = 10;", "utf-8-bom");
        Write(Path.Combine("shared", "sub", "dept.sql"), "select * from dept;");
        return (Path.Combine(_dir, "work"), Path.Combine(_dir, "shared"));
    }

    private static void Pump()
    {
        for (var i = 0; i < 4; i++)
        {
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static async Task Search(ScriptsView view, ScriptsViewModel vm, string text, bool content)
    {
        vm.SearchContent = content;
        view.FindControl<TextBox>("SearchBox")!.Text = text;
        Pump();
        await vm.PendingSearch;
        Pump();
        view.UpdateLayout();
    }

    private static string[] FileNames(ScriptsViewModel vm) => [.. vm.Results.OfType<ScriptSearchFileRow>().Select(r => r.Name)];

    [Fact]
    [Trait("Category", "Integration")]
    public Task Two_folders_name_and_content_search_then_a_new_script_from_a_result_is_found_by_the_next_search() => HeadlessHost.Run(async () =>
    {
        var (work, shared) = Seed();
        var vm = new ScriptsViewModel(new ScriptFolderList()) { SearchDebounceMilliseconds = 0, ActiveEditorText = () => "select * from emp where empno = :empno;" };
        Assert.True(vm.AddFolder(work));
        Assert.True(vm.AddFolder(shared));
        vm.SelectedNode = null;
        var opened = new List<string>();
        vm.OpenRequested += (_, path) => opened.Add(path);
        string? promptFolder = null;
        vm.NewScriptPrompt = request =>
        {
            promptFolder = request.Folder;
            Assert.True(request.CanFill); // 활성 편집기 글이 있으면 채우기를 고를 수 있다
            Assert.False(request.Create("proc_emp_load", false).Ok); // 이미 있는 이름은 덮어쓰지 않는다
            Assert.False(request.Create("bad:name", false).Ok); // 파일 이름에 쓸 수 없는 글자
            return Task.FromResult(request.Create("emp_itest_new", true).Path);
        };

        var view = new ScriptsView { DataContext = vm };
        var window = new Window { Content = view, Width = 420, Height = 560 };
        window.Show();
        Pump();
        try
        {
            var tree = view.FindControl<TreeView>("Tree")!;
            var newButton = view.FindControl<Button>("NewScriptButton")!;
            Assert.False(newButton.IsEnabled); // 폴더 둘·선택 없음
            Render(window, "1-two-folders");

            // 이름: 낱말 둘을 순서 없이, 대소문자 무시. 숨김 폴더·비스크립트 파일은 빠진다.
            await Search(view, vm, "EMP proc", content: false);
            Assert.Equal(["proc_emp_load.sql"], FileNames(vm));
            await Search(view, vm, "emp", content: false);
            Assert.Equal(["proc_emp_load.sql", "공용_emp_조회.sql"], FileNames(vm));
            Assert.DoesNotContain("emp_secret.sql", FileNames(vm));
            Assert.False(tree.IsVisible);
            Render(window, "2-name-search");

            // 내용: 대소문자 무시, 글 파일 포함, 숨김 폴더 제외, UTF-8 BOM.
            await Search(view, vm, "from emp", content: true);
            Assert.Equal(["proc_emp_load.sql", "공용_emp_조회.sql"], FileNames(vm));
            Render(window, "3-content-search");

            // 한글(CP949)·정규식 문자(UTF-16)는 글자 그대로 찾는다.
            await Search(view, vm, "부서 = '영업'", content: true);
            Assert.Equal(["사원.sql"], FileNames(vm));
            await Search(view, vm, "a.b(", content: true);
            Assert.Equal(["pkg_pay.pkb"], FileNames(vm));
            await Search(view, vm, "a?b", content: true);
            Assert.Empty(vm.Results);
            Assert.True(vm.HasEmptyHint);
            Render(window, "4-empty");

            // 결과 줄을 고르면 그 파일의 폴더가 새 스크립트 대상이 된다.
            await Search(view, vm, "insert into emp_hist", content: true);
            var line = vm.Results.OfType<ScriptSearchLineRow>().Single();
            Assert.Equal(3, line.Line);
            vm.SelectedResult = line;
            Pump();
            var dev = Path.Combine(work, "dev");
            Assert.Equal(dev, vm.NewScriptFolder);
            Assert.True(newButton.IsEnabled);

            newButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await HeadlessHost.WaitUntil(() => opened.Count == 1);
            Pump();
            var created = Path.Combine(dev, "emp_itest_new.sql");
            Assert.Equal(dev, promptFolder);
            Assert.Equal([created], opened);
            Assert.Equal("select * from emp where empno = :empno;", File.ReadAllText(created));
            Assert.False(vm.IsSearchActive); // 만든 뒤 검색을 지우고 트리에서 새 파일을 고른다
            Assert.True(tree.IsVisible);
            Assert.Equal(created, vm.SelectedNode!.FullPath);
            Render(window, "5-created");

            // 만든 파일이 다음 검색(이름·내용)에 잡힌다.
            await Search(view, vm, "itest new", content: false);
            Assert.Equal(["emp_itest_new.sql"], FileNames(vm));
            await Search(view, vm, ":EMPNO", content: true);
            Assert.Equal(["emp_itest_new.sql"], FileNames(vm));

            // Esc: 검색을 지우고 트리로 돌아간다.
            view.FindControl<TextBox>("SearchBox")!.Focus();
            window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            Pump();
            Assert.False(vm.IsSearchActive);
            Assert.True(tree.IsVisible);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    [Trait("Category", "Integration")]
    public async Task The_1000_match_limit_stops_the_walk_and_a_newer_search_cancels_the_older_one()
    {
        // 한 파일에 1,200곳: 한도(1,000)에서 멈추고 상태 줄이 알린다.
        Write(Path.Combine("lim", "many.sql"), string.Join('\n', Enumerable.Repeat("select emp from dual;", 1200)));
        var vm = new ScriptsViewModel(new ScriptFolderList()) { SearchDebounceMilliseconds = 0 };
        Assert.True(vm.AddFolder(Path.Combine(_dir, "lim")));

        vm.SearchContent = true;
        vm.SearchText = "emp";
        await vm.PendingSearch;
        Assert.Equal(ScriptSearch.MaxMatches, vm.Results.OfType<ScriptSearchLineRow>().Count());
        Assert.Contains("(한도 도달)", vm.StatusMessage, StringComparison.Ordinal);

        // 큰 파일(2 MB 초과)은 내용을 읽지 않는다.
        Write(Path.Combine("lim", "big.sql"), new string('x', (int)ScriptSearch.MaxFileBytes + 10) + "\nneedle_big");
        var big = ScriptSearch.Run(new ScriptSearchQuery("needle_big", true), [Path.Combine(_dir, "lim")]);
        Assert.Empty(big.Files);
        Assert.Equal(1, big.SkippedLarge);

        // 이어 친 입력이 앞 검색을 취소하고 마지막 결과만 남는다.
        vm.SearchDebounceMilliseconds = 30;
        vm.SearchText = "dual";
        vm.SearchText = "select emp";
        await vm.PendingSearch;
        Assert.All(vm.Results.OfType<ScriptSearchLineRow>(), r => Assert.Contains("select emp", r.Preview, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// TSK-10-03 측정: 파일 5,000개(폴더 50개 × 100, 한 파일 약 1 KB)를 이름·내용으로 찾는 시간. 결과서에 적는다.
    /// 첫 번째는 디스크 캐시가 찬 뒤의 값이 되도록 한 번 훑고 잰다. 한도는 멈춤만 잡는 넉넉한 값이다.
    /// </summary>
    [Fact]
    [Trait("Category", "Perf")]
    public void Five_thousand_files_are_searched_by_name_and_content_in_time()
    {
        var root = Path.Combine(_dir, "five");
        var body = new StringBuilder();
        for (var i = 0; i < 30; i++)
        {
            body.Append("select col_").Append(i).Append(" from some_table where id = :id;\n");
        }

        var text = body.ToString();
        for (var f = 0; f < 50; f++)
        {
            var folder = Path.Combine(root, "f" + f.ToString("D2", System.Globalization.CultureInfo.InvariantCulture));
            Directory.CreateDirectory(folder);
            for (var n = 0; n < 100; n++)
            {
                var name = $"script_{f:D2}_{n:D3}.sql";
                var content = f == 49 && n == 99 ? text + "-- needle_last_file\n" : text;
                File.WriteAllText(Path.Combine(folder, name), content, new UTF8Encoding(false));
            }
        }

        _ = ScriptSearch.Run(new ScriptSearchQuery("warm", true), [root]); // 디스크 캐시 데우기

        var sw = Stopwatch.StartNew();
        var byName = ScriptSearch.Run(new ScriptSearchQuery("script 049", false), [root]);
        var nameTime = sw.Elapsed;

        sw.Restart();
        var byContent = ScriptSearch.Run(new ScriptSearchQuery("needle_last_file", true), [root]);
        var contentTime = sw.Elapsed;

        sw.Restart();
        var many = ScriptSearch.Run(new ScriptSearchQuery("col_7 from", true), [root]);
        var limitTime = sw.Elapsed;

        Assert.Equal(50, byName.Files.Count); // script_00_049 … script_49_049
        Assert.Equal("script_49_099.sql", Assert.Single(byContent.Files).Name);
        Assert.True(many.LimitReached);
        output.WriteLine($"TSK-10-03 스크립트 검색 5,000개: 이름 {nameTime.TotalMilliseconds:N0} ms, 내용(끝까지) {contentTime.TotalMilliseconds:N0} ms, 내용 한도 1,000곳 도달 {limitTime.TotalMilliseconds:N0} ms");
        Assert.True(nameTime < TimeSpan.FromSeconds(30), $"이름 {nameTime}");
        Assert.True(contentTime < TimeSpan.FromSeconds(60), $"내용 {contentTime}");
    }

    private static void Render(Window window, string name, int width = 420, int height = 560)
    {
        var dir = Environment.GetEnvironmentVariable("ORANDA_RENDER_DIR");
        if (string.IsNullOrEmpty(dir))
        {
            return;
        }

        Directory.CreateDirectory(dir);
        try
        {
            foreach (var (variant, tone) in new[] { (ThemeVariant.Light, "light"), (ThemeVariant.Dark, "dark") })
            {
                Application.Current!.RequestedThemeVariant = variant;
                Pump();
                window.UpdateLayout();
                using var bmp = new RenderTargetBitmap(new PixelSize(width, height));
                bmp.Render(window);
#pragma warning disable CS0618 // 렌더 확인용: 새 오버로드가 없는 헤드리스 비트맵
                bmp.Save(Path.Combine(dir, $"itest-scripts-{name}-{tone}.png"));
#pragma warning restore CS0618
            }
        }
        finally
        {
            Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
            Pump();
        }
    }
}
