using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;

namespace Oranda.App.Features.Ai.External.Commands;

/// <summary>
/// 앱 명령 id → 안전 등급 표(2단계 계획 §3.3 정본, 결정 E-30). <c>CommandDefinition</c> 에 칸을 더하지 않고 App 쪽에 따로 둔다.
/// <list type="number">
/// <item><see cref="KnownIds"/> 에 없는 id(동적 등록·새 명령·오타)는 규칙과 상관없이 <see cref="CommandGrade.Deny"/>.
/// 새 명령은 여기에 넣고 등급을 정할 때까지 거부된다(빠짐 시험이 알린다).</item>
/// <item>아는 id 는 <see cref="Exceptions"/>(정확한 id) → <see cref="PrefixRules"/>(가장 긴 접두어) 순으로 등급을 찾는다.</item>
/// <item>아는 id 는 반드시 예외나 규칙 하나에 걸려야 한다(기본값에 기대지 않는다, 빠짐 시험이 본다).</item>
/// </list>
/// 별칭은 레지스트리가 정식 id 로 푼 뒤에 본다(별칭으로 표를 비켜 가지 않게). 운영 접속에서도 같은 표다.
/// </summary>
public static class CommandGradeTable
{
    /// <summary>접두어 규칙. 같은 id 에 여럿 걸리면 가장 긴 접두어가 이긴다.</summary>
    internal static readonly IReadOnlyList<(string Prefix, CommandGrade Grade)> PrefixRules =
    [
        // 바로: 화면·창·이동·도움말 (§3.3)
        ("oranda.view.", CommandGrade.Now),
        ("oranda.window.", CommandGrade.Now),
        ("oranda.nav.", CommandGrade.Now),
        ("oranda.help.", CommandGrade.Now),
        ("oranda.grid.sort", CommandGrade.Now),
        ("oranda.grid.filter", CommandGrade.Now),
        ("oranda.grid.toggle", CommandGrade.Now),
        ("oranda.edit.fold", CommandGrade.Now),
        ("oranda.edit.unfold", CommandGrade.Now),

        // 띠 한 번: 디스크에 씀 (file.save*·saveWorkspace*)
        ("oranda.file.save", CommandGrade.Confirm),

        // 거부 (§3.3)
        ("oranda.edit.", CommandGrade.Deny), // 글 바꿈 → propose_edit
        ("oranda.exec.run", CommandGrade.Deny), // DB 실행 → run_tab
        ("oranda.txn.", CommandGrade.Deny),
        ("oranda.grid.edit.", CommandGrade.Deny),
        ("oranda.grid.flashback.restore", CommandGrade.Deny),
        ("oranda.grid.copy", CommandGrade.Deny), // 클립보드
        ("oranda.monitor.kill", CommandGrade.Deny),
        ("oranda.monitor.disconnect", CommandGrade.Deny),
        ("oranda.monitor.trace", CommandGrade.Deny),
        ("oranda.ai.", CommandGrade.Deny), // → tune_start

        // 거부: 같은 뜻의 다른 묶음
        ("oranda.mybatis.copyExpanded", CommandGrade.Deny), // 클립보드
        ("oranda.plsql.compile", CommandGrade.Deny), // DB 변경
        ("oranda.plsql.run", CommandGrade.Deny), // DB 실행
        ("oranda.schema.insert", CommandGrade.Deny), // 편집기 글 바꿈
        ("oranda.schema.recyclebin", CommandGrade.Deny), // DB 변경
        ("oranda.tools.keymapPreset.", CommandGrade.Deny), // 사용자 키맵을 바로 덮음
        ("oranda.tuning.rerunWith", CommandGrade.Deny), // DB 실행
    ];

    /// <summary>정확한 id 예외. 접두어 규칙보다 먼저 본다.</summary>
    internal static readonly FrozenDictionary<string, CommandGrade> Exceptions = new Dictionary<string, CommandGrade>(StringComparer.Ordinal)
    {
        // ---- 바로 ----
        // 편집: §3.3 이 꼽은 것만(나머지 oranda.edit.* 는 거부)
        ["oranda.edit.find"] = CommandGrade.Now,
        ["oranda.edit.goToLine"] = CommandGrade.Now,

        // 파일: 새 탭·여는 대화상자·이름 바꾸기 창(사람이 마침)
        ["oranda.file.newSqlEditor"] = CommandGrade.Now,
        ["oranda.file.newPlsqlEditor"] = CommandGrade.Now,
        ["oranda.file.newDedicatedSqlEditor"] = CommandGrade.Now, // 새 탭 + 전용 세션, 아무것도 실행하지 않음
        ["oranda.file.duplicateTab"] = CommandGrade.Now,
        ["oranda.file.reopenClosed"] = CommandGrade.Now,
        ["oranda.file.open"] = CommandGrade.Now,
        ["oranda.file.newWorkspace"] = CommandGrade.Now,
        ["oranda.file.openWorkspace"] = CommandGrade.Now,
        ["oranda.file.openWorkspaceFile"] = CommandGrade.Now,
        ["oranda.file.renameTab"] = CommandGrade.Now,
        ["oranda.file.renameWorkspace"] = CommandGrade.Now,

        // 결과 그리드: 화면·읽기 SELECT(fetchAll·countRows·filter* 와 같은 뜻)·대화상자만 여는 내보내기
        ["oranda.grid.hideColumn"] = CommandGrade.Now,
        ["oranda.grid.showAllColumns"] = CommandGrade.Now,
        ["oranda.grid.freezeColumns"] = CommandGrade.Now,
        ["oranda.grid.rowView"] = CommandGrade.Now,
        ["oranda.grid.viewValue"] = CommandGrade.Now,
        ["oranda.grid.find"] = CommandGrade.Now,
        ["oranda.grid.fetchAll"] = CommandGrade.Now,
        ["oranda.grid.countRows"] = CommandGrade.Now,
        ["oranda.grid.export"] = CommandGrade.Now,
        ["oranda.grid.exportExcel"] = CommandGrade.Now, // 대화상자를 Excel 로 골라 연다(저장은 사람이)
        ["oranda.grid.clearLocalFilters"] = CommandGrade.Now,
        ["oranda.grid.clearSort"] = CommandGrade.Now,
        ["oranda.grid.pinResultTab"] = CommandGrade.Now,
        ["oranda.grid.followForeignKey"] = CommandGrade.Now,
        ["oranda.grid.showReferencingRows"] = CommandGrade.Now,
        ["oranda.grid.flashback.asOf"] = CommandGrade.Now,
        ["oranda.grid.flashback.history"] = CommandGrade.Now,

        // 실행: 취소·오류로 가기·보기 토글·바인드 값 창(실행하지 않음)
        ["oranda.exec.cancel"] = CommandGrade.Now,
        ["oranda.exec.goToError"] = CommandGrade.Now,
        ["oranda.exec.toggleDbmsOutput"] = CommandGrade.Now,
        ["oranda.exec.toggleStatistics"] = CommandGrade.Now,
        ["oranda.exec.toggleBlankLineDelimiter"] = CommandGrade.Now,
        ["oranda.exec.errorModeAsk"] = CommandGrade.Now,
        ["oranda.exec.errorModeContinue"] = CommandGrade.Now,
        ["oranda.exec.errorModeStop"] = CommandGrade.Now,
        ["oranda.exec.bindVariables"] = CommandGrade.Now,

        // 모니터: 읽기 화면·새로 고침 (oranda.monitor.locks.* 는 접두어로 묶지 않는다 — killHolder·disconnectHolder 가 있다)
        ["oranda.monitor.sessions"] = CommandGrade.Now,
        ["oranda.monitor.locks"] = CommandGrade.Now,
        ["oranda.monitor.sql"] = CommandGrade.Now,
        ["oranda.monitor.refresh"] = CommandGrade.Now,
        ["oranda.monitor.sessionDetail"] = CommandGrade.Now,
        ["oranda.monitor.blockingTree"] = CommandGrade.Now,
        ["oranda.monitor.toggleAutoRefresh"] = CommandGrade.Now,
        ["oranda.monitor.toggleDelta"] = CommandGrade.Now,
        ["oranda.monitor.sqlSearch"] = CommandGrade.Now,
        ["oranda.monitor.sqlShowBinds"] = CommandGrade.Now,
        ["oranda.monitor.sqlShowPlan"] = CommandGrade.Now,
        ["oranda.monitor.locks.refresh"] = CommandGrade.Now,
        ["oranda.monitor.locks.goToSession"] = CommandGrade.Now,
        ["oranda.monitor.locks.showDdlLocks"] = CommandGrade.Now,
        ["oranda.monitor.locks.showHistory"] = CommandGrade.Now,
        ["oranda.monitor.locks.showLockedRow"] = CommandGrade.Now,
        ["oranda.monitor.locks.showSql"] = CommandGrade.Now,
        ["oranda.monitor.locks.toggleAutoRefresh"] = CommandGrade.Now,

        // 접속·데이터베이스·서식: 관리 창·고르기 창·읽기 화면
        ["oranda.conn.manage"] = CommandGrade.Now,
        ["oranda.conn.switchSession"] = CommandGrade.Now, // 접속 고르기를 펼친다(사람이 고름)
        ["oranda.db.ddlExtract"] = CommandGrade.Now,
        ["oranda.db.info"] = CommandGrade.Now,
        ["oranda.format.profiles"] = CommandGrade.Now,
        ["oranda.journal.showForObject"] = CommandGrade.Now,

        // MyBatis: 이동·새 SQL 탭·폴더 고르기
        ["oranda.mybatis.findStatement"] = CommandGrade.Now,
        ["oranda.mybatis.expandToSqlTab"] = CommandGrade.Now,
        ["oranda.mybatis.openFolder"] = CommandGrade.Now,

        // 계획: 화면만(계획을 DB 에서 만드는 것은 거부 쪽)
        ["oranda.plan.compare"] = CommandGrade.Now,
        ["oranda.plan.pinForCompare"] = CommandGrade.Now,
        ["oranda.plan.displayCursorOptions"] = CommandGrade.Now,

        // PL/SQL·되돌리기 스크립트: 원본 열기·비교 화면
        ["oranda.plsql.addToWorkspace"] = CommandGrade.Now,
        ["oranda.plsql.openSource"] = CommandGrade.Now,
        ["oranda.plsql.showServerDiff"] = CommandGrade.Now,
        ["oranda.plsql.toggleSpecBody"] = CommandGrade.Now,
        ["oranda.rollback.compare"] = CommandGrade.Now,
        ["oranda.rollback.openScript"] = CommandGrade.Now,

        // 스키마 브라우저: 읽기 화면·새 SELECT 탭·설계 창(DDL 실행은 창에서 사람이)
        ["oranda.schema.editFilters"] = CommandGrade.Now,
        ["oranda.schema.filter"] = CommandGrade.Now,
        ["oranda.schema.newSqlWithSelect"] = CommandGrade.Now,
        ["oranda.schema.newTable"] = CommandGrade.Now,
        ["oranda.schema.newTableLike"] = CommandGrade.Now,
        ["oranda.schema.resumeNewTable"] = CommandGrade.Now,
        ["oranda.schema.objectSearch"] = CommandGrade.Now,
        ["oranda.schema.openDetail"] = CommandGrade.Now,
        ["oranda.schema.refresh"] = CommandGrade.Now,
        ["oranda.schema.resetMetadataCache"] = CommandGrade.Now,
        ["oranda.schema.reveal"] = CommandGrade.Now,
        ["oranda.schema.showDdl"] = CommandGrade.Now,
        ["oranda.schema.tableSpec"] = CommandGrade.Now,
        ["oranda.schema.tableStats"] = CommandGrade.Now,

        // 도구: 설정·키맵·역할·드라이버 창, 데이터 복사 창, 설정 내보내기(저장 대화상자)
        ["oranda.settings.openDrivers"] = CommandGrade.Now,
        ["oranda.tools.dataCopy"] = CommandGrade.Now,
        ["oranda.tools.exportSettings"] = CommandGrade.Now,
        ["oranda.tools.keymap"] = CommandGrade.Now,
        ["oranda.tools.language"] = CommandGrade.Now, // 설정 창의 언어 항목을 연다
        ["oranda.tools.roles"] = CommandGrade.Now,
        ["oranda.tools.settings"] = CommandGrade.Now,
        ["oranda.tools.tableSpec"] = CommandGrade.Now,

        // 튜닝: 읽기 화면·도우미 탭 바꾸기·스크립트를 새 탭에 만들기(실행은 사람이)
        ["oranda.tuning.ashWaits"] = CommandGrade.Now,
        ["oranda.tuning.assistant"] = CommandGrade.Now,
        ["oranda.tuning.awrHistory"] = CommandGrade.Now,
        ["oranda.tuning.baselineScript"] = CommandGrade.Now,
        ["oranda.tuning.childCursors"] = CommandGrade.Now,
        ["oranda.tuning.gatherStatsScript"] = CommandGrade.Now,
        ["oranda.tuning.indexCandidates"] = CommandGrade.Now,
        ["oranda.tuning.planControls"] = CommandGrade.Now,
        ["oranda.tuning.recommend"] = CommandGrade.Now,
        ["oranda.tuning.report"] = CommandGrade.Now,
        ["oranda.tuning.rewriteCandidates"] = CommandGrade.Now,
        ["oranda.tuning.stats"] = CommandGrade.Now,
        ["oranda.tuning.traceViewer"] = CommandGrade.Now,
        ["oranda.tuning.tuningAdvisorScript"] = CommandGrade.Now,

        // ---- 띠 한 번 ----
        ["oranda.file.close"] = CommandGrade.Confirm,
        ["oranda.file.closeAll"] = CommandGrade.Confirm,
        ["oranda.file.closeWorkspace"] = CommandGrade.Confirm,
        ["oranda.grid.closeResultTab"] = CommandGrade.Confirm, // 적용 안 한 그리드 고침을 잃을 수 있음
        ["oranda.conn.disconnect"] = CommandGrade.Confirm,
        ["oranda.conn.reconnect"] = CommandGrade.Confirm,
        ["oranda.monitor.locks.exportCsv"] = CommandGrade.Confirm,
        ["oranda.mybatis.save"] = CommandGrade.Confirm, // file.save* 와 같은 뜻
        ["oranda.mybatis.expandMapper"] = CommandGrade.Confirm, // 다시 펼치면 펼친 탭들을 새 결과로 바꿔 끼움
        ["oranda.plsql.saveWorkspaceFile"] = CommandGrade.Confirm,

        // ---- 거부 ----
        ["oranda.agent.external.connect"] = CommandGrade.Deny, // 에이전트가 제 허가 창을 열지 않게
        ["oranda.file.exit"] = CommandGrade.Deny,
        ["oranda.file.clearRecentFiles"] = CommandGrade.Deny, // 되돌릴 수 없음
        ["oranda.file.clearRecentWorkspaces"] = CommandGrade.Deny,
        ["oranda.format.document"] = CommandGrade.Deny,
        ["oranda.format.statement"] = CommandGrade.Deny,
        ["oranda.conn.changePassword"] = CommandGrade.Deny,
        ["oranda.conn.import"] = CommandGrade.Deny,
        ["oranda.conn.export"] = CommandGrade.Deny,
        ["oranda.exec.pasteBindValues"] = CommandGrade.Deny, // 클립보드 읽기
        ["oranda.monitor.locks.killHolder"] = CommandGrade.Deny,
        ["oranda.monitor.locks.disconnectHolder"] = CommandGrade.Deny,
        ["oranda.mybatis.deleteStatement"] = CommandGrade.Deny,
        ["oranda.mybatis.renameStatement"] = CommandGrade.Deny,
        ["oranda.mybatis.applyAutoFix"] = CommandGrade.Deny,
        ["oranda.mybatis.formatAll"] = CommandGrade.Deny,
        ["oranda.mybatis.formatStatement"] = CommandGrade.Deny,
        ["oranda.mybatis.undoFormatAll"] = CommandGrade.Deny,
        ["oranda.mybatis.newStatement"] = CommandGrade.Deny,
        ["oranda.mybatis.checkCombinations"] = CommandGrade.Deny, // 계획을 DB 에서 만든다
        ["oranda.mybatis.convertWorkspace"] = CommandGrade.Deny, // 파일을 여럿 씀
        ["oranda.mybatis.explainScenario"] = CommandGrade.Deny,
        ["oranda.mybatis.runScenario"] = CommandGrade.Deny,
        ["oranda.plan.explain"] = CommandGrade.Deny, // DB 에서 계획을 만든다 → 계획 도구
        ["oranda.plan.actual"] = CommandGrade.Deny,
        ["oranda.plan.runActual"] = CommandGrade.Deny,
        ["oranda.rollback.runScript"] = CommandGrade.Deny,
        ["oranda.schema.addToDataCopy"] = CommandGrade.Deny,
        ["oranda.schema.compileInvalid"] = CommandGrade.Deny,
        ["oranda.schema.gatherStats"] = CommandGrade.Deny,
        ["oranda.schema.reorderColumns"] = CommandGrade.Deny, // 표를 다시 만드는 DDL 도구
        ["oranda.schema.toggleFavorite"] = CommandGrade.Deny,
        ["oranda.sqlplus.runFile"] = CommandGrade.Deny,
        ["oranda.tableEdit.toggle"] = CommandGrade.Deny, // 데이터 고치기 모드
        ["oranda.tools.import"] = CommandGrade.Deny, // DB 에 행을 넣는 마법사
        ["oranda.tools.importSettings"] = CommandGrade.Deny, // 설정을 덮음
        ["oranda.tuning.benchmark"] = CommandGrade.Deny, // 문장을 되풀이 실행
        ["oranda.tuning.gatherStats"] = CommandGrade.Deny,
        ["oranda.tuning.optimizerTrace"] = CommandGrade.Deny,
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>
    /// 대상 탭(<c>tab</c>, 없으면 활성 탭)의 접속을 쓰는 명령(바로·띠 한 번 가운데, 데이터 SELECT 는 <see cref="DataReadCommands"/>).
    /// 탭 접속이 이 허가 밖이면 거부한다. 여기에도 <see cref="DataReadCommands"/> 에도 없는 명령은 접속과 상관없는 화면 명령이다.
    /// </summary>
    internal static readonly FrozenSet<string> ConnectionCommands = new[]
    {
        "oranda.conn.disconnect",
        "oranda.conn.reconnect",
        "oranda.db.ddlExtract",
        "oranda.exec.cancel",
        "oranda.file.newDedicatedSqlEditor",
        "oranda.grid.export", // 대화상자에서 사람이 고르면 서버에서 다시 읽는다
        "oranda.grid.exportExcel",
        "oranda.journal.showForObject",
        "oranda.monitor.locks.exportCsv",
        "oranda.nav.describeObject",
        "oranda.nav.goToDefinition",
        "oranda.nav.quickOpenObject",
        "oranda.plan.displayCursorOptions",
        "oranda.plsql.addToWorkspace",
        "oranda.plsql.openSource",
        "oranda.plsql.showServerDiff",
        "oranda.schema.editFilters",
        "oranda.schema.filter",
        "oranda.schema.newSqlWithSelect",
        "oranda.schema.newTable",
        "oranda.schema.newTableLike",
        "oranda.schema.objectSearch",
        "oranda.schema.openDetail",
        "oranda.schema.refresh",
        "oranda.schema.resetMetadataCache",
        "oranda.schema.resumeNewTable",
        "oranda.schema.reveal",
        "oranda.schema.showDdl",
        "oranda.schema.tableSpec",
        "oranda.tools.tableSpec",
        "oranda.tuning.baselineScript",
        "oranda.tuning.gatherStatsScript",
        "oranda.tuning.indexCandidates",
        "oranda.tuning.rewriteCandidates",
        "oranda.tuning.tuningAdvisorScript",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// 바로 등급 가운데 DB 에 데이터 SELECT(결과 다시 읽기·플래시백·V$ 모니터·튜닝 읽기 화면)를 띄우는 명령.
    /// 탭 접속이 허가 안이어야 하고, 운영 접속이면 <c>AgentGrantPolicy.MayExecute</c> 도 참이어야 한다(결과는 사람 화면에만 보인다).
    /// </summary>
    internal static readonly FrozenSet<string> DataReadCommands = new[]
    {
        "oranda.db.info",
        "oranda.grid.countRows",
        "oranda.grid.fetchAll",
        "oranda.grid.filterByValue",
        "oranda.grid.filterExcludeValue",
        "oranda.grid.filterNullRows",
        "oranda.grid.flashback.asOf",
        "oranda.grid.flashback.history",
        "oranda.grid.followForeignKey",
        "oranda.grid.showReferencingRows",
        "oranda.monitor.blockingTree",
        "oranda.monitor.locks",
        "oranda.monitor.locks.goToSession",
        "oranda.monitor.locks.refresh",
        "oranda.monitor.locks.showDdlLocks",
        "oranda.monitor.locks.showHistory",
        "oranda.monitor.locks.showLockedRow",
        "oranda.monitor.locks.showSql",
        "oranda.monitor.locks.toggleAutoRefresh",
        "oranda.monitor.refresh",
        "oranda.monitor.sessionDetail",
        "oranda.monitor.sessions",
        "oranda.monitor.sql",
        "oranda.monitor.sqlSearch",
        "oranda.monitor.sqlShowBinds",
        "oranda.monitor.sqlShowPlan",
        "oranda.monitor.toggleAutoRefresh",
        "oranda.monitor.toggleDelta",
        "oranda.schema.tableStats",
        "oranda.tuning.ashWaits",
        "oranda.tuning.assistant",
        "oranda.tuning.awrHistory",
        "oranda.tuning.childCursors",
        "oranda.tuning.planControls",
        "oranda.tuning.recommend",
        "oranda.tuning.report",
        "oranda.tuning.stats",
        "oranda.tuning.traceViewer",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>명령이 대상 탭 접속을 어떻게 쓰나. 표가 모르는 id 는 <see cref="CommandScope.None"/>(어차피 거부).</summary>
    public static CommandScope Scope(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        return DataReadCommands.Contains(id) ? CommandScope.DataRead
            : ConnectionCommands.Contains(id) ? CommandScope.Connection
            : CommandScope.None;
    }

    /// <summary>
    /// 표가 아는 명령 id 전체(앱이 특성으로 등록하는 정식 id). 여기에 없는 id 는 무조건 거부다.
    /// 빠짐 시험이 앱 스캔 결과와 양쪽으로 맞춘다: 새 명령이 생기면 여기 넣고 예외·규칙으로 등급을 정한다.
    /// </summary>
    public static readonly FrozenSet<string> KnownIds = new[]
    {
        "oranda.agent.external.connect",

        "oranda.ai.explainPlan",
        "oranda.ai.explainSql",
        "oranda.ai.fixError",
        "oranda.ai.setup.open", // 「oranda.ai.」 앞머리 규칙으로 거부(에이전트가 연결 도우미를 열지 않게)
        "oranda.ai.tune",
        "oranda.ai.tuneStatement",

        "oranda.conn.changePassword",
        "oranda.conn.disconnect",
        "oranda.conn.export",
        "oranda.conn.import",
        "oranda.conn.manage",
        "oranda.conn.reconnect",
        "oranda.conn.switchSession",

        "oranda.db.ddlExtract",
        "oranda.db.info",

        "oranda.edit.addCursorAbove",
        "oranda.edit.addCursorBelow",
        "oranda.edit.commentLines",
        "oranda.edit.copy",
        "oranda.edit.cut",
        "oranda.edit.cycleCase",
        "oranda.edit.deleteLine",
        "oranda.edit.duplicateLine",
        "oranda.edit.find",
        "oranda.edit.findNext",
        "oranda.edit.findPrevious",
        "oranda.edit.fold",
        "oranda.edit.foldAll",
        "oranda.edit.goToLine",
        "oranda.edit.goToMatchingBracket",
        "oranda.edit.indent",
        "oranda.edit.insertSnippet",
        "oranda.edit.invertCase",
        "oranda.edit.lowerCase",
        "oranda.edit.nextBookmark",
        "oranda.edit.outdent",
        "oranda.edit.parameterHints",
        "oranda.edit.paste",
        "oranda.edit.pasteMyBatisLog",
        "oranda.edit.previousBookmark",
        "oranda.edit.redo",
        "oranda.edit.replace",
        "oranda.edit.selectAll",
        "oranda.edit.sentenceCase",
        "oranda.edit.titleCase",
        "oranda.edit.toggleBlockComment",
        "oranda.edit.toggleBookmark",
        "oranda.edit.toggleLineComment",
        "oranda.edit.triggerCompletion",
        "oranda.edit.uncommentLines",
        "oranda.edit.undo",
        "oranda.edit.unfold",
        "oranda.edit.unfoldAll",
        "oranda.edit.upperCase",

        "oranda.exec.bindVariables",
        "oranda.exec.cancel",
        "oranda.exec.errorModeAsk",
        "oranda.exec.errorModeContinue",
        "oranda.exec.errorModeStop",
        "oranda.exec.goToError",
        "oranda.exec.pasteBindValues",
        "oranda.exec.runAllTabs",
        "oranda.exec.runForEdit",
        "oranda.exec.runFromCursor",
        "oranda.exec.runScript",
        "oranda.exec.runSelection",
        "oranda.exec.runStatement",
        "oranda.exec.runStep",
        "oranda.exec.runSubquery",
        "oranda.exec.runToNewResultTab",
        "oranda.exec.toggleBlankLineDelimiter",
        "oranda.exec.toggleDbmsOutput",
        "oranda.exec.toggleStatistics",

        "oranda.file.clearRecentFiles",
        "oranda.file.clearRecentWorkspaces",
        "oranda.file.close",
        "oranda.file.closeAll",
        "oranda.file.closeWorkspace",
        "oranda.file.duplicateTab",
        "oranda.file.exit",
        "oranda.file.newDedicatedSqlEditor",
        "oranda.file.newPlsqlEditor",
        "oranda.file.newSqlEditor",
        "oranda.file.newWorkspace",
        "oranda.file.open",
        "oranda.file.openWorkspace",
        "oranda.file.openWorkspaceFile",
        "oranda.file.renameTab",
        "oranda.file.renameWorkspace",
        "oranda.file.reopenClosed",
        "oranda.file.save",
        "oranda.file.saveAll",
        "oranda.file.saveAs",
        "oranda.file.saveWorkspace",
        "oranda.file.saveWorkspaceFile",
        "oranda.file.saveWorkspaceFileAs",

        "oranda.format.document",
        "oranda.format.profiles",
        "oranda.format.statement",

        "oranda.grid.clearLocalFilters",
        "oranda.grid.clearSort",
        "oranda.grid.closeResultTab",
        "oranda.grid.copy",
        "oranda.grid.copyAsCsv",
        "oranda.grid.copyAsInList",
        "oranda.grid.copyAsInListLines",
        "oranda.grid.copyAsInsert",
        "oranda.grid.copyRows",
        "oranda.grid.copyRowsWithHeaders",
        "oranda.grid.copyWithHeaders",
        "oranda.grid.countRows",
        "oranda.grid.edit.addRow",
        "oranda.grid.edit.addRowidAndRerun",
        "oranda.grid.edit.apply",
        "oranda.grid.edit.applyAndCommit",
        "oranda.grid.edit.chooseTargetTable",
        "oranda.grid.edit.deleteRow",
        "oranda.grid.edit.duplicateRow",
        "oranda.grid.edit.pasteRows",
        "oranda.grid.edit.previewDml",
        "oranda.grid.edit.resetAll",
        "oranda.grid.edit.resetRow",
        "oranda.grid.edit.toggleOptimisticLock",
        "oranda.grid.export",
        "oranda.grid.exportExcel",
        "oranda.grid.fetchAll",
        "oranda.grid.filterByValue",
        "oranda.grid.filterExcludeValue",
        "oranda.grid.filterNullRows",
        "oranda.grid.find",
        "oranda.grid.flashback.asOf",
        "oranda.grid.flashback.history",
        "oranda.grid.flashback.restore",
        "oranda.grid.flashback.restoreRow",
        "oranda.grid.followForeignKey",
        "oranda.grid.freezeColumns",
        "oranda.grid.hideColumn",
        "oranda.grid.pinResultTab",
        "oranda.grid.rowView",
        "oranda.grid.showAllColumns",
        "oranda.grid.showReferencingRows",
        "oranda.grid.sortAscending",
        "oranda.grid.sortDescending",
        "oranda.grid.toggleColumnFilter",
        "oranda.grid.toggleFilterBar",
        "oranda.grid.toggleQuickFilter",
        "oranda.grid.toggleRepeatHide",
        "oranda.grid.toggleTextMode",
        "oranda.grid.viewValue",

        "oranda.help.about",
        "oranda.help.checkUpdate",
        "oranda.help.docs",
        "oranda.help.releaseNotes",

        "oranda.journal.showForObject",

        "oranda.monitor.blockingTree",
        "oranda.monitor.disconnectSession",
        "oranda.monitor.killSession",
        "oranda.monitor.locks",
        "oranda.monitor.locks.disconnectHolder",
        "oranda.monitor.locks.exportCsv",
        "oranda.monitor.locks.goToSession",
        "oranda.monitor.locks.killHolder",
        "oranda.monitor.locks.refresh",
        "oranda.monitor.locks.showDdlLocks",
        "oranda.monitor.locks.showHistory",
        "oranda.monitor.locks.showLockedRow",
        "oranda.monitor.locks.showSql",
        "oranda.monitor.locks.toggleAutoRefresh",
        "oranda.monitor.refresh",
        "oranda.monitor.sessionDetail",
        "oranda.monitor.sessions",
        "oranda.monitor.sql",
        "oranda.monitor.sqlSearch",
        "oranda.monitor.sqlShowBinds",
        "oranda.monitor.sqlShowPlan",
        "oranda.monitor.toggleAutoRefresh",
        "oranda.monitor.toggleDelta",
        "oranda.monitor.traceOff",
        "oranda.monitor.traceOn",

        "oranda.mybatis.applyAutoFix",
        "oranda.mybatis.checkCombinations",
        "oranda.mybatis.convertWorkspace",
        "oranda.mybatis.copyExpanded",
        "oranda.mybatis.copyExpandedWithValues",
        "oranda.mybatis.deleteStatement",
        "oranda.mybatis.expandMapper",
        "oranda.mybatis.expandToSqlTab",
        "oranda.mybatis.explainScenario",
        "oranda.mybatis.findStatement",
        "oranda.mybatis.formatAll",
        "oranda.mybatis.formatStatement",
        "oranda.mybatis.newStatement",
        "oranda.mybatis.openFolder",
        "oranda.mybatis.renameStatement",
        "oranda.mybatis.runScenario",
        "oranda.mybatis.save",
        "oranda.mybatis.undoFormatAll",

        "oranda.nav.back",
        "oranda.nav.describeObject",
        "oranda.nav.documentOutline",
        "oranda.nav.findReferences",
        "oranda.nav.forward",
        "oranda.nav.goToDefinition",
        "oranda.nav.quickOpenObject",

        "oranda.plan.actual",
        "oranda.plan.compare",
        "oranda.plan.displayCursorOptions",
        "oranda.plan.explain",
        "oranda.plan.pinForCompare",
        "oranda.plan.runActual",

        "oranda.plsql.addToWorkspace",
        "oranda.plsql.compile",
        "oranda.plsql.compileSpecAndBody",
        "oranda.plsql.openSource",
        "oranda.plsql.run",
        "oranda.plsql.runFromBrowser",
        "oranda.plsql.saveWorkspaceFile",
        "oranda.plsql.showServerDiff",
        "oranda.plsql.toggleSpecBody",

        "oranda.rollback.compare",
        "oranda.rollback.openScript",
        "oranda.rollback.runScript",

        "oranda.schema.addToDataCopy",
        "oranda.schema.compileInvalid",
        "oranda.schema.editFilters",
        "oranda.schema.filter",
        "oranda.schema.gatherStats",
        "oranda.schema.insertColumns",
        "oranda.schema.insertName",
        "oranda.schema.insertSelect",
        "oranda.schema.newSqlWithSelect",
        "oranda.schema.newTable",
        "oranda.schema.newTableLike",
        "oranda.schema.objectSearch",
        "oranda.schema.openDetail",
        "oranda.schema.recyclebinFlashback",
        "oranda.schema.recyclebinPurge",
        "oranda.schema.refresh",
        "oranda.schema.reorderColumns",
        "oranda.schema.resetMetadataCache",
        "oranda.schema.resumeNewTable",
        "oranda.schema.reveal",
        "oranda.schema.showDdl",
        "oranda.schema.tableSpec",
        "oranda.schema.tableStats",
        "oranda.schema.toggleFavorite",

        "oranda.settings.openDrivers",

        "oranda.sqlplus.runFile",

        "oranda.tableEdit.toggle",

        "oranda.tools.dataCopy",
        "oranda.tools.exportSettings",
        "oranda.tools.import",
        "oranda.tools.importSettings",
        "oranda.tools.keymap",
        "oranda.tools.keymapPreset.default",
        "oranda.tools.keymapPreset.orange5",
        "oranda.tools.keymapPreset.plsqldev",
        "oranda.tools.keymapPreset.sqldev",
        "oranda.tools.language",
        "oranda.tools.roles",
        "oranda.tools.settings",
        "oranda.tools.tableSpec",

        "oranda.tuning.ashWaits",
        "oranda.tuning.assistant",
        "oranda.tuning.awrHistory",
        "oranda.tuning.baselineScript",
        "oranda.tuning.benchmark",
        "oranda.tuning.childCursors",
        "oranda.tuning.gatherStats",
        "oranda.tuning.gatherStatsScript",
        "oranda.tuning.indexCandidates",
        "oranda.tuning.optimizerTrace",
        "oranda.tuning.planControls",
        "oranda.tuning.recommend",
        "oranda.tuning.report",
        "oranda.tuning.rerunWithHint",
        "oranda.tuning.rerunWithStatisticsLevel",
        "oranda.tuning.rewriteCandidates",
        "oranda.tuning.stats",
        "oranda.tuning.traceViewer",
        "oranda.tuning.tuningAdvisorScript",

        "oranda.txn.commit",
        "oranda.txn.rollback",
        "oranda.txn.toggleAutoCommit",

        "oranda.view.changeBundle",
        "oranda.view.changeJournal",
        "oranda.view.commandPalette",
        "oranda.view.compileErrors",
        "oranda.view.extensionTest",
        "oranda.view.history",
        "oranda.view.messages",
        "oranda.view.outline",
        "oranda.view.output",
        "oranda.view.plan",
        "oranda.view.resetLayout",
        "oranda.view.rowView",
        "oranda.view.scripts",
        "oranda.view.templates",
        "oranda.view.themeDark",
        "oranda.view.themeLight",
        "oranda.view.themeSystem",
        "oranda.view.zoomIn",
        "oranda.view.zoomOut",
        "oranda.view.zoomReset",

        "oranda.window.applyLayout",
        "oranda.window.deleteLayout",
        "oranda.window.focusDocument",
        "oranda.window.nextDocument",
        "oranda.window.previousDocument",
        "oranda.window.saveLayoutAs",
        "oranda.window.splitDown",
        "oranda.window.splitRight",
        "oranda.window.toggleResultPane",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>정식 id 의 등급. 표가 모르는 id(동적 등록 포함)는 <see cref="CommandGrade.Deny"/>.</summary>
    public static CommandGrade Grade(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        return KnownIds.Contains(id) && Classify(id) is { } grade ? grade : CommandGrade.Deny;
    }

    /// <summary>예외 → 가장 긴 접두어 규칙으로 찾은 등급. 어디에도 안 걸리면 null(빠짐 시험이 본다). <see cref="KnownIds"/> 는 보지 않는다.</summary>
    internal static CommandGrade? Classify(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        if (Exceptions.TryGetValue(id, out var exact))
        {
            return exact;
        }

        CommandGrade? best = null;
        var length = -1;
        foreach (var (prefix, grade) in PrefixRules)
        {
            if (prefix.Length > length && id.StartsWith(prefix, StringComparison.Ordinal))
            {
                best = grade;
                length = prefix.Length;
            }
        }

        return best;
    }
}
