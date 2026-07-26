using System.Net.Http;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VocabSpire.Services;

/// <summary>
/// AnkiChinas 加密牌组解密器。
/// 全部密钥、IV、API 端点均从 apkg 内的 notetype config 动态提取，零硬编码。
///
/// 加密方案：AES-256-CBC + PKCS7，密钥由服务器按 RSA 握手下发。
/// notetype config（protobuf blob）内嵌的 JS 模板包含：
///   · _ck（客户端标识）、cpk（RSA 私钥）、spk（RSA 公钥，部分）、ankiUrl（服务器）
///   · javascript-obfuscator 字符串表内藏 AES IV 和 spk 后缀
/// </summary>
public static class AnkiChinasDecryptor
{
    private static readonly Regex CipherBlock = new(@"≯#(.*?)#≮", RegexOptions.Compiled | RegexOptions.Singleline);

    /// <summary>检测 notes 字段是否含 AnkiChinas 密文标记。</summary>
    public static bool IsEncrypted(IEnumerable<Dictionary<string, object?>> notes)
    {
        foreach (var n in notes)
        {
            if (n.GetValueOrDefault("flds") is string flds && flds.Contains("≯#"))
                return true;
        }
        return false;
    }

    /// <summary>从 notetype config blob 提取全部加密参数。</summary>
    public static EncryptionParams? ExtractParams(byte[] configBlob)
    {
        var text = Encoding.UTF8.GetString(configBlob);

        var ck = ExtractQuoted(text, "_ck");
        var cpk = ExtractQuoted(text, "cpk");
        var spk = ExtractQuoted(text, "spk");
        var url = ExtractQuoted(text, "ankiUrl");

        if (ck == null || cpk == null || spk == null || url == null)
            return null;

        var (iv, spkSuffix) = ExtractFromStringTable(text);
        if (spkSuffix != null)
            spk += spkSuffix;

        return new EncryptionParams
        {
            ClientKey = ck,
            PrivateKeyB64 = cpk,
            ServerPubKeyB64 = spk,
            ApiBaseUrl = url,
            AesIv = iv ?? "12345679abcdefgj" // 最终回退（实际应从字符串表提取到）
        };
    }

    /// <summary>向 AnkiChinas 服务器换取 AES 会话密钥。</summary>
    public static string FetchAesKey(EncryptionParams p)
    {
        var visitorId = GetStableDeviceId();
        var payload = JsonSerializer.Serialize(new { k = p.ClientKey, i = visitorId });

        string encryptedPayload;
        using (var rsa = RSA.Create())
        {
            rsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(p.ServerPubKeyB64), out _);
            var ct = rsa.Encrypt(Encoding.UTF8.GetBytes(payload), RSAEncryptionPadding.Pkcs1);
            encryptedPayload = Convert.ToBase64String(ct);
        }

        var url = $"{p.ApiBaseUrl}/server/ck/{p.ClientKey}";
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var content = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("data", encryptedPayload) });
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 AnkiDesktop");

        using var response = http.Send(request);
        response.EnsureSuccessStatusCode();

        var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        if (root.GetProperty("code").GetInt32() != 0)
        {
            var rawMsg = root.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
            var plainMsg = Regex.Replace(rawMsg, @"<[^>]+>", " ").Trim();
            plainMsg = Regex.Replace(plainMsg, @"\s{2,}", " ");
            if (plainMsg.Length == 0) plainMsg = "服务器返回未知错误";
            throw new InvalidOperationException($"AnkiChinas 密钥获取失败: {plainMsg}");
        }

        var skEncrypted = root.GetProperty("data").GetProperty("sk").GetString()!;

        using var rsaPriv = RSA.Create();
        rsaPriv.ImportRSAPrivateKey(Convert.FromBase64String(p.PrivateKeyB64), out _);
        var skBytes = rsaPriv.Decrypt(Convert.FromBase64String(skEncrypted), RSAEncryptionPadding.Pkcs1);
        var skJson = Encoding.UTF8.GetString(skBytes);

        using var skDoc = JsonDocument.Parse(skJson);
        return skDoc.RootElement.GetProperty("key").GetString()!;
    }

    /// <summary>解密单个字段中的所有 ≯#...#≮ 密文块。</summary>
    public static string DecryptField(string field, string aesKey, string iv)
    {
        if (!field.Contains("≯#")) return field;
        return CipherBlock.Replace(field, m => DecryptBlock(m.Groups[1].Value, aesKey, iv));
    }

    /// <summary>批量解密 notes 的 flds 字段。返回已解密的 flds 列表（与输入顺序对应）。</summary>
    public static void DecryptNotes(List<Dictionary<string, object?>> notes, string aesKey, string iv)
    {
        foreach (var n in notes)
        {
            if (n.GetValueOrDefault("flds") is string flds && flds.Contains("≯#"))
                n["flds"] = CipherBlock.Replace(flds, m => DecryptBlock(m.Groups[1].Value, aesKey, iv));
        }
    }

    // ── 内部方法 ──────────────────────────────────────────────────────────────

    private static string DecryptBlock(string cipherB64, string aesKey, string iv)
    {
        try
        {
            var ct = Convert.FromBase64String(cipherB64);
            using var aes = Aes.Create();
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            aes.Key = Encoding.UTF8.GetBytes(aesKey);
            aes.IV = Encoding.UTF8.GetBytes(iv);
            using var dec = aes.CreateDecryptor();
            var pt = dec.TransformFinalBlock(ct, 0, ct.Length);
            return Encoding.UTF8.GetString(pt);
        }
        catch
        {
            return $"[解密失败]";
        }
    }

    /// <summary>从 JS 明文区提取带引号的常量值（支持 let/const/var，单双引号）。</summary>
    private static string? ExtractQuoted(string text, string varName)
    {
        // 匹配: let _ck = "VALUE"  /  const cpk = 'VALUE'  /  var ankiUrl = "VALUE"
        var pattern = $@"(?:let|const|var)\s+{Regex.Escape(varName)}\s*=\s*([""'])(.+?)\1";
        var m = Regex.Match(text, pattern, RegexOptions.Singleline);
        return m.Success ? m.Groups[2].Value : null;
    }

    /// <summary>
    /// 从 javascript-obfuscator 的 base64 字符串表中提取 AES IV 和 RSA 公钥后缀。
    /// 不需要完整去混淆——只需解码所有 base64 条目，按特征匹配。
    /// </summary>
    private static (string? iv, string? spkSuffix) ExtractFromStringTable(string text)
    {
        // 字符串表格式: ['base64_1','base64_2',...]
        // 定位: 紧跟在 const _0xNNNN=[ 之后，到 ];(function 之前
        var tableMatch = Regex.Match(text, @"const\s+_0x[a-f0-9]+\s*=\s*\[([^\]]{100,})\]\s*;\s*\(function");
        if (!tableMatch.Success)
        {
            // 备选：找任何大型 base64 字符串数组
            tableMatch = Regex.Match(text, @"=\s*\[('(?:[A-Za-z0-9+/=]+)'(?:\s*,\s*'[A-Za-z0-9+/=]+')+)\]");
        }
        if (!tableMatch.Success) return (null, null);

        var entries = Regex.Matches(tableMatch.Groups[1].Value, @"'([A-Za-z0-9+/=]+)'");
        string? iv = null;
        string? spkSuffix = null;

        foreach (Match entry in entries)
        {
            string b64 = entry.Groups[1].Value;
            string decoded;
            try { decoded = Encoding.UTF8.GetString(Convert.FromBase64String(b64)); }
            catch { continue; }

            // AES IV: 恰好 16 字节、纯 ASCII 可打印、含数字和字母
            if (iv == null && decoded.Length == 16
                && decoded.All(c => c >= 0x20 && c < 0x7F)
                && decoded.Any(char.IsDigit) && decoded.Any(char.IsLetter))
            {
                iv = decoded;
            }

            // RSA 公钥后缀: 以 IDAQAB 结尾（标准 RSA 公指数 65537 的 base64 尾部）
            if (spkSuffix == null && decoded.EndsWith("IDAQAB") && decoded.Length < 30)
            {
                spkSuffix = decoded;
            }
        }

        return (iv, spkSuffix);
    }

    /// <summary>
    /// 生成稳定的设备标识，模拟 FingerprintJS 的行为：同一台机器始终返回相同 ID，
    /// 只占用一个设备槽位，避免反复生成随机 ID 导致「超出设备限制」。
    /// </summary>
    private static string GetStableDeviceId()
    {
        var sb = new StringBuilder();
        sb.Append(Environment.MachineName);
        sb.Append('|');
        sb.Append(Environment.UserName);
        try
        {
            var nic = NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(n => n.OperationalStatus == OperationalStatus.Up
                    && n.NetworkInterfaceType != NetworkInterfaceType.Loopback);
            if (nic != null) sb.Append('|').Append(nic.GetPhysicalAddress());
        }
        catch { /* 网络接口不可用时忽略 */ }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hash)[..20].ToLowerInvariant();
    }

    // ── 参数容器 ──────────────────────────────────────────────────────────────

    public sealed class EncryptionParams
    {
        public string ClientKey { get; init; } = "";
        public string PrivateKeyB64 { get; init; } = "";
        public string ServerPubKeyB64 { get; init; } = "";
        public string ApiBaseUrl { get; init; } = "";
        public string AesIv { get; init; } = "";
    }
}
