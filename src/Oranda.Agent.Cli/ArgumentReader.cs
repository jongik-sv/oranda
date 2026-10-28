namespace Oranda.Agent.Cli;

internal static class ArgumentReader
{
    /// <summary><c>--name value</c> 꼴 옵션의 값을 돌려준다. 없으면 null.</summary>
    public static string? ReadOption(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.Ordinal))
            {
                return args[i + 1];
            }
        }

        return null;
    }
}
