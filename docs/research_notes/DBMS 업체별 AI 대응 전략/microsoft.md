# Microsoft database strategy in response to AI (2024 to 2026-10-20)

Scope note: about 16 tool calls. Several pages were only reachable through search snippets (some secondary sources). Items marked "unverified" or listed under Gaps were not confirmed from a primary source.

## SQL Server 2025 engine features (GA date, vectors, external models, editions)

### Takeaway
SQL Server 2025 (17.x) went GA on 2025-11-18. It adds a native VECTOR type, vector functions, in-database embedding generation, and external model objects as GA features. The DiskANN-based vector index and VECTOR_SEARCH were still PREVIEW at RTM and need the PREVIEW_FEATURES database scoped configuration. As of the 2026-10-19 Learn update, the release notes still list them as preview. Microsoft markets it as an "AI-ready enterprise database".

### Cited Findings
- GA date was 2025-11-18. Paid editions were available through volume licensing/MPSA from that date and other channels from January 2026. — [TrustedTech via search](https://trustedtechteam.com/blogs/sql-server/sql-server-2025-top-concerns-upgrade-planning); the GA blog itself is [SQL Server 2025 is Now Generally Available](https://techcommunity.microsoft.com/blog/SQLServer/sql-server-2025-is-now-generally-available/4470570). I could not read the body of the GA blog. One other source says Nov 15 (unreliable, secondary); the Learn page points to the Nov 18 blog.
- Official "What's new" AI section lists: GitHub Copilot in SSMS; VECTOR data type (stored as optimized binary, exposed as JSON arrays; single-precision 4-byte or half-precision 2-byte floats); vector functions VECTOR_DISTANCE, VECTOR_NORM, VECTOR_NORMALIZE, VECTORPROPERTY; CREATE VECTOR INDEX and VECTOR_SEARCH (preview, require PREVIEW_FEATURES); CREATE/ALTER/DROP EXTERNAL MODEL (location, auth method and purpose of an AI inference endpoint); AI_GENERATE_CHUNKS; AI_GENERATE_EMBEDDINGS (uses a precreated AI model definition stored in the database). — [What's new in SQL Server 2025](https://learn.microsoft.com/en-us/sql/sql-server/what-s-new-in-sql-server-2025)
- Release notes (page updated 2026-10-19) list as still Preview, status "RTM": vector index, CREATE VECTOR INDEX, VECTOR_SEARCH, half-precision vectors (up to 3996 dimensions), fuzzy string matching (EDIT_DISTANCE, JARO_WINKLER), change event streaming. Preview features "will become generally available in future cumulative updates", and are "not recommended for production". — [SQL Server 2025 release notes](https://learn.microsoft.com/en-us/sql/sql-server/sql-server-2025-release-notes)
- sp_invoke_external_rest_endpoint is documented as a SQL Server 2025 feature (call REST/GraphQL endpoints, Azure Functions, Azure OpenAI). Regex functions (REGEXP_LIKE, _REPLACE, _SUBSTR, _INSTR, _COUNT, _MATCHES, _SPLIT_TO_TABLE), JSON native binary type, JSON_OBJECTAGG/JSON_ARRAYAGG are GA features. — [What's new](https://learn.microsoft.com/en-us/sql/sql-server/what-s-new-in-sql-server-2025)
- Data API builder (REST/GraphQL) and "SQL MCP Server" are listed under Developer features, to connect custom and Foundry agents to the database. — [What's new](https://learn.microsoft.com/en-us/sql/sql-server/what-s-new-in-sql-server-2025)
- Editions: Standard raised to lesser of 4 sockets or 32 cores and 256 GB buffer pool; Resource Governor now in Standard; Web edition discontinued; Express max DB size 50 GB; new free Standard Developer and Enterprise Developer editions. — [What's new](https://learn.microsoft.com/en-us/sql/sql-server/what-s-new-in-sql-server-2025)
- A third-party licensing comparison (Licenseware, via search snippet) shows the vector index available in Standard as well as Enterprise. Treat as unverified; I did not find an official per-edition feature table. — [search result summary](https://licenseware.io/?p=6153)
- Pricing: Microsoft datasheet est. retail (open, no level) about $15,123 per 2-core pack (Enterprise) and $3,945 per core (Standard). Third-party figures differ. No separate AI surcharge was found. — [Pricing datasheet](https://www.licensingschool.co.uk/wp-content/uploads/2025/11/SQL-Server-2025-Pricing-Datasheet.pdf) (reseller-hosted copy)
- Fabric mirroring replaces Synapse Link for SQL Server 2025 (Synapse Link discontinued). — [What's new](https://learn.microsoft.com/en-us/sql/sql-server/what-s-new-in-sql-server-2025)

### Inferences
- The AI plumbing (types, embeddings, model objects, REST calls) is GA and works with external endpoints (Azure OpenAI, OpenAI, Ollama per press), so the engine itself does not host LLMs; it calls out. Microsoft chose to ship the ANN index behind a preview gate rather than delay RTM.
- The `PREVIEW_FEATURES` scoped-config pattern is a transferable lesson: ship GA/preview separation as a per-database switch.

### Gaps
- Official per-edition matrix for vector features not read directly. SQL Server 2025 vector index GA/CU status unknown as of Oct 2026 (Azure SQL got it GA 2026-09-29; release notes still say preview).
- Exact wording of Microsoft's "AI-ready enterprise database" tagline not captured from a primary source in this session (appears in press/vendor summaries only).

## Azure SQL DB / MI, PostgreSQL, HorizonDB, Cosmos DB, Fabric databases

### Takeaway
Microsoft is converging on DiskANN as the common vector index across Azure SQL, Cosmos DB, and PostgreSQL. Azure SQL DiskANN went GA 2026-09-29. Azure HorizonDB, a new Postgres-compatible distributed service, exists (announced Ignite 2025) and was still in preview as of Aug 2026. SQL database in Fabric is GA (Ignite 2025).

### Cited Findings
- Azure SQL DB, MI (always-up-to-date policy) and SQL database in Fabric: DiskANN vector index and VECTOR_SEARCH GA on 2026-09-29, with full INSERT/UPDATE/DELETE and async index maintenance, iterative filtering, optimizer choice between exact and approximate search, composability with joins and row-level security. — [Azure SQL blog, Sept 29 2026](https://devblogs.microsoft.com/azure-sql/diskann-vector-index-search-are-now-generally-available-in-azure-sql/)
- Preview began Nov 2025 and initially made indexed tables read-only; a March 2026 update removed that restriction. — [search summary of Azure SQL blog posts](https://devblogs.microsoft.com/azure-sql/diskann-vector-index-improvements/)
- Azure HorizonDB: unveiled at Ignite Nov 2025 as a managed Postgres-compatible service (shared storage, scale-out compute, tiered cache; claims up to 128 TB storage, 3,072 vCores, sub-ms multi-zone commit, 3x OSS Postgres throughput which is a vendor claim); DiskANN index with filter pushdown; integrated AI model management. — [The New Stack (search summary)](https://thenewstack.io/microsofts-azure-horizondb-takes-on-aurora-alloydb/), [The Register](https://www.theregister.com/2025/11/19/microsoft_azure_horizondb/)
- HorizonDB status: InfoWorld (Aug 2026) says it remains in public preview; Azure Databases CVP Shireesh Thota said GA is "currently targeted for the second half of 2026". Another summary says Public Preview was announced at Build 2026 with initial regions Central US, West US3, UK South, Australia East. No GA announcement found as of Oct 2026. — [InfoWorld](https://www.infoworld.com/article/4207403/microsofts-postgresql-alternative-horizondb-worth-the-wait.html), [The Register](https://www.theregister.com/2025/11/19/microsoft_azure_horizondb/). Conflict: a third-party page dates preview to June 2026; Ignite 2025 was an announcement with limited early access. A June 2, 2026 Azure update "Generally Available: Azure HorizonDB Agentic Advisor Solution Accelerator" is a sample accelerator, not GA of the service ([merill.net](https://mc.merill.net/message/AZ563092)).
- Microsoft's Postgres tooling includes a VS Code PostgreSQL extension with a preview of GitHub Copilot-powered Oracle migration (relevant to Oracle users). Mirroring of HorizonDB to Fabric planned. — [The Register](https://www.theregister.com/2025/11/19/microsoft_azure_horizondb/)
- Azure Database for PostgreSQL: azure_ai extension GA in May 2024 (call Azure OpenAI and other AI services from SQL, embeddings). DiskANN (pg_diskann) public preview opened Feb 2025; claims up to 10x faster, 4x cheaper than pgvector HNSW with product quantization. pgvector 0.8.0 supported. DiskANN GA not confirmed. — [Tech Community search results](https://techcommunity.microsoft.com/blog/adforpostgresql/-/4410710)
- Cosmos DB: DiskANN vector index GA for NoSQL API at Ignite 2024 (Nov 2024), plus full-text and hybrid search; DiskANN GA in Cosmos DB for MongoDB vCore May 2025. — [Cosmos DB blog](https://devblogs.microsoft.com/cosmosdb/new-vector-search-full-text-search-and-hybrid-search-features-in-azure-cosmos-db-for-nosql/), [Azure updates](https://mc.merill.net/message/AZ491282)
- SQL database in Fabric: GA at Ignite 2025, built on the SQL Server/Azure SQL engine; its Copilot gained tools for diagnosing performance, finding missing or unused indexes, creating or altering tables. NL2SQL in Fabric Data Warehouse Copilot uses metadata (table and column names), not table data. — [Fabric blog](https://blog.fabric.microsoft.com/es-es/blog/copilot-and-query-editor-in-sql-database-in-fabric-ga-update?ft=All), [Learn: Copilot in Fabric warehouse](https://learn.microsoft.com/fabric/data-warehouse/copilot)

### Inferences
- Microsoft runs a three-engine strategy (SQL engine family, Cosmos, Postgres), unified by DiskANN, Foundry model management, and Fabric mirroring.

### Gaps
- Fabric data agents, Cosmos DB in Fabric details, Copilot in Azure portal for Azure SQL, Ignite 2025/2026 Book of News and Build 2026 sources: not retrieved (403 or not searched). HorizonDB pricing not final; Cosmos pricing for vector not collected. Postgres DiskANN GA unknown.

## Tooling: SSMS Copilot, mssql extension, SQL MCP Server

### Takeaway
GitHub Copilot in SSMS (22.x) offers Ask mode (read-only, single response) and, since SSMS 22.7, Agent mode (multi-step, runs queries with per-action approval, extensible with agent skills and MCP servers). Microsoft's DAB-based SQL MCP Server deliberately omits NL2SQL.

### Cited Findings
- Copilot in SSMS uses the user's GitHub account and a selectable model; autocompletions since SSMS 22.2; Agent mode since 22.7. Supports SQL Server 2014 to 2025, Azure SQL DB/MI, SQL on Azure VM, SQL database in Fabric. Prompts, responses and system metadata are not retained and not used for training (per Microsoft, deferring to the GitHub Trust Center). It "might send additional information about the version of SQL ... or information about your database, along with your prompt". — [Copilot in SSMS overview](https://learn.microsoft.com/en-us/ssms/github-copilot/overview)
- Ask vs Agent: Ask is "read-only queries only", single response; Agent iterates and can execute queries and schema changes "with your approval". Agent uses a local MCP server `sql-tools` with predefined tools; does not inherit the active editor context (prompt must name the server/database). Defaults to READ_ONLY; READ_WRITE set in mcp.json. Approvals: Allow once / for session / always. Cancel does not roll back executed queries. — [Agent mode](https://learn.microsoft.com/en-us/ssms/github-copilot/agent-mode)
- Security framing, verbatim in docs: "The security boundary is SQL Server's permission enforcement, not Copilot's approval system." Queries run as the user's login; Copilot has no separate or elevated permissions. — [Agent mode](https://learn.microsoft.com/en-us/ssms/github-copilot/agent-mode), [Execution context](https://learn.microsoft.com/en-us/ssms/github-copilot/execution-context)
- Per-database `CONSTITUTION.md` stored as an extended property (`sp_addextendedproperty`) holds instructions (coding standards, forbidden operations) and optional `agentExecuteAsUser`; SSMS then uses `EXECUTE AS` for Copilot queries; requires IMPERSONATE, and absence of the grant blocks Copilot for that user. Highest precedence over `AGENTS.md` object-level instructions. Recommends a dedicated low-privilege user (e.g., `CREATE USER ... WITHOUT LOGIN` with SELECT/EXECUTE only). Admin controls via GitHub Copilot dashboard. — [Execution context](https://learn.microsoft.com/en-us/ssms/github-copilot/execution-context)
- VS Code mssql extension: Copilot integration GA Nov 2025; June 2026 release (v1.41/1.43, post is internally inconsistent) made Schema Designer with Copilot, Data API builder with Copilot, and SQL Notebooks GA; Azure SQL provisioning in public preview. Schema changes are shown with per-change Accept/Undo and consolidated diff; DAB config reviewable before deployment. Agent skills were not mentioned in that post (unverified for this extension). — [mssql June 2026](https://devblogs.microsoft.com/azure-sql/vscode-mssql-june-2026/), [GA notice](https://mc.merill.net/message/AZ523105)
- SQL MCP Server (part of Data API builder, open source/free): exposes entities rather than physical tables; role-based permissions shared across REST, GraphQL, MCP; MCP spec 2025-06-18, stdio and streamable HTTP; no NL2SQL by design ("Models aren't deterministic, and complex queries are the most likely to produce subtle errors"); supports SQL Server, PostgreSQL, Cosmos DB, MySQL; hostable on App Service with Entra auth (May 4 2026 post). Dated announcement approx April 2026 (exact date unconfirmed). — [Techstrong](https://techstrong.ai/features/microsoft-brings-sql-to-the-agentic-era-with-sql-mcp-server/), [App Service post](https://devblogs.microsoft.com/azure-sql/sql-mcp-server-app-service/)

### Inferences
- Lessons for an Oracle desktop tool: (1) mode split Ask (read-only) versus Agent (approval-gated); (2) treat DB grants as the boundary, offer a dedicated low-privilege execution identity (Oracle analog: proxy user / separate connection) and say plainly approvals are not security; (3) per-database instruction file stored in the DB; (4) tool surface via a local MCP server with defaults READ_ONLY; (5) per-tool approval scopes (once/session/always); (6) agent must be told the target connection explicitly; (7) show diffs with Accept/Undo for schema changes; (8) Microsoft avoids NL2SQL in the server-side MCP product while using LLM SQL generation in interactive IDE tools where a human reviews.

### Gaps
- Exact metadata payload sent to the model is not specified beyond "version and database information". Copilot in Azure portal for Azure SQL not researched. SSMS 21 versus 22 release-by-release notes not collected; SSMS agent skills page not read.

## Analyst and press view

### Takeaway
Press frames HorizonDB as Microsoft's answer to Aurora/AlloyDB and a possible SQL Server successor tied to Fabric; analysts doubt near-term enterprise adoption given the long preview.

### Cited Findings
- Info-Tech: HorizonDB could cannibalize SQL Server. Futurum's Bradley Shimmin: shores up Fabric with an operational layer, "a smart evolution beyond just SQL Server", but enterprises will likely limit it to experiments. David Linthicum: long preview creates SLA/pricing uncertainty. — [The Register](https://www.theregister.com/2025/11/19/microsoft_azure_horizondb/), [InfoWorld](https://www.infoworld.com/article/4207403/microsofts-postgresql-alternative-horizondb-worth-the-wait.html)
- Stephanie Walter: HorizonDB supports only an approved extension set; Advait Patel: no scale-to-zero, idle instances are billed. — [InfoWorld](https://www.infoworld.com/article/4207403/microsofts-postgresql-alternative-horizondb-worth-the-wait.html)
- Competitive set named: Aurora PostgreSQL, AlloyDB, Databricks Lakebase (GA 2026), Snowflake Postgres (Feb 2026). — [InfoWorld](https://www.infoworld.com/article/4207403/microsofts-postgresql-alternative-horizondb-worth-the-wait.html)

### Inferences
- Stated rationale (from product pages and execs): databases as AI foundation (vectors in-engine, model management in-DB, Foundry/Fabric integration), keeping SQL Server relevant on-prem while Postgres and Fabric carry cloud growth.

### Gaps
- Gartner/Forrester views, Microsoft's own keynote quotes (Ignite 2025 / Build 2026 Book of News), and Microsoft earnings-call database commentary were not retrieved. Neowin and msdynamicsworld returned 403.
