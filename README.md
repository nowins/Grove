# Grove

面向本地仓库的桌面 Git 客户端。在同一个窗口浏览代码、查看差异和历史、整理改动。

## 功能

- 工作区、索引与历史提交的 Diff，提交图、文件历史和 Blame。
- 按文件、代码块或行暂存，提交、Stash、合并、Rebase，以及 Soft／Mixed／Hard／Keep Reset。
- 远端分支一键 Checkout；New Branch 可编辑名称和选择创建后检出，保留跟踪关系并检查 worktree 占用。
- 文件树、按需筛选、当前文件定位；Git 输出和已有 worktree 列表使用非模态工具页。
- Java、C#、Python、JavaScript／TypeScript 高亮与文件内结构导航，以及本地代码评论。
- Markdown 源码与预览切换，支持表格、任务列表、标题锚点及仓库内图片；历史阅读保留同一提交的链接和图片。

## 下载与运行

当前提供开发预览构建，[Releases](https://github.com/nowins/Grove/releases) 尚未发布版本。进入[成功的 CI 运行](https://github.com/nowins/Grove/actions/workflows/ci.yml)，从 **Artifacts** 下载对应包：

| 平台 | 架构 | Artifact | 内部应用包 |
| --- | --- | --- | --- |
| Windows | x64 | `desktop-win-x64` | `desktop-win-x64.zip` |
| Windows | ARM64 | `desktop-win-arm64` | `desktop-win-arm64.zip` |
| macOS | Apple Silicon | `desktop-osx-arm64` | `desktop-osx-arm64.tar.gz` |
| macOS | Intel | `desktop-osx-x64` | `desktop-osx-x64.tar.gz` |

仓库目前为私有：下载需要登录有读取权限的 GitHub 账号。CI 产物保留 **14 天**；下载的 Artifact 外层压缩包内含应用包和 `SHA256SUMS.txt`，请先核对应用压缩包的 SHA-256，再解压运行。

先安装 Git，并确保命令行能运行 `git`。Windows 还需要 Microsoft Edge WebView2 Runtime。应用包包含 .NET 运行时，无需另装 .NET SDK。

- **Windows：** 解压后运行 `GitX.Desktop.exe`。
- **macOS：** 解开 `tar.gz`，在该目录的终端运行 `./GitX.Desktop`；当前不是 `.app` 或 DMG 安装包。
- 启动后选择已有仓库，也可传入路径，例如 Windows 的 `.\GitX.Desktop.exe "C:\path\to\repo"` 或 macOS 的 `./GitX.Desktop ~/projects/repo`。

可执行文件和部分内部目录暂保留 `GitX` 名称以兼容现有配置。构建包未签名；macOS 未公证。四平台构建与自动化测试已通过，原生桌面验收目前覆盖 Windows x64，其他平台的 WebView、输入法与 DPI 仍需实机验证。

## 阅读 Markdown

打开 Markdown 文件后默认显示预览，可在工具栏切换到只读源码；两种视图分别保留滚动位置。Esc 返回源码，Ctrl+F 切到源码搜索。支持 `.md`、`.markdown`、`.mdown` 和 `.mkd`。

相对链接在当前文件目录解析；网页链接点击后交给系统浏览器。原始 HTML 按文本显示，远程图片不会自动加载；暂不支持渲染 Diff、SVG、数学图表或代码块语法着色。

## 从源码运行

准备 Git、PowerShell 7、`global.json` 指定的 .NET SDK 和 `.node-version` 指定的 Node.js：

```powershell
pwsh ./tools/start.ps1 -Repository ../sample-repository
```

省略 `-Repository` 从欢迎页开始。完整恢复、构建、测试与打包：

```powershell
pwsh ./tools/ci.ps1
```

脚本按本机架构执行，输出位于 `artifacts/ci/`。详情见 [开发约定](docs/DEVELOPMENT.md)、[CI](docs/CI.md) 和 [验证范围](docs/STATUS.md)。

## 当前边界

- 固定深色主题；文件内结构导航不包含 LSP 或跨文件语义分析。
- Worktree 支持发现和打开已有目录，尚无创建、删除或 prune 管理界面。
- 文件历史工具页仍是单会话；`.gitignore` 遵循 Git 规则，尚无忽略项管理界面。
- 评论和界面设置保存在本机，不是团队云端评审服务。`GITX_DATA_DIR` 可用于指定独立数据目录。

版本发布准备与平台打包限制见 [Release 方案](docs/RELEASING.md)。
