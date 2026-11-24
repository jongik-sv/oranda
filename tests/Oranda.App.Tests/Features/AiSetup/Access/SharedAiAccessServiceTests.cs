using Oranda.App.Features.AiSetup.Access;
using Oranda.Core.Ai.Access;
using Oranda.Core.Connection;
using Oranda.Core.Events;

namespace Oranda.App.Tests.Features.AiSetup.Access;

/// <summary>
/// 「AI 가 볼 수 있는 접속」 서비스(ai-settings-one-place 2026-11-24): 한 값을 읽고 쓰며, 어느 화면(접속 편집·외부 에이전트 연결 창 포함)에서 저장하든
/// 접속 저장 사건으로 바뀜을 알린다. 비밀번호는 넘기지 않는다.
/// </summary>
public sealed class SharedAiAccessServiceTests
{
    private sealed class Store
    {
        public List<ConnectionProfile> Profiles { get; } = [];

        public List<ConnectionProfile> Saved { get; } = [];

        public EventBus? Bus { get; init; }

        public Task<IReadOnlyList<ConnectionProfile>> LoadAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<ConnectionProfile>>([.. Profiles]);

        public Task<ConnectionProfile> SaveAsync(ConnectionProfile profile, CancellationToken ct)
        {
            Saved.Add(profile);
            Profiles[Profiles.FindIndex(p => p.Id == profile.Id)] = profile;
            Bus?.Publish(new ConnectionProfileSaved(profile)); // 접속 서비스가 하는 일
            return Task.FromResult(profile);
        }
    }

    private static readonly ConnectionProfile Dev = new("dev-erp", "scott", "db.example.com:1521/DEV", Id: Guid.NewGuid());
    private static readonly ConnectionProfile Prd = new("prd-erp", "scott", "db.example.com:1521/PRD", Id: Guid.NewGuid(), Environment: ConnectionEnvironment.Production, AiAccess: AiAccessLevel.Off);

    private static (SharedAiAccessService Service, Store Store) Make(EventBus? bus = null)
    {
        var store = new Store { Bus = bus };
        store.Profiles.AddRange([Dev, Prd]);
        return (new SharedAiAccessService(store.LoadAsync, store.SaveAsync, bus), store);
    }

    [Fact]
    public async Task Load_shows_inside_and_external_levels_per_connection()
    {
        var (service, _) = Make();

        var rows = await service.LoadAsync(CancellationToken.None);

        Assert.Equal(["dev-erp", "prd-erp"], rows.Select(r => r.Name));
        Assert.Equal(AiAccessLevel.Plan, rows[0].Level);
        Assert.Equal(AiAccessLevel.Off, rows[1].ExternalLevel);
        Assert.True(rows[1].ShowsExternalCap);
    }

    [Fact]
    public async Task Set_level_saves_once_and_reports_the_change()
    {
        var (service, store) = Make();
        var changes = new List<AiConnectionAccess>();
        service.Changed += (_, a) => changes.Add(a);

        var (result, access) = await service.SetLevelAsync(Dev.Id, AiAccessLevel.QueryRows, null, CancellationToken.None);
        var (same, _) = await service.SetLevelAsync(Dev.Id, AiAccessLevel.QueryRows, null, CancellationToken.None);

        Assert.Equal(AiAccessChange.Saved, result);
        Assert.Equal(AiAccessChange.Saved, same);
        Assert.Equal(AiAccessLevel.QueryRows, access!.Level);
        Assert.Equal(AiAccessLevel.QueryRows, Assert.Single(store.Saved).AiAccess); // 같은 값은 다시 저장하지 않는다
        Assert.Equal(AiAccessLevel.QueryRows, Assert.Single(changes).ExternalLevel);
    }

    [Fact]
    public async Task Production_raise_needs_the_name_and_the_maximum_holds()
    {
        var (service, store) = Make();

        Assert.Equal(AiAccessChange.NeedsNameConfirmation, (await service.SetLevelAsync(Prd.Id, AiAccessLevel.Plan, null, CancellationToken.None)).Result);
        Assert.Equal(AiAccessChange.NeedsNameConfirmation, (await service.SetLevelAsync(Prd.Id, AiAccessLevel.QueryRows, null, CancellationToken.None)).Result); // 운영 행 값도 이름 재입력(2026-11-24)
        Assert.Equal(AiAccessChange.AboveMaximum, (await service.SetLevelAsync(Prd.Id, (AiAccessLevel)42, "prd-erp", CancellationToken.None)).Result);
        Assert.Empty(store.Saved);

        Assert.Equal(AiAccessChange.Saved, (await service.SetLevelAsync(Prd.Id, AiAccessLevel.Plan, "prd-erp", CancellationToken.None)).Result);
        Assert.Equal(AiAccessLevel.Plan, Assert.Single(store.Saved).AiAccess);
        Assert.Equal(AiAccessChange.NotFound, (await service.SetLevelAsync(Guid.NewGuid(), AiAccessLevel.Plan, null, CancellationToken.None)).Result);
    }

    [Fact]
    public async Task A_save_from_another_screen_is_reported_through_the_bus_once()
    {
        // 외부 에이전트 연결 창의 「조회 허용」·접속 편집·도우미 단계도 접속 서비스로 저장한다 → 같은 사건이 이 목록을 바꾼다.
        var bus = new EventBus();
        var (service, _) = Make(bus);
        var changes = new List<AiConnectionAccess>();
        service.Changed += (_, a) => changes.Add(a);

        bus.Publish(new ConnectionProfileSaved(Dev with { AiAccess = AiAccessLevel.QueryRows }));
        await service.SetLevelAsync(Dev.Id, AiAccessLevel.Metadata, null, CancellationToken.None);

        Assert.Equal([AiAccessLevel.QueryRows, AiAccessLevel.Metadata], changes.Select(c => c.Level));

        service.Dispose();
        bus.Publish(new ConnectionProfileSaved(Dev));
        Assert.Equal(2, changes.Count);
    }
}
