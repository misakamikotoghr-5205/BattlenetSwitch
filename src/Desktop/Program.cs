using System.Security.Principal;

namespace QuickSwitch;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        using var identity = WindowsIdentity.GetCurrent();
        using var instance = new Mutex(true, @"Global\BattleNetQuickSwitch-" + identity.User?.Value, out var first);
        if (!first)
        {
            MessageBox.Show("战网一键切号已在运行。", "战网一键切号", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var launcher = new WindowsLauncher();
        var engine = new SwitchEngine(Path.Combine(local, "BattleNetQuickSwitch"),
            Path.Combine(roaming, "Battle.net", "Battle.net.config"),
            new ClientCache(Path.Combine(local, "Battle.net", "CachedData.db")), launcher, new WindowsAuth());
        try
        {
            Application.Run(new MainWindow(engine, launcher));
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "战网一键切号", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
