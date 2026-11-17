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

    private ColumnDraft _column = new(string.Empty, string.Empty);
    private ColumnChange _changes;

    internal ColumnRowViewModel(TableEditSession session, string key)
        : base(session, key)
    {
    }

    public ColumnDraft Column => _column;

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
            if (size is { } sz)
            {
                // VARCHAR2(20 CHAR)·NUMBER(10,2): 괄호 값은 길이·정밀도 칸으로(모델은 형식 이름에 괄호를 넣지 않는다).
                var numeric = Numeric.Contains(type!);
                Session.UpdateColumn(Key, c => numeric
                    ? c with { DataType = type, Precision = sz.First, Scale = sz.Second }
                    : c with { DataType = type, Length = sz.First, CharSemantics = sz.Semantics ?? c.CharSemantics });
            }
            else if (type != _column.DataType)
            {
                Session.UpdateColumn(Key, c => c with { DataType = type });
            }
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
            if (CanEditLength && value != _column.IsNullable)
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

    /// <summary>신원 열(TE-7: 속성은 안 고침, 이름·주석은 고침).</summary>
    public bool IsIdentity => _column.Identity is not null;

    /// <summary>이름 칸(제한 테이블이 아니고 지울 줄이 아님).</summary>
    public bool CanEditName => CanEditCells;

    /// <summary>형식 칸(신원 열 제외). 가상 열은 형식을 비워도 된다.</summary>
    public bool CanEditType => CanEditCells && !IsIdentity;

    /// <summary>길이·NULL 칸(가상 열·신원 열은 흐림).</summary>
    public bool CanEditLength => CanEditCells && !IsVirtual && !IsIdentity;

    /// <summary>소수 칸(숫자 형식만).</summary>
    public bool CanEditScale => CanEditLength && IsNumeric;

    /// <summary>기본값 칸: 제한된 테이블에서도 고칠 수 있다(설계 3장), 가상·신원 열은 흐림.</summary>
    public bool CanEditDefault => !IsDropped && !IsVirtual && !IsIdentity;

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

    public string ColumnsText
    {
        get => _columnsInput ?? TableEditCellText.IndexColumnsText(Session.Draft, _index.Columns);
        set
        {
            if (!CanEditCells || SameAsShown(value, ColumnsText))
            {
                return;
            }

            var (columns, unknown) = TableEditCellText.IndexColumns(Session.Draft, value);
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

    /// <summary>NORMAL 이 아닌 인덱스(함수 기반·비트맵 …)는 지우기만(TE-9).</summary>
    public bool IsDropOnly => _index.Kind != IndexKind.Normal;

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
