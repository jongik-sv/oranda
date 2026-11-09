using Oranda.Core.Metadata;
using Oranda.Core.Sessions;

namespace Oranda.Tibero.Schema;

/// <summary>
/// 사전 뷰로 만드는 DDL(T3 D2b): 뷰·시퀀스·시노님·인덱스, 트리거 상태 문장, 표의 종속 객체. 글 조립은 <see cref="TiberoDdlAssembler"/> 가 한다.
/// 질의는 되도록 이미 있는 카탈로그(<c>meta.columns</c>·<c>meta.name_step</c>·<c>meta.table_comment</c>·<c>meta.detail_privileges(_user)</c>·
/// <c>meta.detail_triggers</c>·<c>meta.source</c>)를 다시 쓰고, 없는 것만 <c>meta.ddl_*</c> 로 더했다.
/// </summary>
internal sealed partial class TiberoMetadataProvider
{
    private async Task<string> GetViewDdlAsync(ISession s, DbObjectRef obj, DdlOptions o, CancellationToken ct)
    {
        var binds = TiberoCatalogQuery.TableBinds(obj);
        var rows = await RunAsync(s, "meta.ddl_view", binds, ct).ConfigureAwait(false);
        if (rows.Count == 0 || Str(rows[0][0]) is not { } text || string.IsNullOrWhiteSpace(text))
        {
            throw DdlNotFound(obj);
        }

        var columns = await RunAsync(s, "meta.columns", binds, ct).ConfigureAwait(false);
        return TiberoDdlAssembler.View(obj.Owner, obj.Name, [.. columns.Select(r => Str(r[0])!)], text, o.SqlTerminator);
    }

    private async Task<string> GetSequenceDdlAsync(ISession s, DbObjectRef obj, DdlOptions o, CancellationToken ct)
    {
        var rows = await RunAsync(s, "meta.ddl_sequence", TiberoCatalogQuery.TableBinds(obj), ct).ConfigureAwait(false);
        return rows.Count == 0 ? throw DdlNotFound(obj) : TiberoDdlAssembler.Sequence(obj.Owner, obj.Name, rows[0], o.SqlTerminator);
    }

    /// <summary>시노님(PUBLIC 소유자 포함): <c>meta.name_step</c> 의 SYNONYM 행(kind, table_owner, table_name, db_link)이 가리키는 곳.</summary>
    private async Task<string> GetSynonymDdlAsync(ISession s, DbObjectRef obj, DdlOptions o, CancellationToken ct)
    {
        var rows = await RunAsync(s, "meta.name_step", TiberoCatalogQuery.TableBinds(obj), ct).ConfigureAwait(false);
        var row = rows.FirstOrDefault(r => string.Equals(Str(r[0]), "SYNONYM", StringComparison.Ordinal));
        return row is null || Str(row[2]) is not { Length: > 0 } target
            ? throw DdlNotFound(obj)
            : TiberoDdlAssembler.Synonym(obj.Owner, obj.Name, Str(row[1]), target, o.SqlTerminator);
    }

    /// <summary>인덱스: <c>meta.ddl_index</c>(머리·열). 만들 수 없는 인덱스(도메인·파티션 등)면 이유를 담은 <see cref="NotSupportedException"/>.</summary>
    private async Task<string> GetIndexDdlAsync(ISession s, DbObjectRef obj, DdlOptions o, CancellationToken ct)
    {
        var rows = await RunAsync(s, "meta.ddl_index", TiberoCatalogQuery.TableBinds(obj), ct).ConfigureAwait(false);
        var index = TiberoDdlAssembler.Indexes(rows).FirstOrDefault() ?? throw DdlNotFound(obj);
        return TiberoDdlAssembler.IndexRefusal(index) is { } reason
            ? throw new NotSupportedException(reason)
            : TiberoDdlAssembler.Index(index, o.SqlTerminator);
    }

    /// <summary>트리거: 소스 글 생성 문장 + Oracle 처럼 <c>ALTER TRIGGER … ENABLE|DISABLE</c>(<c>meta.ddl_trigger</c> 의 status, 행이 없으면 상태 문장 없음).</summary>
    private async Task<string> GetTriggerDdlAsync(ISession s, DbObjectRef obj, DdlOptions o, CancellationToken ct)
    {
        var create = await SourceDdlAsync(s, obj, "TRIGGER", ct).ConfigureAwait(false);
        var status = await RunAsync(s, "meta.ddl_trigger", TiberoCatalogQuery.TableBinds(obj), ct).ConfigureAwait(false);
        return TiberoDdlAssembler.Trigger(create, obj.Owner, obj.Name, status.Count == 0 ? null : Str(status[0][0]), o.SqlTerminator);
    }

    /// <summary>
    /// 표 DDL 뒤에 종속 문장을 Oracle <c>meta.ddl</c> 의 GET_DEPENDENT_DDL 순서(INDEX, CONSTRAINT, REF_CONSTRAINT, COMMENT, OBJECT_GRANT, TRIGGER)로 붙인다.
    /// 앞부분은 종속 객체 없는 표 DDL 과 같은 글이라 <c>ColumnReorderScripts.DependentPart</c> 가 그대로 떼어 낸다. 종속 부분은 Oracle 처럼 그것만으로 완전하다
    /// (표 DDL 글에 같은 제약·인덱스·주석이 있어도 빼지 않는다 — CTAS 복사본에 종속 부분만 다시 실행하는 열 순서 바꾸기가 기댄다).
    /// <list type="bullet">
    /// <item>인덱스: <c>meta.ddl_table_indexes</c>((소유자, 이름) 으로 묶음). 기본키·유일 키가 만든 인덱스(<see cref="TiberoDdlAssembler.IsConstraintIndex"/>, 사전 정보만 본다)와
    /// LOB 인덱스는 뺀다. 파티션 인덱스 등 만들 수 없는 것은 주석 줄.</item>
    /// <item>제약: <c>meta.ddl_constraints</c>(기본키·유일 키·검사 → 외래키). 진짜 NOT NULL 검사만 뺀다.</item>
    /// <item>권한: dba 권한이 있으면 <c>meta.detail_privileges</c>, 없으면 <c>meta.detail_privileges_user</c>(객체 상세와 같은 고름).</item>
    /// </list>
    /// </summary>
    private async Task<string> WithTableDependentsAsync(ISession s, DbObjectRef obj, string tableDdl, bool terminator, CancellationToken ct)
    {
        var binds = TiberoCatalogQuery.TableBinds(obj);
        var statements = new List<string>();

        var constraints = TiberoDdlAssembler.Constraints(await RunAsync(s, "meta.ddl_constraints", binds, ct).ConfigureAwait(false));
        var indexes = TiberoDdlAssembler.Indexes(await RunAsync(s, "meta.ddl_table_indexes", binds, ct).ConfigureAwait(false));
        foreach (var index in indexes.Where(i => !TiberoDdlAssembler.HasTypeWord(i.IndexType, "LOB") && !TiberoDdlAssembler.IsConstraintIndex(i, constraints)))
        {
            statements.Add(TiberoDdlAssembler.IndexRefusal(index) is { } reason ? "-- " + reason : TiberoDdlAssembler.Index(index, terminator));
        }

        statements.AddRange(TiberoDdlAssembler.ConstraintStatements(obj.Owner, obj.Name, constraints, terminator));

        var tableComment = await RunAsync(s, "meta.table_comment", binds, ct).ConfigureAwait(false);
        var columnRows = await RunAsync(s, "meta.columns", binds, ct).ConfigureAwait(false);
        statements.AddRange(TiberoDdlAssembler.Comments(obj.Owner, obj.Name, tableComment.Count > 0 ? Str(tableComment[0][0]) : null,
            [.. columnRows.Where(r => Str(r[0]) is not null).Select(r => (Str(r[0])!, Str(r[12])))], terminator));

        var privileges = await RunAsync(s, s.Server.Capabilities.CanQueryDictionary ? "meta.detail_privileges" : "meta.detail_privileges_user", binds, ct)
            .ConfigureAwait(false);
        statements.AddRange(TiberoDdlAssembler.Grants(obj.Owner, obj.Name, privileges, terminator));

        // meta.detail_triggers 열: trigger_name, trigger_type, triggering_event, status, when_clause, owner
        var triggers = await RunAsync(s, "meta.detail_triggers", binds, ct).ConfigureAwait(false);
        foreach (var t in triggers)
        {
            if (Str(t[0]) is not { } name || Str(t[5]) is not { } owner)
            {
                continue;
            }

            var lines = await RunAsync(s, "meta.source", ObjectBinds(owner, name, "TRIGGER"), ct).ConfigureAwait(false);
            statements.Add(TiberoTriggerDdl.FromSource(owner, name, [.. lines.Select(r => Str(r[0]))]) is { } create
                ? TiberoDdlAssembler.Trigger(create, owner, name, Str(t[3]), terminator)
                : $"-- 트리거 {owner}.{name} 의 소스를 읽지 못해 DDL 을 만들지 못했다.");
        }

        return TiberoDdlAssembler.WithDependents(tableDdl, statements);
    }
}
