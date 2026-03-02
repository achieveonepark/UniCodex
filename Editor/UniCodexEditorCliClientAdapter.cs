using System;
using System.Threading;
using System.Threading.Tasks;
using Achieve.UniCodex;

namespace Achieve.UniCodex.Editor
{
    /// <summary>
    /// 에디터의 codex CLI 구현을 UniCodex 런타임 Client 계약으로 노출하는 어댑터입니다.
    /// </summary>
    internal sealed class UniCodexEditorCliClientAdapter : UniCodex.IClient
    {
        private readonly Func<UniCodexCliService> _serviceFactory;
        private readonly object _authLock = new object();
        private UniCodexAuthState _authState = new UniCodexAuthState
        {
            IsLoggedIn = false,
            UserId = string.Empty,
            DisplayName = string.Empty,
            Provider = "codex-cli"
        };

        public UniCodexEditorCliClientAdapter(Func<UniCodexCliService> serviceFactory)
        {
            _serviceFactory = serviceFactory ?? throw new ArgumentNullException(nameof(serviceFactory));
        }

        public bool IsAvailable => true;

        public UniCodexAuthState AuthState
        {
            get
            {
                lock (_authLock)
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

        public Task<UniCodexResult> LoginAsync(UniCodexLoginRequest request, CancellationToken ct = default)
        {
            return Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                var service = _serviceFactory();
                var loginRequest = request ?? new UniCodexLoginRequest();
                UniCodexCommandResult command;

                // Editor는 CLI Device Auth가 로그인 표준입니다.
                if (loginRequest.UseDeviceAuth || string.IsNullOrWhiteSpace(loginRequest.BackendSessionToken))
                {
                    command = service.LoginWithDeviceAuth();
                }
                else
                {
                    command = new UniCodexCommandResult
                    {
                        Success = false,
                        Message = "BackendSessionToken login is not supported by the editor CLI adapter."
                    };
                }

                var status = service.QueryLoginStatus();
                SetAuthState(status.Success);

                return new UniCodexResult
                {
                    Success = command.Success,
                    Message = command.Message,
                    ErrorCode = command.Success ? UniCodexErrorCode.None : MapErrorCode(command.Message)
                };
            }, ct);
        }

        public Task<UniCodexResult> LogoutAsync(CancellationToken ct = default)
        {
            return Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                var service = _serviceFactory();
                var result = service.Logout();
                SetAuthState(false);

                return new UniCodexResult
                {
                    Success = result.Success,
                    Message = result.Message,
                    ErrorCode = result.Success ? UniCodexErrorCode.None : MapErrorCode(result.Message)
                };
            }, ct);
        }

        public Task<UniCodexClientRunResult> RunAsync(UniCodexClientRunRequest request, CancellationToken ct = default)
        {
            return Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
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

                var service = _serviceFactory();
                var result = service.Run(
                    request.Prompt,
                    string.IsNullOrWhiteSpace(request.SessionId) ? string.Empty : request.SessionId,
                    request.ProgressCallback);

                if (!result.Success)
                {
                    var code = MapErrorCode(result.Message);
                    if (code == UniCodexErrorCode.Unauthorized)
                    {
                        SetAuthState(false);
                    }

                    return new UniCodexClientRunResult
                    {
                        Success = false,
                        Message = result.Message,
                        SessionId = result.ThreadId ?? string.Empty,
                        InputTokens = result.InputTokens,
                        OutputTokens = result.OutputTokens,
                        TotalTokens = result.TotalTokens,
                        ErrorCode = code
                    };
                }

                return new UniCodexClientRunResult
                {
                    Success = true,
                    Message = result.Message,
                    SessionId = result.ThreadId ?? string.Empty,
                    InputTokens = result.InputTokens,
                    OutputTokens = result.OutputTokens,
                    TotalTokens = result.TotalTokens,
                    ErrorCode = UniCodexErrorCode.None
                };
            }, ct);
        }

        private void SetAuthState(bool isLoggedIn)
        {
            lock (_authLock)
            {
                _authState = new UniCodexAuthState
                {
                    IsLoggedIn = isLoggedIn,
                    UserId = string.Empty,
                    DisplayName = isLoggedIn ? "Codex CLI User" : string.Empty,
                    Provider = "codex-cli"
                };
            }
        }

        private static UniCodexErrorCode MapErrorCode(string message)
        {
            var text = string.IsNullOrWhiteSpace(message) ? string.Empty : message.Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                return UniCodexErrorCode.Unknown;
            }

            if (text.IndexOf("not logged in", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return UniCodexErrorCode.Unauthorized;
            }

            if (text.IndexOf("timed out", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return UniCodexErrorCode.Timeout;
            }

            if (text.IndexOf("not installed", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return UniCodexErrorCode.NotSupportedPlatform;
            }

            if (text.IndexOf("invalid", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return UniCodexErrorCode.InvalidRequest;
            }

            return UniCodexErrorCode.Unknown;
        }
    }
}
