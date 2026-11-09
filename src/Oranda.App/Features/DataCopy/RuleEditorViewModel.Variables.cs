using System.ComponentModel;
using Oranda.Core.Transfer.Copy;

namespace Oranda.App.Features.DataCopy;

/// <summary>변수 줄의 「조건에 넣기」 결과. <paramref name="NeedsAttention"/> 면 상태 줄에 경고색으로 알린다(넣지 못했거나 열 이름을 고쳐야 함).</summary>
public sealed record VariableInsertResult(string Message, bool NeedsAttention);

/// <summary>규칙 편집의 작업 변수 — 변수 줄 「조건에 넣기」(고른 규칙의 WHERE 조건에 <c>열 IN (:이름)</c> 을 붙인다).</summary>
public sealed partial class RuleEditorViewModel
{
    /// <summary>열 이름을 못 찾았을 때 넣는 자리표.</summary>
    public const string ColumnPlaceholder = "열이름";

    // 변수 이름 끝에 흔히 붙는 「여러 개」 꼬리. 떼어 낸 이름으로 열을 찾는다(CSM_DOC_LIST → CSM_DOC).
    private static readonly string[] ListSuffixes = ["_LIST", "_LST", "_IDS", "_NOS", "_CODES", "_CDS", "_VALUES", "_VALS", "_ARR", "_SET"];

    /// <summary>고른 규칙이 변수 조건을 받을 수 있는지(「조건에 넣기」 단추를 켠다).</summary>
    public bool CanInsertVariableCondition => SelectedRule is { CanTakeCondition: true };

    partial void OnSelectedRuleChanged(TableRuleViewModel? oldValue, TableRuleViewModel? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.PropertyChanged -= OnSelectedRuleKindChanged;
        }

        if (newValue is not null)
        {
            newValue.PropertyChanged += OnSelectedRuleKindChanged;
        }

        OnPropertyChanged(nameof(CanInsertVariableCondition));
    }

    private void OnSelectedRuleKindChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TableRuleViewModel.Kind))
        {
            OnPropertyChanged(nameof(CanInsertVariableCondition));
        }
    }

    /// <summary>
    /// 변수 조건(<c>열 IN (:이름)</c>, 값이 하나면 <c>열 = :이름</c> — 변수 줄 안내 <see cref="CopyVariableViewModel.UsageText"/> 와 같은 꼴)을
    /// 고른 규칙의 WHERE 에 넣는다. 직접 조건은 그 조건, 기간 규칙은 추가 조건, 「전체」 규칙은 직접 조건으로 바꿔 넣는다.
    /// 조건이 이미 있으면 <c> AND </c> 로 뒤에 붙이고, 같은 <c>:이름</c> 이 이미 있으면 넣지 않는다.
    /// </summary>
    public VariableInsertResult InsertVariableCondition(CopyVariableViewModel variable)
    {
        ArgumentNullException.ThrowIfNull(variable);
        var name = CopyVariables.NormalizeName(variable.Name);
        if (name.Length == 0)
        {
            return new(DataCopyText.L("변수 이름을 먼저 적으세요."), true);
        }

        if (SelectedRule is not { } rule)
        {
            return new(DataCopyText.L("조건을 넣을 테이블 규칙을 먼저 고르세요."), true);
        }

        if (!rule.CanTakeCondition)
        {
            return new($"{rule.Table} 은(는) 「{KindName(rule.Kind)}」 규칙이라 조건을 넣을 수 없습니다. 직접 조건이나 기간 규칙을 고르세요.", true);
        }

        var current = rule.Kind == RuleKind.Period ? rule.ExtraWhere : rule.Kind == RuleKind.Custom ? rule.CustomWhere : null;
        if (!string.IsNullOrWhiteSpace(current) && CopyVariables.BindNames(current).Contains(name, StringComparer.Ordinal))
        {
            return new($"{rule.Table} 조건에 이미 :{name} 이(가) 있습니다.", true);
        }

        var column = GuessColumn(name, rule.ColumnNames);
        var condition = $"{column ?? ColumnPlaceholder} {variable.UsageText}";
        var combined = string.IsNullOrWhiteSpace(current) ? condition : current.TrimEnd() + " AND " + condition;

        var switched = false;
        if (rule.Kind == RuleKind.All)
        {
            rule.ChangeKind(RuleKind.Custom); // 대상 처리도 범위 지우기로 맞춘다(전체 지우기로 남지 않게)
            switched = true;
        }

        if (rule.Kind == RuleKind.Period)
        {
            rule.ExtraWhere = combined;
        }
        else
        {
            rule.CustomWhere = combined;
        }

        var where = rule.Kind == RuleKind.Period ? "추가 조건" : "직접 조건";
        var message = switched
            ? $"{rule.Table} 규칙을 「전체」에서 「직접 조건」으로 바꾸고 {condition} 을(를) 넣었습니다."
            : $"{rule.Table} {where}에 {condition} 을(를) 넣었습니다.";
        return column is null
            ? new(message + $" 열 이름({ColumnPlaceholder})을 고쳐 주세요.", true)
            : new(message, false);
    }

    /// <summary>
    /// 변수 이름으로 열을 짐작한다(대소문자 무시): 같은 이름 → 끝 S 를 뗀 이름(CSM_DOC_NOS → CSM_DOC_NO) → 흔한 꼬리(_LIST·_IDS 등)를 뗀 이름. 없으면 null.
    /// </summary>
    public static string? GuessColumn(string variableName, IReadOnlyList<string> columns)
    {
        ArgumentNullException.ThrowIfNull(columns);
        var name = CopyVariables.NormalizeName(variableName);
        if (name.Length == 0 || columns.Count == 0)
        {
            return null;
        }

        var candidates = new List<string> { name };
        if (name.Length > 1 && name.EndsWith('S'))
        {
            candidates.Add(name[..^1]);
        }

        foreach (var suffix in ListSuffixes)
        {
            if (name.Length > suffix.Length && name.EndsWith(suffix, StringComparison.Ordinal))
            {
                candidates.Add(name[..^suffix.Length]);
            }
        }

        foreach (var candidate in candidates)
        {
            if (columns.FirstOrDefault(c => string.Equals(c, candidate, StringComparison.OrdinalIgnoreCase)) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private static string KindName(RuleKind kind) => kind switch
    {
        RuleKind.ParentKey => "부모 키",
        RuleKind.Exclude => "제외",
        _ => kind.ToString(),
    };
}
