using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using StardewAI.Companion.Updates;

namespace StardewAI.Companion.Desktop;

internal sealed record PublishedUpdate(string Tag, string? Notes, string Channel, string Url, string Sha256,
    DateTimeOffset PublishedAt)
{
    internal bool CanOfferTo(ReleaseInfo installed)
    {
        if (Tag == installed.Tag) return false;
        if (Channel != installed.Channel) return true;

        var candidate = BaseVersion(Tag);
        var current = BaseVersion(installed.Tag);
        if (candidate is not null && current is not null && candidate != current) return candidate > current;
        return installed.BuiltAt is null || PublishedAt >= installed.BuiltAt;
    }

    private static Version? BaseVersion(string tag)
    {
        string value = tag.TrimStart('v').Split('-', '+')[0];
        return Version.TryParse(value, out var version) && version.Build >= 0 ? version : null;
    }
}

internal sealed class CompanionUpdateCatalog(HttpClient http)
{
    internal const string DefaultUrl = "https://nekoimi.github.io/stardew-game-companion/updates/windows-x64.json";
    internal static readonly CompanionUpdateCatalog Client = new(new HttpClient { MaxResponseContentBufferSize = 8 * 1024 * 1024 });
    private sealed record Catalog(int SchemaVersion, PublishedUpdate[] Releases);

    internal static bool TryHttpsAddress(string? address, out Uri uri)
    {
        if (address is { Length: > 0 and <= 2048 } && Uri.TryCreate(address, UriKind.Absolute, out var parsed)
            && parsed.Scheme == Uri.UriSchemeHttps && parsed.Host.Length != 0
            && parsed.UserInfo.Length == 0 && parsed.Fragment.Length == 0)
        {
            uri = parsed;
            return true;
        }
        uri = null!;
        return false;
    }

    internal async Task<PublishedUpdate?> FindAsync(Uri source, bool preview, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        token = deadline.Token;
        using var response = await GetAsync(source, HttpCompletionOption.ResponseContentRead, token);
        using var body = await response.Content.ReadAsStreamAsync(token);
        var catalog = await JsonSerializer.DeserializeAsync<Catalog>(body, JsonSerializerOptions.Web, token);
        if (catalog is not { SchemaVersion: 1, Releases: not null })
            throw new InvalidDataException("更新目录格式无效，当前仅支持 schemaVersion 1。");
        var tags = new HashSet<string>(StringComparer.Ordinal);
        foreach (var release in catalog.Releases)
        {
            if (release is null || release.Tag is not { Length: > 0 and <= 200 }
                || string.IsNullOrWhiteSpace(release.Tag) || release.Tag.Any(char.IsControl) || !tags.Add(release.Tag)
                || release.Channel is not ("stable" or "preview") || release.PublishedAt == default
                || !TryHttpsAddress(release.Url, out _) || !UpdatePackage.ValidHash(release.Sha256))
                throw new InvalidDataException("更新目录包含无效或重复的发行记录。");
        }
        return catalog.Releases.Where(release => release.Channel == (preview ? "preview" : "stable"))
            .MaxBy(release => release.PublishedAt);
    }

    internal async Task<ReleaseInfo> DownloadAsync(PublishedUpdate release, string stage, string payload,
        Action<UpdateProgress> report, CancellationToken token)
    {
        Directory.CreateDirectory(stage);
        string archive = UpdatePackage.SafePath(stage, "package.zip");
        await DownloadFileAsync(new Uri(release.Url), archive, report, token);
        var verified = await Task.Run(() => UpdatePackage.Extract(archive, release.Sha256, payload, token, report), token);
        if (verified.Tag != release.Tag || verified.Channel != release.Channel)
            throw new InvalidDataException("下载包的发布标签或通道与更新目录不匹配。");
        File.Delete(archive);
        return verified;
    }

    private async Task DownloadFileAsync(Uri uri, string destination, Action<UpdateProgress> report,
        CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(15));
        token = deadline.Token;
        using var response = await GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token);
        var size = response.Content.Headers.ContentLength;
        if (size > UpdatePackage.MaxArchiveBytes) throw new InvalidDataException("发行文件超出大小限制。");
        using var source = await response.Content.ReadAsStreamAsync(token);
        await using var target = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
        var buffer = new byte[64 * 1024];
        long total = 0;
        int? previousPercent = -1;
        for (int read; (read = await source.ReadAsync(buffer, token)) != 0;)
        {
            total += read;
            if (total > UpdatePackage.MaxArchiveBytes) throw new InvalidDataException("发行文件超出大小限制。");
            await target.WriteAsync(buffer.AsMemory(0, read), token);
            int? percent = size > 0 ? (int)Math.Min(100, 100 * total / size.Value) : null;
            if (previousPercent == percent) continue;
            previousPercent = percent;
            report(new("正在下载更新…", percent));
        }
        if (size.HasValue && total != size) throw new IOException("发行文件下载不完整，请重试。");
    }

    private async Task<HttpResponseMessage> GetAsync(Uri uri, HttpCompletionOption completion, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("StardewGameCompanion-Updates/1");
        if (completion == HttpCompletionOption.ResponseContentRead)
            request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        var response = await http.SendAsync(request, completion, token);
        if (response.IsSuccessStatusCode) return response;
        var status = response.StatusCode;
        response.Dispose();
        throw new HttpRequestException(status is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests
            ? "更新服务器暂时限制请求，请稍后重试。" : $"更新服务器请求失败（HTTP {(int)status}）。");
    }
}
