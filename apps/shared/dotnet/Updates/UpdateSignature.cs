using System.IO;
using System.Reflection;
using System.Security.Cryptography;

namespace StardewAI.Companion.Updates;

internal static class UpdateSignature
{
    internal const string Suffix = ".sig";
    internal const int Length = 64;

    internal static byte[]? TrustedKey()
    {
        using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("UpdatePublisherKey");
        if (resource is null) return null;
        using var key = new MemoryStream();
        resource.CopyTo(key);
        return key.ToArray();
    }

    internal static void VerifyFile(string signaturePath, string archiveHash, byte[] publicKey)
    {
        if (!File.Exists(signaturePath)) throw new FileNotFoundException("请将发行包的 .sig 签名文件放在 ZIP 旁边。");
        using var stream = File.OpenRead(signaturePath);
        if (stream.Length != Length) throw new InvalidDataException("发行包签名文件无效。");
        var signature = new byte[Length];
        stream.ReadExactly(signature);
        Verify(archiveHash, signature, publicKey);
    }

    internal static void Verify(string archiveHash, byte[] signature, byte[] publicKey)
    {
        try
        {
            using var verifier = ECDsa.Create();
            verifier.ImportSubjectPublicKeyInfo(publicKey, out int read);
            if (read == publicKey.Length && verifier.KeySize == 256 && UpdatePackage.ValidHash(archiveHash)
                && signature.Length == Length && verifier.VerifyHash(Convert.FromHexString(archiveHash), signature,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
                return;
        }
        catch (CryptographicException) { }
        throw new InvalidDataException("发行包未通过发布者签名校验。");
    }
}
