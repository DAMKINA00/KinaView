using System.IO;
using System.Windows;

namespace KinaView;

public static class App
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] is "--register" or "--unregister")
        {
            try
            {
                if (args[0] == "--register") ShellIntegration.Register(Microsoft.Win32.Registry.CurrentUser, Environment.ProcessPath!);
                else ShellIntegration.Unregister(Microsoft.Win32.Registry.CurrentUser);
                ShellIntegration.RefreshExplorer();
                return 0;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                MessageBox.Show(ex.Message, "키나뷰 · 탐색기 등록 실패");
                return 1;
            }
        }
        AdvancedImages.Initialize();
        var app = new Application();
        var window = new MainWindow();
        if (args.Length == 2 && args[0] == "--self-test")
        {
            window.Loaded += async (_, _) =>
            {
                int code = await SelfTest.Run(window, Path.GetFullPath(args[1]));
                app.Shutdown(code);
            };
        }
        else if (args.Length > 0)
        {
            window.Loaded += async (_, _) => await window.OpenPathAsync(args[0]);
        }
        return app.Run(window);
    }
}
