namespace Achieve.UniCodex
{
    /// <summary>
    /// UniCodex 기본 CSV 데이터 API 구현입니다.
    /// </summary>
    public sealed class UniCodexDefaultDataApi : UniCodex.IData
    {
        /// <summary>
        /// CSV 테이블을 로드합니다.
        /// </summary>
        public UniCodexCsvTable LoadCsv(string tableName, bool reload = false)
        {
            return UniCodexCsvDataTableProvider.Load(tableName, reload);
        }

        /// <summary>
        /// CSV 테이블 로드를 시도합니다.
        /// </summary>
        public bool TryLoadCsv(string tableName, out UniCodexCsvTable table, bool reload = false)
        {
            return UniCodexCsvDataTableProvider.TryLoad(tableName, out table, reload);
        }

        /// <summary>
        /// CSV 캐시를 비웁니다.
        /// </summary>
        public void ClearCsvCache()
        {
            UniCodexCsvDataTableProvider.ClearCache();
        }
    }
}
