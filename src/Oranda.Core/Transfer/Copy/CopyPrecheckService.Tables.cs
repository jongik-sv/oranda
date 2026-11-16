using Oranda.Core.Connection;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;
using Oranda.Core.Text;

namespace Oranda.Core.Transfer.Copy;

public partial class CopyPrecheckService
{
    private sealed partial class Run
    {
        // ---- 표 구조 ----

        private async Task LoadTablesAsync(List<TableRule> active, CancellationToken ct)
        {
            foreach (var rule in active)
            {
                var key = Key(rule);
                var sourceOwner = SourceOwner(rule);
                var targetOwner = TargetOwner(rule);
                progress?.Report($"{key}: 구조 읽는 중");
                if (IsRecreate(rule) && !CanRecreate)
                {
                    _items.Add(new CheckItem(key, "RecreateNotAllowed", CheckLevel.Error,
                        $"{key}: 대상 접속 '{Target.Profile.Name}' 이 개발 접속이 아니라 「테이블 다시 만들기」를 쓸 수 없습니다. 대상 처리를 바꾸세요."));
                }

                await CheckWrittenAsAsync(rule, key, ct).ConfigureAwait(false);

                var source = await TryGetTableAsync(Source, sourceOwner, rule.Table, ct).ConfigureAwait(false);
                if (source is null)
                {
                    var hint = rule.WrittenAs is null && await IsSynonymAsync(sourceOwner, rule.Table, ct).ConfigureAwait(false)
                        ? " 시노님 이름이면 원본 접속을 연 뒤 테이블을 다시 추가하세요(실제 계정·테이블로 풀어 저장합니다)."
                        : string.Empty;
                    _items.Add(new CheckItem(key, "SourceNoSelect", CheckLevel.Error,
                        $"{key}: 원본에 테이블 {sourceOwner}.{rule.Table} 이(가) 없거나 SELECT 권한이 없습니다(권한이 없으면 테이블이 없는 것처럼 보입니다).{hint}"));
                }

                // 대상은 표여야 한다(뷰·시노님·MV 에 지우고 넣지 않는다). 표가 아니거나 확인하지 못하면(조회 오류) 구조를 읽지 않고 막는다.
                var isTable = await TryCountAsync(TargetSql("copy.target_is_table"), ct, ("OWN", targetOwner), ("TBL", rule.Table)).ConfigureAwait(false);
                var target = isTable is > 0 ? await TryGetTableAsync(Target, targetOwner, rule.Table, ct).ConfigureAwait(false) : null;

                // 「없으면 만들기」: 대상 계정에 그 이름의 객체가 아무것도 없을 때만 원본 구조로 만든다(대상 표가 있으면 선택을 무시한다).
                string? createRefused = null;
                if (target is null && source is not null && rule.CreateIfMissing && !IsRecreate(rule) && isTable == 0)
                {
                    createRefused = await CreateRefusedAsync(targetOwner, rule.Table, ct).ConfigureAwait(false);
                    if (createRefused is null)
                    {
                        source = await PlanLinkedSequencesAsync(rule, key, source, creating: true, ct).ConfigureAwait(false);
                        _sourceTables[key] = source;
                        _targetTables[key] = PrecheckRules.AsRecreated(source, targetOwner);
                        _creating.Add(key);
                        continue;
                    }
                }

                if (target is null)
                {
                    _items.Add(new CheckItem(key, "TargetMissing", CheckLevel.Error, isTable is null
                        ? $"{key}: 대상 {targetOwner}.{rule.Table} 이(가) 표인지 확인하지 못했습니다(ALL_TABLES·ALL_MVIEWS 조회 실패). 확인할 수 있을 때까지 실행하지 않습니다."
                        : $"{key}: 대상에 테이블 {targetOwner}.{rule.Table} 이(가) 없거나 테이블이 아니거나(뷰·시노님·MV) 접근 권한이 없습니다(권한이 없으면 테이블이 없는 것처럼 보입니다)."
                          + (createRefused is null ? string.Empty : $" 「없으면 만들기」를 쓸 수 없습니다: {createRefused}")));
                }

                if (source is null || target is null)
                {
                    continue;
                }

                if (IsRecreate(rule) && CanRecreate)
                {
                    source = await PlanLinkedSequencesAsync(rule, key, source, creating: false, ct).ConfigureAwait(false);
                }

                _sourceTables[key] = source;
                // 다시 만들 표는 적재할 때 원본 구조다: 열·키·문장을 옛 대상 구조가 아니라 다시 만든 구조로 정한다.
                _targetTables[key] = IsRecreate(rule) && CanRecreate
                    ? PrecheckRules.AsRecreated(source, targetOwner)
                    : WithAddedColumns(rule, key, source, await AdjustIdentityAsync(target, targetOwner, ct).ConfigureAwait(false));
            }
        }

        /// <summary>
        /// 표를 만들 때(없으면 만들기·다시 만들기, 같은 DBMS·교차 모두) 원본 테이블에 연결된 시퀀스를 정한다(조정 결정 76, 결정 50 을 대신함).
        /// 연결된 시퀀스 = 열 DEFAULT 의 <c>시퀀스.NEXTVAL</c> + (<paramref name="creating"/> 일 때) 그 테이블 트리거가 참조하는 시퀀스(ALL_DEPENDENCIES — 원본 트리거는 새 표에만 만든다).
        /// 원본 소유자(또는 소유자 없음)의 시퀀스가 대상 소유자에 없으면 테이블보다 먼저 원본 속성으로 만든다(<see cref="CopySequencePlan"/>, <see cref="CopyCheckCodes.SequenceCreated"/>).
        /// 만들 수 없으면(CREATE SEQUENCE 권한 없음·원본 속성 못 읽음) 그 열의 DEFAULT 만 빼고 만든다(<see cref="CopyCheckCodes.SequenceDefaultDropped"/>).
        /// 다른 소유자의 시퀀스는 만들지 않는다: 대상에 없으면 DEFAULT 를 뺀다. 대상에 이미 있으면 건드리지 않고, 다음 값이 원본 키 최댓값 이하면 알린다(<see cref="CopyCheckCodes.SequenceBehind"/>).
        /// Oracle 11g 대상은 시퀀스 기본값을 쓸 수 없어(12c 부터, ORA-00984) DEFAULT 는 빼되 시퀀스는 만든다(조정 결정 66). 시퀀스 목록을 읽지 못하면 있는 것으로 보고 그대로 둔다.
        /// 뺀 구조를 점검 계획(<see cref="CopyTablePlan.Source"/>)에 담으므로 미리 보기 DDL 과 실행 DDL 이 같다.
        /// </summary>
        private async Task<TableInfo> PlanLinkedSequencesAsync(TableRule rule, string key, TableInfo source, bool creating, CancellationToken ct)
        {
            var targetOwner = TargetOwner(rule);
            var sourceOwner = SourceOwner(rule);
            var oldOracleTarget = targetDbms == Dbms.Oracle && Target.Server.MajorVersion is > 0 and < 12;
            var canCreate = _targetAccess.Has("CREATE SEQUENCE") || _targetAccess.Has("CREATE ANY SEQUENCE");

            // (열 이름 또는 null = 트리거, 소유자, 시퀀스) — 나온 순서.
            var uses = new List<(string? Column, string? Owner, string Name)>();
            foreach (var c in source.Columns.Where(c => !c.IsVirtual && !c.IsIdentity))
            {
                uses.AddRange(PrecheckRules.SequenceReferences(c.DefaultValue).Select(r => ((string?)c.Name, r.Owner, r.Name)));
            }

            if (creating)
            {
                uses.AddRange((await TriggerSequencesAsync(sourceOwner, rule.Table, ct).ConfigureAwait(false)).Select(r => ((string?)null, (string?)r.Owner, r.Name)));
            }

            _sequencePlans[key] = [];
            if (uses.Count == 0)
            {
                return source;
            }

            var known = new Dictionary<string, HashSet<string>?>(CopyOwners.OwnerComparer);
            var drop = new HashSet<string>(StringComparer.Ordinal);
            var plans = new List<CopySequencePlan>();
            var linkedColumns = new HashSet<string>(StringComparer.Ordinal); // 대상 스키마에 있거나 만드는 시퀀스를 쓰는 기본값 열
            foreach (var group in uses.GroupBy(u => (Linked: IsLinkedSequenceOwner(u.Owner, sourceOwner), Owner: IsLinkedSequenceOwner(u.Owner, sourceOwner) ? targetOwner : u.Owner!, u.Name)))
            {
                var (linked, owner, name) = group.Key;
                var columns = group.Select(u => u.Column).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
                if (!known.TryGetValue(owner, out var names))
                {
                    names = await ReadSequenceNamesAsync(owner, ct).ConfigureAwait(false);
                    known[owner] = names;
                }

                if (names is null)
                {
                    continue; // 목록을 읽지 못했다: 있는 것으로 보고 그대로 둔다(결정 50 과 같다)
                }

                var keyColumns = columns.Count > 0 ? columns : TriggerKeyColumn(source);
                if (names.Contains(name))
                {
                    linkedColumns.UnionWith(columns);
                    await CheckExistingSequenceAsync(rule, key, owner, name, keyColumns, ct).ConfigureAwait(false);
                    continue;
                }

                string? reason = null;
                SequenceSpec? spec = null;
                if (!linked)
                {
                    reason = $"대상 {owner} 에 시퀀스 {name} 이(가) 없어(다른 계정의 시퀀스는 만들지 않습니다)";
                }
                else if (!canCreate)
                {
                    reason = $"대상 {owner} 에 시퀀스 {name} 이(가) 없고 CREATE SEQUENCE 권한이 없어 만들 수 없으므로";
                }
                else
                {
                    spec = await ReadSourceSequenceAsync(sourceOwner, name, ct).ConfigureAwait(false);
                    reason = spec is null ? $"대상 {owner} 에 시퀀스 {name} 이(가) 없고 원본 시퀀스 {sourceOwner}.{name} 의 속성을 읽지 못해 만들 수 없으므로" : null;
                }

                if (spec is not null)
                {
                    plans.Add(new CopySequencePlan(owner, name, spec) { KeyColumns = keyColumns, DefaultColumns = columns });
                    linkedColumns.UnionWith(columns);
                    continue;
                }

                foreach (var column in columns.Where(drop.Add))
                {
                    var value = source.Columns.First(c => c.Name == column).DefaultValue!.Trim();
                    _items.Add(new CheckItem(key, CopyCheckCodes.SequenceDefaultDropped, CheckLevel.Confirm,
                        $"{key}: {reason} 열 {column} 의 기본값({value})을 빼고 만듭니다. 시퀀스를 만든 뒤 기본값을 다시 걸어야 합니다.")
                    {
                        Column = column,
                    });
                }
            }

            if (oldOracleTarget)
            {
                foreach (var column in linkedColumns.Where(c => !drop.Contains(c)).Order(StringComparer.Ordinal).ToList())
                {
                    drop.Add(column);
                    var value = source.Columns.First(c => c.Name == column).DefaultValue!.Trim();
                    var made = plans.Any(p => p.DefaultColumns.Contains(column, StringComparer.Ordinal)) ? " 시퀀스는 만듭니다." : string.Empty;
                    _items.Add(new CheckItem(key, CopyCheckCodes.SequenceDefaultDropped, CheckLevel.Confirm,
                        $"{key}: 대상 서버(Oracle {Target.Server.MajorVersion})는 시퀀스를 열 기본값으로 쓸 수 없어(12c 부터 가능) 열 {column} 의 기본값({value})을 빼고 만듭니다.{made} 값은 그대로 복사되며, 새 행에 값을 채우려면 트리거 등으로 따로 처리해야 합니다.")
                    {
                        Column = column,
                    });
                }
            }

            if (plans.Count > 0)
            {
                var list = string.Join(", ", plans.Select(p => $"{p.Owner}.{p.Name}(START WITH {p.Spec.StartWith})"));
                _items.Add(new CheckItem(key, CopyCheckCodes.SequenceCreated, CheckLevel.Confirm,
                    $"{key}: 원본 테이블에 연결된 시퀀스 {list} 이(가) 대상에 없어 테이블보다 먼저 원본 속성(증가·최소·최대·캐시·순환·순서)으로 만듭니다."
                    + " START WITH 는 원본 다음 값이고, 적재 뒤 키 최댓값이 그 이상이면 그 위로 맞춥니다. 테이블을 만들지 못하거나 적재를 끝내지 못해 지우면 이 시퀀스도 지웁니다(만드는 문장은 미리 보기에 있습니다)."));
            }

            _sequencePlans[key] = plans;
            return PrecheckRules.WithoutDefaults(source, drop);
        }

        /// <summary>시퀀스 소유자가 없거나 원본 소유자인가(대상 소유자의 같은 이름 시퀀스를 쓰게 되어 없으면 만든다).</summary>
        private static bool IsLinkedSequenceOwner(string? owner, string sourceOwner) => owner is null || CopyOwners.SameOwner(owner, sourceOwner);

        /// <summary>트리거만 쓰는 시퀀스의 맞추기 기준 열: 숫자 단일 PK 열(없으면 빈 목록 = 맞추지 않는다).</summary>
        private static IReadOnlyList<string> TriggerKeyColumn(TableInfo source)
        {
            var pk = source.Constraints.FirstOrDefault(c => c.Kind == ConstraintKind.PrimaryKey);
            return pk is { Columns.Count: 1 } && IsNumber(source, pk.Columns[0]) ? pk.Columns : [];
        }

        /// <summary>원본 테이블 트리거가 참조하는 시퀀스(<c>copy.trigger_sequences</c>, 원본 세션). 읽지 못하면 빈 목록.</summary>
        private async Task<IReadOnlyList<(string Owner, string Name)>> TriggerSequencesAsync(string owner, string table, CancellationToken ct)
        {
            try
            {
                var rows = await CopyCatalogQueries.QueryAsync(bulk, Source, SourceSql("copy.trigger_sequences"), ct, ("OWN", owner), ("TBL", table)).ConfigureAwait(false);
                var result = new List<(string Owner, string Name)>();
                foreach (var row in rows)
                {
                    if (sourceParts.Codec.ToText(row[0]) is { } o && sourceParts.Codec.ToText(row[1]) is { } n)
                    {
                        result.Add((o, n));
                    }
                }

                return result;
            }
            catch (Exception ex) when (CopyErrors.IsServer(errors, ex))
            {
                return [];
            }
        }

        /// <summary>원본 시퀀스 속성(<c>copy.sequence_attrs</c>, 원본 세션). 없거나 읽지 못하면 null.</summary>
        private async Task<SequenceSpec?> ReadSourceSequenceAsync(string owner, string name, CancellationToken ct)
        {
            try
            {
                var rows = await CopyCatalogQueries.QueryAsync(bulk, Source, SourceSql("copy.sequence_attrs"), ct, ("OWN", owner), ("SEQ", name)).ConfigureAwait(false);
                return rows.Count == 0 ? null : CopySequenceDdl.FromRow(rows[0], v => sourceParts.Codec.ToText(v));
            }
            catch (Exception ex) when (CopyErrors.IsServer(errors, ex))
            {
                return null;
            }
        }

        /// <summary>
        /// 대상에 이미 있는 시퀀스(건드리지 않는다): 다음 값(LAST_NUMBER)이 원본 키 열 최댓값 이하면 확인 항목을 낸다(새 행의 키가 겹칠 수 있다).
        /// 작업에 이 표·시퀀스가 연결돼 있으면 복사 뒤 맞추므로 내지 않는다. 읽지 못하면 아무것도 내지 않는다.
        /// </summary>
        private async Task CheckExistingSequenceAsync(TableRule rule, string key, string owner, string name, IReadOnlyList<string> keyColumns, CancellationToken ct)
        {
            if (keyColumns.Count == 0 || job.Sequences.Any(s => s.Table == key && string.Equals(s.Sequence, name, StringComparison.Ordinal)))
            {
                return;
            }

            try
            {
                var info = await CopyCatalogQueries.QueryAsync(bulk, Target, TargetSql("copy.sequence_info"), ct, ("OWN", owner), ("SEQ", name)).ConfigureAwait(false);
                if (info.Count == 0 || codec.NumberText(info[0][0]) is not { } nextText || !CopySequenceDdl.TryNumber(nextText, out var next))
                {
                    return;
                }

                foreach (var column in keyColumns)
                {
                    var max = await CopyCatalogQueries.QueryAsync(bulk, Source,
                        $"SELECT MAX({SqlIdentifier.QuoteAlways(column)}) FROM {CopyCommonStatements.Qualified(SourceOwner(rule), rule.Table)}", ct).ConfigureAwait(false);
                    if (max.Count > 0 && sourceParts.Codec.NumberText(max[0][0]) is { } topText && CopySequenceDdl.TryNumber(topText, out var top) && next <= top)
                    {
                        _items.Add(new CheckItem(key, CopyCheckCodes.SequenceBehind, CheckLevel.Confirm,
                            $"{key}: 대상에 이미 있는 시퀀스 {owner}.{name} 은(는) 건드리지 않습니다. 그 다음 값({nextText})이 원본 {column} 최댓값({topText}) 이하라 복사 뒤 새 행의 키가 겹칠 수 있습니다. 작업에 이 시퀀스를 연결하면 복사 뒤 값을 맞춥니다.")
                        {
                            Column = column,
                            Sequence = name,
                        });
                        return;
                    }
                }
            }
            catch (Exception ex) when (CopyErrors.IsServer(errors, ex))
            {
                // 알 수 없으면 알리지 않는다(시퀀스는 건드리지 않는다).
            }
        }

        /// <summary>한 소유자의 시퀀스 이름(<c>copy.sequence_names</c>, ALL_SEQUENCES). 읽지 못하면 null.</summary>
        private async Task<HashSet<string>?> ReadSequenceNamesAsync(string owner, CancellationToken ct)
        {
            try
            {
                var rows = await CopyCatalogQueries.QueryAsync(bulk, Target, TargetSql("copy.sequence_names"), ct, ("OWN", owner)).ConfigureAwait(false);
                return rows.Select(r => codec.ToText(r[0])).OfType<string>().ToHashSet(StringComparer.Ordinal);
            }
            catch (Exception ex) when (CopyErrors.IsNoAccess(errors, ex))
            {
                return null;
            }
        }

        /// <summary>
        /// 대상에 표가 없을 때 「없으면 만들기」를 쓸 수 없는 이유(쓸 수 있으면 null). 개발 접속에서만 만들 수 있고,
        /// 같은 이름의 다른 객체(뷰·시노님·MV·시퀀스 …)가 있거나 있는지 확인하지 못하면 만들 수 없다(CREATE 가 이름 충돌로 실패한다).
        /// </summary>
        private async Task<string?> CreateRefusedAsync(string targetOwner, string table, CancellationToken ct)
        {
            if (!CanRecreate)
            {
                return $"대상 접속 '{Target.Profile.Name}' 이 개발 접속이 아니라 테이블을 만들 수 없습니다. 개발 접속에서만 만들 수 있습니다.";
            }

            var objects = await TryCountAsync(TargetSql("copy.object_exists"), ct, ("OWN", targetOwner), ("NAME", table)).ConfigureAwait(false);
            return objects switch
            {
                null => "같은 이름의 객체가 있는지 확인하지 못했습니다(ALL_OBJECTS 조회 실패).",
                > 0 => $"{targetOwner} 에 같은 이름의 테이블이 아닌 객체가 있습니다.",
                _ => null,
            };
        }

        /// <summary>
        /// 표를 더할 때 적은 시노님(<see cref="TableRule.WrittenAs"/>)을 지금 원본에서 다시 풀어 본다. 다른 표를 가리키거나 풀리지 않으면 확인 항목만 낸다
        /// (작업은 저장된 계정·표를 그대로 복사한다. 자동으로 바꾸지 않는다, 결정 B-4 a).
        /// </summary>
        private async Task CheckWrittenAsAsync(TableRule rule, string key, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(rule.WrittenAs))
            {
                return;
            }

            var saved = $"{SourceOwner(rule)}.{rule.Table}";
            var now = await CopyTableInput.ResolveAsync((o, n) => metadata.GetNameStepAsync(Source, o, n, ct), rule.WrittenAs, job.SourceOwner, _sourceAccess.User)
                .ConfigureAwait(false);
            if (now.Table is { } t && CopyOwners.SameOwner(t.Owner, SourceOwner(rule)) && string.Equals(t.Table, rule.Table, StringComparison.Ordinal))
            {
                return;
            }

            _items.Add(new CheckItem(key, CopyCheckCodes.SynonymChanged, CheckLevel.Confirm, now.Table is { } moved
                ? $"{key}: 더할 때 적은 시노님 {rule.WrittenAs} 이(가) 지금은 {moved.Owner}.{moved.Table} 을(를) 가리킵니다. 작업은 저장된 {saved} 를 그대로 복사합니다. 바꾸려면 테이블을 다시 추가하세요."
                : $"{key}: 더할 때 적은 시노님 {rule.WrittenAs} 을(를) 지금 풀 수 없습니다({now.Problem}). 작업은 저장된 {saved} 를 그대로 복사합니다."));
        }

        private async Task<bool> IsSynonymAsync(string owner, string name, CancellationToken ct)
        {
            var step = await metadata.GetNameStepAsync(Source, owner, name, ct).ConfigureAwait(false);
            if (step is null)
            {
                step = await metadata.GetNameStepAsync(Source, "PUBLIC", name, ct).ConfigureAwait(false);
            }

            return step is { IsSynonym: true };
        }

        // 표가 안 보이면 InvalidOperationException 이다(권한이 없어도 같다).
        private async Task<TableInfo?> TryGetTableAsync(ISession s, string owner, string table, CancellationToken ct)
        {
            try
            {
                // 교차 복사: Oracle 카탈로그가 가상 열로 표시하는 XMLTYPE 열을 형식 판정이 보게 한다(점검 계획·실행기가 쓰는 구조 모두 여기서 온다).
                // 같은 DBMS 는 예전처럼 가상 열로 두어 빼고 복사한다(SameDbmsXmlTypeNotes 가 확인 항목으로 알림).
                var info = await metadata.GetTableAsync(s, new DbObjectRef(owner, table, "TABLE"), ct).ConfigureAwait(false);
                return sourceParts.Dbms != targetDbms ? PrecheckRules.ClearXmlTypeVirtual(info) : info;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }

        // ColumnInfo.IsIdentity 는 ALWAYS 와 BY DEFAULT 를 가리지 않는다. 값을 넣을 수 있는 BY DEFAULT 열은 거짓으로 바꾼다(메모 D12).
        private async Task<TableInfo> AdjustIdentityAsync(TableInfo target, string targetOwner, CancellationToken ct)
        {
            if (!target.Columns.Any(c => c.IsIdentity))
            {
                return target;
            }

            HashSet<string> always;
            try
            {
                var rows = await CopyCatalogQueries.QueryAsync(bulk, Target, TargetSql("copy.identity_cols"), ct,
                    ("OWN", targetOwner), ("TBL", target.Table.Name)).ConfigureAwait(false);
                always = rows.Where(r => string.Equals(codec.ToText(r[1]), "ALWAYS", StringComparison.OrdinalIgnoreCase))
                    .Select(r => codec.ToText(r[0])).OfType<string>().ToHashSet(StringComparer.Ordinal);
            }
            catch (Exception ex) when (CopyErrors.IsNoAccess(errors, ex))
            {
                return target; // 구분할 수 없으면 모두 ALWAYS 로 본다(오류 쪽으로 기운다).
            }

            return target with { Columns = [.. target.Columns.Select(c => c.IsIdentity && !always.Contains(c.Name) ? c with { IsIdentity = false } : c)] };
        }
    }
}
