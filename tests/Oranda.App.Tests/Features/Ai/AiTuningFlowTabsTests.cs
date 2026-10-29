using System.Runtime.CompilerServices;
using System.Text.Json;
using Oranda.App.Features.Ai.Chat;
using Oranda.App.Features.Ai.Tuning;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Extensibility;
using Oranda.Core.Text;

namespace Oranda.App.Tests.Features.Ai;

/// <summary>튜닝 흐름이 시작한 탭을 기억하는 것(f1)과 인덱스 후보 탭을 대화당 하나만 쓰는 것(f2).</summary>
public class AiTuningFlowTabsTests
{
    private const string Original = "SELECT * FROM tb_order WHERE stat_cd = '10'";
    private const string Header = "-- 주문 조회\n";

    private const string ResultJson = """
        {
          "summary": "TB_ORDER 를 FULL 로 읽습니다.",
          "rewrites": [ { "label": "NOT EXISTS 로 재작성", "sql": "SELECT o.* FROM tb_order o WHERE o.stat_cd = '10';", "rationale": "반조인을 쓰게 합니다." } ],
          "index_suggestions": [ { "table": "TB_ORDER", "columns": ["STAT_CD"], "ddl": "CREATE INDEX ix_order_stat ON tb_order (stat_cd);", "rationale": "STAT_CD 로 시작하는 인덱스가 없습니다." } ],
          "evidence": []
        }
        """;

    private static readonly AiContext Context = new("DEV-ERP", ConnectionEnvironment.Development, AiAccessLevel.Query);

    private sealed class FakeTabs : IEditorTabs
    {
        public Dictionary<Guid, string> Texts { get; } = [];

        public Guid? Active { get; set; }

        public List<Guid> Opened { get; } = [];

        public Guid? ActiveTabId => Active;

        public string? GetText(Guid tabId) => Texts.GetValueOrDefault(tabId);

        public SqlStatement? GetStatementAt(Guid tabId, int offset) =>
            Texts.TryGetValue(tabId, out var text) ? Oranda.Language.Lexing.StatementSplitter.GetStatementAt(text, offset, Oranda.Core.Language.SplitOptions.Default) : null;

        public bool Activate(Guid tabId)
        {
            if (!Texts.ContainsKey(tabId))
            {
                return false;
            }

            Active = tabId;
            return true;
        }

        public Guid OpenText(string title, string text)
        {
            var id = Guid.NewGuid();
            Texts[id] = text;
            Opened.Add(id);
            Active = id;
            return id;
        }

        public bool ReplaceText(Guid tabId, string text)
        {
            if (!Texts.ContainsKey(tabId))
            {
                return false;
            }

            Texts[tabId] = text;
            return true;
        }
    }

    /// <summary>실제 편집기처럼 활성 탭만 보인다. 문장은 시작 탭이 활성일 때만 커서 위치에 있다.</summary>
    private sealed class TabEditor(FakeTabs tabs, Guid startTab) : IEditorContext
    {
        public List<(Guid? Tab, TextEdit Edit)> Proposed { get; } = [];

        public EditorSnapshot? GetActiveDocument() =>
            tabs.Active is { } id ? new EditorSnapshot(id, tabs.Texts[id], new TextRange(0, 0), 0) : null;

        public SqlStatement? GetCurrentStatement() =>
            tabs.Active == startTab ? tabs.GetStatementAt(startTab, Header.Length) : null;

        public Task<bool> ProposeEditAsync(TextEdit edit, CancellationToken ct)
        {
            Proposed.Add((tabs.Active, edit));
            return Task.FromResult(true);
        }
    }

    private sealed class FakeDriver(JsonElement result) : IAiDriver
    {
        public AiDriverCapabilities Capabilities { get; } = new(false, true, true, false, true);

        public async IAsyncEnumerable<AiEvent> RunAsync(AiRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            yield return new AiFinal(null, result, "sess");
            await Task.Yield();
        }
    }

    private static (AiTuningFlow Flow, FakeTabs Tabs, TabEditor Editor, AiChatViewModel Chat, Guid StartTab) Create(string resultJson = ResultJson)
    {
        var tabs = new FakeTabs();
        var startTab = Guid.NewGuid();
        tabs.Texts[startTab] = Header + Original + "\n";
        tabs.Active = startTab;
        var editor = new TabEditor(tabs, startTab);
        var chat = new AiChatViewModel();
        var driver = new FakeDriver(JsonDocument.Parse(resultJson).RootElement.Clone());
        var flow = new AiTuningFlow(editor, driver, chat, (_, _) => throw new InvalidOperationException("tabs 가 있으면 쓰지 않는다"), tabs: tabs);
        return (flow, tabs, editor, chat, startTab);
    }

    private static readonly TuningStartChoice Choice = new(true, true, "default");

    [Fact]
    public async Task A_rewrite_is_proposed_in_the_start_tab_even_when_the_index_tab_became_active()
    {
        var (flow, tabs, editor, _, startTab) = Create();
        await flow.StartAsync(Choice, Context);
        Assert.Equal(startTab, Assert.Single(editor.Proposed).Tab);
        var indexTab = Assert.Single(tabs.Opened);
        Assert.Equal(indexTab, tabs.Active); // 첫 튜닝이 인덱스 후보 탭을 열어 활성 탭이 됐다

        await flow.FollowUpAsync("다시 재작성만 보여 줘");

        Assert.Equal(2, editor.Proposed.Count); // 활성 탭이 인덱스 후보 탭이어도 제안이 버려지지 않는다
        Assert.Equal(startTab, editor.Proposed[1].Tab); // 비교 창은 시작 탭에 적용된다
        Assert.Equal(editor.Proposed[0].Edit.Range, editor.Proposed[1].Edit.Range);
    }

    [Fact]
    public async Task A_follow_up_reuses_the_single_index_tab_and_does_nothing_when_the_text_is_the_same()
    {
        var (flow, tabs, _, chat, _) = Create();
        await flow.StartAsync(Choice, Context);
        await flow.FollowUpAsync("한 번 더");
        await flow.FollowUpAsync("또 한 번");

        Assert.Single(tabs.Opened);
        Assert.Contains(chat.Entries.OfType<ChatAssistantEntry>(), e => e.Text.Contains("그대로 두었습니다", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_index_tab_text_is_replaced_when_the_suggestion_changes_and_reopened_when_the_user_closed_it()
    {
        var (flow, tabs, _, _, _) = Create();
        await flow.StartAsync(Choice, Context);
        var indexTab = Assert.Single(tabs.Opened);
        tabs.Texts[indexTab] = "사용자가 고친 글"; // 열려 있고 내용이 다르면 그 탭의 내용을 바꾼다

        await flow.FollowUpAsync("다시");

        Assert.Single(tabs.Opened);
        Assert.Contains("CREATE INDEX ix_order_stat", tabs.Texts[indexTab], StringComparison.Ordinal);

        tabs.Texts.Remove(indexTab); // 사용자가 탭을 닫았다
        await flow.FollowUpAsync("또");

        Assert.Equal(2, tabs.Opened.Count);
        Assert.Contains("CREATE INDEX", tabs.Texts[tabs.Opened[1]], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_new_conversation_opens_its_own_index_tab()
    {
        var (flow, tabs, _, _, startTab) = Create();
        await flow.StartAsync(Choice, Context);
        tabs.Activate(startTab);

        await flow.StartAsync(Choice, Context);

        Assert.Equal(2, tabs.Opened.Count);
    }

    [Fact]
    public async Task A_closed_start_tab_or_a_changed_statement_in_it_discards_the_proposal()
    {
        var (flow, tabs, editor, chat, startTab) = Create();
        await flow.StartAsync(Choice, Context);
        Assert.Single(editor.Proposed);

        tabs.Texts[startTab] = Header + Original + " AND 1 = 1\n"; // 문장 뒤에 글을 이어 침(옛 범위의 앞부분은 그대로)
        await flow.FollowUpAsync("이어 친 뒤");
        Assert.Single(editor.Proposed);
        Assert.Contains(chat.Entries.OfType<ChatAssistantEntry>(), e => e.Text.Contains("문장이 바뀌어", StringComparison.Ordinal));

        tabs.Texts[startTab] = Header + "SELECT 1 FROM dual\n"; // 시작 탭의 그 자리 글이 실제로 바뀜
        await flow.FollowUpAsync("다시");
        Assert.Single(editor.Proposed);
        Assert.Contains(chat.Entries.OfType<ChatAssistantEntry>(), e => e.Text.Contains("문장이 바뀌어", StringComparison.Ordinal));

        tabs.Texts.Remove(startTab); // 시작 탭이 닫힘
        await flow.FollowUpAsync("또");
        Assert.Single(editor.Proposed);
        Assert.Contains(chat.Entries.OfType<ChatAssistantEntry>(), e => e.Text.Contains("탭이 닫혀", StringComparison.Ordinal));
    }
}
