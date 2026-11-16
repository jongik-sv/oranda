
namespace Oranda.Core.Transfer.Copy;

public partial class CopyPrecheckService
{
    private sealed partial class Run
    {
        // ---- 읽기 일관성 ----

        private async Task CheckReadConsistencyAsync(CancellationToken ct)
        {
            if (!job.ConsistentRead)
            {
                return;
            }

            // 같은 시점 읽기는 원본의 일이라 파라미터·안내 글 모두 원본 방언에서 꺼낸다(같은 DBMS 면 예전과 같다).
            var sourceDialect = sourceParts.PrecheckDialect;
            long? undo = null;
            if (sourceDialect.ReadConsistencyParameter is { } parameter)
            {
                try
                {
                    var rows = await CopyCatalogQueries.QueryAsync(bulk, Source, SourceSql("copy.parameter_value"), ct,
                        ("PNAME", parameter)).ConfigureAwait(false);
                    undo = sourceParts.Codec.ToInt64(rows.FirstOrDefault()?[0]);
                }
                catch (Exception ex) when (CopyErrors.IsServer(errors, ex))
                {
                    // V$PARAMETER 를 읽을 권한이 없으면 알 수 없음으로 둔다.
                }
            }

            // 못 읽었으면(null) 어림 시간은 글에 쓰이지 않으므로 계산하지 않는다(옮기기 전과 같음).
            var seconds = undo is null ? 0 : (long)Math.Ceiling(_estimated.Values.Sum() / AssumedRowsPerSecond);
            var (level, message) = sourceDialect.ReadConsistencyHint(seconds, undo);
            _items.Add(new CheckItem(null, "ReadConsistency", level, message));
        }

        // ---- 같은 시점 읽기 + 동시 복사(PF2b) ----

        /// <summary>
        /// 같은 시점 읽기를 켠 채 동시에 복사한다: 시작 시점으로 읽는다는 항목(<see cref="CopyCheckCodes.ReadPoint"/>, 실행기가 이 항목을 보고 동시로 돈다)과,
        /// 다른 계정의 원본 테이블(부모 키 규칙의 부모·조부모 포함)마다 시점 지정 읽기 권한(Oracle FLASHBACK 객체 권한 또는 FLASHBACK ANY TABLE)을 본다.
        /// </summary>
        private async Task CheckReadPointAsync(IReadOnlyList<CopyTablePlan> order, CancellationToken ct)
        {
            if (sourceParts.Dialect.ReadPoint is not { } support)
            {
                // 판정(CopyReadPointDbms)과 원본 방언이 어긋남: 실행하면 시점을 잡지 못해 멈추므로 여기서 막는다.
                _items.Add(new CheckItem(null, CopyCheckCodes.ReadPoint, CheckLevel.Error,
                    $"원본 DBMS({sourceParts.Dbms})는 시점 지정 읽기를 쓰지 않아 같은 시점 읽기로 동시에 복사할 수 없습니다. 동시 테이블 수를 1로 하세요."));
                return;
            }

            _readPoint = support;
            _items.Add(new CheckItem(null, CopyCheckCodes.ReadPoint, CheckLevel.Pass,
                $"복사를 시작할 때 원본 {support.Name} 하나를 잡아 모든 테이블(부모 키 규칙의 부모 포함)을 그 시점({support.Clause})으로 읽습니다. 그 {support.Name} 은 작업 로그와 결과에 남습니다."
                + $" 복사가 오래 걸리면 그동안의 원본 UNDO 가 남아 있어야 합니다(UNDO_RETENTION). 모자라면 {support.SnapshotTooOldErrors ?? "오류"} 로 멈추고 지금 데이터로 바꿔 읽지 않습니다."
                + " 사용자 조건 안의 하위 질의는 지금 데이터를 읽습니다."));

            var seen = new HashSet<(string Owner, string Table)>();
            foreach (var plan in order)
            {
                var key = Key(plan.Rule);
                foreach (var (owner, table) in ReadPointTables(plan.Rule))
                {
                    if (!seen.Add((owner, table)) || _sourceAccess.IsOwner(owner) || _sourceAccess.Has(support.SystemPrivilege))
                    {
                        continue;
                    }

                    var held = await CopyCatalogQueries.ReadObjectPrivilegesAsync(bulk, sourceParts.Catalog, sourceParts.Codec, Source, _sourceAccess, owner, table, "TABLE",
                            sourceParts.PrecheckDialect.SupportsReadPrivilege, ct)
                        .ConfigureAwait(false);
                    if (!held.Contains(support.ObjectPrivilege))
                    {
                        // 권한 이름을 실측하지 못한 DBMS(PF2b2 Tibero)는 막지 않고 확인 필요로 둔다: 실제로 권한이 없으면 그 테이블만 실패한다(지금 데이터로 바꿔 읽지 않음).
                        _items.Add(support.PrivilegeUnverified
                            ? new CheckItem(key, CopyCheckCodes.FlashbackNoPrivilege, CheckLevel.Confirm,
                                $"{key}: 같은 시점 읽기로 동시에 복사하면 원본 {owner}.{table} 을(를) 시작 {support.Name} 시점으로 읽는데, 이 계정에 그 테이블의 {support.ObjectPrivilege} 권한"
                                + $"({support.SystemPrivilege} 포함)이 보이지 않습니다. 원본 {sourceParts.Dbms} 에서 이 권한이 필요한지는 확인되지 않았습니다."
                                + " 그 시점으로 읽지 못하면 그 테이블은 실패하고 지금 데이터로 바꿔 읽지 않습니다. 걱정되면 동시 테이블 수를 1로 하세요.")
                            : new CheckItem(key, CopyCheckCodes.FlashbackNoPrivilege, CheckLevel.Error,
                                $"{key}: 같은 시점 읽기로 동시에 복사하면 원본 {owner}.{table} 을(를) 시작 {support.Name} 시점으로 읽는데, 이 계정에 그 테이블의 {support.ObjectPrivilege} 권한"
                                + $"({support.SystemPrivilege} 포함)이 없습니다. 권한을 받거나, 동시 테이블 수를 1로 하거나, 같은 시점 읽기를 끄세요."));
                    }
                }
            }
        }

        /// <summary>규칙의 원본 SELECT 가 시점 지정으로 읽는 표: 원본 표, 부모 키 규칙이면 원본 쪽 부모(작업 안의 부모가 또 부모 키 규칙이면 그 부모까지).</summary>
        private List<(string Owner, string Table)> ReadPointTables(TableRule rule)
        {
            var tables = new List<(string Owner, string Table)> { (SourceOwner(rule), rule.Table) };
            var visiting = new HashSet<string>(StringComparer.Ordinal) { Key(rule) };
            var current = rule;
            while (current.Kind == RuleKind.ParentKey && current.Parent is { } link)
            {
                tables.Add((CopyOwners.ParentSource(job, current), link.ParentTable));
                var parentKey = CopyOwners.ParentKey(job, current);
                if (!visiting.Add(parentKey) || CopyOwners.Find(job, parentKey) is not { } parent)
                {
                    break;
                }

                current = parent;
            }

            return tables;
        }
    }
}
