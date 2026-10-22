# 제아이디어·나중에 할 일

## 기능 아이디어 (2026-10-20 수집)

- mybatis 파일을 그대로 읽고 태그를 실시간 해석해서 실제 수행된 쿼리를 생성하는 일련의 과정을 모두 구현
- mcp, cli 를 통해 LLM에서 오란다 제어
- 이 맥북을 밖에서 회사안의 win30, win144를 제어해야 하는데 제어가능한 방법이 win144 만 크롬 엑세스로 접속이  가능해.
- 업데이트 , 커밋 후 데이터 원복 기능
- 데이터 변경 추적 기능
- 한번에 하나의 DB가 아니라 여러 세션을 가지게 하자.
  - 워크스페이스 별로도 세션을 할당 가능하도록 하자.
- 튜닝을 위한 기능 여러 튜닝 방법을 사용한다. 
  - 예1)

```sql
ALTER SESSION SET    STATISTICS_LEVEL = ALL;

SELECT ID,
       TXT,
       TS
FROM   FT.BIG_T;


SELECT * FROM TABLE(DBMS_XPLAN.DISPLAY_CURSOR('', NULL, 'ALLSTATS LAST'));
```



처음 적은 세 가지(파티션 테이블 관리, 계산 컬럼 지정, 컬럼 순서 바꾸기)를 출발점으로 넓힌 목록이다.
PRD 6장에 이미 있는 기능은 뺐고, PRD 기능과 겹치거나 PRD 기능을 넓히는 항목은 괄호에 PRD 번호를 적었다.
공통 원칙은 PRD를 따른다.

- 서버에 보조 객체를 설치하지 않는다.
- 바꾸는 작업은 만들 DDL·DML을 먼저 보여 준다.
- Diagnostics/Tuning Pack이 필요한 뷰는 SAFE-07을 통과할 때만 쓴다.
- 운영 접속에서는 SAFE-03 확인을 거친다.

### 먼저 할 만한 기능

들이는 노력에 비해 효과가 큰 항목을 골라 추천한다.

1. 컬럼 순서 바꾸기: INVISIBLE→VISIBLE 방식이면 데이터를 옮기지 않으므로 구현과 실행이 모두 가볍다.
2. 내 세션이 남을 막고 있음 알림: 모니터링 연결(CONN-07)이 이미 있어 조회 하나로 흔한 운영 사고를 줄인다.
3. MyBatis 로그 SQL 재현: 바인드 창(EXEC-03)과 MyBatis 작업 공간(EDIT-09)이 이미 있어 붙이기 쉽고, 현장에서 매일 쓴다.
4. SQL 점검(lint): 파서와 오류 밑줄 표시(EDIT-03)가 이미 있어 규칙만 더하면 된다.
5. 플래시백 조회·복구: 잘못 고친 데이터를 되살리는 길을 도구 안에 둔다(G6).
6. 테이블 정의서 생성: 객체 상세 조회(SCH-03)를 그대로 쓰고 내보내기(EXP-01)만 더한다.
7. 변경 전 DDL 자동 보관·PL/SQL 소스 이력: 서버에 아무것도 설치하지 않고도 되돌릴 수 있게 한다.

### 1. 테이블 구조 편집

- 파티션 테이블 관리: 파티션 목록에 상한값(`high_value`)·행 수·크기·테이블스페이스·압축·마지막 통계 시각을 보인다.
  - ADD·DROP·TRUNCATE·SPLIT·MERGE·EXCHANGE·MOVE(ONLINE)를 대화상자로 고른다.
  - 전역 인덱스가 깨지지 않게 `UPDATE INDEXES`를 기본으로 넣는다.
  - 다음 기능도 넣는다.
    - 「다음 N개월 파티션 미리 만들기」
    - 인터벌 파티션이 자동으로 만든 `SYS_Pxxx` 이름을 규칙대로 바꾸기
    - 일반 테이블을 파티션 테이블로 바꾸기(12.2+ `ALTER TABLE … MODIFY PARTITION BY … ONLINE`)
  - 메모: `high_value`는 LONG이라 읽기 번거롭다.
    - 23ai 계열부터는 `HIGH_VALUE_CLOB`·`HIGH_VALUE_JSON` 컬럼을 쓴다(실제 서버에서 확인).
    - 파티셔닝은 EE 유료 옵션이므로 `v$option`으로 사용 가능 여부를 보고 메뉴를 켠다.
- 파티션 프루닝 확인: 실행 계획의 `Pstart`/`Pstop`을 읽어 「파티션 24개 중 1개만 읽음 / 전부 읽음」을 계획 트리에 표시한다(PLAN-03 확장).
- 계산(가상) 컬럼 지정: 식 편집기(자동완성·문법 검사)에 식을 쓰면 기존 행 20개로 결과를 바로 계산해 보여 준다(`SELECT 식 FROM t FETCH FIRST 20 ROWS ONLY`).
  - 결과 타입을 추정해 채운다.
  - 식에 쓰인 사용자 함수가 `DETERMINISTIC`인지, 식이 다른 가상 컬럼을 참조하지 않는지 미리 검사한다.
  - 가상 컬럼을 만든 뒤에는 「이 컬럼에 인덱스 만들기」·「파티션 키로 쓰기」를 이어서 제안한다.
  - 생성문은 `ALTER TABLE t ADD (c AS (식) VIRTUAL)`이다.
- 컬럼 순서 바꾸기: 컬럼 목록을 끌어다 놓아 순서를 정한다.
  - Oracle에는 순서를 바꾸는 문법이 없으므로 사용자가 다음 두 방식 중에서 고르게 한다.
  - ① 12c+ INVISIBLE→VISIBLE: 숨겼다 다시 보인 컬럼은 맨 뒤로 붙는다.
    - 새 순서에서 처음 달라지는 위치 뒤의 컬럼을 원하는 순서대로 숨겼다 다시 보이면 된다.
    - 메타데이터만 바뀌므로 큰 테이블도 바로 끝난다.
    - 외부·임시·클러스터 테이블에는 쓸 수 없다.
  - ② 재생성: `DBMS_REDEFINITION`(온라인, EE) 또는 CTAS + 이름 바꾸기.
    - 제약·인덱스·트리거·권한·주석·통계를 함께 옮기는 스크립트를 만든다.
  - 어느 방식이든 컬럼 목록 없이 쓴 `INSERT INTO t VALUES (…)`와 `SELECT *`를 쓰는 소스를 소스 검색(SCH-09)으로 찾아 경고한다.
- 테이블 구조 편집기 (SCH-08 확장): 테이블 상세의 컬럼 표에서 이름·타입·길이·NULL·기본값·주석을 바로 고친다.
  - 바뀐 내용은 ALTER 문 묶음으로 만들어 미리 보인 뒤 실행한다(DataGrip Modify Table 방식).
  - 위의 파티션·가상 컬럼·컬럼 순서 기능은 이 편집기 안에 넣는 편이 자연스럽다.
- 타입·길이 변경 사전 점검: 바꾸기 전에 실제 데이터로 바꿀 수 있는지 본다.
  - 길이를 줄이면 `MAX(LENGTHB(c))`로 넘치는 행을 보인다.
  - 숫자 정밀도를 줄이거나 타입을 바꾸려면 컬럼이 비어 있어야 하므로(ORA-01440, ORA-01439) 우회 스크립트(새 컬럼 추가 → 복사 → 교체)를 제안한다.
- BYTE→CHAR 길이 의미 점검: AL32UTF8 DB에서 `VARCHAR2(10 BYTE)`에는 한글이 3자만 들어간다.
  - BYTE 의미로 만든 컬럼 목록과 한글이 잘릴 위험을 보이고 `CHAR`로 바꾸는 문장을 만든다.
- 컬럼 변경 영향 분석: 컬럼 이름을 바꾸거나 지우기 전에 의존 객체(`dba_dependencies`), 그 이름을 쓰는 소스 줄, 뷰·인덱스·제약을 모아 「INVALID가 될 객체」 목록을 보인다.
- 테이블 복제: 구조만 또는 데이터 포함, 같은 스키마 또는 다른 스키마로, 제약·인덱스·주석·권한 포함 여부를 고른다.
  - 「백업 테이블 만들기」(`T_BAK_20261020`) 단축 메뉴도 둔다.
- 주석 일괄 편집: 테이블·컬럼 주석을 표에서 고치고, 엑셀에서 「컬럼명·주석」 두 열을 붙여 넣어 `COMMENT ON` 문을 한꺼번에 만든다.
- 시퀀스·identity 맞추기: 시퀀스 현재값이 `MAX(id)`보다 작으면 경고하고 맞춘다(18c+ `ALTER SEQUENCE … RESTART START WITH`, identity는 `ALTER TABLE … MODIFY … START WITH LIMIT VALUE`).
- 제약 조건 관리: ENABLE·DISABLE·VALIDATE·NOVALIDATE를 바꾼다.
  - 켜기에 실패하면 위반 행을 찾아 결과 그리드로 보인다.
  - 서버에 예외 테이블을 만들지 않도록 `EXCEPTIONS INTO` 대신 도구가 위반 행 조회문을 만든다.
- 공간 회수·재구성: 삭제가 많아 행 수에 비해 블록이 큰 테이블을 찾아 `ALTER TABLE … MOVE ONLINE`(12.2+)·`SHRINK SPACE`·인덱스 REBUILD를 제안한다.
  - 이 기능은 PRD 비목표(2.2)의 「Reorg」와 겹치므로, 넣으려면 범위를 먼저 정해야 한다.

### 2. 데이터 작업

- 플래시백 조회·복구: 결과 그리드에서 「N분 전 값 보기」를 누르면 같은 질의를 `AS OF TIMESTAMP`로 다시 실행해 지금 값과의 차이를 강조한다.
  - 잘못 고친 행을 골라 이전 값으로 되돌리는 UPDATE를 만든다.
  - `VERSIONS BETWEEN`으로 한 행의 변경 이력도 본다.
  - undo 보존 시간(`undo_retention`)이 지났거나 권한(`FLASHBACK`)이 없으면 이유를 알린다.
- 대량 DML 나눠 실행: 큰 UPDATE·DELETE를 키 범위나 `ROWID` 묶음으로 나눠 N건마다 커밋한다.
  - 진행률·남은 시간을 보인다.
  - 중간에 멈췄다가 이어서 할 수 있게 한다.
  - 이렇게 나눠 실행하면 운영에서 undo와 잠금이 크게 잡히지 않는다.
- 삭제 영향 미리보기: DELETE를 실행하기 전에 다음 값을 계산해 SAFE-03 확인 대화상자에 보인다.
  - 지울 행 수
  - `ON DELETE CASCADE`로 함께 지워질 자식 테이블별 행 수
  - 자식 행 때문에 실패할 FK(ORA-02292)
- 데이터 비교·동기화: 두 테이블(같은 접속 또는 다른 접속)을 PK 기준으로 비교해 추가·삭제·변경 행을 보이고 MERGE 스크립트를 만든다(PRD 6.13 「스키마 비교」 후보의 다음 단계).
- 접속 간 데이터 복사: DB 링크 없이 도구가 원본 접속에서 읽어 대상 접속에 배치로 INSERT한다.
  - 운영 코드 테이블을 개발 DB로 옮길 때 쓴다.
  - 타입 매핑, 행 수 확인, 실패 행 기록을 둔다.
- 테스트 데이터 생성 (PRD 6.13 후보): 컬럼 타입·길이·CHECK·FK를 읽어 맞는 값을 만든다.
  - FK 컬럼은 부모 테이블에 있는 값에서 고른다.
  - 한국어 이름·주소·전화번호·사업자등록번호 패턴을 기본으로 둔다.
- 코드 값 풀이 표시: FK·코드 컬럼 옆에 부모 테이블의 이름 컬럼을 회색으로 함께 보인다(`DEPT_ID 10 (영업부)`).
  - 공통 코드 테이블(`그룹코드 + 코드`)은 매핑을 한 번 지정해 둔다.
- 민감 컬럼 가림 (PRD 6.13 후보): 주민등록번호·전화·계좌 같은 컬럼을 이름 규칙이나 직접 지정으로 표시하고, 화면·복사·내보내기에서 가린다.
- 스키마 전체 값 검색: 「이 값이 어느 테이블에 있지?」에 답하도록 문자·숫자 컬럼 전체에서 값을 찾는다.
  - 큰 테이블 건너뛰기, 시간 제한, 운영 접속 차단을 기본으로 둔다.
- 결과 간단 차트: 숫자 열을 골라 막대·선 차트로 보인다.
  - 날짜별 건수 확인처럼 가벼운 용도로만 쓴다.

### 3. 쿼리 작성 보조

- MyBatis·로그 SQL 재현: MyBatis 로그(`==>  Preparing: …`, `==> Parameters: 1(Integer), abc(String)`)를 붙여 넣으면 `?`를 바인드로 바꾸고 바인드 창(EXEC-03)에 값과 타입을 채운다.
  - 매퍼 XML의 `#{param}`·`${param}`·`[[ORCA_RICH_MD:05c2825bb8f53f350ece2db6867ff86c:inline-html:%3Cif%3E]]`·`[[ORCA_RICH_MD:05c2825bb8f53f350ece2db6867ff86c:inline-html:%3Cforeach%3E]]`를 해석해 실행할 수 있는 SQL로 펼치는 데까지 가면 MyBatis 작업 공간(EDIT-09)과 이어진다.
- 바인드 ↔ 리터럴 변환 (EXEC-03 확장): 바인드를 지금 값으로 치환한 SQL을 복사하거나(메신저·문서 공유용), 반대로 리터럴을 바인드로 뽑아낸다.
- FK 기반 조인 완성: `JOIN` 뒤에서 FK로 이어진 테이블을 먼저 제안하고, 사용자가 고르면 `ON` 조건까지 채운다(DataGrip 방식, EDIT-04 확장).
- `SELECT *` 펼치기: 커서 아래의 `*`를 별칭이 붙은 컬럼 목록으로 바꾼다.
  - 컬럼 몇 개만 빼고 싶을 때 자주 쓴다.
- SQL 점검(lint): 실행 전에 흔한 실수를 EDIT-03과 같은 밑줄로 알린다.
  - `UPDATE t SET c = (SELECT …)`에 WHERE가 없어 짝이 없는 행이 NULL이 된다.
  - `ROWNUM <= n`과 `ORDER BY`가 같은 단계에 있어 정렬하기 전에 자른다.
  - `NOT IN (하위 쿼리)`의 컬럼이 NULL을 허용해 결과가 0건이 될 수 있다.
  - 조인 조건이 빠진 테이블이 있다(카테시안 곱).
  - `= NULL`, `!= NULL`을 썼다.
  - 문자 컬럼을 숫자와 비교한다(암시적 변환으로 인덱스를 못 쓰고 ORA-01722 위험).
  - 인덱스 컬럼에 함수를 씌웠다(`TO_CHAR(dt, 'YYYYMMDD') = …`).
  - 바깥 조인 `(+)`를 일부 조건에만 붙여 사실상 내부 조인이 됐다.
- ORA 오류 도우미: 오류가 나면 원인을 설명하고 바로 실행할 수 있는 다음 행동을 붙인다.
  - ORA-00001: 어느 제약·어느 컬럼에서 어떤 값이 겹쳤는지 찾는 질의
  - ORA-00904: 비슷한 컬럼 이름 제안
  - ORA-01722: 숫자로 바뀌지 않는 행 찾기(12.2+ `VALIDATE_CONVERSION`)
  - ORA-12899: 넘친 값의 실제 바이트 길이(한글)
  - ORA-02291, ORA-02292: 부모가 없는 값, 남아 있는 자식 행 찾기
  - ORA-00054: 잠금을 잡은 세션 보이기(SESS-02로 연결)
- 조인 문법 변환: Oracle `(+)` 조인과 ANSI `LEFT JOIN`을 파서로 서로 바꾼다.
  - 오래된 소스를 정리할 때 많이 쓴다.
- 리팩터링: 다음 기능을 둔다.
  - 하위 쿼리를 `WITH` 절로 빼기
  - 별칭 이름 바꾸기(문장 안의 모든 참조)
  - 인라인 뷰 펼치기
- 컬럼 주석 달기: SELECT 목록 각 줄 끝에 `-- 컬럼 주석`을 붙이거나, 컬럼 주석을 한글 별칭으로 단다.
- 두 SQL 비교: 탭 두 개나 실행 이력 두 건의 SQL을 포맷한 뒤 차이를 보인다.
- 반복 실행(watch): 질의를 N초마다 다시 실행해 바뀐 칸을 강조한다.
  - 배치 진행 상황을 확인할 때 쓴다.

### 4. PL/SQL

- 호출문 자동 생성: 프로시저·함수·패키지 멤버에서 이름 지정 표기(`p_id => :p_id`)로 쓴 호출 블록을 만든다.
  - 기본값이 있는 인자는 주석으로 둔다(PLSQL-03의 글 버전).
- 소스 변경 이력 (로컬): 컴파일할 때마다 직전 서버 소스를 로컬에 저장한다.
  - 저장한 소스로 버전 목록, 차이 보기, 되돌리기를 준다.
  - 서버에 아무것도 설치하지 않고 「누가 덮어썼다」 사고를 막는다.
- CRUD 패키지 생성: 테이블에서 INSERT·UPDATE·DELETE·조회 프로시저를 담은 패키지 초안을 만든다.
- PL/Scope 참조 찾기: 개발 DB에서 `plscope_settings = 'IDENTIFIERS:ALL, STATEMENTS:ALL'`로 컴파일한 객체는 `all_identifiers`·`all_statements`로 선언·참조·SQL 위치를 정확히 찾는다.
  - 객체를 다시 컴파일해야 하므로 운영 접속에서는 이 기능을 끈다.
- 스케줄러 잡 관리: 잡 목록에 다음·마지막 실행 시각, 실패 횟수, 실행 이력(`dba_scheduler_job_run_details`)과 오류 메시지를 보인다.
  - 잡의 즉시 실행·켜기·끄기를 한다.

### 5. 운영·튜닝 (가벼운 DBA 일)

- 내 세션이 남을 막고 있음 알림: 모니터링 연결(CONN-07)로 주기적으로 확인한다.
  - 내 미커밋 트랜잭션이 다른 세션을 막고 있으면 상태바를 빨갛게 바꾸고 막힌 세션 수를 보인다.
- 오래 열린 트랜잭션 알림: 미커밋 상태가 N분을 넘으면 알린다(EXEC-06 확장).
- 통계 관리: 오래된 통계(`stale_stats`), 통계가 없는 테이블, 잠긴 통계 목록을 보인다.
  - `DBMS_STATS`로 통계를 수집한다(DDL 미리보기).
  - 수집하기 전의 통계를 로컬에 보관해 되돌릴 수 있게 한다(PRD 6.13 「통계 관리」 후보).
- 인덱스 점검: 다음 항목을 찾는다.
  - FK 컬럼에 인덱스가 없어 잠금을 부르는 곳
  - 선두 컬럼이 겹치는 중복 인덱스
  - 쓰이지 않는 인덱스(12.2+ `DBA_INDEX_USAGE`, 라이선스 조건 확인)
- 새 인덱스 시험: 인덱스를 `INVISIBLE`로 만들고 내 세션에서만 `optimizer_use_invisible_indexes = TRUE`로 바꿔 계획을 비교한다(PLAN-04).
  - 다른 세션의 계획은 바뀌지 않는다.
- 테이블스페이스·세그먼트 사용량: 사용률과 큰 세그먼트 순위를 보인다.
  - 증가 추세는 AWR 대신 도구가 로컬에 쌓은 스냅샷으로 그린다.
- 권한 관리: 다음 기능을 둔다.
  - 사용자·롤별 권한 표
  - 「A와 같은 권한을 B에게」 GRANT 스크립트 생성
  - 객체에 누가 접근할 수 있는지 역조회
- 계정 상태: 잠긴 계정, 비밀번호 만료가 가까운 계정, 프로필 제한을 보인다.
- alert log 보기: `v$diag_alert_ext`에서 최근 ORA- 오류를 시간순으로 걸러 본다.

### 6. 문서·공유·안전

- 테이블 정의서 생성: 고른 테이블의 컬럼명·타입·NULL·PK·FK·기본값·주석과 인덱스를 엑셀·Markdown·HTML로 만든다.
  - 테이블 정의서는 SI 산출물로 자주 요구된다.
- ER 다이어그램 (PRD 6.13 후보): 고른 테이블과 FK로 이어진 테이블을 그린다.
  - 테이블 하나에서 「관계된 테이블 펼치기」로 시작하면 가볍게 쓸 수 있다.
- 결과를 Markdown 표로 복사: GRID-03의 복사 형식에 Markdown 표를 더한다.
  - 이슈·메신저·문서에 붙일 때 쓴다.
- 변경 전 DDL 자동 보관: DROP·ALTER·CREATE OR REPLACE를 실행하기 직전에 대상 객체의 DDL을 로컬 「변경 일지」에 저장한다.
  - 되돌릴 때 이 DDL을 바로 쓴다.
- 운영 반영 스크립트 묶음: 반영할 스크립트를 순서대로 묶는다.
  - 스크립트마다 되돌리기 스크립트를 짝짓는다.
  - 묶음을 실행하며 결과 로그를 남긴다.
  - 실패하면 그 지점에서 멈춘다(EXEC-10).
- 실행 기록 (로컬 감사): 운영 접속에서 실행한 DML·DDL을 시각·접속·영향 행 수와 함께 따로 남긴다.
  - 나중에 이 기록에서 「언제 무엇을 고쳤나」를 찾는다(EXEC-07보다 오래 보관).
- 긴 실행이 끝나면 OS 알림: N초 넘게 걸린 실행이 끝나면 OS 알림을 띄운다.
- 데이터 표준 용어 검사: 사내 표준 단어·용어 사전(엑셀)을 불러와 새 컬럼 이름과 타입이 표준에 맞는지 검사한다.
  - 한글 이름에서 영문 컬럼명을 조합해 제안한다.
  - 공공·SI 프로젝트에서 수요가 크다.

### 7. 버전별 새 기능 활용 (19c·23ai·26ai)

웹 조사(2026-10-20)로 고른 항목이다.
도구는 접속 직후 서버 점검(CONN-05)에서 읽은 버전·에디션으로 메뉴를 켜고 끈다.
꼬리표의 뜻은 다음과 같다.

- \[EE\]: Enterprise Edition 기능
- \[Exadata·ADB\]: Exadata·Autonomous에서만 동작
- \[확인\]: 라이선스나 버전을 실제 문서·서버로 다시 확인해야 함

#### 공통

- 버전별 문법 경고: 접속한 서버가 19c인데 23ai 문법(`IF NOT EXISTS`, `GROUP BY` 별칭, `FROM` 없는 SELECT, `VALUES` 생성자, BOOLEAN)을 쓰면 실행 전에 밑줄로 알린다.
  - 반대로 서버가 23ai 이상이면 도구가 만드는 DDL에 `IF [NOT] EXISTS`를 넣어 여러 번 실행해도 안전한 스크립트를 만든다.

#### 19c

- SQL 계획 고정(SPM) \[EE\]\[확인\]: 계획 화면에서 「이 계획 고정」을 누르면 `DBMS_SPM.LOAD_PLANS_FROM_CURSOR_CACHE`로 baseline을 만든다.
  - baseline 목록(`DBA_SQL_PLAN_BASELINES`)에서 켜기·끄기·고정·삭제를 한다.
  - 23ai의 Real-Time SPM이 자동으로 만든 baseline도 같은 목록에 표시한다.
- 잠금 대기 설정 표시: 19c `MAX_IDLE_BLOCKER_TIME`(잠금을 잡은 채 놀고 있는 세션을 끊는 시간)을 블로킹 트리와 「내 세션이 남을 막고 있음 알림」에 함께 보인다.
  - 「N분 뒤에 이 세션은 서버가 끊음」처럼 알린다.
- 실시간 통계 표시 \[Exadata·ADB\]: `DBA_TAB_STATISTICS.NOTES = 'STATS_ON_CONVENTIONAL_DML'`인 통계를 통계 관리 화면에서 구분해 보인다.
- 자동 인덱스·SQL 격리 보기 \[Exadata·ADB\]: `DBA_AUTO_INDEX_CONFIG`·`DBMS_AUTO_INDEX.REPORT_ACTIVITY`와 `DBA_SQL_QUARANTINE`을 읽기 전용으로 보인다.
  - 인덱스 점검에서 자동 인덱스를 따로 표시한다.
- 하이브리드 파티션 테이블 \[EE\]: 파티션 목록에서 DB 안의 파티션과 외부 파일 파티션을 구분해 보인다.
- 변경 불가·블록체인 테이블 (19.10+, 21c) \[확인\]: 객체 상세에 이 테이블을 표시하고 보존 기간을 보인다.
  - 실행 전에 DELETE·DROP이 막힌다고 알린다.
- 권한 사용 분석 \[EE\]\[확인\]: `DBMS_PRIVILEGE_CAPTURE`로 일정 기간 실제 쓰인 권한을 모아 「쓰지 않는 권한」(`DBA_UNUSED_PRIVS`)을 보인다.
  - 이 기능은 지금 EE에 추가 비용 없이 들어 있다.
  - 캡처 정책을 서버에 만들므로 DBA 접속에서만 연다.
- 스키마 전용 계정 (18c+): 비밀번호 없는 스키마 계정(`AUTHENTICATION_TYPE = 'NONE'`)을 사용자 목록에 표시한다.
  - 프록시 접속(CONN-02)으로 이 계정을 여는 길을 안내한다.
- 19c 함수 템플릿: `LISTAGG(DISTINCT …)`, `APPROX_COUNT`·`APPROX_RANK`(근사 Top-N), `JSON_MERGEPATCH`·`JSON_SERIALIZE`를 자동완성과 템플릿(EDIT-08)에 넣는다.

#### 23ai

- SQL Domain 관리: Domain 목록·정의(`DBA_DOMAINS`)와 그 Domain을 쓰는 컬럼을 보인다.
  - 테이블 구조 편집기에서 컬럼에 Domain을 붙인다.
- 스키마 annotation 편집: 테이블·컬럼 annotation을 보고 고친다(`USER_ANNOTATIONS_USAGE`).
  - annotation은 주석과 함께 그리드 열 머리글의 표시 이름으로 쓸 수 있고, AI 보조(APP-09)의 메타데이터로도 쓴다.
- JSON Duality View 도우미: 관계 테이블을 고르면 Duality View 정의 초안을 만든다.
  - 결과를 JSON 문서로 조회·편집한다(`DBA_JSON_DUALITY_VIEWS`).
- 벡터 검색 도우미: VECTOR 컬럼과 벡터 인덱스(HNSW·IVF)의 목록과 설정을 보이고, `VECTOR_DISTANCE` 질의 템플릿을 준다.
  - VECTOR 칸 보기는 GRID-02를 그대로 쓴다.
- Property Graph: 다음 기능을 둔다.
  - `CREATE PROPERTY GRAPH` 정의 보기(`DBA_PROPERTY_GRAPHS`)
  - `GRAPH_TABLE` 질의 템플릿
  - 결과를 노드·간선 그림으로 보기
- SQL 분석 보고서: 23ai `DBMS_XPLAN` 출력에 붙는 「SQL Analysis Report」 경고를 계획 화면(PLAN-03)에 따로 보인다.
  - 이 기능은 Diagnostics Pack이 필요 없고, SQL 점검(lint) 규칙과 같은 목록에 모을 수 있다.
- 스키마 권한·개발자 롤: 권한 관리에 `GRANT … ON SCHEMA`(`DBA_SCHEMA_PRIVS`) 탭을 둔다.
  - `DB_DEVELOPER_ROLE`로 개발자 계정을 만드는 마법사를 둔다.
- 잠금 없는 예약 컬럼: `RESERVABLE` 컬럼(재고·잔액처럼 동시에 더하고 빼는 숫자)을 객체 상세에 표시하고 구조 편집기에서 지정한다.
- 우선순위 트랜잭션 \[확인\]: 블로킹 트리에 트랜잭션 우선순위와 `PRIORITY_TXNS_*` 설정을 보여, 「낮은 우선순위 쪽이 곧 자동 롤백됨」을 알린다.
- SQL Firewall \[확인\]: 위반 기록(`DBA_SQL_FIREWALL_*`) 조회와 허용 목록 관리를 DBA 화면에 둔다.
  - 라이선스 조건을 먼저 확인한다.
- JavaScript 저장 프로시저 (MLE): 다음 기능을 둔다.
  - `CREATE MLE MODULE` 모듈 목록(`DBA_MLE_MODULES`)
  - JavaScript 문법 강조 편집기
  - 호출 spec 생성

#### 26ai (23ai에 2025년 10월 RU를 적용한 23.26.x)

- Select AI 연결 \[ADB\]\[확인\]: Autonomous의 `DBMS_CLOUD_AI` 프로필(`DBA_CLOUD_AI_PROFILES`)을 쓰면 서버가 자연어를 SQL로 바꾼다(자연어 → SQL).
  - 도구가 API 키를 들고 있지 않아도 되므로, 이 방식은 AI 보조(APP-07, 1.0 이후·D-04)를 시작할 때 첫 단계로 알맞다.
  - 서버가 만든 SQL도 SAFE-02·03을 똑같이 거친다.
- SQL Assertion \[확인\]: 여러 테이블에 걸친 제약(`CREATE ASSERTION`)을 제약 조건 관리에 함께 보인다.
  - 제약을 위반하면 어느 assertion인지 알린다.
  - 이 기능이 들어온 RU를 서버에서 확인한다.
- 하이브리드 벡터 검색 \[확인\]: 텍스트 검색과 벡터 검색을 함께 시험하는 질의 놀이터를 벡터 검색 도우미 안에 둔다.

### 8. LLM으로 강화할 기능

DB 도구가 2024-2026년에 낸 LLM 기능을 웹으로 조사(2026-10-20)해 Oranda에 맞게 고른 목록이다.
검색 요약으로 모은 것이라 버전·날짜는 출처에서 다시 확인한다.
D-04에 따라 1.0에서는 만들지 않는다.
기능을 고를 때는 TRD 4.10의 다음 확장 지점만으로 붙는지를 기준으로 삼는다.

- `IMetadataProvider`
- `IEditorContext`
- `IExecutionPolicy`
- `IOutboundConsent`
- `IToolPanelRegistry`

아래에서 「사례」는 같은 기능을 낸 도구다.

#### 먼저 할 LLM 기능

1. SQL 설명: SQL과 메타데이터만 보내므로 위험이 가장 낮고, 레거시 SQL을 읽는 시간을 바로 줄인다.
2. 오류 고치기: 오류·문장·관련 객체 정의로 원인과 수정 차이를 보인다.
   - 실행은 사람이 한다.
3. 자연어 → SQL: 사용자가 고른 객체의 메타데이터만 보내고, 만든 SQL은 편집기에 넣기만 한다.
4. 실행 계획 해설 + 검증: 도구는 제안을 그대로 믿지 않고 결과 동일성과 계획 비용을 비교해 보인다.
5. Oranda MCP 서버: 코딩 에이전트가 Oranda에 저장된 접속을 정책 관문 안에서만 쓰게 한다.

#### 모든 LLM 기능의 전제

- AI가 만든 SQL은 차이 미리보기를 거쳐 편집기에 넣기만 하고, 실행은 사람이 한다.
  - 실행은 사람이 쓴 SQL과 같은 정책 관문(SAFE-02·03, TRD 4.9)을 지난다.
- 보내는 범위는 사용자가 고른 객체로 한정한다(사례: DBeaver의 Scope 칸, Select AI 프로필).
  - 기본으로는 이름·타입·주석·FK 같은 메타데이터만 보낸다.
  - 행 데이터는 접속마다 따로 동의를 받고, 민감 컬럼은 가린 뒤 보낸다.
- 행 값, 주석, PL/SQL 소스, alert log에 들어 있는 글은 데이터로만 다룬다(프롬프트 인젝션 대비).

#### SQL 작성

- 자연어 → SQL: 한국어 질문과 고른 테이블의 컬럼·주석·FK·annotation으로 SQL을 만든다.
  - 국내 DB는 컬럼 주석이 한글인 경우가 많아서, 주석을 함께 보내면 한국어 질문의 정확도가 오른다.
  - 사례: DataGrip text-to-SQL, DBeaver AI, Toad Chat, Redgate SQL Prompt AI, Select AI `showsql`, APEX SQL Workshop.
- 주석으로 SQL 쓰기·인라인 완성: `-- 부서별 이번 달 매출 상위 5`를 쓰고 Tab을 누르면 그 아래에 SQL을 채운다.
  - 커서 다음에 올 내용을 회색 글자로 제안한다.
  - 사례: Toad + Copilot Next Edit Suggestion.
- 선택 영역 고치기: 사용자가 「월별 집계로 바꿔」, 「부서명도 같이 보여 줘」처럼 지시하면 선택한 SQL을 고친 차이를 보인다.
  - 그런 다음 고친 내용을 받아들일지 묻는다(`IEditorContext`).
  - 사례: DataGrip AI chat.
- 다른 DBMS SQL → Oracle: MySQL·PostgreSQL·SQL Server SQL을 Oracle로 바꾼다(`LIMIT` → `FETCH FIRST`, `ISNULL` → `NVL`).
  - 빈 문자열을 NULL로 다루는 차이처럼 결과가 달라질 수 있는 곳은 따로 경고한다.
  - 19c ↔ 23ai 문법 변환도 같은 기능으로 한다.
- 그리드 자연어 필터: 결과 거르기 줄(GRID-10)에 「지난달 가입한 서울 고객」을 쓰면 WHERE 절로 바꿔 칸에 보인다.
  - 사용자는 Enter로 이 조건을 적용한다.
  - 이 기능은 컬럼 메타데이터만 필요하다.
- MyBatis 매퍼 작성: 사용자가 테이블과 조건을 말하면 `[[ORCA_RICH_MD:05c2825bb8f53f350ece2db6867ff86c:inline-html:%3Cselect%3E]]`·`[[ORCA_RICH_MD:05c2825bb8f53f350ece2db6867ff86c:inline-html:%3Cif%3E]]`·`[[ORCA_RICH_MD:05c2825bb8f53f350ece2db6867ff86c:inline-html:%3Cforeach%3E]]`를 쓴 매퍼 XML 초안을 만든다.
  - 기존 동적 SQL을 설명한다(EDIT-09).

#### 이해·설명

- SQL 설명: 긴 레거시 SQL을 단계별로 한국어로 설명한다(조인 관계, 걸러지는 조건, 업무 의미).
  - 사례: Select AI `explainsql`, Toad Chat, SQL Prompt explain query.
- 실행 계획 해설·튜닝 제안: `DBMS_XPLAN` 출력, 통계, 기존 인덱스로 병목을 설명하고 재작성·인덱스·힌트를 제안한다.
  - 사례: pgAdmin 9.13 AI Insights for EXPLAIN, SQL Prompt index analysis.
  - 도구가 제안을 검증한다.
    - 재작성한 SQL은 원래 SQL과 결과가 같은지(양방향 `MINUS`나 건수·해시 비교, 개발 접속에서만) 확인한다.
    - 계획 비용은 PLAN-04 비교 화면으로 보인다.
  - 제안한 인덱스는 자동으로 만들지 않는다.
    - 5절 「새 인덱스 시험」처럼 INVISIBLE로 만들어 내 세션에서만 시험하게 한다.
- 결과 요약: 결과 그리드를 보고 눈에 띄는 점을 문장으로 요약한다.
  - 사례: Select AI `narrate`.
  - 이 기능은 행 데이터를 보내므로 접속별로 동의를 받고 민감 컬럼을 가려야 한다.
- 의존 관계·데이터 흐름 설명: 「이 테이블을 누가 쓰고, 이 컬럼 값은 어디서 오나」를 의존 관계(`ALL_DEPENDENCIES`), 뷰 정의, 소스 검색(SCH-09) 결과로 설명한다.
- 변경 요약: 운영 반영 스크립트 묶음(6절)이나 스키마 비교 결과를 사람이 읽을 변경 요약·릴리스 노트로 만든다.
  - 사례: Flyway Desktop AI migration summaries.

#### 오류 수정·검토

- 오류 고치기: ORA-·PLS- 오류, 오류 위치, 문장, 관련 객체 정의로 원인을 설명하고 수정 차이를 보인다.
  - 이 기능은 3절 「ORA 오류 도우미」의 규칙으로 풀리지 않는 오류를 맡는다.
  - 사례: Databricks Diagnose Error·Quick Fix.
- 이름 오타 제안: 없는 테이블·컬럼 이름이면 딕셔너리에서 비슷한 이름을 찾아 제안한다.
  - 도구가 규칙(편집 거리)으로 먼저 찾고, LLM은 뜻이 비슷한 이름까지 넓힌다.
  - 사례: DataGrip 2026.2.
- AI 코드 검토: SQL 점검(lint) 규칙에 LLM 검토를 더한다.
  - PL/SQL에서는 다음 항목을 찾는다.
    - `EXECUTE IMMEDIATE`에 문자열을 이어 붙인 곳(SQL 주입)
    - `WHEN OTHERS THEN NULL`
    - 루프 안의 단건 DML(`BULK COLLECT`·`FORALL` 제안)
  - 사례: Toad SQL Validate.

#### 스키마·설계

- 자연어로 테이블 설계: 사용자가 「주문·주문상세·고객 테이블」처럼 말하면 DDL 초안을 만든다.
  - 초안에는 표준 용어 사전(6절), 명명 규칙, `VARCHAR2 CHAR` 길이 의미, identity를 반영한다.
  - 초안은 테이블 구조 편집기(1절)에 열어 사람이 고친다.
  - 사례: VS Code mssql Schema Designer + Copilot, APEX Create Data Model Using AI.
- 설계 검토: PK 없는 테이블, 날짜를 VARCHAR2에 담은 컬럼, 인덱스 없는 FK, 정규화 문제를 찾아 알린다.
  - 사례: VS Code mssql Schema Designer.
- 주석·한글명 초안: 주석이 없는 테이블·컬럼에 이름·타입(동의하면 가린 샘플 값까지)으로 주석 초안을 만든다.
  - 만든 주석은 주석 일괄 편집(1절) 화면에 초안으로 채운다.
  - 한글 이름에서 표준 영문 컬럼명을 제안한다.
- 변경 스크립트 초안: 스키마 비교 차이로 ALTER 문과 되돌리기 스크립트 초안을 만든다.
  - **파괴적인 DDL은 검토 단계를 반드시 거친다.**

#### 데이터 작업

- 테스트 데이터 규칙 만들기: 테이블 정의와 제약만 보내고 실제 행은 보내지 않는다.
  - LLM은 컬럼마다 현실적인 한국어 값 규칙을 만들고, 값은 도구가 만든다(2절 「테스트 데이터 생성」).
  - 사례: Select AI synthetic data.
- 데이터 정제 SQL: 사용자가 「전화번호를 010-0000-0000 형식으로 맞춰」처럼 지시하면 `REGEXP_REPLACE` UPDATE와 바뀔 행 미리보기를 만든다.
  - 실행은 2절 「대량 DML 나눠 실행」으로 넘긴다.

#### PL/SQL

- PL/SQL 작성·설명·문서화: 패키지 요약, 멤버별 설명 주석, 호출 예제를 만든다.
  - 긴 패키지는 멤버 단위로 잘라서 보낸다.
- 단위 시험 생성: 프로시저 서명과 관련 테이블 정의로 시험 코드를 만든다.
  - 서버에 utPLSQL이 이미 있으면 utPLSQL 형식으로, 없으면 익명 블록으로 만든다(서버에 설치하지 않는다).
- 컴파일 오류 풀이: `USER_ERRORS`와 소스 줄로 원인을 설명하고 수정 차이를 보인다(PLSQL-02 확장).

#### 운영·DBA

- 읽기 전용 진단 도우미: 「지금 왜 느려?」에 답하도록, 정해 둔 조회 목록(세션·대기·블로킹·Top SQL 카탈로그 쿼리)만 도구로 실행해 결과를 해설한다.
  - 사례: SSMS Copilot Agent mode, pgAdmin AI reports, Toad Chat + 내장 MCP.
  - 모니터링 연결(CONN-07)을 쓰고 읽기 전용 트랜잭션으로 연다.
  - Pack이 필요한 뷰는 SAFE-07을 지난다.
- 점검 보고서: 권한·프로필·파라미터·보안 설정을 검토한 보고서를 만든다(PRD 6.13 「상태 점검 보고서」 후보).
  - 사례: pgAdmin 9.13 AI security·schema·performance reports.
  - 보고서 자체가 민감한 정보이므로 로컬에만 저장한다.
- alert log·트레이스 해석: 5절 「alert log 보기」에서 고른 오류 묶음의 원인과 확인할 점을 설명한다.

#### 에이전트·MCP 연동

- Oranda MCP 서버: Claude Code·Copilot 같은 코딩 에이전트가 Oranda에 저장된 접속으로 스키마를 조회하고 SQL을 실행하게 한다.
  - 비밀번호는 에이전트에 넘기지 않고, 모든 실행은 Oranda 정책 관문을 지난다.
  - 운영 라벨 접속은 기본으로 빼고, 접속마다 허용할 도구(조회만, 실행 포함)를 고른다.
  - 사례: DataGrip 2026.1-2026.2 MCP tools, Toad 내장 MCP, Oracle SQLcl MCP(`run-sql`, RESTRICT 수준), Flyway Enterprise MCP.
- 에이전트 실행 기록: 도구는 에이전트가 실행한 SQL을 실행 이력(EXEC-07)에 「AI」 표시, 요청 원문과 함께 남긴다.
  - 사례: DataGrip은 에이전트 질의를 질의 이력에 남기고, SQLcl MCP는 서버 테이블 `DBTOOLS$MCP_LOG`에 남긴다.
  - Oranda는 서버 객체를 만들지 않으므로 로컬에 남긴다.
- Select AI 연결: 7절의 ADB Select AI를 쓰면 도구가 API 키를 관리하지 않아도 된다.

#### 안전·거버넌스

- AI 실행 관문: 도구는 AI가 만든 SQL을 키워드가 아니라 파서로 분류한다.
  - 다음 항목은 따로 잡는다.
    - DDL
    - `TRUNCATE`
    - WHERE 없는 UPDATE·DELETE
    - 익명 블록
    - `EXECUTE IMMEDIATE`
    - `DBMS_SCHEDULER`
    - DB 링크
    - 커밋하는 프로시저 호출
  - 운영 접속은 기본으로 차단하고, 쓰기는 사람이 확인하는 경로로만 연다.
  - 사례: SSMS Agent mode의 질의별 승인, Beekeeper Studio의 「읽기 전용 SELECT만 자동 실행」.
- 에이전트 세션은 읽기 전용: 에이전트용 세션은 SAFE-02와 같이 `SET TRANSACTION READ ONLY`로 연다.
  - 권한이 좁은 계정을 쓰도록 안내한다(SSMS 문서도 「DB 권한이 실제 보안 경계」라고 적는다).
- DML 시험 실행: 개발 접속에서는 DML을 실행해 영향 행 수만 보이고 롤백한 뒤 확인을 받는다.
  - **트리거, 자율 트랜잭션, 시퀀스 증가는 롤백되지 않으므로 운영 접속에서는 쓰지 않는다.**
- 전송 통제: 접속마다 처음 한 번 동의를 받는다.
  - 보낼 객체 범위를 지정한다.
  - 보낸 요청 원문을 보고 로그로 남길 수 있게 한다(`IOutboundConsent`).
  - 민감 컬럼은 2절 「민감 컬럼 가림」 규칙으로 가린다.
  - 사례: DBeaver의 AI 데이터 보호 설정, Redgate trust and transparency 페이지.
- 제공자 선택: 사용자 키(OpenAI·Anthropic·Azure OpenAI·Gemini), 사내 게이트웨이(OpenAI 호환 URL, LiteLLM), 로컬 모델(Ollama·LM Studio)을 고른다.
  - APP-07대로 로컬 모델을 우선한다.
  - 사례: DBeaver(Ollama), pgAdmin 9.14의 사용자 지정 제공자 URL.
- 비용 한도: 하루·한 달 토큰 한도를 두고, 요청마다 예상 토큰을 보인다.
  - 에이전트가 같은 일을 되풀이하면 실행을 멈춘다.
  - 사례: Snowflake AI 예산.
- 데이터와 지시 분리: 행 값·주석·소스는 구분된 데이터 블록으로 넣는다.
  - 비공개 데이터를 읽는 에이전트가 밖으로 쓰는 도구를 함께 갖지 않게 한다.

#### 참고한 사고 사례

- Replit (2025-07): 코드 동결 중에 에이전트가 운영 DB를 지우고 가짜 데이터를 만들었다.
  - 프롬프트에 적은 규칙은 잠금이 아니므로 실행기에서 막아야 하고, 개발·운영 접속을 분리해야 한다.
- Supabase MCP (2025-07): 지원 티켓에 심은 지시문 때문에 에이전트가 비공개 토큰 테이블을 읽어 티켓에 다시 썼다.
  - 비공개 데이터, 믿을 수 없는 입력, 밖으로 쓰는 경로를 한 에이전트에 함께 주면 안 된다.
- PocketOS (2026-04, 창업자 설명 기준): 에이전트가 관계없는 파일에 있던 권한이 넓은 토큰으로 볼륨과 백업을 지웠다.
  - 자격 증명 범위를 좁히고, 백업은 에이전트가 쓰는 자격 증명으로 닿지 않는 곳에 둔다.

#### 출처

- DataGrip 2026.2: [https://blog.jetbrains.com/datagrip/2026/07/16/datagrip-2026-2-ai-agent-skills-mcp-tools-and-cli-commands-for-data-source-management-bundled-jdbc-drivers-and-improved-session-control/](https://blog.jetbrains.com/datagrip/2026/07/16/datagrip-2026-2-ai-agent-skills-mcp-tools-and-cli-commands-for-data-source-management-bundled-jdbc-drivers-and-improved-session-control/)
- DBeaver AI 데이터 보호: [https://dbeaver.com/docs/dbeaver/26.1/AI-Assistance-and-Data-Privacy/](https://dbeaver.com/docs/dbeaver/26.1/AI-Assistance-and-Data-Privacy/)
- SQLcl MCP: [https://docs.oracle.com/en/database/oracle/sql-developer-vscode/25.3/sqdnx/sqlcl-mcp-server-tools.html](https://docs.oracle.com/en/database/oracle/sql-developer-vscode/25.3/sqdnx/sqlcl-mcp-server-tools.html)
- Select AI: [https://docs.oracle.com/en/database/oracle/oracle-database/26/selai/use-ai-keyword-enter-prompts.html](https://docs.oracle.com/en/database/oracle/oracle-database/26/selai/use-ai-keyword-enter-prompts.html)
- APEX AI 데이터 모델: [https://docs.oracle.com/en/database/oracle/apex/24.2/aeutl/creating-data-model-ai.html](https://docs.oracle.com/en/database/oracle/apex/24.2/aeutl/creating-data-model-ai.html)
- SSMS Agent mode: [https://learn.microsoft.com/en-us/ssms/github-copilot/agent-mode](https://learn.microsoft.com/en-us/ssms/github-copilot/agent-mode)
- VS Code mssql: [https://devblogs.microsoft.com/azure-sql/vscode-mssql-june-2026/](https://devblogs.microsoft.com/azure-sql/vscode-mssql-june-2026/)
- Redgate SQL Prompt AI: [https://www.red-gate.com/our-company/newsroom/press-releases/redgate-introduces-ai-powered-features-in-sql-prompt/](https://www.red-gate.com/our-company/newsroom/press-releases/redgate-introduces-ai-powered-features-in-sql-prompt/)
- Toad for Oracle 2026 R2: [https://forums.toadworld.com/t/toad-for-oracle-2026-r2-is-now-available/61764](https://forums.toadworld.com/t/toad-for-oracle-2026-r2-is-now-available/61764)
- pgAdmin 9.13·9.14: [https://www.postgresql.org/about/news/pgadmin-4-v913-released-3253](https://www.postgresql.org/about/news/pgadmin-4-v913-released-3253), [https://www.postgresql.org/about/news/pgadmin-4-v914-released-3267](https://www.postgresql.org/about/news/pgadmin-4-v914-released-3267)
- Databricks Genie Code: [https://docs.databricks.com/aws/en/genie-code/features-capabilities](https://docs.databricks.com/aws/en/genie-code/features-capabilities)
- Supabase MCP 사고: [https://simonwillison.net/2025/Jul/6/supabase-mcp-lethal-trifecta](https://simonwillison.net/2025/Jul/6/supabase-mcp-lethal-trifecta)
- Replit 사고: [https://www.bytebase.com/blog/how-to-prevent-ai-agent-from-dropping-your-production-database/](https://www.bytebase.com/blog/how-to-prevent-ai-agent-from-dropping-your-production-database/)
- PocketOS 사고: [https://zenity.io/blog/ai-agent-database-deletion-pocketos](https://zenity.io/blog/ai-agent-database-deletion-pocketos)

### macOS 참고값 (2026-10-20, dev 9dea471, 맥북에서 측정)

**이 값은 판정용 값이 아니다.**
조용한 장비·게시본 측정의 출발점으로만 쓴다.
측정 환경·방법·회차별 값은 `docs/perf/macos-2026-10-20.md`에 있다.


| 항목                  | 값        | 비고                                                      |
| ------------------- | -------- | ------------------------------------------------------- |
| 시작 → 입력 가능          | 391 ms   | 목표 1.5 s                                                |
| 최초 실행 (디스크 캐시 없음)   | 1,243 ms | 목표 4 s                                                  |
| 유휴 메모리              | 131.6 MB | 접속·탭 없음. 목표 150 MB까지 18 MB 여유. 「접속 1, 탭 1」 조건으로 다시 재야 함 |
| 포맷 2,000줄           | 16 ms    | 목표 500 ms                                               |
| 그리드 스크롤 (스파이크)      | 59.9 fps | 목표 60 fps. 제품 그리드(TSK-06-03)로 다시 재야 함                   |
| 편집기 키 입력 p95 (스파이크) | 9-13 ms  | 목표 16 ms. 제품 편집기(TSK-04-02)로 다시 재야 함                    |


macOS에서 아직 재지 못한 항목과 그 이유는 다음과 같다.


| 항목                    | 이유                    |
| --------------------- | --------------------- |
| 자동완성 목록 표시            | 편집기가 실제 앱에서 열리지 않는 결함 |
| 첫 행 표시                | 같은 결함                 |
| 접속 3 + 10만 행 결과 3 메모리 | 같은 결함                 |
| 문법 검사 반영              | TSK-04-03 미구현(측정 당시)  |
| 실행 취소 반응              | 맥북에서 컨테이너 접속 실패       |
| 설치 파일 크기              | 최신 코드 CI 실패로 패키지 없음   |


**위험 항목**: 유휴 메모리는 접속·탭 없이도 131.6 MB라서 150 MB까지 18 MB뿐이다.
「접속 1, 탭 1」 조건에서는 이 목표를 넘을 수 있으니 측정 회차에서 이 항목을 먼저 본다.

CI(run 39276301319)의 Windows 러너에서 `Incremental_lex_of_10k_lines_meets_budget` 시험이 21 ms(한도 16 ms)로 실패했다.
**이 실패는 성능 단정 시험을 기본 시험에서 빼야 하는 근거다.**

### 지금 코드에 있는 성능 단정 시험 (Category=Perf로 옮길 대상)

- `tests/Oranda.Language.Tests/Lexing/StatementSplitterTests.cs`: `Split_of_10k_lines_is_fast_enough`, `Incremental_lex_of_10k_lines_meets_budget`
- `tests/Oranda.Language.Tests/Formatting/`: 2,000줄 포맷 시험 (05-01, 05-02)
- `tests/Oranda.Language.Tests/Completion/`: 완성 응답과 L1 문맥의 시간을 재는 시험 (04-05)
- `tests/Oranda.App.Tests/Features/SqlEditor/`: 1만 줄에서 키 입력 p95를 재는 시험 (04-02)
- `tests/Oranda.App.Tests`의 자동완성 헤드리스 시간 시험 (04-05)
- 그 밖에 이름에 `fast`, `budget`, `ms`가 들어간 시험을 찾아 함께 옮긴다.