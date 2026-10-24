using Oranda.Language.Tuning;

namespace Oranda.Language.Tests.Tuning;

/// <summary>TSK-06-05: 계획 조건 글을 항으로 나누기.</summary>
public class PlanPredicatesTests
{
    [Fact]
    public void Splits_top_level_and_into_equality_and_range_terms()
    {
        var terms = PlanPredicates.Parse("(\"LH\".\"EQP_ID\"=:B1 AND \"LH\".\"EVT_TIME\">=:B2)");
        Assert.Collection(terms,
            t => Assert.Equal(("LH", "EQP_ID", PredicateKind.Equality, "="), (t.Alias, t.Column, t.Kind, t.Operator)),
            t => Assert.Equal(("LH", "EVT_TIME", PredicateKind.Range, ">="), (t.Alias, t.Column, t.Kind, t.Operator)));
    }

    [Fact]
    public void Join_condition_between_two_aliases_is_a_join_term()
    {
        var t = Assert.Single(PlanPredicates.Parse("\"LM\".\"LOT_ID\"=\"LH\".\"LOT_ID\""));
        Assert.Equal(PredicateKind.Join, t.Kind);
        Assert.Equal(("LM", "LOT_ID", "LH", "LOT_ID"), (t.Alias, t.Column, t.OtherAlias, t.OtherColumn));
    }

    [Fact]
    public void Value_on_the_left_is_swapped_and_the_operator_flipped()
    {
        var t = Assert.Single(PlanPredicates.Parse(":B1<\"E\".\"SAL\""));
        Assert.Equal(("E", "SAL", PredicateKind.Range, ">"), (t.Alias, t.Column, t.Kind, t.Operator));
    }

    [Fact]
    public void Same_column_equalities_joined_by_or_become_an_in_list()
    {
        var t = Assert.Single(PlanPredicates.Parse("(\"A\".\"X\"=1 OR \"A\".\"X\"=2 OR \"A\".\"X\"=3)"));
        Assert.Equal(PredicateKind.InList, t.Kind);
        Assert.Equal("X", t.Column);
    }

    [Fact]
    public void Or_over_different_columns_is_not_usable_for_an_index()
    {
        var terms = PlanPredicates.Parse("(\"A\".\"X\"=1 OR \"A\".\"Y\"=2)");
        Assert.All(terms, t => Assert.Equal(PredicateKind.Other, t.Kind));
    }

    [Fact]
    public void Function_wrapping_reports_the_innermost_function()
    {
        var t = Assert.Single(PlanPredicates.Parse("TRUNC(INTERNAL_FUNCTION(\"LH\".\"EVT_TIME\"))=TRUNC(SYSDATE@!)"));
        Assert.Equal(PredicateKind.Function, t.Kind);
        Assert.Equal(("LH", "EVT_TIME", "INTERNAL_FUNCTION"), (t.Alias, t.Column, t.Function));
    }

    [Fact]
    public void Bare_internal_function_term_without_comparison_is_kept()
    {
        var t = Assert.Single(PlanPredicates.Parse("INTERNAL_FUNCTION(\"LOT_ID\")"));
        Assert.Equal((null, "LOT_ID", PredicateKind.Function), (t.Alias, t.Column, t.Kind));
    }

    [Fact]
    public void Like_with_a_leading_wildcard_and_negations_are_other()
    {
        var terms = PlanPredicates.Parse("(\"E\".\"ENAME\" LIKE '%S' AND \"E\".\"JOB\" LIKE 'MAN%' AND \"E\".\"DEPTNO\"<>10 AND \"E\".\"MGR\" IS NULL AND \"E\".\"X\" NOT IN (1,2))");
        Assert.Equal([PredicateKind.Other, PredicateKind.Like, PredicateKind.Other, PredicateKind.Other, PredicateKind.Other], terms.Select(t => t.Kind));
    }

    [Fact]
    public void Between_is_a_single_range_term_and_its_and_is_not_split()
    {
        var t = Assert.Single(PlanPredicates.Parse("\"E\".\"SAL\" BETWEEN 1 AND 10"));
        Assert.Equal(PredicateKind.Range, t.Kind);
    }

    [Fact]
    public void Pseudo_columns_are_values_not_columns()
    {
        var t = Assert.Single(PlanPredicates.Parse("\"O\".\"CREATED\">SYSDATE-1"));
        Assert.Equal(("CREATED", PredicateKind.Range), (t.Column, t.Kind));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("((\"A\".\"X\"=1")]
    [InlineData(":B1=:B2")]
    public void Empty_unbalanced_or_column_free_text_gives_no_terms(string? text) => Assert.Empty(PlanPredicates.Parse(text));

    [Fact]
    public void Aliases_come_from_the_plan_alias_section_quoted_or_not()
    {
        var map = PlanPredicates.AliasesFromPlanText(TuningFixtures.AliasSection);
        Assert.Equal("LM", map[2]);
        Assert.Equal("LH", map[3]);
        Assert.False(map.ContainsKey(1));
        Assert.Empty(PlanPredicates.AliasesFromPlanText(null));
        Assert.Empty(PlanPredicates.AliasesFromPlanText("no alias section here"));
    }
}
