using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Core.Events;
using Oranda.Core.Shell;

namespace Oranda.App.Shell.Docking;

/// <summary>
/// 문서 탭: 열기·미리보기 탭·활성·포커스·닫기(닫기 전 확인 포함)·탭 돌기·문서 영역 나누기.
/// </summary>
public sealed partial class DockWorkspace
{
    /// <summary>문서 탭이 닫혔다(탭의 × 단추·탭 닫기 명령 모두). 문서를 가진 기능이 자기 목록을 맞춘다.</summary>
    public event EventHandler<PanelDocument>? DocumentClosed;

    /// <summary>문서 내용으로 키보드 포커스를 옮겨 달라는 요청(<see cref="FocusDocument"/>). 보기 쪽(<see cref="DockHostView"/>)이 처리한다.</summary>
    public event EventHandler<PanelDocument>? DocumentFocusRequested;

    /// <summary>지금 열린 문서 탭(여러 문서 영역이 있으면 모두).</summary>
    public IReadOnlyList<PanelDocument> Documents =>
        DocumentDocks().SelectMany(d => d.VisibleDockables ?? []).OfType<PanelDocument>().ToList();

    /// <summary>
    /// 문서 탭이 닫히기 전 확인(탭의 × 단추·탭 닫기 명령 모두). 문서를 받아 확인이 필요하면 확인 함수(true = 닫아도 된다)를, 필요 없으면 null 을 돌려준다.
    /// 확인이 필요하면 닫기를 일단 막고 확인을 기다린 뒤 다시 닫는다. Dock 의 닫기 이벤트는 동기라서 비동기 대화상자를 기다릴 수 없기 때문이다.
    /// 확인이 필요 없는 문서는 지금처럼 바로 닫힌다(<see cref="DocumentClosed"/> 시점이 바뀌지 않는다).
    /// </summary>
    public Func<PanelDocument, Func<CancellationToken, Task<bool>>?>? CloseConfirm { get; set; }

    private PanelDocument? _confirmedClose;

    private void OnDockableClosing(object? sender, DockableClosingEventArgs e)
    {
        if (e.Dockable is not PanelDocument document || ReferenceEquals(document, _confirmedClose)
            || CloseConfirm?.Invoke(document) is not { } confirm)
        {
            return;
        }

        e.Cancel = true;
        _ = CloseAfterConfirmAsync(document, confirm);
    }

    /// <summary>확인을 기다린 뒤 닫는다. 닫았거나 이미 닫혀 있으면 true, 취소·실패면 false.</summary>
    private async Task<bool> CloseAfterConfirmAsync(PanelDocument document, Func<CancellationToken, Task<bool>> confirm)
    {
        bool ok;
        try
        {
            ok = await confirm(CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // 확인하다 실패하면 닫지 않는다(미커밋 변경을 잃는 쪽이 더 나쁘다).
            System.Diagnostics.Trace.TraceError("문서 닫기 확인 실패(닫기 취소): {0}", ex);
            ok = false;
        }

        if (!ok || !Documents.Contains(document))
        {
            return ok; // 취소했거나 확인하는 동안 다른 경로로 이미 닫혔다
        }

        _confirmedClose = document;
        try
        {
            CloseDocument(document);
        }
        finally
        {
            _confirmedClose = null;
        }

        return true;
    }

    /// <summary>문서 탭을 연다. 이미 열린 같은 종류(여러 개 불가)가 있으면 그 탭을 활성으로만 한다.</summary>
    public PanelDocument? OpenDocument(string panelId, object? viewModel = null, string? title = null)
    {
        if (_registry.Find(panelId) is not { Kind: ToolPanelKind.Document } def || DocumentDocks().FirstOrDefault() is not { } dock)
        {
            return null;
        }

        if (!def.AllowMultiple && Documents.FirstOrDefault(d => d.Definition.Id == panelId) is { } existing)
        {
            Activate(existing);
            return existing;
        }

        var doc = new PanelDocument(def, $"{panelId}#{++_documentSerial}", title ?? _text.Title(def), viewModel ?? _createViewModel(def));
        Factory.AddDockable(dock, doc);
        Activate(doc);
        return doc;
    }

    /// <summary>지금 미리보기 탭(<see cref="PanelDocument.IsPreview"/>). 없으면 null.</summary>
    public PanelDocument? PreviewDocument => Documents.FirstOrDefault(d => d.IsPreview);

    /// <summary>
    /// 미리보기 탭으로 연다(VS Code 방식). 미리보기 탭이 이미 있으면 그 자리에 새 탭을 넣고 옛 탭을 닫는다 — 탭 수가 늘지 않는다.
    /// 새 탭을 먼저 넣고 옛 탭을 닫으므로 문서가 하나뿐이어도 시작 화면이 끼어 보이지 않는다. 옛 탭을 닫을 때
    /// <see cref="DocumentClosed"/> 가 나가므로 문서를 가진 기능의 정리(열린 커서 닫기 등)가 그대로 돈다.
    /// 여러 개를 열 수 없는 종류는 미리보기 없이 <see cref="OpenDocument"/> 와 같다.
    /// </summary>
    public PanelDocument? OpenPreviewDocument(string panelId, object viewModel, string title)
    {
        if (_registry.Find(panelId) is not { Kind: ToolPanelKind.Document } def || DocumentDocks().FirstOrDefault() is not { } dock)
        {
            return null;
        }

        if (!def.AllowMultiple)
        {
            return OpenDocument(panelId, viewModel, title);
        }

        var doc = new PanelDocument(def, $"{panelId}#{++_documentSerial}", title, viewModel) { IsPreview = true };
        var old = PreviewDocument;
        if (old is { Owner: IDock owner } && owner.VisibleDockables?.IndexOf(old) is int index and >= 0)
        {
            Factory.InsertDockable(owner, doc, index);
            Activate(doc);
            Factory.CloseDockable(old);
        }
        else
        {
            Factory.AddDockable(dock, doc);
        }

        Activate(doc);
        return doc;
    }

    /// <summary>미리보기 탭을 고정한다(다음 미리보기가 이 탭을 바꿔 끼우지 않는다). 이미 고정이면 아무것도 안 한다.</summary>
    public void Pin(PanelDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.IsPreview && Documents.Contains(document))
        {
            document.IsPreview = false;
        }
    }

    /// <summary>문서를 활성으로 하고 그 내용으로 키보드 포커스를 옮겨 달라고 알린다(보기가 화면에 붙은 뒤 옮긴다).</summary>
    public void FocusDocument(PanelDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        Activate(document);
        DocumentFocusRequested?.Invoke(this, document);
    }

    public void Activate(PanelDocument document)
    {
        if (document.Owner is IDock owner)
        {
            Factory.SetActiveDockable(document);
            Factory.SetFocusedDockable(owner, document);
        }
    }

    public bool CloseDocument(PanelDocument document)
    {
        if (!Documents.Contains(document))
        {
            return false;
        }

        Factory.CloseDockable(document);
        ActiveDocumentChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>
    /// 모든 문서를 닫는다. 확인(<see cref="CloseConfirm"/>)이 필요 없는 문서는 바로 닫고, 필요한 문서는 하나씩 차례로 묻는다
    /// (확인 창이 겹치거나 앞 확인이 도는 동안 뒤 문서가 말없이 취소되지 않게). 하나라도 취소하면 남은 문서는 닫지 않는다.
    /// </summary>
    public void CloseAllDocuments()
    {
        var asking = new List<PanelDocument>();
        foreach (var d in Documents.ToList())
        {
            if (CloseConfirm?.Invoke(d) is not null)
            {
                asking.Add(d);
                continue;
            }

            Factory.CloseDockable(d);
        }

        ActiveDocumentChanged?.Invoke(this, EventArgs.Empty);
        if (asking.Count > 0)
        {
            _ = CloseInTurnAsync(asking);
        }
    }

    private async Task CloseInTurnAsync(IReadOnlyList<PanelDocument> documents)
    {
        foreach (var document in documents)
        {
            if (!Documents.Contains(document))
            {
                continue; // 묻는 동안 다른 경로로 이미 닫혔다
            }

            if (CloseConfirm?.Invoke(document) is not { } confirm)
            {
                CloseDocument(document); // 앞 문서를 닫는 사이 확인할 것이 없어졌다
                continue;
            }

            if (!await CloseAfterConfirmAsync(document, confirm).ConfigureAwait(true))
            {
                return; // 취소: 남은 문서는 닫지 않는다
            }
        }
    }

    /// <summary>활성 문서에서 <paramref name="step"/> 칸 옆 탭을 활성으로(끝에서 돌아온다).</summary>
    public void CycleDocument(int step)
    {
        if (ActiveDocument is not { Owner: IDock owner } active)
        {
            return;
        }

        var tabs = (owner.VisibleDockables ?? []).OfType<PanelDocument>().ToList();
        if (tabs.Count < 2)
        {
            return;
        }

        var next = tabs[(tabs.IndexOf(active) + step + tabs.Count) % tabs.Count];
        Activate(next);
    }

    /// <summary>활성 문서를 새 문서 영역으로 분할한다(오른쪽 또는 아래).</summary>
    public bool SplitActiveDocument(bool right)
    {
        if (ActiveDocument is not { } active)
        {
            return false;
        }

        if (right)
        {
            Factory.NewHorizontalDocumentDock(active);
        }
        else
        {
            Factory.NewVerticalDocumentDock(active);
        }

        LayoutChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }
}
