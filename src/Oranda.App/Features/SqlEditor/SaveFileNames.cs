namespace Oranda.App.Features.SqlEditor;

/// <summary>
/// 저장 대화상자에 보여 줄 제안 이름과 돌려받은 경로를 다듬는다. 탭 이름이 이미 확장자를 갖고 있으면
/// 기본 확장자를 또 붙이지 않는다(findEmp.sql → findEmp.sql.sql 방지).
/// </summary>
internal static class SaveFileNames
{
    private static readonly string[] SqlExtensions = [".sql", ".pls", ".pks", ".pkb", ".prc", ".fnc", ".trg", ".vw", ".ddl"];

    private static readonly string[] WorkspaceExtensions = [WorkspaceFiles.Extension];

    private static readonly char[] Invalid = ['/', '\\', ':', '*', '?', '"', '<', '>', '|'];

    /// <summary>SQL 탭의 제안 이름. SQL 계열 확장자로 끝나면 그대로, 아니면 .sql 을 붙인다.</summary>
    public static string SuggestSql(string title) => Suggest(title, SqlExtensions, ".sql");

    /// <summary>작업 공간 파일의 제안 이름. .workspace.md 로 끝나면 그대로, 아니면 .workspace.md 를 붙인다(D-028).</summary>
    public static string SuggestWorkspace(string name) => Suggest(name, WorkspaceExtensions, WorkspaceFiles.Extension);

    /// <summary>
    /// 피커가 제안 이름에 기본 확장자를 겹쳐 붙여 돌려준 경우(제안 「x.sql」 → 「x.sql.sql」)만 한 번으로 정리한다.
    /// 사용자가 제안과 다른 이름을 직접 입력한 경우는 손대지 않는다.
    /// </summary>
    public static string? CollapseDoubled(string? path, string suggestedName, string defaultExtension)
    {
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(defaultExtension))
        {
            return path;
        }

        var ext = "." + defaultExtension.TrimStart('.');
        if (!suggestedName.EndsWith(ext, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetFileName(path), suggestedName + ext, StringComparison.OrdinalIgnoreCase))
        {
            return path;
        }

        return path[..^ext.Length];
    }

    private static string Suggest(string title, string[] extensions, string fallback)
    {
        var name = title;
        foreach (var c in Invalid)
        {
            name = name.Replace(c, '_');
        }

        return extensions.Any(e => name.EndsWith(e, StringComparison.OrdinalIgnoreCase)) ? name : name + fallback;
    }
}
