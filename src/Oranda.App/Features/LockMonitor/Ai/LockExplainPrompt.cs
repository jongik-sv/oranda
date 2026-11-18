using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Oranda.App.Features.Ai.Kit;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Ai.Features;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Monitoring;

namespace Oranda.App.Features.LockMonitor.Ai;

/// <summary>풀이 종류: 락 모니터 사슬(「AI 풀이」) 또는 세션 모니터의 고른 세션(「대기 풀이」).</summary>
public enum LockExplainKind
{
    LockChain,
    SessionWait,
}

/// <summary>
/// 풀이에 보낼 세션 한 줄. 락 모니터(<c>LockTreeItem</c>·<c>LockSessionFacts</c>)나 세션 모니터(<c>SessionRow</c>·<c>SessionDetail</c>)가 가진 값을 그대로 옮긴다.
/// 사슬이면 보유자부터 앞쪽 순회 차례로 넣는다(트리 줄 차례).
/// </summary>
/// <param name="Key">인스턴스·SID·SERIAL#.</param>
public sealed record LockExplainSession(SessionKey Key)
{
    /// <summary>이 세션을 막는 세션. 보유자(뿌리)면 null.</summary>
    public SessionKey? BlockerKey { get; init; }

    public string? Username { get; init; }

    public string? OsUser { get; init; }

    public string? Machine { get; init; }

    public string? Program { get; init; }

    public string? Module { get; init; }

    /// <summary>gv$session.status(ACTIVE·INACTIVE).</summary>
    public string? Status { get; init; }

    public string? Event { get; init; }

    public string? WaitClass { get; init; }

    public long? SecondsInWait { get; init; }

    /// <summary>마지막 호출 뒤 지난 초(INACTIVE 보유자가 얼마나 쉬었는지).</summary>
    public long? LastCallElapsedSeconds { get; init; }

    /// <summary>잠긴 객체(<c>OWNER.NAME</c>).</summary>
    public string? LockedObject { get; init; }

    /// <summary>보유 잠금 모드(「X (6)」, <c>LockText.Mode</c>).</summary>
    public string? HeldMode { get; init; }

    /// <summary>요청 잠금 모드.</summary>
    public string? RequestedMode { get; init; }

    public string? SqlId { get; init; }

    /// <summary>현재 SQL 글. 막는 쪽만 넣는 것이 기본이다(설계 §3.2 꾸러미).</summary>
    public string? SqlText { get; init; }

    public string? PrevSqlId { get; init; }

    /// <summary>직전 SQL 글.</summary>
    public string? PrevSqlText { get; init; }

    public DateTimeOffset? TransactionStart { get; init; }

    public long? UndoBlocks { get; init; }
}

/// <summary>풀이 입력. <paramref name="Sessions"/> 가 비면 보내지 않는다.</summary>
public sealed record LockExplainInput(LockExplainKind Kind, IReadOnlyList<LockExplainSession> Sessions)
{
    /// <summary>모은 시각. 없으면 쓰지 않는다.</summary>
    public DateTimeOffset? CollectedAt { get; init; }
}

/// <summary>
/// 띠가 그릴 풀이 결과. <see cref="Answer"/> 가 null 이면 틀이 깨진 답이라 <see cref="RawText"/> 를 그대로 보인다.
/// <see cref="Conclusion"/> 은 KILL 확인 창에 다시 보인다(<see cref="LockExplainPrompt.KillDialogLine"/>).
/// </summary>
/// <param name="Title">띠 제목.</param>
/// <param name="Answer">상황·원인 추정·권하는 행동·결론 칸(자리표시를 원래 이름으로 되돌린 글).</param>
/// <param name="Conclusion">결론 한 줄. 틀이 깨졌으면 null.</param>
/// <param name="RawText">받은 글 그대로.</param>
/// <param name="Notes">띠 아래 안내(자리표시로 보냄·SQL 뺌).</param>
public sealed record LockExplainResult(string Title, AiTemplatedAnswer? Answer, string? Conclusion, string RawText, IReadOnlyList<string> Notes);

/// <summary>
/// 락·세션 상황 풀이(설계 §3.2, 바로 처리 — 모니터 아래 AI 띠): 입력 → 띠 요청(꾸러미·지시·답 틀), 답 글 → <see cref="LockExplainResult"/>.
/// <list type="bullet">
/// <item>등급 = <see cref="Tier"/>(가벼운 일). 화면은 <c>AiKitRoute.For(profile, settings, LockExplainPrompt.Tier)</c> 로 경로를 만든다.</item>
/// <item>허용 수준 「계획」 미만이면 보내지 않는다.</item>
/// <item>값 수준(<see cref="AiAccessPolicy.DataLevel"/>)이 「조회」 미만이면 사용자명·OS 사용자·머신·프로그램·모듈을 <c>USER1</c>·<c>HOST1</c> 꼴로 바꾼다(AE-3). 로컬 AI 는 값 수준이 「조회+행 값」 이라 그대로 보낸다.</item>
/// <item>SQL 글은 꾸러미의 SQL 칸이라 가림 대상 접속이면 기존 가림기로 가려지고, 가리지 못하면 빠진다(Kit 규칙).</item>
/// </list>
/// </summary>
public static class LockExplainPrompt
{
    /// <summary>요청 등급.</summary>
    public const AiTier Tier = AiCommandTiers.LockExplain;

    /// <summary>한 번에 보내는 세션 수 상한. 넘으면 앞쪽(보유자 쪽)만 보내고 빠진 수를 적는다.</summary>
    public const int MaxSessions = 30;

    /// <summary>SQL 글 하나의 글자 수 상한.</summary>
    public const int MaxSqlChars = 4000;

    public const string ChainTitle = "락 풀이";

    public const string SessionTitle = "대기 풀이";

    public const string LevelTooLowText = "이 접속의 AI 허용 수준이 「계획」 보다 낮아 락·대기 풀이를 보낼 수 없습니다.";

    public const string NothingText = "풀이할 세션이 없습니다.";

    /// <summary>자리표시로 바꿔 보냈을 때 띠에 덧붙이는 안내.</summary>
    public const string AliasNote = "사용자명·머신 이름은 USER1·HOST1 같은 자리표시로 바꿔 보냈습니다.";

    /// <summary>SQL 칸이 빠졌을 때의 안내.</summary>
    public const string SqlWithheldNote = "값을 가릴 수 없는 SQL 글은 빼고 보냈습니다.";

    /// <summary>KILL 확인 창 한 줄의 머리.</summary>
    public const string KillDialogPrefix = "AI 풀이: ";

    public const string Situation = "상황";

    public const string Cause = "원인 추정";

    public const string Action = "권하는 행동";

    public const string ConclusionTitle = "결론";

    /// <summary>답 틀: 세 칸 + 결론 한 줄.</summary>
    public static AiAnswerTemplate Template { get; } = new(
    [
        new AiAnswerSection(Situation, "one line: who holds what and who waits how long"),
        new AiAnswerSection(Cause, "the likely cause in one to three short lines, for example an INACTIVE holder that never committed, a long batch, an unindexed foreign key"),
        new AiAnswerSection(Action, "up to three bullets: whether to wait, whom to contact (by the user, machine or program exactly as written in the data), and the conditions under which a KILL is worth considering"),
        new AiAnswerSection(ConclusionTitle, "exactly one short line, about 40 Korean characters, saying what to do now; it is shown again in the KILL confirmation dialog"),
    ]);

    /// <summary>띠 요청을 준비한다. <paramref name="masker"/> 가 없으면 가림 대상 접속의 SQL 칸은 빠진다.</summary>
    public static AiStripPreparation Prepare(LockExplainInput input, AiOneShotRun run, ISqlLiteralMasker? masker)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(run);
        var route = run.Route;
        if (route.Context.AccessLevel < AiAccessLevel.Plan)
        {
            return AiStripPreparation.Refuse(LevelTooLowText);
        }

        if (input.Sessions.Count == 0)
        {
            return AiStripPreparation.Refuse(NothingText);
        }

        var aliases = Aliases(input, route);
        var builder = route.Bundle(TitleOf(input.Kind), masker);
        var sessions = input.Sessions.Take(MaxSessions).ToList();
        foreach (var session in sessions)
        {
            var key = LockText.Key(session.Key);
            builder.Add("session " + key, "세션 " + key, SessionText(session, aliases), AiAccessLevel.Plan);
            builder.Add("sql_text " + key, "현재 SQL " + key, Cut(session.SqlText), AiAccessLevel.Plan, AiContextKind.Sql);
            builder.Add("prev_sql_text " + key, "직전 SQL " + key, Cut(session.PrevSqlText), AiAccessLevel.Plan, AiContextKind.Sql);
        }

        if (input.Sessions.Count > sessions.Count)
        {
            builder.Add("not_shown", "보내지 않은 세션 수", string.Create(CultureInfo.InvariantCulture, $"{input.Sessions.Count - sessions.Count} more waiting sessions are not shown."), AiAccessLevel.Plan);
        }

        var instruction = new StringBuilder();
        instruction.AppendLine(input.Kind == LockExplainKind.LockChain
            ? "Explain this Oracle lock situation for a DBA who must decide quickly what to do. The sessions form a blocking chain: the first one holds the lock, the others wait (blocked_by gives the session each one waits for). sql_text and prev_sql_text blocks belong to the session with the same key."
            : "Explain what this Oracle session is waiting for, for a DBA who must decide quickly what to do.");
        instruction.AppendLine("You have no tools and cannot run anything. Never claim you ran or killed anything; suggest only, a person decides and runs.");
        if (aliases is not null)
        {
            instruction.AppendLine("User names, OS users, machine names, programs and modules were replaced with placeholders such as USER1, HOST1, PROGRAM1 and MODULE1; write them exactly so when you refer to a session.");
        }

        if (input.CollectedAt is { } at)
        {
            instruction.AppendLine(string.Create(CultureInfo.InvariantCulture, $"Collected at {at:yyyy-MM-dd HH:mm:ss zzz}."));
        }

        return AiStripPreparation.Ready(new AiStripRequest(run, builder.Build(), instruction.ToString(), Template));
    }

    /// <summary>답 글을 띠 결과로 읽는다. 자리표시는 이 PC 화면에 보일 때만 원래 이름으로 되돌린다(같은 입력이면 같은 자리표시가 나온다).</summary>
    public static LockExplainResult Read(string answer, LockExplainInput input, AiStripRequest request)
    {
        ArgumentNullException.ThrowIfNull(answer);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(request);
        var aliases = Aliases(input, request.Run.Route);
        var parsed = Template.Parse(answer);
        if (parsed is not null && aliases is not null)
        {
            parsed = new AiTemplatedAnswer(parsed.Parts.Select(p => p with { Body = aliases.Restore(p.Body) }).ToList());
        }

        var conclusion = parsed?[ConclusionTitle] is { } c ? OneLine(c.Body) : null;
        var notes = new List<string>();
        if (aliases is { Count: > 0 })
        {
            notes.Add(AliasNote);
        }

        if (request.Bundle.Omitted.Any(o => o.Key.StartsWith("sql_text", StringComparison.Ordinal) || o.Key.StartsWith("prev_sql_text", StringComparison.Ordinal)))
        {
            notes.Add(SqlWithheldNote);
        }

        return new LockExplainResult(TitleOf(input.Kind), parsed, string.IsNullOrEmpty(conclusion) ? null : conclusion, answer, notes);
    }

    /// <summary>KILL 확인 창에 보일 한 줄. 앞서 받은 풀이가 없거나 결론이 없으면 null(줄을 숨김, 새 요청 없음).</summary>
    public static string? KillDialogLine(LockExplainResult? result) =>
        result?.Conclusion is { Length: > 0 } conclusion ? KillDialogPrefix + conclusion : null;

    private static string TitleOf(LockExplainKind kind) => kind == LockExplainKind.LockChain ? ChainTitle : SessionTitle;

    /// <summary>값 수준이 「조회」 미만이면 자리표시 사전을 만들어 입력 차례대로 채운다(보낼 때와 읽을 때 같은 결과).</summary>
    private static AiAliasMap? Aliases(LockExplainInput input, AiKitRoute route)
    {
        if (AiAccessPolicy.DataLevel(route.Context.AccessLevel, route.Context.LocalAi) >= AiAccessLevel.Query)
        {
            return null;
        }

        var map = new AiAliasMap();
        foreach (var s in input.Sessions.Take(MaxSessions))
        {
            map.Alias(AiAliasKind.User, s.Username);
            map.Alias(AiAliasKind.User, s.OsUser);
            map.Alias(AiAliasKind.Host, s.Machine);
            map.Alias(AiAliasKind.Program, s.Program);
            map.Alias(AiAliasKind.Module, s.Module);
        }

        return map;
    }

    private static string SessionText(LockExplainSession s, AiAliasMap? aliases)
    {
        var text = new StringBuilder();
        Line("blocked_by", s.BlockerKey is { } b ? LockText.Key(b) : "(none — this session holds the lock)");
        Line("user", Name(AiAliasKind.User, s.Username));
        Line("os_user", Name(AiAliasKind.User, s.OsUser));
        Line("machine", Name(AiAliasKind.Host, s.Machine));
        Line("program", Name(AiAliasKind.Program, s.Program));
        Line("module", Name(AiAliasKind.Module, s.Module));
        Line("status", s.Status);
        Line("event", s.Event);
        Line("wait_class", s.WaitClass);
        Line("seconds_in_wait", Number(s.SecondsInWait));
        Line("seconds_since_last_call", Number(s.LastCallElapsedSeconds));
        Line("locked_object", s.LockedObject);
        Line("held_mode", s.HeldMode);
        Line("requested_mode", s.RequestedMode);
        Line("transaction_start", s.TransactionStart?.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture));
        Line("undo_blocks", Number(s.UndoBlocks));
        Line("sql_id", s.SqlId);
        Line("prev_sql_id", s.PrevSqlId);
        return text.ToString().TrimEnd();

        string? Name(AiAliasKind kind, string? value) => aliases is null ? value : aliases.Alias(kind, value);

        void Line(string name, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                text.Append(name).Append(": ").AppendLine(value.ReplaceLineEndings(" ").Trim());
            }
        }
    }

    private static string? Cut(string? sql) =>
        string.IsNullOrWhiteSpace(sql) ? null : sql.Length <= MaxSqlChars ? sql : sql[..MaxSqlChars] + "\n-- (truncated)";

    /// <summary>결론 칸의 첫 줄에서 글머리표·인용·굵게 표시를 벗긴다.</summary>
    internal static string? OneLine(string body)
    {
        var first = body.ReplaceLineEndings("\n").Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
        return first?.TrimStart('-', '*', '>', ' ', '•').Replace("**", string.Empty, StringComparison.Ordinal).Trim();
    }

    private static string? Number(long? value) => value?.ToString(CultureInfo.InvariantCulture);
}
