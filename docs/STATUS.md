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

## Windows ARM64 流水线

已增加 windows-11-arm 原生 runner、win-arm64 依赖锁和 self-contained 打包；架构探针校验进程、Node 与 Windows 原生 DLL。该轮 hosted 结果以工作流实际运行记录为准；本机为 x64，交叉 publish 只验证包内容，不构成 ARM64 执行或 GUI 通过。

CI 错误诊断修复 8d6c308 的[三平台运行](https://github.com/nowins/Grove/actions/runs/41491008333)已各自通过 Core 459/459、Editor 73/73 及打包；其中包含早退出子进程 stdin 关闭时保留 Git stderr/退出码的回归测试。

## Hosted CI 与验收边界

视觉修改前的清理快照 44a39da 已在[Windows、macOS ARM 与 Intel runner](https://github.com/nowins/Grove/actions/runs/41456640682)分别通过 Core 456/456、Editor 73/73、零警告构建和 self-contained 打包。后续视觉修改的 hosted 结果以[工作流运行](https://github.com/nowins/Grove/actions/workflows/ci.yml)为准；上述本机验证不代替更新后的远端或 macOS 验收。

macOS 桌面、WebView、Cmd 快捷键、输入法、Keychain／askpass、混合 DPI、签名、公证及 .app 打包尚未验收。自动化 Core 测试会实际加载 LibGit2 和操作临时 Git 仓库，但不启动桌面窗口。

本快照不含原机器的 Git 历史、验证材料和个人配置；这些材料留在原本地工作区。CI 产物为未签名测试 archive，不是正式发行。
