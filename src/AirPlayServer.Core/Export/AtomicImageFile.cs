namespace AirPlayServer.Core.Export;

/// <summary>
/// Writes the latest JPEG as mirror.jpg. The bytes land in a uniquely named temp
/// file in the same directory, then that file replaces mirror.jpg. The temp file
/// is closed before the replace, so this process does not keep either path locked.
/// </summary>
internal static class AtomicImageFile
{
    public static Task WriteJpegAsync(string directory, ReadOnlyMemory<byte> jpeg, CancellationToken cancellationToken)
        => WriteJpegAsync(directory, jpeg, MoveIntoPlace, cancellationToken);

    /// <summary>
    /// <paramref name="replace"/> is the first publish attempt. Tests pass a
    /// stand-in that throws the Windows <see cref="UnauthorizedAccessException"/>
    /// <c>File.Move</c> raises when a reader has mirror.jpg open without delete sharing.
    /// </summary>
    internal static async Task WriteJpegAsync(
        string directory,
        ReadOnlyMemory<byte> jpeg,
        Action<string, string> replace,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(replace);
        var fullDirectory = Path.GetFullPath(directory);
        try
        {
            Directory.CreateDirectory(fullDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new IOException("TapClock watch directory \"" + fullDirectory + "\" is not writable: " + ex.Message, ex);
        }

        var finalPath = Path.Combine(fullDirectory, "mirror.jpg");
        var partialPath = Path.Combine(fullDirectory, "mirror-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.Read | FileShare.Delete,
                Options = FileOptions.Asynchronous,
            };
            await using (var stream = new FileStream(partialPath, options))
            {
                await stream.WriteAsync(jpeg, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            Exception? denied = null;
            for (var attempt = 0; attempt < 4; attempt++)
            {
                try
                {
                    replace(partialPath, finalPath);
                    return;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    denied = ex;
                }

                try
                {
                    PublishInPlace(partialPath, finalPath);
                    return;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    denied = ex;
                }

                if (attempt < 3)
                {
                    await Task.Delay(20, cancellationToken).ConfigureAwait(false);
                }
            }

            throw new IOException("TapClock could not publish \"" + finalPath + "\": " + denied?.Message, denied);
        }
        catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && !ex.Message.Contains("TapClock", StringComparison.Ordinal))
        {
            throw new IOException("TapClock could not publish \"" + finalPath + "\": " + ex.Message, ex);
        }
        finally
        {
            try
            {
                if (File.Exists(partialPath))
                {
                    File.Delete(partialPath);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static void MoveIntoPlace(string partialPath, string finalPath)
        => File.Move(partialPath, finalPath, overwrite: true);

    /// <summary>
    /// Used when replacing the directory entry is denied. The destination is
    /// opened with read and delete sharing so a watcher can keep its handle.
    /// </summary>
    private static void PublishInPlace(string partialPath, string finalPath)
    {
        using var source = new FileStream(partialPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        using var destination = new FileStream(finalPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        source.CopyTo(destination);
        destination.Flush(flushToDisk: true);
    }
}
