using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.App.ViewModels;
using Oranda.Core.Connection;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;
using Oranda.Core.Transfer.Copy;

namespace Oranda.App.Features.DataCopy;

/// <summary>접속 고르기 목록의 한 줄. 운영 접속은 대상으로는 비활성이고 이유를 툴팁으로 알린다(설계 10장).</summary>
public sealed record ConnectionOption(ConnectionProfile Profile, bool IsEnabled, string? Tooltip)
{
    public string Display => $"{Profile.Name} ({Profile.User})";
}

/// <summary>상세 칸 「대상 계정」 콤보의 한 줄. 고르면 칸 글자가 <see cref="Owner"/> 가 된다(<see cref="ToString"/>).</summary>
public sealed record TargetOwnerOption(string Owner, string Hint)
{
    public override string ToString() => Owner;
}

/// <summary>기간 종류 고르기 목록의 한 줄.</summary>
public sealed record PeriodKindOption(PeriodKind Kind, string Display);

/// <summary>
/// 규칙 편집 단계(1단계): 원본·대상 접속, 소유자, 기간, 읽기 일관성, 옵션, 테이블별 규칙. 기간은 상대값으로 저장하고
/// 작업을 열 때 오늘 날짜로 다시 계산한 날짜를 항상 화면에 보인다(설계 6.4).
/// </summary>
public sealed partial class RuleEditorViewModel : ViewModelBase
{
    private readonly DataCopyServices _services;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private ConnectionOption? _source;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRecreate), nameof(AvailableActions), nameof(TargetReadOnlyNotice), nameof(HasTargetReadOnlyNotice))]
    private ConnectionOption? _target;

    [ObservableProperty]
    private string _sourceOwner = string.Empty;

    // 대상을 고르기 전에 표를 추가해 원본 표 소유자로 임시로 채운 대상 소유자. 대상을 고르면 그 사용자로 바꾼다.
    private string? _provisionalTargetOwner;

    partial void OnTargetChanged(ConnectionOption? value)
    {
        if (_provisionalTargetOwner is { } provisional && value is { Profile.User: { Length: > 0 } user })
        {
            if (string.Equals(TargetOwner, provisional, StringComparison.Ordinal))
            {
                TargetOwner = user.ToUpperInvariant();
            }

            _provisionalTargetOwner = null;
        }

        foreach (var rule in Rules)
        {
            rule.SessionUser = TargetSessionUser;
        }

        OnPropertyChanged(nameof(TargetSessionUser));
    }

    /// <summary>
    /// 작업의 기본 대상 계정. 표 이름만 적어 더한 표(기본 원본 계정의 표)의 대상 계정 기본값이다.
    /// 바꾸면 옛 기본과 같던 규칙만 따라 바뀐다(<see cref="RebaseRules"/>).
    /// </summary>
    [ObservableProperty]
    private string _targetOwner = string.Empty;

    // 작업을 올리는 동안에는 기본 계정이 바뀌어도 규칙을 따라 바꾸지 않는다(규칙은 저장된 계정 그대로).
    private bool _loading;

    partial void OnSourceOwnerChanged(string? oldValue, string newValue) => RebaseRules(oldValue, newValue, TargetOwner, TargetOwner);

    partial void OnTargetOwnerChanged(string? oldValue, string newValue) => RebaseRules(SourceOwner, SourceOwner, oldValue, newValue);

    /// <summary>
    /// 기본 계정을 바꿀 때 규칙 계정을 맞추는 단 한 곳: 원본 계정이 옛 기본 원본 계정과 같던 규칙은 새 기본 원본 계정으로,
    /// 대상 계정이 옛 기본 대상 계정과 같던 규칙은 새 기본 대상 계정으로 바꾼다. 다른 계정 규칙(시노님으로 더한 APP2.ORDERS 등)은 그대로다.
    /// </summary>
    private void RebaseRules(string? oldSource, string newSource, string? oldTarget, string newTarget)
    {
        if (_loading)
        {
            return;
        }

        foreach (var rule in Rules)
        {
            var source = CopyOwners.SameOwner(rule.SourceOwner, oldSource ?? string.Empty) ? newSource : rule.SourceOwner;
            var target = CopyOwners.SameOwner(rule.TargetOwner, oldTarget ?? string.Empty) ? newTarget : rule.TargetOwner;
            rule.Rebase(source, target);
        }
    }

    /// <summary>대상 접속 계정(대문자). 대상을 고르지 않았으면 빈 글자.</summary>
    public string TargetSessionUser => Target?.Profile.User is { Length: > 0 } user ? user.ToUpperInvariant() : string.Empty;

    /// <summary>
    /// 상세 칸 「대상 계정」 콤보의 고를 거리: 원본과 같음, 기본 대상, 접속 계정(같은 계정은 한 줄로 합친다). 다른 계정은 직접 적는다.
    /// </summary>
    public IReadOnlyList<TargetOwnerOption> TargetOwnerOptions(TableRuleViewModel rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        var options = new List<(string Owner, List<string> Hints)>();
        void Add(string owner, string hint)
        {
            if (string.IsNullOrWhiteSpace(owner))
            {
                return;
            }

            if (options.FirstOrDefault(o => CopyOwners.SameOwner(o.Owner, owner)) is { Owner: not null } found)
            {
                found.Hints.Add(hint);
                return;
            }

            options.Add((owner, [hint]));
        }

        Add(rule.SourceOwner, "원본과 같음");
        Add(TargetOwner, "기본 대상");
        Add(TargetSessionUser, "접속 계정");
        return [.. options.Select(o => new TargetOwnerOption(o.Owner, string.Join(" · ", o.Hints.Select(DataCopyText.L))))];
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PeriodText), nameof(IsCustomPeriod))]
    private PeriodKindOption _periodChoice;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PeriodText))]
    private DateTime? _customFrom;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PeriodText))]
    private DateTime? _customTo;

    /// <summary>「모든 테이블을 같은 시점 기준으로 읽기」(설계 8.1). 새 작업은 켜져 있다.</summary>
    [ObservableProperty]
    private bool _consistentRead = true;

    [ObservableProperty]
    private int _batchSize = 5000;

    [ObservableProperty]
    private CommitMode _commit = CommitMode.PerTable;

    [ObservableProperty]
    private bool _disableTriggersByDefault;

    [ObservableProperty]
    private int _maxFailedRows = 100;

    [ObservableProperty]
    private long _sequenceGapConfirm = 10_000_000;

    [ObservableProperty]
    private TableRuleViewModel? _selectedRule;

    [ObservableProperty]
    private string _notice = string.Empty;

    private CopyJob? _loaded;

    public RuleEditorViewModel(DataCopyServices services)
    {
        _services = services;
        PeriodKinds =
        [
            new(PeriodKind.None, "기간 없음"),
            new(PeriodKind.LastWeek, "최근 1주"),
            new(PeriodKind.LastMonth, "최근 1달"),
            new(PeriodKind.Last3Months, "최근 3달"),
            new(PeriodKind.PreviousMonth, "지난달"),
            new(PeriodKind.Custom, "직접 지정"),
        ];
        _periodChoice = PeriodKinds[0];
    }

    public IReadOnlyList<PeriodKindOption> PeriodKinds { get; }

    public ObservableCollection<ConnectionOption> SourceOptions { get; } = [];

    public ObservableCollection<ConnectionOption> TargetOptions { get; } = [];

    public ObservableCollection<TableRuleViewModel> Rules { get; } = [];

    public IReadOnlyList<RuleKind> RuleKinds { get; } = [RuleKind.All, RuleKind.Period, RuleKind.ParentKey, RuleKind.Custom, RuleKind.Exclude];

    public IReadOnlyList<CommitMode> CommitModes { get; } = [CommitMode.PerTable, CommitMode.PerBatch, CommitMode.AtEnd];

    public bool IsCustomPeriod => PeriodChoice.Kind == PeriodKind.Custom;

    /// <summary>대상이 개발 접속일 때만 「테이블 다시 만들기」를 고를 수 있다(설계 6.2.3).</summary>
    public bool CanRecreate => Target is { } t && CopyTargetGuard.CanRecreate(t.Profile);

    /// <summary>고른 대상 접속이 읽기 전용이면 복사할 수 없다는 안내. 아니면 빈 글자.</summary>
    public string TargetReadOnlyNotice => Target is { Profile.ReadOnly: true } ? DataCopyText.ReadOnlyTarget : string.Empty;

    public bool HasTargetReadOnlyNotice => TargetReadOnlyNotice.Length > 0;

    public IReadOnlyList<TargetAction> AvailableActions =>
        CanRecreate
            ? [TargetAction.DeleteAllInsert, TargetAction.DeleteRangeInsert, TargetAction.AppendOnly, TargetAction.MergeByPk, TargetAction.Recreate]
            : [TargetAction.DeleteAllInsert, TargetAction.DeleteRangeInsert, TargetAction.AppendOnly, TargetAction.MergeByPk];

    /// <summary>오늘 날짜로 계산한 기간 범위 글자(예: <c>2026-10-07 ~ 2026-11-07</c>). 끝 날짜는 포함하지 않는다. 기간이 없으면 안내 글.</summary>
    public string PeriodText
    {
        get
        {
            var resolved = ResolvePeriod();
            return resolved is { From: { } from, To: { } to }
                ? string.Create(CultureInfo.InvariantCulture, $"{from:yyyy-MM-dd} ~ {to:yyyy-MM-dd}")
                : "기간 없음";
        }
    }

    public CopyPeriod Period => new(PeriodChoice.Kind, CustomFrom, CustomTo);

    public ResolvedPeriod ResolvePeriod() => PeriodCalculator.Resolve(Period, _services.Time.GetLocalNow().DateTime);

    /// <summary>접속 목록을 채운다. 대상 목록의 운영 접속은 선택할 수 없고 이유를 툴팁으로 보인다.</summary>
    public void SetConnections(IReadOnlyList<ConnectionProfile> profiles)
    {
        var keepSource = Source?.Profile;
        var keepTarget = Target?.Profile;
        SourceOptions.Clear();
        TargetOptions.Clear();
        foreach (var profile in profiles.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            SourceOptions.Add(new ConnectionOption(profile, true, profile.ReadOnly ? "읽기 전용 접속" : null));
            var allowed = CopyTargetGuard.IsAllowedTarget(profile);
            TargetOptions.Add(new ConnectionOption(profile, allowed, allowed ? (profile.ReadOnly ? DataCopyText.ReadOnlyTarget : null) : DataCopyText.ProductionTarget));
        }

        // 목록을 다시 채워도 고른 접속은 같은 접속으로 이어 둔다(목록을 비우면 선택이 풀리기 때문).
        Source = keepSource is null ? null : SourceOptions.FirstOrDefault(o => o.Profile.Id == keepSource.Id && o.Profile.Name == keepSource.Name);
        Target = keepTarget is null ? null : TargetOptions.FirstOrDefault(o => o.Profile.Id == keepTarget.Id && o.Profile.Name == keepTarget.Name);
    }

    /// <summary>
    /// 저장된 작업을 편집 화면에 올린다. 접속은 호출한 쪽이 다시 이은 결과(<paramref name="source"/>·<paramref name="target"/>)를 받는다.
    /// 다시 이은 대상이 없으면(운영 차단 포함) 대상을 비우고 알림을 남긴다.
    /// </summary>
    public void Load(CopyJob job, ConnectionProfile? source, ConnectionProfile? target, string notice = "")
    {
        ArgumentNullException.ThrowIfNull(job);
        _loaded = job;
        _provisionalTargetOwner = null;
        Name = job.Name;
        Source = source is null ? null : SourceOptions.FirstOrDefault(o => o.Profile.Id == source.Id && o.Profile.Name == source.Name);
        Target = target is null ? null : TargetOptions.FirstOrDefault(o => o.Profile.Id == target.Id && o.Profile.Name == target.Name);
        _loading = true;
        try
        {
            SourceOwner = job.SourceOwner;
            TargetOwner = job.TargetOwner;
        }
        finally
        {
            _loading = false;
        }

        PeriodChoice = PeriodKinds.First(k => k.Kind == job.Period.Kind);
        CustomFrom = job.Period.From;
        CustomTo = job.Period.To;
        ConsistentRead = job.ConsistentRead;
        BatchSize = job.Options.BatchSize;
        Commit = job.Options.Commit;
        DisableTriggersByDefault = job.Options.DisableTriggersByDefault;
        MaxFailedRows = job.Options.MaxFailedRows;
        SequenceGapConfirm = job.Options.SequenceGapConfirm;
        Rules.Clear();
        // 규칙마다 실제 계정을 풀어 둔다(비어 있던 계정 = 작업 기본 계정). 저장할 때는 늘 계정을 적어 저장한다.
        foreach (var rule in job.Tables)
        {
            Rules.Add(new TableRuleViewModel(rule, CopyOwners.Source(job, rule), CopyOwners.Target(job, rule))
            {
                Origin = rule,
                SessionUser = TargetSessionUser,
            });
        }

        SelectedRule = Rules.FirstOrDefault();
        Notice = notice;
        OnPropertyChanged(nameof(PeriodText));
    }

    /// <summary>화면에 없는 필드(마지막 실행·식별자 등)의 기준 작업을 바꾼다. 실행·이름 바꾸기 뒤 옛 값으로 덮어쓰지 않게 한다.</summary>
    public void UpdateBase(CopyJob job) => _loaded = job;

    /// <summary>화면의 값을 작업 모델로 만든다. 접속 참조는 이름과 사용자명, 이 PC 의 ID 는 힌트로만 담는다(설계 6.3).</summary>
    public CopyJob BuildJob()
    {
        var baseJob = _loaded ?? throw new InvalidOperationException("불러온 작업이 없다.");
        var sourceOwner = SourceOwner;
        var targetOwner = string.IsNullOrWhiteSpace(TargetOwner) ? SourceOwner : TargetOwner;
        var defaults = baseJob with { SourceOwner = sourceOwner, TargetOwner = targetOwner };

        // 규칙마다 계정을 적어 저장한다(묶음 B 설계 1장): 비었으면 기본 원본 계정, 대상은 결정 B-1 의 기본값.
        TableRule Explicit(TableRuleViewModel vm)
        {
            var rule = vm.ToRule();
            var source = rule.SourceOwner ?? sourceOwner;
            return rule with { SourceOwner = source, TargetOwner = rule.TargetOwner ?? CopyOwners.MapTarget(defaults, source) };
        }

        var tables = Rules.Select(Explicit).ToList();
        var built = defaults with { Tables = tables };

        // 시퀀스 연결의 표는 규칙 키다. 기본 계정을 바꿔 키가 달라진 규칙은 새 키로 잇는다.
        var sequences = baseJob.Sequences.Select(seq =>
            CopyOwners.Find(baseJob, seq.Table) is { } before && Rules.FirstOrDefault(r => ReferenceEquals(r.Origin, before)) is { } now
                ? seq with { Table = CopyOwners.Key(built, now.SourceOwner.Length > 0 ? now.SourceOwner : sourceOwner, now.Table) }
                : seq).ToList();
        return baseJob with
        {
            Name = Name,
            Source = Source is { } s ? new ConnectionRef(s.Profile.Name, s.Profile.User, s.Profile.Id) : baseJob.Source,
            Target = Target is { } t ? new ConnectionRef(t.Profile.Name, t.Profile.User, t.Profile.Id) : new ConnectionRef(string.Empty, string.Empty),
            SourceOwner = sourceOwner,
            TargetOwner = targetOwner,
            Period = Period,
            Tables = tables,
            Sequences = sequences,
            Options = new CopyOptions
            {
                BatchSize = BatchSize,
                Commit = Commit,
                DisableTriggersByDefault = DisableTriggersByDefault,
                MaxFailedRows = MaxFailedRows,
                SequenceGapConfirm = SequenceGapConfirm,
            },
            ConsistentRead = ConsistentRead,
        };
    }

    /// <summary>
    /// 점검으로 넘어가기 전 입력 확인. 문제가 있으면 그 이유 글자, 없으면 null.
    /// 대상이 운영 접속이면 여기서도 막는다(선택 목록·재연결·가져오기·실행기와 함께 네 곳 중 하나).
    /// </summary>
    public string? Validate()
    {
        if (Source is null)
        {
            return DataCopyText.SourceMissing;
        }

        if (Target is null)
        {
            return DataCopyText.TargetMissing;
        }

        if (!CopyTargetGuard.IsAllowedTarget(Target.Profile))
        {
            return DataCopyText.ProductionTarget;
        }

        // 읽기 전용 접속은 정책 관문이 모든 쓰기를 거부하므로, 사전 점검 전에 이유를 알리고 막는다.
        if (Target.Profile.ReadOnly)
        {
            return DataCopyText.ReadOnlyTarget;
        }

        // 규칙 사이 모순: 같은 원본 표를 두 번, 한 대상 표에 두 규칙.
        var job = _loaded is null ? null : BuildJob();
        if (job is not null && CopyOwners.Problems(job) is { Count: > 0 } problems)
        {
            return problems[0];
        }

        // 같은 접속에서 어떤 규칙이 쓰는(지우는) 대상 표가 어떤 규칙이 읽는 원본 표이면 원본 행을 지운다(표 단위, 규칙 사이까지 본다).
        if (job is not null && Source.Profile.Id == Target.Profile.Id && string.Equals(Source.Profile.Name, Target.Profile.Name, StringComparison.Ordinal)
            && CopyOwners.TargetsReadAsSource(job) is { Count: > 0 } overlaps)
        {
            return DataCopyText.SameSourceTables(overlaps.Select(o => o.Table));
        }

        // 「테이블 다시 만들기」는 개발 접속에서만 쓴다. 대상을 바꾸거나 가져온 작업에 남은 값은 여기서 막는다.
        if (!CopyTargetGuard.CanRecreate(Target.Profile) && Rules.FirstOrDefault(r => r.Kind != RuleKind.Exclude && r.Action == TargetAction.Recreate) is { } recreate)
        {
            return DataCopyText.RecreateNotAllowed(recreate.Table);
        }

        // 괄호 밖으로 빠져나가는 조건(주석·세미콜론 포함)은 대상 지우기 범위를 넓히므로 넘어가지 않는다.
        if (Rules.FirstOrDefault(r => r.Kind != RuleKind.Exclude && r.HasConditionError) is { } invalid)
        {
            return DataCopyText.InvalidCondition(invalid.Table, invalid.ConditionError!);
        }

        return Rules.Any(r => r.Kind != RuleKind.Exclude) ? null : DataCopyText.NoTables;
    }

    /// <summary>스키마 브라우저 「데이터 복사에 추가」 또는 창의 표 추가. 이미 있는 표면 그 규칙을 선택만 한다.</summary>
    public Task<TableRuleViewModel> AddTableAsync(DbObjectRef table, ISession? sourceSession, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(table);
        return AddTableAsync(new CopyTableName(table.Owner, table.Name), sourceSession, ct);
    }

    /// <summary>
    /// 원본에서 풀린 계정·표(<see cref="CopyTableInput.ResolveAsync"/>)를 규칙으로 더한다. 같은 원본 계정·표의 규칙이 있으면(규칙 키가 같으면) 그 규칙을 선택만 한다.
    /// 대상 계정은 결정 B-1: 원본 계정이 기본 원본 계정이면 기본 대상 계정, 아니면 원본 계정 이름 그대로.
    /// </summary>
    public async Task<TableRuleViewModel> AddTableAsync(CopyTableName table, ISession? sourceSession, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(table);
        if (Rules.FirstOrDefault(r => string.Equals(EffectiveSource(r), table.Owner, StringComparison.Ordinal)
                                      && string.Equals(r.Table, table.Table, StringComparison.Ordinal)) is { } existing)
        {
            SelectedRule = existing;
            return existing;
        }

        if (string.IsNullOrWhiteSpace(SourceOwner))
        {
            SourceOwner = table.Owner;
        }

        // 대상 소유자는 대상 접속의 사용자(스키마)로 시작한다. 대상을 아직 고르지 않았으면(스키마 브라우저에서 새 작업으로 추가) 원본 표 소유자로
        // 임시로 채워 두고, 나중에 대상을 고르면 그 사용자로 바꾼다(그 사이 사용자가 고친 값은 그대로 둔다).
        if (string.IsNullOrWhiteSpace(TargetOwner))
        {
            if (Target is { Profile.User: { Length: > 0 } user })
            {
                TargetOwner = user.ToUpperInvariant();
            }
            else
            {
                TargetOwner = table.Owner;
                _provisionalTargetOwner = table.Owner;
            }
        }

        var target = string.Equals(table.Owner, SourceOwner, StringComparison.Ordinal) ? TargetOwner : table.Owner;
        var rule = new TableRuleViewModel(new TableRule(table.Table, RuleKind.All, TargetAction.DeleteAllInsert) { WrittenAs = table.WrittenAs }, table.Owner, target)
        {
            SessionUser = TargetSessionUser,
        };
        Rules.Add(rule);
        SelectedRule = rule;
        await LoadColumnsAsync(rule, new DbObjectRef(table.Owner, table.Table, "TABLE"), sourceSession, ct).ConfigureAwait(true);

        // 기간이 정해진 작업에 업무 날짜 열이 있는 표를 더하면 기간 규칙으로 시작한다. 없으면 모든 행.
        var kind = PeriodChoice.Kind != PeriodKind.None && rule.PeriodColumnOptions.Count > 0 ? RuleKind.Period : RuleKind.All;
        rule.ChangeKind(kind);
        return rule;
    }

    /// <summary>원본 세션이 열려 있으면 표 열 정보와 부모 표 정보를 읽어 규칙에 넣는다. 못 읽으면 알림만 남기고 규칙은 유지한다.</summary>
    public async Task LoadColumnsAsync(TableRuleViewModel rule, DbObjectRef table, ISession? sourceSession, CancellationToken ct)
    {
        if (sourceSession is null)
        {
            return;
        }

        if (_services.Metadata is not { } metadata)
        {
            Notice = DataCopyText.MetadataMissing;
            return;
        }

        try
        {
            var info = await metadata.GetTableAsync(sourceSession, table, ct).ConfigureAwait(true);
            // 부모 표는 계정.표로 담는다(다른 계정의 같은 이름 표가 섞이지 않게).
            var parents = new Dictionary<string, TableInfo>(StringComparer.Ordinal);
            foreach (var fk in info.Constraints.Where(c => c.Kind == ConstraintKind.ForeignKey && c.ReferencedTable is not null))
            {
                var parentRef = fk.ReferencedTable!;
                var key = parentRef.Owner + "." + parentRef.Name;
                if (!parents.ContainsKey(key))
                {
                    parents[key] = await metadata.GetTableAsync(sourceSession, parentRef, ct).ConfigureAwait(true);
                }
            }

            rule.SetSource(info, parents);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Notice = $"테이블 {table.Name} 의 열 정보를 읽지 못했습니다: {e.Message}";
        }
    }

    /// <summary>규칙의 원본 계정(비었으면 기본 원본 계정).</summary>
    public string EffectiveSource(TableRuleViewModel rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        return rule.SourceOwner.Length > 0 ? rule.SourceOwner : SourceOwner;
    }

    public void RemoveRule(TableRuleViewModel rule)
    {
        Rules.Remove(rule);
        if (ReferenceEquals(SelectedRule, rule))
        {
            SelectedRule = Rules.FirstOrDefault();
        }
    }
}
