using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using SDL3;

namespace UndertaleModToolAvalonia;

class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        try
        {
            // The main app copies itself to the temp folder and relaunches it with
            // "--update-install <parentPid>": once the main instance has exited, the temp copy
            // replaces the app files with the downloaded update and restarts the app.
            if (args.Length >= 1 && args[0] == "--update-install")
            {
                RunUpdateInstall(args);
                return;
            }

            // The updated app is started by the updater with "deleteTempFolder" so it can clean
            // up after the updater exits; continue with the normal startup afterwards.
            if (args.Length >= 1 && args[0] == "deleteTempFolder")
            {
                _ = Task.Run(CleanupTempFolder);
                args = args.Skip(1).ToArray();
            }

            BuildAvaloniaApp()
                .StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnMainWindowClose);
        }
        catch (Exception e)
        {
            string localAppData = Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UndertaleModToolAvalonia");
            Directory.CreateDirectory(localAppData);

            File.WriteAllText(Path.Join(localAppData, "CrashLog.txt"), e.ToString());

            // TODO: Figure out a way to actually stop the UI and other threads.
            SDL.ShowSimpleMessageBox(SDL3.SDL.MessageBoxFlags.Error,
                "UndertaleModToolAvalonia " + App.VersionString, $"{e}", 0);
            throw;
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new SkiaOptions() { MaxGpuResourceSizeBytes = 512 * 1024 * 1024 })
            .LogToTrace();

    /// <summary>
    /// Self-updater mode: waits for the main instance (identified by <c>args[1]</c>, the parent
    /// process id) to exit, replaces the app files with the downloaded update, then starts the
    /// updated app with <c>deleteTempFolder</c> so it can clean the temp folder up.
    /// </summary>
    static void RunUpdateInstall(string[] args)
    {
        string tempFolder = Path.Join(Path.GetTempPath(), "UndertaleModToolAvalonia");

        if (args.Length < 2 || !int.TryParse(args[1], out int parentPid))
            return;

        try
        {
            // Wait for the main instance to fully exit so its files are no longer locked.
            try
            {
                using (Process? parent = Process.GetProcessById(parentPid))
                {
                    parent.WaitForExit();
                }
            }
            catch (ArgumentException)
            {
                // Parent already exited.
            }
            catch (InvalidOperationException)
            {
                // Parent already exited.
            }

            string appFolderFile = Path.Join(tempFolder, "actualAppFolder");
            if (!File.Exists(appFolderFile))
                return;
            string appFolder = File.ReadAllText(appFolderFile).Trim();
            string updateFolder = Path.Join(tempFolder, "Update");
            if (!Directory.Exists(updateFolder) || !Directory.Exists(appFolder))
                return;

            // Replace the app files with the updated ones, keeping any files that still exist
            // only in the app folder (same behavior as the WPF updater).
            var files = Directory.EnumerateFiles(updateFolder, "*", SearchOption.AllDirectories)
                                 .GroupBy(Path.GetDirectoryName);
            foreach (var folder in files)
            {
                string targetFolder = folder.Key!.Replace(updateFolder, appFolder);
                Directory.CreateDirectory(targetFolder);

                foreach (string file in folder)
                {
                    string targetFile = Path.Join(targetFolder, Path.GetFileName(file));

                    // The just-exited main instance may still briefly lock its files
                    // (or an antivirus may scan them), so retry for a while.
                    for (int attempt = 0; ; attempt++)
                    {
                        try
                        {
                            if (File.Exists(targetFile))
                                File.Delete(targetFile);
                            File.Copy(file, targetFile);
                            break;
                        }
                        catch (IOException) when (attempt < 20)
                        {
                            Thread.Sleep(250);
                        }
                    }
                }
            }

            // Restart the updated app; it will delete the temp folder in the background.
            string appExe = Path.Join(appFolder, Path.GetFileName(Environment.ProcessPath)!);
            if (File.Exists(appExe))
            {
                Process.Start(new ProcessStartInfo(appExe)
                {
                    WorkingDirectory = appFolder,
                    Arguments = "deleteTempFolder",
                });
            }
        }
        catch (Exception e)
        {
            try
            {
                Directory.CreateDirectory(tempFolder);
                File.WriteAllText(Path.Join(tempFolder, "UpdateError.txt"), e.ToString());
            }
            catch
            {
                // Nothing else we can do.
            }
        }
    }

    /// <summary>
    /// Deletes the update temp folder, retrying for a few seconds in case the updater process
    /// (which runs from that folder) hasn't fully exited yet.
    /// </summary>
    static void CleanupTempFolder()
    {
        string tempFolder = Path.Join(Path.GetTempPath(), "UndertaleModToolAvalonia");

        for (int i = 0; i <= 5; i++)
        {
            try
            {
                if (Directory.Exists(tempFolder))
                    Directory.Delete(tempFolder, true);
                return;
            }
            catch
            {
                Thread.Sleep(1000);
            }
        }
    }
}