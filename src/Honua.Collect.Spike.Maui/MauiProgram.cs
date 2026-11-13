using Honua.Collect.Spike.Shared;
using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Components.WebView.Maui;
namespace Honua.Collect.Spike.Maui;
public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder().UseMauiApp<SpikeApp>();
        builder.Services.AddMauiBlazorWebView();
        builder.Services.AddSingleton(_ => new SpikeStore(FileSystem.AppDataDirectory, async () =>
        {
            const string slot = "shell-spike-db-key";
            var key = await SecureStorage.GetAsync(slot);
            if (key is null)
            {
                key = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
                await SecureStorage.SetAsync(slot, key);
            }
            return key;
        }));
        builder.Services.AddSingleton<ISpikeCapture, NativeCapture>();
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

public sealed class NativeCapture : ISpikeCapture
{
    public bool IsSupported => MediaPicker.Default.IsCaptureSupported;
    public async Task<(string Path, long Started)> CaptureAsync()
    {
        var photo = await MediaPicker.Default.CapturePhotoAsync() ?? throw new OperationCanceledException();
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var directory = Path.Combine(FileSystem.AppDataDirectory, "spike-photos");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".jpg");
        try
        {
            await using var source = await photo.OpenReadAsync();
            await using var target = File.Create(path);
            await source.CopyToAsync(target);
            await target.FlushAsync();
            return (path, started);
        }
        catch { File.Delete(path); throw; }
    }
}
