using Oranda.Core.Metadata;

namespace Oranda.Core.Tests.Metadata;

/// <summary>
/// 결과의 표 이름이 시노님이면 실제 소유자.표로 푼다(플래시백·편집 대상). 오라클 이름 풀이 순서와 같다:
/// 그 스키마의 표·뷰 → 그 스키마의 개인 시노님 → (스키마를 적지 않았을 때만) PUBLIC 시노님. 가짜 사전으로 한 단계씩 답한다.
/// </summary>
public sealed class SynonymResolverTests
{
    private static readonly NameStep Table = NameStep.TableOrView;

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

    private static Task<DbObjectRef> Resolve(Dictionary d, string owner, string name, bool ownerWritten = false) =>
        SynonymResolver.ResolveAsync(d.StepAsync, new DbObjectRef(owner, name, "TABLE"), ownerWritten);

    [Fact]
    public async Task Own_table_stays_as_it_is()
    {
        var d = new Dictionary();
        d.Entries[("APP_USER", "ORDERS")] = Table;
        d.Entries[("PUBLIC", "ORDERS")] = NameStep.Synonym("APP_OWNER", "ORDERS"); // 내 표가 있으면 PUBLIC 시노님보다 먼저

        var t = await Resolve(d, "APP_USER", "ORDERS");

        Assert.Equal(new DbObjectRef("APP_USER", "ORDERS", "TABLE"), t);
        Assert.Equal(["APP_USER.ORDERS"], d.Asked);
    }

    [Fact]
    public async Task Private_synonym_points_at_the_owning_table()
    {
        var d = new Dictionary();
        d.Entries[("APP_USER", "ORDERS")] = NameStep.Synonym("APP_OWNER", "ORDERS_T");
        d.Entries[("APP_OWNER", "ORDERS_T")] = Table;

        Assert.Equal(new DbObjectRef("APP_OWNER", "ORDERS_T", "TABLE"), await Resolve(d, "APP_USER", "ORDERS"));
    }

    [Fact]
    public async Task Public_synonym_is_used_only_when_the_schema_has_nothing_by_that_name()
    {
        var d = new Dictionary();
        d.Entries[("PUBLIC", "ORDERS")] = NameStep.Synonym("APP_OWNER", "ORDERS");
        d.Entries[("APP_OWNER", "ORDERS")] = Table;

        Assert.Equal(new DbObjectRef("APP_OWNER", "ORDERS", "TABLE"), await Resolve(d, "APP_USER", "ORDERS"));
        Assert.Equal(["APP_USER.ORDERS", "PUBLIC.ORDERS", "APP_OWNER.ORDERS"], d.Asked);
    }

    [Fact]
    public async Task Written_schema_does_not_fall_back_to_public()
    {
        var d = new Dictionary();
        d.Entries[("PUBLIC", "ORDERS")] = NameStep.Synonym("APP_OWNER", "ORDERS");

        Assert.Equal(new DbObjectRef("OTHER", "ORDERS", "TABLE"), await Resolve(d, "OTHER", "ORDERS", ownerWritten: true));
        Assert.Equal(["OTHER.ORDERS"], d.Asked);
    }

    [Fact]
    public async Task Synonym_chain_is_followed_to_the_table()
    {
        var d = new Dictionary();
        d.Entries[("APP_USER", "ORDERS")] = NameStep.Synonym("PUBLIC", "ORDERS_ALL");
        d.Entries[("PUBLIC", "ORDERS_ALL")] = NameStep.Synonym("MID", "ORDERS_S");
        d.Entries[("MID", "ORDERS_S")] = NameStep.Synonym("APP_OWNER", "ORDERS");
        d.Entries[("APP_OWNER", "ORDERS")] = Table;

        Assert.Equal(new DbObjectRef("APP_OWNER", "ORDERS", "TABLE"), await Resolve(d, "APP_USER", "ORDERS"));
    }

    [Fact]
    public async Task Target_not_visible_is_still_returned_so_the_caller_reports_it()
    {
        var d = new Dictionary();
        d.Entries[("APP_USER", "ORDERS")] = NameStep.Synonym("APP_OWNER", "ORDERS"); // 권한이 없어 대상이 사전에 안 보임

        Assert.Equal(new DbObjectRef("APP_OWNER", "ORDERS", "TABLE"), await Resolve(d, "APP_USER", "ORDERS"));
    }

    [Fact]
    public async Task Unknown_name_stays_as_it_is()
    {
        var d = new Dictionary();

        Assert.Equal(new DbObjectRef("APP_USER", "NOPE", "TABLE"), await Resolve(d, "APP_USER", "NOPE"));
    }

    [Fact]
    public async Task Cycle_is_refused_with_the_synonym_name()
    {
        var d = new Dictionary();
        d.Entries[("APP_USER", "A")] = NameStep.Synonym("APP_USER", "B");
        d.Entries[("APP_USER", "B")] = NameStep.Synonym("APP_USER", "A");

        var e = await Assert.ThrowsAsync<SynonymResolutionException>(() => Resolve(d, "APP_USER", "A"));
        Assert.Contains("APP_USER.A", e.Message, StringComparison.Ordinal);
        Assert.Contains("순환", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Too_long_chain_is_refused()
    {
        var d = new Dictionary();
        for (var i = 0; i < SynonymResolver.MaxDepth + 2; i++)
        {
            d.Entries[("S", "N" + i)] = NameStep.Synonym("S", "N" + (i + 1));
        }

        var e = await Assert.ThrowsAsync<SynonymResolutionException>(() => Resolve(d, "S", "N0"));
        Assert.Contains("단계", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Db_link_synonym_is_refused_as_unsupported()
    {
        var d = new Dictionary();
        d.Entries[("APP_USER", "ORDERS")] = NameStep.Synonym("APP_OWNER", "ORDERS", "REMOTE_LINK");

        var e = await Assert.ThrowsAsync<SynonymResolutionException>(() => Resolve(d, "APP_USER", "ORDERS"));
        Assert.Contains("DB 링크", e.Message, StringComparison.Ordinal);
        Assert.Contains("APP_USER.ORDERS", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Synonym_to_a_schema_without_owner_keeps_the_synonym_owner()
    {
        var d = new Dictionary();
        d.Entries[("APP_USER", "ORDERS")] = NameStep.Synonym(null, "ORDERS_T"); // 사전에 TABLE_OWNER 가 비어 있으면 시노님 소유자 스키마

        Assert.Equal(new DbObjectRef("APP_USER", "ORDERS_T", "TABLE"), await Resolve(d, "APP_USER", "ORDERS"));
    }
}
