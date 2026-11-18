# AI 파티션 추천 설계

- 날짜: 2026-11-18
- 시안: `2026-11-18-partition-ai-advisor-mockups.html`(①~⑤, 「함께 고칠 것」). 사용자 지시 「시안대로 진행하자」(2026-11-18)
- 앞선 설계: `2026-11-17-table-editor-design.md` 18.4(파티션 편집)·TE-41(일반 → 파티션 바꾸기)·재구성 스크립트(4fb426fd)

## 1. 목표

지금 「쉬운 설정」은 DATE·TIMESTAMP 열 + RANGE/INTERVAL 하나만 만든다. 문자열 날짜 열, 숫자 범위, 코드 열 LIST, HASH, 하위 파티션은 사용자가 직접 정해야 한다.

이 기능은 앱이 근거(통계·분포·PK·인덱스·실제 조회 조건)를 모아 AI 에게 보내고, AI 후보를 받아 **앱이 결정론으로 다시 확인한 뒤** 기존 파티션 편집기에 채운다. 실행은 지금 경로 그대로(DDL 미리보기 → 실행, 또는 재구성 스크립트).

범위 밖: 이미 파티션된 표의 재설계(바꾸기 = 일반 → 파티션 표만), 실행 자동화, DBMS_SCHEDULER 작업 만들기.

## 2. 결정 (시안 「정해 주실 것」 → 시안 기본값으로 확정)

| id | 결정 |
|---|---|
| PA-1 | 분포 조회는 확인 창에서 기본 켬. 표본 = 약 5만 행 목표(행 수 ≤ 20만 = 표본 없이 전체, 그 밖 = `SAMPLE(p)`, p = 50000/행 수×100 을 0.01~10 사이로). 창에 읽을 행 수를 미리 적음 |
| PA-2 | 조회 조건 수집(V$SQL_PLAN) 기본 켬. 권한 없음(ORA-00942·ORA-01031, Tibero 같은 오류) = 「건너뜀(권한 없음)」으로 적고 계속 |
| PA-3 | 실제 값(코드 값·날짜 경계 값) 보내기 = 기본 끔. 가림 대상 접속(운영 표시 또는 가림 설정)이면 로컬 AI 가 아닌 한 켤 수 없음. 끄면 값은 `V1`, `V2`… 자리표시자와 건수만 보내고, 받은 뒤 앱이 실제 값으로 되돌림 |
| PA-4 | Tibero 도 같은 화면. 방언이 못 하는 후보(하위 파티션 바꾸기 등)는 앱 확인에서 ✖ 로 거름. 방언 능력은 실서버 실측으로 정함 |
| PA-5 | 문자열·숫자 범위 키(INTERVAL 불가)의 다음 달 추가 = 「월 추가 스크립트 저장…」 까지만 |
| PA-6 | AI 경로 = AI 튜닝과 같은 라우터(`IAiDriver`)·활성 프로필·받는 곳 표시. 구조 결과 = `AiRequest.OutputSchema`(PartitionAdvice 스키마). 구조 결과가 없는 경로(기타 CLI 등)는 응답 글의 첫 JSON 블록을 읽고, 못 읽으면 원문만 보이고 「편집기에 채우기」를 끔 |
| PA-7 | 근거는 앱이 미리 모아 프롬프트에 넣음. AI 도구 호출(AgentSession) 없음 |
| PA-8 | 「✨ AI 추천…」 단추는 편집 중인 일반(비파티션) 일반 힙 테이블의 파티션 페이지에서 늘 보임. AI 설정이 없으면 누르면 설정 안내 |
| PA-9 | 「분포 보기」 = AI 없이 근거 패널만 여는 단추(시안 「함께 고칠 것」 2) |
| PA-10 | 쉬운 설정 열 목록에 날짜형 문자열·숫자 열 추가(표본이 YYYYMMDD·YYYYMM·YYYY-MM-DD 형식 95% 이상). 이 열은 INTERVAL 없이 「시작 앞 한 줄 + 앞으로 12개월 + MAXVALUE」 |
| PA-11 | 하위 파티션 = 바꾸기(일반 → 파티션) 때만 템플릿으로 만듦(LIST 묶음 또는 HASH 개수). 이미 파티션된 표의 하위 파티션은 지금처럼 보기만 |
| PA-12 | 바꾸기 때 LOCAL 로 만들 인덱스 목록을 모델에 둠. 온라인 바꾸기 = `UPDATE INDEXES (ix LOCAL, …)`, 재구성 스크립트 = 새 표에 `CREATE INDEX … LOCAL` |

## 3. 화면 흐름 (시안 번호)

1. ① 진입: 파티션 페이지 「쉬운 설정」 줄 끝에 `✨ AI 추천…`·`분포 보기`
2. ② 확인 창(모달): 받는 곳, 목적(조회 성능 + 오래된 데이터 정리 / 조회 성능 / 정리), 보낼 정보 체크 5개(열·키·통계·조회 조건·분포)와 실제 값 보내기, 건너뛸 항목과 이유, 「보낼 내용 보기」(실제 프롬프트 펼침·복사). 단추 = 취소 / 모아서 보내기
3. 진행: 같은 창이 단계 표시(열·키 → 통계 → 조회 조건 → 분포 → AI 응답 대기)와 취소
4. ③ 결과 창(비모달, 탭 하나에 하나): 왼쪽 근거 패널(앱이 모은 숫자), 오른쪽 후보 카드(AI 이유 + 앱 확인 ✔/⚠/✖), 「편집기에 채우기」, 「다시 받기」, 「AI 원문 보기」, 「보고서로 저장」(HTML), 「왜 그 열은 안 했나」(not_chosen)
5. ④ 채운 뒤: 파티션 페이지 위 띠 「AI 추천 N안을 채웠습니다 … 」 + `추천 다시 보기`·`되돌리기`(채우기 직전 모델로). 사용자가 고치면 띠 문구가 「AI N안에서 직접 고침」
6. ⑤ verdict = `no_partition` 이면 이유만 보이고 「후보 3개 받기」(같은 근거로 다시 요청, 후보를 꼭 내라고 덧붙임)

✖ 가 하나라도 있는 후보는 「편집기에 채우기」를 끈다. ⚠ 는 채운 뒤 띠에도 남는다.

## 4. Core 계약 (`Oranda.Core.Schema.TableEdit.Partitions.Advisor`)

이름·꼴은 레인 pa-core 첫 체크포인트에서 확정한다. 아래는 초안이며 더 나은 꼴이면 바꾸고 결정으로 남긴다.

```csharp
// 근거
public sealed record PartitionEvidence(
    string Owner, string Table, Dbms Dbms, string? ServerVersion,
    long? NumRows, long? Blocks, DateTime? LastAnalyzed,
    IReadOnlyList<EvidenceColumn> Columns,
    IReadOnlyList<EvidenceIndex> Indexes,          // 이름, 고유, 열 순서, PK 여부
    IReadOnlyList<EvidenceKey> Keys,               // PK·UK·FK 와 열
    IReadOnlyList<PredicateUse> Predicates,        // 열별 조건 사용
    IReadOnlyList<ColumnDistribution> Distributions,
    IReadOnlyList<EvidenceSkip> Skipped);          // 건너뛴 항목과 이유
public sealed record EvidenceColumn(string Name, string DataType, bool Nullable, long? NumDistinct, long? NumNulls, string? Histogram, DateLike? DateLike);
public enum DateLikeFormat { Yyyymmdd, Yyyymm, YyyyDashMmDashDd }
public sealed record DateLike(DateLikeFormat Format, double MatchRatio, int SampleSize);
public enum DistributionKind { Monthly, TopValues }
public sealed record ColumnDistribution(string Column, DistributionKind Kind, IReadOnlyList<DistributionBucket> Buckets, long SampledRows, double? SamplePercent);
public sealed record DistributionBucket(string Label, long Count);   // Monthly = 'YYYY-MM', TopValues = 실제 값(가림은 프롬프트에서)
public sealed record PredicateUse(string Column, long Executions, int Statements, double Share);
public sealed record EvidenceSkip(string Item, string Reason);

// 공급자(Oracle·Tibero 구현, 기본 = 지원 안 함)
public interface IPartitionEvidenceProvider
{
    Task<IReadOnlyList<PredicateUse>> GetPredicateUseAsync(ISession s, string owner, string table, CancellationToken ct);
    Task<DateLike?> DetectDateLikeAsync(ISession s, string owner, string table, string column, CancellationToken ct);
    Task<ColumnDistribution> SampleMonthlyAsync(ISession s, string owner, string table, string column, DateLikeFormat? format, double? samplePercent, CancellationToken ct);
    Task<ColumnDistribution> SampleTopValuesAsync(ISession s, string owner, string table, string column, int top, double? samplePercent, CancellationToken ct);
}

// 모으기: ITuningService(표·열 통계, 인덱스 열) + IMetadataProvider(열·제약) + IPartitionEvidenceProvider
public sealed record EvidenceOptions(bool Predicates, bool Distributions, double? SamplePercent);
public sealed class PartitionEvidenceCollector { Task<PartitionEvidence> CollectAsync(ISession, DbObjectRef, EvidenceOptions, IProgress<EvidenceStep>?, CancellationToken); }

// AI 결과(JSON 스키마 const SchemaJson 포함)
public sealed record PartitionAdvice(string Verdict /* partition | no_partition */, string Summary, IReadOnlyList<PartitionCandidate> Candidates, IReadOnlyList<NotChosen> NotChosen);
public sealed record PartitionCandidate(int Rank, string Title, string Method /* range|list|hash */, IReadOnlyList<string> KeyColumns,
    string? Interval /* month|day|year|none|number:<n> */, PartitionLayout Layout, SubpartitionAdvice? Subpartition,
    IReadOnlyList<string> LocalIndexes, IReadOnlyList<string> Reasons, IReadOnlyList<string> Cautions);
public sealed record PartitionLayout(string Kind /* monthly|yearly|daily|values|hash|number_ranges */, string? Start, int? PeriodsAhead,
    IReadOnlyList<ListGroup>? Groups, int? HashCount, IReadOnlyList<string>? Bounds);
public sealed record ListGroup(string Name, IReadOnlyList<string> Values, bool IsDefault);
public sealed record SubpartitionAdvice(string Method /* list|hash */, IReadOnlyList<string> KeyColumns, IReadOnlyList<ListGroup>? Groups, int? HashCount);
public sealed record NotChosen(string Column, string Reason);

// 프롬프트·가림
public sealed record AdvicePromptOptions(string Goal, bool SendValues, string Language /* ko|en */);
public static class PartitionAdvicePrompt { (string Prompt, EvidenceValueVault Vault) Build(PartitionEvidence e, AdvicePromptOptions o); }
public sealed class EvidenceValueVault { string Mask(string column, string value); string? Unmask(string column, string token); }
public static class PartitionAdviceParser { PartitionAdvice? Parse(JsonElement? result, string? text); }   // 구조 결과 → 없으면 글의 첫 JSON 블록

// 앱 확인·채우기
public enum AdviceCheckLevel { Ok, Warn, Stop }
public sealed record AdviceCheck(AdviceCheckLevel Level, string Code, string TextKo, string TextEn);
public static class PartitionAdviceChecker { IReadOnlyList<AdviceCheck> Check(PartitionCandidate c, PartitionEvidence e, ITableEditDialect d, ServerInfo? server); }
public sealed record AdviceDraft(PartitionTableDraft Draft, IReadOnlyList<string> LocalIndexes, IReadOnlyList<string> Notes);
public static class PartitionAdviceApplier { AdviceDraft Apply(PartitionCandidate c, PartitionEvidence e, EvidenceValueVault vault, DateTime today); }
```

### 4.1 앱 확인 규칙 (최소 목록)

| code | 수준 | 조건 |
|---|---|---|
| key-missing | ✖ | 키 열이 표에 없음·지운 열·가상 열 |
| key-type | ✖ | 방식과 형식이 안 맞음(HASH 외에 LOB·LONG, INTERVAL 인데 DATE·TIMESTAMP·NUMBER 아님) |
| interval-string | ⚠ | 문자열 키 + 월 단위 → INTERVAL 없이 경계 줄 + MAXVALUE, 월 추가 스크립트 필요 |
| interval-null | ✖ | INTERVAL 후보인데 키 열 NULL 행이 있음(ORA-14300) |
| range-null | ⚠ | INTERVAL 없는 범위인데 NULL 행 있음 → MAXVALUE 파티션으로 감 |
| datelike-ratio | ✖/⚠ | 문자열 날짜 키인데 형식 맞는 비율 < 95% ✖, < 100% ⚠ |
| pk-global | ⚠ | PK·UK 에 키 열이 없음 → 그 인덱스는 GLOBAL, DROP PARTITION 때 UPDATE INDEXES |
| local-ok | ✔ | LOCAL 로 바꿀 인덱스의 선두 열 = 키(접두 LOCAL) |
| local-unique | ✖ | 고유 인덱스를 LOCAL 로 하는데 키 열을 포함하지 않음(ORA-14039) |
| hash-pow2 | ⚠ | HASH 개수가 2의 거듭제곱이 아님 |
| list-cover | ⚠ | LIST 묶음이 분포 상위 값을 다 덮지 않고 DEFAULT 도 없음 |
| bounds-order | ✖ | 경계가 정렬되지 않음·중복 |
| dialect | ✖ | 방언이 그 방식·하위 파티션·INTERVAL·온라인 바꾸기를 못 함(재구성 스크립트로 가능하면 ⚠) |
| small-table | ⚠ | 행 수 < 100만 또는 블록 < 1만 → 이득 작음 |
| stats-old | ⚠ | 통계 마지막 수집 30일 넘음(근거 숫자가 오래됨) |

### 4.2 채우기 규칙

- RANGE + DATE·TIMESTAMP + month/day/year: 첫 경계 = 분포의 가장 이른 달(없으면 오늘 달) 다음 경계 하나 + INTERVAL. 이름 = 쉬운 설정 이름 규칙
- RANGE + 날짜형 문자열·숫자: 분포 첫 달부터 오늘 + PeriodsAhead(기본 12)까지 달마다 한 줄 + `P_MAX`(MAXVALUE). 경계 글 = 형식대로(`'20260101'`, `202601`)
- RANGE + number_ranges / Bounds: 그대로 줄로
- LIST: 묶음마다 한 줄, IsDefault = `DEFAULT`. 값은 금고에서 실제 값으로 되돌림. 되돌릴 수 없는 자리표시자 = ✖(그 후보 채우기 끔)
- HASH: 개수만큼(이름 P1…Pn)
- 하위 파티션 = PA-11 템플릿
- LocalIndexes = PA-12 목록

## 5. 공급자 SQL

- 조회 조건: V$SQL_PLAN 에서 `object_owner·object_name` = 표 또는 그 표의 인덱스인 줄의 `access_predicates`·`filter_predicates` 를 읽어, 표 열 이름이 나오는 횟수를 V$SQL `executions` 로 가중. 열 이름 판정은 따옴표 이름과 별칭 접두(`"T"."COL"`·`T.COL`)를 다룸. 상위 200 문장(실행 수 순)만. 11g 변형 필요하면 `.11.sql`
- 날짜형 판정: `SELECT col FROM t SAMPLE(…) WHERE col IS NOT NULL AND ROWNUM <= 500`(작은 표는 SAMPLE 없이). 형식 판정은 C# 에서(정규식 + 실제 날짜 검증)
- 월별 건수: DATE·TIMESTAMP = `TRUNC(col,'MM')`, 문자열 = `SUBSTR(col,1,6)`(YYYYMMDD·YYYYMM)·`SUBSTR(col,1,7)`(YYYY-MM-DD), 숫자 = `TRUNC(col/100)` 등 형식별. 최근 36개월 + 나머지 합 한 줄
- 상위 값: `GROUP BY col ORDER BY COUNT(*) DESC` 상위 20 + 나머지 합. 고유값 수(통계) ≤ 200 인 VARCHAR2·CHAR·NUMBER 열만, 그리고 PK 단일 열 제외
- 열 고르기(모으기 몫): 분포 대상 = DATE·TIMESTAMP 전부 + 날짜형 문자열·숫자 + 저카디널리티 코드 열. 최대 8열(조건 사용이 많은 순)
- 시험: Oracle IT(26ai·11g 컨테이너, `ORT_PA_` 객체만), Tibero IT(ORANDA_IT_TIBERO_*, `ORT_PA_` 객체만)

## 6. 하위 파티션·LOCAL 인덱스·월 추가 스크립트

- `PartitionScheme` 에 바꾸기용 하위 템플릿(LIST 묶음 줄 / HASH 개수)을 더함. 원본에서 읽은 하위 정보(보기용)와 구분
- `PartitionScriptBuilder` 바꾸기 절: `SUBPARTITION BY LIST (c) SUBPARTITION TEMPLATE (SUBPARTITION … VALUES (…), …)` / `SUBPARTITION BY HASH (c) SUBPARTITIONS n`
- 온라인 바꾸기: `UPDATE INDEXES (ix1 LOCAL, ix2 GLOBAL)` 절. 재구성 스크립트: 새 표 인덱스를 LOCAL 로
- 월 추가 스크립트(PA-5): INTERVAL 없는 범위 + MAXVALUE 마지막 줄인 모델 → 다음 N 개월 `SPLIT PARTITION P_MAX AT (…) INTO (PARTITION P_YYYYMM, PARTITION P_MAX) UPDATE INDEXES`. 파티션 페이지 「월 추가 스크립트 저장…」(조건이 맞을 때만 보임)
- 하위 파티션 편집 화면: 바꾸기 중일 때 「하위 파티션」 칸에 방식(없음·LIST·HASH)·키 열·템플릿 줄 표(이름·값 목록) 또는 개수. 쉬운 고치기만(줄 추가·지우기·값 고치기)
- Tibero 의 복합 파티션·SUBPARTITION TEMPLATE·UPDATE INDEXES 절 지원은 실측해 방언 능력 값으로

## 7. 레인 나누기

| 레인 | 모델 | 소유 | 선행 |
|---|---|---|---|
| pa-core | opus | Core `Partitions/Advisor/*` 와 시험 | — (첫 항목 = 계약 체크포인트) |
| pa-script | opus | Core `Partitions/*`(Advisor 제외)·`TableEditScriptBuilder`·재구성 스크립트, App `TableEditSession.Partition*.cs`·쉬운 설정·하위 파티션 편집 UI | — |
| pa-db | sonnet | Oracle·Tibero 공급자 구현·카탈로그 SQL·IT | pa-core 계약 |
| pa-ui | opus | App `Features/TableEdit/Advisor/*`, ObjectDetail 배선, 확인·결과 창, 띠·되돌리기, 보고서 | pa-core 계약 (pa-script 의 하위 파티션 모델은 머지 뒤 이어 붙임) |

`TableEditPartitionsView.axaml` 은 pa-script·pa-ui 둘 다 고침 — 덧붙이기만, 뒤에 머지하는 레인이 rebase 로 합침.

## 8. 시험

- Core: 확인 규칙 표 4.1 의 code 마다 정상·경계 1개 이상, 채우기 4.2 방식별, 파서(구조 결과·글 속 JSON·깨진 JSON), 가림 왕복, 프롬프트에 가린 값이 실제 값으로 남지 않음
- 공급자: Oracle 26ai·11g, Tibero 실서버
- 문장: 하위 템플릿·LOCAL 인덱스·월 추가 스크립트를 실서버에서 실행(Oracle 26ai·11g 재구성, Tibero)
- App: Headless — 확인 창 체크 상태와 가림 잠금, 결과 카드 ✖ 면 채우기 끔, 채우기 → 띠 → 되돌리기, no_partition 화면. 가짜 `IAiDriver` 로
- 실앱 확인(사용자 몫): 실제 AI 프로필로 한 번 받아 보기
