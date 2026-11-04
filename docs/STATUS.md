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

## macOS WebView 原生返回类型修复

原生调用的异步 document load 会将 Promise 直接返回 WKWebView，匹配用户报告的 unsupported type 错误。命令与状态查询已统一为字符串边界，覆盖主编辑器、独立 Diff、文件历史和合并窗口，并保留版本化异步错误回报。

本次回归覆盖命令返回类型、同步与异步错误，以及 WebView2/WebKit 的 JSON 状态交换。此提交不包含在线更新功能。

## Preview 3：macOS WebView 兼容修复

修复原生端接收 Promise 时 WKWebView 返回“JavaScript execution returned a result of an unsupported type”的错误。主编辑器、独立 Diff、文件历史与合并窗口统一使用字符串结果边界，JSON 状态查询保留 WebView2/WebKit 兼容解码；异步结果仍通过原有版本消息处理。另修复慢速 runner 上本地批注并发保存的锁等待。

四平台原生构建与自动化回归通过：Core 524/524、Editor 83/83。WKWebView 探针覆盖异步命令和 JSON 状态交换；完整测试结果以 CI 为准。

版本标识更新为 0.1.0-preview.3；正式安装包以该提交的 CI 和 Release 资产为准。此版本不包含尚在开发的在线更新或工作树删除功能，签名、公证及用户数据兼容边界不变。

## main 中已整合的后续功能（尚未发布新安装包）

在线更新与可取消后台下载、干净关联工作树删除、短 Unix askpass 端点、macOS 标题栏安全区与应用名称、Windows 系统最小化/恢复处理，以及注解菜单冗余标题清理已整合。现有 Preview 3 的 Release 标签和安装包保持原状；合并源代码不等于发布新版，Preview 3 不包含这些后续改动。

Windows 本地整合回归 Core 611/611、Editor 83/83、锁定依赖恢复和零警告 Release 构建通过。原生 Git loopback 凭据交互通过，测试使用临时仓库与隔离配置。四个平台的组合构建、原生 askpass、两种 Mac 架构的 AppKit 标题栏探针及打包检查，以对应 main 的 [CI 运行](https://github.com/nowins/Grove/actions/workflows/ci.yml)结果为准；独立分支曾通过的结果不能代替整合后的验证。

Windows 消息级回归和 Mac 程序化原生事件不代表真实任务栏点击、物理键盘、Retina/多屏或完整人工 GUI 验收。在线更新安装仍需确认，macOS 只打开 DMG 供用户自行替换；发布签名与公证的限制不变。

## Preview 4：在线更新与后续修复

此版本包含 main `8bd4558` 中已整合的在线更新、工作树删除、短 Unix askpass 端点、macOS 标题栏/应用名称、Windows 系统最小化/恢复及注解菜单精简。版本标识为 0.1.0-preview.4，macOS bundle 构建号为 4；Preview 3 标签与资产保持不变。Cmd+Q 间歇性反馈的未验证候选未纳入本版本。

发布包必须来自 Preview 4 精确提交的四平台 CI，并核对 package metadata、内部应用版本及 SHA-256。详细构建提交、原生探针与验收边界记录于 Release 的 BUILD-INFO.json。未签名/未公证、真实任务栏点击、物理键盘、Retina/多屏和 Mac 更新安装界面的限制继续保留。


## 原生菜单与最近文件（开发中，未发布）

- macOS 将原窗口主菜单映射到顶部原生菜单栏：文件、视图、搜索、Git、窗口、帮助。Grove 应用菜单包含关于和检查更新，保留框架的 Services、隐藏及退出动作；退出继续调用 `TryShutdown` 和原有 Closing 保护。窗口内汉堡入口隐藏，仓库及分支按钮保留。Windows 保留窗口主菜单。菜单共享命令标识、动作、状态与平台快捷键，原生点击只绑定一次 Command；命令延迟执行前重新检查上下文。
- 最近文件：Windows/Linux 使用 Ctrl+E，macOS 使用 Command+E。输入按路径或文件名过滤；上下键选择、Enter 打开、Esc 取消，列表有焦点时 Delete 只移除记录。选择和搜索不切换文档；成功打开时复用既有源码标签及其视图状态。不同工作树分别保存，历史源码保留提交 SHA，不默认为当前工作树文件。此轮不记录 Diff 比较组合，也不根据 Git 状态猜测“最近编辑”。
- 本地持久化仅包含仓库根目录、相对路径及可选提交号，最多 32 个工作树、各 64 条，不保存源码内容。读取损坏记录、缺失文件和过时仓库请求均有保护。
- Windows x64：完整 Core 625/625、Editor 85/85 通过。隔离真实窗口中验证了窗口键事件和 WebView DOM 快捷键、过滤、取消、Enter 打开及标签复用、正常关闭前最近记录持久化；这些是程序化测试，不等同实体键盘、中文输入法或 Explorer 任务栏验收。
- 两种 macOS 架构的原生探针已扩展为检查实际 NSMenu、调用菜单动作、Cocoa Command+E 和写入期间 Command+Q 的退出保护，并继续检查标题栏三种宽度。此次尚未在 Mac 执行，不能以 Windows 编译或旧版 CI 替代；间歇性 Command+Q 问题的原因仍未确定。

## macOS 菜单退出键路由回归（验证分支）

原生 CI 的独立临时仓库中，Command+Q 事件到达了 NativeWebView 的托管键盘路由，但没有触发原生 Quit 菜单或应用退出请求；直接调用原生 Quit 动作可正确进入 TryShutdown，并由写入保护取消。因此增加仅针对尚未处理的 Meta+Q 的冒泡回退，调用相同的 TryShutdown，合并排队的请求，不强制退出，也不叠加原生菜单已经消费的事件。此为合成原生事件场景的证据，不代表已经复现用户此前的间歇性系统症状。

探针分别要求原生 Quit 动作和实际 NSApplication 队列中的 Command+Q 各触发一次关闭请求，并由既有写入保护取消；记录原生焦点、菜单、关闭与退出请求。测试仍使用独立数据和内存中的写入占位，不执行实际 Git 写入。当前修正的结果以本验证提交的四平台 CI 为准；物理键盘和用户 Mac 人工验收仍未完成，尚未合并或发布。

相同的嵌入视图路径中，尚未处理的 Command 菜单快捷键复用原生窗口菜单的既有 Command，而不是另写一套动作；命令启用状态、队列及上下文复核保持一致。探针额外从实际 WKWebView 焦点发送 Command+M，检查最小化与恢复。框架应用菜单的 Quit 保持独立的 TryShutdown 保护。


## 历史引用筛选与 Ctrl+G（本地开发，未发布）

基线菜单/最近文件提交 `203d502` 的[四平台 CI](https://github.com/nowins/Grove/actions/runs/41791174720)全部成功，每个平台 Core 625/625、Editor 85/85、构建零警告。两种 Mac 均确认 WKWebView 焦点下 Command+Q 通过同一 TryShutdown 退出保护，原生 Quit 动作单独检查；Command+M 最小化/恢复和 Command+E 打开/取消通过。该结果不包含以下新增功能，也不代表物理键盘或此前间歇性系统问题已定位。

新增历史工具栏筛选包含收藏、最近使用、本地分支、远端分支、标签，保留全部提交、HEAD、当前勾选和搜索。以 canonical ref 区分身份，按 worktree 根目录保存；删除的引用不能继续应用。选择候选、收藏或取消均不检出分支。最近上限 16、收藏上限 256；损坏 JSON 可恢复，不支持版本和超限文件保留原样并提示。

新增源码 Ctrl+G 跳行（Mac 另有 Command+L），支持输入校验、Enter 显示目标、Esc 取消及焦点恢复，历史版本独立行数、切文件失效和 Markdown 返回源码。只读代码不因定位而修改。

Windows x64 本地：Core 631/631、Editor 92/92，通过且无跳过。Release 构建零警告。专用生成仓库与关联 worktree、独立配置、未切换用户桌面的真实 Grove 窗口中，两次修复后的组合场景通过：1380/884 DIP、200% 缩放，搜索/星标/列表方向键及 Space、重新打开持久化、worktree 隔离、引用删除后的拒绝、当前 36 行与历史 7 行范围、菜单和 WebView Ctrl+G、取消、末行显示、过期请求及 Markdown 均已检查。主/关联工作树 HEAD、当前分支、索引内容和工作文件状态保持不变；仅失效引用样本在已标记临时仓库删除一个测试 remote ref。

截图和原始日志只保留本机。WebView PNG 是实际 Chromium 页面截图，原生弹层 PNG 是实际控件的测试渲染，均不冒充完整 OS 合成截图。上述按键为程序化事件，未做实体键盘、真实中文 IME、屏幕阅读器、多屏或本批新功能的 macOS 原生验收。尚未提交、推送、合并或发布本批新功能；Preview 4 安装包不包含它们。

### 验证分支准备（2026-11-04）

本批在独立验证分支提交并运行四平台 CI，不合并或发布。补充 Mac 原生探针：宽/窄窗口 Command+L、保留的 Ctrl+G、Enter/取消/越界、历史行数与切文件失效；历史筛选的本地/远端/标签身份、单结果方向键进入列表与 Space 收藏、最近记录持久化、worktree 隔离和失效 ref 拒绝。原生事件只发给探针自身的 AppKit 窗口；搜索和行号文本由测试设置，真实物理输入、IME 与用户机器验收仍不在此范围。CI 实际结果需以该验证提交对应运行记录为准。
