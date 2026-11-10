using UnityEngine;

namespace VoxelGeoVR
{
    /// <summary>无头自检：带 -selftest 启动时跑运动逻辑检查并自动退出（避免无限循环）。</summary>
    public class SelfTestRunner : MonoBehaviour
    {
        void Start()
        {
            bool selfTest = false;
            foreach (var arg in System.Environment.GetCommandLineArgs())
            {
                if (arg == "-selftest") { selfTest = true; break; }
            }
            if (!selfTest) return;

            int passed = 0;
            int failed = 0;

            void Check(string name, bool ok)
            {
                if (ok) { passed++; Debug.Log("[SELFTEST][PASS] " + name); }
                else { failed++; Debug.LogError("[SELFTEST][FAIL] " + name); }
            }

            Check("Main Camera exists", Camera.main != null);

            var rig = FindFirstObjectByType<CameraRig>();
            Check("CameraRig exists", rig != null);

            if (rig != null)
            {
                Check("VirtualInput wired", rig.virtualInput != null);
                Check("cameraTransform wired", rig.cameraTransform != null);

                if (rig.virtualInput != null)
                {
                    rig.SetMode(1);

                    Vector3 start = rig.transform.position;
                    rig.virtualInput.forward = true;
                    rig.StepForTest(0.5f);
                    rig.virtualInput.forward = false;
                    Check("Forward movement changes position",
                        (rig.transform.position - start).sqrMagnitude > 0.0001f);

                    Vector3 afterForward = rig.transform.position;
                    rig.virtualInput.right = true;
                    rig.StepForTest(0.5f);
                    rig.virtualInput.right = false;
                    Check("Strafe movement changes position",
                        (rig.transform.position - afterForward).sqrMagnitude > 0.0001f);
                }

                Debug.Log("[SELFTEST][INFO] supportsGyroscope = " + SystemInfo.supportsGyroscope);
            }

            Debug.Log("[SELFTEST] RESULT passed=" + passed + " failed=" + failed);
            Debug.Log(failed == 0 ? "[SELFTEST] OVERALL PASS" : "[SELFTEST] OVERALL FAIL");

            Application.Quit(failed == 0 ? 0 : 1);
        }
    }
}
