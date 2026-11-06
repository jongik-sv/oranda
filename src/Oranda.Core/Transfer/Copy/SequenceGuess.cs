namespace Oranda.Core.Transfer.Copy;

/// <summary>이름 규칙으로 키 열에 맞는 시퀀스를 추정한다.</summary>
public static class SequenceGuess
{
    public static IReadOnlyList<SequenceRule> Candidates(string table, IReadOnlyList<string> numericKeyColumns,
                                                         IReadOnlyList<string> sequenceNames)
    {
        var result = new List<SequenceRule>();
        foreach (var col in numericKeyColumns)
        {
            string[] patterns = [$"SEQ_{col}", $"{col}_SEQ", $"{table}_SEQ", $"SEQ_{table}", $"{table}_{col}_SEQ"];
            foreach (var pattern in patterns)
            {
                var hit = sequenceNames.FirstOrDefault(s => string.Equals(s, pattern, StringComparison.OrdinalIgnoreCase));
                if (hit is null) continue;
                result.Add(new SequenceRule(hit, table, col));
                break;
            }
        }
        return result;
    }
}
