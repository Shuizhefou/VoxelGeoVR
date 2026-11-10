# VoxelGeoVR

Unity 6.3 LTS（6000.3.28f1）+ URP 的移动端伪 VR（陀螺仪）体素地图漫游原型。

## 工程结构

```
Assets/
  Scenes/Main.unity           # 由 SceneBuilder 生成，勿手改
  Materials/Ground.mat|Cube.mat
  Scripts/CameraRig.cs        # 三种模式 + 陀螺仪 16 组映射（组合3 为默认）
  Scripts/VirtualInput.cs     # 屏幕按钮状态
  Scripts/OnScreenControls.cs # IMGUI 按钮 + 陀螺仪调试面板
  Scripts/SelfTestRunner.cs   # -selftest 自检
  Editor/SceneBuilder.cs      # 代码生成场景与材质资产
  Editor/AndroidBuild.cs      # 菜单：VoxelGeoVR/Build Android APK
  Editor/BuildScript.cs       # PC 玩家打包（早期验证用）
Packages/                     # 已移除 com.unity.inputsystem
ProjectSettings/              # activeInputHandler = 0（旧 Input Manager）
```

## 相机模式

| 模式 | 输入 | 说明 |
|---|---|---|
| 0 PC | WASD + Q/E + 鼠标 | 编辑器调试 |
| 1 按钮 | 屏幕按钮 WASD / turn / up-down | 手机默认 |
| 2 陀螺仪 | `Input.gyro.attitude` | 相对校准 + 16 组映射 |

陀螺仪映射编码：`gyroMapping = invert + 2*order + 4*rollIndex`
- bit0：姿态取逆
- bit1：世界序（否则设备序）
- bit2-3：画面滚转 0/90/180/270

组合 3（取逆 + 世界序）为默认，其横滚分量按 `ez * -1f` 取反，与手机同向同步。
调试面板显示 `att / cam / roll / ez / map / build`，`roll` 为绕视线轴的真实横滚角（不受欧拉奇异影响）。

## 构建 APK

菜单 `VoxelGeoVR → Build Android APK`，或：

```powershell
& "E:\unity\Editor\6000.3.28f1\Editor\Unity.exe" -batchmode -nographics `
  -projectPath "E:\unity\Projects\VoxelGeoVR" `
  -executeMethod AndroidBuild.BuildApk `
  -logFile "E:\unity\Projects\VoxelGeoVR\Build\build.log"
```

成功标志：日志出现 `[AndroidBuild] result=Succeeded`，产物 `Build/VoxelGeoVR.apk`。

## 安装

```powershell
$adb = "E:\unity\Editor\6000.3.28f1\Editor\Data\PlaybackEngines\AndroidPlayer\SDK\platform-tools\adb.exe"
& $adb install -r "Build\VoxelGeoVR.apk"
& $adb shell am force-stop com.voxelgeo.vr
& $adb shell am start -n com.voxelgeo.vr/com.unity3d.player.UnityPlayerGameActivity
```

> Unity 6.3 的 Activity 是 `UnityPlayerGameActivity`，不是 `UnityPlayerActivity`。

## 环境注意（非代码问题）

1. **JDK 版本串补丁**：Unity 只认 `17.0.18+8`，若用其它 17.x 会报 `JDK not found`。
   `...\AndroidPlayer\OpenJDK\release` 需为：
   ```
   JAVA_VERSION="17.0.18"
   JAVA_RUNTIME_VERSION="17.0.18+8"
   ```
   Hub 修复/重装模块会覆盖，会再次报错。
2. **Android SDK/NDK/JDK 是手工放置的**（Unity 官方下载地址当时 404，Hub 也装不上）：
   `...\AndroidPlayer\{SDK,NDK,OpenJDK}`，SDK 内含 platform-tools 37.0.1、build-tools 36.0.0、platforms 34/35/36、cmake 3.22.1。
3. **场景每次打包重建**：`AndroidBuild.BuildApk()` 会先调 `SceneBuilder.BuildMainScene()`，
   材质必须是 `.mat` 资产，否则 shader 不进包、手机上物体不可见。
