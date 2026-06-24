using System.IO.Compression;

namespace Honua.Collect.Core.Reports;

/// <summary>
/// Bundles a bulk-report <see cref="ReportManifest"/> into a single shareable zip
/// archive (epic #5 / R2b) — closing the gap the <see cref="BulkReportGenerator"/>
/// leaves open ("the host writes these to disk or zips them"). Platform-neutral via
/// <see cref="System.IO.Compression"/>, so one download/attachment carries every
/// rendered report instead of N loose files.
/// </summary>
public static class ReportArchive
{
    /// <summary>Zips a manifest's entries into a single archive's bytes.</summary>
    /// <param name="manifest">The bulk-report manifest to bundle.</param>
    /// <returns>
    /// The zip archive bytes. Each <see cref="ReportManifestEntry"/> becomes one zip
    /// entry named by its (already sanitised and de-duplicated)
    /// <see cref="ReportManifestEntry.FileName"/>. An empty manifest yields a valid,
    /// empty zip.
    /// </returns>
    public static byte[] Bundle(ReportManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entry in manifest.Entries)
            {
                var archiveEntry = zip.CreateEntry(entry.FileName, CompressionLevel.Optimal);

                // Stamp a deterministic timestamp so identical inputs produce identical
                // archives (reproducible exports); DateTimeOffset.MinValue is outside the
                // zip epoch and would throw, so use the start of the DOS-time range.
                archiveEntry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

                using var stream = archiveEntry.Open();
                stream.Write(entry.Content);
            }
        }

        return buffer.ToArray();
    }
}
