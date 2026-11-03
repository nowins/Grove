# 验证范围

## 已核对的当前代码

- Windows 本机：Core 456/456、Editor 73/73，Release 零警告零错误；self-contained publish、编辑器、六种 grammar、许可文件与 LibGit2 原生资产检查通过。
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

## Hosted CI 与验收边界

视觉修改前的清理快照 44a39da 已在[Windows、macOS ARM 与 Intel runner](https://github.com/nowins/Grove/actions/runs/41456640682)分别通过 Core 456/456、Editor 73/73、零警告构建和 self-contained 打包。后续视觉修改的 hosted 结果以[工作流运行](https://github.com/nowins/Grove/actions/workflows/ci.yml)为准；上述本机验证不代替更新后的远端或 macOS 验收。

macOS 桌面、WebView、Cmd 快捷键、输入法、Keychain／askpass、混合 DPI、签名、公证及 .app 打包尚未验收。自动化 Core 测试会实际加载 LibGit2 和操作临时 Git 仓库，但不启动桌面窗口。

本快照不含原机器的 Git 历史、验证材料和个人配置；这些材料留在原本地工作区。CI 产物为未签名测试 archive，不是正式发行。

## 远端 Checkout 与 New Branch 本地验证

Windows Core 480/480、零跳过，Release 零警告；新增测试覆盖自动命名／跟踪、已有本地进度、不同远端同名、多个跟踪分支、嵌套 remote 名称、字面无效名称、脏工作树仅创建、旧快照及其他 worktree 占用。Editor 代码未变，沿用上一批 73/73 结果。

Windows 原生 1382×899 与 884×640：真实临时仓库完成无输入框 Checkout、自定义中文名且不检出、无效名称原位提示、Enter 不提交、Cancel／Esc 取消、worktree 占用及同名远端拒绝。已核对真实分支、跟踪配置、索引和文件；不是 Headless 截图。中文通过文本输入工具上屏，不代表真实 IME 组合验收。浅色、高 DPI、屏幕阅读器与 macOS GUI 仍未验证；本批尚未发布到 GitHub。
