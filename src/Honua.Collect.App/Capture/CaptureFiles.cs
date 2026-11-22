// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

namespace Honua.Collect.App.Capture;

public static class CaptureFiles
{
    public static string MediaDirectory
    {
        get
        {
            var dir = Path.Combine(FileSystem.AppDataDirectory, "media");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string NewPath(string extension)
        => Path.Combine(MediaDirectory, $"{Guid.NewGuid():n}{extension}");

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
