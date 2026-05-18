using System.Diagnostics;
using HttpClient = System.Net.Http.HttpClient;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Logging;

namespace VocabSpire.Services;

/// <summary>
/// 文本转语音服务 —— 在线 API 优先，回退系统 TTS。
///
/// 优先级：有道词典 → Google Translate TTS → 系统 TTS (Windows SAPI / macOS say)
/// 音频缓存在内存中，同一单词只请求一次。
/// </summary>
public sealed class TtsService
{
    public static TtsService Instance { get; } = new();

    private readonly HttpClient _http = new();
    private readonly Dictionary<string, AudioStream?> _cache = new();
    private AudioStreamPlayer? _player;

    private TtsService()
    {
        _http.Timeout = TimeSpan.FromSeconds(1);
        _http.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0");
    }

    /// <summary>播放单词发音。异步，不阻塞游戏。</summary>
    public async void Speak(string word)
    {
        EnsurePlayer();
        if (_player is null) return;

        var stream = await GetAudioStream(word);
        if (stream is not null)
        {
            // 独立音量控制（0-100 → -80dB 到 0dB）
            var vol = VocabConfig.Instance.TtsVolume;
            _player.VolumeDb = vol <= 0 ? -80f : Mathf.LinearToDb(vol / 100f);
            _player.Stream = stream;
            _player.Play();
            return;
        }

        // 所有在线方案失败，回退系统 TTS
        SpeakWithSystemTts(word);
    }

    /// <summary>获取音频流（带缓存，失败不缓存以便重试）。</summary>
    private async Task<AudioStream?> GetAudioStream(string word)
    {
        var key = word.ToLowerInvariant().Trim();
        if (_cache.TryGetValue(key, out var cached) && cached is not null) return cached;

        var urls = new[]
        {
            $"https://dict.youdao.com/dictvoice?audio={Uri.EscapeDataString(key)}&type=2",
            $"https://translate.google.com/translate_tts?ie=UTF-8&client=tw-ob&tl=en&q={Uri.EscapeDataString(key)}"
        };

        // 依次尝试每个 API，不重试
        foreach (var url in urls)
        {
            var data = await TryDownload(url);
            if (data is not null && data.Length > 100)
            {
                var stream = CreateAudioStream(data);
                if (stream is not null)
                {
                    _cache[key] = stream;
                    return stream;
                }
            }
        }

        Log.Warn($"[VocabSpire] All TTS sources failed for '{key}', falling back to system TTS.");
        return null; // 不缓存失败结果，下次还会重试
    }

    private async Task<byte[]?> TryDownload(string url)
    {
        try
        {
            var bytes = await _http.GetByteArrayAsync(url);
            return bytes.Length > 100 ? bytes : null;
        }
        catch (Exception ex)
        {
            Log.Warn($"[VocabSpire] TTS download failed: {ex.Message}");
            return null;
        }
    }

    private static AudioStream? CreateAudioStream(byte[] mp3Data)
    {
        try
        {
            var stream = new AudioStreamMP3();
            stream.Data = mp3Data;
            return stream;
        }
        catch (Exception ex)
        {
            Log.Error($"[VocabSpire] AudioStreamMP3 creation failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>系统 TTS 回退（Windows SAPI / macOS say 命令）。</summary>
    private static void SpeakWithSystemTts(string word)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                // PowerShell 调用 Windows SAPI
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell",
                    Arguments = $"-Command \"Add-Type -AssemblyName System.Speech; " +
                        $"$s = New-Object System.Speech.Synthesis.SpeechSynthesizer; " +
                        $"$s.Speak('{word.Replace("'", "''")}')\"",
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                Process.Start(psi);
            }
            else if (OperatingSystem.IsMacOS())
            {
                // macOS say 命令
                var psi = new ProcessStartInfo
                {
                    FileName = "say",
                    Arguments = $"-v Samantha \"{word.Replace("\"", "\\\"")}\"",
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                Process.Start(psi);
            }
            else
            {
                // Linux: espeak
                var psi = new ProcessStartInfo
                {
                    FileName = "espeak",
                    Arguments = $"\"{word.Replace("\"", "\\\"")}\"",
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                Process.Start(psi);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"[VocabSpire] System TTS failed: {ex.Message}");
        }
    }

    private void EnsurePlayer()
    {
        if (_player is not null && GodotObject.IsInstanceValid(_player)) return;

        var root = GameBridge.GetUIRoot();
        if (root is null) return;

        _player = new AudioStreamPlayer { Name = "VocabSpireTts" };
        root.AddChild(_player);
    }
}
