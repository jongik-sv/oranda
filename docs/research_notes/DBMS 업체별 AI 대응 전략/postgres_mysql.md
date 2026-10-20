# Open-source RDBMS ecosystem (PostgreSQL, MySQL/MariaDB) response to AI, 2024-01 to 2026-10-20

Method note: ~27 web searches (search-result summaries, few full-page fetches). Items marked [secondary] rest on blogs/aggregators, not vendor primary pages. Dates before 2024 are not included.

## 1. PostgreSQL core and extensions (pgvector, pgvectorscale, pgai, VectorChord, ParadeDB, AGE, PG17/18/19)

### Takeaway
pgvector (0.8.x, HNSW/IVFFlat, iterative scans, halfvec) is the de facto vector layer and is supported by every major managed Postgres. Differentiation moved to hybrid search (BM25), disk-based indexes (DiskANN variants, ScaNN) and in-DB embedding/LLM calls. PG18 (2025-09-25) is I/O-focused with no native vector type; PG19 beta (2026-06-04) adds SQL/PGQ graph queries.

### Cited Findings
- pgvector 0.8.0 added iterative index scans (`hnsw.iterative_scan`, `ivfflat.iterative_scan`; limits `hnsw.max_scan_tuples`, `ivfflat.max_probes`) to fix "overfiltering" in filtered vector search, plus planner and HNSW scan improvements; dropped Postgres 12. Changelog dated 2024-10-30, postgresql.org news 2024-11-11. — [postgresql.org news](https://www.postgresql.org/about/news/pgvector-0.8.0-released-2952/), [Nile](https://www.thenile.dev/blog/pgvector-080)
- Cloud pickup of 0.8.0: Amazon RDS for PostgreSQL 2024-11-21; Aurora PostgreSQL 2025-04-09. — [AWS RDS](https://aws.amazon.com/about-aws/whats-new/2024/11/amazon-rds-for-postgresql-pgvector-080/), [AWS Aurora](https://aws.amazon.com/about-aws/whats-new/2025/04/pgvector-0-8-0-aurora-postgresql)
- A 2026 guide lists pgvector 0.8.2 as current default; HNSW limit is 2000 dims for `vector`, extended to 4000 with `halfvec` [secondary]; it also says Postgres-resident vector search fits up to roughly low tens of millions of vectors before dedicated engines (e.g. Qdrant) pay off [secondary]. — [web3aiblog](https://www.web3aiblog.com/blog/postgres-vector-search-compared-pgvector-pgvectorscale-paradedb-lantern-2026), [ecorpit](https://ecorpit.com/postgres-vector-search-pgvector-pgvectorscale-paradedb-lantern-2026/)
- Timescale rebranded to TigerData on 2025-06-17, positioning as "modern PostgreSQL for the analytical and agentic era"; claimed 2,000 customers, mid-8-digit ARR, $180M raised. — [TigerData blog](https://www.tigerdata.com/blog/timescale-becomes-tigerdata), [DEV](https://dev.to/tigerdata/timescale-is-now-tigerdata-building-the-modern-postgresql-for-the-analytical-and-agentic-era-3a51)
- pgai retreat: Tiger Cloud's managed vectorizer worker service and in-database LLM call functions are deprecated, removal scheduled 2026-06-30 (not verified whether executed). Vectorizer code moved from the extension to a standalone Python library in pgai 0.10.0/0.10.1 (self-run worker; data stays in DB). pgvector, pgvectorscale and BM25 via `pg_textsearch` remain supported. — [TigerData deprecation page](https://www.tigerdata.com/docs/ai/latest/vectorizer-deprecation), [pgai vectorizer works with any Postgres](https://www.tigerdata.com/blog/pgai-vectorizer-now-works-with-any-postgres-database)
- Hybrid search: ParadeDB `pg_search` (Rust/Tantivy, BM25, phrase/fuzzy, facets); a 2026 comparison says TigerData's `pg_textsearch` (custom BM25 access method) beat ParadeDB on MS MARCO latency/throughput in March 2026 benchmarks, ParadeDB broader on facets/analytics [secondary, vendor-adjacent benchmarks]. — [ParadeDB hybrid search](https://paradedb.com/blog/hybrid-search-in-postgresql-the-missing-manual), [ecorpit](https://ecorpit.com/postgres-vector-search-pgvector-pgvectorscale-paradedb-lantern-2026/)
- Azure: `azure_ai` extension GA May 2024; `pg_diskann` (DiskANN) public preview Oct 2024, later GA, Microsoft claims up to 10x speed/4x cost vs pgvector HNSW with product quantization (vendor claim); Semantic Operators public preview May 2025. — [Azure Updates](https://mc.merill.net/message/AZ-diskann-indexing-on-azure-database-for-postgresql), [MS Tech Community](https://techcommunity.microsoft.com/blog/adforpostgresql/-/4410710)
- AlloyDB AI: `alloydb_scann` index (ScaNN; Postgres 14-17; Google claims up to 10x faster index build/filtered search than HNSW), `google_ml_integration` (embeddings, ranking, AI-based filters/joins/generation operators), `alloydb_ai_nl` (natural-language questions over data). — [Google Cloud AlloyDB AI docs](https://docs.cloud.google.com/alloydb/docs/ai), [vector search landing](https://docs.cloud.google.com/alloydb/docs/ai/vector-search-landing)
- PostgreSQL 18 released 2025-09-25: async I/O (`io_method`, io_uring on Linux; reported up to ~3x read speedups), native `uuidv7()`, virtual generated columns, OAuth 2.0 auth, MD5 password deprecation. No vector/AI-specific core features found. — [linuxiac](https://linuxiac.com/postgresql-18-released-with-up-to-3-faster-io-and-easier-upgrades/), [Neon PG18 guide](https://neon.com/postgresql/postgresql-18-new-features)
- PostgreSQL 19 Beta 1 released 2026-06-04 (GA expected ~September 2026; GA not verified): SQL/PGQ graph queries over relational tables, concurrent table repacking, incremental materialized views [secondary], online checksums, pg_plan_advice, JIT off by default, lz4 default TOAST compression, RADIUS removed. — [postgresql.org beta news](https://postgresql.org/about/news/postgresql-19-beta-1-released-3313/), [InfoQ](https://infoq.com/news/2026/06/postgresql-19-graph-queries/)
- Stack Overflow 2025 survey: PostgreSQL #1 in usage (55.6-58.2% depending on source), admired (~65.5%) and desired (~46.5%) [figures from secondary blogs, not the survey page]. — [EDB](https://enterprisedb.com/blog/postgresqls-incredible-trip-top-developers-0), [vonng](https://blog.vonng.com/en/pg/so2025-pg/)
- DB-Engines: January 2026 Oracle #1 (1237.34), MySQL #2 (867.52), PostgreSQL #4 (666.27); a secondary report says Aug 2026 PostgreSQL 684.58, the only top-four system gaining YoY. — [DB-Engines](https://db-engines.com/en/ranking), [ecosistemastartup](https://ecosistemastartup.com/postgresql-para-todo-el-4-que-crece-mientras-el-resto-cae/)

### Inferences
- "Postgres as default AI database" is driven by the extension model: any vendor can ship a new index (ScaNN, DiskANN, BM25) without forking core. Oracle shops get the equivalent only from Oracle itself (AI Vector Search in 23ai/26ai, covered by other researchers).
- Rapid churn (pgai pivot/deprecation) shows in-DB embedding pipelines are not yet settled; tools should not hard-wire to one vendor extension.
- PostgreSQL 19 SQL/PGQ mirrors Oracle 23ai SQL property graph: graph-on-relational is becoming standard SQL across both.

### Gaps
- No verified data for VectorChord (searches returned nothing), Apache AGE, or pgvectorscale/StreamingDiskANN release specifics; pgvector 0.7 (quantization/halfvec/sparsevec, 2024) not directly sourced. PG17 AI-relevant features not researched. PG19 GA status unconfirmed. Whether the pgai repo is archived unconfirmed.

## 2. Vendor strategies built on Postgres (Supabase, Neon/Databricks, Crunchy/Snowflake, TigerData, EDB, Xata, Nile, hyperscalers' Postgres parts)

### Takeaway
Data platforms bought Postgres to give agents an OLTP store: Databricks-Neon (~$1B, May 2025) and Snowflake-Crunchy (~$250M reported, June 2025); both shipped GA products (Lakebase, Snowflake Postgres) by early 2026. Supabase raised at $5B (Oct 2025) then reportedly $10.5B (June 2026).

### Cited Findings
- Databricks announced intent to acquire Neon 2025-05-14 for about $1B; said >80% of databases on Neon were created by AI agents; CEO Ghodsi: "four out of five" spun up by code. — [Databricks press release](https://databricks.com/company/newsroom/press-releases/databricks-agrees-acquire-neon-help-developers-deliver-ai-systems), [Axios](https://www.axios.com/2025/05/15/databricks-ali-ghodsi-neon-deals), [EDB commentary](https://enterprisedb.com/news/latest/databricks-1b-neon-deal-yet-another-play-vertically-bundled-agentic-solutions)
- Databricks Lakebase: public preview 2025-06-11; GA on AWS (exact date not found; up to 8TB/instance, Postgres 17 with pgvector, instant branching pitched for agent development); Azure Databricks Lakebase GA March 2026. — [Databricks GA blog](https://databricks.com/blog/databricks-lakebase-generally-available), [Azure GA blog](https://www.databricks.com/blog/azure-databricks-lakebase-generally-available), [devclass](https://devclass.com/2025/06/12/databricks-opens-up-agentic-ai-pipelines-fills-in-postgres-powered-lakebase/)
- Snowflake announced intent to acquire Crunchy Data 2025-06-02 (Summit); price undisclosed, WSJ reported ~$250M (unconfirmed by Snowflake). — [Snowflake press release](https://snowflake.com/en/news/press-releases/snowflake-acquires-crunchy-data-to-bring-enterprise-ready-postgres-offering-to-the-ai-data-cloud/), [Constellation Research](https://www.constellationr.com/insights/news/snowflake-makes-its-postgres-move-acquires-crunchy-data)
- Snowflake Postgres GA 2026-02-24 (dedicated VM per instance, select AWS/Azure regions); `pg_lake` (about 15 extensions for Postgres as lakehouse/Iceberg, open-sourced Nov 2025) available natively. — [Snowflake release note](https://docs.snowflake.com/release-notes/2026/other/2026-02-24-snowflake-postgres-ga), [The New Stack](https://thenewstack.io/pg_lake-comes-to-snowflake-postgres-what-it-means-for-open-standards/)
- Supabase: Series E $100M at $5B (Accel, Peak XV) announced 2025-10-03 (some trackers say $120M); a TipRanks listing shows Series F $500M at $10.5B on 2026-06-04 [secondary, aggregator]. — [Orrick](https://www.orrick.com/en/News/2025/10/Supabase-Raises-100-Million-Series-E-at-5-Billion-Valuation), [TechCrunch](https://techcrunch.com/2025/10/03/supabase-nabs-5b-valuation-four-months-after-hitting-2b/), [TipRanks](https://www.tipranks.com/private-companies/supabase)
- Supabase MCP server: July 2025 General Analysis demo showed "lethal trifecta" (private data + untrusted content + exfil path) via poisoned support ticket; Supabase added read-only mode, project scoping, feature groups and states RLS still applies; no known customer incident per Supabase. — [Supabase defense-in-depth](https://supabase.com/blog/defense-in-depth-mcp), [Simon Willison](https://simonwillison.net/2025/Jul/6/), [Supabase MCP blog](https://supabase.com/blog/mcp)
- EDB Postgres AI: "Agent Factory" (Langflow-based Agent Studio, AI pipelines syncing embeddings, vector search, model serving) and "Sovereign Data & AI Factory" (hardware appliance with Dell/Supermicro/HP/IBM, optional NVIDIA, air-gapped). Vendor claims (50-80% integration work reduction, "4.22x faster than specialized vector DBs") unsubstantiated; page dates not visible. — [EDB Agent Factory](https://www.enterprisedb.com/products/agent-factory), [EDB Sovereign](https://www.enterprisedb.com/products/sovereign-data-ai-factory)
- TigerData "agentic" positioning: see section 1 (pgai managed pieces retired).
- Azure/AlloyDB specifics: see section 1.

### Inferences
- Pattern: analytics/lakehouse vendors want Postgres as the operational store feeding agents; consolidation implies Postgres tooling vendors may continue to be acquired.
- For Oracle-tool builders: customers' agent-facing apps will increasingly land on Postgres-compatible stores, so multi-DB connectivity (Postgres alongside Oracle) is a plausible adjacent need; Oracle-specific depth remains the differentiator.

### Gaps
- Not covered by searches: Xata, Nile strategies (only Nile's pgvector 0.8.0 blog seen), AWS Aurora ML/Bedrock integration (search returned only Azure), Neon post-acquisition metrics, Supabase's own AI Assistant details, EDB dated announcements. Supabase Series F is single-aggregator sourced.

## 3. Why agents create so many Postgres databases (agent share stats, branching, vibe-coding platforms)

### Takeaway
Two vendors report agent-dominated provisioning: Neon >80% (Databricks, 2025-05) and Supabase >60% of new databases from AI coding agents (Sacra analysis, secondary). Cheap instant provisioning plus branching is the pitch.

### Cited Findings
- Neon >80% of databases created by agents (internal telemetry, per Databricks). — [Databricks press release](https://databricks.com/company/newsroom/press-releases/databricks-agrees-acquire-neon-help-developers-deliver-ai-systems)
- Sacra: AI coding agents spin up 60%+ of new Supabase databases; coding-agent-originated launches grew 600% YoY, with Claude Code and Codex fastest-growing sources [single secondary source, not a Supabase disclosure]. — [Sacra](https://sacra.com/research/supabase-170m-year-growing-221-yoy/)
- Same source: Lovable and Bolt.new initially defaulted to Supabase, later launched native backends (Lovable Cloud, Bolt Cloud) still white-labeling Supabase via Supabase for Platforms; Replit chose Neon; v0 is bring-your-own-backend. — [Sacra](https://sacra.com/research/supabase-170m-year-growing-221-yoy/)
- Databricks positions Lakebase branching as ideal for agent development. — [Databricks GA blog](https://databricks.com/blog/databricks-lakebase-generally-available)
- PlanetScale (MySQL/Vitess lineage, also offering Postgres) shipped a hosted MCP server (OAuth, Streamable HTTP; read-only/full access per branch; DDL forces human confirmation), reported 2026-02-26. — [PlanetScale changelog](https://planetscale.com/changelog/mcp-server), [i-programmer](https://i-programmer.info/news/84-database/18693-the-planetscale-mcp-server-announced.html)

### Inferences
- "Agent-created" stats are vendor telemetry about provisioning counts, not workload size; many are tiny, short-lived databases. Treat as growth signal, not market share.
- Patterns worth copying for an Oracle tool: scoped read-only/branch-aware connections, human confirmation for DDL (PlanetScale), RLS/privilege-respecting agent access (Supabase).

### Gaps
- No primary Supabase percentage; no figures for Bolt/v0/Replit; Neon's later (post-May 2025) agent share not found; no Oracle-side equivalent data.

## 4. MySQL / MariaDB / PlanetScale

### Takeaway
Oracle keeps real vector search (DISTANCE, indexes) and in-DB LLMs inside proprietary HeatWave; MySQL Community got only the VECTOR type and helpers (9.x), with Oracle promising more in 9.7 LTS. MariaDB shipped full open-source vector search (11.7 GA Feb 2025, 11.8 LTS). MySQL community health concerns became public in 2026.

### Cited Findings
- HeatWave GenAI: in-database LLMs (no extra charge, no GPUs; models incl. llama3.2-3b-instruct default as of 9.4 per a third-party article), vector store ingesting documents with auto-embedding for RAG, vector processing across up to 512 cluster nodes; runs on OCI, AWS, Azure. NL2SQL not confirmed from retrieved pages. — [Oracle HeatWave GenAI features](https://www.oracle.com/heatwave/genai/features/), [MySQL docs](https://dev.mysql.com/doc/heatwave/en/mys-hw-genai-overview.html)
- MySQL 9.0 (2024-07 Innovation) introduced VECTOR type; Community has no `DISTANCE()`, no vector index, no ORDER BY distance kNN; `DISTANCE()` documented as HeatWave-on-OCI / MySQL AI only. 9.7 manual still lists `DISTANCE()` under HeatWave/MySQL AI; a May 2026 bug (#120425) requests community distance/indexing. — [MySQL 9.1 vector functions](https://dev.mysql.com/doc/refman/9.1/en/vector-functions.html), [Bytebase](https://www.bytebase.com/blog/what-is-new-in-mysql-9/), [MySQL bug 120425](https://bugs.mysql.com/bug.php?id=120425), [theconsensus.dev](https://theconsensus.dev/p/2026/02/08/paths-of-mysql-vector-search-edition.html?focus=1)
- Percona Server for MySQL 9.7.2-2 (Sept 2026) adds `DISTANCE()`/`VECTOR_DISTANCE()` in the open-source fork. — [Percona](https://www.percona.com/blog/vector-distance-vector-similarity-search-in-percona-server-for-mysql/)
- Oracle said 9.7 LTS (expected April 2026) would include vector functions in Community (per InfoWorld/CIO reporting); a blog said 9.7 was only Early Access as of April 2026; actual GA content unverified. — [InfoWorld](https://www.infoworld.com/article/4134394/community-push-intensifies-to-free-mysql-from-oracles-control-amid-stagnation-fears.html), [Vonng](https://vonng.com/en/db/mysql-97-bye/)
- MySQL community health: Percona-led open letter (Feb 2026) asks for a MySQL foundation; 159 (The Stack) to 248 (other outlet) signatures, 66 from Percona; Percona says active contributors fell from 135 (2017) to ~75 (Q3 2025) and commits from 22,000+ (2010) to 4,730 (2024); reported ~50% staff cuts after MySQL moved to OCI in autumn 2025; MySQL community manager Frederic Descamps left for MariaDB Foundation. Percona is an interested party. — [Percona open letter](https://www.percona.com/blog/an-open-letter-to-oracle-lets-talk-about-mysqls-future/), [The Stack](https://www.thestack.technology/a-battle-for-mysqls-heart-and-future/), [i-programmer](https://www.i-programmer.info/news/136-open-source/18696-community-asks-oracle-for-mysql-foundation.html)
- MariaDB 11.7 GA (announced ~2025-02-13) added VECTOR type, vector indexes and distance/conversion functions; 11.8 is first LTS with vector support; Enterprise 11.4 had tech preview. MariaDB released an MCP server with optional embedding providers (needs 11.7+). — [MariaDB 11.7 GA blog](https://mariadb.com/resources/blog/announcing-mariadb-community-server-11-7-ga-with-vector-search-and-mariadb-community-server-11-8-rc/), [InfoQ](https://www.infoq.com/news/2025/06/mariadb-vector-search), [MariaDB MCP blog](https://mariadb.com/resources/blog/build-smarter-with-mariadb-mcp-server-ai-ready-vector-enabled/)
- MariaDB plc acquired Codership (Galera) 2025-05-27; K1 takeover bid (~$37M, Feb 2024 per heise) took it off NYSE (completion terms not confirmed). — [MariaDB press release](https://mariadb.com/newsroom/press-releases/mariadb-acquires-galera-cluster/), [heise](https://heise.de/-9864912)

### Inferences
- MySQL's AI story is a HeatWave upsell with the open-source edition lagging; MariaDB and Percona fill the gap. This weakens MySQL as an "AI default" versus Postgres.
- Relevant to Oracle shops: Oracle owns both Oracle DB and MySQL; AI investment goes to proprietary tiers (Oracle 23ai/26ai, HeatWave), consistent between the two.

### Gaps
- PlanetScale vector support and its Postgres launch details not retrieved (MCP only). HeatWave NL2SQL/AutoML unconfirmed. MySQL 9.7 GA contents unverified.

## 5. Tooling: pgAdmin, DBeaver, Postgres MCP servers

### Takeaway
GUI tools are adding LLM chat, plan analysis and MCP client support; the Anthropic reference Postgres MCP server is archived and unpatched, with community replacements.

### Cited Findings
- pgAdmin 4 v9.13 (2026-03-05) added LLM integration groundwork, AI reports (security, schema, performance), an AI chat in the Query Tool, and AI Insights for EXPLAIN plans; supports PostgreSQL 13-18 and EDB Advanced Server. — [pgAdmin 9.13 release notes](https://pgadmin.org/docs/pgadmin4/9.13/release_notes_9_13.html)
- DBeaver 26.1 (2026-06-08) added external MCP server/tool support in AI Chat with user confirmation and allowlist; 26.2 (2026-09-07) added per-conversation AI engines, AI max-wait setting, open AI-generated scripts in new console, and follow-up-question chat tool. — [DBeaver 26.2](https://dbeaver.com/dbeaver-ultimate-26-2/), [DBeaver 26.1](https://dbeaver.com/dbeaver-ultimate-26-1/), [release notes](https://dbeaver.com/release-notes/)
- Anthropic's reference Postgres MCP server (`@modelcontextprotocol/server-postgres` 0.6.2) archived (2025-05-29 per one source; deprecated 2025-07-10 per Datadog); Datadog (2025-08-21) showed stacked-query SQL injection bypassing its read-only wrapper; fix exists in a Zed fork and an unreleased upstream change, not the npm package; still ~21k weekly npm downloads. — [Datadog Security Labs](https://securitylabs.datadoghq.com/articles/mcp-vulnerability-case-study-SQL-injection-in-the-postgresql-mcp-server/), [The Register](https://www.theregister.com/2025/06/25/anthropic_sql_injection_flaw_unfixed/)
- Postgres MCP Pro (Crystal DBA): MIT-licensed; index tuning, EXPLAIN plans, health checks, safe SQL execution; stdio and SSE. No 2026 release info found. — [Smithery listing](https://smithery.ai/server/crystaldba/postgres-mcp), [mcp.directory](https://mcp.directory/servers/postgres-mcp-pro)

### Inferences
- The shipped feature set converged: chat-to-SQL, EXPLAIN explanation, health/index advice, MCP client with confirmation gates. These are table stakes for a desktop Oracle tool; AWR/ASH/execution-plan interpretation is the Oracle-specific analogue of pgAdmin's AI Insights.
- Read-only enforcement at the MCP layer proved bypassable; enforce read-only at DB role/transaction level (relevant for any Oracle MCP or agent mode).

### Gaps
- pgAdmin's LLM provider list/privacy model not retrieved; DBeaver's earlier (2024-25) AI history not covered; other Postgres MCP servers (Neon, Supabase details, Bytebase DBHub) only touched via listings.
