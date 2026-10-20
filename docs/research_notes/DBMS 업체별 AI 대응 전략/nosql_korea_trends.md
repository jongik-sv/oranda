# NoSQL/특수 DB, 국내 DBMS, 업계 공통 흐름의 AI 대응 전략 (2024-01 ~ 2026-10-20)

방법 메모: WebSearch 요약 결과 위주로 수집했고 원문 페이지 전체를 fetch 하지는 못했다(호출 한도). 따라서 모든 항목은 "검색 요약 수준"의 확인이며, 수치와 날짜는 인용 전에 원문 재확인을 권한다. 출처 없는 항목은 Gaps 로 옮겼다.

## (A) NoSQL / 특수 DB 의 AI 전략

### Takeaway
MongoDB 는 Voyage AI 임베딩을 DB 안으로 끌어들이는 "자동 임베딩"을 Community 먼저, Atlas GA(2026-08)까지 밀었다. Redis 는 AGPL 복귀와 벡터 세트/시맨틱 캐시, ClickHouse 는 LibreChat 인수로 "에이전틱 데이터 스택", Pinecone 은 매각 검토 보도 후 CEO 교체와 Nexus 로 피벗했다. 범용 DB 의 벡터 흡수가 전용 벡터 DB 의 압박 요인이다.

### Cited Findings
- MongoDB: 2026-01 Voyage 4 모델과 함께 "Automated Embedding"을 MongoDB Vector Search 에 공개 프리뷰로 발표(JS/Python/Java 드라이버, Community 대상, Atlas 후속) — [IT Brief](https://itbrief.co.uk/story/mongodb-adds-voyage-4-ai-models-automates-vector-search)
- MongoDB: Atlas 에서 Automated Embedding 공개 프리뷰 2026-05-07 — [MongoDB Updates](https://www.mongodb.com/products/updates/now-in-public-preview-automated-embedding-in-mongodb-vector-search-on-atlas/)
- MongoDB: Atlas 에서 Automated Embedding GA 2026-08-12 (인덱스 정의 수정, 중첩 필드, backpressure 제어 추가) — [MongoDB Updates](https://www.mongodb.com/products/updates/now-ga-automated-embedding-in-atlas-vector-search/)
- MongoDB: M10+ 클러스터에서 사용, 추론 서비스는 Google Cloud 에서 실행되어 임베딩 생성 시 데이터가 그곳으로 전송됨 — [MongoDB Docs](https://www.mongodb.com/docs/vector-search/about/automated-embedding/)
- MongoDB MCP Server: Atlas, Atlas Local, Community, Enterprise Advanced 모두 지원, 자연어로 데이터/배포 조작 — [MongoDB Docs](https://www.mongodb.com/docs/mcp-server/overview/)
- MongoDB Compass 자연어 쿼리: Compass 1.40.x 부터, Azure OpenAI 사용, "experimental"로 표기되며 부정확할 수 있다고 명시 — [MongoDB Docs](https://www.mongodb.com/docs/compass/query-with-natural-language/)
- Redis 8.0 은 OSI 승인 AGPLv3 로 오픈소스 복귀 — [AlternativeTo](https://alternativeto.net/news/2025/5/redis-goes-open-source-again-with-the-launch-of-redis-8-under-the-agplv3-license)
- Redis: 벡터 세트(vector sets) 데이터 타입(2025-04 Register 보도는 beta 로 표현)과 LLM 응답 시맨틱 캐시 LangCache 를 발표 — [The Register](https://www.theregister.com/2025/04/10/return_of_redis_creator/), [The New Stack](https://thenewstack.io/redis-launches-vector-sets-and-a-new-tool-for-semantic-caching-of-llm-responses/)
- Redis: LangCache 는 2025-07 Redis Cloud changelog 상 public preview — [Redis changelog](https://redis.io/docs/latest/operate/rc/changelog/july-2025/)
- Stack Overflow 2025: AI 에이전트 데이터 저장용 신규 질문에서 Redis 가 43%로 1위(GitHub MCP 서버와 동률) — [Stack Overflow Blog](https://stackoverflow.blog/2025/07/29/developers-remain-willing-but-reluctant-to-use-ai-the-2025-developer-survey-results-are-here/)
- ClickHouse: 2025-11-04 LibreChat 인수, 창립자 Danny Avila 합류, 오픈소스 "Agentic Data Stack"로 자연어 질의 에이전트 추진(질문에 있던 "LibreChat 인수"는 사실) — [ClickHouse Blog](https://clickhouse.com/blog/clickhouse-acquires-librechat)
- ClickHouse: 공식 MCP 서버 존재(디렉터리/가이드 기반, 벤더 원문 미확인) — [InfoWorld](https://www.infoworld.com/article/4181843/10-mcp-servers-to-connect-llms-with-databases.html)
- Neo4j: 공식 MCP 서버(스키마 조회, read/write Cypher; Aura 포함) 존재, 디렉터리마다 official/community 표기가 엇갈림 — [Enterprise DNA 디렉터리](https://enterprisedna.co/directories/mcp/neo4j-contrib-mcp-neo4j)
- Elastic: 단순 MCP 서버 대신 Agent Builder(MCP 엔드포인트 포함)를 제공, 2025 중반 Elasticsearch MCP 서버는 deprecated 로 언급 — [InfoWorld](https://www.infoworld.com/article/4181843/10-mcp-servers-to-connect-llms-with-databases.html) (2차 요약, 확인 필요)
- Pinecone: 2025-08 말 The Information 보도로 매각 검토(투자은행과 초기 논의, 인수 후보로 Oracle/IBM/MongoDB/Snowflake 거론, 주요 고객 Notion 이탈 언급) — [Calcalist](https://www.calcalistech.com/ctechnews/article/rz31q82b5); 거래 성사 보도는 못 찾음
- Pinecone: 2025-09 Ash Ashutosh 를 CEO 로 임명, Edo Liberty 는 Chief Scientist — [Pinecone Blog](https://pinecone.io/blog/growing-ai-ambitions), [StorageNewsletter](https://www.storagenewsletter.com/2025/09/10/ash-ashutosh-joins-pinecone-as-ceo/)
- Pinecone: Nexus(에이전트용 "knowledge engine") 2026-07 public preview, 2026-08-06 GA 보도 — [SiliconANGLE](https://siliconangle.com/2026/07/02/pinecone-releases-nexus-public-preview-bring-business-knowledge-ai-agents/), [Unite.AI](https://www.unite.ai/pinecones-nexus-knowledge-engine-for-ai-agents-reaches-general-availability/); The New Stack 는 "RAG 를 대중화한 회사가 이제 RAG 에 반대 베팅"이라고 평가 — [The New Stack](https://thenewstack.io/pinecone-nexus-rag-obsolete/)
- Zilliz(Milvus): "Vector Lakebase"를 2026-06 public preview 로 발표, 벡터 DB 를 통합 데이터 플랫폼으로 확장 — [theATDB](https://www.theatdb.com/news/zilliz-launches-vector-lakebase-extending-the-worlds-most-adopted-vector-database-into-a-unified-data-platform-for-ai)
- Databricks 가 Neon 인수(2025-05-14, 약 $10억 보도), Snowflake 가 Crunchy Data 인수(2025-06, 약 $2.5억 보도); Neon DB 의 약 80%가 AI 에이전트가 생성했다는 Databricks 발언 보도 — [SaaStr](https://saastr.com/snowflake-buys-crunchy-data-for-250m-databricks-buys-neon-for-1b-the-new-ai-database-battle), [EnterpriseDB](https://enterprisedb.com/news/latest/databricks-1b-neon-deal-yet-another-play-vertically-bundled-agentic-solutions)

### Inferences
- 벡터는 "별도 DB"에서 "범용 DB 의 기본 기능 + 임베딩 파이프라인 내장"으로 이동했다. 전용 벡터 DB 는 Nexus/Lakebase 처럼 상위 계층(지식/레이크하우스)으로 피벗 중이다.
- Postgres 계열(Neon, Crunchy) 인수가 에이전트 시대 DB 의 중심이 Postgres 임을 시사한다.
- Oranda 관점: 자동 임베딩의 "데이터가 외부 클라우드로 전송" 이슈는, Oracle 접속 도구에서 AI 기능을 붙일 때 스키마/데이터 외부 전송 정책을 명확히 해야 한다는 선례다.

### Gaps
- MongoDB MAAP, Voyage AI 인수(2025) 공식 발표일/금액, Community 자동 임베딩 GA 여부, "vs Postgres" 포지셔닝 공식 자료: 검색에서 확인 못 함.
- Elastic(ES|QL, AI Assistant), Couchbase Capella AI services, Neo4j GraphRAG, SingleStore, DuckDB/MotherDuck, Weaviate, Qdrant, Chroma 개별 전략: 검색 결과에 유효 자료가 없어 미수집. SingleStore/MotherDuck 은 MCP 관련 결과 자체가 없었음.
- Pinecone 매각이 결국 무산/성사되었는지: 확인 못 함.

## (B) 국내 DBMS 벤더의 AI 전략

### Takeaway
Tibero 는 네이티브 벡터 기능 공식 발표를 찾지 못했고, AI 는 "AI 기반 통합 데이터 플랫폼/소버린 AI 데이터 플랫폼(아울AI)", SQL 튜닝 자동화, OpenSQL(pgvector) 활용 위주다. CUBRID 는 벡터 DB 를 2027 베타/2028 GA 로 계획했다. Altibase 는 자료 없음.

### Cited Findings
- 티맥스티베로 2025-10 브랜드 개편: AI 중심 환경에 맞춰 클라우드 전환, 오픈소스 생태계 강화, 데이터 기술 고도화 — [바이라인네트워크](https://byline.network/2025/10/27-505/)
- 티맥스티베로, AI 기반 통합 데이터 플랫폼으로 제6회 공공부문 SW 어워드 과기정통부 장관상 — [Tibero 블로그](https://blog.tibero.com/)
- 티맥스티베로, 오픈메이드컨설팅과 AI 기반 SQL 튜닝 자동화 MOU — [Tibero 블로그](https://blog.tibero.com/)
- 티맥스티베로, OKESTRO OPUS 2026 에서 티베로7, OpenSQL, ADR, 아울DB 소개, 데이터-AI 연결 '아울AI'로 소버린 AI 데이터 플랫폼 확장 — [Tibero 블로그 검색요약](https://blog.tibero.com/), [tech42](https://www.tech42.co.kr/%ED%8B%B0%EB%A7%A5%EC%8A%A4%ED%8B%B0%EB%B2%A0%EB%A1%9C-%EA%B5%AD%EC%82%B0-%ED%81%B4%EB%9D%BC%EC%9A%B0%EB%93%9C%C2%B7db-%EC%97%B0%EA%B3%84-%ED%99%95%EB%8C%80ai-%EB%8D%B0%EC%9D%B4/)
- Tibero 매뉴얼 RAG 챗봇을 OpenSQL(pgvector)로 구축한 사례 소개(Tibero 본체 벡터가 아님) — [Tibero 블로그](https://blog.tibero.com/)
- 티베로7 은 7.2.4 패치셋(2025 후반)에서 컬럼 압축 등 신기능 — [Tibero 블로그](https://blog.tibero.com/category/global/)
- 포스코그룹 세미나 등에서 Oracle->Tibero 전환 사례, ProSync CDC 기반 Oracle-Tibero 액티브 DR 강조 — [Tibero 블로그](https://blog.tibero.com/author/tibero26/page/7/)
- CUBRID(2025-10 보도): 정병주 대표가 벡터 DB 개발을 핵심 전략으로 제시, IITP 과제 수행, 2027 베타/2028 GA 계획 — [CB Insights 게재 기사 경유(2차)](https://www.cbinsights.com/company/cubeit)
- AI 기본법: 2026-01-20 공포, 일부 즉시 시행, 공공분야 AI 도입 촉진 등은 2026-07-21 시행 예정; 정부는 'AI 제품·서비스 확인 제도'로 공공조달 우선 고려 추진 — [Daum 뉴스](https://v.daum.net/v/20260521120203816), [신김 뉴스레터](https://www.shinkim.com/kor/media/newsletter/3301)

### Inferences
- 국산 DBMS 의 AI 대응은 "DB 엔진 내 AI"보다 "소버린 AI 플랫폼 번들/공공 조달 포지션" 중심이다. 이는 Oranda(Oracle 전용)가 Tibero 미지원 결정을 유지해도 직접 영향이 작다는 근거다.
- 다만 Oracle->Tibero 전환 수요가 공공/대기업에 존재하므로, Oracle 호환 SQL 변환/마이그레이션 보조 기능은 향후 차별화 후보가 될 수 있다(추정).

### Gaps
- Tibero 8 존재 여부, 네이티브 벡터 검색, Altibase AI/벡터 발표: 검색 결과 없음(Tibero 8 은 "확인되지 않음"이지 "없음"이 아님).
- Naver/KT/NHN 클라우드의 DB 벡터/AI 기능: 검색 결과에 관련 정보 없음.
- 공공 SW 국산화 DBMS 정책 문서(행안부/조달청 기준): 확인 못 함. 한 기사(kidd.co.kr)의 시행령 날짜는 다른 출처와 불일치해 사용하지 않음.
- 국내 "AI-native DB" 발표 전반: 미확인.

## (C) 업계 공통 흐름

### Takeaway
MCP 가 DB 접근의 사실상 표준이 되었고 그만큼 프롬프트 인젝션 기반 "lethal trifecta" 위험이 부각됐다. NL2SQL 은 쉬운 벤치마크(Spider 1.0 ~91%)와 엔터프라이즈 현실(Spider 2.0 낮은 두 자릿수)의 괴리가 크고, 업계는 시맨틱 레이어(OSI)로 대응한다. DB-Engines 에서는 PostgreSQL 만 상위권에서 점수가 오른다.

### Cited Findings
- Supabase MCP lethal trifecta 시연(2025-07-06 Willison 글): service_role 로 RLS 를 우회하는 에이전트가 고객 티켓의 주입 지시에 따라 integration_tokens 를 읽어 티켓에 기록; 실제 유출 사례가 아닌 시연. Supabase 는 read-only, project-scoped 모드를 권고 — [Simon Willison](https://simonwillison.net/2025/Jul/06/supabase-mcp-lethal-trifecta/), [Supabase Blog](https://supabase.com/blog/mcp)
- 공식 MCP 서버 현황(디렉터리 기반, 벤더 원문 미검증): MongoDB(공식 문서 확인), ClickHouse, Redis, Couchbase 는 공식 표기, Neo4j 는 표기 엇갈림, Elasticsearch 는 커뮤니티/Agent Builder — [InfoWorld](https://www.infoworld.com/article/4181843/10-mcp-servers-to-connect-llms-with-databases.html), [ITWorld Korea](https://www.itworld.co.kr/article/4184249/자연어로-데이터베이스를-조작하는-시대-mcp-서버가-열.html)
- Spider 2.0(ICLR 2025): o1-preview 기반 코드 에이전트 해결률 21.3%(초기판 17.0%) vs Spider 1.0 91.2%, BIRD 73.0%; Spider2-Lite 547 태스크, Snow 547, DBT 68 — [arXiv 2411.07763](https://arxiv.org/abs/2411.07763v2), [GitHub Spider2](https://github.com/xlang-ai/Spider2)
- 2026-01 기사: Spider 1.0 90%대 모델이 Spider 2.0 엔터프라이즈 DB 에서 10-20%대로 하락(2차 요약) — [ASCII](https://ascii.co.uk/news/article/news-20260118-3e9e9dee/spider-20-exposes-text-to-sql-accuracy-crisis-in-enterprise)
- Open Semantic Interchange(OSI): 2025-10 출범(Snowflake 주도), 2025-11 Amazon/Google/DataHub 등 참여, Snowflake 는 스펙이 "finalized"라고 발표 — [Atlan 요약](https://atlan.com/know/snowflake/snowflake-semantic-views/)
- Snowflake Semantic Views 는 2025 중반 GA, 표준 SQL 질의는 2026-03-02 GA; MetricFlow(dbt)는 2025-10 Apache 2.0 오픈소스화, 2026-01 YAML 스펙 개편 — [Atlan](https://atlan.com/know/snowflake/snowflake-semantic-views/), [Snowflake 문서](https://docs.snowflake.com/release-notes/2025/other/2025-08-26-semantic-views-in-snowsight)
- DB-Engines: 2026-01 기준 Oracle, MySQL, SQL Server, PostgreSQL, MongoDB 순; 2026 H1 점수 증가 PostgreSQL +21.97, Databricks +16.04, MongoDB +11.24, Fabric +10.94, Snowflake +6.78; Snowflake 가 2024 DBMS of the Year — [Redgate 보도자료](https://www.red-gate.com/de/our-company/newsroom/press-releases/db-engines-postgresql-leads-h1-2026-database-growth-as-data-platforms-gain-momentum/), [DB-Engines](https://db-engines.com/en/ranking)
- 2026-08 분석(2차): PostgreSQL 이 점수 684.58로 4위이며 1년간 상위4개 중 유일 상승, Oracle/MySQL/SQL Server 는 60-97점 하락 — [ecosistemastartup](https://ecosistemastartup.com/postgresql-para-todo-el-4-que-crece-mientras-el-resto-cae/)
- Stack Overflow 2025(2025-07-29): AI 답변 불신 75.3%, PostgreSQL 이 차기 사용 희망 47%/계속 사용 희망 66%로 DB 중 최고 — [Stack Overflow 보도자료](https://stackoverflow.co/company/press/archive/stack-overflow-2025-developer-survey/)
- 에이전트용 DB 브랜칭/스케일투제로: Neon 구조(스토리지-컴퓨트 분리, copy-on-write 브랜치)가 Databricks 인수 동기 — [EnterpriseDB](https://enterprisedb.com/news/latest/databricks-1b-neon-deal-yet-another-play-vertically-bundled-agentic-solutions)
- 참고(질문 범위 밖 Oracle 동향): Oracle 26ai 벡터 검색 GA 가 2026-01 로 언급(Linux x86-64 온프레미스) — [Virtual-DBA](https://virtual-dba.com/blog/oracle-ai-database-26ai-now-available-for-linux-x86-64-on-premises/)

### Inferences
- 에이전트가 DB 를 직접 조작하는 구조에서는 read-only 기본값, 환경 분리, 쓰기 승인이 표준 방어책이다. Oranda 의 AI 기능은 기본 읽기 전용, 위험 SQL(DDL/DML) 실행 전 사용자 확인, 결과 데이터의 LLM 전송 최소화가 정당화된다.
- NL2SQL 은 스키마 규모가 클수록 정확도가 급락하므로, Oracle 데이터 딕셔너리/주석/관계 메타데이터를 시맨틱 컨텍스트로 쓰는 설계가 현실적이다.
- Postgres 만 성장하는 구도에서 Oracle 시장은 정체/감소 추세이나, 순위 1위는 유지한다(점수 감소폭 위 근거). 사용자 기반 축소 리스크는 낮지만 존재한다.

### Gaps
- A2A 프로토콜 DB 벤더 채택, 벤더별 MCP 서버 출시 시점 타임라인(공식 원문), 실제 DB 유출 인시던트(Supabase 외): 미확인.
- BIRD/Spider 2.0 2026-10 현재 리더보드 최고 점수: 검색에서 못 얻음(리더보드 직접 조회 필요).
- "Agentic/AI-native database" 정의 문서, 에이전트당 DB 가격 모델 변화(Neon 외): 미확인.
- DB-Engines 2025 DBMS of the Year 수상자와 벡터 DBMS 카테고리 순위: 미확인. Stack Overflow 2025 의 정확한 PostgreSQL 사용률은 2차 출처 간 불일치(55.6% vs 58.2%) — 원 설문 페이지 확인 필요.
