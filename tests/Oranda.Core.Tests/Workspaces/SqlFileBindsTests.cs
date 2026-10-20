using System.Text.Json;
using Oranda.Core.Execution;
using Oranda.Core.Serialization;
using Oranda.Core.Workspaces;

namespace Oranda.Core.Tests.Workspaces;

/// <summary>.sql 끝 바인드 블록(<see cref="SqlFileBinds"/>)과 작업 공간 <c>tabs[].binds</c> 직렬화(EXEC-03, data-model 2.6).</summary>
public class SqlFileBindsTests
{
    private static readonly WorkspaceBind EmpNo = new() { Name = "EMPNO", Type = BindDataType.Number, Value = "1002" };

    [Fact]
    public void Append_writes_marker_then_one_json_line_per_bind()
    {
        var text = SqlFileBinds.Append("select :empno from dual;\n", [EmpNo]);

        Assert.Equal(
            "select :empno from dual;\n\n-- @oranda-binds v1\n"
            + "-- {\"name\":\"EMPNO\",\"type\":\"Number\",\"direction\":\"In\",\"value\":\"1002\",\"isNull\":false}\n",
            text);
    }

    [Fact]
    public void No_binds_leaves_body_untouched()
    {
        Assert.Equal("select 1 from dual", SqlFileBinds.Append("select 1 from dual", []));
        Assert.Equal(("select 1 from dual", (IReadOnlyList<WorkspaceBind>?)null), SqlFileBinds.Split("select 1 from dual"));
    }

    [Theory]
    [InlineData("select :empno from dual;\n")]
    [InlineData("select :empno from dual;")]
    [InlineData("select :empno\r\n  from dual;\r\n")]
    [InlineData("select :empno\r\n  from dual;")]
    [InlineData("select :empno from dual;\n\n\n")]
    [InlineData("")]
    public void Split_returns_body_exactly_including_trailing_newline(string body)
    {
        var (back, binds) = SqlFileBinds.Split(SqlFileBinds.Append(body, [EmpNo]));

        Assert.Equal(body, back);
        Assert.Equal([EmpNo], binds!);
    }

    [Fact]
    public void Block_follows_body_line_endings()
    {
        var text = SqlFileBinds.Append("select :a\r\nfrom dual;", [EmpNo]);

        Assert.StartsWith("select :a\r\nfrom dual;\r\n-- @oranda-binds v1\r\n-- {", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", text.Replace("\r\n", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.EndsWith("}\r\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Append_after_split_is_idempotent()
    {
        var once = SqlFileBinds.Append("select :empno from dual;\r\n", [EmpNo]);
        var (body, binds) = SqlFileBinds.Split(once);
        var twice = SqlFileBinds.Append(body, binds!);

        Assert.Equal(once, twice);
        Assert.Equal(2, twice.Split(SqlFileBinds.Marker).Length); // 블록은 하나
    }

    [Fact]
    public void Tricky_values_round_trip_and_block_stays_ascii_on_one_line_each()
    {
        WorkspaceBind[] binds =
        [
            new() { Name = "A", Value = "line1\nline2\r\nline3" },
            new() { Name = "B", Value = "*/ end of comment /* and -- dash" },
            new() { Name = "C", Value = "it's \"quoted\" \\ back" },
            new() { Name = "D", Value = "&amp & ampersand &1" },
            new() { Name = "E", Type = BindDataType.NVarchar2, Value = "한글 값 😀" },
            new() { Name = "F", Type = BindDataType.Date, Direction = BindDirection.InOut, IsNull = true },
            new() { Name = "G", Type = BindDataType.RefCursor, Direction = BindDirection.Out, IsNull = true },
        ];

        var text = SqlFileBinds.Append("select 1 from dual;\n", binds);
        var block = text[text.IndexOf(SqlFileBinds.Marker, StringComparison.Ordinal)..];

        Assert.All(block, c => Assert.True(c < 128, $"비 ASCII 글자 U+{(int)c:X4}"));
        Assert.DoesNotContain("&", block, StringComparison.Ordinal);
        Assert.Equal(binds.Length + 1, block.TrimEnd('\n').Split('\n').Length);
        Assert.All(block.TrimEnd('\n').Split('\n'), line => Assert.StartsWith("-- ", line, StringComparison.Ordinal));
        Assert.Equal(binds, SqlFileBinds.Split(text).Binds!);
    }

    [Fact]
    public void Reads_upper_case_names_and_missing_fields_leniently()
    {
        var text = "select 1;\n-- @oranda-binds v1\n"
            + "-- {\"name\":\"EMPNO\",\"type\":\"NUMBER\",\"direction\":\"IN OUT\",\"value\":\"7\",\"isNull\":false}\n"
            + "-- {\"name\":\"X\"}\n"
            + "-- {\"name\":\"Y\",\"type\":\"nope\",\"direction\":\"in_out\",\"value\":\"ignored\",\"isNull\":true}";

        var (body, binds) = SqlFileBinds.Split(text);

        Assert.Equal("select 1;", body);
        Assert.Equal(
            [
                new WorkspaceBind { Name = "EMPNO", Type = BindDataType.Number, Direction = BindDirection.InOut, Value = "7" },
                new WorkspaceBind { Name = "X" },
                new WorkspaceBind { Name = "Y", Direction = BindDirection.InOut, IsNull = true },
            ],
            binds!);
    }

    [Theory]
    [InlineData("select 1;\n-- @oranda-binds v1\n-- {\"name\":\"A\"}\nselect 2;\n")] // 블록 뒤에 SQL
    [InlineData("select 1;\n-- @oranda-binds v1\n-- {\"name\":\"A\"}\n\n")] // 블록 뒤 빈 줄
    [InlineData("select 1;\n-- @oranda-binds v1\n-- not json\n")]
    [InlineData("select 1;\n-- @oranda-binds v1\n-- {\"value\":\"no name\"}\n")]
    [InlineData("select 1;\n-- @oranda-binds v1\n--{\"name\":\"A\"}\n")] // `-- ` 가 아니다
    [InlineData("select 1; -- @oranda-binds v1\n-- {\"name\":\"A\"}\n")] // 표지가 줄 처음이 아니다
    [InlineData("select 1;\n-- @oranda-binds v2\n-- {\"name\":\"A\"}\n")]
    public void Text_that_is_not_exactly_a_trailing_block_is_left_alone(string text)
    {
        Assert.Equal((text, (IReadOnlyList<WorkspaceBind>?)null), SqlFileBinds.Split(text));
    }

    [Fact]
    public void Only_the_last_block_is_split_off()
    {
        var pasted = "select :a from dual;\n-- @oranda-binds v1\n-- {\"name\":\"A\",\"value\":\"old\"}\n";
        var text = SqlFileBinds.Append(pasted, [new WorkspaceBind { Name = "A", Value = "new" }]);

        var (body, binds) = SqlFileBinds.Split(text);

        Assert.Equal(pasted, body);
        Assert.Equal("new", Assert.Single(binds!).Value);
    }

    [Fact]
    public void Workspace_tab_binds_round_trip_through_json()
    {
        var ws = new Workspace
        {
            Id = "default",
            Name = "기본",
            Tabs = [new WorkspaceTab { Id = Guid.NewGuid(), Title = "a.sql", Binds = [EmpNo, new() { Name = "D", Type = BindDataType.Date, IsNull = true }] }],
        };

        var json = JsonSerializer.Serialize(ws, OrandaJsonContext.Default.Workspace);
        var back = JsonSerializer.Deserialize(json, OrandaJsonContext.Default.Workspace)!;

        Assert.Contains("\"binds\": [", json, StringComparison.Ordinal);
        Assert.Contains("\"type\": \"Number\"", json, StringComparison.Ordinal);
        Assert.Contains("\"isNull\": true", json, StringComparison.Ordinal);
        Assert.Equal(ws.Tabs[0].Binds, back.Tabs[0].Binds!);
    }

    [Fact]
    public void Old_workspace_json_without_binds_still_reads_and_writes_no_binds()
    {
        const string old = """
            {
              "schemaVersion": 1,
              "id": "default",
              "name": "기본",
              "savedAt": "2026-10-19T09:30:12Z",
              "tabs": [ { "id": "7a8b9c0d-1e2f-4a3b-8c4d-5e6f7a8b9c0d", "title": "SQL 1", "content": "select :x from dual", "isDirty": true } ]
            }
            """;

        var ws = JsonSerializer.Deserialize(old, OrandaJsonContext.Default.Workspace)!;

        Assert.Null(Assert.Single(ws.Tabs).Binds);
        Assert.Equal("select :x from dual", ws.Tabs[0].Content);
        Assert.DoesNotContain("binds", JsonSerializer.Serialize(ws, OrandaJsonContext.Default.Workspace), StringComparison.Ordinal);
    }
}
