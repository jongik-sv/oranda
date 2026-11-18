using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.Core.Schema.TableEdit;
using Oranda.Core.Schema.TableEdit.Partitions;

namespace Oranda.App.Features.TableEdit;

/// <summary>
/// 「파티션」 페이지의 하위 파티션 칸(파티션 추천 설계 PA-11, 6장): 일반 → 파티션 바꾸기 중일 때만 방식(없음·LIST·HASH)·키 열·템플릿 줄 표(LIST) 또는 개수(HASH)를
/// 고친다. 이미 파티션된 테이블의 하위 파티션은 지금처럼 보기만(<see cref="SubpartitionText"/>). 모델 = <see cref="PartitionScheme.SubpartitionTemplate"/>, 문장은 Core 가 만든다.
/// 같은 파일에 「월 추가 스크립트 저장…」(PA-5): INTERVAL 없는 범위 + 마지막 MAXVALUE 테이블에서 다음 달들을 SPLIT 하는 스크립트를 파일로.
/// </summary>
public sealed partial class TableEditSession
{
    private const int DefaultHashSubpartitions = 4;

    private string? _subpartitionKeyInput;

    /// <summary>하위 파티션 템플릿 줄(LIST). 줄 위치가 같은 동안 같은 객체를 쓴다.</summary>
    public ObservableCollection<SubpartitionRowViewModel> SubpartitionRows { get; } = [];

    /// <summary>고른 하위 템플릿 줄(「－ 줄 지우기」 대상).</summary>
    [ObservableProperty]
    private SubpartitionRowViewModel? _selectedSubpartitionRow;

    private SubpartitionTemplate? CurrentSubpartition => _partitionDraft?.Scheme?.SubpartitionTemplate;

    /// <summary>하위 파티션 칸을 보인다: 바꾸기 중이고 고칠 수 있다. 아니면 위 줄의 보기 글(<see cref="SubpartitionText"/>).</summary>
    public bool ShowsSubpartitionEditor => IsPartitionConversion && CanEditPartitions;

    /// <summary>하위 파티션 방식 칸(0 없음 · 1 LIST · 2 HASH). 바꾸면 키 열은 남기고 줄·개수는 기본값으로.</summary>
    public int SubpartitionMethodIndex
    {
        get => CurrentSubpartition?.Method switch
        {
            PartitionMethod.List => 1,
            PartitionMethod.Hash => 2,
            _ => 0,
        };
        set
        {
            if (!ShowsSubpartitionEditor || value is < 0 or > 2 || value == SubpartitionMethodIndex)
            {
                return;
            }

            var keys = CurrentSubpartition?.KeyColumns ?? [];
            SubpartitionTemplate? next = value switch
            {
                1 => new SubpartitionTemplate(PartitionMethod.List, keys) { Rows = [new SubpartitionTemplateRow("SP_OTHER", "DEFAULT")] },
                2 => new SubpartitionTemplate(PartitionMethod.Hash, keys) { HashCount = DefaultHashSubpartitions },
                _ => null,
            };
            _subpartitionKeyInput = null;
            UpdateSubpartition(_ => next);
        }
    }

    public bool IsListSubpartition => CurrentSubpartition?.Method == PartitionMethod.List;

    public bool IsHashSubpartition => CurrentSubpartition?.Method == PartitionMethod.Hash;

    /// <summary>방식을 골랐다(키 열 칸을 보인다).</summary>
    public bool HasSubpartition => CurrentSubpartition is not null;

    /// <summary>하위 키 열(<c>A, B</c>). 모르는 열이면 모델을 바꾸지 않고 <see cref="PartitionHeaderError"/>.</summary>
    public string SubpartitionKeyText
    {
        get => _subpartitionKeyInput ?? string.Join(", ", (CurrentSubpartition?.KeyColumns ?? []).Select(TableEditCellText.Show));
        set
        {
            if (CurrentSubpartition is not { } sub || string.Equals(value ?? string.Empty, SubpartitionKeyText, StringComparison.Ordinal))
            {
                return;
            }

            var names = new List<string>();
            foreach (var part in TableEditCellText.SplitList(value))
            {
                var name = TableEditCellText.Parse(part);
                if (!Draft.Columns.Any(c => !c.IsDropped && string.Equals(c.Name, name, StringComparison.Ordinal)))
                {
                    _subpartitionKeyInput = value;
                    SetPartitionHeaderError(TableEditText.T($"이 테이블에 없는 열: {part}", $"No such column in this table: {part}"));
                    OnPropertyChanged(nameof(SubpartitionKeyText));
                    return;
                }

                names.Add(name);
            }

            _subpartitionKeyInput = null;
            SetPartitionHeaderError(null);
            if (!sub.KeyColumns.SequenceEqual(names, StringComparer.Ordinal))
            {
                UpdateSubpartition(s => s! with { KeyColumns = names });
            }
            else
            {
                OnPropertyChanged(nameof(SubpartitionKeyText));
            }
        }
    }

    /// <summary>HASH 하위 개수 칸(1~1024 정수, 틀리면 모델을 바꾸지 않는다).</summary>
    public string SubpartitionHashCountText
    {
        get => CurrentSubpartition is { Method: PartitionMethod.Hash } sub ? sub.HashCount.ToString(CultureInfo.InvariantCulture) : string.Empty;
        set
        {
            if (CurrentSubpartition is not { Method: PartitionMethod.Hash } sub)
            {
                return;
            }

            if (!int.TryParse(value?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var count) || count is < 1 or > 1024)
            {
                SetPartitionHeaderError(TableEditText.T("하위 파티션 개수는 1~1024 사이 정수입니다", "The subpartition count must be a whole number from 1 to 1024"));
                OnPropertyChanged(nameof(SubpartitionHashCountText));
                return;
            }

            SetPartitionHeaderError(null);
            if (count != sub.HashCount)
            {
                UpdateSubpartition(s => s! with { HashCount = count });
            }
        }
    }

    /// <summary>하위 키 열 하나의 값 종류(값 목록 칸 보통 값 ↔ SQL).</summary>
    internal PartitionKeyKind SubpartitionKeyKind =>
        CurrentSubpartition?.KeyColumns is [var only]
        && Draft.Columns.FirstOrDefault(c => !c.IsDropped && string.Equals(c.Name, only, StringComparison.Ordinal)) is { } column
            ? PartitionBounds.KindOf(column.DataType)
            : PartitionKeyKind.Other;

    /// <summary>「＋ 줄」: 새 LIST 줄(이름 <c>SP_n</c>, 값 빔). 마지막 줄이 DEFAULT 면 그 앞에 넣는다(DEFAULT 는 끝에 있어야 한다).</summary>
    public SubpartitionRowViewModel? AddSubpartitionRow()
    {
        if (CurrentSubpartition is not { Method: PartitionMethod.List } sub)
        {
            return null;
        }

        var taken = sub.Rows.Select(r => r.Name).ToHashSet(StringComparer.Ordinal);
        var name = Enumerable.Range(1, int.MaxValue).Select(i => "SP_" + i.ToString(CultureInfo.InvariantCulture)).First(n => !taken.Contains(n));
        var rows = sub.Rows.ToList();
        var at = rows.Count > 0 && (string.IsNullOrWhiteSpace(rows[^1].Values) || string.Equals(rows[^1].Values!.Trim(), "DEFAULT", StringComparison.OrdinalIgnoreCase)) ? rows.Count - 1 : rows.Count;
        rows.Insert(at, new SubpartitionTemplateRow(name, null));
        UpdateSubpartition(s => s! with { Rows = rows });
        return SelectedSubpartitionRow = SubpartitionRows.ElementAtOrDefault(at);
    }

    /// <summary>「－ 줄」: 고른 LIST 줄을 뺀다.</summary>
    public void RemoveSelectedSubpartitionRow()
    {
        if (CurrentSubpartition is not { Method: PartitionMethod.List } sub || SelectedSubpartitionRow is not { } row || row.Index >= sub.Rows.Count)
        {
            return;
        }

        UpdateSubpartition(s => s! with { Rows = [.. s.Rows.Where((_, i) => i != row.Index)] });
    }

    /// <summary>줄 칸에서 이름·값을 바꾼다(값은 보통 값 → SQL, 키 종류에 맞춤).</summary>
    internal void UpdateSubpartitionRow(int index, string? name, string? plainValues)
    {
        if (CurrentSubpartition is not { Method: PartitionMethod.List } sub || index < 0 || index >= sub.Rows.Count)
        {
            return;
        }

        var old = sub.Rows[index];
        var next = old with
        {
            Name = name is null ? old.Name : TableEditCellText.Parse(name),
            Values = plainValues is null ? old.Values : PartitionBounds.ToSql(plainValues, SubpartitionKeyKind),
        };
        if (next != old)
        {
            UpdateSubpartition(s => s! with { Rows = [.. s.Rows.Select((r, i) => i == index ? next : r)] });
        }
    }

    private void UpdateSubpartition(Func<SubpartitionTemplate?, SubpartitionTemplate?> change)
    {
        if (!ShowsSubpartitionEditor)
        {
            return;
        }

        UpdatePartitions(d => d with { Scheme = d.Scheme! with { SubpartitionTemplate = change(d.Scheme.SubpartitionTemplate) } });
    }

    /// <summary>하위 칸의 실행 전 검사(✖): 키 열 빔·없는 열, LIST 줄 없음·이름 빔·이름 겹침. <see cref="PartitionFindings"/> 가 바꾸기 때 부른다.</summary>
    private IEnumerable<CheckFinding> SubpartitionFindings(HashSet<string> liveColumns)
    {
        if (CurrentSubpartition is not { } sub)
        {
            yield break;
        }

        CheckFinding Block(string subject, string ko, string en) =>
            new(CheckSeverity.Block, CheckCode.InvalidDefinition, TableDraft.TableRowKey) { Subject = subject, Detail = TableEditText.T(ko, en) };

        if (sub.KeyColumns.Count == 0)
        {
            yield return Block(Draft.Name, "하위 파티션 키 열을 고르세요", "choose the subpartition key columns");
        }

        foreach (var key in sub.KeyColumns.Where(k => !liveColumns.Contains(k)))
        {
            yield return Block(key, "하위 파티션 키 열이 이 테이블에 없습니다(이름을 바꿨거나 지운 열)", "the subpartition key column is not in the table (renamed or dropped)");
        }

        if (sub.Method != PartitionMethod.List)
        {
            yield break;
        }

        if (sub.Rows.Count == 0)
        {
            yield return Block(Draft.Name, "하위 파티션 줄을 하나 이상 더하세요", "add at least one subpartition row");
        }

        if (sub.Rows.Any(r => string.IsNullOrWhiteSpace(r.Name)))
        {
            yield return Block(Draft.Name, "하위 파티션 이름을 쓰세요", "enter the subpartition names");
        }

        foreach (var name in sub.Rows.GroupBy(r => r.Name, StringComparer.Ordinal).Where(g => g.Key.Length > 0 && g.Count() > 1).Select(g => g.Key))
        {
            yield return Block(name, "같은 이름의 하위 파티션이 둘 이상입니다", "two subpartitions have the same name");
        }
    }

    /// <summary>LOCAL 로 만들 인덱스(PA-12)가 지금 편집 모델에 없으면 ✖(지웠거나 이름을 바꾼 인덱스).</summary>
    private IEnumerable<CheckFinding> LocalIndexFindings()
    {
        var live = Draft.Indexes.Where(i => !i.IsDropped).Select(i => i.Name).ToHashSet(StringComparer.Ordinal);
        return (_partitionDraft?.LocalIndexes ?? []).Where(i => !live.Contains(i)).Select(i => new CheckFinding(CheckSeverity.Block, CheckCode.InvalidDefinition, TableDraft.TableRowKey)
        {
            Subject = i,
            Detail = TableEditText.T("LOCAL 로 만들 인덱스가 이 테이블에 없습니다(지웠거나 이름을 바꾼 인덱스)", "the index to make LOCAL is not on the table (dropped or renamed)"),
        });
    }

    /// <summary>하위 칸 줄을 맞춘다(<see cref="SyncPartitionRows"/> 끝에서).</summary>
    private void SyncSubpartitionRows()
    {
        var rows = CurrentSubpartition is { Method: PartitionMethod.List } sub ? sub.Rows : [];
        while (SubpartitionRows.Count > rows.Count)
        {
            SubpartitionRows.RemoveAt(SubpartitionRows.Count - 1);
        }

        for (var i = 0; i < rows.Count; i++)
        {
            if (i == SubpartitionRows.Count)
            {
                SubpartitionRows.Add(new SubpartitionRowViewModel(this, i));
            }

            SubpartitionRows[i].Sync(rows[i]);
        }

        if (SelectedSubpartitionRow is { } selected && !SubpartitionRows.Contains(selected))
        {
            SelectedSubpartitionRow = null;
        }

        if (CurrentSubpartition is null)
        {
            _subpartitionKeyInput = null;
        }

        OnPropertyChanged(nameof(ShowsSubpartitionEditor));
        OnPropertyChanged(nameof(SubpartitionMethodIndex));
        OnPropertyChanged(nameof(IsListSubpartition));
        OnPropertyChanged(nameof(IsHashSubpartition));
        OnPropertyChanged(nameof(HasSubpartition));
        OnPropertyChanged(nameof(SubpartitionKeyText));
        OnPropertyChanged(nameof(SubpartitionHashCountText));
        RaiseMonthAdd();
    }

    // ---- 월 추가 스크립트(PA-5) ------------------------------------------------

    private Func<Task>? _saveMonthAddScript;

    /// <summary>객체 상세가 넣는 「월 추가 스크립트 저장…」(글 = <see cref="BuildMonthAddScript"/>, 기존 스크립트 저장 방식). null = 그 길이 없음.</summary>
    public Func<Task>? SaveMonthAddScript
    {
        get => _saveMonthAddScript;
        set
        {
            _saveMonthAddScript = value;
            RaiseMonthAdd();
        }
    }

    /// <summary>서버 상태(원본 모델) 기준 월 추가 계획. 조건(범위 · INTERVAL 없음 · 마지막 MAXVALUE · 앞 경계가 달 첫날)이 안 맞으면 null.</summary>
    public MonthAddPlan? MonthAddPlan => _partitionOriginal is { Scheme: not null } original ? PartitionMonthScript.Plan(original) : null;

    /// <summary>「월 추가 스크립트 저장…」 단추를 보인다(조건이 맞을 때만).</summary>
    public bool ShowsMonthAddScript => _saveMonthAddScript is not null && CanEditPartitions && MonthAddPlan is not null;

    /// <summary>단추 도움말: 무엇을 만드는지.</summary>
    public string MonthAddScriptTip => MonthAddPlan is { } plan
        ? TableEditText.T($"{plan.MaxPartition} 을 달마다 나눠 {plan.Months[0].Name} ~ {plan.Months[^1].Name} ({plan.Months.Count}개월)을 만드는 스크립트를 저장합니다(실행하지 않음)",
            $"Saves a script that splits {plan.MaxPartition} month by month into {plan.Months[0].Name} … {plan.Months[^1].Name} ({plan.Months.Count} months); it is not run")
        : string.Empty;

    /// <summary>월 추가 스크립트 글(머리 주석 + 문장). 조건이 안 맞으면 null. 전역 인덱스는 방언이 요구하면(Tibero) 끝에 REBUILD.</summary>
    public string? BuildMonthAddScript() => MonthAddPlan is { } plan
        ? PartitionMonthScript.Script(Original.Owner, Original.Name, plan, Dialect, _globalIndexes)
        : null;

    public Task SaveMonthAddScriptAsync() => ShowsMonthAddScript && _saveMonthAddScript is { } save ? save() : Task.CompletedTask;

    private void RaiseMonthAdd()
    {
        OnPropertyChanged(nameof(MonthAddPlan));
        OnPropertyChanged(nameof(ShowsMonthAddScript));
        OnPropertyChanged(nameof(MonthAddScriptTip));
    }
}
