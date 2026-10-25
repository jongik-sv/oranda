# Oranda

[한국어](README.ko.md)

Oranda is a free, open source Oracle Database client for Windows and macOS. It is built as a replacement for the aging *Orange for ORACLE* 5.0 workflow: SQL and PL/SQL editing, result grids, execution plans, session and lock monitoring, and tuning tools — without installing an Oracle Client.

> Screenshot placeholder: `docs/images/screenshot-main.png` (to be added)

- No Oracle Client or Instant Client needed: Oranda uses the managed ODP.NET Core driver (thin mode).
- Same features on Windows x64 and Apple Silicon Macs.
- Native .NET 10 + Avalonia desktop app, not Java or Electron.
- MIT licensed and free to use.

## Features

- **Connections** — EZConnect, `tnsnames.ora` aliases, host/port/service form, TLS (TCPS) and Autonomous Database wallets, several connections at once, colour and environment labels (development / test / production), import from SQL Developer, `tnsnames.ora` and Orange connection lists.
- **SQL editor that parses SQL and PL/SQL** — syntax-aware highlighting, error markers, context completion (tables, columns, package members, hints), go to definition, formatter, templates, workspaces with several SQL tabs, crash recovery.
- **Execution and result grid** — scripts and statements, bind variables, editable result grid, LOB editor and row view, CSV and Excel export and import, flashback query.
- **Execution plans** — explain plan, actual plan from the cursor, plan comparison, plan graph, SQL Monitor report (when you declare the Tuning Pack).
- **Schema browser and PL/SQL tools** — object details and DDL, PL/SQL editor with compile errors, PL/SQL run dialog, invalid object compile, recycle bin.
- **Monitoring** — session monitor, SQL monitor (Top SQL, waits, AWR plan history), lock monitor with blocking tree and blocker alert.
- **Tuning** — table and column statistics, child cursors, trace viewer and TKPROF summary, tuning assistant with SQL variant benchmark, single-file HTML tuning report.
- **MyBatis workspace** — open mapper XML files, fill parameters, see the expanded SQL, check tag combinations, and save only the statements you changed.
- **Role-based menus** — pick one or more roles in *Settings > Features* (SQL developer, PL/SQL developer, MyBatis developer, DB tuner, DBA, viewer) and Oranda shows only those menus and features. The *viewer* role blocks DML, DDL and PL/SQL execution. With no role selected, every feature is on.
- **Safety** — read-only connections, production colour labels, confirmation before risky statements, and no use of Diagnostics/Tuning Pack views unless you declare that you own the pack.
- **Keyboard** — keymap presets for Default, Orange 5, PL/SQL Developer and SQL Developer.
- **Languages** — Korean and English user interface.

## Supported versions

| | Supported |
|---|---|
| Oracle Database | 11.2 and later. Tested with 11.2, 19c, 23ai/26ai (23.26.x) and Autonomous Database (Serverless). 12c, 18c and 21c are allowed but not tested. 11.1 and earlier are refused with a message. |
| Windows | Windows 10 and 11, x64 |
| macOS | macOS 14.7 or later on Apple Silicon (ARM64) |
| Not supported | Intel Macs and Windows on ARM (may work, not tested); Linux desktop (builds, not released) |

Source: `docs/PRD.md` section 7.1 and decisions D-026 / D-030 in `docs/decisions.md`.

## Install

Download the latest release from [GitHub Releases](https://github.com/jongik-sv/oranda/releases).

| OS | File |
|---|---|
| Windows | `Oranda-win-Setup.exe` (installer) or `Oranda-win-Portable.zip` |
| macOS | `Oranda-osx-Setup.pkg` |

Windows builds are **not code signed yet** (see [Code signing policy](#code-signing-policy)), so SmartScreen shows "Windows protected your PC": choose **More info → Run anyway**. Unsigned macOS builds must be opened with **right-click → Open** the first time. Step-by-step instructions, automatic updates and uninstalling: [docs/user/install.en.md](docs/user/install.en.md).

### First run

1. Start Oranda. The connection manager opens.
2. Add a connection (EZConnect string, TNS alias or host/port/service) and press **Test**, then **Connect**. See [docs/user/connect.md](docs/user/connect.md) (Korean).
3. Optional: choose your roles in **Settings > Features** to hide menus you do not use.

## Build from source

Requirements: [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0). Windows or macOS.

```sh
git clone https://github.com/jongik-sv/oranda.git
cd oranda
dotnet build Oranda.sln
dotnet run --project src/Oranda.App
```

Tests:

```sh
# unit and UI (headless) tests
dotnet test Oranda.sln --filter "Category!=Integration&Category!=Perf"
# Oracle integration tests start an Oracle Free container with Testcontainers (Docker or Podman required)
dotnet test Oranda.sln --filter "Category=Integration"
```

Release packages are built by `.github/workflows/ci.yml` with [Velopack](https://velopack.io); see [docs/release.md](docs/release.md) (Korean).

## Code signing policy

Windows releases are currently **unsigned**. We have applied to the SignPath Foundation for free code signing of this open source project; once approved, Windows release binaries will be signed with this notice:

Free code signing provided by [SignPath.io](https://about.signpath.io), certificate by [SignPath Foundation](https://signpath.org)

- Only binaries built from this repository by the GitHub Actions release workflow are signed. Third-party libraries keep their own publisher's signature or stay unsigned.
- Every signing request is approved manually by an approver.
- Team roles:
  - Committers and reviewers: [jongik-sv](https://github.com/jongik-sv)
  - Approvers: [jongik-sv](https://github.com/jongik-sv)

macOS releases are signed and notarized with an Apple Developer ID when the release workflow has the Apple credentials; otherwise they are unsigned.

## Privacy

Oranda connects only to the databases you configure. Apart from that:

- **Update check**: an installed copy checks [GitHub Releases](https://github.com/jongik-sv/oranda/releases) for a new version once a day (20 seconds after start). This is a normal HTTPS request to GitHub; [GitHub's privacy statement](https://docs.github.com/site-policy/privacy-policies/github-general-privacy-statement) applies. Turn it off in **Settings > Update**.
- **Crash reports** are saved locally. Nothing is sent unless you review the report and press **Send**; passwords are masked.
- No telemetry or usage statistics are collected.

## License

Oranda is licensed under the [MIT License](LICENSE). Third-party components, including the Oracle Data Provider for .NET (distributed under the Oracle Free Use Terms and Conditions), are listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

Oracle is a registered trademark of Oracle and/or its affiliates. Orange for ORACLE is a product of WareValley. Oranda is an independent project and is not affiliated with or endorsed by Oracle or WareValley.
