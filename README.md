# Grove

面向本地仓库的桌面 Git 客户端。在同一个窗口浏览代码、查看差异和历史、整理改动。

## 功能

- 工作区、索引与历史提交的 Diff，提交图、文件历史和 Blame。
- 按文件、代码块或行暂存，提交、Stash、合并、Rebase，以及 Soft／Mixed／Hard／Keep Reset。
- 远端分支一键 Checkout；New Branch 可编辑名称和选择创建后检出，保留跟踪关系并检查 worktree 占用。
- 文件树、按需筛选、当前文件定位；Git 输出和 worktree 列表使用非模态工具页。
- macOS 使用系统原生菜单；最近文件 Ctrl+E（Mac：Command+E），跳转行 Ctrl+G（Mac 另有 Command+L）。
- 开发版支持阅读位置前进／后退：Alt+Left／Right（Mac：Command+Option+Left／Right），保留文件、修订和 Diff 侧的行列位置。
- 开发版可连续阅读本地改动或历史比较范围的多文件 Diff，按需加载；行号可定位完整 Diff 的对应侧。
- 历史分支筛选提供最近使用、收藏，以及本地/远端分支和标签分组；筛选不会执行 Checkout。
- 文件标签右键关闭当前/其他/全部；文件与文件夹支持系统定位及终端入口。
- Java、C#、Python、JavaScript／TypeScript 高亮与文件内结构导航，以及本地代码评论。
- Markdown 源码与预览切换，支持表格、任务列表、标题锚点及仓库内图片；历史阅读保留同一提交的链接和图片。

## 下载与安装

从 [Grove 0.1.0 Preview 5](https://github.com/nowins/Grove/releases/tag/v0.1.0-preview.5) 的 **Assets** 下载对应架构；GitHub 自动生成的 **Source code** 是源码，不是应用包。

| 平台 | 推荐下载 |
| --- | --- |
| Windows x64 | [安装程序](https://github.com/nowins/Grove/releases/download/v0.1.0-preview.5/Grove-0.1.0-preview.5-win-x64-setup.exe) |
| Windows ARM64 | [安装程序](https://github.com/nowins/Grove/releases/download/v0.1.0-preview.5/Grove-0.1.0-preview.5-win-arm64-setup.exe) |
| macOS Apple Silicon | [DMG](https://github.com/nowins/Grove/releases/download/v0.1.0-preview.5/Grove-0.1.0-preview.5-osx-arm64.dmg) |
| macOS Intel | [DMG](https://github.com/nowins/Grove/releases/download/v0.1.0-preview.5/Grove-0.1.0-preview.5-osx-x64.dmg) |

Release 也提供 Windows 便携 ZIP、包含 `Grove.app` 的 macOS tar.gz、`SHA256SUMS.txt` 和 `BUILD-INFO.json`。先核对 SHA-256。公开 Release 可直接下载，无需 GitHub 登录；Release 包不受 Actions 的 14 天产物保留期限制。

- **Windows：** 安装到当前用户的程序目录，开始菜单入口为 Grove，不要求管理员权限。便携版解压后运行 `Grove.exe`。安装程序外壳为 NSIS x86，应用本体匹配所选 x64/ARM64 架构；选错架构会拒绝安装。
- **macOS 14+：** 打开 DMG，将 `Grove.app` 拖入 Applications。也可解压 app.tar.gz。当前只有 ad-hoc 完整性签名，没有 Apple Developer ID 签名或公证，Gatekeeper 可能阻止打开；不建议在要求受信任签名的设备部署。Mac GUI 仍待实机验收。
- **依赖：** 安装 Git 并确保 Grove 的进程能在 PATH 中找到它；Windows 另需 Microsoft Edge WebView2 Runtime。macOS 从 Finder 启动时的 PATH 与终端可能不同。应用已包含 .NET 运行时，不需要 .NET SDK。安装包不会静默下载这些外部依赖。
- **现有数据：** 应用名称和可执行文件已改为 Grove；配置、评论和 WebView 数据仍使用原有 GitX 目录及 `GITX_DATA_DIR`，避免迁移丢失。卸载保留这些用户数据和安装目录内额外创建的文件。

启动后选择已有仓库；也可从命令行传入路径，例如 `.\Grove.exe "C:\path\to\repo"`，或 `open -a Grove --args ~/projects/repo`。这是未做发行证书签名的预览版。四平台原生自动化检查与 Windows x64 GUI 验证范围见[状态记录](docs/STATUS.md)；CI 通过不代表四平台 GUI 已验收。发布使用普通自包含构建，Native AOT 尚未达到发布条件。

最新开发构建可从 [CI](https://github.com/nowins/Grove/actions/workflows/ci.yml) 的 **Artifacts** 下载，保留 14 天；外层 ZIP 内是应用包与校验文件。

## 阅读 Markdown

打开 Markdown 文件后默认显示预览，可在工具栏切换到源码；两种视图分别保留滚动位置。Esc 返回源码，Ctrl+F 切到源码搜索。支持 `.md`、`.markdown`、`.mdown` 和 `.mkd`。

相对链接在当前文件目录解析；网页链接点击后交给系统浏览器。原始 HTML 按文本显示，远程图片不会自动加载；暂不支持渲染 Diff、SVG、数学图表或代码块语法着色。

## 文本编辑

支持编辑普通工作区 UTF-8 文件，使用 Ctrl+S（macOS：Command+S）保存；标签圆点标识未保存内容。切标签及刷新保留草稿，关闭标签、窗口或切换仓库时提供保存、放弃、取消。外部改写、删除或权限变化不会被静默覆盖。

保留 UTF-8 BOM、LF/CRLF 与末尾换行。单文件上限 1 MiB，单行上限 20,000 字符；混合/单独 CR 换行、其他编码、符号链接、冲突文件、历史及 Diff 保持只读。草稿位于当前窗口内存中，不提供崩溃或重启后的自动恢复。标签与项目树另提供系统文件管理器定位及终端入口。

Preview 5 起提供文本编辑。历史提交与 Diff 始终只读，不会因存在本地同名文件而写入工作区。

Linux 开发候选的依赖、便携包及验证边界见 [Linux 说明](docs/LINUX.md)，尚未加入已发布下载资产。

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
- Worktree 支持发现、打开和带脏文件/锁定检查的删除；主工作树不能删除，尚无创建或 prune 管理界面。
- 文件历史工具页仍是单会话；`.gitignore` 遵循 Git 规则，尚无忽略项管理界面。
- 评论和界面设置保存在本机，不是团队云端评审服务。`GITX_DATA_DIR` 可用于指定独立数据目录。

版本发布准备与平台打包限制见 [Release 方案](docs/RELEASING.md)。

## 许可证

Grove 自身代码采用 [MIT](LICENSE)。第三方依赖、字体及语法资源继续保留各自许可证；应用包保留相应许可与通知文件。

## 在线更新

“帮助 → 检查更新”使用公开 GitHub Releases，无需登录；支持可取消的后台下载和确认后交给系统安装。从 Preview 4 起提供。Preview 3 及更早版本需先手动安装 Preview 4 或更新版本；默认关闭自动检查和后台下载，可在更新工具页启用。详见 [在线更新说明](docs/UPDATES.md)。
