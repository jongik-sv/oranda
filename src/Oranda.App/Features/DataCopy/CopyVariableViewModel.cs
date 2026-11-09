using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.App.ViewModels;
using Oranda.Core.Transfer.Copy;

namespace Oranda.App.Features.DataCopy;

/// <summary>변수 형식 고르기 목록의 한 줄.</summary>
public sealed record VariableTypeOption(CopyVariableType Type, string Display)
{
    public static IReadOnlyList<VariableTypeOption> All { get; } =
    [
        new(CopyVariableType.Text, "문자"),
        new(CopyVariableType.Number, "숫자"),
        new(CopyVariableType.Date, "날짜"),
    ];

    public static VariableTypeOption For(CopyVariableType type) => All.First(o => o.Type == type);
}

/// <summary>
/// 작업 변수 한 줄(규칙 편집 「변수」). 값은 칸에 줄바꿈·쉼표로 나눠 적고, 저장할 때 나눈 목록(<see cref="CopyVariables.SplitValues"/>)으로 담는다.
/// </summary>
public sealed partial class CopyVariableViewModel : ViewModelBase
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Problem), nameof(HasProblem), nameof(UsageText))]
    private string _name = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Problem), nameof(HasProblem))]
    private VariableTypeOption _typeOption = VariableTypeOption.All[0];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Problem), nameof(HasProblem), nameof(CountText), nameof(UsageText))]
    private string _valuesText = string.Empty;

    public CopyVariableViewModel()
    {
    }

    public CopyVariableViewModel(CopyVariable variable)
    {
        ArgumentNullException.ThrowIfNull(variable);
        _name = variable.Name;
        _typeOption = VariableTypeOption.For(variable.Type);
        _valuesText = CopyVariables.JoinValues(variable.Values);
    }

    public IReadOnlyList<string> Values => CopyVariables.SplitValues(ValuesText);

    public CopyVariable ToVariable() => new(CopyVariables.NormalizeName(Name), TypeOption.Type, Values);

    /// <summary>값 수(나눈 뒤, 겹치는 값 뺌).</summary>
    public string CountText => Values.Count.ToString("N0", CultureInfo.InvariantCulture) + "개";

    /// <summary>조건에 쓰는 꼴: 값이 하나면 <c>= :이름</c>, 여럿이면 <c>IN (:이름)</c>.</summary>
    public string UsageText
    {
        get
        {
            var name = CopyVariables.NormalizeName(Name);
            return name.Length == 0 ? string.Empty : Values.Count > 1 ? $"IN (:{name})" : $"= :{name}";
        }
    }

    public string? Problem => CopyVariables.Problem(ToVariable());

    public bool HasProblem => Problem is not null;
}
