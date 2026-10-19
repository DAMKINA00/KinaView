using System.IO;

namespace KinaView;

// Accessed only while MainWindow holds decodeGate.
internal sealed class ImageCache
{
    private const long Budget = 192L * 1024 * 1024;
    private sealed record Entry(string Path, long Length, DateTime Modified, LoadedImage Image, long Bytes);
    private readonly LinkedList<Entry> recent = new();
    private long bytes;

    internal LoadedImage Load(string path)
    {
        var file = new FileInfo(path);
        long length = file.Length;
        DateTime modified = file.LastWriteTimeUtc;
        for (var node = recent.First; node != null; node = node.Next)
        {
            if (!string.Equals(node.Value.Path, file.FullName, StringComparison.OrdinalIgnoreCase)) continue;
            recent.Remove(node);
            bytes -= node.Value.Bytes;
            if (node.Value.Length == length && node.Value.Modified == modified)
            {
                recent.AddFirst(node);
                bytes += node.Value.Bytes;
                return node.Value.Image;
            }
            break;
        }
        var image = ImageFiles.Load(path);
        long size = image.Animation != null
            ? image.Animation.Sum(f => (long)f.Bitmap.PixelWidth * f.Bitmap.PixelHeight * 4)
            : (long)((image.Bitmap.PixelWidth * image.Bitmap.Format.BitsPerPixel + 7) / 8) * image.Bitmap.PixelHeight;
        // Files changed during decoding must be decoded again on their next visit.
        file.Refresh();
        if (size <= Budget && file.Exists && file.Length == length && file.LastWriteTimeUtc == modified)
        {
            while (recent.Count >= 3 || bytes + size > Budget)
            {
                bytes -= recent.Last!.Value.Bytes;
                recent.RemoveLast();
            }
            recent.AddFirst(new Entry(file.FullName, length, modified, image, size));
            bytes += size;
        }
        return image;
    }
}
