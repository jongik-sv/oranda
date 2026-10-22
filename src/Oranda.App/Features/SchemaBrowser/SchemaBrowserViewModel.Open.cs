using Oranda.App.Features.Localization;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;

namespace Oranda.App.Features.SchemaBrowser;

// SchemaBrowserViewModel 의 일부: 객체 열기(미리보기·고정)와 트리에서 찾기(TSK-10-07 에서 나눔, 동작 불변).
public sealed partial class SchemaBrowserViewModel
{
    /// <summary>
    /// 사용자가 노드를 한 번 누르거나 방향키로 골랐을 때 View 가 부른다(코드가 고른 선택 — 트리에서 찾기·필터 다시 읽기 — 는 부르지 않는다).
    /// 설정이 켜져 있고 객체 노드면 잠깐(<see cref="PreviewDelayMilliseconds"/>) 뒤 미리보기 문서로 연다. 포커스는 트리에 남는다.
    /// 앞서 예약한 미리보기는 취소된다.
    /// </summary>
    public void SchedulePreview(SchemaNode? node)
    {
        CancelPendingPreview();
        if (node?.OpenTarget is null || !SingleClickPreview)
        {
            return;
        }

        if (PreviewDelayMilliseconds <= 0)
        {
            _ = OpenNodeAsync(node, pin: false, CancellationToken.None);
            return;
        }

        var cts = _previewDelay = new CancellationTokenSource();
        _ = DelayedPreviewAsync(node, cts.Token);
    }

    private async Task DelayedPreviewAsync(SchemaNode node, CancellationToken ct)
    {
        try
        {
            await Task.Delay(PreviewDelayMilliseconds, ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        // 기다리는 사이 다른 노드로 옮겨 갔으면(코드가 고른 선택 포함) 열지 않는다. 예약 취소 토큰은 읽기에 넘기지 않는다 —
        // 이어서 두 번 누르면 이 미리보기 탭을 고정하는데, 그때 읽던 내용이 끊기면 안 된다(탭을 닫으면 읽기는 opener 가 멈춘다).
        if (ReferenceEquals(SelectedNode, node))
        {
            await OpenNodeAsync(node, pin: false, CancellationToken.None);
        }
    }

    /// <summary>
    /// 두 번 누르기·Enter: 고정 문서로 열고 그 문서로 키보드 포커스를 옮긴다. 예약된 미리보기는 취소하고, 같은 객체의 미리보기가
    /// 이미 열려 있으면 그 탭을 고정한다(탭을 하나 더 만들지 않는다). 연 문서가 있으면 true.
    /// </summary>
    public Task<bool> OpenPinnedAsync(SchemaNode? node, CancellationToken ct = default)
    {
        CancelPendingPreview();
        return OpenNodeAsync(node ?? SelectedNode, pin: true, ct);
    }

    private void CancelPendingPreview()
    {
        _previewDelay?.Cancel();
        _previewDelay?.Dispose();
        _previewDelay = null;
    }

    /// <summary>객체 노드만 연다(스키마·범주·「더 보기」·메시지·열 노드는 열지 않는다). 접속이 없거나 세션이 바쁘면 상태 줄에 알린다.</summary>
    private async Task<bool> OpenNodeAsync(SchemaNode? node, bool pin, CancellationToken ct)
    {
        if (node?.OpenTarget is not { } target)
        {
            return false;
        }

        if (CurrentSession is not { } session)
        {
            StatusText = UiText.Get("Shell.schemaOpen.noSession", "접속이 없어 객체를 열지 않았다");
            return false;
        }

        if (session.State != SessionState.Idle)
        {
            StatusText = session.State == SessionState.Busy
                ? UiText.Get("Shell.schemaOpen.busy", "세션이 실행 중이라 객체를 열지 않았다 — 끝난 뒤 다시 연다")
                : UiText.Get("Shell.schemaOpen.closed", "세션이 끊겨 객체를 열지 않았다");
            return false;
        }

        try
        {
            return await Opener.OpenAsync(target, session, preview: !pin, focus: pin, ct) is not null;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// 객체를 트리에서 찾아 고른다(DataGrip 「Select in Database Tree」): 스키마 → 유형 범주를 트리처럼 지연 로딩으로 펼치고
    /// 노드를 선택한다(아래 속성 창도 선택을 따라간다). 찾으면 true.
    /// </summary>
    public Task<bool> RevealAsync(DbObjectRef target, CancellationToken ct = default) => RevealAsync(target, null, ct);

    /// <summary>
    /// <paramref name="session"/> 을 주면 그 접속 대상(DbKey)의 트리에서 찾는다(지금 보이는 대상과 다르면 트리를 바꾼다).
    /// 이름 필터·유형 필터 등에 가려 못 찾으면 필터를 풀고 한 번 더 찾는다.
    /// </summary>
    public async Task<bool> RevealAsync(DbObjectRef target, ISession? session, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ResolveServices();
        target = target with { SubName = null };

        if (session is not null && (CurrentSession is null || !string.Equals(CurrentSession.DbKey, session.DbKey, StringComparison.Ordinal)))
        {
            await SetSessionAsync(session, ct);
        }
        else
        {
            // 접속 직후 등 트리를 짓는 중이면 끝나기를 기다린다. 도구 창을 방금 만들었으면 트리가 비어 있으니 지금 짓는다.
            await WaitWhileAsync(() => IsBusy, ct);
            if (CurrentSession is null || !Roots.Any(r => r.Kind == SchemaNodeKind.Schema))
            {
                await LoadAsync(ct);
            }
        }

        var node = await FindNodeAsync(target, ct);
        if (node is null && (!_filter.IsEmpty || ActiveSavedFilter is not null))
        {
            await ClearFilterAsync(ct);
            node = await FindNodeAsync(target, ct);
        }

        if (node is null)
        {
            StatusText = $"트리에서 {target.Owner}.{target.Name} 을(를) 찾지 못했다";
            return false;
        }

        for (var p = node.Parent; p is not null; p = p.Parent)
        {
            p.IsExpandedSilently = true;
        }

        SelectedNode = node;
        StatusText = $"{target.Owner}.{target.Name} ({target.ObjectType})";
        RevealRequested?.Invoke(this, node);
        return true;
    }

    /// <summary>스키마 → 범주 → 객체 노드를 찾는다. 범주가 쪽으로 나뉘어 있으면 그 객체가 보일 때까지 「더 보기」를 누른다.</summary>
    private async Task<SchemaNode?> FindNodeAsync(DbObjectRef target, CancellationToken ct)
    {
        var schemas = Roots.Where(r => r.Kind == SchemaNodeKind.Schema).ToList();
        var schema = schemas.FirstOrDefault(r => string.Equals(r.Owner, target.Owner, StringComparison.Ordinal))
            ?? schemas.FirstOrDefault(r => string.Equals(r.Owner, target.Owner, StringComparison.OrdinalIgnoreCase));
        if (schema is null || !await EnsureLoadedAsync(schema, ct))
        {
            return null;
        }

        // 같은 유형 범주를 먼저, 본문(PACKAGE BODY 등)이면 명세 범주를, 그래도 없으면 다른 범주에서 같은 이름을 찾는다.
        var categories = schema.Children.Where(c => c.Kind == SchemaNodeKind.Category && c.CategoryType is not null).ToList();
        var spec = target.ObjectType.EndsWith(" BODY", StringComparison.Ordinal) ? target.ObjectType[..^5] : null;
        var ordered = categories
            .OrderBy(c => string.Equals(c.CategoryType, target.ObjectType, StringComparison.Ordinal) ? 0
                : string.Equals(c.CategoryType, spec, StringComparison.Ordinal) ? 1 : 2)
            .ToList();
        foreach (var cat in ordered)
        {
            var index = IndexOfName(cat.Items, target.Name);
            if (index < 0)
            {
                continue;
            }

            if (!await EnsureLoadedAsync(cat, ct))
            {
                return null;
            }

            while (cat.Shown <= index && cat.Children.LastOrDefault() is { Kind: SchemaNodeKind.More } more)
            {
                ShowMore(more);
            }

            return cat.Children.FirstOrDefault(c => c.Kind == SchemaNodeKind.Item && c.Ref is { } r
                && string.Equals(r.Name, target.Name, StringComparison.Ordinal));
        }

        return null;
    }

    private static int IndexOfName(IReadOnlyList<DbObjectRef> items, string name)
    {
        for (var i = 0; i < items.Count; i++)
        {
            if (string.Equals(items[i].Name, name, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>노드의 자식을 읽어 둔다. 다른 쪽(트리 짓기·사용자 펼침)이 읽는 중이면 끝나기를 기다린다.</summary>
    private async Task<bool> EnsureLoadedAsync(SchemaNode node, CancellationToken ct)
    {
        await WaitWhileAsync(() => node.Loading, ct);
        if (!node.Loaded)
        {
            await ExpandAsync(node, ct);
            await WaitWhileAsync(() => node.Loading, ct);
        }

        return node.Loaded;
    }

    private static async Task WaitWhileAsync(Func<bool> condition, CancellationToken ct)
    {
        for (var i = 0; i < 400 && condition(); i++)
        {
            await Task.Delay(25, ct);
        }
    }
}
