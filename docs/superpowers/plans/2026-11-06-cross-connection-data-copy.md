# 접속 간 데이터 복사 구현 계획

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.
> 이 계획은 `/coordinator` 회차의 분해 입력으로도 쓴다. 「레인 분해표」가 레인·소유 범위·선행 관계를 정한다.

**Goal:** 원본 접속에서 읽은 행을 대상 접속에 바인드 배열로 넣는 클라이언트 경유 복사(규칙 편집 → 사전 점검 → 실행·검증)를 만든다.

**Architecture:** 순수 로직(작업 모델, 기간 계산, 규칙 → SQL, FK 정렬, 마스킹, 접속 재연결)은 `Oranda.Core/Transfer/Copy` 에 둔다. 작업 파일과 실행 기록은 `Oranda.Platform/Storage` 가 읽고 쓴다. 사전 점검과 실행기는 `Oranda.Oracle/Transfer/Copy` 에 두고, 모든 문장은 실행 엔진에 새로 더하는 대량 실행 경로(`IBulkExecutionService`)를 거친다. 이 경로는 문장마다 정책을 한 번 평가한 뒤 바인드 배열로 실행한다. 화면은 `Oranda.App/Features/DataCopy` 의 별도 창 하나에 3단계 뷰모델로 만든다.

**Tech Stack:** .NET 10, C#, ODP.NET Managed(`Oracle.ManagedDataAccess`, ArrayBindCount), Avalonia, System.Text.Json 소스 생성, xUnit, Testcontainers(`gvenzl/oracle-free:23-slim`).

**Spec:** `docs/superpowers/specs/2026-11-06-cross-connection-data-copy-design.md` (이하 「설계」). 실행자는 이 계획과 설계를 함께 읽는다. 절 번호(예: 설계 6.2.1)는 설계 문서의 절이다.

## Global Constraints

- 대상 접속이 `ConnectionEnvironment.Production` 이면 쓰기를 하지 않는다. 선택 목록·재연결·가져오기·실행기 시작 네 곳에서 모두 막고, 우회 설정을 두지 않는다(설계 4장).
- 원본 세션은 `profile with { ReadOnly = true }` 로 열고, 시작하자마자 `SET TRANSACTION READ ONLY` 를 실행한다. 정책 관문의 읽기 전용 규칙이 SELECT 외 문장을 한 번 더 막는다.
- `DROP TABLE`·`CREATE TABLE` 은 대상 환경이 `Development` 일 때만 생성·실행한다.
- 모든 쓰기 문장은 `IExecutionPolicy` 를 지난다. `Deny` 는 미리 받은 확인으로도 통과하지 못한다.
- 행 값은 디스크에 남기지 않는다. 예외는 실행 기록에 남기는 실패 행의 키 열 값뿐이다. 마스킹 대상 열 값은 어떤 경우에도 남기지 않는다(설계 4장, 9장).
- 작업 파일에 비밀번호를 넣지 않는다. 비밀번호는 기존 `ICredentialStore` 와 `ConnectionDialogs.PromptPasswordAsync` 로 얻는다.
- 실제 서버 주소, 회사 DB 별칭, TNS 이름을 코드·시험·문서·커밋에 넣지 않는다. 시험 사용자 이름은 `"CP_" + 태그 + "_SRC"` 처럼 만든다. 문서 예시는 `db.example.com`, `ORCL` 을 쓴다.
- 기간 경계는 시작 포함·끝 제외(`>=`, `<`)다. 열에 `TO_DATE`·`TO_CHAR` 를 씌우지 않는다.
- 「비우고 넣기」는 `DELETE` 로 한다. `TRUNCATE` 는 생성하지 않는다. `DROP TABLE` 에 `PURGE` 를 붙이지 않는다.
- 기본값: 배치 5,000행, 커밋 단위 테이블마다, 실패 행 상한 100, 시퀀스 차이 확인 기준 10,000,000.
- D-006: App 은 Oracle 서비스를 처음 쓸 때 `IServiceProvider` 에서 꺼낸다. 새 서비스 구현에는 `[OrandaService(typeof(인터페이스))]` 를 붙이고, 구현 클래스는 `internal sealed` 로 둔다.
- 계층: Core 는 Oracle·Platform·App 을 참조하지 않는다(`tests/Oranda.Architecture.Tests`).
- 식별자는 항상 `SqlIdentifier.QuoteAlways` 로 감싼다(`src/Oranda.Core/Text/SqlIdentifier.cs`).
- 통합 시험은 `[Trait("Category", "Integration")]`, `[Collection(OracleFreeShared.Name)]` 를 붙이고 기존 컨테이너 하나 안에 시험 사용자를 만든다. 이 PC 에서는 통합 시험을 돌리지 않는다. CI 또는 win144 에서 돌린다.
- 커밋 메시지는 리포 관례(영어 Conventional Commits, 예: `feat(copy): ...`)를 따르고 끝에 `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>` 를 붙인다.

## Review Focus

1. **4,000자(바이트)를 넘는 LOB**: 그리드 경로(`OracleValues.ReadCell`)는 LOB 을 미리보기로 자른다. 복사는 CLOB·BLOB 를 끝까지 읽어 바이트 단위로 같게 넣어야 한다. → Task 6 통합 시험 `Array_insert_round_trips_large_lobs_byte_identical`.
2. **동료가 보낸 작업의 대상 이름이 내 PC 에서는 운영 접속인 경우**: 선택 목록만 막으면 가져오기·재연결로 운영 접속이 대상으로 들어온다. 재연결 결과가 운영이면 대상을 비우고 알려야 한다. → Task 5 `Target_relink_to_production_profile_is_blocked`, Task 12 `Opening_job_whose_target_is_production_clears_target`.
3. **트리거·FK 를 끈 직후 앱이 죽는 경우**: 끈 목록을 `DISABLE` 전에 기록하고 디스크에 써 두어야 다음에 열 때 「다시 켜기」를 보일 수 있다. → Task 7 `Unfinished_run_lists_disabled_objects`, Task 9 `Disabled_objects_are_logged_before_disable`.
4. **문자열 날짜 열에 길이가 다른 값, `YYYYMM` 월 경계**: 경계 문자열이 틀리면 한 달이 통째로 빠지거나 겹친다. → Task 2 `Month_format_bounds_round_to_month`, Task 8 `Precheck_counts_wrong_length_string_dates`.
5. **지난 복사 뒤 원본에서 기간 열 값이 바뀐 행을 다시 복사하는 경우**: 범위 DELETE 로 지워지지 않은 옛 행 때문에 ORA-00001 이 나면 안 된다(PK MERGE). → Task 9 `Rerun_after_period_value_change_merges_without_dup`.

---

## 레인 분해표 (coordinator 입력)

| id | 제목 | 크기 | 레인 | 선행 | 무거운 명령(시험 범위) |
|---|---|---|---|---|---|
| T1 | Core 계약: 작업 모델·저장소·점검·실행기 인터페이스 | M | A | - | 아니오(Core.Tests 의 Copy) |
| T2 | 기간 계산·문자열 날짜 형식 | S | A | T1 | 아니오 |
| T3 | 규칙 → SQL 생성 | L | A | T1, T2, T6(Core 커밋) | 아니오 |
| T4 | FK 정렬·기간 열 후보·시퀀스 후보 | M | A | T1 | 아니오 |
| T5 | 마스킹·접속 재연결·운영 차단 | M | A | T1 | 아니오 |
| T6 | 대량 실행 경로(정책 1회 + 바인드 배열 + 전체 LOB 읽기) | M | B | - | 통합 시험(CI·win144) |
| T7 | 작업 파일 저장소·실행 기록 | M | C | T1 | 아니오(Platform.Tests 의 Copy) |
| T8 | 사전 점검 서비스 | L | D | T3, T4, T5, T6 | 통합 시험(CI·win144) |
| T9 | 복사 실행기 | L | D | T7, T8 | 통합 시험(CI·win144) |
| T10 | 테이블 다시 만들기(개발 접속만) | M | D | T9 | 통합 시험(CI·win144) |
| T11 | 창·규칙 편집 단계·내 작업 메뉴·진입점 | L | F | T1, T2, T4, T5 | 아니오(App.Tests 의 DataCopy) |
| T12 | 사전 점검·실행·검증 단계 화면 | L | F | T1, T11 | 아니오(App.Tests 의 DataCopy, 가짜 서비스) |
| T13 | 문서 마감(PRD·data-model) | S | G | T12 | 아니오 |
| T14 | 전체 검증·실제 서비스 연결 확인·실제 앱 확인 | S | 조정자 | T9, T10, T12, T13 | 전체 시험(win144), 실제 앱(Mac) |

레인 소유 범위(겹치지 않는다):

- **A**: `src/Oranda.Core/Transfer/Copy/**`, `tests/Oranda.Core.Tests/Transfer/Copy/**`. 단 `CreateTableDdl.cs` 와 `CreateTableDdlTests.cs` 는 D 소유다. T1 은 혼자 먼저 머지한다(다른 레인이 이 계약에 맞춰 빌드한다).
- **B**: `src/Oranda.Core/Execution/IBulkExecutionService.cs`, `src/Oranda.Core/Execution/IExecutionService.cs`(옵션 한 개), `src/Oranda.Core/Execution/BindValue.cs`(열거 값 한 개), `src/Oranda.Core/History/HistoryEntry.cs`(열거 값 한 개), `src/Oranda.Oracle/Execution/**`, 해당 시험. 공유 API 이므로 단독으로 일찍 머지한다. **첫 커밋은 Core 쪽 추가분만**(`IBulkExecutionService`, `PreConfirmed`, `TimestampLtz`, `HistorySource.DataCopy`) 담아 바로 머지한다. 선행 작업이 없고, 레인 A 의 T3 가 이 커밋을 기다린다.
- **C**: `src/Oranda.Core/Storage/IAppDataPaths.cs`, `src/Oranda.Platform/Storage/AppDataPaths.cs`, `src/Oranda.Platform/Storage/Copy/**`, `src/Oranda.Core/Settings/SchemaVersions.cs`, `IAppDataPaths` 가짜 구현 3곳(Task 7 Files), `tests/Oranda.Platform.Tests/Storage/Copy/**`.
- **D**: `src/Oranda.Oracle/Transfer/Copy/**`, `src/Oranda.Core/Transfer/Copy/CreateTableDdl.cs`, `tests/Oranda.Oracle.Tests/Transfer/Copy/**`, `tests/Oranda.Oracle.IntegrationTests/Transfer/Copy/**`.
- **F**: `src/Oranda.App/Features/DataCopy/**`, `src/Oranda.App/Features/DedicatedSession/DedicatedSessionCommands.cs`(도우미 한 개 분리), `docs/design/commands.md`, `tests/Oranda.App.Tests/Features/DataCopy/**`.
- **G**: `docs/PRD.md`, `docs/design/data-model.md`.

무거운 명령: 통합 시험과 리포 전체 시험은 이 PC 에서 돌리지 않는다. 조정자가 win144 에 나눠 맡기거나 CI 결과로 확인한다. 이 PC 에서는 아래 「로컬 시험」 명령만 돌린다.

---

### Task 1: Core 계약

**Files:**
- Create: `src/Oranda.Core/Transfer/Copy/CopyJob.cs` (작업 모델 레코드·열거)
- Create: `src/Oranda.Core/Transfer/Copy/CopyJobJsonContext.cs` (소스 생성 문맥, `OrandaJsonContext` 와 같은 옵션)
- Create: `src/Oranda.Core/Transfer/Copy/ICopyJobStore.cs`, `ICopyRunLog.cs`, `ICopyPrecheckService.cs`, `ICopyRunner.cs`
- Test: `tests/Oranda.Core.Tests/Transfer/Copy/CopyJobJsonTests.cs`

**Interfaces:**
- Produces (네임스페이스 `Oranda.Core.Transfer.Copy`, 설계 6.1·6.2·13장의 필드 이름을 이것으로 정한다):

```csharp
public sealed record CopyJob
{
    public int SchemaVersion { get; init; } = CopyJobSchema.Current;   // CopyJobSchema.Current = 1
    public required string Id { get; init; }                           // Guid "N"
    public required string Name { get; init; }
    public required ConnectionRef Source { get; init; }
    public required ConnectionRef Target { get; init; }
    public required string SourceOwner { get; init; }
    public required string TargetOwner { get; init; }
    public CopyPeriod Period { get; init; } = new(PeriodKind.None);
    public IReadOnlyList<TableRule> Tables { get; init; } = [];
    public CopyOptions Options { get; init; } = new();
    public IReadOnlyList<SequenceRule> Sequences { get; init; } = [];
    public bool ConsistentRead { get; init; } = true;                  // 설계 8.1 「같은 시점 기준으로 읽기」
    public required string MaskSalt { get; init; }                     // 32바이트 난수 Base64, 작업 만들 때 한 번
    public LastRunInfo? LastRun { get; init; }
}
public sealed record ConnectionRef(string Name, string User, Guid? IdHint = null);
public enum PeriodKind { None, LastWeek, LastMonth, Last3Months, PreviousMonth, Custom }
public sealed record CopyPeriod(PeriodKind Kind, DateTime? From = null, DateTime? To = null);
public sealed record ResolvedPeriod(DateTime? From, DateTime? To);
public enum RuleKind { All, Period, ParentKey, Custom, Exclude }
public enum TargetAction { DeleteAllInsert, DeleteRangeInsert, AppendOnly, MergeByPk, Recreate }
public sealed record PeriodColumn(string Column, string? StringFormat = null);  // null = DATE·TIMESTAMP 열
public sealed record ParentLink(string ParentTable, IReadOnlyList<string> ChildColumns,
                                IReadOnlyList<string> ParentColumns, string? ForeignKeyName = null);
public enum MaskKind { Fixed, KeepFirst, KeepLast, Hash, Null }
public sealed record MaskRule(string Column, MaskKind Kind, string? Value = null, int Count = 0);
public sealed record TableRule(string Table, RuleKind Kind, TargetAction Action)
{
    public IReadOnlyList<PeriodColumn> PeriodColumns { get; init; } = [];  // 0~2개, 2개면 OR
    public ParentLink? Parent { get; init; }
    public string? CustomWhere { get; init; }
    public IReadOnlyList<MaskRule> Masks { get; init; } = [];
    public IReadOnlyList<string> SkipColumns { get; init; } = [];
}
public enum CommitMode { PerTable, PerBatch, AtEnd }
public sealed record CopyOptions
{
    public int BatchSize { get; init; } = 5000;
    public CommitMode Commit { get; init; } = CommitMode.PerTable;
    public bool DisableTriggersByDefault { get; init; }
    public int MaxFailedRows { get; init; } = 100;
    public long SequenceGapConfirm { get; init; } = 10_000_000;
}
public sealed record SequenceRule(string Sequence, string Table, string Column);
public enum CopyOutcome { Succeeded, PartiallyFailed, Failed, Stopped }
public sealed record LastRunInfo(DateTimeOffset At, CopyOutcome Outcome, long Rows);
public static class TargetActions { public static TargetAction DefaultFor(RuleKind kind, bool hasPrimaryKey); }
```

- `ICopyJobStore`(구현 T7): `ListAsync(ct) → IReadOnlyList<CopyJobEntry>`, `SaveAsync(CopyJob, ct)`, `DuplicateAsync(string id, string newName, ct) → CopyJob`, `RenameAsync(string id, string newName, ct)`, `DeleteAsync(string id, ct)`, `ExportAsync(string id, string path, ct)`, `ImportAsync(string path, ct) → CopyJob`. `CopyJobEntry(CopyJob Job, string FilePath, bool IsReadOnly, string? Notice)`.
- `ICopyRunLog`(구현 T7): `BeginAsync(CopyRunHeader, ct) → ICopyRunRecord`, `FindUnfinishedAsync(string jobId, ct) → IReadOnlyList<UnfinishedRun>`, `MarkRecoveredAsync(string runFile, ct)`. `ICopyRunRecord : IAsyncDisposable` 은 `RecordDisabledAsync(IReadOnlyList<DisabledObject>, ct)`, `RecordStatementAsync(string sql, ct)`, `RecordTableAsync(TableRunResult, ct)`, `RecordFailedRowAsync(FailedRow, ct)`, `CompleteAsync(CopyOutcome, ct)` 를 가진다. 레코드: `CopyRunHeader(string JobId, string JobName, string SourceName, string TargetName, ResolvedPeriod Period, DateTimeOffset StartedAt)`, `DisabledObject(DisabledKind Kind, string Owner, string Table, string Name)`, `enum DisabledKind { Trigger, ForeignKey }`, `UnfinishedRun(string RunFile, DateTimeOffset StartedAt, IReadOnlyList<DisabledObject> Disabled)`, `FailedRow(string Table, IReadOnlyDictionary<string, string?> Key, string Error)`(키 값 글자 형식: 숫자 = 고정 소수점 invariant 글자, DATE = `yyyy-MM-ddTHH:mm:ss`, TIMESTAMP = `yyyy-MM-ddTHH:mm:ss.fffffff`(TZ 는 끝에 `zzz`), RAW = 대문자 16진수, 문자 = 그대로. 값이 ODP.NET 형식이므로 변환 함수는 T9 의 `FailedRowKeys` 에 둔다), `TableRunResult(string Table, long SourceRows, long TargetRows, TimeSpan Elapsed, CopyOutcome Outcome, string? Message)`.
- `ICopyPrecheckService`(구현 T8): `RunAsync(CopySessions sessions, CopyJob job, ResolvedPeriod period, IProgress<string>? progress, CancellationToken ct) → Task<PrecheckReport>`. `CopySessions(ISession Source, ISession Target)`. `enum CheckLevel { Pass, Confirm, Error }`. `enum CheckChoice { DisableTriggers, DisableForeignKeys, SkipColumn, ProceedSequenceGap, SwitchToMerge }`. `CheckItem(string? Table, string Code, CheckLevel Level, string Message) { IReadOnlyList<CheckChoice> Choices; string? Column; }`. `CopyTablePlan(TableRule Rule, TableInfo Source, TableInfo Target, IReadOnlyList<string> Columns, IReadOnlyList<string> KeyColumns) { bool CanDisableTriggers; IReadOnlyList<string> Triggers; IReadOnlyList<DisabledObject> DisablableForeignKeys; }`. `PreviewStatement(string Sql, PreviewSide Side, PolicyDecisionKind Policy, string? PolicyMessage, string? RetypeText)`, `enum PreviewSide { Source, Target }`. `PrecheckReport(IReadOnlyList<CheckItem> Items, IReadOnlyList<CopyTablePlan> Order, IReadOnlyList<PreviewStatement> Preview, IReadOnlyDictionary<string, long> EstimatedRows) { bool HasErrors => Items.Any(i => i.Level == CheckLevel.Error); }`. `Order` 는 부모 → 자식 순서다.
- `ICopyRunner`(구현 T9): `RunAsync(CopySessions sessions, CopyJob job, PrecheckReport report, CopyDecisions decisions, ResolvedPeriod period, IProgress<CopyProgress>? progress, CopyRunControl control, CancellationToken ct) → Task<CopyRunResult>`, `RetryFailedRowsAsync(CopySessions, CopyJob, PrecheckReport, IReadOnlyList<FailedRow>, IProgress<CopyProgress>?, CancellationToken) → Task<CopyRunResult>`. `CopyDecisions(IReadOnlySet<string> DisableTriggerTables, IReadOnlySet<string> DisableForeignKeys, IReadOnlySet<string> PreConfirmedSql, bool ProceedSequenceGap)`. `CopyProgress(string Table, long Rows, long? Estimated, string Phase)`. `CopyRunResult(CopyOutcome Outcome, IReadOnlyList<TableRunResult> Tables, IReadOnlyList<FailedRow> FailedRows, IReadOnlyList<string> Notices, DateTimeOffset ReadStartedAt)`. `sealed class CopyRunControl { void Pause(); void Resume(); bool IsPaused { get; } Task WaitIfPausedAsync(CancellationToken ct); }` 중지는 `ct` 취소로 한다.

- [ ] **Step 1: 실패하는 시험 작성** — `CopyJobJsonTests`:
  - `Round_trips_job_with_all_rule_kinds`: 다섯 규칙 종류와 마스킹·시퀀스·두 기간 열을 가진 작업을 `CopyJobJsonContext.Default.CopyJob` 으로 쓰고 다시 읽으면 `Assert.Equivalent(job, back)`.
  - `Writes_camel_case_enum_strings_and_no_password_field`: 직렬화 문자열에 `"kind": "Period"`, `"action": "DeleteRangeInsert"` 가 있고, `password`·`credential` 이라는 글자가 없다.
  - `Default_action_follows_rule_kind`: `DefaultFor(All, true) == MergeByPk`, `DefaultFor(All, false) == DeleteAllInsert`, `DefaultFor(Period, *) == DeleteRangeInsert`, `DefaultFor(ParentKey, *) == DeleteRangeInsert`, `DefaultFor(Custom, *) == DeleteRangeInsert`.
- [ ] **Step 2: 실패 확인** — `dotnet test tests/Oranda.Core.Tests --filter "FullyQualifiedName~Transfer.Copy"` → 컴파일 오류(형식 없음).
- [ ] **Step 3: 위 계약을 구현한다.** 문맥 옵션은 `OrandaJsonContext` 와 같게 둔다(camelCase, 문자열 열거, null 생략, 들여쓰기, `UtcDateTimeOffsetConverter`). `Exclude` 의 기본 대상 처리는 `DeleteRangeInsert` 를 돌려주되 실행기가 쓰지 않는다.
- [ ] **Step 4: 통과 확인** — 같은 명령 → PASS.
- [ ] **Step 5: 커밋** — `feat(copy): add copy job model and service contracts`

### Task 2: 기간 계산·문자열 날짜 형식

**Files:**
- Create: `src/Oranda.Core/Transfer/Copy/PeriodCalculator.cs`, `src/Oranda.Core/Transfer/Copy/DateStringFormat.cs`
- Test: `tests/Oranda.Core.Tests/Transfer/Copy/PeriodTests.cs`

**Interfaces:**
- Consumes: `CopyPeriod`, `ResolvedPeriod`(T1)
- Produces: `PeriodCalculator.Resolve(CopyPeriod period, DateTime today) → ResolvedPeriod`; `DateStringFormat.KnownFormats` = `["YYYYMMDD", "YYYYMMDDHH24MISS", "YYYY-MM-DD", "YYYY-MM-DD HH24:MI:SS", "YYYYMM"]`; `DateStringFormat.GuessFromLength(int length) → string?`; `DateStringFormat.Format(DateTime value, string format) → string`; `DateStringFormat.Bounds(ResolvedPeriod period, string format) → (string From, string To)`; `DateStringFormat.HasDay(string format) → bool`; `DateStringFormat.IsValid(string format) → bool`.

- [ ] **Step 1: 실패하는 시험 작성** (`today = 2026-11-06 15:30`):
  - `Last_month_ends_tomorrow_midnight`: `LastMonth` → `[2026-10-07 00:00, 2026-11-07 00:00)`. `LastWeek` → `[2026-10-31, 2026-11-07)`. `Last3Months` → `[2026-08-07, 2026-11-07)`.
  - `Previous_month_is_calendar_month`: `[2026-10-01, 2026-11-01)`.
  - `Custom_keeps_saved_dates_and_none_is_open`: `Custom(2026-01-01, 2026-02-01)` 그대로, `None` → `(null, null)`.
  - `Guess_format_from_length`: 6 → `YYYYMM`, 8 → `YYYYMMDD`, 10 → `YYYY-MM-DD`, 14 → `YYYYMMDDHH24MISS`, 19 → `YYYY-MM-DD HH24:MI:SS`, 7 → `null`.
  - `Formats_with_zero_padding`: `Format(2026-03-05 04:07:09, "YYYYMMDDHH24MISS") == "20260305040709"`, `"YYYY-MM-DD HH24:MI:SS"` → `"2026-03-05 04:07:09"`.
  - `Month_format_bounds_round_to_month` (Review Focus 4): `LastMonth` 의 `Bounds(.., "YYYYMM") == ("202610", "202612")`, `PreviousMonth` → `("202610", "202611")`. 끝 경계가 달의 1일 0시면 그 달, 아니면 다음 달이다.
  - `Rejects_unknown_tokens`: `IsValid("YYYY/MM/DD") == true`, `IsValid("DD-MON-YYYY") == false`(`MON` 은 언어에 따라 순서가 틀어지므로 받지 않는다).
- [ ] **Step 2: 실패 확인** — `dotnet test tests/Oranda.Core.Tests --filter "FullyQualifiedName~Transfer.Copy.PeriodTests"` → FAIL.
- [ ] **Step 3: 구현** — 토큰은 `YYYY`, `MM`, `DD`, `HH24`, `MI`, `SS` 만 받고, 나머지 글자는 `-`, `/`, `:`, `.`, 공백 같은 구분자만 허용한다.
- [ ] **Step 4: 통과 확인** — 같은 명령 → PASS.
- [ ] **Step 5: 커밋** — `feat(copy): compute relative periods and string date bounds`

### Task 3: 규칙 → SQL 생성

**Files:**
- Create: `src/Oranda.Core/Transfer/Copy/CopySqlBuilder.cs`
- Test: `tests/Oranda.Core.Tests/Transfer/Copy/CopySqlBuilderTests.cs`

**Interfaces:**
- Consumes: T1 모델, `DateStringFormat.Bounds`(T2), `TableInfo`·`ColumnInfo`(`Oranda.Core.Metadata`), `BindValue`·`BindDataType`(`Oranda.Core.Execution`; `TimestampLtz` 는 T6 이 더한다. T6 머지 전에는 이 값만 시험에서 빼 둔다)
- Produces: `sealed record SqlWithBinds(string Sql, IReadOnlyDictionary<string, BindValue> Binds)`. `sealed class CopySqlBuilder(CopyJob job, ResolvedPeriod period, IReadOnlyDictionary<string, TableInfo> sourceTables, IReadOnlyDictionary<string, TableInfo> targetTables)` 의 메서드:
  - `SourceSelect(string table, IReadOnlyList<string> columns) → SqlWithBinds` (원본 소유자)
  - `SourceCount(string table) → SqlWithBinds`
  - `TargetCount(string table) → SqlWithBinds` (대상 소유자, 같은 조건)
  - `TargetDelete(string table) → SqlWithBinds?` (`DeleteAllInsert` → 조건 없는 DELETE, `DeleteRangeInsert` → 범위 DELETE, 그 밖은 null)
  - `Insert(string table, IReadOnlyList<string> columns) → string`, `Merge(string table, IReadOnlyList<string> columns, IReadOnlyList<string> keyColumns) → string` (바인드 이름은 `:C1`…`:Cn`, 열 순서와 같다)
  - `SourceSelectByKeys(string table, IReadOnlyList<string> columns, IReadOnlyList<string> keyColumns) → string` (실패 행 다시 읽기, 키 바인드 `:K1`…`:Kn`)

생성 규칙(시험이 정하지 못하는 부분):
- 기간 열 바인드 이름은 문장 안에서 나타나는 순서대로 `:B1_FROM`, `:B1_TO`, `:B2_FROM` … 이다. 부모 키 서브쿼리 안의 기간 조건도 같은 번호 공간을 쓴다.
- 바인드 타입은 열 타입을 따른다: `DATE` → `Date`, `TIMESTAMP(n)` → `Timestamp`, `TIMESTAMP(n) WITH TIME ZONE` → `TimestampTz`, `TIMESTAMP(n) WITH LOCAL TIME ZONE` → `TimestampLtz`, 문자열 형식이 있으면 `Varchar2`(값은 `DateStringFormat.Bounds`).
- 두 기간 열은 `(a >= :B1_FROM AND a < :B1_TO) OR (b >= :B2_FROM AND b < :B2_TO)` 를 괄호로 묶는다.
- 직접 조건은 `WHERE (<사용자 조건>)` 이고, 조건 안에 `:FROM_DT`·`:TO_DT` 가 있으면 `Date` 바인드로 기간 값을 넣는다.
- 부모 키: 단일 열이면 `c IN (SELECT p FROM 부모 WHERE <부모 조건>)`, 여러 열이면 `(c1, c2) IN (SELECT p1, p2 …)`. 부모 규칙이 `All` 이면 부모 조건 없이 서브쿼리만 둔다. 대상 쪽 문장은 대상 소유자의 부모 테이블로 같은 모양을 만든다.
- MERGE: `MERGE INTO o.t T USING (SELECT :C1 AS "A", … FROM DUAL) S ON (T."K" = S."K" …) WHEN MATCHED THEN UPDATE SET <키 아닌 열> WHEN NOT MATCHED THEN INSERT (…) VALUES (…)`. 모든 열이 키면 `WHEN MATCHED` 를 뺀다.

- [ ] **Step 1: 실패하는 시험 작성** (각 시험은 생성된 SQL 문자열 전체와 바인드 사전을 비교한다):
  - `All_rule_selects_without_where`
  - `Period_date_column_uses_typed_binds`: DATE 열이면 `"ORDER_DT" >= :B1_FROM AND "ORDER_DT" < :B1_TO`, 바인드 타입 `Date`, 값은 `ResolvedPeriod` 그대로.
  - `Period_timestamp_tz_and_ltz_columns_bind_matching_types`
  - `Period_string_column_binds_formatted_strings`: `VARCHAR2(8)` + `YYYYMMDD` → 바인드 `Varchar2` 값 `"20261007"`, `"20261107"`. SQL 에 `TO_DATE` 가 없다.
  - `Two_period_columns_are_ored`
  - `Custom_where_binds_from_and_to_only_when_used`
  - `Parent_key_nests_two_levels`: 손자 → 자식(부모 키) → 부모(기간). 바인드는 `:B1_FROM`, `:B1_TO` 하나뿐이다.
  - `Parent_key_with_composite_columns_uses_tuple_in`
  - `Target_delete_uses_target_owner_and_target_parent`
  - `Delete_all_has_no_where_and_append_has_no_delete`
  - `Merge_updates_non_key_columns_and_omits_matched_when_all_keys`
  - `Identifiers_are_always_quoted`: 소문자 이름 `"MixedCase"` 가 그대로 따옴표 안에 들어간다.
- [ ] **Step 2: 실패 확인** — `dotnet test tests/Oranda.Core.Tests --filter "FullyQualifiedName~CopySqlBuilderTests"` → FAIL.
- [ ] **Step 3: 구현** — 위 규칙대로 `CopySqlBuilder` 를 만든다.
- [ ] **Step 4: 통과 확인** — 같은 명령 → PASS.
- [ ] **Step 5: 커밋** — `feat(copy): generate source and target SQL from table rules`

### Task 4: FK 정렬·기간 열 후보·시퀀스 후보

**Files:**
- Create: `src/Oranda.Core/Transfer/Copy/CopyOrder.cs`, `PeriodColumnRanker.cs`, `SequenceGuess.cs`
- Test: `tests/Oranda.Core.Tests/Transfer/Copy/CopyOrderTests.cs`, `PeriodColumnRankerTests.cs`, `SequenceGuessTests.cs`

**Interfaces:**
- Produces:
  - `CopyOrder.Sort(IReadOnlyList<string> tables, IReadOnlyList<(string Child, string Parent)> edges) → CopyOrderResult(IReadOnlyList<string> ParentsFirst, IReadOnlyList<IReadOnlyList<string>> Cycles)`. 입력 순서를 지키는 안정 정렬이다. 자기 참조 간선은 무시한다. 작업 밖 테이블이 끼인 간선도 무시한다. 순환에 든 테이블은 남은 입력 순서대로 뒤에 붙인다.
  - `PeriodColumnRanker.Rank(IReadOnlyList<ColumnInfo> columns) → IReadOnlyList<PeriodColumnCandidate>`, `PeriodColumnCandidate(ColumnInfo Column, bool IsCreatedAt, string? DefaultFormat)`. 후보는 DATE·TIMESTAMP 계열과, 길이가 6·8·10·14·19 인 CHAR·VARCHAR2 열이다. 업무 날짜 열이 먼저, 생성 시각 열이 뒤에 온다. 생성 시각 이름 목록: `CREATED_AT`, `CREATE_DT`, `CREATE_DTM`, `CREATED_DATE`, `CRT_DT`, `CRT_DTM`, `REG_DT`, `REG_DTM`, `INS_DT`, `INS_DTM`, `FRST_REG_DTM`, `FIRST_REG_DTM`.
  - `SequenceGuess.Candidates(string table, IReadOnlyList<string> numericKeyColumns, IReadOnlyList<string> sequenceNames) → IReadOnlyList<SequenceRule>`. 패턴 우선순위: `SEQ_{COL}`, `{COL}_SEQ`, `{TABLE}_SEQ`, `SEQ_{TABLE}`, `{TABLE}_{COL}_SEQ`. 열 하나에 첫 번째 일치만 낸다.

- [ ] **Step 1: 실패하는 시험 작성**
  - `Sorts_parents_before_children_stably`: 입력 `[ORDER_ITEM, ORDER, CUSTOMER]`, 간선 `ORDER_ITEM→ORDER`, `ORDER→CUSTOMER` → `[CUSTOMER, ORDER, ORDER_ITEM]`, 순환 없음.
  - `Ignores_self_reference_and_outside_tables`
  - `Reports_cycle_and_keeps_input_order`: `A→B`, `B→A`, `C` → `ParentsFirst == [C, A, B]`, `Cycles == [[A, B]]`.
  - `Business_date_columns_come_before_created_at`: `ORDER_DT DATE`, `REG_DTM TIMESTAMP`, `SALE_YMD VARCHAR2(8)`, `NAME VARCHAR2(8)` 이 아닌 길이 30 열 → `[ORDER_DT, SALE_YMD(YYYYMMDD), REG_DTM(IsCreatedAt)]`.
  - `Guesses_sequence_by_name_patterns`: 시퀀스 `[SEQ_ORDER_ID, ORDERS_SEQ]`, 테이블 `ORDERS`, 열 `ORDER_ID` → `[SequenceRule("SEQ_ORDER_ID", "ORDERS", "ORDER_ID")]`.
- [ ] **Step 2: 실패 확인** — `dotnet test tests/Oranda.Core.Tests --filter "FullyQualifiedName~Transfer.Copy"` → FAIL.
- [ ] **Step 3: 구현** — Kahn 정렬을 쓰고, 큐에서 꺼낼 때 입력 순서가 가장 앞선 테이블을 고른다.
- [ ] **Step 4: 통과 확인** — 같은 명령 → PASS.
- [ ] **Step 5: 커밋** — `feat(copy): order tables by FK and suggest period columns and sequences`

### Task 5: 마스킹·접속 재연결·운영 차단

**Files:**
- Create: `src/Oranda.Core/Transfer/Copy/MaskFunctions.cs`, `ConnectionRelinker.cs`, `CopyTargetGuard.cs`
- Test: `tests/Oranda.Core.Tests/Transfer/Copy/MaskFunctionsTests.cs`, `ConnectionRelinkerTests.cs`

**Interfaces:**
- Produces:
  - `MaskFunctions.Apply(MaskRule rule, string? value, string salt, int maxLength) → string?` (문자 열 전용). 해시는 `HMACSHA256(key = Base64 디코드한 salt, data = UTF-8 값)` 의 대문자 16진수를 `maxLength` 로 자른다. `KeepFirst`·`KeepLast` 는 `Count` 글자를 남기고 나머지를 `*` 로 바꾼다. NULL 입력은 `Fixed` 외 규칙에서 NULL 그대로다.
  - `MaskFunctions.AllowedKinds(string dataType) → IReadOnlyList<MaskKind>`: 문자 타입이면 다섯 가지, 그 밖은 `Fixed`, `Null`.
  - `MaskFunctions.NewSalt() → string` (32바이트 `RandomNumberGenerator`).
  - `ConnectionRelinker.Resolve(ConnectionRef reference, IReadOnlyList<ConnectionProfile> profiles, bool isTarget) → RelinkResult(ConnectionProfile? Profile, RelinkKind Kind)`, `enum RelinkKind { ById, ByName, NotFound, BlockedProduction }`. 순서는 설계 6.3 이다(같은 ID·같은 이름 → 같은 이름 → 없음). 이름 비교는 대소문자를 구분하지 않는다.
  - `CopyTargetGuard.IsAllowedTarget(ConnectionProfile profile) → bool` (`Environment != Production`), `CopyTargetGuard.CanRecreate(ConnectionProfile profile) → bool` (`Environment == Development`), `CopyTargetGuard.EnsureAllowedTarget(ConnectionProfile profile)` 는 운영이면 `InvalidOperationException`.

- [ ] **Step 1: 실패하는 시험 작성**
  - `Hash_is_deterministic_per_salt_and_truncated`: 같은 salt·값 → 같은 결과, 다른 salt → 다른 결과, `maxLength = 10` 이면 길이 10, 결과는 `[0-9A-F]` 만.
  - `Keep_first_and_last_mask_rest`: `KeepFirst(3)` 에 `"01012345678"` → `"010********"`, `KeepLast(4)` → `"*******5678"`.
  - `Non_text_columns_allow_only_fixed_and_null`
  - `Relink_prefers_same_id_then_same_name`
  - `Relink_not_found_when_no_name_matches`
  - `Target_relink_to_production_profile_is_blocked` (Review Focus 2): 이름이 같은 접속이 운영이면 `isTarget = true` 일 때 `BlockedProduction` 이고 `Profile == null`. 원본(`isTarget = false`)이면 `ByName`.
  - `Guard_rejects_production_and_recreate_needs_development`
- [ ] **Step 2: 실패 확인** — `dotnet test tests/Oranda.Core.Tests --filter "FullyQualifiedName~Transfer.Copy"` → FAIL.
- [ ] **Step 3: 구현** — 위 시그니처대로 만든다.
- [ ] **Step 4: 통과 확인** — 같은 명령 → PASS.
- [ ] **Step 5: 커밋** — `feat(copy): add masking, connection relink and production guard`

### Task 6: 대량 실행 경로

**Files:**
- Create: `src/Oranda.Core/Execution/IBulkExecutionService.cs`
- Modify: `src/Oranda.Core/Execution/IExecutionService.cs` (`ExecutionOptions.PreConfirmed` 추가)
- Modify: `src/Oranda.Core/Execution/BindValue.cs` (`BindDataType.TimestampLtz` 를 열거 끝에 추가)
- Modify: `src/Oranda.Core/History/HistoryEntry.cs` (`HistorySource.DataCopy` 를 열거 끝에 추가. 이력 저장소는 이름 문자열로 저장하므로 이전 작업이 필요 없다)
- Modify: `src/Oranda.Oracle/Execution/OracleExecutionService.cs` (`RunAsync` 의 정책 단계를 `PolicyGate` 로 옮기고 미리 받은 확인 사용)
- Create: `src/Oranda.Oracle/Execution/PolicyGate.cs` (`internal static Task<PolicyGateResult> DecideAsync(IExecutionPolicy policy, IExecutionInteraction interaction, IEventBus events, ISession s, SqlStatement stmt, ExecutionOptions options, CancellationToken ct)`, 결과 = `Proceed`(적용한 결정 포함)·`Denied`·`Cancelled`. 두 실행 클래스가 함께 쓴다)
- Create: `src/Oranda.Oracle/Execution/OracleBulkExecutionService.cs`, `src/Oranda.Oracle/Execution/BulkBinds.cs`
- Modify: `src/Oranda.Oracle/Execution/BindBuilder.cs` (`TimestampLtz → OracleDbType.TimeStampLTZ`)
- Test: `tests/Oranda.Oracle.Tests/Execution/PreConfirmedPolicyTests.cs`, `tests/Oranda.Oracle.Tests/Execution/BulkBindsTests.cs`, `tests/Oranda.Oracle.IntegrationTests/Execution/BulkExecutionTests.cs`

**Interfaces:**
- Produces:

```csharp
namespace Oranda.Core.Execution;
public interface IBulkExecutionService
{
    // 정책을 한 번 평가한다. 실효 분류가 Query 가 아니면 행을 내지 않고 BulkDeniedException.
    // 값: CLOB·NCLOB → string(전체), BLOB·RAW → byte[](전체), 그 밖은 손실 없는 ODP.NET 값(OracleDecimal, OracleDate,
    // OracleTimeStamp*, OracleIntervalDS/YM, float, double, string). ExecuteArrayAsync 가 그대로 받는다.
    IAsyncEnumerable<object?[]> StreamRowsAsync(ISession s, SqlStatement query, IReadOnlyDictionary<string, BindValue> binds,
                                                ExecutionOptions options, CancellationToken ct);
    // 같은 SQL 텍스트에 정책을 한 번 평가하고, rows 를 ArrayBindCount 로 한 번에 실행한다.
    Task<BulkResult> ExecuteArrayAsync(ISession s, SqlStatement stmt, IReadOnlyList<BulkColumn> columns,
                                       IReadOnlyList<object?[]> rows, ExecutionOptions options, CancellationToken ct);
}
public sealed record BulkColumn(string BindName, string ServerType);   // ServerType = ColumnInfo.DataType
public sealed record BulkResult(ExecutionStatus Status, long RowsAffected)
{
    public DbError? Error { get; init; }
    public PolicyDecision? Policy { get; init; }
}
public sealed class BulkDeniedException(PolicyDecision decision) : Exception(decision.Message) { public PolicyDecision Decision { get; } = decision; }
// ExecutionOptions 에 추가:
// 호출한 쪽이 이미 사용자 확인을 받은 문장 텍스트(서수 비교). 정책이 Confirm 이고 텍스트가 이 집합에 있으면 IExecutionInteraction 을 부르지 않는다.
// Deny 는 그대로 거부한다. RetypeText 가 있는 Confirm(DROP)도 집합에 있으면 통과한다: 호출한 쪽이 다시 입력을 받는다(설계 6.2.3).
public IReadOnlySet<string>? PreConfirmed { get; init; }
```

- 구현 클래스는 따로 둔 `internal sealed class OracleBulkExecutionService`(`[OrandaService(typeof(IBulkExecutionService))]`)다. 서비스 컨테이너는 등록(서비스 형식)마다 인스턴스를 따로 만들므로, `OracleExecutionService` 에 속성을 하나 더 붙이면 세션별 상태(`_runtimes`)가 두 벌로 갈린다. 그래서 정책 단계만 `PolicyGate` 로 함께 쓴다. 세션 명령 잠금(`CommandGate`)과 바쁨 표시는 `RunAsync` 와 같게 잡는다. `StatementExecuted` 이벤트는 `ExecuteArrayAsync` 한 번에 하나만 낸다(이력은 `options.SkipHistory` 를 따른다).
- `BulkBinds.ToDbType(string serverType) → OracleDbType`: `VARCHAR2`·`CHAR`·`NVARCHAR2`·`NCHAR`, `NUMBER`, `BINARY_FLOAT`, `BINARY_DOUBLE`, `DATE`, `TIMESTAMP(n)`, `… WITH TIME ZONE`, `… WITH LOCAL TIME ZONE`, `INTERVAL DAY(n) TO SECOND(m)`, `INTERVAL YEAR(n) TO MONTH`, `RAW`, `CLOB`, `NCLOB`, `BLOB`. 그 밖은 `NotSupportedException`(설계 8.3 미지원 타입).

- [ ] **Step 1: 실패하는 단위 시험 작성** (`FakeSession`, `FakeExecutionInteraction`, `ExecutionPolicy` 사용)
  - `Preconfirmed_unfiltered_delete_skips_interaction`: `DELETE FROM T`(UnfilteredDml) 를 `PreConfirmed = {그 텍스트}` 로 실행하면 `ui.Confirmations.Count == 0` 이다(`FakeSession` 이라 드라이버 단계에서 `ArgumentException` 이 나는 것은 기대한다).
  - `Preconfirmed_does_not_override_deny`: 읽기 전용 프로필에서 같은 옵션으로 `DELETE` → `ExecutionStatus.Denied`.
  - `Confirm_without_preconfirmed_still_asks`: 집합에 없는 텍스트면 `Confirmations.Count == 1`.
  - `Maps_server_types_to_db_types`: `"TIMESTAMP(6) WITH LOCAL TIME ZONE" → TimeStampLTZ`, `"INTERVAL DAY(2) TO SECOND(6)" → IntervalDS`, `"BINARY_FLOAT" → BinaryFloat`, `"XMLTYPE"` → `NotSupportedException`.
- [ ] **Step 2: 실패 확인** — `dotnet test tests/Oranda.Oracle.Tests --filter "FullyQualifiedName~PreConfirmedPolicyTests|FullyQualifiedName~BulkBindsTests"` → FAIL.
- [ ] **Step 3: 구현** — 위 계약대로 만든다. LOB 은 `reader.GetOracleClob(i).Value`·`GetOracleBlob(i).Value` 로 끝까지 읽는다(`OracleValues.ReadCell` 의 미리보기를 쓰지 않는다).
- [ ] **Step 4: 통과 확인** — 같은 명령 → PASS. `dotnet build Oranda.sln -c Debug` 경고 없음.
- [ ] **Step 5: 통합 시험 작성** — `BulkExecutionTests`(`OracleFreeShared`, 기존 `DataTransferTests` 와 같은 방식으로 SYSTEM 표를 태그 이름으로 만들고 지운다):
  - `Array_insert_round_trips_all_supported_types`: 설계 8.3 1차 지원 타입 열을 모두 가진 원본 표 3행 → `StreamRowsAsync` → `ExecuteArrayAsync`(INSERT) → `MINUS` 양방향 0건.
  - `Array_insert_round_trips_large_lobs_byte_identical` (Review Focus 1): 100,000자 CLOB 과 200,000바이트 BLOB → `DBMS_LOB.COMPARE = 0`.
  - `Array_merge_updates_existing_and_inserts_new`: `MERGE … USING (SELECT :C1 … FROM DUAL)` 에 100,000자 CLOB 열을 넣어도 기존 행은 바뀌고 새 행은 들어간다(설계 전체가 이 경로에 기대므로 가장 먼저 확인한다).
  - `Stream_rows_rejects_non_query`: `DELETE` 문장 → `BulkDeniedException`.
  - `Readonly_source_profile_denies_writes`: `profile with { ReadOnly = true }` 세션에 `ExecuteArrayAsync(INSERT)` → `Status == Denied`.
- [ ] **Step 6: 통합 시험 실행(win144 먼저)** — `dotnet test tests/Oranda.Oracle.IntegrationTests --filter "FullyQualifiedName~BulkExecutionTests"` → PASS. 이 PC 에서는 돌리지 않는다. 레인 D 가 T8 을 시작하기 전에 이 결과를 확인한다(바인드 배열 + 큰 LOB + MERGE 가 안 되면 T8·T9 설계를 다시 정한다).
- [ ] **Step 7: 커밋** — `feat(exec): add bulk execution path with one policy check per statement`

### Task 7: 작업 파일 저장소·실행 기록

**Files:**
- Modify: `src/Oranda.Core/Storage/IAppDataPaths.cs` (`CopyJobsDirectory`, `CopyRunsDirectory`)
- Modify: `src/Oranda.Platform/Storage/AppDataPaths.cs` (`DataDirectory/copy-jobs`, `LogsDirectory/copy-runs`)
- Modify: `tests/Oranda.Platform.Tests/Storage/WorkspaceStoreTests.cs`, `tests/Oranda.Platform.Tests/Credentials/CredentialStoreTests.cs`, `tests/Oranda.App.Tests/Features/SqlEditor/SqlEditorTests.cs` (가짜 `IAppDataPaths` 에 두 속성)
- Create: `src/Oranda.Platform/Storage/Copy/CopyJobStore.cs`, `src/Oranda.Platform/Storage/Copy/CopyRunLog.cs`
- Test: `tests/Oranda.Platform.Tests/Storage/Copy/CopyJobStoreTests.cs`, `CopyRunLogTests.cs`

**Interfaces:**
- Consumes: `ICopyJobStore`, `ICopyRunLog`, `CopyJobJsonContext`(T1), `JsonDocumentFile`(`LoadAsync`·`WriteAsync`, 원자적 쓰기·손상 백업·미래 버전 읽기 전용)
- Produces: `CopyJobStore : ICopyJobStore`, `CopyRunLog : ICopyRunLog` (둘 다 `[OrandaService]`, `internal sealed`)

동작 규칙:
- 파일 이름은 작업 이름에서 파일 이름에 쓸 수 없는 글자를 `_` 로 바꾼 것이다. 겹치면 ` (2)`, ` (3)` 을 붙인다. 작업은 `Id` 로 찾는다.
- `ImportAsync` 는 고른 파일을 읽어 `Id` 가 겹치면 새 `Id` 를, 이름이 겹치면 ` (2)` 를 붙여 `copy-jobs/` 에 저장한다. `MaskSalt` 는 그대로 둔다(동료와 조인 유지, 설계 8.5).
- `schemaVersion` 이 `CopyJobSchema.Current` 보다 크면 목록에 `IsReadOnly = true` 와 알림 문구로 보이고, 저장·이름 바꾸기를 거부한다. 작으면 올려서 읽는다.
- 실행 기록은 `copy-runs/{yyyyMMdd-HHmmss}-{jobId}.jsonl` 이다. 첫 줄은 머리글, 그 뒤 한 줄에 사건 하나를 쓰고, 줄마다 `Flush(true)` 한다. `CompleteAsync` 가 마지막 줄 `{"type":"complete",…}` 을 쓴다. 이 줄이 없는 기록이 「끝나지 않은 실행」이다.
- `RecordFailedRowAsync` 는 키 열 값과 오류 메시지만 쓴다(설계 9장).

- [ ] **Step 1: 실패하는 시험 작성** (임시 폴더를 데이터 폴더로 쓰는 `AppDataPaths(string)`)
  - `Save_list_rename_duplicate_delete_round_trip`
  - `File_name_comes_from_job_name_and_collisions_get_number`: 이름 `"월간/테스트"` → `월간_테스트.json`, 같은 이름 두 번째 → `월간_테스트 (2).json`.
  - `Import_assigns_new_id_on_collision_and_keeps_salt`
  - `Future_schema_version_is_read_only`: `schemaVersion: 99` 파일 → `IsReadOnly`, `SaveAsync` 가 `InvalidOperationException`.
  - `Saved_file_never_contains_password`: 저장한 파일 글자에 `password` 가 없다.
  - `Unfinished_run_lists_disabled_objects` (Review Focus 3): `BeginAsync` → `RecordDisabledAsync([트리거 1, FK 1])` → `CompleteAsync` 없이 버림 → 새 `CopyRunLog` 의 `FindUnfinishedAsync(jobId)` 가 두 객체를 돌려준다. `MarkRecoveredAsync` 뒤에는 빈 목록이다.
  - `Completed_run_is_not_unfinished`
  - `Failed_row_record_keeps_only_key_and_error`: 기록 파일에 키 값과 오류 글자만 있고 다른 열 값이 없다.
- [ ] **Step 2: 실패 확인** — `dotnet test tests/Oranda.Platform.Tests --filter "FullyQualifiedName~Storage.Copy"` → FAIL.
- [ ] **Step 3: 구현** — `FormatterProfileStore` 의 잠금(`SemaphoreSlim`)·내보내기·가져오기 방식을 따른다.
- [ ] **Step 4: 통과 확인** — 같은 명령 → PASS. `dotnet test tests/Oranda.Architecture.Tests` → PASS.
- [ ] **Step 5: 커밋** — `feat(copy): store copy jobs and append-only run logs`

### Task 8: 사전 점검 서비스

**Files:**
- Create: `src/Oranda.Oracle/Transfer/Copy/CopyPrecheckService.cs`, `src/Oranda.Oracle/Transfer/Copy/CopyCatalogQueries.cs`
- Create: `tests/Oranda.Oracle.IntegrationTests/Transfer/Copy/CopyDbScope.cs` (시험 사용자 셋을 만들고 지우는 도우미)
- Test: `tests/Oranda.Oracle.Tests/Transfer/Copy/PrecheckRulesTests.cs`, `tests/Oranda.Oracle.IntegrationTests/Transfer/Copy/CopyPrecheckTests.cs`

**Interfaces:**
- Consumes: T1 계약, `CopySqlBuilder`(T3), `CopyOrder`·`SequenceGuess`(T4), `CopyTargetGuard`·`MaskFunctions.AllowedKinds`(T5), `IBulkExecutionService`·`ExecutionOptions`(T6), `IMetadataProvider.GetTableAsync`, `IExecutionPolicy`
- Produces: `CopyPrecheckService : ICopyPrecheckService` (`[OrandaService]`). 순수 판정 함수는 `internal static class PrecheckRules` 로 따로 두어 단위 시험한다: `PrecheckRules.CompareColumns(TableInfo source, TableInfo target, TableRule rule) → IReadOnlyList<CheckItem>`, `PrecheckRules.CheckMasks(TableRule rule, TableInfo source, IReadOnlyList<string> keyColumns) → IReadOnlyList<CheckItem>`.
- `CopyDbScope`: `CreateAsync(OracleFreeFixture db, string tag) → CopyDbScope` 가 사용자 `CP_{tag}_SRC`(원본 소유자), `CP_{tag}_DST`(대상 소유자), `CP_{tag}_W`(대상 쓰기 계정, 소유자 아님)를 만든다. `GrantWriterAsync(string table)` 은 `CP_{tag}_W` 에 SELECT·INSERT·UPDATE·DELETE 만 준다. `OpenSourceAsync()`·`OpenTargetAsync(bool asWriter, ConnectionEnvironment env)` 는 `OracleSessionManager` 로 `SessionPurpose.Dedicated` 세션을 연다. `DisposeAsync` 는 세 사용자를 `DROP USER … CASCADE` 한다.

점검 코드(`CheckItem.Code`)와 수준은 설계 7장 표를 그대로 따른다. 코드 이름: `TargetProduction`, `SourceNoSelect`, `TargetNoWrite`, `TargetMissing`, `ColumnNotInTarget`, `TargetRequiredColumnMissing`, `NarrowerType`, `UnsupportedType`, `IdentityAlways`, `FkCycle`, `ChildRowsMayBlock`, `TargetTriggers`, `TargetOnlyConstraint`, `SequenceGap`, `ReadConsistency`, `MaskedColumns`, `MaskOnKey`, `NoPrimaryKey`, `WrongLengthDates`, `ParentExcluded`. 권한은 `ALL_TAB_PRIVS`(대상 계정이 소유자가 아닐 때)와 `USER_SYS_PRIVS`(`ALTER ANY TRIGGER`)로 확인한다. 정책 미리 보기는 대상 세션 프로필로 `IExecutionPolicy.EvaluateAsync` 를 불러 `PreviewStatement.Policy` 에 넣는다.

- [ ] **Step 1: 실패하는 단위 시험 작성** (`PrecheckRulesTests`)
  - `Source_column_missing_in_target_is_confirm_and_skipped`
  - `Target_not_null_without_default_missing_in_source_is_error`
  - `Narrower_target_length_and_precision_is_confirm`
  - `Unsupported_type_is_error_with_skip_choice`: `XMLTYPE` 열 → `Error`, `Choices` 에 `SkipColumn`.
  - `Identity_always_is_error_and_virtual_is_skipped_silently`
  - `Mask_on_primary_key_or_parent_link_is_error` (설계 8.5)
- [ ] **Step 2: 실패 확인** — `dotnet test tests/Oranda.Oracle.Tests --filter "FullyQualifiedName~PrecheckRulesTests"` → FAIL.
- [ ] **Step 3: 구현** — 예상 건수는 `SourceCount` 를 `StreamRowsAsync` 로 실행하고, `ct` 로 취소할 수 있게 한다. `UNDO_RETENTION` 은 `V$PARAMETER` 를 읽고, 권한이 없으면 「알 수 없음」 통과로 둔다. 문자열 기간 열은 `SELECT COUNT(*) … WHERE <범위> AND LENGTH(열) <> :len` 으로 센다.
- [ ] **Step 4: 통과 확인** — 같은 명령 → PASS.
- [ ] **Step 5: 통합 시험 작성** — `CopyPrecheckTests`:
  - `Writer_without_owner_rights_passes_basic_checks_and_hides_disable_choices`: 쓰기 계정 대상 → 오류 없음, `CanDisableTriggers == false`, `DisablableForeignKeys` 비어 있음(설계 12장).
  - `Missing_target_table_and_missing_privilege_are_errors`
  - `Production_target_is_error`
  - `Child_rows_outside_job_warn_ora_02292`: 작업 밖 자식 표가 「비우고 넣기」 표를 참조하면 `ChildRowsMayBlock` 이고 선택지에 `SwitchToMerge`.
  - `Precheck_counts_wrong_length_string_dates` (Review Focus 4): `VARCHAR2(8)` 열에 `"2026101"` 1행 → `WrongLengthDates`, 메시지에 `1`.
  - `Preview_lists_target_deletes_with_policy_decision`: 「비우고 넣기」 DELETE 의 `Policy == Confirm`.
- [ ] **Step 6: 통합 시험 실행(CI·win144)** — `dotnet test tests/Oranda.Oracle.IntegrationTests --filter "FullyQualifiedName~Transfer.Copy.CopyPrecheckTests"` → PASS.
- [ ] **Step 7: 커밋** — `feat(copy): precheck privileges, columns, triggers, FKs and sequences`

### Task 9: 복사 실행기

**Files:**
- Create: `src/Oranda.Oracle/Transfer/Copy/CopyRunner.cs`, `src/Oranda.Oracle/Transfer/Copy/SequenceAdvance.cs`, `src/Oranda.Oracle/Transfer/Copy/FailedRowKeys.cs`
- Test: `tests/Oranda.Oracle.Tests/Transfer/Copy/SequenceAdvanceTests.cs`, `FailedRowKeysTests.cs`, `tests/Oranda.Oracle.IntegrationTests/Transfer/Copy/CopyRunnerTests.cs`

**Interfaces:**
- Consumes: T1 계약, `CopySqlBuilder`(T3), `MaskFunctions`·`CopyTargetGuard`(T5), `IBulkExecutionService`·`IExecutionService`(T6), `ICopyRunLog`(T7), `PrecheckReport`(T8), `ImportValueConverter`(마스킹 고정값을 열 타입으로 바꿀 때)
- Produces: `CopyRunner : ICopyRunner` (`[OrandaService]`). `SequenceAdvance.Plan(long current, long maxValue, long increment) → long` (소비할 `NEXTVAL` 개수, 0 이상), `SequenceAdvance.ChunkSize = 10_000`(한 번에 `CONNECT BY LEVEL <= :n` 으로 소비하는 개수). `FailedRowKeys.ToText(object? value, string serverType) → string?`, `FailedRowKeys.FromText(string? text, string serverType) → object?`(T1 의 `FailedRow` 키 글자 형식). 다시 읽을 때 `FromText` 값을 `BulkBinds` 형식으로 바인드한다.

실행 순서(설계 8.2)와 정한 동작:
- 시작할 때 `CopyTargetGuard.EnsureAllowedTarget(sessions.Target.Profile)` 를 부르고, `sessions.Source.Profile.ReadOnly` 가 거짓이면 `InvalidOperationException` 을 낸다.
- `job.ConsistentRead` 면 원본 세션에서 `SET TRANSACTION READ ONLY` 를 먼저 실행하고 그 시각을 `ReadStartedAt` 으로 둔다. SCN 은 읽지도 쓰지도 않는다.
- 끌 트리거·FK 목록을 `ICopyRunRecord.RecordDisabledAsync` 로 먼저 기록한 뒤 `ALTER … DISABLE` 을 실행한다.
- 정리(자식 → 부모 DELETE) → 적재(부모 → 자식) → 시퀀스 보정 → `finally` 에서 다시 켜기 → 건수 비교. FK 는 `ENABLE VALIDATE` 가 실패하면 `ENABLE NOVALIDATE` 로 켜고 `Notices` 에 FK 이름과 부모 없는 행 수를 넣는다.
- PK 가 있는 표의 「같은 범위 지우고 넣기」·「전체」 적재는 `Merge` 문장, PK 가 없으면 `Insert` 문장을 쓴다. 「추가만」은 `Insert` 다.
- DELETE·ALTER 는 `IExecutionService.ExecuteAsync`(`Source = DataCopy`, `PreConfirmed = decisions.PreConfirmedSql`)로 실행해 이력에 남기고, 같은 문장을 `ICopyRunRecord.RecordStatementAsync` 로 실행 기록에도 남긴다. 배치 INSERT·MERGE 는 `SkipHistory = true` 로 실행하고 기록하지 않는다.
- 시퀀스 SELECT 권한이 없으면 보정하지 않고 「테스트 중 PK 충돌이 날 수 있음」을 `Notices` 에 넣는다. 모자란 수가 `SequenceGapConfirm` 이상이고 `decisions.ProceedSequenceGap` 이 거짓이면 그 시퀀스를 건너뛴다.
- 실패한 배치는 한 행씩 다시 실행해 실패 행을 찾는다. 실패 행이 `MaxFailedRows` 를 넘으면 그 표를 `Failed` 로 두고 다음 표로 간다. ORA-01555 는 그 표를 실패로 두고 전체를 멈추며, 기간을 줄이라는 안내를 `Notices` 에 넣는다.
- 일시 정지는 배치 사이의 `control.WaitIfPausedAsync` 에서 멈춘다. `ct` 취소는 현재 배치를 되돌리고 다시 켜기를 거친 뒤 `Stopped` 로 끝난다.
- **중지 뒤 정리에는 `ct` 를 쓰지 않는다.** 실행 엔진은 취소된 토큰을 받으면 문장을 보내지 않고 `Cancelled` 를 돌려준다. 그래서 배치 롤백, 모든 `ENABLE` 문장, `RecordDisabledAsync`·`CompleteAsync` 는 `ct` 대신 새 토큰(`new CancellationTokenSource(TimeSpan.FromMinutes(2)).Token`)으로 실행한다.
- 커밋: `PerTable` 은 표 적재 끝, `PerBatch` 는 배치마다, `AtEnd` 는 모두 끝난 뒤 한 번(실패하면 전체 롤백).

- [ ] **Step 1: 실패하는 단위 시험 작성**
  - `SequenceAdvanceTests`: `Plan(current: 101, max: 500, increment: 1) == 400`, `Plan(1000, 500, 1) == 0`(내리지 않음), `Plan(10, 100, 5) == 19`.
  - `FailedRowKeysTests.Key_text_round_trips`: `FromText(ToText(v, t), t)` 가 `NUMBER` 값 `12345678901234567890.5`(`OracleDecimal`), `DATE` `2026-03-05 14:30:15`, `TIMESTAMP(6)`, `RAW(16)` 에서 원래 값과 같다. 숫자 글자에 지수 표기와 쉼표가 없다.
- [ ] **Step 2: 실패 확인** — `dotnet test tests/Oranda.Oracle.Tests --filter "FullyQualifiedName~SequenceAdvanceTests|FullyQualifiedName~FailedRowKeysTests"` → FAIL.
- [ ] **Step 3: 구현** — 위 규칙대로 `CopyRunner` 와 `SequenceAdvance` 를 만든다.
- [ ] **Step 4: 통과 확인** — 같은 명령 → PASS.
- [ ] **Step 5: 통합 시험 작성** — `CopyRunnerTests`(`CopyDbScope` 사용, 설계 12장 목록):
  - `Copies_all_period_parent_and_excluded_rules`: 기간(DATE 열, TIMESTAMP 생성 시각 열, `YYYYMMDD` 문자열 열, 두 열 OR)·부모 키 2단계·제외 규칙 → 건수 비교가 모두 같다.
  - `Rerun_produces_same_result`
  - `Rerun_after_period_value_change_merges_without_dup` (Review Focus 5): 첫 복사 뒤 원본 행의 기간 열을 범위 안 다른 날로 바꾸고 다시 실행 → ORA-00001 없음, 대상 값이 바뀐 값.
  - `Copies_lob_columns`
  - `Triggers_are_reenabled_even_when_copy_fails`: 대상에만 있는 CHECK 로 실패 행을 만들고 `MaxFailedRows = 0` → 결과 `Failed`, `USER_TRIGGERS.STATUS = 'ENABLED'`.
  - `Triggers_are_reenabled_after_stop`: 적재 도중 `ct` 를 취소 → 결과 `Stopped`, `USER_TRIGGERS.STATUS = 'ENABLED'`, 실행 기록에 `complete` 줄이 있다.
  - `Disabled_objects_are_logged_before_disable` (Review Focus 3): 가짜 `ICopyRunLog` 에 쌓인 사건 순서가 `RecordDisabled` 다음에 `ALTER TRIGGER … DISABLE` 문장 기록이다.
  - `Target_only_check_constraint_creates_failed_rows_with_key_only`
  - `Sequence_is_advanced_by_nextval`: 대상 시퀀스 다음 값 ≥ 최대값 + 1.
  - `Readonly_target_profile_is_denied`: `ReadOnly` 대상 → 표 결과 `Failed`, 메시지에 정책 거부.
  - `Writer_account_copies_without_disable_choices`
  - `Retry_failed_rows_rereads_by_key`: 실패 원인 CHECK 를 지운 뒤 `RetryFailedRowsAsync` → 실패 행 0. NUMBER 키 표와 DATE 가 섞인 복합 키 표 둘 다 확인한다.
- [ ] **Step 6: 통합 시험 실행(CI·win144)** — `dotnet test tests/Oranda.Oracle.IntegrationTests --filter "FullyQualifiedName~Transfer.Copy.CopyRunnerTests"` → PASS.
- [ ] **Step 7: 커밋** — `feat(copy): run copy jobs with batch merge, trigger restore and sequence advance`

### Task 10: 테이블 다시 만들기 (개발 접속만)

**Files:**
- Create: `src/Oranda.Core/Transfer/Copy/CreateTableDdl.cs`
- Create: `src/Oranda.Oracle/Transfer/Copy/RecreateStep.cs`
- Modify: `src/Oranda.Oracle/Transfer/Copy/CopyPrecheckService.cs`, `CopyRunner.cs` (Recreate 분기)
- Test: `tests/Oranda.Core.Tests/Transfer/Copy/CreateTableDdlTests.cs` (이 파일만 레인 D 소유), `tests/Oranda.Oracle.IntegrationTests/Transfer/Copy/RecreateTests.cs`

**Interfaces:**
- Consumes: `TableInfo`, `CopyTargetGuard.CanRecreate`(T5), `IExecutionService`(T6), `CopyRunner`(T9)
- Produces: `CreateTableDdl.Build(TableInfo source, string targetOwner, IReadOnlySet<string> tablesInJob) → IReadOnlyList<string>` (CREATE TABLE, PK·UK·CHECK, 작업 안 표끼리의 FK, 제약에 속하지 않은 인덱스, `COMMENT ON`). 테이블스페이스·저장 속성·파티션·압축·트리거·권한은 만들지 않는다(설계 6.2.3). `RecreateStep.CaptureAsync(ISession target, string owner, string table, ct) → PreservedDdl(IReadOnlyList<string> Triggers, IReadOnlyList<string> Grants, IReadOnlyList<string> ReferencingForeignKeys)` 는 대상 계정의 `DBMS_METADATA.GET_DDL`·`GET_DEPENDENT_DDL` 로 받는다.

정한 동작:
- `TargetAction.Recreate` 는 `CopyTargetGuard.CanRecreate` 가 참일 때만 점검을 통과한다. 거짓이면 `RecreateNotAllowed` 오류다(점검 코드 목록에 이 Task 가 더한다). 대상 계정에 지우고 만들 권한이 없으면 `RecreateNoPrivilege` 오류다.
- DROP 문장은 `DROP TABLE "O"."T" CASCADE CONSTRAINTS` 이고 `PURGE` 가 없다. 사전 점검은 DROP 목록을 미리 보기에 `RetypeText` 와 함께 넣는다. 화면(T12)이 대상 접속 이름을 한 번 다시 입력받으면 그 DROP 텍스트들을 `PreConfirmedSql` 에 넣는다.
- 받아 둔 참조 FK 는 적재 뒤 되살리고, 실패하면 `ENABLE NOVALIDATE` 로 켠다. 결과 `Notices` 에 휴지통에서 되돌리는 방법(`FLASHBACK TABLE … TO BEFORE DROP`)을 넣는다.
- 새로 만든 표는 DELETE·MERGE 없이 `Insert` 로 적재한다.

- [ ] **Step 1: 실패하는 단위 시험 작성** — `CreateTableDdlTests`:
  - `Builds_columns_with_types_defaults_and_not_null`
  - `Builds_pk_uk_check_and_in_job_fk_only`
  - `Skips_constraint_backed_indexes_and_adds_comments`
  - `Never_emits_tablespace_partition_or_compress`
- [ ] **Step 2: 실패 확인** — `dotnet test tests/Oranda.Core.Tests --filter "FullyQualifiedName~CreateTableDdlTests"` → FAIL.
- [ ] **Step 3: 구현** — 위 규칙대로 만든다.
- [ ] **Step 4: 통과 확인** — 같은 명령 → PASS.
- [ ] **Step 5: 통합 시험 작성** — `RecreateTests`:
  - `Recreate_choice_only_for_development_target`: 테스트 라벨 대상에서 `Recreate` 규칙 → `RecreateNotAllowed`.
  - `Recreates_with_source_structure_and_restores_triggers_grants_and_fks`
  - `Dropped_table_is_in_recycle_bin`: `USER_RECYCLEBIN` 에 원래 이름 행이 있다.
  - `Test_target_drop_is_denied_by_policy`: 테스트 라벨 세션에서 `PreConfirmed` 없이 DROP → 확인 요청(`Confirmations.Count == 1`), 거절하면 `Cancelled`.
- [ ] **Step 6: 통합 시험 실행(CI·win144)** — `dotnet test tests/Oranda.Oracle.IntegrationTests --filter "FullyQualifiedName~Transfer.Copy.RecreateTests"` → PASS.
- [ ] **Step 7: 커밋** — `feat(copy): recreate target tables from source structure on development targets`

### Task 11: 창·규칙 편집 단계·내 작업 메뉴·진입점

**Files:**
- Create: `src/Oranda.App/Features/DataCopy/DataCopyCommands.cs`, `DataCopyWindow.axaml(.cs)`, `DataCopyViewModel.cs`, `RuleEditorViewModel.cs`, `TableRuleViewModel.cs`, `JobListViewModel.cs`, `DataCopyServices.cs`
- Modify: `src/Oranda.App/Features/DedicatedSession/DedicatedSessionCommands.cs` (`OpenSessionAsync` 의 비밀번호 얻기를 `OpenDedicatedAsync(ServiceContainer container, ConnectionProfile profile, CancellationToken ct) → Task<ISession?>` 로 분리해 공개)
- Modify: `docs/design/commands.md` (도구·스키마 표에 두 줄)
- Test: `tests/Oranda.App.Tests/Features/DataCopy/RuleEditorTests.cs`, `JobListTests.cs`

**Interfaces:**
- Consumes: T1 모델, `PeriodCalculator`·`DateStringFormat`(T2), `PeriodColumnRanker`(T4), `ConnectionRelinker`·`CopyTargetGuard`·`MaskFunctions`(T5), `ICopyJobStore`(T1 계약, 시험은 가짜 구현), `IConnectionStore`, `IMetadataProvider`
- Produces: 명령 `oranda.tools.dataCopy`(`CommandCategory.Tools`, `Title = "데이터 복사…"`, `Placements = ["menu:tools/dataCopy@41"]`), `oranda.schema.addToDataCopy`(`When = "schemaFocus"`, `Enablement = "schemaNodeSelected"`, `Placements = ["context:schema/dataCopy@46"]`). `DataCopyViewModel` 은 `Step`(`RuleEdit`, `Precheck`, `Run`)과 `CurrentJob` 을 가진다. 창은 모달이 아니다(`Show`, `ShowDialog` 아님).

- [ ] **Step 1: 실패하는 시험 작성**
  - `Target_list_disables_production_with_reason`: 대상 접속 목록의 운영 항목은 `IsEnabled == false`, 툴팁 글자가 있다.
  - `Period_rule_lists_created_at_columns_after_business_dates`: 선택 목록 순서와 「생성 시각」 표시.
  - `Period_rule_defaults_string_format_from_length`
  - `Second_period_column_is_optional_and_max_two`
  - `Recreate_action_visible_only_for_development_target`
  - `Consistent_read_toggle_defaults_on`: 새 작업의 「모든 테이블을 같은 시점 기준으로 읽기」가 켜져 있고, 끄면 `CopyJob.ConsistentRead == false` 로 저장된다.
  - `Calculated_dates_are_always_shown`: 기간을 「최근 1달」로 바꾸면 화면 문자열에 `2026-10-07 ~ 2026-11-07` (가짜 `TimeProvider` 2026-11-06).
  - `Condition_cell_shows_column_and_format`: `ORDER_DT · YYYYMMDD 문자열`.
  - `Job_menu_new_duplicate_rename_export_import_delete`: 가짜 저장소 호출 순서와 인자.
  - `Add_to_data_copy_from_schema_adds_table_rule`
  - `Commands_are_listed_in_commands_md`: `docs/design/commands.md` 에 두 명령 ID 가 있다(`SqlEditorTests` 의 파일 찾기 방식).
- [ ] **Step 2: 실패 확인** — `dotnet test tests/Oranda.App.Tests --filter "FullyQualifiedName~Features.DataCopy"` → FAIL.
- [ ] **Step 3: 구현** — 화면 밀도·아이콘은 `ImportDialogWindow`·DDL 추출 창과 맞춘다. 마스킹 규칙 칸에 「같은 값이 여러 표의 조인 키면 해시를 쓰세요」 안내를 둔다. 「비우고 넣기」 옆에 「원본에서 지워진 행은 MERGE 가 남깁니다」 안내를 둔다(설계 6.2).
- [ ] **Step 4: 통과 확인** — 같은 명령 → PASS.
- [ ] **Step 5: 커밋** — `feat(copy): add data copy window with rule editor and job menu`

### Task 12: 사전 점검·실행·검증 단계 화면

**Files:**
- Create: `src/Oranda.App/Features/DataCopy/PrecheckViewModel.cs`, `RunViewModel.cs`, `DataCopySessions.cs`, `FailedRowsCsv.cs`
- Modify: `src/Oranda.App/Features/DataCopy/DataCopyWindow.axaml(.cs)`, `DataCopyViewModel.cs`
- Test: `tests/Oranda.App.Tests/Features/DataCopy/PrecheckStepTests.cs`, `RunStepTests.cs`

**Interfaces:**
- Consumes: `ICopyPrecheckService`·`ICopyRunner`·`ICopyRunLog`(T1 계약, 시험은 가짜), `DedicatedSessionCommands.OpenDedicatedAsync`(T11), `ConnectionRelinker`·`CopyTargetGuard`(T5)
- Produces: `DataCopySessions.OpenAsync(ServiceContainer c, ConnectionProfile source, ConnectionProfile target, ct) → Task<CopySessions?>` (원본은 `source with { ReadOnly = true }`, 둘 다 `SessionPurpose.Dedicated`, 창을 닫으면 둘 다 닫는다). `FailedRowsCsv.WriteAsync(Stream, IReadOnlyList<FailedRow>, ct)` 는 사용자가 「CSV로 저장」을 눌렀을 때만 부른다.

정한 동작:
- 사전 점검의 `Confirm` 정책 문장은 한 목록으로 보여 주고, 「복사 실행」을 누르면 그 텍스트들을 `PreConfirmedSql` 로 넘긴다. `RetypeText` 가 있는 DROP 문장은 대상 접속 이름을 한 번 다시 입력해야 집합에 들어간다.
- 오류가 하나라도 있으면 「복사 실행」이 꺼진다. 점검 중 예상 건수 세기는 「취소」로 멈출 수 있다.
- SQL 미리 보기: 「편집기로 열기」(새 SQL 탭), 「SQL 스크립트로 저장」.
- 작업을 열 때 `ICopyRunLog.FindUnfinishedAsync` 결과가 있으면 「지난 실행이 끝나지 않았습니다. 대상 트리거·FK 상태를 확인하세요」와 목록, 「다시 켜기」 버튼을 보인다. 「다시 켜기」는 대상 세션에서 `ENABLE` 문장을 실행하고 `MarkRecoveredAsync` 를 부른다.
- 실행 단계: 표마다 진행률, 일시 정지·계속·중지, 끝나면 원본·대상 건수 비교와 실패 행 목록(키·오류), 「실패 행만 다시 넣기」, 「CSV로 저장」, 화면에 읽기 시작 시각.

- [ ] **Step 1: 실패하는 시험 작성**
  - `Opening_job_whose_target_is_production_clears_target` (Review Focus 2): 가져온 작업의 대상 이름이 운영 접속과 같으면 대상이 비고 알림 문구가 보인다.
  - `Run_is_disabled_while_precheck_has_errors`
  - `Confirm_statements_are_preconfirmed_on_run`: 가짜 실행기가 받은 `PreConfirmedSql` 이 미리 보기의 Confirm 문장 집합과 같다.
  - `Drop_statements_need_connection_name_retype`: 다시 입력 전에는 DROP 텍스트가 집합에 없고 「복사 실행」이 꺼진다.
  - `Disable_trigger_choice_hidden_without_privilege`
  - `Unfinished_run_offers_reenable`
  - `Source_session_is_opened_read_only`: 가짜 세션 관리자가 받은 원본 프로필의 `ReadOnly == true`.
  - `Stop_cancels_runner_token_and_shows_stopped`
  - `Csv_is_written_only_on_explicit_save`: 실행이 끝나도 파일 쓰기 호출이 없고, 「CSV로 저장」 뒤 한 번 있다.
- [ ] **Step 2: 실패 확인** — `dotnet test tests/Oranda.App.Tests --filter "FullyQualifiedName~Features.DataCopy"` → FAIL.
- [ ] **Step 3: 구현** — 위 동작대로 만든다.
- [ ] **Step 4: 통과 확인** — 같은 명령 → PASS.
- [ ] **Step 5: 커밋** — `feat(copy): add precheck and run steps with confirmations and recovery`

### Task 13: 문서 마감

**Files:**
- Modify: `docs/PRD.md` (내보내기 표에 `EXP-04` 한 줄, 우선순위 P1)
- Modify: `docs/design/data-model.md` (`copy-jobs/*.json` 형식과 `logs/copy-runs/*.jsonl` 형식, `CopyJobSchema.Current = 1`)
- Modify: `docs/superpowers/specs/2026-11-06-cross-connection-data-copy-design.md` (상태를 「구현됨」으로, 13장 결정을 이 계획의 결정으로 적는다: 대량 실행 API, `PreConfirmed`, 작업 파일 필드 이름, 기간 경계, 시퀀스 보정 방법)

- [ ] **Step 1: 문서를 고친다.** 예시 접속 이름은 `ORCL`, `db.example.com` 만 쓴다.
- [ ] **Step 2: 확인** — `git diff --stat` 에 위 세 파일만 있고, `git diff | grep -iE "[0-9]{1,3}\.[0-9]{1,3}\.[0-9]{1,3}\.[0-9]{1,3}"` 가 아무것도 내지 않는다.
- [ ] **Step 3: 커밋** — `docs(copy): record EXP-04 and copy job file formats`

### Task 14: 전체 검증·실제 앱 확인 (조정자)

- [ ] **Step 1: 로컬 시험** — `dotnet test Oranda.sln -c Debug --filter "Category!=Integration"` → 실패 0.
- [ ] **Step 2: 통합 시험(win144 또는 CI)** — `dotnet test tests/Oranda.Oracle.IntegrationTests --filter "FullyQualifiedName~Transfer.Copy|FullyQualifiedName~BulkExecutionTests"` → 실패 0. 알려진 불안정 시험(메모리 「CI flaky tests」)은 다시 돌려 확인한다.
- [ ] **Step 3: 실제 서비스 연결** — T12 의 창이 가짜가 아닌 `ICopyPrecheckService`·`ICopyRunner`·`ICopyJobStore`·`ICopyRunLog` 를 `IServiceProvider` 에서 꺼내는지 확인한다(`DataCopyServices`). 빠진 연결이 있으면 레인 F 에 고침을 맡긴다.
- [ ] **Step 4: 실제 앱(Mac)** — 시험 DB 컨테이너 하나에 원본·대상 사용자를 만들고, 「도구 › 데이터 복사…」로 기간 규칙 한 표, 부모 키 규칙 한 표를 복사한다. 운영 라벨 접속이 대상 목록에서 꺼져 있는지 본다.
- [ ] **Step 5: 저장소 확인** — `git log -p dev-jji-mac..HEAD | grep -iE "password|[0-9]{1,3}\.[0-9]{1,3}\.[0-9]{1,3}\.[0-9]{1,3}"` 결과에 비밀번호·실제 주소가 없다.
