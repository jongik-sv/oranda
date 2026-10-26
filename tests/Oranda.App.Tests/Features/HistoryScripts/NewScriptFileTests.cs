using Oranda.App.Features.HistoryScripts;
using Oranda.App.Features.SqlEditor;

namespace Oranda.App.Tests.Features.HistoryScripts;

// 소유: TSK-10-02. 새 스크립트 이름 검사·고유 이름·파일 만들기(덮어쓰지 않음)·대상 폴더 판정.
public sealed class NewScriptFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "oranda-newscript-" + Guid.NewGuid().ToString("N"));

    public NewScriptFileTests() => Directory.CreateDirectory(_dir);

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

    // ---- 기본 이름 ----

    [Fact]
    public void Suggested_name_is_the_default_when_the_folder_is_empty()
        => Assert.Equal("새 스크립트.sql", NewScriptFile.SuggestName(_dir));

    [Fact]
    public void Suggested_name_counts_up_while_names_collide()
    {
        File.WriteAllText(Path.Combine(_dir, "새 스크립트.sql"), "");
        Assert.Equal("새 스크립트 2.sql", NewScriptFile.SuggestName(_dir));

        File.WriteAllText(Path.Combine(_dir, "새 스크립트 2.sql"), "");
        Assert.Equal("새 스크립트 3.sql", NewScriptFile.SuggestName(_dir));
    }

    [Fact]
    public void Suggested_name_takes_the_first_free_number()
    {
        File.WriteAllText(Path.Combine(_dir, "새 스크립트.sql"), "");
        File.WriteAllText(Path.Combine(_dir, "새 스크립트 3.sql"), "");

        Assert.Equal("새 스크립트 2.sql", NewScriptFile.SuggestName(_dir));
    }

    [Fact]
    public void A_folder_with_the_default_name_also_counts_as_taken()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "새 스크립트.sql"));

        Assert.Equal("새 스크립트 2.sql", NewScriptFile.SuggestName(_dir));
    }

    // ---- 확장자 ----

    [Theory]
    [InlineData("a", "a.sql")]
    [InlineData("a.sql", "a.sql")]
    [InlineData("A.SQL", "A.SQL")]
    [InlineData("pkg.pkb", "pkg.pkb")]
    [InlineData("notes.txt", "notes.txt")]
    [InlineData("  spaced  ", "spaced.sql")]
    [InlineData("a.foo", "a.foo.sql")] // 트리에 보이지 않는 확장자는 .sql 을 더 붙인다
    [InlineData("v1.2", "v1.2.sql")]
    public void Extension_is_added_when_the_name_is_not_a_script_file(string input, string expected)
        => Assert.Equal(expected, NewScriptFile.Normalize(input));

    [Fact]
    public void Check_reports_the_final_name_with_the_extension()
    {
        var check = NewScriptFile.Check(_dir, "report");

        Assert.True(check.Ok);
        Assert.Equal("report.sql", check.FileName);
        Assert.Equal(string.Empty, check.Message);
    }

    // ---- 이름 검사 ----

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".sql")]
    public void Empty_names_are_rejected(string input)
        => Assert.Equal(NewScriptError.EmptyName, NewScriptFile.Check(_dir, input).Error);

    [Fact]
    public void Null_input_is_an_empty_name()
        => Assert.Equal(NewScriptError.EmptyName, NewScriptFile.Check(_dir, null).Error);

    [Theory]
    [InlineData("a\\b", '\\')]
    [InlineData("a/b", '/')]
    [InlineData("a:b", ':')]
    [InlineData("a*b", '*')]
    [InlineData("a?b", '?')]
    [InlineData("a\"b", '"')]
    [InlineData("a<b", '<')]
    [InlineData("a>b", '>')]
    [InlineData("a|b", '|')]
    public void Characters_not_allowed_in_file_names_are_rejected(string input, char bad)
    {
        var check = NewScriptFile.Check(_dir, input);

        Assert.Equal(NewScriptError.InvalidChar, check.Error);
        Assert.Equal(bad.ToString(), check.Detail);
        Assert.Contains(bad, check.Message);
    }

    [Fact]
    public void Control_characters_are_rejected()
    {
        var check = NewScriptFile.Check(_dir, "a\tb");

        Assert.Equal(NewScriptError.InvalidChar, check.Error);
        Assert.Equal("U+0009", check.Detail);
    }

    [Fact]
    public void A_name_ending_with_a_dot_is_rejected()
        => Assert.Equal(NewScriptError.TrailingDot, NewScriptFile.Check(_dir, "a.").Error);

    [Theory]
    [InlineData("CON")]
    [InlineData("con.sql")]
    [InlineData("Com1.sql")]
    [InlineData("LPT9")]
    [InlineData("NUL.txt")]
    [InlineData("aux.backup.sql")]
    public void Windows_reserved_names_are_rejected_under_windows_rules(string input)
    {
        var check = NewScriptFile.Check(_dir, input, windowsRules: true);

        Assert.Equal(NewScriptError.ReservedName, check.Error);
        Assert.Contains(check.Detail!, check.Message);
    }

    [Theory]
    [InlineData("COM10.sql")]
    [InlineData("CONSOLE")]
    [InlineData("my.con.sql")]
    [InlineData("CON1")]
    public void Names_that_only_look_reserved_are_fine(string input)
        => Assert.True(NewScriptFile.Check(_dir, input, windowsRules: true).Ok);

    [Fact]
    public void Reserved_names_are_not_a_problem_without_windows_rules()
        => Assert.True(NewScriptFile.Check(_dir, "CON", windowsRules: false).Ok);

    [Fact]
    public void A_name_over_255_characters_is_rejected_and_255_is_fine()
    {
        Assert.Equal(NewScriptError.TooLong, NewScriptFile.Check(_dir, new string('a', 252) + ".sql").Error);
        Assert.True(NewScriptFile.Check(_dir, new string('a', 251) + ".sql").Ok);
    }

    [Fact]
    public void An_existing_file_is_reported_without_touching_it()
    {
        var path = Path.Combine(_dir, "a.sql");
        File.WriteAllText(path, "keep");

        var check = NewScriptFile.Check(_dir, "a");

        Assert.Equal(NewScriptError.AlreadyExists, check.Error);
        Assert.Equal("a.sql", check.FileName);
        Assert.Equal("keep", File.ReadAllText(path));
    }

    [Fact]
    public void An_existing_folder_with_the_same_name_counts_as_taken()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "a.sql"));

        Assert.Equal(NewScriptError.AlreadyExists, NewScriptFile.Check(_dir, "a.sql").Error);
    }

    [Fact]
    public void A_missing_folder_is_reported()
        => Assert.Equal(NewScriptError.FolderMissing, NewScriptFile.Check(Path.Combine(_dir, "nope"), "a").Error);

    // ---- 만들기 ----

    [Fact]
    public void Create_makes_an_empty_file_and_returns_its_path()
    {
        var result = NewScriptFile.Create(_dir, "a");

        Assert.True(result.Ok);
        Assert.Equal(Path.Combine(_dir, "a.sql"), result.Path);
        Assert.True(File.Exists(result.Path));
        Assert.Equal(0, new FileInfo(result.Path!).Length);
    }

    [Fact]
    public void Create_fills_the_text_as_utf8_without_a_bom_that_the_editor_reads_back()
    {
        const string text = "select '한글' from dual;\r\n-- 끝";

        var result = NewScriptFile.Create(_dir, "kr.sql", text);

        var bytes = File.ReadAllBytes(result.Path!);
        Assert.NotEqual(0xEF, bytes[0]); // BOM 없음
        var (decoded, encoding) = FileEncoding.Decode(bytes);
        Assert.Equal(text, decoded);
        Assert.Equal("utf-8", encoding);
    }

    [Fact]
    public void Create_never_overwrites_an_existing_file()
    {
        var path = Path.Combine(_dir, "a.sql");
        File.WriteAllText(path, "original");

        var result = NewScriptFile.Create(_dir, "a.sql", "replacement");

        Assert.False(result.Ok);
        Assert.Equal(NewScriptError.AlreadyExists, result.Error);
        Assert.Equal("original", File.ReadAllText(path));
    }

    [Fact]
    public void Creating_the_same_name_twice_fails_the_second_time_and_keeps_the_first_content()
    {
        Assert.True(NewScriptFile.Create(_dir, "twice", "first").Ok);

        var second = NewScriptFile.Create(_dir, "twice", "second");

        Assert.Equal(NewScriptError.AlreadyExists, second.Error);
        Assert.Equal("first", File.ReadAllText(Path.Combine(_dir, "twice.sql")));
    }

    [Fact]
    public void Create_rejects_a_bad_name_without_creating_anything()
    {
        var result = NewScriptFile.Create(_dir, "a|b");

        Assert.Equal(NewScriptError.InvalidChar, result.Error);
        Assert.Null(result.Path);
        Assert.Empty(Directory.GetFileSystemEntries(_dir));
    }

    [Fact]
    public void Create_that_races_with_another_writer_reports_exists_and_leaves_the_other_file()
    {
        var path = Path.Combine(_dir, "race.sql");

        // 검사 뒤에 다른 곳이 먼저 만든 경우: 열기 단계에서 IOException (CreateNew 와 같은 동작).
        var result = NewScriptFile.Create(_dir, "race", null, null, p =>
        {
            File.WriteAllText(p, "other");
            throw new IOException("already exists");
        });

        Assert.Equal(NewScriptError.AlreadyExists, result.Error);
        Assert.Equal("other", File.ReadAllText(path));
    }

    [Fact]
    public void Create_reports_a_missing_write_permission_and_leaves_nothing_behind()
    {
        var result = NewScriptFile.Create(_dir, "denied", null, null, _ => throw new UnauthorizedAccessException());

        Assert.Equal(NewScriptError.NoPermission, result.Error);
        Assert.Empty(Directory.GetFileSystemEntries(_dir));
        Assert.Contains("권한", result.Message);
    }

    [Fact]
    public void Create_removes_the_half_written_file_when_writing_fails()
    {
        var path = Path.Combine(_dir, "half.sql");

        var result = NewScriptFile.Create(_dir, "half", "text", null, p => new FailingStream(new FileStream(p, FileMode.CreateNew, FileAccess.Write)));

        Assert.Equal(NewScriptError.Failed, result.Error);
        Assert.Contains("disk full", result.Message);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Create_in_a_folder_that_vanished_reports_the_folder()
    {
        var result = NewScriptFile.Create(_dir, "gone", null, null, _ => throw new DirectoryNotFoundException());

        Assert.Equal(NewScriptError.FolderMissing, result.Error);
    }

    [Fact]
    public void Describe_has_a_text_for_every_error()
    {
        Assert.Equal(string.Empty, NewScriptFile.Describe(NewScriptError.None));
        foreach (var error in Enum.GetValues<NewScriptError>().Where(e => e != NewScriptError.None))
        {
            Assert.False(string.IsNullOrWhiteSpace(NewScriptFile.Describe(error, "x")), error.ToString());
        }
    }

    // ---- 대상 폴더 ----

    [Fact]
    public void A_selected_folder_is_the_target()
        => Assert.Equal(@"C:\work\sql\dev", NewScriptFile.ResolveTargetFolder(@"C:\work\sql\dev", true, [@"C:\work\sql", @"D:\other"]));

    [Fact]
    public void A_selected_file_uses_its_folder()
    {
        var file = Path.Combine(_dir, "sub", "a.sql");

        Assert.Equal(Path.Combine(_dir, "sub"), NewScriptFile.ResolveTargetFolder(file, false, [_dir, "x"]));
    }

    [Fact]
    public void No_selection_with_one_root_uses_that_root()
        => Assert.Equal(_dir, NewScriptFile.ResolveTargetFolder(null, false, [_dir]));

    [Fact]
    public void No_selection_with_several_roots_has_no_target()
        => Assert.Null(NewScriptFile.ResolveTargetFolder(null, false, [_dir, "other"]));

    [Fact]
    public void No_selection_and_no_roots_has_no_target()
        => Assert.Null(NewScriptFile.ResolveTargetFolder(null, false, []));

    [Fact]
    public void An_empty_path_selection_such_as_the_expand_placeholder_counts_as_no_selection()
    {
        Assert.Equal(_dir, NewScriptFile.ResolveTargetFolder(string.Empty, false, [_dir]));
        Assert.Null(NewScriptFile.ResolveTargetFolder(string.Empty, false, [_dir, "other"]));
    }

    /// <summary>쓰는 순간 실패하는 스트림(디스크가 가득 찬 경우를 흉내 낸다).</summary>
    private sealed class FailingStream(Stream inner) : Stream
    {
        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new IOException("disk full");

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
