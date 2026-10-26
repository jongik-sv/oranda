using System.Diagnostics;
using Oranda.App.Features.DbTools;
using Oranda.App.Features.SchemaBrowser;
using Oranda.Core.Metadata;
using Xunit.Abstractions;

namespace Oranda.App.Tests.Integration;

/// <summary>
/// TSK-12-01 「INVALID 객체 검색 안 됨」 실제 서버 시험(상주 컨테이너 26ai·11.2, ORANDA_IT_PORT/SERVICE 로 고른다).
/// 표를 지워 뷰·프로시저·함수를 INVALID 로 만든 뒤, INVALID 로 거르는 모든 길(메타 제공자·객체 검색·스키마 브라우저·DDL 추출·일괄 컴파일)이
/// 그 객체를 찾는지 본다. 메타 캐시가 데워진 뒤(옛 상태가 캐시에 남은 뒤) 새로 INVALID 가 된 경우가 핵심이다.
/// 시험 객체는 시험 사용자(<see cref="ItDb"/>, 끝나면 사용자째 지운다)에 만든다. ORANDA_IT_ADMIN_PASSWORD 가 없으면 아무것도 하지 않는다.
/// </summary>
[Trait("Category", "Integration")]
[Collection("GridEditStatics")]
[Oranda.App.Tests.TestSupport.ResetUiLanguage]
public sealed class InvalidSearchIntegrationTests(ItDb db, ITestOutputHelper output) : IClassFixture<ItDb>
{
    [Fact]
    public Task Every_invalid_filter_finds_objects_that_became_invalid_after_the_cache_was_filled() => Oranda.App.Tests.Features.SqlEditor.Ui.Run(async () =>
    {
        if (!db.Enabled)
        {
            return;
        }

        var t = "IV_" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        string[] expected = [$"{t}_F", $"{t}_P", $"{t}_V"];
        db.Exec($"create table {t} (id number)");
        db.Exec($"create view {t}_V as select id from {t}");
        db.Exec($"create procedure {t}_P as n number; begin select count(*) into n from {t}; end;");
        db.Exec($"create function {t}_F return number as n number; begin select count(*) into n from {t}; return n; end;");
        var outDir = Path.Combine(Path.GetTempPath(), "oranda-iv-" + t);
        try
        {
            using var rig = new FlowRig(db);
            await rig.ConnectAsync();
            var meta = rig.Container.GetRequiredService<IMetadataProvider>();
            var own = new ObjectQuery { Owner = db.User };

            // 캐시를 데운다: 이때는 모두 VALID.
            var warm = await meta.ListObjectsAsync(rig.Session, own, default);
            Assert.Equal(4, warm.Count(o => o.Name.StartsWith(t, StringComparison.Ordinal)));
            Assert.Empty(await meta.ListObjectsAsync(rig.Session, own with { InvalidOnly = true }, default));

            // 표를 지우면 뷰·프로시저·함수가 INVALID 가 된다(캐시에는 아직 VALID 로 남아 있다).
            db.Exec($"drop table {t} purge");

            // 1. 메타 제공자(스키마 브라우저·DDL 추출·INVALID 거르기의 바탕)
            var sw = Stopwatch.StartNew();
            var invalid = await meta.ListObjectsAsync(rig.Session, own with { InvalidOnly = true }, default);
            output.WriteLine($"meta InvalidOnly {sw.ElapsedMilliseconds} ms");
            Assert.Equal(expected, invalid.Select(o => o.Name).Order(StringComparer.Ordinal));
            var views = await meta.ListObjectsAsync(rig.Session, own with { InvalidOnly = true, ObjectTypes = ["VIEW"] }, default);
            Assert.Equal([$"{t}_V"], views.Select(o => o.Name));
            var named = await meta.ListObjectsAsync(rig.Session, own with { InvalidOnly = true, NamePattern = $"{t}_P%" }, default);
            Assert.Equal([$"{t}_P"], named.Select(o => o.Name));

            // 2. 객체 검색 창
            var search = new ObjectSearchViewModel(rig.Container);
            search.Attach(rig.Session);
            search.InvalidOnly = true;
            sw.Restart();
            await search.SearchAsync();
            output.WriteLine($"object search {sw.ElapsedMilliseconds} ms");
            Assert.Equal(expected, search.Results.Select(r => r.Ref.Name).Order(StringComparer.Ordinal));
            Assert.All(search.Results, r => Assert.Equal("INVALID", r.Row.Status));
            search.TypeFilter = "VIEW";
            await search.SearchAsync();
            Assert.Equal([$"{t}_V"], search.Results.Select(r => r.Ref.Name));
            search.TypeFilter = null;
            search.NameText = t.ToLowerInvariant();
            await search.SearchAsync();
            Assert.Equal(3, search.Results.Count);

            // 3. 스키마 브라우저 「INVALID 객체만」 토글
            var browser = new SchemaBrowserViewModel(rig.Container) { PostToUi = a => a(), DebounceMilliseconds = 0 };
            await browser.SetSessionAsync(rig.Session);
            sw.Restart();
            browser.InvalidOnly = true;
            await Oranda.App.Tests.Shell.Headless.HeadlessHost.WaitUntil(() => !browser.IsLoading);
            output.WriteLine($"schema browser toggle {sw.ElapsedMilliseconds} ms");
            var schema = browser.Roots.First(r => r.Title == db.User);
            Assert.Equal(expected, schema.Children.SelectMany(c => c.Items).Select(o => o.Name).Where(n => n.StartsWith(t, StringComparison.Ordinal)).Order(StringComparer.Ordinal));
            Assert.DoesNotContain(schema.Children.SelectMany(c => c.Items), o => o.ObjectType == "TABLE");

            // 4. DDL 추출 「INVALID 객체만」
            var ddl = new DdlExtractViewModel(rig.Container) { InvalidOnly = true, OutputDirectory = outDir };
            ddl.Attach(rig.Session);
            ddl.OutputDirectory = outDir;
            await ddl.RunAsync();
            Assert.Equal(3, ddl.Results.Count(r => r.Title.Contains(t, StringComparison.Ordinal)));

            // 5. 「INVALID 객체 일괄 컴파일」: 표를 되살리면 모두 컴파일된다.
            db.Exec($"create table {t} (id number)");
            search.NameText = string.Empty;
            await search.CompileInvalidAsync();
            Assert.True(3 == search.CompileResults.Count(r => r.Outcome == CompileOutcome.Compiled && r.Ref.Name.StartsWith(t, StringComparison.Ordinal)),
                string.Join(" | ", search.CompileResults.Select(r => $"{r.Ref.Name} {r.Outcome} {r.Message}")) + " / " + search.StatusText);
            await search.SearchAsync();
            Assert.DoesNotContain(search.Results, r => r.Ref.Name.StartsWith(t, StringComparison.Ordinal));
        }
        finally
        {
            try
            {
                Directory.Delete(outDir, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    });
}
