using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;

namespace Mp4ToGifWinForms;

public partial class Form1 : Form
{
    private const string FfmpegDownloadUrl = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";
    private const int ParallelChunkCount = 6;
    private const int StreamBufferSize = 131072; // 128 KB

    private static readonly HttpClient SharedHttpClient = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = true,
        MaxConnectionsPerServer = 16,
        AutomaticDecompression = DecompressionMethods.None
    });

    private readonly TextBox _txtSource = new();
    private readonly TextBox _txtDestination = new();
    private readonly ComboBox _cmbQuality = new();
    private readonly Button _btnBrowseSource = new();
    private readonly Button _btnBrowseDest = new();
    private readonly Button _btnConvert = new();
    private readonly ProgressBar _progressBar = new();
    private readonly Label _lblStatus = new();

    private string FfmpegExePath => Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");

    private sealed record RangeProbeResult(bool SupportsRanges, long? TotalBytes, byte[]? InitialFirstByte);

    public Form1()
    {
        Text = "MP4 to GIF Converter (FFmpeg)";
        Size = new Size(560, 285);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;

        BuildControls();

        Load += (_, _) =>
        {
            _cmbQuality.Items.AddRange(
            [
                "Low (Small File)",
                "Medium (Balanced)",
                "High (Sharp & Smooth)",
                "Extra High (4fbs)",
                "Extra High (15fbs)"
            ]);
            _cmbQuality.SelectedIndex = 1;
            UpdateConvertButtonState();
        };

        Shown += Form1_Shown;
    }

    private async void Form1_Shown(object? sender, EventArgs e)
    {
        if (File.Exists(FfmpegExePath))
        {
            _lblStatus.Text = "Ready (ffmpeg.exe found in app folder).";
            UpdateConvertButtonState();
            return;
        }

        UpdateConvertButtonState();

        DialogResult choice = MessageBox.Show(
            this,
            "ffmpeg.exe was not found in the application folder.\n\nWould you like to download FFmpeg now?",
            "FFmpeg Required",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);

        if (choice != DialogResult.Yes)
        {
            _lblStatus.Text = "ffmpeg.exe missing. Place ffmpeg.exe in the app folder to convert videos.";
            return;
        }

        await DownloadAndInstallFfmpegAsync();
    }

    private async Task DownloadAndInstallFfmpegAsync()
    {
        SetUiBusy(true, "Checking server range support & file size...", showProgress: true, marquee: true);

        string tempDir = Path.Combine(Path.GetTempPath(), "Mp4ToGif_FFmpeg_" + Guid.NewGuid().ToString("N"));
        string archivePath = Path.Combine(tempDir, "ffmpeg_download.zip");
        Directory.CreateDirectory(tempDir);

        try
        {
            RangeProbeResult probe = await ProbeRangeSupportAsync(FfmpegDownloadUrl);
            long finalDownloadedBytes;

            if (probe.SupportsRanges && probe.TotalBytes is > 1)
            {
                string initialMsg = $"Downloading FFmpeg ({FormatBytes(probe.TotalBytes.Value)}) via {ParallelChunkCount} connections...";
                SetUiBusy(true, initialMsg, showProgress: true, marquee: false, progressValue: 0);

                finalDownloadedBytes = await DownloadParallelRangesAsync(
                    FfmpegDownloadUrl,
                    archivePath,
                    probe.TotalBytes.Value,
                    probe.InitialFirstByte);
            }
            else
            {
                string fallbackMsg = probe.TotalBytes is > 0
                    ? $"Downloading FFmpeg ({FormatBytes(probe.TotalBytes.Value)}) via curl (single stream)..."
                    : "Downloading FFmpeg via curl (single stream)...";

                SetUiBusy(true, fallbackMsg, showProgress: true, marquee: !probe.TotalBytes.HasValue, progressValue: 0);

                string curlArgs = $"-L --fail --silent --show-error \"{FfmpegDownloadUrl}\" -o \"{archivePath}\"";
                var curlResult = await RunCurlWithByteProgressAsync(curlArgs, archivePath, probe.TotalBytes);

                if (curlResult.ExitCode != 0)
                {
                    throw new InvalidOperationException(
                        $"curl exited with code {curlResult.ExitCode}: {GetLastLines(curlResult.StandardError, 5)}");
                }

                finalDownloadedBytes = GetFileLengthSafe(archivePath);
            }

            string totalLabel = probe.TotalBytes is > 0
                ? FormatBytes(probe.TotalBytes.Value)
                : FormatBytes(finalDownloadedBytes);

            _progressBar.Style = ProgressBarStyle.Continuous;
            _progressBar.Value = 100;
            _lblStatus.Text = $"Downloaded {FormatBytes(finalDownloadedBytes)} / {totalLabel} (100%). Extracting ffmpeg.exe...";

            string extractDir = Path.Combine(tempDir, "extracted");
            ZipFile.ExtractToDirectory(archivePath, extractDir, overwriteFiles: true);

            string? extractedExe = Directory
                .GetFiles(extractDir, "ffmpeg.exe", SearchOption.AllDirectories)
                .FirstOrDefault();

            if (string.IsNullOrEmpty(extractedExe))
            {
                throw new FileNotFoundException("ffmpeg.exe was not found inside the downloaded archive.");
            }

            File.Copy(extractedExe, FfmpegExePath, overwrite: true);

            SetUiBusy(false, $"Ready ({FormatBytes(finalDownloadedBytes)} downloaded; ffmpeg.exe installed).", showProgress: false);
            MessageBox.Show(
                this,
                "ffmpeg.exe has been downloaded and placed in the application folder.",
                "Download Complete",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            SetUiBusy(false, "FFmpeg download failed.", showProgress: false);
            MessageBox.Show(
                this,
                $"Could not download or extract FFmpeg.\n\n{ex.Message}",
                "Download Error",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, recursive: true);
                }
            }
            catch
            {
                // Ignore temp cleanup issues
            }

            UpdateConvertButtonState();
        }
    }

    private static async Task<RangeProbeResult> ProbeRangeSupportAsync(string url)
    {
        try
        {
            using var rangeRequest = new HttpRequestMessage(HttpMethod.Get, url);
            rangeRequest.Headers.Range = new RangeHeaderValue(0, 0);

            using var rangeResponse = await SharedHttpClient.SendAsync(
                rangeRequest,
                HttpCompletionOption.ResponseHeadersRead);

            if (rangeResponse.StatusCode == HttpStatusCode.PartialContent)
            {
                long? totalFromContentRange = rangeResponse.Content.Headers.ContentRange?.Length;
                long? totalLen = totalFromContentRange;

                byte[]? firstByte = null;
                byte[] rawBytes = await rangeResponse.Content.ReadAsByteArrayAsync();
                if (rawBytes.Length == 1)
                {
                    firstByte = rawBytes;
                }

                if (totalLen is > 0)
                {
                    return new RangeProbeResult(true, totalLen.Value, firstByte);
                }
            }

            // If server returned 200 OK instead of 206, try to still read Content-Length for progress display
            long? contentLength = rangeResponse.Content.Headers.ContentLength;
            if (contentLength is null or <= 0)
            {
                using var headRequest = new HttpRequestMessage(HttpMethod.Head, url);
                using var headResponse = await SharedHttpClient.SendAsync(
                    headRequest,
                    HttpCompletionOption.ResponseHeadersRead);
                contentLength = headResponse.Content.Headers.ContentLength;
            }

            return new RangeProbeResult(false, contentLength is > 0 ? contentLength : null, null);
        }
        catch
        {
            return new RangeProbeResult(false, null, null);
        }
    }

    private async Task<long> DownloadParallelRangesAsync(
        string url,
        string destinationFilePath,
        long totalBytes,
        byte[]? initialFirstByte)
    {
        // Pre-allocate target file so parallel workers can write to non-overlapping offsets safely
        await using (var preallocStream = new FileStream(
            destinationFilePath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.ReadWrite,
            bufferSize: 4096,
            useAsync: true))
        {
            preallocStream.SetLength(totalBytes);
        }

        long downloadedBytes = 0;

        // If we already fetched byte 0 during the probe, write it now
        if (initialFirstByte is { Length: 1 })
        {
            using var handle = File.OpenHandle(destinationFilePath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            await RandomAccess.WriteAsync(handle, initialFirstByte, 0);
            Interlocked.Add(ref downloadedBytes, 1);
        }

        long startOffset = initialFirstByte is { Length: 1 } ? 1 : 0;
        long remainingToFetch = totalBytes - startOffset;
        int effectiveChunks = (int)Math.Min(ParallelChunkCount, Math.Max(1, remainingToFetch));
        long baseChunkSize = remainingToFetch / effectiveChunks;
        long remainder = remainingToFetch % effectiveChunks;

        var ranges = new List<(long Start, long End)>(effectiveChunks);
        long cursor = startOffset;

        for (int i = 0; i < effectiveChunks; i++)
        {
            long size = baseChunkSize + (i < remainder ? 1 : 0);
            if (size <= 0) continue;

            long end = cursor + size - 1;
            ranges.Add((cursor, end));
            cursor = end + 1;
        }

        using var progressCts = new CancellationTokenSource();
        Task progressTask = MonitorParallelProgressAsync(
            () => Interlocked.Read(ref downloadedBytes),
            totalBytes,
            progressCts.Token);

        try
        {
            var chunkTasks = ranges.Select(range => DownloadSingleRangeAsync(
                url,
                destinationFilePath,
                range.Start,
                range.End,
                bytes => Interlocked.Add(ref downloadedBytes, bytes)));

            await Task.WhenAll(chunkTasks);
        }
        finally
        {
            await progressCts.CancelAsync();
            try
            {
                await progressTask;
            }
            catch (OperationCanceledException)
            {
                // Expected on cancellation
            }
        }

        long finalBytes = Interlocked.Read(ref downloadedBytes);
        UpdateDownloadProgressUi(finalBytes, totalBytes, 0);
        return finalBytes;
    }

    private static async Task DownloadSingleRangeAsync(
        string url,
        string destinationFilePath,
        long startByte,
        long endByte,
        Action<long> onBytesRead)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new RangeHeaderValue(startByte, endByte);

        using var response = await SharedHttpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead);

        if (response.StatusCode != HttpStatusCode.PartialContent)
        {
            throw new HttpRequestException(
                $"Server did not honor byte range {startByte}-{endByte} (status {(int)response.StatusCode} {response.ReasonPhrase}).");
        }

        await using Stream contentStream = await response.Content.ReadAsStreamAsync();
        using var fileHandle = File.OpenHandle(destinationFilePath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);

        byte[] buffer = new byte[StreamBufferSize];
        long currentFileOffset = startByte;

        while (true)
        {
            int bytesRead = await contentStream.ReadAsync(buffer.AsMemory(0, buffer.Length));
            if (bytesRead <= 0)
            {
                break;
            }

            await RandomAccess.WriteAsync(fileHandle, buffer.AsMemory(0, bytesRead), currentFileOffset);
            currentFileOffset += bytesRead;
            onBytesRead(bytesRead);
        }
    }

    private async Task MonitorParallelProgressAsync(
        Func<long> getDownloadedBytes,
        long totalBytes,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        long previousBytes = getDownloadedBytes();
        double previousElapsedSec = 0;
        double smoothedBytesPerSec = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            long currentBytes = getDownloadedBytes();
            double elapsedSec = Math.Max(stopwatch.Elapsed.TotalSeconds, 0.001);
            double deltaSec = Math.Max(elapsedSec - previousElapsedSec, 0.05);
            double instantSpeed = Math.Max(0, (currentBytes - previousBytes) / deltaSec);

            if (instantSpeed > 0)
            {
                smoothedBytesPerSec = smoothedBytesPerSec == 0
                    ? instantSpeed
                    : (smoothedBytesPerSec * 0.7) + (instantSpeed * 0.3);
            }

            previousBytes = currentBytes;
            previousElapsedSec = elapsedSec;

            UpdateDownloadProgressUi(currentBytes, totalBytes, smoothedBytesPerSec);

            await Task.Delay(250, cancellationToken);
        }
    }

    private async Task<(int ExitCode, string StandardError)> RunCurlWithByteProgressAsync(
        string arguments,
        string destinationFilePath,
        long? totalBytes)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "curl.exe",
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };

        using var process = new Process { StartInfo = psi };
        var stdErrBuilder = new StringBuilder();

        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                stdErrBuilder.AppendLine(e.Data);
            }
        };

        process.Start();
        process.BeginErrorReadLine();
        Task readStdOutTask = process.StandardOutput.ReadToEndAsync();

        var stopwatch = Stopwatch.StartNew();
        long previousBytes = 0;
        double previousElapsedSec = 0;
        double smoothedBytesPerSec = 0;

        while (!process.HasExited)
        {
            long currentBytes = GetFileLengthSafe(destinationFilePath);
            double elapsedSec = Math.Max(stopwatch.Elapsed.TotalSeconds, 0.001);
            double deltaSec = Math.Max(elapsedSec - previousElapsedSec, 0.05);
            double instantSpeed = Math.Max(0, (currentBytes - previousBytes) / deltaSec);

            if (instantSpeed > 0)
            {
                smoothedBytesPerSec = smoothedBytesPerSec == 0
                    ? instantSpeed
                    : (smoothedBytesPerSec * 0.7) + (instantSpeed * 0.3);
            }

            previousBytes = currentBytes;
            previousElapsedSec = elapsedSec;

            UpdateDownloadProgressUi(currentBytes, totalBytes, smoothedBytesPerSec);

            await Task.Delay(250);
        }

        await Task.WhenAll(process.WaitForExitAsync(), readStdOutTask);

        long finalBytes = GetFileLengthSafe(destinationFilePath);
        UpdateDownloadProgressUi(finalBytes, totalBytes, smoothedBytesPerSec);

        return (process.ExitCode, stdErrBuilder.ToString());
    }

    private void UpdateDownloadProgressUi(long downloadedBytes, long? totalBytes, double bytesPerSec)
    {
        if (IsDisposed) return;

        void Apply()
        {
            if (totalBytes is > 0)
            {
                double rawPercent = (downloadedBytes * 100.0) / totalBytes.Value;
                int clampedPercent = Math.Clamp((int)Math.Round(rawPercent), 0, 100);

                _progressBar.Style = ProgressBarStyle.Continuous;
                _progressBar.Value = clampedPercent;

                string etaText = bytesPerSec > 1024 && downloadedBytes < totalBytes.Value
                    ? FormatDuration(TimeSpan.FromSeconds((totalBytes.Value - downloadedBytes) / bytesPerSec))
                    : (downloadedBytes >= totalBytes.Value ? "00:00" : "Calculating...");

                string speedText = bytesPerSec > 0 ? $"{FormatBytes((long)bytesPerSec)}/s" : "--";

                _lblStatus.Text =
                    $"Downloaded: {FormatBytes(downloadedBytes)} / {FormatBytes(totalBytes.Value)} ({clampedPercent}%) " +
                    $"• Speed: {speedText} • ETA: {etaText}";
            }
            else
            {
                _progressBar.Style = ProgressBarStyle.Marquee;
                string speedText = bytesPerSec > 0 ? $" • Speed: {FormatBytes((long)bytesPerSec)}/s" : string.Empty;
                _lblStatus.Text = $"Downloaded: {FormatBytes(downloadedBytes)} (Total size unknown){speedText} • ETA: --";
            }
        }

        if (InvokeRequired)
        {
            BeginInvoke(Apply);
        }
        else
        {
            Apply();
        }
    }

    private static long GetFileLengthSafe(string path)
    {
        try
        {
            if (!File.Exists(path)) return 0;
            return new FileInfo(path).Length;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    private static string FormatBytes(long bytes)
    {
        string[] suffixes = ["B", "KB", "MB", "GB", "TB"];
        double value = Math.Max(0, bytes);
        int order = 0;

        while (value >= 1024 && order < suffixes.Length - 1)
        {
            order++;
            value /= 1024;
        }

        return order == 0
            ? $"{value:0} {suffixes[order]}"
            : $"{value.ToString("0.0", CultureInfo.InvariantCulture)} {suffixes[order]}";
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalHours >= 1)
        {
            return $"{(int)duration.TotalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}";
        }

        return $"{duration.Minutes:00}:{duration.Seconds:00}";
    }

    private void BuildControls()
    {
        var lblSource = new Label { Text = "Source MP4:", Location = new Point(16, 22), AutoSize = true };
        _txtSource.SetBounds(125, 19, 310, 25);
        _txtSource.ReadOnly = true;

        _btnBrowseSource.Text = "Browse...";
        _btnBrowseSource.SetBounds(445, 18, 85, 27);
        _btnBrowseSource.Click += BtnBrowseSource_Click;

        var lblDest = new Label { Text = "Destination GIF:", Location = new Point(16, 62), AutoSize = true };
        _txtDestination.SetBounds(125, 59, 310, 25);

        _btnBrowseDest.Text = "Save As...";
        _btnBrowseDest.SetBounds(445, 58, 85, 27);
        _btnBrowseDest.Click += BtnBrowseDest_Click;

        var lblQuality = new Label { Text = "Image Quality:", Location = new Point(16, 102), AutoSize = true };
        _cmbQuality.SetBounds(125, 99, 220, 25);
        _cmbQuality.DropDownStyle = ComboBoxStyle.DropDownList;

        _btnConvert.Text = "Convert to GIF";
        _btnConvert.SetBounds(380, 98, 150, 30);
        _btnConvert.Click += BtnConvert_Click;

        _progressBar.SetBounds(16, 145, 514, 22);
        _progressBar.Minimum = 0;
        _progressBar.Maximum = 100;
        _progressBar.Value = 0;
        _progressBar.Visible = false;

        _lblStatus.Text = "Checking for ffmpeg.exe...";
        _lblStatus.Location = new Point(16, 176);
        _lblStatus.Size = new Size(515, 55);

        Controls.AddRange(
        [
            lblSource, _txtSource, _btnBrowseSource,
            lblDest, _txtDestination, _btnBrowseDest,
            lblQuality, _cmbQuality, _btnConvert,
            _progressBar, _lblStatus
        ]);
    }

    private void BtnBrowseSource_Click(object? sender, EventArgs e)
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Select an MP4 Video",
            Filter = "MP4 Video (*.mp4)|*.mp4|All Video Files (*.mp4;*.mkv;*.mov;*.avi)|*.mp4;*.mkv;*.mov;*.avi"
        };

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _txtSource.Text = dialog.FileName;

            if (string.IsNullOrWhiteSpace(_txtDestination.Text))
            {
                _txtDestination.Text = Path.ChangeExtension(dialog.FileName, ".gif");
            }
        }
    }

    private void BtnBrowseDest_Click(object? sender, EventArgs e)
    {
        using var dialog = new SaveFileDialog
        {
            Title = "Save GIF As",
            Filter = "GIF Image (*.gif)|*.gif",
            DefaultExt = "gif",
            FileName = string.IsNullOrWhiteSpace(_txtSource.Text)
                ? "output.gif"
                : Path.GetFileNameWithoutExtension(_txtSource.Text) + ".gif"
        };

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _txtDestination.Text = dialog.FileName;
        }
    }

    private async void BtnConvert_Click(object? sender, EventArgs e)
    {
        if (!File.Exists(FfmpegExePath))
        {
            MessageBox.Show(this, "ffmpeg.exe is missing from the application folder.", "Missing FFmpeg",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        string inputPath = _txtSource.Text.Trim();
        string outputPath = _txtDestination.Text.Trim();

        if (string.IsNullOrWhiteSpace(inputPath) || !File.Exists(inputPath))
        {
            MessageBox.Show(this, "Please select a valid source MP4 file.", "Missing Source",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (string.IsNullOrWhiteSpace(outputPath))
        {
            MessageBox.Show(this, "Please select a destination GIF path.", "Missing Destination",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (!outputPath.EndsWith(".gif", StringComparison.OrdinalIgnoreCase))
        {
            outputPath += ".gif";
            _txtDestination.Text = outputPath;
        }

        string filterComplex = BuildFilterGraph(_cmbQuality.SelectedIndex);
        string arguments = $"-y -i \"{inputPath}\" -vf \"{filterComplex}\" \"{outputPath}\"";

        SetUiBusy(true, "Converting video to GIF with FFmpeg...", showProgress: false);

        try
        {
            var result = await RunProcessAsync(FfmpegExePath, arguments);

            if (result.ExitCode == 0)
            {
                SetUiBusy(false, $"Done! Saved to: {outputPath}", showProgress: false);
                MessageBox.Show(this, "GIF conversion completed successfully!", "Success",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                SetUiBusy(false, "Conversion failed.", showProgress: false);
                string errorTail = GetLastLines(result.StandardError, 6);
                MessageBox.Show(this, $"FFmpeg exited with code {result.ExitCode}.\n\n{errorTail}",
                    "FFmpeg Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        catch (Exception ex)
        {
            SetUiBusy(false, "Error running FFmpeg.", showProgress: false);
            MessageBox.Show(this, $"Failed to run ffmpeg.exe.\n\nDetails: {ex.Message}",
                "Execution Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            UpdateConvertButtonState();
        }
    }

    private static string BuildFilterGraph(int qualityIndex) => qualityIndex switch
    {
        0 => "fps=10,scale=480:-1:flags=lanczos,split[s0][s1];[s0]palettegen=max_colors=128:stats_mode=diff[p];[s1][p]paletteuse=dither=bayer:bayer_scale=3",
        2 => "fps=15,scale=800:-1:flags=lanczos,split[s0][s1];[s0]palettegen=stats_mode=full[p];[s1][p]paletteuse=dither=sierra2_4a",
        3 => "fps=4,scale=1080:-1:flags=lanczos,split[s0][s1];[s0]palettegen=stats_mode=full[p];[s1][p]paletteuse=dither=sierra2_4a",
        4 => "fps=15,scale=1080:-1:flags=lanczos,split[s0][s1];[s0]palettegen=stats_mode=full[p];[s1][p]paletteuse=dither=sierra2_4a",
        _ => "fps=12,scale=640:-1:flags=lanczos,split[s0][s1];[s0]palettegen[p];[s1][p]paletteuse"
    };

    private static async Task<(int ExitCode, string StandardError)> RunProcessAsync(string fileName, string arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };

        using var process = new Process { StartInfo = psi };
        var stdErrBuilder = new StringBuilder();

        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                stdErrBuilder.AppendLine(e.Data);
            }
        };

        process.Start();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync();

        return (process.ExitCode, stdErrBuilder.ToString());
    }

    private void SetUiBusy(bool busy, string statusText, bool showProgress, bool marquee = false, int progressValue = 0)
    {
        _btnConvert.Enabled = !busy && File.Exists(FfmpegExePath);
        _btnBrowseSource.Enabled = !busy;
        _btnBrowseDest.Enabled = !busy;
        _cmbQuality.Enabled = !busy;
        _progressBar.Visible = showProgress;

        if (showProgress)
        {
            _progressBar.Style = marquee ? ProgressBarStyle.Marquee : ProgressBarStyle.Continuous;
            if (!marquee)
            {
                _progressBar.Value = Math.Clamp(progressValue, 0, 100);
            }
        }

        _lblStatus.Text = statusText;
    }

    private void UpdateConvertButtonState()
    {
        _btnConvert.Enabled = File.Exists(FfmpegExePath);
    }

    private static string GetLastLines(string text, int lineCount)
    {
        if (string.IsNullOrWhiteSpace(text)) return "No error output captured.";
        var lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        return string.Join(Environment.NewLine, lines.TakeLast(lineCount));
    }
}
//using System.Diagnostics;
//using System.IO.Compression;
//using System.Text;

//namespace Mp4ToGifWinForms;

//public partial class Form1 : Form
//{
//    private const string FfmpegDownloadUrl = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";
//    private string FfmpegExePath => Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
//    private readonly TextBox _txtSource = new();
//    private readonly TextBox _txtDestination = new();
//    private readonly ComboBox _cmbQuality = new();
//    private readonly Button _btnBrowseSource = new();
//    private readonly Button _btnBrowseDest = new();
//    private readonly Button _btnConvert = new();
//    private readonly Label _lblStatus = new();

//    public Form1()
//    {
//        InitializeComponent();
//        Text = "MP4 to GIF Converter (FFmpeg)";
//        Size = new Size(560, 250);
//        StartPosition = FormStartPosition.CenterScreen;
//        FormBorderStyle = FormBorderStyle.Fixed3D;
//        MaximizeBox = false;

//        BuildControls();
//        Load += (_, _) =>
//        {
//            _cmbQuality.Items.AddRange(
//            [
//                "Low (Small File)",
//                "Medium (Balanced)",
//                "High (Sharp & Smooth)",
//                "Highest (Maximum Quality)"
//            ]);
//            _cmbQuality.SelectedIndex = 1; // Default: Medium (matches your command)
//        };
//        Shown += Form1_Shown;
//    }
//    private async void Form1_Shown(object? sender, EventArgs e)
//    {
//        if (File.Exists(FfmpegExePath))
//        {
//            _lblStatus.Text = "Ready (ffmpeg.exe found in app folder).";
//            UpdateConvertButtonState();
//            return;
//        }

//        UpdateConvertButtonState();

//        DialogResult choice = MessageBox.Show(
//            this,
//            "ffmpeg.exe was not found in the application folder.\n\nWould you like to download FFmpeg now using curl?",
//            "FFmpeg Required",
//            MessageBoxButtons.YesNo,
//            MessageBoxIcon.Question);

//        if (choice != DialogResult.Yes)
//        {
//            _lblStatus.Text = "ffmpeg.exe missing. Place ffmpeg.exe in the app folder to convert videos.";
//            return;
//        }

//        await DownloadAndInstallFfmpegAsync();
//    }
//    private void BuildControls()
//    {
//        // Source MP4
//        var lblSource = new Label { Text = "Source MP4:", Location = new Point(16, 22), AutoSize = true };
//        _txtSource.SetBounds(125, 19, 310, 25);
//        _txtSource.ReadOnly = true;

//        _btnBrowseSource.Text = "Browse...";
//        _btnBrowseSource.SetBounds(445, 18, 85, 27);
//        _btnBrowseSource.Click += BtnBrowseSource_Click;

//        // Destination GIF
//        var lblDest = new Label { Text = "Destination GIF:", Location = new Point(16, 62), AutoSize = true };
//        _txtDestination.SetBounds(125, 59, 310, 25);

//        _btnBrowseDest.Text = "Save As...";
//        _btnBrowseDest.SetBounds(445, 58, 85, 27);
//        _btnBrowseDest.Click += BtnBrowseDest_Click;

//        // Image Quality
//        var lblQuality = new Label { Text = "Image Quality:", Location = new Point(16, 102), AutoSize = true };
//        _cmbQuality.SetBounds(125, 99, 220, 25);
//        _cmbQuality.DropDownStyle = ComboBoxStyle.DropDownList;

//        // Convert Button
//        _btnConvert.Text = "Convert to GIF";
//        _btnConvert.SetBounds(380, 98, 150, 30);
//        _btnConvert.Click += BtnConvert_Click;

//        // Status Label
//        _lblStatus.Text = "Ready.";
//        _lblStatus.Location = new Point(16, 150);
//        _lblStatus.Size = new Size(515, 45);

//        Controls.AddRange(
//        [
//            lblSource, _txtSource, _btnBrowseSource,
//            lblDest, _txtDestination, _btnBrowseDest,
//            lblQuality, _cmbQuality, _btnConvert,
//            _lblStatus
//        ]);
//    }

//    private void BtnBrowseSource_Click(object? sender, EventArgs e)
//    {
//        using var dialog = new OpenFileDialog
//        {
//            Title = "Select an MP4 Video",
//            Filter = "MP4 Video (*.mp4)|*.mp4|All Video Files (*.mp4;*.mkv;*.mov;*.avi)|*.mp4;*.mkv;*.mov;*.avi"
//        };

//        if (dialog.ShowDialog(this) == DialogResult.OK)
//        {
//            _txtSource.Text = dialog.FileName;

//            // Auto-suggest destination GIF path if empty
//            if (string.IsNullOrWhiteSpace(_txtDestination.Text))
//            {
//                _txtDestination.Text = Path.ChangeExtension(dialog.FileName, ".gif");
//            }
//        }
//    }

//    private void BtnBrowseDest_Click(object? sender, EventArgs e)
//    {
//        using var dialog = new SaveFileDialog
//        {
//            Title = "Save GIF As",
//            Filter = "GIF Image (*.gif)|*.gif",
//            DefaultExt = "gif",
//            FileName = string.IsNullOrWhiteSpace(_txtSource.Text)
//                ? "output.gif"
//                : Path.GetFileNameWithoutExtension(_txtSource.Text) + ".gif"
//        };

//        if (dialog.ShowDialog(this) == DialogResult.OK)
//        {
//            _txtDestination.Text = dialog.FileName;
//        }
//    }

//    private async void BtnConvert_Click(object? sender, EventArgs e)
//    {
//        string inputPath = _txtSource.Text.Trim();
//        string outputPath = _txtDestination.Text.Trim();

//        if (string.IsNullOrWhiteSpace(inputPath) || !File.Exists(inputPath))
//        {
//            MessageBox.Show(this, "Please select a valid source MP4 file.", "Missing Source",
//                MessageBoxButtons.OK, MessageBoxIcon.Warning);
//            return;
//        }

//        if (string.IsNullOrWhiteSpace(outputPath))
//        {
//            MessageBox.Show(this, "Please select a destination GIF path.", "Missing Destination",
//                MessageBoxButtons.OK, MessageBoxIcon.Warning);
//            return;
//        }

//        if (!outputPath.EndsWith(".gif", StringComparison.OrdinalIgnoreCase))
//        {
//            outputPath += ".gif";
//            _txtDestination.Text = outputPath;
//        }

//        string filterComplex = BuildFilterGraph(_cmbQuality.SelectedIndex);
//        string arguments = $"-y -i \"{inputPath}\" -vf \"{filterComplex}\" \"{outputPath}\"";

//        SetUiBusy(true, "Converting video to GIF with FFmpeg...");

//        try
//        {
//            //var result = await RunFfmpegAsync(arguments);
//            var result = await RunProcessAsync(FfmpegExePath, arguments);
//            if (result.ExitCode == 0)
//            {
//                SetUiBusy(false, $"Done! Saved to: {outputPath}");
//                MessageBox.Show(this, "GIF conversion completed successfully!", "Success",
//                    MessageBoxButtons.OK, MessageBoxIcon.Information);
//            }
//            else
//            {
//                SetUiBusy(false, "Conversion failed.");
//                string errorTail = GetLastLines(result.StandardError, 6);
//                MessageBox.Show(this, $"FFmpeg exited with code {result.ExitCode}.\n\n{errorTail}",
//                    "FFmpeg Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
//            }
//        }
//        catch (Exception ex)
//        {
//            SetUiBusy(false, "Error running FFmpeg.");
//            MessageBox.Show(this,
//                $"Could not start ffmpeg.exe. Make sure FFmpeg is installed and in PATH, or placed next to the app.\n\nDetails: {ex.Message}",
//                "Execution Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
//        }
//    }

//    private static string BuildFilterGraph(int qualityIndex) => qualityIndex switch
//    {
//        // Low: smaller dimensions, lower FPS, smaller palette
//        0 => "fps=10,scale=480:-1:flags=lanczos,split[s0][s1];[s0]palettegen=max_colors=128:stats_mode=diff[p];[s1][p]paletteuse=dither=bayer:bayer_scale=3",

//        // High: higher FPS, larger width, smoother dithering
//        2 => "fps=15,scale=800:-1:flags=lanczos,split[s0][s1];[s0]palettegen=stats_mode=full[p];[s1][p]paletteuse=dither=sierra2_4a",

//        3 => "fps=4,scale=1400:-1:flags=lanczos,split[s0][s1];[s0]palettegen=stats_mode=full[p];[s1][p]paletteuse=dither=sierra2_4a",

//        // Medium (Default): matches your original command
//        _ => "fps=12,scale=640:-1:flags=lanczos,split[s0][s1];[s0]palettegen[p];[s1][p]paletteuse"
//    };

//    private static async Task<(int ExitCode, string StandardError)> RunFfmpegAsync(string arguments)
//    {
//        string ffmpegPath = ResolveFfmpegPath();

//        var psi = new ProcessStartInfo
//        {
//            FileName = ffmpegPath,
//            Arguments = arguments,
//            UseShellExecute = false,
//            CreateNoWindow = true,
//            RedirectStandardError = true,
//            RedirectStandardOutput = true
//        };

//        using var process = new Process { StartInfo = psi };
//        var stdErrBuilder = new StringBuilder();

//        process.ErrorDataReceived += (_, e) =>
//        {
//            if (!string.IsNullOrEmpty(e.Data))
//            {
//                stdErrBuilder.AppendLine(e.Data);
//            }
//        };

//        process.Start();
//        process.BeginErrorReadLine();
//        await process.WaitForExitAsync();

//        return (process.ExitCode, stdErrBuilder.ToString());
//    }

//    private static string ResolveFfmpegPath()
//    {
//        string localFfmpeg = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
//        return File.Exists(localFfmpeg) ? localFfmpeg : "ffmpeg";
//    }

//    private void SetUiBusy(bool busy, string statusText)
//    {
//        _btnConvert.Enabled = !busy;
//        _btnBrowseSource.Enabled = !busy;
//        _btnBrowseDest.Enabled = !busy;
//        _cmbQuality.Enabled = !busy;
//        _lblStatus.Text = statusText;
//    }

//    private static string GetLastLines(string text, int lineCount)
//    {
//        if (string.IsNullOrWhiteSpace(text)) return "No error output captured.";
//        var lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
//        return string.Join(Environment.NewLine, lines.TakeLast(lineCount));
//    }

//    private async Task DownloadAndInstallFfmpegAsync()
//    {
//        SetUiBusy(true, "Downloading FFmpeg via curl...");

//        string tempDir = Path.Combine(Path.GetTempPath(), "Mp4ToGif_FFmpeg_" + Guid.NewGuid().ToString("N"));
//        string archivePath = Path.Combine(tempDir, "ffmpeg_download.zip");
//        Directory.CreateDirectory(tempDir);

//        try
//        {
//            string curlArgs = $"-L --fail --silent --show-error \"{FfmpegDownloadUrl}\" -o \"{archivePath}\"";
//            var curlResult = await RunProcessAsync("curl.exe", curlArgs);

//            if (curlResult.ExitCode != 0)
//            {
//                throw new InvalidOperationException(
//                    $"curl exited with code {curlResult.ExitCode}: {GetLastLines(curlResult.StandardError, 5)}");
//            }

//            _lblStatus.Text = "Extracting ffmpeg.exe...";

//            string extractDir = Path.Combine(tempDir, "extracted");
//            ZipFile.ExtractToDirectory(archivePath, extractDir, overwriteFiles: true);

//            string? extractedExe = Directory
//                .GetFiles(extractDir, "ffmpeg.exe", SearchOption.AllDirectories)
//                .FirstOrDefault();

//            if (string.IsNullOrEmpty(extractedExe))
//            {
//                throw new FileNotFoundException("ffmpeg.exe was not found inside the downloaded archive.");
//            }

//            File.Copy(extractedExe, FfmpegExePath, overwrite: true);

//            SetUiBusy(false, "Ready (ffmpeg.exe downloaded and installed).");
//            MessageBox.Show(
//                this,
//                "ffmpeg.exe has been downloaded and placed in the application folder.",
//                "Download Complete",
//                MessageBoxButtons.OK,
//                MessageBoxIcon.Information);
//        }
//        catch (Exception ex)
//        {
//            SetUiBusy(false, "FFmpeg download failed.");
//            MessageBox.Show(
//                this,
//                $"Could not download or extract FFmpeg.\n\n{ex.Message}",
//                "Download Error",
//                MessageBoxButtons.OK,
//                MessageBoxIcon.Error);
//        }
//        finally
//        {
//            try
//            {
//                if (Directory.Exists(tempDir))
//                {
//                    Directory.Delete(tempDir, recursive: true);
//                }
//            }
//            catch
//            {
//                // Ignore temp cleanup issues
//            }

//            UpdateConvertButtonState();
//        }
//    }
//    private static async Task<(int ExitCode, string StandardError)> RunProcessAsync(string fileName, string arguments)
//    {
//        var psi = new ProcessStartInfo
//        {
//            FileName = fileName,
//            Arguments = arguments,
//            UseShellExecute = false,
//            CreateNoWindow = true,
//            RedirectStandardError = true,
//            RedirectStandardOutput = true
//        };

//        using var process = new Process { StartInfo = psi };
//        var stdErrBuilder = new StringBuilder();

//        process.ErrorDataReceived += (_, e) =>
//        {
//            if (!string.IsNullOrEmpty(e.Data))
//            {
//                stdErrBuilder.AppendLine(e.Data);
//            }
//        };

//        process.Start();
//        process.BeginErrorReadLine();
//        await process.WaitForExitAsync();

//        return (process.ExitCode, stdErrBuilder.ToString());
//    }
//    private void UpdateConvertButtonState()
//    {
//        _btnConvert.Enabled = File.Exists(FfmpegExePath);
//    }
//}