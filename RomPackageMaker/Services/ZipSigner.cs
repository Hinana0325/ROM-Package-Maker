using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace RomPackageMaker.Services;

/// <summary>
/// Android 刷机包（zip）签名，使用 APK v1（JAR）签名方案。
/// 默认在运行时生成自签名测试证书，也可传入外部证书与私钥。
/// </summary>
internal static class ZipSigner
{
    /// <summary>对已有 zip 进行签名，输出到目标路径（可为同一路径覆盖）。</summary>
    public static void SignZip(string inputZip, string outputZip, X509Certificate2? cert = null, AsymmetricAlgorithm? privateKey = null)
    {
        // 生成测试证书（如果未提供）
        if (cert == null)
        {
            cert = GenerateTestCertificate(out var generatedKey);
            privateKey ??= generatedKey;
        }

        using var inMs = new MemoryStream(File.ReadAllBytes(inputZip));
        using var archive = new ZipArchive(inMs, ZipArchiveMode.Read, leaveOpen: false);

        // 计算所有条目的摘要
        var manifestEntries = new Dictionary<string, string>();
        using (var sha256 = SHA256.Create())
        {
            foreach (var entry in archive.Entries)
            {
                if (entry.FullName.StartsWith("META-INF/", StringComparison.OrdinalIgnoreCase)) continue;
                using var es = entry.Open();
                var hash = sha256.ComputeHash(es);
                manifestEntries[entry.FullName] = Convert.ToBase64String(hash);
            }
        }

        // 构建 MANIFEST.MF
        var manifest = new StringBuilder();
        manifest.AppendLine("Manifest-Version: 1.0");
        manifest.AppendLine("Created-By: RomPackageMaker");
        manifest.AppendLine();
        foreach (var (name, hash) in manifestEntries)
        {
            manifest.AppendLine($"Name: {name}");
            manifest.AppendLine($"SHA-256-Digest: {hash}");
            manifest.AppendLine();
        }
        byte[] manifestBytes = Encoding.UTF8.GetBytes(manifest.ToString());

        // 构建 CERT.SF
        var sf = new StringBuilder();
        sf.AppendLine("Signature-Version: 1.0");
        sf.AppendLine("Created-By: RomPackageMaker");
        using (var sha256 = SHA256.Create())
        {
            var manifestHash = Convert.ToBase64String(sha256.ComputeHash(manifestBytes));
            sf.AppendLine($"SHA-256-Digest-Manifest: {manifestHash}");
        }
        sf.AppendLine();

        // 对 MANIFEST.MF 中每个段落（Name + SHA-256-Digest 行，含末尾空行）计算摘要
        var manifestText = manifest.ToString();
        var sections = SplitManifestSections(manifestText);
        using (var sha256 = SHA256.Create())
        {
            foreach (var section in sections)
            {
                if (!section.StartsWith("Name:", StringComparison.Ordinal)) continue;
                var hash = Convert.ToBase64String(sha256.ComputeHash(Encoding.UTF8.GetBytes(section)));
                int nameIdx = section.IndexOf("Name:", StringComparison.Ordinal);
                int nlIdx = section.IndexOf('\n', nameIdx);
                string name = section.Substring(nameIdx + 5, nlIdx - nameIdx - 5).Trim();
                sf.AppendLine($"Name: {name}");
                sf.AppendLine($"SHA-256-Digest: {hash}");
                sf.AppendLine();
            }
        }
        byte[] sfBytes = Encoding.UTF8.GetBytes(sf.ToString());

        // 签名 CERT.SF -> CERT.RSA (PKCS#7)
        byte[] certRsa = SignDataPkcs7(sfBytes, cert, privateKey!);

        // 写入输出 zip：先复制原有条目（STORED），再追加 META-INF 文件
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

            // META-INF/MANIFEST.MF (STORED)
            WriteStoredEntry(outArchive, "META-INF/MANIFEST.MF", manifestBytes);
            WriteStoredEntry(outArchive, "META-INF/CERT.SF", sfBytes);
            WriteStoredEntry(outArchive, "META-INF/CERT.RSA", certRsa);
        }
    }

    private static void WriteStoredEntry(ZipArchive archive, string name, byte[] data)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.NoCompression);
        using var s = entry.Open();
        s.Write(data, 0, data.Length);
    }

    private static IEnumerable<string> SplitManifestSections(string manifest)
    {
        // 按空行分割
        var sections = manifest.Split(new[] { "\r\n\r\n", "\n\n" }, StringSplitOptions.None);
        foreach (var s in sections)
        {
            if (!string.IsNullOrWhiteSpace(s))
                yield return s.TrimEnd('\r', '\n') + "\n\n";
        }
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
