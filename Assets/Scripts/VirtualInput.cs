using UnityEngine;

namespace VoxelGeoVR
{
    /// <summary>屏幕按钮聚合出的虚拟输入，供 CameraRig 在按钮/陀螺仪模式读取。</summary>
    public class VirtualInput : MonoBehaviour
    {
        public bool forward;
        public bool back;
        public bool left;
        public bool right;
        public bool up;
        public bool down;
        public bool turnLeft;
        public bool turnRight;

        public Vector2 Move
        {
            get
            {
                float x = (right ? 1f : 0f) - (left ? 1f : 0f);
                float y = (forward ? 1f : 0f) - (back ? 1f : 0f);
                return new Vector2(x, y);
            }
        }

        public float Vertical => (up ? 1f : 0f) - (down ? 1f : 0f);
        public float Turn => (turnRight ? 1f : 0f) - (turnLeft ? 1f : 0f);

        public void ClearMovement()
        {
            forward = false;
            back = false;
            left = false;
            right = false;
            up = false;
            down = false;
            turnLeft = false;
            turnRight = false;
        }
    }
}
