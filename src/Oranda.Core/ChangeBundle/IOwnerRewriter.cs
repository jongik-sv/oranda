namespace Oranda.Core.ChangeBundle;

/// <summary>
/// 반영 묶음이 쓰는 DDL 소유자 붙이기·바꾸기와 대상 읽기(설계 9.3·9.4, TE-22·TE-24·TE-31). 구현은 렉서가 있는 <c>Oranda.Language</c> 에 있고
/// (글 고치기는 <c>OwnerRewriter</c>, 이 계약은 그 위의 <c>ChangeBundleOwnerRewriter</c>), Core 는 렉서를 참조하지 않으므로 인터페이스만 둔다.
/// 문자열 리터럴·주석·힌트 안은 건드리지 않는다. 이름 규칙은 변경 일지(<see cref="Journal.ChangeTarget"/>)와 같다:
/// 따옴표 없는 이름은 대문자, 따옴표 이름은 따옴표를 벗겨 그대로.
/// </summary>
public interface IOwnerRewriter
{
    /// <summary>
    /// 소유자 없이 쓴 대상 이름에 <paramref name="currentSchema"/> 를 붙이고, 문장이 바꾸는 객체(소유자·이름)를 읽는다.
    /// 모양을 모르면 원문 그대로 <see cref="OwnerQualifyResult.Understood"/> = false.
    /// </summary>
    OwnerQualifyResult Qualify(string text, string currentSchema);

    /// <summary>
    /// 소유자 한정 이름(<c>SAMPLE.</c> 뒤에 이름이 오는 꼴)의 소유자를 <paramref name="map"/>(옛 소유자 → 새 소유자)대로 바꾼다(저장·실행 창의 「소유자 바꾸기」).
    /// 바꿀 것이 없으면 원문 그대로 <see cref="OwnerRenameResult.Changed"/> = false.
    /// </summary>
    OwnerRenameResult Rewrite(string text, IReadOnlyDictionary<string, string> map);

    /// <summary><c>ALTER SESSION SET CURRENT_SCHEMA = x</c> 이면 x(이름 규칙대로). 아니면 null.</summary>
    string? CurrentSchemaSwitch(string text);
}

/// <summary>
/// <see cref="IOwnerRewriter.Qualify"/> 결과. <see cref="Understood"/> = false 면 <see cref="Text"/> 는 원문이다(묶음은 ⚠ 를 단다).
/// <see cref="TargetOwner"/>·<see cref="TargetName"/> 은 문장이 바꾸는 객체(붙인 뒤 소유자, 이름 규칙대로)이고, 대상을 읽지 못하면 null 이다
/// (그때 묶음은 세션의 현재 스키마를 소유자로 본다). <see cref="Changed"/> = 글이 바뀌었나.
/// </summary>
public sealed record OwnerQualifyResult(string Text, bool Understood, bool Changed, string? TargetOwner, string? TargetName);

/// <summary><see cref="IOwnerRewriter.Rewrite"/> 결과. <see cref="Uncertain"/> = 문장 모양을 몰라 못 고침(원문).</summary>
public sealed record OwnerRenameResult(string Text, bool Changed, bool Uncertain);
