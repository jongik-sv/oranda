using System.Globalization;
using System.Text.RegularExpressions;
using Oranda.Core.Connection;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;

namespace Oranda.Core.Schema.TableEdit;

/// <summary>서버에 물어야 하는 검사 하나의 종류(<see cref="ITableEditChecks"/> 멤버와 짝). 값을 더할 때는 끝에 덧붙인다.</summary>
public enum TableEditCheckKind
{
    /// <summary>줄이는 길이에 넘치는 값(<see cref="ITableEditChecks.OverflowCountAsync"/>).</summary>
    Overflow,

    /// <summary>NULL 아닌 값이 있나 — 데이터가 있으면 안 되는 형식 변경(<see cref="ITableEditChecks.NonNullCountAsync"/>).</summary>
    NonNull,

    /// <summary>NULL 행(<see cref="ITableEditChecks.NullCountAsync"/>).</summary>
    Nulls,

    DuplicateKeys,
    Orphans,
    CheckViolations,

    /// <summary>가상 식 5줄 계산(<see cref="ITableEditChecks.PreviewExpressionAsync"/>).</summary>
    Expression,

    NameExists,

    /// <summary>테이블을 쓰는 객체(DROP COLUMN ⚠ 에 붙임).</summary>
    Dependents,

    /// <summary>행이 있나(기본값 없는 NOT NULL 새 열, <see cref="ITableEditChecks.CountRowsAsync"/>).</summary>
    Rows,

    /// <summary>새 테이블 식 문법(<see cref="ITableEditChecks.ValidateExpressionAsync"/>).</summary>
    ExpressionSyntax,

    /// <summary>운영 큰 테이블(<see cref="ITableEditChecks.NumRowsAsync"/> ≥ 기준).</summary>
    LargeTable,

    /// <summary>MOVE 로 다시 쓸 행 수(<see cref="ITableEditChecks.NumRowsAsync"/>, 늘 ⚠, 설계 18.2).</summary>
    TableRewrite,
}

/// <summary>
/// 서버에 물을 검사 하나. 쓰는 칸은 종류마다 다르다: Overflow(<see cref="Column"/>·<see cref="Target"/>), NonNull·Nulls(<see cref="Column"/>),
/// DuplicateKeys(<see cref="Columns"/>), Orphans(<see cref="Columns"/>·<see cref="ReferencedTable"/>·<see cref="ReferencedColumns"/>),
/// CheckViolations(<see cref="Condition"/>), Expression(<see cref="Expression"/>·<see cref="Columns"/>), ExpressionSyntax(<see cref="Expression"/>·<see cref="ColumnDrafts"/>),
/// NameExists(<see cref="Name"/>·<see cref="NameKind"/>), Dependents·Rows·LargeTable(없음). 이름은 지금 서버에 있는 이름(이름 바꾸기 전, 사전 표기)이다.
/// </summary>
/// <param name="Code">걸렸을 때의 판정 종류</param>
/// <param name="RowKey">편집 줄</param>
/// <param name="Subject">대상 이름(문구용)</param>
public sealed record TableEditCheck(TableEditCheckKind Kind, CheckCode Code, string RowKey, string? Subject)
{
    public string? Column { get; init; }

    public IReadOnlyList<string> Columns { get; init; } = [];

    public ColumnDraft? Target { get; init; }

    public DbObjectRef? ReferencedTable { get; init; }

    public IReadOnlyList<string> ReferencedColumns { get; init; } = [];

    public string? Condition { get; init; }

    public string? Expression { get; init; }

    public string? Name { get; init; }

    public TableEditNameKind NameKind { get; init; }

    /// <summary>새 테이블 식 문법 확인에 쓰는 열 정의.</summary>
    public IReadOnlyList<ColumnDraft> ColumnDrafts { get; init; } = [];
}

/// <summary>검사 계획: 서버 없이 정해지는 판정(<see cref="Findings"/>: ⚠ 서버 이름·되돌릴 수 없음 등)과 서버에 물을 검사(<see cref="Checks"/>).</summary>
public sealed record TableEditCheckPlan(IReadOnlyList<CheckFinding> Findings, IReadOnlyList<TableEditCheck> Checks)
{
    public static TableEditCheckPlan Empty { get; } = new([], []);
}

/// <summary>검사 조건(설계 10장).</summary>
public sealed record TableEditCheckOptions
{
    /// <summary>접속 환경. 운영이면 큰 테이블 ⚠(LongLock)을 본다.</summary>
    public ConnectionEnvironment Environment { get; init; }

    /// <summary>큰 테이블 기준 행 수(기본 100만, 설계 10장).</summary>
    public long LargeTableRows { get; init; } = 1_000_000;

    /// <summary>검사 질의 하나의 시간 제한(기본 10초). 넘으면 ⚠ 「확인 못 함」.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>가상 식 미리보기 줄 수(설계 7.6).</summary>
    public int PreviewRows { get; init; } = 5;

    /// <summary>접속한 서버(방언 기능 판정). 모르면 null.</summary>
    public ServerInfo? Server { get; init; }
}

/// <summary>
/// 편집 → 검사 계획(설계 10장 표의 ✖/⚠ 조건). 순수 판정이라 서버 없이 시험한다. 실제로 묻는 일은 <see cref="TableEditCheckRunner"/>.
/// 데이터 검사는 지금 서버에 있는 열만 쓴다(새 열·이름 바꾼 열이 낀 것은 실행 뒤에야 알 수 있어 건너뛴다).
/// 형식 변경 판정: 같은 계열(VARCHAR2·CHAR, NVARCHAR2·NCHAR, 같은 이름)에서 문자 길이를 줄이면 넘침 검사, 숫자 정밀도·소수를 줄이면
/// 데이터가 있으면 안 됨(Oracle ORA-01440·Tibero 7238 — 값이 맞아도 실패), 계열이 다르면 데이터가 있으면 안 됨.
/// </summary>
public static partial class TableEditCheckPlanner
{
    /// <summary>
    /// 기존 특수 테이블(GTT·IOT·외부·클러스터)에서 허용하지 않는 편집의 이유 키(설계 3장·18.10). GTT·IOT 는 주석·기본값·열 추가·지우기, 외부·클러스터는 주석·기본값만.
    /// </summary>
    public const string SpecialTableReasonKey = "tableEdit.unsupported.specialTable";

    [GeneratedRegex(@"\d+")]
    private static partial Regex Numbers();

    /// <summary>STORAGE 크기 글(<c>64K</c>, <c>1M</c>, 바이트 수).</summary>
    [GeneratedRegex(@"^\d+\s*[KMG]?$", RegexOptions.IgnoreCase)]
    private static partial Regex ExtentSize();

    public static TableEditCheckPlan Plan(TableDraft original, TableDraft edited, ITableEditDialect dialect, TableEditCheckOptions options)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(edited);
        ArgumentNullException.ThrowIfNull(dialect);
        ArgumentNullException.ThrowIfNull(options);

        var p = new Planner(original, edited, dialect, options);
        if (edited.IsNewTable)
        {
            p.PlanNew();
        }
        else
        {
            var changes = TableDraftDiff.Compute(original, edited, dialect);
            if (changes.IsEmpty)
            {
                return TableEditCheckPlan.Empty;
            }

            p.PlanAlter(changes);
        }

        return p.Findings.Count == 0 && p.Checks.Count == 0 ? TableEditCheckPlan.Empty : new TableEditCheckPlan(p.Findings, p.Checks);
    }

    private sealed class Planner(TableDraft original, TableDraft edited, ITableEditDialect dialect, TableEditCheckOptions options)
    {
        public List<CheckFinding> Findings { get; } = [];

        public List<TableEditCheck> Checks { get; } = [];

        private HashSet<string> _droppedNames = new(StringComparer.Ordinal);

        /// <summary>새 열의 신원·숨김·도메인 지원(설계 18.5·18.8).</summary>
        private void ColumnFeatures(ColumnDraft c)
        {
            if (c.Identity is not null)
            {
                Unsupported(TableEditFeature.IdentityColumn, c.RowKey, c.Name);
                if (dialect.NormalizeDefault(c.DefaultValue) is not null)
                {
                    // 신원 열에는 기본값을 못 준다(ORA-30665). 문장에서 소리 없이 빼지 않고 막는다.
                    Invalid(c.RowKey, c.Name, "identityDefault");
                }
            }

            if (c.IsInvisible)
            {
                Unsupported(TableEditFeature.InvisibleColumn, c.RowKey, c.Name);
            }

            if (!string.IsNullOrWhiteSpace(c.Domain))
            {
                Unsupported(TableEditFeature.DomainColumn, c.RowKey, c.Name);
            }
        }

        // ---------- 저장(설계 18.2) ----------

        private bool Supports(TableEditFeature feature) => ReasonKey(feature) is null;

        /// <summary>방언 판정, 없으면 Core 보수 판정(<see cref="TableEditDefaults.StorageReasonKey"/>, 저장 페이지 기능만).</summary>
        private string? ReasonKey(TableEditFeature feature) =>
            dialect.UnsupportedReasonKey(feature, options.Server) ?? TableEditDefaults.StorageReasonKey(feature, options.Server);

        /// <summary>저장 칸 값 범위(PCTFREE 0~99, INITRANS 1~255, 병렬 0 이상)와 고급 압축 지원.</summary>
        private void StorageValues(TableStorage s, StorageChange which)
        {
            if (which.HasFlag(StorageChange.PctFree) && s.PctFree is < 0 or > 99)
            {
                Invalid(TableDraft.TableRowKey, "PCTFREE", "pctFreeRange");
            }

            if (which.HasFlag(StorageChange.InitTrans) && s.InitTrans is < 1 or > 255)
            {
                Invalid(TableDraft.TableRowKey, "INITRANS", "initTransRange");
            }

            if (which.HasFlag(StorageChange.Parallel) && s.Parallel is < 0)
            {
                Invalid(TableDraft.TableRowKey, "PARALLEL", "parallelRange");
            }

            if (which.HasFlag(StorageChange.Compression))
            {
                if (s.Compression == TableCompression.Advanced)
                {
                    Unsupported(TableEditFeature.AdvancedRowCompression, TableDraft.TableRowKey, edited.Name);
                }
                else if (s.Compression == TableCompression.Basic)
                {
                    Unsupported(TableEditFeature.BasicTableCompression, TableDraft.TableRowKey, edited.Name);
                }
                else if (s.Compression == TableCompression.Other)
                {
                    Invalid(TableDraft.TableRowKey, edited.Name, "compressionOther");
                }
            }
        }

        /// <summary>
        /// LOB 저장 절: LOB 형식이 아닌 열 ✖, 지원, 방식 낱말·SECUREFILE 전용 옵션(압축·중복 제거 — 방식을 못 쓰는 방언은 막음, Tibero 실측 밖), BASICFILE 에 그 옵션 ✖.
        /// </summary>
        private void LobValues(ColumnDraft c, LobStorage? current)
        {
            var target = c.Lob!;
            if (!TableEditDefaults.IsLobType(c.DataType) && current is null)
            {
                Invalid(c.RowKey, c.Name, "lobOnNonLob");
                return;
            }

            Unsupported(TableEditFeature.LobStorage, c.RowKey, c.Name);
            var secureFileOnly = (target.Compression is { } tc && tc != current?.Compression) || (target.Deduplicate is { } td && td != current?.Deduplicate);
            if ((target.Kind is { } kind && kind != current?.Kind) || secureFileOnly)
            {
                Unsupported(TableEditFeature.SecureFileLob, c.RowKey, c.Name);
            }

            // 편집 모델은 지금 값을 그대로 지니므로 바꾼 값만 본다(BASICFILE 로 바꾸면 문장은 지금 압축·중복 제거를 이어 쓰지 않는다).
            var basicFile = (target.Kind ?? current?.Kind) == LobStorageKind.BasicFile;
            var compressionSet = target.Compression is { } and not LobCompression.None && target.Compression != current?.Compression;
            var deduplicateSet = target.Deduplicate == true && current?.Deduplicate != true;
            if (basicFile && (compressionSet || deduplicateSet))
            {
                Invalid(c.RowKey, c.Name, "basicFileLobOption");
            }
        }

        /// <summary>새 테이블 저장 절(설계 18.2): 지원·값 범위·STORAGE 크기 글. GTT 는 저장 절을 쓰지 않으므로 ⚠. IOT 압축은 키 압축이라 ✖.</summary>
        private void NewStorage(IReadOnlyList<ColumnDraft> columns)
        {
            var empty = new TableDraft(edited.Owner, edited.Name);
            var which = TableDraftDiff.StorageChanges(empty, edited) & ~StorageChange.Tablespace;
            var lobs = columns.Where(c => TableDraftDiff.LobChanged(null, c.Lob)).ToList();
            if (which == StorageChange.None && lobs.Count == 0)
            {
                return;
            }

            if (edited.Kind == TableKind.Temporary)
            {
                Findings.Add(new CheckFinding(CheckSeverity.Warning, CheckCode.SettingIgnored, TableDraft.TableRowKey) { Subject = edited.Name, Detail = "temporaryStorage" });
                return;
            }

            if (which != StorageChange.None)
            {
                Unsupported(TableEditFeature.StorageAttributes, TableDraft.TableRowKey, edited.Name);
                StorageValues(edited.Storage, which);
                if (which.HasFlag(StorageChange.Compression) && edited.Kind == TableKind.Iot)
                {
                    Invalid(TableDraft.TableRowKey, edited.Name, "iotCompression");
                }

                foreach (var size in (string?[])[edited.Storage.InitialExtent, edited.Storage.NextExtent])
                {
                    if (!string.IsNullOrWhiteSpace(size) && !ExtentSize().IsMatch(size.Trim()))
                    {
                        Invalid(TableDraft.TableRowKey, size.Trim(), "extentSize");
                    }
                }
            }

            foreach (var c in lobs)
            {
                LobValues(c, null);
            }
        }

        /// <summary>기존 테이블에 더하는 열의 LOB 저장 절(GTT 는 쓰지 않으므로 ⚠).</summary>
        private void AddedLob(ColumnDraft c)
        {
            if (!TableDraftDiff.LobChanged(null, c.Lob))
            {
                return;
            }

            if (original.Kind == TableKind.Temporary)
            {
                Findings.Add(new CheckFinding(CheckSeverity.Warning, CheckCode.SettingIgnored, c.RowKey) { Subject = c.Name, Detail = "temporaryStorage" });
                return;
            }

            LobValues(c, null);
        }

        /// <summary>기존 테이블 저장 변경(설계 18.2). MOVE 가 생기면 참: 행 수 ⚠, ONLINE 이 안 되면 잠금 ⚠, 파티션 테이블 MOVE ✖.</summary>
        private bool AlterStorage(TableDraftChanges changes)
        {
            var lobs = changes.Columns.Where(d => d.State == DraftRowState.Modified && d.Changes.HasFlag(ColumnChange.Lob) && !TableDraftDiff.IsRecreated(d, dialect)).ToList();
            var table = changes.Storage & ~StorageChange.Extents;
            if (changes.Storage.HasFlag(StorageChange.Extents))
            {
                // TE-39: 기존 테이블의 STORAGE(INITIAL/NEXT)는 문장을 만들지 않는다.
                Findings.Add(new CheckFinding(CheckSeverity.Warning, CheckCode.SettingIgnored, TableDraft.TableRowKey) { Subject = original.Name, Detail = "existingStorage" });
            }

            if (table != StorageChange.None)
            {
                Unsupported(TableEditFeature.StorageAttributes, TableDraft.TableRowKey, edited.Name);
                StorageValues(edited.Storage, table);
            }

            foreach (var d in lobs)
            {
                LobValues(d.Edited!, d.Original!.Lob);
            }

            // 문장 빌더와 같은 규칙: Tibero 는 압축을 MOVE 없이 ALTER 로 바꾼다(TableEditDefaults.MoveCarriesAttributes).
            var moving = TableEditDefaults.MoveCarriesAttributes(dialect.Dbms) ? StorageChange.Tablespace | StorageChange.Compression : StorageChange.Tablespace;
            var moves = (table & moving) != 0 || lobs.Count > 0;
            if (!moves)
            {
                return false;
            }

            if (edited.Columns.FirstOrDefault(c => !c.IsDropped && c.DataType?.Trim().ToUpperInvariant() is "LONG" or "LONG RAW") is { } longColumn)
            {
                // LONG·LONG RAW 열이 있는 테이블은 MOVE 가 안 된다(ORA-00997).
                Invalid(TableDraft.TableRowKey, longColumn.Name, "longColumnMove");
            }

            if (original.IsPartitioned)
            {
                // 파티션 테이블은 테이블 단위 MOVE 가 안 된다(ORA-14511) — 파티션 페이지(18.4)에서.
                Invalid(TableDraft.TableRowKey, original.Name, "partitionedMove");
            }

            Checks.Add(new TableEditCheck(TableEditCheckKind.TableRewrite, CheckCode.TableRewrite, TableDraft.TableRowKey, original.Name));
            if (!Supports(TableEditFeature.OnlineMove))
            {
                Findings.Add(new CheckFinding(CheckSeverity.Warning, CheckCode.MoveLocksDml, TableDraft.TableRowKey) { Subject = original.Name });
            }

            return true;
        }

        private void Invalid(string rowKey, string subject, string reason) =>
            Findings.Add(new CheckFinding(CheckSeverity.Block, CheckCode.InvalidDefinition, rowKey) { Subject = subject, Detail = reason });

        /// <summary>기존 특수 테이블에서 허용 밖의 편집을 ✖(설계 3장·18.10).</summary>
        private void SpecialTable(TableDraftChanges changes)
        {
            if (original.Kind == TableKind.Heap)
            {
                return;
            }

            var addDrop = original.Kind is TableKind.Temporary or TableKind.Iot;
            void Block(string rowKey, string subject) =>
                Findings.Add(new CheckFinding(CheckSeverity.Block, CheckCode.Unsupported, rowKey) { Subject = subject, Detail = SpecialTableReasonKey });

            foreach (var d in changes.Columns)
            {
                var allowed = d.State switch
                {
                    DraftRowState.Added or DraftRowState.Dropped => addDrop,
                    _ => (d.Changes & ~(ColumnChange.Comment | ColumnChange.Default)) == 0,
                };
                if (!allowed)
                {
                    Block(d.RowKey, (d.Edited ?? d.Original)!.Name);
                }
            }

            foreach (var d in changes.Constraints)
            {
                Block(d.RowKey, (d.Edited ?? d.Original)!.Name);
            }

            foreach (var d in changes.Indexes)
            {
                Block(d.RowKey, (d.Edited ?? d.Original)!.Name);
            }

            if (changes.Storage != StorageChange.None)
            {
                Block(TableDraft.TableRowKey, edited.Name);
            }

            // GTT·IOT 이름 바꾸기는 된다(실측). 외부·클러스터는 막는다.
            if (changes.Renamed && !addDrop)
            {
                Block(TableDraft.TableRowKey, edited.Name);
            }
        }

        public void PlanNew()
        {
            var columns = edited.Columns.Where(c => !c.IsDropped).ToList();
            var live = edited.Constraints.Where(c => !c.IsDropped).ToList();
            switch (edited.Kind)
            {
                case TableKind.Temporary:
                    Unsupported(TableEditFeature.TemporaryTable, TableDraft.TableRowKey, edited.Name);
                    foreach (var fk in live.Where(c => c.Kind == ConstraintKind.ForeignKey))
                    {
                        Invalid(fk.RowKey, fk.Name, "temporaryForeignKey");
                    }

                    if (!string.IsNullOrWhiteSpace(edited.Tablespace))
                    {
                        // GTT 는 임시 테이블스페이스를 쓰므로 문장에 넣지 않는다(설계 18.10) — 버린다고 알린다.
                        Findings.Add(new CheckFinding(CheckSeverity.Warning, CheckCode.SettingIgnored, TableDraft.TableRowKey)
                        {
                            Subject = edited.Tablespace.Trim(),
                            Detail = "temporaryTablespace",
                        });
                    }

                    break;
                case TableKind.Iot:
                    Unsupported(TableEditFeature.IndexOrganizedTable, TableDraft.TableRowKey, edited.Name);
                    if (!live.Any(c => c.Kind == ConstraintKind.PrimaryKey))
                    {
                        Invalid(TableDraft.TableRowKey, edited.Name, "iotWithoutPrimaryKey");
                    }

                    foreach (var bitmap in edited.Indexes.Where(i => !i.IsDropped && i.Kind == IndexKind.Bitmap))
                    {
                        // IOT 에 비트맵 인덱스는 매핑 테이블이 있어야 한다(ORA-28669) — 만들지 않는다.
                        Invalid(bitmap.RowKey, bitmap.Name, "iotBitmapIndex");
                    }

                    break;
                case TableKind.External or TableKind.Cluster:
                    Findings.Add(new CheckFinding(CheckSeverity.Block, CheckCode.Unsupported, TableDraft.TableRowKey) { Subject = edited.Name, Detail = SpecialTableReasonKey });
                    break;
            }

            foreach (var c in columns)
            {
                ColumnFeatures(c);
            }

            NewStorage(columns);

            if (!string.IsNullOrWhiteSpace(edited.Name))
            {
                Checks.Add(Name(TableDraft.TableRowKey, edited.Name, TableEditNameKind.Table));
            }

            DuplicateColumnNames(columns, _ => true);
            foreach (var c in columns.Where(c => c.IsVirtual))
            {
                Unsupported(TableEditFeature.VirtualColumn, c.RowKey, c.Name);
                Checks.Add(new TableEditCheck(TableEditCheckKind.ExpressionSyntax, CheckCode.ExpressionFails, c.RowKey, c.Name)
                {
                    Expression = c.VirtualExpression, ColumnDrafts = columns,
                });
            }

            var constraints = edited.Constraints.Where(c => !c.IsDropped).ToList();
            foreach (var c in constraints)
            {
                ConstraintFeatures(c, isNew: true);
                if (!string.IsNullOrWhiteSpace(c.Name))
                {
                    NamedConstraint(c, constraints, edited.Indexes.Where(i => !i.IsDropped), [], []);
                }
            }

            var indexes = edited.Indexes.Where(i => !i.IsDropped && i.ConstraintName is null).ToList();
            foreach (var i in indexes)
            {
                IndexFeatures(i);
                NamedIndex(i, indexes, constraints, []);
            }
        }

        public void PlanAlter(TableDraftChanges changes)
        {
            var addedOrRenamed = changes.Columns.Where(d => d.State == DraftRowState.Added || d.Changes.HasFlag(ColumnChange.Name))
                .Select(d => d.RowKey).ToHashSet(StringComparer.Ordinal);
            // 테이블 이름 바꾸기의 의존 객체 검사가 열 지우기 것도 겸한다(같은 질의).
            var dependentsPlanned = changes.Renamed;
            var liveNames = edited.Columns.Where(c => !c.IsDropped && changes.StateOf(c.RowKey) != DraftRowState.Dropped).Select(c => c.Name)
                .ToHashSet(StringComparer.Ordinal);
            _droppedNames = changes.Columns.Where(d => d.State == DraftRowState.Dropped).Select(d => d.Original!.Name)
                .Where(n => !liveNames.Contains(n)).ToHashSet(StringComparer.Ordinal);

            SpecialTable(changes);
            if (changes.Renamed)
            {
                // 설계 18.1: 새 이름 겹침 ✖, 이 테이블을 쓰는 객체가 무효가 됨 ⚠.
                Unsupported(TableEditFeature.RenameTable, TableDraft.TableRowKey, edited.Name);
                Checks.Add(Name(TableDraft.TableRowKey, edited.Name, TableEditNameKind.Table));
                Checks.Add(new TableEditCheck(TableEditCheckKind.Dependents, CheckCode.DependentsInvalidated, TableDraft.TableRowKey, original.Name));
            }

            // 열
            var liveColumns = edited.Columns.Where(c => !c.IsDropped && changes.StateOf(c.RowKey) != DraftRowState.Dropped).ToList();
            DuplicateColumnNames(liveColumns, c => addedOrRenamed.Contains(c.RowKey));
            foreach (var d in changes.Columns)
            {
                switch (d.State)
                {
                    case DraftRowState.Added:
                        ColumnFeatures(d.Edited!);
                        AddedLob(d.Edited!);
                        if (d.Edited!.IsVirtual)
                        {
                            Unsupported(TableEditFeature.VirtualColumn, d.RowKey, d.Edited.Name);
                            Expression(d.RowKey, d.Edited, liveColumns, addedOrRenamed);
                        }
                        else if (!d.Edited.IsNullable && dialect.NormalizeDefault(d.Edited.DefaultValue) is null)
                        {
                            Checks.Add(new TableEditCheck(TableEditCheckKind.Rows, CheckCode.NullRows, d.RowKey, d.Edited.Name));
                        }

                        break;
                    case DraftRowState.Modified:
                        ModifiedColumn(d, liveColumns, addedOrRenamed);
                        break;
                    case DraftRowState.Dropped:
                        Irreversible(d.RowKey, d.Original!.Name);
                        if (!dependentsPlanned)
                        {
                            Checks.Add(new TableEditCheck(TableEditCheckKind.Dependents, CheckCode.Irreversible, TableDraft.TableRowKey, original.Name));
                            dependentsPlanned = true;
                        }

                        break;
                }
            }

            // Tibero 서버 결함: 가상 열이 있는 테이블에서 열을 지우면(가상 식 바꾸기의 DROP + ADD 포함) 지운 값이 나중에 더한 열에 보인다.
            if (dialect.Dbms == Dbms.Tibero
                && (original.Columns.Any(c => c.IsVirtual) || liveColumns.Any(c => c.IsVirtual))
                && (changes.Columns.Any(d => d.State == DraftRowState.Dropped) || Findings.Any(f => f.Code == CheckCode.VirtualRecreated)))
            {
                Findings.Add(new CheckFinding(CheckSeverity.Warning, CheckCode.TiberoDroppedColumnData, TableDraft.TableRowKey) { Subject = original.Name });
            }

            // 제약
            var liveConstraints = edited.Constraints.Where(c => !c.IsDropped && changes.StateOf(c.RowKey) != DraftRowState.Dropped).ToList();
            var freedConstraints = changes.Constraints.Where(d => d.State == DraftRowState.Dropped || d.Changes.HasFlag(ConstraintChange.Name))
                .Select(d => d.Original!.Name).ToHashSet(StringComparer.Ordinal);
            var freedIndexes = changes.Indexes.Where(d => d.State == DraftRowState.Dropped || d.Changes.HasFlag(IndexChange.Name))
                .Select(d => d.Original!.Name)
                .Concat(changes.Constraints.Where(d => d.State == DraftRowState.Dropped || d.Changes.HasFlag(ConstraintChange.Definition))
                    .Where(d => d.Original!.Kind is ConstraintKind.PrimaryKey or ConstraintKind.Unique).Select(d => d.Original!.Name))
                .ToHashSet(StringComparer.Ordinal);
            var liveIndexes = edited.Indexes.Where(i => !i.IsDropped && changes.StateOf(i.RowKey) != DraftRowState.Dropped).ToList();
            foreach (var d in changes.Constraints)
            {
                if (d.State == DraftRowState.Dropped)
                {
                    Irreversible(d.RowKey, d.Original!.Name);
                    ServerName(d.RowKey, d.Original.Name);
                    continue;
                }

                var e = d.Edited!;
                var added = d.State == DraftRowState.Added || d.Changes.HasFlag(ConstraintChange.Definition);
                if (added)
                {
                    ConstraintFeatures(e, isNew: d.State == DraftRowState.Added);
                }
                else if (d.Changes.HasFlag(ConstraintChange.Enabled))
                {
                    Unsupported(TableEditFeature.ConstraintEnableDisable, d.RowKey, e.Name);
                }

                if (d.State == DraftRowState.Modified)
                {
                    ServerName(d.RowKey, d.Original!.Name);
                }

                if (e.IsEnabled && (added || (d.Changes.HasFlag(ConstraintChange.Enabled) && !d.Original!.IsEnabled)))
                {
                    ConstraintData(d.RowKey, e, addedOrRenamed);
                }

                if (d.State == DraftRowState.Added || d.Changes.HasFlag(ConstraintChange.Name))
                {
                    NamedConstraint(e, liveConstraints, liveIndexes, freedConstraints, freedIndexes);
                }
            }

            // 인덱스
            foreach (var d in changes.Indexes)
            {
                if (d.State == DraftRowState.Dropped)
                {
                    Irreversible(d.RowKey, d.Original!.Name);
                    continue;
                }

                if (d.State == DraftRowState.Added || d.Changes.HasFlag(IndexChange.Definition))
                {
                    IndexFeatures(d.Edited!);
                }

                if (d.Changes.HasFlag(IndexChange.Name))
                {
                    Unsupported(TableEditFeature.RenameIndex, d.RowKey, d.Edited!.Name);
                }

                if (d.State == DraftRowState.Added || d.Changes.HasFlag(IndexChange.Name))
                {
                    NamedIndex(d.Edited!, liveIndexes, liveConstraints, freedIndexes);
                }
            }

            // 규칙 이름 따라 바꾸기(설계 18.1, TE-38): 새 이름이 편집 안에서 겹치면 ✖, 아니면 서버에 묻는다.
            var ruleRenames = TableEditRuleNames.Plan(original, edited, changes, dialect.MaxIdentifierBytes(options.Server));
            foreach (var r in ruleRenames)
            {
                var isConstraint = r.Page == TableEditPage.Constraints;
                var clash = (isConstraint
                                ? liveConstraints.Any(c => c.RowKey != r.RowKey && string.Equals(c.Name, r.NewName, StringComparison.Ordinal))
                                : liveIndexes.Any(i => i.RowKey != r.RowKey && string.Equals(i.Name, r.NewName, StringComparison.Ordinal)))
                            || ruleRenames.Any(x => x != r && x.Page == r.Page && string.Equals(x.NewName, r.NewName, StringComparison.Ordinal));
                if (clash)
                {
                    Findings.Add(new CheckFinding(CheckSeverity.Block, CheckCode.NameExists, r.RowKey) { Subject = r.NewName });
                }
                else if (!(isConstraint ? freedConstraints : freedIndexes).Contains(r.NewName))
                {
                    Checks.Add(Name(r.RowKey, r.NewName, isConstraint ? TableEditNameKind.Constraint : TableEditNameKind.Index));
                }
            }

            var moves = AlterStorage(changes);

            // 운영 큰 테이블(설계 10장): MODIFY·NOT NULL·제약 추가·인덱스 만들기·MOVE
            var locks = moves || changes.Columns.Any(d => d.State == DraftRowState.Modified
                                                 && (d.Changes & (ColumnChange.Type | ColumnChange.Nullable | ColumnChange.Default | ColumnChange.VirtualExpression)) != 0)
                        || changes.Columns.Any(d => d.State == DraftRowState.Added && !d.Edited!.IsNullable)
                        || changes.Constraints.Any(d => d.State == DraftRowState.Added || d.Changes.HasFlag(ConstraintChange.Definition)
                                                        || (d.Changes.HasFlag(ConstraintChange.Enabled) && d.Edited!.IsEnabled))
                        || changes.Indexes.Any(d => d.State == DraftRowState.Added || d.Changes.HasFlag(IndexChange.Definition));
            if (locks && options.Environment == ConnectionEnvironment.Production)
            {
                Checks.Add(new TableEditCheck(TableEditCheckKind.LargeTable, CheckCode.LongLock, TableDraft.TableRowKey, original.Name));
            }
        }

        private void ModifiedColumn(ColumnDiff d, List<ColumnDraft> liveColumns, HashSet<string> addedOrRenamed)
        {
            var (o, e) = (d.Original!, d.Edited!);
            if (d.Changes.HasFlag(ColumnChange.Name))
            {
                Unsupported(TableEditFeature.RenameColumn, d.RowKey, e.Name);
            }

            if (d.Changes.HasFlag(ColumnChange.Identity))
            {
                Unsupported(TableEditFeature.IdentityColumn, d.RowKey, e.Name);
                if (!o.IsIdentityColumn && e.IsIdentityColumn)
                {
                    // 일반 열 → 신원 열은 빈 테이블에서도 ORA-30673(설계 12.1).
                    Invalid(d.RowKey, o.Name, "identityFromPlain");
                }
                else if (o.IsIdentityUnknown && e.Identity is not null)
                {
                    // 원본 속성을 몰라 바뀐 옵션만 쓸 수 없다(START WITH 를 다시 쓰면 키가 겹친다). 신원 없애기만 된다.
                    Invalid(d.RowKey, o.Name, "identityUnknown");
                }
            }

            if (o.IsIdentityColumn && e.IsIdentityColumn && (d.Changes & (ColumnChange.Nullable | ColumnChange.Default)) != 0)
            {
                // 신원 열은 늘 NOT NULL 이고 기본값을 못 준다(ORA-30665·ORA-30673). 신원을 없애면서 바꾸는 것은 된다(DROP IDENTITY 가 먼저).
                Invalid(d.RowKey, o.Name, "identityNullDefault");
            }

            if (d.Changes.HasFlag(ColumnChange.Invisible))
            {
                Unsupported(TableEditFeature.InvisibleColumn, d.RowKey, e.Name);
                Findings.Add(new CheckFinding(CheckSeverity.Warning, CheckCode.ColumnOrderChanges, d.RowKey) { Subject = o.Name });
            }

            if (d.Changes.HasFlag(ColumnChange.Domain))
            {
                Unsupported(TableEditFeature.DomainColumn, d.RowKey, e.Name);
            }

            if (d.Changes.HasFlag(ColumnChange.VirtualExpression))
            {
                if (e.IsVirtual)
                {
                    if (!o.IsVirtual)
                    {
                        Unsupported(TableEditFeature.VirtualColumn, d.RowKey, e.Name);
                    }

                    Expression(d.RowKey, e, liveColumns, addedOrRenamed);
                }

                if (o.IsVirtual != e.IsVirtual || !dialect.SupportsVirtualColumnModify)
                {
                    Findings.Add(new CheckFinding(CheckSeverity.Warning, CheckCode.VirtualRecreated, d.RowKey) { Subject = o.Name });
                    if (!o.IsVirtual)
                    {
                        Irreversible(d.RowKey, o.Name);
                    }
                }
            }

            if (o.IsVirtual && !e.IsVirtual && !e.IsNullable && dialect.NormalizeDefault(e.DefaultValue) is null)
            {
                // 가상 → 일반은 DROP + ADD: 기본값 없는 NOT NULL 새 열과 같다.
                Checks.Add(new TableEditCheck(TableEditCheckKind.Rows, CheckCode.NullRows, d.RowKey, e.Name));
            }

            if (d.Changes.HasFlag(ColumnChange.Type) && !o.IsVirtual && !e.IsVirtual)
            {
                var target = e with { CharSemantics = e.CharSemantics ?? o.CharSemantics };
                switch (TypeChange(o, target))
                {
                    case TypeChangeKind.Shrink:
                        Checks.Add(new TableEditCheck(TableEditCheckKind.Overflow, CheckCode.Overflow, d.RowKey, o.Name) { Column = o.Name, Target = target });
                        break;
                    case TypeChangeKind.NeedsEmpty:
                        Checks.Add(new TableEditCheck(TableEditCheckKind.NonNull, CheckCode.IncompatibleTypeChange, d.RowKey, o.Name) { Column = o.Name, Target = target });
                        break;
                }
            }

            if (d.Changes.HasFlag(ColumnChange.Nullable) && !e.IsNullable)
            {
                Checks.Add(new TableEditCheck(TableEditCheckKind.Nulls, CheckCode.NullRows, d.RowKey, o.Name) { Column = o.Name });
            }
        }

        private enum TypeChangeKind
        {
            Safe,
            Shrink,
            NeedsEmpty,
        }

        private static TypeChangeKind TypeChange(ColumnDraft o, ColumnDraft e)
        {
            var (from, to) = (ColumnTypeText.Family(o.DataType), ColumnTypeText.Family(e.DataType));
            if (!SameFamily(from, to))
            {
                // DATE → TIMESTAMP(·LTZ) 는 데이터가 있어도 된다(Oracle). 나머지 계열 바꾸기는 빈 열만.
                return from == "DATE" && to is "TIMESTAMP" or "TIMESTAMP LTZ" ? TypeChangeKind.Safe : TypeChangeKind.NeedsEmpty;
            }

            if (to.StartsWith("TIMESTAMP", StringComparison.Ordinal) || to.StartsWith("INTERVAL", StringComparison.Ordinal))
            {
                // 소수 초·선행 정밀도를 줄이면 데이터가 있으면 안 된다(ORA-30082).
                var before = Numbers().Matches(ColumnTypeText.Canonical(o)).Select(m => int.Parse(m.Value, CultureInfo.InvariantCulture)).ToList();
                var after = Numbers().Matches(ColumnTypeText.Canonical(e)).Select(m => int.Parse(m.Value, CultureInfo.InvariantCulture)).ToList();
                return before.Count != after.Count || before.Zip(after).Any(p => p.Second < p.First) ? TypeChangeKind.NeedsEmpty : TypeChangeKind.Safe;
            }

            switch (to)
            {
                case "VARCHAR2" or "CHAR" or "NVARCHAR2" or "NCHAR" or "RAW":
                    {
                        var oldLength = o.Length ?? (from is "CHAR" or "NCHAR" ? 1 : int.MaxValue);
                        var newLength = e.Length ?? (to is "CHAR" or "NCHAR" ? 1 : int.MaxValue);
                        if (from != to && (newLength != oldLength || o.CharSemantics != e.CharSemantics))
                        {
                            // CHAR ↔ VARCHAR2 는 크기를 그대로 둘 때만 데이터가 있어도 된다.
                            return TypeChangeKind.NeedsEmpty;
                        }

                        var narrower = newLength < oldLength
                                       || (o.CharSemantics == CharSemantics.Characters && e.CharSemantics == CharSemantics.Bytes);
                        return narrower ? TypeChangeKind.Shrink : TypeChangeKind.Safe;
                    }

                case "NUMBER":
                    {
                        if (o.Precision is null && o.Scale is null)
                        {
                            return e.Precision is null && e.Scale is null ? TypeChangeKind.Safe : TypeChangeKind.NeedsEmpty;
                        }

                        var smaller = (e.Precision ?? 38) < (o.Precision ?? 38) || (e.Scale ?? 0) < (o.Scale ?? 0);
                        return smaller ? TypeChangeKind.NeedsEmpty : TypeChangeKind.Safe;
                    }

                case "FLOAT":
                    return (e.Precision ?? 126) < (o.Precision ?? 126) ? TypeChangeKind.NeedsEmpty : TypeChangeKind.Safe;
                default:
                    return TypeChangeKind.Safe;
            }
        }

        private static bool SameFamily(string a, string b) =>
            a == b || (a is "VARCHAR2" or "CHAR" && b is "VARCHAR2" or "CHAR") || (a is "NVARCHAR2" or "NCHAR" && b is "NVARCHAR2" or "NCHAR");

        /// <summary>같은 편집에서 지우는 열을 식이 쓰면 ✖(가상 열·CHECK 를 만든 뒤 열 지우기가 ORA-54031·ORA-12991 로 실패해 반쪽이 된다).</summary>
        private bool UsesDroppedColumn(string rowKey, string subject, string? expression)
        {
            var dropped = TableEditExpression.Names(expression).FirstOrDefault(_droppedNames.Contains);
            if (dropped is null)
            {
                return false;
            }

            Findings.Add(new CheckFinding(CheckSeverity.Block, CheckCode.InvalidDefinition, rowKey) { Subject = subject, Detail = "droppedColumn:" + dropped });
            return true;
        }

        /// <summary>가상 식 계산(지금 있는 열만 쓰면 5줄 미리보기, 새 열·이름 바꾼 열을 쓰면 「실행 뒤에 확인」).</summary>
        private void Expression(string rowKey, ColumnDraft column, List<ColumnDraft> liveColumns, HashSet<string> addedOrRenamed)
        {
            if (UsesDroppedColumn(rowKey, column.Name, column.VirtualExpression))
            {
                return;
            }

            var used = TableEditExpression.Names(column.VirtualExpression)
                .Select(n => liveColumns.FirstOrDefault(c => c.RowKey != rowKey && string.Equals(c.Name, n, StringComparison.Ordinal)))
                .OfType<ColumnDraft>()
                .ToList();
            if (used.Any(c => addedOrRenamed.Contains(c.RowKey)))
            {
                Findings.Add(new CheckFinding(CheckSeverity.Warning, CheckCode.NotChecked, rowKey) { Subject = column.Name, Detail = "afterRun" });
                return;
            }

            Checks.Add(new TableEditCheck(TableEditCheckKind.Expression, CheckCode.ExpressionFails, rowKey, column.Name)
            {
                Expression = column.VirtualExpression, Columns = [.. used.Select(c => c.Name)],
            });
        }

        /// <summary>제약을 만들거나 켤 때의 데이터 검사(지금 있는 열만).</summary>
        private void ConstraintData(string rowKey, ConstraintDraft c, HashSet<string> addedOrRenamed)
        {
            if (c.Kind == ConstraintKind.Check)
            {
                if (UsesDroppedColumn(rowKey, c.Name, c.Condition))
                {
                    return;
                }

                // CHECK 은 조건 글에 새 열·바꾼 이름이 나오면 지금 서버에서 셀 수 없다.
                var pending = edited.Columns.Where(col => addedOrRenamed.Contains(col.RowKey)).Select(col => col.Name).ToHashSet(StringComparer.Ordinal);
                if (TableEditExpression.Names(c.Condition).Any(pending.Contains))
                {
                    Findings.Add(new CheckFinding(CheckSeverity.Warning, CheckCode.NotChecked, rowKey) { Subject = c.Name, Detail = "afterRun" });
                    return;
                }
            }
            else if (c.ColumnKeys.Any(k => original.Column(k) is null || addedOrRenamed.Contains(k)))
            {
                // 새 열(아직 값 없음)·이름 바꾼 열이 낀 키는 실행 뒤에야 안다.
                return;
            }

            var columns = c.ColumnKeys.Select(k => original.Column(k)).OfType<ColumnDraft>().ToList();
            var names = columns.Select(col => col.Name).ToList();
            switch (c.Kind)
            {
                case ConstraintKind.PrimaryKey or ConstraintKind.Unique:
                    Checks.Add(new TableEditCheck(TableEditCheckKind.DuplicateKeys, CheckCode.DuplicateKeys, rowKey, c.Name) { Columns = names });
                    if (c.Kind == ConstraintKind.PrimaryKey)
                    {
                        foreach (var col in columns.Where(col => col.IsNullable))
                        {
                            Checks.Add(new TableEditCheck(TableEditCheckKind.Nulls, CheckCode.NullRows, rowKey, c.Name) { Column = col.Name });
                        }
                    }

                    break;
                case ConstraintKind.ForeignKey when c.ReferencedTable is not null && c.ReferencedColumns.Count == names.Count:
                    Checks.Add(new TableEditCheck(TableEditCheckKind.Orphans, CheckCode.OrphanRows, rowKey, c.Name)
                    {
                        Columns = names, ReferencedTable = c.ReferencedTable, ReferencedColumns = c.ReferencedColumns,
                    });
                    break;
                case ConstraintKind.Check when !string.IsNullOrWhiteSpace(c.Condition):
                    Checks.Add(new TableEditCheck(TableEditCheckKind.CheckViolations, CheckCode.CheckViolations, rowKey, c.Name) { Condition = c.Condition });
                    break;
            }
        }

        private void ConstraintFeatures(ConstraintDraft c, bool isNew)
        {
            if (c.Kind == ConstraintKind.ForeignKey && c.DeleteRule == DeleteRule.SetNull)
            {
                Unsupported(TableEditFeature.ForeignKeyOnDeleteSetNull, c.RowKey, c.Name);
            }

            if (isNew && !c.IsEnabled)
            {
                Unsupported(TableEditFeature.ConstraintEnableDisable, c.RowKey, c.Name);
            }
        }

        private void IndexFeatures(IndexDraft i)
        {
            if (i.Columns.Any(c => c.Descending))
            {
                Unsupported(TableEditFeature.DescendingIndex, i.RowKey, i.Name);
            }

            if (i.Kind == IndexKind.FunctionBased || i.Columns.Any(c => c.Expression is not null))
            {
                Unsupported(TableEditFeature.FunctionBasedIndex, i.RowKey, i.Name);
            }

            if (i.Kind == IndexKind.Bitmap)
            {
                Unsupported(TableEditFeature.BitmapIndex, i.RowKey, i.Name);
                if (i.IsUnique)
                {
                    Invalid(i.RowKey, i.Name, "uniqueBitmap");
                }
            }
        }

        /// <summary>새·바꾼 제약 이름: 편집 안에서 겹치면 ✖, 같은 편집에서 비운 이름이면 넘어가고, 아니면 서버에 묻는다. PK·UK 는 같은 이름 인덱스도.</summary>
        private void NamedConstraint(
            ConstraintDraft c, IReadOnlyList<ConstraintDraft> live, IEnumerable<IndexDraft> liveIndexes, HashSet<string> freed, HashSet<string> freedIndexes)
        {
            if (string.IsNullOrWhiteSpace(c.Name))
            {
                return;
            }

            if (live.Any(o => o.RowKey != c.RowKey && string.Equals(o.Name, c.Name, StringComparison.Ordinal)))
            {
                Findings.Add(new CheckFinding(CheckSeverity.Block, CheckCode.NameExists, c.RowKey) { Subject = c.Name });
                return;
            }

            if (!freed.Contains(c.Name))
            {
                Checks.Add(Name(c.RowKey, c.Name, TableEditNameKind.Constraint));
            }

            if (c.Kind is ConstraintKind.PrimaryKey or ConstraintKind.Unique && !freedIndexes.Contains(c.Name)
                && !liveIndexes.Any(i => string.Equals(i.Name, c.Name, StringComparison.Ordinal) && i.ConstraintName is not null))
            {
                Checks.Add(Name(c.RowKey, c.Name, TableEditNameKind.Index));
            }
        }

        private void NamedIndex(IndexDraft i, IReadOnlyList<IndexDraft> live, IEnumerable<ConstraintDraft> constraints, HashSet<string> freed)
        {
            if (string.IsNullOrWhiteSpace(i.Name))
            {
                return;
            }

            var clash = live.Any(o => o.RowKey != i.RowKey && string.Equals(o.Name, i.Name, StringComparison.Ordinal))
                        || constraints.Any(c => c.Kind is ConstraintKind.PrimaryKey or ConstraintKind.Unique && string.Equals(c.Name, i.Name, StringComparison.Ordinal));
            if (clash)
            {
                Findings.Add(new CheckFinding(CheckSeverity.Block, CheckCode.NameExists, i.RowKey) { Subject = i.Name });
            }
            else if (!freed.Contains(i.Name))
            {
                Checks.Add(Name(i.RowKey, i.Name, TableEditNameKind.Index));
            }
        }

        /// <summary>열 이름 겹침(열은 이 테이블 안 이름이라 서버에 묻지 않는다). 겹친 줄 가운데 <paramref name="blame"/> 인 줄에 ✖.</summary>
        private void DuplicateColumnNames(IReadOnlyList<ColumnDraft> live, Func<ColumnDraft, bool> blame)
        {
            foreach (var group in live.GroupBy(c => c.Name, StringComparer.Ordinal).Where(g => g.Count() > 1))
            {
                foreach (var c in group.Where(blame))
                {
                    Findings.Add(new CheckFinding(CheckSeverity.Block, CheckCode.NameExists, c.RowKey) { Subject = c.Name });
                }
            }
        }

        private void Irreversible(string rowKey, string name) =>
            Findings.Add(new CheckFinding(CheckSeverity.Warning, CheckCode.Irreversible, rowKey) { Subject = name, Detail = dialect.PurgesOnDrop ? "purge" : null });

        private void ServerName(string rowKey, string name)
        {
            if (dialect.IsServerName(name))
            {
                Findings.Add(new CheckFinding(CheckSeverity.Warning, CheckCode.ServerName, rowKey) { Subject = name });
            }
        }

        private void Unsupported(TableEditFeature feature, string rowKey, string subject)
        {
            if (ReasonKey(feature) is { } reason)
            {
                Findings.Add(new CheckFinding(CheckSeverity.Block, CheckCode.Unsupported, rowKey) { Subject = subject, Detail = reason });
            }
        }

        private static TableEditCheck Name(string rowKey, string name, TableEditNameKind kind) =>
            new(TableEditCheckKind.NameExists, CheckCode.NameExists, rowKey, name) { Name = name, NameKind = kind };
    }
}

/// <summary>
/// 검사 계획을 <see cref="ITableEditChecks"/> 로 돌려 판정 목록을 만든다(검사마다 시간 제한, 넘거나 읽기 실패 = ⚠ 「확인 못 함」).
/// 걸리지 않은 검사는 같은 <see cref="CheckCode"/> 의 <see cref="CheckSeverity.Ok"/> 줄(건수 0)로 남긴다(설계 10장 ✔ 항목별 한 줄).
/// </summary>
public static class TableEditCheckRunner
{
    /// <summary>판정 목록(✖ → ⚠ → ✔ 순서, 같은 판정 안은 계획 순서). 사용자 취소(<paramref name="ct"/>)는 그대로 던진다.</summary>
    /// <param name="table">검사할 테이블(지금 이름). 새 테이블은 만들 이름(소유자로 이름 겹침을 묻는다)</param>
    public static async Task<IReadOnlyList<CheckFinding>> RunAsync(
        TableEditCheckPlan plan, DbObjectRef table, ITableEditChecks checks, ISession session, TableEditCheckOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(checks);
        ArgumentNullException.ThrowIfNull(options);

        var results = new List<CheckFinding>(plan.Findings);
        foreach (var check in plan.Checks)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(options.Timeout);
            try
            {
                if (await RunOneAsync(check, table, checks, session, options, timeout.Token).ConfigureAwait(false) is { } finding)
                {
                    results.Add(finding);
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                results.Add(new CheckFinding(CheckSeverity.Warning, CheckCode.NotChecked, check.RowKey) { Subject = check.Subject, Detail = "timeout" });
            }
#pragma warning disable CA1031 // 검사 읽기는 어떤 이유로 실패해도 ⚠ 「확인 못 함」으로 남기고 다음 검사를 한다(설계 4장: 검사가 실행을 막지 않음).
            catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
            {
                results.Add(new CheckFinding(CheckSeverity.Warning, CheckCode.NotChecked, check.RowKey) { Subject = check.Subject, Detail = ex.Message });
            }
        }

        return [.. results.OrderBy(f => f.Severity switch { CheckSeverity.Block => 0, CheckSeverity.Warning => 1, _ => 2 })];
    }

    private static async Task<CheckFinding?> RunOneAsync(
        TableEditCheck check, DbObjectRef table, ITableEditChecks checks, ISession s, TableEditCheckOptions options, CancellationToken ct)
    {
        switch (check.Kind)
        {
            case TableEditCheckKind.Overflow:
                return Counted(check, await checks.OverflowCountAsync(s, table, check.Column!, check.Target!, ct).ConfigureAwait(false),
                    TableEditCheckSql.OverflowWhere(check.Column!, check.Target!), table);
            case TableEditCheckKind.NonNull:
                return Counted(check, await checks.NonNullCountAsync(s, table, check.Column!, ct).ConfigureAwait(false),
                    TableEditCheckSql.NonNullWhere(check.Column!), table);
            case TableEditCheckKind.Nulls:
                return Counted(check, await checks.NullCountAsync(s, table, check.Column!, ct).ConfigureAwait(false),
                    TableEditCheckSql.NullWhere(check.Column!), table);
            case TableEditCheckKind.Rows:
                return Counted(check, await checks.CountRowsAsync(s, table, ct).ConfigureAwait(false), null, table);
            case TableEditCheckKind.DuplicateKeys:
                return Counted(check, await checks.DuplicateKeyCountAsync(s, table, check.Columns, ct).ConfigureAwait(false),
                    TableEditCheckSql.DuplicateWhere(table, check.Columns), table);
            case TableEditCheckKind.Orphans:
                return Counted(check,
                    await checks.OrphanCountAsync(s, table, check.Columns, check.ReferencedTable!, check.ReferencedColumns, ct).ConfigureAwait(false),
                    TableEditCheckSql.OrphanWhere(check.Columns, check.ReferencedTable!, check.ReferencedColumns), table);
            case TableEditCheckKind.CheckViolations:
                return Counted(check, await checks.CheckViolationCountAsync(s, table, check.Condition!, ct).ConfigureAwait(false),
                    TableEditCheckSql.CheckViolationWhere(check.Condition!), table);
            case TableEditCheckKind.Expression:
                {
                    var preview = await checks.PreviewExpressionAsync(s, table, check.Expression!, check.Columns, options.PreviewRows, ct).ConfigureAwait(false);
                    return Judged(check, preview.Error);
                }

            case TableEditCheckKind.ExpressionSyntax:
                return Judged(check, await checks.ValidateExpressionAsync(s, table.Owner, check.ColumnDrafts, check.Expression!, ct).ConfigureAwait(false));
            case TableEditCheckKind.NameExists:
                {
                    var exists = await checks.NameExistsAsync(s, table.Owner, check.Name!, check.NameKind, table, ct).ConfigureAwait(false);
                    return new CheckFinding(exists ? CheckSeverity.Block : CheckSeverity.Ok, check.Code, check.RowKey) { Subject = check.Subject };
                }

            case TableEditCheckKind.Dependents:
                {
                    var dependents = await checks.DependentsAsync(s, table, ct).ConfigureAwait(false);
                    return dependents.Count == 0
                        ? null
                        : new CheckFinding(CheckSeverity.Warning, check.Code, check.RowKey) { Subject = check.Subject, Dependents = dependents };
                }

            case TableEditCheckKind.TableRewrite:
                {
                    var rows = await checks.NumRowsAsync(s, table, ct).ConfigureAwait(false);
                    return new CheckFinding(CheckSeverity.Warning, check.Code, check.RowKey)
                    {
                        Subject = check.Subject, Count = rows, Detail = rows is null ? "unknown" : null,
                    };
                }

            case TableEditCheckKind.LargeTable:
                {
                    var rows = await checks.NumRowsAsync(s, table, ct).ConfigureAwait(false);
                    var large = rows is null || rows >= options.LargeTableRows; // 통계가 없으면 크기를 모름 → ⚠
                    return new CheckFinding(large ? CheckSeverity.Warning : CheckSeverity.Ok, check.Code, check.RowKey)
                    {
                        Subject = check.Subject, Count = rows, Detail = rows is null ? "unknown" : null,
                    };
                }

            default:
                throw new ArgumentOutOfRangeException(nameof(check), check.Kind, null);
        }
    }

    private static CheckFinding Counted(TableEditCheck check, long count, string? where, DbObjectRef table) =>
        new(count > 0 ? CheckSeverity.Block : CheckSeverity.Ok, check.Code, check.RowKey)
        {
            Subject = check.Subject,
            Count = count,
            ViewRowsSql = count > 0 && where is not null ? TableEditCheckSql.RowsSql(table, where) : null,
        };

    private static CheckFinding Judged(TableEditCheck check, string? error) =>
        new(error is null ? CheckSeverity.Ok : CheckSeverity.Block, check.Code, check.RowKey) { Subject = check.Subject, Detail = error };
}
