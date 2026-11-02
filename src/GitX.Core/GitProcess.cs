using System.ComponentModel;
using System.Diagnostics;

namespace GitX.Core;

internal static class GitProcess
{
    public static Process Start(ProcessStartInfo start)
    {
        try { return Process.Start(start) ?? throw new IOException("无法启动 Git。"); }
        catch (Win32Exception e)
        {
            if (!Directory.Exists(start.WorkingDirectory)) throw new IOException("Git 工作目录不存在或不可访问，请重新选择仓库。", e);
            throw new IOException(e.NativeErrorCode switch
            {
                2 or 3 => "找不到 Git 可执行文件。请安装 Git 并将其加入 PATH，然后重新启动 GitX。",
                5 or 13 => "没有权限启动 Git。请检查可执行文件和工作目录的访问权限。",
                _ => "无法启动 Git：" + e.Message
            }, e);
        }
    }
}
