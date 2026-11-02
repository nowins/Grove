# 开发约定

Grove 当前是个人本地 Git 与代码查看器。核心在 src/GitX.Core，Avalonia 桌面壳在 src/GitX.Desktop，离线 Monaco／Tree-sitter 在 Editor 子目录，真实 Git 集成测试在 tests/GitX.Core.Tests。包版本和运行时图由项目及锁文件维护。

## 工作规则

- 保留工作区已有更改。生产 Git 读写分开；写入使用捕获上下文、重新校验和仓库写锁，不能用真实项目做破坏性测试样本。
- I/O、Git 和大 diff 运算离开 UI 线程；异步结果核对仓库、文件、修订和文档代次。取消读取不等于取消已经开始的 Git 写入。
- 测试 Git 写入使用随机专用临时仓库，检查真实 HEAD、索引、工作文件及其他 worktree。取消和拒绝操作不能伪装成功。
- UI 延续代码优先的深色统一工作区；复用字体、图标、颜色和组件状态。视觉建议与已实现行为分开记录。
- 内部 GitX 程序集、命名空间、数据格式和环境标识保持兼容；涉及配置迁移时先识别数据、备份并限定范围。
- 未经任务授权不提交、推送、创建 PR、发布或启动并行代理工作。

## 验证与完成标准

构建与测试入口见 [CI](CI.md)。使用 npm ci 和 NuGet locked restore，不通过放开锁文件、减少历史上限或跳过失败测试制造通过。Editor 资源随源码构建和 publish，不提交 node_modules、dist 或 bin/obj。

按场景验证真实 Git 状态、旧结果失效、取消和错误处理。Windows／macOS 编译、自动化、原生 WebView／焦点／输入法、DPI 和长期性能分别报告；截图、Headless 或跨目标编译不能代替原生桌面验收。

完成说明包含行为变化、实际验证和剩余边界。当前验证范围见 [STATUS](STATUS.md)。
