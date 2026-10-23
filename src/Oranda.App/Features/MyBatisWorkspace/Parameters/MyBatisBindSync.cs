using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Oranda.App.Features.Binds;

namespace Oranda.App.Features.MyBatisWorkspace.Parameters;

/// <summary>
/// 문장 탭의 「바인드 변수」 목록(<see cref="BindVariableStore"/>, 탭마다 하나)을 문장의 파라미터에 맞춘다(TSK-03-02, 2026-10-23 사용자 결정).
/// <list type="bullet">
/// <item>행 = 문장(include 조각 포함)에서 찾은 파라미터 전부. 지금 값으로 블록이 꺼져 펼친 SQL 에서 사라져도 행은 남는다.
/// 문장에서 사라진 파라미터의 행은 지운다(동기화가 만든 행만, 사용자가 직접 더한 행·복구한 행은 둔다).</item>
/// <item>새 행의 형식은 추정값(<see cref="MyBatisValues.GuessType"/>), 값 칸은 지금 값. 그 뒤로 행의 값·형식을 고치면 계산 상태로 보낸다(300 ms 디바운스).</item>
/// <item>시나리오를 고르면(<see cref="MyBatisStatementEval.ValuesReplaced"/>) 값 칸을 그 시나리오 값으로, 형식 콤보를 계산 상태의 형식으로 다시 채운다.</item>
/// </list>
/// 행 이름은 창이 대문자로 바꾸므로 원래 파라미터 이름과의 대응을 여기서 들고 있다. UI 스레드에서 쓴다.
/// </summary>
internal sealed class MyBatisBindSync : IDisposable
{
    private readonly MyBatisStatementEval _eval;
    private readonly BindVariableStore _store;
    // 동기화가 만든 행(동기화를 다시 붙여도 알아보도록 행에 표시한다). 이 표시가 없는 행은 파라미터가 사라져도 지우지 않는다.
    private static readonly ConditionalWeakTable<BindVariableRow, object> Created = [];

    private readonly Dictionary<BindVariableRow, string> _managed = []; // 맡은 행 → 파라미터 이름
    private bool _writing;

    public MyBatisBindSync(MyBatisStatementEval eval, BindVariableStore store)
    {
        _eval = eval;
        _store = store;
        _eval.Changed += OnEvalChanged;
        _eval.ValuesReplaced += OnValuesReplaced;
        _store.Rows.CollectionChanged += OnRowsChanged;
        Sync();
    }

    /// <summary>이 동기화가 맡은 행의 파라미터 이름(시험용). 없으면 null.</summary>
    internal string? ParameterOf(BindVariableRow row) => _managed.GetValueOrDefault(row);

    public void Dispose()
    {
        _eval.Changed -= OnEvalChanged;
        _eval.ValuesReplaced -= OnValuesReplaced;
        _store.Rows.CollectionChanged -= OnRowsChanged;
        foreach (var row in _managed.Keys)
        {
            row.PropertyChanged -= OnRowChanged;
        }

        _managed.Clear();
    }

    private void OnEvalChanged(object? sender, EventArgs e) => Sync();

    private void OnValuesReplaced(object? sender, EventArgs e)
    {
        _writing = true;
        try
        {
            foreach (var (row, name) in _managed)
            {
                row.Type = _eval.TypeOf(name); // 시나리오 값 종류를 따라간 형식(사용자가 고른 형식은 그대로)
                row.Text = CellOf(name);
            }
        }
        finally
        {
            _writing = false;
        }
    }

    // 사용자가 창에서 행을 지우면 맡은 행에서 뺀다(다음 계산 때 다시 생긴다).
    private void OnRowsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action is NotifyCollectionChangedAction.Remove or NotifyCollectionChangedAction.Reset or NotifyCollectionChangedAction.Replace)
        {
            foreach (var row in _managed.Keys.Where(r => !_store.Rows.Contains(r)).ToList())
            {
                row.PropertyChanged -= OnRowChanged;
                _managed.Remove(row);
            }
        }
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_writing || sender is not BindVariableRow row || !_managed.TryGetValue(row, out var name))
        {
            return;
        }

        if (e.PropertyName == nameof(BindVariableRow.Text))
        {
            _eval.SetValue(name, MyBatisValues.FromCellText(row.Text));
        }
        else if (e.PropertyName == nameof(BindVariableRow.TypeChoice))
        {
            _eval.SetType(name, row.Type);
        }
    }

    private void Sync()
    {
        _writing = true;
        try
        {
            var wanted = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in _eval.Parameters)
            {
                var rowName = MyBatisValues.RowName(p);
                if (!wanted.Add(rowName))
                {
                    continue; // 대소문자만 다른 이름: 한 행(계산 상태가 물결 경고를 낸다)
                }

                if (_managed.FirstOrDefault(m => m.Value == p.Name).Key is { } mine && mine.Name == rowName)
                {
                    continue;
                }

                var row = _store.Find(rowName);
                if (row is null)
                {
                    row = _store.Add(rowName);
                    Created.AddOrUpdate(row, Created);
                    row.Type = _eval.TypeOf(p.Name);
                    row.Text = CellOf(p.Name);
                }
                else if (!_managed.ContainsKey(row))
                {
                    // 탭에 이미 있던 행(복구·사용자가 더한 행): 그 값·형식을 계산 상태로 가져온다(시나리오에 저장하지 않는다).
                    _eval.SetType(p.Name, row.Type, byUser: false);
                    if (!_eval.Values.ContainsKey(p.Name) && row.Text.Length > 0)
                    {
                        _eval.SetValue(p.Name, MyBatisValues.FromCellText(row.Text), byUser: false);
                    }
                }

                if (_managed.TryAdd(row, p.Name))
                {
                    row.PropertyChanged += OnRowChanged;
                }
                else
                {
                    _managed[row] = p.Name;
                }
            }

            foreach (var (row, _) in _managed.Where(m => !wanted.Contains(m.Key.Name)).ToList())
            {
                row.PropertyChanged -= OnRowChanged;
                _managed.Remove(row);
                if (Created.TryGetValue(row, out _))
                {
                    _store.Remove(row);
                }
            }
        }
        finally
        {
            _writing = false;
        }
    }

    private string CellOf(string name) =>
        _eval.Values.TryGetValue(name, out var value) && value.Kind != Oranda.Core.MyBatis.MyBatisScenarioValueKind.Null ? MyBatisValues.CellText(value) : string.Empty;
}
