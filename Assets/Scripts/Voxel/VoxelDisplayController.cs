using System;
using UnityEngine;

namespace VoxelGeoVR
{
    /// <summary>
    /// 地形显示交互（阶段 4）：
    /// A 相机半径内 4x4 Bayer 点阵虚化
    /// B 任意方向剖面 + 第二相机展示框
    /// C 按方块 id 的 128-bit 隐藏掩码筛选
    ///
    /// 三条功能全部只改 shader 全局 uniform / 共享材质，不重建 chunk 网格。
    /// UI 使用 IMGUI，与 VoxelWorld.OnGUI / OnScreenControls 保持一致。
    /// </summary>
    [DisallowMultipleComponent]
    public class VoxelDisplayController : MonoBehaviour
    {
        [Header("引用")]
        public VoxelWorld world;
        public Camera sourceCamera;
        public Material baseMaterial;
        public Material doubleSidedMaterial;

        [Header("A 半径虚化")]
        public bool fadeEnabled;
        [Range(2f, 40f)] public float fadeRadius = 12f;
        [Range(0f, 100f)] public float fadeTransparency = 60f;

        [Header("B 剖面")]
        public bool sectionEnabled;
        public int sectionAxis;              // 0=X, 1=Y, 2=Z, 3=任意方向
        public float sectionPosition;
        public float sectionYaw;
        public float sectionPitch;
        public float sectionOffset;

        [Header("C 筛选")]
        public bool filterEnabled;
        public int filterMode;                 // 0 = 隐藏选中，1 = 仅显示选中

        // ------------------------------------------------------------------
        SectionViewport section;
        VoxelPalette palette;
        Bounds worldBounds = new Bounds(new Vector3(0f, 64f, 0f), new Vector3(512f, 384f, 512f));
        bool boundsReady;

        bool sectionFocusReady;
        Vector3 sectionFocus;
        Vector3 planePoint;
        Vector3 planeNormal = Vector3.right;
        float planeD;

        int activePanel = -1;                 // -1 无，0=A，1=B，2=C
        Vector2 filterScroll;
        string filterSearch = "";
        readonly bool[] selected = new bool[128];
        bool filterDirty;
        bool doubleSidedRuntime;
        float sectionZoomCache = 3f;

        GUIStyle labelStyle, titleStyle, smallStyle, buttonStyle, tabStyle;
        int cachedFont;

        static readonly string[] AxisNames = { "X", "Y", "Z", "任意" };

        // ------------------------------------------------------------------ 生命周期
        void Awake()
        {
            Bind(GetComponent<VoxelWorld>());
        }

        void Start()
        {
            Resolve();
        }

        public void Bind(VoxelWorld w)
        {
            if (w == null) return;
            world = w;
            if (baseMaterial == null) baseMaterial = w.voxelMaterial;
            if (sourceCamera == null) sourceCamera = FindCamera(w.view);
        }

        void Resolve()
        {
            if (world == null) world = GetComponent<VoxelWorld>();
            if (world == null) return;
            if (baseMaterial == null) baseMaterial = world.voxelMaterial;
            if (sourceCamera == null) sourceCamera = FindCamera(world.view != null ? world.view : (Camera.main != null ? Camera.main.transform : null));
            if (palette == null) palette = world.Palette;
            if (!boundsReady) RefreshBounds();
        }

        static Camera FindCamera(Transform t)
        {
            if (t == null) return null;
            var cam = t.GetComponent<Camera>();
            if (cam != null) return cam;
            return t.GetComponentInChildren<Camera>();
        }

        void LateUpdate()
        {
            Resolve();
            if (world == null) return;
            if (sectionEnabled && !sectionFocusReady) CaptureSectionFocus();

            ApplyPlane();
            PushGlobals();
            UpdateEnvelope();
            UpdateSectionViewport();
            ApplyMaterialMode();
        }

        void OnDisable()
        {
            if (section != null) section.SetActive(false);
            if (world != null && baseMaterial != null) world.SetSharedMaterial(baseMaterial);
        }

        void OnDestroy()
        {
            if (section != null) section.SetActive(false);
            if (doubleSidedRuntime && doubleSidedMaterial != null) Destroy(doubleSidedMaterial);
        }

        // ------------------------------------------------------------------ 数据/包围盒
        void RefreshBounds()
        {
            if (world == null || world.Manifest == null || world.Manifest.bounds == null) return;
            var b = world.Manifest.bounds;
            if (b.blockX == null || b.blockX.Length < 2 || b.blockZ == null || b.blockZ.Length < 2) return;
            float y0 = -64f, y1 = 319f;
            if (b.blockY != null && b.blockY.Length >= 2) { y0 = b.blockY[0]; y1 = b.blockY[1]; }
            Vector3 min = new Vector3(b.blockX[0], y0, b.blockZ[0]);
            Vector3 max = new Vector3(b.blockX[1], y1, b.blockZ[1]);
            worldBounds = new Bounds((min + max) * 0.5f, max - min);
            boundsReady = true;
        }

        Vector2 AxisRange(int axis)
        {
            if (axis == 0) return new Vector2(worldBounds.min.x, worldBounds.max.x);
            if (axis == 1) return new Vector2(worldBounds.min.y, worldBounds.max.y);
            return new Vector2(worldBounds.min.z, worldBounds.max.z);
        }

        float AxisCoord(int axis, Vector3 p)
        {
            return axis == 0 ? p.x : (axis == 1 ? p.y : p.z);
        }

        void CaptureSectionFocus()
        {
            sectionFocus = sourceCamera != null ? sourceCamera.transform.position : worldBounds.center;
            sectionFocusReady = true;
            if (sectionAxis < 3) sectionPosition = AxisCoord(sectionAxis, sectionFocus);
        }

        // ------------------------------------------------------------------ 平面 / uniform
        Vector3 NormalFromAngles()
        {
            float yaw = sectionYaw * Mathf.Deg2Rad;
            float pitch = sectionPitch * Mathf.Deg2Rad;
            float cp = Mathf.Cos(pitch);
            return new Vector3(cp * Mathf.Sin(yaw), Mathf.Sin(pitch), cp * Mathf.Cos(yaw)).normalized;
        }

        void ApplyPlane()
        {
            Vector3 focus = sectionFocusReady
                ? sectionFocus
                : (sourceCamera != null ? sourceCamera.transform.position : worldBounds.center);

            Vector3 n;
            Vector3 p;
            if (sectionAxis == 0)
            {
                n = Vector3.right;
                p = new Vector3(sectionPosition, focus.y, focus.z);
            }
            else if (sectionAxis == 1)
            {
                n = Vector3.up;
                p = new Vector3(focus.x, sectionPosition, focus.z);
            }
            else if (sectionAxis == 2)
            {
                n = Vector3.forward;
                p = new Vector3(focus.x, focus.y, sectionPosition);
            }
            else
            {
                n = NormalFromAngles();
                p = focus + n * sectionOffset;
            }

            n = n.normalized;
            float d = -Vector3.Dot(p, n);

            // 主相机永远留在"保留侧"；只有明显越界才翻面，避免在切面附近来回闪烁。
            if (sectionEnabled && sourceCamera != null)
            {
                float side = Vector3.Dot(sourceCamera.transform.position, n) + d;
                if (side < -0.4f)
                {
                    n = -n;
                    d = -d;
                }
            }

            planeNormal = n;
            planeD = d;
            planePoint = p;
        }

        void PushGlobals()
        {
            Shader.SetGlobalFloat("_SectionEnabled", sectionEnabled ? 1f : 0f);
            Shader.SetGlobalVector("_SectionNormal", new Vector4(planeNormal.x, planeNormal.y, planeNormal.z, 0f));
            Shader.SetGlobalFloat("_SectionD", planeD);

            Shader.SetGlobalFloat("_FadeEnabled", fadeEnabled ? 1f : 0f);
            Shader.SetGlobalFloat("_FadeRadius", Mathf.Max(0.01f, fadeRadius));
            Shader.SetGlobalFloat("_FadeStrength", Mathf.Clamp01(fadeTransparency / 100f));
            Shader.SetGlobalFloat("_FadeSoftness", Mathf.Max(0.75f, fadeRadius * 0.2f));
            Vector3 c = sourceCamera != null ? sourceCamera.transform.position : Vector3.zero;
            Shader.SetGlobalVector("_FadeCenter", new Vector4(c.x, c.y, c.z, 0f));
        }

        void UpdateEnvelope()
        {
            if (world == null) return;
            Vector3 c = sourceCamera != null ? sourceCamera.transform.position : Vector3.zero;
            world.SetEnvelope(fadeEnabled, c, Mathf.Max(0.01f, fadeRadius));
        }

        // ------------------------------------------------------------------ B：展示框
        void EnsureSectionViewport()
        {
            if (section != null) return;
            section = GetComponent<SectionViewport>();
            if (section == null) section = gameObject.AddComponent<SectionViewport>();
            if (section != null)
            {
                section.world = world;
                section.sourceCamera = sourceCamera;
                section.zoom = sectionZoomCache;
            }
        }

        void UpdateSectionViewport()
        {
            EnsureSectionViewport();
            if (section == null) return;

            section.world = world;
            section.sourceCamera = sourceCamera;
            section.worldBounds = worldBounds;
            section.planePoint = planePoint;
            section.planeNormal = planeNormal;

            section.SetActive(sectionEnabled && activePanel == 1);
            if (section.active) section.Sync();
        }

        void ApplyMaterialMode()
        {
            if (world == null) return;
            if (!sectionEnabled)
            {
                if (baseMaterial != null && world.voxelMaterial != baseMaterial)
                    world.SetSharedMaterial(baseMaterial);
                return;
            }
            var m = GetDoubleSidedMaterial();
            if (m != null && world.voxelMaterial != m)
                world.SetSharedMaterial(m);
        }

        Material GetDoubleSidedMaterial()
        {
            if (doubleSidedMaterial != null) return doubleSidedMaterial;
            if (baseMaterial == null && world != null) baseMaterial = world.voxelMaterial;
            if (baseMaterial == null) return null;
            doubleSidedMaterial = new Material(baseMaterial) { name = baseMaterial.name + "_DoubleSided_Runtime" };
            doubleSidedMaterial.SetFloat("_CullMode", 0f);
            doubleSidedRuntime = true;
            return doubleSidedMaterial;
        }

        void ToggleSection()
        {
            sectionEnabled = !sectionEnabled;
            if (sectionEnabled)
            {
                CaptureSectionFocus();
                activePanel = 1;
                EnsureSectionViewport();
                if (section != null)
                {
                    section.ResetView(sectionFocus, worldBounds);
                    section.world = world;
                    section.sourceCamera = sourceCamera;
                }
            }
            else if (section != null)
            {
                section.SetActive(false);
            }
            ApplyMaterialMode();
        }

        void SetAxis(int axis)
        {
            sectionAxis = Mathf.Clamp(axis, 0, 3);
            if (sectionAxis < 3)
            {
                Vector3 p = sectionFocusReady
                    ? sectionFocus
                    : (sourceCamera != null ? sourceCamera.transform.position : worldBounds.center);
                sectionPosition = AxisCoord(sectionAxis, p);
            }
        }

        void ResetSectionView()
        {
            CaptureSectionFocus();
            if (section != null)
            {
                section.pan = Vector2.zero;
                section.zoom = 3f;
            }
            if (sectionAxis >= 3)
            {
                sectionYaw = 0f;
                sectionPitch = 0f;
                sectionOffset = 0f;
            }
        }

        float SectionZoom
        {
            get { return section != null ? section.zoom : sectionZoomCache; }
            set
            {
                sectionZoomCache = Mathf.Clamp(value, 1f, 16f);
                if (section != null) section.zoom = sectionZoomCache;
            }
        }

        void DrawSectionViewport(float s)
        {
            EnsureSectionViewport();
            if (section == null) return;

            float viewSize = Mathf.Min(Screen.height * 0.40f, 420f * s);
            float vx = Screen.width - viewSize - 16f * s;
            float vy = 72f * s;
            section.Draw(new Rect(vx, vy, viewSize, viewSize), s);
        }

        // ------------------------------------------------------------------ C：筛选（CPU 重建封闭壳）
        bool IsSelected(int id)
        {
            return id >= 0 && id < selected.Length && selected[id];
        }

        void ToggleSelected(int id)
        {
            if (id < 0 || id >= selected.Length) return;
            selected[id] = !selected[id];
            filterDirty = true;
        }

        void ClearSelection()
        {
            Array.Clear(selected, 0, selected.Length);
        }

        int SelectedCount()
        {
            int n = 0;
            for (int i = 0; i < selected.Length; i++) if (selected[i]) n++;
            return n;
        }

        int RenderedKindCount()
        {
            if (palette == null || palette.solid == null) return 0;
            int n = 0;
            for (int i = 1; i < palette.solid.Length; i++) if (palette.solid[i]) n++;
            return n;
        }

        /// <summary>
        /// 构造"可见方块"表：可见集合的边界面会重建成封闭壳。
        /// 隐藏模式 = 所有 solid 去掉选中；仅显示模式 = 只保留选中。
        /// </summary>
        bool[] BuildVisibility()
        {
            if (palette == null || palette.solid == null) return null;
            int n = palette.colors != null ? palette.colors.Length : palette.solid.Length;
            var visible = new bool[n];
            for (int id = 0; id < n; id++)
            {
                if (id >= palette.solid.Length || !palette.solid[id]) continue;
                if (!filterEnabled) visible[id] = true;
                else if (filterMode == 0) visible[id] = !IsSelected(id);
                else visible[id] = IsSelected(id);
            }
            return visible;
        }

        void ApplyFilter()
        {
            if (world == null) return;
            if (palette == null) return;

            int picked = SelectedCount();
            if (filterMode == 1 && picked == 0) return;
            filterEnabled = picked > 0;
            filterDirty = false;

            var visible = BuildVisibility();
            world.RebuildVisibility(visible);
        }

        void ShowAll()
        {
            ClearSelection();
            filterEnabled = false;
            filterDirty = false;
            if (world != null) world.RebuildVisibility(null);
        }

        // ------------------------------------------------------------------ IMGUI
        public void DrawHud()
        {
            Resolve();
            float s = UiScale();
            EnsureStyles(s);

            DrawTabs(s);
            if (sectionEnabled && activePanel == 1) DrawSectionViewport(s);

            if (activePanel == 0) DrawFadePanel(s);
            else if (activePanel == 1) DrawSectionPanel(s);
            else if (activePanel == 2) DrawFilterPanel(s);
        }

        static float UiScale()
        {
            float s = Mathf.Min(Screen.width / 1280f, Screen.height / 720f);
            return Mathf.Clamp(s, 0.75f, 2.2f);
        }

        void EnsureStyles(float s)
        {
            int font = Mathf.RoundToInt(15f * s);
            if (labelStyle != null && cachedFont == font) return;

            cachedFont = font;
            labelStyle = new GUIStyle(GUI.skin.label) { fontSize = font };
            labelStyle.normal.textColor = Color.white;

            titleStyle = new GUIStyle(GUI.skin.label) { fontSize = font + 3, fontStyle = FontStyle.Bold };
            titleStyle.normal.textColor = Color.white;

            smallStyle = new GUIStyle(GUI.skin.label) { fontSize = Mathf.Max(11, font - 2) };
            smallStyle.normal.textColor = new Color(0.85f, 0.9f, 0.95f);

            buttonStyle = new GUIStyle(GUI.skin.button) { fontSize = font };
            tabStyle = new GUIStyle(GUI.skin.button) { fontSize = font, fontStyle = FontStyle.Bold };
        }

        void DrawTabs(float s)
        {
            string[] names = { "A 半径虚化", "B 剖面", "C 筛选" };
            float bw = 128f * s;
            float bh = 38f * s;
            float gap = 8f * s;
            float total = names.Length * bw + (names.Length - 1) * gap;
            float x = (Screen.width - total) * 0.5f;
            float y = 14f * s;

            for (int i = 0; i < names.Length; i++)
            {
                Color old = GUI.backgroundColor;
                bool on = activePanel == i;
                bool featureOn = (i == 0 && fadeEnabled) || (i == 1 && sectionEnabled) || (i == 2 && filterEnabled);
                if (on) GUI.backgroundColor = new Color(0.35f, 1f, 0.45f);
                else if (featureOn) GUI.backgroundColor = new Color(0.45f, 0.75f, 1f);
                if (GUI.Button(new Rect(x + i * (bw + gap), y, bw, bh), names[i], tabStyle))
                    activePanel = activePanel == i ? -1 : i;
                GUI.backgroundColor = old;
            }
        }

        Rect PanelRect(float s, float width, float height)
        {
            if (sectionEnabled && activePanel == 1)
            {
                float viewSize = Mathf.Min(Screen.height * 0.40f, 420f * s);
                float vx = Screen.width - viewSize - 16f * s;
                float px = vx - width - 12f * s;
                if (px < 16f * s) px = 16f * s;
                return new Rect(px, 72f * s, width, height);
            }
            return new Rect(Screen.width - width - 16f * s, 72f * s, width, height);
        }

        float PanelHeader(Rect panel, string title, float s)
        {
            GUI.Box(panel, GUIContent.none);
            GUI.Label(new Rect(panel.x + 14f * s, panel.y + 10f * s, panel.width - 60f * s, 28f * s), title, titleStyle);
            if (GUI.Button(new Rect(panel.xMax - 38f * s, panel.y + 8f * s, 28f * s, 28f * s), "×", buttonStyle))
                activePanel = -1;
            return panel.y + 46f * s;
        }

        bool DrawEnableButton(float x, float y, float w, float s, string onText, string offText, bool on)
        {
            Color old = GUI.backgroundColor;
            GUI.backgroundColor = on ? new Color(0.35f, 1f, 0.45f) : new Color(0.85f, 0.85f, 0.85f);
            bool clicked = GUI.Button(new Rect(x, y, w, 32f * s), on ? onText : offText, buttonStyle);
            GUI.backgroundColor = old;
            return clicked;
        }

        void DrawFadePanel(float s)
        {
            float pw = 420f * s;
            var panel = PanelRect(s, pw, 250f * s);
            float y = PanelHeader(panel, "A  相机半径虚化", s);
            float x = panel.x + 16f * s;
            float w = pw - 32f * s;

            if (DrawEnableButton(x, y, 150f * s, s, "已启用", "已关闭", fadeEnabled)) fadeEnabled = !fadeEnabled;
            y += 42f * s;

            fadeRadius = GUI.HorizontalSlider(new Rect(x, y, w, 22f * s), fadeRadius, 2f, 40f);
            GUI.Label(new Rect(x, y + 22f * s, w, 22f * s), "半径  " + fadeRadius.ToString("F1") + " 格", labelStyle);
            y += 54f * s;

            fadeTransparency = GUI.HorizontalSlider(new Rect(x, y, w, 22f * s), fadeTransparency, 0f, 100f);
            GUI.Label(new Rect(x, y + 22f * s, w, 22f * s), "透明程度  " + fadeTransparency.ToString("F0") + " %", labelStyle);
            y += 54f * s;

            GUI.Label(new Rect(x, y, w, 54f * s),
                "普通表面按距离虚化；半径边界生成不透明岩层包络；\n球内洞穴壁/水面仍按距离虚化，包络不受透明度影响。",
                smallStyle);
        }

        void DrawSectionPanel(float s)
        {
            float pw = 420f * s;
            var panel = PanelRect(s, pw, 440f * s);
            float y = PanelHeader(panel, "B  剖面 + 展示框", s);
            float x = panel.x + 16f * s;
            float w = pw - 32f * s;

            if (DrawEnableButton(x, y, 150f * s, s, "已启用", "已关闭", sectionEnabled)) ToggleSection();
            y += 42f * s;

            GUI.Label(new Rect(x, y, w, 22f * s), "切面轴", smallStyle);
            y += 24f * s;
            float bw = (w - 3f * 6f * s) / 4f;
            for (int i = 0; i < 4; i++)
            {
                Color old = GUI.backgroundColor;
                if (sectionAxis == i) GUI.backgroundColor = new Color(0.35f, 1f, 0.45f);
                if (GUI.Button(new Rect(x + i * (bw + 6f * s), y, bw, 30f * s), AxisNames[i], buttonStyle))
                    SetAxis(i);
                GUI.backgroundColor = old;
            }
            y += 40f * s;

            if (sectionAxis < 3)
            {
                Vector2 range = AxisRange(sectionAxis);
                sectionPosition = GUI.HorizontalSlider(new Rect(x, y, w, 22f * s), sectionPosition, range.x, range.y);
                sectionPosition = Mathf.Clamp(sectionPosition, range.x, range.y);
                GUI.Label(new Rect(x, y + 22f * s, w, 22f * s),
                    string.Format("{0} = {1:F1}   （范围 {2:F0} .. {3:F0}）", AxisNames[sectionAxis], sectionPosition, range.x, range.y),
                    labelStyle);
                y += 54f * s;
            }
            else
            {
                sectionYaw = GUI.HorizontalSlider(new Rect(x, y, w, 22f * s), sectionYaw, 0f, 360f);
                GUI.Label(new Rect(x, y + 22f * s, w, 20f * s), "法线偏航  " + sectionYaw.ToString("F0") + "°", smallStyle);
                y += 48f * s;

                sectionPitch = GUI.HorizontalSlider(new Rect(x, y, w, 22f * s), sectionPitch, -90f, 90f);
                GUI.Label(new Rect(x, y + 22f * s, w, 20f * s), "法线俯仰  " + sectionPitch.ToString("F0") + "°", smallStyle);
                y += 48f * s;

                sectionOffset = GUI.HorizontalSlider(new Rect(x, y, w, 22f * s), sectionOffset, -300f, 300f);
                GUI.Label(new Rect(x, y + 22f * s, w, 20f * s), "沿法线偏移  " + sectionOffset.ToString("F0"), smallStyle);
                y += 48f * s;
            }

            float z = SectionZoom;
            z = GUI.HorizontalSlider(new Rect(x, y, w, 22f * s), z, 1f, 16f);
            SectionZoom = z;
            GUI.Label(new Rect(x, y + 22f * s, w, 22f * s), "展示框缩放  " + SectionZoom.ToString("F1") + "x", labelStyle);
            y += 52f * s;

            if (GUI.Button(new Rect(x, y, 120f * s, 30f * s), "－", buttonStyle))
            {
                if (section != null) section.NudgeZoom(0.8f);
            }
            if (GUI.Button(new Rect(x + 126f * s, y, 120f * s, 30f * s), "＋", buttonStyle))
            {
                if (section != null) section.NudgeZoom(1.25f);
            }
            if (GUI.Button(new Rect(x + 252f * s, y, w - 252f * s, 30f * s), "对准当前", buttonStyle)) ResetSectionView();
            y += 40f * s;

            GUI.Label(new Rect(x, y, w, 48f * s),
                "展示框内拖动平移、滚轮缩放；主视图切面同样使用该平面。\n切面开启时自动切到双面材质，关闭后恢复单面。",
                smallStyle);
        }

        void DrawFilterPanel(float s)
        {
            float pw = 480f * s;
            float ph = Mathf.Min(620f * s, Screen.height - 150f * s);
            var panel = PanelRect(s, pw, ph);
            float y = PanelHeader(panel, "C  方块筛选（封闭壳重建）", s);
            float x = panel.x + 16f * s;
            float w = pw - 32f * s;

            GUI.Label(new Rect(x, y, w, 26f * s),
                filterEnabled ? (filterDirty ? "当前：筛选已应用（有未应用更改）" : "当前：筛选已应用") : "当前：全部显示", labelStyle);
            GUI.Label(new Rect(x + 170f * s, y + 3f * s, w - 170f * s, 24f * s),
                "已选 " + SelectedCount() + " 种 / 可渲染 " + RenderedKindCount(), smallStyle);
            y += 32f * s;

            float bw = (w - 8f * s) * 0.5f;
            Color old = GUI.backgroundColor;
            if (filterMode == 0) GUI.backgroundColor = new Color(1f, 0.55f, 0.5f);
            if (GUI.Button(new Rect(x, y, bw, 32f * s), "隐藏选中", buttonStyle)) { filterMode = 0; filterDirty = true; }
            GUI.backgroundColor = old;
            old = GUI.backgroundColor;
            if (filterMode == 1) GUI.backgroundColor = new Color(0.4f, 1f, 0.55f);
            if (GUI.Button(new Rect(x + bw + 8f * s, y, bw, 32f * s), "仅显示选中", buttonStyle)) { filterMode = 1; filterDirty = true; }
            GUI.backgroundColor = old;
            y += 40f * s;

            string ns = GUI.TextField(new Rect(x, y, w, 30f * s), filterSearch);
            if (ns != filterSearch)
            {
                filterSearch = ns;
                filterScroll.y = 0f;
            }
            y += 38f * s;

            float footer = 92f * s;
            float listH = Mathf.Max(100f * s, panel.yMax - y - footer);
            if (palette == null || palette.names == null)
            {
                GUI.Label(new Rect(x, y, w, listH), "等待 blocks.json ...", labelStyle);
            }
            else
            {
                DrawBlockList(x, y, w, listH, s);
            }

            float by = panel.yMax - 82f * s;
            bool canApply = !(filterMode == 1 && SelectedCount() == 0);
            bool oldEnabled = GUI.enabled;
            GUI.enabled = canApply && !(world != null && world.FilterRebuilding);
            if (GUI.Button(new Rect(x, by, 150f * s, 34f * s), "应用筛选（重建）", buttonStyle))
                ApplyFilter();
            GUI.enabled = oldEnabled;

            if (GUI.Button(new Rect(x + 158f * s, by, 120f * s, 34f * s), "全部显示", buttonStyle))
                ShowAll();

            string status = world != null ? world.FilterStatus : "";
            if (world != null && world.FilterRebuilding)
                status = "重建中 " + world.FilterRebuildDone + "/" + world.FilterRebuildTotal;
            GUI.Label(new Rect(x + 286f * s, by + 4f * s, w - 286f * s, 28f * s), status, smallStyle);

            if (GUI.Button(new Rect(x, by + 40f * s, 90f * s, 28f * s), "上一页", buttonStyle))
                filterScroll.y = Mathf.Max(0f, filterScroll.y - listH * 0.85f);
            if (GUI.Button(new Rect(x + 98f * s, by + 40f * s, 90f * s, 28f * s), "下一页", buttonStyle))
                filterScroll.y += listH * 0.85f;
            GUI.Label(new Rect(x + 198f * s, by + 42f * s, w - 198f * s, 24f * s),
                "只显示选中：每种方块会重建成完整封闭壳。", smallStyle);
        }

        void DrawBlockList(float x, float y, float w, float h, float s)
        {
            const float rowH = 30f;
            float contentH = palette.names.Length * rowH * s + 8f * s;
            var view = new Rect(x, y, w, h);
            var content = new Rect(0f, 0f, w - 22f * s, contentH);
            filterScroll = GUI.BeginScrollView(view, filterScroll, content);

            float ry = 4f * s;
            for (int id = 0; id < palette.names.Length; id++)
            {
                string name = palette.names[id];
                if (string.IsNullOrEmpty(name)) continue;
                if (filterSearch.Length > 0 &&
                    name.IndexOf(filterSearch, StringComparison.OrdinalIgnoreCase) < 0 &&
                    id.ToString().IndexOf(filterSearch, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                bool solid = palette.solid != null && id < palette.solid.Length && palette.solid[id];
                bool picked = IsSelected(id);
                string text = id + "  " + name;
                if (!solid) text += "   (未渲染)";
                else if (picked) text += "   [选中]";

                bool oldEnabled = GUI.enabled;
                Color oldColor = GUI.backgroundColor;
                GUI.enabled = solid;
                if (picked)
                    GUI.backgroundColor = filterMode == 0
                        ? new Color(1f, 0.55f, 0.5f)
                        : new Color(0.4f, 1f, 0.55f);
                if (GUI.Button(new Rect(0f, ry, content.width, 26f * s), text, buttonStyle)) ToggleSelected(id);
                GUI.backgroundColor = oldColor;
                GUI.enabled = oldEnabled;

                ry += rowH * s;
            }

            GUI.EndScrollView();
        }
    }
}
