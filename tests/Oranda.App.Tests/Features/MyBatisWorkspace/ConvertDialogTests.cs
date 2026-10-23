using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Oranda.App.Features.Localization;
using Oranda.App.Features.MyBatisWorkspace.Conversion;
using Oranda.App.Shell;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Execution;
using Oranda.Core.MyBatis;

namespace Oranda.App.Tests.Features.MyBatisWorkspace;

/// <summary>TSK-03-05 J3: 바꾸기 대화 상자 ViewModel(가짜 엔진 대리자)과 창 배치. 나중에 ConvertTests.cs 로 합친다.</summary>
[Collection("UiLanguage")]
public sealed partial class ConvertDialogTests : IDisposable
{
    private readonly IShellText? _previous = UiText.Current;

    public ConvertDialogTests() => UiText.Current = new ShellText(language: "ko");

    public void Dispose() => UiText.Current = _previous;

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_]*$")]
    private static partial Regex IdPattern();

    private static ConvertCandidate Cand(int tab, string id, ConvertStatementKind? kind = ConvertStatementKind.Select, bool include = true, params ConvertNote[] notes) =>
        new(tab, "탭" + tab, 0, "select 1 from dual", kind, include, id, 1, notes);

    private delegate ConvertedStatement ConvertFn(
        ConvertCandidate c, ConvertStatementKind kind, string id, IReadOnlyList<ConvertBind> binds, ConvertOptions options, Oranda.Core.Formatting.ISqlFormatter? formatter);

    private static ConvertedStatement FakeConvert(ConvertStatementKind kind, string id, IReadOnlyList<ConvertNote> notes) => new(
        id,
        kind,
        $"<{kind.ToString().ToLowerInvariant()} id=\"{id}\">x</{kind.ToString().ToLowerInvariant()}>\n",
        new Dictionary<string, MyBatisScenarioValue> { ["name"] = new(MyBatisScenarioValueKind.Text, "SCOTT"), ["n"] = new(MyBatisScenarioValueKind.Number, "7") },
        notes);

    private static ConvertDialogViewModel Make(
        IReadOnlyList<ConvertCandidate> candidates,
        Func<string, ExistingMapperRead>? readExisting = null,
        Func<IReadOnlyCollection<string>, IReadOnlyList<ConvertCandidate>>? analyze = null,
        ConvertFn? convert = null,
        TimeSpan? readDelay = null,
        string suggestedNamespace = "com.acme.New")
    {
        IReadOnlyList<ConvertSourceTab> tabs = [.. candidates.Select(c => new ConvertSourceTab(c.TabTitle, c.Sql, []))];
        convert ??= (_, kind, id, _, _, _) => FakeConvert(kind, id, []);
        return new ConvertDialogViewModel(
            "work.sqlw",
            tabs,
            "/m/NewMapper.xml",
            suggestedNamespace,
            readExisting ?? (_ => new ExistingMapperRead("com.acme.Old", ["findOrder"])),
            (_, existing) => analyze is null ? candidates : analyze(existing),
            (c, kind, id, binds, options, formatter) => convert(c, kind, id, binds, options, formatter),
            id => IdPattern().IsMatch(id),
            existingReadDelay: readDelay ?? TimeSpan.Zero);
    }

    [Fact]
    public void Included_count_drives_the_confirm_text_and_enablement()
    {
        var vm = Make([Cand(0, "a"), Cand(1, "b"), Cand(2, "c", include: false)]);
        Assert.Equal(2, vm.IncludedCount);
        Assert.Equal("문장 2개 바꾸고 열기", vm.ConfirmText);
        Assert.True(vm.CanConfirm);

        vm.Lines[2].Include = true;
        Assert.Equal("문장 3개 바꾸고 열기", vm.ConfirmText);

        foreach (var l in vm.Lines)
        {
            l.Include = false;
        }

        Assert.Equal("문장 0개 바꾸고 열기", vm.ConfirmText);
        Assert.False(vm.CanConfirm);

        vm.Lines[0].Include = true;
        vm.Path = "  ";
        Assert.False(vm.CanConfirm);
    }

    [Fact]
    public void Duplicate_or_invalid_ids_disable_confirm_only_for_included_lines()
    {
        var vm = Make([Cand(0, "a"), Cand(1, "a"), Cand(2, "c", include: false)]);
        Assert.NotNull(vm.Lines[0].IdError);
        Assert.NotNull(vm.Lines[1].IdError);
        Assert.False(vm.CanConfirm);

        vm.Lines[1].Id = "b";
        Assert.Null(vm.Lines[0].IdError);
        Assert.True(vm.CanConfirm);

        vm.Lines[1].Id = "1bad";
        Assert.NotNull(vm.Lines[1].IdError);
        Assert.False(vm.CanConfirm);

        // 제외한 줄의 잘못된 id 는 막지 않는다.
        vm.Lines[1].Include = false;
        Assert.Null(vm.Lines[1].IdError);
        Assert.True(vm.CanConfirm);
    }

    [Fact]
    public void A_line_without_a_kind_cannot_be_picked_or_included()
    {
        var vm = Make([Cand(0, "a"), Cand(1, "ddl", kind: null, include: true, new ConvertNote(ConvertNoteKind.Unsupported))]);
        var line = vm.Lines[1];
        Assert.False(line.CanPickKind);
        Assert.False(line.Include);
        Assert.Null(line.Kind);
        line.Include = true;
        Assert.False(line.Include);
        Assert.Equal(1, vm.IncludedCount);
        Assert.True(line.HasWarning);
        Assert.Contains("바꿀 수 없는 문장", line.NoteText, StringComparison.Ordinal);
        Assert.Equal("바인드 1 · 문제 없음", vm.Lines[0].NoteText);
    }

    [Fact]
    public async Task Switching_to_an_existing_mapper_recomputes_overlap_and_reads_its_namespace()
    {
        var reads = new List<string>();
        var vm = Make([Cand(0, "findOrder"), Cand(1, "other")], p =>
        {
            reads.Add(p);
            return p.EndsWith("bad.xml", StringComparison.Ordinal)
                ? new ExistingMapperRead(null, [], "읽을 수 없습니다")
                : new ExistingMapperRead("com.acme.Old", ["findOrder"]);
        });
        Assert.True(vm.CanConfirm);
        Assert.True(vm.IsNamespaceEditable);
        Assert.Equal("com.acme.New", vm.Namespace);

        vm.IsExistingMapper = true;
        Assert.False(vm.CanConfirm); // 기존 경로가 아직 비어 있다
        vm.Path = "/m/Old.xml";
        Assert.False(vm.CanConfirm); // 읽는 동안에는 확인을 막는다
        await vm.ExistingReadTask;
        Assert.Equal(["/m/Old.xml"], reads);
        Assert.Equal("com.acme.Old", vm.Namespace);
        Assert.False(vm.IsNamespaceEditable);
        Assert.NotNull(vm.Lines[0].IdError);
        Assert.False(vm.CanConfirm);

        vm.Lines[0].Id = "findOrder2";
        Assert.True(vm.CanConfirm);

        vm.Path = "/m/bad.xml";
        await vm.ExistingReadTask;
        Assert.True(vm.HasExistingError);
        Assert.False(vm.CanConfirm);

        vm.IsNewFile = true;
        Assert.Equal("/m/NewMapper.xml", vm.Path);
        Assert.Equal("com.acme.New", vm.Namespace);
        Assert.False(vm.HasExistingError);
        Assert.True(vm.CanConfirm);
    }

    [Fact]
    public void Selecting_a_line_refreshes_the_preview_and_scenario_text()
    {
        var vm = Make([Cand(0, "a"), Cand(1, "b", ConvertStatementKind.Insert)]);
        Assert.Same(vm.Lines[0], vm.SelectedLine);
        Assert.Contains("<select id=\"a\">", vm.PreviewXml, StringComparison.Ordinal);
        Assert.Equal("미리보기 · a", vm.PreviewTitle);
        Assert.Equal("name = 'SCOTT' · n = 7", vm.ScenarioText);

        vm.SelectedLine = vm.Lines[1];
        Assert.Contains("<insert id=\"b\">", vm.PreviewXml, StringComparison.Ordinal);

        vm.Lines[1].KindIndex = (int)ConvertStatementKind.Delete;
        Assert.Contains("<delete id=\"b\">", vm.PreviewXml, StringComparison.Ordinal);

        vm.Lines[1].Id = "b2";
        Assert.Equal("미리보기 · b2", vm.PreviewTitle);

        vm.SaveScenario = false;
        Assert.DoesNotContain("SCOTT", vm.ScenarioText, StringComparison.Ordinal);
    }

    [Fact]
    public void Confirm_returns_the_included_statements_in_order_with_the_chosen_options()
    {
        var vm = Make([Cand(0, "a"), Cand(1, "b", include: false), Cand(2, "c")]);
        vm.WrapWhere = true;
        vm.UseCdata = false;
        var result = vm.TryBuildResult();
        Assert.NotNull(result);
        Assert.Equal(ConvertTargetKind.NewFile, result.Target);
        Assert.Equal("/m/NewMapper.xml", result.Path);
        Assert.Equal("com.acme.New", result.Namespace);
        Assert.Equal(["a", "c"], result.Statements.Select(s => s.Id));
        Assert.True(result.Options.WrapWhere);
        Assert.False(result.Options.UseCdata);

        vm.Lines[0].Include = false;
        vm.Lines[2].Include = false;
        Assert.Null(vm.TryBuildResult());
    }

    // (3) 바꾸기 단계 알림이 확인 열에 합쳐진다.
    [Fact]
    public void Convert_notes_are_merged_into_the_note_text_of_included_lines_when_options_or_the_line_change()
    {
        var boom = false;
        var calls = new List<string>();
        var vm = Make(
            [Cand(0, "a"), Cand(1, "b", include: false), Cand(2, "c", notes: [new ConvertNote(ConvertNoteKind.Substitution, "&x")])],
            convert: (_, kind, id, _, options, _) =>
            {
                calls.Add(id);
                if (boom && id == "c2")
                {
                    throw new InvalidOperationException("engine");
                }

                return FakeConvert(kind, id, options.WrapWhere ? [new ConvertNote(ConvertNoteKind.WhereNotWrapped), new ConvertNote(ConvertNoteKind.Substitution, "&x")] : []);
            });
        Assert.Equal("바인드 1 · 문제 없음", vm.Lines[0].NoteText);
        Assert.False(vm.Lines[0].HasWarning);

        calls.Clear();
        vm.WrapWhere = true;
        Assert.Equal("바인드 1 · where 로 감싸지 못함 · 치환 &x 값 검증 필요", vm.Lines[0].NoteText);
        Assert.True(vm.Lines[0].HasWarning);
        // 분석 단계 알림과 겹치는 알림은 한 번만 보이고, 포함하지 않은 줄은 바꿔 보지 않는다.
        Assert.Equal("바인드 1 · 치환 &x 값 검증 필요 · where 로 감싸지 못함", vm.Lines[2].NoteText);
        Assert.DoesNotContain("b", calls);
        Assert.Equal("바인드 1 · 문제 없음", vm.Lines[1].NoteText);

        // 줄을 포함하면 그 줄의 알림이 더해지고, 빼면 다시 분석 단계 알림만 남는다.
        vm.Lines[1].Include = true;
        Assert.Contains("where 로 감싸지 못함", vm.Lines[1].NoteText, StringComparison.Ordinal);
        vm.Lines[1].Include = false;
        Assert.Equal("바인드 1 · 문제 없음", vm.Lines[1].NoteText);

        vm.WrapWhere = false;
        Assert.Equal("바인드 1 · 문제 없음", vm.Lines[0].NoteText);

        // 엔진 예외는 잡고 알림 없음으로 둔다.
        vm.WrapWhere = true;
        boom = true;
        vm.Lines[2].Id = "c2";
        Assert.Equal("바인드 1 · 치환 &x 값 검증 필요", vm.Lines[2].NoteText);
    }

    // (8) 기존 매퍼 읽기는 디바운스하고 백그라운드에서 하며, 낡은 결과는 버린다.
    [Fact]
    public async Task Typing_an_existing_path_is_debounced_and_reads_only_the_latest_path()
    {
        var reads = new List<string>();
        var vm = Make([Cand(0, "a")], p =>
        {
            lock (reads)
            {
                reads.Add(p);
            }

            return new ExistingMapperRead("ns:" + p, []);
        }, readDelay: TimeSpan.FromMilliseconds(150));
        vm.IsExistingMapper = true;
        vm.Path = "/m/O";
        vm.Path = "/m/Ol";
        vm.Path = "/m/Old.xml";
        Assert.Empty(reads); // 아직 읽지 않았다
        Assert.False(vm.CanConfirm);

        await vm.ExistingReadTask;
        Assert.Equal(["/m/Old.xml"], reads);
        Assert.Equal("ns:/m/Old.xml", vm.Namespace);
        Assert.True(vm.CanConfirm);
    }

    [Fact]
    public async Task A_slow_read_does_not_block_the_setter_and_its_stale_result_is_discarded()
    {
        using var started = new ManualResetEventSlim();
        using var gate = new ManualResetEventSlim();
        var vm = Make([Cand(0, "a")], p =>
        {
            if (p == "/m/slow.xml")
            {
                started.Set();
                gate.Wait(TimeSpan.FromSeconds(10));
            }

            return new ExistingMapperRead("ns:" + p, []);
        });
        vm.IsExistingMapper = true;
        vm.Path = "/m/slow.xml";
        var slow = vm.ExistingReadTask;
        Assert.True(started.Wait(TimeSpan.FromSeconds(10)));
        Assert.False(slow.IsCompleted); // 설정 함수가 돌아왔어도 읽기는 아직 진행 중이다
        Assert.False(vm.CanConfirm);

        vm.Path = "/m/fast.xml";
        await vm.ExistingReadTask;
        Assert.Equal("ns:/m/fast.xml", vm.Namespace);

        gate.Set();
        await slow;
        Assert.Equal("ns:/m/fast.xml", vm.Namespace); // 늦게 끝난 앞 경로의 결과는 반영하지 않는다
        Assert.True(vm.CanConfirm);
    }

    // (11) 기존 매퍼 id 를 읽으면 제안 id 를 다시 받는다.
    [Fact]
    public async Task Reading_an_existing_mapper_resuggests_ids_except_hand_edited_ones()
    {
        ConvertCandidate Suggest(int tab, string baseId, IReadOnlyCollection<string> existing) =>
            Cand(tab, existing.Contains(baseId) ? baseId + "2" : baseId);
        IReadOnlyList<ConvertCandidate> Analyze(IReadOnlyCollection<string> existing) =>
            [Suggest(0, "selectOrders", existing), Suggest(1, "findOrder", existing)];

        var vm = Make(Analyze([]), p => new ExistingMapperRead("com.acme.Old", ["selectOrders", "findOrder"]), Analyze);
        vm.Lines[1].Id = "mine";
        Assert.Equal(["selectOrders", "mine"], vm.Lines.Select(l => l.Id));

        vm.IsExistingMapper = true;
        vm.Path = "/m/Old.xml";
        await vm.ExistingReadTask;
        Assert.Equal(["selectOrders2", "mine"], vm.Lines.Select(l => l.Id));
        Assert.All(vm.Lines, l => Assert.Null(l.IdError));
        Assert.True(vm.CanConfirm);

        // 새 파일로 돌아오면 빈 집합으로 다시 제안한다(손으로 고친 줄은 그대로).
        vm.IsNewFile = true;
        Assert.Equal(["selectOrders", "mine"], vm.Lines.Select(l => l.Id));
    }

    // (12) 새 파일의 namespace 는 점으로 이은 Java 식별자여야 한다.
    [Theory]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("1abc", false)]
    [InlineData("com..acme", false)]
    [InlineData("com.acme.", false)]
    [InlineData("com.acme-x.Order", false)]
    [InlineData("com acme", false)]
    [InlineData("Mapper", true)]
    [InlineData("com.acme.Order_Mapper", true)]
    [InlineData("$a._b.C9", true)]
    public void New_file_namespace_must_be_dotted_java_identifiers(string ns, bool valid)
    {
        var vm = Make([Cand(0, "a")]);
        vm.Namespace = ns;
        Assert.Equal(valid, vm.NamespaceError is null);
        Assert.Equal(!valid, vm.HasNamespaceError);
        Assert.Equal(valid, vm.CanConfirm);
        Assert.Equal(valid, vm.TryBuildResult() is not null);
    }

    [Fact]
    public async Task Existing_mapper_namespace_is_not_validated_and_the_namespace_box_shows_the_error_mark()
    {
        var vm = Make([Cand(0, "a")], _ => new ExistingMapperRead(null, []), suggestedNamespace: "");
        Assert.False(vm.CanConfirm);
        vm.IsExistingMapper = true;
        vm.Path = "/m/Old.xml";
        await vm.ExistingReadTask;
        Assert.Null(vm.NamespaceError);
        Assert.True(vm.CanConfirm);

        await HeadlessHost.Run(() =>
        {
            var model = Make([Cand(0, "a")], suggestedNamespace: "");
            var window = new ConvertDialog { DataContext = model };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var box = window.GetVisualDescendants().OfType<TextBox>().First(c => c.Name == "NamespaceBox");
            Assert.Contains("invalid", box.Classes);
            Assert.NotNull(ToolTip.GetTip(box));
            model.Namespace = "com.acme.Ok";
            Dispatcher.UIThread.RunJobs();
            Assert.DoesNotContain("invalid", box.Classes);
            window.Close();
        });
    }

    [Fact]
    public Task Dialog_lays_out_the_table_options_and_icon_browse_button() => HeadlessHost.Run(() =>
    {
        var vm = Make([Cand(0, "a"), Cand(1, "b", include: false)]);
        var window = new ConvertDialog { DataContext = vm };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        T Named<T>(string name)
            where T : Control => window.GetVisualDescendants().OfType<T>().First(c => c.Name == name);

        Assert.Equal(2, Named<ListBox>("LineList").GetVisualDescendants().OfType<ListBoxItem>().Count());
        Assert.True(Named<Button>("ConfirmButton").IsEnabled);
        Assert.Equal("문장 1개 바꾸고 열기", (string?)Named<Button>("ConfirmButton").Content);
        var browse = Named<Button>("BrowseButton");
        Assert.IsType<Viewbox>(browse.Content); // 글자 단추가 아니라 아이콘(ShellIcons 가 만든 Viewbox)이다
        Assert.NotNull(ToolTip.GetTip(browse));
        Assert.True(Named<TextBox>("PreviewBox").IsReadOnly);
        Assert.Contains("<select", Named<TextBox>("PreviewBox").Text, StringComparison.Ordinal);
        Assert.True(Named<RadioButton>("NewFileRadio").IsChecked);
        Assert.True(Named<ListBox>("LineList").Bounds.Height > 0);

        Named<RadioButton>("ExistingRadio").IsChecked = true;
        Dispatcher.UIThread.RunJobs();
        Assert.True(Named<TextBox>("NamespaceBox").IsReadOnly);
        Assert.Equal(ConvertTargetKind.ExistingMapper, vm.Target);
        window.Close();
    });
}
