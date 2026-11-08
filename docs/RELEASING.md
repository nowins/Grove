# Release 分发

## 0.1.0 Preview 6

已发布 [`v0.1.0-preview.6`](https://github.com/nowins/Grove/releases/tag/v0.1.0-preview.6)，来源为 `e725943262251d8669beba8dccbed7f230c997b3`，仅使用该准确提交的 [六平台成功 CI 42467241304](https://github.com/nowins/Grove/actions/runs/42467241304) 普通 self-contained 构建。安装包和 apphost 均使用 Grove；内部命名空间、Core 程序集及已有用户数据目录仍保留兼容标识。此前 `v0.1.0-preview.1`、`v0.1.0-preview.2` 、`v0.1.0-preview.3` 、`v0.1.0-preview.4` 和 `v0.1.0-preview.5` 保留，不覆盖其标签或资产。

| 平台 | 分发内容 | 自动验证 |
| --- | --- | --- |
| Windows x64 / ARM64 | 当前用户 NSIS 安装程序、便携 ZIP | 原生架构、全部安装文件哈希、安装/重装/卸载、额外用户文件保留 |
| macOS Intel / Apple Silicon | DMG、含 Grove.app 的 tar.gz | 原生构建、Info.plist、图标、执行位、ad-hoc 签名、DMG 只读挂载、复制到含空格路径、包内文件哈希 |
| Linux x64 / ARM64（首次实验性发布） | 便携 tar.gz | 原生构建、LibGit2、ELF 架构、逐文件哈希、解包数量与执行位；不代表 GUI/IME/askpass 验收 |

Windows 安装程序外壳是 NSIS x86 Unicode，可在受支持的原生 x64 / ARM64 系统运行；载荷按对应架构原生发布。默认安装到 `%LocalAppData%/Programs/Grove`，只为当前用户创建开始菜单和卸载入口，不改全局 PATH、文件关联或系统 Git 设置。`/NOINTEGRATION` 供隔离验证和不需要系统入口的安装使用；没有此参数时正常创建入口。

卸载按构建时清单删除应用文件并尝试移除空目录，不递归删除所选安装目录，不删除 GitX/Grove 用户配置、评论、WebView 数据或仓库。重装会覆盖同名应用文件；安装时应先关闭 Grove，文件占用交给安装程序提示，不强杀应用。

macOS 包标识为 `io.github.nowins.grove`，CFBundleExecutable 为 Grove，内容位于 `Contents/MacOS`，图标位于 `Contents/Resources`。DMG 提供 Applications 入口。Developer ID 证书和公证凭据尚未配置，只有 ad-hoc 完整性签名；Gatekeeper 可能拒绝来自网络的应用。不要把签名完整性检查等同于 Apple 信任、公证或 GUI 验收，也不要为运行关闭系统安全保护。

外部依赖仍为 Git，Windows 另需 WebView2 Runtime；安装包不静默安装这些依赖。macOS Finder 的 PATH 可能与终端不同。Preview 4 提供匿名更新检查与可取消后台下载，默认关闭自动检查和后台下载，安装始终需要确认；macOS 确认后只打开 DMG，由用户自行替换。仍未提供受信任发行证书签名或完整六平台人工 GUI 验收。

本次发布已完成的阅读区与工具侧栏简化，连同文件／内容搜索、阅读位置导航、连续 Diff、Windows ARM64 路径状态修正和既有联合功能。使用此前准备的 `preview.6`／Mac 构建号 6，准确标签指向上述六平台验证提交，未复用较早开发包冒充新界面。每平台 Core 974/974、Editor 169/169，零跳过；六份归档的 1,558 个清单文件、12 个发布资产的服务端摘要和匿名重下载均已核对，详见 STATUS。后端性能回归工具属于开发验证，不宣称 GUI 提速；旧引用释放也不能宣称为整体内存或工作集收益。

已知阅读边界保留在发布说明：仓库刷新使连续 Diff 失效，需要重新打开；F7／上一处差异仍仅在当前完整文件内循环。当前 Windows 原生截图捕获失败，新工具侧栏／底部状态栏完整绘制和用户 Mac 实机布局仍待验收，不能用浏览器截图替代。上述限制不扩大成 IDE 能力。

Linux 仅手动更新，依赖与运行要求见 [LINUX](LINUX.md)。Mac 两架构的 AppKit 窗口探针属于 runner 自动化；用户 Mac、物理输入、Retina/多屏和 PyCharm 视觉对照仍未验收。Windows 没有发行证书签名，Mac 没有 Developer ID 或公证。

## 构建与来源

`Directory.Build.props` 的 VersionPrefix/VersionSuffix 定义发布版本，MacBundleVersion 定义递增的 macOS 构建号。`tools/ci.ps1` 执行锁定还原、构建、Editor/Core 测试和平台探针，然后调用 `tools/package.ps1` 及对应安装包验证脚本。Windows 编译器由 `tools/ensure-nsis.ps1` 下载 NSIS 官方便携 3.13，并以固定 SHA-256 校验，不安装系统软件。

调试符号留在诊断构建目录，不进入最终应用包。`metadata.json` 描述原始 publish，`package-metadata.json` 描述实际分发文件；macOS bundleFiles 记录 ad-hoc 签名后的完整 app 内容。每个 CI 产物含应用包和 SHA256SUMS；发布合并为一个 SHA256SUMS.txt，并附 BUILD-INFO.json，明确代码 SHA、CI run、架构、测试与签名/GUI 边界。

发布前核查六平台结果、全部许可证、哈希、可执行位及安装包安全性。只能纳入必要应用包和分发元数据，不上传验证截图、个人路径、应用数据、本地归档或旧 Git 历史。预期资产为 Windows 两架构各 setup.exe/ZIP、Mac 两架构各 DMG/app.tar.gz、Linux 两架构各 tar.gz，另 BUILD-INFO.json/SHA256SUMS.txt，共 12 个；以实际打包结果核定。准确标签指向已验证提交，预览发布不设置为稳定 Latest。仓库公开后以匿名方式重新下载全部资产并核对 SHA-256，确认可直接获取安装包。

相关：[CI](CI.md)、[状态](STATUS.md)、[Native AOT](NATIVE-AOT.md)、[Avalonia macOS 分发](https://docs.avaloniaui.net/docs/deployment/macos)。

Grove 项目代码采用 MIT 许可证，应用分发包含根目录 LICENSE.txt；第三方字体、编辑器及语法资源的原始许可和通知继续保留。
