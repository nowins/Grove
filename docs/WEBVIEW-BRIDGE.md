# Native WebView 脚本边界

macOS 的 WKWebView.evaluateJavaScript 不能直接返回部分 JavaScript 类型，例如 Promise。原先原生端调用异步 window.gitx.load 后，页面已开始加载，但宿主因返回类型报“JavaScript execution returned a result of an unsupported type”。Windows WebView2 对结果的编码行为也与 WebKit 不同。

所有原生脚本调用统一经过 NativeWebViewCommands：命令通过 window.groveNative.command 执行，立即返回固定字符串 ok；异步工作继续运行，并由页面的版本/会话消息回报完成或错误。InvokeCommandAsync 完成只表示命令已派发，不表示异步文档已加载。异步错误报告捕获调用后的文档版本/合并会话，过期错误不影响新文档；同步 JavaScript 异常仍交给原有宿主错误处理。

状态查询通过 window.groveNative.json 返回 JSON 字符串。文件历史的 captureDockView 查询使用有界 JSON 对象解析，兼容 WebView2 的 JSON 编码字符串与 WebKit 的直接字符串；恢复时插入有效 JSON，拒绝畸形、非对象或过大的状态。该路径不新增反射序列化依赖，也不使用 eval。

覆盖主编辑器、独立 Diff、文件历史和合并窗口的原生调用。新增调用应选择命令或 JSON 查询接口，不直接 InvokeScript 返回对象、DOM 元素或 Promise。Editor/merge 页面在 ready 消息之前安装共享边界；文档完成仍用已有 loaded 消息和版本校验。

Node 回归覆盖命令返回类型、同步与异步异常和嵌套状态；C# 回归覆盖宿主结果编码。macOS CI 使用真实 WKWebView API 检查脚本兼容性。引擎 API 探针不代表全部桌面交互测试。

参考：[Apple WKError.javaScriptResultTypeIsUnsupported](https://developer.apple.com/documentation/webkit/wkerror/javascriptresulttypeisunsupported)。
