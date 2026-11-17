using System.Globalization;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.App.Features.Localization;
using Oranda.Core.Schema.TableEdit;
using Oranda.Core.Schema.TableEdit.Partitions;

namespace Oranda.App.Features.TableEdit;

/// <summary>
/// 「파티션」 페이지를 쉽게 쓰는 길(설계 18.4 쉬운 편집, 2026-11-18 사용자 요청 「쉽게 바꾸고 추가는 자동으로」):
/// <list type="bullet">
/// <item>쉬운 설정(일반 → 파티션): 날짜 열과 단위만 고르면 범위 + 자동 추가(INTERVAL) + 첫 파티션 하나(지금 기간 전 전부)를 만든다.</item>
/// <item>자동 추가 칸 = 끔·매일·매월·매년·직접 입력. 꺼진 날짜 범위 테이블에는 「자동 추가 켜기」 한 번.</item>
/// <item>경계 칸 = 보통 값(<c>2026-12-01</c>), 새 줄 이름은 경계로 짓는다. 「＋ 다음 파티션」은 앞 간격을 잇는다.</item>
/// </list>
/// SQL 은 Core(<see cref="PartitionBounds"/>·<see cref="PartitionScriptBuilder"/>)가 만든다.
/// </summary>
public sealed partial class TableEditSession
{
    /// <summary>오늘(쉬운 설정의 첫 경계). 시험이 바꾼다.</summary>
    internal Func<DateTime> Today { get; set; } = () => DateTime.Today;

    /// <summary>쉬운 설정: 고른 날짜 열.</summary>
    [ObservableProperty]
    private string? _easyPartitionColumn;

    /// <summary>쉬운 설정: 단위(0 일 · 1 월 · 2 년).</summary>
    [ObservableProperty]
    private int _easyPartitionUnitIndex = 1;

    private bool _intervalCustom;

    // ---- 키 종류 ------------------------------------------------------------

    /// <summary>지금 키 열(하나일 때)의 값 종류. 경계 보통 값 ↔ SQL 에 쓴다.</summary>
    public PartitionKeyKind PartitionKeyKind =>
        _partitionDraft?.Scheme?.KeyColumns is [var only]
        && Draft.Columns.FirstOrDefault(c => !c.IsDropped && string.Equals(c.Name, only, StringComparison.Ordinal)) is { } column
            ? PartitionBounds.KindOf(column.DataType)
            : PartitionKeyKind.Other;

    /// <summary>경계 칸 빈 글 안내(키 종류에 맞춘 예).</summary>
    public string BoundPlaceholder => (CurrentPartitionMethod, PartitionKeyKind) switch
    {
        (PartitionMethod.List, PartitionKeyKind.Text) => TableEditText.T("예: 서울, 부산", "e.g. Seoul, Busan"),
        (PartitionMethod.List, _) => TableEditText.T("예: 1, 2", "e.g. 1, 2"),
        (_, PartitionKeyKind.Date) => TableEditText.T("예: 2026-12-01 (이 날짜 미만)", "e.g. 2026-12-01 (before this date)"),
        (_, PartitionKeyKind.Number) => TableEditText.T("예: 1000 (이 값 미만)", "e.g. 1000 (below this value)"),
        _ => string.Empty,
    };

    // ---- 쉬운 설정(일반 → 파티션) -------------------------------------------

    /// <summary>쉬운 설정 칸을 보인다: 일반 테이블이고 아직 바꾸기를 시작하지 않았고 바꾸기를 할 수 있다.</summary>
    public bool ShowsEasyPartitioning => CanChoosePartitionMethod && !IsPartitionConversion;

    /// <summary>쉬운 설정에 고를 수 있는 날짜 열(DATE·TIMESTAMP).</summary>
    public IReadOnlyList<string> EasyDateColumns =>
        [.. Draft.Columns.Where(c => !c.IsDropped && !c.IsVirtual && PartitionBounds.KindOf(c.DataType) == PartitionKeyKind.Date).Select(c => c.Name)];

    public bool HasEasyDateColumns => EasyDateColumns.Count > 0;

    /// <summary>쉬운 설정으로 만들 모양 한 줄(무엇이 어디로 가는지).</summary>
    public string EasyPartitionSummary
    {
        get
        {
            if (!HasEasyDateColumns)
            {
                return TableEditText.T("날짜 열이 없어 쉬운 설정을 쓸 수 없습니다. 아래 「방식」에서 직접 고르세요.",
                    "There is no date column for the easy setup. Choose the method below instead.");
            }

            var unit = EasyUnit;
            var start = PartitionBounds.PeriodStart(Today(), unit);
            var first = FirstEasyName(start, unit);
            var period = unit switch
            {
                IntervalUnit.Day => TableEditText.T("날마다", "every day"),
                IntervalUnit.Year => TableEditText.T("해마다", "every year"),
                _ => TableEditText.T("달마다", "every month"),
            };
            var since = start.ToString(unit == IntervalUnit.Year ? "yyyy" : unit == IntervalUnit.Day ? "yyyy-MM-dd" : "yyyy-MM", CultureInfo.InvariantCulture);
            return EasyIntervalSupported
                ? TableEditText.T($"{since} 전 데이터는 {first} 하나에 들어가고, 그 뒤는 {period} 파티션을 DB 가 자동으로 만듭니다.",
                    $"Rows before {since} go into {first}; after that the database creates a partition {period} by itself.")
                : TableEditText.T($"{since} 전 데이터는 {first} 에 들어가고, {period} 파티션 12개를 미리 만듭니다(이 DB 는 자동 추가를 못 씀).",
                    $"Rows before {since} go into {first}, and 12 partitions are created ahead {period} (this database has no automatic add).");
        }
    }

    /// <summary>쉬운 설정 적용: 범위(고른 날짜 열) + 자동 추가 + 첫 파티션(지금 기간 전 전부). 자동 추가를 못 쓰는 DB 는 앞으로 12개를 미리 만든다.</summary>
    public bool ApplyEasyPartitioning()
    {
        if (!ShowsEasyPartitioning || EasyPartitionColumn is not { Length: > 0 } column || !EasyDateColumns.Contains(column))
        {
            return false;
        }

        var unit = EasyUnit;
        var start = PartitionBounds.PeriodStart(Today(), unit);
        var rows = new List<PartitionDraft> { new(DraftKeys.New(), FirstEasyName(start, unit)) { Bound = PartitionBounds.DateSql(start) } };
        string? interval = PartitionBounds.IntervalOf(unit);
        if (!EasyIntervalSupported)
        {
            interval = null;
            rows.AddRange(BoundFiller.Fill(new BoundFill(unit switch { IntervalUnit.Day => BoundUnit.Day, IntervalUnit.Year => BoundUnit.Year, _ => BoundUnit.Month }, 12)
            {
                StartDate = DateOnly.FromDateTime(start),
            }, [rows[0].Name]));
        }

        _partitionKeyInput = null;
        _partitionHeaderError = null;
        UpdatePartitions(d => d with
        {
            Scheme = new PartitionScheme(PartitionMethod.Range, [column]) { Interval = interval },
            Partitions = rows,
        });
        return IsPartitionConversion;
    }

    private IntervalUnit EasyUnit => EasyPartitionUnitIndex switch
    {
        0 => IntervalUnit.Day,
        2 => IntervalUnit.Year,
        _ => IntervalUnit.Month,
    };

    private bool EasyIntervalSupported => Dialect.UnsupportedPartitionReasonKey(PartitionOperation.SetInterval, PartitionMethod.Range, Context.Server) is null;

    private static string FirstEasyName(DateTime start, IntervalUnit unit) =>
        "P_BEFORE_" + start.ToString(unit == IntervalUnit.Year ? "yyyy" : unit == IntervalUnit.Day ? "yyyy_MM_dd" : "yyyy_MM", CultureInfo.InvariantCulture);

    partial void OnEasyPartitionUnitIndexChanged(int value) => OnPropertyChanged(nameof(EasyPartitionSummary));

    // ---- 자동 추가(INTERVAL) ------------------------------------------------

    /// <summary>자동 추가 칸 목록. 날짜 키 = 끔·매일·매월·매년·직접 입력, 그 밖 = 끔·직접 입력(수 간격).</summary>
    public IReadOnlyList<string> PartitionIntervalChoices => PartitionKeyKind == PartitionKeyKind.Date
        ? [IntervalOffText, TableEditText.T("매일", "Daily"), TableEditText.T("매월", "Monthly"), TableEditText.T("매년", "Yearly"), IntervalCustomText]
        : [IntervalOffText, IntervalCustomText];

    private static string IntervalOffText => TableEditText.T("끔", "Off");

    private static string IntervalCustomText => TableEditText.T("직접 입력…", "Custom…");

    /// <summary>자동 추가 칸에서 고른 것.</summary>
    public string PartitionIntervalChoice
    {
        get
        {
            var unit = PartitionBounds.UnitOf(_partitionDraft?.Scheme?.Interval);
            if (_intervalCustom || unit == IntervalUnit.Custom || (unit != IntervalUnit.Off && PartitionKeyKind != PartitionKeyKind.Date))
            {
                return IntervalCustomText;
            }

            return unit switch
            {
                IntervalUnit.Day => PartitionIntervalChoices[1],
                IntervalUnit.Month => PartitionIntervalChoices[2],
                IntervalUnit.Year => PartitionIntervalChoices[3],
                _ => IntervalOffText,
            };
        }
        set
        {
            if (!CanEditPartitionInterval || value is null || string.Equals(value, PartitionIntervalChoice, StringComparison.Ordinal))
            {
                return;
            }

            if (string.Equals(value, IntervalCustomText, StringComparison.Ordinal))
            {
                _intervalCustom = true;
                OnPropertyChanged(nameof(PartitionIntervalChoice));
                OnPropertyChanged(nameof(ShowsIntervalText));
                return;
            }

            _intervalCustom = false;
            var index = PartitionIntervalChoices.ToList().IndexOf(value);
            var unit = index switch
            {
                1 => IntervalUnit.Day,
                2 => IntervalUnit.Month,
                3 => IntervalUnit.Year,
                _ => IntervalUnit.Off,
            };
            SetInterval(PartitionBounds.IntervalOf(unit));
        }
    }

    /// <summary>자동 추가 식 칸(직접 입력)을 보인다.</summary>
    public bool ShowsIntervalText => PartitionIntervalChoice == IntervalCustomText;

    /// <summary>「자동 추가 켜기」 안내: 날짜 범위 테이블인데 자동 추가가 꺼져 있다.</summary>
    public bool ShowsAutoAddOffer => CanEditPartitionInterval && !IsPartitionConversion && PartitionKeyKind == PartitionKeyKind.Date
        && string.IsNullOrWhiteSpace(_partitionDraft?.Scheme?.Interval);

    /// <summary>「자동 추가 켜기」 단추 글(짐작한 단위).</summary>
    public string AutoAddOfferButtonText => GuessedUnit switch
    {
        IntervalUnit.Day => TableEditText.T("매일 자동 추가 켜기", "Add daily automatically"),
        IntervalUnit.Year => TableEditText.T("매년 자동 추가 켜기", "Add yearly automatically"),
        _ => TableEditText.T("매월 자동 추가 켜기", "Add monthly automatically"),
    };

    /// <summary>자동 추가를 켠다(기존 경계 간격으로 짐작한 단위).</summary>
    public void EnableAutoAdd()
    {
        if (ShowsAutoAddOffer)
        {
            _intervalCustom = false;
            SetInterval(PartitionBounds.IntervalOf(GuessedUnit));
        }
    }

    private IntervalUnit GuessedUnit => PartitionBounds.GuessUnit([.. (_partitionDraft?.Partitions ?? []).Where(p => !p.IsDropped).Select(p => p.Bound)]);

    private bool IntervalOn => !string.IsNullOrWhiteSpace(_partitionDraft?.Scheme?.Interval);

    private void SetInterval(string? interval)
    {
        if (!string.Equals(interval, _partitionDraft?.Scheme?.Interval, StringComparison.Ordinal))
        {
            UpdatePartitions(d => d with { Scheme = d.Scheme! with { Interval = interval } });
        }
        else
        {
            OnPropertyChanged(nameof(PartitionIntervalChoice));
            OnPropertyChanged(nameof(ShowsIntervalText));
        }
    }

    // ---- 줄 더하기 · 경계 · 이름 ---------------------------------------------

    /// <summary>「＋」 단추 글: 범위 = 다음 파티션, 그 밖 = 더하기.</summary>
    public string AddPartitionText => CurrentPartitionMethod == PartitionMethod.Range
        ? TableEditText.T("＋ 다음 파티션", "＋ Next partition")
        : TableEditText.T("＋ 더하기", "＋ Add");

    /// <summary>「＋」 도움말(못 쓰면 그 이유).</summary>
    public string AddPartitionTip => CurrentPartitionMethod == PartitionMethod.Range && IntervalOn && !IsPartitionConversion
        ? TableEditText.T("자동 추가가 켜져 있어 새 파티션은 DB 가 알아서 만듭니다", "Automatic add is on, so the database creates new partitions by itself")
        : PartitionOperationReason(PartitionOperation.Add)
          ?? (CurrentPartitionMethod == PartitionMethod.Range
              ? TableEditText.T("앞 파티션 간격대로 다음 파티션을 끝에 붙입니다", "Appends the next partition with the same step as the previous ones")
              : TableEditText.T("새 파티션을 끝에 붙입니다", "Appends a new partition"));

    /// <summary>범위 새 줄: 앞 간격을 이은 이름·경계. 짐작할 수 없으면 null.</summary>
    private PartitionDraft? NextRangeRow()
    {
        if (CurrentPartitionMethod != PartitionMethod.Range)
        {
            return null;
        }

        var bounds = (_partitionDraft?.Partitions ?? []).Where(p => !p.IsDropped && p.MergedInto is null).Select(p => p.Bound).ToList();
        return PartitionBounds.NextRange(bounds) is { } next
            ? new PartitionDraft(DraftKeys.New(), UniquePartitionName(i => i == 1 ? next.Name : next.Name + "_" + i.ToString(CultureInfo.InvariantCulture))) { Bound = next.Bound }
            : null;
    }

    /// <summary>
    /// 새 줄 경계 칸(보통 값): SQL 로 바꿔 넣고, 이름이 자동 이름(<c>P_NEW…</c>·앞 경계로 지은 이름)이면 새 경계로 다시 짓는다.
    /// </summary>
    internal void SetNewPartitionBound(string rowKey, string? plain)
    {
        if (_partitionDraft is null || CurrentPartitionMethod is not { } method)
        {
            return;
        }

        var list = _partitionDraft.Partitions;
        var index = list.ToList().FindIndex(p => p.RowKey == rowKey);
        if (index < 0)
        {
            return;
        }

        var row = list[index];
        var bound = PartitionBounds.ToSql(plain, PartitionKeyKind);
        if (string.Equals(bound, row.Bound, StringComparison.Ordinal))
        {
            return;
        }

        var previous = list.Take(index).LastOrDefault(p => !p.IsDropped && !string.IsNullOrWhiteSpace(p.Bound))?.Bound;
        var name = row.Name;
        if (AutoNamed(row, previous, method) && PartitionBounds.SuggestName(previous, bound, method) is { } suggested)
        {
            var taken = list.Where(p => p.RowKey != rowKey).Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
            name = suggested;
            for (var i = 2; taken.Contains(name); i++)
            {
                name = suggested + "_" + i.ToString(CultureInfo.InvariantCulture);
            }
        }

        UpdatePartition(rowKey, p => p with { Bound = bound, Name = name });
    }

    /// <summary>사람이 지은 이름이 아니다: 비었거나 <c>P_NEW…</c>, 지금 경계로 지은 이름(나누기 줄 이름은 그대로 둔다).</summary>
    private static bool AutoNamed(PartitionDraft row, string? previous, PartitionMethod method)
    {
        if (string.IsNullOrWhiteSpace(row.Name) || AutoName().IsMatch(row.Name))
        {
            return true;
        }

        if (PartitionBounds.SuggestName(previous, row.Bound, method) is { } old
            && (row.Name == old || row.Name.StartsWith(old + "_", StringComparison.Ordinal)))
        {
            return true;
        }

        return false;
    }

    [GeneratedRegex(@"^P_NEW\d*$", RegexOptions.CultureInvariant)]
    private static partial Regex AutoName();

    private void RaiseEasyPartition()
    {
        OnPropertyChanged(nameof(PartitionKeyKind));
        OnPropertyChanged(nameof(BoundPlaceholder));
        OnPropertyChanged(nameof(ShowsEasyPartitioning));
        OnPropertyChanged(nameof(EasyDateColumns));
        OnPropertyChanged(nameof(HasEasyDateColumns));
        OnPropertyChanged(nameof(EasyPartitionSummary));
        OnPropertyChanged(nameof(PartitionIntervalChoices));
        OnPropertyChanged(nameof(PartitionIntervalChoice));
        OnPropertyChanged(nameof(ShowsIntervalText));
        OnPropertyChanged(nameof(ShowsAutoAddOffer));
        OnPropertyChanged(nameof(AutoAddOfferButtonText));
        OnPropertyChanged(nameof(AddPartitionText));
        OnPropertyChanged(nameof(AddPartitionTip));
        if (EasyPartitionColumn is null || !EasyDateColumns.Contains(EasyPartitionColumn))
        {
            EasyPartitionColumn = EasyDateColumns is [var first, ..] ? first : null;
        }
    }
}
