using System;
using System.Collections.Generic;
using UnityEngine;

namespace GameReady.CoACDMeshCutter
{
    [DisallowMultipleComponent]
    [AddComponentMenu("GameReady/CoACD Mesh Cutter")]
    public sealed class CoACDMeshCutter : MonoBehaviour
    {
        private const string GeneratedRootName = "CoACD Cut Pieces";

        [Header("Input")]
        [SerializeField] private MeshFilter source;
        [SerializeField, Tooltip("CoACD normally places its MeshColliders on this object.")]
        private Transform colliderRoot;
        [SerializeField, Tooltip("Also use convex MeshColliders below Collider Root.")]
        private bool includeChildColliders;

        [Header("Output")]
        [SerializeField] private bool capSections = true;
        [SerializeField] private Material capMaterial;
        [SerializeField] private float capUvScale = 1f;
        [SerializeField] private bool addMeshColliders = true;
        [SerializeField, Tooltip("Convex output colliders can be used by dynamic Rigidbodies, but Unity may simplify complex pieces.")]
        private bool outputCollidersConvex;
        [SerializeField] private bool disableSourceRenderer;

        [Header("Precision")]
        [SerializeField, Min(0.0000001f)] private float planeTolerance = 0.00001f;
        [SerializeField, Min(0.0000001f)] private float weldTolerance = 0.0001f;
        [SerializeField, Min(0f)] private float minimumTriangleArea = 0.00000001f;

        [Header("Editor Asset Output")]
        [SerializeField] private string assetOutputFolder = "Assets/Generated/CoACDMeshCutter";
        [SerializeField, HideInInspector] private CoACDGeneratedPieces generatedRoot;

        public MeshFilter Source => source;
        public string AssetOutputFolder => assetOutputFolder;
        public Transform GeneratedRoot
        {
            get
            {
                if (generatedRoot != null)
                    return generatedRoot.transform;
                generatedRoot = GetComponentInChildren<CoACDGeneratedPieces>(true);
                if (generatedRoot == null && source != null && source.transform != transform)
                    generatedRoot = source.GetComponentInChildren<CoACDGeneratedPieces>(true);
                return generatedRoot != null ? generatedRoot.transform : null;
            }
        }

        private void Reset()
        {
            source = GetComponent<MeshFilter>();
            colliderRoot = transform;
        }

        private void OnValidate()
        {
            if (colliderRoot == null)
                colliderRoot = transform;
            planeTolerance = Mathf.Max(0.0000001f, planeTolerance);
            weldTolerance = Mathf.Max(0.0000001f, weldTolerance);
            minimumTriangleArea = Mathf.Max(0f, minimumTriangleArea);
        }

        /// <summary>Builds cut meshes without creating scene objects. The caller owns the returned meshes.</summary>
        public List<Mesh> BuildCutMeshes(out List<string> warnings)
        {
            warnings = new List<string>();
            var result = new List<Mesh>();
            if (source == null || source.sharedMesh == null)
            {
                warnings.Add("Assign a readable source MeshFilter first.");
                return result;
            }

            var root = colliderRoot != null ? colliderRoot : transform;
            var colliders = includeChildColliders
                ? root.GetComponentsInChildren<MeshCollider>(true)
                : root.GetComponents<MeshCollider>();
            var settings = new ConvexMeshClipper.Settings(
                capSections,
                planeTolerance,
                weldTolerance,
                minimumTriangleArea,
                capUvScale);

            foreach (var meshCollider in colliders)
            {
                if (IsGeneratedPiece(meshCollider.transform))
                    continue;

                var mesh = ConvexMeshClipper.Cut(
                    source.sharedMesh,
                    source.transform,
                    meshCollider,
                    settings,
                    out var error);
                if (!string.IsNullOrEmpty(error))
                    warnings.Add(error);
                if (mesh != null && mesh.vertexCount >= 3)
                {
                    mesh.name = $"{source.sharedMesh.name}_Piece_{result.Count:000}";
                    result.Add(mesh);
                }
            }

            if (colliders.Length == 0)
                warnings.Add("No MeshCollider was found. Calculate the CoACD colliders first.");
            else if (result.Count == 0 && warnings.Count == 0)
                warnings.Add("The source mesh did not overlap any collider volumes.");
            return result;
        }

        /// <summary>Replaces the generated child hierarchy with newly cut pieces.</summary>
        [ContextMenu("Generate Cut Pieces")]
        public void GenerateCutPieces()
        {
            ClearGeneratedPieces();
            var meshes = BuildCutMeshes(out var warnings);
            if (meshes.Count > 0)
                CreatePieceObjects(meshes);
            foreach (var warning in warnings)
                Debug.LogWarning($"[CoACD Mesh Cutter] {warning}", this);
        }

        public Transform CreatePieceObjects(IReadOnlyList<Mesh> meshes)
        {
            if (meshes == null || meshes.Count == 0)
                return null;

            var rootObject = new GameObject(GeneratedRootName);
            var rootTransform = rootObject.transform;
            rootTransform.SetParent(source.transform, false);
            generatedRoot = rootObject.AddComponent<CoACDGeneratedPieces>();

            var sourceRenderer = source.GetComponent<MeshRenderer>();
            var sourceMaterials = sourceRenderer != null ? sourceRenderer.sharedMaterials : Array.Empty<Material>();
            for (var i = 0; i < meshes.Count; i++)
            {
                var piece = new GameObject($"Piece {i:000}");
                piece.transform.SetParent(rootTransform, false);
                var filter = piece.AddComponent<MeshFilter>();
                filter.sharedMesh = meshes[i];
                var renderer = piece.AddComponent<MeshRenderer>();
                renderer.sharedMaterials = BuildMaterials(sourceMaterials, meshes[i].subMeshCount);

                if (addMeshColliders)
                {
                    var meshCollider = piece.AddComponent<MeshCollider>();
                    meshCollider.sharedMesh = meshes[i];
                    meshCollider.convex = outputCollidersConvex;
                }
            }

            if (disableSourceRenderer && sourceRenderer != null)
                sourceRenderer.enabled = false;
            return rootTransform;
        }

        [ContextMenu("Clear Cut Pieces")]
        public void ClearGeneratedPieces()
        {
            var root = GeneratedRoot;
            if (root != null && root != transform)
            {
                if (Application.isPlaying)
                    Destroy(root.gameObject);
                else
                    DestroyImmediate(root.gameObject);
            }
            generatedRoot = null;

            if (disableSourceRenderer && source != null && source.TryGetComponent<MeshRenderer>(out var sourceRenderer))
                sourceRenderer.enabled = true;
        }

        private Material[] BuildMaterials(Material[] sourceMaterials, int outputSubMeshCount)
        {
            var materials = new Material[outputSubMeshCount];
            for (var i = 0; i < materials.Length; i++)
            {
                if (i < sourceMaterials.Length)
                    materials[i] = sourceMaterials[i];
                else
                    materials[i] = capMaterial != null
                        ? capMaterial
                        : sourceMaterials.Length > 0 ? sourceMaterials[0] : null;
            }
            return materials;
        }

        private bool IsGeneratedPiece(Transform candidate)
        {
            while (candidate != null && candidate != transform)
            {
                if (candidate.TryGetComponent<CoACDGeneratedPieces>(out _))
                    return true;
                candidate = candidate.parent;
            }
            return false;
        }
    }

}
