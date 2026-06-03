namespace Honua.Collect.App.Capture;

/// <summary>
/// Helpers for landing captured media in the app's private data directory so the
/// files outlive the OS temp/cache locations that camera and picker results use,
/// and so the sync layer has a stable local path to upload.
/// </summary>
public static class CaptureFiles
{
    /// <summary>The directory captured media is copied into.</summary>
    public static string MediaDirectory
    {
        get
        {
            var dir = Path.Combine(FileSystem.AppDataDirectory, "media");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>A fresh, unique path under <see cref="MediaDirectory"/> with the given extension.</summary>
    /// <param name="extension">File extension including the leading dot (e.g. <c>.jpg</c>).</param>
    /// <returns>The new absolute path.</returns>
    public static string NewPath(string extension)
        => Path.Combine(MediaDirectory, $"{Guid.NewGuid():n}{extension}");

    /// <summary>Copies a picker/camera result into <see cref="MediaDirectory"/>.</summary>
    /// <param name="file">The captured or picked file.</param>
    /// <returns>The stable local path of the imported copy.</returns>
    public static async Task<string> ImportAsync(FileResult file)
    {
        ArgumentNullException.ThrowIfNull(file);

        var extension = Path.GetExtension(file.FileName);
        if (string.IsNullOrEmpty(extension))
        {
            extension = ".bin";
        }

        var destination = NewPath(extension);
        using var source = await file.OpenReadAsync();
        using var output = File.Create(destination);
        await source.CopyToAsync(output);
        return destination;
    }
}
