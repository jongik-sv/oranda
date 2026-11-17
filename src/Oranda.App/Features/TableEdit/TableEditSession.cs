using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.Core.Connection;
using Oranda.Core.Metadata;
using Oranda.Core.Schema.TableEdit;

namespace Oranda.App.Features.TableEdit;

/// <summary>편집 세션이 아는 접속 조건(이름 한도·환경·서버).</summary>
/// <param name="MaxIdentifierBytes">식별자 바이트 한도(<see cref="ITableEditDialect.MaxIdentifierBytes"/>)</param>
public sealed record TableEditContext(int MaxIdentifierBytes, ConnectionEnvironment Environment, ServerInfo? Server = null);

/// <summary>
/// 객체 상세 탭 하나의 테이블 편집 상태(설계 7.1, TE-3). 원본(<see cref="Original"/>)과 편집 모델(<see cref="Draft"/>)을 쥐고, 고칠 때마다
/// 차이(<see cref="Changes"/>)와 문장(<see cref="Script"/>)을 다시 만든다. 미리보기 글(<see cref="PreviewText"/>)과 실행 문장은 같은 <see cref="Script"/> 에서 나온다(설계 4장).
/// 페이지(열·제약·인덱스·설명)를 오가도 이 객체가 남아 고친 내용이 유지된다. 원본이 빈 모델이면 새 테이블(설계 7.5, 다른 탭이 같은 세션을 쓴다).
/// </summary>
public sealed partial class TableEditSession : ObservableObject
{
    private readonly ITableEditEngine _engine;

    public TableEditSession(TableDraft original, ITableEditDialect dialect, TableEditContext context, ITableEditEngine? engine = null)
        : this(original, original, dialect, context, engine)
    {
    }

    public TableEditSession(TableDraft original, TableDraft draft, ITableEditDialect dialect, TableEditContext context, ITableEditEngine? engine = null)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(dialect);
        ArgumentNullException.ThrowIfNull(context);
        _engine = engine ?? CoreTableEditEngine.Instance;
        Dialect = dialect;
        Context = context;
        Original = original;
        Draft = draft;
        Recompute();
    }

    public ITableEditDialect Dialect { get; }

    public TableEditContext Context { get; }

    public ITableEditEngine Engine => _engine;

    /// <summary>편집을 켤 때 읽은 원본(실행이 일부 성공하면 다시 읽은 원본으로 바뀜, TE-5).</summary>
    public TableDraft Original { get; private set; }

    /// <summary>지금 편집 모델.</summary>
    public TableDraft Draft { get; private set; }

    /// <summary>원본과 편집의 차이(정규화 뒤).</summary>
    public TableDraftChanges Changes { get; private set; } = new();

    /// <summary>미리보기·실행·저장 문장(같은 객체).</summary>
    public TableEditScript Script { get; private set; } = TableEditScript.Empty;

    /// <summary>문장을 만들지 못한 이유(빌더 예외). 있으면 실행을 막는다.</summary>
    public string? ScriptError { get; private set; }

    /// <summary>DDL 미리보기 글(문장마다 <c>;</c>).</summary>
    public string PreviewText => ScriptError is { } e ? "-- " + e : Script.PreviewText(Environment.NewLine);

    /// <summary>고친 것이 있다(버리기 확인 대상).</summary>
    public bool IsDirty => (ScriptError is null ? !Changes.IsEmpty : Draft != Original) || IsPartitionDirty;

    /// <summary>새 테이블 편집(원본이 빈 모델).</summary>
    public bool IsNewTable => Original.IsNewTable;

    /// <summary>일반 테이블이 아니다(임시·외부·IOT·클러스터): 열 주석·기본값만 고친다(설계 3장).</summary>
    public bool IsRestricted => Draft.Kind != TableKind.Heap && !IsNewTable;

    /// <summary>
    /// 특수 테이블 가운데 열 추가·지우기와 테이블 이름 바꾸기가 되는 것(GTT·IOT, 실측 — 설계 18.10). 외부·클러스터는 열 주석·기본값만.
    /// 제약·인덱스는 어느 특수 테이블에서도 고치지 않는다(Core 검사 계획과 같은 기준).
    /// </summary>
    public bool AllowsColumnAddDrop => !IsRestricted || Draft.Kind is TableKind.Temporary or TableKind.Iot;

    /// <summary>테이블 이름을 바꿀 수 있다(일반·GTT·IOT, 방언이 이름 바꾸기를 함, 설계 18.1).</summary>
    public bool CanRenameTable => !IsNewTable && AllowsColumnAddDrop && RenameTableReason is null;

    /// <summary>테이블 이름 바꾸기를 못 하는 방언 이유(문구). 되면 null.</summary>
    public string? RenameTableReason => Reason(TableEditFeature.RenameTable);

    /// <summary>방언이 그 기능을 못 하는 이유 문구(설계 18.8). 되면 null.</summary>
    public string? Reason(TableEditFeature feature) =>
        Dialect.UnsupportedReasonKey(feature, Context.Server) is { } key ? Oranda.App.Features.Localization.UiText.Get(key, key) : null;

    /// <summary>제한 이유(제한이 없으면 빈 글).</summary>
    public string RestrictionText => !IsRestricted ? string.Empty
        : AllowsColumnAddDrop
            ? TableEditText.T($"{KindName(Draft.Kind)} 테이블은 열 추가·지우기·주석·기본값과 테이블 이름만 고칠 수 있습니다",
                              $"On a {KindName(Draft.Kind)} table only adding/dropping columns, comments, defaults and the table name can be changed")
            : TableEditText.T($"{KindName(Draft.Kind)} 테이블은 열 주석·기본값만 고칠 수 있습니다", $"Only column comments and defaults can be changed on a {KindName(Draft.Kind)} table");

    /// <summary>
    /// 「설명」 페이지 테이블 이름(기존 테이블 = 이름 바꾸기, 설계 18.1). 새 테이블은 <see cref="SetTableName"/>(규칙 이름 따라 바뀜)을 쓴다.
    /// </summary>
    public string TableName
    {
        get => TableEditCellText.Show(Draft.Name);
        set
        {
            if (!CanRenameTable || string.Equals(value ?? string.Empty, TableName, StringComparison.Ordinal))
            {
                return;
            }

            var name = TableEditCellText.Parse(value);
            if (name.Length > 0 && name != Draft.Name)
            {
                Update(d => d with { Name = name });
            }
            else
            {
                OnPropertyChanged(nameof(TableName)); // 빈 이름은 받지 않고 칸을 원래 이름으로 되돌린다
            }
        }
    }

    /// <summary>테이블 이름이 바뀐 칸인가.</summary>
    public bool IsTableNameChanged => Changes.Renamed;

    /// <summary>「규칙 이름도 바꾸기」(TE-38, 기본 끔): 이름 바꾸기 때 <c>PK_&lt;옛 이름&gt;</c> 꼴 제약·인덱스도 따라 바꾼다.</summary>
    public bool RenameRuleNames
    {
        get => Draft.RenameRuleNames;
        set
        {
            if (value != Draft.RenameRuleNames)
            {
                Update(d => d with { RenameRuleNames = value });
            }
        }
    }

    /// <summary>아래 줄 요약(「변경 3건」).</summary>
    public string SummaryText => ScriptError is not null ? TableEditText.T("문장을 만들지 못했습니다", "Could not build the statements")
        : Script.IsEmpty ? TableEditText.T("바뀐 것 없음", "No changes")
        : TableEditText.T($"변경 {Script.Statements.Count}건", $"{Script.Statements.Count} change(s)");

    public ObservableCollection<ColumnRowViewModel> Columns { get; } = [];

    public ObservableCollection<ConstraintRowViewModel> Constraints { get; } = [];

    public ObservableCollection<IndexRowViewModel> Indexes { get; } = [];

    /// <summary>테이블 주석(「설명」 페이지).</summary>
    public string? TableComment
    {
        get => Draft.Comment;
        set
        {
            if (!string.Equals(Draft.Comment ?? string.Empty, value ?? string.Empty, StringComparison.Ordinal))
            {
                Update(d => d with { Comment = value });
            }
        }
    }

    /// <summary>「설명」 페이지 주석 칸이 바뀐 칸인가.</summary>
    public bool IsTableCommentChanged => Changes.CommentChanged;

    /// <summary>모델이 바뀌어 차이·문장을 다시 만든 뒤.</summary>
    public event EventHandler? Recomputed;

    /// <summary>가상 식 칸을 고쳤다(줄 키). 객체 상세가 미리보기를 돌린다(설계 7.6).</summary>
    public event EventHandler<string>? VirtualExpressionEdited;

    internal void RaiseVirtualExpressionEdited(string rowKey) => VirtualExpressionEdited?.Invoke(this, rowKey);

    /// <summary>페이지 이름 옆 바뀐 건수(설계 7.1).</summary>
    public int CountFor(TableEditPage page) => page == TableEditPage.Partitions
        ? PartitionChanges.Rows.Count + (PartitionChanges.IntervalChanged ? 1 : 0) + (PartitionChanges.Converting ? 1 : 0)
          + PartitionChanges.Problems.Select(p => p.RowKey).Distinct(StringComparer.Ordinal).Count(k => PartitionChanges.Rows.All(r => r.RowKey != k))
        : Changes.CountFor(page);

    /// <summary>편집 모델을 바꾼다(차이·문장·줄을 다시 만듦).</summary>
    public void Update(Func<TableDraft, TableDraft> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        var next = change(Draft);
        if (ReferenceEquals(next, Draft) || next == Draft)
        {
            return;
        }

        Draft = next;
        Recompute();
    }

    /// <summary>원본·편집 모델을 함께 바꾼다(실행이 일부 성공한 뒤 다시 읽은 원본, TE-5). <paramref name="draft"/> 가 null 이면 원본 그대로 편집을 새로 시작.</summary>
    public void Reset(TableDraft original, TableDraft? draft = null)
    {
        ArgumentNullException.ThrowIfNull(original);
        Original = original;
        Draft = draft ?? original;
        Columns.Clear();
        Constraints.Clear();
        Indexes.Clear();
        Recompute();
    }

    // ---- 열 ------------------------------------------------------------

    public void UpdateColumn(string rowKey, Func<ColumnDraft, ColumnDraft> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        Update(d => d with { Columns = [.. d.Columns.Select(c => c.RowKey == rowKey ? change(c) : c)] });
    }

    /// <summary>「＋」 새 열(끝에 붙임). 이름은 비어 있다.</summary>
    public ColumnRowViewModel? AddColumn()
    {
        if (!AllowsColumnAddDrop)
        {
            return null;
        }

        var column = new ColumnDraft(DraftKeys.New(), string.Empty) { DataType = "VARCHAR2", Length = 100 };
        Update(d => d with { Columns = [.. d.Columns, column] });
        return Columns.FirstOrDefault(r => r.Key == column.RowKey);
    }

    /// <summary>새 열은 ↑↓ 로 옮길 수 있다(기존 테이블은 새 열끼리만, TE-1).</summary>
    public bool CanMoveColumn(string rowKey, int delta)
    {
        if (!AllowsColumnAddDrop)
        {
            return false;
        }

        var list = Draft.Columns;
        var i = IndexOf(list, rowKey);
        var j = i + delta;
        return i >= 0 && j >= 0 && j < list.Count && IsNewRow(list[i].RowKey) && (IsNewTable || IsNewRow(list[j].RowKey));
    }

    public void MoveColumn(string rowKey, int delta)
    {
        if (!CanMoveColumn(rowKey, delta))
        {
            return;
        }

        Update(d =>
        {
            var list = d.Columns.ToList();
            var i = IndexOf(list, rowKey);
            (list[i], list[i + delta]) = (list[i + delta], list[i]);
            return d with { Columns = list };
        });
    }

    // ---- 제약 ----------------------------------------------------------

    /// <summary>PK 가 이미 있으면(지우지 않은) 메뉴에서 흐리다(설계 7.3).</summary>
    public bool CanAddPrimaryKey => !IsRestricted && !Draft.Constraints.Any(c => c.Kind == ConstraintKind.PrimaryKey && !c.IsDropped);

    public void UpdateConstraint(string rowKey, Func<ConstraintDraft, ConstraintDraft> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        Update(d => d with { Constraints = [.. d.Constraints.Select(c => c.RowKey == rowKey ? change(c) : c)] });
    }

    /// <summary>「＋ ▾」 새 제약(이름은 규칙대로, 설계 7.3).</summary>
    public ConstraintRowViewModel? AddConstraint(ConstraintKind kind)
    {
        if (IsRestricted || (kind == ConstraintKind.PrimaryKey && !CanAddPrimaryKey))
        {
            return null;
        }

        var name = NextName(kind switch
        {
            ConstraintKind.PrimaryKey => ConstraintNameKind.PrimaryKey,
            ConstraintKind.Unique => ConstraintNameKind.Unique,
            ConstraintKind.ForeignKey => ConstraintNameKind.ForeignKey,
            _ => ConstraintNameKind.Check,
        });
        var c = new ConstraintDraft(DraftKeys.New(), name, kind) { IsAutoName = true };
        Update(d => d with { Constraints = [.. d.Constraints, c] });
        return Constraints.FirstOrDefault(r => r.Key == c.RowKey);
    }

    // ---- 인덱스 --------------------------------------------------------

    public void UpdateIndex(string rowKey, Func<IndexDraft, IndexDraft> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        Update(d => d with { Indexes = [.. d.Indexes.Select(i => i.RowKey == rowKey ? change(i) : i)] });
    }

    /// <summary>「＋」 새 인덱스(NORMAL, 이름 규칙 <c>IX_&lt;T&gt;_NN</c>).</summary>
    public IndexRowViewModel? AddIndex()
    {
        if (IsRestricted)
        {
            return null;
        }

        var index = new IndexDraft(DraftKeys.New(), NextName(ConstraintNameKind.Index)) { IsAutoName = true };
        Update(d => d with { Indexes = [.. d.Indexes, index] });
        return Indexes.FirstOrDefault(r => r.Key == index.RowKey);
    }

    // ---- 줄 공통 ------------------------------------------------------

    /// <summary>
    /// 「－」 지우기 토글(빨강 줄긋기, 다시 누르면 되살림, 설계 7.2). 아직 실행하지 않은 새 줄은 목록에서 뺀다.
    /// 제한된 테이블·제약 인덱스·고칠 수 없는 줄은 그대로.
    /// </summary>
    public void ToggleDrop(string rowKey)
    {
        if (IsRestricted && !(AllowsColumnAddDrop && Draft.Column(rowKey) is not null))
        {
            return; // 특수 테이블: GTT·IOT 는 열만 지운다
        }

        RowOf(rowKey)?.ClearInput();

        if (Draft.Column(rowKey) is { } column)
        {
            if (IsNewRow(rowKey))
            {
                Update(d => d with { Columns = [.. d.Columns.Where(c => c.RowKey != rowKey)] });
            }
            else
            {
                UpdateColumn(rowKey, c => c with { IsDropped = !column.IsDropped });
            }
        }
        else if (Draft.Constraint(rowKey) is { } constraint)
        {
            if (IsNewRow(rowKey))
            {
                Update(d => d with { Constraints = [.. d.Constraints.Where(c => c.RowKey != rowKey)] });
            }
            else if (!constraint.IsDropped || CanRestore(constraint))
            {
                UpdateConstraint(rowKey, c => c with { IsDropped = !constraint.IsDropped });
            }
        }
        else if (Draft.Index(rowKey) is { } index && index.ConstraintName is null)
        {
            if (IsNewRow(rowKey))
            {
                Update(d => d with { Indexes = [.. d.Indexes.Where(i => i.RowKey != rowKey)] });
            }
            else
            {
                UpdateIndex(rowKey, i => i with { IsDropped = !index.IsDropped });
            }
        }
    }

    /// <summary>「↺」 줄을 원본대로 되돌린다(새 줄은 뺀다).</summary>
    public void Revert(string rowKey)
    {
        RowOf(rowKey)?.ClearInput();
        if (Original.Column(rowKey) is { } c)
        {
            UpdateColumn(rowKey, _ => c);
        }
        else if (Original.Constraint(rowKey) is { } k)
        {
            if (Draft.Constraint(rowKey) is { IsDropped: true } && !CanRestore(k))
            {
                return; // 되살리면 PK 가 둘이 된다
            }

            UpdateConstraint(rowKey, _ => k);
        }
        else if (Original.Index(rowKey) is { } i)
        {
            UpdateIndex(rowKey, _ => i);
        }
        else
        {
            Update(d => d with
            {
                Columns = [.. d.Columns.Where(x => x.RowKey != rowKey)],
                Constraints = [.. d.Constraints.Where(x => x.RowKey != rowKey)],
                Indexes = [.. d.Indexes.Where(x => x.RowKey != rowKey)],
            });
        }
    }

    /// <summary>모든 편집을 버리고 원본으로.</summary>
    public void RevertAll()
    {
        _partitionDraft = _partitionOriginal;
        if (Draft == Original)
        {
            Recompute();
        }
        else
        {
            Update(_ => Original);
        }
    }

    /// <summary>
    /// 테이블 이름을 바꾼다(새 테이블 이름 칸). 규칙 이름(<see cref="ConstraintDraft.IsAutoName"/>)인 제약·인덱스는 새 이름으로 다시 짓는다(설계 7.5).
    /// </summary>
    public void SetTableName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        Update(d =>
        {
            var next = d with { Name = name };
            if (name.Length == 0)
            {
                return next;
            }

            // 손으로 지은 이름을 먼저 잡아 두어 새로 짓는 규칙 이름이 그것과 겹치지 않게 한다.
            var taken = d.Constraints.Where(c => !c.IsAutoName).Select(c => c.Name)
                .Concat(d.Indexes.Where(i => !i.IsAutoName).Select(i => i.Name)).ToList();
            var constraints = new List<ConstraintDraft>();
            foreach (var c in d.Constraints)
            {
                var renamed = c.IsAutoName ? c with { Name = Next(KindOf(c.Kind), name, taken) } : c;
                if (c.IsAutoName)
                {
                    taken.Add(renamed.Name);
                }

                constraints.Add(renamed);
            }

            var indexes = new List<IndexDraft>();
            foreach (var i in d.Indexes)
            {
                var renamed = i.IsAutoName ? i with { Name = Next(ConstraintNameKind.Index, name, taken) } : i;
                if (i.IsAutoName)
                {
                    taken.Add(renamed.Name);
                }

                indexes.Add(renamed);
            }

            return next with { Constraints = constraints, Indexes = indexes };
        });
    }

    /// <summary>지운 제약을 되살려도 되나: PK 는 지우지 않은 다른 PK 가 없을 때만(PK 는 하나, 설계 7.3).</summary>
    private bool CanRestore(ConstraintDraft constraint) =>
        constraint.Kind != ConstraintKind.PrimaryKey
        || !Draft.Constraints.Any(c => c.RowKey != constraint.RowKey && c.Kind == ConstraintKind.PrimaryKey && !c.IsDropped);

    private TableEditRowViewModel? RowOf(string rowKey) =>
        (TableEditRowViewModel?)Columns.FirstOrDefault(r => r.Key == rowKey)
        ?? (TableEditRowViewModel?)Constraints.FirstOrDefault(r => r.Key == rowKey)
        ?? Indexes.FirstOrDefault(r => r.Key == rowKey);

    /// <summary>테이블 주석 칸(제한된 테이블은 열 주석·기본값만, 설계 3장).</summary>
    public bool CanEditTableComment => Dialect is not null; // 늘 참: Core 검사 계획이 특수 테이블의 테이블 주석을 막지 않는다

    /// <summary>
    /// 화면이 아직 모델에 넣지 않은 칸 입력(칸을 벗어날 때 들어감)을 지금 넣는다. 닫기·끄기·새로 고침·실행·저장 앞에서 부른다 — 쓴 글을 말없이 잃지 않게(설계 4장).
    /// 객체 상세 화면이 채운다.
    /// </summary>
    public Action? FlushPendingInput { get; set; }

    /// <summary>원본에 없는 줄(새 줄)인가.</summary>
    public bool IsNewRow(string rowKey) =>
        Original.Column(rowKey) is null && Original.Constraint(rowKey) is null && Original.Index(rowKey) is null;

    private string NextName(ConstraintNameKind kind) =>
        Next(kind, Draft.Name.Length > 0 ? Draft.Name : "NEW_TABLE", Draft.Constraints.Select(c => c.Name).Concat(Draft.Indexes.Select(i => i.Name)));

    private string Next(ConstraintNameKind kind, string table, IEnumerable<string> taken) =>
        ConstraintNaming.Next(kind, table, taken, Context.MaxIdentifierBytes);

    private static ConstraintNameKind KindOf(ConstraintKind kind) => kind switch
    {
        ConstraintKind.PrimaryKey => ConstraintNameKind.PrimaryKey,
        ConstraintKind.Unique => ConstraintNameKind.Unique,
        ConstraintKind.ForeignKey => ConstraintNameKind.ForeignKey,
        _ => ConstraintNameKind.Check,
    };

    private static int IndexOf(IReadOnlyList<ColumnDraft> list, string rowKey)
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i].RowKey == rowKey)
            {
                return i;
            }
        }

        return -1;
    }

    internal static string KindName(TableKind kind) => kind switch
    {
        TableKind.Temporary => TableEditText.T("임시", "temporary"),
        TableKind.External => TableEditText.T("외부", "external"),
        TableKind.Iot => TableEditText.T("인덱스 구성(IOT)", "index-organized"),
        TableKind.Cluster => TableEditText.T("클러스터", "cluster"),
        _ => TableEditText.T("일반", "heap"),
    };

    private void Recompute()
    {
        ScriptError = null;
        try
        {
            Changes = _engine.Diff(Original, Draft, Dialect);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Changes = new TableDraftChanges();
            ScriptError = ex.Message;
        }

        try
        {
            // 빌더가 아직 못 만드는 꼴(계약만 있는 몸 포함)·틀린 입력이면 실행·저장을 막고 이유를 미리보기에 보인다. 줄 상태(차이)는 그대로 둔다.
            Script = ScriptError is null && !Changes.IsEmpty ? _engine.Build(Original, Draft, Dialect, Context.Server) : TableEditScript.Empty;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Script = TableEditScript.Empty;
            ScriptError = ex.Message;
        }

        // 파티션 문장(설계 18.4)을 테이블 이름 바꾸기 앞에 끼운다. 미리보기·실행·저장은 이 합친 목록 하나를 쓴다.
        var (partitions, partitionError) = BuildPartitionStatements();
        if (partitionError is not null)
        {
            Script = TableEditScript.Empty;
            ScriptError ??= partitionError;
        }
        else if (ScriptError is null)
        {
            Script = MergePartitionStatements(Script, partitions);
        }

        SyncRows();
        SyncPartitionRows();
        OnPropertyChanged(nameof(Changes));
        OnPropertyChanged(nameof(Script));
        OnPropertyChanged(nameof(ScriptError));
        OnPropertyChanged(nameof(PreviewText));
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(TableComment));
        OnPropertyChanged(nameof(IsTableCommentChanged));
        OnPropertyChanged(nameof(CanAddPrimaryKey));
        OnPropertyChanged(nameof(IsRestricted));
        OnPropertyChanged(nameof(RestrictionText));
        OnPropertyChanged(nameof(CanEditTableComment));
        OnPropertyChanged(nameof(TableName));
        OnPropertyChanged(nameof(IsTableNameChanged));
        OnPropertyChanged(nameof(RenameRuleNames));
        OnPropertyChanged(nameof(CanRenameTable));
        OnPropertyChanged(nameof(AllowsColumnAddDrop));
        OnPropertyChanged(nameof(PartitionChanges));
        OnPropertyChanged(nameof(PartitionDraft));
        OnPropertyChanged(nameof(IsPartitionDirty));
        OnPropertyChanged(nameof(CanEditPartitions));
        OnPropertyChanged(nameof(CanOpenPartitioning));
        OnPropertyChanged(nameof(PartitionLockedReason));
        Recomputed?.Invoke(this, EventArgs.Empty);
    }

    private void SyncRows()
    {
        Sync(Columns, Draft.Columns, c => c.RowKey, c => new ColumnRowViewModel(this, c.RowKey), (vm, c) => vm.Sync(c, Changes.Columns.FirstOrDefault(d => d.RowKey == c.RowKey)));
        Sync(Constraints, Draft.Constraints, c => c.RowKey, c => new ConstraintRowViewModel(this, c.RowKey),
            (vm, c) => vm.Sync(c, Changes.Constraints.FirstOrDefault(d => d.RowKey == c.RowKey)));
        Sync(Indexes, Draft.Indexes, i => i.RowKey, i => new IndexRowViewModel(this, i.RowKey),
            (vm, i) => vm.Sync(i, Changes.Indexes.FirstOrDefault(d => d.RowKey == i.RowKey)));
    }

    private static void Sync<TVm, TRow>(ObservableCollection<TVm> rows, IReadOnlyList<TRow> source, Func<TRow, string> keyOf, Func<TRow, TVm> create, Action<TVm, TRow> sync)
        where TVm : TableEditRowViewModel
    {
        var keys = source.Select(keyOf).ToHashSet(StringComparer.Ordinal);
        for (var i = rows.Count - 1; i >= 0; i--)
        {
            if (!keys.Contains(rows[i].Key))
            {
                rows.RemoveAt(i);
            }
        }

        for (var i = 0; i < source.Count; i++)
        {
            var key = keyOf(source[i]);
            var at = -1;
            for (var j = i; j < rows.Count; j++)
            {
                if (rows[j].Key == key)
                {
                    at = j;
                    break;
                }
            }

            TVm vm;
            if (at < 0)
            {
                vm = create(source[i]);
                rows.Insert(i, vm);
            }
            else
            {
                vm = rows[at];
                if (at != i)
                {
                    rows.Move(at, i);
                }
            }

            sync(vm, source[i]);
        }
    }
}
