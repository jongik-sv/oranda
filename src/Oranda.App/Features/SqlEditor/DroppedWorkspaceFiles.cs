namespace Oranda.App.Features.SqlEditor;

/// <summary>
/// 끌어 놓은 파일과 명령줄 인자 가운데 Orange 작업 공간(.sqd)과 MyBatis 매퍼(.xml)를 골라 작업 공간으로 연다(EDIT-09).
/// MyBatis 열기 훅이 있으면 .xml 은 훅이 연다(docs/oranda1/design.md 9장, 구현 TSK-03-01). 훅이 없으면(MyBatis 기능 끔, D-008)
/// 루트가 <c>mapper</c> 인 .xml 만 골라 문장마다 일반 SQL 탭으로 펼쳐 연다(매퍼 파일에는 쓰지 않는다). 그 밖의 .xml 은 고르지 않는다.
/// 같은 파일이 이미 열려 있으면 그 작업 공간으로 옮겨 간다.
/// </summary>
public static class DroppedWorkspaceFiles
{
    /// <summary>경로 가운데 .sqd, 그리고 루트가 mapper 인 .xml 로 파일이 있는 것(순서 유지, 중복 뺌). 주 창의 끌어 놓기가 쓴다.</summary>
    public static IReadOnlyList<string> Pick(IEnumerable<string?>? paths) => Pick(paths, includeMyBatis: false);

    /// <summary>위와 같고, <paramref name="includeMyBatis"/> 면 루트와 상관없이 .xml 을 모두 고른다(훅이 매퍼인지 가린다).</summary>
    public static IReadOnlyList<string> Pick(IEnumerable<string?>? paths, bool includeMyBatis) =>
        (paths ?? [])
            .Where(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p)
                && (WorkspaceFiles.IsSqd(p) || (WorkspaceFiles.IsMyBatis(p) && (includeMyBatis || WorkspaceFiles.IsMapperFile(p)))))
            .Select(p => Path.GetFullPath(p!))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>골라 낸 파일을 차례로 연다. 열 것이 없으면 false. 읽기 오류는 알림으로 보인다.</summary>
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
