using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace Achieve.UniCodex
{
    /// <summary>
    /// CSV 데이터테이블 한 개를 표현하는 읽기 전용 뷰입니다.
    /// </summary>
    public sealed class UniCodexCsvTable
    {
        private readonly IReadOnlyList<string> _columns;
        private readonly IReadOnlyList<UniCodexCsvRowView> _rows;
        private readonly Dictionary<string, UniCodexCsvRowView> _rowById;

        internal UniCodexCsvTable(string name, List<string> columns, List<UniCodexCsvRowView> rows)
        {
            Name = name ?? string.Empty;
            _columns = (columns ?? new List<string>()).AsReadOnly();
            _rows = (rows ?? new List<UniCodexCsvRowView>()).AsReadOnly();
            _rowById = new Dictionary<string, UniCodexCsvRowView>(StringComparer.Ordinal);

            for (var i = 0; i < _rows.Count; i++)
            {
                var row = _rows[i];
                if (row == null)
                {
                    continue;
                }

                _rowById[row.Id] = row;
            }
        }

        /// <summary>테이블 이름(파일명 기준, 확장자 제외)입니다.</summary>
        public string Name { get; }

        /// <summary>CSV 헤더 컬럼 목록입니다.</summary>
        public IReadOnlyList<string> Columns => _columns;

        /// <summary>CSV 데이터 행 목록입니다.</summary>
        public IReadOnlyList<UniCodexCsvRowView> Rows => _rows;

        /// <summary>id로 행을 조회합니다.</summary>
        public bool TryGetRow(string id, out UniCodexCsvRowView row)
        {
            row = null;
            if (string.IsNullOrWhiteSpace(id))
            {
                return false;
            }

            return _rowById.TryGetValue(id.Trim(), out row);
        }
    }

    /// <summary>
    /// CSV 한 행의 컬럼 접근/형 변환을 제공하는 읽기 전용 뷰입니다.
    /// </summary>
    public sealed class UniCodexCsvRowView
    {
        private readonly Dictionary<string, string> _valuesByColumn;

        internal UniCodexCsvRowView(string id, Dictionary<string, string> valuesByColumn)
        {
            Id = id ?? string.Empty;
            _valuesByColumn = valuesByColumn ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>행의 유일키 id 값입니다.</summary>
        public string Id { get; }

        /// <summary>문자열 컬럼 값을 반환합니다. 컬럼이 없으면 fallback을 반환합니다.</summary>
        public string GetString(string column, string fallback = "")
        {
            if (string.IsNullOrWhiteSpace(column))
            {
                return fallback;
            }

            return _valuesByColumn.TryGetValue(column.Trim(), out var value)
                ? value ?? fallback
                : fallback;
        }

        /// <summary>정수 컬럼 값을 반환합니다. 파싱 실패 시 fallback을 반환합니다.</summary>
        public int GetInt(string column, int fallback = 0)
        {
            var raw = GetString(column, null);
            return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                ? value
                : fallback;
        }

        /// <summary>실수 컬럼 값을 반환합니다. 파싱 실패 시 fallback을 반환합니다.</summary>
        public float GetFloat(string column, float fallback = 0f)
        {
            var raw = GetString(column, null);
            return float.TryParse(raw, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var value)
                ? value
                : fallback;
        }

        /// <summary>불리언 컬럼 값을 반환합니다. 파싱 실패 시 fallback을 반환합니다.</summary>
        public bool GetBool(string column, bool fallback = false)
        {
            var raw = GetString(column, null);
            if (string.IsNullOrWhiteSpace(raw))
            {
                return fallback;
            }

            var trimmed = raw.Trim();
            if (bool.TryParse(trimmed, out var parsed))
            {
                return parsed;
            }

            if (trimmed == "1")
            {
                return true;
            }

            if (trimmed == "0")
            {
                return false;
            }

            return fallback;
        }
    }

    /// <summary>
    /// Resources/DataTables 경로의 CSV를 로드/파싱/캐시하는 런타임 제공자입니다.
    /// </summary>
    public static class UniCodexCsvDataTableProvider
    {
        private static readonly object CacheLock = new object();
        private static readonly Dictionary<string, UniCodexCsvTable> TableCache =
            new Dictionary<string, UniCodexCsvTable>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 테이블을 로드합니다. 실패 시 예외를 던집니다.
        /// </summary>
        public static UniCodexCsvTable Load(string tableName, bool reload = false)
        {
            if (TryLoad(tableName, out var table, reload))
            {
                return table;
            }

            throw new InvalidOperationException(
                $"Failed to load CSV table `{tableName}`. Check Resources/DataTables/{tableName}.csv and parser errors in logs.");
        }

        /// <summary>
        /// 테이블 로드를 시도합니다. 실패 시 false를 반환하며 콘솔에 상세 에러를 남깁니다.
        /// </summary>
        public static bool TryLoad(string tableName, out UniCodexCsvTable table, bool reload = false)
        {
            table = null;
            var normalizedTableName = NormalizeTableName(tableName);
            if (string.IsNullOrWhiteSpace(normalizedTableName))
            {
                Debug.LogError("UniCodexCsvDataTableProvider: tableName is empty.");
                return false;
            }

            lock (CacheLock)
            {
                if (!reload && TableCache.TryGetValue(normalizedTableName, out var cached))
                {
                    table = cached;
                    return true;
                }
            }

            var resourcePath = $"DataTables/{normalizedTableName}";
            var textAsset = Resources.Load<TextAsset>(resourcePath);
            if (textAsset == null)
            {
                Debug.LogError(
                    $"UniCodexCsvDataTableProvider: table not found at Resources path `{resourcePath}` (file expected: Assets/Resources/{resourcePath}.csv).");
                return false;
            }

            UniCodexCsvTable parsedTable;
            try
            {
                parsedTable = ParseTable(normalizedTableName, textAsset.text);
            }
            catch (Exception ex)
            {
                Debug.LogError($"UniCodexCsvDataTableProvider: parse failed for `{normalizedTableName}`. {ex.Message}");
                return false;
            }

            lock (CacheLock)
            {
                TableCache[normalizedTableName] = parsedTable;
            }

            table = parsedTable;
            return true;
        }

        /// <summary>
        /// 내부 캐시를 비웁니다.
        /// </summary>
        public static void ClearCache()
        {
            lock (CacheLock)
            {
                TableCache.Clear();
            }
        }

        private static string NormalizeTableName(string tableName)
        {
            if (string.IsNullOrWhiteSpace(tableName))
            {
                return string.Empty;
            }

            var trimmed = tableName.Trim();
            var withoutExtension = Path.GetFileNameWithoutExtension(trimmed);
            return string.IsNullOrWhiteSpace(withoutExtension) ? string.Empty : withoutExtension;
        }

        private static UniCodexCsvTable ParseTable(string tableName, string csvText)
        {
            var records = ParseCsvRecords(csvText);
            if (records.Count == 0)
            {
                throw new FormatException("CSV is empty. Header row is required.");
            }

            var header = records[0];
            if (header.Count == 0)
            {
                throw new FormatException("CSV header is empty.");
            }

            var columns = new List<string>(header.Count);
            var columnSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var idColumnIndex = -1;
            for (var i = 0; i < header.Count; i++)
            {
                var name = (header[i] ?? string.Empty).Trim();
                if (i == 0 && name.Length > 0 && name[0] == '\uFEFF')
                {
                    name = name.Substring(1);
                }

                if (string.IsNullOrWhiteSpace(name))
                {
                    throw new FormatException($"CSV header column at index {i} is empty.");
                }

                if (!columnSet.Add(name))
                {
                    throw new FormatException($"CSV header has duplicate column `{name}`.");
                }

                if (string.Equals(name, "id", StringComparison.OrdinalIgnoreCase))
                {
                    idColumnIndex = i;
                }

                columns.Add(name);
            }

            if (idColumnIndex < 0)
            {
                throw new FormatException("CSV must contain `id` column.");
            }

            var rows = new List<UniCodexCsvRowView>();
            var idSet = new HashSet<string>(StringComparer.Ordinal);
            for (var rowIndex = 1; rowIndex < records.Count; rowIndex++)
            {
                var values = records[rowIndex];
                if (IsBlankRow(values))
                {
                    continue;
                }

                if (values.Count != columns.Count)
                {
                    throw new FormatException(
                        $"CSV row {rowIndex + 1} has {values.Count} values, expected {columns.Count}.");
                }

                var idValue = (values[idColumnIndex] ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(idValue))
                {
                    throw new FormatException($"CSV row {rowIndex + 1} has empty `id`.");
                }

                if (!idSet.Add(idValue))
                {
                    throw new FormatException($"CSV has duplicated `id`: `{idValue}`.");
                }

                var valueMap = new Dictionary<string, string>(columns.Count, StringComparer.OrdinalIgnoreCase);
                for (var colIndex = 0; colIndex < columns.Count; colIndex++)
                {
                    valueMap[columns[colIndex]] = values[colIndex] ?? string.Empty;
                }

                rows.Add(new UniCodexCsvRowView(idValue, valueMap));
            }

            return new UniCodexCsvTable(tableName, columns, rows);
        }

        private static List<List<string>> ParseCsvRecords(string text)
        {
            var records = new List<List<string>>();
            if (text == null)
            {
                return records;
            }

            var row = new List<string>(8);
            var cell = new StringBuilder(64);
            var inQuotes = false;

            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (inQuotes)
                {
                    if (c == '"')
                    {
                        var nextIsQuote = i + 1 < text.Length && text[i + 1] == '"';
                        if (nextIsQuote)
                        {
                            cell.Append('"');
                            i++;
                        }
                        else
                        {
                            inQuotes = false;
                        }
                    }
                    else
                    {
                        cell.Append(c);
                    }

                    continue;
                }

                if (c == '"')
                {
                    if (cell.Length != 0)
                    {
                        throw new FormatException($"Invalid quote usage at character index {i}.");
                    }

                    inQuotes = true;
                    continue;
                }

                if (c == ',')
                {
                    row.Add(cell.ToString());
                    cell.Length = 0;
                    continue;
                }

                if (c == '\r' || c == '\n')
                {
                    row.Add(cell.ToString());
                    cell.Length = 0;
                    records.Add(new List<string>(row));
                    row.Clear();

                    if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                    {
                        i++;
                    }

                    continue;
                }

                cell.Append(c);
            }

            if (inQuotes)
            {
                throw new FormatException("CSV has unclosed quoted field.");
            }

            if (cell.Length > 0 || row.Count > 0)
            {
                row.Add(cell.ToString());
                records.Add(new List<string>(row));
            }

            return records;
        }

        private static bool IsBlankRow(IReadOnlyList<string> values)
        {
            if (values == null || values.Count == 0)
            {
                return true;
            }

            for (var i = 0; i < values.Count; i++)
            {
                if (!string.IsNullOrWhiteSpace(values[i]))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
