# Cloud data platforms and hyperscaler databases: AI-era strategy (2024-01 to 2026-10-20)

Research caveat: about 20 tool calls, mostly search plus selected primary-page fetches. Many 2026 items come from secondary recaps (flagged). Status labels (GA/preview) can drift; verify before relying on them.

## Snowflake: Cortex AI, Snowflake Postgres (Crunchy Data), managed MCP, Openflow, pricing

### Takeaway
Snowflake moved from analytics to an "agentic" platform. It exposes governed data to agents via Cortex Analyst/Search/Agents and a Snowflake-managed MCP server (GA Nov 2025). It also bought Crunchy Data to add an operational Postgres (Snowflake Postgres, GA at Summit June 2026).

### Cited Findings
- Snowflake announced intent to acquire Crunchy Data on June 2, 2025 (Summit 2025), to bring "Snowflake Postgres", an AI-ready enterprise Postgres, to the AI Data Cloud; terms undisclosed, press reports ~US$250M (unconfirmed by Snowflake); announced less than 3 weeks after Databricks bought Neon — [Business Wire](https://www.businesswire.com/news/home/20250602455530/en/snowflake-acquires-crunchy-data-to-bring-enterprise-ready-postgres-offering-to-the-ai-data-cloud) (title only; page 403 on fetch), summarized via search; see also [TechTarget](https://www.techtarget.com/searchdatamanagement/news/366625068/Snowflake-acquisition-of-Crunchy-Data-adds-Postgres-database)
- Snowflake Postgres reached GA at Summit 2026 (June 1-4, 2026), with private link, customer-managed keys, and PGLake (Postgres as an Iceberg catalog / read-write Iceberg); Postgres data mirroring (CDC into Snowflake, no ETL) shown, status not stated — [SELECT recap, Jul 24 2026](https://select.dev/blog/snowflake-summit-2026-what-actually-shipped-and-what-it-means); [Flexera/SELECT/others via search](https://www.flexera.com/blog/perspectives/snowflake-summit-2026/)
- Summit 2025 (June 2025): Snowflake Intelligence (NL interface to data), Cortex AISQL (AI functions in SQL), data science agents announced — [Snowflake blog](https://snowflake.com/en/blog/agentic-ai-ready-enterprise-data)
- Release notes list Cortex Agents, Cortex AI Functions, and Snowflake Intelligence as GA on Nov 4, 2025; Teams/Copilot integration of Cortex Agents Nov 5, 2025 — [Snowflake release notes (feature updates 2025)](https://docs.snowflake.com/release-notes/feature-releases-2025). Cortex Analyst GA date not found.
- Snowflake-managed MCP server: public preview Oct 1, 2025; GA Nov 4, 2025. Tools at launch: Cortex Analyst (NL to SQL on governed structured data), Cortex Search; Cortex Agents planned as tool. RBAC and Snowflake OAuth 2.0; multiple MCP servers per account; no extra compute or charge for the server object; rationale: agents need governed data, MCP avoids per-system custom integrations — [Snowflake blog](https://www.snowflake.com/en/blog/managed-mcp-servers-secure-data-agents/); [GA release note](https://docs.snowflake.com/release-notes/2025/other/2025-11-04-cortex-agents-mcp)
- Aug 7, 2026: Native Apps can ship Cortex Agents and managed MCP servers (GA), exposing app-owned Cortex Search services, semantic views, procedures, UDFs as MCP tools — [Snowflake release note](https://docs.snowflake.com/release-notes/2026/other/2026-08-07-native-apps-agents-mcp-ga)
- Summit 2026 renames: Snowflake Intelligence -> "Snowflake CoWork" (business-user agent, connectors to Redshift, Postgres, Slack/Teams, memory, scheduled tasks); Cortex Code -> "Snowflake CoCo" (Desktop app as VS Code fork, VS Code extension, Claude plugin, skills catalog). AI_COMPLETE accepts audio/video; Cortex AI Function Studio public preview; Cortex Training (fine-tuning open-weight models) introduced, status disputed (private preview per one source); Iceberg v3 GA — [SELECT recap](https://select.dev/blog/snowflake-summit-2026-what-actually-shipped-and-what-it-means); [Flexera recap](https://www.flexera.com/blog/perspectives/snowflake-summit-2026/)
- Openflow gained a headless programmable object model drivable through CoCo (status not given); Observe acquisition (observability) early-stage; per-user daily AI spend quotas announced (status not given); AI-powered migrations claimed 88% time reduction, unverified by recap authors — [SELECT recap](https://select.dev/blog/snowflake-summit-2026-what-actually-shipped-and-what-it-means)

### Inferences
- Snowflake's MCP server is a governed read/analyze tool surface (Analyst/Search/semantic views), not a generic SQL console; user expectation: agents see curated semantic views plus RBAC, not raw schema.
- Postgres via Crunchy follows the "agents need OLTP" thesis; GA ~12 months after deal announcement.

### Gaps
- Crunchy deal closing date, Snowflake Postgres pricing, and Cortex Analyst GA date not verified. Openflow history (2025 GA?) not verified. AI credit/pricing details (AISQL per-token rates) not found. Earnings-call quotes not collected. Snowflake's migration tooling (SnowConvert AI, Oracle source) not researched.

## Databricks: Mosaic AI/Agent Bricks, Genie, Genie Code, Neon -> Lakebase, Unity Catalog, MCP

### Takeaway
Databricks bought Neon (announced May 14, 2025) explicitly because agents create databases; it relaunched as Lakebase (serverless Postgres in the lakehouse; GA Feb 3, 2026 on AWS). It couples this with Agent Bricks, Genie, Genie Code and managed MCP servers governed by Unity Catalog.

### Cited Findings
- Neon acquisition announced May 14, 2025; rationale: >80% of Neon databases provisioned by agents ("four out of every five databases ... spun up by code, not humans" — Ali Ghodsi); 500 ms Postgres launch, instant branching, compute/storage separation; price undisclosed (press reports ~US$1B) — [Databricks press release](https://databricks.com/company/newsroom/press-releases/databricks-agrees-acquire-neon-help-developers-deliver-ai-systems). Figures are Neon telemetry reported by Databricks.
- Lakebase launched June 2025 (Data+AI Summit); GA Feb 3, 2026 on AWS (select regions); Azure beta then (an aggregator reports Azure GA Mar 3, 2026, unverified); features: autoscaling, scale-to-zero, zero-copy branching, PITR, Unity Catalog governance, sync tables, up to 8 TB, Postgres 17, pgvector; claims adoption grew >2x faster than DBSQL since June 2025; use cases: feature serving, agent memory, apps — [Databricks blog](https://www.databricks.com/blog/databricks-lakebase-generally-available)
- Data+AI Summit 2026 (June): Agent Bricks expanded (build/evaluate/govern, multiple agent harnesses), agent memory on Lakebase, Lakebase Search (hybrid vector+full-text) beta, Genie One GA, Unity AI Gateway spend caps for agents; secondary recaps with inconsistent dates — [Addepto](https://addepto.com/blog/databricks-data-ai-summit-2026-new-features-explained/); [Qubika](https://qubika.com/blog/everything-databricks-announced-dais-data-ai-summit-2026/); [Zeb](https://zeb.co/resources/articles/databricks-at-data-and-ai-summit-2026/)
- Managed MCP servers (Public Preview per docs search): Genie, AI Search, Databricks SQL, Unity Catalog functions; OAuth, UC permissions, monitored via Unity AI Gateway; Genie Code (the in-product coding agent/assistant) can connect to MCP servers, admins approve servers — [Databricks managed MCP docs](https://docs.databricks.com/aws/en/agents/mcp-tools/managed-mcp); [Genie Code MCP docs](https://docs.databricks.com/aws/en/assistant/mcp); [announcement blog](https://www.databricks.com/en/blog/announcing-managed-mcp-servers-unity-catalog-and-mosaic-ai-integration)

### Inferences
- Both Databricks and Snowflake describe Postgres as agent infrastructure (state, memory, apps) next to the lakehouse, not as an Oracle replacement for classic OLTP.

### Gaps
- Genie (AI/BI NL2SQL) history and accuracy claims, Mosaic AI -> Agent Bricks timeline, Databricks Assistant -> Genie Code relationship and dates not verified. Lakebase pricing, Neon closing date not verified. Databricks Oracle migration tooling (Lakebridge / BladeBridge acquisition) not researched.

## Google Cloud: Gemini in databases, BigQuery AI, AlloyDB, Spanner, MCP Toolbox, DMS with Gemini

### Takeaway
Google's Next '26 (Apr 2026) made MCP first-class: managed remote MCP servers GA for AlloyDB, Bigtable, Cloud SQL, Firestore, Spanner; open-source MCP Toolbox for Databases 1.0 supports 40+ databases including Oracle. Gemini is used inside DMS to convert Oracle code.

### Cited Findings
- Next '26 databases post (Apr 23, 2026): managed remote MCP servers for AlloyDB, Bigtable, Cloud SQL, Firestore, Spanner (GA); preview for Memorystore, Database Migration Service, Datastream, Database Center, and Oracle AI Database@Google Cloud; Spanner Columnar Engine (GA, up to 200x scan speedup on live data); preview: data-agent tools incl. QueryData text-to-SQL for AlloyDB/Cloud SQL/Spanner ("near-100% text-to-SQL accuracy" is Google's claim), Database Onboarding and Observability agents, AlloyDB 10B vectors with ScaNN, new AI functions (ai.if, ai.rank, ai.generate, ai.forecast, plus ai.analyze_sentiment, ai.summarize), BigQuery/Iceberg federation and reverse ETL, Spanner Omni (downloadable edition), Firestore full-text/geospatial search, "move-to-managed" DMS embedded in Cloud SQL/AlloyDB — [Google Cloud blog](https://cloud.google.com/blog/products/databases/whats-new-for-google-cloud-databases-at-next26)
- MCP Toolbox for Databases: "1.0", 40+ databases, 10 vendors contributing (same post). GitHub repo (renamed from genai-toolbox to mcp-toolbox, Apache-2.0) lists Oracle among supported "Other" sources alongside PostgreSQL, MySQL, MariaDB, SQL Server, MongoDB, Redis, Elasticsearch, CockroachDB, ClickHouse, Couchbase, Neo4j, Snowflake, Trino; prebuilt tools via `--prebuilt=<db>` (e.g. list_tables, execute_sql); HTTP MCP endpoint; custom tools in tools.yaml — [GitHub](https://github.com/googleapis/genai-toolbox)
- Oracle AI Database@Google Cloud expanding to 20 regions, GoldenGate support, replication to BigQuery (same Next '26 post) — evidence Google partners with, not only displaces, Oracle.
- DMS Oracle-to-PostgreSQL / AlloyDB conversion workspaces: Gemini applies corrections on top of rule-based conversion, offers a conversion assistant (explain, fix, optimize), learns from user fixes to propose changes to other objects, and (AlloyDB variant docs) quality assessments of equivalence; requires Gemini for Google Cloud API — [DMS docs: Oracle to PostgreSQL](https://docs.cloud.google.com/database-migration/docs/oracle-to-postgresql/code-conversion-with-gemini); [Oracle to AlloyDB](https://docs.cloud.google.com/database-migration/docs/oracle-to-alloydb/code-conversion-with-gemini)
- BigQuery MCP endpoint exists (bigquery.googleapis.com/mcp) — [Google docs](https://docs.cloud.google.com/bigquery/docs/reference/mcp); release stage not stated in snippet.
- Knowledge Catalog (formerly Dataplex) positioned as "context engine" for agents (Next '26 post, preview).

### Inferences
- Google's Toolbox including Oracle plus DMS-with-Gemini shows Oracle is a first-class source/target in agent and migration tooling; users will expect an MCP server for any DB tool.

### Gaps
- Gemini in BigQuery / data agents / Conversational Analytics status and dates, Gemini Cloud Assist for databases, Toolbox Oracle depth (tools supported, read-only?) and date Oracle support was added, Firestore/Spanner graph+vector dates (2024-25) not verified. DMS Gemini GA/preview date not found. The Next '26 post itself says nothing about Gemini in DMS.

## AWS: DMS Schema Conversion GenAI, MCP servers, Aurora DSQL, vectors, Redshift SQL

### Takeaway
AWS's AI-for-database story is developer tooling (open-source AWS Labs MCP servers, Kiro integrations), vector features inside existing stores, and generative-AI-assisted Oracle-to-PostgreSQL conversion in DMS Schema Conversion.

### Cited Findings
- DMS Schema Conversion with generative AI: converts items rule-based conversion could not (Oracle to RDS/Aurora PostgreSQL; also SQL Server, SAP ASE, Db2 LUW, Db2 z/OS to PostgreSQL targets); covers a defined list of action items (e.g. unconvertible SELECT, MERGE variants, hierarchical queries with pseudocolumns/NOCYCLE, PIVOT, MODEL, FORALL, BULK COLLECT, pipelined functions, ROWID, scheduled jobs, UTL-style system objects); NOT for DEFAULT, computed columns, triggers, column data types, dynamic SQL, indexes, constraints; probabilistic and non-deterministic ("must review and validate"); cross-region inference; 16 regions; per-account quotas (48 or 80 statements/minute) — [AWS DMS docs](https://docs.aws.amazon.com/dms/latest/userguide/schema-conversion-convert.databaseobjects.html)
- May 2024: five generative-AI-assisted built-in functions added for Oracle-to-PostgreSQL via the extension pack — [AWS What's New](https://aws.amazon.com/about-aws/whats-new/2024/05/oracle-postgresql-built-in-system-functions-schema-conversion)
- AWS Labs MCP servers (blog Jun 27, 2025): Aurora PostgreSQL, MySQL, DSQL; DynamoDB; ElastiCache; Neptune; DocumentDB; Timestream for InfluxDB; Keyspaces; MemoryDB. DSQL tools: get_schema, readonly_query, transact. Article does not mention RDS or Redshift servers. Rationale: reduce boilerplate and tool-switching, standardize metadata access — [AWS Database Blog](https://aws.amazon.com/blogs/database/supercharging-aws-database-development-with-aws-mcp-servers)
- Aurora DSQL GA around May 2025 (preview at re:Invent 2024; exact day not confirmed); DSQL MCP server read-only by default, `--allow-writes` enables transactions — [SD Times](https://sdtimes.com/data/amazon-launches-serverless-distributed-sql-database-aurora-dsql/); [Classmethod](https://dev.classmethod.jp/articles/tried-amazon-aurora-dsql-mcp-server/). Feb 2026: DSQL integrated with Kiro powers and agent skills — [AWS What's New](https://aws.amazon.com/jp/about-aws/whats-new/2026/02/amazon-aurora-dsql-integrates-with-kiro-powers-and-agent-skills/)
- S3 Vectors GA at re:Invent 2025: up to 2B vectors per index, ~100 ms frequent-query latency, 14 regions (up from 5 in preview) — [Caylent](https://caylent.com/blog/aws-reinvent-2025-every-ai-announcement-including-amazon-nova-2-and-kiro); [Amazon](https://www.aboutamazon.com/news/aws/aws-re-invent-2025-ai-news-updates). Kiro powers launched with partners incl. Neon, Supabase.
- Aurora PostgreSQL usable as a Bedrock Knowledge Base (pgvector 0.5.0+, RDS Data API, Secrets Manager) — [Aurora docs](https://docs.aws.amazon.com/AmazonRDS/latest/AuroraUserGuide/AuroraPostgreSQL.VectorDB.html)
- Amazon Q generative SQL in Redshift Query Editor: preview Nov 2023, GA Sep 2024, region expansion Nov 2024 — [AWS What's New](https://aws.amazon.com/about-aws/whats-new/2024/11/amazon-q-generative-sql-redshift-query-editor-aws-regions); [AWS blog](https://aws.amazon.com/blogs/aws/amazon-redshift-adds-new-ai-capabilities-to-boost-efficiency-and-productivity)

### Inferences
- AWS has no first-party OLTP-for-agents branding like Lakebase; its angle is MCP servers plus Kiro plus DSQL. The documented DMS GenAI exclusion list (triggers, indexes, dynamic SQL) marks where AI conversion is weakest for PL/SQL migration.

### Gaps
- OpenSearch / DynamoDB / MemoryDB vector search dates, Bedrock KB with S3 Vectors/OpenSearch GA, Amazon Q Developer database features (SQL assistant in Aurora/RDS console), DMS GenAI launch date (GA/preview), AWS-side managed MCP (AWS MCP Server) not verified. 2026 AWS announcements beyond Feb not found.

## Cross-cutting: operational databases for agents, and AI-accelerated Oracle migration

### Takeaway
Both Databricks (Neon, May 2025) and Snowflake (Crunchy, June 2025) bought Postgres vendors within three weeks, citing agents; Google/AWS expose MCP for their operational DBs. Oracle-to-Postgres migration is now AI-assisted at AWS, Google and Microsoft, all with human review.

### Cited Findings
- See Neon and Crunchy findings above for the "agents need operational databases" thesis (80% agent-created DB statistic is vendor telemetry).
- Microsoft: SSMA Copilot-assisted conversion for Oracle/DB2 to SQL Server in public preview; SSMA 10.5 (Feb 2026) expanded AI-assisted conversion — [Microsoft Tech Community](https://techcommunity.microsoft.com/blog/microsoftdatamigration/-/4456031); [Redmond Mag](https://redmondmag.com/blogs/redmond-dispatch/2026/02/microsoft-releases-sql-server-migration-assistant.aspx)
- Microsoft Oracle-to-PostgreSQL migration tooling in the VS Code PostgreSQL extension: public preview announced Nov 2025; schema extraction, validation in a scratch PostgreSQL DB, AI-assisted recommendations (Azure OpenAI), plus app-code migration using GitHub Copilot agent mode — [Azure Updates AZ523593](https://mc.merill.net/message/AZ523593)
- Snowflake claims 88% migration time reduction from AI-powered migrations (unverified) — [SELECT recap](https://select.dev/blog/snowflake-summit-2026-what-actually-shipped-and-what-it-means)

### Inferences (relevance to a desktop Oracle SQL/DBA tool)
- User expectation set by competitors: (1) a built-in MCP endpoint or ability to be driven by agents with read-only default (AWS DSQL `--allow-writes`, Snowflake RBAC-scoped tools); (2) NL-to-SQL grounded by a semantic layer/catalog (Snowflake semantic views, Google Knowledge Catalog, Databricks Unity Catalog); (3) AI explain/fix for converted PL/SQL, hybrid rule-based plus LLM with validation and review (DMS, Gemini DMS, Microsoft scratch-DB validation).
- Oracle is a documented source in every vendor's AI migration path; PL/SQL features that remain hard for LLM conversion (per AWS exclusions): triggers, dynamic SQL, indexes/constraints, pipelined functions, autonomous transactions on collections.

### Gaps
- Earnings-call quotes (Snowflake, Databricks, Alphabet, Amazon) not gathered. Independent accuracy benchmarks of NL2SQL or migration tools not found. Pricing/AI budget controls beyond Snowflake per-user quotas and Databricks AI Gateway spend caps not researched.
