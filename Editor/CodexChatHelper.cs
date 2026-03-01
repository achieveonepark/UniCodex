using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace Achieve.UniCodex
{
    /// <summary>
    /// Helper methods for project paths and prompt/context construction.
    /// </summary>
    internal static class CodexChatHelper
    {
        public static string BuildPrompt(
            string userText,
            string markdownFiles,
            int maxMarkdownChars,
            bool includeMarkdownContext = true,
            IReadOnlyList<string> targetedFiles = null,
            int maxTargetedFileChars = 2800)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Unity Editor Codex bridge context:");
            sb.AppendLine($"- Project root: {GetProjectRootPath()}");
            sb.AppendLine($"- Unity action bridge file: {GetUnityActionFilePath()}");
            sb.AppendLine("- For scene/object/component changes, write JSON actions to that file (schema in Assets/Editor/CodexUnityEditorHelper.cs).");
            sb.AppendLine("- Unity action types: AddComponent, RemoveComponent, CreateSpriteObject.");
            sb.AppendLine("- For image-to-scene: use CreateSpriteObject with spritePath.");
            sb.AppendLine("- Follow workspace files and requested markdown context.");
            AppendOptimizationPolicy(sb);
            sb.AppendLine();

            if (targetedFiles != null && targetedFiles.Count > 0)
            {
                sb.AppendLine("Targeted file context (@mentions):");
                AppendTargetedFileContext(sb, targetedFiles, maxTargetedFileChars);
                sb.AppendLine();
            }

            if (includeMarkdownContext)
            {
                sb.AppendLine("Referenced markdown context:");
                AppendMarkdownContext(sb, markdownFiles, maxMarkdownChars);
            }
            else
            {
                sb.AppendLine("Referenced markdown context:");
                if (targetedFiles != null && targetedFiles.Count > 0)
                {
                    sb.AppendLine("(Skipped because this turn is explicitly targeted via @mentions.)");
                }
                else
                {
                    sb.AppendLine("(Skipped on follow-up turn for speed. Re-read if explicitly requested.)");
                }
            }

            sb.AppendLine();
            sb.AppendLine("User message:");
            sb.AppendLine(userText ?? string.Empty);
            return sb.ToString();
        }

        public static string GetProjectRootPath()
        {
            return Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        }

        public static string GetProjectCodexHome(string projectCodexHomeRelative)
        {
            if (string.IsNullOrWhiteSpace(projectCodexHomeRelative))
            {
                return Path.Combine(GetProjectRootPath(), CodexCliConstants.DefaultCodexHomeRelative);
            }

            if (Path.IsPathRooted(projectCodexHomeRelative))
            {
                return projectCodexHomeRelative;
            }

            return Path.GetFullPath(Path.Combine(GetProjectRootPath(), projectCodexHomeRelative));
        }

        public static string GetUnityActionFilePath()
        {
            return Path.Combine(GetProjectRootPath(), "Library", CodexCliConstants.UnityActionFileName);
        }

        private static void AppendOptimizationPolicy(StringBuilder sb)
        {
            sb.AppendLine("- Optimization policy (always-on):");
            sb.AppendLine("  - Prefer low allocation paths; avoid per-frame LINQ/new allocations.");
            sb.AppendLine("  - Cache component lookups and expensive references.");
            sb.AppendLine("  - Keep Update loops minimal; move work to events/timers where possible.");
            sb.AppendLine("  - Prefer batching/pooling/reuse over instantiate-destroy churn.");
            sb.AppendLine("  - Mention expected perf impact when proposing implementation.");
        }

        private static void AppendTargetedFileContext(StringBuilder sb, IReadOnlyList<string> targetedFiles, int maxCharsPerFile)
        {
            var addedAny = false;
            if (targetedFiles == null || targetedFiles.Count == 0)
            {
                sb.AppendLine("(No @mentioned files were resolved.)");
                return;
            }

            var budget = Mathf.Max(500, maxCharsPerFile);
            for (var i = 0; i < targetedFiles.Count; i++)
            {
                var relativePath = targetedFiles[i];
                var fullPath = ResolvePath(relativePath);
                if (string.IsNullOrWhiteSpace(fullPath) || !File.Exists(fullPath))
                {
                    continue;
                }

                try
                {
                    var content = File.ReadAllText(fullPath);
                    if (content.Length > budget)
                    {
                        content = content.Substring(0, budget);
                        content += "\n[TRUNCATED]";
                    }

                    var relative = MakeDisplayPath(fullPath);
                    sb.AppendLine($"--- BEGIN TARGET FILE: {relative} ---");
                    sb.AppendLine(content);
                    sb.AppendLine($"--- END TARGET FILE: {relative} ---");
                    sb.AppendLine();
                    addedAny = true;
                }
                catch (Exception)
                {
                    // Keep prompt construction resilient.
                }
            }

            if (!addedAny)
            {
                sb.AppendLine("(No @mentioned files were resolved.)");
            }
        }

        private static void AppendMarkdownContext(StringBuilder sb, string markdownFiles, int maxMarkdownChars)
        {
            var addedAny = false;
            foreach (var rawPath in ParseMarkdownPathList(markdownFiles))
            {
                var fullPath = ResolvePath(rawPath);
                if (string.IsNullOrEmpty(fullPath) || !File.Exists(fullPath))
                {
                    continue;
                }

                try
                {
                    var content = File.ReadAllText(fullPath);
                    if (content.Length > maxMarkdownChars)
                    {
                        content = content.Substring(0, maxMarkdownChars);
                        content += "\n[TRUNCATED]";
                    }

                    var relative = MakeDisplayPath(fullPath);
                    sb.AppendLine($"--- BEGIN FILE: {relative} ---");
                    sb.AppendLine(content);
                    sb.AppendLine($"--- END FILE: {relative} ---");
                    sb.AppendLine();
                    addedAny = true;
                }
                catch (Exception)
                {
                    // Intentionally ignore markdown read errors to keep chat clean.
                }
            }

            if (!addedAny)
            {
                sb.AppendLine("(No markdown context was attached.)");
            }
        }

        private static IEnumerable<string> ParseMarkdownPathList(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                yield break;
            }

            var lines = raw.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                if (string.IsNullOrEmpty(line) || line.StartsWith("#", StringComparison.Ordinal))
                {
                    continue;
                }

                yield return line;
            }
        }

        private static string ResolvePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            return Path.IsPathRooted(path)
                ? path
                : Path.GetFullPath(Path.Combine(GetProjectRootPath(), path));
        }

        private static string MakeDisplayPath(string fullPath)
        {
            var root = GetProjectRootPath().Replace('\\', '/');
            var normalized = fullPath.Replace('\\', '/');
            if (normalized.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                return normalized.Substring(root.Length).TrimStart('/');
            }

            return normalized;
        }
    }
}
