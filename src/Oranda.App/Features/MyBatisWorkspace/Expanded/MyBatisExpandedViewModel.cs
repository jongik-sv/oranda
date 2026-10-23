using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using Oranda.App.Features.Localization;
using Oranda.App.Features.MyBatisWorkspace.Parameters;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell;
using Oranda.App.Shell.Menus;
using Oranda.App.ViewModels;
using Oranda.Core.MyBatis;
using Oranda.Core.Shell;
using Oranda.Language.MyBatis;

namespace Oranda.App.Features.MyBatisWorkspace.Expanded;

/// <summary>머리 줄 오른쪽의 명령 단추 하나(레지스트리 명령: 실행·실행 계획·복사 2종).</summary>
public sealed class MyBatisExpandedAction(string commandId, string icon, string title, RegistryCommand command)
{
    public string CommandId { get; } = commandId;

    public string Icon { get; } = icon;

    public string Title { get; } = title;

    public RegistryCommand Command { get; } = command;
}

/// <summary>
/// 「펼친 SQL」(MB-04·05, docs/oranda1/design.md 1장). TSK-03-10 부터 아래 도킹 패널이 아니라 MyBatis 문장 탭 안의 분할 영역이다
/// (<see cref="Editor.MyBatisStatementEditorView"/>). 탭의 문장 계산 상태(<see cref="MyBatisEvalHub.ForTab"/>)를 지금 값으로 펼친 문장을 보이고,
/// 머리 줄에 시나리오 콤보(고르기·추가·이름 바꾸기·복제·지우기: 이 영역의 RelayCommand, 레지스트리 명령이 아니다), 「켜진 조건 n/m」 칩,
/// 실행 계획·복사 2종 단추를 둔다. 값은 「바인드 변수」 창 행에서만 고친다(치환 <c>${}</c> 도 그 창의 <c>$이름</c> 행, <see cref="MyBatisBindSync"/>).
/// 단추는 모두 명령 레지스트리의 명령이다(<see cref="ICommandRegistry.ExecuteAsync"/> 한 경로). 계산 상태가 바뀌면(<see cref="MyBatisStatementEval.Changed"/>)
/// 바로 다시 그린다(300 ms 디바운스는 계산 상태 안에 있다). 탭 없이 만들면(시험) 허브의 활성 문장(<see cref="MyBatisEvalHub.Active"/>)을 따라간다.
/// </summary>
/// <remarks>
/// 시작 시험 제약(OutlineViewModel 주석): Language 형은 필드로 두지 않고 계산 결과를 읽는 코드는 문장이 있을 때만 도는 메서드에 둔다.
/// 화면에 붙을 때 <see cref="Attach"/>, 떨어질 때 <see cref="Detach"/> 한다(탭 View 는 탭마다 오래 살고 탭을 오가면 다시 붙는다).
/// </remarks>
public sealed class MyBatisExpandedViewModel : ViewModelBase, IDisposable
{
    /// <summary>머리 줄 단추 3개(그리는 순서). 실행은 문서 도구 막대에 있다.</summary>
    internal static readonly string[] ActionIds =
    [
        "oranda.mybatis.explainScenario",
        "oranda.mybatis.copyExpanded",
        "oranda.mybatis.copyExpandedWithValues",
    ];

    private readonly IServiceProvider? _services;
    private readonly SqlEditorViewModel? _tab;
    private readonly List<MyBatisExpandedAction> _actions = [];
    private MyBatisEvalHub? _hub;
    private MyBatisStatementEval? _eval;
    private IDisposable? _track;
    private ContextKeyService? _keys;
    private string _header = string.Empty;
    private string _chip = string.Empty;
    private string _sql = string.Empty;
    private readonly ObservableCollection<MyBatisScenarioItem> _scenarios = [];
    private MyBatisScenarioItem? _selected;
    private string _notice = string.Empty;
    private string _actionError = string.Empty;
    private bool _attached;
    private bool _syncing;

    /// <summary>시험·디자이너용: 서비스 없이 만든다(안내 한 줄만 보인다).</summary>
    public MyBatisExpandedViewModel()
    {
        AddCommand = new AsyncRelayCommand(AddAsync, () => CanEdit);
        RenameCommand = new AsyncRelayCommand(RenameAsync, () => CanEdit && _selected is not null);
        DuplicateCommand = new AsyncRelayCommand(DuplicateAsync, () => CanEdit && _selected is not null);
        DeleteCommand = new AsyncRelayCommand(DeleteAsync, () => CanEdit && _selected is not null);
    }

    /// <summary>셸(DI)이 쓰는 생성자.</summary>
    public MyBatisExpandedViewModel(IServiceProvider services)
        : this()
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
    }

    /// <summary>문장 탭 하나의 분할 영역용: 그 탭의 문장만 보인다(「매퍼 쿼리」 창에서 고르기만 한 문장은 따라가지 않는다).</summary>
    public MyBatisExpandedViewModel(IServiceProvider services, SqlEditorViewModel tab)
        : this(services)
    {
        ArgumentNullException.ThrowIfNull(tab);
        _tab = tab;
    }

    /// <summary>이 영역이 맡은 문장 탭(없으면 허브의 활성 문장을 따라간다).</summary>
    public SqlEditorViewModel? Tab => _tab;

    /// <summary>지금 보이는 문장의 계산 상태(시험용).</summary>
    internal MyBatisStatementEval? Eval => _eval;

    public IAsyncRelayCommand AddCommand { get; }

    public IAsyncRelayCommand RenameCommand { get; }

    public IAsyncRelayCommand DuplicateCommand { get; }

    public IAsyncRelayCommand DeleteCommand { get; }

    /// <summary>시나리오 콤보 항목(저장소 순서).</summary>
    public ObservableCollection<MyBatisScenarioItem> Scenarios
    {
        get
        {
            Attach();
            return _scenarios;
        }
    }

    /// <summary>고른 시나리오. 바꾸면 「바인드 변수」 창 값 칸과 펼친 SQL 이 그 시나리오 값으로 바뀐다.</summary>
    public MyBatisScenarioItem? SelectedScenario
    {
        get => _selected;
        set
        {
            if (ReferenceEquals(_selected, value))
            {
                return;
            }

            _selected = value;
            OnPropertyChanged();
            NotifyCommands();
            if (!_syncing && value is not null)
            {
                Pick(value);
            }
        }
    }

    /// <summary>활성 문장이 있다(없으면 안내 한 줄만 보인다).</summary>
    public bool HasStatement
    {
        get
        {
            Attach();
            return _eval is not null;
        }
    }

    public bool HasNoStatement => !HasStatement;

    /// <summary>「시나리오 「이름」 값으로 펼친 문장 · 이대로 실행됩니다」.</summary>
    public string Header
    {
        get
        {
            Attach();
            return _header;
        }
    }

    /// <summary>「켜진 조건 n/m」. 조건이 없으면 빈 글(칩을 숨긴다).</summary>
    public string ConditionChip => _chip;

    public bool HasChip => _chip.Length > 0;

    /// <summary>펼친 SQL(바인드는 <c>:이름</c>, 치환은 값 글자). 계산하지 못했으면 빈 글.</summary>
    public string Sql => _sql;

    /// <summary>머리 줄 아래 한 줄: 계산기 오류나 계산하지 못한 식. 없으면 빈 글.</summary>
    public string Notice => _actionError.Length > 0 ? _actionError : _notice;

    public bool HasNotice => Notice.Length > 0;

    /// <summary>머리 줄 명령 단추(레지스트리에 있는 것만).</summary>
    public IReadOnlyList<MyBatisExpandedAction> Actions
    {
        get
        {
            Attach();
            return _actions;
        }
    }

    /// <summary>시험용: 허브·명령 레지스트리·문맥 키를 서비스로 넣어 만든다.</summary>
    internal static MyBatisExpandedViewModel For(MyBatisEvalHub hub, ICommandRegistry? registry = null, IContextKeys? keys = null, SqlEditorViewModel? tab = null)
    {
        var map = new MyBatisServiceMap().With(hub);
        if (registry is not null)
        {
            map.With(registry);
        }

        if (keys is not null)
        {
            map.With(keys);
        }

        return tab is null ? new MyBatisExpandedViewModel(map) : new MyBatisExpandedViewModel(map, tab);
    }

    /// <summary>허브에 붙어 활성 문장을 따라가기 시작한다(화면에 붙을 때 한 번. 여러 번 불러도 같다).</summary>
    public void Attach()
    {
        if (_attached)
        {
            return;
        }

        _attached = true;
        BuildActions();
        _hub = _services?.GetService(typeof(MyBatisEvalHub)) as MyBatisEvalHub;
        if (_hub is null)
        {
            return;
        }

        _hub.ActiveChanged += OnActiveChanged;
        Follow(Target());
    }

    /// <summary>허브·문맥 키 구독을 끊는다(화면에서 떨어질 때). 다시 <see cref="Attach"/> 하면 처음처럼 붙는다.</summary>
    public void Detach()
    {
        if (!_attached)
        {
            return;
        }

        _attached = false;
        if (_hub is not null)
        {
            _hub.ActiveChanged -= OnActiveChanged;
            _hub = null;
        }

        if (_keys is not null)
        {
            _keys.Changed -= OnKeysChanged;
            _keys = null;
        }

        _actions.Clear();
        Unfollow();
    }

    public void Dispose() => Detach();

    /// <summary>
    /// 따라갈 계산 상태: 탭이 있으면 그 탭의 문장(MyBatis 문장 탭이 아니면 null), 없으면 허브의 활성 문장.
    /// 허브의 활성 문장은 늘 먼저 읽는다 — 그래야 허브가 호스트를 따라가며 활성 탭의 「바인드 변수」 행 동기화를 붙인다.
    /// </summary>
    private MyBatisStatementEval? Target()
    {
        if (_hub is null)
        {
            return null;
        }

        var active = _hub.Active;
        return _tab is null ? active : _hub.ForTab(_tab);
    }

    private void BuildActions()
    {
        if (_services?.GetService(typeof(ICommandRegistry)) is not ICommandRegistry registry
            || _services.GetService(typeof(IContextKeys)) is not IContextKeys keys)
        {
            return;
        }

        var text = _services.GetService(typeof(IShellText)) as IShellText ?? UiText.Current;
        foreach (var id in ActionIds)
        {
            if (registry.Find(id) is not { } definition)
            {
                continue;
            }

            var title = text?.Title(definition) ?? id;
            _actions.Add(new MyBatisExpandedAction(id, definition.Icon ?? string.Empty, title, new RegistryCommand(id, registry, keys)));
        }

        // 활성 상태(문장을 골랐는지·접속·실행 중인지)는 문맥 키가 바뀔 때마다 다시 본다(주 창 메뉴·도구 막대와 같은 방식).
        if (keys is ContextKeyService service)
        {
            _keys = service;
            service.Changed += OnKeysChanged;
        }
    }

    private void OnKeysChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        foreach (var action in _actions)
        {
            action.Command.RaiseCanExecuteChanged();
        }
    });

    private void OnActiveChanged(object? sender, EventArgs e) => Follow(Target());

    private void Follow(MyBatisStatementEval? next)
    {
        if (ReferenceEquals(next, _eval))
        {
            return;
        }

        Unfollow();
        _eval = next;
        if (next is not null)
        {
            next.Changed += OnEvalChanged;
            next.ScenariosChanged += OnScenariosChanged;
            if (_tab is not null)
            {
                // 탭 영역은 붙어 있는 동안 자기 탭을 지켜본다: 「매퍼 쿼리」 창에서 다른 문장을 골라 허브의 활성 문장이 바뀌어도
                // 이 탭의 원문 고침·「바인드 변수」 행 값이 계속 이 문장으로 간다(지켜보기는 탭마다 세므로 허브·편집기 꾸밈과 겹쳐도 된다).
                _track = next.Track(_tab);
                _ = next.EnsureLoadedAsync(); // 활성 문장이 아니어도 계산해 둔다(이미 읽었으면 바로 끝난다).
            }
        }

        OnPropertyChanged(nameof(HasStatement));
        OnPropertyChanged(nameof(HasNoStatement));
        SetActionError(string.Empty);
        SyncScenarios();
        Refresh();
        NotifyCommands();
    }

    private void Unfollow()
    {
        _track?.Dispose();
        _track = null;
        if (_eval is { } old)
        {
            old.Changed -= OnEvalChanged;
            old.ScenariosChanged -= OnScenariosChanged;
            _eval = null;
        }
    }

    private void OnEvalChanged(object? sender, EventArgs e) => Refresh();

    private void OnScenariosChanged(object? sender, EventArgs e)
    {
        SyncScenarios();
        NotifyCommands();
        Refresh();
    }

    private void Refresh()
    {
        if (_eval is null)
        {
            Apply(string.Empty, string.Empty, string.Empty, string.Empty);
            return;
        }

        Show(_eval);
    }

    // 계산 결과(EvalResult 등 Language 형)를 읽는 코드는 이 메서드에만 둔다. 활성 문장이 없을 때는 부르지 않는다.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Show(MyBatisStatementEval eval)
    {
        var scenario = eval.SelectedScenario?.Name;
        var header = scenario is null
            ? UiText.Get("MyBatis.expanded.headerNoScenario", "지금 값으로 펼친 문장 · 이대로 실행됩니다")
            : MyBatisText.Format("MyBatis.expanded.header", "시나리오 「{0}」 값으로 펼친 문장 · 이대로 실행됩니다", scenario);

        var result = eval.Result;
        if (result is null)
        {
            var error = eval.Error is { } message
                ? MyBatisText.Format("MyBatis.expanded.error", "계산하지 못했습니다: {0}", message)
                : string.Empty;
            Apply(header, string.Empty, string.Empty, error);
            return;
        }

        var total = eval.ConditionCount;
        var chip = total == 0
            ? string.Empty
            : MyBatisText.Format("MyBatis.expanded.conditions", "켜진 조건 {0}/{1}", eval.ActiveConditionCount, total);

        var notice = string.Empty;
        // 검사용 가정(값 없는 foreach 를 바인드 자리 하나로)은 계산하지 못한 곳이 아니다. 그것만 있으면 그 문구만 보인다.
        var unresolved = result.Issues.Where(i => i.Kind != MyBatisEvalIssueKind.Assumed).ToList();
        if (unresolved.Count > 0)
        {
            notice = MyBatisText.Format("MyBatis.expanded.issues", "계산하지 못한 곳 {0}개: {1}", unresolved.Count,
                unresolved[0].Reason);
        }
        else if (result.Issues.Count > 0)
        {
            notice = result.Issues[0].Reason;
        }

        Apply(header, chip, result.Sql, notice);
    }

    private void Apply(string header, string chip, string sql, string notice)
    {
        SetField(ref _header, header, nameof(Header));
        if (SetField(ref _chip, chip, nameof(ConditionChip)))
        {
            OnPropertyChanged(nameof(HasChip));
        }

        SetField(ref _sql, sql, nameof(Sql));
        if (!string.Equals(_notice, notice, StringComparison.Ordinal))
        {
            _notice = notice;
            OnNoticeChanged();
        }
    }

    private void OnNoticeChanged()
    {
        OnPropertyChanged(nameof(Notice));
        OnPropertyChanged(nameof(HasNotice));
    }

    private void SetActionError(string message)
    {
        if (!string.Equals(_actionError, message, StringComparison.Ordinal))
        {
            _actionError = message;
            OnNoticeChanged();
        }
    }

    // ---- 시나리오 ---------------------------------------------------------------------------

    private bool CanEdit => _eval is not null && StoreAvailable();

    // 저장소를 묻는 순간 계산기·저장소 어셈블리가 깨어난다. 활성 문장이 있을 때만 부른다.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool StoreAvailable() => _hub?.Store is not null;

    private void NotifyCommands()
    {
        AddCommand.NotifyCanExecuteChanged();
        RenameCommand.NotifyCanExecuteChanged();
        DuplicateCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
    }

    /// <summary>콤보 항목을 계산 상태의 시나리오에 맞춘다. 항목은 ID 로 이어 붙여(저장할 때마다 record 가 새로 만들어진다) 선택을 지킨다.</summary>
    private void SyncScenarios()
    {
        _syncing = true;
        try
        {
            var wanted = _eval?.Scenarios ?? [];
            for (var i = 0; i < wanted.Count; i++)
            {
                var scenario = wanted[i];
                if (i < _scenarios.Count && _scenarios[i].Id == scenario.Id)
                {
                    _scenarios[i].Update(scenario);
                    continue;
                }

                var at = IndexOfId(scenario.Id, i + 1);
                if (at >= 0)
                {
                    _scenarios.Move(at, i);
                    _scenarios[i].Update(scenario);
                }
                else
                {
                    _scenarios.Insert(i, new MyBatisScenarioItem(scenario));
                }
            }

            while (_scenarios.Count > wanted.Count)
            {
                _scenarios.RemoveAt(_scenarios.Count - 1);
            }

            var selectedId = _eval?.SelectedScenario?.Id;
            SelectedScenario = selectedId is null ? null : _scenarios.FirstOrDefault(s => s.Id == selectedId);
        }
        finally
        {
            _syncing = false;
        }
    }

    private int IndexOfId(long id, int from)
    {
        for (var i = from; i < _scenarios.Count; i++)
        {
            if (_scenarios[i].Id == id)
            {
                return i;
            }
        }

        return -1;
    }

    private void Pick(MyBatisScenarioItem item)
    {
        if (_eval is not { } eval)
        {
            return;
        }

        // 디바운스 중인 고침이 있으면 먼저 반영해 둔다(고른 시나리오를 바꾸면 예약이 버려진다).
        eval.Flush();
        if (eval.Scenarios.FirstOrDefault(s => s.Id == item.Id) is { } scenario)
        {
            eval.SelectScenario(scenario);
        }
    }

    private Task AddAsync() => RunAsync(async eval =>
    {
        eval.Flush();
        await eval.AddScenarioAsync(NewName(eval)).ConfigureAwait(true);
    });

    private async Task RenameAsync()
    {
        if (_eval?.SelectedScenario is not { } scenario || _hub is null)
        {
            return;
        }

        var title = UiText.Get("MyBatis.scenario.renameTitle", "시나리오 이름 바꾸기");
        var name = await _hub.Host.Dialogs.PromptAsync(title, scenario.Name, CancellationToken.None).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(name) || string.Equals(name.Trim(), scenario.Name, StringComparison.Ordinal))
        {
            return;
        }

        await RunAsync(eval => eval.RenameScenarioAsync(scenario, name.Trim())).ConfigureAwait(true);
    }

    private Task DuplicateAsync() => RunAsync(async eval =>
    {
        if (eval.SelectedScenario is { } scenario)
        {
            await eval.DuplicateScenarioAsync(scenario, CopyName(eval, scenario.Name)).ConfigureAwait(true);
        }
    });

    private Task DeleteAsync() => RunAsync(async eval =>
    {
        if (eval.SelectedScenario is { } scenario)
        {
            await eval.DeleteScenarioAsync(scenario).ConfigureAwait(true);
        }
    });

    /// <summary>저장소 호출을 감싼다. 실패는 머리 줄 아래 한 줄로 알린다(저장소는 SQLite 라 잠금·디스크 오류가 날 수 있다).</summary>
    private async Task RunAsync(Func<MyBatisStatementEval, Task> action)
    {
        if (_eval is not { } eval)
        {
            return;
        }

        try
        {
            SetActionError(string.Empty);
            await action(eval).ConfigureAwait(true);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            SetActionError(MyBatisText.Format("MyBatis.scenario.failed", "시나리오를 처리하지 못했습니다: {0}", e.Message));
        }
    }

    /// <summary>새 시나리오 이름: 「시나리오 n」(이미 있는 이름과 겹치지 않는 가장 작은 n, 개수 + 1 부터).</summary>
    private static string NewName(MyBatisStatementEval eval)
    {
        for (var n = eval.Scenarios.Count + 1; ; n++)
        {
            var name = MyBatisText.Format("MyBatis.scenario.newName", "시나리오 {0}", n);
            if (!eval.Scenarios.Any(s => string.Equals(s.Name, name, StringComparison.Ordinal)))
            {
                return name;
            }
        }
    }

    /// <summary>복제 이름: 「원래 이름 복사」(이미 있으면 뒤에 번호).</summary>
    private static string CopyName(MyBatisStatementEval eval, string original)
    {
        var name = MyBatisText.Format("MyBatis.scenario.copyName", "{0} 복사", original);
        var candidate = name;
        for (var n = 2; eval.Scenarios.Any(s => string.Equals(s.Name, candidate, StringComparison.Ordinal)); n++)
        {
            candidate = name + " " + n.ToString(CultureInfo.InvariantCulture);
        }

        return candidate;
    }

    private bool SetField(ref string field, string value, string name)
    {
        if (string.Equals(field, value, StringComparison.Ordinal))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(name);
        return true;
    }
}
