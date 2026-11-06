using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.App.ViewModels;
using Oranda.Core.Metadata;
using Oranda.Core.Transfer.Copy;

namespace Oranda.App.Features.DataCopy;

/// <summary>기간 열 고르기 목록의 한 줄. 생성 시각으로 보이는 열은 「생성 시각」 표시가 붙는다(설계 6.2.2).</summary>
public sealed record PeriodColumnOption(string Column, string DataType, bool IsCreatedAt, string? DefaultFormat)
{
    public string Display => IsCreatedAt ? $"{Column} · 생성 시각" : Column;
}

/// <summary>마스킹 규칙 한 줄.</summary>
public sealed partial class MaskRowViewModel : ViewModelBase
{
    [ObservableProperty]
    private MaskKind _kind;

    [ObservableProperty]
    private string _value = string.Empty;

    [ObservableProperty]
    private int _count;

    public MaskRowViewModel(string column, string dataType, MaskRule? rule = null)
    {
        Column = column;
        AllowedKinds = MaskFunctions.AllowedKinds(dataType);
        _kind = rule?.Kind ?? AllowedKinds[0];
        _value = rule?.Value ?? string.Empty;
        _count = rule?.Count ?? 0;
    }

    public string Column { get; }

    public IReadOnlyList<MaskKind> AllowedKinds { get; }

    public bool NeedsValue => Kind == MaskKind.Fixed;

    public bool NeedsCount => Kind is MaskKind.KeepFirst or MaskKind.KeepLast;

    partial void OnKindChanged(MaskKind value)
    {
        OnPropertyChanged(nameof(NeedsValue));
        OnPropertyChanged(nameof(NeedsCount));
    }

    public MaskRule ToRule() => new(Column, Kind, Kind == MaskKind.Fixed ? Value : null, NeedsCount ? Count : 0);
}

/// <summary>건너뛸 열 한 줄.</summary>
public sealed partial class SkipColumnViewModel(string column, bool skipped) : ViewModelBase
{
    [ObservableProperty]
    private bool _isSkipped = skipped;

    public string Column { get; } = column;
}

/// <summary>
/// 규칙 표의 한 줄(테이블 하나)과 오른쪽 패널이 편집하는 내용. 기간 열 후보·문자열 형식·부모 키·마스킹 규칙을 다룬다.
/// 열 정보(<see cref="TableInfo"/>)는 원본 접속에서 읽어 <see cref="SetSource"/> 로 넣는다. 아직 못 읽었으면 저장된 규칙 값만 보인다.
/// </summary>
public sealed partial class TableRuleViewModel : ViewModelBase
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPeriod), nameof(IsParentKey), nameof(IsCustom), nameof(ConditionText))]
    private RuleKind _kind;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMerge))]
    private TargetAction _action;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConditionText), nameof(FirstFormatVisible))]
    private PeriodColumnOption? _firstColumn;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConditionText))]
    private string? _firstFormat;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSecondColumn), nameof(SecondFormatVisible), nameof(CanAddSecondColumn))]
    private PeriodColumnOption? _secondColumn;

    [ObservableProperty]
    private string? _secondFormat;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConditionText))]
    private string? _customWhere;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConditionText))]
    private ParentLink? _parent;

    private TableInfo? _source;

    public TableRuleViewModel(TableRule rule)
    {
        Table = rule.Table;
        _kind = rule.Kind;
        _action = rule.Action;
        _customWhere = rule.CustomWhere;
        _parent = rule.Parent;
        SavedPeriodColumns = rule.PeriodColumns;
        SavedMasks = rule.Masks;
        SavedSkips = rule.SkipColumns;
        RestorePeriodColumns();
    }

    public string Table { get; }

    /// <summary>대상 처리가 PK 기준 MERGE 일 때만 「원본에서 지워진 행은 MERGE 가 남깁니다」 안내를 보인다(설계 6.2).</summary>
    public bool IsMerge => Action == TargetAction.MergeByPk;

    public bool IsPeriod => Kind == RuleKind.Period;

    public bool IsParentKey => Kind == RuleKind.ParentKey;

    public bool IsCustom => Kind == RuleKind.Custom;

    public ObservableCollection<PeriodColumnOption> PeriodColumnOptions { get; } = [];

    public ObservableCollection<MaskRowViewModel> Masks { get; } = [];

    public ObservableCollection<SkipColumnViewModel> Skips { get; } = [];

    /// <summary>부모 키 규칙에 쓸 수 있는 원본 FK 목록(자동 연결 후보).</summary>
    public ObservableCollection<ParentLink> ParentCandidates { get; } = [];

    public static IReadOnlyList<string> StringFormats => DateStringFormat.KnownFormats;

    public bool HasSecondColumn => SecondColumn is not null;

    public bool CanAddSecondColumn => IsPeriod && SecondColumn is null && PeriodColumnOptions.Count > 1;

    public bool FirstFormatVisible => FirstColumn?.DefaultFormat is not null || (FirstColumn is not null && IsStringType(FirstColumn.DataType));

    public bool SecondFormatVisible => SecondColumn is not null && IsStringType(SecondColumn.DataType);

    public bool HasColumns => _source is not null;

    public IReadOnlyList<PeriodColumn> SavedPeriodColumns { get; private set; }

    private IReadOnlyList<MaskRule> SavedMasks { get; set; }

    private IReadOnlyList<string> SavedSkips { get; set; }

    /// <summary>
    /// 규칙 표의 「조건」 칸: 기간 열과 형식(<c>ORDER_DT · YYYYMMDD 문자열</c>), 두 번째 열이 있으면 「 또는 」 로 잇는다.
    /// 실제 비교는 시작 포함·끝 제외다(설계 10장).
    /// </summary>
    public string ConditionText => Kind switch
    {
        RuleKind.All => "모든 행",
        RuleKind.Period => PeriodText(),
        RuleKind.ParentKey => Parent is { } p ? $"{p.ParentTable} 의 키 · {string.Join(", ", p.ChildColumns)}" : "부모 키를 고르세요",
        RuleKind.Custom => string.IsNullOrWhiteSpace(CustomWhere) ? "조건을 입력하세요" : CustomWhere!,
        _ => "복사하지 않음",
    };

    private string PeriodText()
    {
        if (FirstColumn is null)
        {
            return "기간 열을 고르세요";
        }

        var first = ColumnText(FirstColumn, FirstFormat);
        return SecondColumn is null ? first : $"{first} 또는 {ColumnText(SecondColumn, SecondFormat)}";
    }

    private static string ColumnText(PeriodColumnOption column, string? format) =>
        string.IsNullOrEmpty(format) ? column.Column : $"{column.Column} · {format} 문자열";

    /// <summary>원본 테이블의 열 정보를 넣고 기간 열 후보·마스킹 열·건너뛸 열 목록을 만든다.</summary>
    public void SetSource(TableInfo source, IReadOnlyDictionary<string, TableInfo>? parents = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        _source = source;
        PeriodColumnOptions.Clear();
        foreach (var c in PeriodColumnRanker.Rank(source.Columns))
        {
            PeriodColumnOptions.Add(new PeriodColumnOption(c.Column.Name, c.Column.DataType, c.IsCreatedAt, c.DefaultFormat));
        }

        ParentCandidates.Clear();
        foreach (var fk in source.Constraints.Where(c => c.Kind == ConstraintKind.ForeignKey && c.ReferencedTable is not null))
        {
            // 부모 쪽 열은 부모 테이블의 참조 제약(r_constraint_name)에서 읽는다. 부모 정보를 못 읽은 FK 는 후보에서 뺀다.
            if (parents is not null && parents.TryGetValue(fk.ReferencedTable!.Name, out var parent)
                && parent.Constraints.FirstOrDefault(c => c.Name == fk.ReferencedConstraint) is { } referenced)
            {
                ParentCandidates.Add(new ParentLink(fk.ReferencedTable.Name, fk.Columns, referenced.Columns, fk.Name));
            }
        }

        Masks.Clear();
        foreach (var rule in SavedMasks)
        {
            var type = source.Columns.FirstOrDefault(c => string.Equals(c.Name, rule.Column, StringComparison.OrdinalIgnoreCase))?.DataType ?? "VARCHAR2";
            Masks.Add(new MaskRowViewModel(rule.Column, type, rule));
        }

        Skips.Clear();
        foreach (var column in source.Columns.Where(c => !c.IsVirtual))
        {
            Skips.Add(new SkipColumnViewModel(column.Name, SavedSkips.Contains(column.Name, StringComparer.OrdinalIgnoreCase)));
        }

        RestorePeriodColumns();
        OnPropertyChanged(nameof(HasColumns));
        OnPropertyChanged(nameof(CanAddSecondColumn));
    }

    // 저장된 기간 열 이름을 후보 목록의 항목으로 되살린다. 열이 후보에 없으면(이름이 바뀜) 저장된 값을 그대로 보인다.
    private void RestorePeriodColumns()
    {
        FirstColumn = null;
        SecondColumn = null;
        if (SavedPeriodColumns.Count > 0)
        {
            FirstColumn = FindOption(SavedPeriodColumns[0].Column);
            FirstFormat = SavedPeriodColumns[0].StringFormat;
        }

        if (SavedPeriodColumns.Count > 1)
        {
            SecondColumn = FindOption(SavedPeriodColumns[1].Column);
            SecondFormat = SavedPeriodColumns[1].StringFormat;
        }
    }

    private PeriodColumnOption FindOption(string column) =>
        PeriodColumnOptions.FirstOrDefault(o => string.Equals(o.Column, column, StringComparison.OrdinalIgnoreCase))
        ?? new PeriodColumnOption(column, string.Empty, false, null);

    partial void OnFirstColumnChanged(PeriodColumnOption? value)
    {
        // 문자열 열을 고르면 열 길이로 추정한 형식이 기본값이다. DATE·TIMESTAMP 열은 형식이 없다.
        if (value is not null && (FirstFormat is null || value.DefaultFormat is not null))
        {
            FirstFormat = value.DefaultFormat;
        }

        OnPropertyChanged(nameof(CanAddSecondColumn));
    }

    partial void OnSecondColumnChanged(PeriodColumnOption? value)
    {
        if (value is not null)
        {
            SecondFormat = value.DefaultFormat;
        }
    }

    partial void OnKindChanged(RuleKind value) => OnPropertyChanged(nameof(CanAddSecondColumn));

    /// <summary>두 번째 기간 열을 더한다(최대 두 개). 첫 열과 다른 첫 후보가 들어간다.</summary>
    public void AddSecondColumn()
    {
        if (SecondColumn is not null || FirstColumn is null)
        {
            return;
        }

        SecondColumn = PeriodColumnOptions.FirstOrDefault(o => o.Column != FirstColumn.Column);
    }

    public void RemoveSecondColumn() => SecondColumn = null;

    public void AddMask(string column)
    {
        if (_source is null || Masks.Any(m => string.Equals(m.Column, column, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var type = _source.Columns.FirstOrDefault(c => string.Equals(c.Name, column, StringComparison.OrdinalIgnoreCase))?.DataType ?? "VARCHAR2";
        Masks.Add(new MaskRowViewModel(column, type));
    }

    public IEnumerable<string> MaskableColumns =>
        _source?.Columns.Where(c => !Masks.Any(m => string.Equals(m.Column, c.Name, StringComparison.OrdinalIgnoreCase))).Select(c => c.Name) ?? [];

    /// <summary>원본 테이블에 기본키(P)가 있는지. 규칙 종류별 기본 대상 처리를 정할 때 쓴다.</summary>
    public bool HasPrimaryKey => _source?.Constraints.Any(c => c.Kind == ConstraintKind.PrimaryKey) ?? false;

    /// <summary>규칙 종류를 바꿀 때 대상 처리도 그 종류의 기본값으로 맞춘다(설계 6.2).</summary>
    public void ChangeKind(RuleKind kind)
    {
        Kind = kind;
        Action = TargetActions.DefaultFor(kind, HasPrimaryKey);
        if (kind == RuleKind.Period && FirstColumn is null)
        {
            FirstColumn = PeriodColumnOptions.FirstOrDefault();
        }

        if (kind == RuleKind.ParentKey && Parent is null)
        {
            Parent = ParentCandidates.FirstOrDefault();
        }
    }

    public TableRule ToRule()
    {
        var periodColumns = new List<PeriodColumn>();
        if (Kind == RuleKind.Period)
        {
            if (FirstColumn is not null)
            {
                periodColumns.Add(new PeriodColumn(FirstColumn.Column, EmptyToNull(FirstFormat)));
            }

            if (SecondColumn is not null)
            {
                periodColumns.Add(new PeriodColumn(SecondColumn.Column, EmptyToNull(SecondFormat)));
            }
        }

        // 열 정보를 아직 못 읽었으면(원본 세션 없음) 저장된 값을 그대로 돌려준다.
        var masks = _source is null ? SavedMasks : Masks.Select(m => m.ToRule()).ToList();
        var skips = _source is null ? SavedSkips : Skips.Where(s => s.IsSkipped).Select(s => s.Column).ToList();
        return new TableRule(Table, Kind, Action)
        {
            PeriodColumns = periodColumns,
            Parent = Kind == RuleKind.ParentKey ? Parent : null,
            CustomWhere = Kind == RuleKind.Custom ? CustomWhere : null,
            Masks = masks,
            SkipColumns = skips,
        };
    }

    private static string? EmptyToNull(string? text) => string.IsNullOrWhiteSpace(text) ? null : text;

    private static bool IsStringType(string dataType) =>
        dataType.StartsWith("VARCHAR", StringComparison.OrdinalIgnoreCase) || dataType.StartsWith("CHAR", StringComparison.OrdinalIgnoreCase);
}
