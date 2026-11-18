using Oranda.App.Features.Ai.Kit;
using Oranda.App.Features.Rollback.Ai;
using Oranda.App.Tests.Features.Ai.Kit;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Connection;
using Oranda.Language.Ai.Literals;

namespace Oranda.App.Tests.Features.Rollback.Ai;

/// <summary>원복 스크립트 「AI 점검」 의 보낼 글·값 규칙·고친 스크립트 읽기(설계 §4.2).</summary>
public class RollbackCheckPromptTests
{
    private static readonly SqlLiteralMasker Masker = new();

    private const string Script = "INSERT INTO SAMPLE.ORDER_ITEMS (ORDER_ID, ITEM_NO) VALUES (777, 1);\nINSERT INTO SAMPLE.ORDERS (ORDER_ID, CUST_NM) VALUES (777, 'HONG');";

    private static RollbackCheckInput Input() => new(
        [new RollbackCheckChange(1, "DELETE", "SAMPLE.ORDER_ITEMS", 2) { StatementText = "DELETE FROM SAMPLE.ORDER_ITEMS WHERE ORDER_ID = 777" },
         new RollbackCheckChange(2, "DELETE", "SAMPLE.ORDERS", 1) { StatementText = "DELETE FROM SAMPLE.ORDERS WHERE ORDER_ID = 777" }],
        Script)
    {
        ForeignKeys = [new RollbackForeignKey("SAMPLE.ORDER_ITEMS", "SAMPLE.ORDERS", "FK_ITEMS_ORDER")],
        Tables = [new RollbackTableFacts("SAMPLE.ORDERS") { Triggers = ["TRG_ORDERS_AUDIT"], Sequences = ["SEQ_ORDERS"] }],
    };

    private static AiOneShotRun Run(AiAccessLevel level, bool mask = false) =>
        KitFakes.Run(new FakeDriver(), KitFakes.Profile(mask ? ConnectionEnvironment.Production : ConnectionEnvironment.Development, level));

    private static AiStripRequest Ready(AiAccessLevel level, bool mask = false)
    {
        var prepared = RollbackCheckPrompt.Prepare(Input(), Run(level, mask), Masker);
        Assert.Null(prepared.RefusalText);
        return prepared.Request!;
    }

    [Fact]
    public void It_is_a_main_tier_check_with_changes_script_foreign_keys_and_triggers()
    {
        var request = Ready(AiAccessLevel.QueryRows);

        Assert.Equal(AiTier.Main, RollbackCheckPrompt.Tier);
        Assert.Contains("FK_ITEMS_ORDER", request.Prompt, StringComparison.Ordinal);
        Assert.Contains("TRG_ORDERS_AUDIT", request.Prompt, StringComparison.Ordinal);
        Assert.Contains("SEQ_ORDERS", request.Prompt, StringComparison.Ordinal);
        Assert.Contains("'HONG'", request.Prompt, StringComparison.Ordinal);
        Assert.Contains("## 고친 스크립트", request.Prompt, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(AiAccessLevel.Metadata, false)]
    [InlineData(AiAccessLevel.Query, false)]
    [InlineData(AiAccessLevel.QueryRows, true)]
    public void Without_row_values_the_script_goes_with_every_literal_masked(AiAccessLevel level, bool mask)
    {
        var request = Ready(level, mask);

        Assert.DoesNotContain("'HONG'", request.Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("777", request.Prompt, StringComparison.Ordinal);
        Assert.Contains("INSERT INTO SAMPLE.ORDERS (ORDER_ID, CUST_NM) VALUES", request.Prompt, StringComparison.Ordinal);
        Assert.Contains(RollbackCheckPrompt.NoValuesNote, RollbackCheckPrompt.Read("x", Input(), request, Masker).Notes);
    }

    [Fact]
    public void A_script_that_cannot_be_masked_is_refused_without_row_values()
    {
        var prepared = RollbackCheckPrompt.Prepare(Input() with { Script = "INSERT INTO T VALUES ('open);" }, Run(AiAccessLevel.Plan), Masker);

        Assert.Equal(RollbackCheckPrompt.MaskFailedText, prepared.RefusalText);
    }

    [Fact]
    public void Ai_off_or_an_empty_script_is_refused()
    {
        Assert.Equal(RollbackCheckPrompt.AiOffText, RollbackCheckPrompt.Prepare(Input(), Run(AiAccessLevel.Off), Masker).RefusalText);
        Assert.Equal(RollbackCheckPrompt.NothingText, RollbackCheckPrompt.Prepare(Input() with { Script = " " }, Run(AiAccessLevel.Plan), Masker).RefusalText);
    }

    [Fact]
    public void The_fixed_script_is_a_proposal_with_its_masked_values_put_back()
    {
        var request = Ready(AiAccessLevel.Plan);
        var maskedScript = request.Prompt.Split("<context name=\"rollback_script\">\n")[1].Split("\n</context>")[0];
        var lines = maskedScript.Split('\n');
        var reordered = lines[1] + "\n" + lines[0];

        var result = RollbackCheckPrompt.Read(
            "## 점검 결과\n- [FK 순서] 부모 행을 먼저 넣어야 합니다.\n## 결론\n순서를 바꿔야 합니다.\n## 고친 스크립트\n```sql\n" + reordered + "\n```",
            Input(), request, Masker);

        Assert.NotNull(result.Answer);
        Assert.Equal("순서를 바꿔야 합니다.", result.Conclusion);
        var expected = Script.Split('\n');
        Assert.Equal(expected[1] + "\n" + expected[0], result.EditorText);
        Assert.Contains(RollbackCheckPrompt.ProposalNote, result.Notes);
    }

    [Fact]
    public void No_fixed_script_or_an_unknown_placeholder_leaves_the_editor_empty()
    {
        var request = Ready(AiAccessLevel.Plan);

        var none = RollbackCheckPrompt.Read("## 점검 결과\n- 문제 없음\n## 결론\n그대로 써도 됩니다.\n## 고친 스크립트\n없음", Input(), request, Masker);
        Assert.Null(none.EditorText);
        Assert.Null(none.EditorNote);

        var unknown = RollbackCheckPrompt.Read("## 점검 결과\n- x\n## 결론\ny\n## 고친 스크립트\n```sql\nINSERT INTO T VALUES (:v99);\n```", Input(), request, Masker);
        Assert.Null(unknown.EditorText);
        Assert.Equal(RollbackCheckPrompt.RestoreFailedText, unknown.EditorNote);
    }

    [Fact]
    public void An_unmasked_script_with_row_values_comes_back_as_written()
    {
        var request = Ready(AiAccessLevel.QueryRows);

        var result = RollbackCheckPrompt.Read("## 점검 결과\n- x\n## 결론\ny\n## 고친 스크립트\n```sql\nSELECT 1 FROM DUAL;\n```", Input(), request, Masker);

        Assert.Equal("SELECT 1 FROM DUAL;", result.EditorText);
    }
}
