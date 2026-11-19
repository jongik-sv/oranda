using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Dock.Model.Controls;
using Dock.Model.Core;

namespace Oranda.App.Shell.Docking;

/// <summary>
/// 도구 창과 문서가 같은 칸에 섞인 배치를 제자리로 돌린다(2026-11-20 사용자 신고: 문서 탭 줄에 스키마 브라우저가 들어가 화면이 깨졌다).
/// 섞이는 길은 <see cref="OrandaDockFactory"/> 가 막지만, 막기 전 버전·Dock 의 다른 길로 이미 섞였으면 저장할 때(<see cref="Capture"/>) 고친다:
/// 문서 영역 안의 도구 창은 기본 위치 규칙의 묶음으로, 도구 묶음 안의 문서는 문서 영역으로 옮긴다.
/// 그러지 않으면 도구 창은 DTO 에 자리가 없어 다음 실행에 닫힌 창이 되고, 문서는 작업 공간 저장에서 빠진다.
/// </summary>
public sealed partial class DockWorkspace
{
    private bool _repairingMixed;

    /// <summary>섞인 칸을 고쳤으면 true. 고칠 것이 없으면 아무것도 바꾸지 않는다.</summary>
    internal bool RepairMixedDocks()
    {
        if (_repairingMixed || Layout is not IRootDock root)
        {
            return false;
        }

        var tools = new List<PanelTool>();
        var documents = new List<PanelDocument>();
        void Scan(IDockable node) => Visit(node, d =>
        {
            if (d is IDocumentDock { VisibleDockables: { } inDocuments })
            {
                tools.AddRange(inDocuments.OfType<PanelTool>());
            }
            else if (d is IToolDock { VisibleDockables: { } inTools })
            {
                documents.AddRange(inTools.OfType<PanelDocument>());
            }
        });
        Scan(root);
        var windows = new List<IDockWindow>(); // 섞인 칸이 있던 떠 있는 창(옮기고 비면 닫는다)
        foreach (var window in root.Windows ?? [])
        {
            var before = tools.Count + documents.Count;
            if (window.Layout is { } floating)
            {
                Scan(floating);
            }

            if (tools.Count + documents.Count > before)
            {
                windows.Add(window);
            }
        }

        if (tools.Count == 0 && documents.Count == 0)
        {
            return false;
        }

        Trace.TraceWarning(
            "도킹: 섞인 칸을 제자리로 옮겼다(문서 영역의 도구 창 {0}, 도구 묶음의 문서 {1}).",
            tools.Count == 0 ? "-" : string.Join(", ", tools.Select(t => t.Id)),
            documents.Count == 0 ? "-" : string.Join(", ", documents.Select(d => d.Id)));
        _repairingMixed = true;
        try
        {
            if (DocumentDocks().FirstOrDefault() is { } home)
            {
                foreach (var doc in documents)
                {
                    if (doc.Owner is IDock owner)
                    {
                        Factory.MoveDockable(owner, home, doc, null);
                    }
                }
            }

            foreach (var tool in tools)
            {
                // 보이지 않는 창(DTO 에 없다)으로 세므로 ShowTool 이 문서 칸에서 떼어(DetachElsewhere) 규칙 자리 묶음에 넣는다.
                if (!ShowTool(tool.Id, keepFloating: false))
                {
                    DetachElsewhere(tool); // 등록에서 빠진 창(기능 끔): 넣을 자리가 없으니 닫힌 창으로 둔다
                }
            }

            foreach (var window in windows)
            {
                CloseIfEmpty(window);
            }
        }
        finally
        {
            _repairingMixed = false;
        }

        LayoutChanged?.Invoke(this, System.EventArgs.Empty);
        return true;
    }
}
