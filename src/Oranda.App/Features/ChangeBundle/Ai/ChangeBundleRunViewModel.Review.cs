using System.ComponentModel;
using Oranda.App.Features.ChangeBundle.Ai;

namespace Oranda.App.Features.ChangeBundle;

// 묶음 실행 창의 「AI 검토」(TSK-15-09, 설계 §4.1): 실행 단추를 막지 않는 조언. 앱 검사 줄(FindingLines)은 그대로 두고 AI 는 덧붙인다.
public sealed partial class ChangeBundleRunViewModel
{
    private DdlReviewViewModel? _review;

    /// <summary>검토(셸이 <see cref="AttachReview"/> 로 붙인다). 없으면 「AI 검토」 를 숨긴다.</summary>
    public DdlReviewViewModel? Review => _review;

    public bool HasReview => _review is not null;

    /// <summary>검토를 붙인다. 접속을 바꾸거나 실행 상태가 바뀌면 입구 단추를 다시 계산한다.</summary>
    internal void AttachReview(Func<ChangeBundleRunViewModel, DdlReviewViewModel> create)
    {
        ArgumentNullException.ThrowIfNull(create);
        _review = create(this);
        PropertyChanged += OnReviewContextChanged;
        OnPropertyChanged(nameof(Review));
        OnPropertyChanged(nameof(HasReview));
    }

    /// <summary>
    /// 지금 화면의 검토 재료: 고른 접속 · 체크한 문장(소유자 바꾸기 뒤, 화면 순서) · 앱 검사 줄 · 바꾸는 객체(소유자 바꾸기 뒤).
    /// 고를 수 없는 접속이거나 문장이 없으면 null.
    /// </summary>
    internal DdlReviewInput? ReviewInput()
    {
        if (SelectedConnection is not { IsEnabled: true } connection || _items.Count == 0)
        {
            return null;
        }

        var map = OwnerMapRow.ToMap(OwnerMap);
        var prepared = ChangeBundleScript.Prepare(_items, map, _rewriter, _language);
        var script = string.Join(Environment.NewLine + "/" + Environment.NewLine, prepared.Select(p => p.Statement.Text.Trim()));
        var targets = _items
            .Where(i => !string.IsNullOrWhiteSpace(i.TargetName))
            .Select(i =>
            {
                var owner = string.IsNullOrWhiteSpace(i.TargetOwner) ? connection.Session.Profile.User.ToUpperInvariant() : i.TargetOwner!;
                return new DdlReviewTarget(map.TryGetValue(owner, out var to) ? to : owner, i.TargetName!);
            })
            .DistinctBy(t => t.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var server = connection.Session.Server is { } s ? $"{s.Dbms} {s.Version}".Trim() : null;
        return new DdlReviewInput(Route(connection.Session).Profile, script, [.. FindingLines], targets, server);
    }

    private void OnReviewContextChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SelectedConnection) or nameof(IsRunning) or nameof(HasFindings))
        {
            _review?.Refresh();
        }
    }
}
