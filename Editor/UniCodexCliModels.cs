namespace Achieve.UniCodex.Editor
{
    /// <summary>
    /// CLI 설치/로그인 상태를 담는 환경 캐시 모델입니다.
    /// </summary>
    internal sealed class UniCodexEnvironmentState
    {
        /// <summary>Codex CLI 실행 파일 사용 가능 여부입니다.</summary>
        public bool Installed;
        /// <summary>현재 Codex CLI 인증 여부입니다.</summary>
        public bool LoggedIn;
        /// <summary>확인된 Codex CLI 버전 문자열입니다.</summary>
        public string VersionText;
        /// <summary>사용자 표시용 로그인 상태 텍스트입니다.</summary>
        public string LoginText;
        /// <summary>명령 실행에 사용된 CLI 경로입니다.</summary>
        public string ResolvedCliPath;
    }

    /// <summary>
    /// 일반 명령 실행 결과 모델입니다.
    /// </summary>
    internal sealed class UniCodexCommandResult
    {
        /// <summary>명령 성공 여부입니다.</summary>
        public bool Success;
        /// <summary>명령 출력 또는 오류 텍스트입니다.</summary>
        public string Message;
    }

    /// <summary>
    /// <c>codex exec</c> 호출 입력 모델입니다.
    /// </summary>
    internal sealed class UniCodexRunRequest
    {
        /// <summary>CLI 실행 경로입니다.</summary>
        public string CliPath;
        /// <summary>Codex CLI 작업 디렉터리입니다.</summary>
        public string WorkingDirectory;
        /// <summary><c>codex exec</c>에 전달할 프롬프트입니다.</summary>
        public string Prompt;
        /// <summary>재개 모드에 사용할 기존 세션 ID입니다.</summary>
        public string SessionId;
        /// <summary><c>--model</c>로 전달할 모델 ID입니다.</summary>
        public string Model;
        /// <summary><c>-c model_reasoning_effort=...</c>로 전달할 추론 강도입니다.</summary>
        public string ModelReasoningEffort;
        /// <summary>프로젝트 로컬 CODEX_HOME 사용 여부입니다.</summary>
        public bool UseProjectCodexHome;
        /// <summary>프로젝트 로컬 CODEX_HOME 경로입니다.</summary>
        public string ProjectCodexHome;
        /// <summary>실행 시 full-auto 모드 사용 여부입니다.</summary>
        public bool FullAuto;
        /// <summary>실행 타임아웃(ms)입니다. 0 이하면 무제한입니다.</summary>
        public int TimeoutMs;
    }

    /// <summary>
    /// <c>codex exec</c> 결과 파싱 모델입니다.
    /// </summary>
    internal sealed class UniCodexRunResult
    {
        /// <summary>실행 성공 여부입니다.</summary>
        public bool Success;
        /// <summary>어시스턴트 응답 또는 파싱된 오류 텍스트입니다.</summary>
        public string Message;
        /// <summary>Codex CLI가 반환한 스레드 ID입니다.</summary>
        public string ThreadId;
        /// <summary>CLI가 보고한 입력 토큰 수입니다.</summary>
        public int? InputTokens;
        /// <summary>CLI가 보고한 출력 토큰 수입니다.</summary>
        public int? OutputTokens;
        /// <summary>CLI가 보고한 총 토큰 수입니다.</summary>
        public int? TotalTokens;

        /// <summary>
        /// 지정한 오류 메시지로 실패 결과를 생성합니다.
        /// </summary>
        public static UniCodexRunResult FromError(string message)
        {
            return new UniCodexRunResult
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
