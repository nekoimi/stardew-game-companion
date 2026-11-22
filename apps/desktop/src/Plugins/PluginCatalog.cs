using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace StardewAI.Companion.Desktop.Plugins;

internal sealed record CatalogSource(string Url, string PublicKey);
internal sealed record CatalogEnvelope(string Payload, string Signature);
internal sealed record CatalogEntry(string Id, string Name, string Publisher, string Version,
    PluginProtocol HostProtocol, string DownloadUrl, string Sha256, string PackageSignature, int DataVersion)
{
    public bool Compatible => HostProtocol.Min <= PluginPackages.ProtocolVersion && HostProtocol.Max >= PluginPackages.ProtocolVersion;
    public string Compatibility => Compatible ? "兼容" : "协议不兼容";
}
internal sealed record CatalogPublisher(string Name, string PublicKey);
internal sealed record CatalogIndex(int CatalogVersion, DateTimeOffset ExpiresAt, CatalogPublisher[] Publishers, CatalogEntry[] Plugins);

internal sealed class PluginCatalog(HttpClient http)
{
    private const int MaxCatalogBytes = 2 * 1024 * 1024;
    private const int MaxPackageBytes = 512 * 1024 * 1024;

    internal async Task<CatalogIndex> ReadAsync(CatalogSource source, CancellationToken token)
    {
        var uri = SecureUri(source.Url);
        using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var bytes = new MemoryStream();
        await CopyBoundedAsync(stream, bytes, MaxCatalogBytes, token).ConfigureAwait(false);
        var envelope = JsonSerializer.Deserialize<CatalogEnvelope>(bytes.ToArray(), PluginPackages.Json)
            ?? throw new InvalidDataException("插件目录格式无效。");
        var payload = Decode(envelope.Payload);
        if (!Verify(source.PublicKey, payload, envelope.Signature)) throw new InvalidDataException("插件目录签名校验失败。");
        var index = JsonSerializer.Deserialize<CatalogIndex>(payload, PluginPackages.Json)
            ?? throw new InvalidDataException("插件目录内容无效。");
        if (index.CatalogVersion != 1 || index.ExpiresAt <= DateTimeOffset.UtcNow
            || index.Publishers is not { Length: > 0 and <= 256 } || index.Plugins is not { Length: <= 2048 })
            throw new InvalidDataException("插件目录版本无效或已过期。");
        var publishers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var publisher in index.Publishers)
        {
            if (publisher is null || string.IsNullOrWhiteSpace(publisher.Name) || publisher.Name.Length > 120
                || !publishers.Add(publisher.Name))
                throw new InvalidDataException("插件发布者无效或重复。");
            using var _ = ImportKey(publisher.PublicKey);
        }
        var packages = new HashSet<(string, string)>();
        foreach (var plugin in index.Plugins)
        {
            if (plugin is null || !PluginPackages.ValidId(plugin.Id) || !PluginPackages.ValidVersion(plugin.Version)
                || string.IsNullOrWhiteSpace(plugin.Name) || plugin.Name.Length > 80 || !publishers.Contains(plugin.Publisher)
                || plugin.HostProtocol is not { Min: >= 1 } protocol || protocol.Max < protocol.Min || plugin.DataVersion < 1
                || plugin.Sha256 is not { Length: 64 } || !plugin.Sha256.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f')
                || !packages.Add((plugin.Id, plugin.Version)))
                throw new InvalidDataException("插件目录条目无效或重复。");
            SecureUri(plugin.DownloadUrl);
        }
        return index;
    }

    internal async Task DownloadAsync(CatalogIndex index, CatalogEntry entry, string file, CancellationToken token)
    {
        if (!index.Plugins.Contains(entry) || index.ExpiresAt <= DateTimeOffset.UtcNow || !entry.Compatible)
            throw new InvalidDataException("此插件版本已过期或与当前宿主不兼容。");
        var publisher = index.Publishers.Single(item => item.Name == entry.Publisher);
        using var response = await http.GetAsync(SecureUri(entry.DownloadUrl), HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaxPackageBytes) throw new InvalidDataException("插件包过大。");
        await using (var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
        await using (var output = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            await CopyBoundedAsync(stream, output, MaxPackageBytes, token).ConfigureAwait(false);
        await using var content = File.OpenRead(file);
        var hash = await SHA256.HashDataAsync(content, token).ConfigureAwait(false);
        if (!Convert.ToHexStringLower(hash).Equals(entry.Sha256, StringComparison.Ordinal)
            || !Verify(publisher.PublicKey, hash, entry.PackageSignature))
            throw new InvalidDataException("插件包哈希或发布者签名校验失败。");
    }

    private static bool Verify(string publicKey, byte[] data, string signature)
    {
        using var key = ImportKey(publicKey);
        return key.VerifyData(data, Decode(signature), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
    }

    private static ECDsa ImportKey(string publicKey)
    {
        var bytes = Decode(publicKey);
        var key = ECDsa.Create();
        try
        {
            key.ImportSubjectPublicKeyInfo(bytes, out int count);
            if (count != bytes.Length || key.ExportParameters(false).Curve.Oid.Value != ECCurve.NamedCurves.nistP256.Oid.Value)
                throw new CryptographicException();
            return key;
        }
        catch (CryptographicException)
        {
            key.Dispose();
            throw new InvalidDataException("插件目录公钥须为 P-256 的 SPKI 公钥。");
        }
    }

    private static byte[] Decode(string? value)
    {
        try { return Convert.FromBase64String(value ?? ""); }
        catch (FormatException) { throw new InvalidDataException("插件目录签名或内容的编码无效。"); }
    }

    private static Uri SecureUri(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0)
            throw new InvalidDataException("插件目录及下载地址须为 HTTPS。");
        return uri;
    }

    private static async Task CopyBoundedAsync(Stream source, Stream target, long maximum, CancellationToken token)
    {
        byte[] buffer = new byte[81920];
        long total = 0;
        int size;
        while ((size = await source.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
        {
            total += size;
            if (total > maximum) throw new InvalidDataException("插件目录或下载文件超出大小限制。");
            await target.WriteAsync(buffer.AsMemory(0, size), token).ConfigureAwait(false);
        }
    }
}
