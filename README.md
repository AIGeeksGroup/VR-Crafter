# VR-Crafter: Artistic 3D Generation Workflow with Virtual Reality

紫色暗夜风格的静态研究项目页，无需 npm 或构建步骤，直接打开 `index.html` 即可。

也可以启动本地预览：

```sh
cd /Users/zeyuzhang/VR-Crafter
python3 -m http.server 8000
```

然后访问 <http://localhost:8000>。

## 页面内容

- 研究动机：平面操作 3D 的局限，以及 VR 空间交互的研究方向。
- ART-DECO 桌面交互视频与三组案例（包含来源链接）。
- `video/img-3d-vr.mp4`：image → 3D → AR/VR 编辑 → 再生成的早期原型。
- `video/vr-demo.mp4`：头显内交互录像，以及配套第三人称拍摄方案。

第三人称区域是明确标注的拍摄示意，后续可替换为真实录像。首页网格为 Canvas 数学曲面插图，可拖动或用方向键旋转，并可暂停动画。

## 文件

- `index.html`：内容和结构。
- `styles.css`：响应式布局与配色。
- `script.js`：网格、案例切换、图片放大、章节导航。
- `assets/`：本地校徽、字体、ART-DECO 素材与视频封面。
- `SOURCES.md`：外部素材来源。

页面不依赖 CDN，素材均通过相对路径引用，可作为静态网站部署。
