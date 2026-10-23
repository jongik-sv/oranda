using Oranda.App.Features.SqlEditor;

namespace Oranda.App.Features.MyBatisWorkspace.Folder;

/// <summary>매퍼 폴더에서 고른 파일·문장을 MyBatis 작업 공간으로 연다(열기 훅을 그대로 쓴다: 같은 파일이면 그 작업 공간으로 간다).</summary>
internal static class MapperFolderOpener
{
    /// <summary>매퍼를 연다. 열기 훅이 없거나 열지 못하면 false.</summary>
    public static async Task<bool> OpenMapperAsync(SqlEditorHost host, string mapperPath, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentException.ThrowIfNullOrEmpty(mapperPath);
        return host.MyBatisHooks.OpenHandler is { } open
            && await open.TryOpenAsync(host, Path.GetFullPath(mapperPath), ct).ConfigureAwait(true);
    }

    /// <summary>매퍼를 열고 문장 탭을 연다. 매퍼를 열지 못하거나 그 id 가 없으면 false.</summary>
    public static async Task<bool> OpenStatementAsync(SqlEditorHost host, string mapperPath, string statementId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(statementId);
        if (!await OpenMapperAsync(host, mapperPath, ct).ConfigureAwait(true))
        {
            return false;
        }

        var full = Path.GetFullPath(mapperPath);
        var session = host.Workspaces.FirstOrDefault(w => string.Equals(w.SourcePath, full, StringComparison.OrdinalIgnoreCase))?.MyBatis;
        return session?.OpenStatement(statementId) is not null;
    }
}
