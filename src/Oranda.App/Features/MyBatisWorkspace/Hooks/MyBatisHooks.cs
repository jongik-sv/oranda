using Oranda.App.Features.SqlEditor;
using Oranda.Core.MyBatis;

namespace Oranda.App.Features.MyBatisWorkspace.Hooks;

/// <summary>
/// 호스트가 부르는 MyBatis 훅 묶음(design.md 9장). 구현이 하나도 없으면 모두 비어 있고(<see cref="Empty"/>) 호스트는 지금 경로 그대로 간다.
/// 구현은 처음 읽을 때 한 번만 해석한다(Lazy). 해석은 항상 <c>IEnumerable&lt;T&gt;</c> 로 한다:
/// 단일 형식을 물으면 등록이 없을 때 컨테이너가 지연 어셈블리(Language·Oracle)를 훑어 시작 시험이 막는 일을 일으킨다(D-006).
/// UI 스레드에서 쓴다.
/// </summary>
public sealed class MyBatisHooks
{
    /// <summary>구현이 하나도 없는 묶음(<see cref="SqlEditorHost.MyBatisHooks"/> 의 기본값). 바뀌지 않는다.</summary>
    public static MyBatisHooks Empty { get; } = new();

    private readonly Lazy<IMyBatisOpenHandler?> _open;
    private readonly Lazy<IMyBatisSaveHandler?> _save;
    private readonly Lazy<IReadOnlyList<IMyBatisRecoveryParticipant>> _recovery;
    private readonly Lazy<IMyBatisEditorViewFactory?> _viewFactory;
    private readonly Lazy<IReadOnlyList<IMyBatisEditorDecorator>> _decorators;
    private readonly Lazy<IReadOnlyList<IMyBatisStatementBadgeProvider>> _badges;
    private readonly Lazy<IReadOnlyList<IMyBatisStatementsViewMode>> _viewModes;
    private readonly Lazy<IReadOnlyList<IStatementLocator>> _locators;
    private readonly Lazy<IReadOnlyList<IMyBatisFragmentResolver>> _fragments;

    /// <summary>비어 있는 묶음.</summary>
    public MyBatisHooks()
        : this(openHandler: null)
    {
    }

    /// <summary>
    /// 등록된 구현을 <paramref name="resolve"/> 로 처음 읽을 때 해석한다. <paramref name="resolve"/> 는 <c>IEnumerable&lt;T&gt;</c> 를 받으면
    /// 그 구현 배열을 돌려주고 없으면 null 이다(<see cref="IServiceProvider.GetService"/> 와 같은 모양).
    /// </summary>
    public MyBatisHooks(Func<Type, object?> resolve)
    {
        ArgumentNullException.ThrowIfNull(resolve);
        _open = new(() => Last(All<IMyBatisOpenHandler>(resolve)));
        _save = new(() => Last(All<IMyBatisSaveHandler>(resolve)));
        _recovery = new(() => All<IMyBatisRecoveryParticipant>(resolve));
        _viewFactory = new(() => Last(All<IMyBatisEditorViewFactory>(resolve)));
        _decorators = new(() => [.. All<IMyBatisEditorDecorator>(resolve).OrderBy(d => d.Order)]);
        _badges = new(() => All<IMyBatisStatementBadgeProvider>(resolve));
        _viewModes = new(() => All<IMyBatisStatementsViewMode>(resolve));
        _locators = new(() => All<IStatementLocator>(resolve));
        _fragments = new(() => All<IMyBatisFragmentResolver>(resolve));
    }

    /// <summary>DI 컨테이너에서 해석한다.</summary>
    public MyBatisHooks(IServiceProvider services)
        : this(Resolver(services))
    {
    }

    /// <summary>시험용: 값을 직접 넣는다. 안 넣은 훅은 비어 있다.</summary>
    public MyBatisHooks(
        IMyBatisOpenHandler? openHandler = null,
        IMyBatisSaveHandler? saveHandler = null,
        IEnumerable<IMyBatisRecoveryParticipant>? recoveryParticipants = null,
        IMyBatisEditorViewFactory? editorViewFactory = null,
        IEnumerable<IMyBatisEditorDecorator>? editorDecorators = null,
        IEnumerable<IMyBatisStatementBadgeProvider>? badgeProviders = null,
        IEnumerable<IMyBatisStatementsViewMode>? viewModes = null,
        IEnumerable<IStatementLocator>? statementLocators = null,
        IEnumerable<IMyBatisFragmentResolver>? fragmentResolvers = null)
    {
        _open = new(() => openHandler);
        _save = new(() => saveHandler);
        _recovery = new(() => [.. recoveryParticipants ?? []]);
        _viewFactory = new(() => editorViewFactory);
        _decorators = new(() => [.. (editorDecorators ?? []).OrderBy(d => d.Order)]);
        _badges = new(() => [.. badgeProviders ?? []]);
        _viewModes = new(() => [.. viewModes ?? []]);
        _locators = new(() => [.. statementLocators ?? []]);
        _fragments = new(() => [.. fragmentResolvers ?? []]);
    }

    /// <summary>.xml 열기(0~1).</summary>
    public IMyBatisOpenHandler? OpenHandler => _open.Value;

    /// <summary>저장·닫기 확인(0~1).</summary>
    public IMyBatisSaveHandler? SaveHandler => _save.Value;

    /// <summary>복구 상태 참여자(여럿, 등록 순서).</summary>
    public IReadOnlyList<IMyBatisRecoveryParticipant> RecoveryParticipants => _recovery.Value;

    /// <summary>문장 탭 편집기 View 팩토리(0~1).</summary>
    public IMyBatisEditorViewFactory? EditorViewFactory => _viewFactory.Value;

    /// <summary>문장 편집기 꾸밈(<see cref="IMyBatisEditorDecorator.Order"/> 작은 것 먼저).</summary>
    public IReadOnlyList<IMyBatisEditorDecorator> EditorDecorators => _decorators.Value;

    /// <summary>「문장」 창 항목 배지 공급자.</summary>
    public IReadOnlyList<IMyBatisStatementBadgeProvider> BadgeProviders => _badges.Value;

    /// <summary>「문장」 창의 다른 보기 모드.</summary>
    public IReadOnlyList<IMyBatisStatementsViewMode> ViewModes => _viewModes.Value;

    /// <summary>문장 찾기 구현.</summary>
    public IReadOnlyList<IStatementLocator> StatementLocators => _locators.Value;

    /// <summary>다른 파일 sql 조각 해석기(예 03-07 폴더). 세션의 <see cref="IMyBatisWorkspaceSession.Fragments"/> 가 같은 매퍼 다음에 차례로 묻는다.</summary>
    public IReadOnlyList<IMyBatisFragmentResolver> FragmentResolvers => _fragments.Value;

    private static Func<Type, object?> Resolver(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services.GetService;
    }

    // 0~1 개짜리는 컨테이너의 단일 해석과 같이 마지막 등록을 쓴다.
    private static T? Last<T>(IReadOnlyList<T> list)
        where T : class => list.Count > 0 ? list[^1] : null;

    private static IReadOnlyList<T> All<T>(Func<Type, object?> resolve)
        where T : class => resolve(typeof(IEnumerable<T>)) is IEnumerable<T> found ? [.. found] : [];
}
