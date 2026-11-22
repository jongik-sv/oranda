using System;
using System.Collections.Generic;
using System.Linq;
using Oranda.App.Features.SqlEditor;
using Oranda.Core.Execution;
using Oranda.Core.Language;
using Oranda.Language.Lexing;

namespace Oranda.App.Features.Ai.Tuning;

/// <summary>
/// 튜닝 흐름이 편집기 탭을 다루는 데 필요한 부분: 시작한 탭을 기억해 두었다가 그 탭의 글을 읽고, 인덱스 후보 탭을 만들거나 바꾼다.
/// 활성 탭만 보이는 <see cref="Oranda.Core.Extensibility.IEditorContext"/> 로는 튜닝하는 사이 활성 탭이 바뀐 경우를 구분할 수 없어서 따로 둔다.
/// </summary>
public interface IEditorTabs
{
    /// <summary>활성 탭의 식별자. 탭이 없으면 null.</summary>
    Guid? ActiveTabId { get; }

    /// <summary>탭의 지금 글. 탭이 닫혔으면 null.</summary>
    string? GetText(Guid tabId);

    /// <summary>
    /// 탭의 글에서 <paramref name="offset"/> 위치의 문장(편집기가 문장을 나누는 규칙 그대로). 탭이 닫혔거나 문장 밖이면 null.
    /// 시작할 때의 문장과 범위까지 같은지 비교하는 데 쓴다: 문장 뒤에 글이 이어 붙은 경우는 앞부분 글만 비교해서는 알 수 없다.
    /// </summary>
    SqlStatement? GetStatementAt(Guid tabId, int offset);

    /// <summary>탭을 활성으로 만든다(제안 비교 창이 활성 탭에 적용되기 때문이다). 탭이 닫혔으면 false.</summary>
    bool Activate(Guid tabId);

    /// <summary>탭의 제목. 탭이 닫혔으면 null.</summary>
    string? GetTitle(Guid tabId);

    /// <summary>
    /// 글을 새 탭으로 열고(활성이 된다) 그 탭의 식별자를 돌려준다.
    /// <paramref name="connectionFrom"/> 이 열려 있는 탭이면 새 탭이 그 탭과 같은 접속으로 열린다.
    /// </summary>
    Guid OpenText(string title, string text, Guid? connectionFrom = null);

    /// <summary>탭의 글을 바꾼다(활성 탭은 그대로 둔다). 탭이 닫혔으면 false.</summary>
    bool ReplaceText(Guid tabId, string text);

    /// <summary>
    /// <paramref name="fromTab"/> 의 바인드 값을 <paramref name="toTab"/> 의 글이 참조하는 이름만큼 복사한다(「(튜닝)」 탭이 원래 탭의 값으로 바로 실행되게).
    /// 어느 쪽이든 탭이 닫혔으면 아무것도 하지 않는다. 구현하지 않으면 복사하지 않는다.
    /// </summary>
    void CopyBinds(Guid fromTab, Guid toTab)
    {
    }

    /// <summary>이 탭에서 시작한 재작성 제안 카드에 붙일 한 줄(MyBatis 펼친 SQL 이면 「XML 은 바뀌지 않습니다」). 구현하지 않으면 없음.</summary>
    string? ProposalNote(Guid tabId) => null;

    /// <summary>열린 탭 전부의 지금 모습(작업 공간 순서). 밖의 에이전트 도구(탭 목록)가 쓴다. 구현하지 않으면 빈 목록.</summary>
    IReadOnlyList<EditorTabInfo> ListTabs() => [];

    /// <summary>탭 하나의 지금 모습. 탭이 닫혔으면 null. 구현하지 않으면 null.</summary>
    EditorTabInfo? Describe(Guid tabId) => null;
}

/// <summary>탭 하나의 한 시점 모습(글·커서·선택·판·읽기 전용·파일 경로). 밖의 에이전트 도구가 탭을 읽는 단위다.</summary>
/// <param name="TabId">탭 식별자.</param>
/// <param name="Title">탭 제목.</param>
/// <param name="Text">지금 글.</param>
/// <param name="Version">글이 바뀔 때마다 오르는 번호(<see cref="SqlEditorViewModel.Version"/>).</param>
public sealed record EditorTabInfo(Guid TabId, string Title, string Text, long Version)
{
    /// <summary>탭이 든 작업 공간 이름. 모르면 null.</summary>
    public string? Workspace { get; init; }

    /// <summary>탭 종류: <c>sql</c>·<c>mybatis</c>.</summary>
    public string Kind { get; init; } = "sql";

    public Guid? ConnectionProfileId { get; init; }

    public bool IsDirty { get; init; }

    public bool IsReadOnly { get; init; }

    public bool IsActive { get; init; }

    /// <summary>파일 경로(전체). 파일 없는 탭이면 null. 밖으로 내보낼 때는 파일 이름만 쓴다.</summary>
    public string? FilePath { get; init; }

    public int CaretOffset { get; init; }

    public int SelectionStart { get; init; }

    public int SelectionLength { get; init; }

    /// <summary>선택이 열 블록·여러 커서처럼 조각 둘 이상인가(이때 선택은 한 덩어리로 다루지 않는다).</summary>
    public bool SelectionIsBlock { get; init; }

    /// <summary>글이 참조하는 바인드 이름(처음 나온 순서).</summary>
    public IReadOnlyList<string> BindNames { get; init; } = [];
}

/// <summary><see cref="SqlEditorHost"/> 로 <see cref="IEditorTabs"/> 를 구현한다. 호스트는 부를 때마다 <see cref="SqlEditorHost.Current"/> 를 읽는다.</summary>
public sealed class HostEditorTabs : IEditorTabs
{
    private readonly Func<SqlEditorHost> _host;

    public HostEditorTabs()
        : this(() => SqlEditorHost.Current)
    {
    }

    /// <summary>호스트를 정해 쓴다(시험).</summary>
    public HostEditorTabs(Func<SqlEditorHost> host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
    }

    private SqlEditorHost Host => _host();

    public Guid? ActiveTabId => Host.Active?.TabId;

    public string? GetText(Guid tabId) => Find(tabId)?.Text;

    public SqlStatement? GetStatementAt(Guid tabId, int offset) =>
        Find(tabId) is { } tab ? StatementSplitter.GetStatementAt(tab.Text, offset, SplitOptions.Default) : null;

    public bool Activate(Guid tabId)
    {
        if (Find(tabId) is not { } tab)
        {
            return false;
        }

        Host.Active = tab;
        return true;
    }

    public string? GetTitle(Guid tabId) => Find(tabId)?.Title;

    public Guid OpenText(string title, string text, Guid? connectionFrom = null) =>
        Host.OpenTextTab(text, title, connectionFrom is { } from ? Find(from)?.ConnectionProfileId : null).TabId;

    public void CopyBinds(Guid fromTab, Guid toTab) => Host.CopyBinds(fromTab, toTab);

    public bool ReplaceText(Guid tabId, string text)
    {
        if (Find(tabId) is not { } tab)
        {
            return false;
        }

        tab.Document.Text = text;
        return true;
    }

    public IReadOnlyList<EditorTabInfo> ListTabs()
    {
        var host = Host;
        return [.. host.Tabs.Select(t => Info(host, t))];
    }

    public EditorTabInfo? Describe(Guid tabId) => Find(tabId) is { } tab ? Info(Host, tab) : null;

    private static EditorTabInfo Info(SqlEditorHost host, SqlEditorViewModel tab)
    {
        var workspace = host.WorkspaceOf(tab);
        return new EditorTabInfo(tab.TabId, tab.Title, tab.Text, tab.Version)
        {
            Workspace = workspace?.Name,
            Kind = workspace?.DocumentKind == SqlWorkspaceKind.MyBatis ? "mybatis" : "sql",
            ConnectionProfileId = tab.ConnectionProfileId,
            IsDirty = tab.IsDirty,
            IsReadOnly = tab.IsReadOnly,
            IsActive = ReferenceEquals(host.Active, tab),
            FilePath = tab.FilePath,
            CaretOffset = tab.CaretOffset,
            SelectionStart = tab.SelectionStart,
            SelectionLength = tab.SelectionLength,
            SelectionIsBlock = tab.HasMultiPieceSelection || tab.SelectionIsBlock,
            BindNames = tab.ReferencedBindNames,
        };
    }

    private SqlEditorViewModel? Find(Guid tabId) => Host.Tabs.FirstOrDefault(t => t.TabId == tabId);
}
