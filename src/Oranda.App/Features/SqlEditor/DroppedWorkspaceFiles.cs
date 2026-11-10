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

    /// <summary>끌어 놓은 파일이 어디로 가는가: 작업 공간(.sqd·매퍼 .xml) 또는 「파일 열기」와 같은 SQL 파일 열기.</summary>
    public enum DropKind
    {
        Workspace,
        Sql,
    }

    /// <summary>열 수 있는 파일이 하나도 없을 때 상태 표시줄에 보이는 안내(한국어 글이 literals-en.json 의 열쇠).</summary>
    internal const string UnsupportedNotice = "끌어 놓은 파일은 열 수 없는 형식입니다(SQL·텍스트·작업 공간 파일만 열 수 있습니다)";

    /// <summary>다른 형식만 끌어 놓았다는 안내를 상태 표시줄에 한 줄 보인다(영어 화면이면 영어).</summary>
    public static void ShowUnsupportedNotice()
    {
        var text = Localization.LiteralTranslator.IsEnglish && Localization.LiteralTranslator.Table.TryGetValue(UnsupportedNotice, out var english)
            ? english
            : UnsupportedNotice;
        GridEdit.SaveFeedback.Show(new GridEdit.SaveNotice(text, GridEdit.SaveFeedbackKind.Success));
    }

    /// <summary>파일 열기 대화상자가 SQL 파일로 받는 확장자인가(.sql .pls .pks .pkb .plb .prc .fnc .trg .txt).</summary>
    public static bool IsSqlFile(string path) =>
        AvaloniaEditorDialogs.SqlFileExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 끌어 놓은 경로를 놓은 순서대로 갈래를 나눈다(없는 파일·빈 경로·중복은 뺀다). <see cref="Pick(IEnumerable{string?}?, bool)"/> 가 고르는 것은
    /// <see cref="DropKind.Workspace"/>, 그 밖에 SQL 확장자인 것은 <see cref="DropKind.Sql"/>. <c>Ignored</c> 는 둘 다 아닌 (있는) 파일 수.
    /// </summary>
    public static (IReadOnlyList<(string Path, DropKind Kind)> Files, int Ignored) Classify(IEnumerable<string?>? paths, bool includeMyBatis)
    {
        var files = new List<(string Path, DropKind Kind)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ignored = 0;
        foreach (var p in paths ?? [])
        {
            if (string.IsNullOrWhiteSpace(p) || !File.Exists(p) || !seen.Add(Path.GetFullPath(p)))
            {
                continue;
            }

            var full = Path.GetFullPath(p);
            if (Pick([full], includeMyBatis).Count > 0)
            {
                files.Add((full, DropKind.Workspace));
            }
            else if (IsSqlFile(full))
            {
                files.Add((full, DropKind.Sql));
            }
            else
            {
                ignored++;
            }
        }

        return (files, ignored);
    }

    /// <summary>
    /// 끌어 놓은 파일을 놓은 순서대로 연다: 작업 공간은 <see cref="OpenAsync"/> 와 같게, SQL 파일은 <paramref name="openSql"/>(「파일 열기」 명령과 같은 길).
    /// 열 것이 하나도 없고 다른 형식만 있으면 <paramref name="notifyUnsupported"/> 를 한 번 부른다. 끌어 놓기를 받아 처리했으면 true.
    /// </summary>
    public static async Task<bool> OpenDroppedAsync(SqlEditorHost host, IEnumerable<string?>? paths, Func<string, Task> openSql, Action notifyUnsupported, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(openSql);
        ArgumentNullException.ThrowIfNull(notifyUnsupported);
        var (files, ignored) = Classify(paths, includeMyBatis: host.MyBatisHooks.OpenHandler is not null);
        foreach (var (path, kind) in files)
        {
            if (kind == DropKind.Workspace)
            {
                await SqlEditorCommandHandler.OpenWorkspaceFileAsync(host, path, ct).ConfigureAwait(true);
            }
            else
            {
                await openSql(path).ConfigureAwait(true);
            }
        }

        if (files.Count == 0 && ignored > 0)
        {
            notifyUnsupported();
        }

        return files.Count > 0 || ignored > 0;
    }

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
