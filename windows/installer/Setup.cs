// Установщик FolderSizePanel: один exe, внутри — FolderSizePanel.exe как ресурс.
// Ставит в %LOCALAPPDATA%\Programs\FolderSizePanel (без прав администратора), создаёт ярлык в меню «Пуск»
// и запись в «Приложения → Установленные». Запуск с /uninstall — удаление.
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

static class Setup
{
    const string App = "FolderSizePanel";
    const string Title = "Folder Size Panel";
    const string UninstKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + App;

    static string Dir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\" + App); } }
    static string Shortcut { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), Title + ".lnk"); } }

    // Язык — как у Windows: русский для ru, иначе английский.
    static readonly bool Ru = (Environment.GetEnvironmentVariable("FOLDERSIZE_LANG")
        ?? System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName).StartsWith("ru", StringComparison.OrdinalIgnoreCase);
    static string T(string ru, string en) { return Ru ? ru : en; }

    [STAThread]
    static int Main(string[] args)
    {
        Application.EnableVisualStyles();
        bool quiet = Array.Exists(args, a => a.Equals("/quiet", StringComparison.OrdinalIgnoreCase));
        try
        {
            if (Array.Exists(args, a => a.Equals("/uninstall", StringComparison.OrdinalIgnoreCase))) return Uninstall(quiet);
            return Install(quiet);
        }
        catch (Exception e)
        {
            if (!quiet) MessageBox.Show(T("Ошибка: ", "Error: ") + e.Message, Title, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    static int Install(bool quiet)
    {
        if (!quiet && MessageBox.Show(T("Установить ", "Install ") + Title + "?\n\n" + T("Папка: ", "Folder: ") + Dir, Title,
                MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return 2;

        StopApp();
        Directory.CreateDirectory(Dir);
        string exe = Path.Combine(Dir, App + ".exe");
        using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(App + ".exe"))
        using (var f = File.Create(exe)) s.CopyTo(f);

        string uninst = Path.Combine(Dir, "uninstall.exe");
        string self = Assembly.GetExecutingAssembly().Location;
        if (!string.Equals(Path.GetFullPath(self), Path.GetFullPath(uninst), StringComparison.OrdinalIgnoreCase))
            File.Copy(self, uninst, true);

        MakeShortcut(Shortcut, exe);

        using (var k = Registry.CurrentUser.CreateSubKey(UninstKey))
        {
            k.SetValue("DisplayName", Title);
            k.SetValue("DisplayIcon", exe);
            k.SetValue("DisplayVersion", FileVersionInfo.GetVersionInfo(exe).ProductVersion ?? "1.0");
            k.SetValue("Publisher", Title);
            k.SetValue("InstallLocation", Dir);
            k.SetValue("UninstallString", "\"" + uninst + "\" /uninstall");
            k.SetValue("QuietUninstallString", "\"" + uninst + "\" /uninstall /quiet");
            k.SetValue("EstimatedSize", (int)(new FileInfo(exe).Length / 1024) + 1, RegistryValueKind.DWord);
            k.SetValue("NoModify", 1, RegistryValueKind.DWord);
            k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        }

        Process.Start(exe);
        if (!quiet) MessageBox.Show(Title + T(" установлена и запущена.\nЗначок — в области уведомлений.",
                " is installed and running.\nLook for its icon in the notification area."), Title,
            MessageBoxButtons.OK, MessageBoxIcon.Information);
        return 0;
    }

    static int Uninstall(bool quiet)
    {
        if (!quiet && MessageBox.Show(T("Удалить ", "Uninstall ") + Title + "?", Title,
                MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return 2;

        StopApp();
        Run("schtasks.exe", "/delete /tn \"" + App + "\" /f");
        try { using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true)) if (k != null) k.DeleteValue(App, false); } catch { }
        try { Registry.CurrentUser.DeleteSubKeyTree(@"Software\" + App, false); } catch { }
        try { Registry.CurrentUser.DeleteSubKeyTree(UninstKey, false); } catch { }
        try { File.Delete(Shortcut); } catch { }
        try { File.Delete(Path.Combine(Dir, App + ".exe")); } catch { }

        // Сам uninstall.exe запущен из этой папки — удаляем её после выхода.
        Process.Start(new ProcessStartInfo("cmd.exe",
            "/c ping 127.0.0.1 -n 3 >nul & rd /s /q \"" + Dir + "\"")
            { CreateNoWindow = true, UseShellExecute = false, WorkingDirectory = Path.GetTempPath() });

        if (!quiet) MessageBox.Show(Title + T(" удалена.", " has been uninstalled."), Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
        return 0;
    }

    static void StopApp()
    {
        foreach (var p in Process.GetProcessesByName(App))
            try { p.Kill(); p.WaitForExit(3000); } catch { }
        Thread.Sleep(300);
    }

    static void MakeShortcut(string lnk, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(lnk));
        Type t = Type.GetTypeFromProgID("WScript.Shell");
        dynamic sh = Activator.CreateInstance(t);
        dynamic sc = sh.CreateShortcut(lnk);
        sc.TargetPath = target;
        sc.WorkingDirectory = Path.GetDirectoryName(target);
        sc.Description = Title;
        sc.Save();
    }

    static void Run(string file, string args)
    {
        try
        {
            using (var p = Process.Start(new ProcessStartInfo(file, args) { CreateNoWindow = true, UseShellExecute = false }))
                p.WaitForExit(10000);
        }
        catch { }
    }
}
