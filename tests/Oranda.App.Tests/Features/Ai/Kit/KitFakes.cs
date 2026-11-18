using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Oranda.App.Features.Ai.Chat;
using Oranda.App.Features.Ai.Kit;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Connection;
using Oranda.Core.Settings;

namespace Oranda.App.Tests.Features.Ai.Kit;

/// <summary>AI 공통 부품 시험의 가짜들. 가짜 드라이버는 실제 드라이버처럼 실행 범위(Session)가 없거나 폐기됐으면 거절한다.</summary>
internal static class KitFakes
{
    internal static ConnectionProfile Profile(ConnectionEnvironment env = ConnectionEnvironment.Development, AiAccessLevel? access = null) =>
        new("dev-example", "SAMPLE", "h:1521/s", Id: Guid.NewGuid(), Environment: env, AiAccess: access);

    internal static AiOneShotRun Run(FakeDriver driver, ConnectionProfile? profile = null, AiSettings? settings = null, FakeAudit? audit = null, List<AgentSession>? tracked = null) =>
        new(new AiOneShotSetup(driver, AiKitRoute.For(profile ?? Profile(access: AiAccessLevel.Plan), settings ?? new AiSettings { Model = "haiku" }), "lock_explain")
        {
            Audit = audit,
            TrackSession = tracked is null ? null : s =>
            {
                tracked.Add(s);
                return s;
            },
        });

    internal static IReadOnlyList<AiEvent> Answer(string text, string? resume = "resume-1") =>
        [new AiStarted(resume), new AiTextDelta(text), new AiFinal(null, null, resume)];

    internal static AiChatHost Host(AiChatViewModel chat, Func<string, Task<bool>>? askStop = null, Func<AiImageSendNotice, CancellationToken, Task<bool>>? confirmText = null) =>
        new(() => Task.FromResult<AiChatViewModel?>(chat), askStop, confirmText);

    internal static async Task WaitAsync(Func<bool> done)
    {
        for (var i = 0; i < 200 && !done(); i++)
        {
            await Task.Delay(10);
        }

        Assert.True(done(), "기다린 상태가 오지 않았습니다");
    }
}

internal sealed class FakeDriver : IAiDriver
{
    public List<AiRequest> Requests { get; } = [];

    public Func<AiRequest, IReadOnlyList<AiEvent>> Answer { get; set; } = _ => KitFakes.Answer("답");

    public AiDriverCapabilities Capabilities { get; } = new(false, false, true, false, true);

    /// <summary>있으면 요청을 받은 뒤 이것이 끝날 때까지 기다린다(진행 중 상태 시험).</summary>
    public TaskCompletionSource? Gate { get; set; }

    public List<bool> RevokedAtStart { get; } = [];

    public async IAsyncEnumerable<AiEvent> RunAsync(AiRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        Requests.Add(request);
        RevokedAtStart.Add(request.Session?.IsRevoked ?? false);
        if (request.Session is null)
        {
            yield return new AiError("AI 요청에 실행 범위(Session)가 없습니다.", "session_required");
            yield break;
        }

        if (request.Session.IsRevoked)
        {
            yield return new AiError("이 AI 요청의 실행 범위가 이미 폐기되었습니다.", "session_revoked");
            yield break;
        }

        if (Gate is { } gate)
        {
            yield return new AiTextDelta("부분 ");
            await gate.Task.WaitAsync(ct);
        }

        foreach (var e in Answer(request))
        {
            ct.ThrowIfCancellationRequested();
            yield return e;
        }
    }
}

internal sealed class FakeAudit : IAgentAuditStore
{
    public List<AgentAuditEntry> Entries { get; } = [];

    public ValueTask AppendAsync(AgentAuditEntry entry, CancellationToken ct)
    {
        Entries.Add(entry);
        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyList<AgentAuditEntry>> ListRecentAsync(int limit, Guid? profileId, CancellationToken ct) =>
        ValueTask.FromResult<IReadOnlyList<AgentAuditEntry>>(Entries);
}

/// <summary>작은따옴표 문자열을 가리는 시험용 가림. 따옴표가 홀수 개면(닫히지 않은 문자열) 실제 렉서처럼 가리지 못한다.</summary>
internal sealed class KitMasker : ISqlLiteralMasker
{
    public LiteralVault CreateVault(string statementText) => new();

    public SqlMaskResult Mask(string sql, LiteralVault vault) =>
        sql.Count(c => c == '\'') % 2 == 1
            ? SqlMaskResult.Failed
            : SqlMaskResult.Ok(Regex.Replace(sql, "'[^']*'", m => vault.Add(m.Value)));

    public SqlRestoreResult Restore(string sql, LiteralVault vault) => SqlRestoreResult.Ok(sql);

    public string MaskPredicate(string predicate, LiteralVault vault) => predicate;
}
