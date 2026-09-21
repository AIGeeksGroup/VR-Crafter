# Quest Pro No pose 修复

## 实测证据

2026-09-14 12:17 的真实 Unity Play 日志显示，`Left Quest Controller Pointer` 和 `Right Quest Controller Pointer` 均已创建，因此交互对象没有缺失。

右手 Touch Pro 已被 OpenXR 枚举，设备启用，应用拥有 VR 和输入焦点。12:17:44–12:17:58 间，瞄准位置、方向持续更新，Trigger 有 0、0.0427、1 等值。但是 `isTracked` 一直为 0，`trackingState` 为 15，包含 Position 和 Rotation 的有效标志。旧代码额外要求 `isTracked` 为真，因此拒绝了已经收到的有效数据，菜单显示 No pose。

此轮日志中左手实体控制器未被枚举，左手交互对象已存在。左手是否恢复需要双手同时测试，不能从右手数据推断。

## 修改

`QuestProControllerInput` 的 Input System 和 Unity XR Feature API 路径均改为依据位置、方向有效标志接收姿态，不再用 `isTracked=false` 一票否决。

OpenXR 区分数据有效与主动追踪状态；运行时可以提供有效的推断姿态。参见 [OpenXR 空间位置规范](https://registry.khronos.org/OpenXR/specs/1.0-khr/html/xrspec.html#spaces)。这里没有声称已确定运行时持续报告 isTracked=false 的内部原因。

缺少位置或方向有效标志、设备断开、输入焦点丢失仍会停止交互并释放抓取。恢复后需要松开再按 Trigger。

交互节点由 `QuestProVoxelExperience` 在运行时创建在 `OVRCameraRig/TrackingSpace` 下，自带射线和 Trigger 交互。当前没有额外添加完整的手柄外观模型 prefab；外观模型不是读取姿态和点击按钮的前提。

诊断版本更新为 v3。下次 Play 控制台出现 `[Quest Pro Input] v3 loaded` 时，可确认新版已加载。

## 验证

- 修复前：将实测的 isTracked=false、trackingState=15、瞄准姿态和 Trigger=1 重放给旧代码，回归失败，Unity 返回码 1。
- 修复后：28 项输入回归通过，Unity 返回码 0。包括两种控制器布局的左右手、上述有效但非主动追踪状态、缺少位置或方向时的拒绝，以及该状态下真实交互脚本的点击、拖动、松开、失效释放和恢复逻辑。
- Git 差异格式检查通过。
- 尚未确认修改后的实体头显交互结果，需重新 Play 实测。

本轮截图已无 Local Dimming 警告。房间扫描扩展和震动初始化 warning 仍保留；右手有效数据已抵达 Unity，因此无需通过隐藏这些日志解决 No pose。

下一次测试：等待 Unity 编译完成后重新 Play，拿起两只手柄各按几次 Trigger，再用射线点击 Get 3D Model。原项目的 `Library/QuestProInputDiagnostics.log` 会记录前 120 秒的真实状态，便于继续核对。
