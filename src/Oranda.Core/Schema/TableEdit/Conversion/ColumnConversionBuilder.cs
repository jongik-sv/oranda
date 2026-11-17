using System.Text;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Journal;
using Oranda.Core.Metadata;
using Oranda.Core.Text;

namespace Oranda.Core.Schema.TableEdit.Conversion;

/// <summary>
/// 열 형식 변경 자동 변환의 문장 만들기(설계 18.9·시안 ⑧, TE-44·TE-49). 순수 텍스트 — 실행하지 않는다.
/// 열 바꿔치기(<see cref="BuildSwap"/>)는 단계 1~6 문장을, 테이블 다시 만들기(<see cref="BuildRebuild"/>)는 열 순서 바꾸기의 다시 만들기 스크립트에 변환식을 끼운 글을 만든다.
/// 5단계(기본값·NOT NULL·주석·제약·인덱스 되살리기)는 「4단계 뒤 테이블」 → 「원하는 테이블」 차이를 <see cref="TableEditScriptBuilder"/> 로 만들어 문장 꼴을 편집기와 같게 둔다.
/// 3단계는 <c>DROP COLUMN … CASCADE CONSTRAINTS</c> — 이 열을 쓰는 여러 열 제약도 함께 지우고(없으면 ORA-12991) 5단계가 되살린다.
/// 다른 테이블이 이 열을 참조하는 FK 는 함께 지워지므로 검사(<see cref="IColumnConversionChecks.ReferencingForeignKeysAsync"/>)가 ✖ 로 막는다.
/// </summary>
public static class ColumnConversionBuilder
{
    /// <summary>DML 이 든 스크립트의 머리 주석(TE-44).</summary>
    public const string DmlHeader = "-- DML 포함: 열 형식 변환의 UPDATE(바로 뒤 COMMIT)";

    /// <summary>
    /// 임시 열 이름 <c>&lt;열&gt;$N</c>(TE-49): N 은 1부터, 테이블의 다른 열과 겹치지 않는 가장 작은 수.
    /// UTF-8 바이트가 <paramref name="maxBytes"/> 를 넘으면 열 이름 뒤를 글자 단위로 줄인다.
    /// </summary>
    public static string TemporaryName(string column, IEnumerable<string> existing, int maxBytes)
    {
        ArgumentException.ThrowIfNullOrEmpty(column);
        ArgumentNullException.ThrowIfNull(existing);
        var taken = existing.ToHashSet(StringComparer.Ordinal);
        for (var n = 1; ; n++)
        {
            var suffix = "$" + n.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var head = column;
            while (head.Length > 0 && Encoding.UTF8.GetByteCount(head + suffix) > maxBytes)
            {
                head = head[..^(char.IsLowSurrogate(head[^1]) && head.Length > 1 ? 2 : 1)];
            }

            if (head.Length == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxBytes), maxBytes, "이름 한도가 너무 작다.");
            }

            var name = head + suffix;
            if (!taken.Contains(name))
            {
                return name;
            }
        }
    }

    /// <summary>
    /// 서버 없이 정해지는 판정: ✖ 지원 안 함(방언 <see cref="TableEditFeature.ConvertingTypeChange"/>)·가상/신원 열·빈 변환식·특수 테이블(GTT·IOT·외부·클러스터)·
    /// 파티션 테이블·새 이름이 다른 열과 겹침·이 열이 든 FK 의 형식 계열이 바뀜(부모 열과 형식이 달라 ORA-02267),
    /// (바꿔치기) 이 열을 쓰는 가상 열(ORA-54031)·도메인 열·다시 만들 수 없는 종류의 인덱스,
    /// ⚠ (바꿔치기) 되돌릴 수 없음·열이 맨 뒤로 감·변환하는 동안 쓰기 멈춤(행 트리거가 UPDATE 로 발화).
    /// </summary>
    public static IReadOnlyList<CheckFinding> Findings(ColumnConversionRequest request, ColumnConversionMethod method, ITableEditDialect dialect, ServerInfo? server)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(dialect);
        var source = request.Source;
        var key = request.RowKey;
        var findings = new List<CheckFinding>();

        if (dialect.UnsupportedReasonKey(TableEditFeature.ConvertingTypeChange, server) is { } reason)
        {
            findings.Add(new CheckFinding(CheckSeverity.Block, CheckCode.Unsupported, key) { Subject = source.Name, Detail = reason });
        }

        if (source.IsVirtual || request.Target.IsVirtual)
        {
            findings.Add(Invalid(key, source.Name, "virtualColumn"));
        }

        if (source.Identity is not null || request.Target.Identity is not null)
        {
            findings.Add(Invalid(key, source.Name, "identityColumn"));
        }

        if (string.IsNullOrWhiteSpace(request.Expression))
        {
            findings.Add(Invalid(key, source.Name, "emptyExpression"));
        }

        var original = request.Original;
        var target = ResolveTarget(source, request.Target);
        if (original.Kind != TableKind.Heap)
        {
            findings.Add(Invalid(key, source.Name, "specialTable"));
        }

        if (original.IsPartitioned)
        {
            findings.Add(Invalid(key, source.Name, "partitionedTable")); // 파티션 키·LOCAL 인덱스를 편집 모델이 모른다
        }

        if (!string.Equals(source.Name, target.Name, StringComparison.Ordinal) && original.Columns.Any(c => c.RowKey != key && c.Name == target.Name))
        {
            findings.Add(new CheckFinding(CheckSeverity.Block, CheckCode.NameExists, key) { Subject = target.Name });
        }

        if (ColumnTypeText.Family(source.DataType) != ColumnTypeText.Family(target.DataType))
        {
            foreach (var fk in original.Constraints.Where(k => !k.IsDropped && k.Kind == ConstraintKind.ForeignKey && k.ColumnKeys.Contains(key)))
            {
                findings.Add(Invalid(key, source.Name, "foreignKeyTypeChange:" + fk.Name));
            }
        }

        if (method == ColumnConversionMethod.ColumnSwap)
        {
            foreach (var v in original.Columns.Where(c => c.RowKey != key && c.IsVirtual && Uses(c.VirtualExpression, source.Name)))
            {
                findings.Add(Invalid(key, source.Name, "usedByVirtualColumn:" + v.Name));
            }

            if (source.Domain is not null || target.Domain is not null)
            {
                findings.Add(Invalid(key, source.Name, "domainColumn"));
            }

            foreach (var index in original.Indexes.Where(i => !i.IsDropped && i.Kind == IndexKind.Other && UsesColumn(i, key, source.Name)))
            {
                findings.Add(Invalid(key, source.Name, "unsupportedIndex:" + index.Name));
            }

            findings.Add(new CheckFinding(CheckSeverity.Warning, CheckCode.Irreversible, key) { Subject = source.Name, Dependents = request.Dependents });
            findings.Add(new CheckFinding(CheckSeverity.Warning, CheckCode.ConversionNeedsQuiet, key)
            {
                Subject = source.Name,
                Dependents = [.. request.Dependents.Where(d => d.ObjectType == "TRIGGER")],
            });
            var last = request.Original.Columns.LastOrDefault(c => !c.IsInvisible);
            if (last is not null && last.RowKey != key)
            {
                findings.Add(new CheckFinding(CheckSeverity.Warning, CheckCode.ColumnOrderChanges, key) { Subject = source.Name });
            }
        }

        return findings;
    }

    /// <summary>
    /// 5단계가 다시 만드는 제약(이 열을 쓰는 PK·UK·CHECK·FK, 원본 이름·조건 그대로). 검사가 새 값으로 CHECK 위반·키 중복을 센다(3단계 DROP 뒤에 실패하지 않게).
    /// </summary>
    public static IReadOnlyList<ConstraintDraft> AffectedConstraints(ColumnConversionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var source = request.Source;
        return [.. request.Original.Constraints.Where(k =>
            !k.IsDropped && (k.ColumnKeys.Contains(request.RowKey) || (k.Kind == ConstraintKind.Check && Uses(k.Condition, source.Name))))];
    }

    /// <summary>
    /// 새 정의를 실행·검사가 같게 보도록 맞춘다: 이름이 비면 지금 이름, 길이 기준을 안 쓴 문자 형식은 지금 열의 기준(편집기 규칙과 같음).
    /// </summary>
    public static ColumnDraft ResolveTarget(ColumnDraft source, ColumnDraft target)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        return target with
        {
            Name = string.IsNullOrWhiteSpace(target.Name) ? source.Name : target.Name,
            CharSemantics = target.CharSemantics ?? (ColumnTypeText.HasSemantics(target.DataType) ? source.CharSemantics : null),
        };
    }

    /// <summary>
    /// 열 바꿔치기 단계 1~6(설계 18.9). 문장이 없는 5·6단계는 뺀다. 3단계는 옛 열을 지우기 전에 확인 블록을 먼저 돌린다:
    /// 2단계 COMMIT 뒤 다른 세션이 넣거나 바꾼 행(임시 열 ≠ 지금 변환식 값)이 있으면 멈춰 옛 열을 지키고, 남은 단계 스크립트로 2단계부터 다시 하게 한다.
    /// </summary>
    /// <param name="server">이름 한도(<see cref="ITableEditDialect.MaxIdentifierBytes"/>)</param>
    public static ColumnConversionPlan BuildSwap(ColumnConversionRequest request, ITableEditDialect dialect, ServerInfo? server)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(dialect);
        var original = request.Original;
        var source = request.Source;
        var target = ResolveTarget(source, request.Target);
        var temp = TemporaryName(source.Name, original.Columns.Select(c => c.Name), dialect.MaxIdentifierBytes(server));
        string Q(string name) => SqlIdentifier.Quote(name, dialect.IsReservedWord);
        var table = Q(original.Owner) + "." + Q(original.Name);
        var changeTarget = new ChangeTarget(original.Owner, original.Name, "TABLE", ChangeOperation.Alter);
        SqlStatement Alter(string clause) => Ddl($"ALTER TABLE {table} {clause}", "ALTER") with { ChangeTarget = changeTarget };

        var steps = new List<ColumnConversionStep>
        {
            new(ColumnConversionStepKind.AddTemporary,
                [Alter($"ADD ({Q(temp)} {ColumnTypeText.Statement(target)}{(target.IsInvisible ? " INVISIBLE" : string.Empty)})")]),
            new(ColumnConversionStepKind.CopyValues,
            [
                new SqlStatement($"UPDATE {table} SET {Q(temp)} = {request.Expression.Trim()}", default, StatementKind.Dml) { Verb = "UPDATE" },
                new SqlStatement("COMMIT", default, StatementKind.TransactionControl) { Verb = "COMMIT" },
            ]),
            new(ColumnConversionStepKind.DropOriginal,
            [
                Guard(table, Q(temp), target, request.Expression.Trim()),
                Alter($"DROP COLUMN {Q(source.Name)} CASCADE CONSTRAINTS") with
                {
                    ChangeTarget = changeTarget with { DataLoss = DataLossKind.DropColumns, DroppedColumns = [source.Name] },
                },
            ]),
            new(ColumnConversionStepKind.RenameTemporary, [Alter($"RENAME COLUMN {Q(temp)} TO {Q(target.Name)}")]),
        };

        var restore = Restore(request, target, dialect);
        if (restore.Count > 0)
        {
            steps.Add(new ColumnConversionStep(ColumnConversionStepKind.RestoreDefinition, restore));
        }

        var recompile = Recompile(request.Dependents, Q);
        if (recompile.Count > 0)
        {
            steps.Add(new ColumnConversionStep(ColumnConversionStepKind.Recompile, recompile));
        }

        return new ColumnConversionPlan(request.Table, source.Name, temp, target.Name, request.Expression.Trim(), steps);
    }

    /// <summary>
    /// 테이블 다시 만들기 스크립트(자동 실행 안 함 — 열 순서 바꾸기 방식 ②와 같이 「검토 뒤 실행」): 지금 열 순서 그대로, 바꿀 열은
    /// <c>CAST(변환식 AS 새 형식)</c>(LOB 은 변환식 그대로)로 복사하고 새 기본값·NOT NULL 을 맞춘다. 이름도 바꾸면 끝에 <c>RENAME COLUMN</c>.
    /// </summary>
    /// <param name="columns">테이블의 모든 열(<see cref="ColumnInfo"/>, 지금 순서, INVISIBLE 포함)</param>
    /// <param name="enterprise">Enterprise Edition 이면 DBMS_REDEFINITION(변환식은 <c>col_mapping</c>), 아니면 CTAS + RENAME</param>
    public static string BuildRebuild(
        ColumnConversionRequest request, IReadOnlyList<ColumnInfo> columns, bool enterprise, bool usePrimaryKey, string? dependentDdl)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(columns);
        var source = request.Source;
        var target = ResolveTarget(source, request.Target);
        var converted = columns.Select(c => c.Name == source.Name
            ? c with { DataType = target.DataType ?? c.DataType, DefaultValue = target.DefaultValue, IsNullable = target.IsNullable }
            : c).ToList();
        var expression = request.Expression.Trim();
        var type = ColumnTypeText.Statement(target);

        // 문자·RAW 는 CAST 가 값을 잘라 버리므로(실측 6e) 식 그대로 복사하고 MODIFY 로 줄인다 — 넘치면 ORA-01441 로 멈춘다. LOB 은 식 그대로.
        var value = ColumnTypeText.Family(target.DataType) switch
        {
            "CLOB" or "NCLOB" or "BLOB" => new RebuildConversion(expression),
            "VARCHAR2" or "CHAR" or "NVARCHAR2" or "NCHAR" or "RAW" => new RebuildConversion($"({expression})", type),
            _ => new RebuildConversion($"CAST({expression} AS {type})"),
        };
        var desired = columns.Where(c => !c.IsInvisible).Select(c => c.Name).ToList();
        var script = ColumnReorderBuilder.Rebuild(
            request.Table, converted, desired, enterprise, usePrimaryKey, dependentDdl ?? string.Empty,
            new Dictionary<string, RebuildConversion>(StringComparer.Ordinal) { [source.Name] = value });

        var table = SqlIdentifier.QuoteAlways(request.Table.Owner) + "." + SqlIdentifier.QuoteAlways(request.Table.Name);
        var tail = new StringBuilder();
        if (!string.Equals(source.Name, target.Name, StringComparison.Ordinal))
        {
            tail.Append(Environment.NewLine).Append("-- 열 이름을 바꾼다").Append(Environment.NewLine)
                .Append("ALTER TABLE " + table + " RENAME COLUMN " + SqlIdentifier.QuoteAlways(source.Name) + " TO " + SqlIdentifier.QuoteAlways(target.Name) + ";")
                .Append(Environment.NewLine);
        }

        if (!string.Equals(source.Comment ?? string.Empty, target.Comment ?? string.Empty, StringComparison.Ordinal))
        {
            tail.Append(Environment.NewLine).Append("-- 열 주석을 바꾼다").Append(Environment.NewLine)
                .Append("COMMENT ON COLUMN " + table + "." + SqlIdentifier.QuoteAlways(target.Name) + " IS " + SqlLiteral.Quote(target.Comment ?? string.Empty) + ";")
                .Append(Environment.NewLine);
        }

        return script + tail;
    }

    /// <summary>문장 끝 표시: 일반 문장 <c>;</c>, PL/SQL 은 다음 줄 <c>/</c>. 문장 안 줄바꿈도 <paramref name="newline"/> 로.</summary>
    internal static string Terminated(SqlStatement statement, string newline)
    {
        var text = statement.Text.Trim().Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Replace("\n", newline, StringComparison.Ordinal);
        return statement.Kind == StatementKind.PlSql ? text + newline + "/" : text.TrimEnd(';') + ";";
    }

    /// <summary>
    /// 3단계 앞 확인 블록: 임시 열이 지금 변환식 값과 다른 행(2단계 COMMIT 뒤 넣거나 바꾼 행)이 하나라도 있으면 ORA-20990 으로 멈춘다.
    /// 비교는 새 형식으로 CAST 한 값과(숫자 반올림·CHAR 채움을 열과 같게), LOB 은 비교할 수 없어 「임시 열이 비었는데 값이 있는 행」만 본다.
    /// </summary>
    private static SqlStatement Guard(string table, string temp, ColumnDraft target, string expression)
    {
        var lob = ColumnTypeText.Family(target.DataType) is "CLOB" or "NCLOB" or "BLOB";
        var differs = lob
            ? $"t.{temp} IS NULL AND (\n{expression}\n) IS NOT NULL"
            : $"DECODE(t.{temp}, CAST((\n{expression}\n) AS {ColumnTypeText.Statement(target)}), 0, 1) = 1";
        var text = "DECLARE\n  n PLS_INTEGER;\nBEGIN\n"
                   + $"  SELECT COUNT(*) INTO n FROM {table} t WHERE ROWNUM = 1 AND {differs};\n"
                   + "  IF n > 0 THEN\n"
                   + "    RAISE_APPLICATION_ERROR(-20990, '형식 변환: 값 복사 뒤 바뀐 행이 있어 옛 열을 지우지 않고 멈춤 - 쓰기를 멈추고 2단계부터 다시 실행');\n"
                   + "  END IF;\nEND;";
        return new SqlStatement(text, default, StatementKind.PlSql) { Verb = "DECLARE" };
    }

    /// <summary>
    /// 5단계: 4단계 뒤(새 형식·NULL 허용·기본값/주석 없음·이 열의 제약/인덱스 없음, 맨 뒤) → 원하는 모습의 차이.
    /// 서버 이름(SYS_C…) 제약은 이름 없이 다시 만들고(서버가 새로 지음), 이름을 바꿨으면 조건·식 안의 열 이름도 바꾼다.
    /// </summary>
    private static IReadOnlyList<SqlStatement> Restore(ColumnConversionRequest request, ColumnDraft target, ITableEditDialect dialect)
    {
        var original = request.Original;
        var source = request.Source;
        var key = request.RowKey;
        var renames = string.Equals(source.Name, target.Name, StringComparison.Ordinal)
            ? null
            : new Dictionary<string, string>(StringComparer.Ordinal) { [source.Name] = target.Name };
        string Rename(string text) => renames is null ? text : TableEditExpression.RenameNames(text, renames, dialect.IsReservedWord);

        var constraints = AffectedConstraints(request).ToList();
        var indexes = original.Indexes.Where(i => !i.IsDropped && UsesColumn(i, key, source.Name)).ToList();

        var swapped = new ColumnDraft(key, target.Name)
        {
            OriginalPosition = source.OriginalPosition,
            DataType = target.DataType,
            Length = target.Length,
            CharSemantics = target.CharSemantics,
            Precision = target.Precision,
            Scale = target.Scale,
            IsInvisible = target.IsInvisible, // 1단계 ADD 가 INVISIBLE 로 만든다
        };
        var after = original with
        {
            Columns = [.. original.Columns.Where(c => c.RowKey != key), swapped],
            Constraints = [.. original.Constraints.Except(constraints)],
            Indexes = [.. original.Indexes.Except(indexes)],
        };
        var desired = after with
        {
            Columns = [.. after.Columns.Select(c => c.RowKey == key
                ? swapped with { IsNullable = target.IsNullable, DefaultValue = target.DefaultValue, Comment = target.Comment }
                : c)],
            Constraints =
            [
                .. after.Constraints,
                .. constraints.Select(k => k with
                {
                    Name = dialect.IsServerName(k.Name) ? string.Empty : k.Name,
                    Condition = k.Condition is null ? null : Rename(k.Condition),
                }),
            ],
            Indexes =
            [
                .. after.Indexes,
                .. indexes.Select(i => i with
                {
                    Columns = [.. i.Columns.Select(c => c.Expression is null ? c : c with { Expression = Rename(c.Expression) })],
                }),
            ],
        };

        return TableEditScriptBuilder.Build(after, desired, dialect).SqlStatements;
    }

    /// <summary>6단계: 이 테이블을 쓰는 객체 다시 컴파일. 패키지 몸은 패키지가 같이 있으면 뺀다. 컴파일 문장이 없는 종류는 건너뛴다.</summary>
    private static List<SqlStatement> Recompile(IReadOnlyList<DbObjectRef> dependents, Func<string, string> q)
    {
        var statements = new List<SqlStatement>();
        var packages = dependents.Where(d => d.ObjectType == "PACKAGE").Select(d => (d.Owner, d.Name)).ToHashSet();
        var types = dependents.Where(d => d.ObjectType == "TYPE").Select(d => (d.Owner, d.Name)).ToHashSet();
        foreach (var d in dependents)
        {
            var name = q(d.Owner) + "." + q(d.Name);
            var text = d.ObjectType switch
            {
                "VIEW" => $"ALTER VIEW {name} COMPILE",
                "MATERIALIZED VIEW" => $"ALTER MATERIALIZED VIEW {name} COMPILE",
                "PROCEDURE" or "FUNCTION" or "TRIGGER" or "PACKAGE" or "TYPE" => $"ALTER {d.ObjectType} {name} COMPILE",
                "PACKAGE BODY" when !packages.Contains((d.Owner, d.Name)) => $"ALTER PACKAGE {name} COMPILE BODY",
                "TYPE BODY" when !types.Contains((d.Owner, d.Name)) => $"ALTER TYPE {name} COMPILE BODY",
                "SYNONYM" when d.Owner == "PUBLIC" => $"ALTER PUBLIC SYNONYM {q(d.Name)} COMPILE",
                "SYNONYM" => $"ALTER SYNONYM {name} COMPILE",
                _ => null,
            };
            if (text is not null)
            {
                statements.Add(Ddl(text, "ALTER"));
            }
        }

        return statements;
    }

    private static bool UsesColumn(IndexDraft index, string key, string column) =>
        index.Columns.Any(c => c.ColumnKey == key || Uses(c.Expression, column));

    /// <summary>식이 이 열 이름을 쓰나(이름 낱말 비교 — 함수 이름과 겹치면 잘못 볼 수 있으나 더 조심하는 쪽).</summary>
    private static bool Uses(string? expression, string column) => TableEditExpression.Names(expression).Contains(column, StringComparer.Ordinal);

    private static CheckFinding Invalid(string key, string subject, string detail) =>
        new(CheckSeverity.Block, CheckCode.InvalidDefinition, key) { Subject = subject, Detail = detail };

    private static SqlStatement Ddl(string text, string verb) => new(text, default, StatementKind.Ddl) { Verb = verb };
}
