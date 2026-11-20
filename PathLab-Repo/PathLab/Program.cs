using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using PathLab.Ui;

namespace PathLab;

internal static class Program
{
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("kernel32.dll")]
    private static extern uint GetConsoleProcessList(uint[] processList, uint processCount);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr handle, int command);

    [STAThread]
    private static int Main(string[] args)
    {
        // Headless check used during development and CI: no window is created.
        if (args.Any(a => a.Equals("--selftest", StringComparison.OrdinalIgnoreCase)))
            return SelfTest.Run();

        // Hide the console only when this process owns it (launched from Explorer).
        // If a terminal launched us, its console is shared and must stay visible.
        IntPtr console = GetConsoleWindow();
        if (console != IntPtr.Zero)
        {
            var buffer = new uint[2];
            uint owners = GetConsoleProcessList(buffer, (uint)buffer.Length);
            if (owners <= 1) ShowWindow(console, 0);
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MainForm());
        return 0;
    }
}
