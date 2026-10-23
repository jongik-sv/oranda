using System.Text;
using Oranda.App.Features.MyBatisWorkspace.Conversion;
using Oranda.Core.MyBatis;
using Oranda.Language.MyBatis;
using Oranda.Platform.MyBatis;
using Oranda.Testing.MyBatis;

namespace Oranda.App.Tests.Features.MyBatisWorkspace;

/// <summary>TSK-03-05 J4: 새 매퍼·기존 매퍼 추가 쓰기와 첫 시나리오(임시 폴더, 실제 MapperFileWriter).</summary>
public sealed class ConvertWriterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "oranda-convert-" + Guid.NewGuid().ToString("N"));

    public ConvertWriterTests()
    {
        Directory.CreateDirectory(_dir);
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static ConvertedStatement Statement(string id, IReadOnlyDictionary<string, MyBatisScenarioValue>? values = null) => new(
        id,
        ConvertStatementKind.Select,
        $"<select id=\"{id}\">\n  <![CDATA[\n    SELECT * FROM EMP WHERE SAL < #{{sal}}\n  ]]>\n</select>\n",
        values ?? new Dictionary<string, MyBatisScenarioValue>(),
        []);

    private static Dictionary<string, MyBatisScenarioValue> Sal() => new() { ["sal"] = new MyBatisScenarioValue(MyBatisScenarioValueKind.Number, "3000") };

    [Fact]
    public async Task New_mapper_has_doctype_namespace_and_statements_in_order_and_saves_scenarios_for_statements_with_values()
    {
        var store = new FakeMyBatisScenarioStore();
        var path = Path.Combine(_dir, "OrderMapper.xml");

        var result = await new ConvertWriter(new MapperFileWriter(), store)
            .WriteNewAsync(path, "com.acme.OrderMapper", [Statement("selectA", Sal()), Statement("selectB")], saveScenarios: true, CancellationToken.None);

        Assert.True(result.Succeeded, result.Error);
        var doc = new MapperParser().Parse(await File.ReadAllBytesAsync(path));
        Assert.Contains(ConvertWriter.MapperDocType, doc.Text, StringComparison.Ordinal);
        Assert.Equal("com.acme.OrderMapper", doc.MapperNamespace);
        Assert.Equal(["selectA", "selectB"], doc.Elements.Select(e => e.Id));
        Assert.Contains("\n      SELECT * FROM EMP", doc.Text.Replace("\r\n", "\n", StringComparison.Ordinal), StringComparison.Ordinal);
        var scenario = Assert.Single(store.All);
        Assert.Equal(("selectA", "바꿀 때 값", Path.GetFullPath(path)), (scenario.StatementId, scenario.Name, scenario.MapperPath));
        Assert.Equal("3000", scenario.Values["sal"].Text);
    }

    [Fact]
    public async Task New_mapper_refuses_existing_file()
    {
        var path = Path.Combine(_dir, "Exists.xml");
        await File.WriteAllTextAsync(path, MapperSamples.EmpMapper);

        var result = await new ConvertWriter(new MapperFileWriter(), null)
            .WriteNewAsync(path, "x", [Statement("selectA")], saveScenarios: false, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(MapperSamples.EmpMapper, await File.ReadAllTextAsync(path));
    }

    [Theory]
    [InlineData("euc-kr-crlf")]
    [InlineData("utf8-bom")]
    public async Task Appending_keeps_original_bytes_outside_the_insertion_point(string shape)
    {
        byte[] original = shape == "euc-kr-crlf"
            ? Encoding.GetEncoding(51949).GetBytes(
                "<?xml version=\"1.0\" encoding=\"EUC-KR\"?>\r\n<mapper namespace=\"hr.Emp\">\r\n  <!-- 사원 -->\r\n  <select id=\"selectEmp\">SELECT '한글' FROM DUAL</select>\r\n</mapper>\r\n")
            : MapperSamples.Bytes(MapperSamples.WithBom, bom: true);
        var path = Path.Combine(_dir, "Emp.xml");
        await File.WriteAllBytesAsync(path, original);
        int close = LastLineStart(original, "</mapper>"u8);

        var store = new FakeMyBatisScenarioStore();
        var result = await new ConvertWriter(new MapperFileWriter(), store)
            .AppendAsync(path, [Statement("selectNew", Sal())], saveScenarios: true, CancellationToken.None);

        Assert.True(result.Succeeded, result.Error);
        var written = await File.ReadAllBytesAsync(path);
        Assert.Equal(original[..close], written[..close]);
        Assert.Equal(original[close..], written[^(original.Length - close)..]);
        var doc = new MapperParser().Parse(written);
        Assert.Equal("selectNew", doc.Elements[^1].Id);
        Assert.Equal(shape == "euc-kr-crlf" ? "\r\n" : "\n", doc.Format.NewLine);
        Assert.Single(store.All);
    }

    [Fact]
    public async Task Appending_a_duplicate_id_writes_nothing_and_saves_no_scenario()
    {
        var path = Path.Combine(_dir, "Emp.xml");
        await File.WriteAllTextAsync(path, MapperSamples.EmpMapper);
        var id = new MapperParser().Parse(MapperSamples.EmpMapper).Elements.First(e => e.IsStatement).Id!;
        var store = new FakeMyBatisScenarioStore();

        var result = await new ConvertWriter(new MapperFileWriter(), store)
            .AppendAsync(path, [Statement(id, Sal())], saveScenarios: true, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains(id, result.Error, StringComparison.Ordinal);
        Assert.Equal(MapperSamples.EmpMapper, await File.ReadAllTextAsync(path));
        Assert.Empty(store.All);
    }

    [Fact]
    public async Task A_failing_scenario_store_still_leaves_the_written_mapper_and_reports_the_failure()
    {
        var path = Path.Combine(_dir, "OrderMapper.xml");

        var result = await new ConvertWriter(new MapperFileWriter(), new FailingStore())
            .WriteNewAsync(path, "demo.OrderMapper", [Statement("selectA", Sal())], saveScenarios: true, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.NotNull(result.ScenarioError);
        Assert.Equal(0, result.ScenariosSaved);
        Assert.Equal("selectA", Assert.Single(new MapperParser().Parse(await File.ReadAllBytesAsync(path)).Elements).Id);
    }

    [Fact]
    public async Task Multi_line_string_literal_keeps_its_value_after_the_writer_indents_the_element()
    {
        var path = Path.Combine(_dir, "Lit.xml");
        var element = ConvertEngine.Convert(
            new ConvertCandidate(0, "t", 0, "SELECT 'a\nb' AS v FROM DUAL", ConvertStatementKind.Select, true, "selectLit", 0, []),
            ConvertStatementKind.Select, "selectLit", [], new ConvertOptions(), null);

        var result = await new ConvertWriter(new MapperFileWriter(), null).WriteNewAsync(path, "demo.Lit", [element], saveScenarios: false, CancellationToken.None);

        Assert.True(result.Succeeded, result.Error);
        var text = Assert.Single(new MapperParser().Parse(await File.ReadAllBytesAsync(path)).Elements).Text.Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.Contains("'a\nb'", text, StringComparison.Ordinal);
    }

    private sealed class FailingStore : IMyBatisScenarioStore
    {
        public event EventHandler<MyBatisScenarioChangedEventArgs>? Changed
        {
            add { }
            remove { }
        }

        public Task<MyBatisScenario> SaveAsync(MyBatisScenario scenario, CancellationToken ct) => throw new IOException("db locked");

        public Task<IReadOnlyList<MyBatisScenario>> ListAsync(string mapperPath, string? ns, string statementId, CancellationToken ct) => throw new NotSupportedException();

        public Task RenameAsync(long id, string name, CancellationToken ct) => throw new NotSupportedException();

        public Task<MyBatisScenario> DuplicateAsync(long id, string newName, CancellationToken ct) => throw new NotSupportedException();

        public Task DeleteAsync(long id, CancellationToken ct) => throw new NotSupportedException();

        public Task ReorderAsync(IReadOnlyList<long> orderedIds, CancellationToken ct) => throw new NotSupportedException();

        public Task<int> MoveStatementAsync(string mapperPath, string oldId, string newId, CancellationToken ct) => throw new NotSupportedException();
    }

    private static int LastLineStart(byte[] bytes, ReadOnlySpan<byte> marker)
    {
        int at = bytes.AsSpan().LastIndexOf(marker);
        return bytes.AsSpan(0, at).LastIndexOf((byte)'\n') + 1;
    }
}
