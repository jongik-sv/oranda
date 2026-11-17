using System.Text;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Journal;
using Oranda.Core.Metadata;
using Oranda.Core.Text;

namespace Oranda.Core.Schema.TableEdit;

/// <summary>문장에 붙는 표시(미리보기·묶음의 ⚠, 검사 계획 입력).</summary>
[Flags]
public enum TableEditMarks
{
    None = 0,

    /// <summary>서버 이름(SYS_C…) 제약을 그 이름으로 다룬다 — 다른 DB 에서 실패(TE-8).</summary>
    ServerName = 1,

    /// <summary>되돌릴 수 없는 DROP(열·제약·인덱스).</summary>
    Irreversible = 2,

    /// <summary>바꾸기를 위해 지우고 다시 만드는 짝의 한쪽(제약·인덱스 정의 변경, 가상 식 DROP + ADD).</summary>
    Recreate = 4,
}

/// <summary>문장이 서버의 줄 하나에 하는 일(중간 실패 뒤 다시 계산 <see cref="TableDraftRebase"/> 가 성공한 문장만 원본에 적용해 본다).</summary>
public enum TableEditEffectKind
{
    /// <summary>이름·있음에 영향 없음(MODIFY·주석·ENABLE/DISABLE).</summary>
    None,

    /// <summary>줄을 지운다.</summary>
    Drop,

    /// <summary>줄 이름을 <see cref="TableEditEffect.Name"/> 으로 바꾼다.</summary>
    Rename,

    /// <summary>줄을 <see cref="TableEditEffect.Name"/> 으로 만든다.</summary>
    Create,

    /// <summary>새 테이블 CREATE TABLE: 지우지 않은 열과 FK 가 아닌 제약을 모두 만든다.</summary>
    CreateTable,
}

/// <summary>문장 효과.</summary>
public sealed record TableEditEffect(TableEditEffectKind Kind, string? Name = null)
{
    public static TableEditEffect None { get; } = new(TableEditEffectKind.None);
}

/// <summary>
/// 문장 하나와 그 출처. <see cref="Statement"/> 의 <see cref="SqlStatement.Text"/> 가 미리보기에 보이고 그대로 실행·저장된다(설계 4장).
/// </summary>
/// <param name="RowKey">나온 편집 줄(<see cref="TableDraft.TableRowKey"/> = 테이블)</param>
public sealed record TableEditStatement(SqlStatement Statement, string RowKey, TableEditPage Page, TableEditMarks Marks)
{
    /// <summary>이 문장이 <see cref="RowKey"/> 줄에 하는 일(페이지 = 열·제약·인덱스).</summary>
    public TableEditEffect Effect { get; init; } = TableEditEffect.None;
}

/// <summary>편집 하나의 문장 목록(실행 순서, 설계 8.3).</summary>
public sealed record TableEditScript(IReadOnlyList<TableEditStatement> Statements)
{
    public static TableEditScript Empty { get; } = new([]);

    public bool IsEmpty => Statements.Count == 0;

    /// <summary>실행 엔진에 보낼 문장(같은 객체).</summary>
    public IReadOnlyList<SqlStatement> SqlStatements => [.. Statements.Select(s => s.Statement)];

    /// <summary>미리보기 글: 문장마다 끝에 <c>;</c>, 문장 사이 <paramref name="newline"/>.</summary>
    public string PreviewText(string newline = "\n") =>
        string.Join(newline, Statements.Select(s => s.Statement.Text + ";"));
}

/// <summary>
/// 차이 → 문장(설계 8.2 문장 꼴, 8.3 순서, TE-11 한 열 MODIFY 하나). 이름은 늘 소유자를 붙이고, 따옴표는 대문자 규칙을 벗어나거나 예약어일 때만
/// (<c>SqlIdentifier.Quote</c> + <see cref="ITableEditDialect.IsReservedWord"/>). 문장은 모두 <see cref="StatementKind.Ddl"/> 이고 분류기와 같은
/// <see cref="SqlStatement.Verb"/>·<see cref="SqlStatement.ChangeTarget"/>·<see cref="SqlStatement.DangerousTarget"/> 를 단다(변경 일지·정책 관문이 그대로 쓴다).
/// </summary>
public static class TableEditScriptBuilder
{
    /// <summary>원본과 편집의 문장. 바뀐 것이 없으면 <see cref="TableEditScript.Empty"/>. 새 테이블(<see cref="TableDraft.IsNewTable"/>)은 이름이 비면 빈 목록.</summary>
    /// <param name="server">접속한 서버(식별자 바이트 한도 — 규칙 이름 따라 바꾸기). 모르면 null(작은 한도, <see cref="ITableEditDialect.MaxIdentifierBytes"/>).
    /// 검사 계획(<see cref="TableEditCheckOptions.Server"/>)과 같은 값을 준다.</param>
    public static TableEditScript Build(TableDraft original, TableDraft edited, ITableEditDialect dialect, ServerInfo? server = null)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(edited);
        ArgumentNullException.ThrowIfNull(dialect);

        var b = new Builder(original, edited, dialect, dialect.MaxIdentifierBytes(server));
        if (edited.IsNewTable)
        {
            b.Create();
        }
        else
        {
            b.Alter(TableDraftDiff.Compute(original, edited, dialect));
        }

        return b.Statements.Count == 0 ? TableEditScript.Empty : new TableEditScript(b.Statements);
    }

    private sealed class Builder(TableDraft original, TableDraft edited, ITableEditDialect dialect, int maxIdentifierBytes)
    {
        public List<TableEditStatement> Statements { get; } = [];

        private Dictionary<string, string> _renames = new(StringComparer.Ordinal);

        private string Owner => edited.IsNewTable ? edited.Owner : original.Owner;

        private string TableName => edited.IsNewTable ? edited.Name : original.Name;

        private string Table => Q(Owner) + "." + Q(TableName);

        private string Q(string name) => SqlIdentifier.Quote(name, dialect.IsReservedWord);

        // ---------- 새 테이블(설계 7.5) ----------

        public void Create()
        {
            var columns = edited.Columns.Where(c => !c.IsDropped).ToList();
            if (string.IsNullOrWhiteSpace(edited.Name) || columns.Count == 0)
            {
                return;
            }

            var constraints = edited.Constraints.Where(c => !c.IsDropped).ToList();
            var parts = columns.Select(c => ColumnDefinition(c, null)).ToList();
            parts.AddRange(constraints.Where(c => c.Kind != ConstraintKind.ForeignKey).OrderBy(c => KindOrder(c.Kind)).Select(ConstraintDefinition));
            var tablespace = string.IsNullOrWhiteSpace(edited.Tablespace) ? string.Empty : " TABLESPACE " + Q(edited.Tablespace.Trim());
            var create = edited.Kind switch
            {
                // GTT 는 임시 테이블스페이스를 쓰므로 TABLESPACE 를 쓰지 않는다(설계 18.10).
                TableKind.Temporary => $"CREATE GLOBAL TEMPORARY TABLE {Table} ({string.Join(", ", parts)})"
                                       + (edited.OnCommitPreserveRows ? " ON COMMIT PRESERVE ROWS" : " ON COMMIT DELETE ROWS"),
                TableKind.Iot => $"CREATE TABLE {Table} ({string.Join(", ", parts)}) ORGANIZATION INDEX" + tablespace,
                _ => $"CREATE TABLE {Table} ({string.Join(", ", parts)})" + tablespace,
            };
            Add(Ddl(create, "CREATE"), TableDraft.TableRowKey, TableEditPage.Columns, effect: new TableEditEffect(TableEditEffectKind.CreateTable));

            foreach (var fk in constraints.Where(c => c.Kind == ConstraintKind.ForeignKey))
            {
                Add(AlterTable("ADD " + ConstraintDefinition(fk)), fk.RowKey, TableEditPage.Constraints, effect: Created(fk.Name));
            }

            if (!string.IsNullOrEmpty(edited.Comment))
            {
                AddTableComment();
            }

            foreach (var c in columns.Where(c => !string.IsNullOrEmpty(c.Comment)))
            {
                AddColumnComment(c);
            }

            foreach (var index in edited.Indexes.Where(i => !i.IsDropped && i.ConstraintName is null))
            {
                AddCreateIndex(index, TableEditMarks.None);
            }
        }

        // ---------- 기존 테이블(설계 8.2·8.3) ----------

        public void Alter(TableDraftChanges changes)
        {
            if (changes.IsEmpty)
            {
                return;
            }

            // 1. 지울·바꿀 FK → 지울·바꿀 제약 → 지울·바꿀 인덱스
            var droppedConstraints = changes.Constraints.Where(d => d.State == DraftRowState.Dropped || d.Changes.HasFlag(ConstraintChange.Definition)).ToList();
            foreach (var d in droppedConstraints.Where(d => d.Original!.Kind == ConstraintKind.ForeignKey)
                         .Concat(droppedConstraints.Where(d => d.Original!.Kind != ConstraintKind.ForeignKey)))
            {
                var marks = (d.State == DraftRowState.Dropped ? TableEditMarks.Irreversible : TableEditMarks.Recreate) | ServerMark(d.Original!.Name);
                Add(AlterTable("DROP CONSTRAINT " + Q(d.Original.Name)), d.RowKey, TableEditPage.Constraints, marks, Dropped);
            }

            foreach (var d in changes.Indexes.Where(d => d.State == DraftRowState.Dropped || d.Changes.HasFlag(IndexChange.Definition)))
            {
                var target = Q(Owner) + "." + Q(d.Original!.Name);
                var statement = Ddl("DROP INDEX " + target, "DROP") with
                {
                    DangerousTarget = target,
                    ChangeTarget = new ChangeTarget(Owner, d.Original.Name, "INDEX", ChangeOperation.Drop),
                };
                Add(statement, d.RowKey, TableEditPage.Indexes, d.State == DraftRowState.Dropped ? TableEditMarks.Irreversible : TableEditMarks.Recreate, Dropped);
            }

            // 지울 열의 이름을 새 열·이름 바꾼 열이 쓰면 그 열은 이름 바꾸기 앞에 지운다(설계 8.3 예외).
            var droppedColumns = changes.Columns.Where(d => d.State == DraftRowState.Dropped).ToList();
            var claimedNames = changes.Columns
                .Where(d => d.State == DraftRowState.Added || d.Changes.HasFlag(ColumnChange.Name))
                .Select(d => d.Edited!.Name)
                .ToHashSet(StringComparer.Ordinal);
            var earlyDrops = droppedColumns.Where(d => claimedNames.Contains(d.Original!.Name)).ToList();
            foreach (var d in earlyDrops)
            {
                AddDropColumn(d.Original!, d.RowKey, TableEditMarks.Irreversible);
            }

            // 다시 만드는 제약·인덱스의 식(서버 표기 그대로 둔 것)은 이름 바꾼 열을 새 이름으로 고쳐 쓴다.
            _renames = changes.Columns.Where(d => d.State == DraftRowState.Modified && d.Changes.HasFlag(ColumnChange.Name))
                .ToDictionary(d => d.Original!.Name, d => d.Edited!.Name, StringComparer.Ordinal);

            // 2. 열 이름
            AddRenames(changes.Columns.Where(d => d.State == DraftRowState.Modified && d.Changes.HasFlag(ColumnChange.Name)).ToList());

            // 3. 열 MODIFY(한 열 한 문장, TE-11)
            var recreated = new List<ColumnDiff>();
            foreach (var d in changes.Columns.Where(d => d.State == DraftRowState.Modified))
            {
                var (o, e) = (d.Original!, d.Edited!);
                if (TableDraftDiff.IsRecreated(d, dialect))
                {
                    recreated.Add(d);
                    continue;
                }

                // 신원·도메인·숨김은 문법이 달라 따로(설계 18.5). 신원을 없애며 기본값·NULL 을 바꾸면 DROP IDENTITY 가 먼저다(신원 열에는 기본값·NULL 을 못 준다).
                var dropsIdentity = d.Changes.HasFlag(ColumnChange.Identity) && !e.IsIdentityColumn;
                if (dropsIdentity)
                {
                    Add(AlterTable($"MODIFY ({Q(e.Name)} DROP IDENTITY)"), d.RowKey, TableEditPage.Columns);
                }

                if (Modify(d) is { } modify)
                {
                    Add(AlterTable($"MODIFY ({modify})"), d.RowKey, TableEditPage.Columns);
                }

                if (d.Changes.HasFlag(ColumnChange.Identity) && !dropsIdentity && e.Identity is { } identity)
                {
                    Add(AlterTable($"MODIFY ({Q(e.Name)} {IdentityChange(o.Identity, identity)})"), d.RowKey, TableEditPage.Columns);
                }

                if (d.Changes.HasFlag(ColumnChange.Domain))
                {
                    var clause = string.IsNullOrWhiteSpace(e.Domain) ? "DROP DOMAIN" : "DOMAIN " + e.Domain.Trim();
                    Add(AlterTable($"MODIFY ({Q(e.Name)} {clause})"), d.RowKey, TableEditPage.Columns);
                }

                if (d.Changes.HasFlag(ColumnChange.Invisible))
                {
                    Add(AlterTable($"MODIFY ({Q(e.Name)} {(e.IsInvisible ? "INVISIBLE" : "VISIBLE")})"), d.RowKey, TableEditPage.Columns);
                }
            }

            // 4. 열 추가: 일반 열 → 가상 열(지우고 다시 만드는 열 포함)
            var added = changes.Columns.Where(d => d.State == DraftRowState.Added).ToList();
            foreach (var d in added.Where(d => !d.Edited!.IsVirtual))
            {
                Add(AlterTable($"ADD ({ColumnDefinition(d.Edited!, null)})"), d.RowKey, TableEditPage.Columns, effect: Created(d.Edited!.Name));
            }

            foreach (var d in added.Where(d => d.Edited!.IsVirtual).Concat(recreated).OrderBy(d => edited.Columns.ToList().FindIndex(c => c.RowKey == d.RowKey)))
            {
                if (d.State == DraftRowState.Modified)
                {
                    var marks = TableEditMarks.Recreate | (d.Original!.IsVirtual ? TableEditMarks.None : TableEditMarks.Irreversible);
                    AddDropColumn(d.Original with { Name = d.Edited!.Name }, d.RowKey, marks);
                    Add(AlterTable($"ADD ({ColumnDefinition(d.Edited, d.Original)})"), d.RowKey, TableEditPage.Columns, TableEditMarks.Recreate, Created(d.Edited.Name));
                }
                else
                {
                    Add(AlterTable($"ADD ({ColumnDefinition(d.Edited!, null)})"), d.RowKey, TableEditPage.Columns, effect: Created(d.Edited!.Name));
                }
            }

            // 5. 열 지우기
            foreach (var d in droppedColumns.Except(earlyDrops))
            {
                AddDropColumn(d.Original!, d.RowKey, TableEditMarks.Irreversible);
            }

            // 6. 제약: 이름 바꾸기 → 추가(PK·UK → CHECK → FK) → ENABLE/DISABLE
            foreach (var d in changes.Constraints.Where(d => d.State == DraftRowState.Modified && d.Changes == (d.Changes & (ConstraintChange.Name | ConstraintChange.Enabled))
                                                            && d.Changes.HasFlag(ConstraintChange.Name)))
            {
                Add(AlterTable($"RENAME CONSTRAINT {Q(d.Original!.Name)} TO {Q(d.Edited!.Name)}"), d.RowKey, TableEditPage.Constraints, ServerMark(d.Original.Name),
                    Renamed(d.Edited.Name));
            }

            var addedConstraints = changes.Constraints
                .Where(d => d.State == DraftRowState.Added || (d.State == DraftRowState.Modified && d.Changes.HasFlag(ConstraintChange.Definition)))
                .OrderBy(d => KindOrder(d.Edited!.Kind))
                .ToList();
            foreach (var d in addedConstraints)
            {
                var c = d.Edited!;
                if (d.State == DraftRowState.Modified && c.Condition is { } condition
                    && dialect.NormalizeCondition(condition) == dialect.NormalizeCondition(d.Original!.Condition))
                {
                    c = c with { Condition = TableEditExpression.RenameNames(condition, _renames, dialect.IsReservedWord) };
                }

                Add(AlterTable("ADD " + ConstraintDefinition(c)), d.RowKey, TableEditPage.Constraints,
                    d.State == DraftRowState.Modified ? TableEditMarks.Recreate : TableEditMarks.None, Created(d.Edited!.Name));
            }

            foreach (var d in changes.Constraints.Where(d => d.State == DraftRowState.Modified && !d.Changes.HasFlag(ConstraintChange.Definition)
                                                            && d.Changes.HasFlag(ConstraintChange.Enabled)))
            {
                var verb = d.Edited!.IsEnabled ? "ENABLE" : "DISABLE";
                var name = d.Changes.HasFlag(ConstraintChange.Name) ? d.Edited.Name : d.Original!.Name;
                Add(AlterTable($"{verb} CONSTRAINT {Q(name)}"), d.RowKey, TableEditPage.Constraints, ServerMark(name));
            }

            // 7. 인덱스: 이름 바꾸기 → 만들기
            foreach (var d in changes.Indexes.Where(d => d.State == DraftRowState.Modified && d.Changes == IndexChange.Name))
            {
                var statement = Ddl($"ALTER INDEX {Q(Owner)}.{Q(d.Original!.Name)} RENAME TO {Q(d.Edited!.Name)}", "ALTER") with
                {
                    ChangeTarget = new ChangeTarget(Owner, d.Original.Name, "INDEX", ChangeOperation.Alter),
                };
                Add(statement, d.RowKey, TableEditPage.Indexes, effect: Renamed(d.Edited.Name));
            }

            foreach (var d in changes.Indexes.Where(d => d.State == DraftRowState.Added || d.Changes.HasFlag(IndexChange.Definition)))
            {
                var index = d.Edited!;
                if (d.State == DraftRowState.Modified)
                {
                    var serverForms = d.Original!.Columns.Select(c => dialect.NormalizeExpression(c.Expression)).OfType<string>().ToHashSet(StringComparer.Ordinal);
                    index = index with
                    {
                        Columns = [.. index.Columns.Select(c => c.Expression is { } x && dialect.NormalizeExpression(x) is { } n && serverForms.Contains(n)
                            ? c with { Expression = TableEditExpression.RenameNames(x, _renames, dialect.IsReservedWord) }
                            : c)],
                    };
                }

                AddCreateIndex(index, d.State == DraftRowState.Modified ? TableEditMarks.Recreate : TableEditMarks.None);
            }

            // 8. 주석(다시 만든 열은 주석이 사라지므로 다시 단다)
            if (changes.CommentChanged)
            {
                AddTableComment();
            }

            foreach (var d in changes.Columns.Where(d =>
                         (d.State == DraftRowState.Modified && (d.Changes.HasFlag(ColumnChange.Comment) || (recreated.Contains(d) && !string.IsNullOrEmpty(d.Edited!.Comment))))
                         || (d.State == DraftRowState.Added && !string.IsNullOrEmpty(d.Edited!.Comment))))
            {
                AddColumnComment(d.Edited!);
            }

            // 9. 테이블 이름(맨 끝 — 앞 문장이 옛 이름을 쓴다, 설계 18.1). 규칙 이름 따라 바꾸기는 켤 때만(TE-38).
            if (changes.Renamed)
            {
                // 규칙 이름은 RENAME TO 앞(옛 이름의 객체를 다룬다). 끄면 없음(TE-38).
                foreach (var r in TableEditRuleNames.Plan(original, edited, changes, maxIdentifierBytes))
                {
                    if (r.Page == TableEditPage.Constraints)
                    {
                        Add(AlterTable($"RENAME CONSTRAINT {Q(r.OldName)} TO {Q(r.NewName)}"), r.RowKey, TableEditPage.Constraints, effect: Renamed(r.NewName));
                    }
                    else
                    {
                        var statement = Ddl($"ALTER INDEX {Q(Owner)}.{Q(r.OldName)} RENAME TO {Q(r.NewName)}", "ALTER") with
                        {
                            ChangeTarget = new ChangeTarget(Owner, r.OldName, "INDEX", ChangeOperation.Alter),
                        };
                        Add(statement, r.RowKey, TableEditPage.Indexes, effect: Renamed(r.NewName));
                    }
                }

                Add(AlterTable("RENAME TO " + Q(edited.Name)), TableDraft.TableRowKey, TableEditPage.Description);
            }
        }

        /// <summary>새 열·새 테이블의 신원 절(옵션은 값이 있는 것 모두).</summary>
        private static string IdentityClause(IdentityInfo identity) => IdentityText(identity.Generation, identity.StartWith, identity.IncrementBy);

        /// <summary>
        /// 기존 신원 열의 바꾸기 절: 생성 방식은 늘 쓰고(문법), 옵션은 원본과 달라진 것만 쓴다 — <c>START WITH</c> 를 다시 쓰면 시퀀스가 그 값부터 다시 시작해
        /// 이미 있는 키와 겹친다. 원본 속성을 모르면(<paramref name="from"/> null) 옵션을 모두 쓴다(검사가 ✖ 로 막는다).
        /// </summary>
        private static string IdentityChange(IdentityInfo? from, IdentityInfo to) => IdentityText(
            to.Generation,
            from is null || to.StartWith != from.StartWith ? to.StartWith : null,
            from is null || to.IncrementBy != from.IncrementBy ? to.IncrementBy : null);

        private static string IdentityText(IdentityGeneration generation, long? startWith, long? incrementBy)
        {
            var text = generation switch
            {
                IdentityGeneration.ByDefault => "BY DEFAULT",
                IdentityGeneration.ByDefaultOnNull => "BY DEFAULT ON NULL",
                _ => "ALWAYS",
            };
            var options = new List<string>();
            if (startWith is { } start)
            {
                options.Add("START WITH " + start.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            if (incrementBy is { } increment)
            {
                options.Add("INCREMENT BY " + increment.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            return $"GENERATED {text} AS IDENTITY" + (options.Count == 0 ? string.Empty : $" ({string.Join(" ", options)})");
        }

        // ---------- 문장 조각 ----------

        /// <summary>MODIFY 괄호 안(바뀐 속성만: 형식 → DEFAULT → NULL). 바뀐 것이 없으면 null(주석·신원 등은 다른 문장).</summary>
        private string? Modify(ColumnDiff d)
        {
            var (o, e) = (d.Original!, d.Edited!);
            var sb = new StringBuilder(Q(e.Name));
            var any = false;
            if (d.Changes.HasFlag(ColumnChange.Type) && ColumnTypeText.Statement(e with { CharSemantics = e.CharSemantics ?? o.CharSemantics }) is { Length: > 0 } type)
            {
                sb.Append(' ').Append(type);
                any = true;
            }

            if (d.Changes.HasFlag(ColumnChange.VirtualExpression))
            {
                sb.Append(" AS (").Append(e.VirtualExpression!.Trim()).Append(')');
                any = true;
            }

            if (d.Changes.HasFlag(ColumnChange.Default))
            {
                sb.Append(" DEFAULT ").Append(dialect.NormalizeDefault(e.DefaultValue) is null ? "NULL" : e.DefaultValue!.Trim());
                any = true;
            }

            if (d.Changes.HasFlag(ColumnChange.Nullable))
            {
                sb.Append(e.IsNullable ? " NULL" : " NOT NULL");
                any = true;
            }

            return any ? sb.ToString() : null;
        }

        /// <summary>열 정의(ADD·CREATE TABLE). <paramref name="original"/> 이 있으면(다시 만들기) 길이 기준을 원래대로.</summary>
        private string ColumnDefinition(ColumnDraft c, ColumnDraft? original)
        {
            var sb = new StringBuilder(Q(c.Name));
            var type = ColumnTypeText.Statement(c with { CharSemantics = c.CharSemantics ?? original?.CharSemantics });
            if (type.Length > 0)
            {
                sb.Append(' ').Append(type);
            }

            // 문법: 열 [형식] [DOMAIN d] [INVISIBLE] { AS (식) VIRTUAL | GENERATED … AS IDENTITY | DEFAULT … } [NOT NULL]
            if (!string.IsNullOrWhiteSpace(c.Domain))
            {
                sb.Append(" DOMAIN ").Append(c.Domain.Trim());
            }

            if (c.IsInvisible)
            {
                sb.Append(" INVISIBLE");
            }

            if (c.IsVirtual)
            {
                sb.Append(" AS (").Append(c.VirtualExpression!.Trim()).Append(") VIRTUAL");
            }
            else if (c.Identity is { } identity)
            {
                sb.Append(' ').Append(IdentityClause(identity));
            }
            else if (dialect.NormalizeDefault(c.DefaultValue) is not null)
            {
                sb.Append(" DEFAULT ").Append(c.DefaultValue!.Trim());
            }

            if (!c.IsNullable)
            {
                sb.Append(" NOT NULL");
            }

            return sb.ToString();
        }

        private string ConstraintDefinition(ConstraintDraft c)
        {
            var body = c.Kind switch
            {
                ConstraintKind.PrimaryKey => $"PRIMARY KEY ({Columns(c.ColumnKeys)})",
                ConstraintKind.Unique => $"UNIQUE ({Columns(c.ColumnKeys)})",
                ConstraintKind.Check => $"CHECK ({c.Condition?.Trim()})",
                _ => $"FOREIGN KEY ({Columns(c.ColumnKeys)}) REFERENCES {Reference(c)}" + c.DeleteRule switch
                {
                    DeleteRule.Cascade => " ON DELETE CASCADE",
                    DeleteRule.SetNull => " ON DELETE SET NULL",
                    _ => string.Empty,
                },
            };
            var name = string.IsNullOrWhiteSpace(c.Name) ? string.Empty : "CONSTRAINT " + Q(c.Name) + " ";
            return name + body + (c.IsEnabled ? string.Empty : " DISABLE");
        }

        private string Reference(ConstraintDraft fk)
        {
            var parent = fk.ReferencedTable is { } t ? Q(t.Owner) + "." + Q(t.Name) : string.Empty;
            return fk.ReferencedColumns.Count == 0 ? parent : $"{parent} ({string.Join(", ", fk.ReferencedColumns.Select(Q))})";
        }

        private void AddCreateIndex(IndexDraft index, TableEditMarks marks)
        {
            var kind = index.Kind == IndexKind.Bitmap ? "BITMAP " : index.IsUnique ? "UNIQUE " : string.Empty;
            var columns = string.Join(", ", index.Columns.Select(c =>
                (c.Expression is { } expression ? expression.Trim() : ColumnName(c.ColumnKey!)) + (c.Descending ? " DESC" : string.Empty)));
            var text = $"CREATE {kind}INDEX {Q(Owner)}.{Q(index.Name)} ON {Table} ({columns})"
                       + (string.IsNullOrWhiteSpace(index.Tablespace) ? string.Empty : " TABLESPACE " + Q(index.Tablespace.Trim()));
            Add(Ddl(text, "CREATE"), index.RowKey, TableEditPage.Indexes, marks, Created(index.Name));
        }

        private void AddDropColumn(ColumnDraft column, string rowKey, TableEditMarks marks)
        {
            var statement = AlterTable("DROP COLUMN " + Q(column.Name));
            Add(statement with
            {
                ChangeTarget = statement.ChangeTarget! with { DataLoss = DataLossKind.DropColumns, DroppedColumns = [column.Name] },
            }, rowKey, TableEditPage.Columns, marks, Dropped);
        }

        /// <summary>열 이름 바꾸기: 다른 열이 아직 쥔 이름으로 바꾸는 것은 그 열이 먼저 바꾼 뒤에, 서로 맞바꾸는 고리는 임시 이름으로 끊는다.</summary>
        private void AddRenames(List<ColumnDiff> renames)
        {
            var pending = renames.Select(d => (d.RowKey, From: d.Original!.Name, To: d.Edited!.Name)).ToList();
            var used = edited.Columns.Select(c => c.Name).Concat(original.Columns.Select(c => c.Name)).ToHashSet(StringComparer.Ordinal);
            while (pending.Count > 0)
            {
                var next = pending.FindIndex(p => !pending.Any(q => q.RowKey != p.RowKey && string.Equals(q.From, p.To, StringComparison.Ordinal)));
                if (next >= 0)
                {
                    var p = pending[next];
                    Add(AlterTable($"RENAME COLUMN {Q(p.From)} TO {Q(p.To)}"), p.RowKey, TableEditPage.Columns, effect: Renamed(p.To));
                    pending.RemoveAt(next);
                    continue;
                }

                var first = pending[0];
                var temp = TemporaryName(first.From, used);
                used.Add(temp);
                Add(AlterTable($"RENAME COLUMN {Q(first.From)} TO {Q(temp)}"), first.RowKey, TableEditPage.Columns, effect: Renamed(temp));
                pending[0] = first with { From = temp };
            }
        }

        private string TemporaryName(string name, HashSet<string> used)
        {
            var max = maxIdentifierBytes;
            for (var n = 1; ; n++)
            {
                var suffix = "$" + n.ToString(System.Globalization.CultureInfo.InvariantCulture);
                var stem = name;
                while (stem.Length > 1 && Encoding.UTF8.GetByteCount(stem + suffix) > max)
                {
                    stem = stem[..^1];
                }

                if (!used.Contains(stem + suffix))
                {
                    return stem + suffix;
                }
            }
        }

        private void AddTableComment() =>
            Add(Ddl($"COMMENT ON TABLE {Table} IS {SqlLiteral.Quote(edited.Comment ?? string.Empty)}", "COMMENT"), TableDraft.TableRowKey, TableEditPage.Description);

        private void AddColumnComment(ColumnDraft c) =>
            Add(Ddl($"COMMENT ON COLUMN {Table}.{Q(c.Name)} IS {SqlLiteral.Quote(c.Comment ?? string.Empty)}", "COMMENT"), c.RowKey, TableEditPage.Columns);

        /// <summary>열 줄 키 → 편집 이름(이름 바꾸기 뒤). 모르는 키는 머리를 뗀 이름.</summary>
        private string ColumnName(string key) =>
            Q(edited.Column(key)?.Name ?? original.Column(key)?.Name
              ?? (key.StartsWith(TableDraftFactory.UnknownColumnKeyPrefix, StringComparison.Ordinal) ? key[TableDraftFactory.UnknownColumnKeyPrefix.Length..] : key));

        private string Columns(IEnumerable<string> keys) => string.Join(", ", keys.Select(ColumnName));

        private TableEditMarks ServerMark(string name) => dialect.IsServerName(name) ? TableEditMarks.ServerName : TableEditMarks.None;

        private static int KindOrder(ConstraintKind kind) => kind switch
        {
            ConstraintKind.PrimaryKey => 0,
            ConstraintKind.Unique => 1,
            ConstraintKind.Check => 2,
            _ => 3,
        };

        private SqlStatement AlterTable(string clause) =>
            Ddl($"ALTER TABLE {Table} {clause}", "ALTER") with { ChangeTarget = new ChangeTarget(Owner, TableName, "TABLE", ChangeOperation.Alter) };

        private static SqlStatement Ddl(string text, string verb) => new(text, default, StatementKind.Ddl) { Verb = verb };

        private static readonly TableEditEffect Dropped = new(TableEditEffectKind.Drop);

        private static TableEditEffect Created(string name) => new(TableEditEffectKind.Create, name);

        private static TableEditEffect Renamed(string name) => new(TableEditEffectKind.Rename, name);

        private void Add(SqlStatement statement, string rowKey, TableEditPage page, TableEditMarks marks = TableEditMarks.None, TableEditEffect? effect = null) =>
            Statements.Add(new TableEditStatement(statement, rowKey, page, marks) { Effect = effect ?? TableEditEffect.None });
    }
}
