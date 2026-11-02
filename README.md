# Grove

面向个人本地 Git 工作流程的桌面代码查看器。提供工作区／历史代码浏览、并排 diff、文件结构导航、分支图、暂存与提交、工作树选择、四模式 Reset、独立三栏冲突合并和本地行评论。

支持 Java、C#、Python、JavaScript、TypeScript 的词法高亮与当前文件结构；不提供 LSP、类型检查或跨文件语义定义／引用。工作树入口发现并打开已有目录，不执行 checkout、删除或 prune。Reset 保留快照检查、写锁和恢复材料，Hard 明示丢弃风险。

## 开发运行

安装 global.json 允许的 .NET SDK、.node-version 指定的 Node、Git 和 PowerShell 7。Windows 还需要 Microsoft Edge WebView2 Runtime。

```powershell
./tools/start.ps1 -Repository ../sample-repository
```

省略 Repository 从欢迎界面开始。脚本按锁文件恢复依赖、构建离线编辑器和 Release 应用。内部程序集与 src/GitX.* 路径保持兼容；程序入口为 GitX.Desktop.exe（Windows）或 GitX.Desktop（macOS）。GitX 数据目录和 GITX_DATA_DIR 标识继续沿用，已有配置不因品牌名变化而换目录。

界面精修的现状、证据边界与分批实施状态见[视觉与交互规范](docs/VISUAL-SPEC.md)，逐项区分已有实现和待校准建议。

## 构建与测试

```powershell
./tools/ci.ps1
```

[CI 说明](docs/CI.md)涵盖 Windows x64、macOS arm64 和 macOS x64 的锁定恢复、自动化测试及未签名 self-contained 产物。CI 不启动桌面 GUI，也不执行签名、公证或发布。

当前 Windows 本机完整流程为 456/456 Core、73/73 Editor 测试，构建零警告零错误，打包资源检查通过。macOS runner 及真实桌面验收须分别以实际结果为准，详见 [验证范围](docs/STATUS.md)。

本仓库由当前已核查代码的独立快照开始，不包含原项目 Git 历史、验证截图、原始日志、个人配置或构建产物。
