using System.Globalization;
using Oranda.Core.Execution;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;
using Oranda.Core.Text;

namespace Oranda.Core.Transfer.Copy;

/// <summary>
/// 복사 실행기의 표 만들기 쪽(xc-ddl): 테이블 다시 만들기·없으면 만들기·만든 표 정리·되살리기, 그리고 표보다 먼저 만드는 시퀀스(SQ1)와
/// 새 표에 딸린 객체(SQ2). 적재 루프(<c>LoadAllAsync</c>·<c>FlushAsync</c>)는 <c>CopyRunner.cs</c> 에 있다.
/// </summary>
public partial class CopyRunner
{
    // ---- 1a. 테이블 다시 만들기(설계 6.2.3, 개발 대상만) ----

    /// <summary>
    /// 준비(ct 사용, 하나라도 실패하면 아무것도 지우지 않고 멈춤): 표마다 보존 DDL 받기, CREATE 문장(FK 없이), 원본 카탈로그로 작업 안 FK 문장,
    /// 휴지통 목록 → 보존 DDL·지울 표 목록을 실행 기록(디스크)에 쓰기 → 이 세션의 휴지통 켜기.
    /// 실행(첫 파괴적 문장 뒤에는 ct 로 끊지 않음): 자식 → 부모 순서로 표마다 참조 FK 떼어 내기 → DROP(CASCADE 없이) →
    /// 부모 → 자식 CREATE. 작업 안 FK 는 적재 뒤 <see cref="RestoreAsync"/> 가 붙인다(자기 참조·서로 참조 표에서 자식 행이 부모 행보다 먼저 와도 실패하지 않게).
    /// 트리거·권한·참조 FK 되살리기는 대상 트랜잭션을 끝낸 뒤 <see cref="RestoreAsync"/> 가 한다(DDL 이 적재분을 확정하지 않게).
    /// </summary>
    private async Task RecreateTablesAsync(RunState run, CancellationToken ct)
    {
        var plans = run.Report.Order.Where(p => p.Rule.Action == TargetAction.Recreate).ToList();
        if (plans.Count == 0)
        {
            return;
        }

        var step = _recreate; // 대상 쪽 받아 두기·휴지통. 원본 FK 는 원본 벌(_sourceRecreate)이 읽는다.
        var noForeignKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var plan in plans)
        {
            var table = run.Table(plan);
            table.Recreate = true;
            run.Progress(table.Name, 0, null, PhaseRecreate);
            // 표마다 그 표의 대상 계정에 다시 만든다. 작업 안 FK 는 원본 계정·대상 계정이 모두 같은 다시 만들기 표끼리만 붙인다(사전 점검과 같은 판정).
            var owner = plan.Target.Table.Owner;
            var sourceOwner = plan.Source.Table.Owner;
            var planned = BuiltPlans(run).Where(p => CopyOwners.SameOwner(p.Target.Table.Owner, owner)
                                                      && CopyOwners.SameOwner(p.Source.Table.Owner, sourceOwner))
                .Select(p => p.Rule.Table).ToHashSet(StringComparer.Ordinal);
            try
            {
                table.Preserved = await step.CaptureAsync(run.Target, owner, plan.Rule.Table, ct);
                // 작업 안 FK 는 CreateTableDdl 대신 원본 카탈로그로 만든다(ON DELETE·DEFERRABLE·UK 참조를 원본대로, 조정 결정 16).
                // 서버가 지은 제약 이름(Oracle SYS_C…, Tibero <소유자>_CON<숫자>)은 명시하지 않고 대상 서버가 새로 짓게 둔다.
                // 서버가 지은 이름인지는 원본 이름을 보는 판정이라 원본 방언이 한다.
                var built = BuildCreate(plan, owner, noForeignKeys);
                table.CreateDdl = built.Statements;
                table.AfterLoadDdl = built.AfterLoad;
                table.JobForeignKeys = JobForeignKeys(run, plan, await _sourceRecreate.SourceForeignKeysAsync(run.Source, sourceOwner, plan.Rule.Table, planned, owner, ct), built, out var fkNote);
                if (CrossNote(built) is { } crossNote)
                {
                    table.Note(crossNote);
                }

                if (fkNote is not null)
                {
                    table.Note(fkNote);
                }

                table.BinBefore = await step.RecycleBinAsync(run.Target, plan.Rule.Table, ct);
                run.Log($"지우기 전에 받아 둠: 트리거 {table.Preserved.Triggers.Count.ToString(CultureInfo.InvariantCulture)}개 · 권한 {table.Preserved.Grants.Count.ToString(CultureInfo.InvariantCulture)}개"
                        + $" · 참조 FK {table.Preserved.ReferencingForeignKeys.Count.ToString(CultureInfo.InvariantCulture)}개 · 작업 안 FK {table.JobForeignKeys.Count.ToString(CultureInfo.InvariantCulture)}개");
            }
            catch (Exception ex) when (!ct.IsCancellationRequested
                                       && (CopyErrors.IsServer(_errors, ex) || ex is InvalidOperationException or BulkDeniedException or ArgumentException))
            {
                table.Fail("지우기 전에 트리거·권한·참조 FK 를 받아 두거나 CREATE 문장을 만들지 못했습니다: " + SafeError(ex.Message));
                run.Abort($"{table.Name}: 다시 만들 준비를 하지 못해 아무 테이블도 지우지 않고 멈췄습니다.");
                return;
            }
        }

        // 앱이 DROP 뒤에 죽어도 참조 FK 정의가 남게, 첫 파괴적 문장 전에 디스크에 쓴다(Review Focus 3 과 같은 원리).
        // 다른 기록 호출과 달리 예외를 가두고 넘어가지 않는다: 쓰지 못했으면 지우지 않는다.
        try
        {
            await RecordPreservedAsync(run, plans, ct);
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
        {
            run.Abort("실행 기록에 보존 DDL 을 쓰지 못해 아무 테이블도 지우지 않고 멈췄습니다: " + SafeError(ex.Message));
            return;
        }

        // DB 기본이 휴지통 꺼짐이면 PURGE 없이도 영구 삭제된다: 이 세션에서 켠다(묵시 커밋 없음).
        var bin = await ExecuteLoggedAsync(run, run.Target, _dialect.RecycleBinOn(), NoBinds, ct);
        if (bin.Status != ExecutionStatus.Succeeded)
        {
            foreach (var plan in plans)
            {
                run.Table(plan).Fail("휴지통을 켜지 못해 테이블을 지우지 않았습니다: " + Describe(bin));
            }

            run.Abort("휴지통을 켜지 못해 아무 테이블도 지우지 않고 멈췄습니다.");
            return;
        }

        ct.ThrowIfCancellationRequested();
        for (var i = plans.Count - 1; i >= 0; i--)
        {
            var table = run.Table(plans[i]);
            run.Progress(table.Name, 0, null, PhaseRecreate);
            if (!await DetachReferencingAsync(run, table) || !await DropAsync(run, step, table))
            {
                break;
            }
        }

        foreach (var plan in plans.Where(p => run.Table(p).Dropped))
        {
            var table = run.Table(plan);
            run.Progress(table.Name, 0, null, PhaseRecreate);
            await CreateSequencesAsync(run, plan, table, plan.Target.Table.Owner);
            var statements = table.CreateDdl;
            var create = await CleanupStatementAsync(run, CopyCommonStatements.RecreateDdl(statements[0]), CleanupTimeout);
            if (create.Status != ExecutionStatus.Succeeded)
            {
                table.Fail("지운 테이블을 다시 만들지 못했습니다: " + Describe(create));
                await FlashbackDroppedAsync(run, table);
                continue;
            }

            table.Created = true;
            foreach (var text in statements.Skip(1))
            {
                await ApplyOrNoteAsync(run, table, text, "다시 만든 테이블에 적용하지 못한 문장이 있습니다");
            }
        }
    }

    /// <summary>
    /// 다시 만들기에서 지운 뒤 CREATE 가 실패한 표를(같은 DBMS·교차 모두: 데이터 보호가 우선, 조정 결정 67) 그 자리에서 휴지통에서 되살린다(<c>FLASHBACK TABLE … TO BEFORE DROP</c>): 옛 표와 데이터가 그대로 돌아온다.
    /// 휴지통에 남지 않았거나(영구 삭제) 되살리기도 실패하면 아무것도 하지 않고, 그때는 <see cref="NoteRecreated"/> 가 직접 되돌리는 안내를 낸다.
    /// </summary>
    private async Task FlashbackDroppedAsync(RunState run, TableState table)
    {
        if (table.BinKnown && table.BinName is null)
        {
            return; // 휴지통에 남지 않았다(영구 삭제)
        }

        var source = table.BinName is { } binName ? CopyCommonStatements.Qualified(table.Owner, binName) : CopyCommonStatements.Qualified(table.Owner, table.TableName);
        var back = await CleanupStatementAsync(run, CopyCommonStatements.RecreateDdl($"FLASHBACK TABLE {source} TO BEFORE DROP"), LongCleanupTimeout);
        if (back.Status == ExecutionStatus.Succeeded)
        {
            table.Flashbacked = true;
        }
        else
        {
            table.Note("옛 테이블을 휴지통에서 되살리지 못했습니다: " + Describe(back));
        }
    }

    /// <summary>
    /// 만들 표의 문장(다시 만들기·없으면 만들기). 교차 복사는 대상 DBMS 로 옮긴 구조로 만든다(<see cref="CrossTableMap"/>).
    /// 사전 점검 미리 보기가 같은 함수를 써서 보여 준 문장이 곧 보내는 문장이다. 서버가 지은 이름 판정은 원본 이름을 보는 것이라 원본 방언이 한다.
    /// </summary>
    private CrossCreate BuildCreate(CopyTablePlan plan, string targetOwner, IReadOnlySet<string> tablesInJob)
    {
        var sourceOwner = plan.Source.Table.Owner;
        // 「없으면 만들기」로 새로 만드는 표는 PK·UK·인덱스를 적재 뒤에 만든다(PF4a, 조정 결정 74): 행마다 인덱스를 고치지 않는다. 적재 뒤 RestoreAsync 가 맨 먼저 보낸다.
        // 「다시 만들기」는 지금처럼 CREATE TABLE 에 키를 둔다: 키 이름이 겹치는 등으로 만들 수 없으면 CREATE 가 실패해 옛 표를 휴지통에서 되살린다(결정 67 — 실패 규칙은 그대로, #166).
        return CrossTableMap.Build(_sourceDbms, _targetDbms, plan.Source, targetOwner, tablesInJob, name => _sourceDialect.IsServerName(name, sourceOwner), _sourceCodec,
                                   keysAfterLoad: plan.CreateMissing);
    }

    /// <summary>
    /// 교차 복사에서 대상에 만들 수 없는 작업 안 FK 는 붙이지 않는다(<see cref="CrossTableMap.FilterForeignKeys"/>, 사전 점검과 같은 판정): 만들지 않는 열을 쓰는 FK(서버가 지은 이름이라 이름이 없는 FK 도),
    /// 함께 만드는 부모의 PK·UK 를 만들지 않아 참조할 키가 없는 FK(ORA-02270). 빼는 FK 마다 <paramref name="note"/> 에 글을 남긴다. 같은 DBMS 는 그대로다.
    /// 문장에서 열을 읽지 못한 FK 는 이름이 만들지 않는 제약과 같을 때만 뺀다.
    /// </summary>
    private IReadOnlyList<(string Parent, string Ddl, string? Name)> JobForeignKeys(RunState run, CopyTablePlan plan, IReadOnlyList<(string Parent, string Ddl, string? Name)> foreignKeys,
                                                                                    CrossCreate built, out string? note)
    {
        var owner = plan.Target.Table.Owner;
        var sourceOwner = plan.Source.Table.Owner;
        var (kept, skipped) = CrossTableMap.FilterForeignKeys(_sourceDbms, _targetDbms, plan.Source, _sourceCodec, foreignKeys,
            parent => BuiltPlans(run).FirstOrDefault(p => CopyOwners.SameOwner(p.Target.Table.Owner, owner)
                                                         && CopyOwners.SameOwner(p.Source.Table.Owner, sourceOwner)
                                                         && string.Equals(p.Rule.Table, parent, StringComparison.Ordinal))?.Source);
        note = skipped.Count == 0 ? null : "대상으로 옮기지 않는 열·키를 쓰는 작업 안 FK 는 만들지 않습니다: " + string.Join(", ", skipped) + ".";
        return built.OmittedConstraints.Count == 0
            ? kept
            : [.. kept.Where(f => f.Name is null || !built.OmittedConstraints.Contains(f.Name))];
    }

    /// <summary>교차 복사로 만든 표가 원본과 달라지는 것(없으면 null).</summary>
    private static string? CrossNote(CrossCreate built)
        => built.Notes.Count == 0 ? null : "원본과 달라지는 것: " + string.Join(" / ", built.Notes) + ".";

    // ---- 1b. 대상에 없는 표 만들기(「없으면 만들기」, 개발 대상만) ----

    /// <summary>이번 실행이 만드는 표의 계획: 다시 만들기 + 없으면 만들기. 작업 안 FK 를 원본대로 붙이는 부모 후보다(사전 점검 <c>BuiltTables</c> 와 같은 판정).</summary>
    private static IEnumerable<CopyTablePlan> BuiltPlans(RunState run)
        => run.Report.Order.Where(p => p.Rule.Action == TargetAction.Recreate || p.CreateMissing);

    /// <summary>
    /// 대상에 표가 없는 규칙(<see cref="CopyTablePlan.CreateMissing"/>)의 표를 부모 → 자식 순서로 만든다. 지울 것도 보존할 것도 없다(새 표).
    /// 표마다 CREATE 문장·작업 안 FK 문장을 준비한 뒤 CREATE 를 보낸다: 준비·CREATE 에 실패한 표는 실패로 두고(적재하지 않는다) 다른 표는 계속한다.
    /// 작업 안 FK·IDENTITY 다음 값은 적재 뒤 <see cref="RestoreAsync"/> 가 맞춘다(다시 만든 표와 같다).
    /// 이름이 이미 쓰이고 있으면(사전 점검 뒤에 생긴 표, 권한이 없어 안 보이던 표) CREATE 가 거부돼 그 표만 실패한다 — 지우지 않는다(<see cref="TableState.Created"/> 가 거짓).
    /// </summary>
    private async Task CreateMissingTablesAsync(RunState run, CancellationToken ct)
    {
        var plans = run.Report.Order.Where(p => p.CreateMissing).ToList();
        foreach (var plan in plans)
        {
            ct.ThrowIfCancellationRequested();
            var table = run.Table(plan);
            table.CreateRequested = true;
            run.Progress(table.Name, 0, null, PhaseCreate);
            var owner = plan.Target.Table.Owner;
            var sourceOwner = plan.Source.Table.Owner;
            var planned = BuiltPlans(run).Where(p => CopyOwners.SameOwner(p.Target.Table.Owner, owner)
                                                      && CopyOwners.SameOwner(p.Source.Table.Owner, sourceOwner))
                .Select(p => p.Rule.Table).ToHashSet(StringComparer.Ordinal);
            CrossCreate built;
            string? fkNote;
            try
            {
                built = BuildCreate(plan, owner, new HashSet<string>(StringComparer.Ordinal));
                table.CreateDdl = built.Statements;
                table.AfterLoadDdl = built.AfterLoad;
                table.JobForeignKeys = JobForeignKeys(run, plan, await _sourceRecreate.SourceForeignKeysAsync(run.Source, sourceOwner, plan.Rule.Table, planned, owner, ct), built, out fkNote);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested
                                       && (CopyErrors.IsServer(_errors, ex) || ex is InvalidOperationException or BulkDeniedException or ArgumentException))
            {
                table.Fail("만들 테이블의 문장을 준비하지 못했습니다: " + SafeError(ex.Message));
                continue;
            }

            await CreateSequencesAsync(run, plan, table, owner);
            var create = await CleanupStatementAsync(run, CopyCommonStatements.RecreateDdl(table.CreateDdl[0]), CleanupTimeout);
            if (create.Status != ExecutionStatus.Succeeded)
            {
                table.Fail("대상에 테이블을 만들지 못했습니다: " + Describe(create));
                continue;
            }

            table.Created = true;
            table.CreatedNew = true;
            table.Preserved = PreservedDdl.Empty; // 새 표: 되살릴 트리거·권한·참조 FK 가 없다
            table.Note("대상에 테이블이 없어 원본 구조로 만들었습니다.");
            if (CrossNote(built) is { } crossNote)
            {
                table.Note(crossNote);
            }

            if (fkNote is not null)
            {
                table.Note(fkNote);
            }

            foreach (var text in table.CreateDdl.Skip(1))
            {
                await ApplyOrNoteAsync(run, table, text, "만든 테이블에 적용하지 못한 문장이 있습니다");
            }
        }
    }

    /// <summary>
    /// 이번 실행이 만든 표(<see cref="TableState.CreatedNew"/>) 가운데 적재를 끝내지 못한 표를 <c>DROP … PURGE</c> 로 지운다(자식 → 부모). 대상 트랜잭션을 끝낸 뒤에만 부른다(DDL, 묵시 커밋).
    /// 지우는 표: 적재를 끝내지 못한 표(실패·중지·앞 표 실패로 시작하지 못함), 「모두 끝난 뒤 커밋」인데 되돌려져 빈 표.
    /// 지우지 못하면 경고로 남기고 원래 실패는 그대로 둔다(알림에 지울 문장을 적는다). 지운 표는 되살리기 대상에서 뺀다(<see cref="TableState.Purged"/>).
    /// </summary>
    private async Task PurgeUnloadedNewTablesAsync(RunState run)
    {
        var rolledBackAll = run.Job.Options.Commit == CommitMode.AtEnd && run.TargetRolledBack;
        var doomed = run.Tables.Values
            .Where(t => t.CreatedNew && (!t.LoadCompleted || rolledBackAll)).Reverse().ToList();
        foreach (var t in doomed)
        {
            run.Progress(t.Name, 0, null, PhaseRestore);
            var drop = _dialect.DropCreatedTable(t.Owner, t.TableName);
            ExecutionResult? r = null;
            try
            {
                r = await CleanupStatementAsync(run, drop, LongCleanupTimeout);
            }
            catch (Exception ex) when (IsCleanupFailure(ex))
            {
                run.Notices.Add($"{t.Name}: 만든 테이블을 지우지 못했습니다: {SafeError(ex.Message)}");
            }

            if (r is { Status: ExecutionStatus.Succeeded })
            {
                t.Purged = true;
                t.Degrade("적재를 끝내지 못해 이번에 만든 테이블을 지웠습니다(DROP … PURGE). 다시 실행하면 다시 만듭니다.");
                run.Notices.Add($"{t.Name}: 적재를 끝내지 못해 이번에 만든 테이블 {t.TargetName} 을(를) 지웠습니다(DROP … PURGE).");
                continue;
            }

            if (r is not null)
            {
                run.Log("만든 테이블을 지우지 못했습니다: " + Describe(r), CopyLogKind.Warning, t.Name);
            }

            var message = $"적재를 끝내지 못했고 이번에 만든 테이블 {t.TargetName} 을(를) 지우지 못했습니다. 직접 지우세요: {drop.Text}";
            t.Degrade(message);
            run.Notices.Add($"{t.Name}: {message}");
        }

        await DropUnusedSequencesAsync(run);
    }

    /// <summary>보존 DDL·지울 표 목록·작업 안 FK 를 실행 기록에 「-- 보존 DDL」 줄로 쓴다(실행한 문장이 아니다, T13 기록 형식).</summary>
    private static async Task RecordPreservedAsync(RunState run, IReadOnlyList<CopyTablePlan> plans, CancellationToken ct)
    {
        if (run.Record is not { } record)
        {
            return;
        }

        await record.RecordStatementAsync(
            $"-- 보존 DDL: 다시 만들 테이블 {string.Join(", ", plans.Select(p => run.Table(p).TargetName))}(옛 테이블은 휴지통으로). 다시 만들기가 끝나지 않았으면 아래 문장을 직접 실행하세요.", ct);
        foreach (var plan in plans)
        {
            var t = run.Table(plan);
            var kept = t.Preserved!;
            foreach (var text in kept.Triggers.Concat(kept.Grants).Concat(kept.ReferencingForeignKeys).Concat(t.JobForeignKeys.Select(f => f.Ddl)))
            {
                await record.RecordStatementAsync($"-- 보존 DDL {t.Name}: {text}", ct);
            }
        }
    }

    /// <summary>
    /// 이 표를 참조하는 받아 둔 FK 를 떼어 낸다(DROP 이 CASCADE 없이 되게). 이미 지운 다시 만들기 자식 표의 FK 는 표와 함께 없어졌다.
    /// 하나라도 못 떼면(자식 표 ALTER 권한 없음 등) 이 표를 지우지 않고 전체를 멈춘다. 떼어 낸 FK 는 끝낼 때 되붙인다.
    /// </summary>
    private async Task<bool> DetachReferencingAsync(RunState run, TableState table)
    {
        var kept = table.Preserved!;
        for (var j = 0; j < kept.ReferencingConstraints.Count; j++)
        {
            var fk = kept.ReferencingConstraints[j];
            if (run.ByTarget(fk.Owner, fk.Table) is { Dropped: true })
            {
                continue;
            }

            var r = await CleanupStatementAsync(run, _dialect.DropConstraint(fk), CleanupTimeout);
            if (r.Status != ExecutionStatus.Succeeded)
            {
                table.Fail($"이 테이블을 참조하는 FK {fk.Owner}.{fk.Name} 을(를) 먼저 떼어 내지 못해 테이블을 지우지 않았습니다: {Describe(r)}");
                run.Abort($"{table.Name}: 참조 FK 를 떼어 내지 못해 복사를 멈췄습니다. 떼어 낸 FK 는 다시 붙입니다.");
                return false;
            }

            table.DetachedReferencing.Add(j);
        }

        return true;
    }

    /// <summary>DROP(CASCADE 없이). 지웠으면 true. 안 보이는 FK(ORA-02449)·실패면 전체를 멈춘다. 결과를 모르면 표가 있는지 다시 본다.</summary>
    private async Task<bool> DropAsync(RunState run, ICopyRecreateStep step, TableState table)
    {
        var owner = table.Owner;
        var drop = await CleanupStatementAsync(run, _dialect.DropTable(owner, table.TableName), LongCleanupTimeout);
        if (drop.Status != ExecutionStatus.Succeeded)
        {
            if (_dialect.IsReferencedByForeignKeys(drop.Error))
            {
                // 이 실행에서 먼저 지운 자식 표가 FK 를 쥔 채 휴지통에 남는 DBMS 는 그 FK 를 원인으로 짚어 준다(「보이지 않는 FK」 라고만 하면 오안내다).
                var children = table.Preserved is { } kept
                    ? kept.ReferencingConstraints.Where(fk => run.ByTarget(fk.Owner, fk.Table) is { Dropped: true })
                        .Select(fk => fk.Owner + "." + fk.Table).Distinct(StringComparer.Ordinal).ToList()
                    : [];
                var recycled = children.Count > 0 ? _dialect.RecycledChildForeignKeyNote(children) : null;
                var name = ErrorNameNote(_dialect.ReferencedByForeignKeysErrorName);
                table.Fail(recycled is null
                    ? $"대상 계정에 보이지 않는 FK(다른 스키마)가 이 테이블을 참조하고 있어 지우지 않았습니다{name}. 그 FK 를 확인하거나 대상 처리를 바꾸세요."
                    : $"이 테이블을 참조하는 FK 가 남아 있어 지우지 않았습니다{name}. {recycled} 아니면 대상 계정에 보이지 않는 FK(다른 스키마)일 수도 있습니다. 그 FK 를 확인하거나 대상 처리를 바꾸세요.");
                run.Abort($"{table.Name}: 참조 FK 때문에 테이블을 지우지 않고 멈췄습니다. 떼어 낸 FK 는 다시 붙입니다.");
                return false;
            }

            if (!await TableGoneAsync(run, owner, table.TableName))
            {
                table.Fail("테이블을 지우지 못했습니다: " + Describe(drop));
                run.Abort($"{table.Name}: 테이블을 지우지 못해 복사를 멈췄습니다. 이미 지운 테이블은 다시 만들고 트리거·권한·FK 를 되살립니다.");
                return false;
            }

            table.Note("DROP 결과를 받지 못했지만 테이블이 없어 지운 것으로 봅니다.");
        }

        // 지운 표는 비었다: 「지운 뒤 적재를 끝내지 못함」 알림이 이 표에도 걸린다(DDL 이라 바로 확정).
        table.Dropped = true;
        table.Deleted = true;
        run.DeletesPersisted = true;

        // 휴지통 전후 비교: 이번 DROP 이 만든 BIN$ 이름. 비면 휴지통에 남지 않았다(영구 삭제).
        try
        {
            using var check = new CancellationTokenSource(CleanupTimeout);
            var after = await step.RecycleBinAsync(run.Target, table.TableName, check.Token);
            table.BinKnown = true;
            table.BinName = after.Except(table.BinBefore, StringComparer.Ordinal).Order(StringComparer.Ordinal).LastOrDefault();
        }
        catch (Exception ex) when (IsCleanupFailure(ex))
        {
            table.BinKnown = false;
        }

        return true;
    }

    /// <summary>다시 만든 표에 문장 하나. 실패하면 원문을 표 메시지·알림에 남기고 계속한다.</summary>
    private async Task ApplyOrNoteAsync(RunState run, TableState table, string text, string what)
    {
        var r = await TryRestoreAsync(run, text, CleanupTimeout);
        if (r is not { Status: ExecutionStatus.Succeeded })
        {
            var message = $"{what}({DescribeRestore(r)}). 직접 실행하세요: {text}";
            table.Degrade(message);
            run.Notices.Add($"{table.Name}: {message}");
        }
    }

    /// <summary>표가 없으면 true. 확인하지 못하면 false(있다고 본다).</summary>
    private async Task<bool> TableGoneAsync(RunState run, string owner, string table)
    {
        try
        {
            using var step = new CancellationTokenSource(CleanupTimeout);
            var n = await ScalarAsync(run, run.Target, CopyCommonStatements.Query(CopyCatalogQueries.Sql(_catalog, run.Target.Server, "copy.table_exists")), new Dictionary<string, BindValue>
            {
                ["OWNER"] = new(BindDataType.Varchar2, owner),
                ["NAME"] = new(BindDataType.Varchar2, table),
            }, step.Token);
            return _codec.IsZero(n);
        }
        catch (Exception ex) when (IsCleanupFailure(ex))
        {
            return false;
        }
    }

    /// <summary>
    /// 대상 트랜잭션을 끝낸 뒤에만 부른다(DDL, 묵시 커밋). 중지·실패 뒤에도 부른다. 객체마다 자기 토큰을 쓰고 예외를 가둔다.
    /// 다시 만든 표: IDENTITY 다음 값 맞추기 → 트리거 → 권한 → (모든 표 뒤) 참조 FK. 지우지 못한 표: 떼어 낸 참조 FK 만 되붙인다.
    /// 되살리지 못한 것은 원문을 표 메시지·알림에 남기고 결과를 실패로 둔다.
    /// </summary>
    private async Task RestoreAsync(RunState run)
    {
        // 새 표의 PK·UK·인덱스(PF4a): FK(작업 안·참조·작업 밖 부모)가 이 키를 참조하므로 다른 되살리기보다 먼저.
        foreach (var t in run.Tables.Values.Where(t => t.Created && !t.Purged && t.AfterLoadDdl.Count > 0))
        {
            await CreateKeysAfterLoadAsync(run, t);
        }

        await AdjustCreatedSequencesAsync(run);
        var tables = run.Tables.Values.Where(t => t.Created && !t.Purged && t.Preserved is not null).ToList();
        foreach (var t in tables)
        {
            var kept = t.Preserved!;
            run.Progress(t.Name, 0, null, PhaseRestore);

            // IDENTITY 는 새로 만들면 1부터 시작한다: 복사한 최대값 뒤로 옮긴다(앱이 ID 없이 넣을 때 ORA-00001 이 나지 않게).
            var plan = run.Report.Order.FirstOrDefault(p => string.Equals(run.Key(p), t.Name, StringComparison.Ordinal));
            // 교차 복사는 대상에 만든 구조(CrossTableMap)로 본다: IDENTITY 를 일반 열로 만든 열은 맞출 다음 값이 없다.
            var identityColumns = plan is null ? [] : CrossTableMap.MapTable(_sourceDbms, _targetDbms, plan.Source, _sourceCodec).Target.Columns.Where(c => c.IsIdentity).ToList();
            foreach (var c in identityColumns)
            {
                var text = _dialect.RestartIdentity(t.Owner, t.TableName, c.Name);
                var r = await TryRestoreAsync(run, text, LongCleanupTimeout);
                if (r is not { Status: ExecutionStatus.Succeeded })
                {
                    var message = $"IDENTITY 열 {c.Name} 의 다음 값을 맞추지 못했습니다({DescribeRestore(r)}). 직접 실행하세요: {text}";
                    t.Degrade(message);
                    run.Notices.Add($"{t.Name}: {message}");
                }
            }

            await RestoreTriggersAsync(run, t, kept);

            foreach (var text in kept.Grants)
            {
                var r = await TryRestoreAsync(run, text, CleanupTimeout);
                if (r is not { Status: ExecutionStatus.Succeeded })
                {
                    RestoreFailed(run, t, $"권한을 되살리지 못했습니다({DescribeRestore(r)}). 직접 실행하세요: {text}");
                }
            }
        }

        // 새 표에 딸린 객체(SQ2): 함수 기반 인덱스 → 원본 트리거 → 원본 권한 → 작업 밖 부모 FK. 못 만든 것은 알림만(표는 부분 실패).
        foreach (var t in tables.Where(t => !t.CreatedNew || t.LoadCompleted))
        {
            if (PlanOf(run, t) is { Companions.IsEmpty: false } companionPlan)
            {
                await CreateCompanionsAsync(run, t, companionPlan.Companions);
            }
        }

        // 작업 안 FK(원본 카탈로그 기준): 적재가 끝난 뒤 붙인다. 부모 없는 행이면 NOVALIDATE 로 켜고 행 수를 알린다(T9 의 FK 다시 켜기와 같다).
        foreach (var t in tables)
        {
            foreach (var (parent, text, name) in t.JobForeignKeys)
            {
                if (run.ByTarget(t.Owner, parent) is { Purged: true } or { CreateRequested: true, Created: false })
                {
                    var message = $"부모 테이블 {parent} 를 이번에 만들지 못했거나 적재를 끝내지 못해 지웠으므로 FK 를 만들지 않았습니다. 다시 실행하세요.";
                    t.Degrade(message);
                    run.Notices.Add($"{t.Name}: {message}");
                    continue;
                }

                if (run.ByTarget(t.Owner, parent) is { Dropped: true, Created: false })
                {
                    var message = $"부모 테이블 {parent} 를 다시 만들지 못해 FK 를 만들지 않았습니다. 부모를 되돌린 뒤 직접 실행하세요: {text}";
                    t.Degrade(message);
                    run.Notices.Add($"{t.Name}: {message}");
                    continue;
                }

                await AttachJobForeignKeyAsync(run, t, text, name);
            }
        }

        // 참조 FK 는 모든 표의 트리거·권한 뒤에(다른 계정 자식 표의 FK 는 REFERENCES 권한이 먼저 돌아와야 한다).
        // 다시 만든 표 = 받아 둔 참조 FK 전부, 지우지 못한 표 = 떼어 낸 것만. 자식이 다시 만들기로 지워진 FK 는 원본 FK 로 이미 만들었다.
        foreach (var t in run.Tables.Values.Where(t => t.Preserved is not null && (t.Created || (!t.Dropped && t.DetachedReferencing.Count > 0))))
        {
            var kept = t.Preserved!;
            var indexes = t.Created ? Enumerable.Range(0, Math.Min(kept.ReferencingForeignKeys.Count, kept.ReferencingConstraints.Count)) : t.DetachedReferencing;
            foreach (var i in indexes)
            {
                var fk = kept.ReferencingConstraints[i];
                if (run.ByTarget(fk.Owner, fk.Table) is { Dropped: true })
                {
                    continue;
                }

                await ReattachAsync(run, t, fk, kept.ReferencingForeignKeys[i]);
            }
        }
    }

    /// <summary>
    /// 받아 둔 트리거를 되살린다. 만들어졌지만 컴파일 오류로 INVALID 이면 끈다(켜진 채 두면 이 표의 DML 이 막힌다).
    /// 컴파일 오류를 오류로 알려 주는 DBMS(Oracle ORA-24344)는 그 오류로, 오류 없이 받는 DBMS(Tibero)는 만든 뒤 상태를 조회해 알아챈다
    /// (<see cref="ICopyDialect.TriggerStatusSql"/>).
    /// </summary>
    /// <param name="companion">새 표에 딸려 만드는 원본 트리거(SQ2)인가: 그때는 만들지 못해도 실패가 아니라 알림(부분 실패)이다.</param>
    private async Task RestoreTriggersAsync(RunState run, TableState t, PreservedDdl kept, bool companion = false)
    {
        for (var i = 0; i < kept.Triggers.Count; i++)
        {
            var text = kept.Triggers[i];
            var r = await TryRestoreAsync(run, text, CleanupTimeout);
            if (r is { Status: ExecutionStatus.Succeeded })
            {
                // 같은 트리거의 다음 문장이 있으면 그것은 원래 꺼져 있던 트리거의 DISABLE 이다(상태를 볼 필요 없음).
                // 앞 문장과 같은 트리거면 이 문장이 DISABLE 자체다.
                var obj = i < kept.TriggerObjects.Count ? kept.TriggerObjects[i] : null;
                var isCreate = obj is not null && (i == 0 || kept.TriggerObjects[i - 1] != obj);
                var followedByDisable = obj is not null && i + 1 < kept.TriggerObjects.Count && kept.TriggerObjects[i + 1] == obj;
                if (isCreate && !followedByDisable && _dialect.TriggerStatusSql is { } statusSql)
                {
                    await DisableIfInvalidAsync(run, t, obj!, statusSql, companion);
                }

                continue;
            }

            if (r?.Error?.Kind == DbErrorKind.CompiledWithErrors && i < kept.TriggerObjects.Count)
            {
                await DisableInvalidTriggerAsync(run, t, kept.TriggerObjects[i], companion);
                continue;
            }

            Problem(run, t, companion
                ? $"원본 트리거를 만들지 못했습니다({DescribeRestore(r)}). 직접 실행하세요: {text}"
                : $"트리거를 되살리지 못했습니다({DescribeRestore(r)}). 직접 실행하세요: {text}", companion);
        }
    }

    /// <summary>되살린 트리거의 상태를 조회해 INVALID 이면 끈다. 상태를 알 수 없으면 알리고 결과를 실패로 둔다(막힌 DML 을 못 알아챌 수 있다).</summary>
    private async Task DisableIfInvalidAsync(RunState run, TableState t, DisabledObject obj, string statusSql, bool companion = false)
    {
        string? status;
        try
        {
            using var step = new CancellationTokenSource(CleanupTimeout);
            var value = await ScalarAsync(run, run.Target, CopyCommonStatements.Query(statusSql), new Dictionary<string, BindValue>
            {
                ["OWN"] = new(BindDataType.Varchar2, obj.Owner),
                ["NAME"] = new(BindDataType.Varchar2, obj.Name),
            }, step.Token);
            status = _codec.ToText(value);
        }
        catch (Exception ex) when (IsCleanupFailure(ex))
        {
            status = null;
        }

        if (status is null)
        {
            Problem(run, t, $"트리거 {obj.Owner}.{obj.Name} 을 {(companion ? "만들었지만" : "되살렸지만")} 컴파일 상태를 확인하지 못했습니다. 컴파일 오류로 INVALID 이면 이 테이블의 DML 이 막힐 수 있으니 상태를 확인하세요.", companion);
            return;
        }

        if (string.Equals(status.Trim(), "INVALID", StringComparison.OrdinalIgnoreCase))
        {
            await DisableInvalidTriggerAsync(run, t, obj, companion);
        }
    }

    private async Task DisableInvalidTriggerAsync(RunState run, TableState t, DisabledObject obj, bool companion = false)
    {
        // 만들어졌지만 INVALID 다. 켜진 채 두면 이 표의 DML 이 모두 막히므로(Oracle ORA-04098) 끈다. 트리거는 지우지 않고 남긴다(결정 78).
        var off = await TryRestoreAsync(run, _dialect.DisableTrigger(obj.Owner, obj.Name).Text, CleanupTimeout);
        var compile = ErrorNameNote(_dialect.TriggerCompileErrorName);
        var why = companion ? "원본에서 옮겨 만들었지만 대상에서 컴파일되지 않아" : "다시 만든 열 구조와 맞지 않아";
        Problem(run, t, off is { Status: ExecutionStatus.Succeeded }
            ? $"트리거 {obj.Owner}.{obj.Name} 은 {why} 컴파일 오류{compile}로 INVALID 입니다. 꺼 두었으니 고친 뒤 켜세요."
            : $"트리거 {obj.Owner}.{obj.Name} 은 컴파일 오류{compile}로 INVALID 이고 끄지도 못했습니다. 이 테이블의 DML 이 막힐 수 있으니 직접 고치거나 지우세요.", companion);
    }

    /// <summary>되살리기 문제 하나: 대상 쪽 되살리기면 실패(<see cref="RestoreFailed"/>), 새 표에 딸린 객체(SQ2)면 부분 실패 알림.</summary>
    private static void Problem(RunState run, TableState t, string message, bool companion)
    {
        if (!companion)
        {
            RestoreFailed(run, t, message);
            return;
        }

        t.Degrade(message);
        run.Notices.Add($"{t.Name}: {message}");
    }

    /// <summary>
    /// 새로 만든 표에 적재 뒤 PK·UK → 인덱스를 만든다(PF4a). PK·UK 를 만들지 못하면(적재한 행의 키가 겹침 등) 키 없는 표가 남으므로 결과를 실패로 두고 문장을 남긴다.
    /// 인덱스는 지금까지의 「적용하지 못한 문장」과 같이 알림만 남긴다.
    /// </summary>
    private async Task CreateKeysAfterLoadAsync(RunState run, TableState t)
    {
        run.Progress(t.Name, 0, null, PhaseRestore);
        foreach (var text in t.AfterLoadDdl)
        {
            if (!CreateTableDdl.IsKeyStatement(text))
            {
                await ApplyOrNoteAsync(run, t, text, "적재 뒤 인덱스를 만들지 못했습니다");
                continue;
            }

            var r = await TryRestoreAsync(run, text, LongCleanupTimeout);
            if (r is not { Status: ExecutionStatus.Succeeded })
            {
                RestoreFailed(run, t, $"적재 뒤 PK·UK 를 만들지 못했습니다({DescribeRestore(r)}). 키가 겹치는 행을 고친 뒤 직접 실행하세요: {text}");
            }
        }
    }

    /// <summary>실행 상태의 표에 해당하는 점검 계획(규칙 키로 찾는다). 없으면 null.</summary>
    private static CopyTablePlan? PlanOf(RunState run, TableState t)
        => run.Report.Order.FirstOrDefault(p => string.Equals(run.Key(p), t.Name, StringComparison.Ordinal));

    /// <summary>
    /// 새로 만든 표에 딸린 객체를 적재 뒤에 만든다(SQ2, 조정 결정 78): 함수 기반 인덱스 → 원본 트리거(적재 중 실행되지 않게 적재 뒤, 컴파일 오류면 남기고 끈다)
    /// → 원본 권한 → 작업 밖 부모 FK(부모 없는 행 등으로 실패하면 NOVALIDATE 로 바꾸지 않고 뺀다). 만들지 못한 것은 표 메시지·알림에 문장을 남기고 계속한다.
    /// </summary>
    private async Task CreateCompanionsAsync(RunState run, TableState t, CompanionDdl companions)
    {
        run.Progress(t.Name, 0, null, PhaseRestore);
        foreach (var text in companions.FunctionIndexes)
        {
            await ApplyOrNoteAsync(run, t, text, "함수 기반 인덱스를 만들지 못해 뺐습니다");
        }

        if (companions.Triggers.Count > 0)
        {
            await RestoreTriggersAsync(run, t, new PreservedDdl(companions.Triggers, [], []) { TriggerObjects = companions.TriggerObjects }, companion: true);
        }

        foreach (var text in companions.Grants)
        {
            await ApplyOrNoteAsync(run, t, text, "원본 권한을 붙이지 못했습니다(받는 사용자·롤이 대상에 없을 수 있습니다)");
        }

        foreach (var (_, text, _) in companions.OuterForeignKeys)
        {
            await ApplyOrNoteAsync(run, t, text, "작업 밖 부모 테이블을 참조하는 원본 FK 를 붙이지 못해 뺐습니다");
        }
    }

    /// <summary>
    /// 시험·IT 용: 받아 둔 트리거만 <paramref name="target"/> 에서 되살린다(<see cref="RestoreAsync"/> 의 트리거 단계와 같은 코드).
    /// 다른 계정이 없어 복사 전체를 못 돌리는 환경에서 INVALID 트리거 판정과 끄기를 실행기 경로로 보는 데 쓴다. 되살리지 못한 것이 있으면 <c>Failed</c>.
    /// </summary>
    internal async Task<(bool Failed, IReadOnlyList<string> Notices)> RestoreTriggersForTestAsync(ISession target, string owner, string table, PreservedDdl kept)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(kept);
        var job = new CopyJob
        {
            Id = "restore-triggers", Name = "restore-triggers", Source = new ConnectionRef("-", owner), Target = new ConnectionRef("-", owner),
            SourceOwner = owner, TargetOwner = owner, MaskSalt = "AA==",
        };
        var run = new RunState(new CopySessions(target, target), job, new PrecheckReport([], [], [], new Dictionary<string, long>()),
                               new ResolvedPeriod(null, null), new HashSet<string>(StringComparer.Ordinal), null);
        await RestoreTriggersAsync(run, new TableState(owner + "." + table, owner, table), kept);
        return (run.RestoreFailed, [.. run.Notices]);
    }

    /// <summary>
    /// 다시 만든 표의 원본 FK 하나. 실패하면 원문을 표 메시지·알림에 남기고(부분 실패) 계속한다. 부모 없는 행(ORA-02298)이면 NOVALIDATE 로 켠다.
    /// </summary>
    private async Task AttachJobForeignKeyAsync(RunState run, TableState t, string text, string? name)
    {
        run.Progress(t.Name, 0, null, PhaseRestore);
        var r = await TryRestoreAsync(run, text, LongCleanupTimeout);
        if (r is { Status: ExecutionStatus.Succeeded })
        {
            return;
        }

        var loose = _dialect.IsParentKeysNotFound(r?.Error) ? await TryRestoreAsync(run, _dialect.AddForeignKeyWithoutValidation(text), CleanupTimeout) : null;
        if (loose is not { Status: ExecutionStatus.Succeeded })
        {
            var message = $"원본 FK 를 만들지 못했습니다({DescribeRestore(loose ?? r)}). 직접 실행하세요: {text}";
            t.Degrade(message);
            run.Notices.Add($"{t.Name}: {message}");
            return;
        }

        var orphans = "수 모름";
        if (name is not null)
        {
            using var count = new CancellationTokenSource(LongCleanupTimeout);
            orphans = await OrphanCountAsync(run, new DisabledObject(DisabledKind.ForeignKey, t.Owner, t.TableName, name), count.Token);
        }

        run.Notices.Add($"{t.Name}: 원본 FK {name ?? "(서버 이름)"} 는 부모 없는 행 {orphans} 때문에 ENABLE NOVALIDATE 로 만들었습니다.");
    }

    /// <summary>참조 FK 하나를 다시 붙인다. 부모 없는 행(ORA-02298)일 때만 NOVALIDATE 로 켜고 행 수를 알린다(설계 8.2 의 FK 규칙).</summary>
    private async Task ReattachAsync(RunState run, TableState t, DisabledObject fk, string text)
    {
        run.Progress(fk.Table, 0, null, PhaseRestore);
        var r = await TryRestoreAsync(run, text, LongCleanupTimeout);
        if (r is { Status: ExecutionStatus.Succeeded })
        {
            return;
        }

        if (!_dialect.IsParentKeysNotFound(r?.Error))
        {
            RestoreFailed(run, t, $"이 테이블을 참조하던 FK {fk.Owner}.{fk.Name} 을(를) 되살리지 못했습니다({DescribeRestore(r)}). 직접 실행하세요: {text}");
            return;
        }

        var loose = await TryRestoreAsync(run, _dialect.AddForeignKeyWithoutValidation(text), CleanupTimeout);
        if (loose is not { Status: ExecutionStatus.Succeeded })
        {
            RestoreFailed(run, t, $"이 테이블을 참조하던 FK {fk.Owner}.{fk.Name} 을(를) 되살리지 못했습니다({DescribeRestore(loose)}). 직접 실행하세요: {text}");
            return;
        }

        using var count = new CancellationTokenSource(LongCleanupTimeout);
        var orphans = await OrphanCountAsync(run, fk, count.Token);
        run.Notices.Add($"FK {fk.Owner}.{fk.Name}: 부모 없는 행 {orphans} 때문에 ENABLE NOVALIDATE 로 되살렸습니다.");
    }

    /// <summary>되살리기 문장 하나. 예외(시간 초과 포함)는 가두고 null 을 돌려준다.</summary>
    private async Task<ExecutionResult?> TryRestoreAsync(RunState run, string text, TimeSpan timeout)
    {
        try
        {
            return await CleanupStatementAsync(run, CopyCommonStatements.RecreateDdl(text), timeout);
        }
        catch (Exception ex) when (IsCleanupFailure(ex))
        {
            run.Notices.Add("되살리기 문장을 보내지 못했습니다: " + SafeError(ex.Message));
            return null;
        }
    }

    private static string DescribeRestore(ExecutionResult? r) => r is null ? "보내지 못함" : Describe(r);

    /// <summary>되살리지 못함: 결과는 실패, 원문은 표 메시지(실행 기록)와 알림 모두에 남긴다.</summary>
    private static void RestoreFailed(RunState run, TableState table, string message)
    {
        run.RestoreFailed = true;
        table.Degrade(message);
        run.Notices.Add($"{table.Name}: {message}");
    }

    /// <summary>안내 글에 붙이는 서버 오류 이름 괄호(<c>(ORA-02449)</c>). 방언이 번호를 모르면(null) 붙이지 않는다.</summary>
    private static string ErrorNameNote(string? errorName) => errorName is null ? string.Empty : $"({errorName})";

    /// <summary>
    /// 다시 만들기를 한 표의 휴지통 안내와, 되살리지 않은 DDL 원문. 화면 알림과 실행 기록(표 메시지)에 함께 남긴다.
    /// 휴지통 이름(BIN$…)을 알면 그 이름으로 안내한다(같은 원래 이름이 휴지통에 여럿이어도 정확하다).
    /// </summary>
    private void NoteRecreated(RunState run, bool restored)
    {
        foreach (var t in run.Tables.Values.Where(t => t.Dropped))
        {
            var owner = t.Owner;
            var source = t.BinName is { } binName ? CopyCommonStatements.Qualified(owner, binName) : CopyCommonStatements.Qualified(owner, t.TableName);
            if (t.Flashbacked)
            {
                var back = "새 테이블을 만들지 못해 지운 옛 테이블을 휴지통에서 되살렸습니다. 옛 테이블과 데이터는 그대로이고 이 테이블은 복사하지 않았습니다"
                           + _dialect.FlashbackRestoreNote + ".";
                t.Note(back);
                run.Notices.Add($"{t.Name}: {back}");
            }
            else if (t.BinKnown && t.BinName is null)
            {
                const string Lost = "지운 옛 테이블이 휴지통에 남지 않았습니다(영구 삭제: 테이블스페이스가 휴지통을 지원하지 않거나 공간이 부족했습니다).";
                t.Degrade(Lost);
                run.Notices.Add($"{t.Name}: {Lost}");
            }
            else
            {
                var hint = t.Created
                    ? $"지운 옛 테이블은 휴지통에 있습니다. 옛 테이블을 보려면 FLASHBACK TABLE {source} TO BEFORE DROP RENAME TO {SqlIdentifier.QuoteAlways(t.TableName + "_OLD")}"
                      + " 로 다른 이름으로 되살리세요(Flashback 화면에서도 할 수 있습니다)."
                    : $"지운 테이블을 다시 만들지 못해 테이블이 없습니다. FLASHBACK TABLE {source} TO BEFORE DROP 으로 휴지통에서 되돌리세요"
                      + _dialect.FlashbackRestoreNote + ".";
                t.Note(hint);
                run.Notices.Add($"{t.Name}: {hint}");
            }

            if ((!t.Created || !restored) && t.Preserved is { } kept)
            {
                // 이 표의 원본 FK, 그리고 다른 다시 만들기 표가 받아 둔 「이 표가 자식인」 옛 FK 도 넣는다(휴지통에서 되돌려도 다른 표를 참조하는 FK 는 안 돌아온다).
                var asChild = run.Tables.Values.Where(o => o.Preserved is not null && !ReferenceEquals(o, t))
                    .SelectMany(o => o.Preserved!.ReferencingConstraints.Select((fk, i) => (fk, i, o.Preserved!))
                        .Where(x => CopyOwners.SameOwner(x.fk.Owner, owner) && x.fk.Table == t.TableName && x.i < x.Item3.ReferencingForeignKeys.Count)
                        .Select(x => x.Item3.ReferencingForeignKeys[x.i]));
                // 휴지통에서 되살린 옛 표는 트리거·권한이 함께 돌아온다: FK(떼어 낸 참조 FK, 이 표가 자식인 옛 FK)만 남는다.
                // 작업 안 FK 는 새로 만들 표의 원본 FK 라 옛 표에는 해당하지 않는다. (Tibero 대상 다시 만들기는 막혀 있어(TB4) 되살리기는 Oracle 대상만 닿는다.)
                var pending = (t.Flashbacked
                        ? kept.ReferencingForeignKeys.Concat(asChild)
                        : kept.Triggers.Concat(kept.Grants).Concat(kept.ReferencingForeignKeys)
                            .Concat(t.JobForeignKeys.Select(f => f.Ddl))
                            .Concat(t.Created ? [] : asChild))
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
                if (pending.Count > 0)
                {
                    var text = (t.Flashbacked
                                   ? "되살린 옛 테이블에 돌아오지 않는 FK(직접 실행하세요): "
                                   : t.Created
                                       ? "되살리지 않은 DDL(직접 실행하세요): "
                                       : "되살리지 않은 DDL(휴지통에서 되돌렸다면 트리거·권한은 이미 있으니 FK 만 직접 실행하세요): ")
                               + string.Join(" ; ", pending);
                    t.Note(text);
                    run.Notices.Add($"{t.Name}: {text}");
                }
            }
        }

        if (restored)
        {
            return;
        }

        // 만든 표에 딸린 객체(SQ2)·적재 뒤 PK·UK·인덱스(PF4a): 적재 뒤 만들기 전에 대상 트랜잭션을 끝내지 못했다.
        foreach (var t in run.Tables.Values.Where(t => t.Created && !t.Purged))
        {
            if (t.AfterLoadDdl.Count > 0)
            {
                var keys = "만들지 않은 PK·UK·인덱스(직접 실행하세요): " + string.Join(" ; ", t.AfterLoadDdl);
                t.Degrade(keys);
                run.Notices.Add($"{t.Name}: {keys}");
            }

            if (PlanOf(run, t) is { Companions.IsEmpty: false } plan)
            {
                var text = "만들지 않은 딸린 객체(직접 실행하세요): " + string.Join(" ; ", plan.Companions.Statements);
                t.Note(text);
                run.Notices.Add($"{t.Name}: {text}");
            }
        }

        // 이번에 만든 표(「없으면 만들기」)의 원본 FK: 적재 뒤 붙이기 전에 대상 트랜잭션을 끝내지 못했다.
        foreach (var t in run.Tables.Values.Where(t => t.CreatedNew && !t.Purged && t.JobForeignKeys.Count > 0))
        {
            var text = "붙이지 않은 원본 FK(직접 실행하세요): " + string.Join(" ; ", t.JobForeignKeys.Select(f => f.Ddl));
            t.Note(text);
            run.Notices.Add($"{t.Name}: {text}");
        }

        // 지우지 못한 표에서 떼어 낸 참조 FK: 되붙이지 못했으면(대상 트랜잭션을 끝내지 못함) 원문을 남긴다.
        foreach (var t in run.Tables.Values.Where(t => !t.Dropped && t.DetachedReferencing.Count > 0 && t.Preserved is not null))
        {
            var text = "떼어 낸 뒤 다시 붙이지 않은 참조 FK(직접 실행하세요): "
                       + string.Join(" ; ", t.DetachedReferencing.Select(i => t.Preserved!.ReferencingForeignKeys[i]));
            t.Note(text);
            run.Notices.Add($"{t.Name}: {text}");
        }
    }

    // ---- 1c. 표보다 먼저 만드는 시퀀스(SQ1, 조정 결정 76) ----

    /// <summary>이번 실행이 만들려 한 시퀀스 하나의 상태. 여러 표가 같은 시퀀스를 쓰면 한 번만 만들고 쓰는 표를 모은다.</summary>
    private sealed class CreatedSequence(CopySequencePlan plan)
    {
        public CopySequencePlan Plan { get; } = plan;

        /// <summary>이번 실행이 만들었다(맞추기·지우기 대상).</summary>
        public bool Created { get; set; }

        /// <summary>만들지 못한 까닭(null = 실패 아님).</summary>
        public string? Error { get; set; }

        /// <summary>이 시퀀스를 쓰는 표와 그 표의 맞추기 기준 열.</summary>
        public List<(TableState Table, IReadOnlyList<string> KeyColumns)> Users { get; } = [];

        /// <summary>
        /// 이미 있던(이번에 만들지 않은) 표가 쓴다(SQ3 시퀀스 보정 연결): 그 표는 지우지 않으므로 적재가 실패해도 시퀀스를 지우지 않는다(<see cref="DropUnusedSequencesAsync"/>).
        /// </summary>
        public bool Keep { get; set; }
    }

    /// <summary>
    /// 시퀀스 보정 연결이 쓰는데 대상에 없는 시퀀스(<see cref="CopyTablePlan.RuleSequences"/>, SQ3 결정 84)를 표 만들기 뒤·적재 전에 만든다(DDL — 대상 쓰기 트랜잭션을 열기 전이다).
    /// 이미 이 실행이 만든(또는 만들려 한) 시퀀스는 다시 보내지 않는다. 만들지 못한 표(CREATE 실패)의 시퀀스는 만들지 않는다.
    /// 만들지 못하면 알림에 직접 만들 문장을 남긴다(표 결과는 그대로, 적재 뒤 보정은 「볼 수 없음」으로 건너뛴다). 적재 뒤 보정은 지금과 같다(<see cref="AdvanceSequencesAsync"/>).
    /// </summary>
    private async Task CreateRuleSequencesAsync(RunState run)
    {
        foreach (var plan in run.Report.Order.Where(p => p.RuleSequences.Count > 0))
        {
            var table = run.Table(plan);
            if (table.CreateRequested && !table.Created)
            {
                continue;
            }

            foreach (var sequence in plan.RuleSequences)
            {
                if (!run.Sequences.TryGetValue((sequence.Owner, sequence.Name), out var state))
                {
                    state = new CreatedSequence(sequence);
                    run.Sequences[(sequence.Owner, sequence.Name)] = state;
                    run.Progress(table.Name, 0, null, PhaseSequence);
                    var r = await TryRestoreAsync(run, sequence.CreateDdl, CleanupTimeout);
                    if (r is { Status: ExecutionStatus.Succeeded })
                    {
                        state.Created = true;
                        table.Note($"시퀀스 {sequence.Owner}.{sequence.Name} 이(가) 대상에 없어 만들었습니다(START WITH {sequence.Spec.StartWith}).");
                    }
                    else
                    {
                        state.Error = DescribeRestore(r);
                        run.Notices.Add($"{table.Name}: 시퀀스 {sequence.Owner}.{sequence.Name} 을(를) 만들지 못해({state.Error}) 보정하지 않습니다. 직접 만드세요: {sequence.CreateDdl}");
                    }
                }

                state.Users.Add((table, sequence.KeyColumns));
                state.Keep |= !table.Created;
            }
        }
    }

    /// <summary>
    /// 표를 만들기(CREATE TABLE) 바로 전에 그 표의 계획 시퀀스(<see cref="CopyTablePlan.CreateSequences"/>)를 만든다. 이미 이 실행이 만든(또는 만들지 못한) 시퀀스는 다시 보내지 않는다.
    /// 만들지 못하면 그 시퀀스를 쓰는 열의 DEFAULT 를 빼고 CREATE 문장을 다시 만든다(사전 점검의 SequenceDefaultDropped 와 같은 처리, 결과 = 부분 실패).
    /// </summary>
    private async Task CreateSequencesAsync(RunState run, CopyTablePlan plan, TableState table, string owner)
    {
        if (plan.CreateSequences.Count == 0)
        {
            return;
        }

        var dropDefaults = new HashSet<string>(StringComparer.Ordinal);
        foreach (var sequence in plan.CreateSequences)
        {
            if (!run.Sequences.TryGetValue((sequence.Owner, sequence.Name), out var state))
            {
                state = new CreatedSequence(sequence);
                run.Sequences[(sequence.Owner, sequence.Name)] = state;
                var r = await TryRestoreAsync(run, sequence.CreateDdl, CleanupTimeout);
                if (r is { Status: ExecutionStatus.Succeeded })
                {
                    state.Created = true;
                    table.Note($"시퀀스 {sequence.Owner}.{sequence.Name} 을(를) 원본 속성으로 만들었습니다(START WITH {sequence.Spec.StartWith}).");
                }
                else
                {
                    state.Error = DescribeRestore(r);
                }
            }

            state.Users.Add((table, sequence.KeyColumns));
            if (state.Error is null)
            {
                continue;
            }

            string message;
            if (sequence.DefaultColumns.Count > 0)
            {
                dropDefaults.UnionWith(sequence.DefaultColumns);
                message = $"시퀀스 {sequence.Owner}.{sequence.Name} 을(를) 만들지 못해({state.Error}) 열 {string.Join(", ", sequence.DefaultColumns)} 의 기본값을 빼고 테이블을 만들었습니다. 시퀀스를 만든 뒤 기본값을 다시 거세요: {sequence.CreateDdl}";
            }
            else
            {
                message = $"트리거가 쓰는 시퀀스 {sequence.Owner}.{sequence.Name} 을(를) 만들지 못했습니다({state.Error}). 직접 만드세요: {sequence.CreateDdl}";
            }

            table.Degrade(message);
            run.Notices.Add($"{table.Name}: {message}");
        }

        if (dropDefaults.Count > 0)
        {
            var built = BuildCreate(plan with { Source = PrecheckRules.WithoutDefaults(plan.Source, dropDefaults) }, owner, new HashSet<string>(StringComparer.Ordinal));
            table.CreateDdl = built.Statements;
            table.AfterLoadDdl = built.AfterLoad;
        }
    }

    /// <summary>
    /// 적재 뒤(대상 트랜잭션을 끝낸 뒤, 트리거보다 먼저) 이번에 만든 시퀀스를 쓰는 표의 키 최댓값을 보고, 첫 NEXTVAL(START WITH)이 그 이하면 그 위로 맞춘다.
    /// 갓 만든 시퀀스는 아직 쓰이지 않았다(적재는 값을 넣고, 트리거는 이 뒤에 만든다): NEXTVAL 한 번 → 차이만큼 INCREMENT BY 를 바꿔 NEXTVAL 한 번 → 원래 증가값.
    /// DROP 없이 ALTER 만 쓴다(11g·12c 이상·Tibero 공통, 위험 명령 확인이 필요 없다). 실패하면 알림에 직접 실행할 문장을 남긴다.
    /// </summary>
    private async Task AdjustCreatedSequencesAsync(RunState run)
    {
        foreach (var state in run.Sequences.Values.Where(s => s.Created))
        {
            var plan = state.Plan;
            var users = state.Users.Where(u => u.Table is { Created: true, Purged: false, LoadCompleted: true } && u.Table.Loaded > 0 && u.KeyColumns.Count > 0).ToList();
            if (users.Count == 0)
            {
                continue;
            }

            decimal? max = null;
            string? maxAt = null;
            try
            {
                using var step = new CancellationTokenSource(LongCleanupTimeout);
                foreach (var (t, columns) in users)
                {
                    foreach (var column in columns)
                    {
                        var query = CopyCommonStatements.Query($"SELECT MAX({SqlIdentifier.QuoteAlways(column)}) FROM {CopyCommonStatements.Qualified(t.Owner, t.TableName)}");
                        run.LogStatement(run.Target, query.Text);
                        if (_codec.NumberText(await ScalarAsync(run, run.Target, query, NoBinds, step.Token)) is { } text
                            && CopySequenceDdl.TryNumber(text, out var value) && (max is null || value > max))
                        {
                            max = value;
                            maxAt = $"{t.TableName}.{column} = {text}";
                        }
                    }
                }
            }
            catch (Exception ex) when (IsCleanupFailure(ex))
            {
                run.Notices.Add($"시퀀스 {plan.Owner}.{plan.Name}: 적재한 키 최댓값을 읽지 못해 맞추지 않았습니다({SafeError(ex.Message)}). 새 행의 키가 겹칠 수 있습니다.");
                continue;
            }

            if (max is not { } top || CopySequenceDdl.Adjustment(plan.Spec, top) is not { } adjust)
            {
                continue; // 비었거나 이미 키 최댓값 위에서 시작한다
            }

            var restore = CopySequenceDdl.AlterIncrement(plan.Owner, plan.Name, CopySequenceDdl.TryNumber(plan.Spec.Increment, out var inc) ? inc : 1);
            var ok = await NextValueAsync(run, plan);
            if (ok && adjust.Gap > 0)
            {
                var widen = await TryRestoreAsync(run, CopySequenceDdl.AlterIncrement(plan.Owner, plan.Name, adjust.Gap), CleanupTimeout);
                var widened = widen is { Status: ExecutionStatus.Succeeded };
                ok = widened && await NextValueAsync(run, plan);
                if (widened)
                {
                    var back = await TryRestoreAsync(run, restore, CleanupTimeout);
                    if (back is not { Status: ExecutionStatus.Succeeded })
                    {
                        run.Notices.Add($"시퀀스 {plan.Owner}.{plan.Name}: 맞추는 중 증가값을 되돌리지 못했습니다({DescribeRestore(back)}). 직접 실행하세요: {restore}");
                        continue;
                    }
                }
            }

            var number = adjust.Next.ToString(CultureInfo.InvariantCulture);
            if (!ok)
            {
                run.Notices.Add($"시퀀스 {plan.Owner}.{plan.Name}: 적재한 키 최댓값({maxAt}) 뒤로 맞추지 못했습니다. 다음 값이 {number} 이상이 되게 직접 맞추세요. 새 행의 키가 겹칠 수 있습니다.");
                continue;
            }

            run.Notices.Add(CopySequenceDdl.Exceeds(plan.Spec, adjust.Next)
                ? $"시퀀스 {plan.Owner}.{plan.Name}: 적재한 키 최댓값({maxAt}) 뒤로 맞췄지만 다음 값 {number} 이(가) 원본 MAXVALUE 를 넘어 NEXTVAL 이 실패합니다. 시퀀스 한도를 늘리세요."
                : $"시퀀스 {plan.Owner}.{plan.Name}: 적재한 키 최댓값({maxAt}) 뒤로 맞췄습니다(다음 값 {number}).");
        }
    }

    /// <summary>시퀀스 NEXTVAL 하나를 소비한다(<see cref="ICopyDialect.NextValues"/>, 개수 1). 실패하면 false.</summary>
    private async Task<bool> NextValueAsync(RunState run, CopySequencePlan plan)
    {
        try
        {
            using var step = new CancellationTokenSource(CleanupTimeout);
            var next = _dialect.NextValues(plan.Owner, plan.Name);
            run.LogStatement(run.Target, next.Text);
            return _codec.NumberText(await ScalarAsync(run, run.Target, next, CountBind(1), step.Token)) is not null;
        }
        catch (Exception ex) when (IsCleanupFailure(ex))
        {
            run.Log($"시퀀스 {plan.Owner}.{plan.Name} 의 NEXTVAL 을 읽지 못했습니다: {SafeError(ex.Message)}", CopyLogKind.Warning);
            return false;
        }
    }

    /// <summary>
    /// 이번 실행이 만든 시퀀스 가운데, 쓰는 표를 하나도 남기지 못한 것(모두 만들지 못했거나 적재를 끝내지 못해 지움)을 지운다(대상 트랜잭션을 끝낸 뒤, DDL).
    /// 지우는 문장은 위험 명령이라 사전 점검 미리 보기에 같은 글이 있다. 지우지 못하면 알림에 직접 지울 문장을 남긴다.
    /// </summary>
    private async Task DropUnusedSequencesAsync(RunState run)
    {
        foreach (var state in run.Sequences.Values.Where(s => s.Created && !s.Keep && s.Users.All(u => u.Table.Purged || !u.Table.Created)).Reverse().ToList())
        {
            var drop = CopyCommonStatements.DropCreatedSequence(state.Plan.Owner, state.Plan.Name);
            ExecutionResult? r = null;
            try
            {
                r = await CleanupStatementAsync(run, drop, CleanupTimeout);
            }
            catch (Exception ex) when (IsCleanupFailure(ex))
            {
                run.Log("만든 시퀀스를 지우지 못했습니다: " + SafeError(ex.Message), CopyLogKind.Warning);
            }

            var tables = string.Join(", ", state.Users.Select(u => u.Table.Name).Distinct(StringComparer.Ordinal));
            if (r is { Status: ExecutionStatus.Succeeded })
            {
                state.Created = false;
                run.Notices.Add($"시퀀스 {state.Plan.Owner}.{state.Plan.Name}: 쓰는 테이블({tables})을 남기지 못해 이번에 만든 시퀀스를 지웠습니다.");
            }
            else
            {
                run.Notices.Add($"시퀀스 {state.Plan.Owner}.{state.Plan.Name}: 쓰는 테이블({tables})을 남기지 못했고 이번에 만든 시퀀스를 지우지 못했습니다. 직접 지우세요: {drop.Text}");
            }
        }
    }
}
