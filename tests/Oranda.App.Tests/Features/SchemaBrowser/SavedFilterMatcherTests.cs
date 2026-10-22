using Oranda.App.Features.SchemaBrowser;
using Oranda.Core.Metadata;
using Oranda.Core.Settings;

namespace Oranda.App.Tests.Features.SchemaBrowser;

/// <summary>저장 필터(TSK-13-10)가 스키마·유형·이름을 거르는 규칙. 패턴은 이름 칸과 같이 <c>*</c>·<c>%</c> 와일드카드, 따옴표면 대소문자 그대로.</summary>
public sealed class SavedFilterMatcherTests
{
    [Theory]
    [InlineData("emp", true, false, "TB_EMP_X", true)]
    [InlineData("emp", false, false, "TB_EMP_X", false)]
    [InlineData("emp", false, false, "EMP", true)]
    [InlineData("TB_M30*", true, false, "TB_M30_A", true)]
    [InlineData("TB_M30*", true, false, "XTB_M30_A", false)]
    [InlineData("%LOG", true, false, "ERR_LOG", true)]
    [InlineData("%LOG", true, false, "LOG_ERR", false)]
    [InlineData("TB_A", false, false, "TBXA", false)]
    [InlineData("TB_A", false, true, "TBXA", true)]
    [InlineData("TB_A", false, false, "TB_A", true)]
    [InlineData("\"Emp\"", true, false, "MyEmp", true)]
    [InlineData("\"Emp\"", true, false, "EMP", false)]
    [InlineData("", true, false, "ANY", true)]
    [InlineData("  ", false, false, "ANY", true)]
    public void Name_patterns_follow_the_options(string pattern, bool wrap, bool underscore, string name, bool expected)
    {
        var regex = SavedFilterMatcher.ToRegex(pattern, underscoreIsWildcard: underscore, wrapWithPercent: wrap);

        Assert.Equal(expected, regex is null || regex.IsMatch(name));
    }

    [Fact]
    public void Schema_scope_all_shows_every_schema()
    {
        var m = new SavedFilterMatcher(new SchemaBrowserFilter(), "SCOTT");

        Assert.True(m.ShowsSchema("HR"));
        Assert.True(m.ShowsSchema("SCOTT"));
    }

    [Fact]
    public void Logon_schema_scope_shows_only_the_connected_user()
    {
        var m = new SavedFilterMatcher(new SchemaBrowserFilter { Scope = SchemaFilterScope.LogonSchema }, "scott");

        Assert.True(m.ShowsSchema("SCOTT"));
        Assert.False(m.ShowsSchema("HR"));
    }

    [Fact]
    public void Owning_scope_shows_schemas_that_have_objects()
    {
        var m = new SavedFilterMatcher(new SchemaBrowserFilter { Scope = SchemaFilterScope.SchemasWithObjects }, "SCOTT",
            new HashSet<string>(["HR"], StringComparer.Ordinal));

        Assert.True(m.ShowsSchema("HR"));
        Assert.False(m.ShowsSchema("SCOTT"));
        Assert.True(m.NeedsSchemasWithObjects);
    }

    [Fact]
    public void Selected_scope_shows_the_listed_schemas()
    {
        var m = new SavedFilterMatcher(new SchemaBrowserFilter { Scope = SchemaFilterScope.Selected, Schemas = ["MESAPUSER", "eaiapuser"] }, "SCOTT");

        Assert.True(m.ShowsSchema("MESAPUSER"));
        Assert.True(m.ShowsSchema("EAIAPUSER"));
        Assert.False(m.ShowsSchema("SCOTT"));
        Assert.False(m.NeedsSchemasWithObjects);
    }

    [Fact]
    public void Hidden_types_and_per_type_patterns_filter_objects()
    {
        var m = new SavedFilterMatcher(
            new SchemaBrowserFilter
            {
                HiddenTypes = ["JOB"],
                NamePatterns = new Dictionary<string, string> { ["TABLE"] = "TB_M30*" },
            },
            "SCOTT");

        Assert.False(m.ShowsType("JOB"));
        Assert.True(m.ShowsType("VIEW"));
        Assert.True(m.Shows(new DbObjectRef("S", "TB_M30_A", "TABLE")));
        Assert.False(m.Shows(new DbObjectRef("S", "TB_M10_A", "TABLE")));
        Assert.True(m.Shows(new DbObjectRef("S", "V_ANY", "VIEW")));
        Assert.False(m.Shows(new DbObjectRef("S", "SYNC", "JOB")));
    }
}
