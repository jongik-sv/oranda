using Oranda.App.Features.MyBatisWorkspace.Hooks;
using Oranda.App.Features.SqlEditor;
using Oranda.Core.Execution;
using Oranda.Core.MyBatis;
using Oranda.Core.Text;
using Oranda.Language.MyBatis;

namespace Oranda.App.Features.MyBatisWorkspace.Parameters;

/// <summary>
/// 문장 하나의 계산 상태(MB-04·MB-05): 시나리오 목록·고른 시나리오·지금 값·덮어쓰기와 그 값으로 계산한 결과.
/// 값은 「바인드 변수」 창 행(문장 탭의 바인드 목록, <see cref="MyBatisBindSync"/>)의 값 칸 글과 형식이다(2026-10-23 사용자 결정: 별도 파라미터 창 없음).
/// 「바인드 변수」 창·「펼친 SQL」 창·편집기 꾸밈(<c>Editor/EvalDecorator</c>)·실행 명령이 같은 것을 본다(<see cref="MyBatisEvalHub.For"/>).
/// 시나리오를 고르거나 덮어쓰기를 바꾸면 바로, 값·원문을 고치면 300 ms 디바운스 뒤 다시 계산한다. UI 스레드에서 쓴다.
/// 저장소 쓰기는 저장소가 백그라운드에서 하고, 그 <see cref="IMyBatisScenarioStore.Changed"/> 는 UI 스레드로 옮겨 받는다.
/// </summary>
public sealed class MyBatisStatementEval : IDisposable
{
    /// <summary>값·원문 고침 뒤 다시 계산까지 기다리는 시간(MB-05).</summary>
    public static readonly TimeSpan DefaultDebounce = TimeSpan.FromMilliseconds(300);

    private readonly MyBatisEvalHub _hub;
    private readonly Dictionary<string, MyBatisScenarioValue> _values = new(StringComparer.Ordinal);
    private readonly Dictionary<string, BindDataType> _types = new(StringComparer.Ordinal);
    private readonly HashSet<string> _lists = new(StringComparer.Ordinal);
    private readonly HashSet<string> _chosenTypes = new(StringComparer.Ordinal); // 사용자가 형식 콤보로 고른 파라미터
    private readonly HashSet<string> _edited = new(StringComparer.Ordinal); // 고른 시나리오에 아직 저장하지 않은 고친 값
    private readonly Dictionary<TextRange, bool> _overrides = [];
    private readonly Dictionary<SqlEditorViewModel, int> _tracked = [];
    private readonly Dictionary<SqlEditorViewModel, MyBatisBindSync> _syncs = [];
    private IReadOnlyList<MyBatisScenario> _scenarios = [];
    private CancellationTokenSource? _pending;
    private bool _saveSelected;
    private bool _loaded;

    internal MyBatisStatementEval(MyBatisEvalHub hub, IMyBatisWorkspaceSession session, string statementId)
    {
        _hub = hub;
        Session = session;
        StatementId = statementId;
    }

    public IMyBatisWorkspaceSession Session { get; }

    public string StatementId { get; }

    /// <summary>고친 원문이 반영된 문장(세션의 <see cref="IMyBatisWorkspaceSession.ElementOf"/>). 문장이 사라졌으면 null.</summary>
    public MyBatisElement? Element => Session.ElementOf(StatementId);

    /// <summary>문장(include 조각 포함)에서 찾은 파라미터(처음 나온 순서). 계산 전이면 빈 목록.</summary>
    public IReadOnlyList<MyBatisParameter> Parameters { get; private set; } = [];

    /// <summary>지금 값으로 계산한 결과. 계산 전이거나 문장이 없으면 null.</summary>
    public EvalResult? Result { get; private set; }

    /// <summary>계산기가 예외를 냈을 때 그 글(보통 null).</summary>
    public string? Error { get; private set; }

    /// <summary>이 문장의 시나리오(저장소 순서).</summary>
    public IReadOnlyList<MyBatisScenario> Scenarios => _scenarios;

    /// <summary>고른 시나리오. 시나리오가 없으면 null(값은 이 상태에만 있고 저장하지 않는다).</summary>
    public MyBatisScenario? SelectedScenario { get; private set; }

    /// <summary>지금 값(파라미터 이름 → 값 칸 글, <see cref="MyBatisValues.FromCellText"/>). 고른 시나리오 값에서 시작해 「바인드 변수」 창에서 고친다.</summary>
    public IReadOnlyDictionary<string, MyBatisScenarioValue> Values => _values;

    /// <summary>파라미터와 바인드 행 이름이 겹치는 곳(대소문자만 다른 두 이름이 한 행이 됨). 편집기 꾸밈이 물결로 보인다.</summary>
    public IReadOnlyList<(TextRange Range, string Message)> Warnings { get; private set; } = [];

    /// <summary>블록 덮어쓰기(블록 범위 → 켬·끔, 문장 원문 기준). 계산 못 한 식의 켬·끔 고르기가 쓴다.</summary>
    public IReadOnlyDictionary<TextRange, bool> Overrides => _overrides;

    /// <summary>켜진 조건 수(「펼친 SQL」 머리 줄의 「켜진 조건 n/m」): 문장 원문의 if·when 중 켬. 시안 Main 처럼 foreach 는 세지 않는다.</summary>
    public int ActiveConditionCount => Result?.Blocks.Count(b => IsConditionTag(b) && b.IsOn) ?? 0;

    /// <summary>조건 수: 문장 원문의 if·when 블록.</summary>
    public int ConditionCount => Result?.Blocks.Count(IsConditionTag) ?? 0;

    private static bool IsConditionTag(MyBatisBlock block) => block.Fragment is null && block.Tag is "if" or "when";

    /// <summary>다시 계산했다(<see cref="Result"/>·<see cref="Parameters"/>·<see cref="Values"/> 가 바뀌었을 수 있다).</summary>
    public event EventHandler? Changed;

    /// <summary>시나리오 목록이나 고른 시나리오가 바뀌었다.</summary>
    public event EventHandler? ScenariosChanged;

    /// <summary>시나리오를 골라 값이 통째로 바뀌었다(바인드 행 값 칸을 다시 채운다).</summary>
    public event EventHandler? ValuesReplaced;

    /// <summary>파라미터의 행 형식: 사용자가 고른 것, 없으면 jdbcType·test 식에서 추정한 것(<see cref="MyBatisValues.GuessType"/>).</summary>
    public BindDataType TypeOf(string name) => _types.GetValueOrDefault(name, BindDataType.Varchar2);

    /// <summary>값을 목록으로 읽는 파라미터인가(foreach collection 또는 <c>.size()</c> 를 부르는 test).</summary>
    public bool IsList(string name) => _lists.Contains(name);

    /// <summary>
    /// 실행 바인드가 쓸 행 형식(<c>Run/MyBatisRunBinds</c>): 파라미터 경로가 행이 있는 이름이고 목록이 아니면 그 형식, 아니면 null
    /// (목록 항목·조각 안 이름은 값의 형이나 jdbcType 으로 고른다).
    /// </summary>
    public BindDataType? RowTypeOf(string parameterPath) =>
        _types.TryGetValue(parameterPath, out var type) && !_lists.Contains(parameterPath) ? type : null;

    /// <summary>
    /// 행 형식을 고친다(바인드 창 형식 콤보). 300 ms 뒤 다시 계산한다.
    /// <paramref name="byUser"/> 면 사용자가 고른 형식으로 기억해 시나리오를 골라도 바꾸지 않는다(<see cref="SelectScenario"/>).
    /// </summary>
    public void SetType(string name, BindDataType type, bool byUser = true)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (byUser)
        {
            _chosenTypes.Add(name);
        }

        if (_types.TryGetValue(name, out var old) && old == type)
        {
            return;
        }

        _types[name] = type;
        ScheduleRecompute();
    }

    /// <summary>처음 쓸 때 한 번: 저장소에서 시나리오를 읽고 첫 시나리오를 고른 뒤 계산한다. 저장소가 없으면 빈 값으로 계산만 한다.</summary>
    public async Task EnsureLoadedAsync(CancellationToken ct = default)
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        Recompute();
        await ReloadScenariosAsync(selectId: null, ct).ConfigureAwait(true);
    }

    /// <summary>
    /// 시나리오를 고른다: 값을 그 시나리오 값으로 바꾸고 바로 다시 계산한다(디바운스 없음).
    /// 값 종류가 숫자·참거짓이면 사용자가 형식을 고르지 않은 행의 형식도 그 종류로 바꾼다(시안 「전체 조건」 :empNo = NUMBER, 2026-10-23 조정자 결정 A).
    /// </summary>
    public void SelectScenario(MyBatisScenario? scenario)
    {
        CancelPending();
        _saveSelected = false;
        _edited.Clear();
        SelectedScenario = scenario;
        _values.Clear();
        if (scenario is not null)
        {
            // 로그 붙여 넣기 등이 만든 종류 있는 값도 같은 뜻의 칸 글로 바꿔 둔다.
            foreach (var (name, value) in scenario.Values)
            {
                _values[name] = MyBatisValues.FromCellText(MyBatisValues.CellText(value));
                if (!_chosenTypes.Contains(name) && !_lists.Contains(name) && TypeOfKind(value.Kind) is { } type)
                {
                    _types[name] = type;
                }
            }
        }

        ValuesReplaced?.Invoke(this, EventArgs.Empty);
        ScenariosChanged?.Invoke(this, EventArgs.Empty);
        Recompute();
    }

    /// <summary>
    /// 값 하나를 고친다. 300 ms 뒤 다시 계산하고, <paramref name="byUser"/> 면 고른 시나리오에 그 이름의 값만 저장한다
    /// (고치지 않은 값은 시나리오에 있던 종류 그대로 둔다).
    /// </summary>
    public void SetValue(string name, MyBatisScenarioValue value, bool byUser = true)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(value);
        _values[name] = value;
        if (byUser && SelectedScenario is not null)
        {
            _edited.Add(name);
            _saveSelected = true;
        }

        ScheduleRecompute();
    }

    /// <summary>블록 덮어쓰기를 정한다(null = 계산대로). 바로 다시 계산한다.</summary>
    public void SetOverride(TextRange blockRange, bool? on)
    {
        if (on is { } value)
        {
            _overrides[blockRange] = value;
        }
        else
        {
            _overrides.Remove(blockRange);
        }

        // 디바운스 중인 값 고침이 있으면 버리지 않고 함께 반영·저장한다.
        CancelPending();
        Commit();
    }

    /// <summary>문장 탭 글이 바뀔 때 다시 계산하도록 탭을 지켜본다(활성 탭·편집기 꾸밈이 부른다). 돌려받은 것을 Dispose 하면 그만 본다.</summary>
    public IDisposable Track(SqlEditorViewModel tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        // 활성 탭 따라가기와 편집기 꾸밈이 같은 탭을 함께 볼 수 있어 센다.
        var count = _tracked.GetValueOrDefault(tab);
        _tracked[tab] = count + 1;
        if (count == 0)
        {
            tab.TextChanged += OnTabTextChanged;
            _syncs[tab] = new MyBatisBindSync(this, tab.Binds);
        }

        return new Untrack(this, tab);
    }

    /// <summary>값·원문 고침 뒤 다시 계산을 예약한다(이미 예약돼 있으면 처음부터 다시 센다).</summary>
    public void ScheduleRecompute()
    {
        CancelPending();
        var cts = _pending = new CancellationTokenSource();
        _ = RunDebouncedAsync(cts);
    }

    /// <summary>예약된 다시 계산을 지금 한다(시험·실행 직전). 예약이 없으면 아무것도 안 한다.</summary>
    public void Flush()
    {
        if (_pending is null)
        {
            return;
        }

        CancelPending();
        Commit();
    }

    /// <summary>지금 값·덮어쓰기·원문으로 다시 계산한다.</summary>
    public void Recompute()
    {
        var element = Element;
        var evaluator = _hub.Evaluator;
        if (element is null || evaluator is null)
        {
            Parameters = [];
            Warnings = [];
            Result = null;
            Error = null;
            Changed?.Invoke(this, EventArgs.Empty);
            return;
        }

        try
        {
            Parameters = evaluator.ExtractParameters(element, Session.Fragments);
            Classify(element.Text);
            Result = evaluator.Evaluate(element, Session.Fragments, EvaluatorValues(), _overrides.Count == 0 ? null : _overrides);
            Error = null;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Result = null;
            Error = e.Message;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    // ---- 시나리오 목록 ----------------------------------------------------------------------

    /// <summary>
    /// 지금 값으로 새 시나리오를 만들어 목록 끝에 두고 고른다. 저장소가 없으면 null.
    /// </summary>
    public async Task<MyBatisScenario?> AddScenarioAsync(string name, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (_hub.Store is not { } store)
        {
            return null;
        }

        var saved = await store.SaveAsync(
            new MyBatisScenario
            {
                MapperPath = Session.MapperPath,
                Namespace = Session.MapperNamespace,
                StatementId = StatementId,
                Name = name,
                Values = new Dictionary<string, MyBatisScenarioValue>(_values, StringComparer.Ordinal),
            },
            ct).ConfigureAwait(true);
        await ReloadScenariosAsync(saved.Id, ct).ConfigureAwait(true);
        return saved;
    }

    public async Task RenameScenarioAsync(MyBatisScenario scenario, string name, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (_hub.Store is not { } store)
        {
            return;
        }

        await store.RenameAsync(scenario.Id, name, ct).ConfigureAwait(true);
        await ReloadScenariosAsync(SelectedScenario?.Id, ct).ConfigureAwait(true);
    }

    /// <summary>같은 값으로 복제해 원본 바로 뒤에 두고 고른다.</summary>
    public async Task<MyBatisScenario?> DuplicateScenarioAsync(MyBatisScenario scenario, string newName, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        ArgumentException.ThrowIfNullOrWhiteSpace(newName);
        if (_hub.Store is not { } store)
        {
            return null;
        }

        await SaveSelectedNowAsync(ct).ConfigureAwait(true);
        var copy = await store.DuplicateAsync(scenario.Id, newName, ct).ConfigureAwait(true);
        await ReloadScenariosAsync(copy.Id, ct).ConfigureAwait(true);
        return copy;
    }

    /// <summary>지우고, 지운 것이 고른 시나리오면 그 자리의 다음(없으면 앞) 시나리오를 고른다.</summary>
    public async Task DeleteScenarioAsync(MyBatisScenario scenario, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        if (_hub.Store is not { } store)
        {
            return;
        }

        long? next = SelectedScenario?.Id;
        if (next == scenario.Id)
        {
            var index = IndexOf(scenario.Id);
            next = index + 1 < _scenarios.Count ? _scenarios[index + 1].Id : index > 0 ? _scenarios[index - 1].Id : null;
            _saveSelected = false;
        }

        await store.DeleteAsync(scenario.Id, ct).ConfigureAwait(true);
        await ReloadScenariosAsync(next, ct).ConfigureAwait(true);
    }

    /// <summary>
    /// 저장소에서 목록을 다시 읽는다(저장소 <see cref="IMyBatisScenarioStore.Changed"/> 도 이것을 부른다).
    /// <paramref name="selectId"/> 가 있으면 그것을, 없으면 지금 고른 것(같은 ID)을, 그것도 없으면 첫 시나리오를 고른다.
    /// 같은 시나리오를 계속 고르고 있으면 표에서 고친 값을 덮어쓰지 않는다.
    /// </summary>
    public async Task ReloadScenariosAsync(long? selectId, CancellationToken ct = default)
    {
        if (_hub.Store is not { } store)
        {
            return;
        }

        var list = await store.ListAsync(Session.MapperPath, Session.MapperNamespace, StatementId, ct).ConfigureAwait(true);
        _scenarios = list;
        var wanted = selectId ?? SelectedScenario?.Id;
        var pick = list.FirstOrDefault(s => s.Id == wanted) ?? (list.Count > 0 ? list[0] : null);
        if (pick is not null && SelectedScenario is { } current && current.Id == pick.Id && selectId is null)
        {
            SelectedScenario = pick; // 같은 시나리오: 이름 등만 새로, 값은 표의 것을 둔다
            ScenariosChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        SelectScenario(pick);
    }

    /// <summary>예약된 다시 계산을 버리고 지켜보던 탭에서 떨어진다.</summary>
    public void Dispose()
    {
        CancelPending();
        foreach (var tab in _tracked.Keys)
        {
            tab.TextChanged -= OnTabTextChanged;
        }

        foreach (var sync in _syncs.Values)
        {
            sync.Dispose();
        }

        _tracked.Clear();
        _syncs.Clear();
    }

    // ---- 내부 -------------------------------------------------------------------------------

    // 새 파라미터의 처음 형식·목록 여부를 정하고, 행 이름이 겹치는 곳을 모은다.
    private void Classify(string statementText)
    {
        var rows = new Dictionary<string, string>(StringComparer.Ordinal);
        var warnings = new List<(TextRange, string)>();
        foreach (var p in Parameters)
        {
            var (type, isList) = MyBatisValues.GuessType(p, statementText);
            _types.TryAdd(p.Name, type);
            if (isList)
            {
                _lists.Add(p.Name);
            }
            else
            {
                _lists.Remove(p.Name);
            }

            var row = MyBatisValues.RowName(p);
            if (rows.TryGetValue(row, out var first) && p.Locations.Count > 0)
            {
                warnings.Add((p.Locations[0], $"「{p.Name}」 와 「{first}」 는 대소문자만 달라 바인드 변수 창에서 한 행({row})이 됩니다."));
            }
            else
            {
                rows.TryAdd(row, p.Name);
            }
        }

        Warnings = warnings;
    }

    // 시나리오 값 종류가 정하는 행 형식(숫자·참거짓만). 글자·널·목록은 행 형식을 건드리지 않는다.
    private static BindDataType? TypeOfKind(MyBatisScenarioValueKind kind) => kind switch
    {
        MyBatisScenarioValueKind.Number => BindDataType.Number,
        MyBatisScenarioValueKind.Boolean => BindDataType.Boolean,
        _ => null,
    };

    // 값 칸 글을 행 형식대로 계산기 값으로(MyBatisBindText). 점이 든 이름은 평평한 열쇠 그대로 넘긴다.
    private Dictionary<string, object?> EvaluatorValues()
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (name, value) in _values)
        {
            values[name] = MyBatisBindText.Parse(value.Kind == MyBatisScenarioValueKind.Null ? null : MyBatisValues.CellText(value), TypeOf(name), IsList(name));
        }

        return values;
    }

    private int IndexOf(long id)
    {
        for (int i = 0; i < _scenarios.Count; i++)
        {
            if (_scenarios[i].Id == id)
            {
                return i;
            }
        }

        return -1;
    }

    private async Task RunDebouncedAsync(CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(_hub.Debounce, cts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (ReferenceEquals(_pending, cts))
        {
            _pending = null;
            cts.Dispose();
            Commit();
        }
    }

    private void Commit()
    {
        Recompute();
        if (_saveSelected)
        {
            _ = SaveSelectedNowAsync(CancellationToken.None);
        }
    }

    /// <summary>표에서 고친 값을 고른 시나리오에 쓴다(고친 것이 없으면 아무것도 안 한다).</summary>
    private async Task SaveSelectedNowAsync(CancellationToken ct)
    {
        if (!_saveSelected || SelectedScenario is not { } scenario || _hub.Store is not { } store)
        {
            return;
        }

        _saveSelected = false;
        var values = new Dictionary<string, MyBatisScenarioValue>(scenario.Values, StringComparer.Ordinal);
        foreach (var name in _edited)
        {
            values[name] = _values.GetValueOrDefault(name, MyBatisScenarioValue.Null);
        }

        _edited.Clear();
        try
        {
            var saved = await store.SaveAsync(scenario with { Values = values }, ct).ConfigureAwait(true);

            // 저장하는 동안 다른 시나리오를 골랐으면 고른 것을 되돌리지 않는다(목록은 저장소 Changed 로 다시 읽는다).
            if (SelectedScenario?.Id == saved.Id)
            {
                SelectedScenario = saved;
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException and not OperationCanceledException)
        {
            Error = "시나리오를 저장하지 못했습니다: " + e.Message;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private void CancelPending()
    {
        if (_pending is { } cts)
        {
            _pending = null;
            cts.Cancel();
            cts.Dispose();
        }
    }

    private void OnTabTextChanged(object? sender, EventArgs e) => ScheduleRecompute();

    private sealed class Untrack(MyBatisStatementEval owner, SqlEditorViewModel tab) : IDisposable
    {
        private bool _done;

        public void Dispose()
        {
            if (_done)
            {
                return;
            }

            _done = true;
            var count = owner._tracked.GetValueOrDefault(tab);
            if (count > 1)
            {
                owner._tracked[tab] = count - 1;
            }
            else if (owner._tracked.Remove(tab))
            {
                tab.TextChanged -= owner.OnTabTextChanged;
                if (owner._syncs.Remove(tab, out var sync))
                {
                    sync.Dispose();
                }
            }
        }
    }
}
