namespace Oranda.App.Features.SqlEditor;

/// <summary>
/// 끌어 놓은 파일과 명령줄 인자 가운데 Orange 작업 공간(.sqd)을 골라 작업 공간으로 연다(EDIT-09).
/// MyBatis 열기 훅이 있으면 매퍼(.xml)도 고른다(docs/oranda1/design.md 9장, 구현 TSK-03-01). 훅이 없으면 지금처럼 .sqd 만.
/// 같은 파일이 이미 열려 있으면 그 작업 공간으로 옮겨 간다.
/// </summary>
public static class DroppedWorkspaceFiles
{
    /// <summary>경로 가운데 .sqd 로 끝나고 파일이 있는 것(순서 유지, 중복 뺌).</summary>
    public static IReadOnlyList<string> Pick(IEnumerable<string?>? paths) => Pick(paths, includeMyBatis: false);

    /// <summary>위와 같고, <paramref name="includeMyBatis"/> 면 .xml 도 고른다.</summary>
    public static IReadOnlyList<string> Pick(IEnumerable<string?>? paths, bool includeMyBatis) =>
        (paths ?? [])
            .Where(p => !string.IsNullOrWhiteSpace(p) && (WorkspaceFiles.IsSqd(p) || (includeMyBatis && WorkspaceFiles.IsMyBatis(p))) && File.Exists(p))
            .Select(p => Path.GetFullPath(p!))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>골라 낸 .sqd 를 차례로 연다. 열 것이 없으면 false. 읽기 오류는 알림으로 보인다.</summary>
    public static async Task<bool> OpenAsync(SqlEditorHost host, IEnumerable<string?>? paths, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(host);
        var files = Pick(paths, includeMyBatis: host.MyBatisHooks.OpenHandler is not null);
        foreach (var file in files)
        {
            await SqlEditorCommandHandler.OpenWorkspaceFileAsync(host, file, ct).ConfigureAwait(true);
        }

        return files.Count > 0;
    }
}
