using System.Diagnostics;
using HttpClient = System.Net.Http.HttpClient;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Logging;

namespace VocabSpire.Services;

/// <summary>
/// 文本转语音服务 —— 在线 API 优先，回退系统 TTS。
///
/// 优先级：有道词典 → Google Translate TTS → 系统 TTS (Windows SAPI / macOS say)
/// 音频缓存在内存中，同一单词只请求一次；播放后异常短（<200ms）自动驱逐 + 回退。
/// </summary>
public sealed class TtsService
{
    public static TtsService Instance { get; } = new();

    /// <summary>音频字节数硬下限（防 HTML 错误页 / 空响应）。正常 MP3 至少 1KB。</summary>
    private const int MinAudioBytes = 1024;

    /// <summary>实际播放时长低于这个值视为"解码失败/沉默" → 驱逐缓存 + 回退。</summary>
    private const long MinPlaybackMs = 200;

    private readonly HttpClient _http = new();
    private readonly Dictionary<string, AudioStream?> _cache = new();
    private AudioStreamPlayer? _player;

    // 用于 Finished 信号判断"上一次播放是不是过短"
    private string? _lastPlayedWord;
    private long _lastPlayedTicket;
    private long _nextTicket;
    private readonly Stopwatch _playStopwatch = new();
    private readonly object _stateLock = new();

    private TtsService()
    {
        _http.Timeout = TimeSpan.FromSeconds(3);
        _http.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0");
    }

    /// <summary>播放单词发音。异步，不阻塞游戏。</summary>
    public async void Speak(string word)
    {
        var sw = Stopwatch.StartNew();
        Log.Info($"[VocabSpire][TTS] Speak called: '{word}' (cache size={_cache.Count})");

        try
        {
            EnsurePlayer();
            if (_player is null || !GodotObject.IsInstanceValid(_player))
            {
                Log.Error("[VocabSpire][TTS] AudioStreamPlayer unavailable (null/disposed).");
                return;
            }

            var stream = await GetAudioStream(word);
            if (stream is not null)
            {
                var vol = VocabConfig.Instance.TtsVolume;
                var db = vol <= 0 ? -80f : Mathf.LinearToDb(vol / 100f);
                _player.VolumeDb = db;
                _player.Stream = stream;

                lock (_stateLock)
                {
                    _lastPlayedTicket = ++_nextTicket;
                    _lastPlayedWord = word;
                    _playStopwatch.Restart();
                }

                _player.Play();
                Log.Info($"[VocabSpire][TTS] PLAY ok: '{word}' vol={vol} db={db:F1} setup={sw.ElapsedMilliseconds}ms");
                return;
            }

            // 所有在线方案失败，回退系统 TTS
            Log.Warn($"[VocabSpire][TTS] Online TTS exhausted for '{word}' after {sw.ElapsedMilliseconds}ms → falling back to system TTS");
            SpeakWithSystemTts(word);
        }
        catch (Exception ex)
        {
            Log.Error($"[VocabSpire][TTS] Speak('{word}') unhandled ({ex.GetType().Name}): {ex.Message}\n{ex.StackTrace}");
        }
    }

    /// <summary>Godot AudioStreamPlayer 的 Finished 信号回调。</summary>
    private void OnPlayerFinished()
    {
        string? word;
        long elapsed;
        long ticket;
        lock (_stateLock)
        {
            word = _lastPlayedWord;
            elapsed = _playStopwatch.ElapsedMilliseconds;
            ticket = _lastPlayedTicket;
            if (word is null) return; // 已被前一次回调处理
            _lastPlayedWord = null;    // 标记已处理
        }

        if (elapsed < MinPlaybackMs)
        {
            // 解码失败 / 沉默 / stream 损坏
            var key = word.ToLowerInvariant().Trim();
            Log.Warn($"[VocabSpire][TTS] SUSPICIOUS short playback for '{word}' ({elapsed}ms, ticket={ticket}) — evicting cache and falling back to system TTS");
            lock (_cache) { _cache.Remove(key); }
            SpeakWithSystemTts(word);
        }
        else
        {
            Log.Info($"[VocabSpire][TTS] Finished: '{word}' duration={elapsed}ms (ticket={ticket}) — OK");
        }
    }

    /// <summary>获取音频流（带缓存，失败不缓存以便重试）。</summary>
    private async Task<AudioStream?> GetAudioStream(string word)
    {
        var key = word.ToLowerInvariant().Trim();
        lock (_cache)
        {
            if (_cache.TryGetValue(key, out var cached) && cached is not null)
            {
                Log.Info($"[VocabSpire][TTS] Cache HIT for '{key}'");
                return cached;
            }
        }
        Log.Info($"[VocabSpire][TTS] Cache MISS for '{key}', trying online sources (HTTP timeout={_http.Timeout.TotalSeconds}s, min bytes={MinAudioBytes})");

        var urls = new (string name, string url)[]
        {
            ("youdao",  $"https://dict.youdao.com/dictvoice?audio={Uri.EscapeDataString(key)}&type=2"),
            ("google",  $"https://translate.google.com/translate_tts?ie=UTF-8&client=tw-ob&tl=en&q={Uri.EscapeDataString(key)}")
        };

        foreach (var (name, url) in urls)
        {
            var sw = Stopwatch.StartNew();
            var (data, contentType) = await TryDownload(name, url);
            var ms = sw.ElapsedMilliseconds;

            if (data is null)
            {
                Log.Warn($"[VocabSpire][TTS] [{name}] download FAIL '{key}' ({ms}ms)");
                continue;
            }

            Log.Info($"[VocabSpire][TTS] [{name}] download OK '{key}' bytes={data.Length} contentType={contentType} ({ms}ms)");

            // 校验 1：字节数下限
            if (data.Length < MinAudioBytes)
            {
                Log.Warn($"[VocabSpire][TTS] [{name}] REJECT bytes={data.Length} < {MinAudioBytes} (likely empty or error page)");
                continue;
            }

            // 校验 2：Content-Type 不能明显是 HTML/text
            if (!string.IsNullOrEmpty(contentType) && IsObviouslyNotAudio(contentType))
            {
                Log.Warn($"[VocabSpire][TTS] [{name}] REJECT Content-Type='{contentType}' (looks like error page)");
                continue;
            }

            // 校验 3：MP3 magic bytes（ID3 或 MPEG frame sync）
            if (!LooksLikeMp3(data))
            {
                var hex = HexHead(data, 8);
                Log.Warn($"[VocabSpire][TTS] [{name}] REJECT magic bytes mismatch (head={hex}) — not a real MP3");
                continue;
            }

            var stream = CreateAudioStream(data, name);
            if (stream is not null)
            {
                lock (_cache) { _cache[key] = stream; }
                Log.Info($"[VocabSpire][TTS] [{name}] cached AudioStreamMP3 for '{key}'");
                return stream;
            }
        }

        Log.Warn($"[VocabSpire][TTS] ALL online sources failed for '{key}', falling back to system TTS.");
        return null;
    }

    /// <summary>Content-Type 黑名单（明显不是音频的）。</summary>
    private static bool IsObviouslyNotAudio(string contentType)
    {
        var ct = contentType.ToLowerInvariant();
        // 黑名单：html / xml / json / plain text
        if (ct.StartsWith("text/")) return true;
        if (ct.Contains("html") || ct.Contains("xml") || ct.Contains("json")) return true;
        return false;
        // 注：不强制要求 "audio/*"，因为部分 CDN 用 application/octet-stream 也合法。
    }

    /// <summary>校验 MP3 magic bytes。</summary>
    private static bool LooksLikeMp3(byte[] data)
    {
        if (data.Length < 3) return false;
        // 1) ID3v2 标签开头："ID3" = 0x49 0x44 0x33
        if (data[0] == 0x49 && data[1] == 0x44 && data[2] == 0x33) return true;
        // 2) MPEG 帧同步：第一字节 0xFF，第二字节高 3 bit 必须全 1（即 & 0xE0 == 0xE0）
        if (data[0] == 0xFF && (data[1] & 0xE0) == 0xE0) return true;
        // 其他都不是
        return false;
    }

    /// <summary>前 N 字节的 hex（诊断用）。</summary>
    private static string HexHead(byte[] data, int n)
    {
        var len = Math.Min(n, data.Length);
        var sb = new System.Text.StringBuilder(len * 3);
        for (var i = 0; i < len; i++)
        {
            sb.Append(data[i].ToString("X2"));
            if (i < len - 1) sb.Append(' ');
        }
        return sb.ToString();
    }

    /// <summary>下载并返回 (bytes, content-type)。出错返回 (null, null)。</summary>
    private async Task<(byte[]? data, string? contentType)> TryDownload(string sourceName, string url)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead);
            if (!resp.IsSuccessStatusCode)
            {
                Log.Warn($"[VocabSpire][TTS] [{sourceName}] HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}");
                return (null, null);
            }
            var ct = resp.Content.Headers.ContentType?.MediaType;
            var bytes = await resp.Content.ReadAsByteArrayAsync();
            return (bytes, ct);
        }
        catch (TaskCanceledException ex)
        {
            Log.Warn($"[VocabSpire][TTS] [{sourceName}] TIMEOUT after {_http.Timeout.TotalSeconds}s ({ex.Message})");
            return (null, null);
        }
        catch (HttpRequestException ex)
        {
            Log.Warn($"[VocabSpire][TTS] [{sourceName}] HttpRequestException: {ex.Message} (StatusCode={ex.StatusCode})");
            return (null, null);
        }
        catch (Exception ex)
        {
            Log.Warn($"[VocabSpire][TTS] [{sourceName}] download exception ({ex.GetType().Name}): {ex.Message}");
            return (null, null);
        }
    }

    private static AudioStream? CreateAudioStream(byte[] mp3Data, string sourceName)
    {
        try
        {
            var stream = new AudioStreamMP3();
            stream.Data = mp3Data;
            Log.Info($"[VocabSpire][TTS] [{sourceName}] AudioStreamMP3 created (bytes={mp3Data.Length})");
            return stream;
        }
        catch (Exception ex)
        {
            Log.Error($"[VocabSpire][TTS] [{sourceName}] AudioStreamMP3 creation failed ({ex.GetType().Name}): {ex.Message}");
            return null;
        }
    }

    /// <summary>系统 TTS 回退（Windows SAPI / macOS say 命令）。</summary>
    private static void SpeakWithSystemTts(string word)
    {
        try
        {
            string fileName, args, platform;

            if (OperatingSystem.IsWindows())
            {
                platform = "Windows SAPI (PowerShell)";
                fileName = "powershell";
                args = $"-NoProfile -Command \"Add-Type -AssemblyName System.Speech; " +
                       $"$s = New-Object System.Speech.Synthesis.SpeechSynthesizer; " +
                       $"$s.Speak('{word.Replace("'", "''")}')\"";
            }
            else if (OperatingSystem.IsMacOS())
            {
                platform = "macOS say";
                fileName = "say";
                args = $"-v Samantha \"{word.Replace("\"", "\\\"")}\"";
            }
            else
            {
                platform = "Linux espeak";
                fileName = "espeak";
                args = $"\"{word.Replace("\"", "\\\"")}\"";
            }

            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = args,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };

            Log.Info($"[VocabSpire][TTS] System TTS fallback: platform={platform} word='{word}'");
            var proc = Process.Start(psi);
            if (proc is null)
            {
                Log.Error($"[VocabSpire][TTS] Process.Start returned null for {platform}");
                return;
            }
            Log.Info($"[VocabSpire][TTS] System TTS process started (pid={proc.Id})");

            _ = Task.Run(async () =>
            {
                try
                {
                    var stderr = await proc.StandardError.ReadToEndAsync();
                    await proc.WaitForExitAsync();
                    if (proc.ExitCode != 0)
                    {
                        Log.Warn($"[VocabSpire][TTS] System TTS exit={proc.ExitCode} word='{word}' stderr={stderr.Trim()}");
                    }
                    else
                    {
                        Log.Info($"[VocabSpire][TTS] System TTS completed (pid={proc.Id} exit=0)");
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn($"[VocabSpire][TTS] System TTS wait failed ({ex.GetType().Name}): {ex.Message}");
                }
            });
        }
        catch (Exception ex)
        {
            Log.Error($"[VocabSpire][TTS] System TTS launch FAILED ({ex.GetType().Name}): {ex.Message}\n{ex.StackTrace}");
        }
    }

    private void EnsurePlayer()
    {
        if (_player is not null && GodotObject.IsInstanceValid(_player)) return;

        var root = GameBridge.GetUIRoot();
        if (root is null)
        {
            Log.Error("[VocabSpire][TTS] EnsurePlayer: UI root is null");
            return;
        }

        _player = new AudioStreamPlayer
        {
            Name = "VocabSpireTts",
            ProcessMode = Node.ProcessModeEnum.Always // 答题时 SceneTree.Paused=true 也要响
        };
        _player.Finished += OnPlayerFinished;
        root.AddChild(_player);
        Log.Info("[VocabSpire][TTS] AudioStreamPlayer created (ProcessMode=Always, Finished connected)");
    }
}
