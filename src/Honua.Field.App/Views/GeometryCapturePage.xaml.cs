// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using Honua.Field.App.Maps;
using Honua.Field.App.Services;
using Honua.Field.Core.Field.Geometry;
using Honua.Field.Core.Maps;
using Honua.Field.Presentation.Geometry;
using Honua.Sdk.Field.Records;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Field.App.Views;

/// <summary>
/// Geometry capture over a live OpenStreetMap basemap (BACKLOG G1/G2/G4): pan by
/// dragging, zoom with the +/– buttons, and tap to drop a vertex for a point,
/// line, or polygon. Screen taps are projected to lat/lon through
/// <see cref="WebMercator"/> (unit-tested in Core), so the captured vertices —
/// and the resulting GeoJSON from the tested <see cref="MapCaptureViewModel"/> —
/// are real coordinates registered to the basemap.
/// </summary>
public partial class GeometryCapturePage : ContentPage
{
    private const double TapThreshold = 12.0; // DIPs of movement under which a gesture is a tap
    private const int MinZoom = 2;
    private const int MaxZoom = 19;

    private readonly OsmTileLoader _tiles;
    private readonly SlippyMapDrawable _map;
    private MapCaptureViewModel _vm = new(CapturedGeometryType.Point);

    private CancellationTokenSource? _areaDownload;
    private PointF _gestureStart;
    private PointF _lastDrag;
    private double _moved;
    private CancellationTokenSource? _gpsCapture;

    public GeometryCapturePage(IHttpClientFactory httpClientFactory)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        _tiles = new OsmTileLoader(
            Path.Combine(FileSystem.AppDataDirectory, "tiles"),
            httpClientFactory.CreateClient(MauiProgram.TileHttpClient),
            httpClientFactory.CreateClient(MauiProgram.OfflineTileHttpClient));
        InitializeComponent();
        OfflineAreaButton.Text = OfflineAreaAction;

        _map = new SlippyMapDrawable(_tiles) { Vertices = _vm.Vertices };
        Canvas.Drawable = _map;
        _tiles.TileLoaded += (_, _) => MainThread.BeginInvokeOnMainThread(() => Canvas.Invalidate());

        TypePicker.SelectedIndex = 0;
        UpdateStatus();
    }

    private void OnWorkspaceSizeChanged(object? sender, EventArgs e)
    {
        // Use the app window so a tablet in split screen gets the compact layout.
        bool sideBySide = Workspace.Width >= 600;
        if (sideBySide == (Grid.GetColumn(Tools) == 1))
        {
            return;
        }

        Workspace.ColumnDefinitions[1].Width = new GridLength(sideBySide ? 288 : 0);
        Grid.SetRow(Tools, sideBySide ? 0 : 1);
        Grid.SetColumn(Tools, sideBySide ? 1 : 0);
        Tools.Margin = sideBySide ? new Thickness(8, 0, 0, 0) : new Thickness(0, 8, 0, 0);
    }

    private void OnTypeChanged(object? sender, EventArgs e)
    {
        StopGpsCapture();
        ShowGpsStatus(null);
        var type = TypePicker.SelectedIndex switch
        {
            1 => CapturedGeometryType.Line,
            2 => CapturedGeometryType.Polygon,
            _ => CapturedGeometryType.Point,
        };

        _vm = new MapCaptureViewModel(type);
        _map.Vertices = _vm.Vertices;
        _map.IsPolygon = type == CapturedGeometryType.Polygon;
        Canvas.Invalidate();
        UpdateStatus();
    }

    private void OnStart(object? sender, TouchEventArgs e)
    {
        if (e.Touches.Length == 0)
        {
            return;
        }

        _gestureStart = e.Touches[0];
        _lastDrag = _gestureStart;
        _moved = 0;
    }

    private void OnDrag(object? sender, TouchEventArgs e)
    {
        if (e.Touches.Length == 0)
        {
            return;
        }

        var p = e.Touches[0];
        var dx = p.X - _lastDrag.X;
        var dy = p.Y - _lastDrag.Y;
        _moved += Math.Abs(dx) + Math.Abs(dy);
        _lastDrag = p;

        // Pan: shift the centre opposite the drag so the map follows the finger.
        var (cx, cy) = WebMercator.ToWorldPixel(_map.Center, _map.Zoom);
        _map.Center = WebMercator.FromWorldPixel(cx - dx, cy - dy, _map.Zoom);
        Canvas.Invalidate();
    }

    private void OnEnd(object? sender, TouchEventArgs e)
    {
        // A gesture that barely moved is a tap → drop a vertex under the finger.
        if (_moved > TapThreshold || _gpsCapture is not null)
        {
            return;
        }

        var point = e.Touches.Length > 0 ? e.Touches[0] : _gestureStart;
        var geo = WebMercator.FromScreen(point.X, point.Y, _map.Center, _map.Zoom, Canvas.Width, Canvas.Height);
        _vm.AddVertex(geo);
        Canvas.Invalidate();
        UpdateStatus();
    }

    private void OnZoomIn(object? sender, EventArgs e) => SetZoom(_map.Zoom + 1);

    private void OnZoomOut(object? sender, EventArgs e) => SetZoom(_map.Zoom - 1);

    private void SetZoom(int zoom)
    {
        _map.Zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        Canvas.Invalidate();
    }

    private static string OfflineAreaAction => OsmTileLoader.SupportsOfflineDownloads ? "Download area" : "Check offline area";

    private async void OnDownloadArea(object? sender, EventArgs e)
    {
        if (_areaDownload is not null)
        {
            _areaDownload.Cancel();
            return;
        }

        var w = Canvas.Width;
        var h = Canvas.Height;
        if (w <= 0 || h <= 0)
        {
            return;
        }

        var zoom = _map.Zoom;
        var topLeft = WebMercator.FromScreen(0, 0, _map.Center, zoom, w, h);
        var bottomRight = WebMercator.FromScreen(w, h, _map.Center, zoom, w, h);
        var bbox = GeoBoundingBox.FromCorners(topLeft, bottomRight);
        using var cancellation = new CancellationTokenSource();
        _areaDownload = cancellation;
        OfflineAreaButton.Text = "Cancel";
        OfflineAreaStatus.IsVisible = true;
        var status = OsmTileLoader.SupportsOfflineDownloads ? "Downloading offline area…" : "Checking offline area…";
        OfflineAreaStatus.Text = status;
        var displayed = 0;
        try
        {
            var maxZoom = OsmTileLoader.SupportsOfflineDownloads ? Math.Min(zoom + 2, MaxZoom) : zoom;
            var plan = OfflineAreaPlanner.Plan(bbox, zoom, maxZoom);
            var progress = new Progress<int>(cached =>
            {
                if (_areaDownload == cancellation && !cancellation.IsCancellationRequested && cached > displayed)
                {
                    displayed = cached;
                    OfflineAreaStatus.Text = $"{status} {cached}/{plan.Count}";
                }
            });

            var result = await _tiles.PrefetchAreaAsync(plan, progress, cancellation.Token);
            OfflineAreaStatus.Text = result switch
            {
                { Plan.ExceedsCap: true } => "Area too large. Zoom in and retry.",
                { IsReady: true } => $"Offline area ready: {result.Cached} tiles cached (zoom {zoom}–{maxZoom}).",
                _ when !OsmTileLoader.SupportsOfflineDownloads =>
                    $"Offline area incomplete: {result.Cached}/{plan.Count} tiles cached. This basemap only caches viewed tiles.",
                _ => $"Offline area incomplete: {result.Cached}/{plan.Count} tiles cached. Retry when connected.",
            };
            Canvas.Invalidate();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            OfflineAreaStatus.Text = "Offline area check cancelled. Cached tiles were kept.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException)
        {
            OfflineAreaStatus.Text = "Could not check the offline area. Retry when storage and connection are available.";
        }
        finally
        {
            _areaDownload = null;
            OfflineAreaButton.Text = OfflineAreaAction;
        }
    }

    private void OnUndo(object? sender, EventArgs e)
    {
        StopGpsCapture();
        if (_vm.UndoCommand.CanExecute(null))
        {
            _vm.UndoCommand.Execute(null);
            Canvas.Invalidate();
            UpdateStatus();
        }
    }

    private void OnClear(object? sender, EventArgs e)
    {
        StopGpsCapture();
        _vm.ClearCommand.Execute(null);
        Canvas.Invalidate();
        UpdateStatus();
    }

    private async void OnDone(object? sender, EventArgs e)
    {
        StopGpsCapture();
        if (!_vm.IsComplete)
        {
            StatusLabel.Text = $"{_vm.GeometryType} needs more vertices.";
            return;
        }

        await DisplayAlert("Geometry captured", _vm.ToGeoJson(), "OK");
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        if (Window is { } window)
        {
            window.Stopped += OnWindowStopped;
        }
    }

    private void OnWindowStopped(object? sender, EventArgs e) => StopGpsCapture();

    protected override void OnDisappearing()
    {
        if (Window is { } window)
        {
            window.Stopped -= OnWindowStopped;
        }

        _areaDownload?.Cancel();
        StopGpsCapture();
        base.OnDisappearing();
    }

    private async void OnGpsCapture(object? sender, EventArgs e)
    {
        if (_gpsCapture is not null)
        {
            StopGpsCapture();
            return;
        }

        using var capture = new CancellationTokenSource();
        _gpsCapture = capture;
        var vm = _vm;
        var geolocation = Geolocation.Default;
        var finished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = false;
        var startedAt = DateTimeOffset.MaxValue;

        void OnLocationChanged(object? source, GeolocationLocationChangedEventArgs update)
        {
            var location = update.Location;
            var receivedAt = DateTimeOffset.UtcNow;
            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (capture.IsCancellationRequested || finished.Task.IsCompleted || location.Timestamp < startedAt ||
                    !vm.TryAddDeviceGpsSample(
                        location.Latitude, location.Longitude, location.Accuracy, location.Timestamp, receivedAt))
                {
                    return;
                }

                ShowGpsProgress(vm);
                if (vm.AveragingSampleCount == MapCaptureViewModel.RequiredDeviceSamples)
                {
                    finished.TrySetResult(true);
                }
            });
        }

        void OnListeningFailed(object? source, GeolocationListeningFailedEventArgs error)
            => finished.TrySetResult(false);

        vm.BeginGpsAveraging();
        GpsButton.Text = "Cancel GPS";
        ShowGpsProgress(vm);
        try
        {
            var permission = await Permissions.CheckStatusAsync<Permissions.LocationWhenInUse>();
            if (permission != PermissionStatus.Granted)
            {
                capture.Token.ThrowIfCancellationRequested();
                permission = await Permissions.RequestAsync<Permissions.LocationWhenInUse>();
            }

            capture.Token.ThrowIfCancellationRequested();
            if (permission != PermissionStatus.Granted)
            {
                ShowGpsStatus("Location permission is required. Enable precise location and retry.");
                return;
            }

            startedAt = DateTimeOffset.UtcNow;
            geolocation.LocationChanged += OnLocationChanged;
            geolocation.ListeningFailed += OnListeningFailed;
            started = await geolocation.StartListeningForegroundAsync(
                new GeolocationListeningRequest(GeolocationAccuracy.Best, TimeSpan.FromSeconds(1)));
            capture.Token.ThrowIfCancellationRequested();
            if (!started || !await finished.Task.WaitAsync(TimeSpan.FromSeconds(25), capture.Token))
            {
                ShowGpsStatus("Could not receive GPS fixes. Check location settings and retry.");
                return;
            }

            capture.Token.ThrowIfCancellationRequested();
            vm.CommitAveragedVertex();
            _map.Center = vm.Vertices[^1];
            Canvas.Invalidate();
            UpdateStatus();
            ShowGpsStatus($"GPS vertex added. Estimated accuracy {vm.Vertices[^1].AccuracyMeters:F1} m.");
        }
        catch (TimeoutException)
        {
            ShowGpsStatus("GPS accuracy was too low or fixes stopped. Move into the open and retry.");
        }
        catch (PermissionException)
        {
            if (!capture.IsCancellationRequested)
            {
                ShowGpsStatus("Location permission is required. Enable precise location and retry.");
            }
        }
        catch (FeatureNotEnabledException)
        {
            if (!capture.IsCancellationRequested)
            {
                ShowGpsStatus("Device location is off. Turn it on and retry.");
            }
        }
        catch (FeatureNotSupportedException)
        {
            if (!capture.IsCancellationRequested)
            {
                ShowGpsStatus("This device does not support GPS capture.");
            }
        }
        catch (OperationCanceledException) when (capture.IsCancellationRequested)
        {
        }
        finally
        {
            geolocation.LocationChanged -= OnLocationChanged;
            geolocation.ListeningFailed -= OnListeningFailed;
            if (started)
            {
                geolocation.StopListeningForeground();
            }

            vm.CancelGpsAveraging();
            _gpsCapture = null;
            GpsButton.Text = "Add GPS vertex";
            GpsButton.IsEnabled = true;
        }
    }

    private void StopGpsCapture()
    {
        if (_gpsCapture is null || _gpsCapture.IsCancellationRequested)
        {
            return;
        }

        _gpsCapture.Cancel();
        GpsButton.IsEnabled = false;
        ShowGpsStatus(null);
    }

    private void ShowGpsProgress(MapCaptureViewModel vm)
        => ShowGpsStatus($"Hold still for GPS fixes ({vm.AveragingSampleCount}/{MapCaptureViewModel.RequiredDeviceSamples}).");

    private void ShowGpsStatus(string? text)
    {
        GpsStatus.Text = text;
        GpsStatus.IsVisible = text is not null;
    }

    private void UpdateStatus()
        => StatusLabel.Text = $"{_vm.GeometryType}: {_vm.Vertices.Count} vertex(es){(_vm.IsComplete ? " ✓" : string.Empty)}";
}
