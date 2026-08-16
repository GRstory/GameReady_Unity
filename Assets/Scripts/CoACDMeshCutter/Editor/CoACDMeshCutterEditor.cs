using System.IO;
using UnityEditor;
using UnityEngine;

namespace GameReady.CoACDMeshCutter.Editor
{
    [CustomEditor(typeof(CoACDMeshCutter))]
    public sealed class CoACDMeshCutterEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            EditorGUILayout.Space();

            var cutter = (CoACDMeshCutter)target;
            using (new EditorGUI.DisabledScope(EditorApplication.isPlayingOrWillChangePlaymode))
            {
                if (GUILayout.Button("Generate Cut Pieces"))
                    Generate(cutter, false);
                if (GUILayout.Button("Generate And Save Mesh Assets"))
                    Generate(cutter, true);
                if (GUILayout.Button("Clear Generated Pieces"))
                    Clear(cutter);
            }

            EditorGUILayout.HelpBox(
                "1. Add CoACD and calculate its colliders.\n" +
                "2. Add this component to the same object.\n" +
                "3. Generate cut pieces.\n\n" +
                "The source mesh must have Read/Write enabled. CoACD colliders must be convex.",
                MessageType.Info);
        }

        private static void Generate(CoACDMeshCutter cutter, bool saveAssets)
        {
            Undo.RegisterFullObjectHierarchyUndo(cutter.gameObject, "Generate CoACD Cut Pieces");
            cutter.ClearGeneratedPieces();
            var meshes = cutter.BuildCutMeshes(out var warnings);
            if (meshes.Count == 0)
            {
                ShowWarnings(warnings);
                return;
            }

            if (saveAssets)
                SaveMeshes(cutter, meshes);

            var root = cutter.CreatePieceObjects(meshes);
            if (root != null)
                Undo.RegisterCreatedObjectUndo(root.gameObject, "Generate CoACD Cut Pieces");
            EditorUtility.SetDirty(cutter.gameObject);
            ShowWarnings(warnings);
            Debug.Log($"[CoACD Mesh Cutter] Generated {meshes.Count} piece(s).", cutter);
        }

        private static void SaveMeshes(CoACDMeshCutter cutter, System.Collections.Generic.IReadOnlyList<Mesh> meshes)
        {
            var folder = NormalizeAssetFolder(cutter.AssetOutputFolder);
            EnsureAssetFolder(folder);
            var sourceName = cutter.Source != null && cutter.Source.sharedMesh != null
                ? SanitizeFileName(cutter.Source.sharedMesh.name)
                : "Mesh";

            for (var i = 0; i < meshes.Count; i++)
            {
                var path = AssetDatabase.GenerateUniqueAssetPath($"{folder}/{sourceName}_Piece_{i:000}.asset");
                AssetDatabase.CreateAsset(meshes[i], path);
            }
            AssetDatabase.SaveAssets();
        }

        private static void Clear(CoACDMeshCutter cutter)
        {
            var root = cutter.GeneratedRoot;
            if (root != null && root != cutter.transform)
                Undo.DestroyObjectImmediate(root.gameObject);

            if (cutter.Source != null && cutter.Source.TryGetComponent<MeshRenderer>(out var renderer))
            {
                Undo.RecordObject(renderer, "Clear CoACD Cut Pieces");
                renderer.enabled = true;
            }
        }

        private static void ShowWarnings(System.Collections.Generic.IReadOnlyList<string> warnings)
        {
            if (warnings.Count == 0)
                return;
            Debug.LogWarning("[CoACD Mesh Cutter]\n" + string.Join("\n", warnings));
        }

        private static string NormalizeAssetFolder(string folder)
        {
            folder = string.IsNullOrWhiteSpace(folder) ? "Assets/Generated/CoACDMeshCutter" : folder.Trim();
            folder = folder.Replace('\\', '/').TrimEnd('/');
            return folder.StartsWith("Assets/") ? folder : "Assets/Generated/CoACDMeshCutter";
        }

        private static void EnsureAssetFolder(string folder)
        {
            var parts = folder.Split('/');
            var current = parts[0];
            for (var i = 1; i < parts.Length; i++)
            {
                var next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next))
                    AssetDatabase.CreateFolder(current, parts[i]);
                current = next;
            }
        }

        private static string SanitizeFileName(string value)
        {
            foreach (var character in Path.GetInvalidFileNameChars())
                value = value.Replace(character, '_');
            return string.IsNullOrWhiteSpace(value) ? "Mesh" : value;
        }
    }
}
