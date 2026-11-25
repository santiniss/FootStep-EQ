using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using StrikeEQ.Audio;
using StrikeEQ.Services;

namespace StrikeEQ;

public partial class App : System.Windows.Application
{
    private const int SW_SHOWNORMAL = 1;

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    protected override void OnStartup(StartupEventArgs e)
    {
        // Elevated helper mode for one-click StrikeEQ Native APO install/uninstall
        if (e.Args.Length >= 2 && (e.Args[0] == "--install-apo" || e.Args[0] == "--uninstall-apo"))
        {
            bool isInstall = e.Args[0] == "--install-apo";
            string deviceId = e.Args[1];
            string? resultFile = e.Args.Length >= 3 ? e.Args[2] : null;

            bool ok = isInstall
                ? NativeApoInstallerService.InstallOnEndpointElevated(deviceId, out string msg)
                : NativeApoInstallerService.UninstallFromEndpointElevated(deviceId, out msg);

            if (!string.IsNullOrWhiteSpace(resultFile))
            {
                try { File.WriteAllText(resultFile, msg); } catch { }
            }

            Shutdown(ok ? 0 : 1);
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        base.OnStartup(e);

        try
        {
            var window = new MainWindow();
            MainWindow = window;
            window.ShowInTaskbar = true;
            window.WindowState = WindowState.Normal;
            window.Show();

            IntPtr hwnd = new WindowInteropHelper(window).EnsureHandle();
            ShowWindow(hwnd, SW_SHOWNORMAL);
            window.Topmost = true;
            window.Activate();
            SetForegroundWindow(hwnd);
            window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
            {
                window.Topmost = false;
                window.Activate();
            });
        }
        catch (Exception ex)
        {
            LogCrash("Startup Exception", ex);
            System.Windows.MessageBox.Show(
                $"Falha ao iniciar o FootStep EQ:\n\n{ex.Message}",
                "FootStep EQ — Erro de Inicialização",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogCrash("UI Exception", e.Exception);
        System.Windows.MessageBox.Show(
            $"Erro inesperado no FootStep EQ:\n\n{e.Exception.Message}\n\nDetalhes salvos em crash.log.",
            "FootStep EQ — Erro",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }

    private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
            LogCrash("Fatal Exception", ex);
    }

    private static void LogCrash(string kind, Exception ex)
    {
        try
        {
            Directory.CreateDirectory(PresetLibrary.SettingsDirectory);
            string logPath = Path.Combine(PresetLibrary.SettingsDirectory, "crash.log");
            File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {kind}:{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // Ignore logging failures
        }
    }
}
