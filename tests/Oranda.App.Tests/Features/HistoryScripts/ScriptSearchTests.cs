using System.Text;
using Oranda.App.Features.HistoryScripts;
using Oranda.App.Features.SqlEditor;

namespace Oranda.App.Tests.Features.HistoryScripts;

// 소유: TSK-10-01. ScriptSearch 검색기 단위 시험(이름·내용·한도·취소·인코딩).
public sealed class ScriptSearchTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "oranda-ssearch-" + Guid.NewGuid().ToString("N"));

    public ScriptSearchTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // 임시 폴더 정리 실패는 무시한다.
        }
    }

    private string Write(string rel, string text)
    {
        var path = Path.Combine(_dir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text, new UTF8Encoding(false));
        return path;
    }

    private string WriteBytes(string rel, byte[] bytes)
    {
        var path = Path.Combine(_dir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private ScriptSearchResult Run(string query, bool content = false, int limit = ScriptSearch.MaxMatches, long maxBytes = ScriptSearch.MaxFileBytes, CancellationToken ct = default)
        => ScriptSearch.Run(new ScriptSearchQuery(query, content), [_dir], null, limit, maxBytes, ct);

    private static string[] Highlighted(ScriptSearchLine line) => line.Ranges.Select(r => line.Preview.Substring(r.Start, r.Length)).ToArray();

    private static string[] Names(ScriptSearchResult r) => r.Files.Select(f => f.Name).ToArray();

    // ---- 이름 ----

    [Fact]
    public void Name_search_needs_every_word_in_any_order_ignoring_case()
    {
        Write("Proc_Emp_Load.sql", "x");
        Write("proc_dept.sql", "x");
        Write("emp.sql", "x");

        Assert.Equal(["Proc_Emp_Load.sql"], Names(Run("emp PROC")));
        Assert.Equal(["Proc_Emp_Load.sql"], Names(Run("  LOAD   emp ")));
        Assert.Equal(["emp.sql", "Proc_Emp_Load.sql"], Names(Run("emp")));
        Assert.Empty(Run("emp dept").Files);
        Assert.Equal(["proc_dept.sql"], Names(Run("DEPT")));
    }

    [Fact]
    public void Blank_query_finds_nothing()
    {
        Write("a.sql", "x");
        Assert.Empty(Run("").Files);
        Assert.Empty(Run("   \t ").Files);
        Assert.Empty(Run("", content: true).Files);
        Assert.False(Run("").LimitReached);
    }

    [Fact]
    public void Name_search_does_not_read_file_contents()
    {
        Write("a.sql", "select emp from dual");
        Assert.Empty(Run("emp").Files);
        var r = Run("a.sql");
        Assert.Single(r.Files);
        Assert.Empty(r.Files[0].Lines);
        Assert.Equal(1, r.MatchCount);
    }

    [Fact]
    public void Only_script_extensions_are_searched_and_hidden_folders_are_skipped()
    {
        Write("emp.sql", "x");
        Write("emp.pkb", "x");
        Write("emp.png", "x");
        Write("emp.md", "x");
        Write("emp.workspace.md", "x");
        Write("mapper_emp.xml", "x");
        Write("sub/emp_inner.sql", "x");

        Assert.Equal(["emp.pkb", "emp.sql", "emp.workspace.md", "mapper_emp.xml", "emp_inner.sql"], Names(Run("emp")));
    }

    [Fact]
    public void A_folder_the_tree_would_call_hidden_is_skipped_including_its_subfolders()
    {
        // 트리와 같은 판정(FileAttributes.Hidden): Windows 는 속성, 유닉스는 점으로 시작하는 이름.
        var hidden = Path.Combine(_dir, OperatingSystem.IsWindows() ? "secret" : ".secret");
        Write(Path.Combine(hidden, "deep", "emp.sql"), "x");
        Write(Path.Combine(hidden, "emp.sql"), "x");
        if (OperatingSystem.IsWindows())
        {
            File.SetAttributes(hidden, FileAttributes.Directory | FileAttributes.Hidden);
        }

        Write("open/emp.sql", "x");

        Assert.Equal([Path.Combine(_dir, "open", "emp.sql")], Run("emp").Files.Select(f => f.Path));
    }

    [Fact]
    public void A_link_back_to_an_ancestor_does_not_loop()
    {
        Write("emp.sql", "x");
        Write("sub/emp2.sql", "x");
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(_dir, "sub", "back"), _dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return; // 권한이 없어 링크를 못 만드는 환경
        }

        var r = Run("emp");

        Assert.Equal(["emp.sql", "emp2.sql"], Names(r));
    }

    [Fact]
    public void Progress_is_reported_every_twenty_files()
    {
        for (var i = 0; i < 45; i++)
        {
            Write($"f{i:00}.sql", "x");
        }

        var seen = new List<ScriptSearchProgress>();
        ScriptSearch.Run(new ScriptSearchQuery("zzz", false), [_dir], new SyncProgress(seen.Add));

        Assert.Equal([20, 40], seen.Select(p => p.FilesScanned));
    }

    private sealed class SyncProgress(Action<ScriptSearchProgress> report) : IProgress<ScriptSearchProgress>
    {
        public void Report(ScriptSearchProgress value) => report(value);
    }

    [Fact]
    public async Task RunAsync_returns_the_result_and_honors_cancellation()
    {
        Write("emp.sql", "x");

        var r = await ScriptSearch.RunAsync(new ScriptSearchQuery("emp", false), [_dir]);

        Assert.Single(r.Files);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ScriptSearch.RunAsync(new ScriptSearchQuery("emp", false), [_dir], ct: cts.Token));
    }

    [Fact]
    public void Folder_shown_is_relative_and_starts_at_the_linked_folder_name()
    {
        Write("top.sql", "x");
        Write(Path.Combine("dev", "proc", "p.sql"), "x");
        var root = Path.GetFileName(_dir);

        var r = Run(".sql");

        Assert.Equal(root, r.Files.Single(f => f.Name == "top.sql").Folder);
        Assert.Equal(Path.Combine(root, "dev", "proc"), r.Files.Single(f => f.Name == "p.sql").Folder);
    }

    [Fact]
    public void Several_roots_are_searched_in_order_missing_ones_are_ignored_and_nested_duplicates_appear_once()
    {
        var a = Path.Combine(_dir, "a");
        var b = Path.Combine(_dir, "b");
        Directory.CreateDirectory(a);
        Directory.CreateDirectory(b);
        File.WriteAllText(Path.Combine(a, "emp1.sql"), "x");
        File.WriteAllText(Path.Combine(b, "emp2.sql"), "x");
        Directory.CreateDirectory(Path.Combine(a, "in"));
        File.WriteAllText(Path.Combine(a, "in", "emp3.sql"), "x");

        var r = ScriptSearch.Run(new ScriptSearchQuery("emp", false), [b, Path.Combine(_dir, "missing"), a, Path.Combine(a, "in")]);

        Assert.Equal(["emp2.sql", "emp1.sql", "emp3.sql"], Names(r));
    }

    // ---- 내용 ----

    [Fact]
    public void Content_search_reports_line_numbers_and_preview_with_the_match_range()
    {
        Write("a.sql", "-- header\nselect *\n    from EMP e\nwhere 1 = 1\nselect * from emp");

        var r = Run("emp", content: true);

        var file = Assert.Single(r.Files);
        Assert.Equal([3, 5], file.Lines.Select(l => l.LineNumber));
        Assert.Equal("from EMP e", file.Lines[0].Preview); // 들여쓰기는 뺀다
        var hit = file.Lines[0];
        Assert.Equal("EMP", Highlighted(hit).Single());
        Assert.Equal(2, r.MatchCount);
    }

    [Theory]
    [InlineData("a\r\nemp\r\nb", 2)]
    [InlineData("a\remp\rb", 2)]
    [InlineData("a\nemp\nb", 2)]
    [InlineData("a\r\n\r\n\r\nemp", 4)]
    [InlineData("emp", 1)]
    [InlineData("a\n\n\nb\r\nc\remp", 6)]
    public void Line_numbers_follow_every_kind_of_line_break(string text, int expected)
    {
        Write("a.sql", text);
        Assert.Equal(expected, Assert.Single(Assert.Single(Run("emp", content: true).Files).Lines).LineNumber);
    }

    [Fact]
    public void Content_search_is_a_case_insensitive_plain_text_match_not_a_regex()
    {
        Write("a.sql", "axb\na.b\nA.B\nselect (1) from t where x = 'a+b'");

        Assert.Equal([2, 3], Run("a.b", content: true).Files.Single().Lines.Select(l => l.LineNumber));
        Assert.Equal([4], Run("(1)", content: true).Files.Single().Lines.Select(l => l.LineNumber));
        Assert.Equal([4], Run("a+B", content: true).Files.Single().Lines.Select(l => l.LineNumber));
        Assert.Empty(Run("a.*b", content: true).Files);
    }

    [Fact]
    public void A_multi_word_query_is_matched_as_one_phrase_in_content()
    {
        Write("a.sql", "from emp e join dept d\nfrom dept d, emp e");

        var r = Run("emp e", content: true);

        Assert.Equal([1, 2], r.Files.Single().Lines.Select(l => l.LineNumber));
        Assert.Empty(Run("e emp", content: true).Files.Where(f => f.Lines.Count > 0));
    }

    [Fact]
    public void With_content_on_a_file_found_only_by_name_is_still_listed_without_lines()
    {
        Write("emp_load.sql", "select 1 from dual");
        Write("other.sql", "select emp from dual");
        Write("none.sql", "select 1");

        var r = Run("emp", content: true);

        Assert.Equal(["emp_load.sql", "other.sql"], Names(r));
        Assert.Empty(r.Files[0].Lines);
        Assert.Single(r.Files[1].Lines);
        Assert.Equal(2, r.MatchCount);
    }

    [Fact]
    public void A_long_line_is_shown_around_the_match_with_a_leading_ellipsis_and_a_correct_range()
    {
        var line = new string('x', 300) + "NEEDLE" + new string('y', 500);
        Write("a.sql", "short\n" + line);

        var hit = Run("needle", content: true).Files.Single().Lines.Single();

        Assert.StartsWith("…", hit.Preview);
        Assert.True(hit.Preview.Length <= 201);
        Assert.Equal("NEEDLE", Highlighted(hit).Single());
    }

    [Fact]
    public void Tabs_in_a_preview_become_spaces_and_keep_the_range()
    {
        Write("a.sql", "\tselect\t* from\temp");

        var hit = Run("emp", content: true).Files.Single().Lines.Single();

        Assert.DoesNotContain('\t', hit.Preview);
        Assert.Equal("emp", Highlighted(hit).Single());
    }

    [Fact]
    public void Several_matches_on_one_line_count_as_one_place_and_all_are_highlighted()
    {
        Write("a.sql", "Emp x EMP y emp");
        var r = Run("emp", content: true);
        var line = Assert.Single(r.Files.Single().Lines);
        Assert.Equal(1, r.MatchCount);
        Assert.Equal(["Emp", "EMP", "emp"], Highlighted(line));
    }

    [Fact]
    public void The_file_hit_tells_whether_the_name_matched()
    {
        Write("emp.sql", "x");
        Write("other.sql", "emp");

        var r = Run("emp", content: true);

        Assert.True(r.Files.Single(f => f.Name == "emp.sql").NameMatched);
        Assert.False(r.Files.Single(f => f.Name == "other.sql").NameMatched);
        Assert.Equal(2, r.FileCount);
    }

    // ---- 한도 ----

    [Fact]
    public void More_than_the_limit_of_matching_lines_stops_at_the_limit()
    {
        Write("big.sql", string.Join('\n', Enumerable.Repeat("select emp from dual;", 1500)));

        var r = Run("emp", content: true);

        Assert.True(r.LimitReached);
        Assert.Equal(1000, r.MatchCount);
        Assert.Equal(1000, r.Files.Single().Lines.Count);
        Assert.Equal(1000, r.Files.Single().Lines[^1].LineNumber);
    }

    [Fact]
    public void Exactly_the_limit_is_not_reported_as_reached_and_one_more_is()
    {
        Write("a.sql", "emp\nemp\nemp");
        Assert.False(Run("emp", content: true, limit: 3).LimitReached);
        Assert.Equal(3, Run("emp", content: true, limit: 3).MatchCount);

        Write("b.sql", "emp");
        var over = Run("emp", content: true, limit: 3);
        Assert.True(over.LimitReached);
        Assert.Equal(3, over.MatchCount);
        Assert.Equal(["a.sql"], Names(over));
    }

    [Fact]
    public void The_limit_counts_name_hits_and_spans_files()
    {
        for (var i = 0; i < 12; i++)
        {
            Write($"emp_{i:00}.sql", "x");
        }

        var r = Run("emp", limit: 10);

        Assert.True(r.LimitReached);
        Assert.Equal(10, r.Files.Count);
        Assert.Equal(10, r.MatchCount);
        Assert.False(Run("emp", limit: 12).LimitReached);
    }

    [Fact]
    public void A_file_over_the_size_cap_is_skipped_for_content_but_a_name_match_is_kept()
    {
        Write("big_emp.sql", new string('a', 400) + " emp");
        Write("big2.sql", new string('a', 400) + " emp");
        Write("small.sql", "emp");

        var r = Run("emp", content: true, maxBytes: 100);

        Assert.Equal(["big_emp.sql", "small.sql"], Names(r));
        Assert.Empty(r.Files[0].Lines);
        Assert.Equal(2, r.SkippedLarge);
    }

    [Fact]
    public void The_default_cap_is_two_megabytes()
    {
        Assert.Equal(2 * 1024 * 1024, ScriptSearch.MaxFileBytes);
        Assert.Equal(1000, ScriptSearch.MaxMatches);
        Write("edge.sql", "emp" + new string(' ', (int)ScriptSearch.MaxFileBytes - 3));
        Write("over.sql", "emp" + new string(' ', (int)ScriptSearch.MaxFileBytes - 2));

        var r = Run("emp", content: true);

        Assert.Equal(["edge.sql"], Names(r));
        Assert.Equal(1, r.SkippedLarge);
    }

    // ---- 취소·오류 ----

    [Fact]
    public void A_cancelled_token_stops_the_search()
    {
        Write("a.sql", "emp");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => Run("emp", content: true, ct: cts.Token));
    }

    [Fact]
    public async Task Cancelling_while_files_are_being_read_stops_without_a_result()
    {
        for (var i = 0; i < 200; i++)
        {
            Write($"f{i:000}.sql", string.Join('\n', Enumerable.Repeat("select 1 from dual", 2000)));
        }

        using var cts = new CancellationTokenSource();
        var task = Task.Run(() => Run("zzz-not-found", content: true, ct: cts.Token));
        cts.Cancel();

        var ex = await Record.ExceptionAsync(() => task);
        // 취소가 먼저 닿으면 예외, 검색이 먼저 끝났으면 일치 없음. 어느 쪽이든 멈추고 일치를 지어내지 않는다.
        Assert.True(ex is null or OperationCanceledException);
    }

    [Fact]
    public void A_file_that_cannot_be_read_is_skipped_and_counted()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // 다른 프로세스 잠금(공유 거부)은 Windows 에서만 읽기 오류가 된다.
        }

        var locked = Write("locked.sql", "emp");
        Write("fine.sql", "emp");
        using var hold = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var r = Run("emp", content: true);

        Assert.Equal(["fine.sql"], Names(r));
        Assert.Equal(1, r.SkippedUnreadable);
    }

    [Fact]
    public void A_binary_file_with_a_script_extension_gives_no_content_hits()
    {
        WriteBytes("bin.sql", [0x45, 0x4D, 0x50, 0x00, 0x01, 0x02, 0x45, 0x4D, 0x50]);
        Write("text.sql", "emp");

        Assert.Equal(["text.sql"], Names(Run("emp", content: true)));
    }

    [Fact]
    public void An_empty_file_and_an_empty_folder_are_fine()
    {
        Write("blank.sql", "");
        Directory.CreateDirectory(Path.Combine(_dir, "nothing"));

        var r = Run("emp", content: true);

        Assert.Empty(r.Files);
        Assert.Equal(0, r.SkippedUnreadable);
    }

    // ---- 인코딩(편집기가 파일을 여는 것과 같은 FileEncoding) ----

    [Theory]
    [InlineData("utf-8")]
    [InlineData("utf-8-bom")]
    [InlineData("utf-16")]
    [InlineData("utf-16be")]
    [InlineData("cp949")]
    public void Korean_text_is_found_in_every_encoding_the_editor_supports(string encoding)
    {
        var text = "-- 사원 조회\nselect 이름 from 사원 where 부서 = '영업';";
        WriteBytes("한글.sql", FileEncoding.Encode(text, encoding));

        var r = Run("부서 = '영업'", content: true);

        var file = Assert.Single(r.Files);
        Assert.Equal(2, Assert.Single(file.Lines).LineNumber);
        var hit = file.Lines[0];
        Assert.Equal("부서 = '영업'", Highlighted(hit).Single());
    }

    [Fact]
    public void Korean_file_names_are_matched_by_name()
    {
        Write("사원_조회.sql", "x");
        Write("부서.sql", "x");

        Assert.Equal(["사원_조회.sql"], Names(Run("조회")));
        Assert.Equal(["사원_조회.sql"], Names(Run("사원 조회")));
    }

    [Fact]
    public void The_same_text_in_utf8_with_and_without_bom_reports_the_same_preview()
    {
        WriteBytes("a.sql", FileEncoding.Encode("select 1\nselect 한글 from dual", "utf-8"));
        WriteBytes("b.sql", FileEncoding.Encode("select 1\nselect 한글 from dual", "utf-8-bom"));

        var r = Run("한글", content: true);

        Assert.Equal(2, r.Files.Count);
        Assert.Equal(r.Files[0].Lines[0].Preview, r.Files[1].Lines[0].Preview);
        Assert.Equal(r.Files[0].Lines[0].Ranges, r.Files[1].Lines[0].Ranges);
    }
}
