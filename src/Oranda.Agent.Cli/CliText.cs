namespace Oranda.Agent.Cli;

/// <summary>
/// <c>orandactl</c> 이 내는 글. 오류 JSON 의 <c>error</c> 는 LLM 이 읽으므로 영어 한 문장(계약 §2 와 같은 규칙)이다.
/// </summary>
internal static class CliText
{
    public const string NotRunning = "Oranda is not running, or its agent connection is not open.";

    public const string NoEndpoint =
        "No Oranda window has the external agent connection turned on. In Oranda, open Tools > AI > External agent connection and press Connect.";

    public const string MultipleInstances =
        "More than one Oranda window is connected. Choose one with --instance <pid|label> or the ORANDA_INSTANCE environment variable.";

    public const string GrantExpired =
        "The external agent connection of this Oranda window has expired. Extend or reconnect it in Oranda.";

    public const string PipeRequired = "--pipe <name> is required on this system.";

    public const string SocketPathTooLong =
        "The socket path is longer than this system allows. Set ORANDA_DATA_DIR to a shorter folder and reconnect.";

    public const string BadResponse = "The response from Oranda could not be read.";

    public static string InstanceNotFound(string instance) =>
        $"No connected Oranda window matches --instance '{instance}'. Run 'orandactl status --all' to see the windows.";
}
