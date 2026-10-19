using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;

namespace Oranda.App.Features.SchemaBrowser;

/// <summary>속성 창의 탭 하나: 객체 상세의 구역(표) 또는 정의(DDL, 고를 때 읽는다).</summary>
public sealed partial class PropertyTab : ObservableObject
{
    public PropertyTab(string title, IReadOnlyList<string> columns, IReadOnlyList<IReadOnlyList<object?>> rows)
    {
        Title = title;
        Columns = columns;
        Rows = rows;
    }

    private PropertyTab(string title)
    {
        Title = title;
        Columns = [];
        Rows = [];
        IsDefinition = true;
    }

    /// <summary>정의(DDL) 탭.</summary>
    public static PropertyTab Definition() => new(SchemaPropertiesViewModel.DefinitionTitle);

    public string Title { get; }

    public IReadOnlyList<string> Columns { get; }

    public IReadOnlyList<IReadOnlyList<object?>> Rows { get; }

    public bool IsDefinition { get; }

    public bool IsTable => !IsDefinition;

    /// <summary>정의 탭의 DDL(아직 안 읽었으면 null).</summary>
    [ObservableProperty]
    private string? _ddlText;
}

/// <summary>
/// 스키마 브라우저 아래 속성 창(오렌지 Property 창). 트리에서 객체를 고르면 잠깐 기다렸다가(빠르게 옮겨 다닐 때 조회가 쌓이지 않게)
/// 그 객체의 상세 구역을 탭으로 보인다. 탭 순서는 오렌지처럼 열·일반·설명·… 이고 마지막에 정의(DDL)가 온다. 정의는 그 탭을 고를 때만 읽는다.
/// 다른 객체로 옮겨도 보던 탭 이름이 있으면 그 탭을 유지한다.
/// </summary>
public sealed partial class SchemaPropertiesViewModel : ObservableObject, IDisposable
{
    public const string DefinitionTitle = "정의";

    /// <summary>오렌지 Property 창의 탭 순서(Column, General, Comment, …, Segment). 목록에 없는 구역은 뒤에 붙는다.</summary>
    private static readonly string[] Order = ["열", "일반", "설명", "제약", "인덱스", "통계", "크기", "파티션", "권한", "의존"];

    private readonly Func<IMetadataProvider?> _meta;
    private readonly Func<ISession?> _session;
    private CancellationTokenSource? _cts;
    private int _generation;

    public SchemaPropertiesViewModel(Func<IMetadataProvider?> meta, Func<ISession?> session)
    {
        _meta = meta;
        _session = session;
    }

    /// <summary>고른 객체를 보이기 전에 기다리는 시간(시험은 0).</summary>
    public TimeSpan Delay { get; set; } = TimeSpan.FromMilliseconds(250);

    public ObservableCollection<PropertyTab> Tabs { get; } = [];

    public DbObjectRef? Target { get; private set; }

    [ObservableProperty]
    private PropertyTab? _selectedTab;

    [ObservableProperty]
    private string _header = "객체를 고르면 속성이 여기에 보입니다.";

    [ObservableProperty]
    private bool _isBusy;

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    /// <summary>객체를 보인다(null 이면 비운다). 앞서 부른 것은 취소한다.</summary>
    public async Task ShowAsync(DbObjectRef? target)
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        var gen = ++_generation;
        if (target is null)
        {
            Target = null;
            Tabs.Clear();
            Header = "객체를 고르면 속성이 여기에 보입니다.";
            return;
        }

        try
        {
            if (Delay > TimeSpan.Zero)
            {
                await Task.Delay(Delay, ct).ConfigureAwait(true);
            }

            if (_meta() is not { } meta || _session() is not { } session)
            {
                Header = "접속하면 속성을 볼 수 있습니다.";
                return;
            }

            IsBusy = true;
            Header = $"{target.ObjectType} {target.Owner}.{target.Name}";
            var detail = await meta.GetObjectDetailAsync(session, target, ct).ConfigureAwait(true);
            if (gen != _generation)
            {
                return;
            }

            var keep = SelectedTab?.Title;
            Target = target;
            Tabs.Clear();
            foreach (var s in detail.Sections.OrderBy(s => Rank(s.Title)))
            {
                Tabs.Add(new PropertyTab(s.Title, s.Columns, s.Rows));
            }

            Tabs.Add(PropertyTab.Definition());
            SelectedTab = Tabs.FirstOrDefault(t => t.Title == keep) ?? Tabs.FirstOrDefault();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (gen == _generation)
            {
                Tabs.Clear();
                Header = $"{target.Owner}.{target.Name}: 속성을 읽지 못했습니다 — {ex.Message}";
            }
        }
        finally
        {
            if (gen == _generation)
            {
                IsBusy = false;
            }
        }
    }

    partial void OnSelectedTabChanged(PropertyTab? value)
    {
        if (value is { IsDefinition: true, DdlText: null })
        {
            _ = LoadDefinitionAsync(value, _generation);
        }
    }

    private async Task LoadDefinitionAsync(PropertyTab tab, int gen)
    {
        if (Target is not { } target || _meta() is not { } meta || _session() is not { } session)
        {
            return;
        }

        tab.DdlText = "-- 정의를 읽는 중…";
        try
        {
            var ddl = await meta.GetDdlAsync(session, target, new DdlOptions(), _cts?.Token ?? default).ConfigureAwait(true);
            if (gen == _generation)
            {
                tab.DdlText = ddl;
            }
        }
        catch (OperationCanceledException)
        {
            tab.DdlText = null;
        }
        catch (Exception ex)
        {
            if (gen == _generation)
            {
                tab.DdlText = "-- 정의를 읽지 못했습니다: " + ex.Message;
            }
        }
    }

    private static int Rank(string title)
    {
        var i = Array.IndexOf(Order, title);
        return i < 0 ? Order.Length : i;
    }
}
