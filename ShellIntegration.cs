using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace KinaView;

internal static class ShellIntegration
{
    private const string ProgId = "KinaView.Image";
    private const string ApplicationKey = @"Software\Classes\Applications\KinaView.exe";
    private const string Capabilities = @"Software\KinaView\Capabilities";
    private const string AppPath = @"Software\Microsoft\Windows\CurrentVersion\App Paths\KinaView.exe";
    private const string ContextKey = @"Software\Classes\*\shell\KinaView";
    private const string FileExts = @"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\";

    internal static string OpenCommand(string executable) => $"\"{Path.GetFullPath(executable)}\" \"%1\"";

    internal static void Register(RegistryKey root, string executable)
    {
        executable = Path.GetFullPath(executable);
        if (!File.Exists(executable)) throw new FileNotFoundException("키나뷰 실행 파일을 찾을 수 없습니다.", executable);
        string command = OpenCommand(executable);
        Set(root, ApplicationKey, "FriendlyAppName", "키나뷰");
        Set(root, ApplicationKey + @"\DefaultIcon", "", $"\"{executable}\",0");
        Set(root, ApplicationKey + @"\shell\open\command", "", command);
        Set(root, @"Software\Classes\" + ProgId, "", "키나뷰 이미지");
        Set(root, @"Software\Classes\" + ProgId + @"\DefaultIcon", "", $"\"{executable}\",0");
        Set(root, @"Software\Classes\" + ProgId + @"\shell\open\command", "", command);
        Set(root, @"Software\Classes\" + ProgId + @"\Application", "ApplicationName", "키나뷰");
        Set(root, @"Software\Classes\" + ProgId + @"\Application", "ApplicationIcon", $"\"{executable}\",0");
        Set(root, Capabilities, "ApplicationName", "키나뷰");
        Set(root, Capabilities, "ApplicationDescription", "사진, GIF, RAW, PSD 및 HDR 이미지 뷰어");
        Set(root, Capabilities, "ApplicationIcon", $"\"{executable}\",0");
        Set(root, @"Software\RegisteredApplications", "KinaView", Capabilities);
        Set(root, AppPath, "", executable);
        Set(root, ContextKey, "", "키나뷰로 열기");
        Set(root, ContextKey, "Icon", $"\"{executable}\",0");
        Set(root, ContextKey, "MultiSelectModel", "Single");
        Set(root, ContextKey, "AppliesTo", string.Join(" OR ", ImageFiles.Extensions.Select(ext => "System.FileExtension:=" + ext)));
        Set(root, ContextKey + @"\command", "", command);
        foreach (string extension in ImageFiles.Extensions)
        {
            Set(root, ApplicationKey + @"\SupportedTypes", extension, "");
            Set(root, Capabilities + @"\FileAssociations", extension, ProgId);
            using (var key = root.CreateSubKey(@"Software\Classes\" + extension + @"\OpenWithProgids"))
                key.SetValue(ProgId, Array.Empty<byte>(), RegistryValueKind.None);
            using (var key = root.CreateSubKey(FileExts + extension + @"\OpenWithProgids"))
                key.SetValue(ProgId, Array.Empty<byte>(), RegistryValueKind.None);
            // Remove the earlier verb location to avoid duplicate menu entries.
            root.DeleteSubKeyTree(@"Software\Classes\SystemFileAssociations\" + extension + @"\shell\KinaView", false);
        }
    }

    internal static void Unregister(RegistryKey root)
    {
        foreach (string extension in ImageFiles.Extensions)
        {
            using (var key = root.OpenSubKey(@"Software\Classes\" + extension + @"\OpenWithProgids", true)) key?.DeleteValue(ProgId, false);
            using (var key = root.OpenSubKey(FileExts + extension + @"\OpenWithProgids", true)) key?.DeleteValue(ProgId, false);
            root.DeleteSubKeyTree(@"Software\Classes\SystemFileAssociations\" + extension + @"\shell\KinaView", false);
        }
        root.DeleteSubKeyTree(ApplicationKey, false);
        root.DeleteSubKeyTree(ContextKey, false);
        root.DeleteSubKeyTree(@"Software\Classes\" + ProgId, false);
        root.DeleteSubKeyTree(Capabilities, false);
        root.DeleteSubKeyTree(AppPath, false);
        using var registered = root.OpenSubKey(@"Software\RegisteredApplications", true);
        registered?.DeleteValue("KinaView", false);
    }

    internal static bool IsRegistered(RegistryKey root, string executable)
    {
        using var key = root.OpenSubKey(ApplicationKey + @"\shell\open\command");
        return key?.GetValue("") as string == OpenCommand(executable);
    }

    private static void Set(RegistryKey root, string path, string name, string value)
    {
        using var key = root.CreateSubKey(path);
        key.SetValue(name, value, RegistryValueKind.String);
    }

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(uint eventId, uint flags, IntPtr item1, IntPtr item2);
    // Wait for association-change delivery before a --register process exits.
    internal static void RefreshExplorer() => SHChangeNotify(0x08000000, 0x1000, IntPtr.Zero, IntPtr.Zero);
}
