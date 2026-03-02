using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Achieve.UniCodex.Editor
{
    /// <summary>
    /// 제안된 코드 변경을 전용 Diff 창으로 표시하고 적용할 수 있습니다.
    /// </summary>
    public sealed class UniCodexDiffPreviewWindow : EditorWindow
    {
        private const string NoChangesToken = "NO_CHANGES";
        private const string ManualRefreshPrefKey = UniCodexCliConstants.PrefPrefix + "ManualRefreshMode";
        private const float LineNumberColumnWidth = 46f;
        private static readonly Regex HunkHeaderRegex = new Regex(
            @"^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@",
            RegexOptions.Compiled);

        private string _diffTitle = "Diff Preview";
        private string _diffText = NoChangesToken;

        private Label _titleLabel;
        private Label _metaLabel;
        private Button _applyButton;
        private Button _refineButton;
        private ScrollView _tabScrollView;
        private VisualElement _tabStripRoot;
        private ScrollView _diffScrollView;
        private VisualElement _diffContent;
        private VisualElement _refineNarrativePanel;
        private Label _refineNarrativeTitleLabel;
        private Label _refineNarrativeLabel;
        private TextField _refineInputField;
        private Font _monoFont;
        private bool _isRefining;
        private bool _tabsInitialized;
        private int _activeTabIndex;
        private int _initialTabCount;
        private string _tabBuildError = string.Empty;
        private Func<string, string, Task<UniCodexRunResult>> _refineRequestHandler;
        private readonly List<UniCodexDiffTabState> _tabs = new List<UniCodexDiffTabState>();

        private enum DiffLineKind
        {
            Neutral,
            Meta,
            FileHeader,
            HunkHeader,
            Added,
            Removed,
            Context
        }

        private sealed class UniCodexFilePatch
        {
            /// <summary>Diff 헤더의 원본 파일 경로입니다.</summary>
            public string OldPath;
            /// <summary>Diff 헤더의 변경 후 파일 경로입니다.</summary>
            public string NewPath;
            /// <summary>이 파일 패치에 포함된 hunk 목록입니다.</summary>
            public readonly List<UniCodexDiffHunk> Hunks = new List<UniCodexDiffHunk>();
        }

        private sealed class UniCodexDiffHunk
        {
            /// <summary>이 hunk의 원본 파일 시작 라인입니다.</summary>
            public int OldStart;
            /// <summary>이 hunk의 변경 후 파일 시작 라인입니다.</summary>
            public int NewStart;
            /// <summary>이 hunk에 포함된 파싱 라인 목록입니다.</summary>
            public readonly List<HunkLine> Lines = new List<HunkLine>();
        }

        private struct HunkLine
        {
            /// <summary>Diff 라인 접두 문자(' ', '+', '-')입니다.</summary>
            public char Prefix;
            /// <summary>접두 문자를 제외한 라인 텍스트입니다.</summary>
            public string Text;
        }

        private enum DiffTabStatus
        {
            Pending,
            Applying,
            Error
        }

        private sealed class UniCodexDiffTabState
        {
            /// <summary>탭 고유 ID입니다.</summary>
            public string Id;
            /// <summary>사용자에게 표시할 탭 이름입니다.</summary>
            public string DisplayName;
            /// <summary>이 탭의 파싱된 패치 모델입니다.</summary>
            public UniCodexFilePatch Patch;
            /// <summary>이 탭에서 렌더링하는 unified diff 텍스트입니다.</summary>
            public string DiffText;
            /// <summary>어시스턴트가 반환한 refine 설명 텍스트입니다.</summary>
            public string RefineNarrative;
            /// <summary>이 탭의 적용 상태입니다.</summary>
            public DiffTabStatus Status;
        }

        internal static void ShowDiff(string diffTitle, string diffText, Func<string, string, Task<UniCodexRunResult>> refineRequestHandler = null)
        {
            var window = GetWindow<UniCodexDiffPreviewWindow>();
            window.titleContent = new GUIContent("Codex Diff Preview");
            window.minSize = new Vector2(820f, 520f);
            window._diffTitle = string.IsNullOrWhiteSpace(diffTitle) ? "Diff Preview" : diffTitle.Trim();
            window._diffText = string.IsNullOrWhiteSpace(diffText) ? NoChangesToken : diffText;
            window._refineRequestHandler = refineRequestHandler;
            window._tabsInitialized = false;
            window._activeTabIndex = 0;
            window._initialTabCount = 0;
            window._tabBuildError = string.Empty;
            window._tabs.Clear();
            window.Show();
            window.Focus();
            window.RefreshUI();
        }

        private void CreateGUI()
        {
            BuildUI();
            RefreshUI();
        }

        private void BuildUI()
        {
            rootVisualElement.Clear();
            rootVisualElement.style.flexDirection = FlexDirection.Column;
            rootVisualElement.style.flexGrow = 1f;
            rootVisualElement.style.paddingLeft = 10f;
            rootVisualElement.style.paddingRight = 10f;
            rootVisualElement.style.paddingTop = 10f;
            rootVisualElement.style.paddingBottom = 10f;

            _monoFont = TryGetMonospaceFont();

            var header = new VisualElement();
            header.style.flexDirection = FlexDirection.Column;
            header.style.flexShrink = 0f;
            header.style.marginBottom = 8f;

            var titleRow = new VisualElement();
            titleRow.style.flexDirection = FlexDirection.Row;
            titleRow.style.justifyContent = Justify.SpaceBetween;
            titleRow.style.alignItems = Align.Center;
            titleRow.style.marginBottom = 4f;

            _titleLabel = new Label("Diff Preview");
            _titleLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            _titleLabel.style.fontSize = 14f;
            titleRow.Add(_titleLabel);

            var actions = new VisualElement();
            actions.style.flexDirection = FlexDirection.Row;
            actions.style.alignItems = Align.Center;

            _applyButton = new Button(ApplyDiffToProject) { text = "Apply" };
            _applyButton.style.width = 68f;
            _applyButton.style.height = 24f;
            _applyButton.style.marginRight = 4f;
            actions.Add(_applyButton);

            var closeButton = new Button(Close) { text = "Close" };
            closeButton.style.width = 68f;
            closeButton.style.height = 24f;
            actions.Add(closeButton);

            titleRow.Add(actions);
            header.Add(titleRow);

            _metaLabel = new Label("-");
            _metaLabel.style.color = new Color(0.74f, 0.74f, 0.74f, 1f);
            _metaLabel.style.fontSize = 11f;
            _metaLabel.style.marginBottom = 2f;
            header.Add(_metaLabel);

            var refineRow = new VisualElement();
            refineRow.style.flexDirection = FlexDirection.Row;
            refineRow.style.alignItems = Align.Center;
            refineRow.style.marginTop = 4f;

            _refineInputField = new TextField();
            _refineInputField.style.flexGrow = 1f;
            _refineInputField.style.marginRight = 6f;
            _refineInputField.style.height = 22f;
            _refineInputField.tooltip = "Refine request before apply";
            _refineInputField.SetValueWithoutNotify(string.Empty);
            refineRow.Add(_refineInputField);

            _refineButton = new Button(RequestRefineDiff) { text = "Refine" };
            _refineButton.style.width = 68f;
            _refineButton.style.height = 24f;
            refineRow.Add(_refineButton);

            header.Add(refineRow);

            rootVisualElement.Add(header);

            _tabScrollView = new ScrollView(ScrollViewMode.Horizontal);
            _tabScrollView.style.flexShrink = 0f;
            _tabScrollView.style.height = 28f;
            _tabScrollView.style.marginBottom = 6f;
            _tabScrollView.style.borderTopWidth = 1f;
            _tabScrollView.style.borderBottomWidth = 1f;
            _tabScrollView.style.borderLeftWidth = 1f;
            _tabScrollView.style.borderRightWidth = 1f;
            _tabScrollView.style.borderTopColor = new Color(0.24f, 0.24f, 0.24f, 1f);
            _tabScrollView.style.borderBottomColor = new Color(0.24f, 0.24f, 0.24f, 1f);
            _tabScrollView.style.borderLeftColor = new Color(0.24f, 0.24f, 0.24f, 1f);
            _tabScrollView.style.borderRightColor = new Color(0.24f, 0.24f, 0.24f, 1f);
            _tabScrollView.style.backgroundColor = new Color(0.14f, 0.14f, 0.15f, 1f);

            _tabStripRoot = new VisualElement();
            _tabStripRoot.style.flexDirection = FlexDirection.Row;
            _tabStripRoot.style.alignItems = Align.Center;
            _tabStripRoot.style.paddingLeft = 4f;
            _tabStripRoot.style.paddingRight = 4f;
            _tabStripRoot.style.paddingTop = 2f;
            _tabStripRoot.style.paddingBottom = 2f;
            _tabScrollView.Add(_tabStripRoot);

            rootVisualElement.Add(_tabScrollView);

            _diffScrollView = new ScrollView(ScrollViewMode.VerticalAndHorizontal);
            _diffScrollView.style.flexGrow = 1f;
            _diffScrollView.style.borderTopWidth = 1f;
            _diffScrollView.style.borderBottomWidth = 1f;
            _diffScrollView.style.borderLeftWidth = 1f;
            _diffScrollView.style.borderRightWidth = 1f;
            _diffScrollView.style.borderTopColor = new Color(0.24f, 0.24f, 0.24f, 1f);
            _diffScrollView.style.borderBottomColor = new Color(0.24f, 0.24f, 0.24f, 1f);
            _diffScrollView.style.borderLeftColor = new Color(0.24f, 0.24f, 0.24f, 1f);
            _diffScrollView.style.borderRightColor = new Color(0.24f, 0.24f, 0.24f, 1f);
            _diffScrollView.style.backgroundColor = new Color(0.11f, 0.11f, 0.12f, 1f);

            _diffContent = new VisualElement();
            _diffContent.style.flexDirection = FlexDirection.Column;
            _diffContent.style.flexGrow = 1f;
            _diffContent.style.paddingTop = 2f;
            _diffContent.style.paddingBottom = 2f;
            _diffContent.style.paddingLeft = 0f;
            _diffContent.style.paddingRight = 0f;
            _diffScrollView.Add(_diffContent);

            rootVisualElement.Add(_diffScrollView);

            _refineNarrativePanel = new VisualElement();
            _refineNarrativePanel.style.flexDirection = FlexDirection.Column;
            _refineNarrativePanel.style.flexShrink = 0f;
            _refineNarrativePanel.style.marginTop = 6f;
            _refineNarrativePanel.style.paddingTop = 6f;
            _refineNarrativePanel.style.paddingBottom = 6f;
            _refineNarrativePanel.style.paddingLeft = 8f;
            _refineNarrativePanel.style.paddingRight = 8f;
            _refineNarrativePanel.style.borderTopWidth = 1f;
            _refineNarrativePanel.style.borderBottomWidth = 1f;
            _refineNarrativePanel.style.borderLeftWidth = 1f;
            _refineNarrativePanel.style.borderRightWidth = 1f;
            _refineNarrativePanel.style.borderTopColor = new Color(0.24f, 0.24f, 0.24f, 1f);
            _refineNarrativePanel.style.borderBottomColor = new Color(0.24f, 0.24f, 0.24f, 1f);
            _refineNarrativePanel.style.borderLeftColor = new Color(0.24f, 0.24f, 0.24f, 1f);
            _refineNarrativePanel.style.borderRightColor = new Color(0.24f, 0.24f, 0.24f, 1f);
            _refineNarrativePanel.style.backgroundColor = new Color(0.13f, 0.13f, 0.14f, 1f);
            _refineNarrativePanel.style.display = DisplayStyle.None;

            _refineNarrativeTitleLabel = new Label("Refine notes");
            _refineNarrativeTitleLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            _refineNarrativeTitleLabel.style.fontSize = 11f;
            _refineNarrativeTitleLabel.style.marginBottom = 4f;
            _refineNarrativeTitleLabel.style.color = new Color(0.84f, 0.84f, 0.84f, 1f);
            _refineNarrativePanel.Add(_refineNarrativeTitleLabel);

            var narrativeScroll = new ScrollView(ScrollViewMode.Vertical);
            narrativeScroll.style.height = 110f;
            narrativeScroll.style.backgroundColor = new Color(0.10f, 0.10f, 0.11f, 1f);
            narrativeScroll.style.borderTopWidth = 1f;
            narrativeScroll.style.borderBottomWidth = 1f;
            narrativeScroll.style.borderLeftWidth = 1f;
            narrativeScroll.style.borderRightWidth = 1f;
            narrativeScroll.style.borderTopColor = new Color(0.22f, 0.22f, 0.22f, 1f);
            narrativeScroll.style.borderBottomColor = new Color(0.22f, 0.22f, 0.22f, 1f);
            narrativeScroll.style.borderLeftColor = new Color(0.22f, 0.22f, 0.22f, 1f);
            narrativeScroll.style.borderRightColor = new Color(0.22f, 0.22f, 0.22f, 1f);

            _refineNarrativeLabel = new Label(string.Empty);
            _refineNarrativeLabel.style.whiteSpace = WhiteSpace.Normal;
            _refineNarrativeLabel.style.unityTextAlign = TextAnchor.UpperLeft;
            _refineNarrativeLabel.style.fontSize = 12f;
            _refineNarrativeLabel.style.color = new Color(0.86f, 0.86f, 0.86f, 1f);
            _refineNarrativeLabel.style.paddingTop = 6f;
            _refineNarrativeLabel.style.paddingBottom = 6f;
            _refineNarrativeLabel.style.paddingLeft = 6f;
            _refineNarrativeLabel.style.paddingRight = 6f;
            narrativeScroll.Add(_refineNarrativeLabel);

            _refineNarrativePanel.Add(narrativeScroll);
            rootVisualElement.Add(_refineNarrativePanel);
        }

        private void RefreshUI()
        {
            EnsureRefineHandler();
            EnsureTabsInitialized();
            var activeTab = GetActiveTab();

            if (_titleLabel != null)
            {
                _titleLabel.text = _diffTitle ?? "Diff Preview";
            }

            if (_metaLabel != null)
            {
                _metaLabel.text = BuildMetaText(activeTab);
            }

            if (_applyButton != null)
            {
                _applyButton.SetEnabled(!_isRefining && activeTab != null && activeTab.Patch != null && HasApplicableDiff(activeTab.DiffText));
            }

            if (_refineButton != null)
            {
                _refineButton.SetEnabled(!_isRefining && activeTab != null && HasApplicableDiff(activeTab.DiffText));
                _refineButton.tooltip = _refineRequestHandler != null
                    ? "Request another refactor pass before apply"
                    : "Open Codex Chat window and run a diff turn first";
            }

            if (_refineInputField != null)
            {
                _refineInputField.SetEnabled(!_isRefining && activeTab != null);
            }

            RenderTabStrip();
            RenderDiffLines(activeTab?.DiffText);
            RenderRefineNarrative(activeTab);
        }

        private void EnsureTabsInitialized()
        {
            if (_tabsInitialized)
            {
                ClampActiveTabIndex();
                return;
            }

            _tabsInitialized = true;
            _tabs.Clear();
            _activeTabIndex = 0;
            _tabBuildError = string.Empty;
            _initialTabCount = 0;

            var source = _diffText ?? string.Empty;
            if (!HasApplicableDiff(source))
            {
                return;
            }

            if (!BuildTabsFromDiff(source, _tabs, out var parseError))
            {
                _tabBuildError = parseError ?? string.Empty;
                _tabs.Add(new UniCodexDiffTabState
                {
                    Id = Guid.NewGuid().ToString("N"),
                    DisplayName = "RAW Response",
                    Patch = null,
                    DiffText = NormalizeLineEndings(source),
                    Status = DiffTabStatus.Error
                });
            }

            _initialTabCount = _tabs.Count;
            ClampActiveTabIndex();
        }

        private bool BuildTabsFromDiff(string diffText, List<UniCodexDiffTabState> outputTabs, out string error)
        {
            error = string.Empty;
            outputTabs?.Clear();
            if (outputTabs == null)
            {
                error = "Output tab collection is null.";
                return false;
            }

            if (!TryParseUnifiedDiff(diffText, out var patches, out error))
            {
                return false;
            }

            for (var i = 0; i < patches.Count; i++)
            {
                var patch = patches[i];
                if (patch == null)
                {
                    continue;
                }

                outputTabs.Add(CreateTabStateFromPatch(patch));
            }

            if (outputTabs.Count == 0)
            {
                error = "No valid file patch could be built.";
                return false;
            }

            return true;
        }

        private void RenderRefineNarrative(UniCodexDiffTabState activeTab)
        {
            if (_refineNarrativePanel == null || _refineNarrativeLabel == null || _refineNarrativeTitleLabel == null)
            {
                return;
            }

            var narrative = activeTab?.RefineNarrative;
            if (string.IsNullOrWhiteSpace(narrative))
            {
                _refineNarrativePanel.style.display = DisplayStyle.None;
                _refineNarrativeLabel.text = string.Empty;
                return;
            }

            _refineNarrativePanel.style.display = DisplayStyle.Flex;
            _refineNarrativeTitleLabel.text = "Refine notes";
            _refineNarrativeLabel.text = DecodeBasicHtmlEntities(narrative.Trim());
        }

        private static UniCodexDiffTabState CreateTabStateFromPatch(UniCodexFilePatch patch)
        {
            return new UniCodexDiffTabState
            {
                Id = Guid.NewGuid().ToString("N"),
                DisplayName = BuildPatchDisplayName(patch),
                Patch = patch,
                DiffText = BuildPatchUnifiedDiffText(patch),
                RefineNarrative = string.Empty,
                Status = DiffTabStatus.Pending
            };
        }

        private static string BuildPatchDisplayName(UniCodexFilePatch patch)
        {
            if (patch == null)
            {
                return "Unknown";
            }

            var changeType = GetPatchChangeType(patch);
            if (changeType == "R")
            {
                return $"R {patch.OldPath} -> {patch.NewPath}";
            }

            var path = string.IsNullOrWhiteSpace(patch.NewPath) ? patch.OldPath : patch.NewPath;
            return $"{changeType} {path}";
        }

        private static string GetPatchChangeType(UniCodexFilePatch patch)
        {
            if (patch == null)
            {
                return "?";
            }

            var hasOld = !string.IsNullOrWhiteSpace(patch.OldPath);
            var hasNew = !string.IsNullOrWhiteSpace(patch.NewPath);
            if (!hasOld && hasNew)
            {
                return "A";
            }

            if (hasOld && !hasNew)
            {
                return "D";
            }

            if (hasOld && hasNew && !string.Equals(patch.OldPath, patch.NewPath, StringComparison.Ordinal))
            {
                return "R";
            }

            return "M";
        }

        private static string BuildPatchUnifiedDiffText(UniCodexFilePatch patch)
        {
            if (patch == null)
            {
                return NoChangesToken;
            }

            var oldToken = string.IsNullOrWhiteSpace(patch.OldPath) ? "/dev/null" : $"a/{patch.OldPath}";
            var newToken = string.IsNullOrWhiteSpace(patch.NewPath) ? "/dev/null" : $"b/{patch.NewPath}";
            var sb = new StringBuilder();
            sb.Append("diff --git ").Append(oldToken).Append(' ').Append(newToken).Append('\n');
            sb.Append("--- ").Append(oldToken).Append('\n');
            sb.Append("+++ ").Append(newToken).Append('\n');

            for (var i = 0; i < patch.Hunks.Count; i++)
            {
                var hunk = patch.Hunks[i];
                var oldCount = 0;
                var newCount = 0;
                for (var lineIndex = 0; lineIndex < hunk.Lines.Count; lineIndex++)
                {
                    var prefix = hunk.Lines[lineIndex].Prefix;
                    if (prefix == ' ' || prefix == '-')
                    {
                        oldCount++;
                    }

                    if (prefix == ' ' || prefix == '+')
                    {
                        newCount++;
                    }
                }

                sb.Append("@@ -")
                    .Append(hunk.OldStart)
                    .Append(',')
                    .Append(oldCount)
                    .Append(" +")
                    .Append(hunk.NewStart)
                    .Append(',')
                    .Append(newCount)
                    .Append(" @@\n");

                for (var lineIndex = 0; lineIndex < hunk.Lines.Count; lineIndex++)
                {
                    var line = hunk.Lines[lineIndex];
                    sb.Append(line.Prefix);
                    sb.Append(line.Text ?? string.Empty);
                    sb.Append('\n');
                }
            }

            return sb.ToString().TrimEnd('\n', '\r');
        }

        private UniCodexDiffTabState GetActiveTab()
        {
            if (_tabs.Count == 0)
            {
                return null;
            }

            ClampActiveTabIndex();
            return _tabs[_activeTabIndex];
        }

        private void ClampActiveTabIndex()
        {
            if (_tabs.Count == 0)
            {
                _activeTabIndex = 0;
                return;
            }

            _activeTabIndex = Mathf.Clamp(_activeTabIndex, 0, _tabs.Count - 1);
        }

        private string BuildMetaText(UniCodexDiffTabState activeTab)
        {
            var pendingCount = _tabs.Count;
            var totalCount = Math.Max(_initialTabCount, pendingCount);
            if (pendingCount == 0 || activeTab == null)
            {
                return $"Pending 0 / Total {totalCount} | No changes";
            }

            CalculateDiffLineStats(activeTab.DiffText, out var added, out var removed, out var lineCount);
            var statusText = activeTab.Status == DiffTabStatus.Error
                ? "Error"
                : activeTab.Status == DiffTabStatus.Applying
                    ? "Applying"
                    : "Pending";
            var text = $"Pending {pendingCount} / Total {totalCount} | Active: {activeTab.DisplayName} | +{added} / -{removed} | Lines: {lineCount} | {statusText}";
            if (!string.IsNullOrWhiteSpace(_tabBuildError) && activeTab.Patch == null)
            {
                text += " | Parse warning";
            }

            return text;
        }

        private static void CalculateDiffLineStats(string diffText, out int added, out int removed, out int lineCount)
        {
            added = 0;
            removed = 0;
            lineCount = 0;
            if (string.IsNullOrWhiteSpace(diffText))
            {
                return;
            }

            var normalized = NormalizeLineEndings(diffText);
            var lines = normalized.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (i == lines.Length - 1 && line.Length == 0)
                {
                    continue;
                }

                lineCount++;
                if (line.StartsWith("+++ ", StringComparison.Ordinal) || line.StartsWith("--- ", StringComparison.Ordinal))
                {
                    continue;
                }

                if (line.StartsWith("+", StringComparison.Ordinal))
                {
                    added++;
                    continue;
                }

                if (line.StartsWith("-", StringComparison.Ordinal))
                {
                    removed++;
                }
            }
        }

        private void RenderTabStrip()
        {
            if (_tabStripRoot == null || _tabScrollView == null)
            {
                return;
            }

            _tabStripRoot.Clear();
            if (_tabs.Count == 0)
            {
                _tabScrollView.style.display = DisplayStyle.None;
                return;
            }

            _tabScrollView.style.display = DisplayStyle.Flex;
            for (var i = 0; i < _tabs.Count; i++)
            {
                var tab = _tabs[i];
                if (tab == null)
                {
                    continue;
                }

                var index = i;
                var button = new Button(() => SelectTab(index))
                {
                    text = BuildTabButtonText(tab)
                };
                button.style.height = 22f;
                button.style.marginRight = 4f;
                button.style.paddingLeft = 8f;
                button.style.paddingRight = 8f;
                button.style.unityTextAlign = TextAnchor.MiddleCenter;
                button.style.unityFontStyleAndWeight = _activeTabIndex == index ? FontStyle.Bold : FontStyle.Normal;
                button.style.backgroundColor = GetTabBackgroundColor(tab, _activeTabIndex == index);
                button.style.color = new Color(0.92f, 0.92f, 0.92f, 1f);
                button.style.borderTopWidth = 1f;
                button.style.borderBottomWidth = 1f;
                button.style.borderLeftWidth = 1f;
                button.style.borderRightWidth = 1f;
                button.style.borderTopColor = new Color(0.25f, 0.25f, 0.25f, 1f);
                button.style.borderBottomColor = new Color(0.25f, 0.25f, 0.25f, 1f);
                button.style.borderLeftColor = new Color(0.25f, 0.25f, 0.25f, 1f);
                button.style.borderRightColor = new Color(0.25f, 0.25f, 0.25f, 1f);
                button.SetEnabled(!_isRefining);
                _tabStripRoot.Add(button);
            }
        }

        private static string BuildTabButtonText(UniCodexDiffTabState tab)
        {
            if (tab == null)
            {
                return "Unknown";
            }

            switch (tab.Status)
            {
                case DiffTabStatus.Applying:
                    return $"... {tab.DisplayName}";
                case DiffTabStatus.Error:
                    return $"! {tab.DisplayName}";
                default:
                    return tab.DisplayName;
            }
        }

        private static Color GetTabBackgroundColor(UniCodexDiffTabState tab, bool isActive)
        {
            if (tab != null && tab.Status == DiffTabStatus.Error)
            {
                return isActive ? new Color(0.45f, 0.16f, 0.16f, 1f) : new Color(0.35f, 0.14f, 0.14f, 1f);
            }

            if (tab != null && tab.Status == DiffTabStatus.Applying)
            {
                return isActive ? new Color(0.46f, 0.34f, 0.12f, 1f) : new Color(0.38f, 0.29f, 0.10f, 1f);
            }

            return isActive ? new Color(0.18f, 0.34f, 0.68f, 1f) : new Color(0.22f, 0.22f, 0.24f, 1f);
        }

        private void SelectTab(int index)
        {
            if (_isRefining)
            {
                return;
            }

            if (_tabs.Count == 0)
            {
                _activeTabIndex = 0;
                return;
            }

            _activeTabIndex = Mathf.Clamp(index, 0, _tabs.Count - 1);
            RefreshUI();
        }

        private void RenderDiffLines(string diffText)
        {
            if (_diffContent == null)
            {
                return;
            }

            _diffContent.Clear();
            var text = string.IsNullOrWhiteSpace(diffText) ? NoChangesToken : NormalizeLineEndings(diffText);
            var lines = text.Split('\n');
            var oldLine = -1;
            var newLine = -1;
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (i == lines.Length - 1 && line.Length == 0)
                {
                    continue;
                }

                var kind = GetLineKind(line);
                if (kind == DiffLineKind.FileHeader || kind == DiffLineKind.Meta)
                {
                    oldLine = -1;
                    newLine = -1;
                }
                else if (kind == DiffLineKind.HunkHeader)
                {
                    if (TryParseHunkHeader(line, out var parsedOldStart, out var parsedNewStart))
                    {
                        oldLine = Mathf.Max(0, parsedOldStart);
                        newLine = Mathf.Max(0, parsedNewStart);
                    }
                    else
                    {
                        oldLine = -1;
                        newLine = -1;
                    }
                }

                var oldLineText = string.Empty;
                var newLineText = string.Empty;
                switch (kind)
                {
                    case DiffLineKind.Context:
                        if (oldLine > 0)
                        {
                            oldLineText = oldLine.ToString();
                            oldLine++;
                        }

                        if (newLine > 0)
                        {
                            newLineText = newLine.ToString();
                            newLine++;
                        }
                        break;
                    case DiffLineKind.Removed:
                        if (oldLine > 0)
                        {
                            oldLineText = oldLine.ToString();
                            oldLine++;
                        }
                        break;
                    case DiffLineKind.Added:
                        if (newLine > 0)
                        {
                            newLineText = newLine.ToString();
                            newLine++;
                        }
                        break;
                }

                _diffContent.Add(CreateLineElement(line, kind, oldLineText, newLineText));
            }

            if (_diffContent.childCount == 0)
            {
                _diffContent.Add(CreateLineElement(NoChangesToken, DiffLineKind.Neutral, string.Empty, string.Empty));
            }
        }

        private VisualElement CreateLineElement(string sourceLine, DiffLineKind kind, string oldLineText, string newLineText)
        {
            var displayLine = GetDisplayLine(sourceLine, kind);

            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.flexShrink = 0f;
            row.style.paddingLeft = 4f;
            row.style.paddingRight = 8f;
            row.style.paddingTop = 1f;
            row.style.paddingBottom = 1f;
            row.style.minHeight = 18f;

            switch (kind)
            {
                case DiffLineKind.Added:
                    row.style.backgroundColor = new Color(0.10f, 0.32f, 0.16f, 0.60f);
                    break;
                case DiffLineKind.Removed:
                    row.style.backgroundColor = new Color(0.38f, 0.13f, 0.13f, 0.60f);
                    break;
                case DiffLineKind.HunkHeader:
                    row.style.backgroundColor = new Color(0.20f, 0.24f, 0.30f, 0.55f);
                    break;
                case DiffLineKind.FileHeader:
                    row.style.backgroundColor = new Color(0.18f, 0.18f, 0.20f, 0.65f);
                    break;
                case DiffLineKind.Meta:
                    row.style.backgroundColor = new Color(0.16f, 0.16f, 0.18f, 0.45f);
                    break;
            }

            row.Add(CreateLineNumberLabel(oldLineText, kind, false));
            row.Add(CreateLineNumberLabel(newLineText, kind, true));

            var renderedLine = string.IsNullOrEmpty(displayLine)
                ? "\u00A0"
                : ConvertWhitespaceForDiffDisplay(displayLine);
            var text = new Label(renderedLine);
            text.style.whiteSpace = WhiteSpace.NoWrap;
            text.style.unityTextAlign = TextAnchor.MiddleLeft;
            text.style.fontSize = 12f;
            text.style.flexShrink = 0f;
            text.style.color = GetLineColor(kind);
            text.style.marginLeft = 6f;
            if (_monoFont != null)
            {
                text.style.unityFont = _monoFont;
            }

            row.Add(text);
            return row;
        }

        private Label CreateLineNumberLabel(string lineNumberText, DiffLineKind kind, bool isNewColumn)
        {
            var label = new Label(string.IsNullOrWhiteSpace(lineNumberText) ? "\u00A0" : lineNumberText);
            label.style.width = LineNumberColumnWidth;
            label.style.minWidth = LineNumberColumnWidth;
            label.style.maxWidth = LineNumberColumnWidth;
            label.style.unityTextAlign = TextAnchor.MiddleRight;
            label.style.fontSize = 11f;
            label.style.whiteSpace = WhiteSpace.NoWrap;
            label.style.color = GetLineNumberColor(kind, isNewColumn);
            if (_monoFont != null)
            {
                label.style.unityFont = _monoFont;
            }

            return label;
        }

        private static Color GetLineNumberColor(DiffLineKind kind, bool isNewColumn)
        {
            switch (kind)
            {
                case DiffLineKind.Added:
                    return isNewColumn
                        ? new Color(0.62f, 0.86f, 0.66f, 1f)
                        : new Color(0.45f, 0.45f, 0.45f, 0.7f);
                case DiffLineKind.Removed:
                    return isNewColumn
                        ? new Color(0.45f, 0.45f, 0.45f, 0.7f)
                        : new Color(0.92f, 0.66f, 0.66f, 1f);
                case DiffLineKind.Context:
                    return new Color(0.56f, 0.56f, 0.56f, 0.95f);
                default:
                    return new Color(0.48f, 0.48f, 0.48f, 0.9f);
            }
        }

        private static Color GetLineColor(DiffLineKind kind)
        {
            switch (kind)
            {
                case DiffLineKind.Added:
                    return new Color(0.82f, 0.95f, 0.83f, 1f);
                case DiffLineKind.Removed:
                    return new Color(1f, 0.85f, 0.85f, 1f);
                case DiffLineKind.HunkHeader:
                    return new Color(0.80f, 0.86f, 0.98f, 1f);
                case DiffLineKind.FileHeader:
                    return new Color(0.95f, 0.95f, 0.95f, 1f);
                case DiffLineKind.Meta:
                    return new Color(0.80f, 0.80f, 0.80f, 1f);
                default:
                    return new Color(0.88f, 0.88f, 0.88f, 1f);
            }
        }

        private static DiffLineKind GetLineKind(string line)
        {
            if (string.IsNullOrWhiteSpace(line) || string.Equals(line.Trim(), NoChangesToken, StringComparison.OrdinalIgnoreCase))
            {
                return DiffLineKind.Neutral;
            }

            if (line.StartsWith("diff --git ", StringComparison.Ordinal)
                || line.StartsWith("index ", StringComparison.Ordinal)
                || line.StartsWith("new file mode ", StringComparison.Ordinal)
                || line.StartsWith("deleted file mode ", StringComparison.Ordinal)
                || line.StartsWith("rename from ", StringComparison.Ordinal)
                || line.StartsWith("rename to ", StringComparison.Ordinal)
                || line.StartsWith("Binary files ", StringComparison.Ordinal))
            {
                return DiffLineKind.Meta;
            }

            if (line.StartsWith("--- ", StringComparison.Ordinal) || line.StartsWith("+++ ", StringComparison.Ordinal))
            {
                return DiffLineKind.FileHeader;
            }

            if (line.StartsWith("@@ ", StringComparison.Ordinal))
            {
                return DiffLineKind.HunkHeader;
            }

            if (line.StartsWith("+", StringComparison.Ordinal) && !line.StartsWith("+++ ", StringComparison.Ordinal))
            {
                return DiffLineKind.Added;
            }

            if (line.StartsWith("-", StringComparison.Ordinal) && !line.StartsWith("--- ", StringComparison.Ordinal))
            {
                return DiffLineKind.Removed;
            }

            if (line.StartsWith(" ", StringComparison.Ordinal))
            {
                return DiffLineKind.Context;
            }

            return DiffLineKind.Neutral;
        }

        private static string GetDisplayLine(string sourceLine, DiffLineKind kind)
        {
            if (string.IsNullOrEmpty(sourceLine))
            {
                return string.Empty;
            }

            switch (kind)
            {
                case DiffLineKind.Added:
                case DiffLineKind.Removed:
                case DiffLineKind.Context:
                    return sourceLine.Length > 1 ? sourceLine.Substring(1) : string.Empty;
                default:
                    return sourceLine;
            }
        }

        private static string ConvertWhitespaceForDiffDisplay(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            var sb = new StringBuilder(text.Length + 8);
            for (var i = 0; i < text.Length; i++)
            {
                var ch = text[i];
                if (ch == ' ')
                {
                    sb.Append('\u00A0');
                }
                else if (ch == '\t')
                {
                    sb.Append("\u00A0\u00A0\u00A0\u00A0");
                }
                else
                {
                    sb.Append(ch);
                }
            }

            return sb.ToString();
        }

        private void RequestRefineDiff()
        {
            if (_isRefining)
            {
                return;
            }

            var activeTab = GetActiveTab();
            if (activeTab == null)
            {
                ShowNotification(new GUIContent("No active tab to refine"));
                return;
            }

            EnsureRefineHandler();
            if (_refineRequestHandler == null)
            {
                UniCodexChatWindow.OpenWindow();
                EnsureRefineHandler();
            }

            if (_refineRequestHandler == null)
            {
                ShowNotification(new GUIContent("Refine unavailable: run a new diff turn from Codex Chat"));
                return;
            }

            if (!HasApplicableDiff(activeTab.DiffText))
            {
                ShowNotification(new GUIContent("No diff to refine"));
                return;
            }

            var refineInstruction = _refineInputField?.value?.Trim();
            if (string.IsNullOrWhiteSpace(refineInstruction))
            {
                ShowNotification(new GUIContent("Enter a refine request"));
                return;
            }

            _isRefining = true;
            activeTab.Status = DiffTabStatus.Applying;
            RefreshUI();
            if (_metaLabel != null)
            {
                _metaLabel.text = "Refining with Codex...";
            }

            Task<UniCodexRunResult> task;
            try
            {
                task = _refineRequestHandler.Invoke(activeTab.DiffText ?? string.Empty, refineInstruction);
            }
            catch (Exception ex)
            {
                _isRefining = false;
                activeTab.Status = DiffTabStatus.Error;
                RefreshUI();
                EditorUtility.DisplayDialog("Codex Diff Preview", $"Refine failed:\n{ex.Message}", "OK");
                return;
            }

            if (task == null)
            {
                _isRefining = false;
                activeTab.Status = DiffTabStatus.Error;
                RefreshUI();
                EditorUtility.DisplayDialog("Codex Diff Preview", "Refine failed: empty task.", "OK");
                return;
            }

            task.ContinueWith(t =>
            {
                var result = t.IsFaulted
                    ? UniCodexRunResult.FromError(t.Exception?.GetBaseException().Message ?? "Unknown refine error")
                    : t.Result;
                EditorApplication.delayCall += () => HandleRefineResult(result);
            });
        }

        private void HandleRefineResult(UniCodexRunResult result)
        {
            _isRefining = false;
            var activeTab = GetActiveTab();
            if (activeTab == null)
            {
                RefreshUI();
                EditorUtility.DisplayDialog("Codex Diff Preview", "Refine failed: no active tab.", "OK");
                return;
            }

            if (result == null)
            {
                activeTab.Status = DiffTabStatus.Error;
                RefreshUI();
                EditorUtility.DisplayDialog("Codex Diff Preview", "Refine failed: no result.", "OK");
                return;
            }

            if (!result.Success)
            {
                activeTab.Status = DiffTabStatus.Error;
                RefreshUI();
                var message = string.IsNullOrWhiteSpace(result.Message) ? "Unknown refine error." : result.Message;
                EditorUtility.DisplayDialog("Codex Diff Preview", $"Refine failed:\n{message}", "OK");
                return;
            }

            var responseText = string.IsNullOrWhiteSpace(result.Message) ? NoChangesToken : result.Message;
            var refineNarrative = ExtractDiffPreviewNarrative(responseText);
            if (string.Equals(responseText.Trim(), NoChangesToken, StringComparison.OrdinalIgnoreCase))
            {
                activeTab.RefineNarrative = string.IsNullOrWhiteSpace(refineNarrative)
                    ? string.Empty
                    : refineNarrative.Trim();
                activeTab.Status = DiffTabStatus.Pending;
                if (_refineInputField != null)
                {
                    _refineInputField.value = string.Empty;
                }

                RefreshUI();
                ShowNotification(new GUIContent("Refine complete: no additional changes"));
                return;
            }

            var diffText = ExtractUnifiedDiffBlock(responseText);
            if (string.IsNullOrWhiteSpace(diffText))
            {
                activeTab.Status = DiffTabStatus.Error;
                RefreshUI();
                EditorUtility.DisplayDialog("Codex Diff Preview", "Refine response did not contain a valid diff.", "OK");
                return;
            }

            if (!TryParseUnifiedDiff(diffText, out var refinedPatches, out var parseError))
            {
                activeTab.Status = DiffTabStatus.Error;
                RefreshUI();
                EditorUtility.DisplayDialog("Codex Diff Preview", $"Refine parse failed:\n{parseError}", "OK");
                return;
            }

            if (refinedPatches.Count != 1)
            {
                activeTab.Status = DiffTabStatus.Error;
                RefreshUI();
                EditorUtility.DisplayDialog("Codex Diff Preview", "Refine must return exactly one file diff for the active tab.", "OK");
                return;
            }

            var refinedPatch = refinedPatches[0];
            activeTab.Patch = refinedPatch;
            activeTab.DisplayName = BuildPatchDisplayName(refinedPatch);
            activeTab.DiffText = BuildPatchUnifiedDiffText(refinedPatch);
            activeTab.RefineNarrative = string.IsNullOrWhiteSpace(refineNarrative)
                ? string.Empty
                : refineNarrative.Trim();
            activeTab.Status = DiffTabStatus.Pending;
            if (_refineInputField != null)
            {
                _refineInputField.value = string.Empty;
            }

            RefreshUI();
            ShowNotification(new GUIContent("Refine complete"));
        }

        private static string ExtractUnifiedDiffBlock(string responseText)
        {
            if (string.IsNullOrWhiteSpace(responseText))
            {
                return string.Empty;
            }

            var fencedBlocks = Regex.Matches(responseText, "```(?:diff|patch)?\\s*\\n([\\s\\S]*?)```", RegexOptions.IgnoreCase);
            var collected = new StringBuilder();
            for (var i = 0; i < fencedBlocks.Count; i++)
            {
                var block = fencedBlocks[i].Groups.Count > 1 ? fencedBlocks[i].Groups[1].Value : string.Empty;
                if (LooksLikeUnifiedDiff(block))
                {
                    if (collected.Length > 0)
                    {
                        collected.Append('\n');
                    }

                    collected.Append(block.Trim());
                }
            }

            if (collected.Length > 0)
            {
                return collected.ToString();
            }

            return LooksLikeUnifiedDiff(responseText) ? responseText.Trim() : string.Empty;
        }

        private static string ExtractDiffPreviewNarrative(string responseText)
        {
            if (string.IsNullOrWhiteSpace(responseText))
            {
                return string.Empty;
            }

            var trimmed = NormalizeLineEndings(responseText).Trim();
            if (string.Equals(trimmed, NoChangesToken, StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            var lines = trimmed.Split('\n');
            var sb = new StringBuilder(trimmed.Length);
            var inFence = false;
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i] ?? string.Empty;
                var lineTrimmed = line.Trim();

                if (IsFenceDelimiter(lineTrimmed))
                {
                    inFence = !inFence;
                    continue;
                }

                if (inFence)
                {
                    continue;
                }

                if (IsUnifiedDiffMarkerLine(lineTrimmed))
                {
                    break;
                }

                if (lineTrimmed.Length == 0)
                {
                    if (sb.Length > 0)
                    {
                        sb.AppendLine();
                    }

                    continue;
                }

                sb.AppendLine(lineTrimmed);
            }

            var narrative = sb.ToString().Trim();
            if (!string.IsNullOrWhiteSpace(narrative))
            {
                return narrative;
            }

            return string.Empty;
        }

        private static bool IsFenceDelimiter(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return false;
            }

            return line.StartsWith("```", StringComparison.Ordinal)
                   || line.StartsWith("~~~", StringComparison.Ordinal);
        }

        private static bool IsUnifiedDiffMarkerLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return false;
            }

            return line.StartsWith("diff --git ", StringComparison.OrdinalIgnoreCase)
                   || line.StartsWith("--- ", StringComparison.Ordinal)
                   || line.StartsWith("+++ ", StringComparison.Ordinal)
                   || line.StartsWith("@@ ", StringComparison.Ordinal)
                   || line.StartsWith("index ", StringComparison.Ordinal)
                   || line.StartsWith("new file mode ", StringComparison.Ordinal)
                   || line.StartsWith("deleted file mode ", StringComparison.Ordinal)
                   || line.StartsWith("rename from ", StringComparison.Ordinal)
                   || line.StartsWith("rename to ", StringComparison.Ordinal);
        }

        private static string DecodeBasicHtmlEntities(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            return text
                .Replace("&gt;", ">")
                .Replace("&lt;", "<")
                .Replace("&amp;", "&")
                .Replace("&quot;", "\"")
                .Replace("&#39;", "'");
        }

        private static bool LooksLikeUnifiedDiff(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            return text.IndexOf("diff --git ", StringComparison.OrdinalIgnoreCase) >= 0
                   || ((text.StartsWith("--- ", StringComparison.Ordinal) || text.IndexOf("\n--- ", StringComparison.Ordinal) >= 0)
                       && (text.StartsWith("+++ ", StringComparison.Ordinal) || text.IndexOf("\n+++ ", StringComparison.Ordinal) >= 0))
                   || text.StartsWith("@@ ", StringComparison.Ordinal)
                   || text.IndexOf("\n@@ ", StringComparison.Ordinal) >= 0;
        }

        private void EnsureRefineHandler()
        {
            if (_refineRequestHandler != null)
            {
                return;
            }

            _refineRequestHandler = UniCodexChatWindow.TryGetDiffRefineHandler();
        }

        private void ApplyDiffToProject()
        {
            var activeTab = GetActiveTab();
            if (activeTab == null)
            {
                ShowNotification(new GUIContent("No active tab to apply"));
                return;
            }

            if (activeTab.Patch == null || !HasApplicableDiff(activeTab.DiffText))
            {
                ShowNotification(new GUIContent("Active tab has no applicable patch"));
                return;
            }

            activeTab.Status = DiffTabStatus.Applying;
            RefreshUI();
            if (!TryApplyActiveTab(activeTab, out var summary, out var applyError))
            {
                activeTab.Status = DiffTabStatus.Error;
                RefreshUI();
                EditorUtility.DisplayDialog("Codex Diff Preview", $"Apply failed:\n{applyError}", "OK");
                return;
            }

            var manualRefreshMode = EditorPrefs.GetBool(ManualRefreshPrefKey, true);
            if (!manualRefreshMode)
            {
                AssetDatabase.Refresh(ImportAssetOptions.Default);
            }

            var message = manualRefreshMode
                ? $"{summary} (manual refresh mode)"
                : summary;

            var removedTabIndex = _activeTabIndex;
            _tabs.RemoveAt(removedTabIndex);
            if (_tabs.Count <= 0)
            {
                ShowNotification(new GUIContent(message));
                Close();
                return;
            }

            _activeTabIndex = Mathf.Clamp(removedTabIndex, 0, _tabs.Count - 1);
            RefreshUI();
            ShowNotification(new GUIContent($"{message} | Remaining tabs: {_tabs.Count}"));
        }

        private bool TryApplyActiveTab(UniCodexDiffTabState tab, out string summary, out string error)
        {
            summary = string.Empty;
            error = string.Empty;
            if (tab == null || tab.Patch == null)
            {
                error = "No active patch.";
                return false;
            }

            var patch = tab.Patch;
            var isDelete = string.IsNullOrWhiteSpace(patch.NewPath);
            var targetRelativePath = isDelete ? patch.OldPath : patch.NewPath;
            if (!TryResolveSafeProjectPath(targetRelativePath, out var targetAbsolutePath, out error))
            {
                return false;
            }

            if (isDelete)
            {
                if (File.Exists(targetAbsolutePath))
                {
                    File.Delete(targetAbsolutePath);
                }

                summary = $"Applied `{tab.DisplayName}`";
                return true;
            }

            var existedBefore = File.Exists(targetAbsolutePath);
            if (!TryApplySinglePatch(targetAbsolutePath, patch, out error))
            {
                return false;
            }

            var changeSymbol = existedBefore ? "~" : "+";
            summary = $"Applied `{tab.DisplayName}` ({changeSymbol})";
            return true;
        }

        private bool TryApplyPatches(List<UniCodexFilePatch> patches, out string summary, out string error)
        {
            summary = string.Empty;
            error = string.Empty;
            if (patches == null || patches.Count == 0)
            {
                error = "No file patches found.";
                return false;
            }

            var created = 0;
            var modified = 0;
            var deleted = 0;
            var touchedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (var i = 0; i < patches.Count; i++)
            {
                var patch = patches[i];
                var isDelete = string.IsNullOrWhiteSpace(patch.NewPath);
                var targetRelativePath = isDelete ? patch.OldPath : patch.NewPath;
                if (!TryResolveSafeProjectPath(targetRelativePath, out var targetAbsolutePath, out error))
                {
                    return false;
                }

                if (isDelete)
                {
                    if (File.Exists(targetAbsolutePath))
                    {
                        File.Delete(targetAbsolutePath);
                        deleted++;
                    }

                    touchedFiles.Add(targetAbsolutePath);
                    continue;
                }

                var existedBefore = File.Exists(targetAbsolutePath);
                if (!TryApplySinglePatch(targetAbsolutePath, patch, out error))
                {
                    return false;
                }

                if (existedBefore)
                {
                    modified++;
                }
                else
                {
                    created++;
                }

                touchedFiles.Add(targetAbsolutePath);
            }

            summary = $"Applied {touchedFiles.Count} file(s): +{created} / ~{modified} / -{deleted}";
            return true;
        }

        private static bool TryApplySinglePatch(string targetAbsolutePath, UniCodexFilePatch patch, out string error)
        {
            error = string.Empty;
            var existedBefore = File.Exists(targetAbsolutePath);
            var isCreate = string.IsNullOrWhiteSpace(patch.OldPath) && !string.IsNullOrWhiteSpace(patch.NewPath);

            if (!existedBefore && !isCreate)
            {
                error = $"Target file not found: {patch.NewPath ?? patch.OldPath}";
                return false;
            }

            var originalText = existedBefore ? File.ReadAllText(targetAbsolutePath) : string.Empty;
            var newline = originalText.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            if (!TryApplyHunks(originalText, newline, patch.Hunks, out var updatedText, out error))
            {
                var path = patch.NewPath ?? patch.OldPath;
                error = $"{path}: {error}";
                return false;
            }

            var parent = Path.GetDirectoryName(targetAbsolutePath);
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }

            File.WriteAllText(targetAbsolutePath, updatedText, new UTF8Encoding(false));
            return true;
        }

        private static bool TryApplyHunks(string originalText, string newline, List<UniCodexDiffHunk> hunks, out string updatedText, out string error)
        {
            updatedText = originalText ?? string.Empty;
            error = string.Empty;
            if (hunks == null || hunks.Count == 0)
            {
                return true;
            }

            var sourceLines = SplitLines(originalText, out var hadTrailingNewline);
            var outputLines = new List<string>(sourceLines.Count + 32);
            var sourceIndex = 0;

            for (var hunkIndex = 0; hunkIndex < hunks.Count; hunkIndex++)
            {
                var hunk = hunks[hunkIndex];
                var hunkTargetIndex = Math.Max(0, hunk.OldStart - 1);
                if (hunkTargetIndex < sourceIndex)
                {
                    error = $"hunk overlap near old line {hunk.OldStart}.";
                    return false;
                }

                while (sourceIndex < hunkTargetIndex && sourceIndex < sourceLines.Count)
                {
                    outputLines.Add(sourceLines[sourceIndex]);
                    sourceIndex++;
                }

                for (var lineIndex = 0; lineIndex < hunk.Lines.Count; lineIndex++)
                {
                    var hunkLine = hunk.Lines[lineIndex];
                    switch (hunkLine.Prefix)
                    {
                        case ' ':
                            if (!TryMatchSourceLine(sourceLines, sourceIndex, hunkLine.Text, out var mismatchContextError))
                            {
                                error = mismatchContextError;
                                return false;
                            }

                            outputLines.Add(sourceLines[sourceIndex]);
                            sourceIndex++;
                            break;
                        case '-':
                            if (!TryMatchSourceLine(sourceLines, sourceIndex, hunkLine.Text, out var mismatchRemoveError))
                            {
                                error = mismatchRemoveError;
                                return false;
                            }

                            sourceIndex++;
                            break;
                        case '+':
                            outputLines.Add(hunkLine.Text);
                            break;
                        default:
                            error = $"unsupported hunk line prefix `{hunkLine.Prefix}`.";
                            return false;
                    }
                }
            }

            while (sourceIndex < sourceLines.Count)
            {
                outputLines.Add(sourceLines[sourceIndex]);
                sourceIndex++;
            }

            // Preserve original trailing newline for existing files.
            // For brand-new files (no original content), keep newline-at-EOF for readability.
            var shouldHaveTrailingNewline = hadTrailingNewline || sourceLines.Count == 0;
            updatedText = JoinLines(outputLines, newline, shouldHaveTrailingNewline);
            return true;
        }

        private static bool TryMatchSourceLine(List<string> sourceLines, int sourceIndex, string expected, out string error)
        {
            error = string.Empty;
            if (sourceIndex >= sourceLines.Count)
            {
                error = $"patch mismatch: expected `{expected}` but reached end of file.";
                return false;
            }

            var actual = sourceLines[sourceIndex];
            if (!string.Equals(actual, expected, StringComparison.Ordinal))
            {
                error = $"patch mismatch at line {sourceIndex + 1}: expected `{expected}`, actual `{actual}`.";
                return false;
            }

            return true;
        }

        private static List<string> SplitLines(string text, out bool hadTrailingNewline)
        {
            var normalized = NormalizeLineEndings(text);
            hadTrailingNewline = normalized.EndsWith("\n", StringComparison.Ordinal);
            if (string.IsNullOrEmpty(normalized))
            {
                return new List<string>();
            }

            var lines = new List<string>(normalized.Split('\n'));
            if (hadTrailingNewline && lines.Count > 0 && lines[lines.Count - 1].Length == 0)
            {
                lines.RemoveAt(lines.Count - 1);
            }

            return lines;
        }

        private static string JoinLines(List<string> lines, string newline, bool trailingNewline)
        {
            if (lines == null || lines.Count == 0)
            {
                return string.Empty;
            }

            var sb = new StringBuilder(lines.Count * 24);
            for (var i = 0; i < lines.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(newline);
                }

                sb.Append(lines[i]);
            }

            if (trailingNewline)
            {
                sb.Append(newline);
            }

            return sb.ToString();
        }

        private bool TryParseUnifiedDiff(string diffText, out List<UniCodexFilePatch> patches, out string error)
        {
            patches = new List<UniCodexFilePatch>();
            error = string.Empty;
            var normalized = NormalizeLineEndings(diffText);
            var lines = normalized.Split('\n');
            var cursor = 0;

            while (cursor < lines.Length)
            {
                if (!IsUnifiedFileHeader(lines, cursor))
                {
                    cursor++;
                    continue;
                }

                var patch = new UniCodexFilePatch
                {
                    OldPath = ParseDiffPathToken(lines[cursor]),
                    NewPath = ParseDiffPathToken(lines[cursor + 1])
                };
                cursor += 2;

                while (cursor < lines.Length && !IsUnifiedFileHeader(lines, cursor))
                {
                    var line = lines[cursor];
                    if (!line.StartsWith("@@ ", StringComparison.Ordinal))
                    {
                        cursor++;
                        continue;
                    }

                    if (!TryParseHunkHeader(line, out var oldStart, out var newStart))
                    {
                        error = $"Invalid hunk header: `{line}`";
                        return false;
                    }

                    var hunk = new UniCodexDiffHunk
                    {
                        OldStart = oldStart,
                        NewStart = newStart
                    };
                    cursor++;

                    while (cursor < lines.Length
                           && !IsUnifiedFileHeader(lines, cursor)
                           && !lines[cursor].StartsWith("@@ ", StringComparison.Ordinal))
                    {
                        var hunkLine = lines[cursor];
                        if (hunkLine.StartsWith("\\ No newline at end of file", StringComparison.Ordinal))
                        {
                            cursor++;
                            continue;
                        }

                        if (hunkLine.Length == 0)
                        {
                            cursor++;
                            continue;
                        }

                        var prefix = hunkLine[0];
                        if (prefix != ' ' && prefix != '+' && prefix != '-')
                        {
                            break;
                        }

                        hunk.Lines.Add(new HunkLine
                        {
                            Prefix = prefix,
                            Text = hunkLine.Length > 1 ? hunkLine.Substring(1) : string.Empty
                        });
                        cursor++;
                    }

                    patch.Hunks.Add(hunk);
                }

                if (!string.IsNullOrWhiteSpace(patch.OldPath) || !string.IsNullOrWhiteSpace(patch.NewPath))
                {
                    patches.Add(patch);
                }
            }

            if (patches.Count == 0)
            {
                error = "No unified diff file sections (`---` / `+++`) were found.";
                return false;
            }

            return true;
        }

        private static bool TryParseHunkHeader(string line, out int oldStart, out int newStart)
        {
            oldStart = 0;
            newStart = 0;
            var match = HunkHeaderRegex.Match(line);
            if (!match.Success)
            {
                return false;
            }

            oldStart = ParsePositiveInt(match.Groups[1].Value, 0);
            newStart = ParsePositiveInt(match.Groups[3].Value, 0);
            return oldStart >= 0 && newStart >= 0;
        }

        private static int ParsePositiveInt(string raw, int fallback)
        {
            if (int.TryParse(raw, out var value))
            {
                return value;
            }

            return fallback;
        }

        private static bool IsUnifiedFileHeader(string[] lines, int index)
        {
            return index >= 0
                   && index + 1 < lines.Length
                   && lines[index].StartsWith("--- ", StringComparison.Ordinal)
                   && lines[index + 1].StartsWith("+++ ", StringComparison.Ordinal);
        }

        private static string ParseDiffPathToken(string headerLine)
        {
            if (string.IsNullOrWhiteSpace(headerLine))
            {
                return string.Empty;
            }

            var raw = headerLine.Length > 4 ? headerLine.Substring(4).Trim() : string.Empty;
            if (string.IsNullOrWhiteSpace(raw))
            {
                return string.Empty;
            }

            var splitIndex = raw.IndexOfAny(new[] { '\t', ' ' });
            if (splitIndex >= 0)
            {
                raw = raw.Substring(0, splitIndex);
            }

            if (string.Equals(raw, "/dev/null", StringComparison.Ordinal))
            {
                return string.Empty;
            }

            if (raw.StartsWith("a/", StringComparison.Ordinal) || raw.StartsWith("b/", StringComparison.Ordinal))
            {
                raw = raw.Substring(2);
            }

            return raw.Replace('\\', '/');
        }

        private static bool TryResolveSafeProjectPath(string relativePath, out string absolutePath, out string error)
        {
            absolutePath = string.Empty;
            error = string.Empty;
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                error = "Patch path is empty.";
                return false;
            }

            var normalized = relativePath.Replace('\\', '/').Trim();
            while (normalized.StartsWith("/", StringComparison.Ordinal))
            {
                normalized = normalized.Substring(1);
            }

            var segments = normalized.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < segments.Length; i++)
            {
                var segment = segments[i];
                if (segment == "." || segment == "..")
                {
                    error = $"Unsafe path segment in patch path: `{relativePath}`";
                    return false;
                }
            }

            var root = Path.GetFullPath(UniCodexChatHelper.GetProjectRootPath()).Replace('\\', '/');
            absolutePath = Path.GetFullPath(Path.Combine(root, normalized)).Replace('\\', '/');
            var inProject = absolutePath.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(absolutePath, root, StringComparison.OrdinalIgnoreCase);
            if (!inProject)
            {
                error = $"Patch path is outside project: `{relativePath}`";
                return false;
            }

            return true;
        }

        private static bool HasApplicableDiff(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            return !string.Equals(text.Trim(), NoChangesToken, StringComparison.OrdinalIgnoreCase);
        }

        private static int CountLines(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return 0;
            }

            var count = 1;
            for (var i = 0; i < text.Length; i++)
            {
                if (text[i] == '\n')
                {
                    count++;
                }
            }

            return count;
        }

        private static int CountDiffFiles(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return 0;
            }

            var normalized = NormalizeLineEndings(text);
            var lines = normalized.Split('\n');
            var count = 0;
            for (var i = 0; i + 1 < lines.Length; i++)
            {
                if (lines[i].StartsWith("--- ", StringComparison.Ordinal)
                    && lines[i + 1].StartsWith("+++ ", StringComparison.Ordinal))
                {
                    count++;
                }
            }

            return count;
        }

        private static string NormalizeLineEndings(string text)
        {
            return (text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
        }

        private static Font TryGetMonospaceFont()
        {
            try
            {
                return Font.CreateDynamicFontFromOSFont(
                    new[]
                    {
                        "SF Mono",
                        "JetBrains Mono",
                        "Menlo",
                        "Consolas",
                        "Monaco",
                        "Courier New"
                    },
                    13);
            }
            catch
            {
                return null;
            }
        }
    }
}
