using System.ComponentModel;
using Oranda.App.Features.ChangeBundle.Ai;

namespace Oranda.App.Features.ObjectDetail;

// 테이블 편집 DDL 미리 보기의 「AI 검토」(TSK-15-09, 설계 §4.1). 파티션 전환 스크립트도 미리 보기 글 그대로 검토한다.
// 앱 검사 줄(TableEditFindingRows)은 그대로 두고 AI 는 덧붙인다. 실행 단추를 막지 않는다.
public sealed partial class ObjectDetailViewModel
{
    private DdlReviewViewModel? _ddlReview;
    private bool _ddlReviewHooked;

    /// <summary>시험용: 검토 만들기(기본 = 셸 서비스, 서비스가 없으면 검토 없음).</summary>
    internal Func<Func<DdlReviewInput?>, DdlReviewViewModel?>? DdlReviewFactory { get; set; }

    /// <summary>DDL 미리 보기의 검토. 편집이 바뀌면 새로 만든다(앞 편집의 답을 남기지 않는다). 셸 서비스가 없으면 null(단추를 숨긴다).</summary>
    public DdlReviewViewModel? DdlReview
    {
        get
        {
            if (_ddlReview is null && TableEdit is not null)
            {
                _ddlReview = DdlReviewFactory is { } factory ? factory(DdlReviewInput)
                    : _services is { } services ? DdlReviewShell.Create(services, DdlReviewInput)
                    : null;
                HookDdlReview();
            }

            return _ddlReview;
        }
    }

    public bool HasDdlReview => DdlReview is not null;

    /// <summary>지금 미리 보기의 검토 재료: 접속 · 미리 보기 글 · 앱 검사 줄 · 편집하는 표. 문장이 없으면 null.</summary>
    internal DdlReviewInput? DdlReviewInput()
    {
        if (TableEdit is not { } edit || _session is not { } session || !CanSaveTableEditScript)
        {
            return null;
        }

        var targets = !IsNewTableMode && Target is { } target ? new[] { new DdlReviewTarget(target.Owner, target.Name) } : [];
        var server = edit.Context.Server is { } s ? $"{s.Dbms} {s.Version}".Trim() : null;
        return new DdlReviewInput(session.Profile, edit.PreviewText, [.. TableEditFindingRows.Select(r => r.Text)], targets, server);
    }

    private void HookDdlReview()
    {
        if (_ddlReviewHooked)
        {
            return;
        }

        _ddlReviewHooked = true;
        PropertyChanged += OnDdlReviewContextChanged;
    }

    private void OnDdlReviewContextChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TableEdit))
        {
            _ddlReview = null;
            OnPropertyChanged(nameof(DdlReview));
            OnPropertyChanged(nameof(HasDdlReview));
        }
        else if (e.PropertyName is nameof(CanSaveTableEditScript) or nameof(TableEditFindings))
        {
            _ddlReview?.Refresh();
        }
    }
}
