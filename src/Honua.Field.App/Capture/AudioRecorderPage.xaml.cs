// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using Plugin.Maui.Audio;

namespace Honua.Field.App.Capture;

/// <summary>
/// Records an audio clip (BACKLOG C2) using Plugin.Maui.Audio and returns the
/// saved file path to the caller. Shown modally via <see cref="CaptureAsync"/>.
/// </summary>
public partial class AudioRecorderPage : ContentPage
{
    private readonly IAudioManager _audioManager;
    private readonly TaskCompletionSource<AudioCaptureResult?> _result = new();
    private readonly Stopwatch _elapsed = new();

    private IAudioRecorder? _recorder;
    private string? _savedPath;
    private TimeSpan _recordedDuration;
    private bool _isRecording;

    private AudioRecorderPage(IAudioManager audioManager)
    {
        _audioManager = audioManager;
        InitializeComponent();
    }

    /// <summary>
    /// Presents the recorder modally and returns the recorded audio path, or
    /// <see langword="null"/> if cancelled / nothing was recorded. Use
    /// <see cref="CaptureResultAsync"/> to also retrieve the clip duration.
    /// </summary>
    /// <param name="navigation">The navigation stack to present on.</param>
    /// <returns>The recorded clip path, or null.</returns>
    public static async Task<string?> CaptureAsync(INavigation navigation)
    {
        var result = await CaptureResultAsync(navigation);
        return result?.AudioPath;
    }

    /// <summary>
    /// Presents the recorder modally and returns both the saved audio path and
    /// its recorded duration, or <see langword="null"/> if cancelled.
    /// </summary>
    /// <param name="navigation">The navigation stack to present on.</param>
    /// <returns>The audio capture result, or null.</returns>
    public static async Task<AudioCaptureResult?> CaptureResultAsync(INavigation navigation)
    {
        ArgumentNullException.ThrowIfNull(navigation);

        var page = new AudioRecorderPage(AudioManager.Current);
        await navigation.PushModalAsync(page);
        return await page._result.Task;
    }

    private async void OnToggleRecord(object? sender, EventArgs e)
    {
        if (!_isRecording)
        {
            var status = await Permissions.RequestAsync<Permissions.Microphone>();
            if (status != PermissionStatus.Granted)
            {
                StatusLabel.Text = "Microphone permission denied.";
                return;
            }

            _recorder = _audioManager.CreateRecorder();
            await _recorder.StartAsync();
            _elapsed.Restart();
            _isRecording = true;
            RecordButton.Text = "■ Stop";
            StatusLabel.Text = "Recording…";
        }
        else
        {
            _isRecording = false;
            _elapsed.Stop();
            RecordButton.Text = "● Record";

            var source = await _recorder!.StopAsync();
            _recordedDuration = _elapsed.Elapsed;
            _savedPath = CaptureFiles.NewPath(".wav");
            using (var input = source.GetAudioStream())
            using (var output = File.Create(_savedPath))
            {
                await input.CopyToAsync(output);
            }

            SaveButton.IsEnabled = true;
            StatusLabel.Text = $"Recorded {_recordedDuration.TotalSeconds:F0}s. Tap Save to attach.";
        }
    }

    private async void OnSave(object? sender, EventArgs e)
    {
        var outcome = _savedPath is not null
            ? new AudioCaptureResult(_savedPath, _recordedDuration)
            : null;
        _result.TrySetResult(outcome);
        await Navigation.PopModalAsync();
    }

    private async void OnCancel(object? sender, EventArgs e)
    {
        if (_isRecording && _recorder is not null)
        {
            await _recorder.StopAsync();
        }

        _result.TrySetResult(null);
        await Navigation.PopModalAsync();
    }
}

/// <summary>
/// The outcome of an <see cref="AudioRecorderPage"/> session: the recorded WAV
/// file path and its elapsed duration.
/// </summary>
/// <param name="AudioPath">Host-local path to the recorded WAV clip.</param>
/// <param name="Duration">Elapsed recording duration.</param>
public sealed record AudioCaptureResult(string AudioPath, TimeSpan Duration);
