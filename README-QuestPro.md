# Meta Quest Pro：Unity VR 体素交互

已配置项目：`F:\GitHub\Unity\3dGenerationVR`

使用场景：`Assets/Scenes/SampleScene.unity`

## 使用

1. 打开电脑上的 Meta Quest Link / Meta Horizon Link，用 USB Link 或 Air Link 连接 Quest Pro，并在头显中进入 Quest Link。
2. 在 Unity 打开上面的项目与 SampleScene，再点击 **Play**。
3. 用任一手柄的射线指向身前面板的 **Get 3D Model**，按一下食指 **Trigger**。
4. 等待体素模型和 GLB 一起出现，二者应当重叠。状态栏显示 `Revision 0`。
5. 用下表的操作编辑体素，再点击 **Submit Edits**；后端返回新版本，GLB 与体素都按新版本重建，状态栏显示 `Revision {n}`。再次点击 Get 3D Model 会用新结果替换当前模型。

请先进入 Quest Link，再点击 Play。如果已在没有头显连接的情况下进入 Play，请停止 Play，连接 Link 后再进入。

## 操作一览

| 输入 | 作用 |
|---|---|
| 射线 + Trigger 指向面板按钮 | 按下按钮（优先于其他操作） |
| **按住 Grip** + 射线扫过体素 | 体素加入选区，显示为洋红色 |
| **A / X** | 清空选区 |
| Trigger（Move / Fill / Erase 模式） | 抓住体素拖动；抓的是选区内的体素就整个选区一起拖，否则只拖这一个。Move 只平移（射线橙色）；Fill 平移并在离开的格子留下副本（绿色）；Erase 删除经过的格子里的其他体素（红色） |
| Trigger（Add 模式） | 在预览方块的位置放置新体素（紫色射线）；按住扫动可连续放置 |
| 面板 **Move / Fill / Erase / Add** | 切换模式，当前模式的按钮为橙色 |
| 面板 **Submit Edits** | 把编辑后的全部体素发给后端，生成新版本；拖动或请求进行中不可用 |
| B / Y、左右摇杆 | 菜单回到视线前方、升降、平移 |

生成的模型里的体素只能通过上面的编辑模式移动，不能再自由拖动；超出 64³ 网格的体素在提交时被排除。请求进行中不能开始拖动或放置，但仍可用 Grip 选择。

## 已设置

- Windows 编辑器启用 OpenXR 自动初始化；Windows 和 Android 均启用 Touch Pro 与 Oculus Touch 手柄配置。
- 沿用项目里的 OVRCameraRig 与图片上传接口。
- 改为点击按钮才发起生成，并显示进度、失败提示和重试状态。
- 每个体素都有独立碰撞体与 Trigger 拖拽组件；松开、丢失追踪或输入焦点时释放。
- 默认体素边长 0.015 米，生成数量上限保留为 12,000。
- 在此电脑的 Unity Play 模式中自动启动 `backend/app/main.py`（fixture 生成器），停止 Play 后停止由 Unity 启动的服务。如果端口已被现有服务使用，则沿用它。

## 生成服务

后端是 `backend/app/main.py`（FastAPI），有两种生成器，由环境变量 `VRCRAFTER_GENERATOR` 选择：

- `fixture`（默认）：返回固定的测试房屋体素和 `backend/models/PropertyDemoModel.glb`，**不会根据上传图片重建 3D 模型**。Unity 在 Play 时自动启动的就是它，只需要 `backend/requirements.txt`。
- `trellis2`：在 Linux GPU 机器上运行 TRELLIS.2，同时返回 64³ 体素和对应的 GLB。**这条路径尚未在 GPU 上运行过**（权重未下载，o-voxel 和 trimesh 未安装），所有耗时、体素数和显存都没有测量过。安装与启动步骤见 [backend/README.md](backend/README.md)。

自动启动使用 `backend/.venv/Scripts/python.exe`（存在时）。准备方法：

```powershell
cd backend
python -m venv .venv
.\.venv\Scripts\Activate.ps1
python -m pip install -r requirements.txt
```

换解释器时，用 **Tools → Quest Pro → Select Python for VR-Crafter Backend** 选择；解释器路径保存在 Unity Editor 偏好中。自动启动只用于 Unity Editor；Android 独立安装包不包含 Python 服务。服务日志位于 `Library/QuestProTestServer/server.log`。

后端接口与错误格式见 [backend/README.md](backend/README.md)，Unity 请求流程见 [backend/UNITY_INTEGRATION.md](backend/UNITY_INTEGRATION.md)。

## 连接 Linux 上的 TRELLIS.2

Unity 和 Quest Link 在 Windows 上，TRELLIS.2 在 Linux 机器上（`10.12.65.139` / `10.243.171.47`）。先在 Windows PowerShell 中确认网络可达，两个地址都试一遍：

```powershell
Test-NetConnection 10.12.65.139 -Port 8000
Test-NetConnection 10.12.65.139 -Port 22
```

Linux 上用 `sudo ufw status` 确认 8000 端口没有被挡住。

| 方案 | Linux 启动 | Unity `Generate Url` | 注意 |
|---|---|---|---|
| A 局域网直连（8000 可达时） | `--host 0.0.0.0` | `http://<可达IP>:8000/generate` | 自动启动随之关闭，连不上时直接报错；端口对整个局域网开放，没有鉴权，只在可信网络使用 |
| B SSH 隧道（只有 22 可达时） | `--host 127.0.0.1` | 不变 | 在 Windows 上执行 `ssh -N -L 8000:127.0.0.1:8000 zicheng@<IP>`。**隧道没建好就进入 Play，Unity 会悄悄启动 fixture**；每次都先在浏览器打开 `/status`，确认 `generator` 为 `trellis2` |

## Inspector 设置

菜单 **Configure VR Voxel Scene** 会把 `Server` 的 Output Mode 重置为 `Both`，把 Request Timeout Seconds 重置为 `180`。TRELLIS.2 的耗时尚未测量，实测之后请重新设置 Request Timeout Seconds（建议取实测最大值的 2 倍），并按需要调整 `Max Voxels`。每次运行 Configure 之后都要检查这两项。

## 验证与限制

已用 Unity 6000.3.15f1 完成编译、真实 Play 模式按钮到 HTTP 服务的生成、失败后重试、旧体素清理，以及自动化拖拽测试。旁边的验证文本记录了具体结果。

头显尚未连接，因此尚未验证真实 Quest Pro 双眼画面、手柄追踪和佩戴时帧率。预览图片来自 Unity 场景渲染，不是头显截图。

## Windows 校验命令

新增的编辑器代码还没有在 Unity 中运行过。在 PowerShell 中把 `$U` 设为 6000.3.15f1 的 `Unity.exe`，`$P` 设为项目路径，并确认 127.0.0.1:8000 上没有别的服务（或者只有 fixture 后端）：

```powershell
& $U -batchmode -quit -projectPath $P -executeMethod QuestProProjectSetup.ConfigureAndValidate -questReport $P\Logs\setup.txt -logFile $P\Logs\setup.log
& $U -batchmode -quit -projectPath $P -executeMethod QuestProPerformanceValidation.RunBatch -questPerformanceReport $P\Logs\perf.txt -logFile $P\Logs\perf.log
& $U -batchmode -quit -projectPath $P -executeMethod QuestProHighlightValidation.RunBatch -questHighlightReport $P\Logs\highlight.txt -logFile $P\Logs\highlight.log
& $U -batchmode -quit -projectPath $P -executeMethod QuestProInputValidation.RunBatch -questInputReport $P\Logs\input.txt -logFile $P\Logs\input.log
& $U -batchmode -quit -projectPath $P -executeMethod QuestVoxelEditorValidation.RunBatch -questEditorReport $P\Logs\editor.txt -logFile $P\Logs\editor.log
& $U -batchmode -projectPath $P -executeMethod QuestProPlayModeValidation.Run -questPlayReport $P\Logs\play.txt -logFile $P\Logs\play.log
```

最后一条没有 `-quit`，会自行退出。预期结果：

- 每个报告只含 `PASS:` 行，每条命令退出码为 0。
- `input.txt` 增加 4 行 `Grip and A/X read correctly for …`。
- `editor.txt` 有 30 行 PASS，最后一行是 `Plain objects and loose GeneratedVoxels keep free dragging while an editor is Active`。
- `play.txt` 仍以 `Out-of-grid voxels are removed on submit: …` 结尾。
- 失败时把 `Logs\*.log` 和报告文件一并保留。

## 头显中还需确认

- GLB 和体素重叠，不是镜像；模型正面朝向用户（如果不是，需整体绕 Y 轴旋转映射和 GLB 根节点，见 [backend/UNITY_INTEGRATION.md](backend/UNITY_INTEGRATION.md)）。
- Grip 刷选和 A/X 清空正常；四种模式的射线颜色和按钮高亮正确；Add 的预览方块与实际落点一致。
- Submit Edits 之后 GLB 更新，状态栏显示 `Revision n`。
- 把 `Max Voxels` 调小，确认超限提示能显示。
- 大选区连续 Fill 和 Erase 约 20 秒，查看 `Library/QuestProInputDiagnostics.log` 中的平均帧时间和最大帧时间。

若 Play 后没有进入 VR：先检查头显已进入 Quest Link、电脑 Link 应用能识别设备，并确认其为活动 OpenXR 运行时。本次检查时电脑运行时已指向 Meta。

Meta 官方 Link 开发说明：https://developers.meta.com/horizon/documentation/unity/unity-link/
