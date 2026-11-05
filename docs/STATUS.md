## 联合候选当前状态（2026-11-05，未合并/未发布）

Linux 优先联合候选同时包含内存修复、更新诊断、搜索 S1/S2、历史/Diff 响应式 UI、Push 审阅、单块直接回退和草稿恢复。排除 Worktree 创建和 Mac 标题栏候选。下文各“独立候选”的测试计数/环境失败是历史记录，不是这份联合源码的最终结果；最新验证与限制见 [JOINT_CANDIDATE.md](JOINT_CANDIDATE.md)。未改 main，未提交或推送。

# 验证范围

## 当前组合候选：Linux + 内存生命周期 + 更新诊断（2026-11-05，未发布）

独立分支以 205678e 为基线，组合 Linux 候选、内存提交 5d5184f9f9b6a66b0e571a0f07e34eb1a89ea61f 和更新诊断补丁；不包含搜索、标题栏或直接撤销 hunk 候选，未合并 main、提交、推送或发布。以下既有各批次记录为历史覆盖，不取代本节的当前结果。

- 人工合并 DEVELOPMENT/STATUS 的追加段落，保留各自约束；Linux 更新面板、宿主守卫和 Core 请求前限制与原 Linux 候选字节一致，5 个内存实现/测试/脚本文件与 5d5184f 一致。
- 固定 .NET SDK 10.0.401、Node 24.19.0、PowerShell 7.5.4；npm ci、locked restore、完整解决方案 Release build 零警告零错误，Editor 124/124，Core 两次完整执行均 706/706，无跳过。测试仅移除无关多行工具说明变量，未改 HOME/Git 配置；MSBuild 使用 -m:1 适配沙箱 IPC 限制。
- 新增 8 项组合回归：Linux x64/ARM64 在无冷却或已有冷却时都保持手动模式、零网络请求；Windows/macOS 四 RID 继续经过诊断源正常检查。既有取消、冷却恢复、403/429 区分、重定向与安全验证同时通过。这不是 Windows/macOS 原生 GUI 验收。
- 首轮 Core 705/706，未改动的 HistoryLogControlsTests 通知 List 在后台写入和断言枚举间发生竞争；仅将测试收集器改为 ConcurrentQueue，保留全部原断言，不修改产品行为。失败日志和两个最终全量结果均保存。
- Linux x64 自包含 publish、LibGit2 实际加载、ELF 架构、归档与逐文件校验、执行位验证分别记录于交付包。原生 GUI/askpass、Linux ARM64、Win/Mac 原生安装与远端 CI 未验收；完整 ci.ps1 未取得全绿，不将分步验证等同全平台通过。当前仍无显示会话，先前 Unix socket 拒绝未绕过或通过重复尝试规避。
- 内存测试证明旧引用被释放，不证明进程工作集降低比例。Linux 不因加入诊断层而启动自动检查、下载或安装，原更新偏好仍不读取或改写。

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

## 标签右键菜单（2026-11-04，本地未提交）

普通文件标签新增关闭标签、关闭其他、关闭全部，支持后台标签、历史/工作区同路径身份、单标签禁用项、键盘导航及取消；Mac Control-click 有独立的防误选/误关闭分支。后端批量关闭是一次状态变更，版本及目标 ID 过期时不执行。回归发现并修复 ClearDocument 将代次递增放在可空事件参数内的问题，关闭全部后即使没有事件订阅者也能拒绝旧读取结果。

Windows x64 本地 Core 641/641、Editor 101/101，全解决方案 Release 构建零警告。隔离、未切换的 Windows 桌面上，实际 Grove/WebView2 用自有 Chromium CDP 鼠标/键盘输入验证多标签、右键后台目标、取消保持行/版本、关闭三种范围、单标签禁用项、历史修订及失效菜单，1380/884 DIP、200% 缩放均通过。截图为实际 WebView 捕获，内容全部来自生成仓库；Git HEAD、分支、索引及工作文件未变化。测试不是物理键鼠或完整 OS 截图。

Mac 分支目前仅有 DOM 逻辑/Control-click 回归和跨平台代码编译检查；本批尚未执行 Mac 原生 WebView/触控板或 IME 验收。未获本批提交或推送授权，故未启动新的 Mac CI；此前 7e80c42 四平台成功不代表本次未提交菜单已在 Mac 运行。截图、原始日志及隔离测试工具只留本机。本阶段未提交、推送、合并或发布。

## 文件操作、文本编辑与载荷（2026-11-04，本地未提交）

普通工作区文件已接入显式编辑与保存、未保存标记、逐标签撤销/草稿缓存、关闭标签/窗口/换仓确认、原始字节基线与外部变化拒绝覆盖、原子替换和失败恢复。UTF-8 BOM、LF/CRLF 及末尾换行保留；混合/单独 CR、无效编码、超限、只读/冲突、历史和 Diff 不获得写入权限。保存回执仅确认捕获文本，保存期间的新输入继续为脏状态。Markdown 预览使用草稿，旧快照的 Blame/笔记行锚点不用于脏文本。

标签菜单新增文件管理器定位及终端入口；项目树文件/文件夹复用校验和启动计划。Windows 用 Shell 项定位与固定 PowerShell 工作目录；Mac 使用 Finder/Terminal 参数列表。历史标签禁用本地同名文件操作。并排 Diff 的 HTTP JSON 不再重复包含补丁正文；纯标签更新仅在正文已就绪时延迟完整 HTTP 序列化，保留重载所需的最新快照。评论快照算法未变。

最终 Windows x64：Core **677/677**、Editor **110/110**；完整解决方案 Release/warnings-as-errors 构建 **0 警告、0 错误**。新增实际旧版 CommentContext JSON 字节/SHA golden，及 Lazy HTTP 并发读取、替换和最新标签一致性测试。回归曾发现“删除文件读取契约”和“非法 UTF-8 提示标志”问题，均修正后完成全量通过。

专用生成仓库、linked worktree 和独立用户目录，在未切换的私有 Windows 桌面运行真实 Grove/WebView2：**48 项检查通过**。Chromium CDP 输入验证实际文字编辑、Ctrl+S、保存期间继续输入、切标签/刷新、取消标签/窗口/工作树切换、外部修改拒绝覆盖、放弃草稿不改磁盘、保存后关闭、历史/Diff 只读；普通标签右键的三个关闭范围、宽窄布局及旧菜单失效保持通过。原生确认和树菜单通过该隔离应用拥有的实际 Avalonia 控件事件调用。文件/文件夹系统操作捕获 6 个启动计划，未实际启动外部应用。测试结束生成仓库 HEAD、分支、索引及文件字节与基线一致。

证据截图为真实 WebView 页面捕获（1380/884 DIP，200% 缩放），原始日志和临时探针仅留本机，不随源码上传。本批**未执行**真实物理键盘/中文 IME、辅助功能、Mac 原生 WebView、Finder/Terminal 或资源管理器原生选择验收；Mac 仅有逻辑/参数测试。本批未提交、推送、合并或发布，旧版 Preview 4 安装包不包含这些更改。

## Preview 5 功能发布准备（2026-11-05）

用户已授权合并发布菜单、最近文件、历史分支筛选、跳行、标签菜单、系统位置与文本编辑。本批排除内存实验及 Lazy 载荷/Diff patch 省略，保留原有序列化及评论快照算法。文档就绪只统计当前绑定模型，缓存草稿不再让加载提示提前就绪。

本地 Editor 111/111、Release 构建零警告；最终 Core、四平台 CI、Mac 原生编辑输入/Command+S/冲突保护结果由本批发布后续记录补充。既有 Windows 48 项回归是上一完整本地版本的证据，本批仍需对应提交复验。Mac NSEvent 与脚本/控件调用会分别记录，不作为物理键盘、IME、Finder/Terminal 真实启动或完整用户机器验收。

本批独立 Windows x64 复验：Core 675/675、Editor 111/111、Release 构建零警告；修正测试对已排除的 patch 省略行为的旧预期后，新的生成仓库上 48 项 Grove/WebView2 检查通过，HEAD、分支、索引和工作文件均恢复一致。截图仍仅为 WebView 捕获；未启动用户的系统文件管理器或终端。Mac 新增输入/保存保护探针已编译，等待对应提交 CI。

发布审查补充：两种强制发生在原子替换后的外部 Git 变化（分支切换及 HEAD 更新）均复现了旧实现错误确认保存成功的问题。增加替换后上下文校验并保留实际备份后，相关编辑/关闭/消息桥安全回归 139/139 通过。最终 Core 数量增加为 677；以最终提交的四平台 CI 为准。首轮 Mac 保存探针曾观察错误的 worktree 文件，现已显式选择并校验其主工作树和草稿 RootKey；该超时不作为产品 Cmd+S 故障的证据。

## 历史：Linux 独立候选重建验证（2026-11-05）

执行环境恢复后，原 /workspace/shared 中 Linux 工作树、补丁、报告与安装包不可访问，其他未提交候选工作树亦未找到。本候选从远端精确基线 205678ea396c131d11ca5f08780d97bd6f847431 在独立 rebuilt 目录重建，重新验证，不以旧结果冒充新结果；未提交、推送、合并 main 或发布。

本地 Debian 13 x64，.NET SDK 10.0.401、Node 24.19.0、PowerShell 7.5.4：locked restore、Release build、自包含 linux-x64 publish 均通过，零警告零错误。沙箱限制 MSBuild 工作节点 IPC，使用 -m:1。Editor 111/111，Core 686/686，无跳过；测试子进程仅移除无关的多行工具说明环境变量，未改 HOME、Git 配置或 GIT_*，未放宽 BlameInputProof 的保守规则。

PlatformProbe 实际加载 LibGit2，验证 apphost、Skia、HarfBuzz 与 LibGit2 的 ELF64 x64。便携包 259 文件在含空格目录解压，归档 SHA256、逐文件哈希、数量与执行位检查通过；错误 SHA256 在解压前被拒绝。PowerShell 解析与 git diff --check 通过。

原生限制重查：DISPLAY/Wayland 均为空，WebKitGTK/WPE 缺失；实际启动最终 apphost 报 XOpenDisplay failed。已安装 Xorg dummy 在隔离配置、临时 Xauthority、TCP 关闭且保留鉴权下仍不能创建 local/unix socket，退出 1。未改走网络传输或降低安全设置。askpass 原生探针也仍因 Unix named pipe EACCES 退出 134，未标记通过。补依赖不能解决 socket 拒绝。

Linux ARM64、远端 CI、完整 ci.ps1、GUI/输入法/焦点/系统位置与浏览器实际启动均未完成。只可称非 GUI 部分已验证的开发候选，不能称原生桌面全面可用。

Linux 手动更新显示真实 RID，保留浏览器 Releases（含预览版）发现入口；禁用应用内检查/下载/安装及自动偏好，不启动定时器、不读取/改写其他平台旧偏好；核心网络前拒绝不支持 RID。7 项更新能力/零请求回归与2项 Linux 文件定位参数回归已包含在总测试数中，未混入独立更新诊断候选。

## 历史：内存生命周期独立候选（2026-11-05，未发布）

以 205678ea396c131d11ca5f08780d97bd6f847431 为基线，缩略图定位完成或文档替换时清除 pending；结构导航文档失效时同时清除 result、filtered 及旧行节点。普通结构窗口关闭仍保留同文档缓存和已热 worker，未改草稿、撤销、保存和传输格式。

Linux x64 使用项目固定 Node 24.19.0、锁定 npm ci：仅缩略图候选完整 Editor 118/118；两项组合后完整 Editor 124/124，均零失败、零跳过，Editor bundle 构建成功。结构导航新增 6 项控制器/DOM 回归在旧实现有 3 项失败，修复后全部通过；缩略图 7 项回归覆盖切换、关闭、同帧合并、重入与异常。新增测试使用受控 RAF/worker，既有解析器测试仍实际使用锁定 Tree-sitter WASM。

本地没有 .NET SDK，未执行 Core、Desktop build/publish；四平台验证通过独立分支的现有 CI 运行，实际结果以对应提交的运行记录为准。未运行本次改动专属的实际 WebView 交互、物理输入或进程堆/工作集基准。两项结果证明特定旧引用被释放，不证明整体内存下降比例，也不将有界缓存称为无限增长泄漏。原主工作区、main 和发布分支不因本验证而修改，本候选不合并、不发布。

## 历史：更新诊断独立候选（2026-11-05，未发布）

基于 205678e 重建，Linux x64/.NET SDK 10.0.401。新增 12 项测试；更新相关可移植测试 67/67 通过，编译无警告。全量 Core 686/689 通过、3 失败、无跳过：既有 Native_download_marker_is_preserved_without_running_an_installer（Linux 不支持），以及 BlameInputProofTests.Source_proof_covers_missing_ancestor_attributes_and_repository_overrides 和 Linked_worktree_sources_include_private_and_common_metadata。未执行 Windows/macOS 原生 GUI 或平台回归；不宣称全量通过。未提交、推送或发布。

## 快速搜索 Actions 首版（2026-11-05，重建的独立 Linux 工作区）

移除未实现分类和提示，新增 Actions 与 All 的 `/` 动作搜索。动作限于现有导航/帮助入口；保持原菜单可执行性、仓库/文档代次与关闭后分派检查，没有新增 Git 写入入口，也未引入 LSP、调试器或外部项目代码。

环境重置后从同一 205678e 基线与保留的实现脚本重建；本段结果来自重建后重新执行。.NET 10.0.401 缓存依赖锁定恢复、Desktop Debug 构建通过（零警告零错误）；搜索专项 20/20（含新增动作测试 8 项），Editor 111/111。完整 Core 685 项中 682 通过、3 失败、0 跳过：Native_download_marker_is_preserved_without_running_an_installer（Linux 不支持安装更新），以及 BlameInputProofTests 的 Source_proof_covers_missing_ancestor_attributes_and_repository_overrides、Linked_worktree_sources_include_private_and_common_metadata。这三项在前次未改基线 205678e 验证中也均失败；本次未修改或跳过这些测试。

本轮没有 Windows/macOS 原生 UI、WebView 焦点、IME、DPI 或物理键盘验证；编译和逻辑测试不代表这些场景已验收。未提交、推送、创建 PR 或发布。

## 搜索只读预览 S2 候选（2026-11-05，独立 Linux 执行环境）

基于 205678e 加已持久保存的 S1 Actions 候选继续开发，未合入 main，也未混入 Linux/更新/内存组合候选。默认关闭的原生只读预览已实现，支持文件/文本结果、有界上下文、磁盘来源标注、失败重试、过期指纹拒绝及窄窗口收起。Actions 沿用原可执行性检查，不产生文件预览。

.NET 10.0.401 锁定恢复和 Desktop Debug 构建通过（零警告零错误）。搜索/预览专项 36/36，其中新增预览与生命周期测试 16 项，覆盖 100 次快速选择、取消/关闭/上下文切换、过期成功和失败、跨仓同名文件、受限文件及修改状态不变；Editor 111/111。完整 Core 701 项中 698 通过、3 失败、0 跳过，仍为 S1 基线已记录的 Linux 更新标记及两个 BlameInputProof 测试失败，没有跳过或修改这些测试。

本环境无 DISPLAY/Xvfb，且不是 Windows/macOS；未运行实际搜索窗口、未截图，不把编译/纯逻辑测试表述为 GUI 验收。Windows/macOS 的宽窄布局、预览滚动定位、鼠标/键盘焦点、IME、DPI 仍待原生检查。100 次选择测试验证状态隔离与清空，不等于实测对象存活、进程内存或性能收益。

## Push 审阅独立候选（205678e 基线，2026-11-05）

新增 PushPreview 只读模型、提交分页／文件懒加载与统一审阅窗口；尚未合并、推送、发布。临时本地仓库和 bare remote 的 23 项专项覆盖新分支、同步、领先、落后、分叉、未知远端对象、独立 push URL、异名目标、分页、HEAD／分支／配置过期、远端过期、重复执行、拒绝推送、lease 竞态、URL 重写绑定、脱敏、读取取消及指定 PID 终止，全部通过。

Linux 云端 Release Desktop 零警告／错误，npm 离线锁定安装及 Editor 111/111 通过。完整 Core 700 项执行，697 通过、3 失败、0 跳过：两个 BlameInputProof 环境证明断言、一个基线尚不支持 Linux 的更新安装标记测试。三项在干净 205678e 同环境复现，未跳过或修改以制造全绿；基线完整运行另有一次端口重试数量断言波动。日志分别保留，不能称作完整 Core 通过。

未做此窗口的 Windows/macOS/Linux 原生 GUI、真实键盘／IME、辅助功能、DPI、窄窗口或凭据交互验收。没有可用显示服务，编译结果不代替 UI 验收。文件读取复用 30 秒、2 Mi 字符上限的可取消 Git 只读进程；列表最多 1000 项并明确标注；远程对象本地缺失需显式 Fetch 后刷新，不自动获取独立 push endpoint。

实际推送地址由 Git 解析并在执行前重新核对；普通 insteadOf／pushInsteadOf 和显式 pushurl 优先级已有真实临时仓库测试。会再次改写解析结果的级联 insteadOf 配置明确拒绝，需要配置不再重写的最终地址。审阅窗口的终止按钮复用现有确认、操作身份和 PID 检查，不会将关闭／读取取消当作终止写入。

## 历史与 Diff 响应式候选（2026-11-05，基线 205678e）

- 独立候选：历史附加筛选按需展开，生效条件常显；Diff 窄窗保留文件名和版本；历史侧栏会话偏好不因变宽被覆盖。未合并 main、未推送或发布。
- Linux 云环境：npm ci 离线锁定恢复、Editor build、Node 113/113 测试、整个解决方案 Release warnings-as-errors 构建通过。SDK 10.0.401；Node 24.22.1，与仓库声明的 24.19.0 不同，不能替代固定版本 CI。
- Core 全量 685 项：682 通过、3 失败、0 跳过。新增 8 项布局测试通过，覆盖 600/760/1199/1200/1600 宽度、重复缩放与比较操作。失败项为 Native_download_marker_is_preserved_without_running_an_installer 及 BlameInputProofTests 的 Source_proof_covers_missing_ancestor_attributes_and_repository_overrides、Linked_worktree_sources_include_private_and_common_metadata；未改动的 205678e 独立副本在同一环境定向复跑三项，均重现失败；它们不是此候选新增失败，但完整通过状态未达成。
- Node DOM 测试覆盖双方文件名、完整路径、版本、换行与不存在状态。不能据此声称像素布局、焦点和原生 WebView 通过。
- Chromium headless 尝试被环境 Unix socket 权限拒绝，未获得截图或实际宽度渲染结果。Windows/macOS 原生 GUI、筛选键盘焦点、弹层取消、DPI 与完整工具栏拥挤情况待验收；原生 CI 脚本不支持本 Linux 主机，未执行打包/发布检查。

## 单块直接回退（2026-11-05，独立开发工作区）

单块回退取消确认弹窗；底部提供带文件路径的非模态“可撤销”入口，保留写入期间禁用、过期检查和恢复备份。未保存/正在保存的源码草稿会阻止同文件块回退，不能隐式保存或丢弃。整文件确认不变。

本批真实 Windows/macOS GUI、WebView 焦点与物理键盘尚未验收；未发布，不代表已安装版本具备此行为。

Linux 云工作区验证：锁定还原通过，Release 全解决方案 warnings-as-errors 构建 0 警告/错误；Editor 111/111，回退与恢复专项 27/27。最终 Core 全量 679/680，唯一失败为原有 UpdateTests.Native_download_marker_is_preserved_without_running_an_installer（该基线不支持 Linux 安装更新）。一次中间重跑额外遭遇测试监听端口占用，最终全量重跑未复现。未修改或跳过失败测试。

## 2026-11-05 草稿恢复独立候选（未合并、未发布）

基线 `205678ea396c131d11ca5f08780d97bd6f847431`；工作分支 `candidate/draft-recovery`。默认关闭的本机检查点、显式恢复/放弃/清理、正常退出保留副本、磁盘外部变化防覆盖链路已实现。

- 锁定 NuGet restore 成功，但 NU1900 漏洞数据查询受容器只读目录限制；没有把此项报告为通过。
- `npm ci --offline --cache /tmp/grove-npm-cache` 成功，Editor 全套 114/114。
- Desktop Debug 构建成功，0 error，2 个同上 NU1900 warning。
- Core 全套 702 项中 699 通过，3 失败。三项在 pristine 205678e 同环境定向运行均复现：BlameInputProof 的 Source_proof_covers_missing_ancestor_attributes_and_repository_overrides / Linked_worktree_sources_include_private_and_common_metadata；UpdateTests 的 Native_download_marker_is_preserved_without_running_an_installer（Linux 基线未支持原生更新标记）。未静默跳过或计为全套通过。
- 新增 TextDraftStore 25 项涵盖禁用、重启序列化、原子安装前中断、CAS、仓库隔离、磁盘外部变更/删除、排除路径、容量、坏记录、过期、权限、链接、Clear 后 Keep 验证与 pending 容量。
- 原生 Mac/Windows/Linux WebView、真实重启/崩溃/断电、IME、多窗 UI、Windows ACL 实测未验收。构建与单元测试不等于原生验收。

此候选不修改 main，不包含发布、推送或其他候选合并。

## macOS 标题栏安全区修复（2026-11-05，独立验证分支）

基于联合改动 main `6ddff609` 重放标题栏修复。旧实现以标题栏高度乘 4 预留左侧宽度，原生探针只检查不重叠，未限制多余空白。现在读取本窗口三个 AppKit 按钮的最右边界并保留 8 DIP，右侧保持 12 DIP；窗口布局和全屏恢复重新测量，Windows/Linux 原布局不变。启动时原生几何不可用仍保留保守回退。Intel 与 Apple Silicon 的 NSRect 返回 ABI 分别处理。

云端 Linux：锁定还原、完整解决方案 Release warnings-as-errors 构建通过（0 警告、0 错误）；Core 819/819、Editor 131/131 全部通过且无跳过。Core 运行仅移除了工具注入的文档环境变量，未覆盖用户 Git 配置；Editor 使用仓库指定 Node 24.19.0。

ChromeProbe 增加三个窗口宽度及恢复状态的左侧 8±1 DIP、右侧 12±1 DIP 断言，并保留真实 AppKit 边界、命中和垂直对齐检查。新代码的 Mac 原生结果以本分支精确提交 CI 为准；本地编译不代替 ABI 执行。真实用户 Mac 截图、PyCharm 对照、物理键盘/鼠标、Retina/多屏及完整人工 GUI 仍未验收。不访问用户本地 Codex 或 WT01，不发布安装包。
