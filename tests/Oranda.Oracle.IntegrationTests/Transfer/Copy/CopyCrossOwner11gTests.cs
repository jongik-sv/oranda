using Oranda.Oracle.IntegrationTests.Integration;

namespace Oranda.Oracle.IntegrationTests.Transfer.Copy;

// 소유: 데이터 복사 묶음 B. 11.2 서버(Oracle11gShared)에서 시노님 풀기·권한 점검·두 계정 복사. 시험 사용자 이름은 30자 이하(CP_{8자}_APP).

/// <summary>11.2 에서도 <see cref="CopyCrossOwnerTests"/> 의 핵심 셋이 같다(ALL_ 뷰 경로, 12c 전용 뷰 없음).</summary>
[Trait("Category", "Integration")]
[Collection(Oracle11gShared.Name)]
public sealed class CopyCrossOwner11gTests
{
    private readonly Oracle11gFixture _db;

    public CopyCrossOwner11gTests(Oracle11gFixture db) => _db = db;

    [Fact]
    public async Task Synonyms_resolve_to_the_real_owner()
    {
        await using var scope = await CopyDbScope.CreateAsync(_db, CopyDbScope.NewTag());
        await CrossOwnerCases.SynonymsResolveAsync(scope);
    }

    [Fact]
    public async Task Writer_copies_into_another_owner_with_granted_rights_and_missing_delete_is_an_error()
        => await CrossOwnerCases.WriterOnOtherOwnerAsync(await CopyDbScope.CreateAsync(_db, CopyDbScope.NewTag()));

    [Fact]
    public async Task Two_owner_job_orders_by_cross_owner_fk_and_rerun_gives_the_same_result()
        => await CrossOwnerCases.TwoOwnerCopyAsync(await CopyDbScope.CreateAsync(_db, CopyDbScope.NewTag()));

    [Fact]
    public async Task Target_view_synonym_or_materialized_view_is_target_missing()
        => await CrossOwnerCases.TargetThatIsNotATableAsync(await CopyDbScope.CreateAsync(_db, CopyDbScope.NewTag()));
}
