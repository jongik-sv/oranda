using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Oranda.App.Features.Localization;
using Oranda.App.Features.MyBatisWorkspace.Hooks;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell;
using Oranda.App.Shell.Keymaps;
using Oranda.Core.Composition;
using Oranda.Core.Shell;
using Oranda.Language.MyBatis;

namespace Oranda.App.Features.MyBatisWorkspace.Editor;

/// <summary>
/// MyBatis 문장 탭의 편집기 View 와 탭 머리 배지를 만든다(MB-04, design.md 9장). 작업 공간 View 가 탭마다 한 번 불러 들고 있는다.
/// 문서 도구 막대는 명령 레지스트리·문맥 키·키맵·글자 조회를 생성자로 받아야 그린다. 받지 못하는 시험 환경에서는 막대를 숨긴다.
/// 생성자는 등록된 Shell 서비스만 받는다(시작 시험 제약: 등록되지 않은 형식이 있으면 지연 어셈블리를 훑는다, R21).
/// </summary>
[OrandaService(typeof(IMyBatisEditorViewFactory))]
public sealed class MyBatisEditorViewFactory : IMyBatisEditorViewFactory
{
    private readonly ICommandRegistry? _registry;
    private readonly ContextKeyService? _keys;
    private readonly IShellText? _text;
    private readonly KeymapService? _keymap;
    private readonly IServiceProvider? _services;

    /// <summary>시험용(문서 도구 막대 없음).</summary>
    public MyBatisEditorViewFactory()
    {
    }

    public MyBatisEditorViewFactory(ICommandRegistry registry, ContextKeyService keys, IShellText text, KeymapService keymap)
        : this(registry, keys, text, keymap, null)
    {
    }

    /// <summary>셸(DI)이 쓰는 생성자. 서비스 공급자는 탭 안 「펼친 SQL」(계산 허브·설정)이 쓴다(TSK-03-10).</summary>
    public MyBatisEditorViewFactory(ICommandRegistry registry, ContextKeyService keys, IShellText text, KeymapService keymap, IServiceProvider? services)
    {
        _registry = registry;
        _keys = keys;
        _text = text;
        _keymap = keymap;
        _services = services;
    }

    public Control CreateEditor(SqlEditorViewModel tab, IMyBatisWorkspaceSession session)
    {
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentNullException.ThrowIfNull(session);
        var toolbar = _registry is not null && _keys is not null && _text is not null && _keymap is not null
            ? new MyBatisDocumentToolbar(_registry, _keys, _text, _keymap)
            : null;
        return new MyBatisStatementEditorView(tab, session, toolbar, _services);
    }

    public Control? CreateTabBadge(SqlEditorViewModel tab, IMyBatisWorkspaceSession session)
    {
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentNullException.ThrowIfNull(session);
        if (session.StatementIdOf(tab) is not { } id)
        {
            return null;
        }

        // 종류 글자·톤·툴팁 키. Language 의 종류(MyBatisElementKind)는 이 메서드 안에서만 읽는다(R21).
        var (letters, tone, key, fallback) = session.ElementOf(id)?.Kind switch
        {
            MyBatisElementKind.Select => ("SEL", "Purple", "select", "조회 문장(select)"),
            MyBatisElementKind.Insert => ("INS", "Green", "insert", "추가 문장(insert)"),
            MyBatisElementKind.Update => ("UPD", "Orange", "update", "수정 문장(update)"),
            MyBatisElementKind.Delete => ("DEL", "Red", "delete", "삭제 문장(delete)"),
            MyBatisElementKind.Sql => ("SQL", "Teal", "sql", "SQL 조각(sql)"),
            _ => default((string, string, string, string)),
        };
        if (letters is null)
        {
            return null;
        }

        var tip = UiText.Get("MyBatis.badge." + key, fallback);
        var badge = new TextBlock
        {
            Text = letters,
            FontSize = 10,
            FontWeight = FontWeight.Bold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Avalonia.Thickness(0, 0, 4, 0),
        };
        badge.Bind(TextBlock.ForegroundProperty, badge.GetResourceObservable("OrandaIcon" + tone + "Brush"));
        ToolTip.SetTip(badge, tip);
        AutomationProperties.SetName(badge, tip);
        return badge;
    }
}
