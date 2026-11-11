using UnityEngine;

namespace VoxelGeoVR
{
    /// <summary>
    /// 只在剖面相机渲染期间打开 shader 剖切，避免主视角世界被裁掉。
    /// 主相机渲染前 VoxelDisplayController 会把 _SectionEnabled 置 0。
    /// </summary>
    [DisallowMultipleComponent]
    public class SectionCameraClip : MonoBehaviour
    {
        void OnPreRender()
        {
            Shader.SetGlobalFloat("_SectionEnabled", 1f);
        }

        void OnPostRender()
        {
            Shader.SetGlobalFloat("_SectionEnabled", 0f);
        }
    }
}
