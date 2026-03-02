using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Achieve.UniCodex.Editor
{
    /// <summary>
    /// Codex CLI 명령 실행을 감싸는 서비스 래퍼입니다.
    /// 설치/로그인 상태 확인과 채팅 실행을 담당합니다.
    /// </summary>
    internal sealed class UniCodexCliService
    {
        private static readonly Regex ThreadRegex = new Regex("\"thread_id\":\"([^\"]+)\"", RegexOptions.Compiled);
        private static readonly Regex JsonMessageRegex = new Regex("\"message\"\\s*:\\s*\"([^\"]+)\"", RegexOptions.Compiled);
        private static readonly Regex InputTokensRegex = new Regex("\"input_tokens\"\\s*:\\s*(\\d+)", RegexOptions.Compiled);
        private static readonly Regex OutputTokensRegex = new Regex("\"output_tokens\"\\s*:\\s*(\\d+)", RegexOptions.Compiled);
        private static readonly Regex TotalTokensRegex = new Regex("\"total_tokens\"\\s*:\\s*(\\d+)", RegexOptions.Compiled);
        private static readonly Regex PromptTokensRegex = new Regex("\"prompt_tokens\"\\s*:\\s*(\\d+)", RegexOptions.Compiled);
        private static readonly Regex CompletionTokensRegex = new Regex("\"completion_tokens\"\\s*:\\s*(\\d+)", RegexOptions.Compiled);
        private static readonly Regex JsonTypeRegex = new Regex("\"type\"\\s*:\\s*\"([^\"]+)\"", RegexOptions.Compiled);
        private static readonly Regex JsonEventRegex = new Regex("\"event\"\\s*:\\s*\"([^\"]+)\"", RegexOptions.Compiled);
        private static readonly Regex JsonStatusRegex = new Regex("\"status\"\\s*:\\s*\"([^\"]+)\"", RegexOptions.Compiled);
        private static readonly Regex JsonTitleRegex = new Regex("\"title\"\\s*:\\s*\"([^\"]+)\"", RegexOptions.Compiled);
        private static readonly Regex JsonToolNameRegex = new Regex("\"tool_name\"\\s*:\\s*\"([^\"]+)\"", RegexOptions.Compiled);
        private static readonly Regex JsonTextRegex = new Regex("\"text\"\\s*:\\s*\"([^\"]+)\"", RegexOptions.Compiled);
        private static readonly Regex JsonDeltaRegex = new Regex("\"delta\"\\s*:\\s*\"([^\"]+)\"", RegexOptions.Compiled);
        private static readonly Regex JsonContentRegex = new Regex("\"content\"\\s*:\\s*\"([^\"]+)\"", RegexOptions.Compiled);
        private static readonly Regex JsonSummaryTextRegex = new Regex("\"summary\"\\s*:\\s*\\[[^\\]]*\"text\"\\s*:\\s*\"([^\"]+)\"", RegexOptions.Compiled);
        private static readonly Regex DotKeywordRegex = new Regex("^[a-z0-9_]+(\\.[a-z0-9_]+)+$", RegexOptions.Compiled);

        private readonly string _cliPath;
        private readonly string _workingDirectory;
        private readonly bool _useProjectCodexHome;
        private readonly string _projectCodexHome;
        private readonly bool _fullAuto;
        private readonly string _model;
        private readonly string _modelReasoningEffort;
        private readonly int _execTimeoutMs;

        /// <summary>
        /// 지정한 프로젝트 컨텍스트로 Codex CLI 서비스 인스턴스를 생성합니다.
        /// </summary>
        public UniCodexCliService(
            string cliPath,
            string workingDirectory,
            bool useProjectCodexHome,
            string projectCodexHome,
            bool fullAuto,
            string model = null,
            string modelReasoningEffort = null,
            int execTimeoutMs = UniCodexCliConstants.DefaultExecTimeoutMs)
        {
            _cliPath = string.IsNullOrWhiteSpace(cliPath) ? UniCodexCliConstants.DefaultCliPath : cliPath.Trim();
            _workingDirectory = workingDirectory;
            _useProjectCodexHome = useProjectCodexHome;
            _projectCodexHome = projectCodexHome;
            _fullAuto = fullAuto;
            _model = string.IsNullOrWhiteSpace(model) ? string.Empty : model.Trim();
            _modelReasoningEffort = string.IsNullOrWhiteSpace(modelReasoningEffort) ? string.Empty : modelReasoningEffort.Trim();
            // Keep non-positive values as "no timeout".
            _execTimeoutMs = execTimeoutMs;
        }

        /// <summary>
        /// 설치 상태와 버전을 확인한 뒤 로그인 상태를 조회합니다.
        /// </summary>
        public UniCodexEnvironmentState RefreshEnvironmentState()
        {
            var state = new UniCodexEnvironmentState();
            state.Installed = ResolveCliPathAndVersion(out state.VersionText, out state.ResolvedCliPath);
            if (!state.Installed)
            {
                state.LoggedIn = false;
                state.LoginText = "Codex not installed.";
                return state;
            }

            state.LoggedIn = TryQueryLoginStatus(out state.LoginText, state.ResolvedCliPath);
            return state;
        }

        /// <summary>
        /// 로그인 상태만 조회합니다.
        /// </summary>
        public UniCodexCommandResult QueryLoginStatus()
        {
            var loggedIn = TryQueryLoginStatus(out var loginText);
            return new UniCodexCommandResult
            {
                Success = loggedIn,
                Message = loginText
            };
        }

        /// <summary>
        /// Codex CLI의 디바이스 인증 로그인 플로우를 시작합니다.
        /// </summary>
        public UniCodexCommandResult LoginWithDeviceAuth()
        {
            var success = TryRunCodexCommand("login --device-auth", null, 240000, out var exitCode, out var output);
            return new UniCodexCommandResult
            {
                Success = success && exitCode == 0,
                Message = output
            };
        }

        /// <summary>
        /// 현재 Codex CLI 사용자를 로그아웃합니다.
        /// </summary>
        public UniCodexCommandResult Logout()
        {
            var success = TryRunCodexCommand("logout", null, 10000, out var exitCode, out var output);
            return new UniCodexCommandResult
            {
                Success = success && exitCode == 0,
                Message = output
            };
        }

        /// <summary>
        /// <c>codex exec</c>로 한 턴을 실행하고 출력 메타데이터를 파싱합니다.
        /// </summary>
        public UniCodexRunResult Run(string prompt, string sessionId, Action<string> progressCallback = null)
        {
            var request = new UniCodexRunRequest
            {
                CliPath = _cliPath,
                WorkingDirectory = _workingDirectory,
                Prompt = prompt,
                SessionId = string.IsNullOrWhiteSpace(sessionId) ? string.Empty : sessionId,
                Model = _model,
                ModelReasoningEffort = _modelReasoningEffort,
                UseProjectCodexHome = _useProjectCodexHome,
                ProjectCodexHome = _projectCodexHome,
                FullAuto = _fullAuto,
                TimeoutMs = _execTimeoutMs
            };

            return RunCodex(request, progressCallback);
        }

        /// <summary>
        /// 실행 결과에서 토큰 사용량 요약 문자열을 생성합니다.
        /// </summary>
        public static string BuildTokenSummary(UniCodexRunResult result)
        {
            if (result == null)
            {
                return string.Empty;
            }

            if (!result.InputTokens.HasValue && !result.OutputTokens.HasValue && !result.TotalTokens.HasValue)
            {
                return string.Empty;
            }

            var input = result.InputTokens.GetValueOrDefault(0);
            var output = result.OutputTokens.GetValueOrDefault(0);
            var ioSum = input + output;
            var total = result.TotalTokens.GetValueOrDefault(ioSum);
            if (ioSum > 0)
            {
                var reportedTotal = result.TotalTokens.GetValueOrDefault(0);
                if (reportedTotal <= 0 || reportedTotal > ioSum * 3)
                {
                    total = ioSum;
                }
            }

            return $"tok in/out/total: {input}/{output}/{total}";
        }

        private bool ResolveCliPathAndVersion(out string versionText, out string resolvedCliPath)
        {
            var candidates = new List<string>();
            if (!string.IsNullOrWhiteSpace(_cliPath))
            {
                candidates.Add(_cliPath.Trim());
            }

            for (var i = 0; i < UniCodexCliConstants.CliCandidates.Length; i++)
            {
                var candidate = UniCodexCliConstants.CliCandidates[i];
                if (!candidates.Contains(candidate))
                {
                    candidates.Add(candidate);
                }
            }

            foreach (var candidate in candidates)
            {
                if (!TryGetCodexVersion(candidate, out var versionOrError))
                {
                    continue;
                }

                resolvedCliPath = candidate;
                versionText = versionOrError;
                return true;
            }

            resolvedCliPath = string.Empty;
            versionText = "Not found";
            return false;
        }

        private bool TryQueryLoginStatus(out string loginText, string cliPathOverride = null)
        {
            if (!TryRunCodexCommand("login status", null, 10000, out var exitCode, out var output, cliPathOverride))
            {
                loginText = output;
                return false;
            }

            var notLoggedIn = output.IndexOf("Not logged in", StringComparison.OrdinalIgnoreCase) >= 0;
            if (notLoggedIn)
            {
                loginText = "Not logged in";
                return false;
            }

            loginText = exitCode == 0 ? output : "Not logged in";
            return exitCode == 0;
        }

        private bool TryGetCodexVersion(string cliPath, out string resultText)
        {
            if (!TryRunCodexCommand("--version", null, 6000, out var exitCode, out var output, cliPath))
            {
                resultText = output;
                return false;
            }

            if (exitCode == 0)
            {
                resultText = output;
                return true;
            }

            resultText = string.IsNullOrWhiteSpace(output) ? "Unknown error" : output;
            return false;
        }

        private bool TryRunCodexCommand(string arguments, string stdinText, int timeoutMs, out int exitCode, out string output, string cliPathOverride = null)
        {
            exitCode = -1;
            output = string.Empty;

            try
            {
                var resolvedCliPath = string.IsNullOrWhiteSpace(cliPathOverride) ? _cliPath : cliPathOverride;
                var psi = new ProcessStartInfo
                {
                    FileName = resolvedCliPath,
                    Arguments = arguments,
                    WorkingDirectory = _workingDirectory,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                ConfigureUtf8Process(psi);

                if (_useProjectCodexHome && !string.IsNullOrWhiteSpace(_projectCodexHome))
                {
                    Directory.CreateDirectory(_projectCodexHome);
                    psi.EnvironmentVariables["CODEX_HOME"] = _projectCodexHome;
                }

                using var process = new Process { StartInfo = psi };
                process.Start();

                if (!string.IsNullOrEmpty(stdinText))
                {
                    process.StandardInput.Write(stdinText);
                }

                process.StandardInput.Close();

                var timedOut = timeoutMs > 0 && !process.WaitForExit(timeoutMs);
                if (timedOut)
                {
                    try
                    {
                        process.Kill();
                    }
                    catch
                    {
                        // no-op
                    }

                    output = "timeout";
                    return false;
                }
                else if (timeoutMs <= 0)
                {
                    process.WaitForExit();
                }

                var stdout = process.StandardOutput.ReadToEnd().Trim();
                var stderr = process.StandardError.ReadToEnd().Trim();
                exitCode = process.ExitCode;
                output = exitCode == 0
                    ? (string.IsNullOrWhiteSpace(stdout) ? stderr : stdout)
                    : (string.IsNullOrWhiteSpace(stderr) ? stdout : stderr);

                if (string.IsNullOrWhiteSpace(output))
                {
                    output = stdout;
                }

                return true;
            }
            catch (Exception ex)
            {
                output = ex.Message;
                return false;
            }
        }

        private static UniCodexRunResult RunCodex(UniCodexRunRequest request, Action<string> progressCallback)
        {
            var outputFile = Path.Combine(Path.GetTempPath(), $"codex-last-{Guid.NewGuid():N}.txt");
            try
            {
                if (request.UseProjectCodexHome && !string.IsNullOrWhiteSpace(request.ProjectCodexHome))
                {
                    Directory.CreateDirectory(request.ProjectCodexHome);
                }

                var psi = new ProcessStartInfo
                {
                    FileName = request.CliPath,
                    WorkingDirectory = request.WorkingDirectory,
                    Arguments = BuildCodexArguments(request, outputFile),
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                ConfigureUtf8Process(psi);

                if (request.UseProjectCodexHome && !string.IsNullOrWhiteSpace(request.ProjectCodexHome))
                {
                    psi.EnvironmentVariables["CODEX_HOME"] = request.ProjectCodexHome;
                }

                var stdoutBuilder = new StringBuilder();
                var stderrBuilder = new StringBuilder();
                var outputLock = new object();

                using var process = new Process { StartInfo = psi };
                process.OutputDataReceived += (_, args) =>
                {
                    if (args.Data == null)
                    {
                        return;
                    }

                    lock (outputLock)
                    {
                        stdoutBuilder.AppendLine(args.Data);
                    }

                    EmitProgressFromLine(args.Data, progressCallback);
                };

                process.ErrorDataReceived += (_, args) =>
                {
                    if (args.Data == null)
                    {
                        return;
                    }

                    lock (outputLock)
                    {
                        stderrBuilder.AppendLine(args.Data);
                    }

                    EmitProgressFromLine(args.Data, progressCallback);
                };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                process.StandardInput.Write(request.Prompt);
                process.StandardInput.Close();

                var timeoutMs = request.TimeoutMs;
                var timedOut = timeoutMs > 0 && !process.WaitForExit(timeoutMs);
                if (timedOut)
                {
                    try
                    {
                        process.Kill();
                    }
                    catch
                    {
                        // no-op
                    }

                    try
                    {
                        process.WaitForExit(1500);
                    }
                    catch
                    {
                        // no-op
                    }

                    var timedOutStdout = string.Empty;
                    var timedOutStderr = string.Empty;
                    lock (outputLock)
                    {
                        timedOutStdout = stdoutBuilder.ToString();
                        timedOutStderr = stderrBuilder.ToString();
                    }
                    var timedOutCombined = (timedOutStdout + "\n" + timedOutStderr).Trim();
                    var timedOutThreadId = ExtractThreadId(timedOutCombined);
                    ExtractTokenUsage(timedOutCombined, out var timeoutInputTokens, out var timeoutOutputTokens, out var timeoutTotalTokens);

                    return new UniCodexRunResult
                    {
                        Success = false,
                        Message = $"codex execution timed out after {timeoutMs / 1000}s.",
                        ThreadId = timedOutThreadId,
                        InputTokens = timeoutInputTokens,
                        OutputTokens = timeoutOutputTokens,
                        TotalTokens = timeoutTotalTokens
                    };
                }
                else if (timeoutMs <= 0)
                {
                    process.WaitForExit();
                }

                process.WaitForExit(250);
                try
                {
                    process.CancelOutputRead();
                    process.CancelErrorRead();
                }
                catch
                {
                    // no-op
                }

                string stdout;
                string stderr;
                lock (outputLock)
                {
                    stdout = stdoutBuilder.ToString();
                    stderr = stderrBuilder.ToString();
                }
                var combined = (stdout + "\n" + stderr).Trim();
                var threadId = ExtractThreadId(combined);
                ExtractTokenUsage(combined, out var inputTokens, out var outputTokens, out var totalTokens);

                var reply = File.Exists(outputFile) ? File.ReadAllText(outputFile).Trim() : string.Empty;
                var success = process.ExitCode == 0 && !string.IsNullOrWhiteSpace(reply);
                if (!success && string.IsNullOrWhiteSpace(reply))
                {
                    reply = ExtractErrorMessage(combined);
                }

                return new UniCodexRunResult
                {
                    Success = success,
                    Message = string.IsNullOrWhiteSpace(reply) ? "No response from codex CLI." : reply,
                    ThreadId = threadId,
                    InputTokens = inputTokens,
                    OutputTokens = outputTokens,
                    TotalTokens = totalTokens
                };
            }
            catch (Exception ex)
            {
                return UniCodexRunResult.FromError($"codex execution failed: {ex.Message}");
            }
            finally
            {
                if (File.Exists(outputFile))
                {
                    try
                    {
                        File.Delete(outputFile);
                    }
                    catch
                    {
                        // Ignore temp cleanup failure.
                    }
                }
            }
        }

        private static string BuildCodexArguments(UniCodexRunRequest request, string outputFile)
        {
            var sb = new StringBuilder();
            sb.Append("exec ");

            var hasSession = !string.IsNullOrWhiteSpace(request.SessionId);
            if (hasSession)
            {
                sb.Append("resume ");
            }

            sb.Append("--json ");
            if (!string.IsNullOrWhiteSpace(request.Model))
            {
                sb.Append("--model ").Append(EscapeArg(request.Model)).Append(' ');
            }

            if (!string.IsNullOrWhiteSpace(request.ModelReasoningEffort))
            {
                sb.Append("-c ")
                    .Append(EscapeArg($"model_reasoning_effort={request.ModelReasoningEffort}"))
                    .Append(' ');
            }

            sb.Append("--skip-git-repo-check ");
            sb.Append("--output-last-message ").Append(EscapeArg(outputFile)).Append(' ');

            if (request.FullAuto)
            {
                sb.Append("--full-auto ");
            }

            if (!hasSession)
            {
                sb.Append("--cd ").Append(EscapeArg(request.WorkingDirectory)).Append(' ');
            }

            if (hasSession)
            {
                sb.Append(EscapeArg(request.SessionId)).Append(' ');
            }

            // Read prompt from stdin to avoid argument length/escaping issues.
            sb.Append('-');
            return sb.ToString();
        }

        private static string EscapeArg(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "\"\"";
            }

            if (value.IndexOfAny(new[] { ' ', '\t', '\n', '"' }) < 0)
            {
                return value;
            }

            return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        private static string ExtractThreadId(string output)
        {
            if (string.IsNullOrWhiteSpace(output))
            {
                return string.Empty;
            }

            var match = ThreadRegex.Match(output);
            return match.Success ? match.Groups[1].Value : string.Empty;
        }

        private static string ExtractErrorMessage(string output)
        {
            if (string.IsNullOrWhiteSpace(output))
            {
                return string.Empty;
            }

            var jsonMessageMatch = JsonMessageRegex.Match(output);
            if (jsonMessageMatch.Success)
            {
                return jsonMessageMatch.Groups[1].Value.Replace("\\n", "\n");
            }

            using var reader = new StringReader(output);
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                if (line.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return line;
                }
            }

            return output.Length > 400 ? output.Substring(0, 400) + "..." : output;
        }

        private static void EmitProgressFromLine(string line, Action<string> progressCallback)
        {
            if (progressCallback == null || string.IsNullOrWhiteSpace(line))
            {
                return;
            }

            var progress = TryExtractProgressMessage(line);
            if (string.IsNullOrWhiteSpace(progress))
            {
                return;
            }

            progressCallback(progress);
        }

        private static string TryExtractProgressMessage(string line)
        {
            var trimmed = line?.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                return string.Empty;
            }

            // Some codex outputs may emit plain text (non-JSON) progress lines.
            if (!trimmed.StartsWith("{", StringComparison.Ordinal))
            {
                return FormatProgressText(trimmed);
            }

            // Prefer richer textual payloads over generic event/status labels.
            var summaryText = FormatProgressText(TryExtractJsonValue(trimmed, JsonSummaryTextRegex));
            if (!string.IsNullOrWhiteSpace(summaryText))
            {
                return summaryText;
            }

            var text = FormatProgressText(TryExtractJsonValue(trimmed, JsonTextRegex));
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text;
            }

            var delta = FormatProgressText(TryExtractJsonValue(trimmed, JsonDeltaRegex));
            if (!string.IsNullOrWhiteSpace(delta))
            {
                return delta;
            }

            var message = FormatProgressText(TryExtractJsonValue(trimmed, JsonMessageRegex));
            if (!string.IsNullOrWhiteSpace(message))
            {
                return message;
            }

            var content = FormatProgressText(TryExtractJsonValue(trimmed, JsonContentRegex));
            if (!string.IsNullOrWhiteSpace(content))
            {
                return content;
            }

            var title = FormatProgressText(TryExtractJsonValue(trimmed, JsonTitleRegex));
            if (!string.IsNullOrWhiteSpace(title))
            {
                return title;
            }

            var toolName = FormatProgressText(TryExtractJsonValue(trimmed, JsonToolNameRegex));
            if (!string.IsNullOrWhiteSpace(toolName))
            {
                return $"Running {toolName}";
            }

            var status = FormatProgressText(TryExtractJsonValue(trimmed, JsonStatusRegex));
            if (!string.IsNullOrWhiteSpace(status))
            {
                return status;
            }

            var eventName = FormatProgressText(TryExtractJsonValue(trimmed, JsonEventRegex));
            if (!string.IsNullOrWhiteSpace(eventName))
            {
                return eventName;
            }

            var type = FormatProgressText(TryExtractJsonValue(trimmed, JsonTypeRegex));
            if (!string.IsNullOrWhiteSpace(type))
            {
                return type;
            }

            return string.Empty;
        }

        private static string FormatProgressText(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return string.Empty;
            }

            var value = raw
                .Replace("\\n", " ")
                .Replace("\\r", " ")
                .Replace("\\t", " ")
                .Replace("\\\"", "\"")
                .Replace("\\\\", "\\")
                .Replace("\r", " ")
                .Replace("\n", " ")
                .Trim();

            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            // Skip keyword-only progress noise such as `item.started`, `item.completed`, etc.
            if (IsKeywordOnlyProgress(value))
            {
                return string.Empty;
            }

            if (value.Length > 180)
            {
                value = value.Substring(0, 180) + "...";
            }

            return value;
        }

        private static bool IsKeywordOnlyProgress(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return true;
            }

            var normalized = value.Trim().ToLowerInvariant();
            if (normalized.Length == 0)
            {
                return true;
            }

            if (normalized == "start"
                || normalized == "started"
                || normalized == "complete"
                || normalized == "completed"
                || normalized == "item.event"
                || normalized == "event")
            {
                return true;
            }

            return DotKeywordRegex.IsMatch(normalized);
        }

        private static string TryExtractJsonValue(string jsonLine, Regex regex)
        {
            if (string.IsNullOrWhiteSpace(jsonLine) || regex == null)
            {
                return string.Empty;
            }

            var match = regex.Match(jsonLine);
            if (!match.Success || match.Groups.Count < 2)
            {
                return string.Empty;
            }

            return match.Groups[1].Value.Trim();
        }

        private static void ExtractTokenUsage(string output, out int? inputTokens, out int? outputTokens, out int? totalTokens)
        {
            inputTokens = TryGetLastIntMatch(output, InputTokensRegex) ?? TryGetLastIntMatch(output, PromptTokensRegex);
            outputTokens = TryGetLastIntMatch(output, OutputTokensRegex) ?? TryGetLastIntMatch(output, CompletionTokensRegex);
            totalTokens = TryGetLastIntMatch(output, TotalTokensRegex);

            if (!totalTokens.HasValue && (inputTokens.HasValue || outputTokens.HasValue))
            {
                totalTokens = inputTokens.GetValueOrDefault(0) + outputTokens.GetValueOrDefault(0);
            }
        }

        private static int? TryGetLastIntMatch(string text, Regex regex)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            var matches = regex.Matches(text);
            if (matches.Count == 0)
            {
                return null;
            }

            var last = matches[matches.Count - 1];
            if (!last.Success || last.Groups.Count < 2)
            {
                return null;
            }

            return int.TryParse(last.Groups[1].Value, out var parsed) ? parsed : (int?)null;
        }

        private static void ConfigureUtf8Process(ProcessStartInfo psi)
        {
            psi.EnvironmentVariables["LANG"] = "en_US.UTF-8";
            psi.EnvironmentVariables["LC_ALL"] = "en_US.UTF-8";

            try
            {
                var utf8 = new UTF8Encoding(false);
                var type = typeof(ProcessStartInfo);
                type.GetProperty("StandardInputEncoding")?.SetValue(psi, utf8, null);
                type.GetProperty("StandardOutputEncoding")?.SetValue(psi, utf8, null);
                type.GetProperty("StandardErrorEncoding")?.SetValue(psi, utf8, null);
            }
            catch
            {
                // Runtime may not expose encoding properties.
            }
        }
    }
}
