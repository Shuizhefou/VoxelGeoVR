using UnityEngine;

namespace VoxelGeoVR
{
    /// <summary>
    /// 屏幕按钮：顶部模式切换 + 左下 WASD + 右下升降/转身；陀螺仪调试面板（可选）。
    /// 监听 MouseDown/MouseUp 自己维护按下状态，不依赖 EventSystem。
    /// </summary>
    public class OnScreenControls : MonoBehaviour
    {
        public CameraRig rig;
        public VirtualInput virtualInput;
        public int mode = 1;

        private void Start()
        {
            if (rig == null) rig = FindFirstObjectByType<CameraRig>();
            if (rig != null) rig.SetMode(mode);
        }

        private void OnGUI()
        {
            if (rig == null) return;

            float s = Mathf.Min(Screen.width / 1280f, Screen.height / 720f);
            if (s < 0.6f) s = 0.6f;
            if (s > 2.5f) s = 2.5f;

            // ---------- 顶部模式按钮 ----------
            float bw = 100f * s;
            float bh = 40f * s;
            string[] names = { "PC", "按钮", "陀螺仪" };
            for (int i = 0; i < names.Length; i++)
            {
                var r = new Rect(16f * s + i * (bw + 8f * s), 16f * s, bw, bh);
                Color old = GUI.backgroundColor;
                if (rig.mode == i) GUI.backgroundColor = new Color(0.35f, 1f, 0.45f);
                if (GUI.Button(r, names[i]))
                {
                    mode = i;
                    rig.SetMode(i);
                }
                GUI.backgroundColor = old;
            }

            var bDebug = new Rect(16f * s + 3 * (bw + 8f * s), 16f * s, bw, bh);
            {
                Color old = GUI.backgroundColor;
                if (rig.gyroDebug) GUI.backgroundColor = new Color(0.35f, 1f, 0.45f);
                if (GUI.Button(bDebug, "调试")) rig.gyroDebug = !rig.gyroDebug;
                GUI.backgroundColor = old;
            }

            GUI.Label(new Rect(16f * s, 62f * s, 620f * s, 22f * s),
                "mode " + rig.mode + "  |  PC: WASD move, Q/E up-down, mouse look");

            // ---------- 左下 WASD ----------
            float bs = 84f * s;
            var w = new Rect(120f * s, Screen.height - 260f * s, bs, bs);
            var a = new Rect(28f * s, Screen.height - 168f * s, bs, bs);
            var sKey = new Rect(120f * s, Screen.height - 168f * s, bs, bs);
            var d = new Rect(212f * s, Screen.height - 168f * s, bs, bs);

            DrawHold(w, "W", ref flagW);
            DrawHold(a, "A", ref flagA);
            DrawHold(sKey, "S", ref flagS);
            DrawHold(d, "D", ref flagD);

            // ---------- 右下 升降/转身 ----------
            float bw2 = 92f * s;
            float bh2 = 46f * s;
            var turnL = new Rect(Screen.width - 2 * (bw2 + 8f * s) - 16f * s, Screen.height - 140f * s, bw2, bh2);
            var turnR = new Rect(Screen.width - (bw2 + 16f * s), Screen.height - 140f * s, bw2, bh2);
            var upE = new Rect(Screen.width - 2 * (bw2 + 8f * s) - 16f * s, Screen.height - 88f * s, bw2, bh2);
            var dnQ = new Rect(Screen.width - (bw2 + 16f * s), Screen.height - 88f * s, bw2, bh2);

            DrawHold(turnL, "turn L", ref flagTurnL);
            DrawHold(turnR, "turn R", ref flagTurnR);
            DrawHold(upE, "up E", ref flagUp);
            DrawHold(dnQ, "down Q", ref flagDown);

            if (virtualInput != null)
            {
                virtualInput.forward = flagW;
                virtualInput.back = flagS;
                virtualInput.left = flagA;
                virtualInput.right = flagD;
                virtualInput.up = flagUp;
                virtualInput.down = flagDown;
                virtualInput.turnLeft = flagTurnL;
                virtualInput.turnRight = flagTurnR;
            }

            // ---------- 陀螺仪调试面板 ----------
            if (rig.gyroDebug)
            {
                float pw = 470f * s;
                float ph = 380f * s;
                var panel = new Rect(Screen.width - pw - 16f * s, 16f * s, pw, ph);
                GUI.Box(panel, "");
                GUI.Label(new Rect(panel.x + 12f * s, panel.y + 8f * s, pw - 24f * s, 126f * s), rig.DebugText);

                var bCal = new Rect(panel.x + 12f * s, panel.y + 140f * s, 110f * s, 38f * s);
                if (GUI.Button(bCal, "calib")) rig.CalibrateGyro();

                GUI.Label(new Rect(panel.x + 132f * s, panel.y + 140f * s, 330f * s, 38f * s),
                    "calibrate, then pick the 1:1 combo");

                for (int i = 0; i < 16; i++)
                {
                    int col = i % 4;
                    int row = i / 4;
                    var br = new Rect(
                        panel.x + 12f * s + col * 110f * s,
                        panel.y + 186f * s + row * 42f * s,
                        104f * s, 36f * s);

                    Color old = GUI.backgroundColor;
                    if (i == rig.gyroMapping) GUI.backgroundColor = new Color(0.35f, 1f, 0.45f);
                    if (GUI.Button(br, i.ToString())) rig.gyroMapping = i;
                    GUI.backgroundColor = old;
                }
            }
        }

        private bool flagW, flagA, flagS, flagD;
        private bool flagUp, flagDown, flagTurnL, flagTurnR;

        private void DrawHold(Rect r, string label, ref bool flag)
        {
            var e = Event.current;
            bool inside = r.Contains(e.mousePosition);

            if (inside && e.type == EventType.MouseDown) flag = true;
            else if (e.type == EventType.MouseUp) flag = false;

            Color old = GUI.backgroundColor;
            if (flag) GUI.backgroundColor = new Color(0.35f, 1f, 0.45f);
            GUI.Box(r, label);
            GUI.backgroundColor = old;
        }
    }
}
