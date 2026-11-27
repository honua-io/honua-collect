// Copyright (c) Honua Geo Inc.
// SPDX-License-Identifier: Apache-2.0

namespace Honua.Field.Core.Forms;

/// <summary>Resolves private evidence captured before iOS relocated the app's data container.</summary>
internal static class CapturedMediaPath
{
    internal static string Resolve(string path, string? mediaDirectory)
    {
        if (mediaDirectory is null || !Path.IsPathFullyQualified(path)
            || path != Path.GetFullPath(path)
            || !Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out _)
            || !IsIosMediaDirectory(Path.GetDirectoryName(path)) || !IsIosMediaDirectory(mediaDirectory))
        {
            return path;
        }

        return Path.Combine(mediaDirectory, Path.GetFileName(path));
    }

    private static bool IsIosMediaDirectory(string? path)
    {
        if (path is null || !Path.IsPathFullyQualified(path))
        {
            return false;
        }

        var media = new DirectoryInfo(path);
        return media.Name == "media" && media.Parent is { Name: "Library", Parent: { } container }
            && Guid.TryParseExact(container.Name, "D", out _) && container.Parent?.Name == "Application";
    }
}
