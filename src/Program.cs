namespace ModernHFS;

static class Program
{
    [STAThread]
    static void Main()
    {
        // One instance only: a second launch just brings the running one back from the tray.
        using var mutex = new Mutex(true, @"Local\ModernHFS.Instance", out bool first);
        if (!first)
        {
            try { EventWaitHandle.OpenExisting(@"Local\ModernHFS.Show").Set(); } catch { }
            return;
        }
        using var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\ModernHFS.Show");

        ApplicationConfiguration.Initialize();
        var form = new MainForm();
        var waiter = new Thread(() =>
        {
            while (showEvent.WaitOne())
            {
                try { form.BeginInvoke(form.RestoreFromTray); } catch { return; }
            }
        }) { IsBackground = true };
        waiter.Start();
        Application.Run(form);
    }
}
