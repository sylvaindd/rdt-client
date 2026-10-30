using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web;
using System.Xml;
using System.Xml.Linq;

namespace RdtClient.Service.Helpers;

public record DirectDownloadStandIn(String PostUrl, String Title, String? Source);

public record DirectDownloadPart(String Link, IList<String> Mirrors);

public static partial class DirectDownloadHelper
{
    public const String MetaPost = "x-ddl-post";
    public const String MetaSource = "x-ddl-source";

    public const String HostPreferenceEnvVar = "DDL_HOST_PREFERENCE";
    public const String DefaultHostPreference = "rapidgator,nitroflare,ddownload,1fichier,katfile,fikper";

    private static readonly Dictionary<String, String> HostAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["rg"] = "rapidgator",
        ["nitro"] = "nitroflare",
        ["ddl"] = "ddownload"
    };

    private static readonly HttpClient WebhookClient = new()
    {
        Timeout = TimeSpan.FromSeconds(10)
    };

    [GeneratedRegex(@"https?://[^\s""'<>]+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlRegex();

    [GeneratedRegex(@"\.part0*(\d+)\.rar$", RegexOptions.IgnoreCase)]
    private static partial Regex RarPartRegex();

    [GeneratedRegex(@"\.(r\d{2,3}|z\d{2})$", RegexOptions.IgnoreCase)]
    private static partial Regex OldStyleVolumeRegex();

    [GeneratedRegex(@"\.html?$", RegexOptions.IgnoreCase)]
    private static partial Regex HtmlSuffixRegex();

    /// <summary>
    ///     Reads a placeholder NZB produced by a direct-download indexer bridge. It carries the release post URL
    ///     in its head meta instead of usenet segments. Returns null for regular NZBs.
    /// </summary>
    public static DirectDownloadStandIn? ParseStandIn(Byte[] nzb)
    {
        XDocument doc;

        try
        {
            using var stream = new MemoryStream(nzb);
            using var reader = XmlReader.Create(stream, new() { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null });
            doc = XDocument.Load(reader);
        }
        catch (XmlException)
        {
            return null;
        }

        var ns = doc.Root?.GetDefaultNamespace() ?? XNamespace.None;
        var meta = doc.Root?.Elements(ns + "head").Elements(ns + "meta").ToList() ?? [];

        String? Meta(String type)
        {
            return meta.FirstOrDefault(m => m.Attribute("type")?.Value == type)?.Value.Trim();
        }

        var post = Meta(MetaPost);

        if (!Uri.TryCreate(post, UriKind.Absolute, out var postUri) || (postUri.Scheme != Uri.UriSchemeHttp && postUri.Scheme != Uri.UriSchemeHttps))
        {
            return null;
        }

        var title = Meta("name");

        if (String.IsNullOrWhiteSpace(title))
        {
            title = postUri.Segments.Last().Trim('/');
        }

        return new(postUri.ToString(), title, Meta(MetaSource));
    }

    public static String Hash(String postUrl)
    {
        var bytes = SHA1.HashData(Encoding.UTF8.GetBytes(postUrl.Trim()));

        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>
    ///     Extracts the hoster links from pasted text and keeps a single mirror: release posts usually list the same
    ///     archive parts on several hosts, and downloading every mirror would fetch each part more than once.
    /// </summary>
    public static IList<String> PickMirror(String text, String? hostPreference = null)
    {
        return PlanParts(text, hostPreference).Select(p => p.Link).ToList();
    }

    /// <summary>
    ///     Same pick as <see cref="PickMirror" />, and for each picked link the same file on the other hosts, in preference
    ///     order, so a part the debrid service refuses can be fetched from another mirror. Files are matched by the name in
    ///     the hoster URL; a link without a file name gets no mirrors.
    /// </summary>
    public static IList<DirectDownloadPart> PlanParts(String text, String? hostPreference = null)
    {
        var preferred = HostPreference(hostPreference);

        var links = UrlRegex().Matches(text)
                              .Select(m => m.Value.TrimEnd('.', ',', ')', ']', ';'))
                              .Where(m => Uri.TryCreate(m, UriKind.Absolute, out _))
                              .Distinct()
                              .ToList();

        if (links.Count == 0)
        {
            return [];
        }

        var hosts = links.GroupBy(m => HostKey(new Uri(m).Host))
                         .OrderByDescending(g => g.Count())
                         .ThenBy(g => PreferenceIndex(preferred, g.Key))
                         .ToList();

        var others = hosts.Skip(1).SelectMany(g => g).ToList();

        return hosts[0].Select(link =>
                       {
                           var key = MirrorKey(link);

                           var mirrors = key == null ? [] : others.Where(m => MirrorKey(m) == key).ToList();

                           return new DirectDownloadPart(link, mirrors);
                       })
                       .ToList();
    }

    /// <summary>
    ///     The file name a hoster link points to, comparable across hosts: Rapidgator appends ".html", Nitroflare does not.
    /// </summary>
    public static String? MirrorKey(String link)
    {
        var name = HttpUtility.UrlDecode(new Uri(link).Segments.Last()).Trim('/');

        name = HtmlSuffixRegex().Replace(name, "");

        return name.Contains('.') ? name.ToLowerInvariant() : null;
    }

    public static Boolean IsSecondaryArchiveVolume(String? fileName)
    {
        if (String.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        var part = RarPartRegex().Match(fileName);

        if (part.Success)
        {
            return Int32.Parse(part.Groups[1].Value) > 1;
        }

        return OldStyleVolumeRegex().IsMatch(fileName);
    }

    public static String FileNameFromLink(String link)
    {
        var name = HttpUtility.UrlDecode(new Uri(link).Segments.Last());

        return FileHelper.RemoveInvalidFileNameChars(name);
    }

    public static async Task NotifyWaiting(String? webhookUrl, String title, String postUrl)
    {
        if (String.IsNullOrWhiteSpace(webhookUrl))
        {
            return;
        }

        var payload = new
        {
            content = $"**Links needed:** {title}\n{postUrl}"
        };

        using var response = await WebhookClient.PostAsJsonAsync(webhookUrl, payload);
        response.EnsureSuccessStatusCode();
    }

    private static String HostKey(String host)
    {
        var labels = host.ToLowerInvariant().Split('.', StringSplitOptions.RemoveEmptyEntries);
        var name = labels.Length >= 2 ? labels[^2] : host.ToLowerInvariant();

        return HostAliases.GetValueOrDefault(name, name);
    }

    /// <summary>
    ///     Resolves the host tie-break order: the UI setting, else the DDL_HOST_PREFERENCE environment variable, else the
    ///     default. Entries may be names, domains or aliases ("nitroflare", "rapidgator.net", "rg.to").
    /// </summary>
    public static IList<String> HostPreference(String? setting)
    {
        var value = new[] { setting, Environment.GetEnvironmentVariable(HostPreferenceEnvVar) }.FirstOrDefault(v => !String.IsNullOrWhiteSpace(v))
                    ?? DefaultHostPreference;

        return value.Split([',', ';', ' ', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(HostKey)
                    .Distinct()
                    .ToList();
    }

    private static Int32 PreferenceIndex(IList<String> preferred, String hostKey)
    {
        var index = preferred.IndexOf(hostKey);

        return index < 0 ? preferred.Count : index;
    }
}
