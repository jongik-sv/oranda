
namespace Oranda.App.Features.DataCopy;

/// <summary>상세 칸 「대상 계정」 콤보의 한 줄. 고르면 칸 글자가 <see cref="Owner"/> 가 된다(<see cref="ToString"/>).</summary>
public sealed record TargetOwnerOption(string Owner, string Hint)
{
    /// <summary>그 규칙의 기본 대상(<see cref="RuleEditorViewModel.DefaultTargetFor(TableRuleViewModel)"/>)인지. 고르면 다시 기본 계정을 따른다.</summary>
    public bool IsDefault { get; init; }

    public override string ToString() => Owner;
}
