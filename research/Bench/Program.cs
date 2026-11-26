using System.Diagnostics;
using System.Text.Json;
using Jema;

if (args is not [var mode, var rawPath, var archivePath, .. var settings]
    || mode is not ("raw" or "jema") || settings.Length > 2)
{
    Console.Error.WriteLine("Bench raw|jema PCM.f32le AUDIO.jema [REQUESTS [UNBUFFERED_ALIGNMENT]]");
    return 2;
}

int requests = settings.Length >= 1 ? int.Parse(settings[0]) : 1000;
int alignment = settings.Length == 2 ? int.Parse(settings[1]) : 0;
if (requests < 1)
    throw new ArgumentOutOfRangeException(nameof(requests));

using var headerFile = OpenBuffered(archivePath);
using var header = new JemaReader(headerFile);
int width = header.Layout.BytesPerFrame;
if (header.Frames == 0 || new FileInfo(rawPath).Length != header.Frames * width)
    throw new ArgumentException("A matching nonempty raw file is required.");

bool encoded = mode == "jema";
string path = encoded ? archivePath : rawPath;
using var process = Process.GetCurrentProcess();
if (alignment == 0)
{
    using var resident = OpenBuffered(path);
    resident.CopyTo(Stream.Null, 1024 * 1024);
}
var runtime = new
{
    mode,
    alignment,
    frames = header.Frames,
    bytesPerFrame = width,
    blockFrames = header.BlockFrames,
    rows = Run()
};
process.Refresh();
Console.WriteLine(JsonSerializer.Serialize(new { runtime, peakWorkingSetBytes = process.PeakWorkingSet64 }));
return 0;

List<Sample> Run()
{
    int rate = header.Layout.SampleRate;
    int[] windows = [1, 512, Math.Max(1, Math.Min(rate / 3, 40_000)), (int)Math.Min(rate * 2L, 192_000)];
    var results = new List<Sample>();
    foreach (int window in windows.Distinct())
    {
        int frames = (int)Math.Min(header.Frames, window);
        var random = new Random(17891);
        var starts = Enumerable.Range(0, requests)
            .Select(_ => random.NextInt64(header.Frames - frames + 1)).ToArray();
        results.Add(Measure("random", frames, starts, reopen: false));
    }

    int firstFrames = (int)Math.Min(header.Frames, rate);
    results.Add(Measure("open-and-first", firstFrames, new long[Math.Min(requests, 128)], reopen: true));
    var sequence = Enumerable.Range(0, checked((int)(1 + (header.Frames - 1) / header.BlockFrames)))
        .Select(i => (long)i * header.BlockFrames).ToArray();
    results.Add(Measure("sequential", header.BlockFrames, sequence, reopen: false));
    return results;
}

Sample Measure(string name, int frames, long[] starts, bool reopen)
{
    byte[] output = new byte[frames * width];
    var elapsed = new double[starts.Length];
    var openElapsed = new double[starts.Length];
    long io = 0, decoded = 0, requested = 0, issued = 0;
    using var file = Open(path);
    using var reader = encoded ? new JemaReader(file) : null;
    // Prime the exact read path, including JIT, before taking samples.
    Read(file, reader, 0, output.AsSpan(0, (int)Math.Min(header.Frames, frames) * width));
    if (file is UnbufferedFile prime)
        prime.IssuedBytes = 0;
    process.Refresh();
    var cpuStart = process.TotalProcessorTime;
    for (int i = 0; i < starts.Length; i++)
    {
        long start = starts[i];
        var target = output.AsSpan(0, (int)Math.Min(frames, header.Frames - start) * width);
        long before = Stopwatch.GetTimestamp();
        DecodeWork work;
        if (reopen)
        {
            using var opened = Open(path);
            using var decoder = encoded ? new JemaReader(opened) : null;
            openElapsed[i] = Stopwatch.GetElapsedTime(before).TotalMilliseconds;
            work = Read(opened, decoder, start, target);
            issued += (opened as UnbufferedFile)?.IssuedBytes ?? 0;
        }
        else
        {
            work = Read(file, reader, start, target);
            if (file is UnbufferedFile direct)
            {
                issued += direct.IssuedBytes;
                direct.IssuedBytes = 0;
            }
        }
        elapsed[i] = Stopwatch.GetElapsedTime(before).TotalMilliseconds;
        requested += target.Length;
        io += work.FileBytesRead;
        decoded += work.PcmBytesDecoded;
    }
    process.Refresh();
    double cpu = (process.TotalProcessorTime - cpuStart).TotalMilliseconds;
    return new Sample(name, frames, starts.Length, Distribution.From(elapsed),
        reopen ? Distribution.From(openElapsed) : null, requested, io, decoded, issued, elapsed.Sum(), cpu);
}

Stream Open(string path) => alignment == 0 ? OpenBuffered(path) : new UnbufferedFile(path, alignment);

static FileStream OpenBuffered(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.RandomAccess);

DecodeWork Read(Stream file, JemaReader? reader, long start, Span<byte> bytes)
{
    if (reader is not null)
        return reader.ReadFrames(start, bytes);
    file.Position = start * width;
    file.ReadExactly(bytes);
    return new DecodeWork(bytes.Length, bytes.Length, 0);
}

internal sealed record Sample(string Mode, int Frames, int Requests, Distribution LatencyMs,
    Distribution? OpenMs, long RequestedBytes, long FileBytes, long DecodedBytes, long IssuedUnbufferedBytes,
    double WallMs, double CpuMs);

internal sealed record Distribution(double P50, double P95, double P99, double Max)
{
    public static Distribution From(double[] samples)
    {
        Array.Sort(samples);
        double At(double fraction) => samples[(int)Math.Ceiling(fraction * samples.Length) - 1];
        return new Distribution(At(0.50), At(0.95), At(0.99), samples[^1]);
    }
}
