using Oranda.App.Features.SqlEditor;

namespace Oranda.App.Features.MyBatisWorkspace.Editor.Highlighting;

/// <summary>MyBatis xml 을 보여 주는 <see cref="SqlCodeView"/>(변환 미리보기·빠른 고침 미리보기)의 강조 바꾸기.</summary>
internal static class SqlCodeViewMyBatisColors
{
    /// <summary>
    /// 코드 뷰가 건 SQL 강조를 떼고 MyBatis 강조(<see cref="MyBatisSyntaxColorizer"/>)를 건다.
    /// 돌려준 것을 화면이 닫히거나 빠질 때 Dispose 하면 MyBatis 강조가 떨어진다.
    /// </summary>
    public static IDisposable UseMyBatisColors(this SqlCodeView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        view.Colorizer?.Detach();
        return new MyBatisSyntaxColorizer(view.TextEditorControl);
    }
}
