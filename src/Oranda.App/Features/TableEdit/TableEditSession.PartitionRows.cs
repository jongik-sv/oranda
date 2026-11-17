using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.App.Features.Localization;
using Oranda.Core.Schema.TableEdit;
using Oranda.Core.Schema.TableEdit.Partitions;

namespace Oranda.App.Features.TableEdit;

/// <summary>
/// 「파티션」 페이지 화면 상태(설계 18.4, 시안 ⑥): 위 줄(방식 · 키 열 · 자동 추가 · 하위 파티션), 도구줄(＋ － 나누기 합치기 비우기 ↺ │ 경계 채우기…),
/// 줄 표(<see cref="PartitionRows"/>), 아래 ⚠ 줄(<see cref="PartitionWarnings"/>)과 문제 줄(<see cref="PartitionProblemLines"/>).
/// 모든 동작은 <see cref="UpdatePartitions"/> 로 편집 모델만 바꾸고, 문장은 Core 가 만든다.
/// </summary>
public sealed partial class TableEditSession
{
    private const int MaxBoundFillCount = 1000;

    private List<string> _partitionWarnings = [];
    private List<string> _partitionProblemLines = [];
    private string? _partitionKeyInput;
    private string? _partitionHeaderError;

    /// <summary>고른 파티션 줄(도구줄 동작의 대상).</summary>
    [ObservableProperty]
    private PartitionRowViewModel? _selectedPartition;

    /// <summary>「경계 채우기…」 칸이 열려 있다.</summary>
    [ObservableProperty]
    private bool _isBoundFillOpen;

    /// <summary>경계 채우기 시작(날짜 단위 = <c>YYYY-MM</c>·<c>YYYY-MM-DD</c>·<c>YYYY</c>, 숫자 = 수).</summary>
    [ObservableProperty]
    private string _boundFillStart = string.Empty;

    /// <summary>경계 채우기 단위(0 일 · 1 월 · 2 년 · 3 숫자).</summary>
    [ObservableProperty]
    private int _boundFillUnitIndex = 1;

    [ObservableProperty]
    private string _boundFillCount = "12";

    [ObservableProperty]
    private string _boundFillStep = "1";

    /// <summary>경계 채우기 입력이 틀린 이유. 없으면 null.</summary>
    [ObservableProperty]
    private string? _boundFillError;

    /// <summary>파티션 표 줄(편집 모델 순서 = 경계 순서). 줄 키가 같은 동안 같은 객체를 쓴다.</summary>
    public ObservableCollection<PartitionRowViewModel> PartitionRows { get; } = [];

    /// <summary>데이터가 사라지거나 옮겨지는 줄의 ⚠ 문구(지우기·비우기·합치기, 행 수 통계와 함께). 실행 경로가 검사 결과로 쓸 수 있다.</summary>
    public IReadOnlyList<string> PartitionWarnings => _partitionWarnings;

    public bool HasPartitionWarnings => _partitionWarnings.Count > 0;

    /// <summary>문장으로 못 만드는 편집(Core 문제)과 아직 덜 쓴 새 줄의 문구(줄 이름: 이유).</summary>
    public IReadOnlyList<string> PartitionProblemLines => _partitionProblemLines;

    public bool HasPartitionProblems => _partitionProblemLines.Count > 0;

    // ---- 위 줄: 방식 · 키 열 · 자동 추가 · 하위 파티션 ----------------------

    private PartitionMethod? CurrentPartitionMethod => _partitionDraft?.Scheme?.Method;

    /// <summary>원본이 일반 테이블(파티션 아님)이라 방식을 골라 파티션 테이블로 바꿀 수 있는 꼴.</summary>
    public bool ShowsPartitionMethodChoice => _partitionOriginal is { Scheme: null };

    /// <summary>일반 → 파티션 바꾸기 중(원본에 방식 없음, 편집에 있음).</summary>
    public bool IsPartitionConversion => _partitionOriginal is { Scheme: null } && _partitionDraft?.Scheme is not null;

    private string? PartitionConversionReasonKey => Dialect.UnsupportedReasonKey(TableEditFeature.PartitionConversion, Context.Server);

    /// <summary>방식 고르기 칸을 쓸 수 있다(일반 테이블이고 방언이 바꾸기를 함).</summary>
    public bool CanChoosePartitionMethod => CanEditPartitions && ShowsPartitionMethodChoice && PartitionConversionReasonKey is null;

    /// <summary>일반 테이블인데 방식을 고를 수 없다(서버·에디션이 파티션을 못 함): 그 이유를 페이지에 글로 보인다(툴팁만으로는 안 보인다).</summary>
    public bool ShowsPartitionMethodReason => ShowsPartitionMethodChoice && CanEditPartitions && PartitionConversionReasonKey is not null;

    // ---- 한 문장으로 못 바꾸는 서버(Oracle 12.2 미만·Tibero): 테이블 다시 만들기 스크립트(TE-41) ----

    private Func<Task>? _openPartitionRebuild;
    private Func<Task>? _savePartitionRebuild;

    /// <summary>바꾸기 중인데 이 서버는 <c>MODIFY PARTITION BY … ONLINE</c> 이 없다 → 「실행」 대신 다시 만들기 스크립트.</summary>
    public bool NeedsPartitionRebuild => IsPartitionConversion && PartitionConversionReasonKey is null && !Dialect.SupportsOnlinePartitionConversion(Context.Server);

    /// <summary>다시 만들기 스크립트에 넣을 <c>PARTITION BY …</c> 절(키 열이 없거나 바꾸기가 아니면 null).</summary>
    public string? PartitionRebuildClause => _partitionOriginal is { } o && _partitionDraft is { } d ? PartitionScriptBuilder.ConversionClause(o, d, Dialect) : null;

    /// <summary>「다시 만들기 스크립트」 줄을 보인다.</summary>
    public bool ShowsPartitionRebuild => NeedsPartitionRebuild && (_openPartitionRebuild is not null || _savePartitionRebuild is not null);

    /// <summary>스크립트를 만들 수 있다(키 열·첫 파티션까지 정함).</summary>
    public bool CanUsePartitionRebuild => ShowsPartitionRebuild && PartitionRebuildClause is not null
        && (CurrentPartitionMethod == PartitionMethod.Hash || _partitionDraft!.Partitions.Any(p => !p.IsDropped && p.MergedInto is null && !string.IsNullOrWhiteSpace(p.Bound)));

    /// <summary>객체 상세가 넣는 「편집기로 열기」(다시 만들기 스크립트를 새 SQL 탭에). null = 그 길이 없음.</summary>
    public Func<Task>? OpenPartitionRebuild
    {
        get => _openPartitionRebuild;
        set
        {
            _openPartitionRebuild = value;
            RaisePartitionRebuild();
        }
    }

    /// <summary>객체 상세가 넣는 「스크립트 저장…」(다시 만들기 스크립트를 파일로). null = 그 길이 없음.</summary>
    public Func<Task>? SavePartitionRebuild
    {
        get => _savePartitionRebuild;
        set
        {
            _savePartitionRebuild = value;
            RaisePartitionRebuild();
        }
    }

    public Task OpenPartitionRebuildScriptAsync() => CanUsePartitionRebuild && _openPartitionRebuild is { } open ? open() : Task.CompletedTask;

    public Task SavePartitionRebuildScriptAsync() => CanUsePartitionRebuild && _savePartitionRebuild is { } save ? save() : Task.CompletedTask;

    private void RaisePartitionRebuild()
    {
        OnPropertyChanged(nameof(NeedsPartitionRebuild));
        OnPropertyChanged(nameof(ShowsPartitionRebuild));
        OnPropertyChanged(nameof(CanUsePartitionRebuild));
        OnPropertyChanged(nameof(PartitionRebuildClause));
        OnPropertyChanged(nameof(ShowsPartitionMethodReason));
    }

    /// <summary>방식 고르기 칸 도움말(못 쓰면 그 이유).</summary>
    public string PartitionMethodTip => PartitionConversionReasonKey is { } key ? UiText.Get(key, key)
        : !CanEditPartitions ? PartitionLockedReason
        : TableEditText.T("방식을 고르면 일반 테이블을 파티션 테이블로 바꿉니다", "Choose a method to convert this table to a partitioned table");

    /// <summary>방식 글(보기만): RANGE · LIST · HASH, 파티션 아님 = 없음.</summary>
    public string PartitionMethodText => MethodText(CurrentPartitionMethod);

    /// <summary>방식 고르기 칸(0 없음 · 1 RANGE · 2 LIST · 3 HASH). 없음 = 바꾸기 취소(원본으로).</summary>
    public int PartitionMethodIndex
    {
        get => CurrentPartitionMethod switch
        {
            PartitionMethod.Range => 1,
            PartitionMethod.List => 2,
            PartitionMethod.Hash => 3,
            _ => 0,
        };
        set
        {
            if (!CanChoosePartitionMethod || value is < 0 or > 3 || value == PartitionMethodIndex)
            {
                return;
            }

            if (value == 0)
            {
                _partitionKeyInput = null;
                _partitionHeaderError = null;
                RevertPartitions();
                return;
            }

            var method = value switch
            {
                1 => PartitionMethod.Range,
                2 => PartitionMethod.List,
                _ => PartitionMethod.Hash,
            };
            UpdatePartitions(d => d with
            {
                Scheme = d.Scheme is { } s
                    ? s with { Method = method, Interval = method == PartitionMethod.Range ? s.Interval : null }
                    : new PartitionScheme(method, []),
            });
        }
    }

    /// <summary>키 열 칸: 일반 → 파티션 바꾸기 때만(기존 파티션 테이블의 방식·키는 바꿀 수 없다).</summary>
    public bool CanEditPartitionKeys => CanEditPartitions && IsPartitionConversion;

    /// <summary>키 열(<c>A, B</c>). 모르는 열이면 모델을 바꾸지 않고 <see cref="PartitionHeaderError"/>.</summary>
    public string PartitionKeyText
    {
        get => _partitionKeyInput ?? string.Join(", ", (_partitionDraft?.Scheme?.KeyColumns ?? []).Select(TableEditCellText.Show));
        set
        {
            if (!CanEditPartitionKeys || string.Equals(value ?? string.Empty, PartitionKeyText, StringComparison.Ordinal))
            {
                return;
            }

            var names = new List<string>();
            foreach (var part in TableEditCellText.SplitList(value))
            {
                var name = TableEditCellText.Parse(part);
                if (!Draft.Columns.Any(c => !c.IsDropped && string.Equals(c.Name, name, StringComparison.Ordinal)))
                {
                    _partitionKeyInput = value;
                    SetPartitionHeaderError(TableEditText.T($"이 테이블에 없는 열: {part}", $"No such column in this table: {part}"));
                    OnPropertyChanged(nameof(PartitionKeyText));
                    return;
                }

                names.Add(name);
            }

            _partitionKeyInput = null;
            SetPartitionHeaderError(null);
            if (_partitionDraft?.Scheme is { } scheme && !scheme.KeyColumns.SequenceEqual(names, StringComparer.Ordinal))
            {
                UpdatePartitions(d => d with { Scheme = d.Scheme! with { KeyColumns = names } });
            }
            else
            {
                OnPropertyChanged(nameof(PartitionKeyText));
            }
        }
    }

    /// <summary>위 줄 입력 오류(모르는 키 열). 없으면 null.</summary>
    public string? PartitionHeaderError => _partitionHeaderError;

    /// <summary>자동 추가(INTERVAL) 칸: 범위만.</summary>
    public bool CanEditPartitionInterval => CanEditPartitions && CurrentPartitionMethod == PartitionMethod.Range && PartitionOperationReason(PartitionOperation.SetInterval) is null;

    /// <summary>자동 추가 식(<c>NUMTOYMINTERVAL(1, 'MONTH')</c> 등). 비우면 끔.</summary>
    public string PartitionIntervalText
    {
        get => _partitionDraft?.Scheme?.Interval ?? string.Empty;
        set
        {
            if (!CanEditPartitionInterval || string.Equals(value ?? string.Empty, PartitionIntervalText, StringComparison.Ordinal))
            {
                return;
            }

            var interval = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (!string.Equals(interval, _partitionDraft?.Scheme?.Interval, StringComparison.Ordinal))
            {
                UpdatePartitions(d => d with { Scheme = d.Scheme! with { Interval = interval } });
            }
        }
    }

    /// <summary>하위 파티션(보기만, TE-42): 없음 또는 「HASH (CUSTOMER_ID)」.</summary>
    public string SubpartitionText => _partitionDraft?.Scheme is { SubpartitionMethod: { } method } scheme
        ? $"{method} ({string.Join(", ", scheme.SubpartitionKeyColumns.Select(TableEditCellText.Show))})"
        : TableEditText.T("없음", "None");

    /// <summary>경계 칸 머리글(범위 = 상한값, 목록 = 값 목록, 해시 = 없음).</summary>
    public string BoundHeaderText => CurrentPartitionMethod switch
    {
        PartitionMethod.List => TableEditText.T("값 목록", "Values"),
        PartitionMethod.Hash => "—",
        _ => TableEditText.T("이 값 미만 (상한값)", "Below this value (upper bound)"),
    };

    // ---- 도구줄 ----------------------------------------------------------

    /// <summary>방언이 그 파티션 문장을 못 쓰는 이유 문구(설계 18.8). 되면 null.</summary>
    private string? PartitionOperationReason(PartitionOperation operation) =>
        CurrentPartitionMethod is { } method && Dialect.UnsupportedPartitionReasonKey(operation, method, Context.Server) is { } key
            ? Oranda.App.Features.Localization.UiText.Get(key, key)
            : null;

    /// <summary>이 서버에서 못 쓰는 파티션 동작과 이유(도구줄 아래 한 줄, 흐린 단추의 까닭). 없으면 빈 글.</summary>
    public string PartitionUnsupportedText => string.Join(" · ", new (string Label, PartitionOperation Op)[]
        {
            (TableEditText.T("더하기", "add"), PartitionOperation.Add),
            (TableEditText.T("지우기", "drop"), CurrentPartitionMethod == PartitionMethod.Hash ? PartitionOperation.Coalesce : PartitionOperation.Drop),
            (TableEditText.T("나누기", "split"), PartitionOperation.Split),
            (TableEditText.T("합치기", "merge"), PartitionOperation.Merge),
            (TableEditText.T("비우기", "truncate"), PartitionOperation.Truncate),
            (TableEditText.T("자동 추가", "interval"), PartitionOperation.SetInterval),
        }
        .Where(x => !(CurrentPartitionMethod == PartitionMethod.Hash && x.Op is PartitionOperation.Split or PartitionOperation.Merge or PartitionOperation.SetInterval))
        .Select(x => PartitionOperationReason(x.Op) is { } r ? $"{x.Label}: {r}" : null)
        .Where(x => x is not null)
        .Distinct());

    /// <summary>「＋」: 자동 추가가 켜진 범위 테이블은 DB 가 만든다(ADD 는 ORA-14760) — 바꾸기 중(CREATE 안)은 된다.</summary>
    public bool CanAddPartition => CanEditPartitions && CurrentPartitionMethod is not null && PartitionOperationReason(PartitionOperation.Add) is null
        && !(CurrentPartitionMethod == PartitionMethod.Range && IntervalOn && !IsPartitionConversion);

    public bool CanDropPartition => CanEditPartitions && SelectedPartition is not null
        && PartitionOperationReason(CurrentPartitionMethod == PartitionMethod.Hash ? PartitionOperation.Coalesce : PartitionOperation.Drop) is null;

    /// <summary>나누기: 범위·목록의 남는 원본 줄.</summary>
    public bool CanSplitPartition => CanEditPartitions && CurrentPartitionMethod is PartitionMethod.Range or PartitionMethod.List
        && SelectedPartition is { IsNew: false, IsDropped: false, IsMergedAway: false } && PartitionOperationReason(PartitionOperation.Split) is null;

    /// <summary>합치기: 범위·목록의 원본 줄이고 바로 다음에 남는 원본 줄이 있음(이미 합쳤으면 취소).</summary>
    public bool CanMergePartition => CanEditPartitions && CurrentPartitionMethod is PartitionMethod.Range or PartitionMethod.List
        && SelectedPartition is { IsNew: false, IsDropped: false } row && (row.IsMergedAway || MergeTargetOf(row.Key) is not null)
        && PartitionOperationReason(PartitionOperation.Merge) is null;

    /// <summary>비우기: 남는 원본 줄.</summary>
    public bool CanTruncatePartition => CanEditPartitions && SelectedPartition is { IsNew: false, IsDropped: false, IsMergedAway: false }
        && PartitionOperationReason(PartitionOperation.Truncate) is null;

    public bool CanRevertPartition => CanEditPartitions && SelectedPartition is { } row
        && (row.IsNew || _partitionOriginal?.Partitions.FirstOrDefault(p => p.RowKey == row.Key) != row.Row);

    /// <summary>경계 채우기: 범위만(경계 = 다음 구간의 시작).</summary>
    public bool CanFillBounds => CanEditPartitions && CurrentPartitionMethod == PartitionMethod.Range;

    /// <summary>「＋」 새 줄을 끝에 붙이고 고른다(범위 = 앞 간격을 이은 이름·경계, 짐작할 수 없으면 빈 경계).</summary>
    public PartitionRowViewModel? AddPartition()
    {
        if (!CanAddPartition)
        {
            return null;
        }

        var row = NextRangeRow() ?? new PartitionDraft(DraftKeys.New(), UniquePartitionName(i => "P_NEW" + i.ToString(CultureInfo.InvariantCulture)));
        UpdatePartitions(d => d with { Partitions = [.. d.Partitions, row] });
        return SelectedPartition = PartitionRowOf(row.RowKey);
    }

    /// <summary>「－」 고른 줄 지우기 토글(새 줄은 뺀다). 지우면 비우기·합치기 표시는 지운다.</summary>
    public void DropSelectedPartition()
    {
        if (!CanDropPartition || SelectedPartition is not { } row)
        {
            return;
        }

        if (row.IsNew)
        {
            UpdatePartitions(d => d with { Partitions = [.. d.Partitions.Where(p => p.RowKey != row.Key)] });
        }
        else
        {
            var drop = !row.Row.IsDropped;
            UpdatePartition(row.Key, p => drop ? p with { IsDropped = true, IsTruncated = false, MergedInto = null } : p with { IsDropped = false });
        }

        SelectedPartition = PartitionRowOf(row.Key);
    }

    /// <summary>「나누기」 고른 원본 줄 바로 위에 떼어 낼 새 줄을 넣고 고른다(경계는 비어 있다 — 범위 = 나눌 지점, 목록 = 옮길 값).</summary>
    public PartitionRowViewModel? SplitSelectedPartition()
    {
        if (!CanSplitPartition || SelectedPartition is not { } source)
        {
            return null;
        }

        var baseName = source.Row.Name;
        var row = new PartitionDraft(DraftKeys.New(), UniquePartitionName(i => baseName + "_" + (i <= 26 ? new string((char)('A' + i - 1), 1) : i.ToString(CultureInfo.InvariantCulture))))
        {
            SplitFrom = source.Key,
        };
        UpdatePartitions(d =>
        {
            var list = d.Partitions.ToList();
            list.Insert(list.FindIndex(p => p.RowKey == source.Key), row);
            return d with { Partitions = list };
        });
        return SelectedPartition = PartitionRowOf(row.RowKey);
    }

    /// <summary>「합치기」 고른 줄을 바로 다음에 남는 원본 줄로 합친다(다시 누르면 취소).</summary>
    public void MergeSelectedPartition()
    {
        if (!CanMergePartition || SelectedPartition is not { } row)
        {
            return;
        }

        var target = row.IsMergedAway ? null : MergeTargetOf(row.Key);
        UpdatePartition(row.Key, p => p with { MergedInto = target, IsTruncated = target is null && p.IsTruncated });
        SelectedPartition = PartitionRowOf(row.Key);
    }

    /// <summary>「비우기」 토글.</summary>
    public void TruncateSelectedPartition()
    {
        if (!CanTruncatePartition || SelectedPartition is not { } row)
        {
            return;
        }

        UpdatePartition(row.Key, p => p with { IsTruncated = !p.IsTruncated });
        SelectedPartition = PartitionRowOf(row.Key);
    }

    /// <summary>「↺」 고른 줄을 원본대로(새 줄은 뺀다).</summary>
    public void RevertSelectedPartition()
    {
        if (!CanRevertPartition || SelectedPartition is not { } row)
        {
            return;
        }

        if (_partitionOriginal?.Partitions.FirstOrDefault(p => p.RowKey == row.Key) is { } original)
        {
            UpdatePartition(row.Key, _ => original);
        }
        else
        {
            UpdatePartitions(d => d with { Partitions = [.. d.Partitions.Where(p => p.RowKey != row.Key)] });
        }

        SelectedPartition = PartitionRowOf(row.Key);
    }

    /// <summary>「경계 채우기…」 칸을 열고 닫는다. 열 때 시작이 비어 있으면 마지막 경계로 채운다.</summary>
    public void ToggleBoundFill()
    {
        if (IsBoundFillOpen)
        {
            IsBoundFillOpen = false;
            return;
        }

        if (!CanFillBounds)
        {
            return;
        }

        BoundFillError = null;
        if (string.IsNullOrWhiteSpace(BoundFillStart) && _partitionDraft?.Partitions.LastOrDefault(p => !string.IsNullOrWhiteSpace(p.Bound))?.Bound is { } last)
        {
            if (DateBound().Match(last) is { Success: true } m)
            {
                BoundFillStart = m.Groups[1].Value + (m.Groups[3].Value == "01" ? string.Empty : "-" + m.Groups[3].Value);
                BoundFillUnitIndex = m.Groups[3].Value == "01" ? 1 : 0;
            }
            else if (decimal.TryParse(last.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out _))
            {
                BoundFillStart = last.Trim();
                BoundFillUnitIndex = 3;
            }
        }

        IsBoundFillOpen = true;
    }

    /// <summary>경계 채우기 입력으로 범위 새 줄을 끝에 붙인다(이미 있는 이름은 건너뜀). 입력이 틀리면 <see cref="BoundFillError"/> 와 false.</summary>
    public bool ApplyBoundFill()
    {
        if (!CanFillBounds)
        {
            return false;
        }

        var unit = BoundFillUnitIndex switch
        {
            0 => BoundUnit.Day,
            2 => BoundUnit.Year,
            3 => BoundUnit.Number,
            _ => BoundUnit.Month,
        };
        if (!int.TryParse(BoundFillCount?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var count) || count < 1 || count > MaxBoundFillCount)
        {
            BoundFillError = TableEditText.T($"개수는 1~{MaxBoundFillCount} 사이 정수입니다", $"The count must be a whole number from 1 to {MaxBoundFillCount}");
            return false;
        }

        var stepText = string.IsNullOrWhiteSpace(BoundFillStep) ? "1" : BoundFillStep.Trim();
        if (!int.TryParse(stepText, NumberStyles.None, CultureInfo.InvariantCulture, out var step) || step < 1)
        {
            BoundFillError = TableEditText.T("간격은 1 이상 정수입니다", "The step must be a whole number of 1 or more");
            return false;
        }

        var fill = new BoundFill(unit, count, step);
        var start = BoundFillStart?.Trim() ?? string.Empty;
        if (unit == BoundUnit.Number)
        {
            if (!decimal.TryParse(start, NumberStyles.Number, CultureInfo.InvariantCulture, out var number))
            {
                BoundFillError = TableEditText.T("시작은 수입니다(예: 1000)", "The start must be a number (for example 1000)");
                return false;
            }

            fill = fill with { StartNumber = number };
        }
        else
        {
            if (!DateOnly.TryParseExact(start, ["yyyy-MM-dd", "yyyy-MM", "yyyy"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            {
                BoundFillError = TableEditText.T("시작은 YYYY-MM(또는 YYYY-MM-DD·YYYY) 꼴입니다(예: 2026-01)", "The start must look like YYYY-MM (or YYYY-MM-DD, YYYY), for example 2026-01");
                return false;
            }

            fill = fill with { StartDate = date };
        }

        IReadOnlyList<PartitionDraft> rows;
        try
        {
            rows = BoundFiller.Fill(fill, [.. _partitionDraft!.Partitions.Select(p => p.Name)]);
        }
        catch (ArgumentOutOfRangeException)
        {
            BoundFillError = TableEditText.T("채울 범위가 너무 큽니다", "The range to fill is too large");
            return false;
        }

        if (rows.Count == 0)
        {
            BoundFillError = TableEditText.T("만들 줄이 모두 이미 있는 이름입니다", "Every row to create already exists");
            return false;
        }

        BoundFillError = null;
        UpdatePartitions(d => d with { Partitions = [.. d.Partitions, .. rows] });
        IsBoundFillOpen = false;
        SelectedPartition = PartitionRowOf(rows[^1].RowKey);
        return true;
    }

    /// <summary>줄 하나를 바꾼다(줄 칸·도구줄).</summary>
    internal void UpdatePartition(string rowKey, Func<PartitionDraft, PartitionDraft> change) =>
        UpdatePartitions(d => d with { Partitions = [.. d.Partitions.Select(p => p.RowKey == rowKey ? change(p) : p)] });

    partial void OnSelectedPartitionChanged(PartitionRowViewModel? value) => RaisePartitionActions();

    // ---- 줄 맞추기 --------------------------------------------------------

    partial void SyncPartitionRows()
    {
        var source = _partitionDraft?.Partitions ?? [];
        var originals = (_partitionOriginal?.Partitions ?? []).ToDictionary(p => p.RowKey, StringComparer.Ordinal);
        var diffs = PartitionChanges.Rows.GroupBy(r => r.RowKey, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var problems = PartitionChanges.Problems.GroupBy(p => p.RowKey, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)[.. g.Select(p => p.ReasonKey)], StringComparer.Ordinal);
        var method = CurrentPartitionMethod;

        var keys = source.Select(p => p.RowKey).ToHashSet(StringComparer.Ordinal);
        for (var i = PartitionRows.Count - 1; i >= 0; i--)
        {
            if (!keys.Contains(PartitionRows[i].Key))
            {
                PartitionRows.RemoveAt(i);
            }
        }

        for (var i = 0; i < source.Count; i++)
        {
            var row = source[i];
            var at = -1;
            for (var j = i; j < PartitionRows.Count; j++)
            {
                if (PartitionRows[j].Key == row.RowKey)
                {
                    at = j;
                    break;
                }
            }

            PartitionRowViewModel vm;
            if (at < 0)
            {
                vm = new PartitionRowViewModel(this, row.RowKey);
                PartitionRows.Insert(i, vm);
            }
            else
            {
                vm = PartitionRows[at];
                if (at != i)
                {
                    PartitionRows.Move(at, i);
                }
            }

            vm.Sync(row, originals.GetValueOrDefault(row.RowKey), diffs.GetValueOrDefault(row.RowKey), problems.GetValueOrDefault(row.RowKey) ?? [], method);
        }

        if (SelectedPartition is { } selected && !PartitionRows.Contains(selected))
        {
            SelectedPartition = null;
        }

        _partitionWarnings = BuildPartitionWarnings(originals);
        _partitionProblemLines = BuildPartitionProblemLines();
        if (!IsPartitionConversion)
        {
            _partitionKeyInput = null;
            _partitionHeaderError = null;
        }

        if (!CanFillBounds)
        {
            IsBoundFillOpen = false;
        }

        OnPropertyChanged(nameof(PartitionWarnings));
        OnPropertyChanged(nameof(HasPartitionWarnings));
        OnPropertyChanged(nameof(PartitionProblemLines));
        OnPropertyChanged(nameof(HasPartitionProblems));
        OnPropertyChanged(nameof(ShowsPartitionMethodChoice));
        OnPropertyChanged(nameof(IsPartitionConversion));
        OnPropertyChanged(nameof(CanChoosePartitionMethod));
        OnPropertyChanged(nameof(PartitionMethodTip));
        OnPropertyChanged(nameof(PartitionMethodText));
        OnPropertyChanged(nameof(PartitionMethodIndex));
        OnPropertyChanged(nameof(CanEditPartitionKeys));
        OnPropertyChanged(nameof(PartitionKeyText));
        OnPropertyChanged(nameof(PartitionHeaderError));
        OnPropertyChanged(nameof(CanEditPartitionInterval));
        OnPropertyChanged(nameof(PartitionIntervalText));
        OnPropertyChanged(nameof(SubpartitionText));
        OnPropertyChanged(nameof(BoundHeaderText));
        RaiseEasyPartition();
        RaisePartitionRebuild();
        RaisePartitionActions();
    }

    private void RaisePartitionActions()
    {
        OnPropertyChanged(nameof(CanAddPartition));
        OnPropertyChanged(nameof(PartitionUnsupportedText));
        OnPropertyChanged(nameof(CanDropPartition));
        OnPropertyChanged(nameof(CanSplitPartition));
        OnPropertyChanged(nameof(CanMergePartition));
        OnPropertyChanged(nameof(CanTruncatePartition));
        OnPropertyChanged(nameof(CanRevertPartition));
        OnPropertyChanged(nameof(CanFillBounds));
    }

    /// <summary>⚠ 줄: 문장이 나오는 지우기·비우기·합치기만(문제로 빠진 편집은 경고하지 않는다). 원본 순서.</summary>
    private List<string> BuildPartitionWarnings(Dictionary<string, PartitionDraft> originals)
    {
        var result = new List<string>();
        var method = _partitionOriginal?.Scheme?.Method;
        var edited = (_partitionDraft?.Partitions ?? []).ToDictionary(p => p.RowKey, StringComparer.Ordinal);
        foreach (var o in _partitionOriginal?.Partitions ?? [])
        {
            if (PartitionChanges.Rows.FirstOrDefault(r => r.RowKey == o.RowKey) is not { } diff)
            {
                continue;
            }

            var count = o.RowCount?.ToString("N0", CultureInfo.InvariantCulture);
            if (diff.State == DraftRowState.Dropped)
            {
                result.Add(method == PartitionMethod.Hash
                    ? count is null
                        ? TableEditText.T($"⚠ 파티션 줄이기 {o.Name}: 행을 남은 파티션으로 옮김", $"⚠ Coalesce partition {o.Name}: rows move to the remaining partitions")
                        : TableEditText.T($"⚠ 파티션 줄이기 {o.Name}: {count}행을 남은 파티션으로 옮김", $"⚠ Coalesce partition {o.Name}: {count} rows move to the remaining partitions")
                    : Lost(TableEditText.T("파티션 지우기", "Drop partition"), o.Name, count));
            }
            else if (diff.Changes.HasFlag(PartitionRowChange.Truncated))
            {
                result.Add(Lost(TableEditText.T("파티션 비우기", "Truncate partition"), o.Name, count));
            }
            else if (diff.Changes.HasFlag(PartitionRowChange.MergedAway))
            {
                var into = edited.GetValueOrDefault(diff.Edited!.MergedInto!)?.Name ?? originals.GetValueOrDefault(diff.Edited.MergedInto!)?.Name ?? "?";
                result.Add(count is null
                    ? TableEditText.T($"⚠ 파티션 합치기 {o.Name} → {into}: 행을 옮김(오래 걸릴 수 있음)", $"⚠ Merge partition {o.Name} into {into}: rows are moved (may take long)")
                    : TableEditText.T($"⚠ 파티션 합치기 {o.Name} → {into}: {count}행을 옮김(오래 걸릴 수 있음)", $"⚠ Merge partition {o.Name} into {into}: {count} rows are moved (may take long)"));
            }
        }

        return result;

        static string Lost(string what, string name, string? count) => count is null
            ? TableEditText.T($"⚠ {what} {name}: 행이 지워지고 되돌릴 수 없음(행 수 통계 없음)", $"⚠ {what} {name}: its rows are deleted and cannot be restored (no row count statistics)")
            : TableEditText.T($"⚠ {what} {name}: {count}행이 지워지고 되돌릴 수 없음", $"⚠ {what} {name}: {count} rows are deleted and cannot be restored");
    }

    /// <summary>문제 줄: Core 문제(줄 이름: 이유) + 덜 쓴 새 줄. 같은 문구는 한 번.</summary>
    private List<string> BuildPartitionProblemLines()
    {
        var names = (_partitionDraft?.Partitions ?? []).ToDictionary(p => p.RowKey, p => p.Name, StringComparer.Ordinal);
        foreach (var o in _partitionOriginal?.Partitions ?? [])
        {
            names.TryAdd(o.RowKey, o.Name);
        }

        string Label(string key) => key == TableDraft.TableRowKey ? TableEditText.T("파티션 방식", "Partitioning")
            : names.GetValueOrDefault(key) is { Length: > 0 } n ? n : TableEditText.T("(이름 없음)", "(no name)");

        var lines = PartitionChanges.Problems.Select(p => $"⚠ {Label(p.RowKey)}: {UiText.Get(p.ReasonKey, p.ReasonKey)}")
            .Concat(PartitionRows.Where(r => r.Error is not null).Select(r => $"⚠ {Label(r.Key)}: {r.Error}"));
        return [.. lines.Distinct(StringComparer.Ordinal)];
    }

    /// <summary>합칠 대상: 편집 목록에서 이 줄 다음에 오는 지우지 않은 원본 줄(그 줄도 합쳐지면 그 대상).</summary>
    private string? MergeTargetOf(string rowKey)
    {
        if (_partitionDraft is null || _partitionOriginal is null)
        {
            return null;
        }

        var originals = _partitionOriginal.Partitions.Select(p => p.RowKey).ToHashSet(StringComparer.Ordinal);
        var list = _partitionDraft.Partitions;
        var i = list.ToList().FindIndex(p => p.RowKey == rowKey);
        var next = i < 0 ? null : list.Skip(i + 1).FirstOrDefault(p => originals.Contains(p.RowKey) && !p.IsDropped);
        return next is null ? null : next.MergedInto ?? next.RowKey;
    }

    private PartitionRowViewModel? PartitionRowOf(string rowKey) => PartitionRows.FirstOrDefault(r => r.Key == rowKey);

    /// <summary>편집 모델에 아직 없는 이름(1부터 차례로 <paramref name="candidate"/> 을 시험).</summary>
    private string UniquePartitionName(Func<int, string> candidate)
    {
        var taken = (_partitionDraft?.Partitions ?? []).Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        for (var i = 1; ; i++)
        {
            var name = candidate(i);
            if (!taken.Contains(name))
            {
                return name;
            }
        }
    }

    private void SetPartitionHeaderError(string? error)
    {
        if (!string.Equals(_partitionHeaderError, error, StringComparison.Ordinal))
        {
            _partitionHeaderError = error;
            OnPropertyChanged(nameof(PartitionHeaderError));
        }
    }

    private static string MethodText(PartitionMethod? method) => method switch
    {
        PartitionMethod.Range => "RANGE",
        PartitionMethod.List => "LIST",
        PartitionMethod.Hash => "HASH",
        _ => TableEditText.T("없음", "None"),
    };

    [GeneratedRegex(@"DATE\s*'(\d{4}-(\d{2}))-(\d{2})'", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DateBound();
}
