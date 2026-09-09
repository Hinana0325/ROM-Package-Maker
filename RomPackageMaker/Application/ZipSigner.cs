using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace RomPackageMaker.Application;

/// <summary>
/// Android 閸掗攱婧€閸栧拑绱檢ip閿涘顒烽崥宥忕礉娴ｈ法鏁?APK v1閿涘湞AR閿涘顒烽崥宥嗘煙濡楀牄鈧?
/// 姒涙顓婚崷銊ㄧ箥鐞涘本妞傞悽鐔稿灇閼奉亞顒烽崥宥嗙ゴ鐠囨洝鐦夋稊锔肩幢娑旂喎褰查柅姘崇箖鐠佸墽鐤嗘い鐢稿帳缂冾喖顦婚柈銊ь潌闁姐儻绱?pfx 閹?.pk8 + .x509.pem閿涘鈧?
/// 濞夈劍鍓伴敍娆紸R 鐟欏嫯瀵栫憰浣圭湴 CERT.SF 娑擃厼鎮囧▓鍨喅鐟曚椒绗?MANIFEST.MF 閻ㄥ嫬鐤勯梽鍛摟閼哄倷绔撮懛杈剧礉
/// 閸ョ姵顒濋幍鈧張澶嬫瀮閺堫剛绮烘稉鈧担璺ㄦ暏 LF 鐞涘苯鐔敍灞炬喅鐟曚胶娲块幒銉ョ唨娴滃海鏁撻幋鎰畱鐎涙濡拋锛勭暬閵?
/// </summary>
internal static class ZipSigner
{
    private const string Lf = "\n";

    /// <summary>鐎电懓鍑￠張?zip 鏉╂稖顢戠粵鎯ф倳閿涘矁绶崙鍝勫煂閻╊喗鐖ｇ捄顖氱窞閿涘牆褰叉稉鍝勬倱娑撯偓鐠侯垰绶炵憰鍡欐磰閿涘鈧?
    /// 閺堫亝妯夊蹇庣炊閸忋儴鐦夋稊锔芥閿涘奔绱崗鍫滃▏閻劏顔曠純顕€銆夐柊宥囩枂閻ㄥ嫬顦婚柈銊ョ槕闁姐儻绱濋崥锕€鍨悽鐔稿灇濞村鐦拠浣峰姛閵?/summary>
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

        // 閻╁瓨甯撮崷銊︽瀮娴犺埖绁︽稉濠咁嚢閸欐牭绱?GB 缁狙冨煕閺堝搫瀵樻稉宥堝厴閺佺繝缍嬬拠璇插弳閸愬懎鐡ㄩ敍?
        using var inFs = new FileStream(inputZip, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var archive = new ZipArchive(inFs, ZipArchiveMode.Read, leaveOpen: true);

        // 鐠侊紕鐣婚幍鈧張澶嬫蒋閻╊喚娈戦幗妯款洣
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

        // 閺嬪嫬缂?MANIFEST.MF閿涘湢F 鐞涘苯鐔敍?
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
            // 濞堜絻鎯ょ€涙濡?= 娑撱倛顢戠仦鐐粹偓?+ 缂佹挸鐔粚楦款攽閿涘牅绗?MANIFEST.MF 閸愬懎鐣崗銊ょ閼疯揪绱?
            byte[] sectionBytes = Encoding.UTF8.GetBytes(section.ToString() + Lf);
            sections.Add((name, sectionBytes));
            manifest.Append(section).Append(Lf);
        }
        byte[] manifestBytes = Encoding.UTF8.GetBytes(manifest.ToString());

        // 閺嬪嫬缂?CERT.SF閿涙矮瀵屽▓?+ 濮ｅ繋閲滈弶锛勬窗濞堢數娈戦幗妯款洣閿涘牅绗?MANIFEST.MF 鐎涙濡划鍓р€樼€电懓绨查敍?
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

        // 缁涙儳鎮?CERT.SF -> CERT.RSA (PKCS#7)
        byte[] certRsa = SignDataPkcs7(sfBytes, cert, privateKey!);

        // 閸愭瑥鍙嗘潏鎾冲毉 zip閿涙艾鍘涙径宥呭煑閸樼喐婀侀弶锛勬窗閿涘苯鍟€鏉╄棄濮?META-INF 閺傚洣娆?
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

    /// <summary>娴犲氦顔曠純顔款嚢閸欐牕顦婚柈銊ь劮閸氬秶顫嗛柦銉礄.pfx/.p12閿涘本鍨?.pk8 + .x509.pem閿涘鈧倷绗夐崣顖滄暏閺冩儼绻戦崶?null閵?/summary>
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
                var cert = X509CertificateLoader.LoadPkcs12(File.ReadAllBytes(keyPath), (string?)null,
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
            // 婢舵牠鍎寸€靛棝鎸滈崝鐘烘祰婢惰精瑙﹂弮璺烘礀闁偓閸掓澘鍞寸純顔界ゴ鐠囨洝鐦夋稊?
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

    /// <summary>鐠囪褰?PEM閿?-----BEGIN PRIVATE KEY-----"閿涘鍨?DER 閺嶇厧绱￠惃?PKCS#8 缁変線鎸滈妴?/summary>
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

    /// <summary>閻㈢喐鍨氶懛顏嗩劮閸氬秵绁寸拠鏇＄槈娑旓讣绱?048-bit RSA閿涘HA256閿?0 楠炲瓨婀侀弫鍫熸埂閿涘鈧?/summary>
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

        // 鐎电厧鍤稉鍝勭敨缁変線鎸滈惃?PFX 閸愬秹鍣搁弬鏉款嚤閸忋儻绱濈涵顔荤箽缁変線鎸滈崣顖滄暏娴滃海顒烽崥?
        var pfx = cert.Export(X509ContentType.Pfx, "rommaker");
        return X509CertificateLoader.LoadPkcs12(pfx, "rommaker", X509KeyStorageFlags.Exportable);
    }
}
