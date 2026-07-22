using Microsoft.Win32;
using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace TickySetup;

internal static class Program
{
    private const string AppName = "Ticky";
    private const string StartupValueName = "Ticky";
    private const string LegacyStartupValueName = "TodoListLight";
    private const string PayloadResourceName = "Ticky-win-x64.zip";

    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();

        try
        {
            var installDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                AppName);

            InstallPayload(installDirectory);
            RegisterStartup(installDirectory);
            CreateStartMenuShortcut(installDirectory);
            LaunchApp(installDirectory);

            MessageBox.Show(
                "Ticky 설치가 완료되었습니다.",
                "Ticky Setup",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"설치 중 오류가 발생했습니다.\n{ex.Message}",
                "Ticky Setup",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private static void InstallPayload(string installDirectory)
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "TickySetup", Guid.NewGuid().ToString("N"));
        var zipPath = Path.Combine(tempDirectory, "Ticky-win-x64.zip");
        var extractDirectory = Path.Combine(tempDirectory, "extract");

        Directory.CreateDirectory(tempDirectory);
        Directory.CreateDirectory(installDirectory);

        using (var payload = Assembly.GetExecutingAssembly().GetManifestResourceStream(PayloadResourceName))
        {
            if (payload is null)
            {
                throw new InvalidOperationException("설치 payload를 찾을 수 없습니다.");
            }

            using var file = File.Create(zipPath);
            payload.CopyTo(file);
        }

        ZipFile.ExtractToDirectory(zipPath, extractDirectory);
        StopRunningApp();
        CopyDirectory(extractDirectory, installDirectory);
    }

    private static void StopRunningApp()
    {
        foreach (var process in Process.GetProcessesByName(AppName))
        {
            try
            {
                process.Kill();
                process.WaitForExit(5000);
            }
            catch
            {
                // Best effort: install may still succeed if files are not locked.
            }
        }
    }

    private static void CopyDirectory(string sourceDirectory, string targetDirectory)
    {
        foreach (var directory in Directory.GetDirectories(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(directory.Replace(sourceDirectory, targetDirectory));
        }

        foreach (var file in Directory.GetFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var target = file.Replace(sourceDirectory, targetDirectory);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static void RegisterStartup(string installDirectory)
    {
        var exePath = Path.Combine(installDirectory, "Ticky.exe");
        using var runKey = Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Run",
            writable: true);

        if (runKey is null)
        {
            return;
        }

        runKey.DeleteValue(LegacyStartupValueName, throwOnMissingValue: false);
        runKey.SetValue(StartupValueName, $"\"{exePath}\"");
    }

    private static void CreateStartMenuShortcut(string installDirectory)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var exePath = Path.Combine(installDirectory, "Ticky.exe");
        var shortcutPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            "Programs",
            "Ticky.lnk");

        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType is null)
        {
            return;
        }

        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic shortcut = shell.CreateShortcut(shortcutPath);
        shortcut.TargetPath = exePath;
        shortcut.WorkingDirectory = installDirectory;
        shortcut.IconLocation = exePath;
        shortcut.Save();

        Marshal.FinalReleaseComObject(shortcut);
        Marshal.FinalReleaseComObject(shell);
    }

    private static void LaunchApp(string installDirectory)
    {
        var exePath = Path.Combine(installDirectory, "Ticky.exe");
        Process.Start(new ProcessStartInfo
        {
            FileName = exePath,
            UseShellExecute = true
        });
    }
}
