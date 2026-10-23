using System.Globalization;
using Oranda.App.Features.MyBatisLog;
using Oranda.App.Features.MyBatisWorkspace.Hooks;
using Oranda.App.Features.SqlEditor;
using Oranda.Core.MyBatis;
using Oranda.Language.MyBatis;

namespace Oranda.App.Features.MyBatisWorkspace.LogPaste;

/// <summary>로그 한 문장이 열린 곳: 문장 탭이 열린 매퍼·문장 id·저장한 시나리오 이름.</summary>
public sealed record MyBatisLogOpened(string MapperPath, string StatementId, string ScenarioName);

/// <summary>
/// 붙여 넣기 결과. <see cref="Opened"/> = 문장을 찾고 값을 맞춰 시나리오를 만든 것,
/// <see cref="Fallback"/> = 못 맞춰 일반 SQL 탭으로 붙여 넣은 문장 수(그 안내 문구는 처리기가 보인다), <see cref="Cancelled"/> = 후보 고르기에서 취소.
/// </summary>
public sealed record MyBatisLogFlowResult(IReadOnlyList<MyBatisLogOpened> Opened, int Fallback, bool Cancelled);

/// <summary>
/// MyBatis 문서에서 「MyBatis 로그 붙여 넣기」(MB-09, TSK-03-06). 로그 문장마다 logger 이름(<c>namespace.id</c>)으로 열린 매퍼에서 문장을 찾고(<see cref="IStatementLocator"/>),
/// logger 가 없거나 못 찾으면 펼친 SQL 모양이 같은 문장 후보를 고르게 한다. 문장이 정해지면 <see cref="MyBatisLogValueMatcher"/> 로 값을 맞춰
/// 새 시나리오 「로그 HH:mm」로 저장하고 그 문장 탭을 연다. 맞출 수 없는 문장은 일반 SQL 탭 붙여 넣기(<see cref="MyBatisLogPaste"/>)로 보낸다.
/// UI 스레드에서 부른다.
/// </summary>
public sealed class MyBatisLogPasteFlow(
    SqlEditorHost host, IMyBatisEvaluator evaluator, IMyBatisScenarioStore store, IEditorDialogs dialogs, Func<DateTimeOffset>? now = null)
{
    private readonly Func<DateTimeOffset> _now = now ?? (() => DateTimeOffset.Now);

    /// <param name="text">클립보드 로그 글.</param>
    /// <param name="current">붙여 넣기를 한 MyBatis 작업 공간의 세션(후보를 찾을 때 먼저 본다).</param>
    /// <returns>로그에 Preparing 쌍이 없으면 null.</returns>
    public async Task<MyBatisLogFlowResult?> RunAsync(string text, IMyBatisWorkspaceSession current, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(current);
        var entries = MyBatisLogEntries.Split(text);
        if (entries.Count == 0)
        {
            return null;
        }

        var sessions = OpenSessions(current);
        var opened = new List<MyBatisLogOpened>();
        var fallback = new List<MyBatisLogEntry>();
        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            var target = await FindTargetAsync(entry, sessions, ct).ConfigureAwait(true);
            if (target.Cancelled)
            {
                return new MyBatisLogFlowResult(opened, 0, Cancelled: true);
            }

            if (target.Values is null || target.Session is null || target.Id is null)
            {
                fallback.Add(entry);
                continue;
            }

            var scenario = await store.SaveAsync(new MyBatisScenario
            {
                MapperPath = target.Session.MapperPath,
                Namespace = target.Session.MapperNamespace,
                StatementId = target.Id,
                Name = "로그 " + _now().ToString("HH:mm", CultureInfo.InvariantCulture),
                Values = target.Values,
            }, ct).ConfigureAwait(true);
            target.Session.OpenStatement(target.Id);
            opened.Add(new MyBatisLogOpened(target.Session.MapperPath, target.Id, scenario.Name));
        }

        if (fallback.Count > 0)
        {
            var tab = host.NewDocument();
            MyBatisLogPaste.Apply(tab, tab.Binds, string.Join('\n', fallback.Select(e => e.Text)));
        }

        return new MyBatisLogFlowResult(opened, fallback.Count, Cancelled: false);
    }

    // 붙여 넣은 작업 공간의 세션을 먼저, 그 밖의 열린 매퍼가 뒤.
    private List<IMyBatisWorkspaceSession> OpenSessions(IMyBatisWorkspaceSession current)
    {
        var list = new List<IMyBatisWorkspaceSession> { current };
        list.AddRange(host.Workspaces.Select(w => w.MyBatis).OfType<IMyBatisWorkspaceSession>().Where(s => !ReferenceEquals(s, current)));
        return list;
    }

    private async Task<Target> FindTargetAsync(MyBatisLogEntry entry, List<IMyBatisWorkspaceSession> sessions, CancellationToken ct)
    {
        var candidates = new List<Target>();
        var located = false;
        if (entry.Logger is { } logger)
        {
            foreach (var locator in host.MyBatisHooks.StatementLocators)
            {
                foreach (var location in await locator.FindAsync(logger, exact: true, ct).ConfigureAwait(true))
                {
                    var session = sessions.FirstOrDefault(s => SamePath(s.MapperPath, location.MapperPath));
                    located |= session is not null;
                    if (session is not null && TryMatch(session, location.StatementId, entry) is { } hit && candidates.All(c => !c.Same(hit)))
                    {
                        candidates.Add(hit);
                    }
                }
            }
        }

        // logger 로 문장을 찾았는데 값이 안 맞으면 다른 문장으로 넘어가지 않고 일반 탭으로 보낸다.
        if (candidates.Count == 0 && !located)
        {
            // logger 가 없거나 열린 매퍼에서 못 찾았다: 모양이 같은 문장을 모두 훑는다.
            foreach (var session in sessions)
            {
                foreach (var element in session.Document.Elements.Where(e => e.IsStatement && e.Id is not null))
                {
                    if (TryMatch(session, element.Id!, entry) is { } hit)
                    {
                        candidates.Add(hit);
                    }
                }
            }
        }

        if (candidates.Count <= 1)
        {
            return candidates.Count == 1 ? candidates[0] : default;
        }

        var labels = candidates.Select(c => Label(c)).ToList();
        var picked = await dialogs.ChooseAsync("MyBatis 로그에 맞는 문장 고르기", labels, ct).ConfigureAwait(true);
        return picked is null ? new Target { Cancelled = true } : candidates[labels.IndexOf(picked)];
    }

    private Target? TryMatch(IMyBatisWorkspaceSession session, string statementId, MyBatisLogEntry entry)
    {
        // 탭에서 고친 글이 있으면 그 글(저장 전 상태)로 맞춘다.
        var element = session.ElementOf(statementId) ?? session.Document.Find(statementId);
        if (element is not { IsStatement: true })
        {
            return null;
        }

        var values = MyBatisLogValueMatcher.Match(evaluator, element, session.Fragments, entry);
        return values is null ? null : new Target { Session = session, Id = statementId, Values = values };
    }

    private static string Label(Target c) =>
        (c.Session!.MapperNamespace is { } ns ? ns + "." : string.Empty) + c.Id + "  (" + Path.GetFileName(c.Session.MapperPath) + ")";

    private static bool SamePath(string a, string b)
    {
        try
        {
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private readonly record struct Target
    {
        public IMyBatisWorkspaceSession? Session { get; init; }

        public string? Id { get; init; }

        public IReadOnlyDictionary<string, MyBatisScenarioValue>? Values { get; init; }

        public bool Cancelled { get; init; }

        public bool Same(Target other) => ReferenceEquals(Session, other.Session) && Id == other.Id;
    }
}
