using System.Globalization;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Tests.Shell;

namespace Oranda.App.Tests.Features.SqlEditor;

/// <summary>TSK-13-19: 파일 메뉴의 작업 공간 항목 정리(파일 기반 세 항목만 메뉴에 두고 저장소 항목은 팔레트에만 남긴다).</summary>
public sealed partial class SqlEditorTests
{
    private static readonly string[] WorkspaceFileMenuIds = ["oranda.file.openWorkspaceFile", "oranda.file.saveWorkspaceFile", "oranda.file.saveWorkspaceFileAs"];

    [Fact]
    public void File_menu_has_three_workspace_file_items_and_the_store_items_stay_in_the_palette_only()
    {
        var commands = ShellTestHelpers.AppContributions.Commands.ToDictionary(c => c.Definition.Id);
        string[] FileMenu(string id) => commands[id].Definition.Placements.Where(p => p.StartsWith("menu:file/", StringComparison.Ordinal)).ToArray();

        var items = WorkspaceFileMenuIds
            .Select(id => (id, order: int.Parse(FileMenu(id).Single().Split('@')[1], CultureInfo.InvariantCulture)))
            .ToArray();
        Assert.Equal(items.Select(i => i.id), items.OrderBy(i => i.order).Select(i => i.id)); // 열기 → 저장 → 다른 이름으로 저장

        // 저장소 항목: 명령 ID 는 남고(키맵·이전 호출 호환) 메뉴 배치만 없다.
        Assert.Empty(FileMenu("oranda.file.saveWorkspace"));
        Assert.Empty(FileMenu("oranda.file.openWorkspace"));
        Assert.Contains("oranda.file.saveWorkspace", commands.Keys);
        Assert.Contains("oranda.file.openWorkspace", commands.Keys);
    }

    [Fact]
    public Task Save_workspace_writes_the_bound_file_directly_and_asks_for_a_name_when_unbound() => Ui.Run(async () =>
    {
        Directory.CreateDirectory(_root);
        var dialogs = new FakeDialogs { SavePath = Path.Combine(_root, "묶음.md") };
        var host = new SqlEditorHost(NewStore(), dialogs);
        host.NewDocument("select 1 from dual");

        await Run(host, "oranda.file.saveWorkspaceFile"); // 묶인 파일 없음 → 이름을 묻는다
        Assert.Equal(1, dialogs.SaveCalls);
        var bound = Path.Combine(_root, "묶음.workspace.md");
        Assert.Equal(Path.GetFullPath(bound), host.WorkspaceFilePath);

        host.Active!.Document.Text = "select 2 from dual";
        await Run(host, "oranda.file.saveWorkspaceFile"); // 묶인 파일 있음 → 묻지 않고 그 파일에 쓴다
        Assert.Equal(1, dialogs.SaveCalls);
        Assert.Contains("select 2 from dual", await File.ReadAllTextAsync(bound), StringComparison.Ordinal);

        dialogs.SavePath = Path.Combine(_root, "다른.md");
        await Run(host, "oranda.file.saveWorkspaceFileAs"); // 다른 이름으로 저장 → 묶인 파일이 있어도 묻는다
        Assert.Equal(2, dialogs.SaveCalls);
        Assert.True(File.Exists(Path.Combine(_root, "다른.workspace.md")));
    });
}
