using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using SensaDS5Control.Core;

namespace SensaDS5Control.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        var settings = AppSettings.Load();
        ThemeManager.Apply(settings.ThemeId);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow(settings);
        }
        base.OnFrameworkInitializationCompleted();
    }
}
