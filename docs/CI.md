# Windows 与 macOS CI

工作流位于 [.github/workflows/ci.yml](../.github/workflows/ci.yml)，本地与 Actions 共用 [tools/ci.ps1](../tools/ci.ps1)。它不依赖 GitHub 仓库名称；当前解决方案为 Grove.slnx；内部 src/GitX.*、命名空间及 Core 程序集保持兼容；桌面程序集和启动文件为 Grove。

## 原生主机矩阵

| Runner | RID | 自动化范围 |
| --- | --- | --- |
| windows-2025 | win-x64 | 锁定恢复、Editor build／Node tests、完整 Core tests、Desktop build／publish、资源及 LibGit2 检查 |
| windows-11-arm | win-arm64 | 同上，在原生 Windows ARM64 runner 运行测试与 publish |
| macos-15 | osx-arm64 | 同上，在 Apple Silicon runner 运行测试与本机架构 publish |
| macos-15-intel | osx-x64 | 同上，在 Intel runner 运行测试与本机架构 publish |

以 global.json 安装 SDK，.node-version 安装固定 Node，NuGet 使用 --locked-mode、npm 使用 npm ci。四个 job 独立完成，失败不取消其他平台；每个 job 上限 25 分钟。Core 测试必须真实执行且 TRX 不能为空，挂起测试 3 分钟后终止且不生成内存转储。没有按平台整体跳过测试。

Git 测试使用随机专用临时仓库并设置本地作者，CI 使用全新 hosted runner；不提供用户凭据。脚本不覆盖 HOME、不编辑用户 Git 配置，也不设置会改变 Git 标准配置发现的 GIT_CONFIG_*。本地若配置自定义 hooksPath／filter 或 Git 环境覆盖，应先在独立副本审阅其影响；产品的 blame 缓存遇到未知覆盖会有意拒绝复用，不能靠跳过相关测试制造绿灯。

macOS 在启动测试子进程前把 TMPDIR 解析为同一临时目录的物理路径，避免系统的 /var 与 /private/var 别名导致 Git、LibGit2 与测试路径不一致；脚本退出时恢复环境值，不放宽产品的链接写入保护。

## 架构与本机依赖检查

四个 job 都要求 PowerShell、.NET 与 Node 的进程架构和操作系统一致；不把 x64 仿真或交叉发布计为 ARM64 原生测试。PlatformProbe 实际加载 LibGit2，Windows 还核对 apphost、LibGit2、Skia 和 HarfBuzz 的 PE 架构。结果写入 results/platform.json 和 metadata.json。

WebView 包采用托管 WebView2 loader；探针检查发布的托管程序集是否可用于目标位数，并记录已发现的 Windows WebView2 Runtime 版本和架构。系统 WebView2 Runtime 仍是用户机器的先决条件，不由 self-contained .NET 包提供；没有发现 Runtime 不等于浏览器可用。探针不创建 WebView，不构成 ARM64 或其他平台 GUI 验收。参见 [Avalonia 平台依赖](https://docs.avaloniaui.net/docs/app-development/embedding-web-content)。

## 产物与本地运行

执行 `pwsh ./tools/ci.ps1`，或显式传入与本机完全匹配的 `-RuntimeIdentifier`；默认输出 `artifacts/ci/<rid>`，目录已存在则拒绝覆盖，可用 `-OutputDirectory artifacts/ci/<新目录>`。

锁定还原、构建、Editor/Core 测试和架构探针后，`tools/package.ps1` 生成当前用户 Windows 安装程序/便携 ZIP，或 macOS Grove.app、DMG/app tar.gz。构建时下载并校验官方便携 NSIS，macOS 使用系统 sips/iconutil/codesign/hdiutil。脚本不使用发行证书和公证密钥，也不自动发布 Release。

`test-windows-package.ps1` 在独立的含空格路径内安装、逐文件验哈希、重装、卸载，并检查额外用户文件和配置哨兵仍存在。显式 /NOINTEGRATION 防止本机测试改动用户开始菜单/卸载注册项，CI 另在全新用户中自动检查开始菜单和卸载注册项，再确认卸载移除它们；GUI 验收仍单独记录。

`test-macos-package.ps1` 只读挂载 DMG、复制 Grove.app 到含空格路径、核对签名后文件清单和 Info.plist，并卸载磁盘映像；不启动 GUI。metadata 标注 nativeGuiTested=false、signedRelease=false；package-metadata 另标 macAdHocSigned 和 notarized=false。

上传的 desktop-<rid> artifact 含安装程序或 DMG、便携包和 SHA256SUMS；results-<rid> 含测试/平台/安装包检查结果及 publish/package 文件清单。Artifacts 保留 14 天，Release 分发不受该期限限制。调试符号不进入应用包。

## 触发与权限

分支 push、pull_request 和手动 workflow_dispatch 触发；不使用 pull_request_target、任意用户输入 shell、部署凭据或仓库写权限。仅 contents: read，checkout 不持久化凭据。官方 Actions 固定完整 commit SHA，版本在注释中；更新时同时核对上游 release 与 SHA，不用本机登录令牌写入 YAML。

Runner 标签依据 [GitHub 官方 runner 文档](https://docs.github.com/en/actions/reference/runners/github-hosted-runners)；Actions 输入与版本依据 [checkout](https://github.com/actions/checkout/releases/tag/v6.1.0)、[setup-dotnet](https://github.com/actions/setup-dotnet/releases/tag/v5.3.0)、[setup-node](https://github.com/actions/setup-node/releases/tag/v6.4.0)、[upload-artifact](https://github.com/actions/upload-artifact/releases/tag/v7.0.0)。

## 验收边界

Windows 本地执行不能证明 macOS 已通过；macOS 首次结果必须来自真实 runner。CI 的 Core 测试包含原生 LibGit2 加载与真实 Git 操作，但不启动桌面 GUI。metadata 明确 nativeGuiTested=false、signedRelease=false。WebView、输入法、Cmd 快捷键、焦点、Keychain／askpass、跨 DPI、窗口装饰及真正 Mac 启动仍需专门实机验收；即使两个 Mac job 通过也不改写该边界。

提交前审阅差异、完整依赖锁及字体／grammar 许可证。个人配置、验证截图、日志和构建产物不属于源码快照；忽略规则不能移除已被跟踪或已进入历史的文件。

## 原生标题栏探针

Mac CI 在构建后启动实际 Grove 窗口，使用临时仓库和独立配置。`ChromeProbe` 读取 AppKit 原生交通灯边界、命中区域、应用菜单名称和 backing scale，并在 1380、1100、880 DIP 宽度下与实际托管标题栏控件比较。随后对本进程窗口发送合成 NSEvent，检查项目、分支和主菜单能否打开；通过原生按钮动作检查最小化、缩放和关闭。测试不操作其他进程窗口，不使用用户项目。

结果保存在 `results/chrome-native.json`。`nativeChromeTested` 表示这些程序化原生场景通过；`physicalInputTested=false`，`nativeGuiTested=false` 仍表示未完成物理输入、全应用、所有 DPI/多屏和长期人工 GUI 验收。包安装检查与此探针分别报告，不能用一个替代另一个。

## 本地归档与日常开发

日常工作区使用 GitHub 清理快照的历史，与远端正常 fetch、提交和 push。旧完整历史、原有索引、未提交／未跟踪内容和本机验证材料保留在独立的本机归档，不作为当前仓库的 refs 或 objects 导入，也不通过合并无共同祖先历史来同步。

迁移旧改动时先逐项比较：已经包含在上游的改动保留最新实现，独有内容单独迁移并验证；原始字节与旧仓库状态仍可从归档恢复。个人配置、截图、历史归档和构建产物不纳入源码提交。

本机工作区的实际路径、恢复步骤及本地推送保护记录在不提交的 LOCAL-WORKSPACE.md。该保护会拒绝推送旧历史或混合历史，但不自动安装到其他克隆；发布前仍需核对提交范围与隐私。产物 metadata 的文件哈希清单包含隐藏构建文件。
