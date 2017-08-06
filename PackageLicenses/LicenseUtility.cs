using Newtonsoft.Json.Linq;
using NuGet.Common;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace PackageLicenses
{
    public static class LicenseUtility
    {
        private static Dictionary<string, License> _licenses; // key: SPDX id
        private static Dictionary<string, License> _lowerCaseKeyLicenses; // key: lower case SPDX id
        private static Dictionary<string, License> _urlLicenses; // key: license URL
        private static Dictionary<string, License> _caches = new Dictionary<string, License>();

        private const string UserAgent = "PackageLicenses.LicenseUtility";

        /// <summary>
        /// GitHub Client ID
        /// </summary>
        public static string ClientId { get; set; }

        /// <summary>
        /// GitHub Client Secret
        /// </summary>
        public static string ClientSecret { get; set; }

        static LicenseUtility()
        {
            var assembly = typeof(LicenseUtility).GetTypeInfo().Assembly;
            var stream = assembly.GetManifestResourceStream("PackageLicenses.licenses.json");
            var reader = new StreamReader(stream);
            var json = reader.ReadToEnd();
            LoadLicenses(json);
        }

        public static async Task<IEnumerable<License>> GetLicencesAsync(ILogger log = null)
        {
            var url = "https://spdx.org/licenses/licenses.json";
            try
            {
                using (var client = new HttpClient())
                {
                    var res = await client.GetAsync(url);
                    if (res.IsSuccessStatusCode)
                    {
                        var json = await res.Content.ReadAsStringAsync();
                        LoadLicenses(json);
                    }
                    else
                    {
                        log?.LogError($"Response from '{url}' is not success status code (Reason: {res.ReasonPhrase})");
                    }
                }
            }
            catch (Exception ex)
            {
                log?.LogError($"Error occurred when downloading license.json ({ex.Message})");
            }

            return _licenses?.Values.ToList() ?? new List<License>();
        }

        private static void LoadLicenses(string json)
        {
            var o = JObject.Parse(json);

            // SPDX licenses
            _licenses = o["licenses"].ToDictionary(
                i => (string)i["licenseId"],
                i => new License((string)i["licenseId"], (string)i["name"])
                {
                    IsMaster = true,
                    DownloadUri = Uri.IsWellFormedUriString((string)i["detailsUrl"], UriKind.Absolute) ? new Uri((string)i["detailsUrl"]) : null
                });
            _lowerCaseKeyLicenses = _licenses.ToDictionary(i => i.Key.ToLower(), i => i.Value);

            // known license URLs
            _urlLicenses = new Dictionary<string, License>();
            var duplicatedUrls = new List<string>();

            foreach (var license in o["licenses"])
            {
                foreach (var seeAlso in license["seeAlso"].ToList())
                {
                    var url = (string)seeAlso;

                    if (string.IsNullOrWhiteSpace(url) ||
                        !Uri.IsWellFormedUriString(url, UriKind.Absolute)) continue;

                    if (_urlLicenses.ContainsKey(url))
                    {
                        duplicatedUrls.Add(url);
                        continue;
                    }
                    var id = (string)license["licenseId"];
                    _urlLicenses.Add(url, _licenses[id]);
                }
            }
            foreach (var key in duplicatedUrls)
                _urlLicenses.Remove(key);
        }

        public static void ClearCaches()
        {
            _licenses?.Clear();
            _lowerCaseKeyLicenses?.Clear();
            _urlLicenses?.Clear();
            _caches.Clear();

            _licenses = null;
            _lowerCaseKeyLicenses = null;
            _urlLicenses = null;
        }

        public static async Task FillTextAsync(this License license, ILogger log = null)
        {
            if (license?.DownloadUri == null) return;

            try
            {
                using (var client = new HttpClient())
                {
                    client.DefaultRequestHeaders.Add("User-Agent", UserAgent);

                    var res = await client.GetAsync(license.DownloadUri);
                    if (!res.IsSuccessStatusCode)
                    {
                        log?.LogWarning($"Response from '{license.DownloadUri}' is not success status code (Reason: {res.ReasonPhrase})");
                        return;
                    }
                    var json = await res.Content.ReadAsStringAsync();
                    var o = JObject.Parse(json);

                    license.Text = (string)o["licenseText"];
                }
            }
            catch (Exception ex)
            {
                log?.LogWarning($"Error occurred when downloading {license?.Id} text ({ex.Message})");
            }
        }

        public static async Task<License> GetLicenseAsync(this Uri uri, ILogger log = null)
        {
            if (_licenses == null)
            {
                var results = await GetLicencesAsync(log);
                if (!results.Any()) return null;
            }

            // known license url
            if (_urlLicenses.ContainsKey($"{uri}"))
            {
                var license = _urlLicenses[$"{uri}"];
                if (license.Text == null)
                    await license.FillTextAsync(log);

                return license.Clone();
            }

            // opensource.org url
            if (uri.Host == "opensource.org")
            {
                var m = Regex.Match(uri.AbsolutePath.ToLower(), @"^/licenses/(?<id>.*?)(\.html)?/?$", RegexOptions.IgnoreCase);
                if (m.Success)
                {
                    var id = m.Groups["id"].Value;
                    if (!_lowerCaseKeyLicenses.ContainsKey(id))
                    {
                        log?.LogWarning("Unknown ID ({id})");
                        return null;
                    }
                    var license = _lowerCaseKeyLicenses[id];
                    if (license.Text == null)
                        await license.FillTextAsync(log);

                    return license.Clone();
                }
            }
            else
            {
                return await GetFromGithubAsync(uri, log);
            }
            return null;
        }

        private static async Task<License> GetFromGithubAsync(Uri uri, ILogger log = null)
        {
            if (_caches.ContainsKey($"{uri}"))
                return _caches[$"{uri}"];

            string owner;
            string repo;
            Uri downloadUri = null;

            if (uri.Host == "raw.github.com") // old format
                uri = new Uri(uri.ToString().Replace("raw.github.com", "raw.githubusercontent.com"));

            if (uri.Host == "raw.githubusercontent.com")
            {
                var dirs = uri.AbsolutePath.Split('/');
                if (dirs.Length < 2)
                    return null;

                owner = dirs[1];
                repo = dirs[2];
                downloadUri = uri;
            }
            else if (uri.Host == "github.com")
            {
                var dirs = uri.AbsolutePath.Split('/');
                if (dirs.Length < 2)
                    return null;

                owner = dirs[1];
                repo = dirs[2];

                if (dirs.Length > 3) // Path: '/{owner}/{repo}/blob/{branch}/{file}'
                    downloadUri = uri;
            }
            else
            {
                return null;
            }

            var failed = false;

            // GitHub API access
            var json = "{}";

            try
            {
                using (var client = new HttpClient())
                {
                    client.DefaultRequestHeaders.Add("User-Agent", UserAgent);

                    var url = $"https://api.github.com/repos/{owner}/{repo}/license";
                    if (!string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret))
                    {
                        url += $"?client_id={ClientId}&client_secret={ClientSecret}";
                    }
                    var res = await client.GetAsync(url);

                    if (res.Headers.TryGetValues("X-RateLimit-Remaining", out var remainingValues) &&
                        int.TryParse(remainingValues.First(), out var remaining))
                    {
                        if (remaining == 0)
                        {
                            if (res.Headers.TryGetValues("X-RateLimit-Reset", out var resetValues) &&
                                int.TryParse(resetValues.First(), out var reset))
                            {
                                var unixTime = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                                var localTime = unixTime.AddSeconds(reset).ToLocalTime();
                                log?.LogError($"GitHub API rate limit exceeded (Reset: {localTime})");
                            }
                        }
                    }

                    if (!res.IsSuccessStatusCode && res.StatusCode != System.Net.HttpStatusCode.NotFound)
                    {
                        log?.LogWarning($"Response from '{url}' is not success status code (Reason: {res.ReasonPhrase})");
                        failed = true;
                    }

                    json = await res.Content.ReadAsStringAsync();
                }
            }
            catch (Exception ex)
            {
                log?.LogWarning($"Error occurred when downloading {owner}/{repo} license ({ex.Message})");
            }

            var o = JObject.Parse(json);

            string id = null;
            var license = o["license"];
            if (license == null)
            {
                // error or notfound
                var message = (string)o["message"];
                if (message != null && message != "Not Found")
                    log?.LogWarning($"GitHub API result message: '{message}'");

                if (downloadUri == null)
                    return null;
            }
            else
            {
                id = (string)license["spdx_id"];
                var html_url = (string)o["html_url"];
                var download_url = (string)o["download_url"];
                var content = Encoding.UTF8.GetString(Convert.FromBase64String((string)o["content"]));

                if (downloadUri == null ||
                    downloadUri.ToString() == html_url ||
                    downloadUri.ToString() == download_url)
                {
                    var l = new License
                    {
                        Id = id,
                        Name = id != null && _licenses.ContainsKey(id) ? _licenses[id].Name : null,
                        Text = content,
                        DownloadUri = download_url != null ? new Uri(download_url) : null
                    };

                    if (!failed)
                        _caches.Add($"{uri}", l);

                    return l;
                }
            }

            // download license file
            try
            {
                var rawUri = downloadUri.ToRawUri();
                if (rawUri == null)
                {
                    return new License
                    {
                        Id = id,
                        Name = id != null && _licenses.ContainsKey(id) ? _licenses[id].Name : null
                    };
                }

                using (var client = new HttpClient())
                {
                    client.DefaultRequestHeaders.Add("User-Agent", UserAgent);

                    var res = await client.GetAsync(rawUri);
                    string content = null;

                    if (res.IsSuccessStatusCode)
                    {
                        content = await res.Content.ReadAsStringAsync();
                    }
                    else
                    {
                        log?.LogWarning($"Response from '{rawUri}' is not success status code (Reason: {res.ReasonPhrase})");
                        failed = true;
                    }

                    var l = new License
                    {
                        Id = id,
                        Name = id != null && _licenses.ContainsKey(id) ? _licenses[id].Name : null,
                        Text = content,
                        DownloadUri = rawUri
                    };

                    if (!failed)
                        _caches.Add($"{uri}", l);

                    return l;
                }
            }
            catch (Exception ex)
            {
                log?.LogWarning($"Error occurred when downloading '{downloadUri}' ({ex.Message})");
            }
            return null;
        }

        private static Uri ToRawUri(this Uri uri)
        {
            if (uri.Host == "raw.githubusercontent.com") return uri;

            var m = Regex.Match(uri.AbsolutePath, @"^/(?<owner>.*?)/(?<repo>.*?)/blob/(?<branch>.*?)/(?<path>.*?)$");
            if (m.Success)
            {
                var owner = m.Groups["owner"];
                var repo = m.Groups["repo"];
                var branch = m.Groups["branch"];
                var path = m.Groups["path"];
                return new Uri($"https://raw.githubusercontent.com/{owner}/{repo}/{branch}/{path}");
            }
            return null;
        }
    }
}
