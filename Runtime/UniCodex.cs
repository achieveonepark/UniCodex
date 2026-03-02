using System.Threading;
using System.Threading.Tasks;

namespace Achieve.UniCodex
{
    /// <summary>
    /// UniCodex 런타임 진입점입니다.
    /// 개발자는 UniCodex.Data / UniCodex.Client 경유로 기능을 호출합니다.
    /// </summary>
    public static class UniCodex
    {
        private static IData _data = new UniCodexDefaultDataApi();
        private static IClient _client = UniCodexUnavailableClientApi.Instance;

        /// <summary>데이터 테이블 API입니다.</summary>
        public static IData Data => _data;

        /// <summary>로그인/실행 클라이언트 API입니다.</summary>
        public static IClient Client => _client;

        /// <summary>데이터 API 구현을 교체합니다.</summary>
        public static void ConfigureData(IData dataApi)
        {
            _data = dataApi ?? new UniCodexDefaultDataApi();
        }

        /// <summary>클라이언트 API 구현을 교체합니다.</summary>
        public static void ConfigureClient(IClient clientApi)
        {
            _client = clientApi ?? UniCodexUnavailableClientApi.Instance;
        }

        /// <summary>기본 구현으로 되돌립니다.</summary>
        public static void ResetToDefaults()
        {
            _data = new UniCodexDefaultDataApi();
            _client = UniCodexUnavailableClientApi.Instance;
        }

        /// <summary>
        /// CSV 데이터테이블 관련 API 계약입니다.
        /// </summary>
        public interface IData
        {
            /// <summary>CSV 테이블을 로드합니다.</summary>
            UniCodexCsvTable LoadCsv(string tableName, bool reload = false);
            /// <summary>CSV 테이블 로드를 시도합니다.</summary>
            bool TryLoadCsv(string tableName, out UniCodexCsvTable table, bool reload = false);
            /// <summary>CSV 캐시를 비웁니다.</summary>
            void ClearCsvCache();
        }

        /// <summary>
        /// 로그인/실행 클라이언트 API 계약입니다.
        /// </summary>
        public interface IClient
        {
            /// <summary>현재 사용 가능한 구현인지 여부입니다.</summary>
            bool IsAvailable { get; }
            /// <summary>현재 인증 상태입니다.</summary>
            UniCodexAuthState AuthState { get; }
            /// <summary>로그인을 수행합니다.</summary>
            Task<UniCodexResult> LoginAsync(UniCodexLoginRequest request, CancellationToken ct = default);
            /// <summary>로그아웃을 수행합니다.</summary>
            Task<UniCodexResult> LogoutAsync(CancellationToken ct = default);
            /// <summary>프롬프트 실행을 수행합니다.</summary>
            Task<UniCodexClientRunResult> RunAsync(UniCodexClientRunRequest request, CancellationToken ct = default);
        }
    }
}
