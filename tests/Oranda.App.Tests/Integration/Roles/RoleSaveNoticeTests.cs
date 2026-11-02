using Oranda.App.Features.GridEdit;
using Oranda.App.Tests.Shell.Headless;
using Xunit.Abstractions;

namespace Oranda.App.Tests.Integration.Roles;

/// <summary>저장 알림 칸은 역할과 무관하다(TSK-07-09). 정적 SaveFeedback 을 읽고 쓰므로 병렬 묶음(RoleItestUi) 밖에 둔다.</summary>
[Trait("Category", "Integration")]
[Collection("GridEditStatics")] // SaveFeedback 정적 상태를 함께 쓴다(RecentFilesTests 와 같은 묶음)
public sealed class RoleSaveNoticeTests(ITestOutputHelper output)
{
    private static string Features(string[]? roles) => PrdMatrix.FeaturesJson(roles);

    [Fact]
    public Task Save_feedback_notices_have_a_status_item_in_every_role_configuration() => HeadlessHost.Run(async () =>
    {
        // TSK-07-09(결함 D-1 수정): 「저장 알림」 칸은 gridEdit 와 무관한 공통이다. SaveFeedback 은 Excel 내보내기 결과·튜닝 보고서 저장·
        // 최근 파일 없음·MyBatis/PL/SQL 끔 대체 열기 안내까지 싣기 때문에 조회 전용(R6)·튜너(R4)에서도 보여야 한다.
        foreach (var (name, roles) in new[] { ("R6 조회 전용", "viewer"), ("R4 튜너", "tuner"), ("R1 SQL 개발자", "sqlDev") })
        {
            var dir = RoleApp.NewDir();
            try
            {
                SaveFeedback.Clear(); // 앞 시험이 남긴 알림(수명 6초)을 지운다
                using var app = new RoleApp(dir, Features([roles]));
                var chip = app.Window.ViewModel!.StatusItems.Select(e => e.Item).OfType<SaveStatusViewModel>().SingleOrDefault();
                Assert.NotNull(chip); // 칸이 있다
                Assert.False(chip.IsVisible); // 알림이 없으면 접혀 있다

                var file = Path.Combine(dir, "result.xlsx");
                SaveFeedback.ShowExported(120, file);
                try
                {
                    await RoleApp.SettleAsync(() => chip.IsVisible);
                    Assert.True(chip.IsVisible, name);
                    Assert.Equal(file, chip.FilePath); // 「열기」「폴더 열기」 링크가 쓸 경로
                    output.WriteLine($"{name}: 알림 칸 글자 [{chip.Text}]");
                }
                finally
                {
                    SaveFeedback.Clear();
                }

                await RoleApp.SettleAsync(() => !chip.IsVisible);
                Assert.False(chip.IsVisible); // 알림을 지우면 다시 접힌다
            }
            finally
            {
                RoleApp.DeleteDir(dir);
            }
        }
    });
}
