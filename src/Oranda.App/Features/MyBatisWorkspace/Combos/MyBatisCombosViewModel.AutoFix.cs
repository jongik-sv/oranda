using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Oranda.App.Features.MyBatisWorkspace.Combos;

/// <summary>자동 고침 제안 하나(빠른 고침 띠 한 줄).</summary>
public sealed class ComboFixItem
{
    internal ComboFixItem(AutoFixProposal proposal)
    {
        Proposal = proposal;
    }

    internal AutoFixProposal Proposal { get; }

    /// <summary>「&lt;set&gt; 으로 고치기」.</summary>
    public string ActionText => Proposal.Kind == AutoFixKind.WrapSet ? ComboText.FixSet : ComboText.FixWhere;

    /// <summary>「빠른 고침 · 줄 3–9」.</summary>
    public string Heading => ComboText.FixHeading(Proposal.FirstLine, Proposal.LastLine);

    public string Description => Proposal.Kind == AutoFixKind.WrapSet
        ? ComboText.FixSetDescription
        : ComboText.FixWhereDescription;
}

// 자동 고침(MB-07): SET 뒤 if → <set>, WHERE 뒤 if → <where>. 미리보기(고친 원문 + 고친 뒤 검사 결과)를 본 뒤 적용한다.
public sealed partial class MyBatisCombosViewModel
{
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyFixCommand))]
    private ComboFixItem? _previewFix;

    [ObservableProperty]
    private string _previewText = string.Empty;

    [ObservableProperty]
    private string _previewSummary = string.Empty;

    /// <summary>지금 문장 원문에 쓸 수 있는 자동 고침(검사 뒤에 채운다).</summary>
    public ObservableCollection<ComboFixItem> Fixes { get; } = [];

    /// <summary>미리보기 중이다(오른쪽 칸이 고친 원문을 보인다).</summary>
    public bool IsPreviewing => PreviewFix is not null;

    /// <summary>미리보기 칸 제목(「미리보기: &lt;set&gt; 으로 고치기」).</summary>
    public string PreviewTitle => PreviewFix is { } fix ? ComboText.PreviewTitle(fix.ActionText) : string.Empty;

    partial void OnPreviewFixChanged(ComboFixItem? value)
    {
        OnPropertyChanged(nameof(IsPreviewing));
        OnPropertyChanged(nameof(PreviewTitle));
    }

    // 미리보기한 원문과 그 원문 기준 제안. 적용 때 원문이 바뀌었으면 미리보기를 다시 연다.
    private string? _previewSource;
    private AutoFixProposal? _previewProposal;
    private CancellationTokenSource? _previewRun;

    /// <summary>
    /// 제안을 미리 본다: 고친 원문을 보이고, 고친 원문으로 조합을 다시 검사해 결과를 요약한다(편집기는 바꾸지 않는다).
    /// 제안은 검사 때의 원문 기준이므로 지금 원문에서 같은 종류·순번의 제안을 다시 찾는다(없으면 「고칠 곳이 없어짐」).
    /// </summary>
    [RelayCommand]
    public async Task PreviewAsync(ComboFixItem? fix)
    {
        fix ??= Fixes.FirstOrDefault();
        if (fix is null || _session is not { } session || _statementId is not { } id || session.ElementOf(id) is not { } element
            || ComboServices.Runner(_services) is not { } runner)
        {
            return;
        }

        var source = element.Text;
        if (Relocate(fix.Proposal, source) is not { } proposal)
        {
            ClosePreview();
            StatusText = ComboText.FixGone;
            return;
        }

        ClosePreview();
        using var run = new CancellationTokenSource();
        _previewRun = run;
        var fixedText = MyBatisAutoFix.Apply(source, proposal);
        _previewSource = source;
        _previewProposal = proposal;
        PreviewFix = fix;
        PreviewText = fixedText;
        PreviewSummary = ComboText.PreviewChecking;
        try
        {
            var request = new ComboCheckRequest(id, element with { Text = fixedText }, session.Fragments, [], ComboCheckMode.Syntax, null, StrategyIndex == 1 ? 0 : 8);
            var report = await runner.RunAsync(request, null, run.Token).ConfigureAwait(true);
            if (!ReferenceEquals(_previewRun, run) || report.Cancelled)
            {
                return;
            }

            PreviewSummary = report.ErrorCount == 0
                ? ComboText.PreviewAllPass(report.Results.Count)
                : ComboText.PreviewErrorsLeft(report.Results.Count, report.ErrorCount);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (ReferenceEquals(_previewRun, run))
            {
                PreviewSummary = ComboText.CheckFailed(ex.Message);
            }
        }
        finally
        {
            if (ReferenceEquals(_previewRun, run))
            {
                _previewRun = null;
            }
        }
    }

    /// <summary>미리 본 고침을 문장 탭에 적용하고(실행 취소 한 번으로 되돌림) 다시 검사한다. 읽기 전용 문장은 바꾸지 않는다.</summary>
    [RelayCommand(CanExecute = nameof(IsPreviewing))]
    public async Task ApplyFixAsync()
    {
        if (PreviewFix is not { } fix || _previewProposal is not { } proposal || _session is not { } session || _statementId is not { } id
            || session.OpenStatement(id) is not { } tab)
        {
            return;
        }

        if (tab.IsReadOnly)
        {
            StatusText = ComboText.FixReadOnly;
            return;
        }

        // 미리 본 뒤 원문이 바뀌었으면 미리 보지 않은 자리를 바꾸지 않도록 지금 원문으로 미리보기를 다시 연다.
        if (!string.Equals(tab.Text, _previewSource, StringComparison.Ordinal))
        {
            await PreviewAsync(fix).ConfigureAwait(true);
            return;
        }

        tab.Document.Replace(proposal.Range.Start, proposal.Range.Length, proposal.Replacement);
        ClosePreview();
        await CheckAsync(session, id).ConfigureAwait(true);
    }

    /// <summary>미리보기를 닫는다(돌던 미리보기 검사는 취소한다).</summary>
    [RelayCommand]
    public void ClosePreview()
    {
        try
        {
            _previewRun?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 방금 끝난 미리보기 검사.
        }

        _previewRun = null;
        _previewSource = null;
        _previewProposal = null;
        PreviewFix = null;
        PreviewText = string.Empty;
        PreviewSummary = string.Empty;
    }

    // 검사 때 원문 기준 제안을 지금 원문의 같은 종류·같은 순번 제안으로 바꾼다.
    private AutoFixProposal? Relocate(AutoFixProposal proposal, string text)
    {
        int ordinal = Fixes.Select(f => f.Proposal).Where(p => p.Kind == proposal.Kind).TakeWhile(p => !ReferenceEquals(p, proposal)).Count();
        return MyBatisAutoFix.Find(text).Where(p => p.Kind == proposal.Kind).Skip(ordinal).FirstOrDefault();
    }

    // 검사한 원문에 쓸 수 있는 고침으로 띠를 다시 채운다.
    private void RefreshFixes(string statementText)
    {
        ClosePreview();
        Fixes.Clear();
        foreach (var proposal in MyBatisAutoFix.Find(statementText))
        {
            Fixes.Add(new ComboFixItem(proposal));
        }
    }
}
