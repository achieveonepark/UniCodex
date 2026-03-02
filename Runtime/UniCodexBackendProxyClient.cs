using System;
using System.Threading;
using System.Threading.Tasks;

namespace Achieve.UniCodex
{
    /// <summary>
    /// 모바일/런타임 백엔드 연동용 전송 계약입니다.
    /// 실제 HTTP 호출은 프로젝트별로 구현해 주입합니다.
    /// </summary>
    public interface IUniCodexBackendGateway
    {
        /// <summary>세션 토큰 로그인(검증)을 수행합니다.</summary>
        Task<UniCodexResult> LoginWithSessionTokenAsync(string backendSessionToken, CancellationToken ct = default);
        /// <summary>로그아웃을 수행합니다.</summary>
        Task<UniCodexResult> LogoutAsync(string backendSessionToken, CancellationToken ct = default);
        /// <summary>프롬프트 실행을 수행합니다.</summary>
        Task<UniCodexClientRunResult> RunAsync(string backendSessionToken, UniCodexClientRunRequest request, CancellationToken ct = default);
    }

    /// <summary>
    /// 백엔드 세션 토큰 기반 UniCodex 클라이언트 구현입니다.
    /// </summary>
    public sealed class UniCodexBackendProxyClient : UniCodex.IClient
    {
        private readonly IUniCodexBackendGateway _gateway;
        private readonly object _stateLock = new object();
        private UniCodexAuthState _authState = new UniCodexAuthState
        {
            IsLoggedIn = false,
            UserId = string.Empty,
            DisplayName = string.Empty,
            Provider = "backend-proxy"
        };

        private string _backendSessionToken = string.Empty;

        public UniCodexBackendProxyClient(IUniCodexBackendGateway gateway)
        {
            _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        }

        public bool IsAvailable => true;

        public UniCodexAuthState AuthState
        {
            get
            {
                lock (_stateLock)
                {
                    return new UniCodexAuthState
                    {
                        IsLoggedIn = _authState.IsLoggedIn,
                        UserId = _authState.UserId,
                        DisplayName = _authState.DisplayName,
                        Provider = _authState.Provider
                    };
                }
            }
        }

        public async Task<UniCodexResult> LoginAsync(UniCodexLoginRequest request, CancellationToken ct = default)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.BackendSessionToken))
            {
                return new UniCodexResult
                {
                    Success = false,
                    Message = "BackendSessionToken is required.",
                    ErrorCode = UniCodexErrorCode.InvalidRequest
                };
            }

            var token = request.BackendSessionToken.Trim();
            var result = await _gateway.LoginWithSessionTokenAsync(token, ct).ConfigureAwait(false);
            if (result != null && result.Success)
            {
                lock (_stateLock)
                {
                    _backendSessionToken = token;
                    _authState = new UniCodexAuthState
                    {
                        IsLoggedIn = true,
                        UserId = string.Empty,
                        DisplayName = "UniCodex User",
                        Provider = "backend-proxy"
                    };
                }
            }

            return result ?? new UniCodexResult
            {
                Success = false,
                Message = "Gateway returned null login result.",
                ErrorCode = UniCodexErrorCode.Unknown
            };
        }

        public async Task<UniCodexResult> LogoutAsync(CancellationToken ct = default)
        {
            var token = GetSessionToken();
            if (string.IsNullOrWhiteSpace(token))
            {
                return new UniCodexResult
                {
                    Success = true,
                    Message = "Already logged out.",
                    ErrorCode = UniCodexErrorCode.None
                };
            }

            var result = await _gateway.LogoutAsync(token, ct).ConfigureAwait(false);
            if (result != null && result.Success)
            {
                lock (_stateLock)
                {
                    _backendSessionToken = string.Empty;
                    _authState = new UniCodexAuthState
                    {
                        IsLoggedIn = false,
                        UserId = string.Empty,
                        DisplayName = string.Empty,
                        Provider = "backend-proxy"
                    };
                }
            }

            return result ?? new UniCodexResult
            {
                Success = false,
                Message = "Gateway returned null logout result.",
                ErrorCode = UniCodexErrorCode.Unknown
            };
        }

        public async Task<UniCodexClientRunResult> RunAsync(UniCodexClientRunRequest request, CancellationToken ct = default)
        {
            var token = GetSessionToken();
            if (string.IsNullOrWhiteSpace(token))
            {
                return new UniCodexClientRunResult
                {
                    Success = false,
                    Message = "Not logged in. Call LoginAsync with BackendSessionToken first.",
                    SessionId = string.Empty,
                    ErrorCode = UniCodexErrorCode.Unauthorized
                };
            }

            if (request == null || string.IsNullOrWhiteSpace(request.Prompt))
            {
                return new UniCodexClientRunResult
                {
                    Success = false,
                    Message = "Prompt is required.",
                    SessionId = string.Empty,
                    ErrorCode = UniCodexErrorCode.InvalidRequest
                };
            }

            var result = await _gateway.RunAsync(token, request, ct).ConfigureAwait(false);
            return result ?? new UniCodexClientRunResult
            {
                Success = false,
                Message = "Gateway returned null run result.",
                SessionId = string.Empty,
                ErrorCode = UniCodexErrorCode.Unknown
            };
        }

        private string GetSessionToken()
        {
            lock (_stateLock)
            {
                return _backendSessionToken;
            }
        }
    }
}
