using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Oranda.App.Features.PlsqlEditor;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell;
using Oranda.Core.Sessions;

namespace Oranda.App.Features.PlsqlWorkspace;

/// <summary>
/// PL/SQL 소스 파일(<c>.pks .pkb .pls .plb .prc .fnc .trg</c>)을 「파일 열기」로 열 때 SQL 탭이 아니라 PL/SQL 작업 공간 탭으로 연다(B-3).
/// 글·인코딩은 SQL 파일 열기와 같이 읽어 그대로 두므로(<see cref="FileEncoding"/>) 고치지 않고 저장하면 원본과 바이트가 같다.
/// 탭은 서버 객체가 아니라 파일이다: 대상 없음, 컴파일 때 머리글로 정한다(새 문서와 같은 길). <c>.pck</c>·<c>.sql</c> 등은 지금처럼 SQL 탭이다.
/// </summary>
public static class PlsqlFiles
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pks", ".pkb", ".pls", ".plb", ".prc", ".fnc", ".trg",
    };

    /// <summary>PL/SQL 작업 공간 탭으로 여는 확장자인가(대소문자 무시).</summary>
    public static bool IsPlsqlFile(string? path) => path is not null && Extensions.Contains(Path.GetExtension(path));

    /// <summary>
    /// 파일을 PL/SQL 작업 공간 탭으로 연다. 같은 경로가 PL/SQL 탭에 열려 있으면 그 탭을, SQL 탭에 열려 있으면 그 SQL 탭을 앞으로 내고 새로 열지 않는다.
    /// <paramref name="session"/> 이 null 이면 지금 세션. 셸의 작업 공간 라우터가 없거나 문서를 열 수 없으면 false(호출자가 SQL 탭으로 연다).
    /// </summary>
    public static async Task<bool> OpenAsync(IServiceProvider? services, SqlEditorHost host, string path, ISession? session, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (PlsqlWorkspaceRouter.Of(services) is not { } router)
        {
            return false;
        }

        var full = Path.GetFullPath(path);
        if (ShowOpen(services, router, host, full))
        {
            return true;
        }

        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(full, ct).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await host.Dialogs.ShowMessageAsync("파일 열기", $"'{Path.GetFileName(full)}' 을(를) 열지 못했다: {ex.Message}", ct).ConfigureAwait(true);
            return true;
        }

        if (ShowOpen(services, router, host, full))
        {
            return true; // 읽는 사이 같은 파일을 또 열었다(빠른 두 번 열기): 탭은 하나만
        }

        var (text, encoding) = FileEncoding.Decode(bytes);
        var tab = services is null ? new PlsqlEditorViewModel() : new PlsqlEditorViewModel(services);
        tab.StartFile(full, text, encoding, session ?? PlsqlEditorCommandHandler.ResolveSession(services));
        if (router.Place(tab, preview: false, focus: true) is null)
        {
            return false;
        }

        host.NoteFileUsed(full);
        return true;
    }

    /// <summary>같은 경로가 이미 열려 있으면 그 탭을 앞으로 내고 true.</summary>
    private static bool ShowOpen(IServiceProvider? services, PlsqlWorkspaceRouter router, SqlEditorHost host, string full)
    {
        if (host.Tabs.FirstOrDefault(t => Same(t.FilePath, full)) is { } sql)
        {
            host.Active = sql;
            // 그 작업 공간의 활성 탭이 이미 이 탭이면 Active 대입은 이벤트를 내지 않아 문서가 앞으로 안 나온다: 도킹에서 직접 앞으로 낸다.
            if ((services?.GetService(typeof(IShellUi)) as IShellUi)?.Docking is { } docking
                && host.WorkspaceOf(sql) is { } sqlWorkspace
                && docking.Documents.FirstOrDefault(d => ReferenceEquals(d.Panel, sqlWorkspace)) is { } doc)
            {
                docking.Activate(doc);
            }

            host.NoteFileUsed(full);
            return true;
        }

        foreach (var workspace in router.Workspaces)
        {
            if (workspace.Tabs.FirstOrDefault(t => Same(t.Editor.FilePath, full)) is { } open)
            {
                router.Show(workspace, open, focus: true);
                host.NoteFileUsed(full);
                return true;
            }
        }

        return false;
    }

    internal static bool Same(string? a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
