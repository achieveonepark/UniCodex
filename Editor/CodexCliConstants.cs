namespace Achieve.UniCodex
{
    /// <summary>
    /// Shared constants used by the Codex chat editor integration.
    /// </summary>
    internal static class CodexCliConstants
    {
        // EditorPrefs key prefix for persisted window state.
        public const string PrefPrefix = "UniCodex.CodexChat.";

        // Codex CLI executable defaults.
        public const string DefaultCliPath = "codex";
        public const string MacBundledCliPath = "/Applications/Codex.app/Contents/Resources/codex";

        // Project-level defaults.
        public const string DefaultCodexHomeRelative = ".codex-unity";
        public const string DefaultMarkdownFiles = "AGENTS.md";
        public const int DefaultMaxMarkdownChars = 3000;
        // 0 or less means "no timeout" for codex exec turns.
        public const int DefaultExecTimeoutMs = 0;
        public const int DefaultSessionTokenBudget = 200000;
        public const string ChatHistoryFileName = "CodexChatHistory.json";
        public const string UnityActionFileName = "CodexUnityActions.json";

        // Candidate executable paths searched in order.
        public static readonly string[] CliCandidates =
        {
            DefaultCliPath,
            MacBundledCliPath,
            "/opt/homebrew/bin/codex",
            "/usr/local/bin/codex"
        };
    }
}
