using ConduitLLM.Core.Models;

namespace ConduitLLM.Core.Utilities;

/// <summary>Bounds and validates recovered media before the existing storage pipeline consumes it.</summary>
public static class RecoveryMediaValidation
{
    public static async Task<(FileStream Stream, string ContentType)> BufferAsync(Stream source, MediaType type, CancellationToken token)
    {
        var limit = type == MediaType.Image ? 20L * 1024 * 1024 : 500L * 1024 * 1024;
        var file = new FileStream(Path.GetTempFileName(), FileMode.Open, FileAccess.ReadWrite, FileShare.None,
            81920, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
        try
        {
            var buffer = new byte[81920];
            int read;
            long size = 0;
            while ((read = await source.ReadAsync(buffer, token)) != 0)
            {
                size += read;
                if (size > limit) throw new InvalidOperationException("Recovered media exceeds the size limit.");
                await file.WriteAsync(buffer.AsMemory(0, read), token);
            }
            if (size < 12) throw new InvalidOperationException("Recovered media is empty or malformed.");
            file.Position = 0;
            var header = new byte[12];
            await file.ReadExactlyAsync(header, token);
            file.Position = 0;
            var mime = type == MediaType.Image ? ImageUtility.DetectMimeType(header) :
                VideoUtils.ValidateVideoFile(file, limit) ?
                    (header[0] == 0x1a ? "video/webm" : header[0] == 0x52 ? "video/x-msvideo" : "video/mp4") : null;
            if (mime == null) throw new InvalidOperationException("Recovered media has an unsupported format.");
            return (file, mime);
        }
        catch { await file.DisposeAsync(); throw; }
    }
}
