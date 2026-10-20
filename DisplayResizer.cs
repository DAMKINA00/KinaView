using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ImageMagick;

namespace KinaView;

internal sealed record ResizeResult(BitmapSource Bitmap, bool UsedAi, string? FallbackReason = null)
{
    internal BitmapSource? Enhanced { get; init; }
    internal int AiScale { get; init; }
}

/// <summary>One bounded, latest-request-wins display pipeline per view. Never writes an original file.</summary>
internal sealed class DisplayResizer : IDisposable
{
    private sealed record Job(long Id, BitmapSource Source, int Width, int Height, bool Ai, Action<ResizeResult> Ready);
    private readonly DispatcherTimer debounce = new() { Interval = TimeSpan.FromMilliseconds(160) };
    private static readonly SemaphoreSlim GpuGate = new(1, 1);
    private CancellationTokenSource? cancellation;
    private Job? pending, current;
    private long generation;
    private bool busy, disposed;
    private BitmapSource? cachedSource, enhanced;
    private int cachedScale;
    private Job? completed;
    private ResizeResult? completedResult;

    internal DisplayResizer() => debounce.Tick += (_, _) => { debounce.Stop(); _ = WorkAsync(); };

    internal void Request(BitmapSource source, int targetWidth, int targetHeight, bool useAi, Action<ResizeResult> ready)
    {
        if (disposed) return;
        var (width, height) = BoundSize(targetWidth, targetHeight);
        bool Same(Job? job) => job != null && ReferenceEquals(job.Source, source) && job.Width == width && job.Height == height && job.Ai == useAi;
        if (Same(completed) && completedResult != null)
        {
            generation++; pending = null; debounce.Stop(); cancellation?.Cancel(); ready(completedResult); return;
        }
        if (Same(pending) || pending == null && Same(current) && cancellation?.IsCancellationRequested == false) return;
        pending = new(++generation, source, width, height, useAi, ready);
        cancellation?.Cancel();
        debounce.Stop();
        if (useAi) debounce.Start();
        else _ = WorkAsync();
    }

    private async Task WorkAsync()
    {
        if (busy || disposed) return;
        busy = true;
        try
        {
            while (pending is { } job && !disposed)
            {
                pending = null;
                current = job;
                using var stop = new CancellationTokenSource();
                cancellation = stop;
                var reuse = ReferenceEquals(cachedSource, job.Source) ? enhanced : null;
                int reuseScale = reuse != null ? cachedScale : 0;
                try
                {
                    var result = await Task.Run(() => RenderAsync(job.Source, job.Width, job.Height, job.Ai, stop.Token, reuse, reuseScale));
                    if (disposed || stop.IsCancellationRequested || job.Id != generation) continue;
                    if (result.Enhanced != null)
                    {
                        cachedSource = job.Source; enhanced = result.Enhanced; cachedScale = result.AiScale;
                    }
                    else if (!ReferenceEquals(cachedSource, job.Source)) { cachedSource = enhanced = null; cachedScale = 0; }
                    completed = job; completedResult = result;
                    job.Ready(result);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) when (MainWindow.IsFileError(ex))
                {
                    if (!disposed && job.Id == generation) job.Ready(new(job.Source, false, "표시 보정을 적용하지 못했습니다."));
                }
                finally { if (ReferenceEquals(cancellation, stop)) cancellation = null; current = null; }
            }
        }
        finally { busy = false; }
    }

    internal void Clear()
    {
        generation++; pending = null; debounce.Stop(); cancellation?.Cancel();
        cachedSource = enhanced = null; cachedScale = 0; completed = null; completedResult = null;
    }
    public void Dispose() { if (disposed) return; Clear(); disposed = true; }

    internal static (int Width, int Height) BoundSize(int width, int height)
    {
        width = Math.Max(1, width); height = Math.Max(1, height);
        double ratio = Math.Min(1, Math.Sqrt(16_000_000d / ((double)width * height)));
        ratio = Math.Min(ratio, 16000d / Math.Max(width, height));
        return (Math.Max(1, (int)Math.Floor(width * ratio)), Math.Max(1, (int)Math.Floor(height * ratio)));
    }

    internal static async Task<ResizeResult> RenderAsync(BitmapSource source, int width, int height, bool useAi,
        CancellationToken ct = default, BitmapSource? reuse = null, int reuseScale = 0)
    {
        (width, height) = BoundSize(width, height);
        ct.ThrowIfCancellationRequested();
        double ratio = Math.Max((double)width / source.PixelWidth, (double)height / source.PixelHeight);
        int scale = ratio <= 2 ? 2 : ratio <= 3 ? 3 : 4;
        BitmapSource working = source;
        bool ai = false;
        string? fallback = null;
        if (useAi && ratio > 1.001 && (long)source.PixelWidth * source.PixelHeight * scale * scale <= 32_000_000)
        {
            if (reuse != null && reuseScale >= scale) { working = reuse; scale = reuseScale; ai = true; }
            else
            {
                await GpuGate.WaitAsync(ct);
                try { working = await EnhanceAsync(source, scale, ct); ai = true; }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) when (MainWindow.IsFileError(ex) || ex is System.ComponentModel.Win32Exception)
                { fallback = "AI 보정을 사용할 수 없어 고품질 리사이즈로 표시합니다."; }
                finally { GpuGate.Release(); }
            }
        }
        ct.ThrowIfCancellationRequested();
        var output = ResizeBitmap(working, width, height);
        ct.ThrowIfCancellationRequested();
        return new(output, ai, fallback) { Enhanced = ai ? working : null, AiScale = ai ? scale : 0 };
    }

    internal static BitmapSource ResizeBitmap(BitmapSource source, int width, int height)
    {
        (width, height) = BoundSize(width, height);
        if (source.PixelWidth == width && source.PixelHeight == height) return source;
        using var png = new MemoryStream();
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(source)); encoder.Save(png);
        using var image = new MagickImage(png.ToArray());
        image.FilterType = FilterType.Lanczos;
        image.Resize(new MagickGeometry((uint)width, (uint)height) { IgnoreAspectRatio = true });
        using var pixels = image.GetPixels();
        var data = pixels.ToByteArray(PixelMapping.BGRA)!;
        var result = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, data, width * 4);
        result.Freeze(); return result;
    }

    private static async Task<BitmapSource> EnhanceAsync(BitmapSource source, int scale, CancellationToken ct)
    {
        string runtime = Path.Combine(AppContext.BaseDirectory, "Runtime", "RealESRGAN");
        string exe = Path.Combine(runtime, "realesrgan-ncnn-vulkan.exe");
        if (!File.Exists(exe)) throw new FileNotFoundException("AI resize runtime is missing.");
        string folder = Path.Combine(Path.GetTempPath(), "KinaView-Resize-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string input = Path.Combine(folder, "input.png"), output = Path.Combine(folder, "output.png");
            using (var file = File.Create(input))
            {
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(source)); encoder.Save(file);
            }
            var start = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = runtime };
            foreach (var arg in new[] { "-i", input, "-o", output, "-n", "realesr-animevideov3", "-s", scale.ToString(),
                "-t", "128", "-m", Path.Combine(runtime, "models"), "-j", "1:1:1" }) start.ArgumentList.Add(arg);
            using var child = new Process { StartInfo = start };
            child.Start();
            using var registration = ct.Register(() => { try { if (!child.HasExited) child.Kill(entireProcessTree: true); }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { } });
            var stdout = DrainAsync(child.StandardOutput, ct);
            var stderr = DrainAsync(child.StandardError, ct);
            try
            {
                await child.WaitForExitAsync(ct);
                await Task.WhenAll(stdout, stderr);
                ct.ThrowIfCancellationRequested();
                if (child.ExitCode != 0 || !File.Exists(output)) throw new IOException("AI resize process failed.");
                using var file = File.OpenRead(output);
                var result = new BitmapImage(); result.BeginInit(); result.CacheOption = BitmapCacheOption.OnLoad;
                result.StreamSource = file; result.EndInit(); result.Freeze();
                return result;
            }
            finally
            {
                try { if (!child.HasExited) child.Kill(entireProcessTree: true); }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                try { await child.WaitForExitAsync(); await Task.WhenAll(stdout, stderr); } catch (OperationCanceledException) { }
            }
        }
        finally
        {
            // Only these two owned temporary images are ever written or removed.
            try { File.Delete(Path.Combine(folder, "input.png")); File.Delete(Path.Combine(folder, "output.png")); Directory.Delete(folder); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static async Task DrainAsync(StreamReader reader, CancellationToken ct)
    {
        var buffer = new char[2048];
        while (await reader.ReadAsync(buffer.AsMemory(), ct) > 0) { }
    }
}
