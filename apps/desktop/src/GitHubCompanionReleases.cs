using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using StardewAI.Companion.Updates;

namespace StardewAI.Companion.Desktop;

internal sealed record PublishedUpdate(string Tag, string Notes, string Channel, string ArchiveName,
    Uri Archive, Uri Checksum, Uri Signature, string? AssetDigest, DateTimeOffset PublishedAt, DateTimeOffset UploadedAt)
{
    internal bool CanOfferTo(ReleaseInfo installed)
    {
        if (Tag == installed.Tag) return false;
        if (Channel != installed.Channel) return true;

        var candidate = BaseVersion(Tag);
        var current = BaseVersion(installed.Tag);
        if (candidate is not null && current is not null && candidate != current) return candidate > current;
        return installed.BuiltAt is null || UploadedAt >= installed.BuiltAt;
    }

    private static Version? BaseVersion(string tag)
    {
        string value = tag.TrimStart('v').Split('-', '+')[0];
        return Version.TryParse(value, out var version) && version.Build >= 0 ? version : null;
    }
}

internal sealed class GitHubCompanionReleases(HttpClient http)
{
    private const string Repository = "nekoimi/stardew-game-companion";
    internal static readonly GitHubCompanionReleases Official = new(new HttpClient { Timeout = TimeSpan.FromSeconds(30) });

    internal async Task<PublishedUpdate?> FindAsync(bool preview, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        token = deadline.Token;
        PublishedUpdate? latest = null;
        const int pageSize = 30;
        for (int page = 1; page <= 10; page++)
        {
            using var response = await GetAsync(new Uri(
                $"https://api.github.com/repos/{Repository}/releases?per_page={pageSize}&page={page}"), token);
            using var body = await response.Content.ReadAsStreamAsync(token);
            using var json = await JsonDocument.ParseAsync(body, cancellationToken: token);
            foreach (var release in json.RootElement.EnumerateArray())
            {
                if (release.GetProperty("draft").GetBoolean() || release.GetProperty("prerelease").GetBoolean() != preview)
                    continue;
                var candidate = ReadUpdate(release, preview);
                if (candidate is not null && (latest is null || candidate.PublishedAt > latest.PublishedAt)) latest = candidate;
            }
            if (latest is not null || json.RootElement.GetArrayLength() < pageSize) break;
        }
        return latest;
    }

    internal async Task<ReleaseInfo> DownloadAsync(PublishedUpdate release, string stage, string payload, byte[] publisherKey,
        IProgress<int?> progress, CancellationToken token)
    {
        Directory.CreateDirectory(stage);
        string checksum = UpdatePackage.SafePath(stage, "package.sha256");
        string archive = UpdatePackage.SafePath(stage, "package.zip");
        string signature = UpdatePackage.SafePath(stage, "package.sig");
        await DownloadFileAsync(release.Checksum, checksum, 4096, null, token);
        string hash = UpdatePackage.ReadChecksum(checksum, release.ArchiveName);
        if (release.AssetDigest is { } digest && !digest.Equals($"sha256:{hash}", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("GitHub 资产摘要与 SHA256 校验文件不一致。");
        await DownloadFileAsync(release.Signature, signature, UpdateSignature.Length, null, token);
        UpdateSignature.VerifyFile(signature, hash, publisherKey);
        await DownloadFileAsync(release.Archive, archive, UpdatePackage.MaxArchiveBytes, progress, token);
        progress.Report(null);
        var verified = await Task.Run(() => UpdatePackage.Extract(archive, hash, payload, token), token);
        if (verified.Tag != release.Tag || verified.Channel != release.Channel)
            throw new InvalidDataException("下载包的发布标签或通道与 GitHub Release 不匹配。");
        File.Delete(archive);
        File.Delete(checksum);
        File.Delete(signature);
        return verified;
    }

    private async Task DownloadFileAsync(Uri uri, string destination, long limit, IProgress<int?>? progress,
        CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(progress is null ? TimeSpan.FromSeconds(30) : TimeSpan.FromMinutes(15));
        token = deadline.Token;
        using var response = await GetAsync(uri, token);
        var size = response.Content.Headers.ContentLength;
        if (size > limit) throw new InvalidDataException("发行文件超出大小限制。");
        using var source = await response.Content.ReadAsStreamAsync(token);
        await using var target = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
        var buffer = new byte[64 * 1024];
        long total = 0;
        int? previousPercent = -1;
        for (int read; (read = await source.ReadAsync(buffer, token)) != 0;)
        {
            total += read;
            if (total > limit) throw new InvalidDataException("发行文件超出大小限制。");
            await target.WriteAsync(buffer.AsMemory(0, read), token);
            int? percent = size > 0 ? (int)Math.Min(100, 100 * total / size.Value) : null;
            if (previousPercent == percent) continue;
            previousPercent = percent;
            progress?.Report(percent);
        }
        if (size.HasValue && total != size) throw new IOException("发行文件下载不完整，请重试。");
    }

    private async Task<HttpResponseMessage> GetAsync(Uri uri, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("StardewGameCompanion-Updates/1");
        var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        if (response.IsSuccessStatusCode) return response;
        var status = response.StatusCode;
        response.Dispose();
        throw new HttpRequestException(status is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests
            ? "GitHub 暂时限制请求，请稍后再检查。" : $"GitHub 请求失败（{(int)status}）。");
    }

    private static PublishedUpdate? ReadUpdate(JsonElement release, bool preview)
    {
        string? tag = release.GetProperty("tag_name").GetString();
        if (string.IsNullOrWhiteSpace(tag) || !release.GetProperty("published_at").TryGetDateTimeOffset(out var date))
            return null;
        var assets = release.GetProperty("assets").EnumerateArray().ToArray();
        foreach (var asset in assets)
        {
            string? name = asset.GetProperty("name").GetString();
            if (name is null || !name.StartsWith("StardewGameCompanion-", StringComparison.Ordinal)
                || !name.EndsWith("-windows-x64.zip", StringComparison.Ordinal)) continue;
            var archive = AssetUrl(asset, tag, name);
            if (archive is null) continue;
            if (FindAsset(assets, tag, name + ".sha256") is not { } checksumUrl
                || FindAsset(assets, tag, name + UpdateSignature.Suffix) is not { } signatureUrl) continue;
            string? digest = asset.TryGetProperty("digest", out var value) ? value.GetString() : null;
            string notes = release.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "";
            var uploaded = asset.TryGetProperty("created_at", out var created)
                && created.TryGetDateTimeOffset(out var timestamp) ? timestamp : date;
            return new(tag, notes[..Math.Min(notes.Length, 6000)], preview ? "preview" : "stable", name,
                archive, checksumUrl, signatureUrl, digest, date, uploaded);
        }
        return null;
    }

    private static Uri? FindAsset(JsonElement[] assets, string tag, string name) =>
        assets.Where(asset => asset.GetProperty("name").GetString() == name)
            .Select(asset => AssetUrl(asset, tag, name)).FirstOrDefault(uri => uri is not null);

    private static Uri? AssetUrl(JsonElement asset, string tag, string name)
    {
        var expected = new Uri($"https://github.com/{Repository}/releases/download/{Uri.EscapeDataString(tag)}/{Uri.EscapeDataString(name)}");
        return Uri.TryCreate(asset.GetProperty("browser_download_url").GetString(), UriKind.Absolute, out var url)
            && url == expected ? url : null;
    }
}
