using System.Globalization;
using System.Runtime.CompilerServices;
using Avalonia.Threading;
using Oranda.App.Features.MyBatisWorkspace.Hooks;
using Oranda.Core.Composition;

namespace Oranda.App.Features.MyBatisWorkspace.Combos;

/// <summary>
/// 문장마다 마지막 조합 검사 결과(오류 수)를 들고 있는 곳. 조합 검사 창이 넣고 「문장」 창 배지·문장 탭 머리가 읽는다.
/// 검사한 원문과 지금 원문이 다르면(검사 뒤 고침) 결과를 보이지 않는다. 세션이 사라지면 함께 사라진다(약한 참조). UI 스레드에서 쓴다.
/// </summary>
internal static class ComboCheckResults
{
    private static readonly ConditionalWeakTable<IMyBatisWorkspaceSession, Dictionary<string, Entry>> Sessions = new();

    /// <summary>결과가 바뀌었다.</summary>
    public static event EventHandler? Changed;

    /// <summary>검사 결과를 넣는다. <paramref name="checkedText"/> = 검사한 문장 원문.</summary>
    public static void Set(IMyBatisWorkspaceSession session, string statementId, string checkedText, int errors, int combinations, IReadOnlyList<int> errorLines)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!Sessions.TryGetValue(session, out var map))
        {
            map = [];
            Sessions.Add(session, map);

            // 문장을 고치면(세션 StateChanged) 검사 결과가 맞지 않게 된다. 보임이 바뀐 때만 알린다(글자마다 다시 그리지 않게).
            session.StateChanged += (_, _) =>
            {
                bool flipped = false;
                foreach (var (id, entry) in map)
                {
                    bool current = IsCurrent(session, id, entry);
                    flipped |= current != entry.WasCurrent;
                    entry.WasCurrent = current;
                }

                if (flipped)
                {
                    Raise();
                }
            };
        }

        map[statementId] = new Entry(checkedText, errors, combinations, errorLines) { WasCurrent = true };
        Raise();
    }

    /// <summary>지금 원문 그대로인 문장의 마지막 결과(오류 수, 조합 수). 없거나 그 뒤 고쳤으면 null.</summary>
    public static (int Errors, int Combinations)? Get(IMyBatisWorkspaceSession session, string statementId)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!Sessions.TryGetValue(session, out var map) || !map.TryGetValue(statementId, out var entry) || !IsCurrent(session, statementId, entry))
        {
            return null;
        }

        return (entry.Errors, entry.Combinations);
    }

    // 받는 쪽(「문장」 창·편집기)은 UI 스레드 전용이다. 다른 스레드에서 넣으면 UI 스레드로 넘겨 알린다.
    private static void Raise()
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            Changed?.Invoke(null, EventArgs.Empty);
        }
        else
        {
            Dispatcher.UIThread.Post(() => Changed?.Invoke(null, EventArgs.Empty));
        }
    }

    /// <summary>지금 원문 그대로인 문장의 마지막 검사에서 오류 조합의 원인 줄(1 기반, 중복 없이 오름차순). 없으면 빈 목록.</summary>
    public static IReadOnlyList<int> ErrorLines(IMyBatisWorkspaceSession session, string statementId)
    {
        ArgumentNullException.ThrowIfNull(session);
        return Sessions.TryGetValue(session, out var map) && map.TryGetValue(statementId, out var entry) && IsCurrent(session, statementId, entry)
            ? entry.ErrorLines
            : [];
    }

    private static bool IsCurrent(IMyBatisWorkspaceSession session, string statementId, Entry entry) =>
        session.ElementOf(statementId) is { } element && string.Equals(element.Text, entry.Text, StringComparison.Ordinal);

    private sealed record Entry(string Text, int Errors, int Combinations, IReadOnlyList<int> ErrorLines)
    {
        public bool WasCurrent { get; set; }
    }
}

/// <summary>
/// 「문장」 창 항목의 「조합 오류 n」 배지(R5, design.md 9장 <see cref="IMyBatisStatementBadgeProvider"/>). 마지막 조합 검사에서 오류가 있었고
/// 그 뒤 문장을 고치지 않았을 때만 보인다(고치면 다시 검사해야 맞는 수라 숨긴다).
/// </summary>
[OrandaService(typeof(IMyBatisStatementBadgeProvider))]
public sealed class ComboStatementBadgeProvider : IMyBatisStatementBadgeProvider
{
    public ComboStatementBadgeProvider()
    {
        ComboCheckResults.Changed += (_, _) => BadgesChanged?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? BadgesChanged;

    public IReadOnlyList<MyBatisStatementBadge> BadgesFor(IMyBatisWorkspaceSession session, string statementId)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (ComboCheckResults.Get(session, statementId) is not { Errors: > 0 } result)
        {
            return [];
        }

        return
        [
            new MyBatisStatementBadge(
                ComboText.Badge(result.Errors),
                MyBatisBadgeKind.Error,
                ComboText.BadgeTip(result.Combinations, result.Errors)),
        ];
    }
}
