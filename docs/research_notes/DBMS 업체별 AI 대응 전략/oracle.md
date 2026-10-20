# Oracle: database strategy in response to AI (2024 - Oct 2026)

Method note: oracle.com / blogs.oracle.com returned HTTP 403 to direct fetch. Oracle-primary claims below come from search-result summaries of Oracle pages (URLs cited) plus fetched secondary sources (InfoWorld, The Register, heise, virtual-dba). Items marked [snippet] were not read in full by me. Nothing was verified against docs.oracle.com New Features guides.

## 1. Oracle AI Database 26ai (Oct 2025 announcement; 23.26.x RUs): what it bundles, status, pricing

### Takeaway
26ai was announced 2025-10-14 at Oracle AI World as the long-term-support successor/rename of 23ai, delivered to existing 23ai systems via the October 2025 RU (no upgrade/recertification); on-prem Linux x86-64 GA came with the January 2026 RU (23.26.1). Oracle says advanced AI features (e.g. AI Vector Search) are included at no additional charge; the agentic stack (Select AI Agent, MCP, Private Agent Factory) is the new layer.

### Cited Findings
- Announced at Oracle AI World, 2025-10-14; LTS release replacing 23ai; "Oracle Database" now branded "Oracle AI Database" — [Oracle press release URL (date in path) via search summary](https://www.oracle.com/news/announcement/ai-world-database-26ai-powers-the-ai-for-data-revolution-2025-10-14/) [snippet]; [Oracle blog](https://blogs.oracle.com/database/oracle-announces-oracle-ai-database-26ai) [snippet]
- Upgrade path: existing 23ai users apply October 2025 RU only, no database upgrade or app recertification; Oracle EBS blog says internal architecture unchanged — [Oracle blog](https://blogs.oracle.com/database/oracle-announces-oracle-ai-database-26ai) [snippet]; [InfoWorld](https://infoworld.com/article/4072128/oracle-targets-agentic-use-cases-with-ai-database-26ai.html)
- Pricing: Oracle states advanced AI features, including AI Vector Search, are included at no additional charge; a free tier is available — [Oracle 26ai page](https://www.oracle.com/database/ai-native-database-26ai/) [snippet]. The Register's Feb 2026 piece gives no licensing/pricing figures for 26ai — [The Register](https://www.theregister.com/2026/02/02/oracles_on_prem_ai_database/)
- Agentic layer (InfoWorld, relaying Oracle): Select AI Agent = framework to build/deploy/manage agents with pre-built in-database tools plus external tools via REST and MCP servers; AI Private Agent Factory = no-code way to deploy private containerized agents; MCP server support lets agents query in-database data; carried over: data annotations, vector search, Exadata support, Nvidia NeMo Retriever integration; security: SQL Firewall, Zero Data Loss Cloud Protect, quantum-resistant algorithms — [InfoWorld](https://infoworld.com/article/4072128/oracle-targets-agentic-use-cases-with-ai-database-26ai.html)
- Other 26ai items per Oracle: Apache Iceberg support, Autonomous AI Lakehouse (OCI, AWS, Azure, Google Cloud, Exadata Cloud@Customer), Autonomous AI Database Catalog ("catalog of catalogs"), Oracle AI Data Platform, Data Lake Accelerator — [heise](https://heise.de/-10778842)
- Some features in the Oct 2025 release were announced as planned rather than GA — [search summary of Oracle press release](https://www.oracle.com/news/announcement/ai-world-database-26ai-powers-the-ai-for-data-revolution-2025-10-14/) [snippet]; specific feature-by-feature status not confirmed by me.
- On-prem GA: Oracle AI Database 26ai Enterprise Edition for Linux x86-64 in January 2026 RU, version 23.26.1 (blog dated 2026-01-27); eDelivery 23.26.1.0.0; 19c is the "official direct upgrade target" path; on-prem release includes Select AI enhancements previously OCI-only. Other platforms (Windows, AIX) reportedly later in 2026 (secondary) — [Oracle blog GA post](https://blogs.oracle.com/database/ga-of-oracle-ai-database-26ai-for-linux-x86-64-on-premises-platforms) [snippet]; [virtual-dba](https://virtual-dba.com/blog/oracle-ai-database-26ai-now-available-for-linux-x86-64-on-premises/); [The Register](https://www.theregister.com/2026/02/02/oracles_on_prem_ai_database/) (says timing for other OSes incl. Windows unclear)
- Support: 26ai Premier Support end 2031 (Register's table); 19c Premier to 31 Dec 2029, Extended to 31 Dec 2032 — [The Register](https://www.theregister.com/2026/02/02/oracles_on_prem_ai_database/); extension announced Feb 2025 — [The Register](https://www.theregister.com/2025/02/18/oracle_extends_19c_support/) [snippet]
- Critics: Support Revolution CEO Mark Smith warns of AI lock-in, says 26ai is "optimized for Oracle engineered systems" and expects costs to rise; Spinnaker's Martin Biggs says 19c users are "not looking at anything soon" — [The Register](https://www.theregister.com/2026/02/02/oracles_on_prem_ai_database/) (both are third-party-support vendors, i.e. interested parties)
- Analyst reaction to agents: Tony Baer (dbInsight) "making AI Agents first-class citizens inside the database"; Alexander Wurm (Nucleus) calls Select AI Agent "Oracle's take on the emerging agent builder platforms," pro-code; Stephanie Walter (HyperFRAME) sees Private Agent Factory as narrow (platform/security teams) and compares to Snowflake managed MCP and Google ADK — [InfoWorld](https://infoworld.com/article/4072128/oracle-targets-agentic-use-cases-with-ai-database-26ai.html). Constellation Research calls the strategy "late but durable" — [heise](https://heise.de/-10778842)
- Stated rationale (Ellison): frontier LLMs trained on the same public data trend to commodity; value is reasoning over private enterprise data, combining existing Oracle databases + AI Database + Apps + OCI — [Cloud Wars](https://cloudwars.com/innovation-leadership/larry-ellisons-grand-plan-for-oracle-to-win-multi-trillion-dollar-ai-markets) [snippet]; call date conflicts between sources (Dec 2025 FQ2 vs Sept 2025 FQ1) — [MIT Sloan ME](https://www.mitsloanme.com/?p=14034) vs [Longport](https://longportapp.cn/news/256660596). Not verified from transcripts.

### Inferences
- "26ai" is largely a rebrand plus RU-delivered features on the 23ai code line (23.26.x), so a client tool sees version banner 23.26.x; detect via `COMPATIBLE`/`product_component_version`, not the "ai" name.
- "Included at no extra cost" is Oracle's marketing statement; EE licensing/Exadata/cloud metering implications are unverified.

### Gaps
- Exact per-feature GA/preview status (unified hybrid vector search, Private Agent Factory availability, data annotations versions) not confirmed from docs.oracle.com.
- 23.26.2/.3 (Apr/Jul 2026) RU contents and any October 2026 RU news not found.
- Windows/AIX/ARM on-prem dates not confirmed by Oracle source.

## 2. 23ai (2024 GA) and the "bring AI to the data" framing

### Takeaway
23ai went GA 2024-05-02 as an LTS release with AI Vector Search plus 300+ features; Oracle's argument is searching unstructured content together with business data in place, without moving/duplicating it into a separate vector DB.

### Cited Findings
- GA announced 2024-05-02 (cloud services; Oracle Database 23ai LTS; AI Vector Search plus 300+ features); JSON Relational Duality lets the same data be used as relational via SQL and as JSON via REST/native APIs — [Oracle press release](https://www.oracle.com/news/announcement/oracle-announces-availability-database-23ai-with-ai-vector-search-2024-05-02) [snippet]; [Oracle blog](https://blogs.oracle.com/database/oracle-23ai-now-generally-available) [snippet]; [KuppingerCole](https://www.kuppingercole.com/blog/balaganski/the-ai-database)
- Rebrand 23c -> 23ai dropped the "c" suffix — [The Register 2024](https://www.theregister.com/2024/05/04/oracle_database_rebrand/) [snippet]; [The Register 2026](https://www.theregister.com/2026/02/02/oracles_on_prem_ai_database/) (23ai was not broadly available on-prem outside engineered systems)
- Vector mechanics (oracle-base): `VECTOR` type; ONNX models loaded with `DBMS_VECTOR.LOAD_ONNX_MODEL` (e.g. all-MiniLM-L12-v2); `VECTOR_EMBEDDING`, `VECTOR_DISTANCE`, `FETCH APPROXIMATE`; two index types (neighbor partition, in-memory neighbor graph); in-memory index needs `VECTOR_MEMORY_SIZE` — [ORACLE-BASE](https://oracle-base.com/articles/23/ai-vector-search-23)
- Upgrading from 19c: COMPATIBLE must be set manually (to 23.6.0) to use vector features — [Oracle 23.7 features doc](https://docs.oracle.com/en/database/oracle/oracle-database/23/nfcoa/ru_23_7.html) [snippet]
- Oracle positions 26ai as one engine for relational, JSON, graph, spatial, text, vector; supports ONNX embedding models; avoids separate vector store — [Oracle 26ai page](https://www.oracle.com/database/ai-native-database-26ai/) [snippet]

### Inferences
- Vector columns, ONNX model catalog (`USER_MINING_MODELS`), vector indexes, and duality views are surfaceable objects for a client tool (DDL viewers, VECTOR cell rendering).

### Gaps
- Property graph (SQL/PGQ) details and 23ai-era RU timeline not researched in this pass.

## 3. Developer tooling and NL2SQL

### Takeaway
Oracle ships free MCP servers (SQLcl local, ORDS endpoint, OCI Database Tools, Autonomous DB built-in) and an auto-registering Copilot integration in SQL Developer for VS Code, so "AI over your DB" is delivered via Oracle's own client tooling and any MCP host.

### Cited Findings
- SQLcl MCP Server: arrived in SQLcl 25.2.x (July 2025); Java 17+; reuses saved SQLcl connections; tools: list-connections, connect, disconnect, run-sql, run-sqlcl; works with 19c to 23ai per Oracle page — [SQLcl 25.2.1 release notes](https://www.oracle.com/tools/sqlcl/sqlcl-relnotes-25.2.1.html) [snippet]; [Oracle blog](https://blogs.oracle.com/ai-and-datascience/ai-driven-sql-made-simple-with-oracle-sqlcl-mcp-server); [Oracle MCP page](https://www.oracle.com/mcp/) [snippet]
- Oracle lists three MCP options: SQLcl (local stdio), OCI Database Tools MCP Server (managed), built-in ORDS MCP endpoint (HTTPS) — [Oracle MCP page](https://www.oracle.com/mcp/) [snippet]
- Autonomous AI Database MCP Server: built-in to Serverless, exposes MCP endpoints for tools defined with Select AI Agent; clients such as Claude Desktop and OCI AI Agent; reported GA for 19c and 26ai; identity-integrated auth, least-privilege via roles/ACLs/VPD — [Oracle ML blog](https://blogs.oracle.com/machinelearning/announcing-the-oracle-autonomous-ai-database-mcp-server) [snippet]
- SQL Developer for VS Code: SQLcl MCP server auto-registered with GitHub Copilot, agents can run SQL/PL/SQL and read the data dictionary (README change dated ~Aug 2025) — [Oracle VS Code page](https://www.oracle.com/database/sqldeveloper/vscode/) [snippet]; [DEV Community](https://dev.to/demasy/ai-meets-oracle-database-mcp-integration-with-oracle-sqlcl-4fn6)
- APEX: AI Assistant introduced 24.1; 24.2 (Jan 2025) added AI Configuration shared component, RAG sources, vector-search Search Configuration for 23ai — [APEX 24.2 what's new](https://apex.oracle.com/ru/platform/features/whats-new-242); [APEX blog](https://blogs.oracle.com/apex/blog-ai-config-and-rag-sources)
- Select AI: actions `runsql` (default), `showsql`, `narrate`, `chat`, `explainsql`; `DBMS_CLOUD_AI.CREATE_PROFILE` with provider, credential, `object_list`; `SET_PROFILE` per session — [Oracle ADB docs](https://docs.oracle.com/en/cloud/paas/autonomous-database/serverless/adbsb/examples-using-select-ai.html)
- Providers (per Oracle guide, varies by release): OCI Generative AI, OpenAI, Azure OpenAI, Cohere, Google, Anthropic, Hugging Face, AWS, OpenAI-compatible, private LLMs — [Oracle Select AI by release](https://blogs.oracle.com/machinelearning/select-ai-by-release-a-quick-guide-to-26ai-and-19c-capabilities) [snippet]
- Select AI Agent builds on Select AI introduced 2023 — [InfoWorld](https://infoworld.com/article/4072128/oracle-targets-agentic-use-cases-with-ai-database-26ai.html)

### Inferences
- Oracle's free SQLcl MCP + Copilot path directly overlaps a desktop tool's "AI chat over my DB" feature; differentiation must come from UX (result grids, plan viewers, guardrails, approval prompts, DBA workflows) and multi-version/multi-vendor support. A desktop tool can also act as an MCP host/client consuming ADB's MCP endpoint, and can surface Select AI profiles (`USER_CLOUD_AI_PROFILES`-type views, unverified) and `SELECT AI showsql` as a first-class action.

### Gaps
- DISABLE_DATA_ACCESS: not confirmed in any source I could read (searches did not surface it); whether `object_list` is a hard access restriction vs. a prompt hint is undocumented in my results. Check DBMS_CLOUD_AI reference.
- Oracle Code Assist, SQL Developer (classic) AI features, APEX 25.x/26 AI agents, SQLcl current version and newer MCP tools: not found.

## 4. Multicloud distribution

### Takeaway
Oracle ties AI to multicloud: Database@AWS/Azure/Google Cloud carry 26ai/Autonomous AI Database, with Universal Credits and partner programs, so AI features can be used "without moving data" to OCI.

### Cited Findings
- Oracle Multicloud Universal Credits cover Database@AWS, @Azure, @Google Cloud, OCI; Oracle cites 38 multicloud regions; Database@AWS expanded with AI services and partner program; Autonomous AI Lakehouse offered on OCI, AWS, Azure, Google Cloud, Exadata C@C — [heise](https://heise.de/-10778842)
- Partner programs: Database@Azure reportedly 28 regions (+5), integrates with Fabric and Azure AI Foundry; Database@AWS linked to Bedrock, ~20 regions planned; Database@Google Cloud offers 19c, 26ai, RAC, Exadata, Autonomous — secondhand copies of a press release on LinkedIn profile pages, undated, treat as low confidence — [LinkedIn copies](https://jp.linkedin.com/in/takeo-y-37062439)

### Inferences
- A client tool will increasingly connect to Autonomous/Exadata instances inside hyperscaler VNets (private endpoints, mTLS wallets), not just OCI.

### Gaps
- Primary announcements, dates per hyperscaler, and 26ai availability per multicloud offering not verified.

## 5. 19c: what works?

### Takeaway
Select AI core features (chat, NL2SQL, synthetic data, agents, summarization, translation) are reportedly the same on 19c and 26ai (via Oracle's cloud/RU delivery), but anything depending on AI Vector Search (RAG, NL2SQL feedback) is 26ai-only. AI Vector Search itself does not exist in 19c.

### Cited Findings
- Per Oracle's "Select AI by release" guide: core features same on 26ai and 19c; RAG and NL2SQL feedback 26ai-only because they depend on AI Vector Search, which 19c lacks — [Oracle blog](https://blogs.oracle.com/machinelearning/select-ai-by-release-a-quick-guide-to-26ai-and-19c-capabilities) [snippet via search summary; page not fetchable]
- One article claims on-prem Select AI arrives with 26ai with a backport to 19c — [search summary, source unnamed](https://virtual-dba.com/?p=243634); the fetched virtual-dba page itself does not list 19c Select AI capabilities. Conflicting/unclear on which delivery (cloud-only vs on-prem 19c RU).
- Autonomous AI Database MCP server reported GA for 19c and 26ai (Autonomous Serverless) — [Oracle ML blog](https://blogs.oracle.com/machinelearning/announcing-the-oracle-autonomous-ai-database-mcp-server) [snippet]
- SQLcl MCP Server works against 19c to 23ai databases (client-side) — [Oracle MCP page](https://www.oracle.com/mcp/) [snippet]
- Direct upgrade 19c -> 26ai supported — [Oracle blog](https://blogs.oracle.com/database/ga-of-oracle-ai-database-26ai-for-linux-x86-64-on-premises-platforms)

### Inferences
- For 19c a client should gate vector/RAG UI on version >= 23.4 (VECTOR type) and COMPATIBLE; Select AI UI should be gated on `DBMS_CLOUD_AI` package presence rather than version.
- No evidence of a 19.x RU that backports AI Vector Search; I found none.

### Gaps
- Exact 19c RU number(s) enabling DBMS_CLOUD_AI on-prem, if any, not found.

## 6. Competitive positioning commentary

### Takeaway
Independent comparative analysis versus pgvector/MongoDB/Snowflake was thin; available commentary is Oracle's own "no separate vector DB" framing plus a few analyst quotes and third-party-support critics.

### Cited Findings
- HyperFRAME's Walter benchmarks Private Agent Factory against Snowflake managed MCP and Google ADK — [InfoWorld](https://infoworld.com/article/4072128/oracle-targets-agentic-use-cases-with-ai-database-26ai.html)
- Oracle still tops DB-Engines; Stack Overflow shows PostgreSQL most popular with developers; critics suggest some customers may move to PostgreSQL — [The Register](https://www.theregister.com/2026/02/02/oracles_on_prem_ai_database/)
- Constellation Research: strategy "late but durable" — [heise](https://heise.de/-10778842)
- Oracle's own framing: one converged engine avoids a separate vector database — [Oracle 26ai page](https://www.oracle.com/database/ai-native-database-26ai/) [snippet]

### Inferences
- Oracle's real moat argument is data gravity and governance (VPD, SQL Firewall) plus 19c installed base, not vector performance.

### Gaps
- No benchmarks, market-share or independent pgvector/MongoDB Atlas/Snowflake Cortex comparisons found; Winter Corp research note on Oracle AI World exists ([PDF](https://www.oracle.com/a/ocom/docs/database/wintercorp-research-note-oracle-ai-world.pdf)) but was not read and is Oracle-hosted.
