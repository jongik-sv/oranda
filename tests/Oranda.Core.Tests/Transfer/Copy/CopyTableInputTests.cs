using Oranda.Core.Metadata;
using Oranda.Core.Transfer.Copy;
using Xunit;

namespace Oranda.Core.Tests.Transfer.Copy;

/// <summary>
/// 복사 표 입력 풀이(묶음 B): 가짜 이름 풀이 단계로 계정 순서(기본 원본 계정 → 세션 사용자 → PUBLIC), 적은 계정만 보기,
/// 시노님 따라가기·DB 링크·보이지 않는 대상·이름 꼴 오류를 본다.
/// </summary>
public class CopyTableInputTests
{
    private sealed class Dictionary
    {
        public Dictionary<(string Owner, string Name), NameStep> Entries { get; } = [];

        public List<string> Asked { get; } = [];

        public Task<NameStep?> StepAsync(string owner, string name)
        {
            Asked.Add(owner + "." + name);
            return Task.FromResult(Entries.TryGetValue((owner, name), out var step) ? step : null);
        }
    }

    private static Task<CopyTableResolution> Resolve(Dictionary d, string input, string? defaultOwner = "SRC", string? user = "ME")
        => CopyTableInput.ResolveAsync(d.StepAsync, input, defaultOwner, user);

    [Fact]
    public async Task Plain_table_in_default_owner_is_taken_as_is()
    {
        var d = new Dictionary();
        d.Entries[("SRC", "ORDERS")] = NameStep.TableOrView;

        var r = await Resolve(d, "orders");

        Assert.Equal(new CopyTableName("SRC", "ORDERS"), r.Table);
        Assert.False(r.Table!.ViaSynonym);
        Assert.Equal(["SRC.ORDERS"], d.Asked);
    }

    [Fact]
    public async Task Name_only_looks_in_default_owner_then_session_user_then_public()
    {
        var d = new Dictionary();
        d.Entries[("ME", "ORDERS")] = NameStep.Synonym("APP2", "ORDERS");
        d.Entries[("PUBLIC", "ORDERS")] = NameStep.Synonym("OTHER", "ORDERS"); // 세션 사용자 시노님이 먼저
        d.Entries[("APP2", "ORDERS")] = NameStep.TableOrView;

        var r = await Resolve(d, "ORDERS");

        Assert.Equal(new CopyTableName("APP2", "ORDERS", "ORDERS"), r.Table);
        Assert.True(r.Table!.ViaSynonym);
        Assert.Equal(["SRC.ORDERS", "ME.ORDERS", "APP2.ORDERS"], d.Asked);
    }

    [Fact]
    public async Task Public_synonym_is_last_and_chains_are_followed()
    {
        var d = new Dictionary();
        d.Entries[("PUBLIC", "CUST")] = NameStep.Synonym("SRC", "CUST_SYN");
        d.Entries[("SRC", "CUST_SYN")] = NameStep.Synonym("APP", "CUSTOMERS");
        d.Entries[("APP", "CUSTOMERS")] = NameStep.TableOrView;

        var r = await Resolve(d, "CUST");

        Assert.Equal(new CopyTableName("APP", "CUSTOMERS", "CUST"), r.Table);
    }

    [Fact]
    public async Task Written_owner_is_the_only_place_looked_at()
    {
        var d = new Dictionary();
        d.Entries[("PUBLIC", "ORDERS")] = NameStep.Synonym("APP", "ORDERS");
        d.Entries[("APP", "ORDERS")] = NameStep.TableOrView;

        var r = await Resolve(d, "x.orders");

        Assert.False(r.Found);
        Assert.Contains("X.ORDERS", r.Problem, StringComparison.Ordinal);
        Assert.Equal(["X.ORDERS"], d.Asked);

        d.Entries[("X", "ORDERS")] = NameStep.Synonym("APP", "ORDERS");
        Assert.Equal(new CopyTableName("APP", "ORDERS", "X.ORDERS"), (await Resolve(d, "X.ORDERS")).Table);
    }

    [Fact]
    public async Task Db_link_synonym_is_refused_with_choose_that_database_message()
    {
        var d = new Dictionary();
        d.Entries[("SRC", "REMOTE")] = NameStep.Synonym("APP", "ORDERS", "PROD_LINK");

        var r = await Resolve(d, "REMOTE");

        Assert.False(r.Found);
        Assert.Contains("PROD_LINK", r.Problem, StringComparison.Ordinal);
        Assert.Contains("원본 접속으로 고르세요", r.Problem, StringComparison.Ordinal);
        Assert.Contains("원본 접속으로 고르세요", (await Resolve(new Dictionary(), "T@LINK")).Problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Synonym_to_an_invisible_table_and_unknown_name_are_not_added()
    {
        var d = new Dictionary();
        d.Entries[("SRC", "HIDDEN")] = NameStep.Synonym("SECRET", "T");

        Assert.Contains("SECRET.T", (await Resolve(d, "HIDDEN")).Problem, StringComparison.Ordinal);
        Assert.Contains("찾을 수 없습니다", (await Resolve(d, "NOPE")).Problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cycle_is_reported()
    {
        var d = new Dictionary();
        d.Entries[("SRC", "A")] = NameStep.Synonym("SRC", "B");
        d.Entries[("SRC", "B")] = NameStep.Synonym("SRC", "A");

        Assert.Contains("순환", (await Resolve(d, "A")).Problem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("orders", null, "ORDERS")]
    [InlineData(" app . orders ", null, null)]
    [InlineData("app.orders", "APP", "ORDERS")]
    [InlineData("\"App\".\"Mixed Name\"", "App", "Mixed Name")]
    [InlineData("a.b.c", null, null)]
    [InlineData("", null, null)]
    [InlineData("\"open", null, null)]
    public void Parse_splits_owner_and_name(string input, string? owner, string? name)
    {
        var (o, n, problem) = CopyTableInput.Parse(input);
        if (name is null)
        {
            Assert.NotNull(problem);
            return;
        }

        Assert.Null(problem);
        Assert.Equal(owner, o);
        Assert.Equal(name, n);
        Assert.Equal((o, n, (string?)null), CopyTableInput.Parse(CopyTableInput.Format(o, n)));
    }
}
