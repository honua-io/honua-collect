using Honua.Collect.Spike.Shared;
using Microsoft.AspNetCore.Components.WebView.Maui;
namespace Honua.Collect.Spike.Maui;
public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder().UseMauiApp<SpikeApp>();
        builder.Services.AddMauiBlazorWebView();
        builder.Services.AddSingleton(_ => new SpikeStore(FileSystem.AppDataDirectory));
        return builder.Build();
    }
}
public sealed class SpikeApp : Application
{
    protected override Window CreateWindow(IActivationState? activationState)
    {
        var view = new BlazorWebView { HostPage = "wwwroot/index.html" };
        view.RootComponents.Add(new RootComponent { Selector = "#app", ComponentType = typeof(Shell) });
        return new Window(new ContentPage { Content = view });
    }
}
