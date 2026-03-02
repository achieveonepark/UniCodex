namespace Achieve.UniCodex.Editor
{
    /// <summary>
    /// Codex 채팅 에디터 통합에서 공용으로 사용하는 상수 모음입니다.
    /// </summary>
    internal static class UniCodexCliConstants
    {
        /// <summary>창 상태 저장용 EditorPrefs 키 접두사입니다.</summary>
        public const string PrefPrefix = "UniCodex.CodexChat.";

        /// <summary>기본 Codex CLI 명령 이름입니다.</summary>
        public const string DefaultCliPath = "codex";
        /// <summary>macOS 번들 Codex CLI 경로입니다.</summary>
        public const string MacBundledCliPath = "/Applications/Codex.app/Contents/Resources/codex";

        /// <summary>기본 프로젝트 상대 CODEX_HOME 폴더입니다.</summary>
        public const string DefaultCodexHomeRelative = ".codex-unity";
        /// <summary>기본 마크다운 컨텍스트 파일 목록입니다.</summary>
        public const string DefaultMarkdownFiles = "AGENTS.md";
        /// <summary>파일당 기본 마크다운 컨텍스트 길이 제한입니다.</summary>
        public const int DefaultMaxMarkdownChars = 3000;
        /// <summary>기본 실행 타임아웃(ms)입니다. 0 이하면 무제한입니다.</summary>
        public const int DefaultExecTimeoutMs = 0;
        /// <summary>세션당 기본 추정 토큰 예산입니다.</summary>
        public const int DefaultSessionTokenBudget = 200000;
        /// <summary>프로젝트 로컬 채팅 이력 캐시 파일명입니다.</summary>
        public const string ChatHistoryFileName = "CodexChatHistory.json";
        /// <summary>프로젝트 로컬 Unity Action Bridge 파일명입니다.</summary>
        public const string UnityActionFileName = "CodexUnityActions.json";

        /// <summary>검색 순서대로 시도할 Codex CLI 실행 경로 목록입니다.</summary>
        public static readonly string[] CliCandidates =
        {
            DefaultCliPath,
            MacBundledCliPath,
            "/opt/homebrew/bin/codex",
            "/usr/local/bin/codex"
        };
    }
}
