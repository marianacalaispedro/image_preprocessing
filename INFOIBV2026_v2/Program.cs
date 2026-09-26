using Avalonia;

class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<ImageApp.App>()
                     .UsePlatformDetect()
                     .LogToTrace();
}
