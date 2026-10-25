using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SoundManager.Updates;

public sealed record UpdateAsset(string File, string Sha256, long Size, int VersionCode = 0);
public sealed record UpdateManifest(int Schema, string Version, UpdateAsset Windows, UpdateAsset Android)
{
    // Public key of the existing Android release certificate. The private key stays in release-signing/release.jks.
    public const string PublicKey = "MIIBojANBgkqhkiG9w0BAQEFAAOCAY8AMIIBigKCAYEAskaoCbavX3ooeUsouK2U316SgiAfj1j7qdG1c+yKxgBt4jG97rGgKO0/YTc58BdjSBVZPKQ4zuYMiCi4Vh209fMsvCdm9DBS09ajKd5Mde061VR/RVC237zK+XbhQ92PYH6DAT2caxrQrI4d47QBWjCZtc3IBWkoghQZjXu3LY5mmd3AKw+ayyP+oX2jOme2jMIN7ZDNM3K6rHMusHX8bYLyA5d+YsNY1xbkv8ne8mXXor0sby/OTnI89f/7bagnR1YHZObgh6m8cSwe9pQ5+X1xHtuCV31UV9paEJlRBAk3LTKPQKuNvbtHqL/jl86ZFCbwqz/x+qTXtka1IWmeBR1ehYPE4mFMHPD8/J9HwBIBK74Buv50iOzvRIHGzfRkmo61CQKtMp0GOYrNe/RCLZeIFVA1JD6aOEMbHovt/sorvbLEfpRcUMfE2K6YIkI/dOxCFm8tuyPIDUnQcQ5L8/oEcfejUGWJGzMzO0oIXNJIw9Zzfq0bcz7ZDQGREoNnAgMBAAE=";
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static System.Version ParseVersion(string version) => Regex.IsMatch(version, @"^(0|[1-9]\d{0,5})\.(0|[1-9]\d{0,5})\.(0|[1-9]\d{0,5})$")
        ? System.Version.Parse(version) : throw new InvalidDataException("잘못된 업데이트 버전");
    public static UpdateManifest Verify(byte[] bytes, string signature, string publicKey = PublicKey)
    {
        if (bytes.Length > 65536 || signature.Length > 4096) throw new InvalidDataException("업데이트 정보가 너무 큽니다.");
        using var rsa = RSA.Create(); rsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);
        if (!rsa.VerifyData(bytes, Convert.FromBase64String(signature.Trim()), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
            throw new CryptographicException("업데이트 서명을 확인하지 못했습니다.");
        var manifest = JsonSerializer.Deserialize<UpdateManifest>(bytes, Json) ?? throw new InvalidDataException("업데이트 정보가 없습니다.");
        _ = ParseVersion(manifest.Version);
        if (manifest.Schema != 1 || manifest.Windows is null || manifest.Android is null) throw new InvalidDataException("지원하지 않는 업데이트 정보");
        ValidateAsset(manifest.Windows, $"SoundManager-{manifest.Version}-windows-x64.exe", 512L * 1024 * 1024);
        ValidateAsset(manifest.Android, $"SoundManager-{manifest.Version}-android.apk", 128L * 1024 * 1024);
        if (manifest.Android.VersionCode <= 0) throw new InvalidDataException("잘못된 Android 버전");
        return manifest;
    }
    private static void ValidateAsset(UpdateAsset asset, string expected, long max)
    {
        if (asset.File != expected || asset.Size <= 0 || asset.Size > max || !Regex.IsMatch(asset.Sha256 ?? "", "^[a-fA-F0-9]{64}$"))
            throw new InvalidDataException("잘못된 업데이트 파일 정보");
    }
    public static string AssetUrl(string version, string file) =>
        $"https://github.com/lunce0503/sound_manger/releases/download/v{version}/{file}";
}

public sealed record VerifiedUpdate(UpdateManifest Manifest, byte[] ManifestBytes, string Signature);
