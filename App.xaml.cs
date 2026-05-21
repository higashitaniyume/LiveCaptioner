namespace LiveCaptioner;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        var settings = new Services.AppSettingsService().Load();
        Localization.LocalizationManager.ApplyCulture(settings.Language);
        base.OnStartup(e);
    }
}
