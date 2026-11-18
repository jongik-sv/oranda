using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Oranda.Core.Execution;
using Oranda.Core.Extensibility;
using Oranda.Core.Text;

namespace Oranda.App.Features.Ai.Tuning;

/// <summary>
/// 탭의 글이 그대로 SQL 이 아닌 문서(MyBatis 매퍼 문장 탭)가 AI 에 보일 SQL 을 대신 내는 곳(설계 §3.4). AI 는 맡은 탭의 원문 대신 <see cref="SqlOf"/> 만 보고,
/// 그 탭의 글은 AI 경로로 바뀌지 않는다(제안은 늘 새 SQL 탭으로 간다).
/// </summary>
public interface IAiSqlSource
{
    /// <summary>이 탭을 맡는다(AI 는 이 탭의 글 대신 <see cref="SqlOf"/> 를 본다).</summary>
    bool Owns(Guid tabId);

    /// <summary>맡은 탭에서 AI 로 보낼 SQL(MyBatis 면 지금 값으로 펼친 SQL). 만들지 못했으면 null.</summary>
    string? SqlOf(Guid tabId);

    /// <summary>맡은 탭의 바인드 값(벤치마크 실행에만 쓰고 AI 로 보내지 않는다). 없으면 빈 사전.</summary>
    IReadOnlyDictionary<string, BindValue> BindsOf(Guid tabId);

    /// <summary>제안을 넣은 새 탭(<paramref name="toTab"/>)에 맡은 탭(<paramref name="fromTab"/>)의 바인드 값을 넣는다.</summary>
    void CopyBinds(Guid fromTab, Guid toTab);

    /// <summary>재작성 제안 카드에 붙일 한 줄(없으면 null).</summary>
    string? ProposalNote { get; }
}

/// <summary>
/// 편집기 맥락과 탭 접근을 감싸, 맡는 곳(<see cref="IAiSqlSource"/>)이 있는 탭은 그 SQL 로 보이게 한다. 맡는 곳이 없는 탭은 안의 것에 그대로 넘긴다.
/// 맡은 탭에는 글을 쓰지 않는다: <see cref="ReplaceText"/>·<see cref="ProposeEditAsync"/> 는 거절한다(매퍼 XML 은 AI 로 바뀌지 않는다, 결정 AE-5).
/// </summary>
public sealed class AiSqlSourceEditor : IEditorContext, IEditorTabs
{
    private readonly IEditorContext _editor;
    private readonly IEditorTabs _tabs;
    private readonly IReadOnlyList<IAiSqlSource> _sources;

    public AiSqlSourceEditor(IEditorContext editor, IEditorTabs tabs, IReadOnlyList<IAiSqlSource> sources)
    {
        ArgumentNullException.ThrowIfNull(editor);
        ArgumentNullException.ThrowIfNull(tabs);
        ArgumentNullException.ThrowIfNull(sources);
        _editor = editor is AiSqlSourceEditor wrapped ? wrapped._editor : editor;
        _tabs = tabs is AiSqlSourceEditor wrappedTabs ? wrappedTabs._tabs : tabs;
        _sources = sources;
    }

    /// <summary>
    /// 서비스에 등록된 맡는 곳(<see cref="IAiSqlSource"/>, 목록으로 묻는다: 단일 형식을 물으면 등록이 없을 때 컨테이너가 지연 어셈블리를 훑는다)으로 감싼다.
    /// 이미 감싼 것이면 안의 것을 다시 감싼다. <paramref name="tabs"/> 가 없으면 호스트 탭(<see cref="HostEditorTabs"/>).
    /// </summary>
    public static AiSqlSourceEditor Wrap(IServiceProvider services, IEditorContext editor, IEditorTabs? tabs = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var sources = services.GetService(typeof(IEnumerable<IAiSqlSource>)) is IEnumerable<IAiSqlSource> all ? all.ToList() : [];
        return new AiSqlSourceEditor(editor, tabs ?? (editor as AiSqlSourceEditor)?._tabs ?? new HostEditorTabs(), sources);
    }

    /// <summary>이 탭을 맡는 곳이 있다(MyBatis 문장 탭처럼 AI 가 펼친 SQL 을 본다).</summary>
    public bool Owns(Guid tabId) => SourceOf(tabId) is not null;

    /// <summary>맡은 탭이면 그 탭의 바인드 값(벤치마크용). 맡지 않은 탭이면 null(호출한 쪽이 탭의 바인드 목록을 쓴다).</summary>
    public IReadOnlyDictionary<string, BindValue>? BindsOf(Guid tabId) => SourceOf(tabId)?.BindsOf(tabId);

    // ---- IEditorContext ----

    public EditorSnapshot? GetActiveDocument()
    {
        var snapshot = _editor.GetActiveDocument();
        if (snapshot is null || SourceOf(snapshot.TabId) is not { } source)
        {
            return snapshot;
        }

        // 맡은 탭은 AI 에 보일 SQL 로 바꾼다(원문·선택·커서는 싣지 않는다). 접속·제목은 원래 탭 그대로.
        return new EditorSnapshot(snapshot.TabId, source.SqlOf(snapshot.TabId) ?? string.Empty, new TextRange(0, 0), 0)
        {
            Title = snapshot.Title,
            ConnectionProfileId = snapshot.ConnectionProfileId,
            SessionId = snapshot.SessionId,
        };
    }

    public SqlStatement? GetCurrentStatement()
    {
        var snapshot = _editor.GetActiveDocument();
        return snapshot is not null && SourceOf(snapshot.TabId) is { } source
            ? StatementOf(source.SqlOf(snapshot.TabId))
            : _editor.GetCurrentStatement();
    }

    /// <summary>맡은 탭이 활성이면 넣지 않는다(false). 아니면 안의 편집기 맥락에 넘긴다.</summary>
    public Task<bool> ProposeEditAsync(TextEdit edit, CancellationToken ct)
    {
        var snapshot = _editor.GetActiveDocument();
        return snapshot is not null && SourceOf(snapshot.TabId) is not null ? Task.FromResult(false) : _editor.ProposeEditAsync(edit, ct);
    }

    // ---- IEditorTabs ----

    public Guid? ActiveTabId => _tabs.ActiveTabId;

    public string? GetText(Guid tabId) => SourceOf(tabId) is { } source ? source.SqlOf(tabId) : _tabs.GetText(tabId);

    public SqlStatement? GetStatementAt(Guid tabId, int offset) =>
        SourceOf(tabId) is { } source ? StatementOf(source.SqlOf(tabId)) : _tabs.GetStatementAt(tabId, offset);

    public bool Activate(Guid tabId) => _tabs.Activate(tabId);

    public string? GetTitle(Guid tabId) => _tabs.GetTitle(tabId);

    public Guid OpenText(string title, string text, Guid? connectionFrom = null) => _tabs.OpenText(title, text, connectionFrom);

    /// <summary>맡은 탭의 글은 바꾸지 않는다(false). 아니면 안의 것에 넘긴다.</summary>
    public bool ReplaceText(Guid tabId, string text) => SourceOf(tabId) is null && _tabs.ReplaceText(tabId, text);

    public void CopyBinds(Guid fromTab, Guid toTab)
    {
        if (SourceOf(fromTab) is { } source)
        {
            source.CopyBinds(fromTab, toTab);
            return;
        }

        _tabs.CopyBinds(fromTab, toTab);
    }

    public string? ProposalNote(Guid tabId) => SourceOf(tabId)?.ProposalNote;

    private IAiSqlSource? SourceOf(Guid tabId)
    {
        foreach (var source in _sources)
        {
            if (source.Owns(tabId))
            {
                return source;
            }
        }

        return null;
    }

    /// <summary>
    /// 맡은 탭의 SQL 을 문장 하나로 읽는다(편집기가 문장을 나누는 규칙 그대로, 첫 글자 자리의 문장). 비었으면 null.
    /// 문장 나누기는 Oranda.Language 에 있으므로 맡은 탭일 때만 도는 이 메서드에 둔다(시작 때 어셈블리를 깨우지 않게).
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static SqlStatement? StatementOf(string? sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            return null;
        }

        var start = sql.Length - sql.TrimStart().Length;
        var statement = Oranda.Language.Lexing.StatementSplitter.GetStatementAt(sql, start, Oranda.Core.Language.SplitOptions.Default);
        return statement is { Text.Length: > 0 } ? statement : null;
    }
}
