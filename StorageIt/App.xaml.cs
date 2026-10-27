using System.Windows;

namespace StorageIt;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // The Fluent theme follows Windows' light/dark setting. STORAGEIT_THEME=light|dark forces one,
        // which is handy for checking both looks without touching the system setting.
        switch (Environment.GetEnvironmentVariable("STORAGEIT_THEME")?.ToLowerInvariant())
        {
            case "dark": ThemeMode = ThemeMode.Dark; break;
            case "light": ThemeMode = ThemeMode.Light; break;
        }
        base.OnStartup(e);
    }
}
