# VR-Crafter：TRELLIS.2 体素 + GLB 生成与 VR 编辑回传 实现说明

本文汇总分支 `feat/trellis2-voxel-editing` 上的全部修改（基于 `main` 的 `7bc61ac`），说明每一部分做了什么、怎么工作，以及哪些地方还没有验证。

## 1. 概述

这个分支实现了一个完整的闭环：

1. Unity 上传图片 → 后端用 TRELLIS.2 生成 64³ 体素和对应的 GLB，**在同一个请求里返回**。
2. VR 中 GLB 与体素重叠显示，用户可以多选体素，并用四种操作编辑：移动（Move）、拖动填充（Fill）、拖动擦除（Erase）、直接新增（Add）。
3. 点击 Submit Edits，把编辑后的体素回传给后端；后端从编辑后的结构重新生成 GLB，作为新版本（revision）返回。

为此，后端增加了生成记录和版本存储、稳定的体素 id 和回传接口；Unity 增加了体素数据层、提交流程、GLB 对齐、编辑器和配套的验证脚本。没有 GPU 时，后端可以用 fixture 模式（固定的测试房屋）跑完整个往返，所以 Windows 上可以单独测试 Unity 端。

## 2. 验证状态

| 部分 | 状态 |
|---|---|
| 后端（fixture 模式、接口、存储） | 已在 `backend/.venv` 中测试：62 passed, 1 skipped（GPU 测试） |
| TRELLIS.2 生成器 | **只写了代码，从未在 GPU 上运行**：权重未下载，`trellis2` 环境未装 o-voxel 和 trimesh。所有调用都已对照 TRELLIS.2 源码逐一核对 |
| Unity 代码 | **从未在真实 Unity 中运行**。只用本机的 Linux Unity 6000.0 编译器（加部分包的桩）检查过能编译通过；所有验证脚本都需要在 Windows 上跑 |
| 头显中的效果 | 未验证：GLB 与体素是否重合、模型正面朝向、交互手感、帧率 |
| 文档 | `backend/README.md`、`backend/UNITY_INTEGRATION.md`、`README-QuestPro.md` 已更新 |

## 3. 整体数据流

```
Unity (Windows, Quest Link)                      后端 (FastAPI)
─────────────────────────                        ───────────────────────────────────
Get 3D Model ── POST /generate (PNG) ──────────▶ store.create → 生成器.structure → 检查数量
                                                 → 分配 id → 生成器.mesh → 保存 rev_0
            ◀── voxels[id,x,y,z,type,color] ──── 
                + model_url + revisions_url ...
glTFast ────── GET .../revisions/0/model.glb ──▶ 返回该版本的 GLB
编辑（选区 + Move/Fill/Erase/Add）
Submit Edits ─ POST .../revisions (JSON) ──────▶ 校验 → 合并同格 → 分配新 id
                                                 → 生成器.mesh（原图 + 原 seed）→ 保存 rev_n+1
            ◀── 新版本的 voxels + model_url ──── 
```

生成器有两种实现，用环境变量 `VRCRAFTER_GENERATOR` 选择：

- **fixture**（默认）：固定的测试房屋（9,791 个体素）加演示 GLB，Unity 在 Windows 上自动启动的就是这个。
- **trellis2**：在 Linux GPU 机器上运行的 TRELLIS.2。

## 4. 后端

### 4.1 文件

| 文件 | 内容 |
|---|---|
| `backend/app/voxel_mapping.py` | 新建。TRELLIS ↔ Unity 坐标映射、体素 payload 构建（纯 Python） |
| `backend/app/generation_store.py` | 新建。生成记录、版本、体素 id 的磁盘存储 |
| `backend/app/generators.py` | 新建。`GenerationError`、`Generator` 协议、`FixtureGenerator` |
| `backend/app/trellis_pipeline.py` | 新建。`TrellisGenerator`、`load_image`；模块顶层不导入 torch |
| `backend/app/birefnet_local.py` | 新建。`LocalBiRefNet`：用仓库内的 BiRefNet 代码做去背景，不使用 `trust_remote_code` |
| `backend/app/main.py` | 重写路由部分：lifespan、生成器选择、全部接口、GPU 锁、错误约定 |
| `backend/scripts/measure_trellis.py` | 新建。测量体素数、耗时、GLB 大小、显存峰值（尚未运行） |
| `backend/tests/*` | 新建。`test_voxel_mapping.py`、`test_generation_store.py`、`test_api.py`、`test_trellis_pipeline.py`，以及 `conftest.py` |
| `backend/requirements-dev.txt`、`backend/pytest.ini` | 新建。测试依赖 `pytest`、`httpx`、`pillow`；过滤一条第三方弃用警告 |
| `backend/.gitignore` | 增加 `data/generations/` 和 o-voxel 的编译产物 |
| `backend/third_party/TRELLIS.2/`、`backend/third_party/BiRefNet/`、`backend/third_party/README.md` | 新建。官方 TRELLIS.2 的完整代码（`75fbf01`，含 Eigen）、BiRefNet 模型代码（`e2bf8e4`），以及来源说明，见 4.10 节 |

`backend/requirements.txt` 没有改动：fixture 模式仍然只需要 fastapi、uvicorn 和 python-multipart，`import app.main` 不会导入 torch、trellis2 或 PIL（有测试保证）。

### 4.2 运行模式与环境变量

| 变量 | 默认值 | 说明 |
|---|---|---|
| `VRCRAFTER_GENERATOR` | `fixture` | `fixture` 或 `trellis2`；其他值在启动时抛 `RuntimeError` |
| `VRCRAFTER_DATA_DIR` | `backend/data/generations` | 生成记录存放位置（不纳入 Git） |
| `TRELLIS2_MODEL` | `microsoft/TRELLIS.2-4B` | HF 仓库名或本地目录 |
| `TRELLIS2_DEVICE` | `cuda` | **保持 `cuda`**。TRELLIS.2 上游代码里有写死的 `.cuda()`，指定其他设备会出错；要选择用哪块卡，用 `CUDA_DEVICE_ORDER=PCI_BUS_ID CUDA_VISIBLE_DEVICES=<n>` |
| `TRELLIS2_LOW_VRAM` | `0` | 只接受 `0` 或 `1`。`0` 表示模型常驻显存（建议用 48 GB 的那块卡） |

trellis2 模式的启动命令（尚未实际运行过）：

```bash
cd backend
CUDA_DEVICE_ORDER=PCI_BUS_ID CUDA_VISIBLE_DEVICES=1 VRCRAFTER_GENERATOR=trellis2 \
  python -m uvicorn app.main:app --host 127.0.0.1 --port 8000
```

Hugging Face 缓存不在默认位置时，另外设置 `HF_HOME`。TRELLIS.2 的代码不需要另外指定路径，见 4.10 节。

### 4.3 接口

**`POST /generate`**（multipart）

- 字段：`file`（图片）、`response_mode`（`model` / `voxels` / `both`）、`max_voxels`、可选的 `seed`（默认 42）。
- 成功时返回：

```json
{
  "status": "done", "response_mode": "both", "source": "trellis2", "seed": 42,
  "generation_id": "<32 位 hex>", "revision": 0,
  "grid_size": 64, "voxel_count": 2,
  "voxels": [{"id": 0, "x": 53, "y": 30, "z": 43, "type": "occupied", "color": "#C8C8C8"}, "..."],
  "model_url": "http://<host>/generations/<id>/revisions/0/model.glb",
  "model_space": "trellis_unit_cube",
  "revisions_url": "http://<host>/generations/<id>/revisions",
  "generation_url": "http://<host>/generations/<id>"
}
```


- 字段规则：
  - `voxels` 只在 `voxels`/`both` 模式下出现，`model_url` 只在 `model`/`both` 模式下出现；后端总是两者都生成并保存。
  - 没有生成 GLB 时，用 `model_error: "No GLB was produced."` 代替 `model_url`。
  - `model_space` 只在 trellis2 模式下出现，Unity 据此决定是否对齐 GLB。
  - `generation_url` 出现在每个成功响应里。
  - 所有 URL 都基于请求的 Host 生成，因此局域网直连和 SSH 隧道都适用。

**`POST /generations/{generation_id}/revisions`**（JSON）

```json
{"base_revision": 0, "max_voxels": 12000, "voxels": [
  {"id": 0,  "x": 5, "y": 5, "z": 5, "type": "occupied", "color": "#C8C8C8"},
  {"id": -1, "x": 6, "y": 5, "z": 5, "type": "occupied", "color": "#C8C8C8"}]}
```

- 列出编辑后的**全部**体素：没有列出的已有 id 视为删除，`id=-1` 表示新体素。
- 响应结构与 `/generate` 的 `both` 模式相同，`revision` 为 `base_revision + 1`，新体素带上后端分配的 id。

**`GET /generations/{generation_id}/revisions/{revision}/model.glb`**：返回该版本的 GLB；不存在或 id 非法时返回 404。

**`GET /generations/{generation_id}`**：

- 返回最新已提交版本，结构与回传接口的成功响应相同（`both` 模式）。
- 只读，不需要拿 GPU 锁；未提交的版本不会出现在结果里。
- 未知或非法的 id 返回 HTTP 200 + `Unknown generation {id}.`。
- Unity 在提交收到"版本过期"错误后，用它加载服务端的最新版本，详见 5.4 节。

**其他**：
- `GET /status` 增加 `generator` 字段，`GET /health` 保持不变。
- 旧接口 `GET /model` 保留，供旧组件使用。

**错误约定**：除 GLB 下载外，所有失败都返回 HTTP 200 + `{"status": "error", "error": "<文案>"}`。Unity 只有在这种情况下才会把服务端的错误文案显示出来；返回 HTTP 4xx/5xx 时，它只会提示 "Cannot reach generation server"。

| 情况 | 文案 |
|---|---|
| 体素数超过上限 | `Generator produced {n} voxels, above max_voxels={m}. Raise Max Voxels on the Unity Server component.` |
| 一阶段结果为空 | `Generator produced no occupied voxels.` |
| 上传内容不是图片 | `Uploaded file is not a readable image.` |
| `response_mode` 非法 | `response_mode must be model, voxels, or both` |
| 请求格式错误 | `Invalid request: <详情>` |
| 其他异常 | `Generation failed: {exception}` |
| 未知或非法的 generation_id | `Unknown generation {generation_id}.` |
| 版本过期 | `Revision {base} is stale; latest is {latest}.` |
| 未知 id / 重复 id | `Unknown voxel id {id}.` / `Duplicate voxel id {id}.` |
| 越界 / 颜色非法 | `Voxel {id} is outside the 64^3 grid.` / `Voxel {id} has invalid color {color}.` |
| 编辑后为空 / 超过上限 | `Edited model has no voxels.` / `Edited model has {n} voxels, above max_voxels={m}.` |

| 回传时的生成器与创建该记录的生成器不同 | `Generation {gid} was created by {source}; this server runs {current}.` |

### 4.4 坐标映射

TRELLIS 索引为 `(ix, iy, iz)`，Z 轴朝上；Unity 网格索引为 `(ux, uy, uz)`，Y 轴朝上，`R = 64`：

```
ux = R-1-ix   uy = iz   uz = R-1-iy          # trellis_to_unity
ix = R-1-ux   iy = R-1-uz   iz = uy          # unity_to_trellis
```

- 两个方向都会做越界检查，并把结果转成 Python `int`。
- 映射可逆，不做平移。编辑结果能原样送回二阶段，作为它的输入 coords。

### 4.5 生成记录与版本存储

目录布局（`VRCRAFTER_DATA_DIR` 下）：

```
{gid}/input.png                 上传的原图（后缀只接受 png/jpg/jpeg/webp，其他一律存为 .png）
{gid}/meta.json                 {"seed", "source", "next_id"}
{gid}/rev_{n}/model.glb         该版本的 GLB
{gid}/rev_{n}/revision.json     {"parent": n-1 或 null, "voxels": [...]}，提交标记
```

- **`gid` 校验**：`gid` 必须完全匹配 `[0-9a-f]{32}`，且对应目录必须存在，否则所有方法都抛 `KeyError`。所以 URL 里的 id 不能被用来访问数据目录之外的文件。
- **提交标记**：
  - 每个版本先写 GLB，最后写 `revision.json`（先写临时文件，再 `os.replace`）。
  - 只有包含 `revision.json` 的版本目录才算有效版本，中途失败留下的目录会被忽略。
- **体素 id**：`allocate_ids` 按 `meta.json` 中的 `next_id` 单调分配，从不复用，删除后再新增也不会撞号。
- **失败清理**：`/generate` 失败时删除这次新建的生成记录；回传版本失败时，最新版本保持不变，可以直接重试。

### 4.6 处理流程

**生成**（`/generate`）：

1. 读取上传文件，创建生成记录。
2. 在线程池中加锁执行：
   1. `structure()`；结果为空或超过 `max_voxels` 时直接报错，不截断，也不再跑二、三阶段。
   2. 分配 id `0..N-1`。
   3. `mesh()` 写入 `rev_0/model.glb`。
   4. 保存版本 0。

**回传版本**（`/revisions`）：整个流程都在锁内执行，校验按以下顺序进行：

1. generation 存在；创建它的生成器与当前运行的生成器一致（`meta.json` 的 `source`）。
2. `base_revision` 等于最新版本。
3. 逐个检查体素：id 是否未知或重复（只检查 ≥ 0 的 id），坐标是否越界，颜色是否符合 `^#[0-9A-Fa-f]{6}$`。
4. 合并落在同一格的体素：先按 id 从小到大保留已有体素，再按提交顺序保留新体素。
5. 结果为空时报错。
6. 超过 `max_voxels` 时报错。

校验通过后：给新体素分配 id → 用原图和原 seed 调用 `mesh()` → 保存版本 n+1（parent 为 n）。

### 4.7 并发与错误处理

- **GPU 锁**：所有 GPU 工作和存储写入都在 `run_in_threadpool` 中执行，并共用一个模块级 `threading.Lock`，不阻塞事件循环。Unity 超时重试与上一请求重叠时也会排队串行执行。`/status`、GLB 下载和 `GET /generations/{id}` 都不需要拿锁。
- **异常处理**：`GenerationError` 的消息原样返回给客户端；其他异常统一返回 `Generation failed: …`。JSON 接口不会返回 HTTP 500。
- **请求格式校验**：由一个统一的处理器负责。JSON 接口返回 HTTP 200 + `Invalid request: …`；GLB 接口返回 404。

### 4.8 TRELLIS.2 生成器（`trellis_pipeline.py`）

- **加载（`load()`）**：
  1. 把仓库内的 `backend/third_party/TRELLIS.2` 追加到 `sys.path`，导入 `trellis2` 后检查它确实来自这份副本，否则启动失败。
  2. 定义 `Trellis2ImageTo3DPipeline` 的子类，`model_names_to_load` 只包含 `pipeline_type='1024'` 需要的 6 个模型：`sparse_structure_flow_model`、`sparse_structure_decoder`、`shape_slat_flow_model_1024`、`shape_slat_decoder`、`tex_slat_flow_model_1024`、`tex_slat_decoder`。
  3. 设置 `low_vram` 后再 `to(device)`。
  4. 启动时就导入 `o_voxel.postprocess`，缺少依赖时直接启动失败。
  5. 如果没有设置 `PYTORCH_CUDA_ALLOC_CONF`，按官方 app 的做法设为 `expandable_segments:True`。
- **`structure(image, seed)`**：
  1. `preprocess_image`（没有 alpha 通道时会先去背景）。
  2. `torch.manual_seed(seed)`。
  3. `get_cond(512)`。
  4. `sample_sparse_structure(cond, 64)`：解码器原生输出 64³，不做 pool。
  5. 用 `trellis_to_unity` 转成 Unity 坐标的体素，统一颜色 `#C8C8C8`、类型 `occupied`。
- **`mesh(image, seed, coords, glb_out)`**：
  1. `preprocess_image`、`get_cond(1024)`、`torch.manual_seed(seed)`。
  2. 把 coords 经 `unity_to_trellis` 转换、排序后建成张量。排序后的顺序与一阶段 `argwhere` 的输出一致，所以 GLB 只取决于体素集合本身，与提交顺序无关。
  3. `sample_shape_slat` / `sample_tex_slat`（使用 1024 模型）→ `decode_latent(…, 1024)` → `simplify(16777216)`。
  4. `o_voxel.postprocess.to_glb(grid_size=1024, aabb=±0.5, decimation_target=500000, texture_size=2048, remesh=True, remesh_band=1, remesh_project=0)`，导出时不开 WebP。
- **整个过程都在 `torch.no_grad()` 中执行**。
- **体素和 GLB 来自同一组 coords**：版本 0 的体素就是一阶段的输出，GLB 由这同一组 coords 生成，所以两者形状一致。
- **需要的模型与访问权限**：
  - 条件模型 `facebook/dinov3-vitl16-pretrain-lvd1689m`：需要人工审批。它在 transformers 里有现成的模型类，不会下载代码。
  - 去背景模型：官方 `pipeline.json` 里写的是 `briaai/RMBG-2.0`，上游加载时用了 `trust_remote_code=True`，会从 Hugging Face 下载模型代码并执行。这里改成用仓库内的 BiRefNet 代码配合公开的 `ZhengPeng7/BiRefNet` 权重（MIT 许可证，不需要申请权限），详见 4.10 节。所以不再需要 RMBG-2.0。

### 4.9 测试

```bash
cd backend && .venv/bin/python -m pytest tests -v            # 不需要 GPU
VRCRAFTER_GPU_TESTS=1 python -m pytest tests   # 在 trellis2 环境中跑 GPU 测试（尚未运行）
```

- **Generator 替身**：API 测试用一个假生成器，覆盖生成、回传、合并、过期、各类非法输入、中途失败后的恢复、seed 沿用、`model_error`、GLB 路由的 404、`GET /generations/{id}`、生成器不一致、生成与回传混合并发时的串行化，以及 fixture 模式的完整往返（包括按 Unity 的格式整体重新提交）。
- **GPU 测试**：默认跳过，需要权重和完整的 trellis2 环境才能运行。

### 4.10 仓库内的 TRELLIS.2 代码

- **来源**：`backend/third_party/TRELLIS.2/` 是官方仓库 https://github.com/microsoft/TRELLIS.2 在提交 `75fbf01` 的完整代码，未做任何修改，只去掉了 `.git`。
- **Eigen**：官方以嵌套 submodule 的方式引用 Eigen（`o-voxel/third_party/eigen`，`21e4582`），这里直接把文件复制进来，不再依赖外部仓库。
- **来源记录**：来源、许可证（TRELLIS.2 为 MIT，Eigen 为 MPL-2.0）和更新方法见 `backend/third_party/README.md`。
- **唯一来源**：后端只会从这份副本导入 TRELLIS.2，不读取任何外部路径，也没有用来指定外部路径的环境变量。
- **o-voxel 扩展**：从这份副本编译安装，命令是 `cd backend/third_party/TRELLIS.2 && bash setup.sh --o-voxel`。编译产物（`build/`、`*.egg-info/`、`*.so`）已加入 `.gitignore`。
- **测试配置**：`pytest.ini` 设置了 `testpaths = tests`，并排除 `third_party`，所以不会去收集官方代码里的测试。
- **BiRefNet**：`backend/third_party/BiRefNet/` 是公开的 `ZhengPeng7/BiRefNet` 模型代码（Hugging Face 提交 `e2bf8e4`，MIT 许可证），原样复制，只多加了一个 `__init__.py`。
  - `backend/app/birefnet_local.py` 里的 `LocalBiRefNet` 继承 TRELLIS.2 的去背景包装类，只改了 `__init__`：用仓库内的代码构建模型，再加载 `ZhengPeng7/BiRefNet` 的权重。预处理和推理沿用官方实现，官方代码一行没改。
  - `TrellisGenerator.load()` 在构建 pipeline 的过程中把 `rembg.BiRefNet` 临时替换成 `LocalBiRefNet`，构建完马上恢复。
  - 为什么不用 RMBG-2.0：它的仓库受限（非商用许可证），而且自带的 `birefnet.py` 和公开版不一样，现在拿不到。
  - 已验证：在另一个环境（transformers 4.57）中离线导入并构建了模型，CPU 前向推理输出 1024×1024 的掩码。
  - 未验证：没加载真实权重，也没在 `trellis2` 环境（transformers 5.9）中跑过。
  - 这份代码还依赖 `timm`、`kornia`、`einops`，`trellis2` 环境里目前缺 `timm`。
  - 官方代码里另有一处 `torch.hub.load('facebookresearch/dinov2')`，会从 GitHub 下载代码，但它只在 DINOv2 提取器和训练代码里用到，`'1024'` 流程不会执行到。
- **API 一致性**：与之前对照核验所用的本地副本（`5565d24`）相比，后端用到的这些文件在 API 上完全相同：`trellis2_image_to_3d.py`、`base.py`、`rembg/BiRefNet.py`、`image_feature_extractor.py`、`o_voxel/postprocess.py`、`mesh/base.py`。

## 5. Unity

### 5.1 文件

| 文件 | 内容 |
|---|---|
| `ModelGenerationServerTest_Voxel.cs`（含 `GeneratedVoxel`） | 协议字段、体素增删移接口、提交、GLB 对齐 |
| `QuestPro/QuestVoxelEditor.cs` | 新建。选区、四种模式、按格子步进的拖动、Add 落点与预览 |
| `QuestPro/QuestProControllerInput.cs` | 增加读取 Grip（`grip`）和 A/X（`primaryButton`） |
| `QuestPro/QuestProTriggerInteractor.cs` | 刷选、清空、把 Trigger 操作分发给编辑器、射线颜色 |
| `QuestPro/QuestVoxelBatchRenderer.cs` | 显示优先级改为：悬停 > 选中 > 原色 |
| `QuestPro/QuestRayButton.cs` | 增加 `IsActive`，用于高亮当前模式按钮 |
| `QuestPro/QuestProVoxelExperience.cs` | 面板改为 540×480，增加四个模式按钮和 Submit Edits |
| `Editor/QuestPro/QuestVoxelEditorValidation.cs` | 新建。编辑器逻辑的检查脚本 |
| `Editor/QuestPro/QuestProPlayModeValidation.cs` | 按名称查找按钮；增加回传往返的检查 |
| `Editor/QuestPro/QuestProInputValidation.cs` | 增加 Grip 和 A/X 的读取检查 |
| `Editor/QuestPro/QuestProProjectSetup.cs` | Configure 时把 Output Mode 设为 Both、Request Timeout 设为 600 秒 |

新建的两个脚本都带有提交进仓库的 `.meta` 文件。

### 5.2 协议字段与体素数据层

- **响应和请求字段**：
  - `ServerResponse` 增加 `generation_id`、`revision`、`model_space`、`revisions_url`、`generation_url`；`VoxelData` 增加 `id`。
  - 回传请求使用 `RevisionRequest` / `EditedVoxel`，字段都是 public，以便 `JsonUtility` 序列化。
- **`GeneratedVoxel`**：
  - 新增 `id`、`voxelType`、`color` 三个字段；`x/y/z` 的含义改为**当前所在格子**（以前是原始坐标）。
  - `IsOutsideGrid` 由坐标推算。
  - 静态方法 `GridToLocal` / `LocalToCell` 负责格子坐标和局部坐标之间的转换。
  - 修改 `voxelSize` 时按当前格子重新摆放，编辑结果不会丢失。
- **数据层接口**（编辑器只通过这些接口修改体素）：
  - `CreateVoxel(id, cell, type, color)`：体素数已达 `maxVoxels` 时返回 null。
  - `DestroyVoxel`：先 `SetActive(false)` 再 `Destroy`，让批量渲染在同一帧内就跳过它。
  - `MoveVoxelToCell`。
  - 这些接口都会维护 `GeneratedVoxelCount`，并登记到批量渲染器。
- **反射兼容**：现有验证脚本通过反射使用的私有类型和方法签名保持不变，包括 `ServerResponse`、`BuildVoxelsRoutine(ServerResponse)` 和若干序列化字段名。

### 5.3 GLB 与体素对齐

响应中 `model_space == "trellis_unit_cube"` 时，GLB 根节点设置为：

```
localScale    = voxelRootScale · (G · voxelSize)              # 64 × 0.015 = 0.96
localPosition = voxelRootPosition + Scale(voxelRootScale, (0, (G-1)/2 · voxelSize, 0))  # 缩放为 1 时上移 0.4725 m
```

- **推导**：TRELLIS 中体素中心为 `t = (i+0.5)/64 - 0.5`；GLB 导出时做了 `(x,y,z)→(x,z,-y)` 变换；再假设 glTFast 导入时翻转 X 轴。三者合起来，体素和 GLB 的顶点在 Unity 中重合。
- **刷新时机**：修改 `voxelSize` 时 GLB 会同时重新对齐，即使场景里没有体素也会。
- **fixture 模式**：响应不带 `model_space`，GLB 摆放保持原样。演示 GLB 是一栋不相关的房子，不会与体素重合。

### 5.4 提交与失败处理

1. **收集**：Submit Edits 遍历所有激活且 `!IsOutsideGrid` 的体素，按 `{id, x, y, z, type, color}` 发到 `revisions_url`，请求体里带上 `base_revision` 和 `max_voxels`。
2. **成功**：GLB 和体素都按响应重建，选区清空。状态栏显示 `Revision {n}: {count} voxels ready.`；有体素因越界被排除时，再追加 ` {k} voxels outside the grid were removed.`。Get 3D Model 成功后也使用同样的格式（`Revision 0: …`）。GLB 加载失败时，状态栏末尾追加 ` GLB failed to load; see Console.`。
   - 响应没有 `model_url` 时会清掉旧的 GLB，避免旧版本的 GLB 留在新体素旁边。
3. **失败**：状态栏显示服务端的错误文案，场景中的体素和 GLB 都不动，可以直接重试。
4. **按钮可用条件**：Submit Edits 按钮只有同时满足以下条件时才可用：
   - 不在请求中，编辑器不忙，已经拿到 `revisions_url`。
   - 当前的编辑器属于最新建好的这组体素，所以模型重建到一半时不可能把不完整的集合当作删除提交上去。
5. **超时**：生成和提交都使用 `Request Timeout Seconds`，默认值改为 600 秒，等实测后再调整。
   - 请求超时时，状态栏显示 `Request timed out after {N} s; the server may still be working. Wait, then retry.`。
   - 其他网络失败仍然显示原来的 "Cannot reach generation server…"。
   - 判断超时的依据：Unity 返回的 "Request timeout" 错误文本，再加上已用时间作为兜底。
6. **版本过期时自动恢复**：
   - 超时之后，服务端可能仍在后台完成并保存了新版本。这时再按一次 Submit Edits，服务端会回复"版本过期"。
   - 如果服务端还在生成，这次提交会先排队等 GPU 锁，结束后同样回复"版本过期"。
   - Unity 收到这个错误后，会自动请求 `generation_url` 加载最新版本并重建场景，状态栏显示 `Server already had revision {r}; loaded it. Re-apply edits made since then.`。
   - 超时之后在本地做的编辑需要重新做一遍。
   - 超时的 Get 3D Model 无法这样恢复，只能重新生成。
7. **场景配置**：`QuestProProjectSetup.Configure`（菜单 "Configure VR Voxel Scene"）现在把 Output Mode 设为 **Both**（以前是 Voxels），并把 Request Timeout 设为 600 秒。

### 5.5 编辑器：操作与模式

| 输入 | 作用 |
|---|---|
| 射线 + Trigger 指向面板按钮 | 按下按钮（优先于其他操作） |
| **按住 Grip** + 射线扫过体素 | 体素加入选区，显示为洋红色（左右手共用一个选区） |
| **A / X** | 清空选区 |
| Trigger（Move / Fill / Erase 模式） | 抓住体素拖动：抓的是选区内的体素就整个选区一起拖，否则只拖这一个 |
| Trigger（Add 模式） | 放置新体素；按住扫动可以连续放置 |
| B / Y、左右摇杆 | 保持原样（菜单回到视线前方、升降、平移） |

**拖动（Move / Fill / Erase）**：

- **按格子步进**：拖动时按格子步进，只平移不旋转，体素始终位于格子上。
  - 目标偏移的算法：把抓取点在 `voxelRoot` 局部坐标中移动的距离除以 `voxelSize`，再四舍五入。
  - `TrailSteps` 用整数版的 3D DDA 把这段位移拆成若干步，每步只沿一个轴走一格（6 连通），所以快速拖动也不会跳格。
  - 每帧最多走 64 步，剩下的留到下一帧，防止追踪跳变时卡住整帧。
- **三种模式每走一步的效果**：
  - **Move**：只移动。与其他体素重叠的格子在提交时由后端合并。
  - **Fill**：组离开的格子中，如果在网格内且没有被占据，就留下一份同 type、同 color 的副本（`id=-1`）。
  - **Erase**：组进入的格子中，原有的不属于本组的体素被删除。
- **模式锁定**：一次拖动在开始时就确定了模式，中途切换模式不影响这次拖动。

**新增（Add）**：

- **落点**：
  - 射线指向某个体素的一个面时，落点是该面外侧的相邻格，颜色复制被指的体素。
  - 射线没有指到体素时，落点是手柄前端 5 cm 处所在的格子，使用默认色。
- **连续放置**：按住 Trigger 扫动时，同一笔里新放的体素不会成为下一次放置的依据，所以扫过表面只会铺一层，不会朝自己越堆越高。在空中放置时会补齐中间的格子，同样每帧最多 64 格。
- **预览**：每只手各有一个半透明方块预览落点；落点已被占据或越界时不显示。

**通用规则**：

- **数量上限**：体素总数达到 `Max Voxels` 后，Fill 和 Add 停止生成，状态栏显示 `Max Voxels reached; <操作> stopped.`。
- **同时只能一个操作**：同一时刻只能有一个拖动或一笔放置；另一只手此时按 Trigger 不起作用，但面板按钮仍然可以按，Grip 刷选也不受影响。
- **请求期间禁止编辑**：请求进行中时不能开始拖动或放置，因为请求返回后的重建会覆盖这些编辑。
- **生成的体素不走自由拖动**：属于生成模型的体素永远不走原来的自由拖动路径；模型重建期间，编辑器还没有挂上，这些体素暂时不可抓。验证脚本里的普通物体仍然走原来的自由拖动路径。

### 5.6 面板与颜色

- **面板**（canvas 540×480）：
  - 标题：y=195。
  - 说明文字：y=125。
  - 模式按钮行：y=45，`Move` / `Fill` / `Erase` / `Add` 四个按钮，各 120×64，当前模式高亮为橙色。
  - 动作按钮行：y=-45，`Get 3D Model` 和 `Submit Edits`，各 220×80。
  - 状态文字：y=-140；控制器状态：y=-207。
- **颜色**：
  - 体素：悬停为青色，选中为洋红 `(1, 0.35, 0.9)`。
  - 拖动或放置时的射线：Move 橙、Fill 绿、Erase 红、Add 紫。
  - 按住 Grip 时的射线：洋红。

### 5.7 验证脚本（在 Windows 上运行）

运行前确认只有 fixture 后端在监听 8000 端口，即没有设置 `VRCRAFTER_GENERATOR`。在 PowerShell 中，把 `$U` 设为 6000.3.15f1 的 `Unity.exe`，`$P` 设为工程目录，然后依次运行：

```
& $U -batchmode -quit -projectPath $P -executeMethod QuestProProjectSetup.ConfigureAndValidate -questReport $P\Logs\setup.txt -logFile $P\Logs\setup.log
& $U -batchmode -quit -projectPath $P -executeMethod QuestProPerformanceValidation.RunBatch -questPerformanceReport $P\Logs\perf.txt -logFile $P\Logs\perf.log
& $U -batchmode -quit -projectPath $P -executeMethod QuestProHighlightValidation.RunBatch -questHighlightReport $P\Logs\highlight.txt -logFile $P\Logs\highlight.log
& $U -batchmode -quit -projectPath $P -executeMethod QuestProInputValidation.RunBatch -questInputReport $P\Logs\input.txt -logFile $P\Logs\input.log
& $U -batchmode -quit -projectPath $P -executeMethod QuestVoxelEditorValidation.RunBatch -questEditorReport $P\Logs\editor.txt -logFile $P\Logs\editor.log
& $U -batchmode -projectPath $P -executeMethod QuestProPlayModeValidation.Run -questPlayReport $P\Logs\play.txt -logFile $P\Logs\play.log
```

最后一条没有 `-quit`，跑完会自己退出。预期结果：

- 所有报告都只有 `PASS:` 行，所有命令的返回码都是 0。
- `input.txt` 多出 4 行 `Grip and A/X read correctly for …`。
- `editor.txt` 有 32 行 PASS，最后一行是 `Submit Edits waits for the finished build's own editor`。
- `play.txt` 有 34 行 PASS，最后一行是 `The reloaded model submits as revision 3: Revision 3: …`；倒数第三行是 `A stale submit reloads the server's latest revision: Server already had revision 2; loaded it. …`。Play Mode 验证的总时限从 150 秒放宽到 240 秒。

最可能出问题的地方：

- 输入验证里新加的合成设备布局偏移量（grip 在 16、primaryButton 在 20）。
- 编辑器验证在编辑模式下依赖的物理射线检测。
- Play Mode 验证现在也会加载 GLB；glTFast 以前从没在 batchmode 下跑过。

## 6. 部署

Unity 和 Quest Link 在 Windows 上；TRELLIS.2 在 Linux 机器上（GPU0 为 24 GB，GPU1 为 48 GB）。先在 Windows 上确认网络是否可达：

```powershell
Test-NetConnection <Linux IP> -Port 8000
Test-NetConnection <Linux IP> -Port 22
```

| 方案 | Linux 启动 | Unity `Generate Url` | 注意 |
|---|---|---|---|
| A 局域网直连（8000 可达时） | `--host 0.0.0.0` | `http://<IP>:8000/generate` | 自动启动随之关闭，连不上时直接报错；端口对局域网开放，没有鉴权 |
| B SSH 隧道（只有 22 可达时） | `--host 127.0.0.1` | 不变 | Windows 上执行 `ssh -N -L 8000:127.0.0.1:8000 <user>@<IP>`；隧道没建好就进入 Play，Unity 会悄悄启动 fixture，所以每次都要先看 `/status` 的 `generator` |

## 7. 关键设计决定

| 决定 | 原因 |
|---|---|
| 网格固定 64³，使用 `pipeline_type='1024'`，一阶段 coords 同时作为体素和二阶段的输入 | 与 Unity 现有的网格和尺寸一致；GLB 与体素来自同一次采样，形状一致 |
| 体素颜色统一为 `#C8C8C8`（颜色不属于 TRELLIS.2 的职责） | 一阶段只输出占据信息，没有颜色 |
| 坐标映射可逆、不平移 | 编辑结果可以无损送回二阶段 |
| 体素超过上限时报错，不截断 | 截断会切掉模型一整侧 |
| 失败统一返回 HTTP 200 + `status: error` | 只有这样 Unity 才会显示服务端的错误文案 |
| 版本以 `revision.json` 为提交标记，id 单调分配 | 中途失败不会留下脏版本；删除后再新增也不会撞号 |
| 回传时 `base_revision` 必须等于最新版本 | 防止过期的编辑覆盖新版本 |
| 拖动按格子步进，只平移 | "轨迹"才有明确定义；VR 里看到的就是实际提交的 |
| fixture 模式支持完整往返 | Windows 上没有 GPU 也能测试 Unity 端 |

执行过程中做出的其他决定：

- 后端测试用独立的 `backend/.venv`，不改 TRELLIS 的 conda 环境。
- `docs/` 不进 Git。
- 同格合并时，id 为 -1 的新体素不参与重复 id 检查。
- 请求格式错误也返回 HTTP 200。
- Configure 改为设置 Both。
- Add 预览改成每只手各一个。
- 拖动时锁定开始时的模式。
- 生成模型的体素不走自由拖动。
- 请求期间禁止编辑。
- 新脚本带 `.meta` 文件。
- 最终审查后的一轮修复：
  - 超时提示与默认 600 秒超时。
  - 新增 `GET /generations/{id}`，版本过期时自动加载最新版本。
  - 编辑模式下销毁对象改用 `DestroyImmediate`。
  - 生成后的状态也显示 `Revision N`。
  - 回传时检查生成器是否一致。
  - GLB 上移量随 `voxelRootScale` 缩放。
  - GLB 加载失败会在状态栏提示。
  - 加强 Submit 按钮的可用条件。
  - `sys.path` 改为追加，不再插到最前面。

## 8. 未验证项与已知限制

**需要你在使用前完成的事**：

1. **在 Windows 上跑第 5.7 节的六条验证命令。** 这是 Unity 代码第一次在真实 Unity 中运行。
2. **准备 TRELLIS.2 的运行条件**：
   1. 用 Hugging Face 账号 Jet 登录，申请 DINOv3 ViT-L 的访问权限（RMBG-2.0 已经不需要了）。
   2. 在 `trellis2` 环境中执行 `cd backend/third_party/TRELLIS.2 && bash setup.sh --o-voxel`，从仓库内的代码编译 o-voxel，并安装 trimesh，以及 BiRefNet 需要的 timm、kornia、einops。
   3. 下载 6 个 checkpoint，具体命令见 `backend/README.md`。
   4. 运行 GPU 测试和 `scripts/measure_trellis.py`，根据实测结果设置 Unity 的 `Max Voxels` 和 `Request Timeout Seconds`。
3. **在头显中确认**：GLB 与体素是否重合、是否镜像、正面是否朝向用户。
   - 如果正面朝向不对，只能绕 Y 轴旋转映射（四种映射写在计划文档第 3 节里）。
   - 不要只翻转单个轴，否则会得到镜像。
4. **`trellis2` 环境已有改动**：Task 1 开始时（在你要求不修复环境之前），已经往 `trellis2` 环境中补装了一些缺失的包。只是新增，没有升级或降级任何已有的包，之后 `pip check` 不再报告缺失依赖。如需还原，卸载下面这些包即可：
   - `nvidia-nvjitlink-cu12==12.4.127`
   - 原本缺失的依赖：asttokens 3.0.2、charset-normalizer 3.5.2、click 8.5.0、dill 0.4.1、exceptiongroup 1.3.1、filelock 4.1.0、frozenlist 1.8.0、fsspec 2026.9.0、imageio 2.38.0、jinja2 3.1.6、pandas 2.3.3、prompt-toolkit 3.0.53、protobuf 7.36.2、psutil 7.2.2、pure-eval 0.2.4、pygments 2.21.0、python-dateutil 3.0.0、regex 2026.9.29、six 1.17.0、tqdm 4.70.1、traitlets 5.16.1、typing-inspection 0.4.4、tzdata 2026.5、wcwidth 0.9.4
   - 后端依赖带进来的：httptools 0.9.0、iniconfig 2.3.1、pluggy 1.7.0、pytest 8.4.2、python-dotenv 1.2.4、tomli 2.5.0、uvloop 0.23.0、watchfiles 1.3.0、websockets 16.1.1

**已知限制**：

- 体素颜色统一为灰色，不能调色。
- 不支持撤销 / 重做，不能单个取消选中，拖动时不能旋转选区。
- 生成是同步的：一次请求同时返回体素和 GLB，没有进度显示。
- 64³ 下的体素数可能超过 12000，具体要看实测。
- 选中色只在批量渲染的体素上显示，这是默认路径。
- `data/generations/` 会持续增长，需要定期手动清理。
- 方案 A 下端口对局域网开放且没有鉴权，上传大小也没有限制，只应在可信网络中使用。

## 9. 提交记录

```
f6ac674 feat(backend): vendor BiRefNet and build background removal without trust_remote_code
5ce95a2 refactor(backend): load TRELLIS.2 only from the vendored copy
2189620 chore(backend): vendor official TRELLIS.2 at 75fbf01 under backend/third_party
e578845 docs: GET /generations/{id}, timeout recovery, 600 s timeout and new expectations
5d5e071 fix(unity): recover stale submits, timeout status, 600 s timeout, stricter submit gate
f7e86c4 fix(backend): GET /generations/{id}, generation_url and generator-source check
5ca9edd docs: fix editor.txt last line and note earlier verification predates edit code
2956be5 docs: TRELLIS.2 voxel+GLB generation and VR edit round trip
f2fa7bf feat(backend): TRELLIS.2 1024 pipeline producing voxels and GLB
712682b fix(unity): no free drag of model voxels, no edits during requests, capped Add air lines, stronger editor validation
d7018fc feat(unity): selection, Move/Fill/Erase drag modes and Add placement
7378d51 fix(unity): Both output mode, drop stale GLB without model_url, re-align GLB on voxel size change
50d1d1b feat(unity): voxel ids, create/move/destroy API, Submit Edits and GLB alignment
ccf6134 fix(backend): return 200 error JSON for all JSON-route failures and extend API tests
ab966eb feat(backend): generation ids, voxel ids, revisions endpoint and per-revision GLB
d2b114e feat(backend): add on-disk generation and revision store
29167c9 chore: keep docs/ out of git
8e86b1d feat(backend): add TRELLIS-to-Unity voxel coordinate mapping
```
