using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.App.ViewModels;
using Oranda.Core.Execution;
using Oranda.Core.Transfer.Copy;

namespace Oranda.App.Features.DataCopy;

/// <summary>점검 항목 아래에 보이는 선택지 한 개(트리거 끄기 등).</summary>
public sealed partial class CheckChoiceViewModel(CheckChoice choice, string? table, bool isVisible, bool isChosen) : ViewModelBase
{
    [ObservableProperty]
    private bool _isChosen = isChosen;

    public CheckChoice Choice { get; } = choice;

    public string? Table { get; } = table;

    /// <summary>끌 권한이 없으면 선택지를 보이지 않는다(설계 7장: 알림만 남긴다).</summary>
    public bool IsVisible { get; } = isVisible;

    /// <summary>이 선택지가 달린 점검 항목.</summary>
    public CheckItemViewModel? Owner { get; internal set; }

    /// <summary>선택하면 작업(규칙)을 바꾸고 점검을 다시 하는 선택지인지. 나머지는 실행 결정만 바꾼다.</summary>
    public bool ChangesJob => Choice is CheckChoice.SkipColumn or CheckChoice.SwitchToMerge;

    public string Label => Choice switch
    {
        CheckChoice.DisableTriggers => "복사하는 동안 트리거를 끄고 끝나면 다시 켜기",
        CheckChoice.DisableForeignKeys => "복사하는 동안 이 FK 를 끄고 끝나면 다시 켜기",
        CheckChoice.SkipColumn => "이 열 건너뛰기",
        CheckChoice.ProceedSequenceGap => "시간이 걸려도 시퀀스를 보정하며 진행",
        _ => "대상 처리를 PK 기준 MERGE 로 바꾸기",
    };
}

/// <summary>
/// 시퀀스 연결 한 건: 표의 숫자 키 열과 시퀀스를 짝지은 규칙. 켜면 작업의 <see cref="CopyJob.Sequences"/> 에 들어가
/// 복사가 끝난 뒤 시퀀스 값을 원본의 최대값에 맞춘다. 후보(아직 연결 전)와 이미 작업에 있는 규칙을 같은 모양으로 보인다.
/// </summary>
public sealed partial class SequenceLinkViewModel(SequenceRule rule, bool isLinked) : ViewModelBase
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText))]
    private bool _isLinked = isLinked;

    public SequenceRule Rule { get; } = rule;

    public string Text => $"{Rule.Table}.{Rule.Column} ← {Rule.Sequence}";

    /// <summary>연결된 규칙은 「연결됨」, 후보는 빈 글자.</summary>
    public string StateText => IsLinked ? "연결됨" : string.Empty;
}

/// <summary>점검 결과 한 줄.</summary>
public sealed class CheckItemViewModel
{
    // PrecheckRules·CopyPrecheckService 가 만드는 CheckItem 코드 → 한국어 이름(영어는 Resources/literals-en.json).
    private static readonly Dictionary<string, string> CodeNames = new(StringComparer.Ordinal)
    {
        ["ChildRowsMayBlock"] = "자식 행이 지우기를 막음",
        ["ColumnNotInTarget"] = "대상에 없는 열",
        [CopyCheckCodes.CreateBlocked] = "없으면 만들기 막힘",
        ["ParentWillBeCreated"] = "부모 테이블을 새로 만들어 범위를 못 지움",
        [CopyCheckCodes.CreateNoPrivilege] = "없으면 만들기 권한 없음",
        [CopyCheckCodes.CreateTable] = "없으면 테이블 만들기",
        [CopyCheckCodes.EmptyPeriod] = "빈 기간",
        ["FkCycle"] = "외래키 순환",
        ["IdentityAlways"] = "IDENTITY 열",
        ["MaskedColumns"] = "마스킹 열",
        ["MaskOnKey"] = "키 열 마스킹",
        ["NarrowerType"] = "대상 열이 좁음",
        ["NoPrimaryKey"] = "기본키 없음",
        [CopyCheckCodes.OtherOwnerTargets] = "다른 계정 테이블에 씀",
        ["ParentExcluded"] = "부모 테이블 제외",
        ["ReadConsistency"] = "같은 시점 읽기",
        ["RecreateBlocked"] = "다시 만들기 막힘",
        ["RecreateNoPrivilege"] = "다시 만들기 권한 없음",
        ["RecreateNotAllowed"] = "다시 만들기 쓸 수 없음",
        ["RecreateTable"] = "테이블 다시 만들기",
        ["RuleInvalid"] = "규칙 오류",
        ["SameSourceTarget"] = "원본과 대상이 같음",
        ["SequenceGap"] = "시퀀스",
        ["SourceNoSelect"] = "원본 읽기 권한 없음",
        [CopyCheckCodes.SynonymChanged] = "시노님이 가리키는 테이블 바뀜",
        ["TargetMissing"] = "대상 테이블 없음",
        ["TargetNoWrite"] = "대상 쓰기 권한 없음",
        ["TargetOnlyConstraint"] = "대상에만 있는 제약",
        ["TargetProduction"] = "운영 대상",
        ["TargetRequiredColumnMissing"] = "필수 열 값 없음",
        ["TargetTriggers"] = "대상 트리거",
        ["UnsupportedType"] = "지원하지 않는 형식",
        ["WrongLengthDates"] = "날짜 길이 다름",
        [CopyCheckCodes.ColumnDropped] = "형식 때문에 뺀 열",
        [CopyCheckCodes.KeyColumnDropped] = "옮길 수 없는 키 열",
        [CopyCheckCodes.TypeConverted] = "형식 바꿔 옮김",
        [CopyCheckCodes.CrossCharset] = "대상 문자셋 안내",
        [CopyCheckCodes.BlobEmptyToNull] = "빈 BLOB 은 NULL",
        [CopyCheckCodes.CrossStringBytes] = "Oracle 바이트 한도 넘을 수 있음",
        [CopyCheckCodes.CharPadDiffers] = "CHAR 뒤 공백 달라짐",
        [CopyCheckCodes.SequenceDefaultDropped] = "시퀀스 기본값 뺌",
        [CopyCheckCodes.ConcurrentCopy] = "동시 복사 안내",
        [CopyCheckCodes.ReadPoint] = "시작 SCN 으로 읽기",
        [CopyCheckCodes.FlashbackNoPrivilege] = "시점 읽기 권한 없음",
        [CopyCheckCodes.SequenceCreated] = "시퀀스 함께 만듦",
        [CopyCheckCodes.SequenceBehind] = "대상 시퀀스가 키보다 뒤",
        [CopyCheckCodes.CompanionObjects] = "함께 만드는 객체",
        [CopyCheckCodes.ExternalLoad] = "외부 도구로 적재",
        [CopyCheckCodes.ExternalLoadFallback] = "기본 방식으로 적재",
    };

    public CheckItemViewModel(CheckItem item, IReadOnlyList<CheckChoiceViewModel> choices, string? tableText = null)
    {
        Item = item;
        Choices = choices;
        TableText = tableText ?? item.Table ?? string.Empty;
        foreach (var choice in choices)
        {
            choice.Owner = this;
        }
    }

    /// <summary>점검 항목 코드 → 한국어 이름 표. 시험이 문구 표(영어)와 맞는지 본다.</summary>
    public static IReadOnlyDictionary<string, string> CodeNameTable => CodeNames;

    public CheckItem Item { get; }

    public CheckLevel Level => Item.Level;

    public string Message => Item.Message;

    /// <summary>규칙 키(<see cref="CopyOwners.Key(CopyJob, TableRule)"/>: 기본 원본 계정의 표는 이름만). 작업 전체 항목이면 null.</summary>
    public string? Table => Item.Table;

    /// <summary>그리드·상세 칸에 보이는 표 이름 <c>계정.표</c>(작업에서 규칙을 찾지 못하면 키 그대로).</summary>
    public string TableText { get; }

    /// <summary>점검 항목 코드(Oracle 점검이 정한 이름). 그리드에는 <see cref="CodeText"/> 를 보이고 코드는 상세 칸에 작게 남긴다.</summary>
    public string Code => Item.Code;

    /// <summary>점검 항목의 사람이 읽는 이름(그리드 「항목」 칸). 모르는 코드면 코드 그대로. 영어 화면은 문구 표가 바꾼다.</summary>
    public string CodeText => CodeNames.TryGetValue(Item.Code, out var name) ? name : Item.Code;

    public IReadOnlyList<CheckChoiceViewModel> Choices { get; }

    /// <summary>시퀀스 후보 항목이면 「연결」 체크 한 개. 아니면 null.</summary>
    public SequenceLinkViewModel? SequenceLink { get; internal set; }

    public bool HasSequenceLink => SequenceLink is not null;

    /// <summary>수준별 아이콘 키(오류 = 빨강, 확인 필요 = 주황·노랑, 통과 = 초록 계열 색은 아이콘 톤이 정한다).</summary>
    public string IconKey => Level switch
    {
        CheckLevel.Error => "error",
        CheckLevel.Confirm => "warn",
        _ => "ok",
    };

    public bool IsError => Level == CheckLevel.Error;

    public bool IsConfirm => Level == CheckLevel.Confirm;

    public bool IsPass => Level == CheckLevel.Pass;

    public string LevelText => Level switch
    {
        CheckLevel.Pass => "통과",
        CheckLevel.Confirm => "확인 필요",
        _ => "오류",
    };
}

/// <summary>SQL 미리 보기 한 줄. 정책이 확인을 요구하는 문장은 「복사 실행」으로 미리 확인한 것으로 본다.</summary>
public sealed class PreviewRowViewModel(PreviewStatement statement)
{
    public PreviewStatement Statement { get; } = statement;

    public string Sql => Statement.Sql;

    public bool IsTarget => Statement.Side == PreviewSide.Target;

    public bool IsConfirm => Statement.Policy == PolicyDecisionKind.Confirm;

    public bool IsDenied => Statement.Policy == PolicyDecisionKind.Deny;

    public string SideText => IsTarget ? "대상" : "원본";
}

/// <summary>
/// 사전 점검 단계(2단계): 점검 결과, 대상에서 실행할 SQL 미리 보기, 선택지. 오류가 하나라도 있으면 「복사 실행」이 꺼진다.
/// 정책이 <c>Confirm</c> 을 내는 문장은 한 목록으로 보이고 실행할 때 <see cref="CopyDecisions.PreConfirmedSql"/> 로 넘긴다.
/// </summary>
public sealed partial class PrecheckViewModel(DataCopyServices services) : ViewModelBase
{
    private CancellationTokenSource? _cts;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRun), nameof(CanCancel))]
    private bool _isRunning;

    [ObservableProperty]
    private string _progressText = string.Empty;

    [ObservableProperty]
    private string _notice = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRun), nameof(HasErrors), nameof(HasDenied), nameof(NeedsRetype), nameof(ScriptText))]
    private PrecheckReport? _report;

    /// <summary>DROP 문장을 확인하려고 다시 입력한 대상 접속 이름(목록 전체에 대한 확인 하나).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRun), nameof(RetypeSatisfied), nameof(RetypeState))]
    private string _retypeInput = string.Empty;

    /// <summary>다시 입력할 대상 접속 이름(안내 글·입력 칸 흐린 글자에 보인다).</summary>
    public string RetypeTargetName => _targetName;

    /// <summary>다시 입력 칸 옆의 상태 글: 아직 안 맞으면 「복사 실행」이 꺼진 이유, 맞으면 확인됨.</summary>
    public string RetypeState => !NeedsRetype ? string.Empty
        : IsRetypeMatch() ? "확인했습니다. 「복사 실행」을 누를 수 있습니다."
        : $"「{_targetName}」 을(를) 입력하기 전에는 「복사 실행」을 누를 수 없습니다.";

    /// <summary>점검 결과 그리드에서 고른 항목(아래 상세 칸이 문장 전체와 고를 거리를 보인다).</summary>
    [ObservableProperty]
    private CheckItemViewModel? _selectedItem;

    /// <summary>SQL 미리 보기 그리드에서 고른 문장(아래 코드 칸이 문장 전체를 보인다).</summary>
    [ObservableProperty]
    private PreviewRowViewModel? _selectedPreview;

    public ObservableCollection<CheckItemViewModel> Items { get; } = [];

    public ObservableCollection<PreviewRowViewModel> Preview { get; } = [];

    /// <summary>작업에 이미 연결된 시퀀스 규칙. 체크를 끄면 연결이 풀린다.</summary>
    public ObservableCollection<SequenceLinkViewModel> LinkedSequences { get; } = [];

    public bool HasLinkedSequences => LinkedSequences.Count > 0;

    /// <summary>새 SQL 탭으로 열기(창이 연결한다). 연결이 없으면 아무 일도 하지 않는다.</summary>
    public Func<string, Task>? OpenInEditor { get; set; }

    /// <summary>SQL 스크립트로 저장(창이 파일 선택 뒤 쓰기를 연결한다).</summary>
    public Func<string, Task>? SaveScript { get; set; }

    public bool HasErrors => Report?.HasErrors ?? false;

    public bool HasDenied => Preview.Any(p => p.IsDenied);

    /// <summary>다시 입력이 필요한 문장(DROP 등)이 있는지.</summary>
    public bool NeedsRetype => Preview.Any(p => p.IsConfirm && p.Statement.RetypeText is not null);

    /// <summary>필요한 문장이 모두 그 글자를 입력했는지. 없으면 늘 참.</summary>
    public bool RetypeSatisfied => !NeedsRetype || IsRetypeMatch();

    /// <summary>
    /// 다시 입력하는 글자는 대상 접속 이름이다(설계 6.2.3: 한 번 입력하면 지울 테이블 목록 전체를 확인한 것으로 본다).
    /// 정책이 문장마다 내는 <c>RetypeText</c>(지울 테이블 이름)는 「다시 입력이 필요하다」는 표시로만 쓴다.
    /// 대소문자·앞뒤 공백은 가리지 않는다(SQL 실행 확인 창 <c>ExecutionDialogs.CanAccept</c> 와 같다).
    /// </summary>
    private bool IsRetypeMatch() =>
        !string.IsNullOrEmpty(_targetName) && string.Equals(_targetName.Trim(), RetypeInput.Trim(), StringComparison.OrdinalIgnoreCase);

    private string _targetName = string.Empty;

    public bool CanCancel => IsRunning;

    /// <summary>점검을 마쳤고 오류·거부된 문장이 없으며, DROP 확인이 끝났을 때만 실행할 수 있다.</summary>
    public bool CanRun => !IsRunning && Report is not null && !Report.HasErrors && !HasDenied && RetypeSatisfied;

    /// <summary>대상에서 실행할 문장과 원본 조회 조건을 한 스크립트로 만든다(원본 문장은 주석).</summary>
    public string ScriptText
    {
        get
        {
            var text = new StringBuilder();
            foreach (var row in Preview)
            {
                if (!row.IsTarget)
                {
                    text.Append("-- 원본 조회\n-- ").Append(row.Sql.ReplaceLineEndings("\n-- ")).Append("\n\n");
                    continue;
                }

                text.Append(row.Sql.TrimEnd().TrimEnd(';')).Append(";\n\n");
            }

            return text.ToString();
        }
    }

    public async Task RunAsync(CopySessions sessions, CopyJob job, ResolvedPeriod period, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(job);
        if (services.Precheck is not { } service)
        {
            Notice = DataCopyText.PrecheckMissing;
            return;
        }

        if (IsRunning)
        {
            return; // 점검이 이미 도는 중이면 겹쳐 시작하지 않는다
        }

        // 새 점검을 시작하면 이전 결과는 실행에 쓸 수 없다(점검이 실패·취소돼도 옛 결과로 실행하지 않는다).
        ClearResult();
        _targetName = sessions.Target.Profile.Name;
        OnPropertyChanged(nameof(RetypeTargetName));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _cts = cts;
        IsRunning = true;
        Notice = string.Empty;
        ProgressText = "점검 중…";
        try
        {
            var report = await service.RunAsync(sessions, job, period, new UiProgress<string>(text => ProgressText = text), cts.Token).ConfigureAwait(true);
            Fill(job, report);
            ProgressText = report.HasErrors ? "오류가 있어 실행할 수 없습니다." : "점검을 마쳤습니다.";
        }
        catch (OperationCanceledException)
        {
            ProgressText = "점검을 취소했습니다.";
            ClearResult();
        }
        catch (Exception e)
        {
            ClearResult();
            Notice = "사전 점검을 마치지 못했습니다: " + e.Message;
            ProgressText = Notice;
        }
        finally
        {
            _cts = null;
            IsRunning = false;
        }
    }

    /// <summary>점검 결과를 비운다(작업·접속이 바뀌어 옛 결과로 실행하면 안 될 때).</summary>
    public void Invalidate() => ClearResult();

    /// <summary>다른 작업을 열 때: 이전 점검 결과로 실행하지 않게 비운다.</summary>
    public void Reset()
    {
        Cancel();
        ClearResult();
        _picked = null;
        ProgressText = string.Empty;
        Notice = string.Empty;
    }

    private void ClearResult()
    {
        Report = null;
        SelectedItem = null;
        SelectedPreview = null;
        Items.Clear();
        Preview.Clear();
        LinkedSequences.Clear();
        RetypeInput = string.Empty;
        OnPropertyChanged(nameof(HasLinkedSequences));
        OnPropertyChanged(nameof(HasDenied));
        OnPropertyChanged(nameof(NeedsRetype));
        OnPropertyChanged(nameof(RetypeSatisfied));
        OnPropertyChanged(nameof(RetypeState));
        OnPropertyChanged(nameof(CanRun));
        OnPropertyChanged(nameof(ScriptText));
    }

    /// <summary>예상 건수 세기까지 포함한 점검을 멈춘다.</summary>
    public void Cancel() => _cts?.Cancel();

    // 마지막으로 고른 점검 항목(테이블·코드). 다시 점검하면 결과를 비운 뒤 새로 채우므로 고른 항목을 여기에 기억해 둔다. 다른 작업을 열면 지운다.
    private (string? Table, string Code, string Message)? _picked;

    partial void OnSelectedItemChanged(CheckItemViewModel? value)
    {
        if (value is not null)
        {
            _picked = (value.Table, value.Code, value.Message);
        }
    }

    private void Fill(CopyJob job, PrecheckReport report)
    {
        var previous = _picked;
        SelectedItem = null;
        SelectedPreview = null;
        Items.Clear();
        Preview.Clear();
        LinkedSequences.Clear();
        RetypeInput = string.Empty;
        foreach (var rule in job.Sequences)
        {
            LinkedSequences.Add(new SequenceLinkViewModel(rule, isLinked: true));
        }

        _job = job;
        var plans = PlansByKey(job, report);
        foreach (var item in report.Items.OrderBy(i => i.Level switch { CheckLevel.Error => 0, CheckLevel.Confirm => 1, _ => 2 }))
        {
            var choices = new List<CheckChoiceViewModel>();
            plans.TryGetValue(item.Table ?? string.Empty, out var plan);
            foreach (var choice in item.Choices)
            {
                var visible = choice switch
                {
                    CheckChoice.DisableTriggers => plan?.CanDisableTriggers == true,
                    CheckChoice.DisableForeignKeys => plan is { DisablableForeignKeys.Count: > 0 },
                    _ => true,
                };
                var chosen = choice == CheckChoice.DisableTriggers && visible && job.Options.DisableTriggersByDefault;
                choices.Add(new CheckChoiceViewModel(choice, item.Table, visible, chosen));
            }

            var row = new CheckItemViewModel(item, choices, TableText(job, item.Table));
            if (item is { Code: "SequenceGap", Table: { } table, Column: { } column, Sequence: { } sequence })
            {
                row.SequenceLink = new SequenceLinkViewModel(new SequenceRule(sequence, table, column), isLinked: false);
            }

            Items.Add(row);
        }

        foreach (var statement in report.Preview)
        {
            Preview.Add(new PreviewRowViewModel(statement));
        }

        // 다시 점검하면 같은 항목(테이블·코드·내용, 내용이 바뀌었으면 테이블·코드)을 다시 고르고, 처음이면 첫 「확인 필요」 항목(없으면 첫 항목)을 고른다: 고를 거리는 상세 칸에만 있다.
        SelectedItem = (previous is { } p
                ? Items.FirstOrDefault(i => i.Table == p.Table && i.Code == p.Code && i.Message == p.Message) ?? Items.FirstOrDefault(i => i.Table == p.Table && i.Code == p.Code)
                : null)
            ?? Items.FirstOrDefault(i => i.IsConfirm) ?? Items.FirstOrDefault();
        Report = report;
        OnPropertyChanged(nameof(HasLinkedSequences));
        OnPropertyChanged(nameof(HasDenied));
        OnPropertyChanged(nameof(NeedsRetype));
        OnPropertyChanged(nameof(RetypeSatisfied));
        OnPropertyChanged(nameof(RetypeState));
        OnPropertyChanged(nameof(CanRun));
        OnPropertyChanged(nameof(ScriptText));
    }

    /// <summary>
    /// 실행 결정을 만든다. 미리 확인한 문장은 정책이 확인을 요구하는 것 가운데 다시 입력이 필요 없거나 입력을 마친 것뿐이다.
    /// 정책이 거부한 문장은 어떤 경우에도 넣지 않는다.
    /// </summary>
    public CopyDecisions BuildDecisions()
    {
        var disableTriggers = new HashSet<string>(StringComparer.Ordinal);
        var disableForeignKeys = new HashSet<ForeignKeyRef>(); // (계정, 이름): 다른 계정의 같은 이름 FK 는 끄지 않는다
        var plans = Report is { } report && _job is { } job ? PlansByKey(job, report) : [];
        var proceedGap = false;
        foreach (var item in Items)
        {
            foreach (var choice in item.Choices.Where(c => c.IsVisible && c.IsChosen))
            {
                switch (choice.Choice)
                {
                    case CheckChoice.DisableTriggers when choice.Table is { } table:
                        disableTriggers.Add(table);
                        break;
                    case CheckChoice.DisableForeignKeys when choice.Table is { } fkTable && plans.TryGetValue(fkTable, out var plan):
                        foreach (var fk in plan.DisablableForeignKeys)
                        {
                            disableForeignKeys.Add(ForeignKeyRef.Of(fk));
                        }

                        break;
                    case CheckChoice.ProceedSequenceGap:
                        proceedGap = true;
                        break;
                    default:
                        break;
                }
            }
        }

        var confirmed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in Preview.Where(p => p.IsConfirm))
        {
            if (row.Statement.RetypeText is null || IsRetypeMatch())
            {
                confirmed.Add(row.Sql);
            }
        }

        return new CopyDecisions(disableTriggers, disableForeignKeys, confirmed, proceedGap);
    }

    // 점검한 작업(점검 항목·건수 어림·트리거 끄기 결정의 표 이름은 규칙 키다).
    private CopyJob? _job;

    // 점검 계획을 규칙 키로 찾는다(같은 이름 표가 두 계정에 있어도 섞이지 않게).
    private static Dictionary<string, CopyTablePlan> PlansByKey(CopyJob job, PrecheckReport report)
    {
        var plans = new Dictionary<string, CopyTablePlan>(StringComparer.Ordinal);
        foreach (var plan in report.Order)
        {
            plans.TryAdd(CopyOwners.Key(job, plan.Rule), plan);
        }

        return plans;
    }

    /// <summary>규칙 키 → 화면에 보일 <c>계정.표</c>. 작업에 그 규칙이 없으면 키 그대로, 작업 전체 항목(null)이면 빈 글자.</summary>
    public static string TableText(CopyJob job, string? key)
    {
        ArgumentNullException.ThrowIfNull(job);
        return key is null ? string.Empty : CopyOwners.Find(job, key) is { } rule ? CopyOwners.SourceName(job, rule) : key;
    }

    /// <summary>작업 규칙을 바꾸는 선택지를 적용한다. 바뀐 작업을 돌려주고, 호출한 쪽이 점검을 다시 한다.</summary>
    public static CopyJob? ApplyJobChoice(CopyJob job, CheckItemViewModel item, CheckChoice choice)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(item);
        var tables = job.Tables.Select(rule => CopyOwners.Key(job, rule) != item.Table ? rule : choice switch
        {
            CheckChoice.SkipColumn when item.Item.Column is { } column && !rule.SkipColumns.Contains(column, StringComparer.OrdinalIgnoreCase)
                => rule with { SkipColumns = [.. rule.SkipColumns, column] },
            CheckChoice.SwitchToMerge => rule with { Action = TargetAction.MergeByPk },
            _ => rule,
        }).ToList();
        return job with { Tables = tables };
    }

    /// <summary>
    /// 시퀀스 연결을 켜거나 끈 작업을 만든다. 같은 표·열의 규칙은 하나뿐이라 켤 때 먼저 지우고 넣는다.
    /// 바뀐 것이 없으면 null(호출한 쪽이 점검을 다시 하지 않는다).
    /// </summary>
    public static CopyJob? ApplySequenceLink(CopyJob job, SequenceRule rule, bool linked)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(rule);
        var rest = job.Sequences.Where(s => !(s.Table == rule.Table && s.Column == rule.Column)).ToList();
        if (linked)
        {
            rest.Add(rule);
        }

        return rest.SequenceEqual(job.Sequences) ? null : job with { Sequences = rest };
    }

    public async Task OpenScriptInEditorAsync()
    {
        if (OpenInEditor is { } open)
        {
            await open(ScriptText).ConfigureAwait(true);
        }
    }

    public async Task SaveScriptAsync()
    {
        if (SaveScript is { } save)
        {
            await save(ScriptText).ConfigureAwait(true);
        }
    }
}
