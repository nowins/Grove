# Windows 与 macOS CI

工作流位于 [.github/workflows/ci.yml](../.github/workflows/ci.yml)，本地与 Actions 共用 [tools/ci.ps1](../tools/ci.ps1)。它不依赖 GitHub 仓库名称；当前解决方案为 Grove.slnx；内部 src/GitX.*、命名空间及程序集保持兼容。

## 原生主机矩阵

| Runner | RID | 自动化范围 |
| --- | --- | --- |
| windows-2025 | win-x64 | 锁定恢复、Editor build／Node tests、完整 Core tests、Desktop build／publish、资源及 LibGit2 检查 |
| macos-15 | osx-arm64 | 同上，在 Apple Silicon runner 运行测试与本机架构 publish |
| macos-15-intel | osx-x64 | 同上，在 Intel runner 运行测试与本机架构 publish |

以 global.json 安装 SDK，.node-version 安装固定 Node，NuGet 使用 --locked-mode、npm 使用 npm ci。三个 job 独立完成，失败不取消其他平台；每个 job 上限 25 分钟。Core 测试必须真实执行且 TRX 不能为空，挂起测试 3 分钟后终止且不生成内存转储。没有按平台整体跳过测试。

Git 测试使用随机专用临时仓库并设置本地作者，CI 使用全新 hosted runner；不提供用户凭据。脚本不覆盖 HOME、不编辑用户 Git 配置，也不设置会改变 Git 标准配置发现的 GIT_CONFIG_*。本地若配置自定义 hooksPath／filter 或 Git 环境覆盖，应先在独立副本审阅其影响；产品的 blame 缓存遇到未知覆盖会有意拒绝复用，不能靠跳过相关测试制造绿灯。

macOS 在启动测试子进程前把 TMPDIR 解析为同一临时目录的物理路径，避免系统的 /var 与 /private/var 别名导致 Git、LibGit2 与测试路径不一致；脚本退出时恢复环境值，不放宽产品的链接写入保护。

## 运行与产物

准备 PowerShell 7、Git、global.json 允许的 .NET SDK 与 .node-version 的 Node，在项目根目录执行：

```powershell
./tools/ci.ps1
# 默认识别当前主机；也可显式选择与主机完全匹配的 RID。
./tools/ci.ps1 -RuntimeIdentifier win-x64 -OutputDirectory artifacts/ci/local-run-2
```

脚本拒绝跨主机 RID、artifacts 以外的输出目录或已存在的输出，绝不自动递归清理。结果位于 artifacts/ci/<rid>/：results 日志与 TRX、metadata.json 的文件哈希及平台状态、publish 目录、desktop-<rid>.zip（Windows）或 .tar.gz（macOS）和 SHA256SUMS.txt。tar 保留 Unix 执行权限。Actions 只上传明确的测试结果／metadata 和打包文件，保留 14 天；不上传整个工作区、用户目录或应用数据。

产物为 self-contained、未裁剪、非单文件的原生 apphost 与依赖，不要求目标机器另装 .NET；仍需 Git 和对应平台的原生 WebView 条件。Windows 解压后运行 GitX.Desktop.exe；macOS 解包后从终端运行 ./GitX.Desktop。它不是安装程序、签名发布或可在 Finder 分发的 .app。本轮没有加入证书、Apple 账号、签名、公证、自动发布或 GitHub Release。

## 触发与权限

分支 push、pull_request 和手动 workflow_dispatch 触发；不使用 pull_request_target、任意用户输入 shell、部署凭据或仓库写权限。仅 contents: read，checkout 不持久化凭据。官方 Actions 固定完整 commit SHA，版本在注释中；更新时同时核对上游 release 与 SHA，不用本机登录令牌写入 YAML。

Runner 标签依据 [GitHub 官方 runner 文档](https://docs.github.com/en/actions/reference/runners/github-hosted-runners)；Actions 输入与版本依据 [checkout](https://github.com/actions/checkout/releases/tag/v6.1.0)、[setup-dotnet](https://github.com/actions/setup-dotnet/releases/tag/v5.3.0)、[setup-node](https://github.com/actions/setup-node/releases/tag/v6.4.0)、[upload-artifact](https://github.com/actions/upload-artifact/releases/tag/v7.0.0)。

## 验收边界

Windows 本地执行不能证明 macOS 已通过；macOS 首次结果必须来自真实 runner。CI 的 Core 测试包含原生 LibGit2 加载与真实 Git 操作，但不启动桌面 GUI。metadata 明确 nativeGuiTested=false、signedRelease=false。WebView、输入法、Cmd 快捷键、焦点、Keychain／askpass、跨 DPI、窗口装饰及真正 Mac 启动仍需专门实机验收；即使两个 Mac job 通过也不改写该边界。

提交前审阅差异、完整依赖锁及字体／grammar 许可证。个人配置、验证截图、日志和构建产物不属于源码快照；忽略规则不能移除已被跟踪或已进入历史的文件。
