using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AeroCut;

static class FFmpeg
{
    static string ExePath => Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffmpeg.exe");

    static string N(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    public static List<string> BuildArgs(string input, string output, double start, double duration,
        string? audioFilter, bool compress, int quality, string? crop, bool forceReencode)
    {
        string ext = Path.GetExtension(output).ToLowerInvariant();
        bool sameExt = string.Equals(Path.GetExtension(input), ext, StringComparison.OrdinalIgnoreCase);
        bool webm = ext == ".webm";

        var a = new List<string>
        {
            "-y",
            "-ss", N(start),
            "-i", input,
            "-t", N(duration)
        };

        if (!forceReencode && !compress && audioFilter == null && crop == null && sameExt)
        {
            a.Add("-c");
            a.Add("copy");
        }
        else if (webm)
        {
            int crf = compress ? (int)Math.Round(24 + quality / 100.0 * (42 - 24)) : 24;
            if (crop != null) { a.Add("-vf"); a.Add(crop); }
            a.AddRange(new[] { "-c:v", "libvpx-vp9", "-crf", crf.ToString(), "-b:v", "0", "-row-mt", "1" });
            a.AddRange(new[] { "-c:a", "libopus" });
            if (audioFilter != null) { a.Add("-af"); a.Add(audioFilter); }
        }
        else
        {
            int crf = compress ? (int)Math.Round(18 + quality / 100.0 * (34 - 18)) : 18;
            if (crop != null) { a.Add("-vf"); a.Add(crop); }
            a.AddRange(new[] { "-c:v", "libx264", "-preset", "medium", "-crf", crf.ToString(), "-pix_fmt", "yuv420p" });
            a.AddRange(new[] { "-c:a", "aac", "-b:a", "192k" });
            if (ext is ".mp4" or ".mov" or ".m4v")
                a.AddRange(new[] { "-movflags", "+faststart" });
            if (audioFilter != null) { a.Add("-af"); a.Add(audioFilter); }
        }

        a.Add(output);
        return a;
    }

    public static Task<bool> ExtractFrameAsync(string input, double time, string output)
        => RunQuiet(new List<string> { "-y", "-ss", N(time), "-i", input, "-frames:v", "1", output });

    public static Task<bool> WaveformAsync(string input, string output, int width, int height, string colorHex)
        => RunQuiet(new List<string>
        {
            "-y", "-i", input,
            "-filter_complex", $"showwavespic=s={width}x{height}:colors={colorHex}",
            "-frames:v", "1", output
        });

    static async Task<bool> RunQuiet(List<string> args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ExePath,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var proc = new Process { StartInfo = psi };
        proc.Start();
        _ = proc.StandardError.ReadToEndAsync();
        _ = proc.StandardOutput.ReadToEndAsync();
        await proc.WaitForExitAsync();
        return proc.ExitCode == 0;
    }

    public static async Task RunAsync(List<string> args, double totalSeconds,
        IProgress<double>? progress, Action<Process> onStart, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ExePath,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var proc = new Process { StartInfo = psi };
        proc.Start();
        onStart(proc);

        _ = proc.StandardOutput.ReadToEndAsync();
        var error = new StringBuilder();
        string? line;
        while ((line = await proc.StandardError.ReadLineAsync()) != null)
        {
            error.AppendLine(line);
            if (progress != null && totalSeconds > 0)
            {
                int i = line.IndexOf("time=", StringComparison.Ordinal);
                if (i >= 0)
                {
                    string value = line.Substring(i + 5).TrimStart();
                    int space = value.IndexOf(' ');
                    if (space > 0)
                        value = value.Substring(0, space);
                    if (TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var ts))
                        progress.Report(Math.Clamp(ts.TotalSeconds / totalSeconds, 0, 1));
                }
            }
        }

        await proc.WaitForExitAsync(ct);

        if (proc.ExitCode != 0)
        {
            string text = error.ToString();
            int cut = Math.Max(0, text.Length - 600);
            throw new Exception("ffmpeg failed:\n" + text.Substring(cut));
        }
    }
}
