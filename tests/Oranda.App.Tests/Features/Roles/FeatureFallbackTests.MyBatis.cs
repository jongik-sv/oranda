using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Oranda.App.Features.GridEdit;
using Oranda.App.Features.MyBatisWorkspace.Hooks;
using Oranda.App.Features.RecentFiles;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Tests.Features.SqlEditor;
using Oranda.Core.Shell;
using Oranda.Testing.MyBatis;

namespace Oranda.App.Tests.Features.Roles;

// MyBatis 끔(훅 = MyBatisHooks.Empty, D-008): 파일 열기·최근 파일·끌어 놓기·명령줄이 매퍼를 문장마다 일반 SQL 탭으로 펼치고 매퍼 파일에는 쓰지 않는다.
public sealed partial class FeatureFallbackTests
{
    private const string MyBatisNotice = "MyBatis 기능이 꺼져 있어 매퍼를 일반 SQL 탭으로 펼쳐 열었습니다(매퍼 파일에는 저장하지 않습니다)";

    [Fact]
    public Task MyBatis_off_file_open_expands_the_mapper_into_sql_tabs_and_saving_never_touches_the_mapper() => Ui.Run(async () =>
    {
        using var dir = new TempDir();
        var mapper = dir.Write("EmpMapper.xml", MapperSamples.EmpMapper);
        var hash = Hash(mapper);
        var statements = Regex.Count(MapperSamples.EmpMapper, "<(select|insert|update|delete)\\b");
        var dialogs = new SaveDialogs(dir.Path);
        var host = new SqlEditorHost(null, dialogs) { MyBatisHooks = MyBatisHooks.Empty };
        var handler = new SqlEditorCommandHandler { Host = host };
        SaveFeedback.Clear();

        // 「파일 열기」(최근 파일도 같은 명령으로 간다).
        Assert.Equal(RecentKind.File, RecentFilesService.KindOf(mapper));
        await handler.ExecuteAsync(new CommandContext("oranda.file.open", AlwaysKeys.Instance, mapper), default);

        var ws = Assert.Single(host.Workspaces);
        Assert.Equal(Path.GetFullPath(mapper), ws.SourcePath);
        Assert.Equal(SqlWorkspaceKind.Sql, ws.DocumentKind); // MyBatis 문서가 아니다(세션 없음)
        Assert.Equal(statements, ws.Tabs.Count);
        Assert.All(ws.Tabs, t => Assert.Null(t.FilePath));
        Assert.Contains(ws.Tabs, t => t.Title == "selectEmp" && t.Text.Contains(":empNo", StringComparison.Ordinal) && !t.Text.Contains("#{", StringComparison.Ordinal));
        Assert.Equal(MyBatisNotice, SaveFeedback.Current?.Text);
        Assert.Null(host.WorkspaceFilePath); // 매퍼는 읽기 전용 원본: 작업 공간 저장은 경로를 묻는다

        // 고치고 저장: 탭은 새 .sql 로, 작업 공간은 .workspace.md 로 간다. 매퍼 바이트는 그대로.
        var tab = ws.Tabs[0];
        tab.Document.Insert(0, "-- 고침\n");
        Assert.True(await host.SaveAsync(tab, default));
        Assert.True(await host.SaveWorkspaceFileAsync(askPath: false, default));
        Assert.True(await host.SaveAllAsync(default));
        Assert.Equal(hash, Hash(mapper));
        Assert.All(dialogs.Saved, p => Assert.NotEqual(Path.GetFullPath(mapper), Path.GetFullPath(p)));
        Assert.True(File.Exists(Path.Combine(dir.Path, "picked.workspace.md")));

        Assert.Equal(Path.Combine(dir.Path, "picked.workspace.md"), ws.SourcePath); // 저장한 작업 공간은 이제 .md 에 묶인다

        // 매퍼를 다시 열면 새 펼침 작업 공간, 한 번 더 열면 그리로 옮겨 간다. 안내는 처음 한 번뿐이다.
        SaveFeedback.Clear();
        Assert.NotNull(await host.OpenFileAsync(mapper, default));
        Assert.Equal(2, host.Workspaces.Count);
        Assert.NotNull(await host.OpenFileAsync(mapper, default));
        Assert.Equal(2, host.Workspaces.Count);
        Assert.Equal(Path.GetFullPath(mapper), host.ActiveWorkspace?.SourcePath);
        Assert.Null(SaveFeedback.Current);
        Assert.Equal(hash, Hash(mapper));
    });

    [Fact]
    public Task MyBatis_off_drop_and_command_line_open_only_mappers_and_leave_other_xml_alone() => Ui.Run(async () =>
    {
        using var dir = new TempDir();
        var mapper = dir.Write("EmpMapper.xml", MapperSamples.EmpMapper);
        var pom = dir.Write("pom.xml", "<?xml version=\"1.0\"?><project><modelVersion>4.0.0</modelVersion></project>");
        var broken = dir.Write("broken.xml", "<mapper namespace=\"x\"><select id=\"a\">");
        var host = new SqlEditorHost { MyBatisHooks = MyBatisHooks.Empty };

        // 주 창의 끌어 놓기·명령줄 인자(App.axaml.cs)가 쓰는 길.
        Assert.Equal([Path.GetFullPath(mapper)], DroppedWorkspaceFiles.Pick([pom, mapper, Path.Combine(dir.Path, "없음.xml"), null, mapper]));
        Assert.True(await DroppedWorkspaceFiles.OpenAsync(host, [pom, mapper], default));
        var ws = Assert.Single(host.Workspaces);
        Assert.NotEmpty(ws.Tabs);
        Assert.False(await DroppedWorkspaceFiles.OpenAsync(new SqlEditorHost(), [pom], default));

        // 루트가 mapper 가 아니거나 깨진 .xml 은 지금처럼 글자 탭이다(펼치지 않는다).
        Assert.False(WorkspaceFiles.IsMapperFile(pom));
        // 깨진 매퍼는 매퍼로 보고 펼치려다 알림으로 실패를 보인다(글자 탭으로 열면 저장이 매퍼를 덮어쓴다).
        Assert.True(WorkspaceFiles.IsMapperFile(broken));
        var tabsBefore = host.Tabs.Count;
        await host.OpenFileAsync(broken, default);
        Assert.Equal(tabsBefore, host.Tabs.Count);
        Assert.DoesNotContain(host.Tabs, t => t.FilePath == Path.GetFullPath(broken));
        var text = await host.OpenFileAsync(pom, default);
        Assert.Equal(Path.GetFullPath(pom), text!.FilePath);
        Assert.Single(host.Workspaces, w => w.SourcePath is not null);
        Assert.False(WorkspaceFiles.IsMapperFile(null));
        Assert.False(WorkspaceFiles.IsMapperFile(Path.Combine(dir.Path, "없음.xml")));
        Assert.False(WorkspaceFiles.IsMapperFile(dir.Write("a.sql", "<mapper/>")));
    });

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "oranda-fallback-" + Guid.NewGuid().ToString("N"));

        public TempDir() => Directory.CreateDirectory(Path);

        public string Write(string name, string text)
        {
            var file = System.IO.Path.Combine(Path, name);
            File.WriteAllText(file, text);
            return file;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>저장 경로를 물으면 시험 폴더의 새 파일을 준다(작업 공간은 picked.workspace.md, 탭은 picked-n.sql). 준 경로를 남긴다.</summary>
    private sealed class SaveDialogs(string dir) : IEditorDialogs
    {
        public List<string> Saved { get; } = [];

        public Task<string?> PickOpenFileAsync(CancellationToken ct) => Task.FromResult<string?>(null);

        public Task<string?> PickSaveFileAsync(string suggestedName, CancellationToken ct)
        {
            var path = System.IO.Path.Combine(dir, $"picked-{Saved.Count + 1}.sql");
            Saved.Add(path);
            return Task.FromResult<string?>(path);
        }

        public Task<string?> PickWorkspaceFileAsync(bool save, string suggestedName, CancellationToken ct)
        {
            var path = System.IO.Path.Combine(dir, "picked.workspace.md");
            Saved.Add(path);
            return Task.FromResult<string?>(save ? path : null);
        }

        public Task<string?> PromptAsync(string title, string initial, CancellationToken ct) => Task.FromResult<string?>(null);

        public Task<string?> ChooseAsync(string title, IReadOnlyList<string> items, CancellationToken ct) => Task.FromResult<string?>(null);

        public Task<bool> ConfirmAsync(string message, CancellationToken ct) => Task.FromResult(false);

        public Task<bool> PreviewEditAsync(string title, string before, string after, CancellationToken ct) => Task.FromResult(false);
    }
}
