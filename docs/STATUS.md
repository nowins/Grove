# 验证范围

## 已核对的当前代码

- Windows 本机：Core 480/480、Editor 73/73，Release 零警告零错误；self-contained publish、编辑器、六种 grammar、许可文件与 LibGit2 原生资产检查通过。
- Windows 原生：Grove 标题与图标、代码浏览和关于窗口正常。此前四模式 Reset、取消、过期快照拒绝及关联 worktree 隔离在专用临时仓库验证；未在真实项目执行 Reset。
- Python／JavaScript／TypeScript：文件识别、高亮、合并和当前文件结构导航已验证；不包含 LSP 或项目级语义分析。
- 窄窗口：已有停靠空间约束、编辑区聚焦／还原、小地图窄列隐藏和稳定错误反馈。视觉草案已批准，第一批文字／状态角色、图文间距与组内边界已实施；第二批文件树列位、Diff 版本条、About／Reset 层级与工作树行排版也已实现。

## 视觉第一批 Windows 验证

原生与 Web 的辅助、禁用、焦点颜色对应；工具标题 Medium、共享图文间距 6、原生图标描边 1.5。1382×899 和 884×640 实际窗口截图已保留本地，输入焦点、中文文本查找、Esc 与 F7 路径通过。Core 456/456、Editor 73/73、零警告构建通过；本批未进行 Git 写入。当前固定深色；浅色、真实 IME 组合输入、跨 DPI、屏幕阅读器和 Mac GUI 未重新验收。

## 视觉第二批 Windows 验证

文件名占剩余列、图标与状态列固定，窄列表隐藏父路径；Diff 把只读状态、等宽修订号和路径分层。About 内容定高；Reset 上下文分组、正文可滚动且动作栏保留；工作树行统一字级与间距。1382×899 与 884×640 截图、中文长名称、项目树／改动列表、About 焦点／Esc、Reset 四种模式键盘选择／默认 Enter 取消、当前及关联工作树选择／取消均已检查。测试仓库的 HEAD、索引、文件状态以及关联工作树均保持原样。Core 456/456、Editor 73/73、零警告构建和 Windows self-contained 发布资源检查通过；浅色、真实 IME 组合输入、跨 DPI、屏幕阅读器和 macOS GUI 的限制同上。

## 非模态工具与定位 Windows 验证

Git 输出／任务与 Worktree 使用主界面工具页，终止任务仍需确认；Worktree 打开前重新校验，当前项不可重复打开。项目筛选默认收起、活动条件持续可见；项目树 Ctrl+F、编辑器／项目树 Ctrl+Shift+E 和定位按钮已实测。独立文件历史定位使用其自己的文档上下文；定位保留主编辑器文件和历史版本。1382×899 与 884×640 原始截图已留本地，包含 121 个文件中的视口外长中文目标定位。最终 Core 464/464、Editor 73/73、零警告 Release／Windows self-contained 发布资源检查通过。

此前视觉提交 0008053 的 Windows 与 macOS Intel CI 通过，macOS ARM 的提交钩子错误被 stdin 关闭的破管异常遮盖。修复会先完整读取 Git stderr 与退出码；新增 0／32／1 MiB 输入回归，本机均通过。更新后的三平台结果必须按实际提交运行核对，不能引用旧快照全绿作为当前验收。

## Windows ARM64 流水线

已增加 windows-11-arm 原生 runner、win-arm64 依赖锁和 self-contained 打包；架构探针校验进程、Node 与 Windows 原生 DLL。提交 c1a2ae2 的[四平台运行](https://github.com/nowins/Grove/actions/runs/41560453611)已各自通过 Core 459/459、Editor 73/73、零警告及打包。Windows ARM64 的 .NET、Node、LibGit2 与发布的四项原生 PE 资产均核对为 ARM64，并发现 ARM64 WebView2 Runtime；没有启动 GUI。

CI 错误诊断修复 8d6c308 的[三平台运行](https://github.com/nowins/Grove/actions/runs/41491008333)已各自通过 Core 459/459、Editor 73/73 及打包；其中包含早退出子进程 stdin 关闭时保留 Git stderr/退出码的回归测试。

## Hosted CI 与验收边界

视觉修改前的清理快照 44a39da 已在[Windows、macOS ARM 与 Intel runner](https://github.com/nowins/Grove/actions/runs/41456640682)分别通过 Core 456/456、Editor 73/73、零警告构建和 self-contained 打包。后续视觉修改的 hosted 结果以[工作流运行](https://github.com/nowins/Grove/actions/workflows/ci.yml)为准；上述本机验证不代替更新后的远端或 macOS 验收。

macOS 桌面、WebView、Cmd 快捷键、输入法、Keychain／askpass、混合 DPI、签名、公证及 .app 打包尚未验收。自动化 Core 测试会实际加载 LibGit2 和操作临时 Git 仓库，但不启动桌面窗口。

本快照不含原机器的 Git 历史、验证材料和个人配置；这些材料留在原本地工作区。CI 产物为未签名测试 archive，不是正式发行。

## 远端 Checkout 与 New Branch 本地验证

Windows Core 480/480、零跳过，Release 零警告；新增测试覆盖自动命名／跟踪、已有本地进度、不同远端同名、多个跟踪分支、嵌套 remote 名称、字面无效名称、脏工作树仅创建、旧快照及其他 worktree 占用。Editor 代码未变，沿用上一批 73/73 结果。

Windows 原生 1382×899 与 884×640：真实临时仓库完成无输入框 Checkout、自定义中文名且不检出、无效名称原位提示、Enter 不提交、Cancel／Esc 取消、worktree 占用及同名远端拒绝。已核对真实分支、跟踪配置、索引和文件；不是 Headless 截图。中文通过文本输入工具上屏，不代表真实 IME 组合验收。浅色、高 DPI、屏幕阅读器与 macOS GUI 仍未验证；本批 hosted 结果以[当前提交的工作流记录](https://github.com/nowins/Grove/actions/workflows/ci.yml)为准。

## Markdown 预览 Windows 验证

单文件 Markdown 默认预览，支持 GFM 表格、任务列表、代码块、中文标题及重复标题锚点。源码与预览可切换并各自保留阅读位置；Esc 返回源码，预览中 Ctrl+F 会切回源码并聚焦搜索。相对文件链接在新源码标签中打开，历史链接与图片保留原提交；远程图片和原始 HTML 执行被阻止。Diff 仍为文本对比。

Windows x64：Core 499/499、Editor 78/78，均无跳过；Release 零警告构建、锁定还原、self-contained 发布及原生依赖探针通过。Marked 与 DOMPurify 许可证随包验证。完整 CI 入口先通过 Core 498/498；增加 worktree 图片用例后再次完整 Core 回归为 499/499，最后搜索焦点修正后重跑 Editor 与独立 self-contained 发布通过。

专用隔离仓库的 1382×899 与 884×641 原生截图已保留本机：表格/图片排版、中文相对路径、跨文件深层标题定位、缺失目标保留原文件、两个视图独立滚动、源码链接、Esc、Ctrl+F 焦点及输入均实测。验收前后临时仓库 HEAD 与 Git 状态一致，原工作区及 Typora 未被此验收操作改变。

限制：仅支持单文件预览，不提供渲染 Diff、原始 HTML、远程图片、SVG、数学/图表插件或代码块语法着色。历史及 worktree 资源隔离有自动化 Git 测试，但本轮未做 macOS 原生界面、浅色主题、真实 IME 或屏幕阅读器验收；Windows 实测不替代其他平台验收。

## Markdown 发布前复核

补齐常见 Markdown 扩展识别，网页链接拒绝空白、控制字符、不完整协议或反斜线歧义；源码搜索和笔记入口在退出预览时同步恢复布局与焦点。README 已加入预览使用方式、四平台 CI 下载和校验步骤及真实分发限制。

本地 Windows 完整 CI：Core 509/509、Editor 78/78，零跳过、零警告构建、自包含打包和架构探针通过。Marked/DOMPurify 许可证列入 MSBuild 输出与 CI 必需资源，Editor/Diff 样式也列为必需资源；发布文件清单包含隐藏文件。四平台结论以本次提交的 Actions 运行结果为准，macOS GUI 仍未实测，未创建版本标签或 Release。

## 首个可下载预发布版

`v0.1.0-preview.1` 已发布：[发布页](https://github.com/nowins/Grove/releases/tag/v0.1.0-preview.1)。代码与四个平台的应用包来自 a48e6cb / CI 41587178858；四平台 Core 509/509、Editor 78/78 全部通过。Release 资产包括四个版本/架构应用包、SHA256SUMS.txt 和 BUILD-INFO.json，发布后另行下载核验；main 后续下载说明的文档提交不改变本版本的代码来源。

仍未做发行签名或 macOS 公证，Mac 为原始 tar.gz，没有 .app / DMG。GUI 仅 Windows x64 实测，其他架构 GUI 未验收；保留私有仓库权限，预发布不标作稳定 Latest。

## Grove 安装包与名称修正（本地验证）

桌面 apphost/程序集、Windows 元数据、资源 URI 和运行脚本统一为 Grove，版本为 0.1.0-preview.2。内部命名空间、Core 程序集和 GitX 用户数据路径保持兼容。Windows NSIS 当前用户安装程序、macOS .app/DMG 打包与对应验证脚本已接入 CI；发行仍为普通未裁剪 self-contained 构建。

本机 Windows x64：零警告构建、Core 509/509、Editor 78/78、258 个安装载荷文件哈希、安装/重装/卸载、开始菜单及卸载注册项、额外用户文件和配置哨兵保留均通过。原生向导取消未创建目标目录；实际安装后的 Grove.exe 正常打开临时仓库，显示中文 Markdown、表格和相对图片，并正常退出后卸载。原始截图和日志只保留本机，不进入源码或 Release。

SourceLanguage 已改为源生成 JSON；独立 Windows x64 AOT 探针恢复 TypeScript 识别及 LibGit2 读取。其余 JSON 和反射绑定仍未达到 AOT 发布条件。macOS/ARM64 实际包的构建与自动检查以本次准确提交的 CI 结果为准；本机无法代替这些原生平台验证。Mac .app 仅 ad-hoc 签名，无 Developer ID、公证或 GUI 验收。最终发布范围、准确来源和资产校验以 Release 的 BUILD-INFO/SHA256SUMS 记录为准。
