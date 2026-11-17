using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.App.Features.Localization;
using Oranda.Core.Schema.TableEdit;
using Oranda.Core.Schema.TableEdit.Partitions;

namespace Oranda.App.Features.TableEdit;

/// <summary>
/// 「파티션」 페이지 한 줄(설계 18.4 표: 이름 · 상한값/값 목록 · 테이블스페이스 · 행 수). 상태 색은 다른 페이지와 같다(새 줄 초록 · 지울 줄 빨강 줄긋기),
/// 비우기·합치기·나누기는 줄 머리 표시와 풍선 도움말로. 원본 줄의 경계·테이블스페이스는 고칠 수 없다(문장이 없음 — 나누기·합치기를 쓴다).
/// 칸이 보이던 글 그대로 다시 들어오면 모델을 건드리지 않는다(<see cref="TableEditRowViewModel"/> 와 같은 규칙).
/// </summary>
public sealed class PartitionRowViewModel : ObservableObject
{
    private PartitionDraft _row;
    private PartitionDraft? _original;
    private PartitionRowDiff? _diff;
    private IReadOnlyList<string> _problems = [];
    private PartitionMethod? _method;

    internal PartitionRowViewModel(TableEditSession session, string key)
    {
        Session = session;
        Key = key;
        _row = new PartitionDraft(key, string.Empty);
    }

    public TableEditSession Session { get; }

    public string Key { get; }

    /// <summary>지금 편집 모델의 줄.</summary>
    public PartitionDraft Row => _row;

    /// <summary>원본에 없는 줄(새 줄 · 나누기로 떼어 낸 줄).</summary>
    public bool IsNew => _original is null;

    /// <summary>새 줄(초록). 지운 새 줄은 목록에서 빠지므로 늘 새 줄 = 초록.</summary>
    public bool IsAdded => IsNew;

    /// <summary>지울 줄(빨강 줄긋기, 해시는 줄이기).</summary>
    public bool IsDropped => _row.IsDropped;

    public bool IsTruncated => _row.IsTruncated;

    /// <summary>다음 줄로 합쳐져 사라지는 줄.</summary>
    public bool IsMergedAway => _row.MergedInto is not null;

    /// <summary>나누기로 떼어 낸 새 줄.</summary>
    public bool IsSplitOff => IsNew && _row.SplitFrom is not null;

    /// <summary>원본과 다른 줄(문장이 있거나 문제가 있음).</summary>
    public bool IsModified => !IsNew && (_diff is not null || _problems.Count > 0 || _row != _original);

    public bool IsNameChanged => _original is not null && !string.Equals(_original.Name, _row.Name, StringComparison.Ordinal);

    /// <summary>문장으로 만들 수 없는 편집의 이유(지역화).</summary>
    public string? ProblemText => _problems.Count == 0 ? null : string.Join(Environment.NewLine, _problems.Select(k => UiText.Get(k, k)).Distinct(StringComparer.Ordinal));

    public bool HasProblem => _problems.Count > 0;

    /// <summary>아직 다 쓰지 않은 새 줄(이름·경계가 빔). 문장은 Core 가 그대로 만들므로 실행 전에 채워야 한다.</summary>
    public string? Error
    {
        get
        {
            if (!IsNew || _row.IsDropped || _row.MergedInto is not null)
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(_row.Name))
            {
                return TableEditText.T("이름을 쓰세요", "Enter a name");
            }

            return _method is PartitionMethod.Range or PartitionMethod.List && string.IsNullOrWhiteSpace(_row.Bound)
                ? _method == PartitionMethod.Range ? TableEditText.T("상한값을 쓰세요", "Enter the upper bound") : TableEditText.T("값 목록을 쓰세요", "Enter the values")
                : null;
        }
    }

    public bool HasError => Error is not null;

    /// <summary>줄 머리 표시: ⚠ 문제 · − 지움 · ⇣ 합쳐짐 · ∅ 비움 · ⑂ 떼어 낸 새 줄 · + 새 줄 · ✎ 바뀜.</summary>
    public string StateMark
    {
        get
        {
            var mark = _row.IsDropped ? "−"
                : IsMergedAway ? "⇣"
                : IsSplitOff ? "⑂"
                : IsNew ? "+"
                : _row.IsTruncated ? "∅"
                : IsModified ? "✎"
                : string.Empty;
            return HasProblem || HasError ? "⚠" + mark : mark;
        }
    }

    /// <summary>줄 풍선 도움말(상태 설명 · 문제 이유 · 입력 오류). 없으면 null.</summary>
    public string? Tip
    {
        get
        {
            var lines = new List<string>();
            if (Error is { } error)
            {
                lines.Add(error);
            }

            if (ProblemText is { } problem)
            {
                lines.Add(problem);
            }

            if (_row.IsDropped)
            {
                lines.Add(_method == PartitionMethod.Hash
                    ? TableEditText.T("해시 파티션 줄이기(COALESCE): 행은 남은 파티션으로 옮겨집니다", "Hash partition coalesce: rows move to the remaining partitions")
                    : TableEditText.T("이 파티션을 지웁니다(행이 지워지고 되돌릴 수 없음)", "Drops this partition (its rows are deleted and cannot be restored)"));
            }
            else if (IsMergedAway)
            {
                var into = Session.PartitionRows.FirstOrDefault(r => r.Key == _row.MergedInto)?.Name ?? "?";
                lines.Add(TableEditText.T($"{into} 파티션으로 합칩니다", $"Merges into partition {into}"));
            }
            else if (IsSplitOff)
            {
                var from = Session.PartitionRows.FirstOrDefault(r => r.Key == _row.SplitFrom)?.Name ?? "?";
                lines.Add(TableEditText.T($"{from} 파티션에서 떼어 냅니다(나누기)", $"Split off partition {from}"));
            }
            else if (_row.IsTruncated)
            {
                lines.Add(TableEditText.T("이 파티션을 비웁니다(행이 지워지고 되돌릴 수 없음)", "Truncates this partition (its rows are deleted and cannot be restored)"));
            }

            return lines.Count == 0 ? null : string.Join(Environment.NewLine, lines);
        }
    }

    /// <summary>줄 칸을 고칠 수 있다(파티션을 고칠 수 있고, 지우거나 합쳐지는 줄이 아님).</summary>
    public bool CanEditCells => Session.CanEditPartitions && !_row.IsDropped && _row.MergedInto is null;

    public bool CanEditName => CanEditCells;

    /// <summary>경계 칸: 새 줄만(원본 경계 바꾸기는 문장이 없다), 해시는 경계가 없다.</summary>
    public bool CanEditBound => CanEditCells && IsNew && _method is PartitionMethod.Range or PartitionMethod.List;

    /// <summary>테이블스페이스 칸: 새 줄만.</summary>
    public bool CanEditTablespace => CanEditCells && IsNew;

    public string Name
    {
        get => TableEditCellText.Show(_row.Name);
        set
        {
            if (!CanEditName || SameAsShown(value, Name))
            {
                return;
            }

            var name = TableEditCellText.Parse(value);
            if (!string.Equals(name, _row.Name, StringComparison.Ordinal))
            {
                Session.UpdatePartition(Key, p => p with { Name = name });
            }
        }
    }

    /// <summary>
    /// 상한값 · 값 목록을 보통 값으로(<c>2026-12-01</c>, <c>A, B</c>). 쓰면 SQL 로 바꿔 넣고 자동 이름을 다시 짓는다(<see cref="TableEditSession.SetNewPartitionBound"/>).
    /// 해시는 빈 글.
    /// </summary>
    public string BoundText
    {
        get => PartitionBounds.ToPlain(_row.Bound);
        set
        {
            if (!CanEditBound || SameAsShown(value, BoundText))
            {
                return;
            }

            Session.SetNewPartitionBound(Key, value);
        }
    }

    /// <summary>경계 칸 빈 글 안내.</summary>
    public string BoundPlaceholder => IsNew ? Session.BoundPlaceholder : string.Empty;

    /// <summary>테이블스페이스(빈 글 = 테이블 기본).</summary>
    public string Tablespace
    {
        get => TableEditCellText.Show(_row.Tablespace);
        set
        {
            if (!CanEditTablespace || SameAsShown(value, Tablespace))
            {
                return;
            }

            var space = string.IsNullOrWhiteSpace(value) ? null : TableEditCellText.Parse(value);
            if (!string.Equals(space, _row.Tablespace, StringComparison.Ordinal))
            {
                Session.UpdatePartition(Key, p => p with { Tablespace = space });
            }
        }
    }

    /// <summary>통계 행 수(천 단위 쉼표). 모르면 빈 글.</summary>
    public string RowCountText => _row.RowCount?.ToString("N0", CultureInfo.InvariantCulture) ?? string.Empty;

    /// <summary>모델이 바뀐 뒤 줄 내용을 맞춘다(세션의 줄 맞추기가 부른다).</summary>
    internal void Sync(PartitionDraft row, PartitionDraft? original, PartitionRowDiff? diff, IReadOnlyList<string> problems, PartitionMethod? method)
    {
        _row = row;
        _original = original;
        _diff = diff;
        _problems = problems;
        _method = method;
        OnPropertyChanged(string.Empty);
    }

    private static bool SameAsShown(string? value, string shown) => string.Equals(value ?? string.Empty, shown, StringComparison.Ordinal);
}
