using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Achieve.UniCodex;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;
using UnityEngine.UIElements;

namespace Achieve.UniCodex.Editor
{
    /// <summary>
    /// Codex CLI용 UI Toolkit 채팅 창입니다.
    /// 뷰 상태, 렌더링, 에디터 상호작용을 담당합니다.
    /// </summary>
    public sealed class UniCodexChatWindow : EditorWindow
    {
        private static readonly Regex MarkdownLinkRegex = new Regex("\\[([^\\]]+)\\]\\(([^)]+)\\)", RegexOptions.Compiled);
        private static readonly Regex MarkdownBoldAsteriskRegex = new Regex("(?<!\\*)\\*\\*(.+?)\\*\\*(?!\\*)", RegexOptions.Compiled);
        private static readonly Regex MarkdownBoldUnderscoreRegex = new Regex("(?<!_)__(.+?)__(?!_)", RegexOptions.Compiled);
        private static readonly Regex MarkdownItalicAsteriskRegex = new Regex("(?<!\\*)\\*(?!\\*)(.+?)(?<!\\*)\\*(?!\\*)", RegexOptions.Compiled);
        private static readonly Regex MarkdownItalicUnderscoreRegex = new Regex("(?<!_)_(?!_)(.+?)(?<!_)_(?!_)", RegexOptions.Compiled);
        private static readonly Regex MarkdownCodeRegex = new Regex("`([^`\\n]+)`", RegexOptions.Compiled);
        private static readonly Regex MentionPathRegex = new Regex("@([^\\s@]+)", RegexOptions.Compiled);
        private static readonly Queue<UniCodexDeferredRunResult> DeferredRunResults = new Queue<UniCodexDeferredRunResult>();
        private static readonly object DeferredRunLock = new object();
        private static readonly object ActiveRunCountLock = new object();
        private static int ActiveRunCount;

        private readonly List<UniCodexChatMessage> _messages = new List<UniCodexChatMessage>();
        private readonly List<UniCodexChatSessionInfo> _chatSessions = new List<UniCodexChatSessionInfo>();
        private readonly List<string> _sessionOptionLabels = new List<string>();
        private readonly List<string> _sessionOptionIds = new List<string>();
        private readonly List<string> _projectFileIndex = new List<string>();
        private readonly List<string> _mentionSuggestions = new List<string>();
        private readonly List<Button> _mentionSuggestionButtonPool = new List<Button>();

        // UI references.
        private ScrollView _chatScrollView;
        private TextField _inputField;
        private PopupField<string> _sessionPopup;
        private Button _newSessionButton;
        private VisualElement _newSessionPopupPanel;
        private TextField _newSessionNameField;
        private Button _newSessionCreateButton;
        private Label _availabilityLabel;
        private Label _settingsStateLabel;
        private Button _sendButton;
        private Button _settingsButton;
        private Button _planModeButton;
        private Button _buildModeButton;
        private Button _diffModeButton;
        private UniCodexTokenGaugeElement _tokenGauge;
        private VisualElement _tokenGaugeHost;
        private Label _tokenGaugePercentLabel;
        private VisualElement _availabilityDot;
        private VisualElement _settingsStateDot;
        private VisualElement _settingsPanel;
        private VisualElement _mentionSuggestionPanel;

        // Persisted/runtime options.
        private string _cliPath = UniCodexCliConstants.DefaultCliPath;
        private readonly bool _useProjectCodexHome = false;
        private readonly string _projectCodexHomeRelative = UniCodexCliConstants.DefaultCodexHomeRelative;
        private readonly bool _fullAuto = true;
        private string _markdownFiles = UniCodexCliConstants.DefaultMarkdownFiles;
        private int _maxMarkdownChars = UniCodexCliConstants.DefaultMaxMarkdownChars;
        private bool _disableDomainReloadOnPlay = true;
        private bool _disableSceneReloadOnPlay;
        private bool _manualRefreshMode = true;
        private bool _buildDiffPreviewMode;
        private string _selectedModel = DefaultModel;
        private string _selectedReasoningEffort = DefaultReasoningEffort;

        // UI/session state.
        private bool _isBusy;
        private bool _autoRefreshLocked;
        private string _sessionId = string.Empty;
        private string _activeChatSessionId = string.Empty;
        private string _statusText = "Ready";
        private bool _codexInstalled;
        private bool _codexLoggedIn;
        private string _codexVersionText = "Unknown";
        private string _loginStatusText = "Unknown";
        private string _lastTokenUsageText = "-";
        private ChatMode _chatMode = ChatMode.Build;
        private int _sessionTokenUsed;
        private int _sessionTokenBudget = UniCodexCliConstants.DefaultSessionTokenBudget;
        private readonly Queue<int> _recentTurnTokenCosts = new Queue<int>();

        // Pending assistant animation state.
        private UniCodexChatMessage _pendingAssistantMessage;
        private IVisualElementScheduledItem _pendingAnimationItem;
        private int _pendingDotCount;
        private string _pendingProgressText = "Preparing request";
        private readonly List<string> _pendingProgressLines = new List<string>();
        private readonly object _progressUpdateLock = new object();
        private string _queuedProgressText;
        private bool _progressDispatchPending;
        private int _activeMentionStartIndex = -1;
        private string _activeMentionQuery = string.Empty;
        private int _selectedMentionSuggestionIndex = -1;
        private bool _suppressMentionRefresh;
        private bool _suppressNextMentionSubmitKeyUp;
        private bool _deferredMentionCommitPending;
        private bool _suppressSessionChangeEvent;
        private bool _projectFileIndexDirty = true;
        private double _nextProjectFileIndexRefreshAt;

        // Font fallback for Korean/Unicode rendering.
        private static Font _preferredUiFont;
        private static Texture2D _settingsIconTexture;
        private const string DefaultUiFontFamily = "SUIT Variable";
        private static readonly string[] PreferredUiFontCandidates =
        {
            DefaultUiFontFamily,
            "Pretendard Variable",
            "Pretendard",
            "SUIT",
            "Apple SD Gothic Neo",
            "SF Pro Text",
            "SF Pro Display",
            "Noto Sans KR",
            "Noto Sans CJK KR",
            "Malgun Gothic",
            "Arial Unicode MS"
        };
        private const int TurnEstimateWindowSize = 5;
        private const int MaxMentionSuggestions = 6;
        private const int MaxTargetedFilesPerTurn = 5;
        private const int MaxTargetedFileChars = 2800;
        private const float MentionSuggestionItemHeight = 22f;
        private const string BaseFieldInputClassName = "unity-base-field__input";
        private const string TextInputClassName = "unity-text-input";
        private const string BaseTextFieldInputClassName = "unity-base-text-field__input";
        private const string DefaultModel = "gpt-5.3-codex";
        private const string DefaultReasoningEffort = "xhigh";
        private static readonly Color UiTextPrimary = new Color(0.93f, 0.95f, 0.98f, 1f);
        private static readonly Color UiTextSecondary = new Color(0.73f, 0.78f, 0.86f, 1f);
        private static readonly Color UiPanelBackground = new Color(0.10f, 0.13f, 0.18f, 0.95f);
        private static readonly Color UiPanelBorder = new Color(0.30f, 0.37f, 0.49f, 1f);
        private static readonly Color UiControlBackground = new Color(0.16f, 0.19f, 0.25f, 0.96f);
        private static readonly Color UiControlBorder = new Color(0.33f, 0.41f, 0.54f, 1f);
        private static readonly Color UiSecondaryButton = new Color(0.19f, 0.23f, 0.30f, 1f);
        private static readonly Color UiSecondaryButtonBorder = new Color(0.35f, 0.44f, 0.58f, 1f);
        private static readonly Color UiPrimaryButton = new Color(0.15f, 0.45f, 0.87f, 1f);
        private static readonly Color UiPrimaryButtonBorder = new Color(0.29f, 0.59f, 1f, 1f);
        private static readonly Color UiDangerButton = new Color(0.60f, 0.23f, 0.23f, 1f);
        private static readonly Color UiDangerButtonBorder = new Color(0.76f, 0.33f, 0.33f, 1f);
        private static readonly List<string> ModelOptions = new List<string>
        {
            "gpt-5.3-codex",
            "gpt-5.2-codex"
        };
        private static readonly List<string> ReasoningEffortOptions = new List<string>
        {
            "none",
            "minimal",
            "low",
            "medium",
            "high",
            "xhigh"
        };

        /// <summary>
        /// Codex 채팅 창을 엽니다.
        /// </summary>
        [MenuItem("Tools/Codex/Codex Chat")]
        public static void OpenWindow()
        {
            var window = GetWindow<UniCodexChatWindow>();
            window.titleContent = new GUIContent("Codex Chat");
            window.minSize = new Vector2(600f, 420f);
            window.Show();
        }

        private void OnEnable()
        {
            LoadPrefs();
            LoadChatHistory();
            MarkProjectFileIndexDirty();

            if (HasActiveRuns())
            {
                _isBusy = true;
                _statusText = "Codex is thinking...";
                UniCodexToolbarShortcut.SetBusyState();
            }

            ApplyDeferredRunResults();
            SynchronizeBusyState();

            AssemblyReloadEvents.beforeAssemblyReload -= ReleaseAutoRefreshLock;
            AssemblyReloadEvents.beforeAssemblyReload += ReleaseAutoRefreshLock;

            EditorApplication.quitting -= ReleaseAutoRefreshLock;
            EditorApplication.quitting += ReleaseAutoRefreshLock;
            EditorApplication.projectChanged -= OnProjectChanged;
            EditorApplication.projectChanged += OnProjectChanged;

            ApplyEnterPlayModeSettings(false);
            if (_manualRefreshMode)
            {
                SetAutoRefreshLock(true);
            }
        }

        private void OnDisable()
        {
            SavePrefs();
            SaveChatHistory();
            ReleaseAutoRefreshLock();
            StopPendingAssistantAnimation();

            AssemblyReloadEvents.beforeAssemblyReload -= ReleaseAutoRefreshLock;
            EditorApplication.quitting -= ReleaseAutoRefreshLock;
            EditorApplication.projectChanged -= OnProjectChanged;
        }

        /// <summary>
        /// 에디터가 창 시각 요소를 만들 때 UI Toolkit 트리를 구성합니다.
        /// </summary>
        public void CreateGUI()
        {
            RebuildUI();
            SynchronizeBusyState();
            EnsurePendingAssistantForActiveRun();
            RefreshEnvironmentState(true);
        }

        private void RebuildUI()
        {
            rootVisualElement.Clear();
            rootVisualElement.style.flexDirection = FlexDirection.Column;
            rootVisualElement.style.flexGrow = 1f;
            rootVisualElement.style.paddingBottom = 8f;
            rootVisualElement.style.paddingLeft = 8f;
            rootVisualElement.style.paddingRight = 8f;
            rootVisualElement.style.paddingTop = 8f;
            ApplyPreferredFont(rootVisualElement);

            BuildHeaderPanel();
            BuildChatArea();
            RefreshChatUI();
            UpdateEnvironmentUI();
            UpdateStatusUI();
        }

        private void BuildHeaderPanel()
        {
            var topRow = new VisualElement();
            topRow.style.flexDirection = FlexDirection.Row;
            topRow.style.justifyContent = Justify.SpaceBetween;
            topRow.style.alignItems = Align.Center;
            topRow.style.marginBottom = 6f;

            var left = new VisualElement();
            left.style.flexDirection = FlexDirection.Row;
            left.style.alignItems = Align.Center;
            left.style.flexShrink = 1f;

            var title = new Label("Codex Chat");
            ApplyPreferredFont(title);
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.fontSize = 14f;
            title.style.marginRight = 8f;
            left.Add(title);

            _sessionPopup = new PopupField<string>(new List<string> { "1. Session 1" }, 0);
            _sessionPopup.style.width = 168f;
            _sessionPopup.style.minWidth = 148f;
            _sessionPopup.style.marginRight = 4f;
            ApplyPopupFieldStyle(_sessionPopup, 24f);
            _sessionPopup.RegisterValueChangedCallback(OnSessionPopupChanged);
            left.Add(_sessionPopup);

            _newSessionButton = new Button(ToggleNewSessionPopup) { text = "+" };
            _newSessionButton.tooltip = "Create a new chat session";
            _newSessionButton.style.width = 24f;
            _newSessionButton.style.height = 24f;
            _newSessionButton.style.minWidth = 24f;
            _newSessionButton.style.minHeight = 24f;
            _newSessionButton.style.paddingLeft = 0f;
            _newSessionButton.style.paddingRight = 0f;
            _newSessionButton.style.unityTextAlign = TextAnchor.MiddleCenter;
            ApplyButtonStyle(_newSessionButton, UiSecondaryButton, UiSecondaryButtonBorder, UiTextPrimary, 24f, 6f);
            left.Add(_newSessionButton);

            var right = new VisualElement();
            right.style.flexDirection = FlexDirection.Row;
            right.style.alignItems = Align.Center;

            _availabilityDot = CreateStatusDot(10f);
            _availabilityDot.style.marginRight = 6f;
            right.Add(_availabilityDot);

            _availabilityLabel = new Label("Not Ready");
            ApplyPreferredFont(_availabilityLabel);
            _availabilityLabel.style.color = UiTextPrimary;
            _availabilityLabel.style.marginRight = 8f;
            right.Add(_availabilityLabel);

            _settingsButton = new Button(ToggleSettingsPanel) { tooltip = "Settings" };
            _settingsButton.text = string.Empty;
            _settingsButton.style.width = 26f;
            _settingsButton.style.height = 24f;
            _settingsButton.style.minWidth = 26f;
            _settingsButton.style.minHeight = 24f;
            _settingsButton.style.paddingLeft = 0f;
            _settingsButton.style.paddingRight = 0f;
            _settingsButton.style.flexShrink = 0f;
            _settingsButton.style.justifyContent = Justify.Center;
            _settingsButton.style.alignItems = Align.Center;
            ApplyButtonStyle(_settingsButton, UiSecondaryButton, UiSecondaryButtonBorder, UiTextPrimary, 24f, 6f);

            var settingsIcon = GetSettingsIconTexture();
            if (settingsIcon != null)
            {
                var icon = new Image
                {
                    image = settingsIcon,
                    scaleMode = ScaleMode.ScaleToFit,
                    pickingMode = PickingMode.Ignore
                };
                icon.style.width = 14f;
                icon.style.height = 14f;
                icon.style.unityBackgroundImageTintColor = UiTextPrimary;
                _settingsButton.Add(icon);
            }
            else
            {
                _settingsButton.text = "S";
            }

            right.Add(_settingsButton);

            topRow.Add(left);
            topRow.Add(right);
            rootVisualElement.Add(topRow);

            _settingsPanel = BuildSettingsPanel();
            _settingsPanel.style.display = DisplayStyle.None;
            rootVisualElement.Add(_settingsPanel);
            BuildNewSessionPopupPanel();
            RefreshSessionPopupChoices();
        }

        private VisualElement BuildSettingsPanel()
        {
            var panel = new VisualElement();
            ApplyPanelSurfaceStyle(panel, UiPanelBackground, UiPanelBorder, 8f);
            panel.style.paddingBottom = 6f;
            panel.style.paddingLeft = 8f;
            panel.style.paddingRight = 8f;
            panel.style.paddingTop = 6f;
            panel.style.marginBottom = 8f;

            var stateRow = new VisualElement();
            stateRow.style.flexDirection = FlexDirection.Row;
            stateRow.style.alignItems = Align.Center;
            stateRow.style.marginBottom = 8f;

            _settingsStateDot = CreateStatusDot(8f);
            _settingsStateDot.style.marginRight = 6f;
            stateRow.Add(_settingsStateDot);

            _settingsStateLabel = new Label("Not Ready");
            ApplyPreferredFont(_settingsStateLabel);
            _settingsStateLabel.style.color = UiTextPrimary;
            stateRow.Add(_settingsStateLabel);

            panel.Add(stateRow);

            var loginRow = new VisualElement();
            loginRow.style.flexDirection = FlexDirection.Row;
            loginRow.style.alignItems = Align.Center;
            loginRow.style.flexWrap = Wrap.Wrap;
            loginRow.style.marginBottom = 2f;

            var loginButton = new Button(LoginWithDeviceAuth) { text = "Login (Device)" };
            loginButton.style.width = 120f;
            loginButton.style.marginRight = 6f;
            ApplyButtonStyle(loginButton, UiPrimaryButton, UiPrimaryButtonBorder, Color.white, 24f, 6f);
            loginRow.Add(loginButton);

            var logoutButton = new Button(LogoutCodex) { text = "Logout" };
            logoutButton.style.width = 90f;
            logoutButton.style.marginRight = 6f;
            ApplyButtonStyle(logoutButton, UiDangerButton, UiDangerButtonBorder, Color.white, 24f, 6f);
            loginRow.Add(logoutButton);

            var refreshButton = new Button(RefreshEnvironmentState) { text = "Refresh" };
            refreshButton.style.width = 88f;
            refreshButton.tooltip = "Re-check Codex install and login status";
            ApplyButtonStyle(refreshButton, UiSecondaryButton, UiSecondaryButtonBorder, UiTextPrimary, 24f, 6f);
            loginRow.Add(refreshButton);

            panel.Add(loginRow);

            var modelRow = new VisualElement();
            modelRow.style.flexDirection = FlexDirection.Row;
            modelRow.style.alignItems = Align.Center;
            modelRow.style.marginTop = 8f;
            modelRow.style.marginBottom = 4f;

            var modelLabel = new Label("Model");
            ApplyPreferredFont(modelLabel);
            modelLabel.style.color = UiTextSecondary;
            modelLabel.style.width = 90f;
            modelLabel.style.minWidth = 90f;
            modelLabel.style.marginRight = 6f;
            modelRow.Add(modelLabel);

            var modelIndex = GetOptionIndex(_selectedModel, ModelOptions, DefaultModel);
            var modelPopup = new PopupField<string>(ModelOptions, modelIndex);
            modelPopup.style.flexGrow = 1f;
            ApplyPopupFieldStyle(modelPopup, 24f);
            modelPopup.RegisterValueChangedCallback(evt =>
            {
                _selectedModel = NormalizeOption(evt.newValue, ModelOptions, DefaultModel);
                SavePrefs();
            });
            modelRow.Add(modelPopup);
            panel.Add(modelRow);

            var reasoningRow = new VisualElement();
            reasoningRow.style.flexDirection = FlexDirection.Row;
            reasoningRow.style.alignItems = Align.Center;

            var reasoningLabel = new Label("Reasoning");
            ApplyPreferredFont(reasoningLabel);
            reasoningLabel.style.color = UiTextSecondary;
            reasoningLabel.style.width = 90f;
            reasoningLabel.style.minWidth = 90f;
            reasoningLabel.style.marginRight = 6f;
            reasoningRow.Add(reasoningLabel);

            var reasoningIndex = GetOptionIndex(_selectedReasoningEffort, ReasoningEffortOptions, DefaultReasoningEffort);
            var reasoningPopup = new PopupField<string>(ReasoningEffortOptions, reasoningIndex);
            reasoningPopup.style.flexGrow = 1f;
            ApplyPopupFieldStyle(reasoningPopup, 24f);
            reasoningPopup.RegisterValueChangedCallback(evt =>
            {
                _selectedReasoningEffort = NormalizeOption(evt.newValue, ReasoningEffortOptions, DefaultReasoningEffort);
                SavePrefs();
            });
            reasoningRow.Add(reasoningPopup);
            panel.Add(reasoningRow);

            return panel;
        }

        private VisualElement BuildModeSelector()
        {
            var modeRow = new VisualElement();
            modeRow.style.flexDirection = FlexDirection.Row;
            modeRow.style.alignItems = Align.Center;
            modeRow.style.justifyContent = Justify.SpaceBetween;
            modeRow.style.marginBottom = 6f;
            modeRow.style.flexShrink = 0f;

            var modeLeft = new VisualElement();
            modeLeft.style.flexDirection = FlexDirection.Row;
            modeLeft.style.alignItems = Align.Center;
            modeLeft.style.flexShrink = 0f;

            var modeLabel = new Label("Mode:");
            ApplyPreferredFont(modeLabel);
            modeLabel.style.marginRight = 6f;
            modeLabel.style.color = UiTextSecondary;
            modeLeft.Add(modeLabel);

            _planModeButton = new Button(() => SetChatMode(ChatMode.Plan)) { text = "Plan" };
            _planModeButton.style.width = 80f;
            _planModeButton.style.height = 26f;
            _planModeButton.style.minHeight = 26f;
            _planModeButton.style.maxHeight = 26f;
            _planModeButton.style.marginRight = 4f;
            ApplyButtonStyle(_planModeButton, UiSecondaryButton, UiSecondaryButtonBorder, UiTextPrimary, 26f, 7f);
            modeLeft.Add(_planModeButton);

            _buildModeButton = new Button(() => SetChatMode(ChatMode.Build)) { text = "Build" };
            _buildModeButton.style.width = 80f;
            _buildModeButton.style.height = 26f;
            _buildModeButton.style.minHeight = 26f;
            _buildModeButton.style.maxHeight = 26f;
            _buildModeButton.style.marginRight = 4f;
            ApplyButtonStyle(_buildModeButton, UiSecondaryButton, UiSecondaryButtonBorder, UiTextPrimary, 26f, 7f);
            modeLeft.Add(_buildModeButton);

            _diffModeButton = new Button(ToggleBuildDiffPreviewMode);
            _diffModeButton.style.width = 74f;
            _diffModeButton.style.height = 26f;
            _diffModeButton.style.minHeight = 26f;
            _diffModeButton.style.maxHeight = 26f;
            ApplyButtonStyle(_diffModeButton, UiSecondaryButton, UiSecondaryButtonBorder, UiTextPrimary, 26f, 7f);
            modeLeft.Add(_diffModeButton);

            modeRow.Add(modeLeft);
            modeRow.Add(BuildTokenGaugeIndicator(24f, 20f, 8f));

            UpdateModeButtons();
            UpdateTokenGaugeUI();
            return modeRow;
        }

        private void ToggleSettingsPanel()
        {
            if (_settingsPanel == null)
            {
                return;
            }

            var isOpen = _settingsPanel.style.display != DisplayStyle.None;
            _settingsPanel.style.display = isOpen ? DisplayStyle.None : DisplayStyle.Flex;
        }

        private void BuildNewSessionPopupPanel()
        {
            _newSessionPopupPanel = new VisualElement();
            _newSessionPopupPanel.style.position = Position.Absolute;
            _newSessionPopupPanel.style.display = DisplayStyle.None;
            _newSessionPopupPanel.style.width = 340f;
            _newSessionPopupPanel.style.minWidth = 340f;
            _newSessionPopupPanel.style.paddingLeft = 12f;
            _newSessionPopupPanel.style.paddingRight = 12f;
            _newSessionPopupPanel.style.paddingTop = 12f;
            _newSessionPopupPanel.style.paddingBottom = 12f;
            ApplyPanelSurfaceStyle(_newSessionPopupPanel, new Color(0.09f, 0.12f, 0.18f, 0.99f), UiPanelBorder, 10f);

            var title = new Label("New Session Name (optional)");
            ApplyPreferredFont(title);
            title.style.fontSize = 13f;
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.color = UiTextPrimary;
            title.style.marginBottom = 6f;
            _newSessionPopupPanel.Add(title);

            var hint = new Label("Leave empty and press Create to use default name.");
            ApplyPreferredFont(hint);
            hint.style.fontSize = 11f;
            hint.style.color = UiTextSecondary;
            hint.style.marginBottom = 8f;
            _newSessionPopupPanel.Add(hint);

            _newSessionNameField = new TextField();
            _newSessionNameField.style.height = 28f;
            _newSessionNameField.style.minHeight = 28f;
            _newSessionNameField.style.marginBottom = 10f;
            _newSessionNameField.style.fontSize = 13f;
            _newSessionNameField.tooltip = "Leave empty to use default name.";
            StyleTextFieldInput(_newSessionNameField, 8f);
            _newSessionNameField.RegisterCallback<KeyDownEvent>(OnNewSessionNameFieldKeyDown, TrickleDown.TrickleDown);
            _newSessionPopupPanel.Add(_newSessionNameField);

            var actionRow = new VisualElement();
            actionRow.style.flexDirection = FlexDirection.Row;
            actionRow.style.justifyContent = Justify.FlexEnd;
            actionRow.style.alignItems = Align.Center;

            var cancelButton = new Button(HideNewSessionPopup) { text = "Cancel" };
            cancelButton.style.width = 84f;
            cancelButton.style.height = 28f;
            cancelButton.style.marginRight = 6f;
            ApplyButtonStyle(cancelButton, UiSecondaryButton, UiSecondaryButtonBorder, UiTextPrimary, 28f, 8f);
            actionRow.Add(cancelButton);

            _newSessionCreateButton = new Button(CreateSessionFromPopup) { text = "Create" };
            _newSessionCreateButton.style.width = 92f;
            _newSessionCreateButton.style.height = 28f;
            ApplyButtonStyle(_newSessionCreateButton, UiPrimaryButton, UiPrimaryButtonBorder, Color.white, 28f, 8f);
            actionRow.Add(_newSessionCreateButton);

            _newSessionPopupPanel.Add(actionRow);
            rootVisualElement.Add(_newSessionPopupPanel);
        }

        private void ToggleNewSessionPopup()
        {
            if (_isBusy)
            {
                return;
            }

            if (_newSessionPopupPanel == null)
            {
                BuildNewSessionPopupPanel();
            }

            var isOpen = _newSessionPopupPanel.style.display != DisplayStyle.None;
            if (isOpen)
            {
                HideNewSessionPopup();
                return;
            }

            ShowNewSessionPopup();
        }

        private void ShowNewSessionPopup()
        {
            if (_newSessionPopupPanel == null || _newSessionButton == null)
            {
                return;
            }

            var rootRect = rootVisualElement.worldBound;
            var anchorRect = _newSessionButton.worldBound;
            var popupWidth = 340f;
            var popupHeight = 150f;

            var left = anchorRect.xMin - rootRect.xMin;
            var top = anchorRect.yMax - rootRect.yMin + 8f;

            var maxLeft = Mathf.Max(4f, rootVisualElement.resolvedStyle.width - popupWidth - 4f);
            var maxTop = Mathf.Max(4f, rootVisualElement.resolvedStyle.height - popupHeight - 4f);
            left = Mathf.Clamp(left, 4f, maxLeft);
            top = Mathf.Clamp(top, 4f, maxTop);

            _newSessionPopupPanel.style.left = left;
            _newSessionPopupPanel.style.top = top;
            _newSessionPopupPanel.style.display = DisplayStyle.Flex;
            _newSessionPopupPanel.BringToFront();

            if (_newSessionNameField != null)
            {
                _newSessionNameField.SetValueWithoutNotify(string.Empty);
                _newSessionNameField.Focus();
            }
        }

        private void HideNewSessionPopup()
        {
            if (_newSessionPopupPanel != null)
            {
                _newSessionPopupPanel.style.display = DisplayStyle.None;
            }
        }

        private void OnNewSessionNameFieldKeyDown(KeyDownEvent evt)
        {
            if (evt == null)
            {
                return;
            }

            if (evt.keyCode == KeyCode.Escape)
            {
                evt.StopPropagation();
                evt.StopImmediatePropagation();
                HideNewSessionPopup();
                return;
            }

            if (evt.keyCode != KeyCode.Return && evt.keyCode != KeyCode.KeypadEnter)
            {
                return;
            }

            evt.StopPropagation();
            evt.StopImmediatePropagation();
            CreateSessionFromPopup();
        }

        private void CreateSessionFromPopup()
        {
            var preferredName = _newSessionNameField?.value;
            CreateNewChatSession(preferredName);
            HideNewSessionPopup();
        }

        private static string NormalizeSessionName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var trimmed = value.Trim();
            if (trimmed.Length > 36)
            {
                trimmed = trimmed.Substring(0, 36).TrimEnd();
            }

            return trimmed;
        }

        private void SetChatMode(ChatMode mode)
        {
            if (_chatMode == mode)
            {
                return;
            }

            _chatMode = mode;
            UpdateModeButtons();
            UpdateStatusUI();
            SavePrefs();
        }

        private void UpdateModeButtons()
        {
            ApplyModeButtonStyle(_planModeButton, _chatMode == ChatMode.Plan);
            ApplyModeButtonStyle(_buildModeButton, _chatMode == ChatMode.Build);
            UpdateDiffModeButton();
        }

        private void ToggleBuildDiffPreviewMode()
        {
            _buildDiffPreviewMode = !_buildDiffPreviewMode;
            UpdateDiffModeButton();
            SavePrefs();
        }

        private void UpdateDiffModeButton()
        {
            if (_diffModeButton == null)
            {
                return;
            }

            var buildSelected = _chatMode == ChatMode.Build;
            var active = buildSelected && _buildDiffPreviewMode;
            _diffModeButton.text = _buildDiffPreviewMode ? "Diff On" : "Diff Off";
            _diffModeButton.tooltip = buildSelected
                ? "Build mode diff preview. Shows proposed unified diff in a separate window before apply."
                : "Diff preview is available in Build mode only.";
            _diffModeButton.SetEnabled(buildSelected);
            _diffModeButton.style.backgroundColor = active
                ? UiPrimaryButton
                : UiSecondaryButton;
            _diffModeButton.style.color = buildSelected
                ? UiTextPrimary
                : UiTextSecondary;
            _diffModeButton.style.borderTopColor = active ? UiPrimaryButtonBorder : UiSecondaryButtonBorder;
            _diffModeButton.style.borderBottomColor = active ? UiPrimaryButtonBorder : UiSecondaryButtonBorder;
            _diffModeButton.style.borderLeftColor = active ? UiPrimaryButtonBorder : UiSecondaryButtonBorder;
            _diffModeButton.style.borderRightColor = active ? UiPrimaryButtonBorder : UiSecondaryButtonBorder;
            _diffModeButton.style.unityFontStyleAndWeight = active ? FontStyle.Bold : FontStyle.Normal;
        }

        private void UpdateTokenGaugeUI()
        {
            var budget = Mathf.Max(1000, _sessionTokenBudget);
            var used = Mathf.Max(0, _sessionTokenUsed);
            var remaining = Mathf.Max(0, budget - used);
            var ratio = Mathf.Clamp01((float)used / budget);
            var usagePercent = Mathf.Clamp01(ratio) * 100f;
            var tooltipText = BuildTokenGaugeTooltip(used, budget, remaining);

            if (_tokenGauge != null)
            {
                _tokenGauge.Progress = ratio;
                _tokenGauge.FillColor = ratio < 0.6f
                    ? new Color(0.17f, 0.56f, 0.94f, 1f)
                    : (ratio < 0.9f ? new Color(0.93f, 0.65f, 0.20f, 1f) : new Color(0.90f, 0.28f, 0.28f, 1f));
                _tokenGauge.tooltip = tooltipText;
            }

            if (_tokenGaugeHost != null)
            {
                _tokenGaugeHost.tooltip = tooltipText;
            }

            if (_tokenGaugePercentLabel != null)
            {
                _tokenGaugePercentLabel.text = $"{Mathf.RoundToInt(usagePercent)}%";
                _tokenGaugePercentLabel.tooltip = tooltipText;
            }
        }

        private string BuildTokenGaugeTooltip(int used, int budget, int remaining)
        {
            var usageRatio = budget > 0 ? (float)used / budget : 0f;
            var usagePercent = Mathf.Clamp01(usageRatio) * 100f;
            var avgTurn = Mathf.Max(0, GetRecentAverageTurnCost());
            var estimatedTurnsLeft = avgTurn > 0f ? Mathf.FloorToInt(remaining / avgTurn) : -1;

            var sb = new StringBuilder();
            sb.AppendLine($"세션 누적(추정): {FormatTokenCount(used)} / {FormatTokenCount(budget)} ({usagePercent:0.#}%)");
            sb.AppendLine($"남은 토큰(추정): {FormatTokenCount(remaining)}");

            if (estimatedTurnsLeft >= 0)
            {
                sb.AppendLine($"남은 대화 추정: 약 {estimatedTurnsLeft}턴 (최근 {TurnEstimateWindowSize}턴 평균 {FormatTokenCount(Mathf.RoundToInt(avgTurn))}/턴)");
            }
            else
            {
                sb.AppendLine("남은 대화 추정: 계산 중 (턴 데이터 부족)");
            }

            sb.Append("참고: codex exec 토큰 기반 추정치이며 실제 계정 quota와 다를 수 있습니다.");
            return sb.ToString();
        }

        private float GetRecentAverageTurnCost()
        {
            if (_recentTurnTokenCosts == null || _recentTurnTokenCosts.Count == 0)
            {
                return 0f;
            }

            var sum = 0;
            foreach (var value in _recentTurnTokenCosts)
            {
                sum += Mathf.Max(0, value);
            }

            return sum <= 0 ? 0f : (float)sum / _recentTurnTokenCosts.Count;
        }

        private static string FormatTokenCount(int value)
        {
            var safe = Mathf.Max(0, value);
            if (safe >= 1000000)
            {
                return $"{safe / 1000000f:0.#}M";
            }

            if (safe >= 1000)
            {
                return $"{safe / 1000f:0.#}k";
            }

            return safe.ToString();
        }

        private void AccumulateSessionTokens(UniCodexRunResult result)
        {
            var turnCost = ComputeTurnTokenCost(result);
            if (turnCost <= 0)
            {
                return;
            }

            _sessionTokenUsed = Mathf.Max(0, _sessionTokenUsed + turnCost);
            PushRecentTurnTokenCost(turnCost);
            SavePrefs();
            UpdateTokenGaugeUI();
        }

        private void PushRecentTurnTokenCost(int turnCost)
        {
            if (turnCost <= 0)
            {
                return;
            }

            _recentTurnTokenCosts.Enqueue(turnCost);
            while (_recentTurnTokenCosts.Count > TurnEstimateWindowSize)
            {
                _recentTurnTokenCosts.Dequeue();
            }
        }

        private static int ComputeTurnTokenCost(UniCodexRunResult result)
        {
            if (result == null)
            {
                return 0;
            }

            var input = result.InputTokens.GetValueOrDefault(0);
            var output = result.OutputTokens.GetValueOrDefault(0);
            var ioSum = input + output;
            if (ioSum > 0)
            {
                return ioSum;
            }

            return Mathf.Max(0, result.TotalTokens.GetValueOrDefault(0));
        }

        private static void ApplyModeButtonStyle(Button button, bool selected)
        {
            if (button == null)
            {
                return;
            }

            button.style.backgroundColor = selected
                ? UiPrimaryButton
                : UiSecondaryButton;
            button.style.color = selected
                ? Color.white
                : UiTextPrimary;
            button.style.borderTopColor = selected ? UiPrimaryButtonBorder : UiSecondaryButtonBorder;
            button.style.borderBottomColor = selected ? UiPrimaryButtonBorder : UiSecondaryButtonBorder;
            button.style.borderLeftColor = selected ? UiPrimaryButtonBorder : UiSecondaryButtonBorder;
            button.style.borderRightColor = selected ? UiPrimaryButtonBorder : UiSecondaryButtonBorder;
            button.style.unityFontStyleAndWeight = selected ? FontStyle.Bold : FontStyle.Normal;
        }

        private void BuildChatArea()
        {
            _chatScrollView = new ScrollView(ScrollViewMode.Vertical);
            _chatScrollView.style.flexGrow = 1f;
            _chatScrollView.style.flexShrink = 1f;
            _chatScrollView.style.minHeight = 140f;
            _chatScrollView.style.borderBottomColor = new Color(0.24f, 0.24f, 0.24f, 1f);
            _chatScrollView.style.borderBottomWidth = 1f;
            _chatScrollView.style.borderLeftColor = new Color(0.24f, 0.24f, 0.24f, 1f);
            _chatScrollView.style.borderLeftWidth = 1f;
            _chatScrollView.style.borderRightColor = new Color(0.24f, 0.24f, 0.24f, 1f);
            _chatScrollView.style.borderRightWidth = 1f;
            _chatScrollView.style.borderTopColor = new Color(0.24f, 0.24f, 0.24f, 1f);
            _chatScrollView.style.borderTopWidth = 1f;
            _chatScrollView.style.paddingBottom = 6f;
            _chatScrollView.style.paddingTop = 6f;
            rootVisualElement.Add(_chatScrollView);

            var modeRow = BuildModeSelector();
            modeRow.style.marginTop = 8f;
            rootVisualElement.Add(modeRow);

            var composer = BuildComposer();
            composer.style.flexShrink = 0f;
            composer.style.marginTop = 4f;
            rootVisualElement.Add(composer);
        }

        private VisualElement BuildComposer()
        {
            var composer = new VisualElement();
            composer.style.flexDirection = FlexDirection.Row;
            composer.style.alignItems = Align.FlexEnd;
            composer.style.flexShrink = 0f;

            var inputContainer = new VisualElement();
            inputContainer.style.flexDirection = FlexDirection.Column;
            inputContainer.style.flexGrow = 1f;
            inputContainer.style.flexShrink = 1f;
            inputContainer.style.marginRight = 8f;

            _mentionSuggestionPanel = new VisualElement();
            _mentionSuggestionPanel.style.display = DisplayStyle.None;
            _mentionSuggestionPanel.style.flexDirection = FlexDirection.Column;
            _mentionSuggestionPanel.style.maxHeight = 132f;
            _mentionSuggestionPanel.style.marginBottom = 4f;
            _mentionSuggestionPanel.style.overflow = Overflow.Hidden;
            _mentionSuggestionPanel.style.backgroundColor = new Color(0.13f, 0.15f, 0.18f, 0.98f);
            _mentionSuggestionPanel.style.borderTopWidth = 1f;
            _mentionSuggestionPanel.style.borderBottomWidth = 1f;
            _mentionSuggestionPanel.style.borderLeftWidth = 1f;
            _mentionSuggestionPanel.style.borderRightWidth = 1f;
            _mentionSuggestionPanel.style.borderTopColor = new Color(0.28f, 0.33f, 0.40f, 1f);
            _mentionSuggestionPanel.style.borderBottomColor = new Color(0.28f, 0.33f, 0.40f, 1f);
            _mentionSuggestionPanel.style.borderLeftColor = new Color(0.28f, 0.33f, 0.40f, 1f);
            _mentionSuggestionPanel.style.borderRightColor = new Color(0.28f, 0.33f, 0.40f, 1f);
            inputContainer.Add(_mentionSuggestionPanel);

            _inputField = new TextField { multiline = true };
            _inputField.style.flexGrow = 1f;
            _inputField.style.minHeight = 64f;
            _inputField.style.whiteSpace = WhiteSpace.Normal;
            _inputField.style.unityTextAlign = TextAnchor.UpperLeft;
            StyleTextFieldInput(_inputField, 10f);
            _inputField.RegisterCallback<KeyDownEvent>(OnInputKeyDown, TrickleDown.TrickleDown);
            _inputField.RegisterCallback<KeyUpEvent>(OnInputKeyUp, TrickleDown.TrickleDown);
            _inputField.RegisterValueChangedCallback(OnInputValueChanged);
            ConfigureInputFieldWordWrap();
            inputContainer.Add(_inputField);
            inputContainer.schedule.Execute(ConfigureInputFieldWordWrap).ExecuteLater(10);
            composer.Add(inputContainer);

            var rightColumn = new VisualElement();
            rightColumn.style.flexDirection = FlexDirection.Column;
            rightColumn.style.alignItems = Align.Center;
            rightColumn.style.flexShrink = 0f;
            rightColumn.style.width = 96f;

            _sendButton = new Button(SendCurrentInput) { text = "Send" };
            _sendButton.style.width = 96f;
            _sendButton.style.minHeight = 64f;
            _sendButton.style.flexShrink = 0f;
            ApplyButtonStyle(_sendButton, UiPrimaryButton, UiPrimaryButtonBorder, Color.white, 64f, 10f, true);
            rightColumn.Add(_sendButton);

            composer.Add(rightColumn);

            UpdateTokenGaugeUI();

            return composer;
        }

        private VisualElement BuildTokenGaugeIndicator(float hostSize, float gaugeSize, float fontSize)
        {
            _tokenGaugeHost = new VisualElement();
            _tokenGaugeHost.style.width = hostSize;
            _tokenGaugeHost.style.height = hostSize;
            _tokenGaugeHost.style.minWidth = hostSize;
            _tokenGaugeHost.style.minHeight = hostSize;
            _tokenGaugeHost.style.maxWidth = hostSize;
            _tokenGaugeHost.style.maxHeight = hostSize;
            _tokenGaugeHost.style.marginLeft = 8f;
            _tokenGaugeHost.style.alignItems = Align.Center;
            _tokenGaugeHost.style.justifyContent = Justify.Center;
            _tokenGaugeHost.style.position = Position.Relative;
            _tokenGaugeHost.style.backgroundColor = new Color(0.12f, 0.14f, 0.17f, 0.95f);
            _tokenGaugeHost.style.borderTopWidth = 1f;
            _tokenGaugeHost.style.borderBottomWidth = 1f;
            _tokenGaugeHost.style.borderLeftWidth = 1f;
            _tokenGaugeHost.style.borderRightWidth = 1f;
            _tokenGaugeHost.style.borderTopColor = new Color(0.28f, 0.33f, 0.40f, 1f);
            _tokenGaugeHost.style.borderBottomColor = new Color(0.28f, 0.33f, 0.40f, 1f);
            _tokenGaugeHost.style.borderLeftColor = new Color(0.28f, 0.33f, 0.40f, 1f);
            _tokenGaugeHost.style.borderRightColor = new Color(0.28f, 0.33f, 0.40f, 1f);
            _tokenGaugeHost.style.borderTopLeftRadius = hostSize * 0.5f;
            _tokenGaugeHost.style.borderTopRightRadius = hostSize * 0.5f;
            _tokenGaugeHost.style.borderBottomLeftRadius = hostSize * 0.5f;
            _tokenGaugeHost.style.borderBottomRightRadius = hostSize * 0.5f;
            _tokenGaugeHost.style.flexShrink = 0f;

            _tokenGauge = new UniCodexTokenGaugeElement();
            _tokenGauge.style.width = gaugeSize;
            _tokenGauge.style.height = gaugeSize;
            _tokenGauge.pickingMode = PickingMode.Ignore;
            _tokenGaugeHost.Add(_tokenGauge);

            _tokenGaugePercentLabel = new Label("0%");
            ApplyPreferredFont(_tokenGaugePercentLabel);
            _tokenGaugePercentLabel.style.position = Position.Absolute;
            _tokenGaugePercentLabel.style.fontSize = fontSize;
            _tokenGaugePercentLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            _tokenGaugePercentLabel.style.color = new Color(0.92f, 0.92f, 0.92f, 1f);
            _tokenGaugePercentLabel.style.unityTextAlign = TextAnchor.MiddleCenter;
            _tokenGaugePercentLabel.style.left = 0f;
            _tokenGaugePercentLabel.style.right = 0f;
            _tokenGaugePercentLabel.style.top = 0f;
            _tokenGaugePercentLabel.style.bottom = 0f;
            _tokenGaugePercentLabel.pickingMode = PickingMode.Ignore;
            _tokenGaugeHost.Add(_tokenGaugePercentLabel);

            return _tokenGaugeHost;
        }

        private void OnInputValueChanged(ChangeEvent<string> evt)
        {
            if (_suppressMentionRefresh)
            {
                return;
            }

            var normalized = NormalizeMentionBrokenLine(evt.newValue ?? string.Empty);
            if (!string.Equals(evt.newValue, normalized, StringComparison.Ordinal))
            {
                _suppressMentionRefresh = true;
                _inputField.value = normalized;
                _suppressMentionRefresh = false;
                RefreshMentionSuggestions(normalized);
                return;
            }

            RefreshMentionSuggestions(evt.newValue);
        }

        private void ConfigureInputFieldWordWrap()
        {
            if (_inputField == null)
            {
                return;
            }

            var textInput = QueryTextInputElement(_inputField);
            if (textInput != null)
            {
                textInput.style.whiteSpace = WhiteSpace.Normal;
                textInput.style.overflow = Overflow.Hidden;
                textInput.style.unityTextAlign = TextAnchor.UpperLeft;
                ApplyPreferredFont(textInput);
            }

            var textElement = _inputField.Q<TextElement>();
            if (textElement != null)
            {
                textElement.style.whiteSpace = WhiteSpace.Normal;
                textElement.style.unityTextAlign = TextAnchor.UpperLeft;
                ApplyPreferredFont(textElement);
            }
        }

        private bool HandleMentionInputKeyDown(KeyDownEvent evt)
        {
            if (_mentionSuggestionPanel == null || _mentionSuggestionPanel.style.display == DisplayStyle.None || _mentionSuggestions.Count == 0)
            {
                return false;
            }

            var isSubmitKey = evt.keyCode == KeyCode.Return
                || evt.keyCode == KeyCode.KeypadEnter
                || evt.character == '\n'
                || evt.character == '\r';

            switch (evt.keyCode)
            {
                case KeyCode.DownArrow:
                    ConsumeMentionKeyEvent(evt);
                    SelectMentionSuggestionDelta(1);
                    return true;
                case KeyCode.UpArrow:
                    ConsumeMentionKeyEvent(evt);
                    SelectMentionSuggestionDelta(-1);
                    return true;
                case KeyCode.Tab:
                    ConsumeMentionKeyEvent(evt);
                    TryCommitSelectedMention();
                    return true;
                case KeyCode.Escape:
                    ConsumeMentionKeyEvent(evt);
                    HideMentionSuggestions();
                    return true;
            }

            if (isSubmitKey && !evt.shiftKey)
            {
                ConsumeMentionKeyEvent(evt);
                _suppressNextMentionSubmitKeyUp = true;
                ScheduleDeferredMentionCommit();
                return true;
            }

            return false;
        }

        private static void ConsumeMentionKeyEvent(KeyDownEvent evt)
        {
            if (evt == null)
            {
                return;
            }

            evt.StopPropagation();
            evt.StopImmediatePropagation();
        }

        private static void ConsumeMentionKeyEvent(KeyUpEvent evt)
        {
            if (evt == null)
            {
                return;
            }

            evt.StopPropagation();
            evt.StopImmediatePropagation();
        }

        private void OnInputKeyUp(KeyUpEvent evt)
        {
            if (!_suppressNextMentionSubmitKeyUp)
            {
                return;
            }

            var isSubmitKey = evt.keyCode == KeyCode.Return
                || evt.keyCode == KeyCode.KeypadEnter
                || evt.character == '\n'
                || evt.character == '\r';

            if (isSubmitKey)
            {
                ConsumeMentionKeyEvent(evt);
            }

            _suppressNextMentionSubmitKeyUp = false;
        }

        private void ScheduleDeferredMentionCommit()
        {
            if (_deferredMentionCommitPending)
            {
                return;
            }

            _deferredMentionCommitPending = true;
            EditorApplication.delayCall += CommitMentionSelectionDeferred;
        }

        private void CommitMentionSelectionDeferred()
        {
            _deferredMentionCommitPending = false;
            if (_inputField == null)
            {
                return;
            }

            TryCommitSelectedMention();

            // Some platforms may still inject an enter newline; collapse @path line breaks.
            var current = _inputField.value ?? string.Empty;
            var normalized = NormalizeMentionBrokenLine(current);
            if (!string.Equals(current, normalized, StringComparison.Ordinal))
            {
                _suppressMentionRefresh = true;
                _inputField.value = normalized;
                _suppressMentionRefresh = false;
                MoveInputCaretToEnd(true);
            }
        }

        private void RefreshMentionSuggestions(string text)
        {
            if (!TryExtractTrailingMention(text, out _activeMentionStartIndex, out _activeMentionQuery))
            {
                HideMentionSuggestions();
                return;
            }

            EnsureProjectFileIndex();
            _mentionSuggestions.Clear();

            var query = _activeMentionQuery ?? string.Empty;
            if (_projectFileIndex.Count > 0)
            {
                var normalizedQuery = query.Replace('\\', '/');
                var added = 0;
                for (var i = 0; i < _projectFileIndex.Count && added < MaxMentionSuggestions; i++)
                {
                    var path = _projectFileIndex[i];
                    if (!string.IsNullOrEmpty(normalizedQuery)
                        && path.IndexOf(normalizedQuery, StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }

                    _mentionSuggestions.Add(path);
                    added++;
                }
            }

            _selectedMentionSuggestionIndex = _mentionSuggestions.Count > 0 ? 0 : -1;
            RenderMentionSuggestions();
        }

        private void RenderMentionSuggestions()
        {
            if (_mentionSuggestionPanel == null)
            {
                return;
            }

            if (_mentionSuggestions.Count == 0)
            {
                _mentionSuggestionPanel.style.display = DisplayStyle.None;
                for (var i = 0; i < _mentionSuggestionButtonPool.Count; i++)
                {
                    _mentionSuggestionButtonPool[i].style.display = DisplayStyle.None;
                }
                return;
            }

            _mentionSuggestionPanel.style.display = DisplayStyle.Flex;
            for (var i = 0; i < _mentionSuggestions.Count; i++)
            {
                EnsureMentionSuggestionButtonPoolSize(i + 1);
                var button = _mentionSuggestionButtonPool[i];
                button.text = _mentionSuggestions[i];
                button.userData = i;
                button.style.display = DisplayStyle.Flex;
                button.style.borderBottomWidth = i == _mentionSuggestions.Count - 1 ? 0f : 1f;
                button.style.borderBottomColor = new Color(0.23f, 0.27f, 0.33f, 1f);
                button.style.backgroundColor = i == _selectedMentionSuggestionIndex
                    ? new Color(0.16f, 0.36f, 0.78f, 0.95f)
                    : new Color(0.15f, 0.17f, 0.20f, 0.95f);
            }

            for (var i = _mentionSuggestions.Count; i < _mentionSuggestionButtonPool.Count; i++)
            {
                _mentionSuggestionButtonPool[i].style.display = DisplayStyle.None;
            }
        }

        private void EnsureMentionSuggestionButtonPoolSize(int targetSize)
        {
            if (_mentionSuggestionPanel == null)
            {
                return;
            }

            while (_mentionSuggestionButtonPool.Count < targetSize)
            {
                Button button = null;
                button = new Button(() =>
                {
                    if (button?.userData is int idx)
                    {
                        ApplyMentionSuggestion(idx);
                    }
                });

                button.style.height = MentionSuggestionItemHeight;
                button.style.minHeight = MentionSuggestionItemHeight;
                button.style.unityTextAlign = TextAnchor.MiddleLeft;
                button.style.paddingLeft = 6f;
                button.style.paddingRight = 6f;
                button.style.marginLeft = 0f;
                button.style.marginRight = 0f;
                button.style.marginTop = 0f;
                button.style.marginBottom = 0f;
                button.style.display = DisplayStyle.None;
                button.style.flexShrink = 0f;

                _mentionSuggestionButtonPool.Add(button);
                _mentionSuggestionPanel.Add(button);
            }
        }

        private void SelectMentionSuggestionDelta(int delta)
        {
            if (_mentionSuggestions.Count == 0)
            {
                _selectedMentionSuggestionIndex = -1;
                return;
            }

            if (_selectedMentionSuggestionIndex < 0)
            {
                _selectedMentionSuggestionIndex = 0;
            }
            else
            {
                _selectedMentionSuggestionIndex = (_selectedMentionSuggestionIndex + delta + _mentionSuggestions.Count) % _mentionSuggestions.Count;
            }

            RenderMentionSuggestions();
        }

        private void TryCommitSelectedMention()
        {
            if (_mentionSuggestions.Count == 0)
            {
                return;
            }

            var safeIndex = Mathf.Clamp(_selectedMentionSuggestionIndex, 0, _mentionSuggestions.Count - 1);
            ApplyMentionSuggestion(safeIndex);
        }

        private void ApplyMentionSuggestion(int index)
        {
            if (_inputField == null || index < 0 || index >= _mentionSuggestions.Count)
            {
                return;
            }

            var selected = NormalizeMentionSuggestionValue(_mentionSuggestions[index]);
            if (string.IsNullOrEmpty(selected))
            {
                HideMentionSuggestions();
                return;
            }

            var current = _inputField.value ?? string.Empty;
            var mentionStart = -1;
            var mentionEnd = -1;

            if (TryExtractTrailingMention(current, out var trailingStart, out _))
            {
                mentionStart = trailingStart;
                mentionEnd = current.Length;
            }
            else if (TryResolveMentionBoundsFromActiveState(current, out mentionStart, out mentionEnd))
            {
                // Fallback when enter key inserted whitespace before mention commit.
            }

            if (mentionStart < 0 || mentionEnd < mentionStart)
            {
                HideMentionSuggestions();
                return;
            }

            var replaceStart = mentionStart + 1;
            var replaceLength = mentionEnd - replaceStart;
            if (replaceStart < 0 || replaceStart > current.Length)
            {
                HideMentionSuggestions();
                return;
            }

            var prefix = current.Substring(0, replaceStart);
            var suffix = current.Substring(replaceStart + Mathf.Max(0, replaceLength));
            suffix = TrimLeadingMentionCommitWhitespace(suffix);

            var needsSpaceSuffix = string.IsNullOrEmpty(suffix) || !char.IsWhiteSpace(suffix[0]);
            var inserted = needsSpaceSuffix ? $"{selected} " : selected;
            var updated = prefix + inserted + suffix;

            _suppressMentionRefresh = true;
            _inputField.value = updated;
            _suppressMentionRefresh = false;
            HideMentionSuggestions();
            MoveInputCaretToEnd(true);
        }

        private bool TryResolveMentionBoundsFromActiveState(string current, out int mentionStart, out int mentionEnd)
        {
            mentionStart = _activeMentionStartIndex;
            mentionEnd = -1;
            if (string.IsNullOrEmpty(current) || mentionStart < 0 || mentionStart >= current.Length)
            {
                return false;
            }

            if (current[mentionStart] != '@')
            {
                mentionStart = current.LastIndexOf('@');
                if (mentionStart < 0 || mentionStart >= current.Length)
                {
                    return false;
                }
            }

            mentionEnd = mentionStart + 1;
            while (mentionEnd < current.Length && !char.IsWhiteSpace(current[mentionEnd]))
            {
                mentionEnd++;
            }

            return true;
        }

        private static string NormalizeMentionSuggestionValue(string rawValue)
        {
            if (string.IsNullOrWhiteSpace(rawValue))
            {
                return string.Empty;
            }

            var normalized = rawValue
                .Replace("\r", string.Empty)
                .Replace("\n", string.Empty)
                .Trim()
                .Replace('\\', '/');

            return normalized;
        }

        private static string TrimLeadingMentionCommitWhitespace(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            var index = 0;
            while (index < text.Length)
            {
                var ch = text[index];
                if (ch != '\r' && ch != '\n' && ch != '\t' && ch != ' ')
                {
                    break;
                }

                index++;
            }

            return index > 0 ? text.Substring(index) : text;
        }

        private static string NormalizeMentionBrokenLine(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
            return Regex.Replace(normalized, @"@([^\s@\n]+)\s*\n\s*/", "@$1/");
        }

        private void MoveInputCaretToEnd(bool defer)
        {
            if (_inputField == null)
            {
                return;
            }

            void Apply()
            {
                var value = _inputField.value ?? string.Empty;
                var end = value.Length;
                _inputField.Focus();
                SetTextFieldSelectionToEnd(_inputField, end);
            }

            if (defer)
            {
                _inputField.schedule.Execute(Apply).ExecuteLater(0);
            }
            else
            {
                Apply();
            }
        }

        private void HideMentionSuggestions()
        {
            _activeMentionStartIndex = -1;
            _activeMentionQuery = string.Empty;
            _selectedMentionSuggestionIndex = -1;
            _mentionSuggestions.Clear();
            if (_mentionSuggestionPanel != null)
            {
                _mentionSuggestionPanel.style.display = DisplayStyle.None;
                for (var i = 0; i < _mentionSuggestionButtonPool.Count; i++)
                {
                    _mentionSuggestionButtonPool[i].style.display = DisplayStyle.None;
                }
            }
        }

        private void EnsureProjectFileIndex()
        {
            var now = EditorApplication.timeSinceStartup;
            if (!_projectFileIndexDirty && now < _nextProjectFileIndexRefreshAt && _projectFileIndex.Count > 0)
            {
                return;
            }

            _projectFileIndexDirty = false;
            _nextProjectFileIndexRefreshAt = now + 8d;
            _projectFileIndex.Clear();

            var assetPaths = AssetDatabase.GetAllAssetPaths();
            for (var i = 0; i < assetPaths.Length; i++)
            {
                var path = assetPaths[i];
                if (string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                if (!path.StartsWith("Assets/", StringComparison.Ordinal) && !path.StartsWith("Packages/", StringComparison.Ordinal))
                {
                    continue;
                }

                if (path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                _projectFileIndex.Add(path.Replace('\\', '/'));
            }

            _projectFileIndex.Sort(StringComparer.OrdinalIgnoreCase);
        }

        private void MarkProjectFileIndexDirty()
        {
            _projectFileIndexDirty = true;
        }

        private void OnProjectChanged()
        {
            MarkProjectFileIndexDirty();
        }

        private static bool TryExtractTrailingMention(string text, out int mentionStart, out string query)
        {
            mentionStart = -1;
            query = string.Empty;
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            var atIndex = text.LastIndexOf('@');
            if (atIndex < 0)
            {
                return false;
            }

            if (atIndex > 0)
            {
                var prev = text[atIndex - 1];
                if (!char.IsWhiteSpace(prev) && prev != '(' && prev != '[' && prev != '{' && prev != '"' && prev != '\'')
                {
                    return false;
                }
            }

            for (var i = atIndex + 1; i < text.Length; i++)
            {
                if (char.IsWhiteSpace(text[i]))
                {
                    return false;
                }
            }

            mentionStart = atIndex;
            query = text.Substring(atIndex + 1);
            return true;
        }

        private List<string> ResolveTargetedFilesFromMentions(string text)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(text))
            {
                return result;
            }

            EnsureProjectFileIndex();
            var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var matches = MentionPathRegex.Matches(text);
            for (var i = 0; i < matches.Count; i++)
            {
                var rawToken = matches[i].Groups[1].Value;
                var token = SanitizeMentionToken(rawToken);
                if (string.IsNullOrWhiteSpace(token))
                {
                    continue;
                }

                if (!TryResolveMentionPath(token, out var resolved))
                {
                    continue;
                }

                if (unique.Add(resolved))
                {
                    result.Add(resolved);
                    if (result.Count >= MaxTargetedFilesPerTurn)
                    {
                        break;
                    }
                }
            }

            return result;
        }

        private bool TryResolveMentionPath(string rawPath, out string resolvedPath)
        {
            resolvedPath = string.Empty;
            if (string.IsNullOrWhiteSpace(rawPath))
            {
                return false;
            }

            var normalized = rawPath.Trim().Replace('\\', '/');
            if (normalized.StartsWith("./", StringComparison.Ordinal))
            {
                normalized = normalized.Substring(2);
            }

            if (normalized.StartsWith("/", StringComparison.Ordinal))
            {
                normalized = normalized.TrimStart('/');
            }

            for (var i = 0; i < _projectFileIndex.Count; i++)
            {
                var candidate = _projectFileIndex[i];
                if (string.Equals(candidate, normalized, StringComparison.OrdinalIgnoreCase))
                {
                    resolvedPath = candidate;
                    return true;
                }
            }

            if (!normalized.Contains("/"))
            {
                string found = null;
                for (var i = 0; i < _projectFileIndex.Count; i++)
                {
                    var candidate = _projectFileIndex[i];
                    var fileName = Path.GetFileName(candidate);
                    if (!string.Equals(fileName, normalized, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (found != null && !string.Equals(found, candidate, StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }

                    found = candidate;
                }

                if (!string.IsNullOrWhiteSpace(found))
                {
                    resolvedPath = found;
                    return true;
                }
            }

            var projectRoot = UniCodexChatHelper.GetProjectRootPath().Replace('\\', '/');
            if (Path.IsPathRooted(rawPath))
            {
                var normalizedAbsolute = Path.GetFullPath(rawPath).Replace('\\', '/');
                if (normalizedAbsolute.StartsWith(projectRoot + "/", StringComparison.OrdinalIgnoreCase))
                {
                    var relative = normalizedAbsolute.Substring(projectRoot.Length + 1);
                    for (var i = 0; i < _projectFileIndex.Count; i++)
                    {
                        if (string.Equals(_projectFileIndex[i], relative, StringComparison.OrdinalIgnoreCase))
                        {
                            resolvedPath = _projectFileIndex[i];
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        private static string SanitizeMentionToken(string rawToken)
        {
            if (string.IsNullOrWhiteSpace(rawToken))
            {
                return string.Empty;
            }

            var sanitized = rawToken.Trim();
            while (sanitized.Length > 0)
            {
                var ch = sanitized[sanitized.Length - 1];
                if (ch == ')' || ch == ']' || ch == '}' || ch == ',' || ch == '.' || ch == ';' || ch == ':' || ch == '\'' || ch == '"')
                {
                    sanitized = sanitized.Substring(0, sanitized.Length - 1);
                    continue;
                }

                break;
            }

            return sanitized;
        }

        // -------------------------
        // Environment / auth actions
        // -------------------------

        private void RefreshEnvironmentState()
        {
            RefreshEnvironmentState(false);
        }

        private void RefreshEnvironmentState(bool allowWhileBusy)
        {
            if (_isBusy && !allowWhileBusy)
            {
                return;
            }

            if (!_isBusy)
            {
                SetStatus("Checking codex and login status...");
            }
            Task.Run(() =>
            {
                var service = CreateCliService();
                return service.RefreshEnvironmentState();
            }).ContinueWith(task =>
            {
                var state = task.IsFaulted
                    ? new UniCodexEnvironmentState
                    {
                        Installed = false,
                        LoggedIn = false,
                        VersionText = "Unknown",
                        LoginText = task.Exception?.GetBaseException().Message ?? "Status check failed."
                    }
                    : task.Result;

                EditorApplication.delayCall += () =>
                {
                    _codexInstalled = state.Installed;
                    _codexLoggedIn = state.LoggedIn;
                    _codexVersionText = state.VersionText;
                    _loginStatusText = state.LoginText;
                    if (!string.IsNullOrWhiteSpace(state.ResolvedCliPath)
                        && !string.Equals(_cliPath, state.ResolvedCliPath, StringComparison.Ordinal))
                    {
                        _cliPath = state.ResolvedCliPath;
                        SavePrefs();
                    }

                    UpdateEnvironmentUI();
                    if (!_isBusy)
                    {
                        SetStatus("Ready");
                    }
                };
            });
        }

        private void RefreshLoginState()
        {
            if (!_codexInstalled)
            {
                RefreshEnvironmentState();
                return;
            }

            if (_isBusy)
            {
                return;
            }

            SetStatus("Checking login status...");
            Task.Run(() =>
            {
                var service = CreateCliService();
                var loginResult = service.QueryLoginStatus();
                return new UniCodexEnvironmentState
                {
                    Installed = _codexInstalled,
                    LoggedIn = loginResult.Success,
                    VersionText = _codexVersionText,
                    LoginText = loginResult.Message
                };
            }).ContinueWith(task =>
            {
                var state = task.IsFaulted
                    ? new UniCodexEnvironmentState
                    {
                        Installed = _codexInstalled,
                        LoggedIn = false,
                        VersionText = _codexVersionText,
                        LoginText = task.Exception?.GetBaseException().Message ?? "Login check failed."
                    }
                    : task.Result;

                EditorApplication.delayCall += () =>
                {
                    _codexLoggedIn = state.LoggedIn;
                    _loginStatusText = state.LoginText;
                    UpdateEnvironmentUI();
                    SetStatus("Ready");
                };
            });
        }

        private void LoginWithDeviceAuth()
        {
            if (_isBusy)
            {
                return;
            }

            if (!_codexInstalled)
            {
                AddMessage(ChatRole.Error, "Codex CLI was not found. Install it, then click Refresh in Settings.");
                return;
            }

            AddMessage(ChatRole.System, "Starting Codex login. Complete browser/device auth, then click Refresh in Settings.");
            SetStatus("Starting device login...");
            ConfigureUniCodexClient();
            UniCodex.Client.LoginAsync(new UniCodexLoginRequest { UseDeviceAuth = true }).ContinueWith(task =>
            {
                var result = task.IsFaulted
                    ? new UniCodexCommandResult
                    {
                        Success = false,
                        Message = task.Exception?.GetBaseException().Message ?? "Login failed."
                    }
                    : ToLegacyCommandResult(task.Result);

                EditorApplication.delayCall += () =>
                {
                    if (result.Success)
                    {
                        AddMessage(ChatRole.System, "Codex login completed.");
                    }
                    else
                    {
                        AddMessage(ChatRole.Error, $"Codex login failed: {result.Message}\nYou can also run `codex login --device-auth` in terminal.");
                    }

                    SetStatus("Ready");
                    RefreshLoginState();
                };
            });
        }

        private void LogoutCodex()
        {
            if (_isBusy || !_codexInstalled)
            {
                return;
            }

            SetBusy(true, "Logging out...");
            ConfigureUniCodexClient();
            UniCodex.Client.LogoutAsync().ContinueWith(task =>
            {
                var result = task.IsFaulted
                    ? new UniCodexCommandResult
                    {
                        Success = false,
                        Message = task.Exception?.GetBaseException().Message ?? "Logout failed."
                    }
                    : ToLegacyCommandResult(task.Result);

                EditorApplication.delayCall += () =>
                {
                    if (result.Success)
                    {
                        AddMessage(ChatRole.System, "Codex logout completed.");
                    }
                    else
                    {
                        AddMessage(ChatRole.Error, $"Codex logout failed: {result.Message}");
                    }

                    SetBusy(false, "Ready");
                    RefreshLoginState();
                };
            });
        }

        private void UpdateEnvironmentUI()
        {
            UpdateStatusUI();
        }

        // -------------------------
        // Chat request flow
        // -------------------------

        private void OnInputKeyDown(KeyDownEvent evt)
        {
            if (HandleMentionInputKeyDown(evt))
            {
                return;
            }

            if (_isBusy)
            {
                return;
            }

            if (evt.keyCode != KeyCode.Return && evt.keyCode != KeyCode.KeypadEnter)
            {
                return;
            }

            if (evt.shiftKey)
            {
                return;
            }

            evt.StopPropagation();
            evt.StopImmediatePropagation();
            SendCurrentInput();
        }

        private void SendCurrentInput()
        {
            if (_isBusy)
            {
                return;
            }

            if (!_codexInstalled)
            {
                AddMessage(ChatRole.Error, "Codex CLI is not installed. Install it, then click Refresh in Settings.");
                return;
            }

            if (!_codexLoggedIn)
            {
                AddMessage(ChatRole.Error, "Codex login is required. Click Login (Device) in Settings.");
                return;
            }

            var text = _inputField.value?.Trim();
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            HideMentionSuggestions();
            HideNewSessionPopup();
            _inputField.value = string.Empty;
            AddMessage(ChatRole.User, text);
            StartPendingAssistantMessage();

            var diffPreviewThisTurn = _chatMode == ChatMode.Build && _buildDiffPreviewMode;
            var prompt = diffPreviewThisTurn ? BuildDiffPreviewPrompt(text) : BuildPrompt(text);
            SetBusy(true, diffPreviewThisTurn ? "Codex is generating diff preview..." : "Codex is thinking...");
            IncrementActiveRuns();

            RunThroughUniCodexClient(prompt, diffPreviewThisTurn ? false : (bool?)null).ContinueWith(task =>
            {
                var result = task.IsFaulted
                    ? UniCodexRunResult.FromError(task.Exception?.GetBaseException().Message ?? "Unknown execution error")
                    : task.Result;
                DecrementActiveRuns();

                EditorApplication.delayCall += () => DispatchRunResult(result, diffPreviewThisTurn);
            });
        }

        private string BuildPrompt(string userText)
        {
            var targetedFiles = ResolveTargetedFilesFromMentions(userText);
            var sb = new StringBuilder();
            sb.AppendLine($"Chat mode: {_chatMode}");
            if (_chatMode == ChatMode.Plan)
            {
                sb.AppendLine("Mode instruction: Focus on analysis and step-by-step planning. Do not assume code edits are already applied.");
            }
            else
            {
                sb.AppendLine("Mode instruction: Focus on implementation details, concrete changes, and verification steps.");
            }

            var includeMarkdownContext = string.IsNullOrWhiteSpace(_sessionId) && targetedFiles.Count == 0;
            if (targetedFiles.Count > 0)
            {
                sb.AppendLine($"Prompt mode: Targeted file turn ({targetedFiles.Count} file(s) from @mentions).");
            }
            if (!includeMarkdownContext)
            {
                sb.AppendLine("Prompt mode: Compact follow-up turn (reduced context for faster response).");
            }

            sb.AppendLine();
            sb.Append(UniCodexChatHelper.BuildPrompt(
                userText,
                _markdownFiles,
                _maxMarkdownChars,
                includeMarkdownContext,
                targetedFiles,
                MaxTargetedFileChars));
            return sb.ToString();
        }

        private void EnsurePendingAssistantForActiveRun()
        {
            if (!HasActiveRuns())
            {
                return;
            }

            var loading = FindLatestLoadingMessage();
            if (loading != null)
            {
                StartPendingAssistantMessage(loading);
                return;
            }

            StartPendingAssistantMessage(null);
        }

        private string BuildDiffPreviewPrompt(string userText)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Build diff preview mode:");
            sb.AppendLine("- Do not apply or write any files.");
            sb.AppendLine("- Do not write Unity action bridge JSON.");
            sb.AppendLine("- Propose all required code changes as unified git diff.");
            sb.AppendLine("- Include every file that should change.");
            sb.AppendLine("- Response format:");
            sb.AppendLine("  1) Short refactor spec (what changed / why / risk or check points).");
            sb.AppendLine("  2) One ```diff fenced block with the full patch.");
            sb.AppendLine("- If no changes are needed, respond exactly with NO_CHANGES.");
            sb.AppendLine();
            sb.Append(BuildPrompt(userText));
            return sb.ToString();
        }

        private void HandleCodexResult(UniCodexRunResult result, bool diffPreviewTurn)
        {
            if (!string.IsNullOrWhiteSpace(result.ThreadId))
            {
                _sessionId = result.ThreadId;
                SavePrefs();
            }

            var tokenSummary = UniCodexCliService.BuildTokenSummary(result);
            if (!string.IsNullOrWhiteSpace(tokenSummary))
            {
                _lastTokenUsageText = tokenSummary;
            }

            AccumulateSessionTokens(result);
            if (!diffPreviewTurn)
            {
                ApplyPendingUnityActionsFromBridge();
            }

            if (result.Success)
            {
                var finalText = string.IsNullOrWhiteSpace(result.Message) ? "(Empty response)" : result.Message;
                if (diffPreviewTurn)
                {
                    ShowDiffPreviewWindow(finalText);
                    var diffSummaryMessage = BuildDiffPreviewAssistantMessage(finalText);
                    CompletePendingAssistantMessage(diffSummaryMessage, ChatRole.Assistant, tokenSummary);
                }
                else
                {
                    CompletePendingAssistantMessage(finalText, ChatRole.Assistant, tokenSummary);
                }
                SetBusy(false, $"Ready (turn tok: {FormatTokenCount(ComputeTurnTokenCost(result))})");
                return;
            }

            var errorText = string.IsNullOrWhiteSpace(result.Message) ? "Codex execution failed." : result.Message;
            CompletePendingAssistantMessage(errorText, ChatRole.Error, tokenSummary);
            SetBusy(false, "Codex execution failed");
        }

        private static void DispatchRunResult(UniCodexRunResult result, bool diffPreviewTurn)
        {
            var window = TryGetAnyChatWindow();
            if (window != null)
            {
                window.HandleCodexResult(result, diffPreviewTurn);
                return;
            }

            lock (DeferredRunLock)
            {
                DeferredRunResults.Enqueue(new UniCodexDeferredRunResult
                {
                    Result = result,
                    DiffPreviewTurn = diffPreviewTurn
                });
            }

            if (HasActiveRuns())
            {
                UniCodexToolbarShortcut.SetBusyState();
            }
            else
            {
                UniCodexToolbarShortcut.SetCompleteState();
            }
        }

        private void ApplyDeferredRunResults()
        {
            while (true)
            {
                UniCodexDeferredRunResult pending;
                lock (DeferredRunLock)
                {
                    if (DeferredRunResults.Count <= 0)
                    {
                        break;
                    }

                    pending = DeferredRunResults.Dequeue();
                }

                HandleCodexResult(pending.Result, pending.DiffPreviewTurn);
            }
        }

        private void ShowDiffPreviewWindow(string responseText)
        {
            if (string.IsNullOrWhiteSpace(responseText))
            {
                UniCodexDiffPreviewWindow.ShowDiff("Diff Preview", "NO_CHANGES", RequestDiffRefinementAsync);
                return;
            }

            if (string.Equals(responseText.Trim(), "NO_CHANGES", StringComparison.OrdinalIgnoreCase))
            {
                UniCodexDiffPreviewWindow.ShowDiff("Diff Preview", "NO_CHANGES", RequestDiffRefinementAsync);
                return;
            }

            var diffText = ExtractUnifiedDiffBlock(responseText);
            if (string.IsNullOrWhiteSpace(diffText))
            {
                // Fallback: show raw response so user can still inspect what model produced.
                diffText = responseText;
            }

            var sessionLabel = _sessionPopup?.value;
            var title = string.IsNullOrWhiteSpace(sessionLabel) ? "Diff Preview" : $"Diff Preview - {sessionLabel}";
            UniCodexDiffPreviewWindow.ShowDiff(title, diffText, RequestDiffRefinementAsync);
        }

        private Task<UniCodexRunResult> RequestDiffRefinementAsync(string currentDiff, string refineRequest)
        {
            var prompt = BuildDiffRefinementPrompt(currentDiff, refineRequest);
            return RunThroughUniCodexClient(prompt, false).ContinueWith(task =>
            {
                var result = task.IsFaulted
                    ? UniCodexRunResult.FromError(task.Exception?.GetBaseException().Message ?? "Diff refine execution error")
                    : task.Result;

                EditorApplication.delayCall += () => ApplyCodexResultRuntimeState(result);
                return result;
            });
        }

        internal Task<UniCodexRunResult> RequestDiffRefinementFromPreview(string currentDiff, string refineRequest)
        {
            return RequestDiffRefinementAsync(currentDiff, refineRequest);
        }

        internal static Func<string, string, Task<UniCodexRunResult>> TryGetDiffRefineHandler()
        {
            var window = TryGetAnyChatWindow();
            return window == null ? null : window.RequestDiffRefinementFromPreview;
        }

        private static UniCodexChatWindow TryGetAnyChatWindow()
        {
            var windows = Resources.FindObjectsOfTypeAll<UniCodexChatWindow>();
            if (windows == null || windows.Length == 0)
            {
                return null;
            }

            for (var i = 0; i < windows.Length; i++)
            {
                var window = windows[i];
                if (window != null)
                {
                    return window;
                }
            }

            return null;
        }

        private static void IncrementActiveRuns()
        {
            lock (ActiveRunCountLock)
            {
                ActiveRunCount++;
            }
        }

        private static void DecrementActiveRuns()
        {
            lock (ActiveRunCountLock)
            {
                if (ActiveRunCount > 0)
                {
                    ActiveRunCount--;
                }
            }
        }

        private static bool HasActiveRuns()
        {
            lock (ActiveRunCountLock)
            {
                return ActiveRunCount > 0;
            }
        }

        private static bool HasDeferredRunResults()
        {
            lock (DeferredRunLock)
            {
                return DeferredRunResults.Count > 0;
            }
        }

        private string BuildDiffRefinementPrompt(string currentDiff, string refineRequest)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Build diff refinement mode:");
            sb.AppendLine("- You are refining an existing unified diff before apply.");
            sb.AppendLine("- Return exactly one unified diff (prefer ```diff fenced block).");
            sb.AppendLine("- Keep valid patch structure (`---`, `+++`, `@@`).");
            sb.AppendLine("- Preserve untouched behavior unless explicitly requested.");
            sb.AppendLine("- If no changes are needed, respond exactly with NO_CHANGES.");
            sb.AppendLine();
            sb.AppendLine("Refine request:");
            sb.AppendLine(refineRequest ?? string.Empty);
            sb.AppendLine();
            sb.AppendLine("Current diff:");
            sb.AppendLine("```diff");
            sb.AppendLine(currentDiff ?? string.Empty);
            sb.AppendLine("```");
            return sb.ToString();
        }

        private void ApplyCodexResultRuntimeState(UniCodexRunResult result)
        {
            if (result == null)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(result.ThreadId))
            {
                _sessionId = result.ThreadId;
                SavePrefs();
            }

            var tokenSummary = UniCodexCliService.BuildTokenSummary(result);
            if (!string.IsNullOrWhiteSpace(tokenSummary))
            {
                _lastTokenUsageText = tokenSummary;
            }

            AccumulateSessionTokens(result);
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

        private static string BuildDiffPreviewAssistantMessage(string responseText)
        {
            var trimmed = responseText?.Trim() ?? string.Empty;
            if (string.Equals(trimmed, "NO_CHANGES", StringComparison.OrdinalIgnoreCase))
            {
                return "No code changes were needed.\n\n`Codex Diff Preview` window shows `NO_CHANGES`.";
            }

            var diffText = ExtractUnifiedDiffBlock(responseText);
            var narrative = ExtractDiffPreviewNarrative(responseText);
            var stats = BuildDiffQuickStats(diffText);

            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(narrative))
            {
                sb.AppendLine(narrative.Trim());
                sb.AppendLine();
            }
            else
            {
                sb.AppendLine("Diff preview generated.");
                sb.AppendLine();
            }

            sb.Append("Opened `Codex Diff Preview` window");
            if (!string.IsNullOrWhiteSpace(stats))
            {
                sb.Append(" (");
                sb.Append(stats);
                sb.Append(")");
            }

            sb.Append(". ");
            sb.Append("Use file tabs to refine/apply one-by-one in the preview window.");
            return sb.ToString();
        }

        private static string ExtractDiffPreviewNarrative(string responseText)
        {
            if (string.IsNullOrWhiteSpace(responseText))
            {
                return string.Empty;
            }

            var trimmed = responseText.Trim();
            if (string.Equals(trimmed, "NO_CHANGES", StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            // Preferred path: remove fenced diff/patch blocks and keep prose.
            var withoutFencedDiff = Regex.Replace(
                trimmed,
                "```(?:diff|patch)?\\s*\\n[\\s\\S]*?```",
                string.Empty,
                RegexOptions.IgnoreCase).Trim();
            if (!string.IsNullOrWhiteSpace(withoutFencedDiff))
            {
                return withoutFencedDiff;
            }

            // Fallback for unfenced diff output: keep prose before first diff marker.
            if (TryFindDiffStartIndex(trimmed, out var startIndex) && startIndex > 0)
            {
                var prefix = trimmed.Substring(0, startIndex).Trim();
                if (!string.IsNullOrWhiteSpace(prefix))
                {
                    return prefix;
                }
            }

            return string.Empty;
        }

        private static bool TryFindDiffStartIndex(string text, out int index)
        {
            index = -1;
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            UpdateMinimumIndex(ref index, text.IndexOf("diff --git ", StringComparison.OrdinalIgnoreCase));
            UpdateMinimumIndex(ref index, text.IndexOf("\ndiff --git ", StringComparison.OrdinalIgnoreCase));
            UpdateMinimumIndex(ref index, text.IndexOf("\n--- ", StringComparison.Ordinal));
            UpdateMinimumIndex(ref index, text.IndexOf("--- ", StringComparison.Ordinal));
            UpdateMinimumIndex(ref index, text.IndexOf("\n@@ ", StringComparison.Ordinal));
            UpdateMinimumIndex(ref index, text.IndexOf("@@ ", StringComparison.Ordinal));

            return index >= 0;
        }

        private static void UpdateMinimumIndex(ref int index, int candidate)
        {
            if (candidate < 0)
            {
                return;
            }

            if (index < 0 || candidate < index)
            {
                index = candidate;
            }
        }

        private static string BuildDiffQuickStats(string diffText)
        {
            if (string.IsNullOrWhiteSpace(diffText))
            {
                return string.Empty;
            }

            var normalized = diffText.Replace("\r\n", "\n").Replace('\r', '\n');
            var lines = normalized.Split('\n');
            var fileCount = 0;
            var added = 0;
            var removed = 0;

            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (line.StartsWith("diff --git ", StringComparison.OrdinalIgnoreCase))
                {
                    fileCount++;
                    continue;
                }

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

            if (fileCount == 0)
            {
                for (var i = 0; i + 1 < lines.Length; i++)
                {
                    if (lines[i].StartsWith("--- ", StringComparison.Ordinal)
                        && lines[i + 1].StartsWith("+++ ", StringComparison.Ordinal))
                    {
                        fileCount++;
                    }
                }
            }

            return $"files: {fileCount}, +{added} / -{removed}";
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

        private void ApplyPendingUnityActionsFromBridge()
        {
            if (!UniCodexUnityEditorHelper.TryApplyPendingActions(out var summary))
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(summary))
            {
                AddMessage(ChatRole.System, summary);
            }
        }

        // -------------------------
        // Unity editor controls
        // -------------------------

        private void ApplyEnterPlayModeSettings(bool addChatMessage)
        {
            var options = EnterPlayModeOptions.None;
            if (_disableDomainReloadOnPlay)
            {
                options |= EnterPlayModeOptions.DisableDomainReload;
            }

            if (_disableSceneReloadOnPlay)
            {
                options |= EnterPlayModeOptions.DisableSceneReload;
            }

            EditorSettings.enterPlayModeOptionsEnabled = options != EnterPlayModeOptions.None;
            EditorSettings.enterPlayModeOptions = options;

            if (addChatMessage)
            {
                AddMessage(ChatRole.System, $"EnterPlayMode applied: Enabled={EditorSettings.enterPlayModeOptionsEnabled}, Options={EditorSettings.enterPlayModeOptions}");
            }

            SetStatus("EnterPlayMode settings applied");
        }

        private void RefreshScriptsManually()
        {
            var shouldRelock = _autoRefreshLocked;
            if (shouldRelock)
            {
                SetAutoRefreshLock(false);
            }

            AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
            CompilationPipeline.RequestScriptCompilation();

            if (shouldRelock)
            {
                EditorApplication.delayCall += () => SetAutoRefreshLock(true);
            }

            SetStatus("Refresh + script compilation requested");
        }

        private void SetAutoRefreshLock(bool shouldLock)
        {
            if (shouldLock == _autoRefreshLocked)
            {
                return;
            }

            try
            {
                if (shouldLock)
                {
                    AssetDatabase.DisallowAutoRefresh();
                    _autoRefreshLocked = true;
                    SetStatus("Auto refresh locked (manual refresh mode)");
                    return;
                }

                AssetDatabase.AllowAutoRefresh();
                _autoRefreshLocked = false;
                SetStatus("Auto refresh enabled");
            }
            catch (Exception ex)
            {
                AddMessage(ChatRole.Error, $"AutoRefresh lock change failed: {ex.Message}");
            }
        }

        private void ReleaseAutoRefreshLock()
        {
            if (!_autoRefreshLocked)
            {
                return;
            }

            try
            {
                AssetDatabase.AllowAutoRefresh();
            }
            catch
            {
                // Best-effort unlock on shutdown/reload.
            }
            finally
            {
                _autoRefreshLocked = false;
            }
        }

        private void ResetSession()
        {
            _sessionId = string.Empty;
            _sessionTokenUsed = 0;
            _recentTurnTokenCosts.Clear();
            _lastTokenUsageText = "-";
            SaveActiveSessionSnapshot();
            SaveChatHistory();
            SavePrefs();
            AddMessage(ChatRole.System, "Session reset. Next message starts a new codex thread.");
            UpdateTokenGaugeUI();
            UpdateStatusUI();
        }

        private void ClearChat()
        {
            _messages.Clear();
            _pendingAssistantMessage = null;
            StopPendingAssistantAnimation();
            _lastTokenUsageText = "-";
            _sessionTokenUsed = 0;
            _recentTurnTokenCosts.Clear();
            _sessionId = string.Empty;
            SaveActiveSessionSnapshot();
            SaveChatHistory();
            SavePrefs();
            UpdateTokenGaugeUI();
            RefreshChatUI();
            SetStatus("Chat cleared and session reset");
        }

        // -------------------------
        // Chat rendering
        // -------------------------

        private UniCodexChatMessage AddMessage(ChatRole role, string text, string tokenSummary = null, bool isLoading = false)
        {
            var message = new UniCodexChatMessage
            {
                Role = role,
                Text = text,
                Time = DateTime.Now.ToString("HH:mm:ss"),
                TokenSummary = tokenSummary,
                IsLoading = isLoading
            };

            _messages.Add(message);
            if (!isLoading)
            {
                SaveChatHistory();
            }

            AddMessageToView(message);
            ScrollToBottom();
            return message;
        }

        private void StartPendingAssistantMessage(UniCodexChatMessage existingMessage = null)
        {
            StopPendingAssistantAnimation();
            lock (_progressUpdateLock)
            {
                _queuedProgressText = null;
                _progressDispatchPending = false;
            }

            _pendingDotCount = 0;
            _pendingProgressText = "Preparing request";
            _pendingProgressLines.Clear();
            _pendingProgressLines.Add(_pendingProgressText);
            if (existingMessage != null)
            {
                existingMessage.IsLoading = true;
                existingMessage.Role = ChatRole.Assistant;
                existingMessage.Text = BuildThinkingText(_pendingDotCount, _pendingProgressLines);
                existingMessage.Time = DateTime.Now.ToString("HH:mm:ss");
                existingMessage.TokenSummary = null;
                _pendingAssistantMessage = existingMessage;
                RefreshChatUI();
            }
            else
            {
                _pendingAssistantMessage = AddMessage(ChatRole.Assistant, BuildThinkingText(_pendingDotCount, _pendingProgressLines), null, true);
            }

            _pendingAnimationItem = rootVisualElement.schedule.Execute(() =>
            {
                if (_pendingAssistantMessage == null || !_pendingAssistantMessage.IsLoading)
                {
                    StopPendingAssistantAnimation();
                    return;
                }

                _pendingDotCount = (_pendingDotCount + 1) % 4;
                _pendingAssistantMessage.Text = BuildThinkingText(_pendingDotCount, _pendingProgressLines);
                RefreshChatUI();
            }).Every(450);
        }

        private void QueueCliProgressUpdate(string progressText)
        {
            if (string.IsNullOrWhiteSpace(progressText))
            {
                return;
            }

            lock (_progressUpdateLock)
            {
                _queuedProgressText = progressText;
                if (_progressDispatchPending)
                {
                    return;
                }

                _progressDispatchPending = true;
            }

            EditorApplication.delayCall += ApplyQueuedProgressUpdate;
        }

        private void ApplyQueuedProgressUpdate()
        {
            string textToApply;
            lock (_progressUpdateLock)
            {
                textToApply = _queuedProgressText;
                _queuedProgressText = null;
                _progressDispatchPending = false;
            }

            UpdatePendingProgressText(textToApply);
        }

        private void UpdatePendingProgressText(string progressText)
        {
            if (_pendingAssistantMessage == null || !_pendingAssistantMessage.IsLoading)
            {
                return;
            }

            var normalized = NormalizeProgressText(progressText);
            if (string.IsNullOrWhiteSpace(normalized) || string.Equals(normalized, _pendingProgressText, StringComparison.Ordinal))
            {
                return;
            }

            _pendingProgressText = normalized;
            _pendingProgressLines.Add(normalized);
            if (_pendingProgressLines.Count > 4)
            {
                _pendingProgressLines.RemoveAt(0);
            }

            _pendingAssistantMessage.Text = BuildThinkingText(_pendingDotCount, _pendingProgressLines);
            RefreshChatUI();
            ScrollToBottom();
        }

        private static string NormalizeProgressText(string rawText)
        {
            if (string.IsNullOrWhiteSpace(rawText))
            {
                return string.Empty;
            }

            var text = rawText.Replace("\r", " ").Replace("\n", " ").Trim();
            if (text.Length > 140)
            {
                text = text.Substring(0, 140) + "...";
            }

            return text;
        }

        private void StopPendingAssistantAnimation()
        {
            if (_pendingAnimationItem == null)
            {
                return;
            }

            _pendingAnimationItem.Pause();
            _pendingAnimationItem = null;
        }

        private void CompletePendingAssistantMessage(string text, ChatRole role, string tokenSummary)
        {
            StopPendingAssistantAnimation();
            lock (_progressUpdateLock)
            {
                _queuedProgressText = null;
                _progressDispatchPending = false;
            }
            _pendingProgressText = string.Empty;
            _pendingProgressLines.Clear();

            if (_pendingAssistantMessage == null)
            {
                _pendingAssistantMessage = FindLatestLoadingMessage();
                if (_pendingAssistantMessage == null)
                {
                    AddMessage(role, text, tokenSummary);
                    return;
                }
            }

            _pendingAssistantMessage.IsLoading = false;
            _pendingAssistantMessage.Role = role;
            _pendingAssistantMessage.Text = text;
            _pendingAssistantMessage.Time = DateTime.Now.ToString("HH:mm:ss");
            _pendingAssistantMessage.TokenSummary = tokenSummary;
            _pendingAssistantMessage = null;
            SaveChatHistory();
            RefreshChatUI();
            ScrollToBottom();
        }

        private UniCodexChatMessage FindLatestLoadingMessage()
        {
            for (var i = _messages.Count - 1; i >= 0; i--)
            {
                var message = _messages[i];
                if (message != null && message.IsLoading)
                {
                    return message;
                }
            }

            return null;
        }

        private void SynchronizeBusyState()
        {
            if (!_isBusy)
            {
                return;
            }

            if (HasActiveRuns() || HasDeferredRunResults())
            {
                return;
            }

            var loading = FindLatestLoadingMessage();
            if (loading != null)
            {
                loading.IsLoading = false;
                loading.Role = ChatRole.System;
                loading.Time = DateTime.Now.ToString("HH:mm:ss");
                loading.TokenSummary = null;
                loading.Text = "Previous request was interrupted. Please send again.";
                SaveChatHistory();
            }

            _pendingAssistantMessage = null;
            StopPendingAssistantAnimation();
            _isBusy = false;
            UniCodexToolbarShortcut.SetCompleteState();
            SetStatus("Ready");
        }

        private static string BuildThinkingText(int dotCount, List<string> progressLines)
        {
            var title = "Codex is thinking" + new string('.', dotCount);
            if (progressLines == null || progressLines.Count == 0)
            {
                return title + "\nWorking...";
            }

            var sb = new StringBuilder();
            sb.AppendLine(title);
            for (var i = 0; i < progressLines.Count; i++)
            {
                var line = progressLines[i];
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                sb.Append("- ").AppendLine(line);
            }

            return sb.ToString().TrimEnd();
        }

        private void RefreshChatUI()
        {
            if (_chatScrollView == null)
            {
                return;
            }

            _chatScrollView.contentContainer.Clear();
            for (var i = 0; i < _messages.Count; i++)
            {
                AddMessageToView(_messages[i]);
            }

            ScrollToBottom();
        }

        private void AddMessageToView(UniCodexChatMessage message)
        {
            if (_chatScrollView == null)
            {
                return;
            }

            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignSelf = Align.Stretch;
            row.style.marginBottom = 6f;
            row.style.justifyContent = message.Role == ChatRole.User ? Justify.FlexEnd : Justify.FlexStart;
            row.style.paddingLeft = 2f;
            row.style.paddingRight = 2f;

            var bubble = new VisualElement();
            bubble.style.maxWidth = new StyleLength(new Length(78f, LengthUnit.Percent));
            bubble.style.flexShrink = 1f;
            bubble.style.minWidth = 0f;
            bubble.style.paddingBottom = 6f;
            bubble.style.paddingLeft = 8f;
            bubble.style.paddingRight = 8f;
            bubble.style.paddingTop = 6f;
            bubble.style.borderBottomLeftRadius = 6f;
            bubble.style.borderBottomRightRadius = 6f;
            bubble.style.borderTopLeftRadius = 6f;
            bubble.style.borderTopRightRadius = 6f;

            var headerText = $"{message.Role}  {message.Time}";

            var roleLabel = new Label(headerText);
            ApplyPreferredFont(roleLabel);
            roleLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            roleLabel.style.marginBottom = 2f;
            roleLabel.style.fontSize = 11f;
            roleLabel.style.color = new Color(0.85f, 0.85f, 0.85f, 1f);
            bubble.Add(roleLabel);

            AddMessageContent(bubble, message);

            switch (message.Role)
            {
                case ChatRole.User:
                    bubble.style.backgroundColor = new Color(0.14f, 0.34f, 0.76f, 1f);
                    break;
                case ChatRole.Assistant:
                    bubble.style.backgroundColor = new Color(0.20f, 0.20f, 0.22f, 1f);
                    break;
                case ChatRole.System:
                    bubble.style.backgroundColor = new Color(0.25f, 0.32f, 0.18f, 1f);
                    break;
                default:
                    bubble.style.backgroundColor = new Color(0.55f, 0.18f, 0.18f, 1f);
                    break;
            }

            row.Add(bubble);
            _chatScrollView.Add(row);
        }

        private void AddMessageContent(VisualElement bubble, UniCodexChatMessage message)
        {
            if (message == null)
            {
                return;
            }

            if (message.IsLoading)
            {
                AddPlainTextLine(bubble, message.Text);
                return;
            }

            AddMarkdownContent(bubble, message.Text);
        }

        private void AddMarkdownContent(VisualElement parent, string markdown)
        {
            var text = markdown ?? string.Empty;
            var normalized = text.Replace("\r\n", "\n");
            var lines = normalized.Split('\n');

            var inCodeBlock = false;
            var codeBuilder = new StringBuilder();

            for (var i = 0; i < lines.Length; i++)
            {
                var rawLine = lines[i] ?? string.Empty;
                var trimmed = rawLine.TrimStart();

                if (trimmed.StartsWith("```", StringComparison.Ordinal))
                {
                    if (inCodeBlock)
                    {
                        AddCodeBlock(parent, codeBuilder.ToString());
                        codeBuilder.Clear();
                        inCodeBlock = false;
                    }
                    else
                    {
                        inCodeBlock = true;
                    }

                    continue;
                }

                if (inCodeBlock)
                {
                    codeBuilder.AppendLine(rawLine);
                    continue;
                }

                if (string.IsNullOrWhiteSpace(rawLine))
                {
                    AddPlainTextLine(parent, " ");
                    continue;
                }

                if (trimmed.StartsWith("> ", StringComparison.Ordinal))
                {
                    AddRichTextLine(parent, $"<i>{FormatInlineMarkdown(trimmed.Substring(2))}</i>", new Color(0.78f, 0.86f, 0.96f, 1f));
                    continue;
                }

                if (trimmed.StartsWith("- ", StringComparison.Ordinal) || trimmed.StartsWith("* ", StringComparison.Ordinal) || trimmed.StartsWith("+ ", StringComparison.Ordinal))
                {
                    AddRichTextLine(parent, $"• {FormatInlineMarkdown(trimmed.Substring(2))}");
                    continue;
                }

                if (StartsWithNumberedList(trimmed, out var listText))
                {
                    AddRichTextLine(parent, listText);
                    continue;
                }

                var headingLevel = GetHeadingLevel(trimmed, out var headingText);
                if (headingLevel > 0)
                {
                    var size = headingLevel == 1 ? 16 : headingLevel == 2 ? 15 : 14;
                    AddRichTextLine(parent, $"<b><size={size}>{FormatInlineMarkdown(headingText)}</size></b>");
                    continue;
                }

                AddRichTextLine(parent, FormatInlineMarkdown(rawLine));
            }

            if (inCodeBlock && codeBuilder.Length > 0)
            {
                AddCodeBlock(parent, codeBuilder.ToString());
            }
        }

        private void AddCodeBlock(VisualElement parent, string code)
        {
            var container = new VisualElement();
            container.style.marginTop = 3f;
            container.style.marginBottom = 3f;
            container.style.paddingBottom = 6f;
            container.style.paddingTop = 6f;
            container.style.paddingLeft = 8f;
            container.style.paddingRight = 8f;
            container.style.backgroundColor = new Color(0.13f, 0.13f, 0.14f, 1f);
            container.style.borderBottomLeftRadius = 4f;
            container.style.borderBottomRightRadius = 4f;
            container.style.borderTopLeftRadius = 4f;
            container.style.borderTopRightRadius = 4f;

            var normalized = (code ?? string.Empty).Replace("\t", "    ");
            var label = new Label(normalized);
            ApplyPreferredFont(label);
            // Code blocks should render raw characters like < and > as-is.
            label.enableRichText = false;
            label.style.whiteSpace = WhiteSpace.PreWrap;
            label.style.fontSize = 12f;
            label.style.color = new Color(0.87f, 0.91f, 0.95f, 1f);
            container.Add(label);

            parent.Add(container);
        }

        private void AddPlainTextLine(VisualElement parent, string text)
        {
            var label = new Label(text ?? string.Empty);
            ApplyPreferredFont(label);
            label.style.whiteSpace = WhiteSpace.PreWrap;
            label.style.flexShrink = 1f;
            label.style.minWidth = 0f;
            label.style.unityTextAlign = TextAnchor.UpperLeft;
            label.style.fontSize = 12f;
            label.style.color = new Color(0.96f, 0.96f, 0.96f, 1f);
            parent.Add(label);
        }

        private void AddRichTextLine(VisualElement parent, string text, Color? color = null)
        {
            var label = new Label(text ?? string.Empty);
            ApplyPreferredFont(label);
            label.enableRichText = true;
            label.style.whiteSpace = WhiteSpace.PreWrap;
            label.style.flexShrink = 1f;
            label.style.minWidth = 0f;
            label.style.unityTextAlign = TextAnchor.UpperLeft;
            label.style.fontSize = 12f;
            label.style.color = color ?? new Color(0.96f, 0.96f, 0.96f, 1f);
            parent.Add(label);
        }

        private static int GetHeadingLevel(string trimmedLine, out string headingText)
        {
            headingText = string.Empty;
            var level = 0;
            while (level < trimmedLine.Length && trimmedLine[level] == '#')
            {
                level++;
            }

            if (level == 0 || level > 6)
            {
                return 0;
            }

            if (trimmedLine.Length <= level || trimmedLine[level] != ' ')
            {
                return 0;
            }

            headingText = trimmedLine.Substring(level + 1);
            return level;
        }

        private static bool StartsWithNumberedList(string trimmedLine, out string text)
        {
            text = string.Empty;
            var dotIndex = trimmedLine.IndexOf('.');
            if (dotIndex <= 0)
            {
                return false;
            }

            for (var i = 0; i < dotIndex; i++)
            {
                if (!char.IsDigit(trimmedLine[i]))
                {
                    return false;
                }
            }

            if (dotIndex + 1 >= trimmedLine.Length || trimmedLine[dotIndex + 1] != ' ')
            {
                return false;
            }

            text = $"{trimmedLine.Substring(0, dotIndex + 1)} {FormatInlineMarkdown(trimmedLine.Substring(dotIndex + 2))}";
            return true;
        }

        private static string FormatInlineMarkdown(string line)
        {
            var text = EscapeRichText(line ?? string.Empty);
            text = MarkdownLinkRegex.Replace(text, "$1 ($2)");

            // Protect inline code spans before applying other inline markdown transforms.
            var inlineCodeSegments = new List<string>();
            text = MarkdownCodeRegex.Replace(text, match =>
            {
                var token = $"%%CODESEG{inlineCodeSegments.Count}%%";
                inlineCodeSegments.Add($"<color=#E6C07B><b>{match.Groups[1].Value}</b></color>");
                return token;
            });

            text = MarkdownBoldAsteriskRegex.Replace(text, "<b>$1</b>");
            text = MarkdownBoldUnderscoreRegex.Replace(text, "<b>$1</b>");
            text = MarkdownItalicAsteriskRegex.Replace(text, "<i>$1</i>");
            text = MarkdownItalicUnderscoreRegex.Replace(text, "<i>$1</i>");

            for (var i = 0; i < inlineCodeSegments.Count; i++)
            {
                text = text.Replace($"%%CODESEG{i}%%", inlineCodeSegments[i]);
            }

            return text.Replace("\\*", "*").Replace("\\_", "_").Replace("\\`", "`");
        }

        private static string EscapeRichText(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            return value
                .Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;");
        }

        private void ScrollToBottom()
        {
            if (_chatScrollView == null)
            {
                return;
            }

            _chatScrollView.schedule.Execute(() =>
            {
                _chatScrollView.scrollOffset = new Vector2(0f, float.MaxValue);
            }).ExecuteLater(10);
        }

        // -------------------------
        // Chat sessions
        // -------------------------

        private void CreateNewChatSession(string preferredName = null)
        {
            if (_isBusy)
            {
                AddMessage(ChatRole.System, "Cannot create a new session while a request is running.");
                return;
            }

            SaveActiveSessionSnapshot();

            var normalizedName = NormalizeSessionName(preferredName);
            var session = new UniCodexChatSessionInfo
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = string.IsNullOrWhiteSpace(normalizedName) ? $"Session {_chatSessions.Count + 1}" : normalizedName
            };

            _chatSessions.Add(session);
            SwitchToSession(session.Id);
            SetStatus("Ready");
        }

        private void OnSessionPopupChanged(ChangeEvent<string> evt)
        {
            if (_suppressSessionChangeEvent || _sessionPopup == null)
            {
                return;
            }

            var selectedLabel = evt.newValue ?? string.Empty;
            var index = _sessionOptionLabels.IndexOf(selectedLabel);
            if (index < 0 || index >= _sessionOptionIds.Count)
            {
                return;
            }

            var sessionId = _sessionOptionIds[index];
            if (string.IsNullOrWhiteSpace(sessionId) || string.Equals(sessionId, _activeChatSessionId, StringComparison.Ordinal))
            {
                return;
            }

            SwitchToSession(sessionId);
        }

        private void SwitchToSession(string sessionId)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                return;
            }

            var target = _chatSessions.Find(s => string.Equals(s?.Id, sessionId, StringComparison.Ordinal));
            if (target == null)
            {
                return;
            }

            SaveActiveSessionSnapshot();
            _activeChatSessionId = target.Id;
            ApplySessionToRuntime(target);
            StopPendingAssistantAnimation();
            HideMentionSuggestions();
            HideNewSessionPopup();
            RefreshSessionPopupChoices();
            RefreshChatUI();
            UpdateTokenGaugeUI();
            UpdateStatusUI();
            SaveChatHistory();
            SavePrefs();
        }

        private void RefreshSessionPopupChoices()
        {
            if (_sessionPopup == null)
            {
                return;
            }

            _sessionOptionLabels.Clear();
            _sessionOptionIds.Clear();

            for (var i = 0; i < _chatSessions.Count; i++)
            {
                var session = _chatSessions[i];
                if (session == null)
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(session.Id))
                {
                    session.Id = Guid.NewGuid().ToString("N");
                }

                if (string.IsNullOrWhiteSpace(session.Name))
                {
                    session.Name = $"Session {i + 1}";
                }

                _sessionOptionIds.Add(session.Id);
                _sessionOptionLabels.Add($"{i + 1}. {session.Name}");
            }

            if (_sessionOptionLabels.Count == 0)
            {
                var fallback = new UniCodexChatSessionInfo
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Name = "Session 1"
                };
                _chatSessions.Add(fallback);
                _sessionOptionIds.Add(fallback.Id);
                _sessionOptionLabels.Add("1. Session 1");
            }

            if (string.IsNullOrWhiteSpace(_activeChatSessionId))
            {
                _activeChatSessionId = _sessionOptionIds[0];
            }

            var activeIndex = _sessionOptionIds.IndexOf(_activeChatSessionId);
            if (activeIndex < 0)
            {
                activeIndex = 0;
                _activeChatSessionId = _sessionOptionIds[0];
            }

            _suppressSessionChangeEvent = true;
            _sessionPopup.choices.Clear();
            for (var i = 0; i < _sessionOptionLabels.Count; i++)
            {
                _sessionPopup.choices.Add(_sessionOptionLabels[i]);
            }
            _sessionPopup.SetValueWithoutNotify(_sessionOptionLabels[Mathf.Clamp(activeIndex, 0, _sessionOptionLabels.Count - 1)]);
            _suppressSessionChangeEvent = false;
        }

        private void EnsureSessionCollectionInitialized()
        {
            if (_chatSessions.Count > 0)
            {
                return;
            }

            var session = new UniCodexChatSessionInfo
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = "Session 1"
            };

            for (var i = 0; i < _messages.Count; i++)
            {
                var message = _messages[i];
                if (message == null || message.IsLoading)
                {
                    continue;
                }

                session.Messages.Add(new UniCodexChatHistoryItem
                {
                    Role = (int)message.Role,
                    Text = message.Text,
                    Time = message.Time,
                    TokenSummary = message.TokenSummary
                });
            }

            session.CodexSessionId = _sessionId ?? string.Empty;
            session.TokenUsed = _sessionTokenUsed;
            foreach (var turnCost in _recentTurnTokenCosts)
            {
                session.RecentTurnTokenCosts.Add(Mathf.Max(0, turnCost));
            }

            _chatSessions.Add(session);
            _activeChatSessionId = session.Id;
        }

        private UniCodexChatSessionInfo GetActiveChatSession()
        {
            if (string.IsNullOrWhiteSpace(_activeChatSessionId))
            {
                return null;
            }

            return _chatSessions.Find(s => s != null && string.Equals(s.Id, _activeChatSessionId, StringComparison.Ordinal));
        }

        private void SaveActiveSessionSnapshot()
        {
            var active = GetActiveChatSession();
            if (active == null)
            {
                return;
            }

            active.CodexSessionId = _sessionId ?? string.Empty;
            active.TokenUsed = Mathf.Max(0, _sessionTokenUsed);
            active.Messages.Clear();
            active.RecentTurnTokenCosts.Clear();

            foreach (var turnCost in _recentTurnTokenCosts)
            {
                active.RecentTurnTokenCosts.Add(Mathf.Max(0, turnCost));
            }

            for (var i = 0; i < _messages.Count; i++)
            {
                var message = _messages[i];
                if (message == null || message.IsLoading)
                {
                    continue;
                }

                active.Messages.Add(new UniCodexChatHistoryItem
                {
                    Role = (int)message.Role,
                    Text = message.Text,
                    Time = message.Time,
                    TokenSummary = message.TokenSummary
                });
            }
        }

        private void ApplySessionToRuntime(UniCodexChatSessionInfo session)
        {
            if (session == null)
            {
                return;
            }

            _messages.Clear();
            var sourceMessages = session.Messages ?? new List<UniCodexChatHistoryItem>();
            for (var i = 0; i < sourceMessages.Count; i++)
            {
                var item = sourceMessages[i];
                if (item == null)
                {
                    continue;
                }

                _messages.Add(new UniCodexChatMessage
                {
                    Role = ParseChatRole(item.Role),
                    Text = item.Text ?? string.Empty,
                    Time = string.IsNullOrWhiteSpace(item.Time) ? DateTime.Now.ToString("HH:mm:ss") : item.Time,
                    TokenSummary = item.TokenSummary,
                    IsLoading = false
                });
            }

            _sessionId = session.CodexSessionId ?? string.Empty;
            _sessionTokenUsed = Mathf.Max(0, session.TokenUsed);
            _recentTurnTokenCosts.Clear();
            if (session.RecentTurnTokenCosts != null)
            {
                for (var i = 0; i < session.RecentTurnTokenCosts.Count; i++)
                {
                    _recentTurnTokenCosts.Enqueue(Mathf.Max(0, session.RecentTurnTokenCosts[i]));
                }
            }
        }

        // -------------------------
        // Status + prefs
        // -------------------------

        private void SetBusy(bool busy, string status)
        {
            var wasBusy = _isBusy;
            _isBusy = busy;
            if (busy)
            {
                UniCodexToolbarShortcut.SetBusyState();
            }
            else if (wasBusy)
            {
                if (HasActiveRuns())
                {
                    UniCodexToolbarShortcut.SetBusyState();
                }
                else
                {
                    UniCodexToolbarShortcut.SetCompleteState();
                }
            }

            SetStatus(status);
        }

        private void SetStatus(string status)
        {
            _statusText = status;
            UpdateStatusUI();
        }

        private void UpdateStatusUI()
        {
            var canSend = IsChatReady();
            var isBusy = _isBusy;
            var reasonText = BuildReadinessReasonText(canSend);
            var availableColor = canSend
                ? new Color(0.17f, 0.56f, 0.94f, 1f)
                : new Color(0.86f, 0.26f, 0.26f, 1f);
            if (_availabilityDot != null)
            {
                _availabilityDot.style.backgroundColor = availableColor;
            }

            if (_availabilityLabel != null)
            {
                _availabilityLabel.text = isBusy ? "Busy" : (canSend ? "Ready" : "Not Ready");
                _availabilityLabel.tooltip = reasonText;
            }

            if (_settingsStateDot != null)
            {
                _settingsStateDot.style.backgroundColor = availableColor;
            }

            if (_settingsStateLabel != null)
            {
                _settingsStateLabel.text = isBusy
                    ? "Busy"
                    : (canSend ? "Ready" : $"Not Ready ({reasonText})");
                _settingsStateLabel.tooltip = _statusText;
            }

            UpdateTokenGaugeUI();
            _sendButton?.SetEnabled(canSend);
            _inputField?.SetEnabled(canSend);
            if (_sendButton != null)
            {
                _sendButton.style.opacity = canSend ? 1f : 0.62f;
            }

            if (_inputField != null)
            {
                _inputField.style.opacity = canSend ? 1f : 0.84f;
            }
            _sessionPopup?.SetEnabled(!isBusy);
            _newSessionButton?.SetEnabled(!isBusy);
            _newSessionNameField?.SetEnabled(!isBusy);
            _newSessionCreateButton?.SetEnabled(!isBusy);
            if (!canSend)
            {
                HideMentionSuggestions();
            }
            if (isBusy)
            {
                HideNewSessionPopup();
            }
        }

        private bool IsChatReady()
        {
            return !_isBusy && _codexInstalled && _codexLoggedIn;
        }

        private string BuildReadinessReasonText(bool canSend)
        {
            if (canSend)
            {
                return "Ready";
            }

            if (_isBusy)
            {
                return "Busy";
            }

            if (!_codexInstalled)
            {
                return "Missing Codex";
            }

            if (!_codexLoggedIn)
            {
                return "Login required";
            }

            return "Not ready";
        }

        private static VisualElement CreateStatusDot(float size)
        {
            var dot = new VisualElement();
            dot.style.width = size;
            dot.style.height = size;
            var radius = size * 0.5f;
            dot.style.borderBottomLeftRadius = radius;
            dot.style.borderBottomRightRadius = radius;
            dot.style.borderTopLeftRadius = radius;
            dot.style.borderTopRightRadius = radius;
            return dot;
        }

        private static Texture2D GetSettingsIconTexture()
        {
            if (_settingsIconTexture != null)
            {
                return _settingsIconTexture;
            }

            var iconNames = new[]
            {
                "d_SettingsIcon",
                "SettingsIcon",
                "d__Popup",
                "_Popup"
            };

            for (var i = 0; i < iconNames.Length; i++)
            {
                var content = EditorGUIUtility.IconContent(iconNames[i]);
                if (content?.image is Texture2D texture)
                {
                    _settingsIconTexture = texture;
                    return _settingsIconTexture;
                }
            }

            return null;
        }

        private void LoadPrefs()
        {
            var prefix = UniCodexCliConstants.PrefPrefix;
            _cliPath = EditorPrefs.GetString(prefix + "CliPath", UniCodexCliConstants.DefaultCliPath);
            _markdownFiles = EditorPrefs.GetString(prefix + "MarkdownFiles", UniCodexCliConstants.DefaultMarkdownFiles);
            _maxMarkdownChars = Mathf.Max(500, EditorPrefs.GetInt(prefix + "MaxMarkdownChars", UniCodexCliConstants.DefaultMaxMarkdownChars));
            _disableDomainReloadOnPlay = EditorPrefs.GetBool(prefix + "DisableDomainReload", true);
            _disableSceneReloadOnPlay = EditorPrefs.GetBool(prefix + "DisableSceneReload", false);
            _manualRefreshMode = EditorPrefs.GetBool(prefix + "ManualRefreshMode", true);
            _buildDiffPreviewMode = EditorPrefs.GetBool(prefix + "BuildDiffPreviewMode", false);
            _sessionId = EditorPrefs.GetString(prefix + "SessionId", string.Empty);
            _activeChatSessionId = EditorPrefs.GetString(prefix + "ActiveChatSessionId", string.Empty);
            var modeText = EditorPrefs.GetString(prefix + "ChatMode", ChatMode.Build.ToString());
            _chatMode = Enum.TryParse(modeText, out ChatMode parsedMode) ? parsedMode : ChatMode.Build;
            _selectedModel = NormalizeOption(EditorPrefs.GetString(prefix + "Model", DefaultModel), ModelOptions, DefaultModel);
            _selectedReasoningEffort = NormalizeOption(EditorPrefs.GetString(prefix + "ReasoningEffort", DefaultReasoningEffort), ReasoningEffortOptions, DefaultReasoningEffort);
            _sessionTokenUsed = 0;
            _recentTurnTokenCosts.Clear();
            _sessionTokenBudget = Mathf.Max(1000, EditorPrefs.GetInt(prefix + "SessionTokenBudget", UniCodexCliConstants.DefaultSessionTokenBudget));
        }

        private void SavePrefs()
        {
            var prefix = UniCodexCliConstants.PrefPrefix;
            EditorPrefs.SetString(prefix + "CliPath", _cliPath ?? UniCodexCliConstants.DefaultCliPath);
            EditorPrefs.SetString(prefix + "MarkdownFiles", _markdownFiles ?? string.Empty);
            EditorPrefs.SetInt(prefix + "MaxMarkdownChars", Mathf.Max(500, _maxMarkdownChars));
            EditorPrefs.SetBool(prefix + "DisableDomainReload", _disableDomainReloadOnPlay);
            EditorPrefs.SetBool(prefix + "DisableSceneReload", _disableSceneReloadOnPlay);
            EditorPrefs.SetBool(prefix + "ManualRefreshMode", _manualRefreshMode);
            EditorPrefs.SetBool(prefix + "BuildDiffPreviewMode", _buildDiffPreviewMode);
            EditorPrefs.SetString(prefix + "SessionId", _sessionId ?? string.Empty);
            EditorPrefs.SetString(prefix + "ActiveChatSessionId", _activeChatSessionId ?? string.Empty);
            EditorPrefs.SetString(prefix + "ChatMode", _chatMode.ToString());
            EditorPrefs.SetString(prefix + "Model", NormalizeOption(_selectedModel, ModelOptions, DefaultModel));
            EditorPrefs.SetString(prefix + "ReasoningEffort", NormalizeOption(_selectedReasoningEffort, ReasoningEffortOptions, DefaultReasoningEffort));
            EditorPrefs.SetInt(prefix + "SessionTokenBudget", Mathf.Max(1000, _sessionTokenBudget));
        }

        /// <summary>
        /// 프로젝트 로컬 캐시 파일에서 채팅 이력을 복원합니다.
        /// </summary>
        private void LoadChatHistory()
        {
            _chatSessions.Clear();
            _messages.Clear();
            var historyPath = GetChatHistoryPath();
            if (!File.Exists(historyPath))
            {
                EnsureSessionCollectionInitialized();
                var firstSession = _chatSessions.Count > 0 ? _chatSessions[0] : null;
                if (firstSession != null)
                {
                    _activeChatSessionId = firstSession.Id;
                    ApplySessionToRuntime(firstSession);
                }
                return;
            }

            try
            {
                var json = File.ReadAllText(historyPath);
                if (string.IsNullOrWhiteSpace(json))
                {
                    EnsureSessionCollectionInitialized();
                    var first = _chatSessions.Count > 0 ? _chatSessions[0] : null;
                    if (first != null)
                    {
                        _activeChatSessionId = first.Id;
                        ApplySessionToRuntime(first);
                    }
                    return;
                }

                var state = JsonUtility.FromJson<UniCodexChatHistoryState>(json);
                if (state == null)
                {
                    EnsureSessionCollectionInitialized();
                    var first = _chatSessions.Count > 0 ? _chatSessions[0] : null;
                    if (first != null)
                    {
                        _activeChatSessionId = first.Id;
                        ApplySessionToRuntime(first);
                    }
                    return;
                }

                var loadedFromSessions = state.Sessions != null && state.Sessions.Count > 0;
                if (loadedFromSessions)
                {
                    for (var i = 0; i < state.Sessions.Count; i++)
                    {
                        var session = state.Sessions[i];
                        if (session == null)
                        {
                            continue;
                        }

                        if (string.IsNullOrWhiteSpace(session.Id))
                        {
                            session.Id = Guid.NewGuid().ToString("N");
                        }

                        if (string.IsNullOrWhiteSpace(session.Name))
                        {
                            session.Name = $"Session {_chatSessions.Count + 1}";
                        }

                        if (session.Messages == null)
                        {
                            session.Messages = new List<UniCodexChatHistoryItem>();
                        }

                        if (session.RecentTurnTokenCosts == null)
                        {
                            session.RecentTurnTokenCosts = new List<int>();
                        }

                        _chatSessions.Add(session);
                    }
                }

                if (!loadedFromSessions)
                {
                    var fallback = new UniCodexChatSessionInfo
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        Name = "Session 1",
                        Messages = state.Messages ?? new List<UniCodexChatHistoryItem>()
                    };
                    _chatSessions.Add(fallback);
                }

                EnsureSessionCollectionInitialized();
                var requestedActiveId = !string.IsNullOrWhiteSpace(_activeChatSessionId)
                    ? _activeChatSessionId
                    : state.ActiveSessionId;
                _activeChatSessionId = string.IsNullOrWhiteSpace(requestedActiveId) ? _chatSessions[0].Id : requestedActiveId;
                var active = GetActiveChatSession() ?? _chatSessions[0];
                _activeChatSessionId = active.Id;
                ApplySessionToRuntime(active);
            }
            catch
            {
                // Ignore history parse/read failures and start with an empty chat.
                _chatSessions.Clear();
                _messages.Clear();
                EnsureSessionCollectionInitialized();
                var session = _chatSessions.Count > 0 ? _chatSessions[0] : null;
                if (session != null)
                {
                    _activeChatSessionId = session.Id;
                    ApplySessionToRuntime(session);
                }
            }
        }

        /// <summary>
        /// 창을 다시 열었을 때 이전 메시지를 복원할 수 있도록 채팅 이력을 저장합니다.
        /// </summary>
        private void SaveChatHistory()
        {
            EnsureSessionCollectionInitialized();
            SaveActiveSessionSnapshot();

            var state = new UniCodexChatHistoryState();
            state.ActiveSessionId = _activeChatSessionId;
            for (var i = 0; i < _chatSessions.Count; i++)
            {
                var session = _chatSessions[i];
                if (session == null)
                {
                    continue;
                }

                var copy = new UniCodexChatSessionInfo
                {
                    Id = session.Id,
                    Name = session.Name,
                    CodexSessionId = session.CodexSessionId,
                    TokenUsed = session.TokenUsed,
                    Messages = new List<UniCodexChatHistoryItem>(),
                    RecentTurnTokenCosts = new List<int>()
                };

                if (session.Messages != null)
                {
                    for (var msgIndex = 0; msgIndex < session.Messages.Count; msgIndex++)
                    {
                        var item = session.Messages[msgIndex];
                        if (item == null)
                        {
                            continue;
                        }

                        copy.Messages.Add(new UniCodexChatHistoryItem
                        {
                            Role = item.Role,
                            Text = item.Text,
                            Time = item.Time,
                            TokenSummary = item.TokenSummary
                        });
                    }
                }

                if (session.RecentTurnTokenCosts != null)
                {
                    for (var costIndex = 0; costIndex < session.RecentTurnTokenCosts.Count; costIndex++)
                    {
                        copy.RecentTurnTokenCosts.Add(Mathf.Max(0, session.RecentTurnTokenCosts[costIndex]));
                    }
                }

                state.Sessions.Add(copy);
            }

            state.Messages = new List<UniCodexChatHistoryItem>();
            var active = GetActiveChatSession();
            if (active?.Messages != null)
            {
                for (var i = 0; i < active.Messages.Count; i++)
                {
                    var item = active.Messages[i];
                    if (item == null)
                    {
                        continue;
                    }

                    state.Messages.Add(new UniCodexChatHistoryItem
                    {
                        Role = item.Role,
                        Text = item.Text,
                        Time = item.Time,
                        TokenSummary = item.TokenSummary
                    });
                }
            }

            try
            {
                var historyPath = GetChatHistoryPath();
                var directory = Path.GetDirectoryName(historyPath);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var json = JsonUtility.ToJson(state);
                File.WriteAllText(historyPath, json);
            }
            catch
            {
                // Ignore write failures; chat still works without persistence.
            }
        }

        private string GetChatHistoryPath()
        {
            return Path.Combine(UniCodexChatHelper.GetProjectRootPath(), "Library", UniCodexCliConstants.ChatHistoryFileName);
        }

        private static ChatRole ParseChatRole(int rawValue)
        {
            switch (rawValue)
            {
                case (int)ChatRole.User:
                    return ChatRole.User;
                case (int)ChatRole.Assistant:
                    return ChatRole.Assistant;
                case (int)ChatRole.System:
                    return ChatRole.System;
                case (int)ChatRole.Error:
                    return ChatRole.Error;
                default:
                    return ChatRole.Assistant;
            }
        }

        private static int GetOptionIndex(string selected, List<string> options, string fallback)
        {
            if (options == null || options.Count == 0)
            {
                return 0;
            }

            var resolved = NormalizeOption(selected, options, fallback);
            for (var i = 0; i < options.Count; i++)
            {
                if (string.Equals(options[i], resolved, StringComparison.Ordinal))
                {
                    return i;
                }
            }

            return 0;
        }

        private static string NormalizeOption(string selected, List<string> options, string fallback)
        {
            if (options == null || options.Count == 0)
            {
                return fallback ?? string.Empty;
            }

            var trimmed = string.IsNullOrWhiteSpace(selected) ? string.Empty : selected.Trim();
            if (!string.IsNullOrEmpty(trimmed))
            {
                for (var i = 0; i < options.Count; i++)
                {
                    if (string.Equals(options[i], trimmed, StringComparison.Ordinal))
                    {
                        return options[i];
                    }
                }
            }

            for (var i = 0; i < options.Count; i++)
            {
                if (string.Equals(options[i], fallback, StringComparison.Ordinal))
                {
                    return options[i];
                }
            }

            return options[0];
        }

        // -------------------------
        // Local utility wrappers
        // -------------------------

        private UniCodex.IClient ConfigureUniCodexClient(bool? fullAutoOverride = null)
        {
            UniCodex.ConfigureClient(new UniCodexEditorCliClientAdapter(() => CreateCliService(fullAutoOverride)));
            return UniCodex.Client;
        }

        private Task<UniCodexRunResult> RunThroughUniCodexClient(string prompt, bool? fullAutoOverride = null)
        {
            var client = ConfigureUniCodexClient(fullAutoOverride);
            var fullAutoForCurrentMode = _chatMode == ChatMode.Build && _fullAuto;
            if (fullAutoOverride.HasValue)
            {
                fullAutoForCurrentMode = fullAutoOverride.Value;
            }

            var request = new UniCodexClientRunRequest
            {
                Prompt = prompt,
                SessionId = _sessionId ?? string.Empty,
                Model = NormalizeOption(_selectedModel, ModelOptions, DefaultModel),
                ReasoningEffort = NormalizeOption(_selectedReasoningEffort, ReasoningEffortOptions, DefaultReasoningEffort),
                FullAuto = fullAutoForCurrentMode,
                ProgressCallback = QueueCliProgressUpdate
            };

            return client.RunAsync(request).ContinueWith(task =>
            {
                if (task.IsFaulted)
                {
                    return UniCodexRunResult.FromError(task.Exception?.GetBaseException().Message ?? "Unknown execution error");
                }

                return ToLegacyRunResult(task.Result);
            });
        }

        private static UniCodexCommandResult ToLegacyCommandResult(UniCodexResult result)
        {
            return new UniCodexCommandResult
            {
                Success = result != null && result.Success,
                Message = result == null ? "Unknown command result." : result.Message
            };
        }

        private static UniCodexRunResult ToLegacyRunResult(UniCodexClientRunResult result)
        {
            if (result == null)
            {
                return UniCodexRunResult.FromError("Run result is null.");
            }

            return new UniCodexRunResult
            {
                Success = result.Success,
                Message = result.Message,
                ThreadId = result.SessionId,
                InputTokens = result.InputTokens,
                OutputTokens = result.OutputTokens,
                TotalTokens = result.TotalTokens
            };
        }

        private UniCodexCliService CreateCliService(bool? fullAutoOverride = null)
        {
            var fullAutoForCurrentMode = _chatMode == ChatMode.Build && _fullAuto;
            if (fullAutoOverride.HasValue)
            {
                fullAutoForCurrentMode = fullAutoOverride.Value;
            }

            return new UniCodexCliService(
                _cliPath,
                UniCodexChatHelper.GetProjectRootPath(),
                _useProjectCodexHome,
                GetProjectCodexHome(),
                fullAutoForCurrentMode,
                NormalizeOption(_selectedModel, ModelOptions, DefaultModel),
                NormalizeOption(_selectedReasoningEffort, ReasoningEffortOptions, DefaultReasoningEffort));
        }

        private string GetProjectCodexHome()
        {
            return UniCodexChatHelper.GetProjectCodexHome(_projectCodexHomeRelative);
        }

        private static Font GetPreferredUiFont()
        {
            if (_preferredUiFont != null)
            {
                return _preferredUiFont;
            }

            try
            {
#pragma warning disable CS0618
                _preferredUiFont = Font.CreateDynamicFontFromOSFont(PreferredUiFontCandidates, 14);
#pragma warning restore CS0618
            }
            catch
            {
                _preferredUiFont = null;
            }

            return _preferredUiFont;
        }

        private static void ApplyPreferredFont(VisualElement element)
        {
            if (element == null)
            {
                return;
            }

            var font = GetPreferredUiFont();
            if (font == null)
            {
                return;
            }

#pragma warning disable CS0618
            element.style.unityFont = font;
#pragma warning restore CS0618
        }

        private static void ApplyPreferredFont(TextElement label)
        {
            if (label == null)
            {
                return;
            }

            ApplyPreferredFont((VisualElement)label);
        }

        private static VisualElement QueryBaseFieldInputElement(VisualElement root)
        {
            if (root == null)
            {
                return null;
            }

            return root.Q<VisualElement>(className: BaseFieldInputClassName);
        }

        private static VisualElement QueryTextInputElement(VisualElement root)
        {
            if (root == null)
            {
                return null;
            }

            return root.Q<VisualElement>(className: TextInputClassName)
                ?? root.Q<VisualElement>(className: BaseTextFieldInputClassName)
                ?? QueryBaseFieldInputElement(root);
        }

        private static void SetTextFieldSelectionToEnd(TextField field, int index)
        {
            if (field == null)
            {
                return;
            }

            if (TrySetTextSelectionUsingReflection(field, index))
            {
                return;
            }

#pragma warning disable CS0618
            field.cursorIndex = index;
            field.selectIndex = index;
#pragma warning restore CS0618
        }

        private static bool TrySetTextSelectionUsingReflection(TextField field, int index)
        {
            try
            {
                var textSelectionProp = field.GetType().GetProperty("textSelection", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var textSelection = textSelectionProp?.GetValue(field, null);
                if (textSelection == null)
                {
                    return false;
                }

                var selectionType = textSelection.GetType();
                var cursorProp = selectionType.GetProperty("cursorIndex", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var selectProp = selectionType.GetProperty("selectIndex", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                var wroteValue = false;
                if (cursorProp != null && cursorProp.CanWrite)
                {
                    cursorProp.SetValue(textSelection, index, null);
                    wroteValue = true;
                }

                if (selectProp != null && selectProp.CanWrite)
                {
                    selectProp.SetValue(textSelection, index, null);
                    wroteValue = true;
                }

                if (wroteValue)
                {
                    return true;
                }

                var selectRangeMethod = selectionType.GetMethod(
                    "SelectRange",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null,
                    new[] { typeof(int), typeof(int) },
                    null);
                if (selectRangeMethod == null)
                {
                    return false;
                }

                selectRangeMethod.Invoke(textSelection, new object[] { index, index });
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void ApplyPanelSurfaceStyle(VisualElement element, Color background, Color border, float radius)
        {
            if (element == null)
            {
                return;
            }

            element.style.backgroundColor = background;
            element.style.borderTopWidth = 1f;
            element.style.borderBottomWidth = 1f;
            element.style.borderLeftWidth = 1f;
            element.style.borderRightWidth = 1f;
            element.style.borderTopColor = border;
            element.style.borderBottomColor = border;
            element.style.borderLeftColor = border;
            element.style.borderRightColor = border;
            element.style.borderTopLeftRadius = radius;
            element.style.borderTopRightRadius = radius;
            element.style.borderBottomLeftRadius = radius;
            element.style.borderBottomRightRadius = radius;
        }

        private static void ApplyButtonStyle(Button button, Color background, Color border, Color textColor, float height, float radius, bool bold = false)
        {
            if (button == null)
            {
                return;
            }

            ApplyPreferredFont(button);
            button.style.height = height;
            button.style.minHeight = height;
            button.style.backgroundColor = background;
            button.style.color = textColor;
            button.style.borderTopWidth = 1f;
            button.style.borderBottomWidth = 1f;
            button.style.borderLeftWidth = 1f;
            button.style.borderRightWidth = 1f;
            button.style.borderTopColor = border;
            button.style.borderBottomColor = border;
            button.style.borderLeftColor = border;
            button.style.borderRightColor = border;
            button.style.borderTopLeftRadius = radius;
            button.style.borderTopRightRadius = radius;
            button.style.borderBottomLeftRadius = radius;
            button.style.borderBottomRightRadius = radius;
            button.style.unityTextAlign = TextAnchor.MiddleCenter;
            button.style.unityFontStyleAndWeight = bold ? FontStyle.Bold : FontStyle.Normal;
        }

        private static void ApplyPopupFieldStyle(PopupField<string> popup, float height)
        {
            if (popup == null)
            {
                return;
            }

            popup.style.height = height;
            popup.style.minHeight = height;
            popup.style.maxHeight = height;
            popup.style.color = UiTextPrimary;

            popup.schedule.Execute(() =>
            {
                var input = QueryBaseFieldInputElement(popup);
                if (input != null)
                {
                    input.style.height = height;
                    input.style.minHeight = height;
                    input.style.maxHeight = height;
                    input.style.paddingLeft = 8f;
                    input.style.paddingRight = 8f;
                    input.style.backgroundColor = UiControlBackground;
                    input.style.borderTopWidth = 1f;
                    input.style.borderBottomWidth = 1f;
                    input.style.borderLeftWidth = 1f;
                    input.style.borderRightWidth = 1f;
                    input.style.borderTopColor = UiControlBorder;
                    input.style.borderBottomColor = UiControlBorder;
                    input.style.borderLeftColor = UiControlBorder;
                    input.style.borderRightColor = UiControlBorder;
                    input.style.borderTopLeftRadius = 7f;
                    input.style.borderTopRightRadius = 7f;
                    input.style.borderBottomLeftRadius = 7f;
                    input.style.borderBottomRightRadius = 7f;
                }

                var text = popup.Q<Label>();
                if (text != null)
                {
                    ApplyPreferredFont(text);
                    text.style.color = UiTextPrimary;
                    text.style.unityTextAlign = TextAnchor.MiddleLeft;
                }

                var arrow = popup.Q<VisualElement>(className: "unity-base-popup-field__arrow")
                    ?? popup.Q<VisualElement>(className: "unity-popup-field__arrow");
                if (arrow != null)
                {
                    arrow.style.unityBackgroundImageTintColor = UiTextSecondary;
                }
            }).ExecuteLater(0);
        }

        private static void StyleTextFieldInput(TextField field, float radius)
        {
            if (field == null)
            {
                return;
            }

            field.style.borderTopWidth = 1f;
            field.style.borderBottomWidth = 1f;
            field.style.borderLeftWidth = 1f;
            field.style.borderRightWidth = 1f;
            field.style.borderTopColor = UiControlBorder;
            field.style.borderBottomColor = UiControlBorder;
            field.style.borderLeftColor = UiControlBorder;
            field.style.borderRightColor = UiControlBorder;
            field.style.borderTopLeftRadius = radius;
            field.style.borderTopRightRadius = radius;
            field.style.borderBottomLeftRadius = radius;
            field.style.borderBottomRightRadius = radius;

            field.schedule.Execute(() =>
            {
                var input = QueryTextInputElement(field);
                if (input != null)
                {
                    input.style.backgroundColor = UiControlBackground;
                    input.style.color = UiTextPrimary;
                    input.style.paddingLeft = 8f;
                    input.style.paddingRight = 8f;
                    input.style.borderTopLeftRadius = radius;
                    input.style.borderTopRightRadius = radius;
                    input.style.borderBottomLeftRadius = radius;
                    input.style.borderBottomRightRadius = radius;
                }

                var text = field.Q<TextElement>();
                if (text != null)
                {
                    ApplyPreferredFont(text);
                    text.style.color = UiTextPrimary;
                }
            }).ExecuteLater(0);
        }

        /// <summary>
        /// 세션 토큰 사용량을 표시하는 경량 원형 게이지입니다.
        /// </summary>
        private sealed class UniCodexTokenGaugeElement : VisualElement
        {
            private float _progress;
            private Color _fillColor = new Color(0.17f, 0.56f, 0.94f, 1f);
            private readonly Color _trackColor = new Color(0.26f, 0.30f, 0.36f, 1f);
            private readonly Color _centerColor = new Color(0.10f, 0.12f, 0.15f, 1f);

            /// <summary>[0, 1] 범위의 진행률 값입니다.</summary>
            public float Progress
            {
                get => _progress;
                set
                {
                    var clamped = Mathf.Clamp01(value);
                    if (Mathf.Approximately(_progress, clamped))
                    {
                        return;
                    }

                    _progress = clamped;
                    MarkDirtyRepaint();
                }
            }

            /// <summary>채워진 호(arc)에 사용할 색상입니다.</summary>
            public Color FillColor
            {
                get => _fillColor;
                set
                {
                    if (_fillColor.Equals(value))
                    {
                        return;
                    }

                    _fillColor = value;
                    MarkDirtyRepaint();
                }
            }

            /// <summary>토큰 게이지 UI 요소를 생성합니다.</summary>
            public UniCodexTokenGaugeElement()
            {
                pickingMode = PickingMode.Ignore;
                generateVisualContent += OnGenerateVisualContent;
            }

            private void OnGenerateVisualContent(MeshGenerationContext context)
            {
                var rect = contentRect;
                if (rect.width <= 1f || rect.height <= 1f)
                {
                    return;
                }

                var center = rect.center;
                var radius = Mathf.Max(1f, Mathf.Min(rect.width, rect.height) * 0.5f - 1f);
                const float lineWidth = 4f;
                var innerRadius = Mathf.Max(1f, radius - lineWidth - 1f);

                var painter = context.painter2D;
                painter.lineWidth = lineWidth;

                painter.fillColor = _centerColor;
                painter.BeginPath();
                painter.Arc(center, innerRadius, 0f, Mathf.PI * 2f);
                painter.Fill();

                painter.strokeColor = _trackColor;
                painter.BeginPath();
                painter.Arc(center, radius, 0f, Mathf.PI * 2f);
                painter.Stroke();

                if (_progress <= 0.0001f)
                {
                    return;
                }

                painter.strokeColor = _fillColor;
                painter.BeginPath();
                var start = -Mathf.PI * 0.5f;
                var end = start + Mathf.PI * 2f * _progress;
                painter.Arc(center, radius, start, end);
                painter.Stroke();
            }
        }

        private enum ChatMode
        {
            Plan,
            Build
        }

        private enum ChatRole
        {
            User,
            Assistant,
            System,
            Error
        }

        [Serializable]
        private sealed class UniCodexChatHistoryState
        {
            /// <summary>현재 활성 채팅 세션 ID입니다.</summary>
            public string ActiveSessionId;
            /// <summary>저장된 채팅 세션 목록입니다.</summary>
            public List<UniCodexChatSessionInfo> Sessions = new List<UniCodexChatSessionInfo>();
            /// <summary>레거시 호환용 최상위 메시지 목록입니다.</summary>
            public List<UniCodexChatHistoryItem> Messages = new List<UniCodexChatHistoryItem>();
        }

        [Serializable]
        private sealed class UniCodexChatSessionInfo
        {
            /// <summary>내부 고유 세션 ID입니다.</summary>
            public string Id;
            /// <summary>사용자 표시용 세션 이름입니다.</summary>
            public string Name;
            /// <summary>연결된 Codex CLI 세션 ID입니다.</summary>
            public string CodexSessionId;
            /// <summary>세션 누적 추정 토큰 사용량입니다.</summary>
            public int TokenUsed;
            /// <summary>추정 계산용 최근 턴별 토큰 비용입니다.</summary>
            public List<int> RecentTurnTokenCosts = new List<int>();
            /// <summary>이 세션에 저장된 메시지 목록입니다.</summary>
            public List<UniCodexChatHistoryItem> Messages = new List<UniCodexChatHistoryItem>();
        }

        [Serializable]
        private sealed class UniCodexChatHistoryItem
        {
            /// <summary>직렬화된 <see cref="ChatRole"/> 값입니다.</summary>
            public int Role;
            /// <summary>메시지 본문 텍스트입니다.</summary>
            public string Text;
            /// <summary>표시용 시각 텍스트입니다.</summary>
            public string Time;
            /// <summary>선택적 토큰 사용량 요약 텍스트입니다.</summary>
            public string TokenSummary;
        }

        private sealed class UniCodexDeferredRunResult
        {
            /// <summary>Codex 실행 결과 페이로드입니다.</summary>
            public UniCodexRunResult Result;
            /// <summary>이 실행이 Diff Preview 모드 요청인지 여부입니다.</summary>
            public bool DiffPreviewTurn;
        }

        private sealed class UniCodexChatMessage
        {
            /// <summary>채팅 타임라인에서의 메시지 역할입니다.</summary>
            public ChatRole Role;
            /// <summary>메시지 본문입니다.</summary>
            public string Text;
            /// <summary>UI 렌더링용 시각 텍스트입니다.</summary>
            public string Time;
            /// <summary>메시지 하단에 표시할 선택적 토큰 요약입니다.</summary>
            public string TokenSummary;
            /// <summary>대기/로딩용 플레이스홀더 메시지인지 여부입니다.</summary>
            public bool IsLoading;
        }
    }
}
