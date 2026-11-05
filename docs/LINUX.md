# Linux 开发候选

目标为 Ubuntu 24.04 x64/ARM64 的 glibc 桌面，X11 或带 XWayland 的 Wayland 会话。尚未发布，不宣称纯 Wayland、musl/Alpine 或所有发行版通过验收。

## 依赖与运行

包自带 .NET，不自带 Git、WebKitGTK 或窗口系统。Ubuntu 24.04 / Debian 13：

```sh
sudo apt-get install git libwebkit2gtk-4.1-0 libfontconfig1 libx11-6 libice6 libsm6 libxrandr2 libxi6 libxcursor1 libxext6 xdg-utils
```

Wayland 桌面还需 XWayland 和有效 DISPLAY。以普通用户在桌面会话运行，不禁用 WebKit 沙箱，不以 root 启动。文件定位用 xdg-open 打开所在目录（不选中文件）；终端需要 /usr/bin/x-terminal-emulator，使用目标 WorkingDirectory，不拼接 shell 代码。

所有主窗口、历史、Diff、Merge 编辑器显式选择 WebKitGtk adapter，使用 GTK 3 / WebKitGTK 4.1 X11 后端，不要求 WPE 2.0。数据和缓存继续位于 GitX/GITX_DATA_DIR，缺依赖会明确报错，主窗口仅有纯文本回退，不等于完整编辑器可用。

准备 README 指定的 .NET、Node、PowerShell 7 与 Git，在对应原生主机执行：

```sh
pwsh ./tools/ci.ps1 -RuntimeIdentifier linux-x64
# ARM64 主机改用 linux-arm64
sha256sum -c SHA256SUMS.txt
mkdir grove
tar -xzf desktop-linux-x64.tar.gz -C grove
./grove/Grove /path/to/repository
```

tar.gz 是便携目录，不安装系统文件或桌面入口。工具要求本机架构一致，不将交叉 publish 计作原生验证。源码开发可用 tools/start.ps1。

## 手动更新

更新工具页显示本机 Linux RID，并提供“查看 Releases 并手动更新”，可在浏览器发现稳定版和预览版。若尚无对应架构 Linux 包，继续使用当前开发版本，不将其表述为“已是最新”。不运行启动/定时检查，不读取或改写其他平台留下的更新偏好，禁用后台下载/安装与自动偏好控件；核心也在网络请求前拒绝不支持的 RID。

核对来源和 SHA-256，退出应用后解压新版本到新目录，验证后再决定如何处理旧版本。保留 GitX 用户数据。Linux 自动安装、deb/rpm/AppImage、签名和菜单集成不在本批范围。

## 验证边界

CI 增加 Ubuntu 24.04 x64/ARM64 的 locked restore、Editor/Core 测试、原生 LibGit2 与 ELF 检查、自包含发布、tar.gz 校验/解包/执行位验证。CI 不启动 GUI，nativeGuiTested=false。当前本地结果见 STATUS.md；配置存在不等于远端 CI 已通过。

原生桌面仍需验证主编辑器、Diff、Merge、历史、Markdown、保存/取消、剪贴板、输入法、焦点、快捷键、DPI、多窗口、系统定位/终端与 askpass。虚拟显示探针不替代完整人工验收。
