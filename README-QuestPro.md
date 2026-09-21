# Meta Quest Pro：Unity VR 体素交互

已配置项目：`F:\GitHub\Unity\3dGenerationVR`

使用场景：`Assets/Scenes/SampleScene.unity`

## 使用

1. 打开电脑上的 Meta Quest Link / Meta Horizon Link，用 USB Link 或 Air Link 连接 Quest Pro，并在头显中进入 Quest Link。
2. 在 Unity 打开上面的项目与 SampleScene，再点击 **Play**。
3. 用任一手柄的射线指向身前面板的 **Get 3D Model**，按一下食指 **Trigger**。
4. 等待体素模型出现。指向其中一个体素，按住 Trigger 并移动手柄；松开后，体素停在当前位置。手柄靠近体素时也支持直接抓取。
5. 左右手可以同时移动不同体素。再次点击 Get 3D Model 会用新结果替换当前模型。

请先进入 Quest Link，再点击 Play。如果已在没有头显连接的情况下进入 Play，请停止 Play，连接 Link 后再进入。

## 已设置

- Windows 编辑器启用 OpenXR 自动初始化；Windows 和 Android 均启用 Touch Pro 与 Oculus Touch 手柄配置。
- 沿用项目里的 OVRCameraRig 与图片上传接口。
- 改为点击按钮才发起生成，并显示进度、失败提示和重试状态。
- 每个体素都有独立碰撞体与 Trigger 拖拽组件；松开、丢失追踪或输入焦点时释放。
- 默认体素边长 0.015 米，生成数量上限保留为 12,000。
- 在此电脑的 Unity Play 模式中自动启动原 Notebook 测试服务，停止 Play 后停止由 Unity 启动的服务。如果端口已被现有服务使用，则沿用它。

## 现有生成服务的范围

项目原来的 `ModelGenerationServer_Voxel.ipynb` 返回固定测试房屋体素，**还没有根据上传图片重建 3D 模型的算法**。这次保留该行为，并完成 VR 显示与逐体素交互。本次实际请求生成了 **9,791 个体素**。

当前完成的是 Voxel 模式；没有新增点云重建算法。

服务地址仍为 `http://127.0.0.1:8000/generate`。Unity Editor 在电脑运行，因此 Link 模式可以使用该地址。自动启动只用于 Unity Editor；Android 独立安装包不包含 Python 服务。

自动启动使用本机已有的 `F:\anaconda3\python.exe`，需要的 fastapi、uvicorn 和 python-multipart 已确认存在。解释器路径保存在 Unity Editor 偏好中；换电脑时可通过 **Tools → Quest Pro → Select Python for Existing Test Server** 选择解释器，也可以手动运行原 Notebook。

服务日志位于项目的 `Library/QuestProTestServer/server.log`；上传的测试图片也保存在该临时目录中。

## 验证与限制

已用 Unity 6000.3.15f1 完成编译、真实 Play 模式按钮到 HTTP 服务的生成、失败后重试、旧体素清理，以及自动化拖拽测试。旁边的验证文本记录了具体结果。

头显尚未连接，因此尚未验证真实 Quest Pro 双眼画面、手柄追踪和佩戴时帧率。预览图片来自 Unity 场景渲染，不是头显截图。

若 Play 后没有进入 VR：先检查头显已进入 Quest Link、电脑 Link 应用能识别设备，并确认其为活动 OpenXR 运行时。本次检查时电脑运行时已指向 Meta。

Meta 官方 Link 开发说明：https://developers.meta.com/horizon/documentation/unity/unity-link/
