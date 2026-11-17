namespace Oranda.Core.Schema.TableEdit;

/// <summary>편집 칸에 쓴 이름 → 모델 이름(사전 표기).</summary>
public static class TableEditNames
{
    /// <summary>
    /// 큰따옴표로 감싸 쓰면 안의 글자 그대로(<c>""</c> 는 <c>"</c>), 아니면 앞뒤 공백을 떼고 대문자(따옴표 없는 Oracle 이름 규칙).
    /// 문장은 이 이름을 대문자 규칙을 벗어날 때만 다시 따옴표로 감싼다.
    /// </summary>
    public static string FromInput(string? typed)
    {
        var t = typed?.Trim() ?? string.Empty;
        if (t.Length >= 2 && t[0] == '"' && t[^1] == '"')
        {
            return t[1..^1].Replace("\"\"", "\"", StringComparison.Ordinal);
        }

        return t.ToUpperInvariant();
    }
}
