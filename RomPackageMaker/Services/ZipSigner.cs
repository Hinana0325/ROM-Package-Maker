using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace RomPackageMaker.Services;

/// <summary>
/// Android 刷机包（zip）签名，使用 APK v1（JAR）签名方案。
/// 默认在运行时生成自签名测试证书；也可通过设置页配置外部私钥（.pfx 或 .pk8 + .x509.pem）。
/// 注意：JAR 规范要求 CERT.SF 中各段摘要与 MANIFEST.MF 的实际字节一致，
/// 因此所有文本统一使用 LF 行尾，摘要直接基于生成的字节计算。
/// </summary>
internal static class ZipSigner
{
    private const string Lf = "\n";

    /// <summary>对已有 zip 进行签名，输出到目标路径（可为同一路径覆盖）。
    /// 未显式传入证书时，优先使用设置页配置的外部密钥，否则生成测试证书。</summary>
    public static void SignZip(string inputZip, string outputZip, X509Certificate2? cert = null, AsymmetricAlgorithm? privateKey = null)
    {
        if (cert == null)
        {
            var external = TryLoadExternalSigner();
            if (external is not null)
            {
                cert = external.Value.Cert;
                privateKey ??= external.Value.Key;
            }
            else
            {
                cert = GenerateTestCertificate(out var generatedKey);
                privateKey ??= generatedKey;
            }
        }

        // 直接在文件流上读取（3GB 级刷机包不能整体读入内存）
        using var inFs = new FileStream(inputZip, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var archive = new ZipArchive(inFs, ZipArchiveMode.Read, leaveOpen: true);

        // 计算所有条目的摘要
        var manifestEntries = new List<(string Name, string Hash)>();
        using (var sha256 = SHA256.Create())
        {
            foreach (var entry in archive.Entries)
            {
                if (entry.FullName.StartsWith("META-INF/", StringComparison.OrdinalIgnoreCase)) continue;
                using var es = entry.Open();
                manifestEntries.Add((entry.FullName, Convert.ToBase64String(sha256.ComputeHash(es))));
            }
        }

        // 构建 MANIFEST.MF（LF 行尾）
        var manifest = new StringBuilder();
        AppendLf(manifest, "Manifest-Version: 1.0");
        AppendLf(manifest, "Created-By: RomPackageMaker");
        manifest.Append(Lf);
        var sections = new List<(string Name, byte[] SectionBytes)>();
        foreach (var (name, hash) in manifestEntries)
        {
            var section = new StringBuilder();
            AppendLf(section, $"Name: {name}");
            AppendLf(section, $"SHA-256-Digest: {hash}");
            // 段落字节 = 两行属性 + 结尾空行（与 MANIFEST.MF 内完全一致）
            byte[] sectionBytes = Encoding.UTF8.GetBytes(section.ToString() + Lf);
            sections.Add((name, sectionBytes));
            manifest.Append(section).Append(Lf);
        }
        byte[] manifestBytes = Encoding.UTF8.GetBytes(manifest.ToString());

        // 构建 CERT.SF：主段 + 每个条目段的摘要（与 MANIFEST.MF 字节精确对应）
        var sf = new StringBuilder();
        AppendLf(sf, "Signature-Version: 1.0");
        AppendLf(sf, "Created-By: RomPackageMaker");
        using (var sha256 = SHA256.Create())
        {
            string manifestHash = Convert.ToBase64String(sha256.ComputeHash(manifestBytes));
            AppendLf(sf, $"SHA-256-Digest-Manifest: {manifestHash}");
        }
        sf.Append(Lf);
        using (var sha256Sec = SHA256.Create())
        {
            foreach (var (name, sectionBytes) in sections)
            {
                string digest = Convert.ToBase64String(sha256Sec.ComputeHash(sectionBytes));
                AppendLf(sf, $"Name: {name}");
                AppendLf(sf, $"SHA-256-Digest: {digest}");
                sf.Append(Lf);
            }
        }
        byte[] sfBytes = Encoding.UTF8.GetBytes(sf.ToString());

        // 签名 CERT.SF -> CERT.RSA (PKCS#7)
        byte[] certRsa = SignDataPkcs7(sfBytes, cert, privateKey!);

        // 写入输出 zip：先复制原有条目，再追加 META-INF 文件
        using (var outFs = File.Create(outputZip))
        {
            using var outArchive = new ZipArchive(outFs, ZipArchiveMode.Create, leaveOpen: false);
            foreach (var entry in archive.Entries)
            {
                if (entry.FullName.StartsWith("META-INF/", StringComparison.OrdinalIgnoreCase)) continue;
                var outEntry = outArchive.CreateEntry(entry.FullName, CompressionLevel.Optimal);
                using var es = entry.Open();
                using var oe = outEntry.Open();
                es.CopyTo(oe);
            }

            WriteStoredEntry(outArchive, "META-INF/MANIFEST.MF", manifestBytes);
            WriteStoredEntry(outArchive, "META-INF/CERT.SF", sfBytes);
            WriteStoredEntry(outArchive, "META-INF/CERT.RSA", certRsa);
        }
    }

    private static void AppendLf(StringBuilder sb, string line)
    {
        sb.Append(line).Append(Lf);
    }

    private static void WriteStoredEntry(ZipArchive archive, string name, byte[] data)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.NoCompression);
        using var s = entry.Open();
        s.Write(data, 0, data.Length);
    }

    private static byte[] SignDataPkcs7(byte[] data, X509Certificate2 cert, AsymmetricAlgorithm key)
    {
        var content = new ContentInfo(data);
        var signedCms = new SignedCms(content, true);
        var signer = new CmsSigner(SubjectIdentifierType.IssuerAndSerialNumber, cert, key);
        signer.IncludeOption = X509IncludeOption.WholeChain;
        signedCms.ComputeSignature(signer);
        return signedCms.Encode();
    }

    /// <summary>从设置读取外部签名私钥（.pfx/.p12，或 .pk8 + .x509.pem）。不可用时返回 null。</summary>
    private static (X509Certificate2 Cert, AsymmetricAlgorithm Key)? TryLoadExternalSigner()
    {
        var settings = AppSettings.Current;
        string keyPath = settings.SignKeyPath;
        if (string.IsNullOrWhiteSpace(keyPath) || !File.Exists(keyPath)) return null;

        try
        {
            string ext = Path.GetExtension(keyPath).ToLowerInvariant();
            if (ext == ".pfx" || ext == ".p12")
            {
                var cert = new X509Certificate2(keyPath, (string?)null,
                    X509KeyStorageFlags.Exportable | X509KeyStorageFlags.EphemeralKeySet);
                if (cert.GetRSAPrivateKey() is RSA rsa) return (cert, rsa);
                if (cert.GetECDsaPrivateKey() is ECDsa ec) return (cert, ec);
                return null;
            }

            if (ext == ".pk8")
            {
                AsymmetricAlgorithm? key = ImportPkcs8(ReadPemOrDer(keyPath));
                if (key is null) return null;

                string certPath = settings.SignCertPath;
                if (string.IsNullOrWhiteSpace(certPath) || !File.Exists(certPath)) return null;
                var cert2 = X509Certificate2.CreateFromPemFile(certPath);
                return (cert2, key);
            }
        }
        catch
        {
            // 外部密钥加载失败时回退到内置测试证书
            return null;
        }
        return null;
    }

    private static AsymmetricAlgorithm? ImportPkcs8(byte[] der)
    {
        try
        {
            var rsa = RSA.Create();
            rsa.ImportPkcs8PrivateKey(der, out _);
            return rsa;
        }
        catch (CryptographicException)
        {
            try
            {
                var ec = ECDsa.Create();
                ec.ImportPkcs8PrivateKey(der, out _);
                return ec;
            }
            catch (CryptographicException)
            {
                return null;
            }
        }
    }

    /// <summary>读取 PEM（"-----BEGIN PRIVATE KEY-----"）或 DER 格式的 PKCS#8 私钥。</summary>
    private static byte[] ReadPemOrDer(string path)
    {
        string text = File.ReadAllText(path).Trim();
        const string begin = "-----BEGIN PRIVATE KEY-----";
        const string end = "-----END PRIVATE KEY-----";
        int b = text.IndexOf(begin, StringComparison.Ordinal);
        if (b >= 0)
        {
            int e = text.IndexOf(end, StringComparison.Ordinal);
            string b64 = text[(b + begin.Length)..e].Replace("\r", "").Replace("\n", "");
            return Convert.FromBase64String(b64);
        }
        return File.ReadAllBytes(path);
    }

    /// <summary>生成自签名测试证书（2048-bit RSA，SHA256，10 年有效期）。</summary>
    public static X509Certificate2 GenerateTestCertificate(out RSA privateKey)
    {
        privateKey = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=RomPackageMaker Testkey, OU=ROM, O=Custom, L=Unknown, S=Unknown, C=US",
            privateKey,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        var cert = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddYears(10));

        // 导出为带私钥的 PFX 再重新导入，确保私钥可用于签名
        var pfx = cert.Export(X509ContentType.Pfx, "rommaker");
        return new X509Certificate2(pfx, "rommaker", X509KeyStorageFlags.Exportable);
    }
}
