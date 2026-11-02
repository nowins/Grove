using Avalonia;

namespace GitX.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (Environment.GetEnvironmentVariable(GitX.Core.GitAskPassSession.PipeVariable) is not null)
        { Environment.ExitCode = GitX.Core.GitAskPassSession.RunClientAsync(args.FirstOrDefault() ?? "Git 凭据").GetAwaiter().GetResult(); return; }
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}
