using UnityEngine;

namespace VoxelGeoVR
{
    /// <summary>相机输入源抽象。</summary>
    public interface ICameraInput
    {
        string ModeName { get; }
        Vector2 Move { get; }
        float Vertical { get; }
        Vector2 LookDelta { get; }
        float Turn { get; }
        bool TryGetAttitude(out Quaternion attitude);
    }

    /// <summary>PC 键鼠输入。</summary>
    public class PCInput : ICameraInput
    {
        public string ModeName => "PC 键鼠";

        public Vector2 Move
        {
            get
            {
                float x = 0f, y = 0f;
                if (Input.GetKey(KeyCode.A)) x -= 1f;
                if (Input.GetKey(KeyCode.D)) x += 1f;
                if (Input.GetKey(KeyCode.S)) y -= 1f;
                if (Input.GetKey(KeyCode.W)) y += 1f;
                return new Vector2(x, y);
            }
        }

        public float Vertical
        {
            get
            {
                float v = 0f;
                if (Input.GetKey(KeyCode.Q)) v -= 1f;
                if (Input.GetKey(KeyCode.E)) v += 1f;
                return v;
            }
        }

        public Vector2 LookDelta => new Vector2(Input.GetAxis("Mouse X"), Input.GetAxis("Mouse Y"));
        public float Turn => 0f;

        public bool TryGetAttitude(out Quaternion attitude)
        {
            attitude = Quaternion.identity;
            return false;
        }
    }

    /// <summary>陀螺仪输入（Input.gyro；无传感器的设备返回 false）。</summary>
    public class GyroInput : ICameraInput
    {
        public string ModeName => "陀螺仪";
        public Vector2 Move => Vector2.zero;
        public float Vertical => 0f;
        public Vector2 LookDelta => Vector2.zero;
        public float Turn => 0f;

        public bool TryGetAttitude(out Quaternion attitude)
        {
            if (SystemInfo.supportsGyroscope && Input.gyro.enabled)
            {
                attitude = Input.gyro.attitude;
                return true;
            }
            attitude = Quaternion.identity;
            return false;
        }
    }

    /// <summary>
    /// 相机架：以相机为基准做上下左右前后移动 + 视角旋转。
    /// mode: 0=PC键鼠 1=屏幕按钮 2=陀螺仪
    ///
    /// 陀螺仪采用「相对校准」：进入陀螺仪模式（或点校准）时记录当时的姿态 q0
    /// 与相机朝向 R0，之后只把「相对 q0 的旋转」叠加到 R0 上。
    /// 这样 Unity 姿态四元数里任何固定的坐标系偏移都会自动抵消，剩下的只是
    /// 组合方式（取正/取逆、设备序/世界序、画面滚转）需要现场试出来：
    ///   gyroMapping = invert + 2*order + 4*rollIndex
    /// </summary>
    public class CameraRig : MonoBehaviour
    {
        public Transform cameraTransform;
        public VirtualInput virtualInput;

        public float moveSpeed = 3f;
        public float mouseSensitivity = 1.5f;
        public float turnSpeed = 90f;
        public float gyroSmoothing = 18f;
        public int mode = 0;

        [Header("Gyro (debug)")]
        public bool gyroDebug = false;
        /// <summary>bit0=姿态取逆, bit1=世界序(否则设备序), bit2-3=画面滚转 0/90/180/270</summary>
        public int gyroMapping = 3;

        private float yaw;
        private float pitch;
        private readonly PCInput pc = new PCInput();
        private readonly GyroInput gyro = new GyroInput();
        private bool gyroInitialized;

        private Quaternion gyroQ0 = Quaternion.identity;
        private Quaternion gyroR0 = Quaternion.identity;
        private bool gyroCalibrated;
        private int gyroLastMapping = -1;
        private string debugText = "";
        private float lastEz;
        private const string kBuildStamp = "2026-11-10 14:16";

        public string DebugText => debugText;

        void Start()
        {
            if (cameraTransform == null)
            {
                var cam = GetComponentInChildren<Camera>();
                if (cam != null) cameraTransform = cam.transform;
            }
            // 手机上默认用屏幕按钮
            if (Application.isMobilePlatform && mode == 0) mode = 1;
        }

        void Update()
        {
            if (mode == 2 && !gyroInitialized)
            {
                if (SystemInfo.supportsGyroscope)
                {
                    Input.gyro.enabled = true;
                    Input.gyro.updateInterval = 1f / 120f;
                }
                gyroInitialized = true;
            }

            Vector2 move = Vector2.zero;
            float vertical = 0f;
            Vector2 look = Vector2.zero;
            float turn = 0f;

            if (mode == 0)
            {
                move = pc.Move;
                vertical = pc.Vertical;
                look = pc.LookDelta;
            }
            else if (virtualInput != null)
            {
                move = virtualInput.Move;
                vertical = virtualInput.Vertical;
                turn = virtualInput.Turn;
            }

            // 以相机为基准的移动（水平面内）
            Vector3 forward = transform.forward; forward.y = 0f;
            Vector3 right = transform.right; right.y = 0f;
            if (forward.sqrMagnitude < 1e-6f) forward = Vector3.forward;
            if (right.sqrMagnitude < 1e-6f) right = Vector3.right;
            forward.Normalize();
            right.Normalize();

            Vector3 dir = forward * move.y + right * move.x + Vector3.up * vertical;
            if (dir.sqrMagnitude > 1f) dir.Normalize();
            transform.position += dir * moveSpeed * Time.deltaTime;

            // 视角
            Quaternion attitude;
            if (mode == 2 && gyro.TryGetAttitude(out attitude))
            {
                ApplyGyro(attitude);
            }
            else
            {
                yaw += look.x * mouseSensitivity + turn * turnSpeed * Time.deltaTime;
                pitch = Mathf.Clamp(pitch - look.y * mouseSensitivity, -89f, 89f);

                transform.localRotation = Quaternion.Euler(0f, yaw, 0f);
                if (cameraTransform != null)
                    cameraTransform.localRotation = Quaternion.Euler(pitch, 0f, 0f);
            }
        }

        void ApplyGyro(Quaternion attitude)
        {
            if (!gyroCalibrated) CalibrateGyro();

            bool invert = (gyroMapping & 1) != 0;
            bool worldOrder = (gyroMapping & 2) != 0;
            int rollIndex = (gyroMapping >> 2) & 3;

            Quaternion q = invert ? Quaternion.Inverse(attitude) : attitude;
            Quaternion q0 = invert ? Quaternion.Inverse(gyroQ0) : gyroQ0;

            Quaternion target;
            if (gyroMapping == 3)
            {
                // 组合3（取逆 + 世界序）：上一版 x/y 已经对了，唯一问题是横滚速率不对。
                // 这里把相机第三个欧拉角相对零位的变化率乘 0.5：
                //   cam.z 变化量 = att.z 变化量 × 0.5
                Vector3 e = ((q * Quaternion.Inverse(q0)) * gyroR0).eulerAngles;
                float ez = Mathf.DeltaAngle(0f, e.z);
                lastEz = ez;
                target = Quaternion.Euler(e.x, e.y, ez * -1f);
            }
            else if (worldOrder)
            {
                // 世界序：相对旋转直接左乘
                target = (q * Quaternion.Inverse(q0)) * gyroR0;
            }
            else
            {
                // 设备序：相对旋转要经过相机安装矩阵共轭
                Quaternion c = Quaternion.Inverse(q0) * q;
                Quaternion mount = Quaternion.Euler(0f, 180f, 0f);
                target = gyroR0 * (Quaternion.Inverse(mount) * c * mount);
            }

            if (rollIndex != 0)
                target *= Quaternion.Euler(0f, 0f, rollIndex * 90f);

            float k = 1f - Mathf.Exp(-gyroSmoothing * Time.deltaTime);
            if (gyroMapping != gyroLastMapping)
            {
                k = 1f;
                gyroLastMapping = gyroMapping;
            }

            transform.rotation = Quaternion.Slerp(transform.rotation, target, k);
            if (cameraTransform != null)
                cameraTransform.localRotation = Quaternion.identity;

            if (gyroDebug)
            {
                Vector3 a = attitude.eulerAngles;
                Vector3 c = transform.eulerAngles;

                // 绕视线轴的「真实横滚角」，不受欧拉分解奇异影响
                float roll = 0f;
                Vector3 fwd = transform.forward;
                Vector3 refUp = Vector3.ProjectOnPlane(Vector3.up, fwd);
                if (refUp.sqrMagnitude > 1e-4f)
                    roll = Vector3.SignedAngle(refUp.normalized, transform.up, fwd);

                debugText = string.Format(
                    "att ({0:F1}, {1:F1}, {2:F1})\ncam ({3:F1}, {4:F1}, {5:F1})\nroll {6:F1}   ez {7:F1}\nmap {8}/15 inv{9} ord{10} roll{11}\nbuild {12}",
                    a.x, a.y, a.z, c.x, c.y, c.z, roll, lastEz,
                    gyroMapping, invert ? 1 : 0, worldOrder ? 1 : 0, rollIndex * 90, kBuildStamp);
            }
        }

        /// <summary>把当前手机姿态记为「正前方」，之后相对它旋转。</summary>
        public void CalibrateGyro()
        {
            Quaternion q;
            if (gyro.TryGetAttitude(out q))
            {
                gyroQ0 = q;
                gyroR0 = Quaternion.Euler(0f, transform.eulerAngles.y, 0f);
                gyroCalibrated = true;
            }
        }

        public void SetMode(int newMode)
        {
            mode = Mathf.Clamp(newMode, 0, 2);
            if (mode == 2)
            {
                gyroCalibrated = false;          // 重新以当前朝向为基准
                gyroLastMapping = -1;
            }
            else
            {
                yaw = transform.eulerAngles.y;   // 从姿态模式切回来时保持水平朝向
                pitch = 0f;
            }
        }

        /// <summary>自检：按当前按钮输入手动走一步。</summary>
        public void StepForTest(float dt)
        {
            if (virtualInput == null) return;
            Vector2 move = virtualInput.Move;
            float vertical = virtualInput.Vertical;

            Vector3 forward = transform.forward; forward.y = 0f;
            Vector3 right = transform.right; right.y = 0f;
            if (forward.sqrMagnitude < 1e-6f) forward = Vector3.forward;
            if (right.sqrMagnitude < 1e-6f) right = Vector3.right;
            forward.Normalize();
            right.Normalize();

            Vector3 dir = forward * move.y + right * move.x + Vector3.up * vertical;
            if (dir.sqrMagnitude > 1f) dir.Normalize();
            transform.position += dir * moveSpeed * dt;
        }
    }
}
