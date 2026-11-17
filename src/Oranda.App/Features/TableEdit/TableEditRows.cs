using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.Core.Metadata;
using Oranda.Core.Schema.TableEdit;

namespace Oranda.App.Features.TableEdit;

/// <summary>편집 표 한 줄의 공통 부분: 줄 키·상태 색(노랑·초록·빨강, 설계 2장)·칸 입력 오류.</summary>
public abstract class TableEditRowViewModel : ObservableObject
{
    private DraftRowState _state;
    private string? _error;

    protected TableEditRowViewModel(TableEditSession session, string key)
    {
        Session = session;
        Key = key;
    }

    public TableEditSession Session { get; }

    public string Key { get; }

    public DraftRowState State
    {
        get => _state;
        protected set
        {
            if (SetProperty(ref _state, value))
            {
                OnPropertyChanged(nameof(IsAdded));
                OnPropertyChanged(nameof(IsDropped));
                OnPropertyChanged(nameof(IsModified));
                OnPropertyChanged(nameof(StateMark));
                OnPropertyChanged(nameof(CanEditCells));
            }
        }
    }

    /// <summary>새 줄(초록).</summary>
    public bool IsAdded => State == DraftRowState.Added || (State == DraftRowState.Unchanged && Session.IsNewRow(Key));

    /// <summary>지울 줄(빨강 줄긋기).</summary>
    public bool IsDropped => State == DraftRowState.Dropped;

    public bool IsModified => State == DraftRowState.Modified;

    /// <summary>줄 머리 표시(+ 새 줄, − 지울 줄, ✎ 바뀐 줄).</summary>
    public string StateMark => State switch
    {
        DraftRowState.Added => "+",
        DraftRowState.Dropped => "−",
        DraftRowState.Modified => "✎",
        _ => IsAdded ? "+" : string.Empty,
    };

    /// <summary>칸을 고칠 수 있다(지울 줄이 아니고 제한된 테이블이 아님 — 줄 종류별 조건은 각 칸 속성).</summary>
    public virtual bool CanEditCells => !IsDropped && !Session.IsRestricted;

    /// <summary>마지막 칸 입력이 모델에 들어가지 못한 이유(모르는 열 이름 등). 없으면 null.</summary>
    public string? Error
    {
        get => _error;
        protected set
        {
            if (SetProperty(ref _error, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => Error is not null;

    protected void SetState(DraftRowState state) => State = state;

    /// <summary>모델에 못 들어간 칸 입력과 그 오류를 지운다(되돌리기·지우기 때).</summary>
    internal virtual void ClearInput() => Error = null;

    /// <summary>
    /// 칸이 보이던 글 그대로 다시 들어왔다(칸을 벗어나기만 함). 그때는 모델을 건드리지 않는다 — 서버 표기 앞뒤 공백을 잘라 거짓 차이를 만들지 않게(설계 8.4).
    /// </summary>
    protected static bool SameAsShown(string? value, string shown) => string.Equals(value ?? string.Empty, shown, StringComparison.Ordinal);

    /// <summary>모든 칸 속성을 다시 알린다(모델이 바뀐 뒤).</summary>
    protected void RaiseAll() => OnPropertyChanged(string.Empty);
}

/// <summary>열 페이지 한 줄(설계 7.2): 이름·형식·길이·소수·NULL·기본값·가상 식·주석.</summary>
public sealed class ColumnRowViewModel : TableEditRowViewModel
{
    private static readonly HashSet<string> Numeric = new(StringComparer.Ordinal) { "NUMBER", "FLOAT", "DECIMAL", "INTEGER", "NUMERIC" };

    /// <summary>크기 칸 계열: number(정밀도·소수) · float(2진 정밀도) · other(길이·길이 기준).</summary>
    private static string Family(string? type) => type switch
    {
        "FLOAT" => "float",
        { } t when Numeric.Contains(t) => "number",
        _ => "other",
    };

    private ColumnDraft _column = new(string.Empty, string.Empty);
    private ColumnChange _changes;

    internal ColumnRowViewModel(TableEditSession session, string key)
        : base(session, key)
    {
    }

    public ColumnDraft Column => _column;

    /// <summary>칸을 고칠 수 있다: 일반 테이블, 또는 GTT·IOT 의 새 열(설계 18.10). 지울 줄은 아님.</summary>
    public override bool CanEditCells => !IsDropped && (!Session.IsRestricted || (Session.AllowsColumnAddDrop && Session.IsNewRow(Key)));

    // ---- 신원·숨김·도메인(설계 18.5) ----------------------------------------------------

    /// <summary>신원 칸 번호: 0 없음 · 1 ALWAYS · 2 BY DEFAULT · 3 BY DEFAULT ON NULL. 속성을 못 읽은 신원 열은 4(「알 수 없음」, 흐림).</summary>
    public int IdentityIndex
    {
        get => _column.IsIdentityUnknown ? 4 : _column.Identity?.Generation switch
        {
            IdentityGeneration.Always => 1,
            IdentityGeneration.ByDefault => 2,
            IdentityGeneration.ByDefaultOnNull => 3,
            _ => 0,
        };
        set
        {
            if (!CanEditIdentity || value == IdentityIndex || value is < 0 or > 3)
            {
                return;
            }

            Session.UpdateColumn(Key, c => value == 0
                ? c with { Identity = null, IsIdentityUnknown = false }
                : c with
                {
                    Identity = (c.Identity ?? new IdentityInfo(IdentityGeneration.ByDefault)) with
                    {
                        Generation = value switch { 1 => IdentityGeneration.Always, 2 => IdentityGeneration.ByDefault, _ => IdentityGeneration.ByDefaultOnNull },
                    },
                });
        }
    }

    /// <summary>신원 열인데 속성을 못 읽었다 — 「신원 없애기」만 된다(te-core 권고).</summary>
    public bool IsIdentityUnknown => _column.IsIdentityUnknown;

    public string IdentityStartText
    {
        get => _column.Identity?.StartWith?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        set => SetIdentityNumber(value, IdentityStartText, (i, n) => i with { StartWith = n });
    }

    public string IdentityIncrementText
    {
        get => _column.Identity?.IncrementBy?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        set => SetIdentityNumber(value, IdentityIncrementText, (i, n) => i with { IncrementBy = n });
    }

    /// <summary>「신원 없애기」(속성을 모르는 신원 열 포함).</summary>
    public void DropIdentity()
    {
        if (CanEditCells && IdentityReason is null && _column.IsIdentityColumn)
        {
            Session.UpdateColumn(Key, c => c with { Identity = null, IsIdentityUnknown = false });
        }
    }

    public bool IsInvisible
    {
        get => _column.IsInvisible;
        set
        {
            if (CanEditInvisible && value != _column.IsInvisible)
            {
                Session.UpdateColumn(Key, c => c with { IsInvisible = value });
            }
        }
    }

    public string Domain
    {
        get => _column.Domain ?? string.Empty;
        set
        {
            if (!CanEditDomain || SameAsShown(value, Domain))
            {
                return;
            }

            var v = string.IsNullOrWhiteSpace(value) ? null : TableEditCellText.Parse(value);
            if (!string.Equals(v, _column.Domain, StringComparison.Ordinal))
            {
                Session.UpdateColumn(Key, c => c with { Domain = v });
            }
        }
    }

    /// <summary>방언이 신원 열을 못 하는 이유(설계 18.8). 되면 null.</summary>
    public string? IdentityReason => Session.Reason(TableEditFeature.IdentityColumn);

    public string? InvisibleReason => Session.Reason(TableEditFeature.InvisibleColumn);

    public string? DomainReason => Session.Reason(TableEditFeature.DomainColumn);

    /// <summary>신원 칸: 방언이 되고, 속성을 아는 열(모르면 「신원 없애기」만), 가상 열이 아님.</summary>
    public bool CanEditIdentity => CanEditCells && IdentityReason is null && !_column.IsIdentityUnknown && !IsVirtual;

    public bool CanEditIdentityOptions => CanEditIdentity && _column.Identity is not null;

    /// <summary>「신원 없애기」 단추(속성을 모르는 신원 열).</summary>
    public bool CanDropUnknownIdentity => CanEditCells && IdentityReason is null && _column.IsIdentityUnknown;

    public bool CanEditInvisible => CanEditCells && InvisibleReason is null;

    public bool CanEditDomain => CanEditCells && DomainReason is null && !IsVirtual;

    /// <summary>신원 칸 툴팁(못 쓰는 이유 또는 「알 수 없음」 안내).</summary>
    public string? IdentityTip => IdentityReason
        ?? (_column.IsIdentityUnknown ? TableEditText.T("서버에서 신원 속성을 읽지 못했습니다. 「신원 없애기」만 됩니다", "The identity attributes could not be read. Only “Drop identity” is possible") : null);

    public bool IsIdentityChanged => _changes.HasFlag(ColumnChange.Identity);

    public bool IsInvisibleChanged => _changes.HasFlag(ColumnChange.Invisible);

    public bool IsDomainChanged => _changes.HasFlag(ColumnChange.Domain);

    private void SetIdentityNumber(string? value, string shown, Func<IdentityInfo, long?, IdentityInfo> apply)
    {
        if (!CanEditIdentityOptions || SameAsShown(value, shown))
        {
            return;
        }

        long? n = null;
        if (!string.IsNullOrWhiteSpace(value))
        {
            if (!long.TryParse(value.Trim(), System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
            {
                Error = TableEditText.T("신원 시작·증가는 정수입니다", "Identity start and increment must be whole numbers");
                OnPropertyChanged(string.Empty);
                return;
            }

            n = parsed;
        }

        Error = null;
        Session.UpdateColumn(Key, c => c.Identity is { } i ? c with { Identity = apply(i, n) } : c);
    }

    /// <summary>원래 위치(「#」 칸, 편집 중에도 원래 순서). 새 열은 빈 글.</summary>
    public string PositionText => _column.OriginalPosition?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;

    public string Name
    {
        get => TableEditCellText.Show(_column.Name);
        set
        {
            if (!CanEditName || SameAsShown(value, Name))
            {
                return;
            }

            var name = TableEditCellText.Parse(value);
            if (name != _column.Name)
            {
                Session.UpdateColumn(Key, c => c with { Name = name });
            }
        }
    }

    public string DataType
    {
        get => _column.DataType ?? string.Empty;
        set
        {
            if (!CanEditType || SameAsShown(value, DataType))
            {
                return;
            }

            var (type, size) = TableEditCellText.ParseType(value);
            if (size is null && type == _column.DataType)
            {
                return;
            }

            var original = Session.Original.Column(Key);
            var familyChanged = Family(type) != Family(_column.DataType);
            Session.UpdateColumn(Key, c =>
            {
                var next = c with { DataType = type };
                if (familyChanged)
                {
                    // 계열(숫자·FLOAT·문자/그 밖)이 바뀌면 크기 칸을 모두 비운다 — 숨은 옛 길이·정밀도가 문장에 남지 않게(FLOAT 정밀도는 2진 자리라 따로).
                    next = next with { Length = null, CharSemantics = null, Precision = null, Scale = null };
                }

                if (size is { } sz)
                {
                    // VARCHAR2(20 CHAR)·NUMBER(10,2): 괄호 값은 길이·정밀도 칸으로(모델은 형식 이름에 괄호를 넣지 않는다).
                    return Family(type) is "number" or "float"
                        ? next with { Precision = sz.First, Scale = sz.Second, Length = null, CharSemantics = null }
                        : next with { Length = sz.First, CharSemantics = sz.Semantics ?? next.CharSemantics, Precision = null, Scale = null };
                }

                // 원래 형식으로 돌아오면 원래 크기를 되살린다(CHAR(10) → NUMBER → CHAR 가 거짓 MODIFY 를 만들지 않게).
                return original is not null && type == original.DataType && familyChanged
                    ? next with { Length = original.Length, CharSemantics = original.CharSemantics, Precision = original.Precision, Scale = original.Scale }
                    : next;
            });
        }
    }

    /// <summary>숫자 형식이면 정밀도·소수 칸, 아니면 길이 칸.</summary>
    public bool IsNumeric => _column.DataType is { } t && Numeric.Contains(t);

    /// <summary>길이(문자) 또는 정밀도(숫자) 칸.</summary>
    public string LengthText
    {
        get => (IsNumeric ? _column.Precision : _column.Length)?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        set
        {
            if (!CanEditLength || SameAsShown(value, LengthText))
            {
                Error = null;
                return;
            }

            if (!TableEditCellText.TryParseInt(value, out var n))
            {
                Error = TableEditText.T("길이는 0 이상 정수입니다", "The length must be a whole number");
                OnPropertyChanged();
                return;
            }

            Error = null;
            if (IsNumeric ? n != _column.Precision : n != _column.Length)
            {
                Session.UpdateColumn(Key, c => IsNumeric ? c with { Precision = n } : c with { Length = n });
            }
        }
    }

    public string ScaleText
    {
        get => IsNumeric ? _column.Scale?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty : string.Empty;
        set
        {
            if (!CanEditScale || SameAsShown(value, ScaleText))
            {
                return;
            }

            if (!TableEditCellText.TryParseInt(value, out var n))
            {
                Error = TableEditText.T("소수 자리는 0 이상 정수입니다", "The scale must be a whole number");
                OnPropertyChanged();
                return;
            }

            Error = null;
            if (n != _column.Scale)
            {
                Session.UpdateColumn(Key, c => c with { Scale = n });
            }
        }
    }

    public bool IsNullable
    {
        get => _column.IsNullable;
        set
        {
            if (CanEditNullable && value != _column.IsNullable)
            {
                Session.UpdateColumn(Key, c => c with { IsNullable = value });
            }
        }
    }

    public string DefaultValue
    {
        get => _column.DefaultValue ?? string.Empty;
        set
        {
            if (!CanEditDefault || SameAsShown(value, DefaultValue))
            {
                return;
            }

            var v = string.IsNullOrWhiteSpace(value) ? null : value;
            if (!string.Equals(v, _column.DefaultValue, StringComparison.Ordinal) && !(v is null && string.IsNullOrWhiteSpace(_column.DefaultValue)))
            {
                Session.UpdateColumn(Key, c => c with { DefaultValue = v });
            }
        }
    }

    public string VirtualExpression
    {
        get => _column.VirtualExpression ?? string.Empty;
        set
        {
            if (!CanEditVirtual || SameAsShown(value, VirtualExpression))
            {
                return;
            }

            var v = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (!string.Equals(v, _column.VirtualExpression, StringComparison.Ordinal))
            {
                Session.UpdateColumn(Key, c => c with { VirtualExpression = v });
                Session.RaiseVirtualExpressionEdited(Key);
            }
        }
    }

    public string Comment
    {
        get => _column.Comment ?? string.Empty;
        set
        {
            if (!CanEditComment || SameAsShown(value, Comment))
            {
                return;
            }

            var v = string.IsNullOrEmpty(value) ? null : value;
            if (!string.Equals(v ?? string.Empty, _column.Comment ?? string.Empty, StringComparison.Ordinal))
            {
                Session.UpdateColumn(Key, c => c with { Comment = v });
            }
        }
    }

    public bool IsVirtual => _column.IsVirtual;

    /// <summary>신원 열(속성을 알든 모르든). 기본값 칸은 흐린다(신원 열은 기본값을 쓰지 않음).</summary>
    public bool IsIdentity => _column.IsIdentityColumn;

    /// <summary>이름 칸(제한 테이블이 아니고 지울 줄이 아님).</summary>
    public bool CanEditName => CanEditCells;

    /// <summary>형식 칸. 가상 열은 형식을 비워도 된다.</summary>
    public bool CanEditType => CanEditCells;

    /// <summary>길이·NULL 칸(가상 열은 흐림). 신원 열의 기본값·NULL 은 Core 검사가 판정한다(설계 18.5).</summary>
    public bool CanEditLength => CanEditCells && !IsVirtual;

    /// <summary>소수 칸(숫자 형식만).</summary>
    public bool CanEditScale => CanEditLength && IsNumeric;

    /// <summary>기본값 칸: 제한된 테이블에서도 고칠 수 있다(설계 3장), 가상·신원 열은 흐림.</summary>
    public bool CanEditDefault => !IsDropped && !IsVirtual && !IsIdentity;

    /// <summary>NULL 칸(가상 열은 흐림).</summary>
    public bool CanEditNullable => CanEditCells && !IsVirtual;

    /// <summary>가상 식 칸: 새 열 또는 원래 가상 열만(일반 열을 가상 열로 바꾸는 문장은 없다).</summary>
    public bool CanEditVirtual => CanEditCells && (Session.IsNewRow(Key) || Session.Original.Column(Key)?.IsVirtual == true);

    /// <summary>주석 칸: 제한된 테이블에서도 고칠 수 있다.</summary>
    public bool CanEditComment => !IsDropped;

    public bool IsNameChanged => _changes.HasFlag(ColumnChange.Name);

    public bool IsTypeChanged => _changes.HasFlag(ColumnChange.Type);

    public bool IsNullableChanged => _changes.HasFlag(ColumnChange.Nullable);

    public bool IsDefaultChanged => _changes.HasFlag(ColumnChange.Default);

    public bool IsVirtualChanged => _changes.HasFlag(ColumnChange.VirtualExpression);

    public bool IsCommentChanged => _changes.HasFlag(ColumnChange.Comment);

    internal void Sync(ColumnDraft column, ColumnDiff? diff)
    {
        _column = column;
        _changes = diff?.State == DraftRowState.Modified ? diff.Changes : ColumnChange.None;
        SetState(diff?.State ?? DraftRowState.Unchanged);
        RaiseAll();
    }
}

/// <summary>제약 페이지 한 줄(설계 7.3): 이름·종류·열·참조·조건·사용.</summary>
public sealed class ConstraintRowViewModel : TableEditRowViewModel
{
    private ConstraintDraft _constraint = new(string.Empty, string.Empty, ConstraintKind.Check);
    private ConstraintChange _changes;
    private string? _columnsInput;
    private string? _referenceInput;

    internal ConstraintRowViewModel(TableEditSession session, string key)
        : base(session, key)
    {
    }

    public ConstraintDraft Constraint => _constraint;

    public string Name
    {
        get => TableEditCellText.Show(_constraint.Name);
        set
        {
            if (!CanEditCells || SameAsShown(value, Name))
            {
                return;
            }

            var name = TableEditCellText.Parse(value);
            if (name != _constraint.Name)
            {
                Session.UpdateConstraint(Key, c => c with { Name = name, IsAutoName = false });
            }
        }
    }

    public string KindText => _constraint.Kind switch
    {
        ConstraintKind.PrimaryKey => "PK",
        ConstraintKind.Unique => "UK",
        ConstraintKind.ForeignKey => "FK",
        _ => "CHECK",
    };

    public bool IsForeignKey => _constraint.Kind == ConstraintKind.ForeignKey;

    public bool IsCheck => _constraint.Kind == ConstraintKind.Check;

    /// <summary>열 목록(<c>A, B</c>). 모르는 이름이면 모델을 바꾸지 않고 <see cref="TableEditRowViewModel.Error"/>.</summary>
    public string ColumnsText
    {
        get => _columnsInput ?? TableEditCellText.ColumnsText(Session.Draft, _constraint.ColumnKeys);
        set
        {
            if (!CanEditColumns || SameAsShown(value, ColumnsText))
            {
                return;
            }

            var (keys, unknown) = TableEditCellText.ColumnKeys(Session.Draft, value);
            if (keys is null)
            {
                _columnsInput = value;
                Error = TableEditText.T($"이 테이블에 없는 열: {unknown}", $"No such column in this table: {unknown}");
                OnPropertyChanged();
                return;
            }

            _columnsInput = null;
            Error = null;
            if (!keys.SequenceEqual(_constraint.ColumnKeys))
            {
                Session.UpdateConstraint(Key, c => c with { ColumnKeys = keys });
            }
        }
    }

    /// <summary>FK 참조(<c>SAMPLE.ORDERS (ORDER_ID)</c>).</summary>
    public string ReferenceText
    {
        get => _referenceInput ?? TableEditCellText.ReferenceText(_constraint);
        set
        {
            if (!CanEditReference || SameAsShown(value, ReferenceText))
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(value))
            {
                _referenceInput = null;
                Error = null;
                Session.UpdateConstraint(Key, c => c with { ReferencedTable = null, ReferencedColumns = [] });
                return;
            }

            if (TableEditCellText.ParseReference(value, Session.Draft.Owner) is not { } r)
            {
                _referenceInput = value;
                Error = TableEditText.T("참조는 「소유자.테이블 (열, …)」 꼴로 씁니다", "Write the reference as OWNER.TABLE (COLUMN, …)");
                OnPropertyChanged();
                return;
            }

            _referenceInput = null;
            Error = null;
            if (r.Table != _constraint.ReferencedTable || !r.Columns.SequenceEqual(_constraint.ReferencedColumns))
            {
                Session.UpdateConstraint(Key, c => c with { ReferencedTable = r.Table, ReferencedColumns = r.Columns, ReferencedConstraint = null });
            }
        }
    }

    /// <summary>FK 삭제 규칙 칸 번호(0 없음, 1 CASCADE, 2 SET NULL).</summary>
    public int DeleteRuleIndex
    {
        get => (int)_constraint.DeleteRule;
        set
        {
            if (CanEditReference && value is >= 0 and <= 2 && (DeleteRule)value != _constraint.DeleteRule)
            {
                Session.UpdateConstraint(Key, c => c with { DeleteRule = (DeleteRule)value });
            }
        }
    }

    public string Condition
    {
        get => _constraint.Condition ?? string.Empty;
        set
        {
            if (!CanEditCondition || SameAsShown(value, Condition))
            {
                return;
            }

            var v = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (!string.Equals(v, _constraint.Condition, StringComparison.Ordinal))
            {
                Session.UpdateConstraint(Key, c => c with { Condition = v });
            }
        }
    }

    public bool IsEnabled
    {
        get => _constraint.IsEnabled;
        set
        {
            if (CanEditCells && value != _constraint.IsEnabled)
            {
                Session.UpdateConstraint(Key, c => c with { IsEnabled = value });
            }
        }
    }

    /// <summary>서버가 지은 이름(SYS_C… 등) — 다른 DB 에서 실패(TE-8, ⚠ 표시).</summary>
    public bool IsServerName => _constraint.Name.Length > 0 && Session.Dialect.IsServerName(_constraint.Name);

    public bool CanEditReference => CanEditCells && IsForeignKey;

    public bool CanEditCondition => CanEditCells && IsCheck;

    public bool CanEditColumns => CanEditCells && !IsCheck;

    public bool IsNameChanged => _changes.HasFlag(ConstraintChange.Name);

    public bool IsDefinitionChanged => _changes.HasFlag(ConstraintChange.Definition);

    public bool IsEnabledChanged => _changes.HasFlag(ConstraintChange.Enabled);

    internal override void ClearInput()
    {
        _columnsInput = null;
        _referenceInput = null;
        base.ClearInput();
    }

    internal void Sync(ConstraintDraft constraint, ConstraintDiff? diff)
    {
        _constraint = constraint;
        _changes = diff?.State == DraftRowState.Modified ? diff.Changes : ConstraintChange.None;
        SetState(diff?.State ?? DraftRowState.Unchanged);
        RaiseAll();
    }
}

/// <summary>인덱스 페이지 한 줄(설계 7.4): 이름·유일·종류·열(ASC/DESC)·테이블스페이스.</summary>
public sealed class IndexRowViewModel : TableEditRowViewModel
{
    private IndexDraft _index = new(string.Empty, string.Empty);
    private IndexChange _changes;
    private string? _columnsInput;

    internal IndexRowViewModel(TableEditSession session, string key)
        : base(session, key)
    {
    }

    public IndexDraft Index => _index;

    public string Name
    {
        get => TableEditCellText.Show(_index.Name);
        set
        {
            if (!CanEditCells || SameAsShown(value, Name))
            {
                return;
            }

            var name = TableEditCellText.Parse(value);
            if (name != _index.Name)
            {
                Session.UpdateIndex(Key, i => i with { Name = name, IsAutoName = false });
            }
        }
    }

    public bool IsUnique
    {
        get => _index.IsUnique;
        set
        {
            if (CanEditCells && value != _index.IsUnique)
            {
                Session.UpdateIndex(Key, i => i with { IsUnique = value });
            }
        }
    }

    public string KindText => _index.Kind switch
    {
        IndexKind.Normal => "NORMAL",
        IndexKind.FunctionBased => "FUNCTION-BASED",
        IndexKind.Bitmap => "BITMAP",
        _ => TableEditText.T("기타", "OTHER"),
    };

    /// <summary>종류 칸 번호: 0 NORMAL · 1 FUNCTION-BASED · 2 BITMAP(설계 18.6). 그 밖의 종류는 3(지우기만).</summary>
    public int KindIndex
    {
        get => _index.Kind switch { IndexKind.Normal => 0, IndexKind.FunctionBased => 1, IndexKind.Bitmap => 2, _ => 3 };
        set
        {
            if (!CanEditCells || value == KindIndex || value is < 0 or > 2)
            {
                return;
            }

            var kind = value switch { 1 => IndexKind.FunctionBased, 2 => IndexKind.Bitmap, _ => IndexKind.Normal };
            if (ReasonFor(kind) is { } reason)
            {
                Error = reason;
                OnPropertyChanged(string.Empty);
                return;
            }

            if (kind != IndexKind.FunctionBased && _index.Columns.Any(c => c.Expression is not null))
            {
                // 식을 말없이 버리지 않는다: 식 자리를 먼저 열로 바꾸게 한다.
                Error = TableEditText.T("식이 있는 인덱스는 먼저 열 칸에서 식을 빼야 일반·비트맵으로 바꿀 수 있습니다", "Remove the expressions from the columns cell before switching to NORMAL or BITMAP");
                OnPropertyChanged(string.Empty);
                return;
            }

            Error = null;
            Session.UpdateIndex(Key, i => i with { Kind = kind });
        }
    }

    /// <summary>방언이 그 종류를 못 만드는 이유(설계 18.8). 되면 null.</summary>
    private string? ReasonFor(IndexKind kind) => kind switch
    {
        IndexKind.FunctionBased => Session.Reason(TableEditFeature.FunctionBasedIndex),
        IndexKind.Bitmap => Session.Reason(TableEditFeature.BitmapIndex),
        _ => null,
    };

    /// <summary>종류 칸 툴팁: 이 서버가 못 만드는 종류와 그 이유.</summary>
    public string? KindTip => string.Join(Environment.NewLine, new[]
    {
        ReasonFor(IndexKind.FunctionBased) is { } f ? "FUNCTION-BASED: " + f : null,
        ReasonFor(IndexKind.Bitmap) is { } b ? "BITMAP: " + b : null,
    }.Where(x => x is not null)) is { Length: > 0 } tip ? tip : null;

    public string ColumnsText
    {
        get => _columnsInput ?? TableEditCellText.IndexColumnsText(Session.Draft, _index.Columns);
        set
        {
            if (!CanEditCells || SameAsShown(value, ColumnsText))
            {
                return;
            }

            var (columns, unknown) = TableEditCellText.IndexColumns(Session.Draft, value, allowExpressions: _index.Kind == IndexKind.FunctionBased);
            if (columns is null)
            {
                _columnsInput = value;
                Error = TableEditText.T($"이 테이블에 없는 열: {unknown}", $"No such column in this table: {unknown}");
                OnPropertyChanged();
                return;
            }

            _columnsInput = null;
            Error = null;
            if (!columns.SequenceEqual(_index.Columns))
            {
                Session.UpdateIndex(Key, i => i with { Columns = columns });
            }
        }
    }

    public string Tablespace
    {
        get => _index.Tablespace ?? string.Empty;
        set
        {
            if (!CanEditCells || SameAsShown(value, Tablespace))
            {
                return;
            }

            var v = string.IsNullOrWhiteSpace(value) ? null : TableEditCellText.Parse(value);
            if (!string.Equals(v, _index.Tablespace, StringComparison.Ordinal))
            {
                Session.UpdateIndex(Key, i => i with { Tablespace = v });
            }
        }
    }

    /// <summary>PK·UK 가 만든 인덱스 — 제약 페이지에서 고친다(흐림, 설계 7.4).</summary>
    public bool IsConstraintIndex => _index.ConstraintName is not null;

    /// <summary>NORMAL·함수 기반·비트맵이 아닌 인덱스(도메인·LOB …)는 지우기만(설계 18.6, TE-9 를 뒤집고 남은 것).</summary>
    public bool IsDropOnly => _index.Kind == IndexKind.Other;

    public override bool CanEditCells => base.CanEditCells && !IsConstraintIndex && !IsDropOnly;

    /// <summary>줄을 흐리게 보이는 이유(없으면 빈 글).</summary>
    public string LockedReason => IsConstraintIndex
        ? TableEditText.T($"제약 {_index.ConstraintName} 이 만든 인덱스입니다. 제약 페이지에서 고칩니다", $"Created by constraint {_index.ConstraintName}. Change it on the Constraints page")
        : IsDropOnly ? TableEditText.T("이 종류의 인덱스는 지우기만 됩니다", "Indexes of this kind can only be dropped") : string.Empty;

    public bool IsNameChanged => _changes.HasFlag(IndexChange.Name);

    public bool IsDefinitionChanged => _changes.HasFlag(IndexChange.Definition);

    internal override void ClearInput()
    {
        _columnsInput = null;
        base.ClearInput();
    }

    internal void Sync(IndexDraft index, IndexDiff? diff)
    {
        _index = index;
        _changes = diff?.State == DraftRowState.Modified ? diff.Changes : IndexChange.None;
        SetState(diff?.State ?? DraftRowState.Unchanged);
        RaiseAll();
    }
}
