# Oranda PRD — Windows·macOS용 Oracle 개발·운영 도구

| 항목 | 내용 |
|---|---|
| 문서 상태 | 초안 v0.1 (2026-10-19) |
| 제품명 | Oranda (가칭) |
| 기반 자료 | `docs/orange-analysis.md` (Orange 5.0 역분석), 경쟁 도구 조사, 최신 Oracle·ODP.NET 조사 (2026-10) |
| 기술 스택 | .NET 10, Avalonia 12, ODP.NET Core Managed (`Oracle.ManagedDataAccess.Core` 23.x) |
| 다음 문서 | TRD (`docs/TRD.md`) — 아키텍처·컴포넌트 선정 결과를 담는다 |

---

## 1. 배경

### 1.1 지금 쓰는 도구의 문제
- **Orange for ORACLE 5.0**은 32비트 MFC 앱이고 32비트 `oci.dll`이 있어야 동작한다. Oracle은 23 버전부터 32비트 클라이언트를 내지 않으므로 최신 서버(19c 이상, 26ai)에는 사실상 붙을 수 없다. 후속인 Orange 7도 Windows 전용이고 Oracle Client 설치가 필요하다.
- **Oracle SQL Developer 데스크톱**은 24.3.1(2024-12)이 마지막 릴리스이며 24.3 지원이 2026-04에 끝났다. 신기능은 VS Code 확장으로만 나온다.
- Oracle 기능이 깊은 도구(Toad, PL/SQL Developer, Orange, dbForge)는 **Windows 전용이거나 Oracle Client(Thick)를 요구한다.** Toad의 Mac판은 2026년에도 Intel 빌드다.
- 범용 도구(DBeaver, DataGrip, TablePlus, Beekeeper)는 Mac에서 돌지만 **PL/SQL 디버깅, 세션·락 분석, 실행 계획 같은 Oracle 고유 기능이 얕다.**
- Java·Electron 기반 도구는 시작 시간과 메모리 부담이 크다는 불만이 반복된다. SQL Developer는 시작 직후 약 350 MB, 장시간 사용 시 600 MB 이상이라는 보고가 있고, DataGrip은 연결 3개에 910 MB라는 비교 측정이 있다(측정 방법 미공개).

### 1.2 기회
"**Instant Client 설치 없이, Windows와 Apple Silicon Mac에서 같은 기능으로, Orange·PL/SQL Developer 수준의 Oracle 기능을 2초 안에 뜨는 앱으로**" 제공하는 도구는 시장에 확실한 강자가 없다. 신생 도구(Allround Automations의 Plyxon, Jam SQL Studio)가 이 자리를 노리기 시작했으므로, **시작 시간·메모리·Oracle 깊이·운영 안전장치**를 수치로 증명해야 한다.

---

## 2. 목표와 비목표

### 2.1 제품 목표
| ID | 목표 | 측정 |
|---|---|---|
| G1 | Oracle Client 설치 없이 19c~26ai, Autonomous Database에 접속한다 | 7.1 지원 매트릭스 전 항목 접속 성공 |
| G2 | Windows x64와 macOS ARM64에서 같은 기능을 제공한다 | 기능 수락 기준을 두 OS에서 모두 통과 |
| G3 | 경쟁 Java·Electron 도구보다 가볍다 | 7.2 성능 예산 충족 (시작 1.5 s 이하, 유휴 150 MB 이하) |
| G4 | 개발자의 일상 작업(SQL 작성·실행·편집, PL/SQL 작성·컴파일)을 이 도구 하나로 끝낸다 | 6장 P0·P1 기능 완성 |
| G5 | DBA의 1차 진단(세션·락·Top SQL·실행 계획)을 무료 기능 범위에서 제공한다 | Diagnostics/Tuning Pack 없이 동작 |
| G6 | 운영 DB 사고를 막는다 | 6.10 안전장치 수락 기준 통과, 사용자 데이터 손상 0건 |

### 2.2 비목표 (1.0 범위 밖)
- Oracle 외 DBMS 지원 (Tibero 포함, D-03)
- 외부 판매·라이선스 관리 기능 (사내 전용, D-01)
- PL/SQL 디버거 (D-02, 필요해지면 재검토)
- AI 챗봇 기능 구현 (D-04, 단 나중에 붙일 수 있게 확장 지점은 1.0 구조에 둔다 — APP-09)
- Linux 데스크톱 정식 지원 (빌드는 가능하게 두되 검증·배포하지 않는다)
- 서버에 보조 스키마·패키지·테이블 설치 (Orange의 `ORANGE` 스키마 방식은 쓰지 않는다)
- Reorg, Log Miner, Data Quality 같은 Orange 7 DBA 관리 모듈
- 웹 브라우저판, 팀 협업 서버

---

## 3. 사용자

| 페르소나 | 상황 | 꼭 필요한 것 |
|---|---|---|
| **P1 애플리케이션 개발자** | 하루 대부분 SQL·PL/SQL을 쓴다. 회사 PC는 Windows, 개인 장비는 Mac | 문법을 이해하는 에디터, 자동완성, 포매터, 결과 그리드 편집, 컴파일 오류 위치 이동 |
| **P2 운영 DBA** | 장애 시 세션·락·느린 SQL을 몇 분 안에 찾아야 한다 | 세션 목록·블로킹 트리·Kill, Top SQL, 실제 실행 계획, 권한에 따른 기능 자동 조정 |
| **P3 데이터 분석·운영 담당** | 운영 DB를 조회만 한다 | 실수 방지(읽기 전용, 운영 표시), Excel/CSV 내보내기 |
| **P4 Orange 5 이전 사용자** | 손에 익은 단축키·화면 구성이 있다 | Orange 단축키 프리셋, 같은 개념의 도구(SQL Tool, Session Monitor, SQL Monitor, Schema Browser) |

---

## 4. 경쟁 구도와 포지셔닝

| 도구 | OS | Client 필요 | Oracle 깊이 | 약점 |
|---|---|---|---|---|
| Toad for Oracle | Win (Mac은 Intel 빌드) | 필요 | 최상 | 연 €857~5,526, Mac 기능 축소 |
| PL/SQL Developer 17 | Win | 필요 | 최상 (디버거) | Windows 전용, $243 영구 |
| Orange 7 | Win | 필요 (11gR2 32bit 안내) | 상 (모니터링) | Windows 전용, 32bit 클라이언트 의존 |
| SQL Developer / VS Code 확장 | Win·Mac·Linux | 불필요 | 중상 | 데스크톱 단종, Java 백엔드 메모리 |
| DBeaver / DataGrip | Win·Mac·Linux | 불필요 | 중 | 메모리 680~910 MB 보고, Oracle DBA 기능 얕음 |
| Plyxon / Jam SQL Studio | Win·Mac·Linux | 불필요 (Jam) | 미확인 | 신생, 검증 사례 적음 |

**Oranda의 위치**: PL/SQL Developer·Orange의 Oracle 깊이 + 범용 도구의 크로스플랫폼 + 네이티브에 가까운 시작 시간·메모리.
차별화 축 4가지 — (1) Thin 드라이버로 Client 설치 0, (2) 성능 예산을 공개 KPI로 관리, (3) 무료 범위의 DBA 진단과 **라이선스 팩 보호장치**, (4) 운영 DB 안전장치.

### 4.1 Orange 대비 기능표
현재 Orange는 **8.0**(2024~2025 출시, 멀티 DBMS, ODBC/JDBC 접속, 64비트, Windows 10/11 전용)이고, Oracle 전용 **7.0**(build 393)도 계속 패치되고 있다. 자세한 내용은 `docs/orange-analysis.md` 8장, Orange 5.0 전체 기능은 `docs/orange5-features.md`.

| 영역 | Orange 5.0 | Orange 7.0 / 8.0 | Oranda 1.0 | Oranda 요구사항 |
|---|---|---|---|---|
| 접속 방식 | OCI 32비트 | 7: OCI(비트 수 일치) / 8: ODBC·JDBC | Thin, 설치 0 | CONN-01~03 |
| OS | Windows | Windows (8은 64비트만) | Windows·macOS | 7.1 |
| 지원 서버 | ~11g | 7: 문서상 12.1까지 / 8: 19c(드라이버 기준) | 19c·26ai·ADB | 7.1 |
| 세션 라벨 색상 | 없음 | 있음 | 있음 + 운영 보호 | CONN-06, SAFE-01 |
| 자동완성 | 없음 | 단어·객체 목록 | 문맥 해석(별칭→컬럼) | EDIT-04 |
| 문법 오류 표시 | 없음 | 없음 | 입력 중 표시 | EDIT-03 |
| 포매터 | 옵션 4종 | 옵션 4종 | 상세 옵션·프로필 공유 | FMT-01~11 |
| 실행 단축키 체계 | F5, Ctrl+M/J/K/L, F9 | 같음 + F11 새 탭 | Orange 프리셋 제공 | EDIT-10, EXEC-01 |
| 결과 편집 | PK/UK 필수, FROM 첫 테이블 | 같음 | PK/UK 또는 ROWID 필수, 키 보존 검사, 영향 행 1 검증 | GRID-04, 6.5.1 |
| LOB 보기·편집 | 제한적 | CLOB 편집, BLOB 저장, 이미지 | 같음 + JSON·VECTOR | GRID-02, GRID-07 |
| SQL 이력 | Cached SQL, Named Script | 같음 | 같음 + 검색 | EXEC-07, EXEC-09 |
| 실행 계획 | EXPLAIN PLAN (SELECT만) | + DISPLAY_CURSOR 옵션, 위험 단계 강조, Tuning Advisor | DISPLAY/DISPLAY_CURSOR, 위험 단계 강조 | PLAN-01~05 |
| PL/SQL 편집 | 컴파일, Check-Out(서버 스키마) | 같음 | 컴파일, 서버 변경 충돌 확인(서버 객체 없음) | PLSQL-01~05 |
| PL/SQL 디버거 | DBMS_DEBUG | DBMS_DEBUG, Watch | 범위 밖(필요 시 재검토) | PLSQL-06, D-02 |
| 세션·락 모니터 | Session Monitor, Lock 트리 | + Transaction, Lock/Latch Monitor | 세션·블로킹 트리·Kill·Trace | SESS-01~06 |
| Top SQL | SQL Monitor | 같음 | 같음(sql_id 기반) | SQLM-01~02 |
| ASH/AWR | AWR Manager | ASH Monitor, AWR Manager (팩 확인 장치 없음) | 팩 보유 선언 시에만 | SQLM-03, SAFE-07 |
| 스키마 브라우저 | 트리, CREATE/ALTER GUI | 같음 + Comment | 트리·상세·DDL, 마법사는 이후 | SCH-01~08 |
| 스키마 비교 | 없음 | 8: Object Compare | 이후 | 6.13 |
| 내보내기·적재 | Unload, Load | + JSON, Excel 미설치 저장 | CSV·xlsx·JSON·INSERT, 가져오기 | EXP-01~02 |
| AI | 없음 | 8: 독립 채팅 창(API 키) | 1.0은 확장 지점만, 이후 쿼리 작성 챗봇 | APP-07, APP-09 |
| 서버 객체 설치 | ORANGE 스키마 | ORANGE 스키마 | 없음 | C-05 |
| HiDPI | 미지원 | 100% 권장(그 외 글자 잘림) | 100~250% | APP-02 |

---

## 5. 릴리스 계획

| 단계 | 목적 | 포함 기능 (6장 ID) |
|---|---|---|
| **M0 기술 검증** | 위험한 가정을 먼저 증명한다 | 실제 DB 접속(19c·26ai 컨테이너, ADB), 에디터 컴포넌트 선정, 100만 행 그리드 시험, 시작 시간 1.5 s 달성 방법, macOS 빌드·서명 시험 |
| **M1 알파** | 개발자가 매일 쓸 수 있다 | CONN, EDIT(문법 인식 1단계), FMT 기본 프로필, EXEC, GRID 조회, PLAN, SCH 읽기 전용, SAFE 기본 |
| **M2 베타** | Orange 5를 대체한다 | GRID 편집, PLSQL 편집·컴파일, SESS, SQLM, FMT 전체 옵션, EXP 내보내기, SAFE 전체 |
| **M3 1.0 출시** | 사내 전체에 배포한다 | 서명·공증 설치 파일, 자동 업데이트, 사용자 문서, 접근성·다국어 |
| **이후** | 기능 확대 | AI 챗봇(자연어→쿼리 작성), 스키마 비교, ASH/AWR(팩 보유 선언 시), ER 다이어그램, PL/SQL 디버거(필요 시) |

우선순위 표기: **P0** = 해당 단계 출시 차단, **P1** = 해당 단계 목표, **P2** = 여유 있으면.

---

## 6. 기능 요구사항

### 6.1 접속 관리 (CONN)
| ID | 요구사항 | 우선 | 단계 |
|---|---|---|---|
| CONN-01 | 접속 방식: EZConnect Plus 문자열, `tnsnames.ora` 별칭(TNS 디렉터리 지정), 호스트·포트·서비스명 입력 폼 | P0 | M1 |
| CONN-02 | 인증: 사용자/비밀번호, SYSDBA·SYSOPER·SYSBACKUP·SYSDG·SYSKM, 프록시 사용자(`app[scott]`) | P0 | M1 |
| CONN-03 | TLS(TCPS)와 mTLS 지갑. ADB 지갑 zip을 끌어다 놓으면 압축을 풀어 접속 프로필을 만든다. 지갑 없는 단방향 TLS 지원 | P0 | M1 |
| CONN-04 | 비밀번호 만료(ORA-28001) 시 새 비밀번호 입력 대화상자 | P1 | M1 |
| CONN-05 | 접속 직후 서버 점검: 버전(23.26.x는 "26ai"로 표시), 인스턴스·호스트·서비스·PDB/CDB, ADB 여부, 권한·역할 → 기능 자동 켜기/끄기 (구현 시작됨: `ServerProbe`) | P0 | M1 |
| CONN-06 | 동시 접속 여러 개, 접속마다 색상·환경 라벨(개발/검증/운영) | P0 | M1 |
| CONN-07 | 도구별 별도 세션: 모니터링 도구는 편집 세션과 다른 연결을 써서 긴 쿼리 중에도 동작 | P0 | M2 |
| CONN-08 | NLS 세션 설정(날짜 형식, 언어, 정렬)을 프로필에 저장 | P1 | M1 |
| CONN-09 | 가져오기: SQL Developer `connections.json`, `tnsnames.ora`, Orange 접속 목록(`Data/*.lst`) | P1 | M2 |
| CONN-10 | Kerberos, OCI IAM 토큰, Entra ID 토큰, LDAP 네이밍 | P2 | 이후 |
| CONN-11 | 접속 끊김 감지 후 재접속 안내. 미커밋 트랜잭션이 있었으면 잃었다고 알린다 | P0 | M2 |
| CONN-12 | 세션 유지(Keep Alive): 지정 간격으로 가벼운 호출을 보내 방화벽 유휴 끊김 방지. 접속별 켜기/끄기 | P1 | M1 |
| CONN-13 | 명령줄 접속(`oranda user@alias`, `as sysdba`) — 비밀번호는 명령줄로 받지 않고 대화상자나 저장소에서 읽는다 | P2 | M3 |

### 6.2 에디터 — 문법 인식 (EDIT)
에디터는 색칠만 하는 수준이 아니라 **SQL·PL/SQL을 실제로 파싱**해서 그 결과로 기능을 만든다.

| ID | 요구사항 | 우선 | 단계 |
|---|---|---|---|
| EDIT-01 | Oracle SQL·PL/SQL 파서: 문장 경계, 블록 구조(BEGIN/END, 패키지 명세·본문), 식별자·별칭·문자열·주석·q-quote(`q'[...]'`)를 구분한다. 23ai/26ai 문법(BOOLEAN, VECTOR, `IF [NOT] EXISTS`, Domain, Annotation, `GRAPH_TABLE`)을 포함한다 | P0 | M1 |
| EDIT-02 | 문법 기반 강조: 키워드·내장 함수·객체명·변수·바인드를 파서 결과로 구분해서 칠한다 | P0 | M1 |
| EDIT-03 | 문법 오류 표시: 입력 중 오류 위치에 밑줄과 메시지. 서버 컴파일 오류(`user_errors`)도 같은 방식으로 표시 | P0 | M1 |
| EDIT-04 | 문맥 자동완성: `FROM` 뒤 테이블·뷰·시노님, 별칭 뒤 `.` 입력 시 그 테이블 컬럼, 패키지명 뒤 멤버와 인자, 힌트(`/*+`) 안의 힌트 이름. 딕셔너리는 접속별로 캐시하고 백그라운드에서 갱신 | P0 | M1 |
| EDIT-05 | 정의로 이동(Ctrl+클릭), 객체 정보 팝업(컬럼·타입·주석), 참조 찾기(현재 파일) | P1 | M2 |
| EDIT-06 | 접기(블록·주석), 문서 개요(패키지 멤버 목록), 괄호·BEGIN/END 짝 표시 | P1 | M1 |
| EDIT-07 | 다중 커서, 열 선택, 찾기/바꾸기(정규식), 북마크, 주석 토글 | P0 | M1 |
| EDIT-08 | 코드 조각(템플릿)과 사용자 정의 조각. 옵티마이저 힌트·내장 함수·의사 컬럼 설명과 예제 삽입(Orange Template Browser 대응) | P1 | M2 |
| EDIT-11 | 대소문자 변환(대문자, 소문자, 단어 첫 글자, 문장 첫 글자, 반전), 블록 들여쓰기·내어쓰기 | P1 | M1 |
| EDIT-09 | 탭 묶음(Workspace) 저장·복원, 비정상 종료 시 미저장 내용 복구 | P0 | M1 |
| EDIT-10 | 단축키 프리셋: 기본, Orange 5, PL/SQL Developer, SQL Developer | P1 | M2 |

### 6.3 포매터 (FMT)
| ID | 요구사항 | 우선 | 단계 |
|---|---|---|---|
| FMT-01 | 파서 기반 포매터. 전체 문서, 선택 영역, 현재 문장 단위로 실행 | P0 | M1 |
| FMT-02 | 옵션 — 대소문자: 키워드·내장 함수·식별자 각각 대문자/소문자/그대로 | P0 | M1 |
| FMT-03 | 옵션 — 들여쓰기: 공백·탭, 폭, 하위 쿼리·CASE·PL/SQL 블록 들여쓰기 깊이 | P0 | M1 |
| FMT-04 | 옵션 — 쉼표 위치(줄 앞/줄 뒤), 연산자 앞뒤 공백, 괄호 안 공백 | P0 | M2 |
| FMT-05 | 옵션 — 줄바꿈 규칙: SELECT 목록 한 줄에 하나/최대 폭까지, JOIN 절마다 줄바꿈, WHERE의 AND/OR 앞 줄바꿈, IN 목록 줄바꿈 기준, 최대 줄 폭 | P0 | M2 |
| FMT-06 | 옵션 — 정렬: 별칭(`AS`) 열 맞춤, 대입(`:=`) 열 맞춤, 컬럼 정의 열 맞춤, `=` 정렬 | P1 | M2 |
| FMT-07 | 옵션 — 주석 보존(위치·빈 줄 유지), 빈 줄 최대 개수, 문장 끝 `;`·`/` 처리 | P0 | M1 |
| FMT-08 | 서식 프로필: 여러 개 저장, 기본 프로필 지정, 파일(JSON)로 내보내기·가져오기로 팀 공유. 기본 제공 프로필 3종 이상 | P0 | M2 |
| FMT-09 | 미리보기: 옵션을 바꾸면 예제 SQL에 즉시 반영해 보여 준다 | P1 | M2 |
| FMT-10 | 포맷 제외 구간 표시(`-- @formatter:off` / `on`) | P1 | M2 |
| FMT-11 | 포맷 결과 보장: 포맷 전후 토큰 열(공백·주석 제외)이 같아야 한다. 다르면 적용하지 않고 알린다 | P0 | M1 |

### 6.4 SQL 실행 (EXEC)
| ID | 요구사항 | 우선 | 단계 |
|---|---|---|---|
| EXEC-01 | 실행 단위: 커서 위치 문장(Ctrl+Enter), 선택 영역, 스크립트 전체(F5). 문장 구분은 파서 결과(`;`, `/`, 빈 줄 규칙 선택) | P0 | M1 |
| EXEC-02 | 실행 취소: 진행 중인 호출을 서버에서 중단(드라이버 cancel). 취소 버튼은 1 s 안에 반응 | P0 | M1 |
| EXEC-03 | 바인드 변수 입력 대화상자(타입 지정, 마지막 값 기억), 치환 변수(`&`, `&&`) | P0 | M1 |
| EXEC-04 | `DBMS_OUTPUT` 자동 수집과 출력 창 | P0 | M1 |
| EXEC-05 | SQL*Plus 주요 명령 해석: `SET SERVEROUTPUT`, `DEFINE`, `PROMPT`, `@파일`, `SPOOL`, `WHENEVER SQLERROR` | P1 | M2 |
| EXEC-06 | 트랜잭션: 수동 커밋 기본, 미커밋 표시(상태바), 커밋·롤백 버튼, 종료·접속 해제 시 미커밋 경고 | P0 | M1 |
| EXEC-07 | 실행 이력: 접속별 SQL 이력(시간·소요·행 수·결과), 검색, 다시 실행 | P0 | M1 |
| EXEC-08 | 실행 통계: 문장별 `v$mystat` 전후 차이(consistent gets, physical reads, redo size 등) | P2 | M2 |
| EXEC-09 | 이름 붙인 스크립트: 로컬 폴더를 연결해 트리로 보여 주고 열기·실행·추가·삭제(Orange Named Script 대응) | P1 | M2 |
| EXEC-10 | 스크립트 실행 중 오류 처리 선택: 묻기 / 중지 / 계속. 오류 문장 위치로 이동 | P0 | M1 |
| EXEC-11 | 결과를 새 결과 탭으로 열기(이전 결과 유지), Orange 단축키 F11 | P1 | M1 |

### 6.5 결과 그리드 (GRID)
| ID | 요구사항 | 우선 | 단계 |
|---|---|---|---|
| GRID-01 | 가상화 그리드: 필요한 만큼만 가져오기(Fetch As Need), 100만 행 스크롤, 열 정렬·숨김·고정, 텍스트 결과 모드 | P0 | M1 |
| GRID-02 | 타입별 표시: DATE/TIMESTAMP(NLS 형식), NUMBER 정밀도 보존, CLOB/BLOB 뷰어(텍스트·16진·이미지), JSON 정렬 표시, BOOLEAN 체크 표시, VECTOR 요약(차원·포맷, 펼쳐 보기), RAW, INTERVAL, XMLType | P0 | M1 |
| GRID-03 | 복사: 셀·행·열 머리글 포함, TSV/CSV/INSERT문/IN 목록 형태 | P0 | M1 |
| GRID-04 | 셀 편집 후 반영(UPDATE·INSERT·DELETE). **6.5.1 행 식별 규칙을 반드시 따른다** | P0 | M2 |
| GRID-05 | 편집 내용 미리보기: 반영 전 생성될 DML을 보여 주고 확인을 받는다 | P0 | M2 |
| GRID-06 | 결과 탭 고정·비교(두 결과의 차이 강조) | P2 | 이후 |
| GRID-07 | 행 보기(한 행을 세로로), LOB 편집기: CLOB 보기·편집·파일 저장, BLOB 파일 저장·불러오기, 이미지 미리보기 | P1 | M2 |
| GRID-08 | 표시 옵션: 이전 행과 같은 값 숨김, 천 단위 구분, NULL 표시 문자·색 | P2 | M2 |

#### 6.5.1 그리드 편집의 행 식별 규칙 (필수)
1. 결과를 편집하려면 결과 집합에 다음 중 하나가 **반드시** 있어야 한다.
   - 대상 테이블의 **PK 전체 컬럼** (PK가 없으면 NOT NULL 컬럼으로만 이뤄진 유니크 키 전체 컬럼)
   - **ROWID** (사용자가 SELECT에 넣었거나, 도구가 단일 테이블 조회에 자동으로 덧붙인 것)
2. 둘 다 없으면 그리드는 **읽기 전용**이 된다. 이유와 해결 방법("ROWID를 SELECT에 추가하거나 PK 컬럼을 포함하세요")을 그리드 위에 표시한다.
3. UPDATE와 DELETE는 항상 `WHERE <PK 컬럼> = :바인드` 또는 `WHERE ROWID = :rid` 조건으로만 만든다. 값을 문자열로 이어 붙이지 않고 바인드 변수를 쓴다.
4. 실행 결과 영향 받은 행 수가 정확히 1이 아니면 그 문장을 반영하지 않고(세이브포인트로 롤백) 해당 행을 표시해 알린다.
5. 집계·`DISTINCT`·`GROUP BY`·집합 연산 결과는 편집할 수 없다. 조인 결과는 사용자가 편집 대상 테이블 하나를 고르고(기본값: FROM 절 첫 테이블), 그 테이블의 PK 또는 ROWID가 결과에 있으며 그 테이블이 키 보존(key-preserved)일 때만 편집할 수 있다. 대상 테이블이 아닌 컬럼은 편집 불가로 표시한다(Orange 5.0 결과 수정 모드와 같은 방식, 단 ROWID와 키 보존 검사를 더함).
6. 편집 중 다른 세션이 같은 행을 바꿨는지 확인할 수 있도록, 반영 전에 원래 값과 현재 값을 비교하는 선택 옵션(낙관적 잠금)을 둔다.

### 6.6 실행 계획 (PLAN)
| ID | 요구사항 | 우선 | 단계 |
|---|---|---|---|
| PLAN-01 | 예상 계획: `EXPLAIN PLAN` + `DBMS_XPLAN.DISPLAY`, 트리와 텍스트 보기. SELECT 외 DML도 지원 | P0 | M1 |
| PLAN-02 | 실제 계획: 실행한 문장의 `sql_id`·child로 `DBMS_XPLAN.DISPLAY_CURSOR(..., 'ALLSTATS LAST')` | P0 | M1 |
| PLAN-03 | 계획 트리에서 위험 단계(Full Table Scan, Index Full Scan, Cartesian join)와 예상·실제 행 수 차이가 큰 단계 강조, 조건(predicate) 표시 | P1 | M2 |
| PLAN-04 | 두 계획 비교(plan_hash_value 차이 표시) | P2 | 이후 |
| PLAN-05 | `DISPLAY_CURSOR` 형식 옵션 UI: 수준(BASIC/TYPICAL/SERIAL/ALL/ADVANCED/ADAPTIVE), 통계(IOSTATS/MEMSTATS/ALLSTATS, LAST), 항목 켜기/끄기(ROWS, BYTES, COST, PARTITION, PARALLEL, PEEKED_BINDS, PREDICATE, PROJECTION, OUTLINE, ALIAS, REMOTE) | P1 | M2 |
| PLAN-06 | 계획 분석 묶음 저장: SQL·계획·관련 객체 통계를 한 파일로 저장해 오프라인 분석·공유 | P2 | 이후 |

### 6.7 스키마 브라우저 (SCH)
| ID | 요구사항 | 우선 | 단계 |
|---|---|---|---|
| SCH-01 | 스키마별 객체 트리: 테이블, 뷰, 머티리얼라이즈드 뷰, 인덱스, 시퀀스, 시노님, 프로시저·함수·패키지, 트리거, 타입, DB 링크, 스케줄러 잡, 디렉터리, 휴지통. 26ai 객체(Duality View, Domain, Property Graph) 포함 | P0 | M1 |
| SCH-02 | 권한에 따라 `dba_*`/`all_*`/`user_*` 뷰를 자동 선택 | P0 | M1 |
| SCH-03 | 객체 상세: 컬럼(도메인·주석·가상·identity·invisible), 제약, 인덱스, 파티션, 권한, 의존 관계, 통계, 크기 | P0 | M1 |
| SCH-04 | DDL 보기: `DBMS_METADATA.GET_DDL`(서버가 지원하면 `DBMS_DEVELOPER.GET_METADATA` 우선 검토). 저장 속성 표시 여부 옵션 | P0 | M1 |
| SCH-05 | 검색·필터(이름 패턴, 객체 유형, INVALID만), 접속별 즐겨찾기 | P0 | M1 |
| SCH-06 | INVALID 객체 일괄 컴파일, 휴지통 Purge·Flashback | P1 | M2 |
| SCH-07 | 객체 끌어다 놓기로 에디터에 이름·SELECT문·컬럼 목록 삽입 | P1 | M1 |
| SCH-08 | 생성·변경 마법사(테이블, 인덱스, 시퀀스, 시노님, 권한) — 생성할 DDL을 미리 보여 준다 | P2 | 이후 |
| SCH-09 | 객체 검색: 이름·유형·크기·생성일·변경일 조건, 참조하는/참조되는 객체(Orange Description Tool 대응) | P1 | M2 |
| SCH-10 | DDL 일괄 추출: 스키마·객체 유형·이름 필터, 스키마·테이블스페이스 이름 바꾸기, 저장 속성 포함 여부(Orange Script Generation 대응) | P2 | M3 |
| SCH-11 | DB 정보: 인스턴스·DB·버전·옵션, 파라미터 조회(권한 있으면 변경, 변경 DDL 미리 보기), NLS 설정 | P1 | M2 |

### 6.8 PL/SQL 도구 (PLSQL)
| ID | 요구사항 | 우선 | 단계 |
|---|---|---|---|
| PLSQL-01 | 객체 소스 열기·편집·저장(=컴파일), 패키지 명세/본문 전환 | P0 | M2 |
| PLSQL-02 | 컴파일 오류·경고(`PLSQL_WARNINGS`) 목록, 클릭 시 위치로 이동 | P0 | M2 |
| PLSQL-03 | 실행 대화상자: 인자 입력 폼 생성(`all_arguments`), 결과·OUT 값·REF CURSOR 결과 표시 | P0 | M2 |
| PLSQL-04 | 저장 전 충돌 확인: 편집 시작 후 서버 소스가 바뀌었으면(`last_ddl_time`) 차이를 보여 준다 | P0 | M2 |
| PLSQL-05 | 다른 세션이 사용 중인 객체를 컴파일하려 하면 경고(라이브러리 캐시 잠금 대기 방지) | P1 | M2 |
| PLSQL-06 | 디버거: 중단점, 단계 실행, 변수·레코드·컬렉션 보기, 호출 스택. 현재 범위 밖(D-02), 필요해지면 방식 결정 | P2 | 이후 |
| PLSQL-07 | 계층 프로파일러(`DBMS_HPROF`) 실행·결과 보기 | P2 | 이후 |

### 6.9 모니터링 (SESS, SQLM)
| ID | 요구사항 | 우선 | 단계 |
|---|---|---|---|
| SESS-01 | 세션 목록: 사용자·프로그램·모듈·상태·대기 이벤트·SQL ID·경과 시간. 필터(활성만, 백그라운드 제외, 사용자, 모듈), 자동 새로고침(간격 지정) | P0 | M2 |
| SESS-02 | 블로킹 트리: `blocking_session`·`final_blocking_session` 기반. RAC는 `gv$` + `inst_id` | P0 | M2 |
| SESS-03 | 세션 상세: 현재·직전 SQL 전문, 실제 실행 계획, 트랜잭션(undo 사용량), 잠금 대상 객체, 세션 통계 | P0 | M2 |
| SESS-04 | 세션 Kill(`ALTER SYSTEM KILL SESSION ... IMMEDIATE`)과 Disconnect. 확인 대화상자에 대상 세션 정보 표시 | P0 | M2 |
| SESS-05 | SQL 트레이스 켜기/끄기(`DBMS_MONITOR`, waits·binds 선택), 트레이스 파일 위치(`v$diag_info`) 표시 | P1 | M2 |
| SESS-06 | 델타 모드(새로고침 사이 증가량), 사용자 지정 통계 열 | P2 | 이후 |
| SQLM-01 | Top SQL 검색: 공유 풀(`v$sql`)에서 소요 시간·읽은 블록·실행 횟수·텍스트 조건으로 찾기. Orange의 사전 정의 구간(Critical/OLTP/Batch 등) 제공 | P0 | M2 |
| SQLM-02 | 결과에서 SQL 전문, 실제 계획, 바인드 캡처(`v$sql_bind_capture`) 보기 | P0 | M2 |
| SQLM-03 | ASH·AWR·실시간 SQL Monitor 화면 — **라이선스 보호장치(6.10 SAFE-07)를 통과할 때만** | P2 | 이후 |

### 6.10 안전장치 (SAFE)
| ID | 요구사항 | 우선 | 단계 |
|---|---|---|---|
| SAFE-01 | 환경 라벨: 운영 접속은 창 테두리·탭·상태바를 빨간색으로 표시 | P0 | M1 |
| SAFE-02 | 읽기 전용 프로필: DML·DDL·PL/SQL 실행을 파서로 판별해 막고, 세션은 `SET TRANSACTION READ ONLY`로 연다 | P0 | M1 |
| SAFE-03 | 운영 접속에서 DML·DDL 실행 시 확인 대화상자. WHERE 없는 UPDATE·DELETE는 모든 접속에서 경고 | P0 | M1 |
| SAFE-04 | 자동 커밋 기본 끔. 미커밋 상태로 창·앱 종료 시 커밋/롤백/취소 선택 | P0 | M1 |
| SAFE-05 | 서버 추적 정보: `module`·`action`·`client_identifier`에 도구명과 OS 사용자를 설정해 서버 감사에서 식별되게 한다 | P0 | M1 |
| SAFE-06 | 자격 증명 저장: Windows는 DPAPI(현재 사용자) 또는 자격 증명 관리자, macOS는 키체인. 평문 저장 금지. "저장 안 함" 선택 가능 | P0 | M1 |
| SAFE-07 | 라이선스 팩 보호장치: 접속마다 Diagnostics/Tuning Pack 보유 여부를 선언(기본 "없음"). 선언이 없거나 서버 `control_management_pack_access`가 꺼져 있으면 ASH·`DBA_HIST_*`·`V$SQL_MONITOR`·`DBMS_SQLTUNE` 쿼리를 **도구가 자동으로 보내지 않는다** | P0 | M2 |
| SAFE-08 | 위험 명령(DROP, TRUNCATE, `ALTER SYSTEM`) 실행 전 대상 이름을 다시 입력하게 하는 옵션 | P1 | M2 |

### 6.11 내보내기·가져오기 (EXP)
| ID | 요구사항 | 우선 | 단계 |
|---|---|---|---|
| EXP-01 | 결과·테이블을 CSV, TSV, Excel(xlsx), JSON, INSERT문으로 내보내기. 행 수 제한 없이 스트리밍 | P0 | M2 |
| EXP-02 | CSV·Excel 가져오기(컬럼 매핑, 타입 변환 미리보기, 배치 크기 지정) | P1 | M2 |

### 6.12 앱 공통 (APP)
| ID | 요구사항 | 우선 | 단계 |
|---|---|---|---|
| APP-01 | 도킹 창 레이아웃(도구 창 끌어 배치, 저장·복원), 탭 분할 | P0 | M1 |
| APP-02 | 밝은·어두운 테마, OS 설정 따르기, 에디터 글꼴·크기 지정, HiDPI(배율 100~250%) | P0 | M1 |
| APP-03 | 한국어·영어 UI | P0 | M3 |
| APP-04 | 명령 팔레트(모든 명령을 이름으로 검색·실행) | P1 | M1 |
| APP-05 | 자동 업데이트(서명 검증), 오프라인 설치 파일 별도 제공 | P0 | M3 |
| APP-06 | 충돌 보고: 로컬에 덤프와 로그 저장, 사용자가 동의할 때만 전송 | P0 | M3 |
| APP-07 | AI 보조(자연어→SQL, 실행 계획 설명): 사용자 키 방식, 로컬 모델 우선, 운영 접속은 기본 차단, 전송할 메타데이터를 미리 보여 준다 | P2 | 이후 |
| APP-08 | 단축키 사용자 정의, 설정 내보내기·가져오기 | P1 | M2 |
| APP-09 | **AI 확장 지점(구현은 이후, 구조는 1.0에 포함)**: (1) 접속별 메타데이터 제공 인터페이스(스키마·컬럼·주석·통계를 자동완성 캐시에서 읽기), (2) 에디터 조작 인터페이스(현재 문장·선택 영역 읽기, 제안 SQL 삽입·차이 미리보기), (3) 실행 정책 관문(AI가 만든 SQL도 SAFE-02·03·07을 똑같이 통과), (4) 도킹 가능한 패널 슬롯과 명령 등록, (5) 외부 전송 동의·전송 내용 미리보기 훅. 1.0에서는 이 인터페이스를 자동완성·스키마 브라우저가 실제로 쓰게 해서 검증한다 | P1 | M2 |

### 6.13 1.0 이후 후보 (Orange 7·8 대비)
| 후보 | Orange 대응 도구 | 판단 |
|---|---|---|
| 스키마 비교(객체 속성·DDL 차이, 동기화 스크립트) | 8.0 Object Compare | 1.0 직후 1순위. 데이터 비교까지 하면 Orange보다 앞선다 |
| ER 다이어그램(역공학, 관계선, 내보내기) | ER-Viewer | 후보 |
| 쿼리 빌더 | Query Builder | 우선순위 낮음 |
| SQL ↔ 프로그램 코드 변환(Java, C#, Python, 문자열 이어 붙이기 제거) | SQL 코드 변환 | 후보. 대상 언어는 사용자 조사로 결정 |
| SELECT/INSERT/UPDATE문 생성 | Make SQL | SCH-07 확장으로 흡수 |
| 테스트 데이터 생성 | Data Generation | 후보 |
| 인스턴스·대기 이벤트 차트 | Instance / Wait Event Monitor | 후보(무료 V$ 뷰 기반) |
| 상태 점검 보고서 | Health Check | 후보 |
| 공간·보안·통계 관리 GUI | Space / Security / Stats Manager | 후보. DDL 미리 보기 원칙 |
| ASH·AWR 분석 | ASH Monitor, AWR Manager | SAFE-07 통과 시에만 |
| 트레이스 파일 분석 | Trace Tool, Plan Tool 실시간 트레이스 | 서버 파일 접근 방식 검토 후 결정 |
| 민감 컬럼 표시·가림 | 8.0 Sensitive Column | 후보. Orange는 표시만 하므로 마스킹까지 검토 |
| 비목표 | Reorg, Log Miner, Data Quality, Export/Import(Data Pump GUI), Network Configuration | 범위 밖 |

---

## 7. 비기능 요구 사항 (NFR)

### 7.1 지원 매트릭스
| 구분 | 지원 |
|---|---|
| 클라이언트 OS | Windows 10/11 x64, macOS 14.7 이상 ARM64(Apple Silicon). Intel Mac과 Windows ARM64는 비보증 |
| Oracle 서버 | **19c, 23ai/26ai(23.26.x), Autonomous Database(Serverless)만 지원** — Oracle 장기 지원(LTR) 버전과 ADB (D-06) |
| 미지원 서버 | 11.2·12c·18c(나중에 검토해서 추가, 11.3), 21c(지원 안 함). 접속 직후 버전을 확인해 지원 버전이 아니면 서버 버전과 이유를 안내하고 접속을 끝낸다 |
| 구성 | 단일 인스턴스, RAC(SCAN), Data Guard 대기 DB(읽기 전용), CDB/PDB |

### 7.2 성능 예산
기준 장비: Windows 11, 개발 PC(이 저장소 개발 장비), macOS는 M1 이상. 측정은 자동화해 CI에서 매 빌드 기록한다.

| 항목 | 목표 | 현재 기준선 (2026-10-19 실측) |
|---|---|---|
| 앱 시작 → 메인 창 입력 가능 (두 번째 실행 이후) | **1.5 s 이하** | 빈 창 약 1.8~1.9 s (.NET 10 + Avalonia 12, Release, ReadyToRun) — M0에서 줄일 방법 확인 |
| 최초 실행(디스크 캐시 없음) | 4 s 이하 | 10~15 s (백신 검사 포함 추정) |
| 유휴 메모리(접속 1개, 탭 1개) | 전용 메모리 150 MB 이하 | 빈 창 142~153 MB |
| 접속 3개 + 10만 행 결과 3개 | 400 MB 이하 | 미측정 |
| 키 입력 → 화면 반영 (1만 줄 파일) | 16 ms 이하 (p95) | 미측정 |
| 자동완성 목록 표시 (딕셔너리 캐시 후) | 150 ms 이하 | 미측정 |
| 문법 검사 반영 (1만 줄 파일, 입력 멈춘 뒤) | 300 ms 이하 | 미측정 |
| 포맷 (2,000줄) | 500 ms 이하 | 미측정 |
| 첫 행 표시 (서버가 첫 묶음을 돌려준 뒤) | 100 ms 이하 | 미측정 |
| 100만 행 그리드 스크롤 | 60 fps 유지, 추가 메모리 행당 200 bytes 이하(가져온 행 기준) | 미측정 |
| 실행 취소 반응 | 1 s 이하 | 미측정 |
| 설치 파일 크기 | 80 MB 이하 | 게시 폴더 235 MB(비압축, ReadyToRun) |

UI 스레드에서 DB 호출을 하지 않는다. 모든 DB 작업은 취소 가능해야 한다.

### 7.3 신뢰성
- 비정상 종료 시 편집 중이던 모든 탭 내용을 복구한다(자동 저장 간격 10 s 이하).
- 네트워크 끊김·서버 재시작 후에도 앱이 멈추지 않고 해당 접속만 "끊김" 상태가 된다.
- 결과 그리드 편집으로 의도하지 않은 행이 바뀌는 사고 0건(6.5.1 규칙으로 보장, 자동 테스트로 검증).

### 7.4 보안
- 자격 증명은 OS 보안 저장소에만 둔다(SAFE-06). 로그·이력·충돌 보고에 비밀번호와 바인드 값을 남기지 않는다(바인드 값 기록은 사용자 선택).
- TLS 1.2 이상, `SSL_SERVER_DN_MATCH` 기본 켬. 약한 암호(3DES, MD5 서명 지갑) 사용 시 경고.
- 원격 전송(업데이트 확인 제외)은 모두 사용자 동의 후. 기본값은 전송 안 함.
- 배포 파일은 Windows 코드 서명, macOS 서명·공증(notarization).
- 회사 배포용으로 설정·접속 목록을 관리자가 미리 넣고 잠글 수 있게 한다(M3 이후).

### 7.5 사용성·접근성
- 모든 기능에 키보드로 접근할 수 있다. 화면 낭독기(Windows 내레이터, macOS VoiceOver)로 메뉴·대화상자·그리드 셀을 읽을 수 있다.
- 오류 메시지는 ORA 번호와 함께 원인과 다음 행동을 한 줄로 보여 준다(예: ORA-00942 → 권한 또는 스키마 확인).

### 7.6 유지보수성·품질
- 딕셔너리 SQL은 코드에 흩어 두지 않고 "버전·권한·RAC 여부"로 고르는 SQL 카탈로그로 관리한다.
- Core 계층 단위 테스트 커버리지 80% 이상. 실제 DB 통합 테스트(19c, 26ai 컨테이너)를 CI에서 돌린다.
- 파서·포매터는 공개 SQL 예제 1,000개 이상 회귀 테스트(포맷 전후 토큰 동일성 포함).

---

## 8. 제약 사항

| ID | 제약 | 근거·영향 |
|---|---|---|
| C-01 | **그리드 편집은 PK(또는 NOT NULL 유니크 키) 전체 컬럼이나 ROWID가 결과에 있을 때만 허용한다.** 없으면 읽기 전용 | 행을 하나로 특정하지 못하면 여러 행이 바뀌는 사고가 난다. 6.5.1 |
| C-02 | 드라이버는 ODP.NET Core Managed만 쓴다. Oracle Client·Instant Client를 요구하지 않는다 | 설치 0이 핵심 가치. Unmanaged ODP.NET은 26ai에서 deprecated |
| C-03 | 1.0 지원 서버는 19c, 23ai/26ai, ADB뿐이다(D-06) | 11.2·12c·18c·21c는 접속을 막고 안내한다. 도구 SQL은 19c 문법·뷰 기준 한 벌에 23ai/26ai 추가분만 둔다. 단 11.2·12c·18c를 나중에 추가할 수 있도록 SQL 카탈로그는 버전 키 구조로 만든다(11.3) |
| C-04 | Managed 드라이버로 안 되는 것: RADIUS, Windows 외 OS 인증, local auto-login 지갑, macOS의 NTS | 해당 인증 고객은 지원 불가로 안내 |
| C-05 | 서버에 객체(스키마·테이블·패키지)를 설치하지 않는다. PL/SQL 체크아웃 잠금 같은 기능은 서버 객체 없이 구현하거나 하지 않는다 | 운영 DB 변경 승인 부담 제거 |
| C-06 | ASH, `DBA_HIST_*`(무료 6종 제외), `V$SQL_MONITOR`, `DBMS_SQLTUNE`, `DBMS_SQL_MONITOR`, `DBMS_WORKLOAD_REPOSITORY`는 사용자가 팩 보유를 선언한 접속에서만 호출한다 | Diagnostics/Tuning Pack 라이선스 위반 방지 |
| C-07 | Autonomous Database에서는 SYSDBA, `ALTER SYSTEM` 대부분, JDWP 역접속 디버깅을 쓸 수 없다 | 접속 시 ADB를 감지해 해당 기능을 숨긴다 |
| C-08 | .NET 10(LTS, 2028-11까지 지원)과 Avalonia 12를 쓴다 | .NET 8은 2026-11-10 지원 종료. 2026-10-19 전환(TRD T-D01) |
| C-09 | Orange의 코드·리소스·문구를 복사하지 않는다. 역분석 결과는 기능 개념과 Oracle 공개 뷰 사용 방식 참고에만 쓴다 | `docs/orange-analysis.md` 부록 B |
| C-10 | macOS 배포는 Apple Developer ID 서명과 공증이 필요하다 | 미서명 앱은 Gatekeeper가 막는다 |

---

## 9. 수락 기준

### 9.1 M0 기술 검증
- [ ] Oracle 19c, 26ai(Free 컨테이너)와 ADB(지갑 있음·없음)에 Windows와 macOS에서 각각 접속하고 `ServerProbe` 결과가 맞다.
- [ ] 후보 에디터 컴포넌트로 1만 줄 PL/SQL 패키지를 열어 키 입력 반영 16 ms 이하(p95)를 측정했다.
- [ ] 후보 그리드로 100만 행을 스크롤하며 60 fps, 메모리 증가량을 측정했다.
- [ ] 메인 창 시작 1.5 s 이하를 달성하는 방법(트리밍, ReadyToRun, 지연 로딩 등)을 찾았거나, 불가능하면 대안 목표를 근거와 함께 제시했다.
- [ ] macOS ARM64 앱 번들을 서명·공증해 다른 Mac에서 경고 없이 실행했다.

### 9.2 M1 알파
- [ ] 7.1의 정식 지원 서버 각각에 EZConnect, TNS 별칭, 지갑 방식으로 접속된다.
- [ ] 지원하지 않는 버전(11g·12c·18c·21c)의 서버에 접속하면 서버 버전과 미지원 이유가 표시되고 접속이 끝난다(버전 판별 단위 테스트 포함).
- [ ] 문법 오류가 있는 SQL을 입력하면 300 ms 안에 오류 위치에 밑줄이 생긴다.
- [ ] `SELECT e. FROM emp e`에서 `e.` 입력 시 EMP 컬럼 목록이 150 ms 안에 뜬다.
- [ ] 포매터를 회귀 예제 전체에 돌려 포맷 전후 토큰 열이 모두 같다(FMT-11).
- [ ] 키워드 대소문자·들여쓰기 옵션을 바꾸면 결과가 그 옵션대로 바뀐다.
- [ ] 실행 중인 10분짜리 쿼리를 취소하면 1 s 안에 실행 상태가 풀린다.
- [ ] 100만 행 결과를 처음부터 끝까지 스크롤해도 앱이 멈추지 않는다.
- [ ] 운영 라벨 접속에서 UPDATE 실행 시 확인 대화상자가 뜨고, 읽기 전용 프로필에서는 실행이 막힌다.
- [ ] 비밀번호가 설정 파일·로그 어디에도 평문으로 없다(자동 검사).
- [ ] 7.2 성능 예산의 "시작", "유휴 메모리", "키 입력" 항목을 충족한다.

### 9.3 M2 베타
- [ ] **PK도 ROWID도 없는 결과 그리드는 편집할 수 없고, 이유와 해결 방법이 표시된다.**
- [ ] **PK가 있는 테이블의 결과를 편집하면 생성 DML이 `WHERE <PK> = :바인드` 형태다. ROWID만 있으면 `WHERE ROWID = :rid` 형태다.**
- [ ] **집계 결과·DISTINCT 결과는 편집할 수 없다. 조인 결과는 대상 테이블의 PK나 ROWID가 없으면 편집할 수 없고, 있으면 대상 테이블 컬럼만 편집된다.**
- [ ] **영향 행 수가 0 또는 2 이상이 되도록 만든 시험(동시 삭제, 중복 키)에서 해당 변경이 반영되지 않고 알림이 뜬다.**
- [ ] 패키지 본문을 고쳐 저장하면 컴파일되고, 오류를 클릭하면 해당 줄로 이동한다.
- [ ] 다른 세션이 잠금을 잡은 상황에서 세션 모니터의 블로킹 트리가 보유자와 대기자를 보여 주고, Kill로 풀 수 있다.
- [ ] SQL Monitor에서 "1 s 이상, 1만 블록 이상" 조건으로 찾은 SQL의 실제 실행 계획을 볼 수 있다.
- [ ] 팩 "없음"으로 선언한 접속에서 앱을 1시간 사용하는 동안 서버 SQL 트레이스에 C-06 객체 호출이 0건이다.
- [ ] 포매터 옵션 전체(FMT-02~07)를 프로필로 저장해 다른 PC에서 가져오면 같은 결과가 나온다.
- [ ] 10만 행 테이블을 xlsx로 내보낸 파일이 Excel에서 열리고 행 수·값이 원본과 같다.

### 9.4 M3 1.0
- [ ] APP-09 인터페이스만 써서 만든 시험용 패널(예: 선택한 SQL을 대문자로 바꿔 다시 넣는 패널)이 Core 수정 없이 동작하고, 그 패널이 넣은 UPDATE문도 운영 접속 확인 대화상자를 거친다.
- [ ] Windows 설치 파일과 macOS dmg가 서명돼 있고 자동 업데이트가 서명을 검증한다.
- [ ] 한국어·영어 UI 전환 시 누락 문자열이 0개다(자동 검사).
- [ ] 화면 낭독기로 접속·쿼리 실행·결과 읽기를 끝까지 할 수 있다.
- [ ] 7.2 성능 예산 전 항목을 두 OS에서 충족한다.

---

## 10. 성공 지표 (1.0 출시 후 6개월)
| 지표 | 목표 |
|---|---|
| 내부 Orange 5 사용자 전환율 | 80% 이상 |
| 주간 활성 사용자 중 macOS 비율 | 측정해 기록(목표 설정은 첫 달 이후) |
| 충돌 없는 세션 비율 | 99.5% 이상 |
| 그리드 편집 사고(의도치 않은 행 변경) | 0건 |
| 시작 시간·유휴 메모리 회귀 | 릴리스마다 7.2 예산 안 |

---

## 11. 위험과 미결 사항

### 11.1 위험
| 위험 | 영향 | 대응 |
|---|---|---|
| Oracle SQL·PL/SQL 문법이 방대해 파서 완성도가 낮을 수 있다 | 자동완성·포매터 품질 저하 | M0에서 기존 문법(ANTLR grammars-v4 PL/SQL 등)과 직접 작성 파서를 비교. 파싱 실패 구간은 기능을 끄고 색칠만 유지하는 단계적 축소 |
| Avalonia 기본 그리드가 100만 행에 부족할 수 있다 | 성능 예산 미달 | M0에서 측정 후 필요 시 자체 가상화 그리드 작성 |
| 빈 창 시작이 이미 2.3 s다 | 시작 1.5 s 목표 미달 | 트리밍, 지연 로딩, 글꼴 로딩 최적화 검증. NativeAOT 가능성 조사 |
| 사내에 지원하지 않는 버전(19c 미만, 21c)의 DB가 남아 있을 수 있다 | 해당 DB는 이 도구로 접속 불가 | 1.0은 미지원(D-06). 배포 전에 사내 DB 버전 목록을 확인해, 11.2·12c·18c가 많으면 1.0 이후 추가 우선순위를 올린다. 그동안은 기존 도구를 유지한다 |
| 경쟁 신생 도구(Plyxon, Jam) | 차별화 약화 | 성능 KPI 공개, Orange 사용자 이전 기능(단축키·접속 가져오기) |

### 11.2 결정 사항
| ID | 질문 | 결정 / 제안 | 상태 |
|---|---|---|---|
| D-01 | 판매·배포 모델 | **사내 전용** | 결정 (2026-10-19) |
| D-02 | PL/SQL 디버거 | **지금은 필요 없음.** 범위 밖, 필요해지면 방식(JDWP / `DBMS_DEBUG`) 재검토 | 결정 (2026-10-19) |
| D-03 | Tibero 지원 | **지원 안 함** | 결정 (2026-10-19) |
| D-04 | AI | **챗봇으로 쿼리 작성까지 가는 것이 목표지만 지금은 구현하지 않는다.** 1.0 구조에 확장 지점(APP-09)을 둔다 | 결정 (2026-10-19) |
| D-05 | 에디터 컴포넌트 | AvaloniaEdit 12.0.0 + 자체 L1 증분 렉서 (decisions D-007) | 결정 |
| D-06 | 지원 서버 버전 | **1.0은 최신 DB만: 19c와 23ai/26ai, ADB.** 11.2·12c·18c는 **나중에 검토해서 추가**한다(추가 비용 판단은 11.3). 21c는 지원하지 않는다. 지원 버전이 아니면 버전과 이유를 안내하고 접속을 끝낸다 | 결정 (2026-10-19) |

### 11.3 11.2·12c·18c 접속 지원 비용 (나중에 추가할 때의 판단 자료)
- **드라이버가 붙기만 하면 추가 코드는 적다.** 이 도구가 쓰는 핵심 딕셔너리·성능 뷰(`v$session`의 `sql_id`·`blocking_session`·`final_blocking_session`, `v$sql`, `DBMS_XPLAN.DISPLAY_CURSOR`, `DBMS_MONITOR`, `DBMS_METADATA`)는 11.2에 이미 있다. 다른 점은 CDB/PDB, identity·invisible 컬럼 같은 12c 이후 속성, 23ai 이후 타입·객체 정도이고, 이는 "버전별 SQL 카탈로그"(7.6)와 `ServerCapabilities` 기능 끄기로 흡수한다. 도구 자체 쿼리에서 12c 문법(`FETCH FIRST`)을 쓰지 않는 규칙이 필요하다. 추정 추가량: 딕셔너리 SQL의 10~15%에 별도 변형, 화면 코드 변경은 거의 없음.
- **비용이 커지는 경우는 드라이버다.** 지금 쓰는 ODP.NET 23은 공식 최소 서버가 19c다. 11.2에 실제로 붙지 않으면 구버전 드라이버를 함께 실어야 하는데, 두 버전의 어셈블리 이름이 같아서 한 프로세스에 넣으려면 격리 로딩(별도 AssemblyLoadContext 또는 별도 프로세스)과 드라이버 추상화 계층이 필요하다. 이 경우 접속 계층 작업량이 대략 2배가 되고 시험 매트릭스도 늘어난다.
- **결정**: 1.0에서는 지원하지 않고 나중에 검토해서 추가한다(D-06). 추가할 때는 먼저 현재 드라이버로 실제 접속을 시험하고, 붙지 않으면 구버전 드라이버 격리 비용을 따져 결정한다.
- **지금 지켜야 할 것**: 나중에 추가할 수 있도록 (1) 딕셔너리 SQL을 버전 키로 고르는 카탈로그 구조(7.6)를 처음부터 쓰고, (2) 접속 계층은 드라이버를 인터페이스 뒤에 두고, (3) 지원 버전 판별은 설정 한 곳에서 바꿀 수 있게 한다. 1.0에서 구버전용 SQL 변형은 만들지 않는다.

---

## 12. 용어 정의
| 용어 | 뜻 |
|---|---|
| Thin(Managed) 드라이버 | Oracle Client 없이 순수 .NET 코드로 Oracle 네트워크 프로토콜을 직접 말하는 드라이버 |
| Thick 클라이언트 | `oci.dll` 등 Oracle Client 라이브러리를 거쳐 접속하는 방식 |
| EZConnect Plus | `host:port/service?옵션` 형태의 접속 문자열 형식 |
| 지갑(Wallet) | TLS 인증서·자격 증명을 담은 파일 묶음. ADB 접속에 쓴다 |
| ADB | Oracle Autonomous Database (클라우드 관리형) |
| CDB/PDB | 멀티테넌트 구조의 컨테이너 DB와 그 안의 플러그인 DB |
| ROWID | Oracle 행의 물리 주소. 테이블 안에서 행을 하나로 특정한다 |
| 키 보존(key-preserved) | 조인 결과의 각 행이 특정 테이블의 행 하나에 대응하는 성질 |
| Diagnostics/Tuning Pack | ASH·AWR·SQL Monitor·SQL Tuning Advisor 등을 쓰려면 별도로 사야 하는 Oracle EE 옵션 |
| Fetch As Need | 화면에 필요한 만큼만 서버에서 행을 가져오는 방식 |
| 26ai | Oracle AI Database 26ai. 버전 문자열은 23.26.x |

---

## 13. 참고 자료
- 내부: `docs/orange-analysis.md` (Orange 5.0 구조·도구별 SQL·현대화 표)
- Oracle 지원 정책: https://www.oracle.com/tools/sqldeveloper/support.html, https://endoflife.date/oracle-database
- 26ai 발표: https://blogs.oracle.com/database/post/oracle-announces-oracle-ai-database-26ai
- ODP.NET 23 시스템 요구사항: https://docs.oracle.com/en/database/oracle/oracle-database/23/odpnt/InstallSystemRequirements.html
- ODP.NET 접속 기능: https://docs.oracle.com/en/database/oracle/oracle-database/23/odpnt/featConnecting.html
- 라이선스 정보(팩 범위): https://docs.oracle.com/en/database/oracle/oracle-database/19/dblic/Licensing-Information.html
- DBMS_DEBUG_JDWP: https://docs.oracle.com/en/database/oracle/oracle-database/26/arpls/DBMS_DEBUG_JDWP.html
- ADB TLS 접속(.NET): https://docs.oracle.com/en-us/iaas/autonomous-database-shared/doc/connect-microsoft-net-tls.html
- PL/SQL Developer 17: https://www.allroundautomations.com/?p=5718
- Toad Mac Intel 경고: https://support.quest.com/kb/4382832/macos-warning-about-intel-based-apps-in-toad-for-oracle-2026-r1
- Orange 7 설치 가이드: https://www.warevalley.com/onlineupdate/manual/ko/7/OrangeforOracle_InstallGuide_kr.pdf
- SQL Developer 메모리 보고: https://forums.oracle.com/ords/apexds/post/extreme-memory-usage-in-sql-developer-3722
