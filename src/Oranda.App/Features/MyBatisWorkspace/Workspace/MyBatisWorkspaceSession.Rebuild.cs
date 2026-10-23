using Oranda.App.Features.SqlEditor;
using Oranda.Language.MyBatis;

namespace Oranda.App.Features.MyBatisWorkspace.Workspace;

// MyBatisWorkspaceSession 의 일부: 요소 목록 짓기 · 디스크 글 다시 맞추기(MyBatisWorkspaceSession.cs 에서 나눔, 동작 불변).
public sealed partial class MyBatisWorkspaceSession
{
    private void LoadEntries(IMyBatisMapperDocument document)
    {
        _entries.Clear();
        var counts = document.Elements.Where(e => e.Id is not null).GroupBy(e => e.Id!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        foreach (var el in document.Elements)
        {
            bool ambiguous = el.Id is { } id && counts[id] > 1 && IsAmbiguous(document, el);
            _entries.Add(FromFile(document, el, MyBatisStatementEntry.KindOf(el.Kind), el.Id, el.Text, ambiguous));
        }
    }

    /// <summary>
    /// 새 기준 문서로 요소를 다시 짓는다. <paramref name="keepMine"/> 가 참인 고친 요소는 내 상태(원문·id·지움)를 남기고,
    /// 나머지는 새 기준 것이 된다. 열린 탭은 새 요소에 다시 잇고, 사라진 요소의 탭은 닫는다.
    /// <paramref name="takeDisk"/> = 밖에서 바뀐 디스크(5.3): 요소를 기준 id 로 맞댄다. 거짓 = 저장 뒤(Rebase): 저장한 요소는 지금 id 로 맞댄다.
    /// </summary>
    private void Rebuild(IMyBatisMapperDocument document, Func<MyBatisStatementEntry, bool> keepMine, bool takeDisk)
    {
        var old = _entries.ToList();
        var oldSelected = _selected;
        _selected = null;
        _document = document;
        LoadEntries(document);
        var taken = new HashSet<MyBatisStatementEntry>(ReferenceEqualityComparer.Instance);
        foreach (var prev in old)
        {
            bool mine = prev.IsModified && keepMine(prev);
            var lookup = !takeDisk && !mine && prev.IsModified ? prev.Id : prev.OriginalId;

            // 같은 id(databaseId 변형)는 원문 순서대로 하나씩 맞댄다.
            var target = lookup is null ? null
                : _entries.FirstOrDefault(e => !taken.Contains(e) && string.Equals(e.OriginalId, lookup, StringComparison.Ordinal)
                    && (prev.IsNew || e.Kind == prev.Kind));
            if (target is not null)
            {
                taken.Add(target);
            }

            if (mine && !(prev.IsDeleted && target is null))
            {
                if (target is null)
                {
                    // 새 문장(또는 새 기준에서 사라진 문장)은 새 문장으로 남긴다.
                    target = new MyBatisStatementEntry(null, prev.Kind, prev.Id, prev.Text, ambiguous: false, prev.Indent);
                    var at = _entries.FindLastIndex(e => e.Kind == prev.Kind);
                    _entries.Insert(at < 0 ? _entries.Count : at + 1, target);
                    taken.Add(target);
                }
                else
                {
                    target.Id = prev.Id;
                    target.IsDeleted = prev.IsDeleted;
                    target.StoredText = prev.Text;

                    // 내 글은 prev 의 접두로 걷어 낸 글이다: 접두를 물려받아 병합 계획에 쓴 파일 글(FileText) 그대로 쓴다.
                    if (prev.Indent is { } indent && !string.Equals(target.Indent, indent, StringComparison.Ordinal))
                    {
                        target.SetIndent(indent);
                    }
                }
            }

            if (ReferenceEquals(prev, oldSelected) && target is { IsDeleted: false })
            {
                _selected = target;
            }

            if (prev.Tab is not { } tab)
            {
                continue;
            }

            Untrack(prev);
            if (target is null || target.IsDeleted)
            {
                _syncingTabs = true;
                try
                {
                    _workspace.Host.Close(tab);
                }
                finally
                {
                    _syncingTabs = false;
                }

                continue;
            }

            if (!string.Equals(tab.Text, target.StoredText, StringComparison.Ordinal))
            {
                tab.LoadText(target.StoredText);
            }

            Track(target, tab);
        }

        RaiseStateChanged();
    }

    /// <summary>InsertElement 의 AfterId: 앞쪽에서 가장 가까운, 지우지 않았고 id 로 고를 수 있는 기준 요소.</summary>
    private string? InsertAnchor(int index)
    {
        for (int i = index - 1; i >= 0; i--)
        {
            var e = _entries[i];
            if (!e.IsNew && !e.IsDeleted && !e.IsAmbiguous && e.OriginalId is not null)
            {
                return e.OriginalId;
            }
        }

        return null;
    }
}
