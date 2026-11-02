# Mani

[English](README.md) | **简体中文**

![Mani 图标](src/Mani.App/Assets/Square44x44Logo.targetsize-256.png)

Mani 是一款受 [IINA](https://github.com/iina/iina) 启发的 Windows 媒体播放器，
使用 C#、WinUI 3、.NET 10 和 libmpv 构建，将 IINA 风格的交互和受 Liquid Glass
启发的视觉效果带到 Windows。

## 功能

- 打开多个文件、文件夹和 HTTP(S) 媒体地址，支持拖放。
- 播放列表及 M3U8 导出、章节与可选时间轴标记、媒体版本切换。
- 支持搜索和分组的播放历史、断点续播，以及可配置的本地文件缩略图。
- 浮动、顶部和底部播放控制栏，可调整的侧栏。
- 播放倍速、逐帧播放、A–B 循环和截图。
- 外挂音频、输出设备选择、增益上限，以及带预设的十段均衡器。
- 字幕匹配、编码重载、主副字幕 ASS 控制，以及共享文本样式。
- 全屏、画中画、音乐模式和多个播放窗口。
- 键盘、鼠标及原生手势控制。
- 界面默认英文，可切换简体中文并保存选择。

## 构建

环境要求：

- Windows 11 x64，系统版本 26100 或更高。
- .NET SDK 10.0.402，或由 [global.json](global.json) 选择的兼容补丁版本。
- Windows SDK 10.0.26100.0 及 WinUI 开发所需组件。
- Python 3.11 或更高版本，用于还原原生依赖。
- 启用 Windows 开发者模式，以运行本地开发包。

在仓库根目录执行：

```powershell
python build/restore_libmpv.py
dotnet restore Mani.sln --locked-mode -p:Platform=x64
dotnet build Mani.sln -c Debug -p:Platform=x64 --no-restore
dotnet msbuild src/Mani.App/Mani.App.csproj -target:RunPackagedApp -property:Configuration=Debug -property:Platform=x64
```

还原脚本会校验 [config/libmpv.lock.json](config/libmpv.lock.json)
中锁定的完整原生运行包，包括依赖和许可文件。归档及对应原生源码位于
[原生依赖附件](https://github.com/showmethecold/mani/releases/tag/native-sup-20261101-r2)。
运行包可公开下载。若使用已下载的归档还原，可执行
`python build/restore_libmpv.py --archive C:\path\to\mani-sup-msys2-ucrt64-20261101-r2.zip`。
原生二进制文件不纳入 Git。
NuGet 依赖由 [Directory.Packages.props](Directory.Packages.props)
及各项目的锁定文件固定。

## 测试

```powershell
dotnet restore Mani.Tests.slnf --locked-mode -p:Platform=x64
dotnet test --solution Mani.Tests.slnf --no-restore --configuration Release -p:Platform=x64
```

单元测试在 Windows x64 上运行，无需 libmpv 或交互式桌面。
[CI 工作流](.github/workflows/unit-tests.yml) 会在向 `main` 推送代码及提交
Pull Request 时运行测试，并编译 Debug 和 Release 版本的 WinUI 应用。
应用构建任务使用自身只读 GitHub 凭据获取锁定运行包。
真实引擎播放和窗口交互检查单独执行。

## Beta 测试包

维护者可以在 `main` 上手动运行 [Beta MSIX 工作流](.github/workflows/beta-package.yml)，
生成带签名、自包含且关闭诊断的 Windows x64 测试包，使用仓库已有的内部签名凭据。
下载工作流产物，校验 `SHA256SUMS.txt`，按 `INSTALL.txt` 信任随附的公共测试证书并安装 MSIX。
使用者无需开发工具，也不需要单独安装 .NET 或 Windows App Runtime。

[Beta 安装程序工作流](.github/workflows/beta-setup.yml) 将固定的 Beta 0.1 MSIX
和公共证书封装为带签名的 `Setup.exe`，提供中英文安装界面。
证书授权和 Windows 安装在同一流程中完成，仍可能需要管理员批准并显示 Windows 安全提示。
已有 MSIX、包身份和应用内容保持不变。

产物保留 14 天，下载权限跟随仓库权限。这些包属于预览测试包，不是公开稳定发行版。
[Internal MSIX 工作流](.github/workflows/internal-package.yml) 继续保留显式启用诊断的开发检查构建。

本地生成自包含、关闭诊断的 Beta 测试包可使用
`build/Build-InternalPackage.ps1 -Beta -Version 0.5.6.0 -UpgradeVersion 0.5.6.1`。
独立的 `Mani.Beta.Test` 身份保留其他 Mani 安装。
第二个包仅用于验证相同应用内容的升级流程。

## 许可

Mani 的原创代码采用 **GPL-3.0-only** 许可证。
详见 [LICENSE](LICENSE) 和 [Mani 声明](licenses/Mani-NOTICE.txt)。

源自 IINA 的代码和资源保留上游声明，详见
[IINA 致谢与声明](licenses/IINA-NOTICE.txt)。第三方组件保留各自的许可证。
应用内也可通过 **更多 > 关于 Mani** 查看随附的声明。
