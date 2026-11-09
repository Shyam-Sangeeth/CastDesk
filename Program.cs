using CastDesk.Configuration;
using CastDesk.Diagnostics;
using Microsoft.Web.WebView2.Core;

namespace CastDesk;

internal static class Program
{
    // Two copies would fight over the DIAL port and the phone's session, so only one may run.
    private const string SingleInstanceMutexName = @"Local\CastDesk.SingleInstance";

    // Set by "CastDesk.exe --exit" to ask the running copy to quit (the installer uses it before replacing files).
    private const string ExitEventName = @"Local\CastDesk.Exit";

    /// <summary>
    /// Command-line options, used by the installer:
    /// <c>--exit</c> closes a running CastDesk and waits for it to quit;
    /// <c>--add-startup</c> and <c>--remove-startup</c> turn "Start CastDesk when I sign in" on or off.
    /// Each one does its job and returns without starting CastDesk.
    /// </summary>
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length > 0)
        {
            switch (args[0].ToLowerInvariant())
            {
                case "--exit": return ExitRunningCopy();
                case "--add-startup": return StartupRegistration.SetEnabled(true) == null ? 0 : 1;
                case "--remove-startup": return StartupRegistration.SetEnabled(false) == null ? 0 : 1;
            }
        }

        using var singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            MessageBox.Show(
                "CastDesk is already running. Look for its icon in the notification area of the taskbar.",
                "CastDesk", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }

        ApplicationConfiguration.Initialize();
        Application.ThreadException += (_, e) => AppLog.Write("host", $"Unhandled UI exception: {e.Exception}");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => AppLog.Write("host", $"Unhandled exception: {e.ExceptionObject}");

        // YouTube's TV app runs in Microsoft Edge WebView2, which Windows 11 includes.
        try
        {
            AppLog.Write("host", $"CastDesk starting (WebView2 {CoreWebView2Environment.GetAvailableBrowserVersionString()}).");
        }
        catch (WebView2RuntimeNotFoundException)
        {
            MessageBox.Show(
                "CastDesk needs the Microsoft Edge WebView2 Runtime, which isn't installed.\n\n" +
                "Install it from https://developer.microsoft.com/microsoft-edge/webview2/ and start CastDesk again.",
                "CastDesk", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }

        // So Windows' media controls show "CastDesk" instead of "Unknown app".
        CastDesk.Interop.AppIdentity.Apply("CastDesk", Path.Combine(AppContext.BaseDirectory, "Assets", "CastDesk.ico"));

        var mainForm = new MainForm(CastDeskOptions.Load());
        using var exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ExitEventName);
        var exitWait = ThreadPool.RegisterWaitForSingleObject(exitEvent, (_, _) =>
        {
            AppLog.Write("host", "Asked to exit (--exit).");
            // On a pool thread: an exception here would crash CastDesk instead of closing it.
            try
            {
                // Still starting: wait for the window, which has the message loop to exit through.
                for (var waited = 0; !mainForm.IsHandleCreated && waited < 100; waited++) Thread.Sleep(100);
                mainForm.BeginInvoke(mainForm.ExitApplication);
            }
            catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException)
            {
                // Already closing.
            }
        }, null, Timeout.Infinite, executeOnlyOnce: true);
        Application.Run(mainForm);
        exitWait.Unregister(null);
        return 0;
    }

    /// <summary>Asks a running CastDesk to quit and waits up to 15 seconds for it. 0 when none is left running.</summary>
    private static int ExitRunningCopy()
    {
        if (!EventWaitHandle.TryOpenExisting(ExitEventName, out var exitEvent)) return 0; // not running
        using (exitEvent) exitEvent.Set();

        using var mutex = new Mutex(false, SingleInstanceMutexName);
        try
        {
            if (!mutex.WaitOne(TimeSpan.FromSeconds(15))) return 1;
            mutex.ReleaseMutex();
        }
        catch (AbandonedMutexException)
        {
            // It quit without releasing the mutex; it's gone either way.
        }
        return 0;
    }
}
