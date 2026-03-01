namespace Achieve.UniCodex
{
    /// <summary>
    /// Cached environment state for CLI availability and login status.
    /// </summary>
    internal sealed class EnvironmentState
    {
        public bool Installed;
        public bool LoggedIn;
        public string VersionText;
        public string LoginText;
        public string ResolvedCliPath;
    }

    /// <summary>
    /// Generic command execution result.
    /// </summary>
    internal sealed class CommandResult
    {
        public bool Success;
        public string Message;
    }

    /// <summary>
    /// Input payload for codex exec invocation.
    /// </summary>
    internal sealed class CodexRunRequest
    {
        public string CliPath;
        public string WorkingDirectory;
        public string Prompt;
        public string SessionId;
        public bool UseProjectCodexHome;
        public string ProjectCodexHome;
        public bool FullAuto;
        public int TimeoutMs;
    }

    /// <summary>
    /// Parsed output of a codex exec call.
    /// </summary>
    internal sealed class CodexRunResult
    {
        public bool Success;
        public string Message;
        public string ThreadId;
        public int? InputTokens;
        public int? OutputTokens;
        public int? TotalTokens;

        public static CodexRunResult FromError(string message)
        {
            return new CodexRunResult
            {
                Success = false,
                Message = message,
                ThreadId = string.Empty,
                InputTokens = null,
                OutputTokens = null,
                TotalTokens = null
            };
        }
    }
}
