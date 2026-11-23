using Oranda.App.Features.Ai.Tuning;
using Oranda.App.Features.SqlEditor;
using Oranda.Core.Ai.Schemas;
using Oranda.Core.Composition;
using Oranda.Core.Execution;
using Oranda.Core.Language;
using Oranda.Core.Security;
using Oranda.Core.Text;
using Oranda.Language.Lexing;

namespace Oranda.App.Features.Ai.External.Editor;

/// <summary>
/// 밖의 에이전트 편집기 도구의 안쪽(설계 agent-cli-control §5.3 「편집기」, §6.1): 탭 목록·읽기·활성화·새 탭·제안·저장 요청·바인드.
/// 도구 처리자(계약의 <c>IAppToolHandler</c>)는 인자를 풀어 이것을 UI 스레드에서 부른다. 이 객체는 DB 를 쓰지 않는다.
/// 기존 탭 글은 제안으로만 바뀐다: 띠 → 사람이 「받아들이기」 → 비교 창 → 실행 취소 1단계로 넣음. 모든 메서드는 UI 스레드에서 부른다.
/// 처리자들이 하나를 함께 쓴다(제안 대기열이 이 안에 있다).
/// </summary>
[OrandaService(typeof(ExternalEditorService))]
public sealed partial class ExternalEditorService
{
    /// <summary>새 탭 한도: <see cref="NewTabWindow"/> 안에 이만큼.</summary>
    public const int NewTabLimit = AppToolValues.NewTabsPerMinute;

    /// <summary>탭 글을 한 번에 줄 수 있는 최대 글자 수.</summary>
    public const int MaxChars = 50_000;

    /// <summary>탭 글 기본 글자 수.</summary>
    public const int DefaultChars = 20_000;

    /// <summary>새 탭 한도를 세는 시간.</summary>
    public static readonly TimeSpan NewTabWindow = TimeSpan.FromMinutes(1);

    private readonly Func<SqlEditorHost> _host = () => SqlEditorHost.Current;
    private readonly Queue<DateTime> _newTabTimes = new();

    // 생성자는 매개변수 없이 둔다(셸의 생성자 고르기가 등록 안 된 매개변수로 지연 어셈블리를 깨우지 않게, EditorContext 와 같다).

    /// <summary>다루는 호스트(시험이 바꾼다). 기본 = <see cref="SqlEditorHost.Current"/>.</summary>
    internal Func<SqlEditorHost> HostSource
    {
        init => _host = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>시계(시험이 바꾼다).</summary>
    internal Func<DateTime> UtcNow { get; init; } = () => DateTime.UtcNow;

    private SqlEditorHost Host => _host();

    private HostEditorTabs Tabs => new(_host);

    // ---- list_tabs · get_tab · focus_tab ------------------------------------------------

    /// <summary>
    /// 열린 탭 목록(<c>list_tabs</c>). 허가 밖 접속의 탭은 잠김(<c>locked</c>)으로 식별자·제목만 준다. 파일은 이름만(폴더 경로를 빼서) 준다.
    /// <paramref name="workspace"/> 가 있으면 그 작업 공간 이름의 탭만.
    /// </summary>
    public IReadOnlyList<ExternalTabSummary> ListTabs(IExternalTabAccess access, string? workspace = null)
    {
        ArgumentNullException.ThrowIfNull(access);
        return [.. Tabs.ListTabs()
            .Where(t => workspace is null || string.Equals(t.Workspace, workspace, StringComparison.OrdinalIgnoreCase))
            .Select(t => Summary(access, t))];
    }

    private static ExternalTabSummary Summary(IExternalTabAccess access, EditorTabInfo tab)
    {
        if (access.IsLocked(tab.ConnectionProfileId))
        {
            return new ExternalTabSummary(tab.TabId, tab.Title, Locked: true) { Active = tab.IsActive };
        }

        return new ExternalTabSummary(tab.TabId, tab.Title, Locked: false)
        {
            Workspace = tab.Workspace,
            Kind = tab.Kind,
            ConnectionProfileId = tab.ConnectionProfileId,
            Dirty = tab.IsDirty,
            ReadOnly = tab.IsReadOnly,
            Version = tab.Version,
            Active = tab.IsActive,
            FileName = tab.FilePath is { } path ? Path.GetFileName(path) : null,
        };
    }

    /// <summary>
    /// 탭 글 읽기(<c>get_tab</c>). <paramref name="tabId"/> 가 없으면 활성 탭. 범위는 문서·선택·커서 문장 중 하나이고 <paramref name="maxChars"/>(1~50,000, 기본 20,000)에서 자른다.
    /// 열 블록·여러 커서 선택은 캐럿 자리(길이 0)와 <c>block</c> 으로 알린다. 잠긴 탭은 거부한다.
    /// </summary>
    public ExternalEditorResult<ExternalTabContent> GetTab(IExternalTabAccess access, Guid? tabId, ExternalTabScope scope = ExternalTabScope.Document, int? maxChars = null)
    {
        ArgumentNullException.ThrowIfNull(access);
        if (Resolve<ExternalTabContent>(access, tabId, out var tab) is { } refused)
        {
            return refused;
        }

        var text = tab!.Text;
        var selection = tab.SelectionIsBlock
            ? new ExternalSelection(tab.CaretOffset, 0, Block: true)
            : new ExternalSelection(tab.SelectionStart, tab.SelectionLength, Block: false);
        var statement = StatementSplitter.GetStatementAt(text, tab.CaretOffset, SplitOptions.Default);
        var range = scope switch
        {
            ExternalTabScope.Selection => new TextRange(selection.Start, selection.Length),
            ExternalTabScope.Statement => statement?.Range ?? new TextRange(tab.CaretOffset, 0),
            _ => new TextRange(0, text.Length),
        };
        range = Clamp(range, text.Length);
        var limit = Math.Clamp(maxChars ?? DefaultChars, 1, MaxChars);
        var truncated = range.Length > limit;
        var piece = text.Substring(range.Start, truncated ? limit : range.Length);
        var (line, col) = LineCol(text, tab.CaretOffset);
        return ExternalEditorResult.Ok<ExternalTabContent>(new ExternalTabContent(tab.TabId, tab.Version, piece, truncated, range)
        {
            Caret = new ExternalCaret(tab.CaretOffset, line, col),
            Selection = selection,
            Statement = statement is null ? null : new ExternalStatement(statement.Range.Start, statement.Range.Length, KindText(statement.Kind)),
            ConnectionProfileId = tab.ConnectionProfileId,
            Binds = tab.BindNames,
        });
    }

    /// <summary>
    /// 탭을 활성으로 만든다(<c>focus_tab</c>, 화면만 바뀐다). <paramref name="line"/> 이 있으면 그 줄로 커서를 옮기고, <paramref name="range"/> 가 있으면 그 범위를 잠깐 칠한다.
    /// 글을 주지 않으므로 잠긴 탭도 활성화할 수 있다.
    /// </summary>
    public ExternalEditorResult<bool> FocusTab(Guid tabId, int? line = null, TextRange? range = null)
    {
        if (Find(tabId) is not { } tab)
        {
            return ExternalEditorResult.Fail<bool>(ExternalEditorErrors.TabNotFound, ExternalEditorText.TabNotFound);
        }

        if (line is { } l && (l < 1 || l > tab.Document.LineCount))
        {
            return ExternalEditorResult.Fail<bool>(ExternalEditorErrors.InvalidRange, ExternalEditorText.LineOutside);
        }

        if (range is { } r && !r.IsWithin(tab.Document.TextLength))
        {
            return ExternalEditorResult.Fail<bool>(ExternalEditorErrors.InvalidRange, ExternalEditorText.RangeOutside);
        }

        Host.Active = tab;
        if (line is { } go)
        {
            tab.GoToLine(go);
        }

        if (range is { } flash)
        {
            tab.Surface?.ScrollToLine(tab.PositionOf(flash.Start).Line);
            tab.FlashRange(flash.Start, flash.Length);
        }

        return ExternalEditorResult.Ok<bool>(true);
    }

    // ---- new_tab -------------------------------------------------------------------------

    /// <summary>
    /// 글을 새 탭에 넣는다(<c>new_tab</c>, 기존 글을 덮지 않으므로 바로 한다). 제목 앞에 <c>AI·</c> 를 붙이고, 변경문(DML·DDL·PL/SQL·트랜잭션)이 있으면
    /// 새 탭 띠에 「AI 가 만든 변경문 — 검토 후 직접 실행」 을 보인다. 사람이 보던 탭은 그대로 활성으로 둔다(입력 중인 글이 새 탭으로 가지 않게).
    /// 1분에 10개까지. 접속은 허가 안이어야 한다.
    /// </summary>
    public ExternalEditorResult<ExternalNewTab> NewTab(IExternalTabAccess access, string text, string? title = null, Guid? connectionProfileId = null, string? client = null)
    {
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(text);
        if (connectionProfileId is not null && Refuse<ExternalNewTab>(access, connectionProfileId) is { } refused)
        {
            return refused; // 접속 없는 새 탭은 「탭 글 읽기 허용」과 상관없이 만든다
        }

        var now = UtcNow();
        while (_newTabTimes.Count > 0 && now - _newTabTimes.Peek() >= NewTabWindow)
        {
            _newTabTimes.Dequeue();
        }

        if (_newTabTimes.Count >= NewTabLimit)
        {
            return ExternalEditorResult.Fail<ExternalNewTab>(ExternalEditorErrors.RateLimited, ExternalEditorText.RateLimited);
        }

        _newTabTimes.Enqueue(now);
        var host = Host;
        var previous = host.Active;
        var tab = host.OpenTextTab(text, AiTitle(title), connectionProfileId);
        if (previous is not null && host.Tabs.Contains(previous))
        {
            host.Active = previous;
        }

        if (HasChangeStatement(text))
        {
            BarOf(tab).Enqueue(new AgentBarItem(AgentBarItemKind.Notice, () => ExternalEditorText.ChangeStatementNotice)
            {
                DetailSource = () => ExternalEditorText.NewTabHeadline(ClientName(client)), // 이름이 없으면 「외부 에이전트」 도 지금 언어로
            });
        }

        return ExternalEditorResult.Ok<ExternalNewTab>(new ExternalNewTab(tab.TabId, tab.Version, tab.Title));
    }

    /// <summary>제목 앞에 <c>AI·</c>(이미 있으면 그대로). 빈 제목은 <c>AI·SQL</c>.</summary>
    internal static string AiTitle(string? title)
    {
        var trimmed = string.IsNullOrWhiteSpace(title) ? "SQL" : title.Trim();
        return trimmed.StartsWith(ExternalEditorText.TitlePrefix, StringComparison.Ordinal) ? trimmed : ExternalEditorText.TitlePrefix + trimmed;
    }

    /// <summary>글에 사람이 검토해야 할 변경문(DML·DDL·PL/SQL·트랜잭션)이 있는가.</summary>
    internal static bool HasChangeStatement(string text) =>
        StatementSplitter.Split(text, SplitOptions.Default)
            .Any(s => s.Kind is StatementKind.Dml or StatementKind.Ddl or StatementKind.PlSql or StatementKind.TransactionControl);

    // ---- get_binds -----------------------------------------------------------------------

    /// <summary>
    /// 탭의 바인드(<c>get_binds</c>): 글이 참조하는 이름과 바인드 창의 줄. 값은 허가가 허락할 때만, 비밀 이름(<see cref="SensitiveBindNames"/>)은 언제나 뺀다.
    /// </summary>
    public ExternalEditorResult<IReadOnlyList<ExternalBind>> GetBinds(IExternalTabAccess access, Guid tabId)
    {
        ArgumentNullException.ThrowIfNull(access);
        if (Find(tabId) is not { } tab)
        {
            return ExternalEditorResult.Fail<IReadOnlyList<ExternalBind>>(ExternalEditorErrors.TabNotFound, ExternalEditorText.TabNotFound);
        }

        if (Refuse<IReadOnlyList<ExternalBind>>(access, tab.ConnectionProfileId) is { } refused)
        {
            return refused;
        }

        return ExternalEditorResult.Ok(DescribeBinds(access, tab));
    }

    /// <summary>
    /// 탭이 아는 바인드 이름(글이 참조하는 이름 + 바인드 창의 줄, 대문자로). <c>get_binds</c> 가 보이는 이름이고 <c>set_binds</c> 가 받는 이름이다.
    /// </summary>
    internal static IEnumerable<string> KnownBindNames(SqlEditorViewModel tab) =>
        tab.ReferencedBindNames
            .Concat(tab.Binds.Rows.Select(r => r.Name))
            .Select(Oranda.App.Features.Binds.BindVariableRow.Normalize)
            .Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 바인드 줄들(<c>get_binds</c> 모양). 값은 허가가 허락할 때만, 비밀 이름은 가린다. <paramref name="only"/> 가 있으면 그 이름만
    /// (<c>set_binds</c> 가 탭 글을 읽을 수 없는 허가에서 에이전트가 보낸 이름만 돌려줄 때).
    /// </summary>
    internal static IReadOnlyList<ExternalBind> DescribeBinds(IExternalTabAccess access, SqlEditorViewModel tab, IEnumerable<string>? only = null)
    {
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(tab);
        var values = access.CanReadBindValues(tab.ConnectionProfileId);
        var binds = (only ?? KnownBindNames(tab)).Select(name =>
        {
            var row = tab.Binds.Find(name);
            var has = row is { Text.Length: > 0 };
            var value = !has || !values ? null : access.MasksSensitive(tab.ConnectionProfileId) && SensitiveBindNames.IsSensitive(name) ? AppToolValues.Masked : row!.Text;
            return new ExternalBind(name, row?.Type.ToString().ToLowerInvariant(), has, value);
        });
        return [.. binds];
    }

    // ---- 도움 ------------------------------------------------------------------------------

    private SqlEditorViewModel? Find(Guid tabId) => Host.Tabs.FirstOrDefault(t => t.TabId == tabId);

    /// <summary>탭을 찾고 잠김을 본다. 거부면 결과, 아니면 null 과 탭 모습.</summary>
    private ExternalEditorResult<T>? Resolve<T>(IExternalTabAccess access, Guid? tabId, out EditorTabInfo? tab)
    {
        var id = tabId ?? Host.Active?.TabId;
        tab = id is { } found ? Tabs.Describe(found) : null;
        if (tab is null)
        {
            return id is null
                ? ExternalEditorResult.Fail<T>(ExternalEditorErrors.NoActiveTab, ExternalEditorText.NoActiveTab)
                : ExternalEditorResult.Fail<T>(ExternalEditorErrors.TabNotFound, ExternalEditorText.TabNotFound);
        }

        return Refuse<T>(access, tab.ConnectionProfileId);
    }

    /// <summary>탭 글을 읽을 수 없으면 거부 결과, 읽을 수 있으면 null.</summary>
    private static ExternalEditorResult<T>? Refuse<T>(IExternalTabAccess access, Guid? connectionProfileId)
    {
        if (access.CanReadText(connectionProfileId))
        {
            return null;
        }

        var (code, message) = access.Refusal(connectionProfileId);
        return ExternalEditorResult.Fail<T>(code, message);
    }

    private static AgentEditorBarViewModel BarOf(SqlEditorViewModel tab) => tab.AgentBar ??= new AgentEditorBarViewModel();

    private static string ClientName(string? client) => string.IsNullOrWhiteSpace(client) ? ExternalEditorText.DefaultClient : client.Trim();

    private static TextRange Clamp(TextRange range, int length)
    {
        var start = Math.Clamp(range.Start, 0, length);
        return new TextRange(start, Math.Clamp(range.Length, 0, length - start));
    }

    /// <summary>글 안 위치의 줄·열(1부터). 줄 끝 <c>\r\n</c>·<c>\n</c>·<c>\r</c> 를 모두 줄바꿈으로 센다(편집기와 같다).</summary>
    internal static (int Line, int Col) LineCol(string text, int offset)
    {
        offset = Math.Clamp(offset, 0, text.Length);
        int line = 1, lineStart = 0;
        for (var i = 0; i < offset; i++)
        {
            var c = text[i];
            if (c == '\n' || (c == '\r' && (i + 1 >= text.Length || text[i + 1] != '\n')))
            {
                line++;
                lineStart = i + 1;
            }
        }

        return (line, offset - lineStart + 1);
    }

    private static string KindText(StatementKind kind) => kind switch
    {
        StatementKind.Query => "query",
        StatementKind.Dml => "dml",
        StatementKind.Ddl => "ddl",
        StatementKind.PlSql => "plsql",
        StatementKind.TransactionControl => "transaction",
        StatementKind.SqlPlus => "sqlplus",
        _ => "other",
    };
}
