using System;
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
}

/// <summary><see cref="SqlEditorHost"/> 로 <see cref="IEditorTabs"/> 를 구현한다. 호스트는 부를 때마다 <see cref="SqlEditorHost.Current"/> 를 읽는다.</summary>
public sealed class HostEditorTabs : IEditorTabs
{
    private static SqlEditorHost Host => SqlEditorHost.Current;

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

    public bool ReplaceText(Guid tabId, string text)
    {
        if (Find(tabId) is not { } tab)
        {
            return false;
        }

        tab.Document.Text = text;
        return true;
    }

    private static SqlEditorViewModel? Find(Guid tabId) => Host.Tabs.FirstOrDefault(t => t.TabId == tabId);
}
