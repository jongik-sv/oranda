using System.Text.RegularExpressions;
using Oranda.Core.Metadata;

namespace Oranda.Core.Schema.TableEdit;

/// <summary>편집 모델 만들기(설계 7.1·7.5).</summary>
public static partial class TableDraftFactory
{
    /// <summary>열 이름을 열 목록에서 못 찾은 제약·인덱스 열의 줄 키 머리(뒤에 이름). 문장은 이름을 그대로 쓴다.</summary>
    internal const string UnknownColumnKeyPrefix = "?";

    /// <summary>
    /// 기존 테이블의 원본 모델. 가상 열은 <see cref="ColumnInfo.DefaultValue"/> 를 <see cref="ColumnDraft.VirtualExpression"/> 으로 옮기고(TE-30),
    /// 서버 이름의 NOT NULL CHECK 는 빼며(설계 7.3), 제약 인덱스는 <see cref="IndexDraft.ConstraintName"/> 을 채운다. 줄 키는 종류·순번(<c>c1</c>·<c>k1</c>·<c>i1</c>).
    /// Oracle 이 DESC 인덱스 열을 함수 기반(<c>SYS_NC…$</c> + 식 <c>"C"</c>)으로 보이는 것은 열 + DESC 로 되돌린다(설계 12.1 #5).
    /// 화면은 이 결과를 원본으로 두고 같은 값을 편집 모델로 쓴다(불변이라 그대로 나눠 써도 된다).
    /// </summary>
    public static TableDraft FromTable(TableInfo table, ITableEditDialect dialect)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(dialect);

        var columns = table.Columns.OrderBy(c => c.Position).Select((c, i) => Column(c, "c" + (i + 1))).ToList();
        var keys = columns.ToDictionary(c => c.Name, c => c.RowKey, StringComparer.Ordinal);
        string Key(string name) => keys.TryGetValue(name, out var k) ? k : UnknownColumnKeyPrefix + name;

        var constraints = table.Constraints
            .Where(c => !(c.Kind == ConstraintKind.Check && dialect.IsServerName(c.Name) && NotNullCheck().IsMatch(c.Condition ?? string.Empty)))
            .Select((c, i) => new ConstraintDraft("k" + (i + 1), c.Name, c.Kind)
            {
                ColumnKeys = [.. c.Columns.Select(Key)],
                ReferencedTable = c.ReferencedTable,
                ReferencedConstraint = c.ReferencedConstraint,
                ReferencedColumns = c.ReferencedColumns,
                DeleteRule = c.DeleteRule,
                Condition = c.Condition,
                IsEnabled = c.IsEnabled,
            })
            .ToList();

        var keyConstraints = table.Constraints.Where(c => c.Kind is ConstraintKind.PrimaryKey or ConstraintKind.Unique)
            .Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
        var indexes = table.Indexes.Select((index, i) => Index(index, "i" + (i + 1), keys, Key, keyConstraints)).ToList();

        return new TableDraft(table.Table.Owner, table.Table.Name)
        {
            Comment = table.Comment,
            Columns = columns,
            Constraints = constraints,
            Indexes = indexes,
            Kind = table.Kind,
            IsPartitioned = table.IsPartitioned,
            Tablespace = table.Tablespace,
        };
    }

    /// <summary>새 테이블의 빈 원본(<see cref="TableDraft.IsNewTable"/>, 이름 없음).</summary>
    public static TableDraft Empty(string owner)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        return new TableDraft(owner, string.Empty) { IsNewTable = true };
    }

    /// <summary>
    /// 「이 구조로 새 테이블…」(설계 7.5): 열(가상 포함)·주석·제약·인덱스를 복사한 새 테이블 편집 모델. 제약·인덱스 이름은 새 이름으로 규칙대로 다시 만들고
    /// (<see cref="ConstraintNaming"/>, <see cref="ConstraintDraft.IsAutoName"/>), 줄 키는 모두 새 키다. 제약이 만든 인덱스와 다룰 수 없는 종류의 인덱스는 옮기지 않는다.
    /// 테이블스페이스는 옮기지 않는다(TE-22). 신원 속성은 옮긴다(설계 18.5 — TE-7 의 예외를 없앰). 다만 속성을 못 읽은 신원 열(<see cref="ColumnDraft.IsIdentityUnknown"/>)은
    /// 신원 없이 옮긴다(생성 방식을 짐작하지 않는다). 원본은 <see cref="Empty"/> 와 짝이다.
    /// </summary>
    public static TableDraft CopyStructure(TableInfo source, string owner, string name, ITableEditDialect dialect, int maxIdentifierBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var from = FromTable(source, dialect);

        var keyMap = from.Columns.ToDictionary(c => c.RowKey, _ => DraftKeys.New(), StringComparer.Ordinal);
        string Remap(string key) => keyMap.GetValueOrDefault(key, key);

        var taken = new List<string>();
        string NextName(ConstraintNameKind kind)
        {
            var next = ConstraintNaming.Next(kind, name, taken, maxIdentifierBytes);
            taken.Add(next);
            return next;
        }

        var columns = from.Columns.Select(c => c with
        {
            RowKey = Remap(c.RowKey),
            OriginalPosition = null,
            IsIdentityUnknown = false,
        }).ToList();
        var constraints = from.Constraints.Select(c => c with
        {
            RowKey = DraftKeys.New(),
            Name = NextName(c.Kind switch
            {
                ConstraintKind.PrimaryKey => ConstraintNameKind.PrimaryKey,
                ConstraintKind.Unique => ConstraintNameKind.Unique,
                ConstraintKind.ForeignKey => ConstraintNameKind.ForeignKey,
                _ => ConstraintNameKind.Check,
            }),
            ColumnKeys = [.. c.ColumnKeys.Select(Remap)],
            IsAutoName = true,
        }).ToList();
        var indexes = from.Indexes.Where(i => i.ConstraintName is null && i.Kind != IndexKind.Other).Select(i => i with
        {
            RowKey = DraftKeys.New(),
            Name = NextName(ConstraintNameKind.Index),
            Columns = [.. i.Columns.Select(c => c.ColumnKey is null ? c : c with { ColumnKey = Remap(c.ColumnKey) })],
            Tablespace = null,
            IsAutoName = true,
        }).ToList();

        return new TableDraft(owner, name)
        {
            IsNewTable = true,
            Comment = from.Comment,
            Columns = columns,
            Constraints = constraints,
            Indexes = indexes,
        };
    }

    private static ColumnDraft Column(ColumnInfo c, string key) => new(key, c.Name)
    {
        OriginalPosition = c.Position,
        DataType = c.DataType,
        Length = c.Length,
        // 공급자가 CHAR_USED 를 못 줬으면 Oracle 사전 값으로 짐작한다(글자 기준 열은 DATA_LENGTH 가 CHAR_LENGTH 보다 크다). Tibero 는 공급자가 꼭 채운다(12.1 #7).
        CharSemantics = c.CharSemantics ?? (ColumnTypeText.HasSemantics(c.DataType) && c is { Length: { } n, ByteLength: { } b }
            ? (b > n ? CharSemantics.Characters : CharSemantics.Bytes)
            : null),
        Precision = c.Precision,
        Scale = c.Scale,
        IsNullable = c.IsNullable,
        DefaultValue = c.IsVirtual || c.IsIdentity ? null : c.DefaultValue,
        VirtualExpression = c.IsVirtual ? c.DefaultValue : null,
        Comment = c.Comment,
        // 속성을 못 읽은 신원 열은 짐작하지 않는다(ALWAYS 로 짐작하면 BY DEFAULT 열이 ALWAYS 로 바뀐다).
        Identity = c.IsIdentity ? c.Identity : null,
        IsIdentityUnknown = c.IsIdentity && c.Identity is null,
        IsInvisible = c.IsInvisible,
        Domain = c.Domain,
    };

    private static IndexDraft Index(IndexInfo index, string key, Dictionary<string, string> keys, Func<string, string> keyOf, HashSet<string> keyConstraints)
    {
        var columns = new List<IndexColumnDraft>();
        for (var j = 0; j < index.Columns.Count; j++)
        {
            var descending = j < index.Descending.Count && index.Descending[j];
            var expression = j < index.Expressions.Count ? index.Expressions[j] : null;
            if (expression is null)
            {
                columns.Add(new IndexColumnDraft(keyOf(index.Columns[j]), descending));
            }
            else if (SingleName(expression) is { } name && keys.TryGetValue(name, out var columnKey))
            {
                columns.Add(new IndexColumnDraft(columnKey, descending));
            }
            else
            {
                columns.Add(new IndexColumnDraft(null, descending) { Expression = expression });
            }
        }

        var anyExpression = columns.Any(c => c.Expression is not null);
        var kind = index.IndexType?.ToUpperInvariant() switch
        {
            null or "NORMAL" => anyExpression ? IndexKind.FunctionBased : IndexKind.Normal,
            "FUNCTION-BASED NORMAL" => anyExpression ? IndexKind.FunctionBased : IndexKind.Normal,
            "BITMAP" => anyExpression ? IndexKind.Other : IndexKind.Bitmap,
            "FUNCTION-BASED BITMAP" => anyExpression ? IndexKind.Other : IndexKind.Bitmap,
            _ => IndexKind.Other,
        };

        return new IndexDraft(key, index.Name)
        {
            IsUnique = index.IsUnique,
            Columns = columns,
            Kind = kind,
            Tablespace = index.Tablespace,
            ConstraintName = index.ConstraintName ?? (keyConstraints.Contains(index.Name) ? index.Name : null),
        };
    }

    /// <summary>식이 이름 하나(<c>"C"</c> 또는 <c>C</c>)면 그 이름(사전 표기), 아니면 null.</summary>
    private static string? SingleName(string expression)
    {
        var tokens = TableEditExpression.Tokenize(expression);
        return tokens is [{ Kind: TableEditExpression.TokenKind.Quoted } q] ? q.Text
            : tokens is [{ Kind: TableEditExpression.TokenKind.Word } w] && !char.IsDigit(w.Text[0]) ? w.Text.ToUpperInvariant()
            : null;
    }

    [GeneratedRegex("""^\s*("[^"]+"|[A-Za-z_$#][\w$#]*)\s+IS\s+NOT\s+NULL\s*$""", RegexOptions.IgnoreCase)]
    private static partial Regex NotNullCheck();
}
