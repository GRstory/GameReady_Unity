using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace GameReady.EditorTools
{
    public sealed class ObjectScreenshotWindow : EditorWindow
    {
        [SerializeField] private GameObject target;
        [SerializeField] private Vector2Int resolution = new Vector2Int(1024, 1024);
        [SerializeField] private Vector2 cameraAngle = new Vector2(0f, 180f);
        [SerializeField] private Vector3 objectRotation;
        [SerializeField, Range(0.1f, 1f)] private float frameFill = 0.8f;
        [SerializeField] private float zoom = 1f;
        [SerializeField] private Vector2 framingOffset;
        [SerializeField] private Color background = new Color(0.15f, 0.15f, 0.15f, 1f);
        [SerializeField] private string outputFolder = "Assets/Screenshots";

        [NonSerialized] private Texture2D previewImage;
        [NonSerialized] private Texture2D checkerTexture;
        [NonSerialized] private bool previewDirty = true;
        [NonSerialized] private string previewError;
        private static byte[] linearToGammaLut;

        [MenuItem("Tools/GameReady/Object Screenshot")]
        private static void Open()
        {
            var window = GetWindow<ObjectScreenshotWindow>("Object Screenshot");
            window.minSize = new Vector2(380f, 600f);
            if (Selection.activeGameObject != null)
                window.target = Selection.activeGameObject;
        }

        private void OnEnable()
        {
            previewDirty = true;
        }

        private void OnDisable()
        {
            if (previewImage != null)
                DestroyImmediate(previewImage);
            if (checkerTexture != null)
                DestroyImmediate(checkerTexture);
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("Editor Object Screenshot", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Base Fill is the fitted object size. 80% means the object occupies 80% of the limiting image axis before Zoom.\n" +
                "Lower the Background alpha to capture a transparent PNG.",
                MessageType.Info);

            EditorGUI.BeginChangeCheck();
            target = (GameObject)EditorGUILayout.ObjectField("Target", target, typeof(GameObject), true);
            if (GUILayout.Button("Use Selected Object"))
            {
                target = Selection.activeGameObject;
                previewDirty = true;
            }

            EditorGUILayout.Space();
            resolution = EditorGUILayout.Vector2IntField("Resolution", resolution);
            resolution.x = Mathf.Clamp(resolution.x, 16, 8192);
            resolution.y = Mathf.Clamp(resolution.y, 16, 8192);
            cameraAngle.x = EditorGUILayout.Slider("Pitch", cameraAngle.x, -89f, 89f);
            cameraAngle.y = EditorGUILayout.Slider("Yaw", cameraAngle.y, -180f, 180f);
            objectRotation = EditorGUILayout.Vector3Field("Object Rotation", objectRotation);
            frameFill = EditorGUILayout.Slider("Base Fill (%)", frameFill * 100f, 10f, 100f) / 100f;
            zoom = EditorGUILayout.Slider("Zoom", zoom, 0.1f, 20f);
            framingOffset = EditorGUILayout.Vector2Field("Framing Offset", framingOffset);
            if (GUILayout.Button("Reset Framing"))
            {
                objectRotation = Vector3.zero;
                zoom = 1f;
                framingOffset = Vector2.zero;
                previewDirty = true;
            }
            background = EditorGUILayout.ColorField("Background", background);
            outputFolder = EditorGUILayout.TextField("Output Folder", outputFolder);
            if (EditorGUI.EndChangeCheck())
                previewDirty = true;

            EditorGUILayout.Space();
            DrawPreview();

            EditorGUILayout.Space();
            using (new EditorGUI.DisabledScope(target == null || EditorApplication.isPlayingOrWillChangePlaymode))
            {
                if (GUILayout.Button("Capture PNG", GUILayout.Height(32f)))
                    Capture();
            }
        }

        private void DrawPreview()
        {
            var aspect = (float)resolution.x / resolution.y;
            var width = Mathf.Min(position.width - 24f, 512f);
            var height = width / aspect;
            if (height > 320f)
            {
                height = 320f;
                width = height * aspect;
            }

            var rect = GUILayoutUtility.GetRect(width, height, GUILayout.ExpandWidth(false));
            rect.x += (position.width - rect.width) * 0.5f - 4f;

            if (previewDirty && Event.current.type == EventType.Repaint)
                RefreshPreview();

            EditorGUI.DrawRect(rect, Color.black);
            if (previewImage != null)
            {
                if (background.a < 1f)
                {
                    GUI.DrawTextureWithTexCoords(
                        rect,
                        GetCheckerTexture(),
                        new Rect(0f, 0f, rect.width / 16f, rect.height / 16f),
                        false);
                }
                GUI.DrawTexture(rect, previewImage, ScaleMode.StretchToFill, true);
            }
            else if (!string.IsNullOrEmpty(previewError))
                GUI.Label(rect, previewError, EditorStyles.centeredGreyMiniLabel);

            HandlePreviewInput(rect);
            EditorGUILayout.LabelField(
                "Left drag: rotate  |  Right/Middle drag: move region  |  Wheel: zoom",
                EditorStyles.centeredGreyMiniLabel);
        }

        private void HandlePreviewInput(Rect rect)
        {
            var currentEvent = Event.current;
            if (!rect.Contains(currentEvent.mousePosition))
                return;

            if (currentEvent.type == EventType.MouseDrag && currentEvent.button == 0)
            {
                var cameraRotation = GetCameraRotation();
                var horizontal = Quaternion.AngleAxis(
                    currentEvent.delta.x * 0.4f,
                    cameraRotation * Vector3.up);
                var vertical = Quaternion.AngleAxis(
                    currentEvent.delta.y * 0.4f,
                    cameraRotation * Vector3.right);
                objectRotation = (vertical * horizontal * Quaternion.Euler(objectRotation)).eulerAngles;
            }
            else if (currentEvent.type == EventType.MouseDrag &&
                     (currentEvent.button == 1 || currentEvent.button == 2))
            {
                var scale = 2f / (rect.height * zoom);
                framingOffset.x -= currentEvent.delta.x * scale;
                framingOffset.y += currentEvent.delta.y * scale;
            }
            else if (currentEvent.type == EventType.ScrollWheel)
            {
                zoom = Mathf.Clamp(zoom * Mathf.Pow(1.1f, -currentEvent.delta.y), 0.1f, 20f);
            }
            else
            {
                return;
            }

            previewDirty = true;
            currentEvent.Use();
            Repaint();
        }

        private void RefreshPreview()
        {
            previewDirty = false;
            previewError = null;
            if (previewImage != null)
                DestroyImmediate(previewImage);
            previewImage = null;

            if (target == null)
                return;

            try
            {
                var aspect = (float)resolution.x / resolution.y;
                var previewResolution = aspect >= 1f
                    ? new Vector2Int(512, Mathf.Max(16, Mathf.RoundToInt(512f / aspect)))
                    : new Vector2Int(Mathf.Max(16, Mathf.RoundToInt(512f * aspect)), 512);
                previewImage = RenderImage(previewResolution);
            }
            catch (Exception exception)
            {
                previewError = exception.Message;
            }
        }

        private void Capture()
        {
            try
            {
                var assetPath = CreateAssetPath();
                var image = RenderImage(resolution);
                try
                {
                    File.WriteAllBytes(ToAbsolutePath(assetPath), image.EncodeToPNG());
                }
                finally
                {
                    DestroyImmediate(image);
                }
                AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);
                Selection.activeObject = AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
                EditorGUIUtility.PingObject(Selection.activeObject);
                Debug.Log($"[Object Screenshot] Saved: {assetPath}", target);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                EditorUtility.DisplayDialog("Object Screenshot", exception.Message, "OK");
            }
        }

        private Texture2D GetCheckerTexture()
        {
            if (checkerTexture == null)
            {
                checkerTexture = new Texture2D(2, 2, TextureFormat.RGBA32, false)
                {
                    hideFlags = HideFlags.HideAndDontSave,
                    wrapMode = TextureWrapMode.Repeat,
                    filterMode = FilterMode.Point
                };
                var light = new Color32(200, 200, 200, 255);
                var dark = new Color32(120, 120, 120, 255);
                checkerTexture.SetPixels32(new[] { light, dark, dark, light });
                checkerTexture.Apply();
            }
            return checkerTexture;
        }

        private Texture2D RenderImage(Vector2Int outputResolution)
        {
            var preview = new PreviewRenderUtility();
            var previewBegun = false;

            try
            {
                var clone = Instantiate(target);
                clone.name = target.name;
                clone.hideFlags = HideFlags.HideAndDontSave;
                clone.transform.SetPositionAndRotation(Vector3.zero, Quaternion.Euler(objectRotation));
                clone.transform.localScale = target.transform.lossyScale;
                preview.AddSingleGO(clone);

                var bounds = GetRendererBounds(clone);
                var cameraRotation = GetCameraRotation();
                var viewDirection = cameraRotation * Vector3.forward;
                var camera = preview.camera;
                var depth = ProjectedExtent(bounds.extents, viewDirection);
                var baseSize = CalculateOrthographicSize(bounds, cameraRotation, outputResolution);
                var cameraCenter = bounds.center +
                                   cameraRotation * Vector3.right * (framingOffset.x * baseSize) +
                                   cameraRotation * Vector3.up * (framingOffset.y * baseSize);

                camera.transform.SetPositionAndRotation(
                    cameraCenter - viewDirection * (depth + Mathf.Max(bounds.size.magnitude, 1f)),
                    cameraRotation);
                camera.orthographic = true;
                camera.orthographicSize = baseSize / zoom;
                camera.nearClipPlane = 0.01f;
                camera.farClipPlane = Mathf.Max(100f, depth * 4f + bounds.size.magnitude + 10f);
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = GetClearColor();
                camera.allowHDR = false;

                preview.ambientColor = new Color(0.35f, 0.35f, 0.35f);
                preview.lights[0].intensity = 1.2f;
                preview.lights[0].transform.rotation = Quaternion.Euler(35f, 35f, 0f);
                preview.lights[1].intensity = 0.8f;

                // EndStaticPreview() returns an RGB24 texture, which drops alpha.
                // Read the RGBA render texture ourselves so a transparent background survives.
                preview.BeginPreview(new Rect(0f, 0f, outputResolution.x, outputResolution.y), GUIStyle.none);
                previewBegun = true;
                preview.Render(true, false);
                previewBegun = false;
                return ReadImage(preview.EndPreview(), outputResolution, background.a < 1f);
            }
            finally
            {
                if (previewBegun)
                    preview.EndPreview();
                preview.Cleanup();
            }
        }

        // With a transparent background the clear color is premultiplied by its alpha, so anti-aliased
        // edges blend toward "nothing" instead of toward the background RGB (no dark/colored fringe).
        // FinalizePixels() converts the premultiplied result back to straight alpha.
        private Color GetClearColor()
        {
            if (background.a >= 1f)
                return background;

            var linear = QualitySettings.activeColorSpace == ColorSpace.Linear;
            var color = linear ? background.linear : background;
            color = new Color(color.r * background.a, color.g * background.a, color.b * background.a, background.a);
            return linear ? color.gamma : color;
        }

        private static Texture2D ReadImage(Texture rendered, Vector2Int size, bool transparent)
        {
            // The preview render texture stores linear values (no sRGB flag) and may be larger than
            // the requested size on high-DPI displays, so blit it down to an exact-size copy first.
            var temp = RenderTexture.GetTemporary(
                size.x, size.y, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            var previousActive = RenderTexture.active;
            var image = new Texture2D(size.x, size.y, TextureFormat.RGBA32, false, false);
            try
            {
                Graphics.Blit(rendered, temp);
                RenderTexture.active = temp;
                image.ReadPixels(new Rect(0f, 0f, size.x, size.y), 0, 0);
                FinalizePixels(image, transparent);
                return image;
            }
            catch
            {
                DestroyImmediate(image);
                throw;
            }
            finally
            {
                RenderTexture.active = previousActive;
                RenderTexture.ReleaseTemporary(temp);
            }
        }

        private static void FinalizePixels(Texture2D image, bool transparent)
        {
            var toGamma = QualitySettings.activeColorSpace == ColorSpace.Linear ? LinearToGammaLut : null;
            var pixels = image.GetPixelData<Color32>(0);
            for (var i = 0; i < pixels.Length; i++)
            {
                var pixel = pixels[i];
                if (!transparent)
                {
                    pixel.a = 255;
                }
                else if (pixel.a > 0 && pixel.a < 255)
                {
                    var scale = 255f / pixel.a;
                    pixel.r = (byte)Mathf.Min(255, Mathf.RoundToInt(pixel.r * scale));
                    pixel.g = (byte)Mathf.Min(255, Mathf.RoundToInt(pixel.g * scale));
                    pixel.b = (byte)Mathf.Min(255, Mathf.RoundToInt(pixel.b * scale));
                }

                if (toGamma != null)
                {
                    pixel.r = toGamma[pixel.r];
                    pixel.g = toGamma[pixel.g];
                    pixel.b = toGamma[pixel.b];
                }

                pixels[i] = pixel;
            }
            image.Apply(false, false);
        }

        private static byte[] LinearToGammaLut
        {
            get
            {
                if (linearToGammaLut == null)
                {
                    linearToGammaLut = new byte[256];
                    for (var i = 0; i < 256; i++)
                        linearToGammaLut[i] = (byte)Mathf.RoundToInt(Mathf.LinearToGammaSpace(i / 255f) * 255f);
                }
                return linearToGammaLut;
            }
        }

        private Quaternion GetCameraRotation()
        {
            var viewDirection = Quaternion.Euler(cameraAngle.x, cameraAngle.y, 0f) * Vector3.forward;
            return Quaternion.LookRotation(viewDirection, Vector3.up);
        }

        private float CalculateOrthographicSize(
            Bounds bounds,
            Quaternion cameraRotation,
            Vector2Int outputResolution)
        {
            var verticalExtent = ProjectedExtent(bounds.extents, cameraRotation * Vector3.up);
            var horizontalExtent = ProjectedExtent(bounds.extents, cameraRotation * Vector3.right);
            var aspect = (float)outputResolution.x / outputResolution.y;
            var size = Mathf.Max(verticalExtent, horizontalExtent / aspect) / frameFill;
            if (size <= 0f || float.IsNaN(size) || float.IsInfinity(size))
                throw new InvalidOperationException("The target has invalid renderer bounds.");
            return size;
        }

        private static float ProjectedExtent(Vector3 extents, Vector3 axis)
        {
            axis = new Vector3(Mathf.Abs(axis.x), Mathf.Abs(axis.y), Mathf.Abs(axis.z));
            return Vector3.Dot(extents, axis);
        }

        private static Bounds GetRendererBounds(GameObject root)
        {
            var renderers = root.GetComponentsInChildren<Renderer>();
            var found = false;
            var bounds = new Bounds();
            foreach (var renderer in renderers)
            {
                if (!renderer.enabled || !renderer.gameObject.activeInHierarchy)
                    continue;
                if (!found)
                {
                    bounds = renderer.bounds;
                    found = true;
                }
                else
                {
                    bounds.Encapsulate(renderer.bounds);
                }
            }

            if (!found)
                throw new InvalidOperationException("The target has no enabled Renderer in its active hierarchy.");
            return bounds;
        }

        private string CreateAssetPath()
        {
            var folder = string.IsNullOrWhiteSpace(outputFolder)
                ? "Assets/Screenshots"
                : outputFolder.Trim().Replace('\\', '/').TrimEnd('/');
            if (folder != "Assets" && !folder.StartsWith("Assets/", StringComparison.Ordinal))
                throw new InvalidOperationException("Output Folder must be an Assets-relative path.");

            var absoluteFolder = ToAbsolutePath(folder);
            var assetsFolder = Path.GetFullPath(Application.dataPath);
            if (absoluteFolder != assetsFolder &&
                !absoluteFolder.StartsWith(assetsFolder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Output Folder must be inside Assets.");

            Directory.CreateDirectory(absoluteFolder);
            outputFolder = folder;
            return AssetDatabase.GenerateUniqueAssetPath($"{folder}/{SanitizeFileName(target.name)}.png");
        }

        private static string ToAbsolutePath(string projectRelativePath)
        {
            var projectRoot = Directory.GetParent(Application.dataPath)?.FullName
                ?? throw new InvalidOperationException("Could not find the Unity project root.");
            return Path.GetFullPath(Path.Combine(projectRoot, projectRelativePath));
        }

        private static string SanitizeFileName(string value)
        {
            foreach (var character in Path.GetInvalidFileNameChars())
                value = value.Replace(character, '_');
            return string.IsNullOrWhiteSpace(value) ? "Object" : value;
        }
    }
}
