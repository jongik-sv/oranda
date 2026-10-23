using System.Runtime.CompilerServices;
using System.Text;
using Avalonia.Controls;
using Oranda.App.Features.ConnectionManager;
using Oranda.App.Features.FormatterProfiles;
using Oranda.App.Features.MyBatisWorkspace.Workspace;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell;
using Oranda.Core.Formatting;
using Oranda.Core.Shell;
using Oranda.Core.Storage;
using Oranda.Core.Text;

namespace Oranda.App.Features.MyBatisWorkspace.Format;

/// <summary>
/// 「매퍼 전체 포맷」 명령: 활성 MyBatis 작업 공간 매퍼의 select·insert·update·delete·sql 요소 전부를 「쿼리 포맷」 과 같은 문장 포매터로 정리한다.
/// 파일에 바로 쓰지 않는다: 바뀐 요소만 세션의 저장 안 한 변경(*)이 되고, 「xml 에 저장」 비교·저장 흐름으로 쓴다(저장 때 파일 들여쓰기를 되붙인다).
/// 포맷은 스레드 풀에서, 적용은 UI 스레드에서 한 번에 한다(<see cref="MyBatisWorkspaceSession.SetEntryTexts"/>). 실패·읽기 전용 요소는 원문을 두고,
/// 끝에 「N개 정리, K개는 이미 정리됨, M개는 원문 유지」 와 원문 유지 사유를 메시지 상자로 한 번 알린다.
/// 「매퍼 전체 포맷 되돌리기」 는 직전 전체 포맷 한 번분을 되돌린다: 지금 글이 포맷 글 그대로인 요소만 이전 글로 돌리고, 그 사이 고친 요소는 건너뛴다.
/// 저장·밖에서 바뀐 디스크 병합(세션 기준 문서가 바뀜)이나 다른 작업 공간(다른 매퍼 등)을 활성으로 하면 되돌릴 목록을 버린다.
/// 처리기는 실행마다 새로 만들어지므로 실행 중 표시·되돌릴 목록은 호스트마다 따로 둔다.
/// </summary>
[OrandaCommand(MyBatisFormatAllCommands.FormatAll, CommandCategory.Format, Icon = "formatAll", Enablement = "mybatisDocActive && !" + MyBatisFormatAllCommands.BusyKey,
    Placements = ["toolbar:mybatis/formatAll@36", "menu:edit/mybatisFormatAll@406"])]
[OrandaCommand(MyBatisFormatAllCommands.UndoFormatAll, CommandCategory.Format, Enablement = "mybatisDocActive && !" + MyBatisFormatAllCommands.BusyKey,
    Placements = ["menu:edit/mybatisUndoFormatAll@407"])]
public sealed class MyBatisFormatAllCommands : ICommandHandler
{
    public const string FormatAll = "oranda.mybatis.formatAll";

    public const string UndoFormatAll = "oranda.mybatis.undoFormatAll";

    /// <summary>전체 포맷이 도는 중(문맥 키). 두 명령이 그동안 꺼진다.</summary>
    internal const string BusyKey = "mybatisFormatAllBusy";

    /// <summary>알림에 적는 요소 id 수 상한(메시지 상자에 스크롤이 없다). 넘으면 「외 K개」.</summary>
    internal const int MaxListed = 20;

    private static readonly ConditionalWeakTable<SqlEditorHost, HostState> States = new();

    private readonly IServiceProvider? _services;

    public MyBatisFormatAllCommands()
    {
    }

    public MyBatisFormatAllCommands(IServiceProvider services) => _services = services;

    /// <summary>다루는 호스트. 기본은 <see cref="SqlEditorHost.Current"/>(시험이 바꾼다).</summary>
    internal SqlEditorHost? Host { get; init; }

    /// <summary>시험용: 포매터.</summary>
    internal ISqlFormatter? Formatter { get; init; }

    /// <summary>시험용: 지금 쓸 옵션.</summary>
    internal Func<CancellationToken, Task<FormatterOptions>>? OptionsProvider { get; init; }

    /// <summary>시험용·셸: 알림 표시. 기본은 메인 창 메시지 상자.</summary>
    internal Func<string, Task>? Notify { get; init; }

    /// <summary>실행 중 표시를 걸 문맥 키. 기본은 셸의 <see cref="ContextKeyService"/>.</summary>
    internal ContextKeyService? Keys { get; init; }

    /// <summary>
    /// 그 작업 공간의 매퍼를 전체 포맷하는 중인가. 저장 훅이 그동안 저장을 미룬다: 저장 끝의 기준 다시 싣기가 막 적용한 포맷 글을 덮기 때문이다
    /// (「xml 에 저장」 단추는 문맥 키로 꺼지지만 Ctrl+S·모두 저장·닫기 확인 저장은 훅을 바로 부른다).
    /// </summary>
    internal static bool IsFormatting(SqlWorkspaceViewModel workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        return States.TryGetValue(workspace.Host, out var state) && ReferenceEquals(state.Running, workspace);
    }

    public Task ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.CommandId == UndoFormatAll ? UndoAsync() : FormatAsync(cancellationToken);
    }

    private async Task FormatAsync(CancellationToken ct)
    {
        var host = Host ?? SqlEditorHost.Current;
        var state = States.GetOrCreateValue(host);
        if (state.Busy || host.ActiveWorkspace?.MyBatis is not MyBatisWorkspaceSession session)
        {
            return;
        }

        var formatter = Formatter ?? _services?.GetService(typeof(IMyBatisStatementFormatter)) as ISqlFormatter;
        if (formatter is null)
        {
            return;
        }

        var keys = Keys ?? _services?.GetService(typeof(ContextKeyService)) as ContextKeyService;
        string message;
        state.Running = session.Workspace;
        keys?.Set(BusyKey, true);
        try
        {
            message = await FormatSessionAsync(host, state, session, formatter, ct).ConfigureAwait(true);
        }
        finally
        {
            state.Running = null;
            keys?.Set(BusyKey, false);
        }

        await NotifyAsync(message).ConfigureAwait(true);
    }

    private async Task<string> FormatSessionAsync(SqlEditorHost host, HostState state, MyBatisWorkspaceSession session, ISqlFormatter formatter, CancellationToken ct)
    {
        // 탭 글은 UI 스레드에서만 읽는다: 요소와 그때 글을 먼저 잡아 두고, 포매터에는 글만 넘긴다.
        var document = session.Document;
        var jobs = session.Entries.Where(e => !e.IsDeleted && e.CanOpen).Select(e => (Entry: e, Text: e.Text, Editable: e.IsEditable)).ToList();
        if (jobs.Count == 0)
        {
            return MyBatisFormatAllText.NothingToFormat;
        }

        var options = await GetOptionsAsync(ct).ConfigureAwait(true);
        var inputs = jobs.Select(j => j.Editable ? j.Text : null).ToArray();
        var results = await Task.Run(() => Array.ConvertAll(inputs, t => t is null ? null : FormatOne(formatter, t, options)), ct).ConfigureAwait(true);

        // 포맷하는 사이 저장·밖 병합으로 요소가 새로 지어졌거나 작업 공간을 닫았으면 아무것도 바꾸지 않는다.
        if (!ReferenceEquals(session.Workspace.MyBatis, session) || !ReferenceEquals(session.Document, document))
        {
            return MyBatisFormatAllText.MapperChanged;
        }

        var changes = new List<(MyBatisStatementEntry Entry, string Text)>();
        var befores = new List<string>();
        var kept = new List<string>();
        int already = 0;
        for (int i = 0; i < jobs.Count; i++)
        {
            var (entry, before, _) = jobs[i];
            if (results[i] is not { } result)
            {
                kept.Add(MyBatisFormatAllText.Reason(entry.Id!, MyBatisFormatAllText.ReadOnly));
            }
            else if (entry.IsDeleted || !entry.IsEditable || !session.Entries.Contains(entry) || !string.Equals(entry.Text, before, StringComparison.Ordinal))
            {
                kept.Add(MyBatisFormatAllText.Reason(entry.Id ?? "?", MyBatisFormatAllText.ChangedWhileFormatting));
            }
            else if (!result.Succeeded)
            {
                kept.Add(MyBatisFormatAllText.Reason(entry.Id!, FormatterProfilesCommands.Describe(result)));
            }
            else if (string.Equals(result.Text, before, StringComparison.Ordinal))
            {
                already++;
            }
            else
            {
                changes.Add((entry, result.Text));
                befores.Add(before);
            }
        }

        // 되돌리기는 직전 한 번분이다: 바꾼 것이 없는 실행도 앞 실행의 목록을 버린다.
        state.Forget();
        if (changes.Count > 0)
        {
            session.SetEntryTexts(changes);

            // 지금 글(탭이면 탭 문서)을 포맷 글로 남긴다: 되돌리기는 이것과 같은 요소만 돌린다.
            // 포맷하는 사이 다른 작업 공간으로 옮겼으면 남기지 않는다(옮김 알림이 이미 지나가 목록을 버릴 때를 놓친다).
            if (ReferenceEquals(host.ActiveWorkspace, session.Workspace))
            {
                state.Remember(host, new History(session, document, [.. changes.Select((c, i) => (c.Entry, befores[i], c.Entry.Text))]));
            }
        }

        var text = new StringBuilder(MyBatisFormatAllText.Summary(changes.Count, already, kept.Count));
        AppendList(text, MyBatisFormatAllText.KeptHeading, kept);
        return text.ToString();
    }

    private async Task UndoAsync()
    {
        var host = Host ?? SqlEditorHost.Current;
        var state = States.GetOrCreateValue(host);
        if (state.Busy || host.ActiveWorkspace?.MyBatis is not MyBatisWorkspaceSession session)
        {
            return;
        }

        var history = state.Last;
        state.Forget(); // 직전 한 번분만: 되돌린 뒤에는 다시 되돌릴 것이 없다
        if (history is null || !ReferenceEquals(history.Session, session) || !ReferenceEquals(history.Document, session.Document)
            || !ReferenceEquals(session.Workspace.MyBatis, session))
        {
            await NotifyAsync(MyBatisFormatAllText.NothingToUndo).ConfigureAwait(true);
            return;
        }

        var back = new List<(MyBatisStatementEntry Entry, string Text)>();
        var skipped = new List<string>();
        foreach (var (entry, before, after) in history.Items)
        {
            if (!entry.IsDeleted && entry.IsEditable && session.Entries.Contains(entry) && string.Equals(entry.Text, after, StringComparison.Ordinal))
            {
                back.Add((entry, before));
            }
            else
            {
                skipped.Add("· " + (entry.Id ?? "?"));
            }
        }

        if (back.Count > 0)
        {
            session.SetEntryTexts(back);
        }

        var text = new StringBuilder(MyBatisFormatAllText.Undone(back.Count, skipped.Count));
        AppendList(text, MyBatisFormatAllText.SkippedHeading, skipped);
        await NotifyAsync(text.ToString()).ConfigureAwait(true);
    }

    /// <summary>요소 하나를 포맷한다. 포매터가 던지면 그 요소만 실패(내부 오류)로 둔다 — 요소 하나 때문에 전체가 멈추지 않게.</summary>
    private static FormatResult FormatOne(ISqlFormatter formatter, string text, FormatterOptions options)
    {
        try
        {
            return formatter.Format(text, options);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new FormatResult(text, new TextRange(0, text.Length), FormatFailure.InternalError) { Message = ex.Message };
        }
    }

    private static void AppendList(StringBuilder text, string heading, List<string> lines)
    {
        if (lines.Count == 0)
        {
            return;
        }

        text.Append('\n').Append('\n').Append(heading);
        foreach (var line in lines.Take(MaxListed))
        {
            text.Append('\n').Append(line);
        }

        if (lines.Count > MaxListed)
        {
            text.Append('\n').Append(MyBatisFormatAllText.More(lines.Count - MaxListed));
        }
    }

    private async Task<FormatterOptions> GetOptionsAsync(CancellationToken ct)
    {
        if (OptionsProvider is not null)
        {
            return await OptionsProvider(ct).ConfigureAwait(true);
        }

        return await FormatterIds.ActiveOptionsAsync(
            _services?.GetService(typeof(ISettingsStore)) as ISettingsStore,
            _services?.GetService(typeof(IFormatterProfileStore)) as IFormatterProfileStore,
            ct).ConfigureAwait(true);
    }

    private Task NotifyAsync(string message)
    {
        if (Notify is not null)
        {
            return Notify(message);
        }

        return (_services?.GetService(typeof(ShellController)) as ShellController)?.MainWindow is Window owner
            ? ConnectionDialogs.MessageAsync(owner, message)
            : Task.CompletedTask;
    }

    /// <summary>
    /// 직전 전체 포맷 한 번분: 세션·그때 기준 문서(저장·병합으로 바뀌면 무효)와 요소마다 (요소, 이전 글, 포맷 글).
    /// 기준 문서는 <c>object</c> 로 든다(App 형 필드에 Language 형을 두지 않는다, design.md 9장 R21).
    /// </summary>
    private sealed record History(MyBatisWorkspaceSession Session, object Document, IReadOnlyList<(MyBatisStatementEntry Entry, string Before, string After)> Items);

    /// <summary>호스트 하나의 실행 중 표시와 되돌릴 목록. 다른 작업 공간(다른 매퍼 등)이 활성이 되면 목록을 버린다.</summary>
    private sealed class HostState
    {
        private SqlEditorHost? _host;

        /// <summary>전체 포맷 중인 작업 공간(없으면 null).</summary>
        public SqlWorkspaceViewModel? Running { get; set; }

        public bool Busy => Running is not null;

        public History? Last { get; private set; }

        public void Remember(SqlEditorHost host, History history)
        {
            Forget();
            _host = host;
            Last = history;
            host.ActiveWorkspaceChanged += OnActiveWorkspaceChanged;
        }

        public void Forget()
        {
            if (_host is { } host)
            {
                host.ActiveWorkspaceChanged -= OnActiveWorkspaceChanged;
            }

            _host = null;
            Last = null;
        }

        private void OnActiveWorkspaceChanged(object? sender, EventArgs e)
        {
            // 새 매퍼를 열 때는 작업 공간이 먼저 활성이 되고 세션은 그 뒤에 붙는다: 세션이 아니라 작업 공간으로 견준다.
            if (_host is { } host && !ReferenceEquals(host.ActiveWorkspace, Last?.Session.Workspace))
            {
                Forget();
            }
        }
    }
}
