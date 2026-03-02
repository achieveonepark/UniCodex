using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Achieve.UniCodex.Editor
{
    /// <summary>
    /// Codex CLI용 파일 기반 Unity 브리지입니다.
    /// Codex가 Library/CodexUnityActions.json에 액션 JSON을 기록하면 에디터에서 이를 적용합니다.
    /// </summary>
    internal static class UniCodexUnityEditorHelper
    {
        private const string MenuRoot = "Tools/Codex/Unity Helper/";
        private const string DefaultGeneratedPrefabFolder = "Assets/Res/Prefabs/CodexGenerated";
        private const string DefaultCsvTableFolder = "Assets/Resources/DataTables";
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
            var actionPath = UniCodexChatHelper.GetUnityActionFilePath();
            Directory.CreateDirectory(Path.GetDirectoryName(actionPath) ?? "Library");

            var template = new UniCodexUnityActionRequest
            {
                scene = "Title",
                saveScene = true,
                actions = new[]
                {
                    new UniCodexUnityAction
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
                    },
                    new UniCodexUnityAction
                    {
                        type = "SavePrefabFromTarget",
                        target = "GuideCat",
                        prefabName = "GuideCatPrefab",
                        outputFolder = DefaultGeneratedPrefabFolder,
                        overwriteExisting = false
                    },
                    new UniCodexUnityAction
                    {
                        type = "CreateCsvDataTable",
                        tableName = "EnemyStats",
                        columns = new[] { "id", "name", "hp" },
                        rows = new[]
                        {
                            new UniCodexCsvRow { values = new[] { "slime_001", "Slime", "25" } },
                            new UniCodexCsvRow { values = new[] { "slime_002", "Big Slime", "40" } }
                        },
                        csvFolder = DefaultCsvTableFolder,
                        overwriteTable = false
                    }
                }
            };

            var json = JsonUtility.ToJson(template, true);
            File.WriteAllText(actionPath, json);
            AssetDatabase.Refresh();
            Debug.Log($"Unity action template written: {actionPath}");
        }

        [MenuItem(MenuRoot + "Write CSV Table Template")]
        private static void WriteCsvTableTemplateMenu()
        {
            if (!TryResolveWritableAssetFolderPath(DefaultCsvTableFolder, out var csvFolder, out var error))
            {
                Debug.LogError($"Unity CSV template write failed: {error}");
                return;
            }

            var filePath = GetUniqueAssetFilePath(csvFolder, "SampleTable", ".csv");
            if (string.IsNullOrWhiteSpace(filePath))
            {
                Debug.LogError("Unity CSV template write failed: could not allocate output file path.");
                return;
            }

            var csv = "id,name,desc\nsample_001,Sample Item,This is a sample row.\n";
            if (!WriteUtf8File(filePath, csv, out var writeError))
            {
                Debug.LogError($"Unity CSV template write failed: {writeError}");
                return;
            }

            AssetDatabase.ImportAsset(filePath, ImportAssetOptions.ForceUpdate);
            AssetDatabase.Refresh();
            Debug.Log($"Unity CSV table template written: {filePath}");
        }

        [MenuItem(MenuRoot + "Open Generated Prefab Folder")]
        private static void OpenGeneratedPrefabFolderMenu()
        {
            if (!TryResolveWritableAssetFolderPath(DefaultGeneratedPrefabFolder, out var folderPath, out var error))
            {
                Debug.LogError($"Open generated prefab folder failed: {error}");
                return;
            }

            var absolute = ToProjectAbsolutePath(folderPath);
            if (string.IsNullOrWhiteSpace(absolute) || !Directory.Exists(absolute))
            {
                Debug.LogError($"Open generated prefab folder failed: folder not found `{folderPath}`.");
                return;
            }

            EditorUtility.RevealInFinder(absolute);
        }

        /// <summary>
        /// 대기 중인 액션 파일을 읽어 Unity 에디터에 적용합니다.
        /// 대기 파일이 없으면 false를 반환합니다.
        /// </summary>
        public static bool TryApplyPendingActions(out string summary)
        {
            summary = string.Empty;
            var actionPath = UniCodexChatHelper.GetUnityActionFilePath();
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

            UniCodexUnityActionRequest request;
            try
            {
                request = JsonUtility.FromJson<UniCodexUnityActionRequest>(json);
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

        private static ActionOutcome ApplyAction(Scene scene, UniCodexUnityAction action, StringBuilder log)
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

            if (actionType.Equals("SavePrefabFromTarget", StringComparison.OrdinalIgnoreCase))
            {
                return TrySavePrefabFromTarget(scene, action, log);
            }

            if (actionType.Equals("CreateCsvDataTable", StringComparison.OrdinalIgnoreCase))
            {
                return TryCreateCsvDataTable(action, log);
            }

            log.AppendLine($"- Failed: unsupported action type `{actionType}`.");
            return ActionOutcome.Failed;
        }

        private static ActionOutcome TrySavePrefabFromTarget(Scene scene, UniCodexUnityAction action, StringBuilder log)
        {
            if (!TryFindTargetObject(scene, action.target, action.includeInactive, out var gameObject, out var findError))
            {
                log.AppendLine($"- Failed to save prefab: {findError}");
                return ActionOutcome.Failed;
            }

            var requestedFolder = string.IsNullOrWhiteSpace(action.outputFolder)
                ? DefaultGeneratedPrefabFolder
                : action.outputFolder.Trim();
            if (!TryResolveWritableAssetFolderPath(requestedFolder, out var outputFolder, out var folderError))
            {
                log.AppendLine($"- Failed to save prefab: {folderError}");
                return ActionOutcome.Failed;
            }

            var baseNameRaw = string.IsNullOrWhiteSpace(action.prefabName) ? gameObject.name : action.prefabName.Trim();
            var baseName = SanitizeAssetName(baseNameRaw, "CodexPrefab");
            var targetPath = $"{outputFolder}/{baseName}.prefab";
            if (AssetDatabase.LoadAssetAtPath<GameObject>(targetPath) != null && !action.overwriteExisting)
            {
                targetPath = GetUniqueAssetFilePath(outputFolder, baseName, ".prefab");
            }

            var prefab = PrefabUtility.SaveAsPrefabAsset(gameObject, targetPath, out var success);
            if (!success || prefab == null)
            {
                log.AppendLine($"- Failed to save prefab: Unity could not write `{targetPath}`.");
                return ActionOutcome.Failed;
            }

            AssetDatabase.ImportAsset(targetPath, ImportAssetOptions.ForceUpdate);
            log.AppendLine($"- Saved prefab `{targetPath}` from `{GetHierarchyPath(gameObject.transform)}`.");
            return ActionOutcome.Applied;
        }

        private static ActionOutcome TryCreateCsvDataTable(UniCodexUnityAction action, StringBuilder log)
        {
            if (action == null)
            {
                log.AppendLine("- Failed to create CSV table: action is missing.");
                return ActionOutcome.Failed;
            }

            var rawTableName = string.IsNullOrWhiteSpace(action.tableName) ? string.Empty : action.tableName.Trim();
            if (string.IsNullOrWhiteSpace(rawTableName))
            {
                log.AppendLine("- Failed to create CSV table: tableName is missing.");
                return ActionOutcome.Failed;
            }

            if (action.columns == null || action.columns.Length == 0)
            {
                log.AppendLine("- Failed to create CSV table: columns are missing.");
                return ActionOutcome.Failed;
            }

            var normalizedColumns = new string[action.columns.Length];
            var idIndex = -1;
            var columnSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < action.columns.Length; i++)
            {
                var name = string.IsNullOrWhiteSpace(action.columns[i]) ? string.Empty : action.columns[i].Trim();
                if (string.IsNullOrWhiteSpace(name))
                {
                    log.AppendLine($"- Failed to create CSV table: columns[{i}] is empty.");
                    return ActionOutcome.Failed;
                }

                if (!columnSet.Add(name))
                {
                    log.AppendLine($"- Failed to create CSV table: duplicate column `{name}`.");
                    return ActionOutcome.Failed;
                }

                normalizedColumns[i] = name;
                if (string.Equals(name, "id", StringComparison.OrdinalIgnoreCase))
                {
                    idIndex = i;
                }
            }

            if (idIndex < 0)
            {
                log.AppendLine("- Failed to create CSV table: required `id` column is missing.");
                return ActionOutcome.Failed;
            }

            var rows = action.rows ?? Array.Empty<UniCodexCsvRow>();
            var idSet = new HashSet<string>(StringComparer.Ordinal);
            for (var rowIndex = 0; rowIndex < rows.Length; rowIndex++)
            {
                var row = rows[rowIndex];
                var values = row?.values;
                if (values == null || values.Length != normalizedColumns.Length)
                {
                    log.AppendLine($"- Failed to create CSV table: row[{rowIndex}] value count does not match columns.");
                    return ActionOutcome.Failed;
                }

                var idValue = values[idIndex]?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(idValue))
                {
                    log.AppendLine($"- Failed to create CSV table: row[{rowIndex}] has empty id.");
                    return ActionOutcome.Failed;
                }

                if (!idSet.Add(idValue))
                {
                    log.AppendLine($"- Failed to create CSV table: duplicate id `{idValue}`.");
                    return ActionOutcome.Failed;
                }
            }

            var requestedFolder = string.IsNullOrWhiteSpace(action.csvFolder)
                ? DefaultCsvTableFolder
                : action.csvFolder.Trim();
            if (!TryResolveWritableAssetFolderPath(requestedFolder, out var outputFolder, out var folderError))
            {
                log.AppendLine($"- Failed to create CSV table: {folderError}");
                return ActionOutcome.Failed;
            }

            var sanitizedTableName = SanitizeAssetName(rawTableName, "Table");
            var tablePath = $"{outputFolder}/{sanitizedTableName}.csv";
            var existing = AssetDatabase.LoadAssetAtPath<TextAsset>(tablePath) != null || File.Exists(ToProjectAbsolutePath(tablePath));
            if (existing && !action.overwriteTable)
            {
                log.AppendLine($"- Failed to create CSV table: `{tablePath}` already exists (set overwriteTable=true to replace).");
                return ActionOutcome.Failed;
            }

            var csvText = BuildCsvText(normalizedColumns, rows);
            if (!WriteUtf8File(tablePath, csvText, out var writeError))
            {
                log.AppendLine($"- Failed to create CSV table: {writeError}");
                return ActionOutcome.Failed;
            }

            AssetDatabase.ImportAsset(tablePath, ImportAssetOptions.ForceUpdate);
            AssetDatabase.Refresh();
            log.AppendLine($"- Created CSV table `{tablePath}` ({rows.Length} rows).");
            return ActionOutcome.Applied;
        }

        private static ActionOutcome TryAddComponent(Scene scene, UniCodexUnityAction action, StringBuilder log)
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

        private static ActionOutcome TryRemoveComponent(Scene scene, UniCodexUnityAction action, StringBuilder log)
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

        private static ActionOutcome TryCreateSpriteObject(Scene scene, UniCodexUnityAction action, StringBuilder log)
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
                var absolute = Path.GetFullPath(Path.Combine(UniCodexChatHelper.GetProjectRootPath(), normalized));
                return File.Exists(absolute) ? normalized : string.Empty;
            }

            var absoluteInput = Path.GetFullPath(Path.Combine(UniCodexChatHelper.GetProjectRootPath(), normalized));
            if (!File.Exists(absoluteInput))
            {
                return string.Empty;
            }

            var projectRoot = Path.GetFullPath(UniCodexChatHelper.GetProjectRootPath()).Replace('\\', '/');
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
                Directory.CreateDirectory(Path.Combine(UniCodexChatHelper.GetProjectRootPath(), "Assets/Res/Sprites/CodexImported"));

                var destinationPath = $"{importFolder}/{fileName}".Replace('\\', '/');
                var fullDestinationPath = Path.GetFullPath(Path.Combine(UniCodexChatHelper.GetProjectRootPath(), destinationPath));

                if (File.Exists(fullDestinationPath))
                {
                    var name = Path.GetFileNameWithoutExtension(fileName);
                    var ext = Path.GetExtension(fileName);
                    destinationPath = $"{importFolder}/{name}_{Guid.NewGuid():N}{ext}".Replace('\\', '/');
                    fullDestinationPath = Path.GetFullPath(Path.Combine(UniCodexChatHelper.GetProjectRootPath(), destinationPath));
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

        private static bool TryResolveWritableAssetFolderPath(string rawFolderPath, out string assetFolderPath, out string error)
        {
            assetFolderPath = string.Empty;
            error = string.Empty;

            if (string.IsNullOrWhiteSpace(rawFolderPath))
            {
                error = "output folder is missing.";
                return false;
            }

            var normalized = rawFolderPath.Trim().Replace('\\', '/');
            if (Path.IsPathRooted(normalized))
            {
                normalized = ToUnityAssetPath(normalized);
            }

            if (string.IsNullOrWhiteSpace(normalized))
            {
                error = $"invalid folder path `{rawFolderPath}`.";
                return false;
            }

            var isAssetsRoot = normalized.Equals("Assets", StringComparison.Ordinal);
            var isAssetsChild = normalized.StartsWith("Assets/", StringComparison.Ordinal);
            if (!isAssetsRoot && !isAssetsChild)
            {
                error = $"folder path must be under Assets (`{rawFolderPath}`).";
                return false;
            }

            var absolute = ToProjectAbsolutePath(normalized);
            if (string.IsNullOrWhiteSpace(absolute))
            {
                error = $"failed to resolve folder path `{rawFolderPath}`.";
                return false;
            }

            try
            {
                Directory.CreateDirectory(absolute);
                assetFolderPath = normalized;
                return true;
            }
            catch (Exception ex)
            {
                error = $"failed to create folder `{normalized}`: {ex.Message}";
                return false;
            }
        }

        private static string GetUniqueAssetFilePath(string assetFolderPath, string baseName, string extension)
        {
            if (string.IsNullOrWhiteSpace(assetFolderPath))
            {
                return string.Empty;
            }

            var safeBaseName = SanitizeAssetName(baseName, "Asset");
            var safeExtension = string.IsNullOrWhiteSpace(extension) ? string.Empty : extension.Trim();
            if (!string.IsNullOrEmpty(safeExtension) && !safeExtension.StartsWith(".", StringComparison.Ordinal))
            {
                safeExtension = "." + safeExtension;
            }

            var candidate = $"{assetFolderPath}/{safeBaseName}{safeExtension}";
            if (!File.Exists(ToProjectAbsolutePath(candidate)) && AssetDatabase.LoadMainAssetAtPath(candidate) == null)
            {
                return candidate;
            }

            for (var i = 1; i <= 9999; i++)
            {
                candidate = $"{assetFolderPath}/{safeBaseName}_{i:000}{safeExtension}";
                if (!File.Exists(ToProjectAbsolutePath(candidate)) && AssetDatabase.LoadMainAssetAtPath(candidate) == null)
                {
                    return candidate;
                }
            }

            return string.Empty;
        }

        private static bool WriteUtf8File(string assetPath, string content, out string error)
        {
            error = string.Empty;
            if (string.IsNullOrWhiteSpace(assetPath))
            {
                error = "asset path is empty.";
                return false;
            }

            var absolute = ToProjectAbsolutePath(assetPath);
            if (string.IsNullOrWhiteSpace(absolute))
            {
                error = $"failed to resolve file path `{assetPath}`.";
                return false;
            }

            try
            {
                var dir = Path.GetDirectoryName(absolute);
                if (!string.IsNullOrWhiteSpace(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                File.WriteAllText(absolute, content ?? string.Empty, new UTF8Encoding(false));
                return true;
            }
            catch (Exception ex)
            {
                error = $"failed to write `{assetPath}`: {ex.Message}";
                return false;
            }
        }

        private static string BuildCsvText(string[] columns, UniCodexCsvRow[] rows)
        {
            var sb = new StringBuilder(1024);
            AppendCsvRow(sb, columns);
            sb.Append('\n');

            if (rows != null)
            {
                for (var i = 0; i < rows.Length; i++)
                {
                    var values = rows[i]?.values ?? Array.Empty<string>();
                    AppendCsvRow(sb, values);
                    sb.Append('\n');
                }
            }

            return sb.ToString();
        }

        private static void AppendCsvRow(StringBuilder sb, IReadOnlyList<string> values)
        {
            if (values == null)
            {
                return;
            }

            for (var i = 0; i < values.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(',');
                }

                AppendCsvCell(sb, values[i] ?? string.Empty);
            }
        }

        private static void AppendCsvCell(StringBuilder sb, string raw)
        {
            var value = raw ?? string.Empty;
            var mustQuote = value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0;
            if (!mustQuote)
            {
                sb.Append(value);
                return;
            }

            sb.Append('"');
            for (var i = 0; i < value.Length; i++)
            {
                if (value[i] == '"')
                {
                    sb.Append("\"\"");
                }
                else
                {
                    sb.Append(value[i]);
                }
            }
            sb.Append('"');
        }

        private static string SanitizeAssetName(string rawName, string fallback)
        {
            var name = string.IsNullOrWhiteSpace(rawName) ? string.Empty : rawName.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                name = fallback;
            }

            var invalid = Path.GetInvalidFileNameChars();
            for (var i = 0; i < invalid.Length; i++)
            {
                name = name.Replace(invalid[i], '_');
            }

            return string.IsNullOrWhiteSpace(name) ? fallback : name;
        }

        private static string ToProjectAbsolutePath(string assetPath)
        {
            if (string.IsNullOrWhiteSpace(assetPath))
            {
                return string.Empty;
            }

            var normalized = assetPath.Replace('\\', '/');
            if (Path.IsPathRooted(normalized))
            {
                return Path.GetFullPath(normalized);
            }

            return Path.GetFullPath(Path.Combine(UniCodexChatHelper.GetProjectRootPath(), normalized));
        }

        private static string ToUnityAssetPath(string absolutePath)
        {
            if (string.IsNullOrWhiteSpace(absolutePath))
            {
                return string.Empty;
            }

            var root = Path.GetFullPath(UniCodexChatHelper.GetProjectRootPath()).Replace('\\', '/');
            if (!root.EndsWith("/", StringComparison.Ordinal))
            {
                root += "/";
            }

            var full = Path.GetFullPath(absolutePath).Replace('\\', '/');
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            return full.Substring(root.Length);
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

            var fromProjectRoot = Path.GetFullPath(Path.Combine(UniCodexChatHelper.GetProjectRootPath(), sceneRef));
            if (File.Exists(fromProjectRoot))
            {
                scenePath = ToUnityScenePath(fromProjectRoot);
                return true;
            }

            if (sceneRef.StartsWith("Assets/", StringComparison.Ordinal))
            {
                var assetFullPath = Path.GetFullPath(Path.Combine(UniCodexChatHelper.GetProjectRootPath(), sceneRef));
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
            var root = UniCodexChatHelper.GetProjectRootPath();
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
    internal sealed class UniCodexUnityActionRequest
    {
        /// <summary>대상 씬 이름 또는 경로입니다.</summary>
        public string scene;
        /// <summary>수정 적용 후 씬 저장 여부입니다.</summary>
        public bool saveScene = true;
        /// <summary>순서대로 적용할 액션 목록입니다.</summary>
        public UniCodexUnityAction[] actions;
    }

    [Serializable]
    internal sealed class UniCodexUnityAction
    {
        /// <summary>액션 타입(AddComponent, RemoveComponent, CreateSpriteObject, SavePrefabFromTarget, CreateCsvDataTable)입니다.</summary>
        public string type;
        /// <summary>액션에 따라 사용할 대상 오브젝트 이름 또는 계층 경로입니다.</summary>
        public string target;
        /// <summary>컴포넌트 추가/제거 시 사용할 컴포넌트 타입명입니다.</summary>
        public string component;
        /// <summary>대상 탐색 시 비활성 오브젝트 포함 여부입니다.</summary>
        public bool includeInactive = true;

        // Sprite object creation.
        /// <summary>생성할 스프라이트 오브젝트 이름입니다.</summary>
        public string objectName;
        /// <summary>프로젝트 상대 스프라이트 에셋 경로입니다.</summary>
        public string spritePath;
        /// <summary>생성 후 로컬 위치를 강제로 설정할지 여부입니다.</summary>
        public bool setPosition;
        /// <summary><see cref="setPosition"/>이 true일 때 적용할 로컬 위치 X 값입니다.</summary>
        public float posX;
        /// <summary><see cref="setPosition"/>이 true일 때 적용할 로컬 위치 Y 값입니다.</summary>
        public float posY;
        /// <summary><see cref="setPosition"/>이 true일 때 적용할 로컬 위치 Z 값입니다.</summary>
        public float posZ;
        /// <summary>생성 후 로컬 스케일을 강제로 설정할지 여부입니다.</summary>
        public bool setScale;
        /// <summary><see cref="setScale"/>이 true일 때 적용할 로컬 스케일 X 값입니다.</summary>
        public float scaleX = 1f;
        /// <summary><see cref="setScale"/>이 true일 때 적용할 로컬 스케일 Y 값입니다.</summary>
        public float scaleY = 1f;
        /// <summary><see cref="setScale"/>이 true일 때 적용할 로컬 스케일 Z 값입니다.</summary>
        public float scaleZ = 1f;
        /// <summary>SpriteRenderer에 적용할 Sorting Layer 이름입니다.</summary>
        public string sortingLayer;
        /// <summary>SpriteRenderer에 적용할 Order in Layer 값입니다.</summary>
        public int orderInLayer;

        // Prefab save.
        /// <summary>저장할 프리팹 이름입니다. 비어 있으면 대상 오브젝트 이름을 사용합니다.</summary>
        public string prefabName;
        /// <summary>프리팹 저장 폴더입니다. 비어 있으면 기본 폴더를 사용합니다.</summary>
        public string outputFolder;
        /// <summary>동일 경로 프리팹이 있을 때 덮어쓸지 여부입니다.</summary>
        public bool overwriteExisting;

        // CSV data table.
        /// <summary>생성할 데이터테이블 이름(확장자 제외)입니다.</summary>
        public string tableName;
        /// <summary>CSV 컬럼 목록입니다. id 컬럼은 필수입니다.</summary>
        public string[] columns;
        /// <summary>CSV 데이터 행 목록입니다.</summary>
        public UniCodexCsvRow[] rows;
        /// <summary>CSV 저장 폴더입니다. 비어 있으면 기본 폴더를 사용합니다.</summary>
        public string csvFolder;
        /// <summary>동일 이름 CSV가 있을 때 덮어쓸지 여부입니다.</summary>
        public bool overwriteTable;
    }

    [Serializable]
    internal sealed class UniCodexCsvRow
    {
        /// <summary>CSV 한 행의 셀 값 배열입니다.</summary>
        public string[] values;
    }
}
