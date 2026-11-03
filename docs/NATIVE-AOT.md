# Native AOT 状态

发行包继续使用普通、未裁剪的 self-contained 构建，不能将当前 AOT 实验用作 Release。

Avalonia 12.1.4 支持 Native AOT；实际 WebView 12.1.1 包声明 AOT/trimming 兼容，许可证为 MIT。Windows x64 隔离实验能编译并显示窗口/WebView；LibGit2Sharp 0.32.0 的 AOT 探针成功读取临时仓库。完整应用仍在反射式 System.Text.Json 与反射绑定处失败，包括编辑器桥接、配置/布局、评论、恢复数据和 askpass 等，不能靠关闭警告发布。

首个兼容改造是 SourceLanguage 的语言表 JSON 改用源生成上下文，保持既有数据和文件识别规则。后续需要逐项明确 DTO/JsonTypeInfo，覆盖全部桥接与持久化，并替换 WorktreePanel/MainWindow/WindowChrome 中的反射绑定；针对实际 AOT 发布可执行文件运行文件、历史/Diff、合并、语言、Markdown、评论、布局重启与凭据取消测试。JIT 单元测试通过不能代替该验证。

2026-11-03 Windows x64 实验，排除调试符号后普通包约 135.0 MB，失败的 AOT 实验约 69.6 MB；同口径 ZIP 约 53.7/24.2 MB。三个普通新进程样本的窗口 Opened 中位约 1.411 秒，编辑器 ready 约 2.300 秒；AOT 的单次窗口约 0.468 秒但仓库加载失败，不能计算有效完整启动加速。没有清除 OS 缓存或重启，不是严格冷启动基准。实验原始材料仅保留本机，不上传截图或用户路径。

Windows ARM64 与 macOS 两架构尚未实测 AOT；macOS 需要 Mac/Xcode，不能在 Windows 跨 OS 编译。现有 CI 的 PublishTrimmed=false 以及要求 managed DLL/deps/runtimeconfig 的探针需要独立 AOT 模式。AOT 不替代 WebView、Editor JS/WASM、原生图形库、Git、安装包、证书或公证。

参考：[Avalonia](https://docs.avaloniaui.net/docs/deployment/native-aot)、[Microsoft AOT](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/)、[JSON 源生成](https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/source-generation)。
