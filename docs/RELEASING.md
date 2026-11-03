# Release 分发

## 当前预发布版

[Grove 0.1.0 Preview 1](https://github.com/nowins/Grove/releases/tag/v0.1.0-preview.1) 已在私有仓库发布。版本标签 `v0.1.0-preview.1` 精确指向 `a48e6cb041922838364e9ebd7b84fdc0c3dd0539`；应用包来自[四平台成功 CI 41587178858](https://github.com/nowins/Grove/actions/runs/41587178858)，每个平台 Core 509/509、Editor 78/78，无跳过。随后 main 的文档更新不改变此版本的二进制来源。

Release 提供四个包含版本与架构的 `Grove-...` 应用压缩包、汇总 `SHA256SUMS.txt` 和 `BUILD-INFO.json`。四个包与 CI 产物字节相同，逐文件清单、隐藏构建标记、许可证、样式、CPU 架构与 macOS 可执行权限已核对。GitHub 自动生成的 Source code ZIP / tar.gz 是源码，不是应用包。

Release 资产不受 Actions 的 14 天保留期限制，但仍受仓库权限与资产删除影响。私有仓库下载需要 GitHub 登录及读取权限。预发布版不作为稳定 Latest；使用具体版本的发布页链接。

## 运行与验收边界

- Windows x64 / ARM64：self-contained ZIP，运行 `GitX.Desktop.exe`，需要系统 Git 与 WebView2 Runtime。
- macOS Apple Silicon / Intel：保留执行位的 self-contained tar.gz，在解压目录终端运行 `./GitX.Desktop`。当前没有 `.app`、Info.plist、应用图标封装或 DMG。
- 未做发行签名或 macOS 公证。CI 原生运行 .NET、Node、LibGit2 与架构探针，不能代替 GUI 验收；GUI 目前仅 Windows x64 在本机实测，其他架构 GUI 尚未实机验收。
- 不提供安装器或自动更新。部分内部标识与数据目录保留 GitX 以兼容配置。

## 后续发布检查

1. 确定具体版本、准确代码提交和发布范围，避免隐式把未经验证的最新 main 当作二进制来源。
2. 对代码变更使用该准确提交的四平台成功 CI。仅文档更新时，可以继续引用既有代码产物，但必须明确原始提交，不能把文档提交标成二进制构建来源。
3. 逐项检查测试、运行资源、字体与 grammar 许可证、架构、可执行权限和哈希。仅上传必要应用包与分发元数据，不包含本机路径、验证截图、用户配置、归档或旧 Git 历史。
4. 使用不冲突的版本/架构文件名、汇总校验值和构建来源说明；检查同名标签和 Release，不能覆盖无关内容。具备用户授权后再创建准确标签并发布，保持预发布与稳定版标记准确。
5. 发布后用有读取权限的账号实际下载资产并重新核对哈希、数量、私有属性与发布状态；保留本机核验记录。

## macOS 正式分发前的缺口

- 在 macOS runner 生成 `Grove.app/Contents/{MacOS,Resources}`，补全 Info.plist、图标与版本信息，保留运行资源和可执行权限。
- 在 Apple Silicon 与 Intel 实机验收启动、WebView、Editor、Git 凭据、焦点、Cmd 快捷键、输入法与 DPI。
- 确定应用标识和 Developer ID 后再处理 hardened runtime、发行签名、公证、staple 以及 DMG 分发。
- Windows 若要安装体验，另行确定安装器、应用标识和签名方案；当前 ZIP 不代表安装器验收。

参考：[GitHub Releases](https://docs.github.com/en/repositories/releasing-projects-on-github/about-releases)、[Actions 产物下载](https://docs.github.com/en/actions/how-tos/manage-workflow-runs/download-workflow-artifacts)、[Avalonia macOS 分发](https://docs.avaloniaui.net/docs/deployment/macos)。
