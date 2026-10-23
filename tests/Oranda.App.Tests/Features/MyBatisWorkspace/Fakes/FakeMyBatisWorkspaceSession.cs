using Oranda.App.Features.MyBatisWorkspace.Hooks;
using Oranda.App.Features.SqlEditor;
using Oranda.Core.MyBatis;
using Oranda.Language.MyBatis;

namespace Oranda.App.Tests.Features.MyBatisWorkspace.Fakes;

/// <summary>
/// <see cref="IMyBatisWorkspaceSession"/> 가짜(TSK-01-02, design.md 9장 「시험 이음새」, 동결). 계약 전부를 메모리 값으로 구현한다.
/// 시험이 <see cref="IsDirty"/>·<see cref="SelectedStatementId"/> 를 바꾸면 <see cref="StateChanged"/> 가 난다.
/// 작업 공간에 붙이는 자리는 internal 이다: <c>workspace.MyBatis = new FakeMyBatisWorkspaceSession()</c>.
/// </summary>
internal sealed class FakeMyBatisWorkspaceSession : IMyBatisWorkspaceSession
{
    private bool _isDirty;
    private string? _selectedStatementId;

    public string MapperPath { get; init; } = "/tmp/UserMapper.xml";

    public string? MapperNamespace { get; init; } = "demo.UserMapper";

    public IMyBatisMapperDocument Document { get; set; } = new FakeMapperDocument();

    public string BaselineHash { get; set; } = "fake-hash";

    public bool IsDirty
    {
        get => _isDirty;
        set
        {
            if (_isDirty != value)
            {
                _isDirty = value;
                RaiseStateChanged();
            }
        }
    }

    public string? SelectedStatementId
    {
        get => _selectedStatementId;
        set
        {
            if (_selectedStatementId != value)
            {
                _selectedStatementId = value;
                RaiseStateChanged();
            }
        }
    }

    public IMyBatisFragmentResolver Fragments { get; set; } = new NoFragments();

    /// <summary>문장 탭 ↔ 문장 id 대응(시험이 채운다).</summary>
    public Dictionary<SqlEditorViewModel, string> StatementIds { get; } = [];

    /// <summary>고친 원문이 반영된 요소(시험이 채운다).</summary>
    public Dictionary<string, MyBatisElement> Elements { get; } = [];

    public List<MapperEdit> Edits { get; } = [];

    /// <summary><see cref="StateChanged"/> 구독자 수(구독을 풀었는지 보는 용도).</summary>
    public int StateChangedSubscribers { get; private set; }

    public string? StatementIdOf(SqlEditorViewModel tab) => StatementIds.GetValueOrDefault(tab);

    public MyBatisElement? ElementOf(string statementId) => Elements.GetValueOrDefault(statementId);

    public IReadOnlyList<MapperEdit> PendingEdits() => Edits;

    public void SelectStatement(string? statementId) => SelectedStatementId = statementId;

    /// <summary><see cref="OpenStatement"/> 를 부른 id 기록.</summary>
    public List<string> OpenedStatementIds { get; } = [];

    /// <summary><see cref="OpenStatement"/> 가 돌려줄 탭(시험이 채운다). 없는 id 면 null.</summary>
    public Dictionary<string, SqlEditorViewModel> OpenableTabs { get; } = [];

    /// <summary><see cref="BeginNewStatementAsync"/> 를 부른 횟수.</summary>
    public int BeginNewStatementCalls { get; private set; }

    /// <summary><see cref="Relocate"/> 로 받은 경로 기록(<see cref="MapperPath"/> 는 init 전용이라 바꾸지 않는다).</summary>
    public List<string> RelocatedPaths { get; } = [];

    public SqlEditorViewModel? OpenStatement(string statementId)
    {
        OpenedStatementIds.Add(statementId);
        return OpenableTabs.GetValueOrDefault(statementId);
    }

    public Task BeginNewStatementAsync(CancellationToken ct)
    {
        BeginNewStatementCalls++;
        return Task.CompletedTask;
    }

    public void Relocate(string newMapperPath) => RelocatedPaths.Add(newMapperPath);

    public void Rebase(string newText, IReadOnlyCollection<string> writtenStatementIds)
    {
        Document = new FakeMapperDocument(newText);
        Edits.Clear();
        IsDirty = false;
    }

    public void ApplyDiskText(string diskText, IReadOnlyCollection<string> keepMine) => Document = new FakeMapperDocument(diskText);

    public event EventHandler? StateChanged
    {
        add
        {
            _stateChanged += value;
            StateChangedSubscribers++;
        }
        remove
        {
            _stateChanged -= value;
            StateChangedSubscribers--;
        }
    }

    private EventHandler? _stateChanged;

    /// <summary>상태가 바뀌었다고 알린다(<see cref="IsDirty"/>·<see cref="SelectedStatementId"/> 를 거치지 않고 알릴 때).</summary>
    public void RaiseStateChanged() => _stateChanged?.Invoke(this, EventArgs.Empty);

    private sealed class NoFragments : IMyBatisFragmentResolver
    {
        public MyBatisFragment? Resolve(string refid, string? currentNamespace) => null;
    }
}
