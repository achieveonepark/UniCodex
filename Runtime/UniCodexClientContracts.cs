using System;

namespace Achieve.UniCodex
{
    /// <summary>
    /// UniCodex 공통 오류 코드입니다.
    /// </summary>
    public enum UniCodexErrorCode
    {
        None = 0,
        NotConfigured = 1,
        NotSupportedPlatform = 2,
        Unauthorized = 3,
        Timeout = 4,
        BackendUnavailable = 5,
        InvalidRequest = 6,
        Unknown = 99
    }

    /// <summary>
    /// UniCodex 일반 실행 결과 모델입니다.
    /// </summary>
    [Serializable]
    public sealed class UniCodexResult
    {
        /// <summary>실행 성공 여부입니다.</summary>
        public bool Success;
        /// <summary>사용자 노출용 메시지입니다.</summary>
        public string Message;
        /// <summary>표준 오류 코드입니다.</summary>
        public UniCodexErrorCode ErrorCode = UniCodexErrorCode.None;
    }

    /// <summary>
    /// UniCodex 로그인 요청 모델입니다.
    /// </summary>
    [Serializable]
    public sealed class UniCodexLoginRequest
    {
        /// <summary>
        /// 모바일/런타임에서는 백엔드 세션 토큰을 사용합니다.
        /// 에디터 디바이스 로그인에서는 비워둘 수 있습니다.
        /// </summary>
        public string BackendSessionToken;

        /// <summary>
        /// 에디터의 codex device-auth 로그인 호출 여부입니다.
        /// </summary>
        public bool UseDeviceAuth;
    }

    /// <summary>
    /// UniCodex 인증 상태 모델입니다.
    /// </summary>
    [Serializable]
    public sealed class UniCodexAuthState
    {
        /// <summary>로그인 여부입니다.</summary>
        public bool IsLoggedIn;
        /// <summary>사용자 식별자(선택)입니다.</summary>
        public string UserId;
        /// <summary>표시 이름(선택)입니다.</summary>
        public string DisplayName;
        /// <summary>인증 공급자 식별자(예: codex-cli, backend-proxy)입니다.</summary>
        public string Provider;
    }

    /// <summary>
    /// UniCodex 클라이언트 실행 요청 모델입니다.
    /// </summary>
    [Serializable]
    public sealed class UniCodexClientRunRequest
    {
        /// <summary>실행할 프롬프트입니다.</summary>
        public string Prompt;
        /// <summary>세션 재개용 세션 ID입니다.</summary>
        public string SessionId;
        /// <summary>모델 ID(선택)입니다.</summary>
        public string Model;
        /// <summary>추론 강도(선택)입니다.</summary>
        public string ReasoningEffort;
        /// <summary>full-auto 모드 여부입니다.</summary>
        public bool FullAuto;
        /// <summary>진행 텍스트 콜백(선택)입니다.</summary>
        public Action<string> ProgressCallback;
    }

    /// <summary>
    /// UniCodex 클라이언트 실행 결과 모델입니다.
    /// </summary>
    [Serializable]
    public sealed class UniCodexClientRunResult
    {
        /// <summary>실행 성공 여부입니다.</summary>
        public bool Success;
        /// <summary>응답 본문 또는 오류 메시지입니다.</summary>
        public string Message;
        /// <summary>세션 ID입니다.</summary>
        public string SessionId;
        /// <summary>입력 토큰 수(선택)입니다.</summary>
        public int? InputTokens;
        /// <summary>출력 토큰 수(선택)입니다.</summary>
        public int? OutputTokens;
        /// <summary>총 토큰 수(선택)입니다.</summary>
        public int? TotalTokens;
        /// <summary>오류 코드입니다.</summary>
        public UniCodexErrorCode ErrorCode = UniCodexErrorCode.None;
    }
}
