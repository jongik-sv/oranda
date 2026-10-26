# DataGrip 코드 지원·AI 기능과 Oranda 비교 — 개선 방안

- 작성일: 2026-10-25
- 질문: 「DataGrip 의 기능, SQL 자동완성, AI 기능을 조사하고, idea.md 에 연결된 LLM 연구서와 함께 비교해 Oranda 의 더 나은 개선 방안을 찾는다」
- 함께 본 문서: [LLM 연동 연구서](llm-editor-integration-2026-10-24.md)(이하 「연구서」), [전체 기능 비교](feature-gap-2026-10-24.md)
- 범위: 저장소 코드는 읽기만 했다. 설계안이고 구현은 하지 않았다.
- 표기: 출처나 코드로 확인하지 못한 내용은 「확인 못 함」으로 적는다.

---

## 0. 결론

1. **DataGrip 의 힘은 AI 보다 「결정적 코드 지원」에 있다.** FK 조인 완성, 약어·부분 일치, 미해결 이름 검사, Alt+Enter quick fix·의도 동작, 이름 바꾸기가 기본이다. AI 는 그 위에 얹혀 있다.
   - Oranda 의 완성은 렉서 문맥 + 메타데이터 캐시 수준이다. 위 기능은 모두 없다(§2).
   - 이 빈칸은 AI 없이 채울 수 있고, 사람과 에이전트가 함께 쓴다. **AI 보다 먼저 한다.**
2. **DataGrip 2025.3~2026.2 의 AI 방향은 연구서의 권장 구조와 같다.** 외부 에이전트(Claude Code·Codex·Junie)가 IDE 내장 MCP 도구와 번들 스킬로 DB 를 다룬다. 동의는 4종류(스키마 읽기·데이터 읽기·스키마 변경·데이터 변경)로 나눈다. 에이전트 질의는 질의 이력에 남긴다.
   - 연구서의 「orandactl MCP + 제안 막대 + SELECT 전용」 결론은 그대로 둔다.
   - 더할 것 두 가지
     - **번들 스킬**: Oranda 용 Claude Code 스킬을 orandactl 과 함께 낸다.
     - **편집기 안 AI 입구**: 오류·계획·문장에 「AI로 설명·고치기·최적화」 단추를 둔다.
3. **Oranda 가 DataGrip 보다 낫게 할 수 있는 곳은 다섯 군데다.**
   - ① 한글 주석으로 컬럼 완성
   - ② FK 가 없는 DB 의 조인 추론(실행 이력·이름 규칙·graphify)
   - ③ 튜닝 근거(A-Rows·자식 커서·벤치마크)를 붙인 AI 튜닝과 결과 검증
   - ④ DB 접근 통제(결재) 도구 환경에 맞춘 서버(Oranda) 쪽 실행 통제
   - ⑤ MyBatis 매퍼까지 이어지는 제안
4. **권장 순서**
   - 단계 0: 결정적 코드 지원 (약 7 w, AI 결정 D-04 와 무관)
   - 연구서 단계 1: MCP 다리 + 번들 스킬 + AI 입구
   - 단계 2: 튜닝 루프
   - 인라인 AI 완성: 맨 뒤

---

## 1. DataGrip 조사 결과

### 1.1 코드 지원 (AI 아님)

| 영역 | DataGrip 기능 | 출처 |
|---|---|---|
| 완성 문맥 | 커서에서 닿는 객체만 제안한다. 라이브 템플릿도 같은 목록에 나온다. 키보드 배열이 틀려도 맞춘다(한/영) | 완성 도움말 |
| 절 완성 | SELECT·INSERT·GROUP BY 필드 목록을 채운다. **FK 로 이어진 표의 JOIN … ON 을 통째로 만든다**(피연산자 순서 설정 가능). 윈도 함수에 `OVER()` 를 붙이고 커서를 안에 둔다 | 완성 도움말 |
| 이름 일치 | camelCase·`_` 이름 머리글자 약어(`ord_dt` → `ORDER_DATE`), 이름 가운데 부분 일치, 점 뒤 거르기 | 완성 도움말 |
| 별칭 | 표를 완성할 때 별칭을 자동으로 붙인다. 표마다 별칭 규칙을 지정할 수 있다 | 완성 도움말 |
| 순위 | 로컬에서 모은 선택 기록으로 순서를 바꾸는 ML 순위. 항목을 더하지는 않는다 | 완성 도움말 |
| 받아들이기 | Enter=넣기, Tab=오른쪽 글을 바꾸기, Ctrl+Shift+Enter=문장 마무리 | 완성 도움말 |
| postfix | `식.from`, `.join`, `.cast` 뒤 Tab. 사용자 정의 가능 | 완성 도움말(구판) |
| 검사 | 미해결 참조(FROM 에 없는 표의 컬럼 등), 방언 미지정, 위험 작업·죽은 코드를 실행 전에 표시 | 검사 도움말, SQL 지원 소개 |
| quick fix·의도 동작 | Alt+Enter: `*` 펼치기(충돌하면 한정자 붙임), 한정자 붙이기·떼기, 하위 쿼리 별칭 넣기 등. 설정에서 목록을 켜고 끈다 | 의도 동작 도움말 |
| 리팩터링 | 표·별칭·컬럼 이름 바꾸기. 스크립트와 DB 의 사용처를 함께 고친다 | SQL 지원 소개 |
| 코드 생성 | 모든 객체 DDL(스키마 일괄), 결과로 DML 만들기, 표 편집기 + DDL 미리보기, 프로시저 실행 스크립트 | SQL 지원 소개 |

### 1.2 AI 기능 (판별 연혁)

| 판 | 추가된 것 |
|---|---|
| 2024.1 | AI Assistant 채팅. 스키마를 붙이면 자연어로 질의를 만든다. 붙이는 정보는 표·컬럼 이름뿐이고 50개 표까지다 |
| 2024.3 | 실행 오류마다 「Explain with AI」(채팅)·「Fix with AI」(편집기에 고친 질의). 편집기 AI 차이 보기(덩어리 강조, 거터 되돌리기, 차이 안에서 고치기) |
| 2025.1 | 「질의 설명」 때 해당 스키마를 저절로 붙인다 |
| 2025.2 | `@`/`#` + `dbObject:` 로 표·뷰 하나만 붙인다. 클라우드 인라인 완성(한 줄·블록·스크립트, Alt+Shift+\\) |
| 2025.3 | Services 창 계획 탭의 「Analyze SQL Plan with AI」. 「Optimize Query with AI」(불필요한 JOIN·빠진 인덱스·나쁜 계획을 찾아 제안하거나 고쳐 씀, 계획 출력 사용) |
| 2026.1 | AI 채팅에 Claude Agent·Codex 통합. MCP 도구: 접속 설정 읽기·시험, 스키마·객체 탐색, 최근·실행 중 질의 보기, 질의 실행·취소, 표 미리보기, CSV 결과. **동의 4종류가 기본으로 켜짐**: 스키마 읽기·데이터 읽기·스키마 변경·데이터 변경 |
| 2026.2 | 번들 스킬 3개(`database-tools`, `database-text-to-sql`, `database-connection-management`). 결과는 작은 표본만 먼저 문맥에 넣는다. 실패하면 DB 의 실제 오류문을 에이전트에 돌려준다. 에이전트 질의를 질의 이력에 남긴다. 앞 결과를 기억해 다시 실행하지 않고 더 가져온다. 에이전트가 만든 객체는 탐색기에 바로 보인다 |
| 상시 | **로컬 Full Line 완성**: 로컬 딥러닝 모델, 추가 라이선스 없음, SQL 지원, Tab 전체·Ctrl+→ 단어·End 줄 단위 받아들이기. x64 AVX2 또는 ARM64 필요. 오프라인 모드에서는 Ollama·LM Studio 로컬 모델만 쓴다 |

- 확인 못 함: 데이터 편집기의 자연어 필터, 클라우드 모드에서 보내는 스키마 범위의 자세한 규칙.
- **연구서 정정**: 연구서 §2 는 DataGrip `execute_sql_query` 의 확인 여부가 문서에 없다고 적었다. 2026.1 릴리스 노트에 동의 4종류가 있으므로, 연구서를 고칠 때 반영한다.

---

## 2. 비교표

표기: ● 있음 / ◐ 일부 / ○ 없음. Oranda 칸의 근거는 코드다(§5.1).

### 2.1 결정적 코드 지원

| 기능 | DataGrip | Oranda 지금 | Oranda 근거 / 메모 |
|---|---|---|---|
| 절별 키워드·객체·별칭 컬럼 완성 | ● | ● | `Completion/CompletionProvider.cs:184-213` |
| 패키지 멤버·시그니처, 매개변수 힌트 | ● | ● | `CompletionProvider.cs:254-370`, `ParameterHints.cs` |
| 힌트 이름 완성 | ? | ● | `CompletionWords.cs:38`. Oracle 전용 장점 |
| FK 기반 JOIN … ON 완성 | ● | ○ | idea.md 3절에 아이디어만 있음 |
| CTE·인라인 뷰 컬럼 완성 | ● | ○ | |
| 약어·부분 일치 | ● | ◐ | 서버 쪽은 접두어 일치뿐(`CompletionProvider.cs:95`). 팝업 안 거르기는 AvaloniaEdit 기본 동작(확인 못 함) |
| 사용 기록 순위 | ● (ML) | ○ | 종류별 고정 우선순위만(`CompletionData.cs:40-47`) |
| 템플릿이 완성 목록에 섞임, postfix | ● | ○ | 조각 약 100개는 있으나 명령으로만 넣음(`SnippetInsertion.cs:160-167`) |
| 미해결 표·컬럼 검사 | ● | ○ | 밑줄은 문법·컴파일 오류·lint 8개뿐(`LintRules.cs:14-24`) |
| quick fix (Alt+Enter·전구) | ● | ○ | 검색 결과 0건 |
| `*` 펼치기·한정자·별칭 넣기 | ● | ○ | idea.md 3절에 아이디어만 있음 |
| 별칭·식별자 이름 바꾸기 | ● | ○ | 정의로 가기·문서 안 참조 찾기만 있음(`Navigation/SymbolAnalyzer.cs`) |
| 완성 시간 예산 | — | ● 150 ms | 넘으면 캐시만으로 답함(`CompletionProvider.cs:13-21`) |

### 2.2 AI

| 기능 | DataGrip | Oranda 연구서 계획 | 차이 |
|---|---|---|---|
| 외부 에이전트 연결 | 내장 MCP(2026.1~) | `orandactl mcp` stdio 중계(단계 1) | 같은 방향. TCP 없음이 Oranda 장점 |
| 번들 스킬 | 3개(2026.2) | 없음 | **추가 필요**(§3.2) |
| 동의 단위 | 4종류(스키마·데이터 × 읽기·변경) | 접속별 5단계(끔·메타·계획·조회·조회+행 값) | Oranda 는 「변경」을 아예 없앰. 더 엄격 |
| 쓰기 실행 | 동의하면 에이전트가 실행 | 절대 안 함. 편집기 제안 → 사람이 실행(SAFE-03·DB 접근 통제(결재) 도구) | Oranda 가 맞다(DB 접근 통제(결재) 도구) |
| 편집기 반영 | AI 차이 보기·거터 되돌리기 | 비모달 제안 막대·덩어리별 수락·`stale` | 같은 수준 |
| 오류 → AI 설명·고치기 | 오류 줄에 단추(2024.3~) | `fix-error` 프롬프트(단계 1~2), 편집기 단추는 없음 | **편집기 입구 필요**(§3.3) |
| 계획 → AI 분석 | 계획 탭 단추(2025.3) | `explain_plan`·`suggest_tuning` 도구(단계 2) | Oranda 는 A-Rows·자식 커서·벤치마크까지 줄 수 있음(§3.4) |
| 질의 최적화 결과 검증 | 확인 못 함(제안·재작성) | 결과 동일성·벤치마크 비교(연구서 §8) | **Oranda 가 앞설 수 있음** |
| 에이전트 질의 이력 | 질의 이력에 남김 | `HistorySource.Agent` + 감사 표 | 같음 |
| 결과 표본만 문맥에 | 작은 표본 먼저 | 요약 먼저·페이지·행 상한 | 같음 |
| 객체 하나 붙이기(`@`) | ● | `resolve_objects`·`describe_object` | 외부 클라이언트의 `@` 는 MCP 리소스로 대응 |
| 인라인 AI 완성 | 로컬 Full Line + 클라우드 | 단계 3 내장(지연 때문에 MCP 불가) | 비용 큼. 맨 뒤 |
| 도메인 지식 | 없음 | llm-wiki nl2sql·graphify 연결 | **Oranda 고유** |

---

## 3. 개선 방안

### 3.1 단계 0 — 결정적 코드 지원 (AI 무관, 먼저)

에이전트도 이 엔진을 쓴다. `validate_sql` 도구가 같은 검사 결과와 고칠 방법을 돌려주면 LLM 의 수정 루프(nl2sql `repair`)가 짧아진다.

| 순서 | 기능 | 내용 | DataGrip 보다 나은 점 | 크기 |
|---|---|---|---|---|
| 1 | 미해결 이름 검사 + quick fix 틀 | 캐시에 없는 표·컬럼에 밑줄을 긋는다. Alt+Enter 전구 메뉴로 고칠 방법을 고른다: 비슷한 이름(편집 거리), FROM 에 표 넣기, 한정자 붙이기. 캐시가 아직 덜 찼거나 권한이 없으면 표시를 약하게 한다(오탐 방지) | 결과를 `validate_sql` 로도 내보낸다 | 1.5 w |
| 2 | **한글 주석으로 완성** | `주문일` 을 치면 주석이 「주문일자」인 `ORDER_DT` 가 나온다. 목록에 주석을 함께 보인다. 컬럼·표 주석을 캐시에서 찾는다 | DataGrip 에는 없음. 한글 주석이 많은 국내 DB 에서 효과가 큼 | 0.5 w |
| 3 | 약어·부분 일치 | `od` → `ORDER_DT`(밑줄 머리글자), 이름 가운데 일치. 접두어 일치를 맨 앞에 둔다 | — | 0.5 w |
| 4 | **조인 완성 (FK + 추론)** | `JOIN` 뒤에 이어질 표와 `ON` 조건을 제안한다. 근거 순서: FK 제약 → 실행 이력에서 실제로 쓴 조인 조건(`oranda.db` history 를 파서로 읽음) → 이름 규칙(같은 이름의 PK 컬럼). 근거를 목록에 보인다(「FK」「이력 12회」「이름 일치」) | **FK 제약이 없는 레거시·MES DB 에서도 동작.** DataGrip 은 FK 만 씀 | 1.5 w |
| 5 | `*` 펼치기·별칭 넣기·한정자 | Alt+Enter 의도 동작. 펼칠 때 주석을 `-- 주석` 으로 붙이는 선택지(idea.md 3절 「컬럼 주석 달기」와 합침) | 주석 붙이기 | 1 w |
| 6 | CTE·인라인 뷰 컬럼 완성 | `WITH a AS (SELECT x, y …)` 뒤 `a.` 에서 `x, y` 를 제안한다. 파서(L2)의 SELECT 목록을 쓴다 | — | 1 w |
| 7 | 사용 기록 순위 | 이 접속에서 자주 고른 항목을 앞에 둔다. 로컬에만 저장한다 | — | 0.5 w |
| 8 | 템플릿을 완성 목록에 | 조각 약 100개를 목록에 섞고 Tab 으로 펼친다 | — | 0.5 w |

- 성능: 1~4번은 150 ms 예산 안에서 캐시만 쓴다. 4번의 이력 분석은 뒤에서 미리 색인한다.
- 별칭 이름 바꾸기(문서 안)는 `SymbolAnalyzer` 위에 작게 얹을 수 있다(0.5 w). DB 객체 이름 바꾸기는 DDL 이므로 하지 않는다.

### 3.2 번들 스킬 — `oranda-sql` (연구서 단계 1에 더함)

DataGrip 2026.2 처럼 MCP 도구와 함께 「쓰는 법」을 스킬로 낸다. 도구만 주면 에이전트가 순서를 매번 다시 찾는다.

- 내용
  - 도구 순서: `resolve_objects` → `describe_object` → 작성 → `validate_sql` → (개발 접속) `explain_plan` → `propose_edit`
  - Oracle 규칙: 버전별 문법(19c ↔ 23ai), 바인드 사용, `ROWNUM`/`FETCH FIRST`
  - 이 환경 규칙: 쓰기 문장은 실행하지 말고 제안만 한다. 운영 접속에는 붙지 않는다. DB 접근 통제(결재) 도구 가 결재를 요구할 수 있는 문장(ALTER SESSION 등)은 사람에게 넘긴다
  - nl2sql 와의 관계: llm-wiki 세션에서는 nl2sql 가 SQL 을 만들고, `oranda-sql` 은 실제 DB 검증과 편집기 반영 절만 맡는다
- 배포: orandactl 과 같은 설치본에 넣고, 「에이전트 설정」 화면에 「Claude Code 에 스킬 설치」 단추를 둔다.
- 크기: 0.5 w

### 3.3 편집기 안 AI 입구 (연구서 단계 1~2에 더함)

DataGrip 은 AI 를 채팅 창에만 두지 않고 일하는 자리에 단추로 둔다. Oranda 는 내장 LLM 이 없으므로, 단추를 「문맥 꾸러미 넘기기」로 만든다.

| 자리 | 단추 | 넘기는 꾸러미 |
|---|---|---|
| 오류 띠 | AI로 설명 / AI로 고치기 | 오류문·위치·문장·관련 객체 정의(메타데이터만) |
| 계획 탭 | AI로 계획 분석 | 계획(A-Rows 포함)·통계·인덱스 목록 |
| 문장 우클릭 | AI로 설명 / 최적화 / 다른 DBMS SQL 을 Oracle 로 | 문장·관련 객체 정의 |

- 동작
  - MCP 클라이언트가 붙어 있으면 MCP 프롬프트(`fix-error`·`tune-statement`)를 「대기 요청」으로 올린다. 에이전트가 `get_pending_requests` 로 가져간다. 결과는 늘 `propose_edit` 로 돌아온다.
  - 붙어 있지 않으면 꾸러미를 클립보드에 Markdown 으로 복사한다. 보낼 글을 먼저 보여 주고 동의를 받는다(`IOutboundConsent`).
- 이유: DataGrip 의 「Explain/Fix with AI」 체감을 내장 LLM 없이 낸다. D-04 를 풀지 않고도 낼 수 있다.
- 크기: 1.5 w

### 3.4 튜닝 루프의 차별점 (연구서 단계 2 보강)

DataGrip 의 「Optimize Query with AI」는 제안이나 재작성까지다. Oranda 는 WP-06 이 이미 만든 근거로 **검증까지** 한다.

1. 에이전트에 주는 근거: `DISPLAY_CURSOR ALLSTATS LAST` 계획(E-Rows 대 A-Rows 차이 표시), 자식 커서와 불일치 사유, 표·열 통계, 대기 표본.
2. 에이전트의 재작성 SQL 을 Oranda 가 검증한다.
   - 결과 동일성: 건수·해시 비교, 개발 접속만
   - 벤치마크: SELECT 전용 실행기, v$mystat 전후 차이
3. 제안 막대에 「원래 / 제안」 표(시간·논리 읽기·건수 일치)를 붙인다. 인덱스 제안은 만들지 않고 스크립트로만 준다.
4. 튜닝 HTML 보고서(TSK-06-07)에 「AI 제안과 검증 결과」 절을 더한다.

### 3.5 인라인 AI 완성 (맨 뒤)

- DataGrip 은 로컬 모델로 무료 인라인 완성을 준다. Oranda 가 같은 것을 하려면 로컬 추론 런타임과 모델 배포가 필요하다. 설치 크기와 시작 시간 목표, 오래된 노트북(이 PC: i5-1135G7)의 CPU 부하와 충돌한다.
- 대안: 사용자가 이미 Ollama·사내 게이트웨이를 쓰면 그쪽에 붙는 선택 기능으로만 둔다(연구서 단계 3).
- 인라인 완성보다 §3.1(결정적)과 §3.3(입구)이 비용 대비 효과가 크다.

---

## 4. 정리한 단계

| 단계 | 내용 | 크기 | AI 결정(D-04) |
|---|---|---|---|
| 0 | §3.1 결정적 코드 지원 1~8 | 약 7 w | 무관 |
| 1 | 연구서 단계 1 + §3.2 스킬 + §3.3 입구(클립보드·대기 요청) | 약 10 w | 개발 빌드 먼저 |
| 2 | 연구서 단계 2 + §3.4 검증 표·보고서 절 | 약 9 w | 필요 |
| 3 | 연구서 단계 3 + §3.5 선택 인라인 완성 | 약 10 w+ | 필요 |

### 사용자가 정할 것

1. 단계 0을 지금 WBS(docs/oranda1)에 올릴까? 올리면 WP 하나(가칭 「코드 지원 강화」)로 묶는다.
2. 조인 추론에 실행 이력을 써도 될까? 이력 SQL 은 로컬에만 있고 밖으로 나가지 않는다.
3. §3.3 클립보드 경로에서 꾸러미에 객체 정의(컬럼·주석)를 넣어도 될까? 연구서 §13.2-2 와 같은 질문이다.
4. 번들 스킬을 Oranda 저장소에 둘까, llm-wiki 에 둘까?

---

## 5. 출처

### 5.1 저장소 (읽기만)

- `src/Oranda.Language/Completion/{CompletionContextAnalyzer,CompletionProvider,CompletionWords,ParameterHints}.cs`
- `src/Oranda.App/Features/Completion/{CompletionData,CompletionController,CompletionCommands}.cs`
- `src/Oranda.App/Features/Templates/SnippetInsertion.cs`, `Snippets/SnippetLibrary.cs`
- `src/Oranda.App/Features/Diagnostics/EditorDiagnostics.cs`, `Lint/LintRules.cs`
- `src/Oranda.App/Features/Navigation/{NavigationCommands,SymbolAnalyzer}.cs`
- `src/Oranda.Core/Extensibility/{IEditorContext,IOutboundConsent}.cs`
- `docs/PRD.md` EDIT-03·04·08, D-04
- `docs/idea.md`, `docs/oranda1/research/llm-editor-integration-2026-10-24.md`

### 5.2 웹 (2026-10-25 조사)

- DataGrip 코드 완성: https://www.jetbrains.com/help/datagrip/auto-completing-code.html
- DataGrip 의도 동작: https://www.jetbrains.com/help/datagrip/intention-actions.html
- 미해결 참조 검사: https://www.jetbrains.com/help/datagrip/sql-unresolved-reference.html
- SQL 지원 소개: https://lp.jetbrains.com/sql-support/
- 로컬 Full Line 완성: https://www.jetbrains.com/help/datagrip/full-line-code-completion.html
- AI Assistant — Use AI with databases: https://www.jetbrains.com/help/ai-assistant/use-ai-with-databases.html
- What's New 2026.1: https://www.jetbrains.com/datagrip/whatsnew/2026-1/
- 2026.2 블로그: https://blog.jetbrains.com/datagrip/2026/07/16/datagrip-2026-2-ai-agent-skills-mcp-tools-and-cli-commands-for-data-source-management-bundled-jdbc-drivers-and-improved-session-control/
- AI Agents in DataGrip (2026-08-26): https://blog.jetbrains.com/datagrip/2026/08/26/ai-agents-in-datagrip/ (본문은 403 으로 못 읽음, 검색 요약만)
- What's New 2025.2: https://www.jetbrains.com/datagrip/whatsnew/2025-2/
- What's New 2025.3: https://www.jetbrains.com/datagrip/whatsnew/2025-3/
- What's New 2025.1: https://www.jetbrains.com/datagrip/whatsnew/2025-1/
- 2024.3(Explain/Fix with AI, AI 차이 보기): https://www.componentsource.com/product/datagrip/releases/2868361
- 2024.1(스키마 붙이기 50개 표 한도): https://www.jetbrains.com/datagrip/whatsnew/2024-1
- AI Assistant 오프라인 모드·로컬 모델: https://www.jetbrains.com/help/ai-assistant/2025.2/use-custom-models.html
