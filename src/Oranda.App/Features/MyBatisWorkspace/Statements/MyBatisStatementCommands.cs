using Oranda.App.Features.MyBatisWorkspace.Workspace;
using Oranda.App.Features.SqlEditor;
using Oranda.Core.Shell;
using Oranda.Core.Workspaces;
using CoreWorkspace = Oranda.Core.Workspaces.Workspace;

namespace Oranda.App.Features.MyBatisWorkspace.Statements;

/// <summary>
/// 「문장」 창 명령 5개(새 문장·id 바꾸기·지우기·새 SQL 탭으로 펼치기·매퍼 전체 펼치기)의 처리기(docs/oranda1/design.md 4.1). TSK-01-02 는 특성(ID·활성 조건·자리)만 동결했다.
/// 대상 문장은 세션의 고른 요소(<see cref="MyBatisWorkspaceSession.SelectedEntry"/>)이고, 우클릭 메뉴는 띄우기 전에 그 문장을 고른다.
/// </summary>
[OrandaCommand("oranda.mybatis.newStatement", CommandCategory.Edit, Icon = "add", Enablement = "mybatisDocActive", Placements = ["context:mybatisStatements/new@10"])]
[OrandaCommand("oranda.mybatis.renameStatement", CommandCategory.Edit, Icon = "rename", Enablement = "mybatisDocActive && mybatisStatementSelected", Placements = ["context:mybatisStatements/rename@20"])]
[OrandaCommand("oranda.mybatis.deleteStatement", CommandCategory.Edit, Icon = "delete", Enablement = "mybatisDocActive && mybatisStatementSelected", Placements = ["context:mybatisStatements/delete@30"])]
[OrandaCommand("oranda.mybatis.expandToSqlTab", CommandCategory.Sql, Icon = "fileSql", Enablement = "mybatisDocActive && mybatisStatementSelected", Placements = ["context:mybatisStatements/expand@40"])]
[OrandaCommand("oranda.mybatis.expandMapper", CommandCategory.Sql, Icon = "fileSql", Enablement = "mybatisDocActive", Placements = ["context:mybatisStatements/expandAll@50"])]
public sealed class MyBatisStatementCommands : ICommandHandler
{
    private readonly IServiceProvider? _services;
    private SqlEditorHost? _host;

    // 인자 없는 생성자는 계산기를 찾을 공급자가 없는 쪽이다(시험·헤드리스). 셸은 IServiceProvider 를 받는 생성자를 고른다
    // (등록된 형식이라 지연 어셈블리를 훑지 않는다, ChangeJournalCommandHandler 와 같은 모양).
    public MyBatisStatementCommands()
    {
    }

    public MyBatisStatementCommands(IServiceProvider services) => _services = services;

    /// <summary>다루는 호스트. 기본은 <see cref="SqlEditorHost.Current"/>(시험은 초기화 구문으로 준다).</summary>
    internal SqlEditorHost Host
    {
        get => _host ?? SqlEditorHost.Current;
        init => _host = value;
    }

    public async Task ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var host = Host;
        if (host.ActiveWorkspace?.MyBatis is not MyBatisWorkspaceSession session)
        {
            return;
        }

        switch (context.CommandId)
        {
            case "oranda.mybatis.newStatement":
                await session.BeginNewStatementAsync(cancellationToken).ConfigureAwait(true);
                break;
            case "oranda.mybatis.renameStatement":
                await RenameAsync(host, session, cancellationToken).ConfigureAwait(true);
                break;
            case "oranda.mybatis.deleteStatement":
                await DeleteAsync(host, session, cancellationToken).ConfigureAwait(true);
                break;
            case "oranda.mybatis.expandToSqlTab":
                await ExpandToSqlTabAsync(host, session, cancellationToken).ConfigureAwait(true);
                break;
            case "oranda.mybatis.expandMapper":
                await ExpandMapperAsync(host, session, cancellationToken).ConfigureAwait(true);
                break;
        }
    }

    private static async Task RenameAsync(SqlEditorHost host, MyBatisWorkspaceSession session, CancellationToken ct)
    {
        const string title = "쿼리 id 바꾸기";
        var entry = await TargetAsync(host, session, title, editableOnly: true, ct).ConfigureAwait(true);
        if (entry?.Id is not { } id)
        {
            return;
        }

        var answer = await host.Dialogs.PromptAsync(title, id, ct).ConfigureAwait(true);
        var newId = answer?.Trim();
        if (string.IsNullOrEmpty(newId) || string.Equals(newId, id, StringComparison.Ordinal))
        {
            return;
        }

        if (session.ValidateId(newId, entry) is { } problem)
        {
            await host.Dialogs.ShowMessageAsync(title, problem, ct).ConfigureAwait(true);
            return;
        }

        session.RenameEntry(entry, newId);
    }

    private static async Task DeleteAsync(SqlEditorHost host, MyBatisWorkspaceSession session, CancellationToken ct)
    {
        var entry = await TargetAsync(host, session, "쿼리 지우기", editableOnly: true, ct).ConfigureAwait(true);
        if (entry?.Id is not { } id)
        {
            return;
        }

        var yes = await host.Dialogs.ConfirmAsync($"'{id}' 을(를) 지울까요? 매퍼 파일에는 저장할 때 반영됩니다.", ct).ConfigureAwait(true);
        if (yes)
        {
            session.DeleteEntry(entry);
        }
    }

    private async Task ExpandToSqlTabAsync(SqlEditorHost host, MyBatisWorkspaceSession session, CancellationToken ct)
    {
        const string title = "펼친 SQL 을 새 SQL 탭으로";
        var entry = await TargetAsync(host, session, title, editableOnly: false, ct).ConfigureAwait(true);
        if (entry?.Id is not { } id)
        {
            return;
        }

        if (!entry.IsStatement)
        {
            await host.Dialogs.ShowMessageAsync(title, "select·insert·update·delete 문장을 골라 주세요.", ct).ConfigureAwait(true);
            return;
        }

        if (!MyBatisStatementExpander.IsAvailable(_services))
        {
            await host.Dialogs.ShowMessageAsync(title, "계산기가 아직 없습니다.", ct).ConfigureAwait(true);
            return;
        }

        if (MyBatisStatementExpander.Expand(session, id, _services) is not { } sql)
        {
            await host.Dialogs.ShowMessageAsync(title, $"'{id}' 을(를) 펼치지 못했습니다.", ct).ConfigureAwait(true);
            return;
        }

        // MyBatis 작업 공간이 활성이면 호스트가 알아서 일반 SQL 작업 공간으로 보낸다(design.md 2.2).
        host.NewDocument(sql).Title = id;
    }

    private static async Task ExpandMapperAsync(SqlEditorHost host, MyBatisWorkspaceSession session, CancellationToken ct)
    {
        const string title = "매퍼 전체를 SQL 탭으로 펼치기";
        WorkspaceFile file;
        try
        {
            // 저장 안 한 고침까지 넣은 지금 원문을 펼친다(디스크 파일이 아니라).
            var text = session.Document.Apply(session.PendingEdits());
            file = WorkspaceFiles.ReadMyBatis(text, Path.GetFileNameWithoutExtension(session.MapperPath));
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or System.Xml.XmlException)
        {
            await host.Dialogs.ShowMessageAsync(title, $"{Path.GetFileName(session.MapperPath)} 을(를) 펼치지 못했습니다: {ex.Message}", ct).ConfigureAwait(true);
            return;
        }

        if (file.Tabs.Count == 0)
        {
            await host.Dialogs.ShowMessageAsync(title, "펼칠 문장이 없습니다.", ct).ConfigureAwait(true);
            return;
        }

        // 원본 경로는 넣지 않는다(넣으면 MyBatis 복구가 붙는다). ID 는 매퍼마다 하나: 다시 펼치면 그 작업 공간의 탭을 새 결과로 바꿔 끼우고,
        // 닫을 때 남는 저장본도 매퍼마다 하나뿐이다.
        var name = Path.GetFileNameWithoutExtension(session.MapperPath);
        var expanded = new CoreWorkspace
        {
            Id = "mybatis-expand-" + SqlEditorHost.FileWorkspaceId(name, session.MapperPath),
            Name = Path.GetFileNameWithoutExtension(session.MapperPath) + " (펼침)",
            SavedAt = DateTimeOffset.UtcNow,
            Tabs = [.. file.Tabs.Select(t => new WorkspaceTab { Id = Guid.NewGuid(), Title = t.Title, Content = t.Text })],
        };
        host.Restore(expanded);
    }

    /// <summary>대상 문장 요소(고른 문장 → 활성 문장 탭). 없거나 편집할 수 없으면 알리고 null.</summary>
    private static async Task<MyBatisStatementEntry?> TargetAsync(SqlEditorHost host, MyBatisWorkspaceSession session, string title, bool editableOnly, CancellationToken ct)
    {
        var entry = session.SelectedEntry;
        if (entry is null)
        {
            await host.Dialogs.ShowMessageAsync(title, "문장을 먼저 골라 주세요.", ct).ConfigureAwait(true);
            return null;
        }

        if (editableOnly && !entry.IsEditable)
        {
            var why = entry.IsAmbiguous ? "같은 id 가 여럿이라 편집할 수 없는 문장입니다." : "문장과 sql 조각만 바꾸거나 지울 수 있습니다.";
            await host.Dialogs.ShowMessageAsync(title, why, ct).ConfigureAwait(true);
            return null;
        }

        return entry;
    }
}
