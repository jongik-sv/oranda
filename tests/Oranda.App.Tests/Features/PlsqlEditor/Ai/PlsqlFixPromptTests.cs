using System.Globalization;
using Oranda.App.Features.PlsqlEditor.Ai;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Contracts;
using Oranda.App.Features.Ai.Kit;
using Oranda.App.Tests.Features.Ai.Kit;
using Oranda.Core.Connection;
using Oranda.Core.Metadata;
using Oranda.Language.Ai.Literals;

namespace Oranda.App.Tests.Features.PlsqlEditor.Ai;

/// <summary>PL/SQL 오류 고치기의 보낼 글·오류 주변 자르기·고친 소스 끼워 넣기(설계 §3.3).</summary>
public class PlsqlFixPromptTests
{
    private static readonly SqlLiteralMasker Masker = new();

    private const string SmallSource = "CREATE OR REPLACE PROCEDURE P_DEMO IS\n  v_name VARCHAR2(10) := 'abc';\nBEGIN\n  v_nam := 'x';\nEND;";

    private static PlsqlFixInput Small() =>
        new("SAMPLE.P_DEMO", "PROCEDURE", SmallSource, [new CompileError(4, 3, "PLS-00201: identifier 'V_NAM' must be declared", false) { MessageNumber = 201 }]);

    private static AiOneShotRun Run(bool mask = false, AiAccessLevel level = AiAccessLevel.Metadata) =>
        KitFakes.Run(new FakeDriver(), KitFakes.Profile(mask ? ConnectionEnvironment.Production : ConnectionEnvironment.Development, level));

    private static AiStripRequest Ready(PlsqlFixInput input, bool mask = false)
    {
        var prepared = PlsqlFixPrompt.Prepare(input, Run(mask), Masker);
        Assert.Null(prepared.RefusalText);
        return prepared.Request!;
    }

    private static string BigSource(int lines) =>
        string.Join("\n", Enumerable.Range(1, lines).Select(i => string.Create(CultureInfo.InvariantCulture, $"  line_{i:000} := {i};")));

    [Fact]
    public void A_small_source_is_sent_whole_with_the_error_line_quoted()
    {
        var request = Ready(Small());

        Assert.Equal(AiTier.Light, PlsqlFixPrompt.Tier);
        Assert.Contains("<context name=\"source_part 1-5\">", request.Prompt, StringComparison.Ordinal);
        Assert.Contains("PLS-00201: identifier 'V_NAM' must be declared", request.Prompt, StringComparison.Ordinal);
        Assert.Contains("v_nam := 'x';", request.Prompt, StringComparison.Ordinal);
        Assert.Contains("line 4", request.Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void A_big_source_sends_only_the_lines_around_each_error_and_merges_close_windows()
    {
        var errors = new[] { new CompileError(100, 1, "PLS-00103: e1", false), new CompileError(130, 1, "PLS-00103: e2", false), new CompileError(400, 1, "PLS-00103: e3", false) };
        var input = new PlsqlFixInput("SAMPLE.PKG", "PACKAGE BODY", BigSource(500), errors);

        var parts = PlsqlFixPrompt.Parts(input.Source, input.Errors);

        Assert.Equal([(60, 170), (360, 440)], parts.Select(p => (p.Start, p.End)));
        var request = Ready(input);
        Assert.Contains("line_060", request.Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("line_059", request.Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("line_200 ", request.Prompt, StringComparison.Ordinal);
        Assert.Equal(PlsqlFixPrompt.ContextLines, 40);
    }

    [Fact]
    public void Windows_are_clipped_at_both_ends()
    {
        var parts = PlsqlFixPrompt.Parts(BigSource(300), [new CompileError(3, 1, "e", false), new CompileError(299, 1, "e", false)]);

        Assert.Equal([(1, 43), (259, 300)], parts.Select(p => (p.Start, p.End)));
    }

    [Fact]
    public void A_package_body_carries_the_whole_spec_as_reference()
    {
        var input = Small() with { ObjectType = "PACKAGE BODY", SpecSource = "CREATE OR REPLACE PACKAGE PKG IS PROCEDURE P; END;" };

        var request = Ready(input);

        Assert.Contains("<context name=\"package_spec\">", request.Prompt, StringComparison.Ordinal);
        Assert.Contains("PROCEDURE P; END;", request.Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Masked_sends_codes_and_masked_source_but_no_server_message()
    {
        var request = Ready(Small(), mask: true);

        Assert.True(request.Bundle.Masked);
        Assert.DoesNotContain("'abc'", request.Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("must be declared", request.Prompt, StringComparison.Ordinal);
        Assert.Contains("PLS-00201", request.Prompt, StringComparison.Ordinal);
        Assert.Contains(PlsqlFixPrompt.MessageWithheldNote, PlsqlFixPrompt.Read("x", Small(), request, Masker).Notes);
    }

    [Fact]
    public void Masked_source_that_cannot_be_masked_is_refused()
    {
        var input = Small() with { Source = "BEGIN\n  x := 'open;\nEND;" };

        var prepared = PlsqlFixPrompt.Prepare(input, Run(mask: true), Masker);

        Assert.Equal(PlsqlFixPrompt.MaskFailedText, prepared.RefusalText);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void No_errors_or_ai_off_is_refused(bool off)
    {
        var input = off ? Small() : Small() with { Errors = [new CompileError(1, 1, "PLW-06002: warning", true)] };

        var prepared = PlsqlFixPrompt.Prepare(input, Run(level: off ? AiAccessLevel.Off : AiAccessLevel.Metadata), Masker);

        Assert.Equal(off ? PlsqlFixPrompt.AiOffText : PlsqlFixPrompt.NoErrorText, prepared.RefusalText);
    }

    [Fact]
    public void The_answer_puts_the_fixed_part_back_into_the_whole_source_with_the_reason_line()
    {
        var input = Small();
        var request = Ready(input);

        var answer = PlsqlFixPrompt.Read("""
            ## 까닭
            변수 이름 오타(v_nam → v_name)를 고쳤습니다.
            ## 고친 소스
            <fixed_part start="1" end="5">
            CREATE OR REPLACE PROCEDURE P_DEMO IS
              v_name VARCHAR2(10) := 'abc';
            BEGIN
              v_name := 'x';
            END;
            </fixed_part>
            """, input, request, Masker);

        Assert.True(answer.Answer is not null);
        Assert.Equal("변수 이름 오타(v_nam → v_name)를 고쳤습니다.", answer.Reason);
        Assert.Equal(SmallSource.Replace("v_nam :=", "v_name :=", StringComparison.Ordinal), answer.EditorText);
    }

    [Fact]
    public void Fixed_parts_of_a_big_source_are_spliced_and_keep_crlf()
    {
        var source = BigSource(500).Replace("\n", "\r\n", StringComparison.Ordinal);
        var input = new PlsqlFixInput("SAMPLE.PKG", "PACKAGE BODY", source, [new CompileError(100, 1, "e", false)]);
        var request = Ready(input);
        var replacement = string.Join("\n", Enumerable.Range(60, 81).Select(i => i == 100 ? "  fixed := 1;" : string.Create(CultureInfo.InvariantCulture, $"  line_{i:000} := {i};")));

        var answer = PlsqlFixPrompt.Read("## 까닭\n고침\n## 고친 소스\n<fixed_part start=\"60\" end=\"140\">\n" + replacement + "\n</fixed_part>", input, request, Masker);

        var lines = answer.EditorText!.Split("\r\n");
        Assert.Equal(500, lines.Length);
        Assert.Equal("  fixed := 1;", lines[99]);
        Assert.Equal("  line_141 := 141;", lines[140]);
    }

    [Fact]
    public void A_fixed_part_with_a_range_that_was_not_sent_is_not_applied()
    {
        var input = Small();
        var request = Ready(input);

        var answer = PlsqlFixPrompt.Read("## 까닭\n고침\n## 고친 소스\n<fixed_part start=\"2\" end=\"4\">\nx\n</fixed_part>", input, request, Masker);

        Assert.Null(answer.EditorText);
        Assert.Equal(PlsqlFixPrompt.RangeMismatchText, answer.EditorNote);
        Assert.Equal("고침", answer.Reason);
    }

    [Fact]
    public void Masked_fixed_source_gets_its_literals_back()
    {
        var input = Small();
        var request = Ready(input, mask: true);
        var maskedPart = request.Prompt.Split("<context name=\"source_part 1-5\">\n")[1].Split("\n</context>")[0];

        var answer = PlsqlFixPrompt.Read("## 까닭\n고침\n## 고친 소스\n<fixed_part start=\"1\" end=\"5\">\n" + maskedPart.Replace("v_nam :=", "v_name :=", StringComparison.Ordinal) + "\n</fixed_part>", input, request, Masker);

        Assert.Equal(SmallSource.Replace("v_nam :=", "v_name :=", StringComparison.Ordinal), answer.EditorText);
    }

    [Fact]
    public void An_unknown_placeholder_keeps_the_fix_out_of_the_editor()
    {
        var input = Small();
        var request = Ready(input, mask: true);
        var prefix = request.Bundle.Vault!.Placeholder(9);

        var answer = PlsqlFixPrompt.Read("## 까닭\n고침\n## 고친 소스\n<fixed_part start=\"1\" end=\"5\">\nBEGIN x := " + prefix + "; END;\n</fixed_part>", input, request, Masker);

        Assert.Null(answer.EditorText);
        Assert.Equal(PlsqlFixPrompt.RestoreFailedText, answer.EditorNote);
    }

    [Fact]
    public void An_answer_without_a_fixed_part_has_no_editor_text()
    {
        var input = Small();
        var request = Ready(input);

        var answer = PlsqlFixPrompt.Read("모르겠습니다.", input, request, Masker);

        Assert.False(answer.Answer is not null);
        Assert.Null(answer.EditorText);
        Assert.Equal(PlsqlFixPrompt.NoFixText, answer.EditorNote);
    }
}
