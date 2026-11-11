using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace VoxelGeoVR
{
    /// <summary>
    /// B 功能的展示框：第二台正交相机 + RenderTexture，不进入主相机堆栈。
    /// 相机始终放在剖切平面的"被切掉"一侧，正对被切出的剖面。
    /// </summary>
    [DisallowMultipleComponent]
    public class SectionViewport : MonoBehaviour
    {
        public VoxelWorld world;
        public Camera sourceCamera;

        public bool active;
        public float zoom = 3f;
        public Vector2 pan;

        public Vector3 planePoint;
        public Vector3 planeNormal = Vector3.right;
        public Bounds worldBounds = new Bounds(new Vector3(0f, 64f, 0f), new Vector3(512f, 384f, 512f));

        public RenderTexture ViewTexture { get; private set; }
        public Camera SectionCamera { get; private set; }
        public Rect ViewportRect { get; private set; }
        public Vector3 ViewCenter { get; private set; }
        public Vector3 ViewRight { get; private set; }
        public Vector3 ViewUp { get; private set; }
        public float ViewHalfSize { get; private set; } = 32f;

        const int RtSize = 512;
        const float CameraDistance = 8f;
        const float BaseOrthoSize = 64f;

        GameObject cameraGo;
        bool dragging;
        GUIStyle overlayStyle;
        int overlayFont;

        void OnEnable()
        {
            RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
            RenderPipelineManager.endCameraRendering += OnEndCameraRendering;
        }

        void OnDisable()
        {
            RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
            RenderPipelineManager.endCameraRendering -= OnEndCameraRendering;
            active = false;
            if (SectionCamera != null) SectionCamera.enabled = false;
            ReleaseTexture();
        }

        void OnBeginCameraRendering(ScriptableRenderContext context, Camera cam)
        {
            if (active && cam == SectionCamera)
                Shader.SetGlobalFloat("_SectionEnabled", 1f);
        }

        void OnEndCameraRendering(ScriptableRenderContext context, Camera cam)
        {
            if (cam == SectionCamera)
                Shader.SetGlobalFloat("_SectionEnabled", 0f);
        }

        public void SetActive(bool on)
        {
            if (active == on)
            {
                if (on) EnsureCamera();
                return;
            }
            active = on;
            if (on)
            {
                EnsureCamera();
            }
            else
            {
                if (SectionCamera != null) SectionCamera.enabled = false;
                ReleaseTexture();
            }
        }

        public void ResetView(Vector3 focus, Bounds bounds)
        {
            worldBounds = bounds;
            planePoint = focus;
            pan = Vector2.zero;
            zoom = 3f;
        }

        public void NudgeZoom(float factor)
        {
            zoom = Mathf.Clamp(zoom * factor, 1f, 16f);
        }

        /// <summary>由 VoxelDisplayController.LateUpdate 调用，保证相机在渲染前就位。</summary>
        public void Sync()
        {
            if (!active) return;
            EnsureCamera();
            if (SectionCamera == null) return;

            EnsureTexture();
            if (ViewTexture != null && SectionCamera.targetTexture != ViewTexture)
                SectionCamera.targetTexture = ViewTexture;
            SectionCamera.enabled = true;

            Vector3 n = planeNormal.sqrMagnitude > 1e-8f ? planeNormal.normalized : Vector3.right;
            Vector3 upHint = Mathf.Abs(n.y) > 0.95f ? Vector3.forward : Vector3.up;
            Vector3 right = Vector3.Cross(upHint, n).normalized;
            Vector3 up = Vector3.Cross(n, right).normalized;

            ClampPan(right, up);
            Vector3 center = planePoint + right * pan.x + up * pan.y;
            ViewCenter = center;
            ViewRight = right;
            ViewUp = up;

            SectionCamera.transform.SetPositionAndRotation(
                center - n * CameraDistance,
                Quaternion.LookRotation(n, up));
            SectionCamera.orthographicSize = BaseOrthoSize / Mathf.Clamp(zoom, 1f, 16f);
            ViewHalfSize = SectionCamera.orthographicSize * 1.15f;
            if (sourceCamera != null) SectionCamera.backgroundColor = sourceCamera.backgroundColor;
        }

        public void Draw(Rect rect, float uiScale)
        {
            ViewportRect = rect;

            GUI.Box(new Rect(rect.x - 2f, rect.y - 2f, rect.width + 4f, rect.height + 4f), GUIContent.none);
            if (ViewTexture != null && ViewTexture.IsCreated())
                GUI.DrawTexture(rect, ViewTexture, ScaleMode.ScaleAndCrop);
            else
                GUI.Label(rect, "剖面相机未就绪");

            int font = Mathf.RoundToInt(14f * uiScale);
            if (overlayStyle == null || overlayFont != font)
            {
                overlayStyle = new GUIStyle(GUI.skin.label) { fontSize = font };
                overlayStyle.normal.textColor = Color.white;
                overlayFont = font;
            }
            GUI.Label(new Rect(rect.x + 8f * uiScale, rect.y + 6f * uiScale, rect.width - 16f * uiScale, 24f * uiScale),
                string.Format("剖面视口  {0:F1}x  拖动平移 / 滚轮缩放", zoom), overlayStyle);

            HandleInput(rect);
        }

        void HandleInput(Rect rect)
        {
            Event e = Event.current;
            if (e == null) return;

            if (e.type == EventType.MouseDown && rect.Contains(e.mousePosition))
            {
                dragging = true;
                e.Use();
            }
            else if (dragging && e.type == EventType.MouseDrag)
            {
                float worldPerPixel = (SectionCamera != null ? SectionCamera.orthographicSize * 2f : 64f)
                                      / Mathf.Max(rect.height, 1f);
                pan.x -= e.delta.x * worldPerPixel;
                pan.y += e.delta.y * worldPerPixel;
                e.Use();
            }
            else if (dragging && e.type == EventType.MouseUp)
            {
                dragging = false;
                e.Use();
            }

            if (rect.Contains(e.mousePosition) && e.type == EventType.ScrollWheel)
            {
                NudgeZoom(1f + Input.mouseScrollDelta.y * 0.12f);
                e.Use();
            }
        }

        void ClampPan(Vector3 right, Vector3 up)
        {
            Vector3 center = planePoint + right * pan.x + up * pan.y;
            Vector3 min = worldBounds.min;
            Vector3 max = worldBounds.max;
            const float margin = 8f;
            Vector3 clamped = new Vector3(
                Mathf.Clamp(center.x, min.x - margin, max.x + margin),
                Mathf.Clamp(center.y, min.y - margin, max.y + margin),
                Mathf.Clamp(center.z, min.z - margin, max.z + margin));

            Vector3 delta = clamped - center;
            pan.x += Vector3.Dot(delta, right);
            pan.y += Vector3.Dot(delta, up);
        }

        void EnsureCamera()
        {
            if (SectionCamera != null)
            {
                if (active) SectionCamera.enabled = true;
                return;
            }

            cameraGo = new GameObject("Section Camera");
            cameraGo.transform.SetParent(transform, false);

            var cam = cameraGo.AddComponent<Camera>();
            cam.orthographic = true;
            cam.nearClipPlane = 0.1f;
            cam.farClipPlane = 800f;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = sourceCamera != null ? sourceCamera.backgroundColor : new Color(0.53f, 0.72f, 0.91f);
            cam.cullingMask = (1 << 0) | (1 << VoxelWorld.SectionCapLayer);
            cam.allowHDR = false;
            cam.allowMSAA = false;
            cam.useOcclusionCulling = false;
            cam.depth = -100f;
            cam.enabled = false;
            cameraGo.AddComponent<SectionCameraClip>();

            var data = cameraGo.AddComponent<UniversalAdditionalCameraData>();
            data.renderPostProcessing = false;
            data.antialiasing = AntialiasingMode.None;
            data.renderShadows = false;

            SectionCamera = cam;
            if (active)
            {
                EnsureTexture();
                cam.targetTexture = ViewTexture;
                cam.enabled = true;
            }
        }

        void EnsureTexture()
        {
            if (ViewTexture != null && ViewTexture.IsCreated()) return;
            ReleaseTexture();

            ViewTexture = new RenderTexture(RtSize, RtSize, 24, RenderTextureFormat.ARGB32)
            {
                name = "SectionViewRT",
                antiAliasing = 1,
                useMipMap = false,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };
            ViewTexture.Create();
        }

        void ReleaseTexture()
        {
            if (SectionCamera != null) SectionCamera.targetTexture = null;
            if (ViewTexture != null)
            {
                if (RenderTexture.active == ViewTexture) RenderTexture.active = null;
                ViewTexture.Release();
                Destroy(ViewTexture);
                ViewTexture = null;
            }
        }

        void OnDestroy()
        {
            RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
            RenderPipelineManager.endCameraRendering -= OnEndCameraRendering;
            ReleaseTexture();
            if (cameraGo != null) Destroy(cameraGo);
        }
    }
}
