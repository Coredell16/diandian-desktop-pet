# 点点桌宠

Windows 桌面宠物项目，同时维护二维与 Unity 三维两条版本路线。

- **v1 二维版**：Python 桌宠、帧动画、叫声、互动和可视化编辑器。
- **v2 三维版**：Unity 工程，包含三维模型、骨骼、展翅和桌面窗口功能。

详细使用、构建与版本记录见 [项目说明.md](项目说明.md)。

## 开发入口

- 二维版：`app/`；本机可双击 `编辑点点.cmd` 打开编辑器。
- 三维版：用 Unity **6000.6.7f1** 打开 `unity/Diandian/`。
- 二维打包：在安装了项目依赖的 Python 环境中运行 `python build.py`。
- 第三方许可：`licenses/`。

## 从 GitHub 获取项目

先安装 Git 和 Git LFS，再运行：

```powershell
git lfs install
git clone https://github.com/Coredell16/diandian-desktop-pet.git
```

大型三维素材通过 Git LFS 保存；若克隆时未取回素材，在项目目录运行 `git lfs pull`。

## GitHub 同步范围

源码、必要图片/音频/模型、Unity 的 `Assets/`（含 `.meta`）、`Packages/` 与 `ProjectSettings/` 应纳入版本管理。

`.gitignore` 排除 Unity 缓存、日志、本机设置、Python 缓存，以及 `outputs/` 和构建临时目录。忽略规则不会删除本机文件；历史安装包和归档仍保留在本机，不会随源码自动备份。

需要分享安装包时，可另行上传到 GitHub Releases。修改源码后，需要提交并推送才会同步到 GitHub；朋友的客户端自动更新还需要单独配置发布地址，详见项目说明。

## 问题反馈

使用中如有问题，请联系邮箱：[coredell116@gmail.com](mailto:coredell116@gmail.com)。
