using Oranda.App.Features.SqlEditor;
using Oranda.Platform.Storage;

namespace Oranda.App.Tests.Features.SqlEditor;

/// <summary>작업 공간 메뉴가 보이는 저장된 작업 공간 ID 목록.</summary>
public sealed class SqlEditorDockWiringTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "oranda-dw-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void Saved_workspace_ids_skip_names_that_are_not_a_valid_id()
    {
        var paths = new AppDataPaths(_root);
        Directory.CreateDirectory(paths.WorkspacesDirectory);
        File.WriteAllText(Path.Combine(paths.WorkspacesDirectory, "work-1.json"), "{}");
        File.WriteAllText(Path.Combine(paths.WorkspacesDirectory, "Upper.json"), "{}");
        try
        {
            // 끝에 줄바꿈이 붙은 이름(정규식 끝의 $ 가 통과시키던 모양). 줄바꿈을 못 쓰는 파일 시스템이면 이 줄은 건너뛴다.
            File.WriteAllText(Path.Combine(paths.WorkspacesDirectory, "work-2\n.json"), "{}");
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
        }

        var wiring = new SqlEditorDockWiring(null!, null!, null!, null!, null!, paths);

        Assert.Equal(["work-1"], wiring.ListSavedWorkspaceIds());
    }
}
