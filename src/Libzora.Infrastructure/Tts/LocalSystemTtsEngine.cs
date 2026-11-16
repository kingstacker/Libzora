using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Libzora.Core;

namespace Libzora.Infrastructure;

/// <summary>Offline synthesis using installed macOS voices or Windows SAPI. Never falls back to a network service.</summary>
public sealed class LocalSystemTtsEngine : ITtsEngine
{
    public const string ProviderId = "local-system-tts";
    private bool _disposed;
    public string Id => ProviderId;
    public bool IsAvailable => !_disposed && (OperatingSystem.IsMacOS() || OperatingSystem.IsWindows());

    // Static script only: book text, paths and voice identifiers arrive as JSON on stdin.
    private const string WindowsScript = """
        $ErrorActionPreference = 'Stop'
        [Console]::InputEncoding = New-Object System.Text.UTF8Encoding($false)
        [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
        $request = [Console]::In.ReadToEnd() | ConvertFrom-Json
        $speaker = New-Object -ComObject SAPI.SpVoice
        $stream = $null
        try {
            if ($request.List) {
                $voices = @($speaker.GetVoices() | ForEach-Object {
                    $culture = ''
                    try { $culture = [Globalization.CultureInfo]::GetCultureInfo([Convert]::ToInt32(($_.GetAttribute('Language') -split ';')[0], 16)).Name } catch {}
                    @{ Id = $_.Id; Name = $_.GetDescription(); Culture = $culture }
                })
                ConvertTo-Json -InputObject $voices -Compress
            } else {
                $voice = @($speaker.GetVoices() | Where-Object { $_.Id -eq $request.Voice })
                if ($voice.Count -ne 1) { throw 'The selected local voice is no longer installed.' }
                $speaker.Voice = $voice[0]
                $speaker.Rate = [int]$request.Rate
                $speaker.Volume = [int]$request.Volume
                $stream = New-Object -ComObject SAPI.SpFileStream
                $stream.Open($request.Path, 3, $false)
                $speaker.AudioOutputStream = $stream
                # SVSFIsNotXML: document content must never be interpreted as SAPI markup.
                $null = $speaker.Speak($request.Text, 16)
                $stream.Close()
            }
        } finally {
            if ($null -ne $stream) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($stream) }
            [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($speaker)
        }
        """;

    public async Task<TtsAvailability> CheckAvailabilityAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var voices = await GetVoicesAsync(cancellationToken).ConfigureAwait(false);
            return new(voices.Count > 0, voices.Count > 0
                ? "系统本地语音已就绪；朗读无需联网。"
                : "未找到已安装的本地语音，请在系统设置中安装所需语言的语音。");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) { return new(false, $"无法使用系统本地语音：{exception.Message}"); }
    }

    public async Task<IReadOnlyList<TtsVoiceInfo>> GetVoicesAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsAvailable) return [];
        if (OperatingSystem.IsWindows())
        {
            var json = await RunWindowsAsync(new { List = true }, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<TtsVoiceInfo[]>(json) ?? [];
        }
        var output = await RunAsync("/usr/bin/say", ["-v", "?"], "", cancellationToken).ConfigureAwait(false);
        return ParseMacVoices(output);
    }

    internal static IReadOnlyList<TtsVoiceInfo> ParseMacVoices(string output)
        => output.Split('\n').Select(line => Regex.Match(line, @"^(.+?)\s+([a-z]{2,3}_[A-Z]{2})\s+#"))
            .Where(match => match.Success)
            .Select(match => new TtsVoiceInfo(match.Groups[1].Value.Trim(), match.Groups[1].Value.Trim(),
                match.Groups[2].Value.Replace('_', '-'))).ToArray();

    internal static TtsVoiceInfo? SelectVoice(IReadOnlyList<TtsVoiceInfo> voices, string preferred)
        => voices.FirstOrDefault(voice => voice.Id.Equals(preferred, StringComparison.OrdinalIgnoreCase))
            ?? voices.FirstOrDefault(voice => voice.Culture?.Equals(
                preferred.StartsWith("en-", StringComparison.OrdinalIgnoreCase) ? "en-US" : "zh-CN",
                StringComparison.OrdinalIgnoreCase) == true)
            ?? voices.FirstOrDefault();

    public async Task<TtsResult> SynthesizeAsync(string text, TtsOptions options, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        var path = Path.Combine(Path.GetTempPath(), "libzora-local-tts", Guid.NewGuid().ToString("N") + ".wav");
        var success = false;
        try
        {
            var voices = await GetVoicesAsync(cancellationToken).ConfigureAwait(false);
            var voice = SelectVoice(voices, options.Voice);
            if (voice is null) return TtsResult.Failure("未找到本地语音，请先在系统设置中安装语音。");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (OperatingSystem.IsWindows())
            {
                await RunWindowsAsync(new
                {
                    List = false, Text = text, Voice = voice.Id, Path = path,
                    Rate = Math.Clamp((int)Math.Round(10 * Math.Log2(1 + Math.Clamp(options.RatePercent, -50, 100) / 100.0)), -10, 10),
                    Volume = Math.Clamp(options.VolumePercent + 100, 0, 100)
                }, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                // PCM WAV keeps the shared cache and both platform players on the same format.
                var rate = (int)Math.Round(180 * (1 + Math.Clamp(options.RatePercent, -50, 100) / 100.0));
                var volume = (Math.Clamp(options.VolumePercent + 100, 0, 100) / 100.0).ToString(CultureInfo.InvariantCulture);
                await RunAsync("/usr/bin/say", ["-v", voice.Id, "-r", rate.ToString(CultureInfo.InvariantCulture),
                    "-o", path, "--file-format=WAVE", "--data-format=LEI16@22050", "-f", "-"],
                    $"[[volm {volume}]]" + text.Replace("[[", "［［", StringComparison.Ordinal), cancellationToken).ConfigureAwait(false);
            }
            if (!File.Exists(path) || new FileInfo(path).Length <= 44)
                return TtsResult.Failure("系统本地语音没有生成有效音频。");
            success = true;
            return TtsResult.Success(path, "wav");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) { return TtsResult.Failure($"本地语音合成失败：{exception.Message}"); }
        finally
        {
            if (!success) { try { File.Delete(path); } catch (IOException) { } }
        }
    }

    private static Task<string> RunWindowsAsync(object request, CancellationToken cancellationToken)
    {
        var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");
        return RunAsync(executable, ["-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand",
            Convert.ToBase64String(Encoding.Unicode.GetBytes(WindowsScript))], JsonSerializer.Serialize(request), cancellationToken);
    }

    private static async Task<string> RunAsync(string executable, string[] arguments, string input, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动系统语音组件。");
        using var registration = timeout.Token.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
        });
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try
        {
            await process.StandardInput.WriteAsync(input.AsMemory(), timeout.Token).ConfigureAwait(false);
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            var result = await output.ConfigureAwait(false);
            var errorText = await error.ConfigureAwait(false);
            timeout.Token.ThrowIfCancellationRequested();
            if (process.ExitCode != 0) throw new InvalidOperationException($"系统语音组件执行失败：{errorText.Trim()}");
            return result;
        }
        catch when (timeout.IsCancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException("系统本地语音处理超时。");
        }
    }

    public void Dispose() => _disposed = true;
}
