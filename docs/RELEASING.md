# Release 准备与验收

## 当前情况

目前没有 GitHub Release 或版本标签发布。本项目的 Actions 上传四个平台的测试结果与未签名 self-contained 应用包，保留 14 天；这些是 CI artifact，不是安装程序或长期版本下载。私有仓库的下载者需要对应读取权限和 GitHub 登录。

Windows 包为 ZIP，入口 `GitX.Desktop.exe`，需要系统 Git 与 WebView2 Runtime。macOS 包为保留执行位的 tar.gz，入口 `GitX.Desktop`；当前没有 `.app`、Info.plist、应用图标封装、DMG、Developer ID 签名或公证。CI 原生运行 .NET／Node／LibGit2 并检查 Windows 原生 DLL 架构，不能代替各架构的桌面验收。

## 可执行的首个预览版方案

1. 由维护者确认版本号、准确提交和发布范围；预览版本号仅可作为提议，例如 `v0.1.0-preview.1`，不视为已选定。
2. 以该准确提交的四平台成功 CI 为输入。逐项核对 Core／Editor 结果、包内编辑器与 grammar／许可证资源、CPU 架构、执行权限和 SHA-256；保留原始 metadata。新文档与本地迁移材料不自动进入应用包。
3. Windows x64／ARM64 先提供明确标注“未签名、便携预览”的 ZIP。macOS 若沿用当前 tar.gz，必须写明终端启动、未签名未公证以及 GUI 未验收；不得标成 Finder 安装包或已完成桌面兼容验收。
4. 为四个应用包提供互不冲突的文件名和一份汇总 `SHA256SUMS.txt`，附准确提交、变更、先决条件和验证边界。GitHub 自动生成的 Source code ZIP／tar.gz 是源码，不是应用包。
5. 具体版本与产物获得授权后，才创建准确提交上的标签、上传二进制资产并发布预览 Release。发布后以有读取权限的账号重新下载并验证哈希和资产清单；仓库私有属性保持现状。

Release 资产不沿用 Actions 的 14 天保留策略，但仍受仓库访问权限和资产删除影响。尚未获批的版本不创建标签、不创建或发布 Release；也不引入签名证书或安装额外发布服务。

## macOS 正式分发前的缺口

- 在 macOS runner 生成 `Grove.app/Contents/{MacOS,Resources}`、正确的 Info.plist、图标与版本信息，保留所有可执行文件权限。
- 在 Apple Silicon 与 Intel 上实际检查启动、WebView／离线 Editor 资源、Git／凭据、焦点、Cmd 快捷键、输入法与 DPI。
- 确认应用标识、Developer ID 证书和签名方式后，按 hardened runtime 要求签名、验证、公证并 staple；DMG 在应用包验证后制作。
- Windows 若需要安装体验，另行确定 MSIX／安装器与代码签名方案。当前 ZIP 不声明安装器或可信发布者签名。

参考：[GitHub Releases](https://docs.github.com/en/repositories/releasing-projects-on-github/about-releases)、[Actions 产物下载](https://docs.github.com/en/actions/how-tos/manage-workflow-runs/download-workflow-artifacts)、[Avalonia macOS 部署](https://docs.avaloniaui.net/docs/deployment/macos)。
