using System.Diagnostics;
using System.Globalization;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.History;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;
using Oranda.Core.Transfer.Copy.CrossDbms;

namespace Oranda.Core.Transfer.Copy;

public partial class CopyRunner
{
    // ---- 상태 ----

    /// <summary>교차 복사(원본·대상 DBMS 가 다름)의 두 DBMS 와 대상 값 코덱. 같은 DBMS 복사는 이것이 없다(값 변환을 거치지 않는다).</summary>
    private sealed record CrossSides(Dbms Source, Dbms Target, IBulkValueCodec TargetCodec);

    /// <summary>교차 복사에서 옮길 형식이 없어 뺀 열(<see cref="CrossColumnMapping.DropReason"/>).</summary>
    private sealed record DroppedColumn(string Name, string DataType, CrossDropReason Reason);

    /// <summary>
    /// 표 하나의 열 배치: 복사 열·바인드 열, 마스킹, 키 열 위치. 값 해석(마스킹·실패 행 키)은 원본 값을 보므로 원본 벌의 코덱이다.
    /// 교차 복사면 열을 <see cref="CrossTypeMap"/> 으로 옮기고(옮길 형식이 없는 열은 뺀다) 마스킹한 원본 값을 대상 DBMS 값으로 바꾼다(<see cref="TryToTarget"/>).
    /// </summary>
    private sealed class RowShape
    {
        private readonly IBulkValueCodec _codec;
        private readonly (int Index, MaskRule Rule, int MaxLength, object? Fixed, bool Text)[] _masks;
        private readonly int[] _keyIndexes;
        private readonly string[] _keyTypes;
        private readonly string _salt;

        // 교차 복사만: 대상 값 코덱, 복사 열마다의 원본 형식(이식 값으로 읽을 형식). 같은 DBMS 면 null.
        private readonly IBulkValueCodec? _targetCodec;
        private readonly string[] _sourceTypes;

        private RowShape(IBulkValueCodec codec, IReadOnlyList<string> copyColumns, IReadOnlyList<BulkColumn> columns,
                         (int, MaskRule, int, object?, bool)[] masks, int[] keyIndexes, string[] keyTypes, IReadOnlyList<string> keyNames, string salt,
                         IBulkValueCodec? targetCodec, string[] sourceTypes, IReadOnlyList<DroppedColumn> dropped)
        {
            _codec = codec;
            CopyColumns = copyColumns;
            Columns = columns;
            _masks = masks;
            _keyIndexes = keyIndexes;
            _keyTypes = keyTypes;
            KeyNames = keyNames;
            _salt = salt;
            _targetCodec = targetCodec;
            _sourceTypes = sourceTypes;
            Dropped = dropped;
        }

        /// <summary>읽고 넣는 열(원본 SELECT·대상 DML 의 열 순서). 같은 DBMS 면 <see cref="CopyTablePlan.Columns"/> 그대로, 교차면 옮길 형식이 없는 열을 뺀 것.</summary>
        public IReadOnlyList<string> CopyColumns { get; }

        /// <summary>대상 DML 바인드 열. 같은 DBMS = 원본 열 형식, 교차 = 원본 형식을 대상 DBMS 로 옮긴 형식(<see cref="CrossColumnMapping.BindType"/>, D3).</summary>
        public IReadOnlyList<BulkColumn> Columns { get; }

        public IReadOnlyList<string> KeyNames { get; }

        /// <summary>교차 복사에서 옮길 형식이 없어 뺀 열(원본 열 순서). 같은 DBMS 면 비었다.</summary>
        public IReadOnlyList<DroppedColumn> Dropped { get; }

        /// <summary>값을 대상 DBMS 값으로 바꿔야 한다(교차 복사).</summary>
        public bool Converts => _targetCodec is not null;

        public static RowShape Create(IBulkValueCodec codec, CopyJob job, CopyTablePlan plan, CrossSides? cross = null)
        {
            var source = plan.Source.Columns.ToDictionary(c => c.Name, StringComparer.Ordinal);
            var target = plan.Target.Columns.ToDictionary(c => c.Name, StringComparer.Ordinal);
            ColumnInfo SourceColumn(string name) => source.TryGetValue(name, out var c)
                ? c
                : throw new InvalidOperationException($"{plan.Rule.Table}: 원본에 열 {name} 이(가) 없습니다.");

            // 같은 DBMS: 바인드 형식은 원본 열 형식이다. 값이 원본 형식의 DBMS 고유 값이고, 형식이 달라도 변환은 서버가 맡는다.
            // 교차: 열마다 CrossTypeMap 으로 옮긴다. 옮길 형식이 없는 열은 빼고, 바인드 형식은 원본 형식을 대상 DBMS 로 옮긴 형식이다(대상 열 형식이 아님, D3).
            IReadOnlyList<string> copyColumns = plan.Columns;
            string[] sourceTypes = [];
            IReadOnlyList<DroppedColumn> dropped = [];
            List<BulkColumn> columns;
            if (cross is null)
            {
                var bindNames = CopyCommonStatements.BindNames(plan.Columns.Count);
                columns = plan.Columns.Select((c, i) => new BulkColumn(bindNames[i], SourceColumn(c).DataType)).ToList();
            }
            else
            {
                var kept = plan.Columns.Select(SourceColumn)
                    .Select(c => (Column: c, Map: CrossTypeMap.Map(cross.Source, cross.Target, c)))
                    .Where(x => !x.Map.IsDropped)
                    .ToList();
                dropped = DroppedColumns(cross, plan, target);
                if (plan.KeyColumns.FirstOrDefault(k => dropped.Any(d => string.Equals(d.Name, k, StringComparison.Ordinal))) is { } lostKey)
                {
                    throw new InvalidOperationException($"{plan.Rule.Table}.{lostKey}: 키 열을 대상 DBMS 로 옮길 형식이 없어 이 테이블을 복사할 수 없습니다.");
                }

                copyColumns = [.. kept.Select(x => x.Column.Name)];
                sourceTypes = [.. kept.Select(x => x.Column.DataType)];
                var bindNames = CopyCommonStatements.BindNames(kept.Count);
                columns = kept.Select((x, i) => new BulkColumn(bindNames[i], x.Map.BindType!)).ToList();
            }

            var masks = new List<(int, MaskRule, int, object?, bool)>();
            foreach (var rule in plan.Rule.Masks)
            {
                var index = IndexOf(copyColumns, rule.Column);
                if (index < 0)
                {
                    continue; // 복사하지 않는 열이다.
                }

                if (plan.KeyColumns.Contains(rule.Column, StringComparer.Ordinal))
                {
                    throw new InvalidOperationException($"{plan.Rule.Table}.{rule.Column}: 키 열에는 마스킹을 걸 수 없습니다.");
                }

                var col = SourceColumn(rule.Column);
                var kind = codec.KindOf(col.DataType);
                var text = kind == BulkKind.Text;
                if (!text && rule.Kind is not (MaskKind.Fixed or MaskKind.Null))
                {
                    throw new InvalidOperationException($"{plan.Rule.Table}.{rule.Column}: 문자 열이 아니면 고정값·NULL 마스킹만 쓸 수 있습니다.");
                }

                var maxLength = (target.TryGetValue(rule.Column, out var t) ? t.Length : null) ?? col.Length ?? 64;
                object? fixedValue = null;
                if (!text && rule.Kind == MaskKind.Fixed && rule.Value is { } v)
                {
                    try
                    {
                        fixedValue = kind switch
                        {
                            BulkKind.Clob => v,
                            BulkKind.Blob => Convert.FromHexString(v),
                            _ => codec.FromKeyText(v, col.DataType),
                        };
                    }
                    catch (Exception ex) when (ex is FormatException or NotSupportedException or OverflowException)
                    {
                        throw new InvalidOperationException($"{plan.Rule.Table}.{rule.Column}: 마스킹 고정값을 열 형식으로 바꾸지 못했습니다({ex.Message}).", ex);
                    }
                }

                masks.Add((index, rule, maxLength, fixedValue, text));
            }

            var keyIndexes = plan.KeyColumns.Select(k => IndexOf(copyColumns, k)).ToArray();
            if (keyIndexes.Any(i => i < 0))
            {
                throw new InvalidOperationException($"{plan.Rule.Table}: 키 열이 복사 열에 없습니다.");
            }

            var keyTypes = plan.KeyColumns.Select(k => SourceColumn(k).DataType).ToArray();
            return new RowShape(codec, copyColumns, columns, [.. masks], keyIndexes, keyTypes, plan.KeyColumns, job.MaskSalt,
                                cross?.TargetCodec, sourceTypes, dropped);
        }

        /// <summary>
        /// 교차 복사에서 형식 때문에 빠지는 열: 형식만 아니면 복사했을 열(가상 열 아님·건너뛸 열 아님·대상에도 있음) 가운데 <see cref="CrossTypeMap"/> 이 Dropped 로 본 것.
        /// 점검이 미리 뺐든(<see cref="CopyTablePlan.Columns"/> 에 없음) 아니든 같은 목록이다 — 실행 로그에 남길 목록.
        /// </summary>
        private static List<DroppedColumn> DroppedColumns(CrossSides cross, CopyTablePlan plan, Dictionary<string, ColumnInfo> target)
        {
            var skip = plan.Rule.SkipColumns.ToHashSet(StringComparer.Ordinal);
            return [.. plan.Source.Columns
                .Where(c => !c.IsVirtual && !skip.Contains(c.Name) && target.TryGetValue(c.Name, out var t) && !t.IsVirtual)
                .Select(c => (Column: c, Map: CrossTypeMap.Map(cross.Source, cross.Target, c)))
                .Where(x => x.Map.IsDropped)
                .Select(x => new DroppedColumn(x.Column.Name, x.Column.DataType, x.Map.DropReason))];
        }

        /// <summary>
        /// 외부 도구 적재(조정 결정 88): 마스킹한 원본 행 → 이식 값 행(원본 코덱 <c>ToPortable</c>, 같은 DBMS·교차 모두). 데이터 파일은 이 값을 대상 형식의 글자로 쓴다.
        /// 못 바꾸면 false 와 고정 글 사유(<see cref="TryToTarget"/> 와 같은 규칙: 값은 넣지 않는다).
        /// </summary>
        public bool TryToPortable(object?[] row, out object?[] values, out string? error)
        {
            var portable = new object?[row.Length];
            for (var i = 0; i < row.Length; i++)
            {
                var sourceType = _targetCodec is null ? Columns[i].ServerType : _sourceTypes[i];
                try
                {
                    portable[i] = row[i] is null ? null : _codec.ToPortable(row[i], sourceType);
                }
                catch (Exception ex) when (ex is PortableConversionException or InvalidCastException or FormatException or OverflowException)
                {
                    values = [];
                    error = $"열 {CopyColumns[i]} 의 값(원본 형식 {sourceType})을 외부 도구용 값으로 바꾸지 못했습니다.";
                    return false;
                }
            }

            values = portable;
            error = null;
            return true;
        }

        /// <summary>
        /// 교차 복사: 마스킹한 원본 행 → 새 배열의 대상 DBMS 값(원본 코덱 <c>ToPortable</c> → 대상 코덱 <c>FromPortable</c>, 바인드 형식 = <see cref="Columns"/>).
        /// 값 하나라도 못 바꾸면(<see cref="PortableConversionException"/>·<see cref="InvalidCastException"/>·<see cref="FormatException"/>·<see cref="OverflowException"/>)
        /// false 와 그 행의 실패 사유(열 이름·형식 이름만의 고정 글, 예외 글·값은 넣지 않는다, D5). <see cref="NotSupportedException"/> 은 잡지 않는다(표가 실패). 원본 행은 바꾸지 않는다(실패 행 키를 원본 값으로 남긴다).
        /// </summary>
        public bool TryToTarget(object?[] row, out object?[] values, out string? error)
        {
            var target = _targetCodec ?? throw new InvalidOperationException("같은 DBMS 복사는 값을 바꾸지 않습니다.");
            var converted = new object?[row.Length];
            for (var i = 0; i < row.Length; i++)
            {
                try
                {
                    converted[i] = row[i] is null ? null : target.FromPortable(_codec.ToPortable(row[i], _sourceTypes[i]), Columns[i].ServerType);
                }
                catch (Exception ex) when (ex is PortableConversionException or InvalidCastException or FormatException or OverflowException)
                {
                    // 코덱 예외 글에는 행 값(숫자·날짜 등)이 들어 있을 수 있어 사유에 넣지 않는다: 열 이름·형식 이름만으로 만든 고정 글.
                    values = [];
                    error = $"열 {CopyColumns[i]} 의 값(원본 형식 {_sourceTypes[i]})을 대상 형식({Columns[i].ServerType})으로 바꾸지 못했습니다.";
                    return false;
                }
            }

            values = converted;
            error = null;
            return true;
        }

        /// <summary>원본 행에 마스킹을 건 새 행. 원본 값은 어디에도 남기지 않는다.</summary>
        public object?[] Apply(object?[] row)
        {
            foreach (var (index, rule, maxLength, fixedValue, text) in _masks)
            {
                row[index] = text
                    ? MaskFunctions.Apply(rule, row[index] as string, _salt, maxLength)
                    : rule.Kind == MaskKind.Fixed ? fixedValue : null;
            }

            return row;
        }

        /// <summary>실패 행 기록용 키 글자. 키 열이 없으면 빈 사전(다시 넣기 불가).</summary>
        public Dictionary<string, string?> KeyOf(object?[] row)
        {
            var key = new Dictionary<string, string?>(StringComparer.Ordinal);
            for (var i = 0; i < _keyIndexes.Length; i++)
            {
                key[KeyNames[i]] = _codec.ToKeyText(row[_keyIndexes[i]], _keyTypes[i]);
            }

            return key;
        }

        /// <summary>실패 행 키 글자 → 다시 읽기 바인드(<c>K1</c>…<c>Kn</c>).</summary>
        public Dictionary<string, BindValue> KeyBinds(FailedRow row)
        {
            var binds = new Dictionary<string, BindValue>(StringComparer.Ordinal);
            for (var i = 0; i < KeyNames.Count; i++)
            {
                if (!row.Key.TryGetValue(KeyNames[i], out var text))
                {
                    throw new InvalidOperationException($"{row.Table}: 실패 행 기록에 키 열 {KeyNames[i]} 이(가) 없습니다.");
                }

                binds["K" + (i + 1).ToString(CultureInfo.InvariantCulture)] =
                    new BindValue(_codec.BindTypeOf(_keyTypes[i]), _codec.FromKeyText(text, _keyTypes[i]));
            }

            return binds;
        }

        private static int IndexOf(IReadOnlyList<string> columns, string name)
        {
            for (var i = 0; i < columns.Count; i++)
            {
                if (string.Equals(columns[i], name, StringComparison.Ordinal))
                {
                    return i;
                }
            }

            return -1;
        }
    }

    /// <param name="name">규칙 키(결과·진행·실패 행·실행 기록의 표 이름).</param>
    /// <param name="owner">대상 계정.</param>
    /// <param name="table">대상 표 이름.</param>
    private sealed class TableState(string name, string owner, string table)
    {
        private readonly List<string> _messages = [];

        /// <summary>규칙 키.</summary>
        public string Name { get; } = name;

        /// <summary>대상 계정.</summary>
        public string Owner { get; } = owner;

        /// <summary>대상 표 이름.</summary>
        public string TableName { get; } = table;

        /// <summary>대상 표 <c>계정.표</c>(안내 글).</summary>
        public string TargetName => Owner + "." + TableName;

        public long SourceRows { get; set; }

        public long Loaded { get; set; }

        /// <summary>보낸 배치 수(작업 로그의 배치 번호).</summary>
        public int Batches { get; set; }

        public long TargetRows { get; set; } = -1;

        public int FailedRows { get; set; }

        /// <summary>확정(커밋)을 기다리는 실패 행.</summary>
        public List<FailedRow> PendingFailed { get; } = [];

        /// <summary>이 표의 적재가 커밋으로 확정됐다(실패 행을 결과에 넣었다).</summary>
        public bool Confirmed { get; set; }

        /// <summary>이 표의 적재를 되돌렸다(실패 행 목록을 비웠다).</summary>
        public bool WasRolledBack { get; private set; }

        /// <summary>적재를 되돌렸다: 실패 행은 「실패 행만 다시 넣기」에 쓸 수 없으므로 버리고, 다시 실행하라고 적는다.</summary>
        public void RolledBack()
        {
            if (!WasRolledBack && (PendingFailed.Count > 0 || FailedRows > 0))
            {
                Note("적재를 되돌려 확정되지 않은 실패 행을 버렸습니다. 「실패 행만 다시 넣기」 대신 다시 실행하세요.");
            }

            WasRolledBack = true;
            PendingFailed.Clear();
        }

        /// <summary>대상 정리(DELETE)를 했다. 다시 만든 표는 DROP 을 했다.</summary>
        public bool Deleted { get; set; }

        /// <summary>「테이블 다시 만들기」 표.</summary>
        public bool Recreate { get; set; }

        /// <summary>지우기 전에 받아 둔 트리거·권한·참조 FK.</summary>
        public PreservedDdl? Preserved { get; set; }

        /// <summary>DROP 이 성공했다(옛 표는 휴지통에 있다).</summary>
        public bool Dropped { get; set; }

        /// <summary>CREATE TABLE 이 성공했다(되살리기 대상).</summary>
        public bool Created { get; set; }

        /// <summary>지운 뒤 CREATE 가 실패해 옛 표를 휴지통에서 되살렸다(<see cref="Created"/> 는 거짓, 옛 표·데이터가 그대로 돌아왔다).</summary>
        public bool Flashbacked { get; set; }

        /// <summary>대상에 없던 표를 이번 실행이 만들었다(「없으면 만들기」, <see cref="Created"/> 도 참). 옛 표가 없으니 휴지통·보존 DDL 이 없다.</summary>
        public bool CreatedNew { get; set; }

        /// <summary>만들 표로 정해 CREATE 를 시도했다(<see cref="Created"/> 가 거짓이면 만들지 못했다 — 그 표는 대상 건수를 세지 않는다).</summary>
        public bool CreateRequested { get; set; }

        /// <summary>적재를 끝내지 못해 이번에 만든 표를 <c>DROP … PURGE</c> 로 지웠다(되살리기·건수 비교 대상이 아니다).</summary>
        public bool Purged { get; set; }

        /// <summary>CREATE 문장(FK 없음, 준비 단계에서 만든다).</summary>
        public IReadOnlyList<string> CreateDdl { get; set; } = [];

        /// <summary>새 표에 적재 뒤 보낼 PK·UK·인덱스 문장(PF4a, <see cref="CrossCreate.AfterLoad"/>).</summary>
        public IReadOnlyList<string> AfterLoadDdl { get; set; } = [];

        /// <summary>함께 다시 만드는 부모를 참조하는 원본 FK(부모 표, 문장). 모든 CREATE 뒤에 붙인다.</summary>
        public IReadOnlyList<(string Parent, string Ddl, string? Name)> JobForeignKeys { get; set; } = [];

        /// <summary>DROP 전 휴지통의 이 이름 표(BIN$…).</summary>
        public IReadOnlySet<string> BinBefore { get; set; } = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>이번 DROP 이 만든 휴지통 이름. <see cref="BinKnown"/> 인데 null 이면 영구 삭제됐다.</summary>
        public string? BinName { get; set; }

        /// <summary>DROP 뒤 휴지통을 확인했다.</summary>
        public bool BinKnown { get; set; }

        /// <summary>DROP 전에 떼어 낸 참조 FK(<see cref="PreservedDdl.ReferencingForeignKeys"/> 의 위치). 지우지 못한 표는 이것을 되붙인다.</summary>
        public List<int> DetachedReferencing { get; } = [];

        /// <summary>다시 넣기 커밋이 연결 끊김으로 실패했다(확정됐는지 모른다).</summary>
        public bool CommitUnknown { get; set; }

        /// <summary>적재 밖의 일부(다시 만든 표의 문장·되살리기)를 하지 못했다. 성공 표는 부분 실패로 보인다.</summary>
        public bool Incomplete { get; private set; }

        public void Degrade(string message)
        {
            Incomplete = true;
            _messages.Add(message);
        }

        /// <summary>적재를 끝까지 했다(실패 행이 있어도 표를 멈추지 않았다).</summary>
        public bool LoadCompleted { get; set; }

        /// <summary>표 시작 세이브포인트를 세웠다. 세우기 전에는 되돌리지 않는다(ORA-01086).</summary>
        public bool SavepointSet { get; set; }

        public TimeSpan Elapsed { get; set; }

        /// <summary>적재 시간 나눔(결정 74: 읽기·변환·쓰기). 적재 흐름(PumpTableAsync)을 지났을 때만 결과에 싣는다(<see cref="Pumped"/>).</summary>
        public TableTimes Times { get; } = new();

        /// <summary>적재 흐름을 지났다(시간 나눔이 뜻이 있다).</summary>
        public bool Pumped { get; set; }

        /// <summary>
        /// 지금 배치를 시작할 때의 <see cref="PendingFailed"/> 수. 대기 한도로 그 배치를 배치 세이브포인트로 되돌리면 그 뒤에 넣은 실패 행을 뺀다
        /// (「배치마다 커밋」, 리뷰 PF1-2: 되돌린 배치의 실패 행이 확정되지 않게).
        /// </summary>
        public int PendingAtBatchStart { get; set; }

        public CopyOutcome Outcome { get; private set; } = CopyOutcome.Succeeded;

        /// <summary>실패 메시지를 작업 로그에 남기는 통로.</summary>
        public Action<string>? Failed { get; init; }

        public void Fail(string message)
        {
            Outcome = CopyOutcome.Failed;
            _messages.Add(message);
            Failed?.Invoke(message);
        }

        public void Note(string message) => _messages.Add(message);

        /// <summary>중지로 적재를 끝내지 못한 표(실패로 판정된 표는 그대로 둔다).</summary>
        public void MarkStopped()
        {
            if (Outcome != CopyOutcome.Failed)
            {
                Outcome = CopyOutcome.Stopped;
            }
        }

        public TableRunResult ToResult()
        {
            var outcome = Outcome == CopyOutcome.Succeeded && (FailedRows > 0 || Incomplete) ? CopyOutcome.PartiallyFailed : Outcome;
            // 표 하나가 「중지」여도 실행 전체 판정(RunState.Outcome)은 따로 한다.
            var message = _messages.Count > 0 ? string.Join(" ", _messages) : null;
            return new TableRunResult(Name, SourceRows, TargetRows, Elapsed, outcome, message) { Times = Pumped ? Times.ToTimes(Loaded, Elapsed) : null };
        }
    }

    /// <summary>
    /// 테이블 하나의 적재 시간 나눔(결정 74). 읽기 = 원본 행을 받기까지 기다린 시간(겹치기에서는 읽기 작업이 실제로 DB 를 기다린 시간),
    /// 변환 = 마스킹·교차 복사 값 변환(바인드 값 준비), 쓰기 = 배치를 보낸 시간에서 그 배치의 값 변환을 뺀 것. 겹치기 켬·끔 모두 같은 자리에서 잰다.
    /// 읽기·마스킹은 읽기 작업, 쓰기·값 변환은 쓰기 흐름이 더한다(Interlocked).
    /// </summary>
    private sealed class TableTimes
    {
        private long _read;
        private long _convert;
        private long _write;
        private long _batchConvert; // 쓰기 흐름만: 지금 배치의 값 변환(쓰기 시간에서 뺀다)
        private long _firstRow = -1; // 첫 행까지(질의 시작·커서 열기). -1 = 아직
        private long _load; // 적재 구간(PumpTableAsync) 경과
        private long _outside; // 적재 흐름(LoadTableAsync) 밖에서 이 테이블에 쓴 시간: 대상 정리(DELETE)·건수 비교

        /// <summary>원본 행 하나를 받기까지 기다린 시간. 처음 부른 것(질의 시작 → 첫 행)은 따로도 남긴다(원격 #155).</summary>
        public void AddRead(long started)
        {
            var spent = Stopwatch.GetTimestamp() - started;
            Interlocked.Add(ref _read, spent);
            Interlocked.CompareExchange(ref _firstRow, spent, -1);
        }

        /// <summary>읽기 시간을 Stopwatch 틱으로 더한다(외부 도구: 조각 데이터 파일 쓰기, 결정 97).</summary>
        public void AddReadTicks(long ticks) => Interlocked.Add(ref _read, Math.Max(0, ticks));

        /// <summary>쓰기 시간을 Stopwatch 틱으로 더한다(외부 도구: 도구가 돈 시간의 합).</summary>
        public void AddWriteTicks(long ticks) => Interlocked.Add(ref _write, Math.Max(0, ticks));

        /// <summary>적재 구간(원본 질의 시작 → 마지막 배치 쓰기 끝) 경과. 행/초의 기준이다.</summary>
        public void AddLoad(long started) => Interlocked.Add(ref _load, Stopwatch.GetTimestamp() - started);

        /// <summary>적재 흐름 밖에서 이 테이블에 쓴 시간(대상 정리 DELETE·건수 비교): 테이블 전체 경과에 든다.</summary>
        public void AddOutside(long started) => Interlocked.Add(ref _outside, Stopwatch.GetTimestamp() - started);

        public void AddConvert(long started) => Interlocked.Add(ref _convert, Stopwatch.GetTimestamp() - started);

        /// <summary>
        /// 지금 배치의 값 변환. <paramref name="excluded"/> = 변환 고리 안에서 실패 행을 처리한 시간(상한을 넘으면 ROLLBACK TO 왕복):
        /// 대상 DB 를 기다린 시간이라 변환에서 빼고 쓰기에 남긴다(리뷰 PF1-5).
        /// </summary>
        public void AddBatchConvert(long started, long excluded = 0)
        {
            var spent = Math.Max(0, Stopwatch.GetTimestamp() - started - excluded);
            Interlocked.Add(ref _convert, spent);
            _batchConvert += spent;
        }

        public void AddWrite(long started)
        {
            var spent = Stopwatch.GetTimestamp() - started - _batchConvert;
            _batchConvert = 0;
            Interlocked.Add(ref _write, Math.Max(0, spent));
        }

        /// <summary>
        /// 행/초 = 넣은 행 / 적재 구간 경과(적재 구간을 재지 못했으면 <paramref name="elapsed"/>). 테이블 전체 = <paramref name="elapsed"/>(적재 흐름: 세이브포인트·
        /// 읽기 시점·적재 구간·커밋) + 대상 정리·건수 비교(원격 #155: 「걸린 시간과 행/초가 안 맞음」 — 기준을 나눠 적는다).
        /// </summary>
        public CopyTableTimes ToTimes(long written, TimeSpan elapsed)
        {
            var load = Interlocked.Read(ref _load);
            var basis = load > 0 ? Span(load) : elapsed;
            var first = Interlocked.Read(ref _firstRow);
            return new CopyTableTimes(Span(Interlocked.Read(ref _read)), Span(Interlocked.Read(ref _convert)), Span(Interlocked.Read(ref _write)),
                basis.TotalSeconds > 0 ? written / basis.TotalSeconds : 0)
            {
                Load = load > 0 ? Span(load) : null,
                Total = elapsed + Span(Interlocked.Read(ref _outside)),
                FirstRow = first >= 0 ? Span(first) : null,
            };
        }

        private static TimeSpan Span(long stopwatchTicks) => Stopwatch.GetElapsedTime(0, stopwatchTicks);
    }

    /// <summary>알림 목록. 더할 때마다 <paramref name="added"/> 로 작업 로그에 남긴다.</summary>
    private sealed class NoticeList(Action<string> added) : IEnumerable<string>
    {
        private readonly List<string> _items = [];

        public void Add(string notice)
        {
            // 동시 복사(PF2)면 레인 여럿이 더한다. 읽기(결과 만들기)는 레인이 모두 끝난 뒤다.
            lock (_items)
            {
                _items.Add(notice);
            }

            added(notice);
        }

        public IEnumerator<string> GetEnumerator() => _items.GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class RunState
    {
        private readonly IProgress<CopyProgress>? _progress;
        private readonly object _reportGate = new();
        private readonly Dictionary<string, TableInfo> _sourceTables;
        private readonly Dictionary<string, TableInfo> _targetTables;

        // 동시 복사(PF2): 레인(세션 쌍)마다 따로인 것(세션·지금 표·단계·버린 세션)은 지금 흐름의 레인(_lane)에 두고, 없으면(첫 레인·한 테이블씩) 주 값을 쓴다.
        // 레인 흐름은 시작할 때 UseLane 으로 자기 레인을 건다(AsyncLocal: 그 흐름과 그 흐름이 띄운 읽기·감시 작업에만 보인다). 나머지는 모두 공유다.
        private readonly AsyncLocal<RunLane?> _lane = new();
        private readonly ISession _source;
        private readonly ISession _target;
        private string _phase = PhaseStart;
        private string? _table;
        private volatile bool _aborted;
        private bool _sourceAbandoned;
        private bool _targetAbandoned;

        public RunState(CopySessions sessions, CopyJob job, PrecheckReport report, ResolvedPeriod period,
                        IReadOnlySet<string> preConfirmed, IProgress<CopyProgress>? progress)
        {
            _source = sessions.Source;
            _target = sessions.Target;
            External = sessions.ExternalLoad;
            Job = job;
            Report = report;
            _progress = progress;
            _sourceTables = report.Order.ToDictionary(p => CopyOwners.Key(job, p.Rule), p => p.Source, StringComparer.Ordinal);
            _targetTables = report.Order.ToDictionary(p => CopyOwners.Key(job, p.Rule), p => p.Target, StringComparer.Ordinal);
            Builder = new CopySqlBuilder(job, period, _sourceTables, _targetTables, FreshKeys(job, report));
            HistoryOptions = new ExecutionOptions { Source = HistorySource.DataCopy, PreConfirmed = preConfirmed };
            BulkOptions = new ExecutionOptions { Source = HistorySource.DataCopy, SkipHistory = true, PreConfirmed = preConfirmed };
            Notices = new NoticeList(text => Log(text, CopyLogKind.Warning));
        }

        /// <summary>오래된 외부 도구 임시 폴더를 이 실행에서 지웠는가(1 = 지움, 레인 여럿이 함께 본다).</summary>
        public int ExternalStaleCleaned;

        /// <summary>이 실행의 짧은 식별자(외부 도구 임시 폴더 이름).</summary>
        public string RunId { get; } = Guid.NewGuid().ToString("N")[..12];

        /// <summary>외부 적재 도구 준비물(<see cref="CopySessions.ExternalLoad"/>). null = 쓸 수 없음.</summary>
        public ExternalLoad.CopyExternalLoad? External { get; }

        /// <summary>원본 세션(지금 흐름이 레인이면 그 레인의 원본).</summary>
        public ISession Source => _lane.Value?.Source ?? _source;

        /// <summary>대상 세션(지금 흐름이 레인이면 그 레인의 대상).</summary>
        public ISession Target => _lane.Value?.Target ?? _target;

        /// <summary>지금 흐름의 레인(첫 레인·레인 밖이면 null).</summary>
        public RunLane? CurrentLane => _lane.Value;

        /// <summary>둘째 레인부터(<see cref="CopySessions.Workers"/>로 연 쌍). 첫 레인은 주 쌍이라 여기 없다. 한 테이블씩이면 비었다.</summary>
        public List<RunLane> Workers { get; } = [];

        /// <summary>동시 테이블 수(레인 수). 1 이면 예전처럼 한 테이블씩.</summary>
        public int LaneCount { get; set; } = 1;

        /// <summary>레인이 도는 중(<see cref="CopyRunner.LoadInLanesAsync"/>). 이때만 다른 레인의 전체 중단을 배치마다 본다.</summary>
        public bool InLanes { get; set; }

        /// <summary>둘째 레인부터 가운데 대상 트랜잭션을 끝내지 못한 것이 있다(<see cref="CopyRunner.FinishLanesAsync"/>). 마무리가 다시 켜기·되살리기를 하지 않는다.</summary>
        public bool LaneLeftOpen { get; set; }

        /// <summary>이 흐름(과 그 흐름이 띄우는 작업)을 <paramref name="lane"/> 의 세션으로 돌린다. 부른 비동기 메서드가 끝나면 원래대로 돌아간다.</summary>
        public void UseLane(RunLane lane) => _lane.Value = lane;

        public CopyJob Job { get; }

        public PrecheckReport Report { get; }

        public CopySqlBuilder Builder { get; }

        /// <summary>DELETE·ALTER: 이력에 남긴다.</summary>
        public ExecutionOptions HistoryOptions { get; }

        /// <summary>조회·배치 INSERT·MERGE·트랜잭션 제어: 이력에 남기지 않는다.</summary>
        public ExecutionOptions BulkOptions { get; }

        public ICopyRunRecord? Record { get; set; }

        public DateTimeOffset ReadStartedAt { get; set; } = DateTimeOffset.Now;

        /// <summary>같은 시점 읽기로 동시에 복사할 때(PF2b) 모든 테이블을 읽는 원본 시점(<see cref="CopyRunner.CaptureReadPointAsync"/>). 아니면 null.</summary>
        public CopyReadPoint? ReadPoint { get; set; }

        public Dictionary<string, TableState> Tables { get; } = new(StringComparer.Ordinal);

        public List<DisabledObject> Disabled { get; } = [];

        /// <summary>이번 실행이 표보다 먼저 만들려 한 시퀀스(대상 소유자·이름 → 상태, CopyRunner.Create.cs).</summary>
        public Dictionary<(string Owner, string Name), CreatedSequence> Sequences { get; } = [];

        /// <summary>끈 객체의 진행을 보인 규칙 키(다시 켤 때도 같은 줄에 보인다).</summary>
        public Dictionary<DisabledObject, string> DisabledKeys { get; } = [];

        public List<FailedRow> FailedRows { get; } = [];

        /// <summary>알림. 더할 때마다 작업 로그에도 한 줄 남긴다.</summary>
        public NoticeList Notices { get; }

        /// <summary>지금 단계(마지막 진행 보고의 단계). 작업 로그 줄의 단계 칸. 레인이면 그 레인의 것.</summary>
        public string CurrentPhase
        {
            get => _lane.Value is { } lane ? lane.Phase : _phase;
            private set
            {
                if (_lane.Value is { } lane)
                {
                    lane.Phase = value;
                }
                else
                {
                    _phase = value;
                }
            }
        }

        /// <summary>지금 표(마지막 진행 보고의 규칙 키). 작업 로그 줄의 표 칸. 레인이면 그 레인의 것.</summary>
        public string? CurrentTable
        {
            get => _lane.Value is { } lane ? lane.Table : _table;
            private set
            {
                if (_lane.Value is { } lane)
                {
                    lane.Table = value;
                }
                else
                {
                    _table = value;
                }
            }
        }

        /// <summary>실행 전체에 걸린 시간.</summary>
        public Stopwatch Clock { get; } = Stopwatch.StartNew();

        /// <summary>전체를 멈춤(끄기 실패·ORA-01555·원본 준비 실패). 중지(<see cref="Stopped"/>)와 달리 결과는 실패다.</summary>
        public bool Aborted => _aborted;

        public bool Stopped { get; set; }

        public bool CommitFailed { get; set; }

        public bool RolledBackAll { get; set; }

        /// <summary>「실패 행만 다시 넣기」 실행.</summary>
        public bool IsRetry { get; set; }

        /// <summary>「실패 행만 다시 넣기」의 입력 행. 확정되지 않은 표의 행은 끝낼 때 그대로 결과에 돌려준다.</summary>
        public IReadOnlyList<FailedRow> RetryInputs { get; set; } = [];

        /// <summary>끈 트리거·FK 중 다시 켜지 못한 것이 있음. 결과는 실패다(꺼진 채 남은 것을 사용자가 알아야 한다, 설계 4장).</summary>
        public bool EnableFailed { get; set; }

        /// <summary>다시 만든 표의 트리거·권한·참조 FK 중 되살리지 못한 것이 있음. 결과는 실패다(사라진 객체를 사용자가 알아야 한다).</summary>
        public bool RestoreFailed { get; set; }

        /// <summary>대상 정리(DELETE) 뒤 커밋이 한 번이라도 성공함(다른 표의 DELETE 도 확정됨).</summary>
        public bool DeletesPersisted { get; set; }

        /// <summary>끝낼 때 대상 트랜잭션을 롤백으로 끝냈다(커밋하지 않은 적재가 모두 되돌아갔다).</summary>
        public bool TargetRolledBack { get; set; }

        /// <summary>
        /// 원본 명령이 취소에도 돌아오지 않아 원본 세션을 더 쓰지 않는다(<see cref="CopyRunner.AbandonStuckAsync"/>). Core 는 세션을 새로 열 수 없어 실행을 멈춘다.
        /// </summary>
        public bool SourceAbandoned
        {
            // 레인이면 그 레인의 원본이다.
            get => _lane.Value is { } lane ? lane.SourceAbandoned : _sourceAbandoned;
            set
            {
                if (_lane.Value is { } lane)
                {
                    lane.SourceAbandoned = value;
                }
                else
                {
                    _sourceAbandoned = value;
                }
            }
        }

        /// <summary>
        /// 대상 명령이 취소에도 돌아오지 않아 대상 세션을 더 쓰지 않는다(마무리의 커밋·롤백·다시 켜기·건수 비교를 보내지 않는다). 레인이면 그 레인의 대상이다
        /// (레인 마무리가 그 레인을 끝내지 못하면 주 값도 켠다: 남은 트랜잭션이 다시 켜기를 막는다).
        /// </summary>
        public bool TargetAbandoned
        {
            get => _lane.Value is { } lane ? lane.TargetAbandoned : _targetAbandoned;
            set
            {
                if (_lane.Value is { } lane)
                {
                    lane.TargetAbandoned = value;
                }
                else
                {
                    _targetAbandoned = value;
                }
            }
        }

        /// <summary>대상 쪽 대기 원인 감시(WH1). 앱이 감시 세션 여는 길을 주지 않았으면 null.</summary>
        public CopyWaitProbe? WaitProbe { get; set; }

        /// <summary>규칙 키.</summary>
        public string Key(CopyTablePlan plan) => CopyOwners.Key(Job, plan.Rule);

        public TableState Table(CopyTablePlan plan)
        {
            var key = Key(plan);
            if (!Tables.TryGetValue(key, out var t))
            {
                t = new TableState(key, plan.Target.Table.Owner, plan.Rule.Table)
                {
                    Failed = message => Log(message, CopyLogKind.Error, key),
                };
                Tables[key] = t;
            }

            return t;
        }

        /// <summary>대상 계정·표로 이 실행의 표를 찾는다(대상 FK 의 자식·부모를 맞출 때). 없으면 null.</summary>
        public TableState? ByTarget(string owner, string table)
            => Tables.Values.FirstOrDefault(t => CopyOwners.SameOwner(t.Owner, owner)
                                                 && string.Equals(t.TableName, table, StringComparison.Ordinal));

        public void Abort(string? notice)
        {
            _aborted = true;
            if (notice is not null)
            {
                Notices.Add(notice);
            }
        }

        public void Progress(string table, long rows, long? estimated, string phase)
        {
            CurrentPhase = phase;
            CurrentTable = table;
            Send(new CopyProgress(table, rows, estimated, phase));
        }

        /// <summary>적재 진행: 읽은 수(<paramref name="rows"/>)와 넣은 수(<paramref name="written"/>)를 함께 보낸다.</summary>
        public void Progress(string table, long rows, long? estimated, string phase, long written)
        {
            CurrentPhase = phase;
            CurrentTable = table;
            Send(new CopyProgress(table, rows, estimated, phase) { Written = written });
        }

        /// <summary>
        /// 멈춤 표시(감시가 보낸다). 지금 표·단계(<see cref="CurrentTable"/>·<see cref="CurrentPhase"/>)를 바꾸지 않는다: 감시는 적재 흐름과 따로 돈다.
        /// <paramref name="waits"/> 가 비면 표시를 지운다.
        /// </summary>
        /// <param name="phase">진행 줄 단계(적재, 또는 대상 정리 DELETE 를 기다릴 때 대상 정리).</param>
        public void ReportWaits(string table, long rows, long? estimated, long written, IReadOnlyList<CopyWait> waits, string phase = PhaseLoad) =>
            Send(new CopyProgress(table, rows, estimated, phase) { Written = written, Waits = waits });

        /// <summary>
        /// 테이블 적재 끝 알림(<see cref="PhaseLoadEnd"/>). 지금 표·단계를 바꾸지 않는다(뒤따르는 로그 줄의 단계 이름이 달라지지 않게).
        /// </summary>
        public void ReportLoadEnd(string table, long rows, long written) =>
            Send(new CopyProgress(table, rows, rows, PhaseLoadEnd) { Written = written });

        /// <summary>진행·로그 보고는 한 번에 하나씩(적재 흐름·멈춤 감시가 서로 다른 스레드에서 보낼 수 있다).</summary>
        private void Send(CopyProgress p)
        {
            if (_progress is null)
            {
                return;
            }

            lock (_reportGate)
            {
                _progress.Report(p);
            }
        }

        /// <summary>단계를 바꾼다(표와 상관없는 단계: 시작·원본 준비·마무리). 진행 줄은 보내지 않는다.</summary>
        public void Enter(string phase)
        {
            CurrentPhase = phase;
            CurrentTable = null;
        }

        /// <summary>작업 로그 한 줄. 표·단계를 주지 않으면 지금 표·단계다. 행 값·비밀은 넣지 않는다(부르는 쪽 책임).</summary>
        public void Log(string text, CopyLogKind kind = CopyLogKind.Info, string? table = null, string? phase = null) =>
            Send(CopyProgress.ForLog(new CopyLogEntry(DateTimeOffset.Now, phase ?? CurrentPhase, table ?? CurrentTable, text, kind)));

        /// <summary>보낸 문장 한 줄(원본·대상, 바인드 요약).</summary>
        public void LogStatement(ISession session, string sql, IReadOnlyDictionary<string, BindValue>? binds = null) =>
            Log(CopyLogText.Statement(SideOf(session), sql, binds), CopyLogKind.Statement);

        /// <summary>문장 줄의 쪽 글. 원본 세션 집합(주 원본·레인 원본) 가운데 하나면 「원본」, 아니면 「대상」.</summary>
        public string SideOf(ISession session) =>
            ReferenceEquals(session, _source) || ReferenceEquals(session, Source) || Workers.Exists(w => ReferenceEquals(w.Source, session)) ? "원본" : "대상";

        /// <summary>동시 복사 단계 알림(<see cref="CopyProgress.Stage"/>): 화면 상태 글.</summary>
        public void ReportStage(CopyStage stage) => Send(new CopyProgress(string.Empty, 0, null, PhaseLoad) { Stage = stage });

        /// <summary>
        /// 대상 문장 결과를 받는다. 성공이면 true. 중지로 취소됐으면 중지 예외를 올리고, 정책 거부·확인 거절·실패면 표를 실패로 둔다(예외로 던지지 않는다).
        /// </summary>
        public static bool Accept(BulkResult r, TableState table, string what, CancellationToken ct)
        {
            if (r.Status == ExecutionStatus.Succeeded)
            {
                return true;
            }

            if (r.Status == ExecutionStatus.Cancelled && ct.IsCancellationRequested)
            {
                throw new OperationCanceledException(ct);
            }

            table.Fail(what + ": " + Describe(r));
            return false;
        }

        public CopyOutcome Outcome(bool faulted)
        {
            // 다시 켜지 못한 트리거·FK 가 있으면 중지했더라도 실패다(꺼진 채 남은 것을 결과에서 알아야 한다).
            if (EnableFailed || RestoreFailed)
            {
                return CopyOutcome.Failed;
            }

            if (Stopped)
            {
                return CopyOutcome.Stopped;
            }

            var results = Tables.Values.Select(t => t.ToResult().Outcome).ToList();
            if (faulted || Aborted || CommitFailed || RolledBackAll || EnableFailed || (results.Count > 0 && results.All(o => o == CopyOutcome.Failed)))
            {
                return CopyOutcome.Failed;
            }

            return results.Any(o => o is CopyOutcome.Failed or CopyOutcome.PartiallyFailed) ? CopyOutcome.PartiallyFailed : CopyOutcome.Succeeded;
        }

        public CopyRunResult Result() =>
            new(Outcome(faulted: false), [.. Tables.Values.Select(t => t.ToResult())], [.. FailedRows], [.. Notices], ReadStartedAt)
            {
                // 버린 세션은 앱이 닫고 새로 연다(리뷰 PF1-3).
                SourceAbandoned = SourceAbandoned,
                TargetAbandoned = TargetAbandoned,
                ReadPoint = ReadPoint?.Text,
            };
    }
}
