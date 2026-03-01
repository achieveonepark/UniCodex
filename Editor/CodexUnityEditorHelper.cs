using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Achieve.UniCodex
{
    /// <summary>
    /// File-based Unity bridge for Codex CLI.
    /// Codex writes action JSON to Library/CodexUnityActions.json and this helper applies it in-editor.
    /// </summary>
    internal static class CodexUnityEditorHelper
    {
        private const string MenuRoot = "Tools/Codex/Unity Helper/";
        private static readonly Dictionary<string, Type> ComponentTypeCache = new Dictionary<string, Type>(StringComparer.Ordinal);

        [MenuItem(MenuRoot + "Apply Pending Actions")]
        private static void ApplyPendingActionsMenu()
        {
            if (!TryApplyPendingActions(out var summary))
            {
                Debug.Log("Unity action bridge: no pending action file.");
                return;
            }

            if (!string.IsNullOrWhiteSpace(summary))
            {
                Debug.Log(summary);
            }
        }

        [MenuItem(MenuRoot + "Write Action Template")]
        private static void WriteActionTemplateMenu()
        {
            var actionPath = CodexChatHelper.GetUnityActionFilePath();
            Directory.CreateDirectory(Path.GetDirectoryName(actionPath) ?? "Library");

            var template = new CodexUnityActionRequest
            {
                scene = "Title",
                saveScene = true,
                actions = new[]
                {
                    new CodexUnityAction
                    {
                        type = "CreateSpriteObject",
                        objectName = "GuideCat",
                        spritePath = "Assets/Res/Sprites/Cat/Cat_01.png",
                        setPosition = true,
                        posX = 0f,
                        posY = 0f,
                        posZ = 0f,
                        setScale = true,
                        scaleX = 1f,
                        scaleY = 1f,
                        scaleZ = 1f
                    }
                }
            };

            var json = JsonUtility.ToJson(template, true);
            File.WriteAllText(actionPath, json);
            AssetDatabase.Refresh();
            Debug.Log($"Unity action template written: {actionPath}");
        }

        /// <summary>
        /// Consume and apply actions from the pending action file.
        /// Returns false when there is no pending file.
        /// </summary>
        public static bool TryApplyPendingActions(out string summary)
        {
            summary = string.Empty;
            var actionPath = CodexChatHelper.GetUnityActionFilePath();
            if (!File.Exists(actionPath))
            {
                return false;
            }

            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                summary = "Unity action apply skipped: editor is in play mode.";
                return true;
            }

            string json;
            try
            {
                json = File.ReadAllText(actionPath);
            }
            catch (Exception ex)
            {
                summary = $"Unity action apply failed while reading request file: {ex.Message}";
                return true;
            }

            ArchiveAndRemovePendingFile(actionPath, json);

            if (string.IsNullOrWhiteSpace(json))
            {
                summary = "Unity action request file was empty.";
                return true;
            }

            CodexUnityActionRequest request;
            try
            {
                request = JsonUtility.FromJson<CodexUnityActionRequest>(json);
            }
            catch (Exception ex)
            {
                summary = $"Unity action request JSON is invalid: {ex.Message}";
                return true;
            }

            if (request == null || request.actions == null || request.actions.Length == 0)
            {
                summary = "Unity action request has no actions.";
                return true;
            }

            if (!TryResolveScenePath(request.scene, out var scenePath, out var sceneError))
            {
                summary = $"Unity action apply failed: scene resolve failed ({sceneError}).";
                return true;
            }

            var previousSetup = EditorSceneManager.GetSceneManagerSetup();
            var log = new StringBuilder();
            var appliedCount = 0;
            var skippedCount = 0;
            var failedCount = 0;

            try
            {
                var targetScene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
                for (var i = 0; i < request.actions.Length; i++)
                {
                    var action = request.actions[i];
                    switch (ApplyAction(targetScene, action, log))
                    {
                        case ActionOutcome.Applied:
                            appliedCount++;
                            break;
                        case ActionOutcome.Skipped:
                            skippedCount++;
                            break;
                        default:
                            failedCount++;
                            break;
                    }
                }

                if (request.saveScene && appliedCount > 0)
                {
                    EditorSceneManager.SaveScene(targetScene);
                }

                summary =
                    "Unity action result\n"
                    + $"- Scene: {Path.GetFileNameWithoutExtension(scenePath)}\n"
                    + $"- Applied: {appliedCount}\n"
                    + $"- Skipped: {skippedCount}\n"
                    + $"- Failed: {failedCount}\n"
                    + log.ToString().TrimEnd();
            }
            catch (Exception ex)
            {
                summary = $"Unity action apply failed: {ex.Message}";
            }
            finally
            {
                try
                {
                    EditorSceneManager.RestoreSceneManagerSetup(previousSetup);
                }
                catch
                {
                    // Best-effort restore only.
                }
            }

            return true;
        }

        private static void ArchiveAndRemovePendingFile(string actionPath, string json)
        {
            try
            {
                var archivePath = actionPath + ".last.json";
                File.WriteAllText(archivePath, json ?? string.Empty);
            }
            catch
            {
                // Archive is optional.
            }

            try
            {
                File.Delete(actionPath);
            }
            catch
            {
                // Best-effort consume.
            }
        }

        private static ActionOutcome ApplyAction(Scene scene, CodexUnityAction action, StringBuilder log)
        {
            if (action == null || string.IsNullOrWhiteSpace(action.type))
            {
                log.AppendLine("- Failed: missing action type.");
                return ActionOutcome.Failed;
            }

            var actionType = action.type.Trim();
            if (actionType.Equals("AddComponent", StringComparison.OrdinalIgnoreCase))
            {
                return TryAddComponent(scene, action, log);
            }

            if (actionType.Equals("RemoveComponent", StringComparison.OrdinalIgnoreCase))
            {
                return TryRemoveComponent(scene, action, log);
            }

            if (actionType.Equals("CreateSpriteObject", StringComparison.OrdinalIgnoreCase))
            {
                return TryCreateSpriteObject(scene, action, log);
            }

            log.AppendLine($"- Failed: unsupported action type `{actionType}`.");
            return ActionOutcome.Failed;
        }

        private static ActionOutcome TryAddComponent(Scene scene, CodexUnityAction action, StringBuilder log)
        {
            if (!TryFindTargetObject(scene, action.target, action.includeInactive, out var gameObject, out var findError))
            {
                log.AppendLine($"- Failed to add component: {findError}");
                return ActionOutcome.Failed;
            }

            var componentType = ResolveComponentType(action.component);
            if (componentType == null)
            {
                log.AppendLine($"- Failed to add component: type not found `{action.component}`.");
                return ActionOutcome.Failed;
            }

            if (gameObject.GetComponent(componentType) != null)
            {
                log.AppendLine($"- Skipped: `{componentType.Name}` already exists on `{GetHierarchyPath(gameObject.transform)}`.");
                return ActionOutcome.Skipped;
            }

            var added = Undo.AddComponent(gameObject, componentType);
            if (added == null)
            {
                log.AppendLine($"- Failed to add component: Unity could not add `{componentType.Name}` to `{GetHierarchyPath(gameObject.transform)}`.");
                return ActionOutcome.Failed;
            }

            EditorSceneManager.MarkSceneDirty(scene);
            log.AppendLine($"- Added `{componentType.Name}` to `{GetHierarchyPath(gameObject.transform)}`.");
            return ActionOutcome.Applied;
        }

        private static ActionOutcome TryRemoveComponent(Scene scene, CodexUnityAction action, StringBuilder log)
        {
            if (!TryFindTargetObject(scene, action.target, action.includeInactive, out var gameObject, out var findError))
            {
                log.AppendLine($"- Failed to remove component: {findError}");
                return ActionOutcome.Failed;
            }

            var componentType = ResolveComponentType(action.component);
            if (componentType == null)
            {
                log.AppendLine($"- Failed to remove component: type not found `{action.component}`.");
                return ActionOutcome.Failed;
            }

            var component = gameObject.GetComponent(componentType);
            if (component == null)
            {
                log.AppendLine($"- Skipped: `{componentType.Name}` not found on `{GetHierarchyPath(gameObject.transform)}`.");
                return ActionOutcome.Skipped;
            }

            Undo.DestroyObjectImmediate(component);
            EditorSceneManager.MarkSceneDirty(scene);
            log.AppendLine($"- Removed `{componentType.Name}` from `{GetHierarchyPath(gameObject.transform)}`.");
            return ActionOutcome.Applied;
        }

        private static ActionOutcome TryCreateSpriteObject(Scene scene, CodexUnityAction action, StringBuilder log)
        {
            if (!TryResolveSpriteAssetPath(action.spritePath, out var spriteAssetPath, out var spriteError))
            {
                log.AppendLine($"- Failed to create sprite object: {spriteError}");
                return ActionOutcome.Failed;
            }

            if (!TryLoadSprite(spriteAssetPath, out var sprite, out var loadError))
            {
                log.AppendLine($"- Failed to create sprite object: {loadError}");
                return ActionOutcome.Failed;
            }

            var objectName = string.IsNullOrWhiteSpace(action.objectName)
                ? (!string.IsNullOrWhiteSpace(sprite.name) ? sprite.name : "CodexSpriteObject")
                : action.objectName.Trim();

            var gameObject = new GameObject(objectName);
            Undo.RegisterCreatedObjectUndo(gameObject, "Create Codex sprite object");
            SceneManager.MoveGameObjectToScene(gameObject, scene);

            if (action.setPosition)
            {
                gameObject.transform.position = new Vector3(action.posX, action.posY, action.posZ);
            }

            if (action.setScale)
            {
                gameObject.transform.localScale = new Vector3(
                    Mathf.Approximately(action.scaleX, 0f) ? 1f : action.scaleX,
                    Mathf.Approximately(action.scaleY, 0f) ? 1f : action.scaleY,
                    Mathf.Approximately(action.scaleZ, 0f) ? 1f : action.scaleZ);
            }

            var renderer = Undo.AddComponent<SpriteRenderer>(gameObject);
            renderer.sprite = sprite;

            if (!string.IsNullOrWhiteSpace(action.sortingLayer))
            {
                renderer.sortingLayerName = action.sortingLayer.Trim();
            }

            renderer.sortingOrder = action.orderInLayer;

            EditorSceneManager.MarkSceneDirty(scene);
            log.AppendLine(
                $"- Created sprite object `{GetHierarchyPath(gameObject.transform)}` from `{spriteAssetPath}`.");
            return ActionOutcome.Applied;
        }

        private static bool TryResolveSpriteAssetPath(string spritePathOrFile, out string spriteAssetPath, out string error)
        {
            spriteAssetPath = string.Empty;
            error = string.Empty;

            if (string.IsNullOrWhiteSpace(spritePathOrFile))
            {
                error = "spritePath is missing.";
                return false;
            }

            var raw = spritePathOrFile.Trim();
            var pathUnderAssets = TryToAssetRelativePath(raw);
            if (!string.IsNullOrWhiteSpace(pathUnderAssets))
            {
                spriteAssetPath = pathUnderAssets;
                return true;
            }

            if (Path.IsPathRooted(raw) && File.Exists(raw))
            {
                return TryImportExternalSprite(raw, out spriteAssetPath, out error);
            }

            var fileName = Path.GetFileNameWithoutExtension(raw);
            var guidMatches = AssetDatabase.FindAssets($"{fileName} t:Sprite");
            if (guidMatches != null && guidMatches.Length > 0)
            {
                spriteAssetPath = AssetDatabase.GUIDToAssetPath(guidMatches[0]);
                return !string.IsNullOrWhiteSpace(spriteAssetPath);
            }

            error = $"sprite path not found: `{spritePathOrFile}`.";
            return false;
        }

        private static string TryToAssetRelativePath(string maybePath)
        {
            if (string.IsNullOrWhiteSpace(maybePath))
            {
                return string.Empty;
            }

            var normalized = maybePath.Replace('\\', '/');
            if (normalized.StartsWith("Assets/", StringComparison.Ordinal))
            {
                var absolute = Path.GetFullPath(Path.Combine(CodexChatHelper.GetProjectRootPath(), normalized));
                return File.Exists(absolute) ? normalized : string.Empty;
            }

            var absoluteInput = Path.GetFullPath(Path.Combine(CodexChatHelper.GetProjectRootPath(), normalized));
            if (!File.Exists(absoluteInput))
            {
                return string.Empty;
            }

            var projectRoot = Path.GetFullPath(CodexChatHelper.GetProjectRootPath()).Replace('\\', '/');
            var absoluteNormalized = absoluteInput.Replace('\\', '/');
            if (!absoluteNormalized.StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            var relative = absoluteNormalized.Substring(projectRoot.Length).TrimStart('/');
            return relative.StartsWith("Assets/", StringComparison.Ordinal) ? relative : string.Empty;
        }

        private static bool TryImportExternalSprite(string externalAbsolutePath, out string spriteAssetPath, out string error)
        {
            spriteAssetPath = string.Empty;
            error = string.Empty;

            try
            {
                var fileName = Path.GetFileName(externalAbsolutePath);
                var importFolder = "Assets/Res/Sprites/CodexImported";
                Directory.CreateDirectory(Path.Combine(CodexChatHelper.GetProjectRootPath(), "Assets/Res/Sprites/CodexImported"));

                var destinationPath = $"{importFolder}/{fileName}".Replace('\\', '/');
                var fullDestinationPath = Path.GetFullPath(Path.Combine(CodexChatHelper.GetProjectRootPath(), destinationPath));

                if (File.Exists(fullDestinationPath))
                {
                    var name = Path.GetFileNameWithoutExtension(fileName);
                    var ext = Path.GetExtension(fileName);
                    destinationPath = $"{importFolder}/{name}_{Guid.NewGuid():N}{ext}".Replace('\\', '/');
                    fullDestinationPath = Path.GetFullPath(Path.Combine(CodexChatHelper.GetProjectRootPath(), destinationPath));
                }

                File.Copy(externalAbsolutePath, fullDestinationPath);
                AssetDatabase.ImportAsset(destinationPath, ImportAssetOptions.ForceUpdate);
                spriteAssetPath = destinationPath;
                return true;
            }
            catch (Exception ex)
            {
                error = $"failed to import external image: {ex.Message}";
                return false;
            }
        }

        private static bool TryLoadSprite(string spriteAssetPath, out Sprite sprite, out string error)
        {
            sprite = null;
            error = string.Empty;

            if (string.IsNullOrWhiteSpace(spriteAssetPath))
            {
                error = "sprite asset path is empty.";
                return false;
            }

            var importer = AssetImporter.GetAtPath(spriteAssetPath) as TextureImporter;
            if (importer != null && importer.textureType != TextureImporterType.Sprite)
            {
                importer.textureType = TextureImporterType.Sprite;
                importer.spriteImportMode = SpriteImportMode.Single;
                importer.SaveAndReimport();
            }

            sprite = AssetDatabase.LoadAssetAtPath<Sprite>(spriteAssetPath);
            if (sprite != null)
            {
                return true;
            }

            var subs = AssetDatabase.LoadAllAssetRepresentationsAtPath(spriteAssetPath);
            for (var i = 0; i < subs.Length; i++)
            {
                if (subs[i] is Sprite s)
                {
                    sprite = s;
                    return true;
                }
            }

            error = $"could not load sprite at `{spriteAssetPath}`.";
            return false;
        }

        private static bool TryFindTargetObject(Scene scene, string target, bool includeInactive, out GameObject gameObject, out string error)
        {
            gameObject = null;
            error = string.Empty;

            if (string.IsNullOrWhiteSpace(target))
            {
                error = "target is missing.";
                return false;
            }

            var trimmed = target.Trim();
            gameObject = trimmed.IndexOf('/', StringComparison.Ordinal) >= 0
                ? FindByHierarchyPath(scene, trimmed, includeInactive)
                : FindByName(scene, trimmed, includeInactive);

            if (gameObject != null)
            {
                return true;
            }

            error = $"target `{trimmed}` was not found.";
            return false;
        }

        private static GameObject FindByHierarchyPath(Scene scene, string hierarchyPath, bool includeInactive)
        {
            var parts = hierarchyPath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
            {
                return null;
            }

            var roots = scene.GetRootGameObjects();
            for (var i = 0; i < roots.Length; i++)
            {
                var root = roots[i];
                if (!string.Equals(root.name, parts[0], StringComparison.Ordinal))
                {
                    continue;
                }

                if (!includeInactive && !root.activeInHierarchy)
                {
                    continue;
                }

                var current = root.transform;
                var matched = true;
                for (var partIndex = 1; partIndex < parts.Length; partIndex++)
                {
                    current = FindDirectChild(current, parts[partIndex], includeInactive);
                    if (current != null)
                    {
                        continue;
                    }

                    matched = false;
                    break;
                }

                if (matched && current != null)
                {
                    return current.gameObject;
                }
            }

            return null;
        }

        private static Transform FindDirectChild(Transform parent, string name, bool includeInactive)
        {
            for (var i = 0; i < parent.childCount; i++)
            {
                var child = parent.GetChild(i);
                if (!includeInactive && !child.gameObject.activeInHierarchy)
                {
                    continue;
                }

                if (string.Equals(child.name, name, StringComparison.Ordinal))
                {
                    return child;
                }
            }

            return null;
        }

        private static GameObject FindByName(Scene scene, string objectName, bool includeInactive)
        {
            var roots = scene.GetRootGameObjects();
            for (var i = 0; i < roots.Length; i++)
            {
                var found = FindByNameRecursive(roots[i].transform, objectName, includeInactive);
                if (found != null)
                {
                    return found;
                }
            }

            return null;
        }

        private static GameObject FindByNameRecursive(Transform current, string objectName, bool includeInactive)
        {
            if (current == null)
            {
                return null;
            }

            if (!includeInactive && !current.gameObject.activeInHierarchy)
            {
                return null;
            }

            if (string.Equals(current.name, objectName, StringComparison.Ordinal))
            {
                return current.gameObject;
            }

            for (var i = 0; i < current.childCount; i++)
            {
                var found = FindByNameRecursive(current.GetChild(i), objectName, includeInactive);
                if (found != null)
                {
                    return found;
                }
            }

            return null;
        }

        private static bool TryResolveScenePath(string sceneRef, out string scenePath, out string error)
        {
            scenePath = string.Empty;
            error = string.Empty;

            if (string.IsNullOrWhiteSpace(sceneRef))
            {
                var active = SceneManager.GetActiveScene();
                if (active.IsValid() && !string.IsNullOrWhiteSpace(active.path))
                {
                    scenePath = active.path;
                    return true;
                }

                error = "scene is missing and there is no active scene path fallback.";
                return false;
            }

            var trimmed = sceneRef.Trim();
            if (TryResolveScenePathAsFile(trimmed, out scenePath))
            {
                return true;
            }

            var sceneName = Path.GetFileNameWithoutExtension(trimmed);
            var guids = AssetDatabase.FindAssets($"{sceneName} t:Scene");
            if (guids == null || guids.Length == 0)
            {
                error = $"could not find scene `{sceneRef}`.";
                return false;
            }

            var bestIndex = 0;
            for (var i = 0; i < guids.Length; i++)
            {
                var candidatePath = AssetDatabase.GUIDToAssetPath(guids[i]);
                var candidateName = Path.GetFileNameWithoutExtension(candidatePath);
                if (string.Equals(candidateName, sceneName, StringComparison.Ordinal))
                {
                    bestIndex = i;
                    break;
                }
            }

            scenePath = AssetDatabase.GUIDToAssetPath(guids[bestIndex]);
            return !string.IsNullOrWhiteSpace(scenePath);
        }

        private static bool TryResolveScenePathAsFile(string sceneRef, out string scenePath)
        {
            scenePath = string.Empty;

            if (Path.IsPathRooted(sceneRef))
            {
                if (File.Exists(sceneRef))
                {
                    scenePath = ToUnityScenePath(sceneRef);
                    return true;
                }

                return false;
            }

            var fromProjectRoot = Path.GetFullPath(Path.Combine(CodexChatHelper.GetProjectRootPath(), sceneRef));
            if (File.Exists(fromProjectRoot))
            {
                scenePath = ToUnityScenePath(fromProjectRoot);
                return true;
            }

            if (sceneRef.StartsWith("Assets/", StringComparison.Ordinal))
            {
                var assetFullPath = Path.GetFullPath(Path.Combine(CodexChatHelper.GetProjectRootPath(), sceneRef));
                if (File.Exists(assetFullPath))
                {
                    scenePath = sceneRef.Replace('\\', '/');
                    return true;
                }
            }

            return false;
        }

        private static string ToUnityScenePath(string filePath)
        {
            var root = CodexChatHelper.GetProjectRootPath();
            var normalizedRoot = Path.GetFullPath(root).Replace('\\', '/');
            if (!normalizedRoot.EndsWith("/", StringComparison.Ordinal))
            {
                normalizedRoot += "/";
            }

            var normalizedFile = Path.GetFullPath(filePath).Replace('\\', '/');

            if (!normalizedFile.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
            {
                return normalizedFile;
            }

            return normalizedFile.Substring(normalizedRoot.Length);
        }

        private static Type ResolveComponentType(string rawTypeName)
        {
            if (string.IsNullOrWhiteSpace(rawTypeName))
            {
                return null;
            }

            var typeName = rawTypeName.Trim();
            if (ComponentTypeCache.TryGetValue(typeName, out var cached))
            {
                return cached;
            }

            var exact = Type.GetType(typeName, false);
            if (IsValidComponentType(exact))
            {
                ComponentTypeCache[typeName] = exact;
                return exact;
            }

            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (var i = 0; i < assemblies.Length; i++)
            {
                var type = assemblies[i].GetType(typeName, false);
                if (!IsValidComponentType(type))
                {
                    continue;
                }

                ComponentTypeCache[typeName] = type;
                return type;
            }

            for (var asmIndex = 0; asmIndex < assemblies.Length; asmIndex++)
            {
                var asm = assemblies[asmIndex];
                Type[] types;
                try
                {
                    types = asm.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    types = ex.Types;
                }
                catch
                {
                    continue;
                }

                if (types == null)
                {
                    continue;
                }

                for (var typeIndex = 0; typeIndex < types.Length; typeIndex++)
                {
                    var type = types[typeIndex];
                    if (!IsValidComponentType(type))
                    {
                        continue;
                    }

                    if (!string.Equals(type.Name, typeName, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    ComponentTypeCache[typeName] = type;
                    return type;
                }
            }

            return null;
        }

        private static bool IsValidComponentType(Type type)
        {
            return type != null
                   && typeof(Component).IsAssignableFrom(type)
                   && !type.IsAbstract;
        }

        private static string GetHierarchyPath(Transform transform)
        {
            if (transform == null)
            {
                return string.Empty;
            }

            var names = new List<string>();
            var current = transform;
            while (current != null)
            {
                names.Add(current.name);
                current = current.parent;
            }

            names.Reverse();
            return string.Join("/", names);
        }

        private enum ActionOutcome
        {
            Applied,
            Skipped,
            Failed
        }
    }

    [Serializable]
    internal sealed class CodexUnityActionRequest
    {
        public string scene;
        public bool saveScene = true;
        public CodexUnityAction[] actions;
    }

    [Serializable]
    internal sealed class CodexUnityAction
    {
        public string type;
        public string target;
        public string component;
        public bool includeInactive = true;

        // Sprite object creation.
        public string objectName;
        public string spritePath;
        public bool setPosition;
        public float posX;
        public float posY;
        public float posZ;
        public bool setScale;
        public float scaleX = 1f;
        public float scaleY = 1f;
        public float scaleZ = 1f;
        public string sortingLayer;
        public int orderInLayer;
    }
}
