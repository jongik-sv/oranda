using Avalonia.Controls;
using Avalonia.Threading;
using Oranda.App.Features.ObjectDetail;
using Oranda.App.Features.SchemaBrowser;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;

namespace Oranda.App.Tests.Features.SchemaBrowser;

/// <summary>
/// TSK-14-22 Java 저장 객체: 스키마 브라우저 폴더(Java 소스·클래스·리소스, 긴 이름 제목, INVALID 표시, 행 수 제한)와
/// 객체 상세(JAVA SOURCE 는 DDL 탭 먼저, JAVA CLASS 의 「소스 열기」). 가짜 메타데이터로 돈다(Oracle 없음).
/// </summary>
public class JavaObjectsBrowserTests
{
    private static readonly DbObjectRef Source = new("SCOTT", "com/example/Hello", "JAVA SOURCE");

    private static readonly DbObjectRef ShortClass = new("SCOTT", "/1a2b3c4d_VeryLongName", "JAVA CLASS");

    [Fact]
    public async Task Java_objects_get_their_own_folders_with_long_names_as_titles()
    {
        var h = new SchemaBrowserTests.Harness();
        h.Meta.Java.AddRange(
        [
            new JavaObjectInfo(Source, "com/example/Hello", "VALID"),
            new JavaObjectInfo(ShortClass, "com/example/deep/pkg/VeryLongName", "INVALID"),
            new JavaObjectInfo(new DbObjectRef("SCOTT", "com/example/Hello", "JAVA CLASS"), "com/example/Hello", "VALID"),
            new JavaObjectInfo(new DbObjectRef("SCOTT", "config.properties", "JAVA RESOURCE"), "config.properties", "VALID"),
            new JavaObjectInfo(new DbObjectRef("HR", "Other", "JAVA SOURCE"), "Other", "VALID"),
        ]);

        await h.Vm.SetSessionAsync(h.Session);

        var scott = h.Vm.Roots[1];
        Assert.Equal(["테이블 (2)", "뷰 (1)", "프로시저 (1)", "Java 소스 (1)", "Java 클래스 (2)", "Java 리소스 (1)"], scott.Children.Select(c => c.Title));
        Assert.Equal("SCOTT", h.Meta.LastJavaQuery?.Owner); // 펼친 스키마만 묻는다
        Assert.Equal(SchemaBrowserViewModel.JavaListLimit + 1, h.Meta.LastJavaQuery?.Limit);

        var classes = scott.Children.Single(c => c.CategoryType == "JAVA CLASS");
        await h.Vm.ExpandAsync(classes);
        Assert.Equal(["com/example/Hello", "com/example/deep/pkg/VeryLongName"], classes.Children.Select(c => c.Title)); // 긴 이름 순
        var longOne = classes.Children[1];
        Assert.Equal(ShortClass, longOne.Ref); // 키는 사전의 짧은 이름
        Assert.Equal("INVALID", longOne.Status);
        Assert.Null(classes.Children[0].Status);
        Assert.Equal("objJava", longOne.IconKey);
        Assert.Equal("objJava", scott.Children.Single(c => c.CategoryType == "JAVA RESOURCE").IconKey);
    }

    [Fact]
    public async Task Type_filter_offers_java_types_and_passes_them_to_the_java_query()
    {
        Assert.Contains("JAVA SOURCE", SchemaBrowserViewModel.KnownTypes);
        Assert.Contains("JAVA CLASS", Oranda.App.Features.DbTools.ObjectSearchViewModel.SearchableTypes);

        var h = new SchemaBrowserTests.Harness();
        h.Meta.Java.Add(new JavaObjectInfo(Source, "com/example/Hello", "VALID"));
        await h.Vm.SetSessionAsync(h.Session);
        h.Vm.TypeFilter = "JAVA SOURCE";
        await h.Vm.ApplyFilterAsync();

        Assert.Equal(["JAVA SOURCE"], h.Meta.LastJavaQuery?.ObjectTypes);
        Assert.Equal(["Java 소스 (1)"], h.Vm.Roots.Single(r => r.Title == "SCOTT").Children.Select(c => c.Title));
    }

    [Fact]
    public async Task A_schema_with_too_many_java_objects_shows_the_first_ones_and_says_so()
    {
        var h = new SchemaBrowserTests.Harness();
        h.Meta.Java.AddRange(Enumerable.Range(0, SchemaBrowserViewModel.JavaListLimit + 3)
            .Select(i => new JavaObjectInfo(new DbObjectRef("SCOTT", $"c/C{i:D5}", "JAVA CLASS"), $"c/C{i:D5}", "VALID")));

        await h.Vm.SetSessionAsync(h.Session);

        var scott = h.Vm.Roots[1];
        Assert.Contains(scott.Children, c => c.Title == $"Java 클래스 ({SchemaBrowserViewModel.JavaListLimit})");
        Assert.Contains($"Java 객체는 처음 {SchemaBrowserViewModel.JavaListLimit}개만", h.Vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Java_source_opens_on_the_ddl_tab_and_a_class_links_to_its_source()
    {
        var meta = new JavaMeta();
        var vm = new ObjectDetailViewModel(meta);
        var session = new SchemaBrowserTests.TestSession();

        await vm.OpenAsync(session, Source, showDdl: false);
        Assert.True(vm.IsDdlSelected); // 소스 글이 본문
        Assert.StartsWith("CREATE OR REPLACE AND COMPILE JAVA SOURCE", vm.DdlText, StringComparison.Ordinal);
        Assert.False(vm.HasSource);

        var cls = new DbObjectRef("SCOTT", "com/example/Hello", "JAVA CLASS");
        await vm.OpenAsync(session, cls, showDdl: false);
        Assert.False(vm.IsDdlSelected);
        Assert.Equal(Source, vm.SourceTarget);
        Assert.True(vm.HasSource);
        Assert.Equal(["Java 클래스", "메서드"], vm.Sections.Select(s => s.Title));

        DbObjectRef? opened = null;
        vm.SourceOpener = (target, _) =>
        {
            opened = target;
            return Task.CompletedTask;
        };
        Assert.True(await vm.OpenSourceAsync());
        Assert.Equal(Source, opened);

        await vm.OpenAsync(session, new DbObjectRef("SCOTT", "EMP", "TABLE"), showDdl: false);
        Assert.False(vm.HasSource); // 다른 객체를 열면 링크가 사라진다
        Assert.False(await vm.OpenSourceAsync());
    }

    [Fact]
    public Task Detail_view_shows_the_open_source_button_only_for_a_class_with_a_source() => HeadlessHost.Run(async () =>
    {
        var vm = new ObjectDetailViewModel(new JavaMeta());
        var session = new SchemaBrowserTests.TestSession();
        DbObjectRef? opened = null;
        vm.SourceOpener = (target, _) =>
        {
            opened = target;
            return Task.CompletedTask;
        };
        await vm.OpenAsync(session, new DbObjectRef("SCOTT", "com/example/Hello", "JAVA CLASS"), showDdl: false);
        var view = new ObjectDetailView { DataContext = vm };
        var w = new Window { Content = view, Width = 900, Height = 600 };
        w.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            var button = view.FindControl<Button>("OpenSourceButton")!;
            Assert.True(button.IsEffectivelyVisible);
            Assert.Equal("소스 열기", button.Content);
            button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            await HeadlessHost.WaitUntil(() => opened is not null);
            Assert.Equal(Source, opened);

            await vm.OpenAsync(session, Source, showDdl: false);
            Dispatcher.UIThread.RunJobs();
            Assert.False(button.IsEffectivelyVisible);
            Assert.Equal(view.FindControl<TabItem>("DdlTab"), view.FindControl<TabControl>("Tabs")!.SelectedItem); // JAVA SOURCE 는 DDL 탭
        }
        finally
        {
            w.Close();
        }
    });

    /// <summary>Java 객체 상세만 아는 가짜 메타데이터.</summary>
    private sealed class JavaMeta : IMetadataProvider
    {
        public Task<Oranda.Core.Metadata.ObjectDetail> GetObjectDetailAsync(ISession s, DbObjectRef obj, CancellationToken ct) =>
            Task.FromResult(obj.ObjectType == "JAVA CLASS"
                ? new Oranda.Core.Metadata.ObjectDetail(obj,
                [
                    new DetailSection("Java 클래스", ["항목", "값"], [["종류", "CLASS"]]),
                    new DetailSection("메서드", ["#", "이름"], [[0, "hi"]]),
                ]) { SourceObject = Source }
                : new Oranda.Core.Metadata.ObjectDetail(obj, [new DetailSection("일반", ["항목", "값"], [["유형", obj.ObjectType]])]));

        public Task<string> GetDdlAsync(ISession s, DbObjectRef obj, DdlOptions o, CancellationToken ct) =>
            Task.FromResult(obj.ObjectType == "JAVA SOURCE"
                ? $"CREATE OR REPLACE AND COMPILE JAVA SOURCE NAMED \"{obj.Owner}\".\"{obj.Name}\" AS\nclass Hello {{}}\n/"
                : "-- note");

        public Task<IReadOnlyList<string>> ListSchemasAsync(ISession s, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<DbObjectRef>> ListObjectsAsync(ISession s, ObjectQuery q, CancellationToken ct) => throw new NotSupportedException();

        public Task<TableInfo> GetTableAsync(ISession s, DbObjectRef table, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<ProcedureInfo>> GetProceduresAsync(ISession s, DbObjectRef obj, CancellationToken ct) => throw new NotSupportedException();

        public Task<string> GetSourceAsync(ISession s, DbObjectRef obj, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<CompileError>> GetCompileErrorsAsync(ISession s, DbObjectRef obj, CancellationToken ct) => throw new NotSupportedException();
    }
}
