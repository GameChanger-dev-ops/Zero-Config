using System;
using System.Threading;
using System.Windows.Forms;
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
            MessageBox.Show(
                "Zero-Config Bridge is already running in the Windows System Tray.",
                "Zero-Config",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        ApplicationConfiguration.Initialize();
        using var context = new TrayApplicationContext();
        Application.Run(context);
        _singleInstanceMutex.ReleaseMutex();
    }
}