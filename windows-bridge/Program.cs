using System;
using System.Threading;
using System.Windows;
using ZeroConfigBridge.UI;

namespace ZeroConfigBridge;

internal static class Program
{
    private static Mutex? _singleInstanceMutex;

    [STAThread]
    private static void Main()
    {
        _singleInstanceMutex = new Mutex(true, "Global\\ZeroConfigBridge_SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            System.Windows.MessageBox.Show(
                "Zero-Config Bridge is already running in the Windows System Tray.",
                "Zero-Config",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var app = new System.Windows.Application
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown
        };

        var mainWindow = new MainWindow();
        using var trayHost = new TrayIconHost(mainWindow, () =>
        {
            mainWindow.AllowExit = true;
            mainWindow.CleanupOnExitSync();
            mainWindow.Close();
            app.Shutdown();
        });

        AppDomain.CurrentDomain.ProcessExit += (_, _) => mainWindow.CleanupOnExitSync();
        mainWindow.Show();
        app.Run();
        _singleInstanceMutex.ReleaseMutex();
    }
}