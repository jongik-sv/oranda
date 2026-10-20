using System.Globalization;
using System.Text;
using Oranda.App.Features.ConnectionManager;
using Oranda.App.Features.ResultGrid;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell.Composition;
using Oranda.Core.Events;
using Oranda.Core.Execution;
using Oranda.Core.Grid;
using Oranda.Core.History;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;
using Oranda.Language.Editing;

namespace Oranda.App.Features.Execution;

/// <summary>
/// 외래키 따라가기(GRID-11)의 실행 쪽 — PL/SQL Developer 「Linked Query」, DataGrip 「Related Rows」(F4), DBeaver 참조 패널.
/// 활성 칸의 열이 어느 테이블 열인지는 그리드 편집과 같은 규칙(<see cref="RowEditPlanner.MapColumns"/>, 분석하지 못하면 드라이버가 알려 준 출처)으로 찾고,
/// 메타데이터 제공자의 제약 정보로 부모(외래키가 참조하는 키) 또는 자식(이 테이블의 키를 참조하는 외래키, 객체 상세 「참조됨」)을 정해
/// <c>SELECT * FROM "OWNER"."TABLE" WHERE "COL" = :v1 [AND …]</c> 을 행 값 바인드로 실행 엔진(치환 변수 → 정책 관문 → 드라이버)에 보낸다.
/// 앱이 대신 실행하는 질의라 이력에는 남기지 않는다. 메타데이터를 읽고 실행하는 동안 세션을 「실행 중」으로 둔다(외래키를 고르는 동안은 아니다).
/// 실패는 이유를 담은 예외, 취소는 <see cref="OperationCanceledException"/>, 외래키 고르기를 취소하면 null.
/// </summary>
public sealed class RelatedRowsRunner(ExecutionState state, Func<ISession?> session, Func<IExecutionService> execution,
                                      Func<IMetadataProvider?> metadata, Func<string, IReadOnlyList<string>, CancellationToken, Task<string?>> choose)
{
    /// <summary>앱이 대신 실행하는 도우미 질의라 이력에 남기지 않는다(정책 관문은 그대로 거친다).</summary>
    private static readonly ExecutionOptions Options = new() { Source = HistorySource.Editor, SkipHistory = true };

    /// <summary>객체 상세에서 이 테이블의 키를 참조하는 외래키 묶음의 제목(<c>meta.detail_referenced_by</c>).</summary>
    internal const string ReferencedBySection = "참조됨";

    /// <summary>키 값 바인드 이름 앞머리(<c>:v1</c>, <c>:v2</c> …).</summary>
    internal const string BindPrefix = "v";

    /// <summary>
    /// 앱 조립용: 세션·실행 엔진·메타데이터 제공자는 부를 때 해석하고(시작 때 Oracle 어셈블리를 깨우지 않게), 상태는 버스마다 하나인 것을 쓴다.
    /// 외래키가 여럿이면 목록 대화상자로 고른다.
    /// </summary>
    public static RelatedRowsRunner Create(ServiceContainer container)
    {
        ArgumentNullException.ThrowIfNull(container);
        var state = ExecutionState.For(container.GetService<IEventBus>());
        return new RelatedRowsRunner(
            state,
            () => ConnectionManagerCommands.Resolve(container).ActiveSession,
            () =>
            {
                ExecutionCommands.EnsureEngineServices(container, state);
                return container.GetRequiredService<IExecutionService>();
            },
            () => container.GetService<IMetadataProvider>(),
            (title, items, ct) => new AvaloniaEditorDialogs().ChooseAsync(title, items, ct));
    }

    /// <summary>요청한 행의 부모 또는 자식 행을 조회한다. 받은 결과 집합의 수명은 부른 쪽이 가진다.</summary>
    public async Task<RelatedRowsResult?> OpenAsync(RelatedRowsRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Query.Kind != StatementKind.Query)
        {
            throw new InvalidOperationException("질의(SELECT) 결과에서만 외래키를 따라갈 수 있습니다.");
        }

        if (request.Ordinal < 0 || request.Ordinal >= request.Columns.Count || request.Row.Count != request.Columns.Count)
        {
            throw new ArgumentException("열 번호나 행 값이 결과 열과 맞지 않습니다.", nameof(request));
        }

        var s = Check();
        var meta = metadata() ?? throw new InvalidOperationException("메타데이터 서비스가 없어 외래키를 찾을 수 없습니다.");
        var source = ResolveTable(request, (s.Server.SessionUser ?? s.Profile.User).ToUpperInvariant());
        return request.Kind == RelatedRowsKind.Parent
            ? await OpenParentAsync(request, s, meta, source, ct).ConfigureAwait(true)
            : await OpenChildrenAsync(request, s, meta, source, ct).ConfigureAwait(true);
    }

    /// <summary>
    /// 활성 칸 열이 속한 테이블과, 결과 열마다 그 테이블의 어느 열인지(아니면 null). 그리드 편집처럼 FROM 절을 읽어 맞추고,
    /// 활성 칸이 식이면 단일 테이블 결과의 그 테이블을 쓴다. 분석하지 못한 질의(WITH·DISTINCT·GROUP BY 등)는 드라이버가 알려 준 출처를 쓴다.
    /// 스키마를 적지 않은 테이블은 <paramref name="defaultOwner"/>(현재 사용자).
    /// </summary>
    internal static SourceTable ResolveTable(RelatedRowsRequest request, string defaultOwner)
    {
        ArgumentNullException.ThrowIfNull(request);
        var cols = request.Columns;
        var shape = SelectAnalyzer.Analyze(request.Query.Text);
        if (shape.Block == EditBlockReason.None && shape.Tables.Count > 0)
        {
            var sources = RowEditPlanner.MapColumns(shape, cols);
            var table = sources[request.Ordinal]?.Table
                        ?? (shape.Tables.Count == 1 && !shape.Tables[0].IsInlineView ? shape.Tables[0] : null);
            if (table is not null)
            {
                if (table.IsRemote)
                {
                    throw new InvalidOperationException("DB 링크 테이블은 외래키를 따라갈 수 없습니다.");
                }

                var names = sources.Select(x => x is not null && ReferenceEquals(x.Table, table) && x.Column != "ROWID" ? x.Column : null).ToArray();
                return new SourceTable(new DbObjectRef(table.Owner ?? defaultOwner, table.Name!, "TABLE"), names);
            }
        }

        var col = cols[request.Ordinal];
        if (col.BaseTable is { Length: > 0 } baseTable && col.BaseColumn is { Length: > 0 })
        {
            var owner = col.BaseOwner is { Length: > 0 } o ? o : defaultOwner;
            var names = cols.Select(c => c.BaseColumn is { Length: > 0 } bc
                                         && string.Equals(c.BaseTable, baseTable, StringComparison.Ordinal)
                                         && (c.BaseOwner is not { Length: > 0 } || string.Equals(c.BaseOwner, owner, StringComparison.Ordinal))
                    ? bc
                    : null)
                .ToArray();
            return new SourceTable(new DbObjectRef(owner, baseTable, "TABLE"), names);
        }

        throw new InvalidOperationException($"열 {col.Name} 이(가) 어느 테이블의 열인지 알 수 없습니다. 테이블 열 칸에서 하세요.");
    }

    /// <summary>
    /// 부모 행: 활성 칸 열이 든 외래키(여럿이면 고른다)의 열 값을 바인드로 묶어 참조하는 키로 부모 테이블을 조회한다.
    /// 외래키 열 중 하나라도 NULL 이면 따라갈 부모가 없다(Oracle 은 그런 행의 외래키를 검사하지 않는다).
    /// </summary>
    private async Task<RelatedRowsResult?> OpenParentAsync(RelatedRowsRequest request, ISession s, IMetadataProvider meta, SourceTable source,
                                                           CancellationToken ct)
    {
        var column = source.Names[request.Ordinal]
                     ?? throw new InvalidOperationException($"열 {request.Columns[request.Ordinal].Name} 은(는) {RowEditPlanner.Display(source.Table)} 의 열이 아닙니다.");
        var info = await BusyAsync(() => meta.GetTableAsync(s, source.Table, ct)).ConfigureAwait(true);
        var fks = info.Constraints
            .Where(c => c.Kind == ConstraintKind.ForeignKey && c.ReferencedTable is not null && c.Columns.Count > 0
                        && c.Columns.Contains(column, StringComparer.OrdinalIgnoreCase))
            .ToList();
        if (fks.Count == 0)
        {
            throw new InvalidOperationException($"열 {column} 은(는) {RowEditPlanner.Display(source.Table)} 의 외래키 열이 아닙니다.");
        }

        var picked = fks.Count == 1 ? 0 : await ChooseAsync("따라갈 외래키 고르기",
            [.. fks.Select(c => $"{c.Name} → {RowEditPlanner.Display(c.ReferencedTable!)} ({string.Join(", ", c.Columns)})")], ct).ConfigureAwait(true);
        if (picked < 0)
        {
            return null;
        }

        var fk = fks[picked];
        var binds = BindValues(request, source.Names, fk.Columns, $"외래키 {fk.Name} 의 열",
            $"외래키 {fk.Name} 값이 NULL 이라 따라갈 부모 행이 없습니다.");
        var parent = fk.ReferencedTable!;
        return await BusyAsync(async () =>
        {
            var parentInfo = await meta.GetTableAsync(s, parent, ct).ConfigureAwait(true);
            var key = ReferencedKey(parentInfo, fk)
                      ?? throw new InvalidOperationException($"{RowEditPlanner.Display(parent)} 에서 외래키 {fk.Name} 이(가) 참조하는 키를 찾지 못했습니다.");
            var stmt = BuildQuery(parent, key.Columns);
            var rs = await ExecuteAsync(s, stmt, binds, ct).ConfigureAwait(true);
            return new RelatedRowsResult(rs, "부모: " + parent.Name, stmt, binds);
        }).ConfigureAwait(true);
    }

    /// <summary>
    /// 자식 행: 이 테이블의 기본키·유일 키를 참조하는 외래키(객체 상세 「참조됨」) 중 참조되는 키 열이 모두 결과에 있는 것을 고르고(여럿이면 묻는다),
    /// 이 행의 키 값을 바인드로 묶어 자식 테이블을 그 외래키 열로 조회한다. 외래키 열과 키 열은 위치 순서로 짝을 이룬다.
    /// </summary>
    private async Task<RelatedRowsResult?> OpenChildrenAsync(RelatedRowsRequest request, ISession s, IMetadataProvider meta, SourceTable source,
                                                             CancellationToken ct)
    {
        var (info, detail) = await BusyAsync(async () =>
        {
            var table = await meta.GetTableAsync(s, source.Table, ct).ConfigureAwait(true);
            var objectDetail = await meta.GetObjectDetailAsync(s, source.Table, ct).ConfigureAwait(true);
            return (table, objectDetail);
        }).ConfigureAwait(true);
        var display = RowEditPlanner.Display(source.Table);
        var referencing = ReadReferencedBy(detail);
        if (referencing.Count == 0)
        {
            throw new InvalidOperationException($"{display} 을(를) 참조하는 외래키가 없습니다.");
        }

        var usable = new List<(ChildForeignKey Fk, ConstraintInfo Key)>();
        var missing = new List<string>();
        foreach (var fk in referencing)
        {
            var key = info.Constraints.FirstOrDefault(c => c.Kind is ConstraintKind.PrimaryKey or ConstraintKind.Unique
                                                           && string.Equals(c.Name, fk.KeyName, StringComparison.Ordinal));
            if (key is null || key.Columns.Count != fk.Columns.Count || key.Columns.Count == 0)
            {
                continue;
            }

            var absent = key.Columns.Where(c => IndexOf(source.Names, c) < 0).ToList();
            if (absent.Count == 0)
            {
                usable.Add((fk, key));
            }
            else
            {
                missing.AddRange(absent);
            }
        }

        if (usable.Count == 0)
        {
            throw new InvalidOperationException(missing.Count > 0
                ? $"결과에 {display} 의 키 열 {string.Join(", ", missing.Distinct(StringComparer.Ordinal))} 이(가) 없어 참조하는 행을 찾을 수 없습니다."
                : $"{display} 을(를) 참조하는 외래키가 쓰는 키를 찾지 못했습니다.");
        }

        var picked = usable.Count == 1 ? 0 : await ChooseAsync("참조하는 외래키 고르기",
            [.. usable.Select(u => $"{u.Fk.Owner}.{u.Fk.Table}({u.Fk.Name}): {string.Join(", ", u.Fk.Columns)}")], ct).ConfigureAwait(true);
        if (picked < 0)
        {
            return null;
        }

        var (child, referenced) = usable[picked];
        var binds = BindValues(request, source.Names, referenced.Columns, $"키 {referenced.Name} 의 열",
            $"키 {referenced.Name} 값이 NULL 이라 이 행을 참조하는 행이 없습니다.");
        var stmt = BuildQuery(new DbObjectRef(child.Owner, child.Table, "TABLE"), child.Columns);
        return await BusyAsync(async () =>
        {
            var rs = await ExecuteAsync(s, stmt, binds, ct).ConfigureAwait(true);
            return new RelatedRowsResult(rs, $"자식: {child.Table}({child.Name})", stmt, binds);
        }).ConfigureAwait(true);
    }

    /// <summary>
    /// <c>SELECT * FROM "OWNER"."TABLE" WHERE "C1" = :v1 AND "C2" = :v2 …</c>. 이름은 사전에 적힌 그대로 큰따옴표로 감싸고,
    /// 값은 본문에 넣지 않고 바인드(<see cref="BindName"/>)로만 넘긴다.
    /// </summary>
    internal static SqlStatement BuildQuery(DbObjectRef table, IReadOnlyList<string> columns)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(columns);
        if (columns.Count == 0)
        {
            throw new InvalidOperationException("조건으로 쓸 키 열이 없습니다.");
        }

        var text = new StringBuilder("SELECT * FROM ").Append(Quote(table.Owner)).Append('.').Append(Quote(table.Name)).Append(" WHERE ");
        var names = new List<string>(columns.Count);
        for (var i = 0; i < columns.Count; i++)
        {
            if (i > 0)
            {
                text.Append(" AND ");
            }

            var bind = BindName(i);
            text.Append(Quote(columns[i])).Append(" = :").Append(bind);
            names.Add(bind);
        }

        return new SqlStatement(text.ToString(), default, StatementKind.Query) { Verb = "SELECT", BindNames = names };
    }

    /// <summary>키 값 바인드 이름: 0 → <c>v1</c>.</summary>
    internal static string BindName(int index) => BindPrefix + (index + 1).ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// 부모 테이블에서 외래키가 참조하는 키. 참조 키 이름(<see cref="ConstraintInfo.ReferencedConstraint"/>)을 알면 그 이름으로 찾고,
    /// 모르면(옛 캐시) 열 수가 같은 기본키, 없으면 열 수가 같은 하나뿐인 유일 키. 못 찾으면 null.
    /// </summary>
    internal static ConstraintInfo? ReferencedKey(TableInfo parent, ConstraintInfo fk)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(fk);
        var keys = parent.Constraints
            .Where(c => c.Kind is ConstraintKind.PrimaryKey or ConstraintKind.Unique && c.Columns.Count == fk.Columns.Count)
            .ToList();
        if (fk.ReferencedConstraint is { } name)
        {
            return keys.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.Ordinal));
        }

        return keys.FirstOrDefault(c => c.Kind == ConstraintKind.PrimaryKey) ?? (keys.Count == 1 ? keys[0] : null);
    }

    /// <summary>
    /// 객체 상세의 「참조됨」 묶음(소유자·테이블·외래키·열·참조 키 …)을 읽는다. 열 이름 목록은 위치 순서로 <c>", "</c> 로 이어져 있다.
    /// </summary>
    internal static List<ChildForeignKey> ReadReferencedBy(Oranda.Core.Metadata.ObjectDetail detail)
    {
        ArgumentNullException.ThrowIfNull(detail);
        var section = detail.Sections.FirstOrDefault(x => string.Equals(x.Title, ReferencedBySection, StringComparison.Ordinal));
        if (section is null)
        {
            return [];
        }

        var headers = section.Columns.ToList();
        int At(string header, int fallback) => headers.IndexOf(header) is var i && i >= 0 ? i : fallback;
        int owner = At("소유자", 0), table = At("테이블", 1), name = At("외래키", 2), columns = At("열", 3), key = At("참조 키", 4);
        var list = new List<ChildForeignKey>();
        foreach (var row in section.Rows)
        {
            if (Text(row, owner) is { } o && Text(row, table) is { } t && Text(row, name) is { } n
                && Text(row, columns) is { } c && Text(row, key) is { } k)
            {
                list.Add(new ChildForeignKey(o, t, n, c.Split(", ", StringSplitOptions.RemoveEmptyEntries), k));
            }
        }

        return list;

        static string? Text(IReadOnlyList<object?> row, int index) =>
            index < row.Count && row[index] is { } v && Convert.ToString(v, CultureInfo.InvariantCulture) is { Length: > 0 } text ? text : null;
    }

    /// <summary>
    /// 키 열마다 결과 열을 찾아 그 행 값을 바인드(<c>v1</c>…, 열 순서대로)로 만든다. 결과에 열이 없거나 값이 NULL 이거나
    /// 바인드로 넘길 수 없는 형식(LOB 등)이면 이유를 담은 예외.
    /// </summary>
    private static Dictionary<string, BindValue> BindValues(RelatedRowsRequest request, string?[] names, IReadOnlyList<string> keyColumns,
                                                           string what, string nullMessage)
    {
        var binds = new Dictionary<string, BindValue>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < keyColumns.Count; i++)
        {
            var ordinal = IndexOf(names, keyColumns[i]);
            if (ordinal < 0)
            {
                throw new InvalidOperationException($"결과에 {what} {keyColumns[i]} 이(가) 없습니다.");
            }

            var value = request.Row[ordinal];
            if (value is null or DBNull)
            {
                throw new InvalidOperationException(nullMessage);
            }

            var column = request.Columns[ordinal];
            binds[BindName(i)] = ResultFilter.ToBind(column, value)
                                 ?? throw new InvalidOperationException($"{column.DbTypeName} 열 {column.Name} 값은 바인드로 넘길 수 없습니다.");
        }

        return binds;
    }

    /// <summary>테이블 열 이름의 결과 열 번호(같은 글자 먼저, 없으면 대소문자 무시). 없으면 -1.</summary>
    private static int IndexOf(string?[] names, string column)
    {
        var exact = Array.FindIndex(names, n => string.Equals(n, column, StringComparison.Ordinal));
        return exact >= 0 ? exact : Array.FindIndex(names, n => string.Equals(n, column, StringComparison.OrdinalIgnoreCase));
    }

    private static string Quote(string name) =>
        string.IsNullOrEmpty(name) || name.Contains('"', StringComparison.Ordinal) || name.Contains('\0', StringComparison.Ordinal)
            ? throw new InvalidOperationException($"이름 {name} 은(는) SQL 에 쓸 수 없습니다.")
            : "\"" + name + "\"";

    /// <summary>목록에서 하나 고르기. 고른 번호, 취소하면 -1.</summary>
    private async Task<int> ChooseAsync(string title, List<string> labels, CancellationToken ct)
    {
        var picked = await choose(title, labels, ct).ConfigureAwait(true);
        return picked is null ? -1 : labels.IndexOf(picked);
    }

    /// <summary>세션을 「실행 중」으로 두고 일을 한다(다른 실행과 겹치지 않게, 시작 전에 접속·바쁨을 다시 본다).</summary>
    private async Task<T> BusyAsync<T>(Func<Task<T>> body)
    {
        Check();
        state.BeginRun();
        try
        {
            return await body().ConfigureAwait(true);
        }
        finally
        {
            state.EndRun();
        }
    }

    private ISession Check()
    {
        if (session() is not { } s)
        {
            throw new InvalidOperationException("접속이 없습니다. 접속한 뒤 다시 하세요.");
        }

        if (state.RunState == SessionRunState.Broken)
        {
            throw new InvalidOperationException("세션이 끊겼습니다. 다시 접속한 뒤 하세요.");
        }

        if (state.Busy)
        {
            throw new InvalidOperationException("다른 문장을 실행하는 중입니다. 끝난 뒤 다시 하세요.");
        }

        return s;
    }

    private async Task<IResultSet> ExecuteAsync(ISession s, SqlStatement stmt, Dictionary<string, BindValue> binds, CancellationToken ct)
    {
        var exec = execution();
        var result = await Task.Run(() => exec.ExecuteAsync(s, stmt, binds, Options, ct), CancellationToken.None).ConfigureAwait(true);
        return await GridFilterRunner.TakeResultAsync(result, ct, "질의가 결과를 돌려주지 않았습니다.").ConfigureAwait(true);
    }
}

/// <summary>활성 칸 열이 속한 테이블과, 결과 열 번호마다 그 테이블의 열 이름(그 테이블 열이 아니면 null).</summary>
internal sealed record SourceTable(DbObjectRef Table, string?[] Names);

/// <summary>이 테이블의 키를 참조하는 외래키 1개(객체 상세 「참조됨」 한 줄). <see cref="KeyName"/> 은 참조되는 키(이 테이블의 기본키·유일 키) 이름.</summary>
internal sealed record ChildForeignKey(string Owner, string Table, string Name, IReadOnlyList<string> Columns, string KeyName);
