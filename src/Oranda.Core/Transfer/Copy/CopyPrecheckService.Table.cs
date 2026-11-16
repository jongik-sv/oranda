using Oranda.Core.Metadata;

namespace Oranda.Core.Transfer.Copy;

public partial class CopyPrecheckService
{
    private sealed partial class Run
    {
        // ---- 표 하나 ----

        private async Task<CopyTablePlan> PrepareTableAsync(TableRule rule, CancellationToken ct)
        {
            var key = Key(rule);
            progress?.Report($"{key}: 점검 중");
            var source = _sourceTables[key];
            var target = _targetTables[key];

            // 교차 복사(DBMS 가 다름): 옮길 형식이 없는 열은 오류가 아니라 복사 열에서 빠지고(ColumnDropped), 실행기가 SELECT·DML 에 넣는 열과 같은 목록이 된다.
            // 대상 구조가 원본에서 만들어지는 표(없으면 만들기·다시 만들기)는 대상 열 형식을 옮긴 형식으로 본다.
            var fromSource = _creating.Contains(key) || (IsRecreate(rule) && CanRecreate);
            _items.AddRange(PrecheckRules.CompareColumns(sourceParts.Codec, codec, source, target, rule, key, sourceParts.Dbms, targetDbms, fromSource));
            _items.AddRange(PrecheckRules.SameDbmsXmlTypeNotes(source, rule, key, sourceParts.Dbms, targetDbms));
            var dropped = PrecheckRules.CrossDroppedColumns(sourceParts.Codec, source, target, rule, sourceParts.Dbms, targetDbms);
            var columns = PrecheckRules.SelectColumns(sourceParts.Codec, codec, source, target, rule, sourceParts.Dbms, targetDbms, fromSource);
            if (PrecheckRules.CrossBlobNote(sourceParts.Dbms, targetDbms, source, columns, key) is { } blob)
            {
                _items.Add(blob);
            }

            if (PrecheckRules.CrossStringByteLimitNote(sourceParts.Dbms, targetDbms, source, target, columns, key) is { } bytes)
            {
                _items.Add(bytes);
            }

            if (PrecheckRules.CrossCharPadNote(sourceParts.Dbms, targetDbms, source, target, columns, key) is { } pad)
            {
                _items.Add(pad);
            }

            var keys = PrecheckRules.KeyColumns(target, columns);
            _items.AddRange(PrecheckRules.CheckMasks(rule, source, keys, key, dropped));

            var creating = _creating.Contains(key);

            // 교차: PK 열이 옮길 형식이 없어 빠졌으면 KeyColumnDropped 오류가 이미 있다. 「PK 없음」은 그 이유를 가리므로 내지 않는다.
            var keyDropped = dropped.Any(c => IsPrimaryKeyColumn(source, c) || IsPrimaryKeyColumn(target, c));
            if (keys.Count == 0 && !keyDropped)
            {
                // 새로 만드는 표는 비어 있어 INSERT 로 넣는다: PK 기준 MERGE 에 쓸 키가 없어도 막지 않는다.
                _items.Add(rule.Action == TargetAction.MergeByPk && !creating
                    ? new CheckItem(key, "NoPrimaryKey", CheckLevel.Error,
                        $"{key}: PK 기준 MERGE 인데 대상에 쓸 수 있는 PK 가 없습니다. 대상 처리를 바꾸세요.")
                    : new CheckItem(key, "NoPrimaryKey", CheckLevel.Confirm,
                        $"{key}: PK 없음. 다시 실행하면 행이 겹칠 수 있습니다."));
            }

            var readable = await CheckSourcePrivilegeAsync(rule, ct).ConfigureAwait(false);
            var canDisableTriggers = false;
            IReadOnlyList<string> triggers = [];
            var noTargetTriggers = false;
            if (creating)
            {
                // 대상에 표가 없어 만든다: 옛 대상의 제약·권한·트리거·자식 FK 가 없다. 새 표라 지우지도 않는다.
                await CheckCreateAsync(rule, source, ct).ConfigureAwait(false);
            }
            else if (IsRecreate(rule))
            {
                // 지우고 다시 만들므로 옛 대상의 제약·권한 비교는 뜻이 없다. 트리거는 적재 뒤에 되살리므로 끌 것도 없다.
                await CheckRecreateAsync(rule, source, ct).ConfigureAwait(false);
            }
            else
            {
                await CheckTargetPrivilegesAsync(rule, keys, ct).ConfigureAwait(false);
                AddTargetOnlyConstraints(rule, source, target);
                (canDisableTriggers, triggers, noTargetTriggers) = await CheckTriggersAsync(rule, ct).ConfigureAwait(false);
            }

            IReadOnlyList<DisabledObject> disablable = creating ? [] : await CheckChildForeignKeysAsync(rule, keys, ct).ConfigureAwait(false);
            var companions = creating || (IsRecreate(rule) && CanRecreate)
                ? await PlanCompanionsAsync(rule, key, source, creating, ct).ConfigureAwait(false)
                : CompanionDdl.Empty;

            if (readable)
            {
                await CountAsync(rule, ct).ConfigureAwait(false);
                await CheckStringDatesAsync(rule, ct).ConfigureAwait(false);
            }

            return new CopyTablePlan(rule, source, target, columns, keys)
            {
                CreateMissing = creating,
                CreateSequences = _sequencePlans.GetValueOrDefault(key) ?? [],
                Companions = companions,
                CanDisableTriggers = canDisableTriggers,
                Triggers = triggers,
                NoTargetTriggers = noTargetTriggers,
                DisablableForeignKeys = disablable,
            };
        }

        private static bool IsPrimaryKeyColumn(TableInfo table, string column)
            => table.Constraints.FirstOrDefault(c => c.Kind == ConstraintKind.PrimaryKey)?.Columns.Contains(column, StringComparer.Ordinal) == true;

        private async Task<bool> CheckSourcePrivilegeAsync(TableRule rule, CancellationToken ct)
        {
            var held = await CopyCatalogQueries.ReadObjectPrivilegesAsync(bulk, sourceParts.Catalog, sourceParts.Codec, Source, _sourceAccess, SourceOwner(rule), rule.Table, "TABLE",
                    sourceParts.PrecheckDialect.SupportsReadPrivilege, ct)
                .ConfigureAwait(false);
            if (held.Contains("SELECT"))
            {
                return true;
            }

            _items.Add(new CheckItem(Key(rule), "SourceNoSelect", CheckLevel.Error, $"{Key(rule)}: 원본 테이블 {SourceOwner(rule)}.{rule.Table} 에 SELECT 권한이 없습니다."));
            return false;
        }

        private async Task CheckTargetPrivilegesAsync(TableRule rule, IReadOnlyList<string> keys, CancellationToken ct)
        {
            var held = await CopyCatalogQueries.ReadObjectPrivilegesAsync(bulk, catalog, codec, Target, _targetAccess, TargetOwner(rule), rule.Table, "TABLE", dialect.SupportsReadPrivilege, ct)
                .ConfigureAwait(false);
            var missing = PrecheckRules.RequiredTargetPrivileges(rule, keys).Where(p => !held.Contains(p)).ToList();
            if (missing.Count > 0)
            {
                _items.Add(new CheckItem(Key(rule), "TargetNoWrite", CheckLevel.Error,
                    $"{Key(rule)}: 대상 테이블 {TargetOwner(rule)}.{rule.Table} 에 필요한 권한이 없습니다: {string.Join(", ", missing)}."));
            }
        }

        /// <summary>
        /// 「테이블 다시 만들기」 점검(조정 결정 9·10). 대상 세션이 대상 소유자이고 CREATE TABLE(트리거가 있으면 CREATE TRIGGER)이 있어야 한다:
        /// 비소유자는 트리거 DDL(GET_DDL)·권한 되살리기에 더 큰 권한이 들어 DROP 뒤에 되살리지 못할 수 있다.
        /// 통과하면 되돌릴 수 없음·휴지통·만들지 않는 FK 를 Confirm 항목 하나로 알린다.
        /// </summary>
        private async Task CheckRecreateAsync(TableRule rule, TableInfo source, CancellationToken ct)
        {
            if (!CanRecreate)
            {
                return; // RecreateNotAllowed 를 이미 냈다.
            }

            var key = Key(rule);
            var owner = TargetOwner(rule);
            var rows = await CopyCatalogQueries.QueryAsync(bulk, Target, TargetSql("copy.trigger_count_all"), ct,
                ("OWN", owner), ("TBL", rule.Table)).ConfigureAwait(false);
            var triggerCount = codec.ToInt64(rows.FirstOrDefault()?[0]) ?? 0;

            // 표마다 본다: 계정이 섞인 작업에서 접속 계정이 아닌 계정의 표는 다시 만들 수 없다.
            if (!_targetAccess.IsOwner(owner))
            {
                _items.Add(new CheckItem(key, "RecreateNoPrivilege", CheckLevel.Error,
                    $"{key}: 「테이블 다시 만들기」는 대상 소유자({owner}) 계정으로 접속해야 합니다(지금 {_targetAccess.User})."));
                return;
            }

            var missing = new List<string>();
            if (!_targetAccess.Has("CREATE TABLE") && !_targetAccess.Has("CREATE ANY TABLE"))
            {
                missing.Add("CREATE TABLE");
            }

            if (triggerCount > 0 && !_targetAccess.Has("CREATE TRIGGER") && !_targetAccess.Has("CREATE ANY TRIGGER"))
            {
                missing.Add("CREATE TRIGGER");
            }

            if (missing.Count > 0)
            {
                _items.Add(new CheckItem(key, "RecreateNoPrivilege", CheckLevel.Error,
                    $"{key}: 테이블을 지우고 다시 만들 권한이 없습니다: {string.Join(", ", missing)}."));
                return;
            }

            // 대상 계정이 같은 다시 만들기 표(먼저 지워지므로 이름 겹침에서 뺀다), 그중 원본 계정도 같은 표(작업 안 FK 를 원본대로 붙인다).
            var recreatedHere = RecreatedTables(owner, sourceOwner: null);
            var linked = BuiltTables(owner, SourceOwner(rule));
            var blocked = await RecreateBlockersAsync(rule, source, recreatedHere, linked, creating: false, ct).ConfigureAwait(false);
            if (blocked.Count > 0)
            {
                // DROP 뒤에야 드러나는 CREATE 실패(표가 휴지통에만 남음)를 지우기 전에 막는다(조정 결정 16).
                _items.Add(new CheckItem(key, "RecreateBlocked", CheckLevel.Error,
                    $"{key}: 지운 뒤 원본 구조로 다시 만들 수 없어 「테이블 다시 만들기」를 쓸 수 없습니다: {string.Join(" / ", blocked)}."));
                return;
            }

            var tablespace = codec.ToText((await CopyCatalogQueries.QueryAsync(bulk, Target, TargetSql("copy.tablespace"), ct,
                ("OWN", owner), ("TBL", rule.Table)).ConfigureAwait(false)).FirstOrDefault()?[0]);
            var message = $"{key}: 대상 테이블 {owner}.{rule.Table} 을(를) 지우고 원본 구조로 다시 만든 뒤 넣습니다."
                          + dialect.RecycleBinNote(tablespace)
                          + " DROP·CREATE 는 DDL 이라 커밋 방식과 상관없이 되돌릴 수 없습니다."
                          + $" 대상의 트리거 {Number(triggerCount)}개·다른 계정에 준 권한·이 테이블을 참조하던 FK 는 적재 뒤 되살립니다"
                          + "(참조 FK 는 지우기 전에 떼어 내고, 대상 계정에 보이지 않는 FK 가 있으면 지우지 않고 멈춥니다).";
            message += " 원본의 함수 기반 인덱스·작업 밖 부모 FK 는 적재 뒤 함께 만듭니다(「함께 만드는 객체」 항목). 파티션·압축·테이블스페이스·저장 속성은 대상 기본값을 씁니다.";

            message += CrossSkippedForeignKeyNote(rule, source, linked);
            message += dialect.RecreateDifferenceNote(source.Columns.Any(c => c.Domain is not null)) + CrossCreateNote(source) + LargeCharNote(source, recreating: true);
            message += await dialect.RecreateTableNoteAsync(new RecreateNoteInput(owner, rule.Table, sourceParts.Dbms, JobForeignKeyNames(rule, source, linked),
                (id, token) => TargetRowsAsync(id, owner, rule.Table, token)), ct).ConfigureAwait(false);
            var lost = await NotRestoredAsync(rule, ct).ConfigureAwait(false);
            if (lost.Count > 0)
            {
                message += $" 되살리지 않는 것: {string.Join(", ", lost)}.";
            }

            _items.Add(new CheckItem(key, "RecreateTable", CheckLevel.Confirm, message));
        }

        /// <summary>대상 계정이 <paramref name="targetOwner"/> 인 다시 만들기 표 이름(<paramref name="sourceOwner"/> 를 주면 원본 계정도 같은 것만).</summary>
        private HashSet<string> RecreatedTables(string targetOwner, string? sourceOwner)
            => job.Tables.Where(r => r.Kind != RuleKind.Exclude && IsRecreate(r)
                                     && CopyOwners.SameOwner(TargetOwner(r), targetOwner)
                                     && (sourceOwner is null || CopyOwners.SameOwner(SourceOwner(r), sourceOwner)))
                .Select(r => r.Table).ToHashSet(StringComparer.Ordinal);

        /// <summary>
        /// 이번 실행이 만드는 표(다시 만들기 + 없으면 만들기) 가운데 대상 계정이 <paramref name="targetOwner"/>, 원본 계정이 <paramref name="sourceOwner"/> 인 표 이름.
        /// 작업 안 FK 를 원본대로 붙이는 부모 후보다(실행기 <c>CopyRunner</c> 의 <c>planned</c> 와 같은 판정).
        /// </summary>
        private HashSet<string> BuiltTables(string targetOwner, string sourceOwner)
            => job.Tables.Where(r => r.Kind != RuleKind.Exclude && (IsRecreate(r) || _creating.Contains(Key(r)))
                                     && CopyOwners.SameOwner(TargetOwner(r), targetOwner)
                                     && CopyOwners.SameOwner(SourceOwner(r), sourceOwner))
                .Select(r => r.Table).ToHashSet(StringComparer.Ordinal);

        /// <summary>
        /// 「없으면 만들기」 점검(D4): 대상 소유자 계정으로 접속해 CREATE TABLE 이 있어야 하고, 원본 구조를 만들 수 없는 이유(<see cref="RecreateBlockersAsync"/> 와 같은 조건)가 없어야 한다.
        /// 통과하면 만드는 것·안 만드는 것·적재 실패 때의 정리를 Confirm 항목 하나로 알린다.
        /// </summary>
        private async Task CheckCreateAsync(TableRule rule, TableInfo source, CancellationToken ct)
        {
            var key = Key(rule);
            var owner = TargetOwner(rule);
            if (!_targetAccess.IsOwner(owner))
            {
                _items.Add(new CheckItem(key, CopyCheckCodes.CreateNoPrivilege, CheckLevel.Error,
                    $"{key}: 「없으면 만들기」는 대상 소유자({owner}) 계정으로 접속해야 합니다(지금 {_targetAccess.User})."));
                return;
            }

            if (!_targetAccess.Has("CREATE TABLE") && !_targetAccess.Has("CREATE ANY TABLE"))
            {
                _items.Add(new CheckItem(key, CopyCheckCodes.CreateNoPrivilege, CheckLevel.Error,
                    $"{key}: 테이블을 만들 권한이 없습니다: CREATE TABLE."));
                return;
            }

            var linked = BuiltTables(owner, SourceOwner(rule));
            var blocked = await RecreateBlockersAsync(rule, source, RecreatedTables(owner, sourceOwner: null), linked, creating: true, ct).ConfigureAwait(false);
            if (blocked.Count > 0)
            {
                _items.Add(new CheckItem(key, CopyCheckCodes.CreateBlocked, CheckLevel.Error,
                    $"{key}: 원본 구조로 만들 수 없어 「없으면 만들기」를 쓸 수 없습니다: {string.Join(" / ", blocked)}."));
                return;
            }

            var message = $"{key}: 대상에 테이블 {owner}.{rule.Table} 이(가) 없어 원본 구조로 만든 뒤 넣습니다(만드는 문장은 미리 보기에 있습니다). 새 표라 대상을 지우지 않습니다."
                          + " 넣기가 실패하면 만든 테이블을 휴지통 없이 지웁니다(DROP … PURGE)."
                          + " 원본의 트리거·권한·함수 기반 인덱스·작업 밖 부모 FK 는 적재 뒤 함께 만듭니다(「함께 만드는 객체」 항목)."
                          + " 만들지 않는 것: 시노님, 파티션·압축·테이블스페이스·저장 속성(대상 기본값을 씁니다).";

            message += CrossSkippedForeignKeyNote(rule, source, linked);
            message += dialect.RecreateDifferenceNote(source.Columns.Any(c => c.Domain is not null)) + CrossCreateNote(source) + LargeCharNote(source, recreating: false);
            _items.Add(new CheckItem(key, CopyCheckCodes.CreateTable, CheckLevel.Confirm, message));
        }
    }
}
