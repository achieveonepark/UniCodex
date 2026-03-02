using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Achieve.UniCodex.Editor
{
    /// <summary>
    /// Injects a small shortcut button into the Unity main toolbar (left of play controls).
    /// </summary>
    [InitializeOnLoad]
    internal static class UniCodexToolbarShortcut
    {
        private const string ToolbarTypeName = "UnityEditor.Toolbar, UnityEditor";
        private const string GUIViewTypeName = "UnityEditor.GUIView, UnityEditor";
        private const string ShortcutButtonName = "UniCodexToolbarShortcut";
        private const string StatusDotName = "UniCodexToolbarStatusDot";
        private const double PollIntervalSeconds = 0.8d;
        private enum ShortcutStatus
        {
            Ready,
            Busy,
            Complete
        }
        private static readonly string[] ZoneNameCandidates =
        {
            "ToolbarZonePlayModes",
            "ToolbarZonePlayMode",
            "ToolbarZoneMainPlayMode",
            "ToolbarZoneCenter",
            "ToolbarZoneMiddle",
            "ToolbarZoneLeftAlign"
        };

        private static readonly Type ToolbarType = Type.GetType(ToolbarTypeName);
        private static readonly Type GUIViewType = Type.GetType(GUIViewTypeName);
        private static double _nextPollAt;
        private static UnityEngine.Object _cachedToolbar;
        private static int _cachedToolbarId;
        private static ShortcutStatus _shortcutStatus = ShortcutStatus.Ready;

        static UniCodexToolbarShortcut()
        {
            EditorApplication.update -= EnsureShortcutInstalled;
            EditorApplication.update += EnsureShortcutInstalled;
        }

        [MenuItem("Tools/Codex/Reinstall Toolbar Shortcut")]
        private static void ReinstallShortcut()
        {
            _cachedToolbar = null;
            _cachedToolbarId = 0;
            _nextPollAt = 0d;
            EnsureShortcutInstalled();
        }

        internal static void SetReadyState()
        {
            SetShortcutStatus(ShortcutStatus.Ready);
        }

        internal static void SetBusyState()
        {
            SetShortcutStatus(ShortcutStatus.Busy);
        }

        internal static void SetCompleteState()
        {
            SetShortcutStatus(ShortcutStatus.Complete);
        }

        private static void SetShortcutStatus(ShortcutStatus status)
        {
            if (_shortcutStatus == status)
            {
                return;
            }

            _shortcutStatus = status;
            _nextPollAt = 0d;
            EnsureShortcutInstalled();
        }

        private static void EnsureShortcutInstalled()
        {
            if (EditorApplication.timeSinceStartup < _nextPollAt)
            {
                return;
            }

            _nextPollAt = EditorApplication.timeSinceStartup + PollIntervalSeconds;

            var playZone = TryGetPlayModeZone();
            if (playZone == null)
            {
                return;
            }

            var existing = playZone.Q<Button>(ShortcutButtonName);
            if (existing != null)
            {
                SyncButtonSizeWithPlayControl(playZone, existing);
                ApplyShortcutVisual(existing);
                return;
            }

            var created = CreateShortcutButton();
            playZone.Insert(0, created);
            SyncButtonSizeWithPlayControl(playZone, created);
            ApplyShortcutVisual(created);
        }

        private static VisualElement TryGetPlayModeZone()
        {
            var toolbar = FindToolbarInstance();
            if (toolbar == null)
            {
                return null;
            }

            var root = GetToolbarRoot(toolbar);
            if (root == null)
            {
                return null;
            }

            for (var i = 0; i < ZoneNameCandidates.Length; i++)
            {
                var byName = FindByNameDeep(root, ZoneNameCandidates[i]);
                if (byName != null)
                {
                    return byName;
                }
            }

            return FindByNameContainsDeep(root, "playmode");
        }

        private static UnityEngine.Object FindToolbarInstance()
        {
            if (_cachedToolbar != null && _cachedToolbar.GetInstanceID() == _cachedToolbarId)
            {
                return _cachedToolbar;
            }

            if (ToolbarType == null)
            {
                return null;
            }

            var found = Resources.FindObjectsOfTypeAll(ToolbarType);
            if (found == null || found.Length == 0)
            {
                _cachedToolbar = null;
                _cachedToolbarId = 0;
                return null;
            }

            _cachedToolbar = found[0];
            _cachedToolbarId = _cachedToolbar.GetInstanceID();
            return _cachedToolbar;
        }

        private static VisualElement GetToolbarRoot(object toolbar)
        {
            if (GUIViewType != null)
            {
                var visualTreeProp = GUIViewType.GetProperty("visualTree", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (visualTreeProp?.GetValue(toolbar) is VisualElement visualTree)
                {
                    return visualTree;
                }
            }

            if (toolbar is EditorWindow editorWindow && editorWindow.rootVisualElement != null)
            {
                return editorWindow.rootVisualElement;
            }

            if (ToolbarType == null)
            {
                return null;
            }

            var rootField = ToolbarType.GetField("m_Root", BindingFlags.Instance | BindingFlags.NonPublic);
            if (rootField?.GetValue(toolbar) is VisualElement rootByField)
            {
                return rootByField;
            }

            var rootProperty = ToolbarType.GetProperty("rootVisualElement", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return rootProperty?.GetValue(toolbar) as VisualElement;
        }

        private static VisualElement FindByNameDeep(VisualElement root, string targetName)
        {
            if (root == null || string.IsNullOrWhiteSpace(targetName))
            {
                return null;
            }

            var direct = root.Q<VisualElement>(targetName);
            if (direct != null)
            {
                return direct;
            }

            var stack = new Stack<VisualElement>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var current = stack.Pop();
                if (string.Equals(current.name, targetName, StringComparison.Ordinal))
                {
                    return current;
                }

                for (var i = current.childCount - 1; i >= 0; i--)
                {
                    if (current[i] is VisualElement child)
                    {
                        stack.Push(child);
                    }
                }
            }

            return null;
        }

        private static VisualElement FindByNameContainsDeep(VisualElement root, string containsText)
        {
            if (root == null || string.IsNullOrWhiteSpace(containsText))
            {
                return null;
            }

            var needle = containsText.ToLowerInvariant();
            var stack = new Stack<VisualElement>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var current = stack.Pop();
                var name = current.name ?? string.Empty;
                if (name.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return current;
                }

                for (var i = current.childCount - 1; i >= 0; i--)
                {
                    if (current[i] is VisualElement child)
                    {
                        stack.Push(child);
                    }
                }
            }

            return null;
        }

        private static Button CreateShortcutButton()
        {
            var button = new Button(OnShortcutButtonClicked)
            {
                name = ShortcutButtonName,
                tooltip = "Open Codex Chat"
            };

            button.style.width = 24f;
            button.style.height = 24f;
            button.style.minWidth = 24f;
            button.style.minHeight = 24f;
            button.style.marginRight = 4f;
            button.style.marginLeft = 2f;
            button.style.marginTop = 0f;
            button.style.marginBottom = 0f;
            button.style.paddingLeft = 0f;
            button.style.paddingRight = 0f;
            button.style.justifyContent = Justify.Center;
            button.style.alignItems = Align.Center;
            button.style.alignSelf = Align.Center;
            button.style.flexGrow = 0f;
            button.style.flexShrink = 0f;
            button.style.backgroundColor = new Color(0.18f, 0.19f, 0.22f, 1f);

            var dot = new VisualElement
            {
                name = StatusDotName
            };
            dot.style.width = 9f;
            dot.style.height = 9f;
            dot.style.borderTopLeftRadius = 4.5f;
            dot.style.borderTopRightRadius = 4.5f;
            dot.style.borderBottomLeftRadius = 4.5f;
            dot.style.borderBottomRightRadius = 4.5f;
            dot.style.borderTopWidth = 1f;
            dot.style.borderBottomWidth = 1f;
            dot.style.borderLeftWidth = 1f;
            dot.style.borderRightWidth = 1f;
            dot.style.borderTopColor = new Color(0.12f, 0.12f, 0.12f, 1f);
            dot.style.borderBottomColor = new Color(0.12f, 0.12f, 0.12f, 1f);
            dot.style.borderLeftColor = new Color(0.12f, 0.12f, 0.12f, 1f);
            dot.style.borderRightColor = new Color(0.12f, 0.12f, 0.12f, 1f);
            dot.pickingMode = PickingMode.Ignore;
            button.Add(dot);

            return button;
        }

        private static void SyncButtonSizeWithPlayControl(VisualElement playZone, Button shortcutButton)
        {
            if (playZone == null || shortcutButton == null)
            {
                return;
            }

            var reference = FindPlayControlReference(playZone, shortcutButton);
            if (reference == null)
            {
                return;
            }

            var refWidth = reference.resolvedStyle.width;
            var refHeight = reference.resolvedStyle.height;
            if (refWidth <= 0f || refHeight <= 0f)
            {
                return;
            }

            shortcutButton.style.width = refWidth;
            shortcutButton.style.height = refHeight;
            shortcutButton.style.minWidth = refWidth;
            shortcutButton.style.minHeight = refHeight;
            shortcutButton.style.maxWidth = refWidth;
            shortcutButton.style.maxHeight = refHeight;
            shortcutButton.style.marginTop = reference.resolvedStyle.marginTop;
            shortcutButton.style.marginBottom = reference.resolvedStyle.marginBottom;
            shortcutButton.style.alignSelf = Align.Center;
        }

        private static VisualElement FindPlayControlReference(VisualElement playZone, VisualElement shortcutButton)
        {
            Button fallbackButton = null;
            for (var i = 0; i < playZone.childCount; i++)
            {
                if (!(playZone[i] is VisualElement child) || child == shortcutButton)
                {
                    continue;
                }

                if (child is Button childButton)
                {
                    fallbackButton ??= childButton;
                    var buttonName = childButton.name ?? string.Empty;
                    if (buttonName.IndexOf("play", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return childButton;
                    }
                }

                var name = child.name ?? string.Empty;
                if (name.IndexOf("play", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    var nestedButton = child.Q<Button>();
                    if (nestedButton != null && nestedButton != shortcutButton)
                    {
                        return nestedButton;
                    }
                }
            }

            if (fallbackButton != null)
            {
                return fallbackButton;
            }

            for (var i = 0; i < playZone.childCount; i++)
            {
                if (!(playZone[i] is VisualElement child) || child == shortcutButton)
                {
                    continue;
                }

                if (child is Button)
                {
                    return child;
                }
            }

            return null;
        }

        private static void OnShortcutButtonClicked()
        {
            if (_shortcutStatus == ShortcutStatus.Complete)
            {
                SetReadyState();
            }

            UniCodexChatWindow.OpenWindow();
        }

        private static void ApplyShortcutVisual(Button button)
        {
            if (button == null)
            {
                return;
            }

            var dot = button.Q<VisualElement>(StatusDotName);
            if (dot != null)
            {
                dot.style.backgroundColor = GetStatusColor(_shortcutStatus);
            }

            button.tooltip = $"Open Codex Chat ({GetStatusLabel(_shortcutStatus)})";
        }

        private static Color GetStatusColor(ShortcutStatus status)
        {
            switch (status)
            {
                case ShortcutStatus.Busy:
                    return new Color(0.86f, 0.26f, 0.26f, 1f);
                case ShortcutStatus.Complete:
                    return new Color(0.24f, 0.73f, 0.33f, 1f);
                default:
                    return new Color(0.17f, 0.56f, 0.94f, 1f);
            }
        }

        private static string GetStatusLabel(ShortcutStatus status)
        {
            switch (status)
            {
                case ShortcutStatus.Busy:
                    return "Busy";
                case ShortcutStatus.Complete:
                    return "Complete";
                default:
                    return "Ready";
            }
        }
    }
}
