using System.Globalization;
using Oranda.Core.Execution;
using Oranda.Core.Metadata;
using Oranda.Core.Transfer.Copy.CrossDbms;

namespace Oranda.Core.Transfer.Copy;

public partial class CopyPrecheckService
{
    private sealed partial class Run
    {
        // ---- 새 표에 딸린 객체(SQ2, 조정 결정 78) ----

        /// <summary>
        /// 만드는 표에 적재 뒤 함께 만들 객체(<see cref="CompanionDdl"/>)를 원본 카탈로그로 정하고 「함께 만드는 객체」 항목 하나로 알린다(<see cref="CopyCheckCodes.CompanionObjects"/>).
        /// 함수 기반 인덱스·작업 밖 부모 FK 는 만드는 표 모두, 트리거·권한은 「없으면 만들기」 표만(<paramref name="creating"/>; 「다시 만들기」는 대상 쪽 받아 두기·되살리기가 우선한다).
        /// 시노님·파티션·압축·테이블스페이스·저장 속성은 만들지 않는다(대상 기본값, 항목 글에 적는다). 원본을 읽지 못한 것은 빼고 그 까닭을 적는다.
        /// </summary>
        private async Task<CompanionDdl> PlanCompanionsAsync(TableRule rule, string key, TableInfo source, bool creating, CancellationToken ct)
        {
            var sourceOwner = SourceOwner(rule);
            var targetOwner = TargetOwner(rule);
            var cross = sourceParts.Dbms != targetDbms;
            var made = new List<string>();
            var skipped = new List<string>();
            var mapped = CrossTableMap.MapTable(sourceParts.Dbms, targetDbms, source, sourceParts.Codec);
            var removed = source.Columns.Select(c => c.Name).Except(mapped.Target.Columns.Select(c => c.Name), StringComparer.Ordinal)
                .Concat(mapped.LobMappedColumns.Keys).ToHashSet(StringComparer.Ordinal);

            // 1. 함수 기반 인덱스: 식은 원본 세션의 인덱스 열 조회로 읽는다(TableInfo 에는 식이 없다).
            var indexes = new List<string>();
            foreach (var index in source.Indexes.Where(CopyCompanions.IsFunctionIndex))
            {
                IReadOnlyList<IndexColumnDetail>? columns = null;
                try
                {
                    columns = await sourceParts.Recreate.IndexColumnsAsync(Source, source.Table, index.Name, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (CopyErrors.IsServer(errors, ex))
                {
                    // 읽지 못함: 아래에서 뺀다.
                }

                if (columns is not { Count: > 0 })
                {
                    skipped.Add($"함수 기반 인덱스 {index.Name}(식을 읽지 못함)");
                }
                else if (CopyCompanions.UsesColumn(columns, removed) is { } column)
                {
                    skipped.Add($"함수 기반 인덱스 {index.Name}(대상에 만들지 않거나 LOB 로 옮기는 열 {column} 을 씀)");
                }
                else if (CopyCompanions.FunctionIndex(targetOwner, rule.Table, index, columns, sourceOwner) is { } ddl)
                {
                    indexes.Add(ddl);
                    made.Add($"함수 기반 인덱스 {index.Name}");
                }
            }

            // 2. 트리거·권한(새 표만): 원본 세션으로 받아 두기(다시 만들기의 대상 받아 두기와 같은 조회)를 하고 대상 소유자 이름으로 바꾼다.
            var triggers = new List<string>();
            var triggerObjects = new List<DisabledObject>();
            var grants = new List<string>();
            if (creating)
            {
                PreservedDdl? kept = null;
                try
                {
                    kept = await sourceParts.Recreate.CaptureAsync(Source, sourceOwner, rule.Table, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (CopyErrors.IsServer(errors, ex) || ex is InvalidOperationException or BulkDeniedException)
                {
                    skipped.Add($"원본 트리거·권한(읽지 못함: {CopyRunner.SafeError(ex.Message)})");
                }

                if (kept is not null)
                {
                    var canTrigger = _targetAccess.Has("CREATE TRIGGER") || _targetAccess.Has("CREATE ANY TRIGGER");
                    for (var i = 0; i < kept.Triggers.Count && i < kept.TriggerObjects.Count; i++)
                    {
                        var obj = kept.TriggerObjects[i];
                        if (!CopyOwners.SameOwner(obj.Owner, sourceOwner))
                        {
                            if (i == 0 || kept.TriggerObjects[i - 1] != obj)
                            {
                                skipped.Add($"트리거 {obj.Owner}.{obj.Name}(다른 계정의 트리거)");
                            }

                            continue;
                        }

                        if (!canTrigger)
                        {
                            if (i == 0 || kept.TriggerObjects[i - 1] != obj)
                            {
                                skipped.Add($"트리거 {obj.Name}(CREATE TRIGGER 권한 없음)");
                            }

                            continue;
                        }

                        if (i == 0 || kept.TriggerObjects[i - 1] != obj)
                        {
                            made.Add($"트리거 {obj.Name}");
                        }

                        triggers.Add(CopyCompanions.Reowned(kept.Triggers[i], sourceOwner, targetOwner));
                        triggerObjects.Add(new DisabledObject(DisabledKind.Trigger, targetOwner, rule.Table, obj.Name));
                    }

                    foreach (var grant in kept.Grants)
                    {
                        var grantee = CopyCompanions.Grantee(grant);
                        if (grantee is null || CopyOwners.SameOwner(grantee, targetOwner))
                        {
                            skipped.Add($"권한 {grant}({(grantee is null ? "받는 쪽을 읽지 못함" : "대상 소유자 자신")})");
                            continue;
                        }

                        var text = CopyCompanions.Reowned(grant, sourceOwner, targetOwner);
                        if (grantee != "PUBLIC"
                            && await TryCountAsync(TargetSql("copy.user_exists"), ct, ("NAME", grantee)).ConfigureAwait(false) is not > 0)
                        {
                            made.Add($"권한 → {grantee}(대상에서 사용자로 확인되지 않음: 롤이면 붙고, 없으면 실행 때 알림)");
                        }
                        else
                        {
                            made.Add($"권한 → {grantee}");
                        }

                        grants.Add(text);
                    }
                }
            }

            // 3. 작업 밖 부모 FK: 부모가 같은 원본 계정의 표이고 대상 소유자에 그 표가 있으면 적재 뒤 붙여 본다(실패하면 뺀다, NOVALIDATE 로 바꾸지 않는다).
            var outer = new List<(string Parent, string Ddl, string? Name)>();
            var linked = BuiltTables(targetOwner, sourceOwner);
            var parents = new HashSet<string>(StringComparer.Ordinal);
            foreach (var fk in source.Constraints.Where(c => c.Kind == ConstraintKind.ForeignKey && c.ReferencedTable is { } p && !IsLinkedParent(p, rule, linked)))
            {
                var parent = fk.ReferencedTable!;
                if (!CopyOwners.SameOwner(parent.Owner, sourceOwner))
                {
                    skipped.Add($"FK {fk.Name}(→{parent.Owner}.{parent.Name}: 다른 계정의 부모)");
                }
                else if (await TryCountAsync(TargetSql("copy.target_is_table"), ct, ("OWN", targetOwner), ("TBL", parent.Name)).ConfigureAwait(false) is > 0)
                {
                    parents.Add(parent.Name);
                }
                else
                {
                    skipped.Add($"FK {fk.Name}(→{parent.Name}: 대상 {targetOwner} 에 부모 테이블 없음)");
                }
            }

            if (parents.Count > 0)
            {
                try
                {
                    var ddls = (await sourceParts.Recreate.SourceForeignKeysAsync(Source, sourceOwner, rule.Table, parents, targetOwner, ct).ConfigureAwait(false))
                        .Where(f => parents.Contains(f.Parent)).ToList();
                    var (keptFks, skippedFks) = CrossTableMap.FilterForeignKeys(sourceParts.Dbms, targetDbms, source, sourceParts.Codec, ddls, _ => null);
                    outer.AddRange(keptFks);
                    made.AddRange(keptFks.Select(f => $"FK {f.Name ?? "(서버 이름)"}(→{f.Parent}, 작업 밖 부모)"));
                    skipped.AddRange(skippedFks.Select(x => "FK " + x));
                }
                catch (Exception ex) when (CopyErrors.IsServer(errors, ex) || ex is InvalidOperationException or BulkDeniedException)
                {
                    skipped.Add($"작업 밖 부모 FK(원본에서 읽지 못함: {CopyRunner.SafeError(ex.Message)})");
                }
            }

            var companions = new CompanionDdl(triggers, triggerObjects, grants, indexes, outer);
            if (made.Count > 0 || skipped.Count > 0)
            {
                var message = $"{key}: 적재가 끝난 뒤 함께 만듭니다: {(made.Count == 0 ? "없음" : string.Join(", ", made))}.";
                if (skipped.Count > 0)
                {
                    message += $" 만들지 않는 것: {string.Join(", ", skipped)}.";
                }

                if (cross && (triggers.Count > 0 || indexes.Count > 0))
                {
                    message += $" {targetDbms} 로 옮기는 트리거 본문·인덱스 식은 대상에서 맞지 않을 수 있습니다: 컴파일 오류 트리거는 남겨 두되 끄고, 만들지 못한 인덱스는 빼고 알립니다.";
                }

                message += " 함께 만들지 못한 것이 있어도 테이블은 실패로 보지 않고 알림에 문장을 남깁니다(만드는 문장은 미리 보기에 있습니다).";
                _items.Add(new CheckItem(key, CopyCheckCodes.CompanionObjects, skipped.Count > 0 ? CheckLevel.Confirm : CheckLevel.Pass, message));
            }

            return companions;
        }

        /// <summary>교차 복사로 만드는 표가 원본과 달라지는 것(<see cref="CrossTableMap"/>)을 확인 글에 덧붙일 문장(앞 공백 포함). 같은 DBMS 거나 달라지는 것이 없으면 빈 글.</summary>
        private string CrossCreateNote(TableInfo source)
        {
            var notes = CrossTableMap.MapTable(sourceParts.Dbms, targetDbms, source, sourceParts.Codec).Notes;
            return notes.Count == 0 ? string.Empty : " " + targetDbms + " 로 옮기며 달라지는 것: " + string.Join(" / ", notes) + ".";
        }

        /// <summary>
        /// 대상 Tibero 에 만들 큰 문자 열의 길이 단위(TR1 위험 2, <see cref="CrossTableMap.TiberoByteKeptColumns"/>): 바이트 단위로 남기는 열과,
        /// 글자 단위라 서버가 받지 않을 수 있는 열을 이름으로 알린다. 없으면 빈 글.
        /// </summary>
        private string LargeCharNote(TableInfo source, bool recreating)
        {
            var mapped = CrossTableMap.MapTable(sourceParts.Dbms, targetDbms, source, sourceParts.Codec).Target;
            var kept = CrossTableMap.TiberoByteKeptColumns(targetDbms, mapped);
            var large = CrossTableMap.TiberoLargeCharColumns(targetDbms, mapped);
            var note = string.Empty;
            if (kept.Count > 0)
            {
                note += $" 큰 문자 열 {Lengths(kept, "BYTE")} 은(는) 글자 단위로 바꾸면 {targetDbms} 바이트 한도를 넘을 수 있어 원본처럼 바이트 단위로 만듭니다.";
            }

            if (large.Count > 0)
            {
                note += $" 글자 단위 큰 문자 열 {Lengths(large, "CHAR")} 은(는) {targetDbms} 바이트 한도(VARCHAR2 {Number(CrossTableMap.TiberoVarcharMaxBytes)}·CHAR {Number(CrossTableMap.TiberoCharMaxBytes)} 바이트)를 넘을 수 있습니다. 서버가 받지 않으면 "
                        + (recreating ? "지운 옛 테이블을 휴지통에서 되살리고 이 테이블은 복사하지 않습니다." : "테이블을 만들지 못해 이 테이블은 복사하지 않습니다.");
            }

            return note;

            static string Lengths(IReadOnlyList<ColumnInfo> columns, string unit)
                => string.Join(", ", columns.Select(c => $"{c.Name}({(c.Length ?? 0).ToString(CultureInfo.InvariantCulture)} {unit})"));
        }

        /// <summary>작업 안 FK(함께 만드는 부모를 참조하고 대상에 만드는 것)의 원본 이름. 실행기가 원본 카탈로그로 붙이는 FK 와 같은 판정이다.</summary>
        private List<string> JobForeignKeyNames(TableRule rule, TableInfo source, HashSet<string> linked)
        {
            var skipped = CrossSkippedForeignKeys(rule, source, linked).Select(x => x.Fk.Name).ToHashSet(StringComparer.Ordinal);
            return [.. source.Constraints
                .Where(c => c.Kind == ConstraintKind.ForeignKey && c.ReferencedTable is { } p && IsLinkedParent(p, rule, linked) && !skipped.Contains(c.Name))
                .Select(c => c.Name)];
        }

        /// <summary>대상 세션에서 카탈로그 조회 하나(바인드 OWN·TBL)를 보내 행을 글자로 읽는다. 서버 오류면 null(<see cref="RecreateNoteInput.TargetRows"/>).</summary>
        private async Task<IReadOnlyList<string?[]>?> TargetRowsAsync(string id, string owner, string table, CancellationToken ct)
        {
            try
            {
                var rows = await CopyCatalogQueries.QueryAsync(bulk, Target, TargetSql(id), ct, ("OWN", owner), ("TBL", table)).ConfigureAwait(false);
                return [.. rows.Select(r => r.Select(codec.ToText).ToArray())];
            }
            catch (Exception ex) when (CopyErrors.IsServer(errors, ex))
            {
                return null;
            }
        }

        /// <summary>
        /// 교차 복사에서 함께 만드는 부모를 참조하지만 대상에 만들 수 없는 작업 안 FK(자식·부모 열을 옮기지 않거나, 부모의 PK·UK 를 만들지 않음)와 이유.
        /// 실행기(<c>CopyRunner</c>)가 같은 판정(<see cref="CrossTableMap.FilterForeignKeys"/>)으로 붙이지 않는 FK 다. 같은 DBMS 면 비어 있다.
        /// </summary>
        private IReadOnlyList<(ConstraintInfo Fk, string Reason)> CrossSkippedForeignKeys(TableRule rule, TableInfo source, HashSet<string> linked)
            => CrossTableMap.SkippedForeignKeys(sourceParts.Dbms, targetDbms, source, sourceParts.Codec,
                source.Constraints.Where(c => c.Kind == ConstraintKind.ForeignKey && c.ReferencedTable is { } p && IsLinkedParent(p, rule, linked)),
                parent => LinkedParentSource(parent, rule));

        private string CrossSkippedForeignKeyNote(TableRule rule, TableInfo source, HashSet<string> linked)
        {
            var skipped = CrossSkippedForeignKeys(rule, source, linked)
                .Select(x => CrossTableMap.FormatSkipped(x.Fk.Name, x.Fk.ReferencedTable!.Name, x.Reason)).ToList();
            return skipped.Count == 0 ? string.Empty : $" 대상으로 옮기지 않는 열·키를 쓰는 작업 안 FK {string.Join(", ", skipped)} 는 만들지 않습니다.";
        }

        /// <summary>함께 만드는 부모 표(<see cref="BuiltTables"/> 와 같은 판정: 원본·대상 계정이 같고 이번 실행이 만든다)의 원본 구조. 없으면 null.</summary>
        private TableInfo? LinkedParentSource(DbObjectRef parent, TableRule rule)
            => job.Tables.Where(r => r.Kind != RuleKind.Exclude && (IsRecreate(r) || _creating.Contains(Key(r)))
                                     && string.Equals(r.Table, parent.Name, StringComparison.Ordinal)
                                     && CopyOwners.SameOwner(SourceOwner(r), SourceOwner(rule))
                                     && CopyOwners.SameOwner(TargetOwner(r), TargetOwner(rule)))
                .Select(r => _sourceTables.GetValueOrDefault(Key(r)))
                .FirstOrDefault(t => t is not null);

        /// <summary>원본 FK 의 부모가 같은 원본 계정·같은 대상 계정으로 함께 다시 만드는 표인가(실행기 <c>RecreateStep.SourceForeignKeysAsync</c> 와 같은 판정).</summary>
        private bool IsLinkedParent(DbObjectRef parent, TableRule rule, HashSet<string> linked)
            => CopyOwners.SameOwner(parent.Owner, SourceOwner(rule)) && linked.Contains(parent.Name);

        /// <summary>
        /// 지우기 전에 알 수 있는 CREATE 실패 원인. 지원 밖 형식 열(건너뛴 열도 다시 만들 때 만들어진다: 객체형·VECTOR·JSON·XMLTYPE·LONG),
        /// STANDARD 한도를 넘는 길이(대상 MAX_STRING_SIZE 가 EXTENDED 로 확인되지 않으면), 식이 가리키는 객체가 대상 소유자에 없음,
        /// 대상 소유자의 다른 표와 겹치는 제약·인덱스 이름(함께 다시 만드는 표의 이름은 먼저 지워지므로 뺀다).
        /// </summary>
        private async Task<List<string>> RecreateBlockersAsync(TableRule rule, TableInfo source, HashSet<string> recreated, HashSet<string> linked,
                                                               bool creating, CancellationToken ct)
        {
            var owner = TargetOwner(rule);
            var blocked = new List<string>();
            // 새 표 만들기와 지우고 다시 만들기는 DBMS 가 따로 막을 수 있다.
            if ((creating ? dialect.CreateBlockedReason(Target) : dialect.RecreateBlockedReason(Target)) is { } reason)
            {
                blocked.Add(reason);
            }

            // 교차 복사(DBMS 가 다름)는 대상 DBMS 에 만들 구조(CrossTableMap)로 본다: 옮길 형식이 없는 열은 만들지 않으므로 막지 않고(ColumnDropped 로 알린다),
            // 옮긴 형식은 대상 코덱이 쓸 수 있어야 한다. 같은 DBMS 는 원본 열 형식을 원본 코덱으로 본다.
            var cross = sourceParts.Dbms != targetDbms;
            var mapped = CrossTableMap.MapTable(sourceParts.Dbms, targetDbms, source, sourceParts.Codec);
            var effective = cross ? mapped.Target : source;
            var unsupported = (cross
                    ? source.Columns.Where(c => !c.IsVirtual
                                                && CrossTypeMap.Map(sourceParts.Dbms, targetDbms, c) is { } map
                                                && !CrossTableMap.IsDropped(map, c, sourceParts.Codec)
                                                && !codec.IsCrossWritable(map.BindType!))
                    : source.Columns.Where(c => !c.IsVirtual && !sourceParts.Codec.IsSupported(c.DataType)))
                .Select(c => $"{c.Name}({c.DataType})").ToList();
            if (unsupported.Count > 0)
            {
                blocked.Add($"지원하지 않는 형식 열 {string.Join(", ", unsupported)}(건너뛴 열도 다시 만들 때 만들어집니다)");
            }

            // 교차: 원본은 LOB 이 아닌데 대상에서 LOB 이 되는 열(Tibero VARCHAR2 4000 초과·긴 NVARCHAR2·NCHAR·LONG·LONG RAW → CLOB·NCLOB·BLOB)에는 PK·UK 를 만들 수 없다.
            // 지우고 나서야 CREATE 가 실패하므로(Oracle ORA-02329) 지우기 전에 막는다. CHECK·FK·인덱스는 만들지 않고 넘어간다(CrossTableMap).
            foreach (var key in source.Constraints.Where(k => k.Kind is ConstraintKind.PrimaryKey or ConstraintKind.Unique))
            {
                var lob = key.Columns.Where(mapped.LobMappedColumns.ContainsKey).ToList();
                if (lob.Count > 0)
                {
                    var described = lob.Select(name => $"{name}({source.Columns.FirstOrDefault(c => c.Name == name)?.DataType} → {mapped.LobMappedColumns[name]})");
                    blocked.Add($"{source.Table.Name} 의 {(key.Kind == ConstraintKind.PrimaryKey ? "PK" : "UK")} {key.Name} 의 열 {string.Join(", ", described)} 은(는) {targetDbms} 에서 LOB 형식이 되어 "
                                + "LOB 열에는 PK·UK 를 만들 수 없습니다");
                }
            }

            // 기본 문자열 한도를 넘는 길이(Oracle: MAX_STRING_SIZE 가 EXTENDED 로 확인되지 않으면 막는다). 교차는 CLOB 으로 옮긴 뒤의 구조로 본다.
            blocked.AddRange(await dialect.ExtendedStringBlockersAsync(effective, TargetParameterAsync, ct).ConfigureAwait(false));

            // 기본값·가상 열 식이 원본 스키마 객체(시퀀스·함수)를 가리키면 대상 소유자의 같은 이름 객체로 바꿔 만든다.
            var referenced = effective.Columns.Where(c => !c.IsIdentity)
                .SelectMany(c => CreateTableDdl.ReferencedObjects(c.DefaultValue, SourceOwner(rule)).Select(o => (Column: c.Name, Object: o)))
                .ToList();
            var missingObjects = new List<string>();
            var creatingSequences = _sequencePlans.GetValueOrDefault(Key(rule)) ?? [];
            foreach (var name in referenced.Select(x => x.Object).Distinct(StringComparer.Ordinal)
                         .Where(n => !creatingSequences.Any(p => string.Equals(p.Name, n, StringComparison.Ordinal))))
            {
                if (await TryCountAsync(TargetSql("copy.object_exists"), ct, ("OWN", owner), ("NAME", name)).ConfigureAwait(false) is not > 0)
                {
                    missingObjects.Add(name); // 확인하지 못해도 없는 것으로 본다(막는 쪽).
                }
            }

            if (missingObjects.Count > 0)
            {
                var columns = referenced.Where(x => missingObjects.Contains(x.Object)).Select(x => x.Column).Distinct(StringComparer.Ordinal);
                blocked.Add($"열 {string.Join(", ", columns)} 의 기본값·가상 열 식이 가리키는 객체 {string.Join(", ", missingObjects)} 가 대상 소유자({owner})에 없습니다");
            }

            // 이름 겹침: 원본 이름 그대로 만드는 제약·인덱스·작업 안 FK.
            // 서버가 지은 이름 판정은 실행기와 같아야 한다(DBMS 마다 다르다: Oracle SYS_…, Tibero <소유자>_CON<숫자>).
            // 원본 이름을 보는 판정이라 원본 방언이다.
            bool IsServer(string name) => sourceParts.PrecheckDialect.IsServerName(name, source.Table.Owner);
            var (constraints, indexes) = CreateTableDdl.NamedObjects(effective, IsServer);
            var notCreated = CrossSkippedForeignKeys(rule, source, linked).Select(x => x.Fk.Name).ToHashSet(StringComparer.Ordinal); // 교차: 만들지 않는 FK 는 이름이 겹쳐도 상관없다
            var foreignKeys = effective.Constraints
                .Where(c => c.Kind == ConstraintKind.ForeignKey && !IsServer(c.Name) && !notCreated.Contains(c.Name)
                            && c.ReferencedTable is { } p && IsLinkedParent(p, rule, linked))
                .Select(c => c.Name);
            var inUse = new List<string>();
            foreach (var name in constraints.Concat(foreignKeys).Distinct(StringComparer.Ordinal))
            {
                inUse.AddRange(await UsedByOtherTablesAsync(TargetSql("copy.constraint_owner"), owner, name, recreated, ct).ConfigureAwait(false));
            }

            foreach (var name in indexes)
            {
                inUse.AddRange(await UsedByOtherTablesAsync(TargetSql("copy.index_owner"), owner, name, recreated, ct).ConfigureAwait(false));
            }

            if (inUse.Count > 0)
            {
                blocked.Add($"대상 소유자의 다른 테이블이 이미 쓰는 이름 {string.Join(", ", inUse.Distinct(StringComparer.Ordinal))}");
            }

            return blocked;
        }

        private async Task<IEnumerable<string>> UsedByOtherTablesAsync(string sql, string owner, string name, HashSet<string> recreated, CancellationToken ct)
        {
            List<object?[]> rows;
            try
            {
                rows = await CopyCatalogQueries.QueryAsync(bulk, Target, sql, ct, ("OWN", owner), ("NAME", name)).ConfigureAwait(false);
            }
            catch (Exception ex) when (CopyErrors.IsServer(errors, ex))
            {
                return [$"{name}(확인 못 함)"]; // 겹치는지 모르면 막는 쪽.
            }

            return rows.Select(r => codec.ToText(r[0]))
                .Where(t => t is not null && !recreated.Contains(t))
                .Select(t => $"{name}({t})");
        }

        /// <summary>
        /// DROP 과 함께 사라지고 되살리지 않는 것(<see cref="ICopyPrecheckDialect.NotRestoredProbes"/>, Oracle = 열 권한, 다른 계정이 준 권한, MV 로그, VPD·FGA 정책).
        /// 볼 권한이 없으면 「확인 못 함」.
        /// </summary>
        private async Task<List<string>> NotRestoredAsync(TableRule rule, CancellationToken ct)
        {
            var lost = new List<string>();
            foreach (var (id, what) in dialect.NotRestoredProbes)
            {
                // 권한·뷰 열 차이 등 어떤 서버 오류든 「확인 못 함」으로 둔다(사전 점검 전체가 예외로 끝나지 않게).
                var n = await TryCountAsync(TargetSql(id), ct, ("OWN", TargetOwner(rule)), ("TBL", rule.Table)).ConfigureAwait(false);
                if (n is null)
                {
                    lost.Add($"{what}(확인 못 함)");
                }
                else if (n > 0)
                {
                    lost.Add($"{what} {Number(n.Value)}개");
                }
            }

            return lost;
        }

        /// <summary>대상에서 COUNT(*) 하나. 서버 오류면 null.</summary>
        private async Task<long?> TryCountAsync(string sql, CancellationToken ct, params (string Name, string Value)[] binds)
        {
            try
            {
                return codec.ToInt64((await CopyCatalogQueries.QueryAsync(bulk, Target, sql, ct, binds).ConfigureAwait(false)).FirstOrDefault()?[0]) ?? 0;
            }
            catch (Exception ex) when (CopyErrors.IsServer(errors, ex))
            {
                return null;
            }
        }

        // 대상에만 있는 CHECK·FK(켜진 것). 원본에 같은 조건·같은 열의 제약이 있으면 뺀다.
        private void AddTargetOnlyConstraints(TableRule rule, TableInfo source, TableInfo target)
        {
            var sourceChecks = source.Constraints.Where(c => c.Kind == ConstraintKind.Check && c.Condition is not null)
                .Select(c => Normalize(c.Condition!)).ToHashSet(StringComparer.Ordinal);
            var sourceForeignKeys = source.Constraints.Where(c => c.Kind == ConstraintKind.ForeignKey)
                .Select(ForeignKeySignature).ToHashSet(StringComparer.Ordinal);

            var onlyInTarget = new List<string>();
            foreach (var c in target.Constraints.Where(c => c.IsEnabled))
            {
                if (c.Kind == ConstraintKind.Check && c.Condition is not null && !sourceChecks.Contains(Normalize(c.Condition)))
                {
                    onlyInTarget.Add($"{c.Name}(CHECK)");
                }
                else if (c.Kind == ConstraintKind.ForeignKey && !sourceForeignKeys.Contains(ForeignKeySignature(c)))
                {
                    onlyInTarget.Add($"{c.Name}(FK)");
                }
            }

            if (onlyInTarget.Count > 0)
            {
                _items.Add(new CheckItem(Key(rule), "TargetOnlyConstraint", CheckLevel.Confirm,
                    $"{Key(rule)}: 대상에만 있는 제약 {string.Join(", ", onlyInTarget)} 때문에 실패 행이 생길 수 있습니다."));
            }
        }

        private async Task<(bool CanDisable, IReadOnlyList<string> Triggers, bool None)> CheckTriggersAsync(TableRule rule, CancellationToken ct)
        {
            var key = Key(rule);
            var owner = TargetOwner(rule);
            var rows = await CopyCatalogQueries.QueryAsync(bulk, Target, TargetSql("copy.enabled_triggers"), ct,
                ("OWN", owner), ("TBL", rule.Table)).ConfigureAwait(false);
            var all = rows.Select(r => (Owner: codec.ToText(r[0]), Name: codec.ToText(r[1]))).ToList();

            // 트리거 이름은 그 표의 대상 소유자 기준이다(메모 D4). 다른 소유자의 트리거는 보여 주기만 한다.
            var owned = all.Where(t => t.Name is not null && CopyOwners.SameOwner(t.Owner, owner))
                .Select(t => t.Name!).ToList();
            var canDisable = _targetAccess.IsOwner(owner) || _targetAccess.Has("ALTER ANY TRIGGER");

            if (all.Count > 0)
            {
                var offer = canDisable && owned.Count > 0;
                _items.Add(new CheckItem(key, "TargetTriggers", CheckLevel.Confirm,
                    offer
                        ? $"{key}: 대상 트리거 {Number(all.Count)}개가 복사한 행에도 실행됩니다. 복사하는 동안 끄고 끝나면 다시 켤 수 있습니다."
                        : $"{key}: 복사한 행에도 트리거가 실행됩니다({Number(all.Count)}개).")
                {
                    Choices = offer ? [CheckChoice.DisableTriggers] : [],
                });
            }
            else if (!_targetAccess.IsOwner(owner))
            {
                // 소유자가 아닌 쓰기 계정은 ALL_TRIGGERS·ALL_CONSTRAINTS 에서 남의 표 트리거·참조 FK 를 못 볼 수 있다.
                _items.Add(new CheckItem(key, "TargetTriggers", CheckLevel.Pass,
                    $"{key}: 대상 계정이 소유자가 아니라 트리거·이 테이블을 참조하는 FK 가 보이지 않을 수 있습니다. 있으면 복사한 행에도 트리거가 실행됩니다."));
            }

            // 「없음」은 소유자로 봤을 때만 믿는다(소유자가 아니면 남의 표 트리거가 안 보일 수 있다).
            return (canDisable, owned, all.Count == 0 && _targetAccess.IsOwner(owner));
        }

        // 이 표를 참조하는 대상 외래키 중 ORA-02292 를 낼 수 있는 것(메모 D15). 작업 밖 자식 표도 본다.
        private async Task<IReadOnlyList<DisabledObject>> CheckChildForeignKeysAsync(TableRule rule, IReadOnlyList<string> keys, CancellationToken ct)
        {
            if (rule.Action is not (TargetAction.DeleteAllInsert or TargetAction.DeleteRangeInsert))
            {
                return [];
            }

            var rows = await CopyCatalogQueries.QueryAsync(bulk, Target, TargetSql("copy.referencing_fks"), ct,
                ("OWN", TargetOwner(rule)), ("TBL", rule.Table)).ConfigureAwait(false);
            var blocking = rows
                .Select(r => (Owner: codec.ToText(r[0]), Table: codec.ToText(r[1]), Name: codec.ToText(r[2])))
                .Where(fk => fk.Owner is not null && fk.Table is not null && fk.Name is not null && ChildKeepsRows(fk.Owner, fk.Table, rule))
                .Select(fk => new DisabledObject(DisabledKind.ForeignKey, fk.Owner!, fk.Table!, fk.Name!))
                .ToList();
            if (blocking.Count == 0)
            {
                return [];
            }

            var canAlterAny = _targetAccess.Has("ALTER ANY TABLE");
            var disablable = blocking.Where(fk => canAlterAny || _targetAccess.IsOwner(fk.Owner)).ToList();
            var choices = new List<CheckChoice>();
            if (keys.Count > 0)
            {
                choices.Add(CheckChoice.SwitchToMerge);
            }

            if (disablable.Count > 0)
            {
                choices.Add(CheckChoice.DisableForeignKeys);
            }

            var children = string.Join(", ", blocking.Select(fk => (CopyOwners.SameOwner(fk.Owner, TargetOwner(rule)) ? "" : fk.Owner + ".")
                                                                    + $"{fk.Table}({fk.Name})").Distinct(StringComparer.Ordinal));
            _items.Add(new CheckItem(Key(rule), "ChildRowsMayBlock", CheckLevel.Confirm,
                $"{Key(rule)}: 자식 테이블 {children} 의 행이 남아 있으면 지울 때 {dialect.ChildRecordFoundError} 가 날 수 있습니다. 대상 처리를 PK 기준 MERGE 로 바꾸세요.")
            {
                Choices = choices,
            });
            return disablable;
        }

        // 부모 행을 지워도 자식 행이 남을 수 있는가. 작업 안에서 자식이 먼저 비워지거나 부모 범위에 맞춰 지워지면 막지 않는다.
        // 자식 규칙은 대상 계정·표로 찾고, 부모 키 규칙이면 그 부모가 이 규칙(키)인지 본다.
        private bool ChildKeepsRows(string childOwner, string childTable, TableRule parentRule)
        {
            var childRule = CopyOwners.FindByTarget(job, childOwner, childTable);
            if (childRule is null || childRule.Kind == RuleKind.Exclude)
            {
                return true;
            }

            return childRule.Action switch
            {
                TargetAction.DeleteAllInsert or TargetAction.Recreate => false,
                TargetAction.DeleteRangeInsert => !(childRule.Kind == RuleKind.ParentKey && childRule.Parent is not null
                                                    && string.Equals(CopyOwners.ParentKey(job, childRule), Key(parentRule), StringComparison.Ordinal)),
                _ => true,
            };
        }
    }
}
