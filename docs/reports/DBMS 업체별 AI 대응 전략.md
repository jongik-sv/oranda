# DBMS 업체별 AI 대응 전략

## 요약

2024-01부터 2026-10까지 DBMS 업체들은 거의 같은 세 가지 대응을 했다.

- 벡터 검색과 임베딩을 범용 엔진 안으로 넣었다. 예: Oracle 23ai(2024-05), SQL Server 2025(2025-11), pgvector, MariaDB, MongoDB.
- 에이전트가 DB에 접근하는 통로를 Model Context Protocol(MCP)로 표준화했다. Oracle은 2025-07에 SQLcl에 MCP 서버를 넣었고, Snowflake는 2025-11에, Google은 2026-04에 MCP 서버를 정식 출시(GA)했다.
- 에이전트용 작업 환경을 따로 마련했다. 예: Databricks의 Neon 인수, Snowflake의 Crunchy Data 인수, Oracle Select AI Agent, Snowflake Cortex Agents.

업체마다 다른 점은 AI로 무엇을 지키려 하느냐다.

- Oracle과 Microsoft는 AI 기능을 추가 요금 없이 기존 엔진에 넣어 설치 기반을 지킨다.
- 대형 클라우드 업체는 AI 코드 변환으로 Oracle 고객을 PostgreSQL로 옮기려 한다.
- 국산 업체는 엔진 기능보다 소버린 AI 플랫폼과 공공 조달을 앞세운다.

경쟁의 초점은 이제 "DB가 AI를 할 수 있나"가 아니라 "에이전트가 무엇을, 누구 권한으로 다룰 수 있나"로 옮겨 갔다.

- Microsoft는 SQL MCP Server에서 자연어→SQL 기능을 일부러 뺐다.
- 엔터프라이즈 규모 벤치마크 Spider 2.0을 발표한 논문(ICLR 2025)에서 o1-preview 기반 에이전트가 푼 문제는 21.3%에 그쳤다.

Oranda에게 이 흐름은 'DB와 대화하기'가 Oracle이 무료로 주는 기본 기능이 됐다는 뜻이다. Oranda에 남는 차별점은 다음 네 가지다.

- 버전 인지: 벡터 기능이 없는 19c(Premier 지원 2029년 말까지)에서도 동작한다.
- 정책 관문: DB 권한을 실제 보안 경계로 삼는다.
- 메타데이터 맥락: 한글 주석을 살린다.
- 관문형 MCP 서버: 코딩 에이전트가 반드시 거쳐 가는 MCP 서버다.

이번 연구 결과는 1.0에서 AI 구현을 미룬 결정(D-04)을 더 뒷받침한다. **반면 에이전트 안전을 읽기 전용 트랜잭션에 기대는 현재 설계는 고쳐야 한다.**

## 일곱 업체군은 같은 세 가지 대응을 하지만 지키려는 자산은 다르다

업체군을 일곱으로 나누면 공통된 세 가지 대응이 보이지만, AI로 지키거나 노리는 자산은 업체군마다 다르다. 세 가지 대응은 다음과 같다.

- 벡터와 임베딩을 범용 엔진의 기본 기능으로 만든다.
- 에이전트가 DB에 접근하는 표준 통로로 MCP 서버를 내놓는다.
- 에이전트용 작업 환경을 마련한다. 에이전트 프레임워크나, 에이전트가 만들고 지우는 운영용 PostgreSQL이 여기에 속한다.

아래 표는 2026-10-20 기준으로 업체군별 세 가지 대응과 AI로 지키거나 노리는 자산을 정리한다. 근거는 이어지는 절에 달았다.

| 업체군 | 벡터·임베딩 | 자연어→SQL·에이전트 | MCP | AI로 지키거나 노리는 것 |
|---|---|---|---|---|
| Oracle | 23ai AI Vector Search(2024-05 GA), DB 안 ONNX 임베딩, 26ai에서 "추가 비용 없음" 표방 | Select AI(runsql·showsql·narrate·chat·explainsql), 26ai의 Select AI Agent·Private Agent Factory | SQLcl MCP(2025-07), ORDS·OCI Database Tools, Autonomous 내장 MCP | 19c·23ai 설치 기반, 데이터를 옮기지 않게 하는 '데이터 중력' |
| Microsoft | SQL Server 2025 VECTOR GA(2025-11-18), 벡터 인덱스는 아직 프리뷰. Azure SQL DiskANN GA(2026-09-29) | 엔진은 외부 모델을 호출(EXTERNAL MODEL), SSMS Copilot Ask/Agent | SSMS 로컬 `sql-tools`, SQL MCP Server(자연어→SQL 제외) | 온프레미스 SQL Server, Fabric·PostgreSQL(HorizonDB)로 클라우드 성장 |
| PostgreSQL·MySQL 생태계 | pgvector 0.8, 클라우드별 ScaNN·DiskANN 확장. MySQL Community는 VECTOR 타입만, MariaDB는 인덱스까지 | pgAdmin 9.13 AI 채팅·EXPLAIN 해설 | Supabase·PlanetScale·MariaDB MCP, DBeaver의 MCP 클라이언트 | 확장 생태계, '에이전트가 만드는 DB' |
| Snowflake·Databricks | Cortex Search, Lakebase(pgvector) | Cortex Analyst·Agents, Genie·Agent Bricks | 관리형 MCP(Snowflake GA 2025-11-04, Databricks 프리뷰) | 거버넌스가 걸린 분석 데이터와 새로 산 PostgreSQL |
| Google·AWS | AlloyDB ScaNN, S3 Vectors GA(re:Invent 2025) | QueryData(프리뷰), Amazon Q 생성형 SQL | 관리형 원격 MCP GA(Google 2026-04), AWS Labs MCP | AI 코드 변환을 앞세운 Oracle→PostgreSQL 이관 |
| MongoDB·NoSQL·벡터 DB | MongoDB 자동 임베딩 GA(2026-08-12), Redis 벡터 세트 | Compass 자연어 질의(실험 기능), Pinecone Nexus | MongoDB MCP 등 | 개발자 경험. 전용 벡터 DB는 지식 계층으로 방향 전환 |
| 국산 DBMS | 엔진 벡터 발표를 찾지 못함, CUBRID는 2027년 베타 계획 | Tibero의 AI SQL 튜닝 자동화 협약 | 확인 못 함 | 공공 조달, 소버린 AI 플랫폼 |

세 가지 대응의 사업 논리는 기반 기능을 무료로 주고, 데이터가 머무는 곳에서 돈을 버는 구조다. 무료 제공의 근거는 다음과 같다.

- Oracle은 26ai에서 AI Vector Search를 비롯한 고급 AI 기능을 추가 비용 없이 제공한다고 밝혔다([Oracle](https://www.oracle.com/database/ai-native-database-26ai/)).
- Snowflake는 관리형 MCP 서버 객체에는 별도 컴퓨트나 요금이 붙지 않는다고 했다([Snowflake](https://www.snowflake.com/en/blog/managed-mcp-servers-secure-data-agents/)).
- SQL Server 2025 가격표에서도 AI 기능용 별도 요금은 찾지 못했다([가격 자료](https://www.licensingschool.co.uk/wp-content/uploads/2025/11/SQL-Server-2025-Pricing-Datasheet.pdf)).

돈은 그 위에서 쓰는 컴퓨트와 토큰에서 나오고, 데이터를 옮기기 어려워지는 효과에서도 나온다. Larry Ellison은 공개 데이터로 학습한 최전선 LLM은 결국 상품이 되고, 가치는 기업 사설 데이터 위에서 하는 추론에서 나온다고 설명했다([Cloud Wars](https://cloudwars.com/innovation-leadership/larry-ellisons-grand-plan-for-oracle-to-win-multi-trillion-dollar-ai-markets)). 이 논리는 Oracle에만 해당하지 않는다. Snowflake의 Crunchy Data 인수와 MongoDB의 DB 안 임베딩 생성도 같은 방향이다.

비판도 있다. 제3자 지원 업체 Support Revolution의 Mark Smith는 26ai가 AI 종속을 키우고 비용을 올릴 것이라고 경고했다([The Register](https://www.theregister.com/2026/02/02/oracles_on_prem_ai_database/)). 다만 Mark Smith는 고객이 Oracle 정규 지원에서 떠나야 이득을 보는 이해관계자다.

### 이 보고서가 확인하지 못한 사항

Oracle 쪽 사실 상당수는 Oracle 페이지의 검색 요약과 InfoWorld·The Register·heise 같은 2차 보도에 기댄다. oracle.com과 blogs.oracle.com은 직접 열람이 막혔기 때문이다.

다음 사항은 확인하지 못했다.

- Oracle 26ai의 기능별 GA·프리뷰 구분
- 23.26.2·23.26.3 Release Update(RU)의 내용
- 19c 온프레미스에서 Select AI를 받는 경로
- SQL Server 2025 벡터 인덱스가 Cumulative Update(CU)로 GA됐는지 여부
- Azure HorizonDB의 GA
- PostgreSQL 19 GA
- MySQL 9.7에 실제로 들어간 기능
- Azure `pg_diskann`의 GA

국산 업체와 벤치마크 자료의 상태는 다음과 같다.

- Tibero: 엔진 벡터 기능의 발표를 '찾지 못했다'. 기능이 '없다'는 뜻은 아니다.
- Altibase: 자료를 얻지 못했다.
- Oracle 벡터 검색을 pgvector·MongoDB·Snowflake와 독립적으로 비교한 벤치마크: 찾지 못했다.

## 벡터 검색은 2년 만에 범용 엔진의 기본 기능이 됐다

벡터 검색은 2년 만에 별도 제품의 기능에서 범용 엔진의 기본 기능이 됐다. 그러자 차별점은 두 곳으로 옮겨 갔다.

- 임베딩을 누가 어디서 만드는가: Oracle은 DB 안 ONNX, Microsoft는 외부 호출, MongoDB는 자사 클라우드 추론으로 만든다.
- 필터를 건 검색과 대규모 인덱스

### Oracle은 23ai에서 벡터 검색을 엔진에 넣었다

Oracle은 2024-05-02에 23ai를 GA하면서 AI Vector Search를 300개가 넘는 신기능의 대표 기능으로 내세웠다([Oracle 보도자료](https://www.oracle.com/news/announcement/oracle-announces-availability-database-23ai-with-ai-vector-search-2024-05-02)). 23ai에 들어간 벡터 기능은 다음과 같다([ORACLE-BASE](https://oracle-base.com/articles/23/ai-vector-search-23)).

- `VECTOR` 타입
- DB 안 임베딩 생성: `DBMS_VECTOR.LOAD_ONNX_MODEL`로 ONNX 모델을 DB에 올린 후 `VECTOR_EMBEDDING`으로 임베딩을 만든다.
- `VECTOR_DISTANCE`
- `FETCH APPROXIMATE` 근사 검색
- 두 종류의 벡터 인덱스: neighbor partition 인덱스, 메모리 그래프 인덱스

Oracle은 관계형·JSON·그래프·공간·텍스트·벡터를 엔진 하나에서 처리하므로 데이터를 별도 벡터 DB로 옮길 필요가 없다고 주장한다([Oracle](https://www.oracle.com/database/ai-native-database-26ai/)). Oracle 문서는 19c에서 업그레이드한 DB에서 벡터 기능을 쓰려면 `COMPATIBLE`을 직접 올려야 한다고 안내한다([Oracle 23.7 문서](https://docs.oracle.com/en/database/oracle/oracle-database/23/nfcoa/ru_23_7.html)).

### SQL Server 2025는 벡터 타입을 GA했지만 벡터 인덱스는 아직 프리뷰다

Microsoft는 Oracle보다 1년 반 늦게 같은 기능을 냈지만 방식이 다르다. SQL Server 2025는 2025-11-18에 GA됐다([Microsoft Tech Community](https://techcommunity.microsoft.com/blog/SQLServer/sql-server-2025-is-now-generally-available/4470570)). GA 기능은 다음과 같다([What's new in SQL Server 2025](https://learn.microsoft.com/en-us/sql/sql-server/what-s-new-in-sql-server-2025)).

- `VECTOR` 타입(4바이트 단정밀도와 2바이트 반정밀도)
- `VECTOR_DISTANCE` 같은 벡터 함수
- `CREATE EXTERNAL MODEL`: 추론 엔드포인트의 위치·인증·용도를 정의한다.
- `AI_GENERATE_EMBEDDINGS`: `CREATE EXTERNAL MODEL`로 정의한 모델로 임베딩을 만든다.

반면 DiskANN 기반 벡터 인덱스와 `VECTOR_SEARCH`는 2026-10-19에 갱신된 릴리스 노트에서도 여전히 프리뷰다. `PREVIEW_FEATURES` DB 범위 설정을 켜야 이 두 기능을 쓸 수 있고, Microsoft는 운영 환경에는 권장하지 않는다([SQL Server 2025 release notes](https://learn.microsoft.com/en-us/sql/sql-server/sql-server-2025-release-notes)).

Microsoft의 클라우드 서비스가 앞서 있다. Azure SQL Database·Managed Instance·Fabric의 SQL database에서는 같은 인덱스가 2026-09-29에 GA됐다. 이 GA에는 다음 내용이 포함됐다([Azure SQL blog](https://devblogs.microsoft.com/azure-sql/diskann-vector-index-search-are-now-generally-available-in-azure-sql/)).

- DML 전체 지원
- 비동기 인덱스 유지
- 옵티마이저의 정확 검색·근사 검색 선택
- 행 수준 보안과의 결합

Cosmos DB는 2024-11에 이미 DiskANN을 GA했다([Cosmos DB blog](https://devblogs.microsoft.com/cosmosdb/new-vector-search-full-text-search-and-hybrid-search-features-in-azure-cosmos-db-for-nosql/)). Microsoft는 SQL·Cosmos·PostgreSQL 세 엔진을 DiskANN 하나로 묶고 있다. 이와 별개로 SQL Server 엔진은 모델을 엔진 안에 두지 않고 외부 엔드포인트를 호출하는 방식을 택했다.

### PostgreSQL은 코어가 아니라 확장으로 벡터 검색을 지원한다

PostgreSQL 진영에서는 코어가 아니라 확장이 벡터 검색을 맡았다. pgvector 0.8.0(2024-11 공지)은 iterative index scan을 넣었다. 이 기능은 필터를 건 벡터 검색에서 결과가 모자라는 '과잉 필터링' 문제를 고쳤다([postgresql.org](https://www.postgresql.org/about/news/pgvector-0.8.0-released-2952/)). Amazon RDS는 2024-11-21에, Aurora는 2025-04-09에 이 버전을 받아들였다([AWS RDS](https://aws.amazon.com/about-aws/whats-new/2024/11/amazon-rds-for-postgresql-pgvector-080/), [AWS Aurora](https://aws.amazon.com/about-aws/whats-new/2025/04/pgvector-0-8-0-aurora-postgresql)).

대형 클라우드 업체는 인덱스 확장으로 차별화한다.

- Google AlloyDB는 ScaNN 기반 `alloydb_scann`과 자연어→SQL 확장 `alloydb_ai_nl`을 내놨다([Google Cloud](https://docs.cloud.google.com/alloydb/docs/ai)).
- Azure는 `pg_diskann`을 프리뷰로 열었다. GA 여부는 출처끼리 엇갈려 확인하지 못했다([Azure Updates](https://mc.merill.net/message/AZ-diskann-indexing-on-azure-database-for-postgresql)).

정작 PostgreSQL 18(2025-09-25)에는 벡터 관련 코어 기능이 없었다([linuxiac](https://linuxiac.com/postgresql-18-released-with-up-to-3-faster-io-and-easier-upgrades/)). 2026-06-04에 나온 PostgreSQL 19 베타는 SQL/PGQ를 넣었다. SQL/PGQ는 관계형 테이블 위에서 그래프를 질의하는 기능으로, Oracle 23ai의 property graph와 같은 방향이다([postgresql.org](https://postgresql.org/about/news/postgresql-19-beta-1-released-3313/)).

DB 안에서 임베딩을 만드는 파이프라인은 아직 자리를 잡지 못했다. TigerData(옛 Timescale)는 관리형 vectorizer 서비스와 DB 안 LLM 호출 함수를 폐기 대상으로 돌렸고, 2026-06-30 제거를 예고했다([TigerData](https://www.tigerdata.com/docs/ai/latest/vectorizer-deprecation)).

### MySQL Community에는 벡터 타입만 들어갔다

MySQL은 Oracle이 AI의 핵심 가치를 유료 계층에 남겨 둔 사례다. MySQL 9.0 Community에는 `VECTOR` 타입만 들어갔다. 거리 함수 `DISTANCE()`와 벡터 인덱스는 HeatWave나 MySQL AI 전용으로 문서화돼 있다([MySQL 문서](https://dev.mysql.com/doc/refman/9.1/en/vector-functions.html)). 2026-05에는 Community에도 이 기능을 달라는 버그 리포트가 올라왔다([MySQL bug 120425](https://bugs.mysql.com/bug.php?id=120425)). HeatWave GenAI는 GPU 없이 DB 안에서 LLM을 돌리고, 문서를 자동으로 임베딩해 검색 증강 생성(RAG)에 쓰는 기능을 판다([Oracle HeatWave](https://www.oracle.com/heatwave/genai/features/)).

빈자리는 다른 업체가 채웠다.

- MariaDB는 2025-02 11.7 GA에서 벡터 타입·인덱스·거리 함수를 오픈소스로 냈다([MariaDB](https://mariadb.com/resources/blog/announcing-mariadb-community-server-11-7-ga-with-vector-search-and-mariadb-community-server-11-8-rc/)).
- Percona는 2026-09 자사 포크 9.7.2-2에 `DISTANCE()`를 넣었다([Percona](https://www.percona.com/blog/vector-distance-vector-similarity-search-in-percona-server-for-mysql/)).

2026-02에는 Percona가 주도해 MySQL 재단 설립을 요구하는 공개서한을 냈다. Percona는 활동 기여자가 2017년 135명에서 2025년 3분기 약 75명으로 줄었다고 집계했다([Percona](https://www.percona.com/blog/an-open-letter-to-oracle-lets-talk-about-mysqls-future/)). 다만 Percona 역시 이해관계자라는 점은 고려해야 한다.

### MongoDB는 임베딩 생성을 DB 안으로 넣었고 전용 벡터 DB는 방향을 바꿨다

NoSQL과 전용 벡터 DB에서도 흐름은 같다. MongoDB는 Voyage AI 임베딩을 DB 기능으로 넣은 '자동 임베딩'을 2026-01에 Community 대상 프리뷰로 공개했다([IT Brief](https://itbrief.co.uk/story/mongodb-adds-voyage-4-ai-models-automates-vector-search)). 이어 Atlas에서 2026-05-07에 프리뷰, 2026-08-12에 GA로 냈다([MongoDB](https://www.mongodb.com/products/updates/now-ga-automated-embedding-in-atlas-vector-search/)). 이 편의에는 대가가 따른다. 추론 서비스가 Google Cloud에서 돌기 때문에, 임베딩을 만들 때 데이터가 Google Cloud로 전송된다([MongoDB Docs](https://www.mongodb.com/docs/vector-search/about/automated-embedding/)).

Redis는 8.0에서 AGPLv3로 오픈소스에 복귀하면서 벡터 세트 타입과 LLM 응답용 시맨틱 캐시 LangCache를 내놨다([The New Stack](https://thenewstack.io/redis-launches-vector-sets-and-a-new-tool-for-semantic-caching-of-llm-responses/)).

압박을 받는 쪽은 전용 벡터 DB다.

- Pinecone
  - 2025-08에 매각을 검토한다는 보도가 나왔다([Calcalist](https://www.calcalistech.com/ctechnews/article/rz31q82b5)).
  - 2025-09에 CEO를 교체했다([Pinecone](https://pinecone.io/blog/growing-ai-ambitions)).
  - 2026-08-06에 에이전트용 '지식 엔진' Nexus를 GA했다([Unite.AI](https://www.unite.ai/pinecones-nexus-knowledge-engine-for-ai-agents-reaches-general-availability/)). The New Stack은 이를 두고 "RAG를 대중화한 회사가 이제 RAG에 반대로 베팅한다"고 평했다([The New Stack](https://thenewstack.io/pinecone-nexus-rag-obsolete/)).
- Zilliz(Milvus)는 2026-06에 'Vector Lakebase'로 통합 데이터 플랫폼을 표방하고 나섰다([theATDB](https://www.theatdb.com/news/zilliz-launches-vector-lakebase-extending-the-worlds-most-adopted-vector-database-into-a-unified-data-platform-for-ai)).

## Oracle과 Microsoft는 AI 기능을 기존 엔진에 넣어 설치 기반을 지킨다

Oracle과 Microsoft는 AI 기능을 추가 요금 없이 기존 엔진에 넣어 온프레미스 설치 기반을 붙잡는다. 두 회사가 갈리는 곳은 AI 기능을 어디에 두느냐다.

| 항목 | Oracle | Microsoft |
|---|---|---|
| AI 기능의 위치 | 임베딩 모델과 에이전트 프레임워크까지 DB 안에 넣고, 클라이언트 도구는 그 기능을 여는 문으로 쓴다 | 엔진에는 모델을 두지 않고, AI 기능을 SSMS·VS Code의 Copilot과 Azure AI Foundry 쪽에 둔다 |
| 클라이언트 도구 업체가 보는 경쟁 방식 | DB 기능과 무료 도구를 함께 내민다 | 도구 시장에서 직접 경쟁한다 |

### Oracle은 23ai 계열에 26ai라는 이름을 붙이고 에이전트 기능을 더했다

Oracle은 2025-10-14 Oracle AI World에서 Oracle AI Database 26ai를 발표했다. 이때 제품명도 'Oracle Database'에서 'Oracle AI Database'로 바꿨다. 26ai는 23ai를 잇는 장기 지원 릴리스다. 기존 23ai 사용자는 2025-10 RU만 적용하면 되고, DB 업그레이드나 애플리케이션 재인증이 필요 없다([Oracle blog](https://blogs.oracle.com/database/oracle-announces-oracle-ai-database-26ai), [InfoWorld](https://infoworld.com/article/4072128/oracle-targets-agentic-use-cases-with-ai-database-26ai.html)). 26ai의 실체는 23ai 코드 계열(23.26.x)에 RU로 기능을 더하고 이름을 바꾼 것에 가깝다.

온프레미스는 사정이 다르다. 온프레미스 Enterprise Edition은 2026-01 RU(23.26.1)에서야 Linux x86-64용으로 GA됐다([Oracle blog](https://blogs.oracle.com/database/ga-of-oracle-ai-database-26ai-for-linux-x86-64-on-premises-platforms), [virtual-dba](https://virtual-dba.com/blog/oracle-ai-database-26ai-now-available-for-linux-x86-64-on-premises/)). The Register는 다음과 같이 전했다([The Register](https://www.theregister.com/2026/02/02/oracles_on_prem_ai_database/)).

- 23ai는 엔지니어드 시스템 밖 온프레미스에는 널리 풀리지 않았다.
- Windows를 포함한 다른 OS의 출시 시점은 불분명하다.

온프레미스 고객은 23ai 클라우드 GA로부터 약 20개월 뒤에야, 그것도 한 플랫폼에서만 'AI 데이터베이스'를 받은 셈이다.

26ai가 새로 더한 층은 에이전트 기능이다([InfoWorld](https://infoworld.com/article/4072128/oracle-targets-agentic-use-cases-with-ai-database-26ai.html)).

- Select AI Agent: DB 안의 기본 도구에 REST와 MCP 서버로 연결한 외부 도구를 더해, 에이전트를 만들고 운영하는 프레임워크다.
- AI Private Agent Factory: 컨테이너로 된 사설 에이전트를 코드 없이 배포하는 도구다.
- MCP 서버 지원: 에이전트가 DB 데이터를 질의하게 한다.

다만 Oracle은 발표 시점에 일부 기능을 GA가 아니라 '계획'으로 소개했다([Oracle 보도자료](https://www.oracle.com/news/announcement/ai-world-database-26ai-powers-the-ai-for-data-revolution-2025-10-14/)).

에이전트 기능의 바탕은 Select AI다. Select AI에서는 `DBMS_CLOUD_AI.CREATE_PROFILE`로 프로필을 만든다. 프로필은 LLM 제공자, 자격 증명, 대상 객체 목록(`object_list`)을 묶는다. Select AI는 이 프로필 위에서 `runsql`·`showsql`·`narrate`·`chat`·`explainsql` 동작을 제공한다([Oracle Autonomous Database(ADB) 문서](https://docs.oracle.com/en/cloud/paas/autonomous-database/serverless/adbsb/examples-using-select-ai.html)). Select AI가 받는 LLM 제공자는 다음과 같다([Oracle blog](https://blogs.oracle.com/machinelearning/select-ai-by-release-a-quick-guide-to-26ai-and-19c-capabilities)).

- OCI Generative AI
- OpenAI
- Azure OpenAI
- Cohere
- Google
- Anthropic
- Hugging Face
- AWS
- 사설 LLM

Oracle은 모델을 고르지 않고, 모델이 데이터에 닿는 진입점을 쥐는 전략을 택했다. 같은 논리로 Oracle은 Database@AWS·@Azure·@Google Cloud에 26ai와 Autonomous AI Lakehouse를 싣고 있다([heise](https://heise.de/-10778842)). Google은 Next '26에서 Oracle AI Database@Google Cloud를 20개 리전으로 늘리고 관리형 MCP 프리뷰를 붙인다고 밝혔다([Google Cloud](https://cloud.google.com/blog/products/databases/whats-new-for-google-cloud-databases-at-next26)). 대형 클라우드 업체는 Oracle 고객을 자사 DB로 옮기려 하면서도 Oracle과 협력한다.

Oracle은 자사의 무료 클라이언트 도구로 AI 기능을 퍼뜨린다. 2025-07 SQLcl 25.2에 MCP 서버가 들어갔다. SQLcl MCP 서버의 특징은 다음과 같다([Oracle blog](https://blogs.oracle.com/ai-and-datascience/ai-driven-sql-made-simple-with-oracle-sqlcl-mcp-server), [Oracle MCP](https://www.oracle.com/mcp/)).

- 저장해 둔 SQLcl 접속을 그대로 쓴다.
- `list-connections`·`connect`·`disconnect`·`run-sql`·`run-sqlcl` 도구를 노출한다.
- 19c부터 23ai까지 동작한다.

Oracle의 MCP 제공 방식은 세 가지다([Oracle MCP](https://www.oracle.com/mcp/)).

- SQLcl: 로컬 stdio
- OCI Database Tools MCP Server: 관리형
- ORDS 내장 엔드포인트: HTTPS로 연다.

SQL Developer for VS Code는 SQLcl MCP 서버를 GitHub Copilot에 자동으로 등록한다. 그러면 에이전트가 SQL과 PL/SQL을 실행하고 데이터 딕셔너리를 읽을 수 있다([Oracle](https://www.oracle.com/database/sqldeveloper/vscode/)). Autonomous AI Database에는 MCP 서버가 내장됐다. 이 MCP 서버는 19c와 26ai 모두에서 GA로 보고됐고, 역할·접근 제어 목록(ACL)·Virtual Private Database(VPD)로 최소 권한을 건다([Oracle ML blog](https://blogs.oracle.com/machinelearning/announcing-the-oracle-autonomous-ai-database-mcp-server)).

업계 평가는 엇갈린다.

- Constellation Research는 Oracle의 전략을 "늦었지만 오래갈" 전략이라 불렀다([heise](https://heise.de/-10778842)).
- Tony Baer는 "에이전트를 DB 안의 일급 시민으로 만든다"고 평했다([InfoWorld](https://infoworld.com/article/4072128/oracle-targets-agentic-use-cases-with-ai-database-26ai.html)).
- 제3자 지원 업체 Spinnaker의 Martin Biggs는 19c 사용자들이 "당분간 아무것도 보고 있지 않다"고 말했다([The Register](https://www.theregister.com/2026/02/02/oracles_on_prem_ai_database/)).

Martin Biggs의 말도 이해관계자의 발언이지만 지원 일정과는 맞는다. 다음 일정은 Martin Biggs 발언과 같은 The Register 기사에 나온다.

- 19c Premier 지원: 2029-12-31까지
- 19c Extended 지원: 2032-12-31까지
- 26ai Premier 지원: 2031년까지

### Microsoft는 엔진 밖의 모델을 호출하고 AI 기능을 도구와 클라우드에 둔다

SQL Server 2025는 모델을 엔진 안에서 돌리지 않는다. `EXTERNAL MODEL` 객체로 외부 엔드포인트를 정의하고, `sp_invoke_external_rest_endpoint`로 Azure OpenAI 같은 REST 서비스를 부른다([What's new](https://learn.microsoft.com/en-us/sql/sql-server/what-s-new-in-sql-server-2025)).

Microsoft는 출시 일정도 기능에 맞춰 늦추지 않았다. 미완성 기능은 DB별 `PREVIEW_FEATURES` 스위치 뒤에 두고 정식판(RTM)을 내보냈다. 판매 조건도 다음과 같이 넓혔다(같은 What's new 문서).

- Standard 에디션 상한을 4소켓·32코어·버퍼 풀 256GB로 올렸다.
- 무료 Standard Developer 에디션을 새로 냈다.

Microsoft의 DB 전략은 다음 세 엔진을 축으로 한다.

- SQL 엔진 계열
- Cosmos DB
- PostgreSQL

Microsoft는 2025-11 Ignite에서 Azure HorizonDB를 발표했다. HorizonDB는 PostgreSQL 호환 분산 서비스로, DiskANN과 AI 모델 관리를 내장했다. 오픈소스 PostgreSQL 대비 처리량 3배라는 수치는 업체 주장이다([The Register](https://www.theregister.com/2025/11/19/microsoft_azure_horizondb/)). 그러나 HorizonDB는 2026-08에도 공개 프리뷰였다. Azure DB 담당 부사장(CVP) Shireesh Thota는 GA를 "2026년 하반기"로 잡았다고 했지만, 2026-10-20까지 GA 발표는 찾지 못했다([InfoWorld](https://www.infoworld.com/article/4207403/microsofts-postgresql-alternative-horizondb-worth-the-wait.html)).

HorizonDB를 두고 다음 평가가 나왔다(앞의 The Register·InfoWorld 두 기사).

- Info-Tech는 HorizonDB가 SQL Server 수요를 잠식할 수 있다고 봤다.
- David Linthicum은 긴 프리뷰가 서비스 수준 협약(SLA)과 가격을 불확실하게 만든다고 지적했다.

한편 2025년 Ignite에서 GA된 Fabric의 SQL database에는 Copilot 도구가 붙었다. 이 도구는 성능을 진단하고 누락·미사용 인덱스를 찾는다([Fabric blog](https://blog.fabric.microsoft.com/es-es/blog/copilot-and-query-editor-in-sql-database-in-fabric-ga-update?ft=All)).

## 분석 플랫폼은 PostgreSQL 회사를 샀고 대형 클라우드 업체는 AI 변환 도구로 Oracle 이관 비용을 낮춘다

분석 플랫폼 두 곳은 PostgreSQL 회사를 사들이고 에이전트 플랫폼으로 바뀌었다. 대형 클라우드 업체는 운영 DB 전반에 MCP 서버를 내고, AI 변환 도구로 Oracle을 떠나는 비용을 낮춘다.

### Databricks와 Snowflake는 PostgreSQL 회사를 사서 1년 안에 제품을 냈다

분석 플랫폼 두 곳이 3주 사이에 PostgreSQL 회사를 샀다.

- Databricks는 2025-05-14에 Neon 인수를 발표했다. 금액은 보도 기준 약 10억 달러다. Databricks는 Neon에서 만들어진 DB의 80% 이상을 AI 에이전트가 만들었다는 수치를 근거로 들었다. 이 수치는 Databricks가 인용한 Neon 내부 집계다([Databricks](https://databricks.com/company/newsroom/press-releases/databricks-agrees-acquire-neon-help-developers-deliver-ai-systems)).
- Snowflake는 2025-06-02에 Crunchy Data 인수를 발표했다. 금액은 공개하지 않았고, 약 2억 5천만 달러라는 보도가 있다([Snowflake](https://snowflake.com/en/news/press-releases/snowflake-acquires-crunchy-data-to-bring-enterprise-ready-postgres-offering-to-the-ai-data-cloud/), [Constellation Research](https://www.constellationr.com/insights/news/snowflake-makes-its-postgres-move-acquires-crunchy-data)).

두 회사 모두 1년 안에 제품을 냈다.

- Databricks Lakebase
  - 2026-02-03에 AWS에서 GA됐다. PostgreSQL 17, pgvector, 최대 8TB에 즉시 브랜칭과 scale-to-zero를 갖췄다. Databricks는 이 제품을 에이전트 개발용으로 홍보한다([Databricks](https://www.databricks.com/blog/databricks-lakebase-generally-available)).
  - Azure에서는 2026-03에 GA됐다([Databricks](https://www.databricks.com/blog/azure-databricks-lakebase-generally-available)).
- Snowflake Postgres
  - 릴리스 노트상 2026-02-24에 GA됐다([Snowflake](https://docs.snowflake.com/release-notes/2026/other/2026-02-24-snowflake-postgres-ga)).
  - Summit 2026(2026-06) 정리 기사들이 소개한 private link, 고객 관리 키, Iceberg 읽기·쓰기(PGLake)는 GA 뒤에 더해진 기능으로 봐야 한다([SELECT](https://select.dev/blog/snowflake-summit-2026-what-actually-shipped-and-what-it-means)).

Supabase는 2025-10에 50억 달러 가치로 투자를 받았다([TechCrunch](https://techcrunch.com/2025/10/03/supabase-nabs-5b-valuation-four-months-after-hitting-2b/)). 새 Supabase DB의 60% 이상을 AI 코딩 에이전트가 만든다는 추정도 있다. 다만 이 추정은 Supabase 공시가 아니라 Sacra 한 곳의 분석이다([Sacra](https://sacra.com/research/supabase-170m-year-growing-221-yoy/)).

이런 '에이전트가 만든 DB' 수치는 작고 금방 지워지는 DB의 생성 건수다. 그러므로 이 수치는 운영 온라인 트랜잭션 처리(OLTP) 시장 점유율이 아니라, 에이전트가 만드는 앱이 어디에 자리 잡는지 보여 주는 신호로 읽어야 한다. Databricks와 Snowflake도 PostgreSQL을 Oracle OLTP의 대체재가 아니라, 레이크하우스 옆에서 에이전트의 상태·기억·앱을 담는 저장소로 설명한다.

### Snowflake와 Databricks는 에이전트 플랫폼이 됐다

분석 플랫폼은 그 자체가 에이전트 플랫폼으로 바뀌었다. Snowflake의 움직임은 다음과 같다.

- 2025-11-04: Cortex Agents·Cortex AI Functions·Snowflake Intelligence를 GA했다([Snowflake](https://docs.snowflake.com/release-notes/feature-releases-2025)).
- 2025-11-04: 관리형 MCP 서버를 GA했다. 도구는 자연어→SQL 도구인 Cortex Analyst와 Cortex Search였고, 역할 기반 접근 제어(RBAC)와 OAuth로 묶였다([Snowflake](https://docs.snowflake.com/release-notes/2025/other/2025-11-04-cortex-agents-mcp)).
- 2026-08: Native App이 자기 에이전트와 MCP 서버를 함께 싣고 배포할 수 있게 됐다([Snowflake](https://docs.snowflake.com/release-notes/2026/other/2026-08-07-native-apps-agents-mcp-ga)).
- Summit 2026: 정리 기사에 따르면 Cortex Code는 'CoCo'로 이름을 바꾸고, VS Code를 포크한 데스크톱 앱과 Claude 플러그인을 내놨다. 사용자별로 하루 AI 지출에 한도를 거는 기능도 발표됐다([SELECT](https://select.dev/blog/snowflake-summit-2026-what-actually-shipped-and-what-it-means)).

데이터 플랫폼 업체인 Snowflake가 데스크톱 개발 도구 영역까지 들어왔다.

Databricks는 Genie, AI Search, Databricks SQL, Unity Catalog 함수를 관리형 MCP 서버로 열었다(공개 프리뷰). 권한은 Unity Catalog로 건다. Databricks 자체 코딩 에이전트 Genie Code가 외부 MCP 서버를 쓰려면 관리자 승인을 받아야 한다([Databricks 문서](https://docs.databricks.com/aws/en/agents/mcp-tools/managed-mcp), [Genie Code MCP](https://docs.databricks.com/aws/en/assistant/mcp)).

### 대형 클라우드 업체는 운영 DB 전반에 MCP 서버를 냈다

Google은 2026-04-23에 AlloyDB·Bigtable·Cloud SQL·Firestore·Spanner의 관리형 원격 MCP 서버를 GA했다. 같은 발표에서 다음 두 가지도 내놨다([Google Cloud](https://cloud.google.com/blog/products/databases/whats-new-for-google-cloud-databases-at-next26)).

- 40개 넘는 DB를 지원하는 오픈소스 MCP Toolbox for Databases 1.0
- AlloyDB·Cloud SQL·Spanner용 자연어→SQL 도구 QueryData(프리뷰)

QueryData의 "100%에 가까운 자연어→SQL 정확도"는 Google의 주장일 뿐 독립 검증은 없다. MCP Toolbox 저장소는 지원 대상에 Oracle을 올려 두고, `list_tables`·`execute_sql` 같은 기본 도구를 제공한다([GitHub](https://github.com/googleapis/genai-toolbox)).

AWS는 2025-06에 Aurora PostgreSQL·MySQL·DSQL, DynamoDB, Neptune 등을 위한 AWS Labs MCP 서버를 정리해 내놨다([AWS Database Blog](https://aws.amazon.com/blogs/database/supercharging-aws-database-development-with-aws-mcp-servers)). DSQL MCP 서버는 기본이 읽기 전용이고, `--allow-writes`를 줄 때만 쓰기 트랜잭션을 연다([Classmethod](https://dev.classmethod.jp/articles/tried-amazon-aurora-dsql-mcp-server/)). AWS는 re:Invent 2025에서 인덱스당 최대 20억 개 벡터를 담는 S3 Vectors를 GA했다([Caylent](https://caylent.com/blog/aws-reinvent-2025-every-ai-announcement-including-amazon-nova-2-and-kiro)).

### AI 변환 도구는 Oracle 이관 비용 가운데 코드 변환 부분을 줄인다

대형 클라우드 업체는 AI로 Oracle을 떠나는 비용을 낮추려 한다. 모든 업체가 Oracle을 이관 출발지로 명시하고, 모두 사람의 검토를 전제로 한다.

- AWS DMS Schema Conversion은 규칙 기반 변환이 실패한 항목을 생성형 AI로 변환한다([AWS DMS 문서](https://docs.aws.amazon.com/dms/latest/userguide/schema-conversion-convert.databaseobjects.html)).
  - 대상: 계층 질의, MERGE 변형, PIVOT, MODEL, FORALL, BULK COLLECT, pipelined 함수 등
  - 제외: 트리거, 동적 SQL, 인덱스, 제약, 컬럼 데이터 타입
  - 문서는 결과가 확률적이므로 "반드시 검토하고 검증해야 한다"고 적는다.
- Google DMS의 Oracle→PostgreSQL·AlloyDB 변환 작업 공간에서는 Gemini가 규칙 기반 변환 결과를 고친다. Gemini는 사용자가 고친 내용을 학습해 다른 객체에 같은 수정을 제안한다([Google DMS 문서](https://docs.cloud.google.com/database-migration/docs/oracle-to-postgresql/code-conversion-with-gemini)).
- Microsoft는 Oracle→SQL Server용 SSMA에 Copilot 보조 변환을 공개 프리뷰로 붙였고, 2026-02 SSMA 10.5에서 이 기능을 확대했다([Redmond Mag](https://redmondmag.com/blogs/redmond-dispatch/2026/02/microsoft-releases-sql-server-migration-assistant.aspx)).
- Microsoft는 VS Code PostgreSQL 확장에 Oracle 이관 프리뷰를 넣었다. 이 기능은 임시 PostgreSQL DB로 검증한다([Azure Updates](https://mc.merill.net/message/AZ523593)).

시장 지표도 같은 방향을 가리킨다.

- 2026-01 DB-Engines에서 Oracle은 1237.34점으로 1위, PostgreSQL은 666.27점으로 4위였다([DB-Engines](https://db-engines.com/en/ranking)).
- 2026년 상반기 점수 증가는 PostgreSQL이 +21.97로 가장 컸다([Redgate](https://www.red-gate.com/de/our-company/newsroom/press-releases/db-engines-postgresql-leads-h1-2026-database-growth-as-data-platforms-gain-momentum/)).

Oracle은 여전히 1위지만 흐름은 PostgreSQL 쪽이다. AI는 이 이동 비용 가운데 코드 변환 부분을 줄인다. 다만 AWS의 제외 목록에서 보듯 PL/SQL이 무거운 부분은 여전히 사람이 맡는다.

## MCP가 DB 접근의 표준이 되자 경쟁은 에이전트가 무엇을 다룰 수 있는지로 옮겨 갔다

2025년과 2026년 상반기를 거치며 MCP는 DB 접근의 사실상 표준이 됐다. 통로가 같아지자 업체들은 에이전트가 무엇을, 누구 권한으로 다룰 수 있는지를 두고 경쟁한다.

### MCP 서버와 AI 기능은 DB 업체와 GUI 도구 전반으로 퍼졌다

앞 절의 Oracle·Microsoft·Snowflake·Databricks·Google·AWS 외에 다음 업체도 MCP 서버를 냈다.

- MongoDB: Atlas·Community·Enterprise Advanced 공통([MongoDB Docs](https://www.mongodb.com/docs/mcp-server/overview/))
- MariaDB([MariaDB](https://mariadb.com/resources/blog/build-smarter-with-mariadb-mcp-server-ai-ready-vector-enabled/))
- PlanetScale: 2026-02에 냈다. DDL에는 사람의 확인을 강제한다([PlanetScale](https://planetscale.com/changelog/mcp-server)).

ClickHouse는 2025-11에 LibreChat을 인수해 '에이전틱 데이터 스택'을 꾸렸다([ClickHouse](https://clickhouse.com/blog/clickhouse-acquires-librechat)). 범용 GUI 도구도 같은 기능 세트로 모였다.

- pgAdmin 9.13(2026-03-05): 질의 도구 안 AI 채팅, 보안·스키마·성능 AI 보고서, EXPLAIN 계획 AI 해설을 넣었다([pgAdmin](https://pgadmin.org/docs/pgadmin4/9.13/release_notes_9_13.html)).
- DBeaver 26.1(2026-06-08): AI 채팅에서 외부 MCP 도구를 사용자 확인과 허용 목록 아래에서 쓰게 했다([DBeaver](https://dbeaver.com/dbeaver-ultimate-26-1/)).

### MCP 도구 설계는 자유 SQL 콘솔형과 큐레이션형으로 나뉜다

통로가 표준화되자 MCP 서버가 노출할 도구를 설계하는 방식이 두 갈래로 나뉘었다.

- 자유 SQL 콘솔형: SQLcl의 `run-sql`, MCP Toolbox의 `execute_sql`, DSQL의 `transact`
- 큐레이션된 거버넌스형
  - Snowflake는 의미 계층(semantic view) 위의 Cortex Analyst·Search만 노출한다.
  - Microsoft의 SQL MCP Server는 Data API builder 기반이다. 물리 테이블이 아니라 정의된 엔티티를 노출하고, REST·GraphQL·MCP가 같은 역할 권한을 공유한다.

Microsoft는 SQL MCP Server에서 자연어→SQL을 설계 단계에서 뺐다. Microsoft는 "모델은 결정적이지 않고, 복잡한 질의일수록 미묘한 오류를 만들기 쉽다"는 이유를 들었다([Techstrong](https://techstrong.ai/features/microsoft-brings-sql-to-the-agentic-era-with-sql-mcp-server/)).

벤치마크가 이 판단을 뒷받침한다. Spider 2.0을 발표한 논문(ICLR 2025)에서 o1-preview 기반 코드 에이전트는 21.3%를 풀었다. 이 수치는 Spider 1.0의 91.2%, BIRD의 73.0%와 대비된다([arXiv](https://arxiv.org/abs/2411.07763v2)). 2026-10 현재 리더보드 최고 점수는 확인하지 못했다. 실제 기업 스키마의 규모와 SQL 방언이 들어가면 정확도가 크게 떨어진다.

업계는 의미 계층으로 대응한다. Snowflake 주도로 2025-10에 출범한 Open Semantic Interchange(OSI)에는 2025-11에 Amazon·Google·DataHub 등이 합류했다([Atlan](https://atlan.com/know/snowflake/snowflake-semantic-views/)). Fabric Data Warehouse Copilot은 자연어→SQL에 테이블 데이터가 아니라 테이블·컬럼 이름 같은 메타데이터만 쓴다([Microsoft Learn](https://learn.microsoft.com/fabric/data-warehouse/copilot)). MongoDB Compass의 자연어 질의에는 아직 "실험 기능이며 부정확할 수 있다"는 표기가 붙어 있다([MongoDB Docs](https://www.mongodb.com/docs/compass/query-with-natural-language/)).

### 보안 사고 뒤 업계는 DB 권한을 보안 경계로 삼았다

보안 사고가 설계를 바꿨다. 2025-07 보안 업체 General Analysis가 Supabase MCP 시연을 공개했다. 시연에서 에이전트는 행 수준 보안(RLS)을 우회하는 `service_role`로 돌았고, 다음 순서로 움직였다.

1. 고객 지원 티켓에 심긴 지시를 따랐다.
2. 비공개 `integration_tokens` 테이블을 읽었다.
3. 읽은 내용을 티켓에 다시 썼다.

Simon Willison은 비공개 데이터, 믿을 수 없는 입력, 밖으로 내보내는 경로가 한 에이전트에 겹치는 이 구조를 'lethal trifecta'라 불렀다. 이 사례는 실제 유출이 아니라 시연이었다([Simon Willison](https://simonwillison.net/2025/Jul/06/supabase-mcp-lethal-trifecta/)). Supabase는 읽기 전용 모드와 프로젝트 범위 제한을 추가했다([Supabase](https://supabase.com/blog/defense-in-depth-mcp)).

더 직접적인 교훈은 Anthropic의 참조용 Postgres MCP 서버에서 나왔다. 이 서버는 모든 질의를 `BEGIN TRANSACTION READ ONLY`로 연 트랜잭션 안에서 실행했다. Datadog은 이 방식을 우회하는 방법을 보였다. `COMMIT; DROP SCHEMA public CASCADE;`처럼 먼저 COMMIT으로 트랜잭션을 끝내고 다음 문장을 이어 붙이면, 그 문장은 읽기 전용 트랜잭션 밖에서 실행된다. 이 서버는 이미 보관 처리됐지만 npm 패키지에는 수정이 반영되지 않았고, 여전히 매주 약 2만 1천 번 내려받힌다([Datadog Security Labs](https://securitylabs.datadoghq.com/articles/mcp-vulnerability-case-study-SQL-injection-in-the-postgresql-mcp-server/)).

Microsoft는 이 교훈을 문서로 정리했다. SSMS 22의 GitHub Copilot은 두 모드로 나뉜다.

- Ask 모드: 읽기 전용 단일 응답
- Agent 모드: 22.7부터 승인을 받아 질의와 스키마 변경까지 실행한다. 기본이 `READ_ONLY`인 로컬 MCP 서버 `sql-tools`를 쓴다. 승인 범위는 한 번·세션·항상 중에서 고른다. 사용자가 취소해도 이미 실행된 질의는 롤백되지 않는다([Microsoft Learn](https://learn.microsoft.com/en-us/ssms/github-copilot/agent-mode)).

핵심은 다음 한 문장이다. **"보안 경계는 Copilot의 승인 체계가 아니라 SQL Server의 권한 집행이다."** 또한 DB 확장 속성에 저장하는 `CONSTITUTION.md`에 `agentExecuteAsUser`를 지정하면, SSMS는 Copilot 질의를 `EXECUTE AS`로 그 저권한 사용자 권한에서 실행한다([Microsoft Learn](https://learn.microsoft.com/en-us/ssms/github-copilot/execution-context)).

업계가 모인 방어 패턴은 네 겹이다.

- DB 권한을 실제 보안 경계로 삼는다.
- 기본값을 읽기 전용으로 둔다.
- 쓰기와 DDL에는 사람의 승인을 받는다.
- 예산 한도를 건다. 예: Snowflake의 사용자별 AI 지출 한도, Databricks의 AI Gateway 지출 상한

래퍼 수준의 읽기 전용이나 프롬프트에 적은 규칙은 이 네 겹에 들지 않는다.

## 국산 DBMS 업체는 엔진 AI 기능보다 소버린 AI 플랫폼과 공공 조달을 앞세운다

국산 업체의 AI 대응은 엔진 기능보다 플랫폼과 조달 위치를 앞세운다. 티맥스티베로의 움직임은 다음과 같다.

- 2025-10에 AI 중심 환경에 맞춰 브랜드를 개편했다. 내건 방향은 클라우드 전환, 오픈소스 생태계 강화, 데이터 기술 고도화다([바이라인네트워크](https://byline.network/2025/10/27-505/)).
- 'AI 기반 통합 데이터 플랫폼'으로 공공부문 SW 어워드에서 과기정통부 장관상을 받았고, 오픈메이드컨설팅과 AI로 SQL 튜닝을 자동화하는 협약을 맺었다([Tibero 블로그](https://blog.tibero.com/)).
- OKESTRO OPUS 2026에서 데이터와 AI를 잇는 '아울AI'를 소버린 AI 데이터 플랫폼으로 소개했다([tech42](https://www.tech42.co.kr/%ED%8B%B0%EB%A7%A5%EC%8A%A4%ED%8B%B0%EB%B2%A0%EB%A1%9C-%EA%B5%AD%EC%82%B0-%ED%81%B4%EB%9D%BC%EC%9A%B0%EB%93%9C%C2%B7db-%EC%97%B0%EA%B3%84-%ED%99%95%EB%8C%80ai-%EB%8D%B0%EC%9D%B4/)).

눈여겨볼 대목이 있다. Tibero가 소개한 매뉴얼 RAG 챗봇은 Tibero 엔진이 아니라 PostgreSQL 기반 OpenSQL의 pgvector로 만들었다([Tibero 블로그](https://blog.tibero.com/)). Tibero 엔진 자체의 벡터 기능 발표는 찾지 못했다.

CUBRID는 2025-10 보도에서 벡터 DB 개발을 핵심 전략으로 제시하고 2027년 베타, 2028년 GA를 계획했다(2차 보도)([CB Insights 경유](https://www.cbinsights.com/company/cubeit)). Altibase의 AI 전략은 자료를 찾지 못했다.

국산 업체의 이 선택은 국내 시장 구조와 맞물린다. 정부는 'AI 제품·서비스 확인 제도'로 공공조달에서 우선 고려하는 방안을 추진하고 있다. AI 기본법에서 공공분야의 AI 도입을 촉진하는 조항 등은 2026-05 보도 기준으로 2026-07-21에 시행될 예정이었다. 실제 시행 여부는 확인하지 못했다([Daum 뉴스](https://v.daum.net/v/20260521120203816), [신김 뉴스레터](https://www.shinkim.com/kor/media/newsletter/3301)). 국산 업체에게는 글로벌 업체와 엔진 속 벡터 성능으로 겨루기보다, 소버린 AI와 공공 조달 자격을 묶어 파는 편이 합리적이다.

Tibero는 포스코그룹 세미나 등에서 Oracle→Tibero 전환 사례와, ProSync 변경 데이터 캡처(CDC)를 쓰는 Oracle-Tibero 액티브 재해 복구(DR)를 계속 강조한다([Tibero 블로그](https://blog.tibero.com/author/tibero26/page/7/)). 그래서 국내 Oracle 고객은 두 방향에서 이탈 압력을 받는다. 하나는 대형 클라우드 업체의 AI 변환 도구고, 다른 하나는 공공 조달 자격을 앞세운 국산 DBMS다. Oranda처럼 Oracle 전용 도구에게 국산 업체의 AI 기능은 직접 경쟁 상대가 아니다. 하지만 공공 부문 Oracle 설치 기반이 줄어드는 속도는 Oranda의 시장 크기를 정한다.

## Oranda의 차별점은 19c 지원, 정책 관문, 메타데이터 맥락이다

'DB와 대화하기'는 Oracle이 무료로 주는 기능이 됐다. 그래서 Oranda의 가치는 19c 지원, 정책 관문, 메타데이터 맥락에 남는다.

### Oranda는 1.0에서 AI를 구현하지 않고 확장 지점과 안전 장치를 둔다

D-04(2026-10-19)는 질의 작성 챗봇이 목표지만 1.0에서는 구현하지 않는다는 결정이다. 대신 APP-09로 다섯 가지 확장 지점을 1.0 구조에 둔다.

- 접속별 메타데이터 제공
- 편집기 조작: 현재 문장 읽기와 제안 SQL의 차이 미리보기
- 정책 관문: AI가 만든 SQL도 똑같이 통과한다.
- 도킹 패널 슬롯
- 외부 전송 동의 훅

안전 장치는 다음과 같다.

| ID | 내용 |
|---|---|
| SAFE-01 | 운영 라벨 |
| SAFE-02 | 읽기 전용 프로필. 파서로 DML·DDL·PL/SQL을 막고 세션을 `SET TRANSACTION READ ONLY`로 연다 |
| SAFE-03 | 운영 접속의 DML·DDL 실행 확인 |
| SAFE-05 | `module`·`action`에 도구명을 남겨 서버 감사에서 식별되게 한다 |
| SAFE-06 | 비밀번호를 OS 자격 증명 저장소에 둔다 |
| SAFE-07 | Diagnostics/Tuning Pack 보유를 선언하지 않은 접속에서는 Active Session History(ASH)·`DBA_HIST_*` 질의를 자동으로 보내지 않는다 |

아래 표는 업계 흐름에 비춰 이 계획을 점검한 결과다.

| Oranda 현재 계획 | 업계에서 확인된 근거 | 권고 |
|---|---|---|
| D-04: 1.0에는 AI를 만들지 않고 APP-09 확장 지점만 둔다 | Oracle이 SQLcl MCP와 VS Code Copilot 연동을 무료로 배포. 벡터 인덱스 상태, pgai, 제품명이 아직 바뀌고 있음 | 유지. 다만 APP-09 정책 관문에 '무엇을 실행하나'뿐 아니라 '누구 권한으로 실행하나'를 1.0 구조에 넣는다 |
| 에이전트 세션을 `SET TRANSACTION READ ONLY`로 열고, 저권한 계정은 '안내'만 한다 | Datadog의 읽기 전용 래퍼 우회. Microsoft는 "권한 집행이 경계"라고 명시 | 에이전트·MCP 접속에는 저권한 계정(또는 프록시 사용자)을 필수로 하고, 파서와 읽기 전용 트랜잭션은 겹 방어로 둔다 |
| Select AI 연결을 AI 보조의 첫 단계로 둔다 | Select AI는 ADB·26ai 중심이고 19c 온프레미스 제공 경로는 불명확. RAG는 26ai 전용 | ADB·26ai 사용자용 선택지로 두고, 19c용 경로는 Oranda가 직접 갖는다 |
| 서버에 보조 객체를 설치하지 않는다 | SSMS는 에이전트 지침을 DB 확장 속성(`CONSTITUTION.md`)에 저장 | 기본은 로컬 접속별 지침. 팀 공유가 필요하면 기존 주석과 23ai annotation을 읽기만 한다 |
| Oranda MCP 서버 | SQLcl MCP와 Google MCP Toolbox가 이미 Oracle을 지원 | 저장 접속, 운영 접속 제외, SAFE 규칙의 정책 관문, 팩 보호, 로컬 기록으로 차별화 |

### Oracle이 무료로 주는 기능과 겹치는 기능으로는 이길 수 없다

"Oracle DB에 자연어로 묻고 SQL을 실행한다"는 기능은 이제 Oracle이 무료로 주는 기본 기능이다. SQLcl MCP와 SQL Developer for VS Code의 Copilot 자동 등록이 나온 뒤로 그렇게 됐다. DBeaver와 pgAdmin도 같은 기능을 기본으로 싣는다. 독립 채팅 창 하나로는 Oranda가 이길 수 없다.

그래서 1.0에서 AI를 미룬 D-04 결정은 더 타당해졌다. 업계 기능이 아직 흔들리고 있다는 점도 기다릴 이유가 된다.

- SQL Server 벡터 인덱스는 GA 11개월째 프리뷰다.
- TigerData는 DB 안 LLM 호출을 거둬들였다.
- Snowflake는 1년 만에 제품명을 다시 바꿨다.

다만 AI 구현을 미뤄도 손해가 없으려면, APP-09의 확장 지점이 업계가 수렴한 설계와 맞아야 한다. 업계가 수렴한 설계는 다음과 같다.

- 제안 SQL은 차이 미리보기를 거쳐 Accept/Undo로 넣는다. 예: VS Code mssql([Microsoft](https://devblogs.microsoft.com/azure-sql/vscode-mssql-june-2026/))
- 보낼 객체 범위를 고른다. 예: Select AI `object_list`
- 외부 도구는 확인과 허용 목록을 거친다. 예: DBeaver

이 목록에서 빠지기 쉬운 항목은 실행 신원이다. 나중에 에이전트용 저권한 접속을 따로 붙이려면 접속 관리자와 세션 모델을 고쳐야 한다. 그러므로 '이 실행은 어느 신원으로 하나'를 지금 정책 관문의 입력으로 넣어 두는 편이 비용이 적다.

### 19c가 2029년까지 주력이라는 사실이 AI 기능의 설계를 정한다

Oracle의 AI 기능은 대부분 26ai와 Autonomous에 있지만, 온프레미스 고객의 DB는 19c에 머물 이유가 충분하다.

- 19c 지원은 2032년까지 이어진다.
- 26ai 온프레미스는 2026-01에야 Linux x86-64 한 플랫폼에서 시작됐다.

아래 표는 연구 노트가 뒷받침하는 범위만 정리한다.

| 기능 | 19c | 23ai | 26ai (23.26.x) | Autonomous |
|---|---|---|---|---|
| VECTOR 타입·AI Vector Search | 없음(백포트 근거 못 찾음) | 있음. 19c에서 올렸다면 `COMPATIBLE` 수동 상향 필요(문서 요약 기준) | 있음. Oracle은 추가 비용 없음이라 밝힘 | 26ai에서 있음 |
| Select AI 기본 기능(자연어→SQL·chat·narrate 등) | Oracle은 26ai와 같다고 안내. 온프레미스 제공 경로는 불명확 | 확인 못 함 | 온프레미스 26ai에 OCI 전용이던 개선 포함 | 있음 |
| Select AI RAG·자연어→SQL 피드백 | 없음(벡터에 의존) | 확인 못 함 | 있음 | 26ai에서 있음 |
| 온프레미스 제공 | 있음 | 엔지니어드 시스템 밖에서는 넓게 풀리지 않음 | Linux x86-64만(2026-01, 23.26.1). Windows·AIX 시점 불명 | 해당 없음 |
| SQLcl MCP(클라이언트 측) | 동작 | 동작 | 확인 못 함(같은 23 코드 계열) | 해당 없음 |
| 내장 MCP 서버 | 확인 못 함 | 확인 못 함 | 확인 못 함 | 19c·26ai에서 GA |
| 지원 종료 | Premier 2029-12-31, Extended 2032-12-31 | 26ai로 대체 | Premier 2031 | 해당 없음 |

근거: [Oracle blog(Select AI by release)](https://blogs.oracle.com/machinelearning/select-ai-by-release-a-quick-guide-to-26ai-and-19c-capabilities), [Oracle blog(26ai 온프레미스 GA)](https://blogs.oracle.com/database/ga-of-oracle-ai-database-26ai-for-linux-x86-64-on-premises-platforms), [Oracle MCP](https://www.oracle.com/mcp/), [Oracle ML blog](https://blogs.oracle.com/machinelearning/announcing-the-oracle-autonomous-ai-database-mcp-server), [The Register](https://www.theregister.com/2026/02/02/oracles_on_prem_ai_database/).

이 표에서 세 가지 설계 원칙이 나온다.

1. **Oranda의 AI 기능은 19c에서 서버 쪽 AI 없이 완결돼야 한다.**
   - 19c 온프레미스에서는 Select AI도 벡터도 기대할 수 없다. 그러므로 LLM 호출 경로와 메타데이터 맥락 구성은 Oranda가 직접 가져야 한다.
   - Select AI는 ADB와 26ai 사용자가 API 키 없이 쓰는 추가 선택지로 둬야 한다.
   - Select AI는 서버에 설정된 제공자로 메타데이터를 보낸다. 그러면 무엇을 밖으로 보낼지 정하는 통제권이 도구에서 DBA로 넘어간다. Oranda는 이 점도 사용자에게 알려야 한다.
2. 기능을 켜고 끄는 판단은 이름이 아니라 능력으로 한다.
   - 26ai는 버전 문자열상 23.26.x로 보인다.
   - 그래서 벡터 메뉴는 버전과 `COMPATIBLE`로, Select AI 메뉴는 `DBMS_CLOUD_AI` 패키지가 있는지로 판단해야 안전하다.
   - 이 판정 규칙은 검색 요약에서 나왔으므로 실제 서버로 확인해야 한다.
3. 벡터 도우미 같은 26ai 기능은 몇 년 동안 소수 사용자용이다. 그러므로 1.x의 AI 투자는 19c에서도 쓸모 있는 다음 기능에 먼저 가야 한다.
   - SQL 설명
   - 오류 풀이
   - 실행 계획 해설
   - 자연어→SQL

### 읽기 전용 트랜잭션은 보안 경계가 아니다

**에이전트와 MCP 접속에는 저권한 계정을 필수로 걸어야 한다.** 이 연구에서 나온 가장 직접적인 수정 사항이다. 현재 계획은 에이전트 세션을 SAFE-02처럼 `SET TRANSACTION READ ONLY`로 열고, 저권한 계정은 쓰도록 '안내'만 한다. 그러나 이 방식으로는 부족하다.

- Datadog 사례는 트랜잭션 수준의 읽기 전용이 우회될 수 있음을 보였다.
- Microsoft는 아예 승인 체계가 아니라 DB 권한이 보안 경계라고 문서에 적었다.

Oracle에도 같은 구조의 구멍이 있을 수 있다. 다음 두 경우는 추론이므로 19c·23ai에서 시험으로 확인해야 한다.

1. SELECT 문 안에서 부른 함수가 `PRAGMA AUTONOMOUS_TRANSACTION`으로 선언돼 있다면, 그 함수는 별도 트랜잭션에서 DML과 COMMIT을 할 수 있다. 그렇다면 파서가 'SELECT'로 분류한 문장이 읽기 전용 트랜잭션 밖에서 데이터를 바꿀 수 있다.
2. 계정에 `UTL_HTTP` 실행 권한과 네트워크 ACL이 열려 있으면, SELECT 한 줄이 그대로 lethal trifecta의 유출 경로가 된다.

두 경우 모두 파서와 읽기 전용 트랜잭션으로는 막지 못하고, 권한으로만 막을 수 있다. 에이전트와 MCP 접속에 쓸 저권한 계정은 다음 조건을 갖춰야 한다.

- 필요한 객체의 SELECT 권한만 있다.
- 부수 효과가 있는 함수의 EXECUTE 권한이 없다.
- 네트워크 ACL이 없다.

Oracle 프록시 사용자는 SSMS `EXECUTE AS`에 대응하는 수단이 될 수 있다. 파서 분류와 읽기 전용 트랜잭션은 저권한 계정 아래의 겹 방어로 둔다.

에이전트 지침을 어디에 저장할지도 정해야 한다. SSMS는 지침과 실행 신원을 DB 확장 속성에 저장한다. 그래서 지침이 DB를 따라다니고 팀 전체에 같은 규칙이 적용된다. Oranda는 "서버에 보조 객체를 설치하지 않는다"는 원칙 때문에 이 방식을 그대로 쓸 수 없다. 남는 선택지는 다음 두 가지다.

- 로컬 접속별 지침 파일: 원칙을 지키지만 팀 사이에 공유되지 않는다.
- 기존 테이블·컬럼 주석이나 23ai annotation에 규칙을 적는 방식: 새 객체를 만들지는 않지만 ALTER 권한이 필요하다.

그러므로 Oranda는 주석과 annotation을 읽기만 하고, 쓰기는 DBA의 결정으로 남기는 편이 원칙과 맞다.

### Oranda MCP 서버는 자유 SQL 콘솔이 아니라 정책 관문이어야 한다

Oranda MCP 서버의 가치는 도구가 이미 가진 정책을 에이전트에게도 똑같이 강제하는 데서 나온다. Oracle 접속을 에이전트에 여는 길은 이미 무료로 여럿 있다. SQLcl MCP는 저장된 접속과 `run-sql`을 노출하고, Google MCP Toolbox는 Oracle에 `execute_sql`을 제공한다. 같은 자유 SQL 콘솔을 하나 더 내도 의미가 없다.

Oranda MCP 서버가 강제할 정책은 다섯 가지다.

- 비밀번호는 OS 자격 증명 저장소(SAFE-06)에 두고 에이전트에 넘기지 않는다.
- 운영 라벨(SAFE-01)이 붙은 접속은 기본으로 노출하지 않는다.
- 사람이 쓴 SQL과 같은 정책 관문(SAFE-02·SAFE-03)을 거치게 한다.
- SAFE-07의 팩 보호를 에이전트가 만든 SQL에도 적용한다. 그러면 에이전트가 무심코 ASH나 `DBA_HIST_*`를 질의해 Diagnostics Pack 라이선스 문제를 만드는 일을 막는다.
- SAFE-05로 에이전트 실행에 별도 `module` 값을 남긴다. 그러면 DBA가 서버 감사에서 사람과 에이전트의 실행을 구분할 수 있다.

SQLcl MCP의 공개 도구 목록은 자유 SQL 실행 중심이다. 그래서 Oracle 라이선스 팩 보호와 운영 접속 차단은 Oracle 전용 도구인 Oranda가 줄 수 있는 차별점이 된다. 단, SQLcl의 RESTRICT 수준 같은 세부 제어는 이 연구에서 확인하지 못했다.

도구 구성은 Microsoft와 Snowflake의 큐레이션형을 따르는 편이 좋다.

- 세션·대기·블로킹·Top SQL·실행 계획처럼 이름 붙은 읽기 전용 DBA 도구를 기본으로 노출한다.
- 자유 SQL은 별도로 허용해야 연다.

PostgreSQL 쪽의 Postgres MCP Pro도 같은 발상으로 인덱스 튜닝, EXPLAIN, 상태 점검을 도구로 노출한다([Smithery](https://smithery.ai/server/crystaldba/postgres-mcp)). lethal trifecta에서 나온 직접적인 규칙도 있다. 같은 에이전트가 비공개 데이터를 읽는 도구와 밖으로 쓰는 도구를 함께 갖지 않게 한다.

자연어→SQL의 성패는 SQL 생성보다 맥락에 달려 있다. Spider 2.0의 결과는 큰 스키마에서 모델이 혼자서는 맞히지 못한다는 뜻이다. 업계가 의미 계층(Snowflake semantic view, OSI, Unity Catalog)에 투자하는 이유도 여기에 있다. Oracle에서 의미 계층 역할을 할 재료는 이미 데이터 딕셔너리에 있다.

- 컬럼 주석
- 외래 키(FK)
- 23ai annotation
- Domain

국내 DB의 주석이 한글로 적혀 있다면, 한국어 질문을 한글 주석과 맞춰 주는 맥락 구성은 글로벌 도구가 신경 쓰지 않는 영역이다(추론).

생성된 SQL은 편집기에 넣기만 하고 실행은 사람이 한다. 이 선택은 Microsoft의 다음 판단·실무와 같다.

- 서버 쪽 자연어→SQL을 거부한 판단
- 사람이 검토하는 IDE에서만 생성을 쓰는 Microsoft 자신의 실무

전송 통제도 이미 제품 사양의 일부가 됐다.

- MongoDB는 임베딩 데이터가 Google Cloud로 간다고 밝힌다.
- SSMS는 DB 정보를 함께 보낼 수 있다고 밝힌다([Microsoft Learn](https://learn.microsoft.com/en-us/ssms/github-copilot/overview)).
- Fabric은 메타데이터만 쓴다고 밝힌다.

Oranda도 접속마다 무엇이 나가는지 미리 보여 주고, 로컬 모델을 먼저 고를 수 있게 해야 한다. 이 방향은 제품 요구사항 문서(PRD)의 APP-07 방향과 같다.

## Oranda를 만들며 AI에 대해 할 수 있는 일

Oranda가 AI에 대해 할 일은 시기에 따라 세 묶음으로 나뉜다.

- 1.0에서는 LLM을 부르지 않고, 나중의 AI 기능이 기댈 구조를 준비한다.
- 1.x 초기에는 SQL을 실행하지 않는 기능부터 붙인다.
- 그 뒤에 MCP 관문과 버전별 서버 AI 연동을 연다.

이 목록은 이 보고서의 근거와 `docs/idea.md` 8절의 기능 목록을 묶은 것이다.

### 1.0에서 할 일: LLM 없이 구조를 준비한다

D-04에 따라 1.0은 LLM을 부르지 않는다. 대신 나중에 붙일 AI 기능이 기댈 경계를 지금 만든다.

1. 실행 정책 관문(APP-09)이 실행 신원을 입력으로 받게 한다.
   - 관문은 「무엇을 실행하나」와 함께 「누구 권한으로 실행하나」를 받는다.
   - 그러면 나중에 에이전트용 접속을 붙일 때 접속 관리자와 세션 모델을 다시 고치지 않아도 된다.
2. 접속 프로필에 에이전트용 저권한 계정을 지정할 자리를 만든다.
   - 저권한 계정이나 Oracle 프록시 사용자를 쓴다.
   - 이 계정에는 필요한 객체의 SELECT 권한만 주도록 안내한다.
3. SAFE-05의 `module` 값으로 사람의 실행과 에이전트의 실행을 구분할 수 있게 한다.
4. `IMetadataProvider`가 컬럼 주석, FK, 23ai annotation, Domain을 함께 돌려주게 한다.
   - 이 정보가 나중에 자연어→SQL의 맥락이 된다.
5. `IOutboundConsent`에 접속별 동의와 전송 내용 미리보기를 둔다.
   - 1.0에서는 업데이트 확인과 충돌 보고 전송에 먼저 쓴다.
6. 19c와 23ai에서 읽기 전용 우회 경로를 시험한다.
   - `PRAGMA AUTONOMOUS_TRANSACTION` 함수를 부르는 SELECT가 데이터를 바꾸는지 확인한다.
   - `UTL_HTTP`를 부르는 SELECT가 데이터를 밖으로 보낼 수 있는지 확인한다.
   - 시험 결과로 SAFE-02 설계와 에이전트 계정의 권한 기준을 정한다.

### 1.x 초기에 할 일: 실행하지 않는 LLM 기능부터 붙인다

1.x 초기에는 SQL을 실행하지 않는 기능부터 붙인다. 이 기능들은 19c에서도 서버 AI 없이 동작한다.

| 순서 | 기능 | 보내는 정보 | 실행 |
|---|---|---|---|
| 1 | SQL 설명 | SQL, 관련 객체의 메타데이터 | 하지 않음 |
| 2 | 오류 고치기 | 오류, 문장, 관련 객체의 정의 | 수정안을 차이로만 보임 |
| 3 | 실행 계획 해설 | `DBMS_XPLAN` 출력, 통계, 인덱스 | 검증 질의만 개발 접속에서 실행 |
| 4 | 자연어→SQL | 사용자가 고른 객체의 메타데이터 | 편집기에 넣기만 함 |

- 제공자는 로컬 모델을 먼저 고르게 한다. 사용자 키와 사내 게이트웨이도 고를 수 있게 한다(APP-07).
- 행 데이터는 접속별 동의를 받은 뒤에만 보낸다. 민감 컬럼은 가린 뒤 보낸다.
- 한국어 질문을 한글 컬럼 주석과 맞춰 맥락을 만든다. 글로벌 도구가 신경 쓰지 않는 영역이다(추론).

### 그 뒤에 할 일: MCP 관문과 서버 AI 연동을 연다

1. Oranda MCP 서버를 관문으로 연다.
   - 비밀번호는 에이전트에 넘기지 않는다.
   - 운영 라벨이 붙은 접속은 기본으로 노출하지 않는다.
   - 에이전트의 SQL도 사람이 쓴 SQL과 같은 SAFE-02·03 관문과 SAFE-07 팩 보호를 거친다.
   - 세션·대기·블로킹·Top SQL·실행 계획처럼 이름 붙은 읽기 전용 도구를 먼저 연다.
   - 자유 SQL 실행은 따로 허용해야 열린다.
   - 비공개 데이터를 읽는 도구와 밖으로 쓰는 도구를 한 에이전트에 함께 주지 않는다.
2. 서버 AI는 서버의 능력을 감지해 선택지로 연다.
   - `DBMS_CLOUD_AI` 패키지가 있으면 Select AI 메뉴를 켠다.
   - 버전과 `COMPATIBLE` 값이 맞으면 벡터 도우미를 켠다.
   - Select AI를 쓰면 전송 통제가 DBA에게 넘어간다는 점을 사용자에게 알린다.
3. 개발하는 동안 코딩 에이전트로 Oranda MCP 서버를 직접 써 보며 관문 규칙을 다듬는다.

### 하지 않을 일

- Oracle이 무료로 주는 독립 채팅 창과 같은 기능으로 경쟁하지 않는다.
- AI가 만든 SQL을 자동으로 실행하지 않는다.
- 키워드 검사만으로 위험한 SQL을 막지 않는다. 파서와 DB 권한을 함께 쓴다.
- 에이전트 지침이나 실행 기록을 위해 서버에 객체를 만들지 않는다. 기록은 로컬에 남긴다.
- 사용자 동의 없이 행 데이터를 보내지 않는다.

## 결론

2026년에는 모든 엔진이 벡터와 MCP를 갖췄고, 남은 희소한 자원은 믿을 수 있는 에이전트 보안 경계다. 2024년에는 "어느 DB가 벡터를 하나"가 질문이었다. 업체들에게는 사실상 무엇을 생성하느냐보다 무엇을 허락하지 않느냐가 제품 사양이 됐다. 데스크톱 도구에게 이것은 가장 값진 AI 기능이 생성이 아니라 통제라는 뜻이다. 통제 수단은 다음 네 가지다.

- 실행 신원
- 전송 범위
- 승인
- 기록

그렇게 보면 Oranda가 1.0에 넣는 정책 관문과 접속 모델은, LLM을 한 번도 부르지 않아도 이미 AI 기능의 절반이다. **1.0 구조에서 '누구 권한으로 실행하나'를 빠뜨리면, 나중에 붙일 AI 기능 전체가 약한 보안 경계 위에 서게 된다.**

국내 Oracle 전용 도구가 마주한 진짜 AI 위협은 채팅 창이 없다는 점이 아니다. AI가 Oracle을 떠나는 비용을 양쪽에서 깎는다는 점이다. 한쪽은 대형 클라우드 업체의 변환 도구고, 다른 쪽은 공공 조달 자격을 앞세운 국산 DBMS다. 그런데 Oracle 자신의 AI 기능은 26ai, Autonomous, 클라우드에 몰려 있고, 온프레미스 사용자는 19c에 남아 있다.

Oranda의 기회는 "Oracle의 AI가 있는 곳"과 "Oracle 사용자가 있는 곳" 사이의 간극에 있다. Oranda는 다음 조건을 갖춘 도구로 그 간극을 채운다.

- 서버를 바꾸지 않는다.
- 서버에 아무것도 설치하지 않는다.
- 19c에서도 클라이언트 쪽에서 안전하게 AI 보조를 준다.

이관 도구는 Oracle을 떠나도록 돕지만, 남아 있는 Oracle 자산을 더 안전하고 생산적으로 운영하게 하는 일은 대신하지 못한다. 그 빈자리가 Oranda의 시장이다.
