using Oranda.App.Features.MyBatisWorkspace.Conversion;
using Oranda.App.Features.MyBatisWorkspace.Hooks;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Tests.Features.MyBatisWorkspace.Fakes;
using Oranda.App.Tests.Features.SqlEditor;
using Oranda.Core.Execution;
using Oranda.Language.MyBatis;
using Oranda.Platform.MyBatis;
using Oranda.Testing.MyBatis;

namespace Oranda.App.Tests.Features.MyBatisWorkspace;

/// <summary>TSK-03-05 J4: 바꾸기 흐름(대화 상자 결과 → 쓰기 → 열기), 대상 매퍼가 열려 있을 때(조정자 답 Q2), 원래 작업 공간 그대로.</summary>
public sealed class ConvertCommandTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "oranda-convertcmd-" + Guid.NewGuid().ToString("N"));

    public ConvertCommandTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>탭 2개(조회·수정)짜리 일반 작업 공간과 가짜 열기 훅을 가진 호스트.</summary>
    private static (SqlEditorHost Host, SqlWorkspaceViewModel Ws, RecordingOpen Open, MessageDialogs Dialogs) NewHost()
    {
        var open = new RecordingOpen();
        var dialogs = new MessageDialogs();
        var host = new SqlEditorHost(null, dialogs) { MyBatisHooks = new MyBatisHooks(openHandler: open) };
        var ws = host.EnsureWorkspace();
        var select = host.NewDocument("SELECT * FROM EMP WHERE DEPTNO = :deptNo");
        select.Title = "사원 목록";
        select.Binds.Add("DEPTNO").Type = BindDataType.Number;
        select.Binds.Find("DEPTNO")!.Text = "10";
        var update = host.NewDocument("UPDATE EMP SET SAL = :sal WHERE EMPNO = :empNo");
        update.Title = "급여 바꾸기";
        return (host, ws, open, dialogs);
    }

    /// <summary>대화 상자 대신: 대상을 정하고, 기존 매퍼와 겹쳐 표시된 id 는 사용자처럼 고친 뒤 받아들인다.</summary>
    private static Func<ConvertDialogViewModel, Task<ConvertDialogResult?>> Accept(ConvertTargetKind target, string path, string ns = "demo.EmpMapper") => async vm =>
    {
        vm.Target = target;
        vm.Path = path;
        vm.Namespace = ns;
        await vm.ExistingReadTask; // 기존 매퍼 읽기(디바운스·백그라운드)가 끝나야 겹친 id 가 표시된다
        foreach (var line in vm.Lines.Where(l => l.IdError is not null))
        {
            line.Id += "New";
        }

        return vm.TryBuildResult();
    };

    [Fact]
    public Task New_mapper_is_written_with_a_first_scenario_and_opened_while_the_original_workspace_is_unchanged() => Ui.Run(async () =>
    {
        var (host, ws, open, dialogs) = NewHost();
        var before = ws.Tabs.Select(t => (t.Title, t.Text)).ToList();
        var store = new FakeMyBatisScenarioStore();
        var path = Path.Combine(_dir, "EmpMapper.xml");

        await MyBatisConvertCommands.RunAsync(host, ws, Accept(ConvertTargetKind.NewFile, path), new ConvertWriter(new MapperFileWriter(), store), null, null, default);

        Assert.Empty(dialogs.Messages);
        Assert.Equal([Path.GetFullPath(path)], open.Paths);
        var doc = new MapperParser().Parse(await File.ReadAllBytesAsync(path));
        Assert.Equal("demo.EmpMapper", doc.MapperNamespace);
        Assert.Equal([MyBatisElementKind.Select, MyBatisElementKind.Update], doc.Elements.Select(e => e.Kind));
        var scenario = Assert.Single(store.All, s => s.StatementId == doc.Elements[0].Id);
        Assert.Equal("10", scenario.Values["deptNo"].Text);
        Assert.Equal(before, ws.Tabs.Select(t => (t.Title, t.Text)).ToList());
        Assert.Null(ws.MyBatis);
    });

    [Fact]
    public Task Appending_to_an_open_mapper_with_unsaved_edits_is_refused_and_the_file_is_untouched() => Ui.Run(async () =>
    {
        var (host, ws, open, dialogs) = NewHost();
        var path = Path.GetFullPath(Path.Combine(_dir, "Emp.xml"));
        await File.WriteAllTextAsync(path, MapperSamples.EmpMapper);
        var mapper = host.Restore(new Oranda.Core.Workspaces.Workspace { Id = "emp-mapper", Name = "Emp", SavedAt = DateTimeOffset.UtcNow });
        mapper.SourcePath = path;
        mapper.MyBatis = new FakeMyBatisWorkspaceSession { MapperPath = path, IsDirty = true };

        await MyBatisConvertCommands.RunAsync(host, ws, Accept(ConvertTargetKind.ExistingMapper, path), new ConvertWriter(new MapperFileWriter(), null), null, null, default);

        Assert.Single(dialogs.Messages);
        Assert.Empty(open.Paths);
        Assert.Equal(MapperSamples.EmpMapper, await File.ReadAllTextAsync(path));

        // 저장 안 한 고침이 없으면 막지 않고 쓴 뒤 그 매퍼를 연다(다시 읽기는 03-03 감시 몫).
        ((FakeMyBatisWorkspaceSession)mapper.MyBatis).IsDirty = false;
        await MyBatisConvertCommands.RunAsync(host, ws, Accept(ConvertTargetKind.ExistingMapper, path), new ConvertWriter(new MapperFileWriter(), null), null, null, default);

        Assert.Single(dialogs.Messages);
        Assert.Same(mapper, host.ActiveWorkspace); // 이미 열린 매퍼는 호스트가 그 작업 공간을 앞으로 가져온다
        Assert.Contains("selectEmp2", await File.ReadAllTextAsync(path), StringComparison.Ordinal);
        Assert.StartsWith(MapperSamples.EmpMapper[..MapperSamples.EmpMapper.LastIndexOf("</mapper>", StringComparison.Ordinal)], await File.ReadAllTextAsync(path), StringComparison.Ordinal);
    });

    [Fact]
    public void Suggested_namespace_uses_ascii_words_and_falls_back_to_Mapper()
    {
        Assert.Equal("OrderApiMapper", MyBatisConvertCommands.SuggestNamespace("order api"));
        Assert.Equal("Mapper", MyBatisConvertCommands.SuggestNamespace("주문조회"));
    }

    private sealed class RecordingOpen : IMyBatisOpenHandler
    {
        public List<string> Paths { get; } = [];

        public Task<bool> TryOpenAsync(SqlEditorHost host, string fullPath, CancellationToken ct)
        {
            Paths.Add(fullPath);
            return Task.FromResult(true);
        }
    }

    private sealed class MessageDialogs : IEditorDialogs
    {
        public List<string> Messages { get; } = [];

        public Task<string?> PickOpenFileAsync(CancellationToken ct) => Task.FromResult<string?>(null);

        public Task<string?> PickSaveFileAsync(string suggestedName, CancellationToken ct) => Task.FromResult<string?>(null);

        public Task<string?> PromptAsync(string title, string initial, CancellationToken ct) => Task.FromResult<string?>(null);

        public Task<string?> ChooseAsync(string title, IReadOnlyList<string> items, CancellationToken ct) => Task.FromResult<string?>(null);

        public Task<bool> ConfirmAsync(string message, CancellationToken ct) => Task.FromResult(false);

        public Task<bool> PreviewEditAsync(string title, string before, string after, CancellationToken ct) => Task.FromResult(false);

        public Task<SaveChoice> AskSaveChangesAsync(string tabName, CancellationToken ct) => Task.FromResult(SaveChoice.Cancel);

        public Task ShowMessageAsync(string title, string message, CancellationToken ct)
        {
            Messages.Add(message);
            return Task.CompletedTask;
        }
    }
}
