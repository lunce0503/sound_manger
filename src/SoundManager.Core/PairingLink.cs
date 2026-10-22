using System.Net;

namespace SoundManager.Core;

public sealed record PairingLink(string Address, string Key)
{
    public static string NormalizeAddress(string address)
    {
        if (address.Length > 2048 || !Uri.TryCreate(address.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || string.IsNullOrEmpty(uri.Host) || uri.Port == 0 ||
            uri.UserInfo.Length != 0 || uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new ArgumentException("서버의 http(s) 주소만 입력하세요. 경로·계정·쿼리는 사용할 수 없습니다.");
        if (uri.Scheme == "http" && !IsPrivateHost(uri.Host))
            throw new ArgumentException("외부 서버는 HTTPS 주소가 필요합니다.");
        return uri.GetLeftPart(UriPartial.Authority);
    }

    private static bool IsPrivateHost(string host)
    {
        host = host.Trim('[', ']');
        if (!IPAddress.TryParse(host, out var ip))
            return host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                !host.Contains('.') || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase);
        if (IPAddress.IsLoopback(ip) || ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal) return true;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        byte[] bytes = ip.GetAddressBytes();
        if (bytes.Length == 16) return (bytes[0] & 0xfe) == 0xfc;
        return bytes[0] == 10 || bytes[0] == 127 || bytes[0] == 192 && bytes[1] == 168 ||
            bytes[0] == 172 && bytes[1] is >= 16 and <= 31 || bytes[0] == 169 && bytes[1] == 254 ||
            bytes[0] == 100 && bytes[1] is >= 64 and <= 127;
    }

    public string Encode()
    {
        string address = NormalizeAddress(Address);
        ValidateKey(Key);
        return $"soundmanager://pair?v=1&server={Uri.EscapeDataString(address)}&key={Uri.EscapeDataString(Key)}";
    }

    public static PairingLink Parse(string text)
    {
        if (text.Length > 4096 || !Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme != "soundmanager" || uri.Host != "pair" || uri.AbsolutePath is not ("" or "/") ||
            uri.UserInfo.Length != 0 || uri.Port != -1 || uri.Fragment.Length != 0)
            throw new ArgumentException("Sound Manager 연결 QR이 아닙니다.");
        var fields = new Dictionary<string, string>();
        foreach (string field in uri.Query.TrimStart('?').Split('&'))
        {
            string[] parts = field.Split('=', 2);
            if (parts.Length != 2 || !fields.TryAdd(Uri.UnescapeDataString(parts[0]), Uri.UnescapeDataString(parts[1])))
                throw new ArgumentException("연결 정보에 중복되거나 잘못된 항목이 있습니다.");
        }
        if (fields.Count != 3 || fields.GetValueOrDefault("v") != "1" ||
            !fields.TryGetValue("server", out string? address) || !fields.TryGetValue("key", out string? key))
            throw new ArgumentException("지원하지 않는 연결 정보입니다.");
        ValidateKey(key);
        return new(NormalizeAddress(address), key);
    }

    private static void ValidateKey(string key)
    {
        if (key.Length is < 24 or > 128 || key.Any(c => !char.IsAsciiLetterOrDigit(c)))
            throw new ArgumentException("연결 키를 확인하세요.");
    }
}
