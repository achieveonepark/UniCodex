using System.Threading;
using System.Threading.Tasks;

namespace Achieve.UniCodex
{
    /// <summary>
    /// Client Provider가 설정되지 않았을 때 사용하는 기본 구현입니다.
    /// </summary>
    internal sealed class UniCodexUnavailableClientApi : UniCodex.IClient
    {
        internal static readonly UniCodexUnavailableClientApi Instance = new UniCodexUnavailableClientApi();

        private static readonly UniCodexAuthState NotLoggedInState = new UniCodexAuthState
        {
            IsLoggedIn = false,
            UserId = string.Empty,
            DisplayName = string.Empty,
            Provider = "none"
        };

        public bool IsAvailable => false;

        public UniCodexAuthState AuthState => NotLoggedInState;

        public Task<UniCodexResult> LoginAsync(UniCodexLoginRequest request, CancellationToken ct = default)
        {
            return Task.FromResult(NotConfiguredResult());
        }

        public Task<UniCodexResult> LogoutAsync(CancellationToken ct = default)
        {
            return Task.FromResult(NotConfiguredResult());
        }

        public Task<UniCodexClientRunResult> RunAsync(UniCodexClientRunRequest request, CancellationToken ct = default)
        {
            return Task.FromResult(new UniCodexClientRunResult
            {
                Success = false,
                Message = "UniCodex client provider is not configured. Configure UniCodex.ConfigureClient(...) first.",
                SessionId = string.Empty,
                InputTokens = null,
                OutputTokens = null,
                TotalTokens = null,
                ErrorCode = UniCodexErrorCode.NotConfigured
            });
        }

        private static UniCodexResult NotConfiguredResult()
        {
            return new UniCodexResult
            {
                Success = false,
                Message = "UniCodex client provider is not configured. Configure UniCodex.ConfigureClient(...) first.",
                ErrorCode = UniCodexErrorCode.NotConfigured
            };
        }
    }
}
