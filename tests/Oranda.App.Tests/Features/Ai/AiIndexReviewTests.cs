using System.Runtime.CompilerServices;
using System.Text.Json;
using Oranda.App.Features.Ai.Chat;
using Oranda.App.Features.Ai.Tuning;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Ai.Schemas;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Extensibility;
using Oranda.Core.Text;
using Oranda.Core.Tuning;

namespace Oranda.App.Tests.Features.Ai;

/// <summary>AI 인덱스 후보를 받은 뒤 그 표의 실제 기존 인덱스와 대조(결정론)하고, 탭 글에서 변형(modify)을 먼저 보인다.</summary>
public class AiIndexReviewTests
{
    private const string CoilTable = "APSUSER.TB_APS_COIL_INST";
    private const string MoTable = "APSUSER.TB_APS_MO_INST";

    private sealed class FixedEditor(string text) : IEditorContext
    {
        public EditorSnapshot? GetActiveDocument() => null;

        public SqlStatement? GetCurrentStatement() => new(text, new TextRange(0, text.Length), StatementKind.Query);

        public Task<bool> ProposeEditAsync(TextEdit edit, CancellationToken ct) => Task.FromResult(false);
    }

    private sealed class ScriptedDriver(params AiEvent[] events) : IAiDriver
    {
        public AiDriverCapabilities Capabilities { get; } = new(false, true, true, false, true);

        public async IAsyncEnumerable<AiEvent> RunAsync(AiRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            foreach (var e in events)
            {
                yield return e;
                await Task.Yield();
            }
        }
    }

    private static TuningIndexSuggestion Create(string table, params string[] columns) =>
        new(table, columns, $"CREATE INDEX IX_NEW ON {table} ({string.Join(", ", columns)});", "r");

    private static IReadOnlyList<IndexColumnsRow> CoilIndexes() => [new("IX_APS_COIL_INST_T1", ["MTL_MO_NO"])];

    [Fact]
    public void Create_that_only_adds_a_column_to_an_existing_index_is_marked_as_widening_it()
    {
        var (dropped, notes) = AiIndexReview.Review(Create(CoilTable, "MTL_MO_NO", "MO_NO"), CoilIndexes());

        Assert.Null(dropped);
        var note = Assert.Single(notes);
        Assert.Contains("IX_APS_COIL_INST_T1 (MTL_MO_NO) 를 넓히는 안과 같음", note, StringComparison.Ordinal);
    }

    [Fact]
    public void Six_column_index_gets_a_warning_line()
    {
        var six = Create(MoTable, "MO_NO", "FAC_CD", "PROC_SEQ", "PROC_CD", "WK_STR_SCH_DH", "WK_END_SCH_DH");

        var (dropped, notes) = AiIndexReview.Review(six, [new("IX_APS_MO_INST_N1", ["PROC_CD"])]);

        Assert.Null(dropped);
        Assert.Contains(notes, n => n.Contains("열이 6개", StringComparison.Ordinal));
    }

    [Fact]
    public void Four_columns_get_no_warning()
    {
        var (_, notes) = AiIndexReview.Review(Create(MoTable, "A", "B", "C", "D"), []);

        Assert.Empty(notes);
    }

    [Fact]
    public void Candidate_already_covered_by_an_existing_index_is_dropped_with_one_line()
    {
        var (dropped, notes) = AiIndexReview.Review(Create(CoilTable, "MTL_MO_NO"), [new("IX_APS_COIL_INST_T1", ["MTL_MO_NO", "MO_NO"])]);

        Assert.NotNull(dropped);
        Assert.StartsWith("뺀 인덱스 후보:", dropped, StringComparison.Ordinal);
        Assert.Contains("IX_APS_COIL_INST_T1 (MTL_MO_NO, MO_NO) 가 이미 덮음", dropped, StringComparison.Ordinal);
        Assert.Empty(notes);
    }

    [Fact]
    public void Modify_is_not_compared_with_the_index_it_changes_but_with_the_others()
    {
        var modify = new TuningIndexSuggestion(CoilTable, ["MTL_MO_NO", "MO_NO"], "DROP INDEX ...; CREATE INDEX ...;", "r",
            Action: TuningIndexSuggestion.ActionModify, ExistingIndex: "APSUSER.IX_APS_COIL_INST_T1", ExistingColumns: ["MTL_MO_NO"]);

        var (dropped, notes) = AiIndexReview.Review(modify, CoilIndexes());
        Assert.Null(dropped);
        Assert.Empty(notes); // 바꿀 인덱스 자신은 견주지 않는다

        var (droppedByOther, _) = AiIndexReview.Review(modify, [.. CoilIndexes(), new("IX_OTHER", ["MTL_MO_NO", "MO_NO", "X"])]);
        Assert.Contains("IX_OTHER", droppedByOther, StringComparison.Ordinal);
    }

    [Fact]
    public void Modify_naming_an_index_the_table_does_not_have_is_flagged()
    {
        var modify = new TuningIndexSuggestion(CoilTable, ["MTL_MO_NO", "MO_NO"], "ddl", "r",
            Action: TuningIndexSuggestion.ActionModify, ExistingIndex: "APSUSER.IX_NOPE", ExistingColumns: ["MTL_MO_NO"]);

        var (dropped, notes) = AiIndexReview.Review(modify, CoilIndexes());

        Assert.Null(dropped);
        Assert.Contains(notes, n => n.Contains("IX_NOPE", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Unreadable_indexes_skip_the_comparison_and_say_so()
    {
        var suggestions = new[] { Create(CoilTable, "MTL_MO_NO", "MO_NO") };

        foreach (Func<string, string, CancellationToken, Task<IReadOnlyList<IndexColumnsRow>?>>? read in new Func<string, string, CancellationToken, Task<IReadOnlyList<IndexColumnsRow>?>>?[]
        {
            null,
            (_, _, _) => Task.FromResult<IReadOnlyList<IndexColumnsRow>?>(null),
            (_, _, _) => throw new NotSupportedException(),
        })
        {
            var result = await AiIndexReview.ReviewAsync(suggestions, read, CancellationToken.None);

            Assert.Empty(result.Dropped);
            var kept = Assert.Single(result.Kept);
            Assert.Contains(kept.Notes, n => n.Contains("확인하지 못했습니다", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task Owner_and_table_are_split_and_each_table_is_read_once()
    {
        var calls = new List<(string Owner, string Table)>();
        var suggestions = new[] { Create(CoilTable, "MTL_MO_NO", "MO_NO"), Create(CoilTable.ToLowerInvariant(), "MO_NO"), Create("tb_plain", "A") };

        await AiIndexReview.ReviewAsync(suggestions, (o, t, _) =>
        {
            calls.Add((o, t));
            return Task.FromResult<IReadOnlyList<IndexColumnsRow>?>([]);
        }, CancellationToken.None);

        Assert.Equal([("APSUSER", "TB_APS_COIL_INST"), ("", "TB_PLAIN")], calls);
    }

    [Fact]
    public void Index_text_lists_modify_before_create_with_the_change_header_and_dropped_lines()
    {
        var modify = new TuningIndexSuggestion(CoilTable, ["MTL_MO_NO", "MO_NO"],
            "DROP INDEX APSUSER.IX_APS_COIL_INST_T1;\nCREATE INDEX APSUSER.IX_APS_COIL_INST_T1 ON APSUSER.TB_APS_COIL_INST (MTL_MO_NO, MO_NO);", "MO_NO 조건을 더합니다.",
            Risk: "T1 을 쓰는 다른 SQL 에도 영향", Action: TuningIndexSuggestion.ActionModify, ExistingIndex: "APSUSER.IX_APS_COIL_INST_T1", ExistingColumns: ["MTL_MO_NO"]);
        var created = Create(MoTable, "MO_NO", "FAC_CD");

        var text = AiTuningFlow.BuildIndexText(
            [new ReviewedIndex(created, ["열이 곧 늘어납니다"]), new ReviewedIndex(modify, [])],
            ["뺀 인덱스 후보: X — 기존 IX 가 이미 덮음"],
            masked: false);

        Assert.Contains("-- 기존 APSUSER.IX_APS_COIL_INST_T1 (MTL_MO_NO) → (MTL_MO_NO, MO_NO) 로 변형", text, StringComparison.Ordinal);
        Assert.Contains("-- 뺀 인덱스 후보: X — 기존 IX 가 이미 덮음", text, StringComparison.Ordinal);
        Assert.Contains("-- 위험: T1 을 쓰는 다른 SQL 에도 영향", text, StringComparison.Ordinal);
        Assert.Contains("-- 확인: 열이 곧 늘어납니다", text, StringComparison.Ordinal);
        Assert.True(text.IndexOf("로 변형", StringComparison.Ordinal) < text.IndexOf("-- 새 인덱스:", StringComparison.Ordinal));
        Assert.True(text.IndexOf("DROP INDEX", StringComparison.Ordinal) < text.IndexOf("CREATE INDEX IX_NEW", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Flow_drops_covered_candidates_from_the_tab_and_tells_the_chat()
    {
        const string json = """
            { "summary": "s", "rewrites": [], "evidence": [], "index_suggestions": [
                { "action": "create", "table": "APSUSER.TB_APS_COIL_INST", "columns": ["MTL_MO_NO"], "ddl": "CREATE INDEX IX_A ON APSUSER.TB_APS_COIL_INST (MTL_MO_NO);", "rationale": "r" },
                { "action": "create", "table": "APSUSER.TB_APS_MO_INST", "columns": ["MO_NO", "FAC_CD"], "ddl": "CREATE INDEX IX_B ON APSUSER.TB_APS_MO_INST (MO_NO, FAC_CD);", "rationale": "r" } ] }
            """;
        var driver = new ScriptedDriver(new AiFinal(null, JsonDocument.Parse(json).RootElement.Clone(), "s"));
        var chat = new AiChatViewModel();
        var tabs = new List<string>();
        var flow = new AiTuningFlow(
            new FixedEditor("SELECT 1 FROM dual"), driver, chat, (_, text, _) => tabs.Add(text),
            readIndexes: (_, table, _) => Task.FromResult<IReadOnlyList<IndexColumnsRow>?>(
                table == "TB_APS_COIL_INST" ? [new("IX_APS_COIL_INST_T1", ["MTL_MO_NO"])] : []));

        // 컬럼이 MTL_MO_NO 한 개인 후보는 T1 (MTL_MO_NO) 이 이미 덮는다.
        await flow.StartAsync(new TuningStartChoice(false, true, "default"), new AiContext("DEV", ConnectionEnvironment.Development, AiAccessLevel.Query));

        var tab = Assert.Single(tabs);
        Assert.DoesNotContain("CREATE INDEX IX_A", tab, StringComparison.Ordinal);
        Assert.Contains("CREATE INDEX IX_B", tab, StringComparison.Ordinal);
        Assert.Contains(chat.Entries.OfType<ChatAssistantEntry>(), e => e.Text.Contains("뺀 인덱스 후보:", StringComparison.Ordinal) && e.Text.Contains("IX_APS_COIL_INST_T1", StringComparison.Ordinal));
    }
}
