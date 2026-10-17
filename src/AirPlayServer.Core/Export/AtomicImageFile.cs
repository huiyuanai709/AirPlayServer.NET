namespace AirPlayServer.Core.Export;

/// <summary>
/// Writes the latest JPEG as mirror.jpg. The bytes land in mirror.jpg.part in the
/// same directory, then replace mirror.jpg. A directory watcher sees the renamed file.
/// </summary>
internal static class AtomicImageFile
{
    public static async Task WriteJpegAsync(string directory, ReadOnlyMemory<byte> jpeg, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(directory);
        var finalPath = Path.Combine(directory, "mirror.jpg");
        var partialPath = finalPath + ".part";
        await File.WriteAllBytesAsync(partialPath, jpeg, cancellationToken).ConfigureAwait(false);
        File.Move(partialPath, finalPath, overwrite: true);
    }
}
