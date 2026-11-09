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
    [NotifyPropertyChangedFor(nameof(IsPeriod), nameof(IsParentKey), nameof(IsCustom), nameof(ConditionText), nameof(ConditionError), nameof(HasConditionError),
        nameof(CanTakeCondition))]
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
    [NotifyPropertyChangedFor(nameof(ConditionText), nameof(ConditionError), nameof(HasConditionError))]
    private string? _customWhere;

    /// <summary>기간 규칙의 추가 조건(기간식과 AND). 다른 규칙 종류에서는 저장하지 않는다.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConditionText), nameof(HasExtraWhere), nameof(ConditionError), nameof(HasConditionError))]
    private string? _extraWhere;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConditionText))]
    private ParentLink? _parent;

    /// <summary>
    /// 대상 계정(대상 표 = 이 계정의 <see cref="Table"/>). 상세 칸 콤보에서 고치거나 적는다. 비면 이 규칙의 기본 대상(<see cref="DefaultTarget"/>).
    /// 기본 계정을 따라 바꾸는 때(<see cref="Rebase"/>) 말고 바꾸면 「기본 대상을 따름」이 풀린다(비우면 다시 따른다).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TargetName), nameof(IsTargetSameAsSource), nameof(IsOtherTargetOwner), nameof(OtherTargetOwnerNotice),
        nameof(EffectiveTarget), nameof(IsTargetDefaultShown))]
    private string _targetOwner = string.Empty;

    /// <summary>대상 접속 계정(대문자). 편집 화면이 대상 접속을 고를 때 넣는다. 모르면 빈 글자.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOtherTargetOwner), nameof(OtherTargetOwnerNotice))]
    private string _sessionUser = string.Empty;

    /// <summary>
    /// 이 규칙의 기본 대상 계정: 원본이 기본 원본 계정을 따르면 작업 기본 대상 계정, 아니면 원본 계정 이름 그대로(결정 B-1).
    /// 대상 칸이 비면 이 계정에 쓴다(그리드에 흐린 글자로 보인다). 편집 화면이 기본 계정이 바뀔 때마다 넣는다.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TargetName), nameof(IsTargetSameAsSource), nameof(IsOtherTargetOwner), nameof(OtherTargetOwnerNotice),
        nameof(EffectiveTarget))]
    private string _defaultTarget = string.Empty;

    // 기본 계정을 따라 바꾸는 중(따름 표시를 풀지 않는다).
    private bool _following;

    partial void OnTargetOwnerChanged(string value)
    {
        if (!_following)
        {
            // 사용자가 고치면 기본을 따르지 않는다. 비우면 기본 대상을 쓴다는 뜻이라 다시 따른다.
            FollowsDefaultTarget = string.IsNullOrWhiteSpace(value);
        }
    }

    /// <summary>원본 계정이 작업 기본 원본 계정을 따르는지. 따르면 기본 원본 계정을 바꿀 때 함께 바뀐다(불러올 때·더할 때 정한다).</summary>
    public bool FollowsDefaultSource { get; internal set; }

    /// <summary>대상 계정이 이 규칙의 기본 대상(<see cref="DefaultTarget"/>)을 따르는지. 따르면 기본 계정을 바꿀 때 함께 바뀐다.</summary>
    public bool FollowsDefaultTarget { get; internal set; }

    /// <summary>실제로 쓰는 대상 계정: 칸이 비었으면 <see cref="DefaultTarget"/>.</summary>
    public string EffectiveTarget => TargetOwner.Trim().Length > 0 ? TargetOwner.Trim() : DefaultTarget;

    /// <summary>대상 칸이 비어 기본 대상을 보이는지(그리드에서 흐린 글자).</summary>
    public bool IsTargetDefaultShown => TargetOwner.Trim().Length == 0;

    private TableInfo? _source;

    /// <summary>저장된 규칙 그대로. 계정이 비었으면 빈 글자로 둔다(<see cref="RuleEditorViewModel"/> 는 계정을 푼 생성자를 쓴다).</summary>
    public TableRuleViewModel(TableRule rule)
        : this(rule, rule?.SourceOwner ?? string.Empty, rule?.TargetOwner ?? string.Empty)
    {
    }

    /// <summary>규칙과 그 규칙의 실제 원본·대상 계정(<see cref="CopyOwners.Source"/>·<see cref="CopyOwners.Target"/> 로 푼 값).</summary>
    public TableRuleViewModel(TableRule rule, string sourceOwner, string targetOwner)
    {
        ArgumentNullException.ThrowIfNull(rule);
        Table = rule.Table;
        SourceOwner = sourceOwner;
        WrittenAs = rule.WrittenAs;
        _targetOwner = targetOwner;
        _kind = rule.Kind;
        _action = rule.Action;
        _customWhere = rule.CustomWhere;
        _extraWhere = rule.ExtraWhere;
        _parent = rule.Parent;
        SavedPeriodColumns = rule.PeriodColumns;
        SavedMasks = rule.Masks;
        SavedSkips = rule.SkipColumns;
        RestorePeriodColumns();
        RestoreParent();
    }

    public string Table { get; }

    /// <summary>원본 계정. 표를 더할 때 정해지고 상세 칸에서는 바꾸지 않는다(바꾸려면 표를 다시 더한다). 기본 계정을 바꾸면 따라 바뀔 수 있다.</summary>
    public string SourceOwner { get; private set; }

    /// <summary>시노님으로 더했을 때 사용자가 적은 이름(표시용). 아니면 null.</summary>
    public string? WrittenAs { get; }

    /// <summary>작업에서 불러온 규칙이면 그 규칙(시퀀스 연결의 규칙 키를 저장할 때 다시 잇는다). 새로 더한 표면 null.</summary>
    internal TableRule? Origin { get; set; }

    /// <summary>마지막으로 <see cref="RuleEditorViewModel.BuildJob"/> 이 이 줄에서 만든 규칙. 기준 작업을 바꿀 때(<see cref="RuleEditorViewModel.UpdateBase"/>) <see cref="Origin"/> 을 다시 잇는 데 쓴다.</summary>
    internal TableRule? LastBuilt { get; set; }

    /// <summary>원본 표 이름 <c>계정.표</c>(계정을 모르면 표 이름만). 상세 칸 머리에 쓴다.</summary>
    public string SourceName => SourceOwner.Length > 0 ? SourceOwner + "." + Table : Table;

    /// <summary>대상 표 이름 <c>계정.표</c>.</summary>
    public string TargetName => EffectiveTarget.Length > 0 ? EffectiveTarget + "." + Table : Table;

    /// <summary>상세 칸 머리 옆 작은 글: 「← 시노님 ORDERS」. 시노님으로 더하지 않았으면 빈 글자.</summary>
    public string SynonymText => WrittenAs is { } written ? "← " + DataCopyText.L("시노님") + " " + written : string.Empty;

    public bool HasSynonym => WrittenAs is not null;

    /// <summary>대상 계정이 원본 계정과 같은지(그리드에서 흐린 글자).</summary>
    public bool IsTargetSameAsSource => string.Equals(SourceOwner, EffectiveTarget, StringComparison.Ordinal);

    /// <summary>
    /// 실제 대상 계정(<see cref="EffectiveTarget"/>, 칸이 비면 기본 대상)이 대상 접속 계정이 아닌지(그리드에서 경고색, 상세 칸 안내). 접속을 모르면 거짓.
    /// 계정은 정리한 이름이라(<see cref="CopyOwners.NormalizeOwner"/>) 글자 그대로 비교한다: 문장에 따옴표로 그대로 들어가므로 대소문자가 다르면 다른 계정이다.
    /// </summary>
    public bool IsOtherTargetOwner => SessionUser.Length > 0 && EffectiveTarget.Length > 0
        && !string.Equals(EffectiveTarget, SessionUser, StringComparison.Ordinal);

    /// <summary>접속 계정이 아닌 계정에 쓸 때 상세 칸 안내. 아니면 빈 글자.</summary>
    public string OtherTargetOwnerNotice => IsOtherTargetOwner ? DataCopyText.L(DataCopyText.OtherTargetOwner) : string.Empty;

    /// <summary>
    /// 기본 계정이 바뀔 때(<see cref="RuleEditorViewModel"/> 한 곳에서만 부른다): 원본 계정·이 규칙의 기본 대상을 새 값으로 바꾸고,
    /// 대상이 기본을 따르면(<see cref="FollowsDefaultTarget"/>, 칸이 비어 있지 않으면) 대상 계정도 새 기본 대상으로 바꾼다. 따름 표시는 그대로다.
    /// 원본 계정이 바뀌면 같은 계정 FK 의 부모(부모 계정 = 옛 원본 계정)도 함께 옮긴다. 계정을 넘는 FK 의 부모는 그대로다.
    /// </summary>
    internal void Rebase(string sourceOwner, string defaultTarget)
    {
        if (!string.Equals(SourceOwner, sourceOwner, StringComparison.Ordinal))
        {
            var old = SourceOwner;
            SourceOwner = sourceOwner;
            MoveSameOwnerParents(old, sourceOwner);
            OnPropertyChanged(nameof(SourceOwner));
            OnPropertyChanged(nameof(SourceName));
            OnPropertyChanged(nameof(IsTargetSameAsSource));
            OnPropertyChanged(nameof(ConditionText));
        }

        _following = true;
        try
        {
            DefaultTarget = defaultTarget;
            if (FollowsDefaultTarget && TargetOwner.Trim().Length > 0)
            {
                TargetOwner = defaultTarget;
            }
        }
        finally
        {
            _following = false;
        }
    }

    /// <summary>편집 화면이 기본 대상만 새로 알릴 때(대상 계정은 그대로). 따름 표시를 건드리지 않는다.</summary>
    internal void SetFollowedTarget(string targetOwner, bool follows)
    {
        _following = true;
        try
        {
            TargetOwner = targetOwner;
            FollowsDefaultTarget = follows;
        }
        finally
        {
            _following = false;
        }
    }

    // 부모 계정이 옛 원본 계정인 연결(같은 계정 FK)을 새 원본 계정으로 옮긴다. 계정을 적지 않은 연결(null)은 자식 원본 계정을 따르므로 그대로 둔다.
    private void MoveSameOwnerParents(string oldSource, string newSource)
    {
        if (oldSource.Length == 0)
        {
            return;
        }

        ParentLink Move(ParentLink link) => link.ParentOwner is { Length: > 0 } owner && CopyOwners.SameOwner(owner, oldSource)
            ? link with { ParentOwner = newSource.Length > 0 ? newSource : null }
            : link;

        var selected = Parent;
        ParentLink? movedSelected = selected is null ? null : Move(selected);
        for (var i = 0; i < ParentCandidates.Count; i++)
        {
            var moved = Move(ParentCandidates[i]);
            if (ReferenceEquals(ParentCandidates[i], selected))
            {
                movedSelected = moved;
            }

            if (!ReferenceEquals(moved, ParentCandidates[i]))
            {
                ParentCandidates[i] = moved;
            }
        }

        if (!ReferenceEquals(movedSelected, selected))
        {
            Parent = movedSelected;
        }
    }

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

    public bool SecondFormatVisible => SecondColumn is not null && (SecondColumn.DefaultFormat is not null || IsStringType(SecondColumn.DataType));

    public bool HasColumns => _source is not null;

    /// <summary>원본 열 이름(열 정보를 아직 읽지 않았으면 빈 목록). 변수를 조건에 넣을 때 열 이름을 짐작하는 데 쓴다.</summary>
    public IReadOnlyList<string> ColumnNames => _source is null ? [] : [.. _source.Columns.Select(c => c.Name)];

    /// <summary>
    /// 사용자 조건(WHERE)을 받을 수 있는 규칙인지: 직접 조건·기간(추가 조건)·전체(직접 조건으로 바꿔 넣는다). 부모 키·제외는 받지 않는다.
    /// </summary>
    public bool CanTakeCondition => Kind is RuleKind.All or RuleKind.Period or RuleKind.Custom;

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
        RuleKind.ParentKey => Parent is { } p ? $"{ParentDisplay(p)} 의 키 · {string.Join(", ", p.ChildColumns)}" : "부모 키를 고르세요",
        RuleKind.Custom => string.IsNullOrWhiteSpace(CustomWhere) ? "조건을 입력하세요" : CustomWhere!,
        _ => "복사하지 않음",
    };

    /// <summary>
    /// 지금 규칙 종류에서 쓰는 사용자 조건(직접 조건·기간 규칙의 추가 조건)을 쓸 수 없는 이유. 문제가 없으면 null.
    /// 괄호 밖으로 빠져나가는 조건은 대상 지우기 범위를 넓히므로 사전 점검으로 넘어가지 못하게 한다(<see cref="CopySqlBuilder.CheckUserCondition"/>).
    /// </summary>
    public string? ConditionError => Kind switch
    {
        RuleKind.Custom when !string.IsNullOrWhiteSpace(CustomWhere) => CopySqlBuilder.CheckUserCondition(CustomWhere),
        RuleKind.Period when HasExtraWhere => CopySqlBuilder.CheckUserCondition(ExtraWhere!),
        _ => null,
    };

    public bool HasConditionError => ConditionError is not null;

    /// <summary>추가 조건이 있는가(비어 있지 않은 글).</summary>
    public bool HasExtraWhere => !string.IsNullOrWhiteSpace(ExtraWhere);

    private string PeriodText()
    {
        if (FirstColumn is null)
        {
            return "기간 열을 고르세요";
        }

        var first = ColumnText(FirstColumn, FirstFormat);
        var period = SecondColumn is null ? first : $"{first} 또는 {ColumnText(SecondColumn, SecondFormat)}";
        return HasExtraWhere ? $"{period} · 추가 조건 {ExtraWhere!.Trim()}" : period;
    }

    /// <summary>부모 표 이름: 자식 표와 같은 계정이면 이름만, 다른 계정이면 <c>계정.표</c>.</summary>
    public string ParentDisplay(ParentLink link)
    {
        ArgumentNullException.ThrowIfNull(link);
        return link.ParentOwner is { Length: > 0 } owner && !string.Equals(owner, SourceOwner, StringComparison.Ordinal)
            ? owner + "." + link.ParentTable
            : link.ParentTable;
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
            // 부모 표는 계정.표로 찾고(다른 계정의 같은 이름 표와 섞이지 않게), 이름만 넣은 사전이면 이름으로 찾는다. 후보에는 부모 계정을 담는다.
            var referencedTable = fk.ReferencedTable!;
            if (parents is not null
                && (parents.TryGetValue(referencedTable.Owner + "." + referencedTable.Name, out var parent) || parents.TryGetValue(referencedTable.Name, out parent))
                && parent.Constraints.FirstOrDefault(c => c.Name == fk.ReferencedConstraint) is { } referenced)
            {
                ParentCandidates.Add(new ParentLink(referencedTable.Name, fk.Columns, referenced.Columns, fk.Name)
                {
                    ParentOwner = string.IsNullOrEmpty(referencedTable.Owner) ? null : referencedTable.Owner,
                });
            }
        }

        RestoreParent();

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

    // 저장된 기간 열 이름을 후보 목록의 항목으로 되살린다. 열이 후보에 없으면(열 정보를 아직 안 읽음·이름이 바뀜) 저장된 열을
    // 후보 목록에 넣어 콤보에 그대로 보이게 한다(목록에 없는 SelectedItem 은 콤보가 빈칸으로 그린다).
    // 저장된 문자열 형식은 그 항목의 기본 형식으로 두어 형식 칸도 보인다. 「열 정보 읽기」(SetSource) 뒤에는 실제 열 항목으로 바뀐다.
    private void RestorePeriodColumns()
    {
        FirstColumn = null;
        SecondColumn = null;
        if (SavedPeriodColumns.Count > 0)
        {
            FirstColumn = FindOption(SavedPeriodColumns[0]);
            FirstFormat = SavedPeriodColumns[0].StringFormat;
        }

        if (SavedPeriodColumns.Count > 1)
        {
            SecondColumn = FindOption(SavedPeriodColumns[1]);
            SecondFormat = SavedPeriodColumns[1].StringFormat;
        }
    }

    private PeriodColumnOption FindOption(PeriodColumn saved)
    {
        if (PeriodColumnOptions.FirstOrDefault(o => string.Equals(o.Column, saved.Column, StringComparison.OrdinalIgnoreCase)) is { } found)
        {
            return found;
        }

        var kept = new PeriodColumnOption(saved.Column, string.Empty, false, saved.StringFormat);
        PeriodColumnOptions.Add(kept);
        return kept;
    }

    // 고른(저장된) 부모 키를 후보 목록의 항목으로 되살린다. 후보에 없으면(열 정보를 아직 안 읽음·FK 가 바뀜) 그 연결을 후보에 넣어
    // 콤보에 그대로 보이게 한다(목록에 없는 SelectedItem 은 콤보가 빈칸으로 그린다). 「열 정보 읽기」 뒤에는 같은 FK 의 실제 후보로 바뀐다.
    private void RestoreParent()
    {
        if (Parent is not { } saved)
        {
            return;
        }

        var found = ParentCandidates.FirstOrDefault(c => ReferenceEquals(c, saved))
            ?? ParentCandidates.FirstOrDefault(c => saved.ForeignKeyName is not null
                && string.Equals(c.ForeignKeyName, saved.ForeignKeyName, StringComparison.OrdinalIgnoreCase))
            ?? ParentCandidates.FirstOrDefault(c => string.Equals(c.ParentTable, saved.ParentTable, StringComparison.OrdinalIgnoreCase)
                && CopyOwners.SameOwner(c.ParentOwner ?? SourceOwner, saved.ParentOwner ?? SourceOwner)
                && c.ChildColumns.SequenceEqual(saved.ChildColumns, StringComparer.OrdinalIgnoreCase));
        if (found is null)
        {
            ParentCandidates.Add(saved);
            found = saved;
        }

        Parent = found;
    }

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

    /// <summary>
    /// 마스킹 줄 하나를 뺀다. 열 정보를 아직 읽지 않은 규칙(저장된 마스킹만 있는 상태)이면 저장된 목록에서도 뺀다:
    /// 그러지 않으면 <see cref="ToRule"/> 이 저장된 목록을 그대로 돌려 뺀 마스킹이 되살아난다.
    /// </summary>
    public void RemoveMask(MaskRowViewModel mask)
    {
        ArgumentNullException.ThrowIfNull(mask);
        Masks.Remove(mask);
        SavedMasks = [.. SavedMasks.Where(m => !string.Equals(m.Column, mask.Column, StringComparison.OrdinalIgnoreCase))];
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
            SourceOwner = EmptyToNull(SourceOwner),
            TargetOwner = EmptyToNull(TargetOwner)?.Trim(),
            WrittenAs = WrittenAs,
            PeriodColumns = periodColumns,
            Parent = Kind == RuleKind.ParentKey ? Parent : null,
            CustomWhere = Kind == RuleKind.Custom ? CustomWhere : null,
            ExtraWhere = Kind == RuleKind.Period ? EmptyToNull(ExtraWhere)?.Trim() : null,
            Masks = masks,
            SkipColumns = skips,
        };
    }

    private static string? EmptyToNull(string? text) => string.IsNullOrWhiteSpace(text) ? null : text;

    private static bool IsStringType(string dataType) =>
        dataType.StartsWith("VARCHAR", StringComparison.OrdinalIgnoreCase) || dataType.StartsWith("CHAR", StringComparison.OrdinalIgnoreCase);
}
