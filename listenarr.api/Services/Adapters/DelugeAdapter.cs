using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Listenarr.Api.Services;
using Listenarr.Domain.Models;
using Microsoft.Extensions.Logging;

namespace Listenarr.Api.Services.Adapters
{
    public class DelugeAdapter : IDownloadClientAdapter
    {
        public string ClientId => "deluge";
        public string ClientType => "deluge";
        public DownloadProtocol Protocol => DownloadProtocol.Torrent;

        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IRemotePathMappingService _pathMappingService;
        private readonly ITorrentFileDownloader _torrentFileDownloader;
        private readonly ILogger<DelugeAdapter> _logger;

        private static int _rpcIdCounter = 0;

        public DelugeAdapter(
            IHttpClientFactory httpClientFactory,
            IRemotePathMappingService pathMappingService,
            ITorrentFileDownloader torrentFileDownloader,
            ILogger<DelugeAdapter> logger)
        {
            _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
            _pathMappingService = pathMappingService ?? throw new ArgumentNullException(nameof(pathMappingService));
            _torrentFileDownloader = torrentFileDownloader ?? throw new ArgumentNullException(nameof(torrentFileDownloader));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<(bool Success, string Message)> TestConnectionAsync(DownloadClientConfiguration client, CancellationToken ct = default)
        {
            try
            {
                var (cookies, httpClient) = CreateAuthenticatedClient(client);
                using (httpClient)
                {
                    var authed = await AuthenticateAsync(client, cookies, httpClient, ct);
                    if (!authed)
                    {
                        return (false, "Deluge: authentication failed (check password)");
                    }

                    // Call a simple method to verify the connection works
                    var response = await InvokeRpcAsync(client, httpClient, cookies, "core.get_torrents_status", new object[] { new { }, new string[0] }, ct);

                    if (response.TryGetProperty("error", out var errorProp) && errorProp.ValueKind != JsonValueKind.Null)
                    {
                        var errorMsg = errorProp.TryGetProperty("message", out var msgProp) ? msgProp.GetString() : "unknown RPC error";
                        return (false, $"Deluge: RPC error ({errorMsg})");
                    }

                    return (true, "Deluge: connected");
                }
            }
            catch (HttpRequestException httpEx) when (httpEx.StatusCode == HttpStatusCode.Unauthorized || httpEx.StatusCode == HttpStatusCode.Forbidden)
            {
                _logger.LogDebug(httpEx, "Deluge authentication failed for client {ClientId}", LogRedaction.SanitizeText(client?.Id ?? client?.Name ?? client?.Type));
                return (false, "Deluge: authentication failed (check password)");
            }
            catch (HttpRequestException httpEx)
            {
                _logger.LogDebug(httpEx, "Deluge test failed for client {ClientId}", LogRedaction.SanitizeText(client?.Id ?? client?.Name ?? client?.Type));
                return (false, $"Deluge: network error ({httpEx.StatusCode?.ToString() ?? "unavailable"})");
            }
            catch (TaskCanceledException tce)
            {
                _logger.LogDebug(tce, "Deluge test timed out for client {ClientId}", LogRedaction.SanitizeText(client?.Id ?? client?.Name ?? client?.Type));
                return (false, "Deluge: connection timed out");
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogDebug(ex, "Deluge test failed for client {ClientId}", LogRedaction.SanitizeText(client?.Id ?? client?.Name ?? client?.Type));
                return (false, "Deluge: connection failed");
            }
        }

        public async Task<string?> AddAsync(DownloadClientConfiguration client, SearchResult result, CancellationToken ct = default)
        {
            if (client == null) throw new ArgumentNullException(nameof(client));
            if (result == null) throw new ArgumentNullException(nameof(result));

            var options = new Dictionary<string, object>
            {
                ["add_paused"] = false
            };

            if (!string.IsNullOrWhiteSpace(client.DownloadPath))
            {
                options["download_location"] = client.DownloadPath;
            }

            if (client.Settings != null && client.Settings.TryGetValue("category", out var categoryObj))
            {
                var category = categoryObj?.ToString();
                if (!string.IsNullOrWhiteSpace(category))
                {
                    options["label"] = category;
                }
            }

            try
            {
                var (cookies, httpClient) = CreateAuthenticatedClient(client);
                using (httpClient)
                {
                    var authed = await AuthenticateAsync(client, cookies, httpClient, ct);
                    if (!authed)
                    {
                        throw new InvalidOperationException("Deluge authentication failed");
                    }

                    // Prefer torrent file content
                    byte[]? torrentFileData = result.TorrentFileContent;
                    var magnetLink = DownloadClientUriBuilder.NormalizeMagnetLink(result.MagnetLink);
                    var httpTorrentUrl = NormalizeTorrentUrl(result.TorrentUrl);

                    _logger.LogDebug("AddAsync entry for '{Title}': TorrentFileContent={HasContent}, MagnetLink={HasMagnet}, TorrentUrl={Url}",
                        LogRedaction.SanitizeText(result.Title),
                        result.TorrentFileContent != null && result.TorrentFileContent.Length > 0 ? $"{result.TorrentFileContent.Length} bytes" : "null",
                        magnetLink.Length > 0 ? "yes" : "no",
                        LogRedaction.SanitizeUrl(httpTorrentUrl ?? string.Empty));

                    // Try pre-downloading torrent file if we have a URL
                    if ((torrentFileData == null || torrentFileData.Length == 0) && !string.IsNullOrEmpty(httpTorrentUrl))
                    {
                        try
                        {
                            var downloadResult = await _torrentFileDownloader.DownloadAsync(httpTorrentUrl, ct);
                            if (downloadResult.HasBytes)
                            {
                                torrentFileData = downloadResult.TorrentBytes;
                                _logger.LogInformation("Pre-downloaded torrent file ({Bytes} bytes) for '{Title}'",
                                    torrentFileData!.Length, LogRedaction.SanitizeText(result.Title));
                            }
                            else if (downloadResult.HasMagnet)
                            {
                                magnetLink = DownloadClientUriBuilder.NormalizeMagnetLink(downloadResult.MagnetUri);
                                _logger.LogInformation("Indexer redirected to magnet link for '{Title}'", LogRedaction.SanitizeText(result.Title));
                            }
                        }
                        catch (Exception ex) when (ex is not OutOfMemoryException && ex is not StackOverflowException)
                        {
                            _logger.LogDebug(ex, "TorrentUrl pre-download failed for '{Title}', will try magnet/URL", LogRedaction.SanitizeText(result.Title));
                        }
                    }

                    JsonElement response;
                    if (torrentFileData != null && torrentFileData.Length > 0)
                    {
                        var base64 = Convert.ToBase64String(torrentFileData);
                        var filename = $"{(result.Title ?? "torrent").Replace("/", "_")}.torrent";
                        response = await InvokeRpcAsync(client, httpClient, cookies, "core.add_torrent_file", new object[] { filename, base64, options }, ct);
                        _logger.LogDebug("Used add_torrent_file for '{Title}'", LogRedaction.SanitizeText(result.Title));
                    }
                    else if (magnetLink.Length > 0)
                    {
                        response = await InvokeRpcAsync(client, httpClient, cookies, "core.add_torrent_magnet", new object[] { magnetLink, options }, ct);
                        _logger.LogDebug("Used add_torrent_magnet for '{Title}'", LogRedaction.SanitizeText(result.Title));
                    }
                    else if (!string.IsNullOrEmpty(httpTorrentUrl))
                    {
                        response = await InvokeRpcAsync(client, httpClient, cookies, "core.add_torrent_url", new object[] { httpTorrentUrl, options }, ct);
                        _logger.LogDebug("Used add_torrent_url for '{Title}'", LogRedaction.SanitizeText(result.Title));
                    }
                    else
                    {
                        throw new ArgumentException("No magnet link, torrent URL, or cached torrent file provided", nameof(result));
                    }

                    if (response.TryGetProperty("error", out var errorProp) && errorProp.ValueKind != JsonValueKind.Null)
                    {
                        var errorMsg = errorProp.TryGetProperty("message", out var msgProp) ? msgProp.GetString() : "unknown RPC error";
                        throw new InvalidOperationException($"Deluge RPC error: {errorMsg}");
                    }

                    if (response.TryGetProperty("result", out var resultProp) && resultProp.ValueKind == JsonValueKind.String)
                    {
                        var hash = resultProp.GetString();
                        if (!string.IsNullOrWhiteSpace(hash))
                        {
                            _logger.LogInformation("Deluge successfully added torrent '{Title}' with hash: {Hash}",
                                LogRedaction.SanitizeText(result.Title), LogRedaction.SanitizeText(hash));
                            return hash.ToUpperInvariant();
                        }
                    }

                    _logger.LogWarning("Deluge AddAsync returning null - torrent may not have been added");
                    return null;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Failed to add torrent to Deluge for client {ClientName}", LogRedaction.SanitizeText(client.Name ?? client.Id));
                throw;
            }
        }

        public async Task<bool> RemoveAsync(DownloadClientConfiguration client, string id, bool deleteFiles = false, CancellationToken ct = default)
        {
            if (client == null) throw new ArgumentNullException(nameof(client));
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentNullException(nameof(id));

            try
            {
                var (cookies, httpClient) = CreateAuthenticatedClient(client);
                using (httpClient)
                {
                    var authed = await AuthenticateAsync(client, cookies, httpClient, ct);
                    if (!authed)
                    {
                        _logger.LogWarning("Deluge authentication failed when removing torrent {Id}", LogRedaction.SanitizeText(id));
                        return false;
                    }

                    var response = await InvokeRpcAsync(client, httpClient, cookies, "core.remove_torrent",
                        new object[] { id.ToLowerInvariant(), deleteFiles }, ct);

                    if (response.TryGetProperty("error", out var errorProp) && errorProp.ValueKind != JsonValueKind.Null)
                    {
                        var errorMsg = errorProp.TryGetProperty("message", out var msgProp) ? msgProp.GetString() : "unknown error";
                        _logger.LogWarning("Deluge failed to remove torrent {Id}: {Message}",
                            LogRedaction.SanitizeText(id), LogRedaction.SanitizeText(errorMsg));
                        return false;
                    }

                    if (response.TryGetProperty("result", out var resultProp) && resultProp.ValueKind == JsonValueKind.True)
                    {
                        _logger.LogInformation("Removed torrent {Id} from Deluge (deleteFiles={DeleteFiles})", LogRedaction.SanitizeText(id), deleteFiles);
                        return true;
                    }

                    _logger.LogWarning("Deluge remove_torrent returned unexpected result for {Id}", LogRedaction.SanitizeText(id));
                    return false;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error removing torrent {Id} from Deluge", LogRedaction.SanitizeText(id));
                return false;
            }
        }

        public async Task<List<QueueItem>> GetQueueAsync(DownloadClientConfiguration client, CancellationToken ct = default)
        {
            var items = new List<QueueItem>();
            if (client == null) return items;

            var configuredCategory = DownloadClientCategoryFilter.GetConfiguredCategory(client);
            var statusKeys = new[]
            {
                "name", "state", "progress", "total_size", "total_done", "download_payload_rate",
                "eta", "save_path", "label", "time_added", "ratio", "is_finished", "seeding_time"
            };

            try
            {
                var (cookies, httpClient) = CreateAuthenticatedClient(client);
                using (httpClient)
                {
                    var authed = await AuthenticateAsync(client, cookies, httpClient, ct);
                    if (!authed) return items;

                    var response = await InvokeRpcAsync(client, httpClient, cookies, "core.get_torrents_status",
                        new object[] { new { }, statusKeys }, ct);

                    if (!response.TryGetProperty("result", out var resultProp) || resultProp.ValueKind != JsonValueKind.Object)
                        return items;

                    foreach (var torrentProp in resultProp.EnumerateObject())
                    {
                        try
                        {
                            var hash = torrentProp.Name;
                            var torrent = torrentProp.Value;
                            var label = torrent.TryGetProperty("label", out var labelProp) ? labelProp.GetString() ?? string.Empty : string.Empty;

                            if (!DownloadClientCategoryFilter.MatchesAny(configuredCategory, new[] { label }))
                                continue;

                            var queueItem = await MapTorrentToQueueItemAsync(client, hash, torrent, ct);
                            items.Add(queueItem);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                        {
                            _logger.LogDebug(ex, "Failed to map Deluge torrent entry (non-fatal)");
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Failed to retrieve Deluge queue for client {ClientName}", LogRedaction.SanitizeText(client.Name ?? client.Id));
            }

            return items;
        }

        public async Task<List<DownloadClientItem>> GetItemsAsync(DownloadClientConfiguration client, CancellationToken ct = default)
        {
            var items = new List<DownloadClientItem>();
            if (client == null) return items;

            var configuredCategory = DownloadClientCategoryFilter.GetConfiguredCategory(client);
            var statusKeys = new[]
            {
                "name", "state", "progress", "total_size", "total_done", "download_payload_rate",
                "eta", "save_path", "label", "time_added", "ratio", "is_finished", "seeding_time"
            };

            var removeCompletedDownloads = client.Settings?.TryGetValue("removeCompletedDownloads", out var removeVal) is true &&
                (removeVal is bool boolVal && boolVal);

            try
            {
                var (cookies, httpClient) = CreateAuthenticatedClient(client);
                using (httpClient)
                {
                    var authed = await AuthenticateAsync(client, cookies, httpClient, ct);
                    if (!authed) return items;

                    var response = await InvokeRpcAsync(client, httpClient, cookies, "core.get_torrents_status",
                        new object[] { new { }, statusKeys }, ct);

                    if (!response.TryGetProperty("result", out var resultProp) || resultProp.ValueKind != JsonValueKind.Object)
                        return items;

                    foreach (var torrentProp in resultProp.EnumerateObject())
                    {
                        try
                        {
                            var hash = torrentProp.Name;
                            var torrent = torrentProp.Value;
                            var label = torrent.TryGetProperty("label", out var labelProp) ? labelProp.GetString() ?? string.Empty : string.Empty;

                            if (!DownloadClientCategoryFilter.MatchesAny(configuredCategory, new[] { label }))
                                continue;

                            var item = await MapTorrentToDownloadClientItemAsync(client, hash, torrent, removeCompletedDownloads, ct);
                            items.Add(item);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                        {
                            _logger.LogDebug(ex, "Failed to map Deluge torrent entry (non-fatal)");
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Failed to retrieve Deluge items for client {ClientName}", LogRedaction.SanitizeText(client.Name ?? client.Id));
            }

            return items;
        }

        public Task<List<(string Id, string Name)>> GetRecentHistoryAsync(DownloadClientConfiguration client, int limit = 100, CancellationToken ct = default)
        {
            // Deluge does not expose a dedicated history endpoint via RPC.
            return Task.FromResult(new List<(string Id, string Name)>());
        }

        public async Task<DownloadClientItem> GetImportItemAsync(
            DownloadClientConfiguration client,
            DownloadClientItem item,
            DownloadClientItem? previousAttempt = null,
            CancellationToken ct = default)
        {
            var result = item.Clone();

            try
            {
                var (cookies, httpClient) = CreateAuthenticatedClient(client);
                using (httpClient)
                {
                    var authed = await AuthenticateAsync(client, cookies, httpClient, ct);
                    if (!authed) return result;

                    var torrentId = item.DownloadId?.ToLowerInvariant() ?? string.Empty;
                    var response = await InvokeRpcAsync(client, httpClient, cookies, "core.get_torrent_status",
                        new object[] { torrentId, new[] { "save_path", "name" } }, ct);

                    if (!response.TryGetProperty("result", out var resultProp) || resultProp.ValueKind != JsonValueKind.Object)
                    {
                        _logger.LogWarning("Failed to query Deluge for torrent {TorrentId}", item.DownloadId);
                        return result;
                    }

                    var savePath = resultProp.TryGetProperty("save_path", out var savePathProp) ? savePathProp.GetString() : null;
                    var name = resultProp.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;

                    if (string.IsNullOrEmpty(savePath) || string.IsNullOrEmpty(name))
                    {
                        _logger.LogWarning("Missing save_path or name for Deluge torrent {TorrentId}", item.DownloadId);
                        return result;
                    }

                    var contentPath = CombinePath(savePath, name);
                    var localContentPath = await _pathMappingService.TranslatePathAsync(client.Id, contentPath);
                    result.OutputPath = localContentPath;

                    _logger.LogDebug("Resolved Deluge content path for {TorrentId}: {ContentPath}", item.DownloadId, localContentPath);
                    return result;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Error resolving import item for Deluge torrent {TorrentId}", item.DownloadId);
                return result;
            }
        }

        public async Task<QueueItem> GetImportItemAsync(
            DownloadClientConfiguration client,
            Download download,
            QueueItem queueItem,
            QueueItem? previousAttempt = null,
            CancellationToken ct = default)
        {
            var result = queueItem.Clone();

            try
            {
                var (cookies, httpClient) = CreateAuthenticatedClient(client);
                using (httpClient)
                {
                    var authed = await AuthenticateAsync(client, cookies, httpClient, ct);
                    if (!authed) return result;

                    var torrentId = queueItem.Id?.ToLowerInvariant() ?? string.Empty;
                    var response = await InvokeRpcAsync(client, httpClient, cookies, "core.get_torrent_status",
                        new object[] { torrentId, new[] { "save_path", "name" } }, ct);

                    if (!response.TryGetProperty("result", out var resultProp) || resultProp.ValueKind != JsonValueKind.Object)
                    {
                        _logger.LogWarning("Failed to query Deluge for torrent {TorrentId}", queueItem.Id);
                        return result;
                    }

                    var savePath = resultProp.TryGetProperty("save_path", out var savePathProp) ? savePathProp.GetString() : null;
                    var name = resultProp.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;

                    if (string.IsNullOrEmpty(savePath) || string.IsNullOrEmpty(name))
                    {
                        _logger.LogWarning("Missing save_path or name for Deluge torrent {TorrentId}", queueItem.Id);
                        return result;
                    }

                    var contentPath = CombinePath(savePath, name);
                    var localContentPath = await _pathMappingService.TranslatePathAsync(client.Id, contentPath);
                    result.ContentPath = localContentPath;

                    _logger.LogDebug("Resolved Deluge content path for {TorrentId}: {ContentPath}", queueItem.Id, localContentPath);
                    return result;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Error resolving import item for Deluge torrent {TorrentId}", queueItem.Id);
                return result;
            }
        }

        // ─── Private helpers ────────────────────────────────────────────────────────

        private (CookieContainer Cookies, HttpClient Client) CreateAuthenticatedClient(DownloadClientConfiguration client)
        {
            var cookies = new CookieContainer();
            var handler = new HttpClientHandler
            {
                CookieContainer = cookies,
                UseCookies = true,
                AutomaticDecompression = DecompressionMethods.All
            };
            var httpClient = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(30)
            };
            return (cookies, httpClient);
        }

        private async Task<bool> AuthenticateAsync(DownloadClientConfiguration client, CookieContainer cookies, HttpClient httpClient, CancellationToken ct)
        {
            var password = client.Password ?? string.Empty;
            var authPayload = new
            {
                method = "auth.login",
                @params = new object[] { password },
                id = System.Threading.Interlocked.Increment(ref _rpcIdCounter)
            };

            var url = DownloadClientUriBuilder.BuildUri(client, "/json").ToString();
            var json = JsonSerializer.Serialize(authPayload);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await httpClient.PostAsync(url, content, ct);
            var body = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("Deluge auth failed with HTTP {StatusCode}", response.StatusCode);
                return false;
            }

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (root.TryGetProperty("result", out var resultProp) && resultProp.ValueKind == JsonValueKind.True)
            {
                return true;
            }

            _logger.LogDebug("Deluge auth.login returned false for client {ClientId}", LogRedaction.SanitizeText(client?.Id ?? client?.Name));
            return false;
        }

        private async Task<JsonElement> InvokeRpcAsync(
            DownloadClientConfiguration client,
            HttpClient httpClient,
            CookieContainer cookies,
            string method,
            object[] parameters,
            CancellationToken ct)
        {
            var payload = new
            {
                method,
                @params = parameters,
                id = System.Threading.Interlocked.Increment(ref _rpcIdCounter)
            };

            var url = DownloadClientUriBuilder.BuildUri(client, "/json").ToString();
            var json = JsonSerializer.Serialize(payload);

            _logger.LogDebug("Deluge RPC request [{Method}] to {Url}", method, LogRedaction.SanitizeUrl(url));

            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await httpClient.PostAsync(url, content, ct);
            var body = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Deluge returned {StatusCode} for method {Method}", response.StatusCode, method);
                throw new HttpRequestException($"Deluge returned {response.StatusCode}", null, response.StatusCode);
            }

            if (string.IsNullOrWhiteSpace(body))
            {
                using var emptyDoc = JsonDocument.Parse("{}");
                return emptyDoc.RootElement.Clone();
            }

            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.Clone();
        }

        private async Task<QueueItem> MapTorrentToQueueItemAsync(DownloadClientConfiguration client, string hash, JsonElement torrent, CancellationToken ct)
        {
            var name = torrent.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? string.Empty : string.Empty;
            var state = torrent.TryGetProperty("state", out var stateProp) ? stateProp.GetString() ?? string.Empty : string.Empty;
            var progress = torrent.TryGetProperty("progress", out var progressProp) ? progressProp.GetDouble() : 0d;
            var totalSize = torrent.TryGetProperty("total_size", out var sizeProp) ? sizeProp.GetInt64() : 0L;
            var totalDone = torrent.TryGetProperty("total_done", out var doneProp) ? doneProp.GetInt64() : 0L;
            var downloadRate = torrent.TryGetProperty("download_payload_rate", out var rateProp) ? rateProp.GetDouble() : 0d;
            var eta = torrent.TryGetProperty("eta", out var etaProp) ? etaProp.GetInt32() : -1;
            var savePath = torrent.TryGetProperty("save_path", out var savePathProp) ? savePathProp.GetString() ?? string.Empty : string.Empty;
            var label = torrent.TryGetProperty("label", out var labelProp) ? labelProp.GetString() ?? string.Empty : string.Empty;
            var timeAdded = torrent.TryGetProperty("time_added", out var timeProp) ? timeProp.GetInt64() : 0L;
            var ratio = torrent.TryGetProperty("ratio", out var ratioProp) ? ratioProp.GetDouble() : 0d;
            var isFinished = torrent.TryGetProperty("is_finished", out var finishedProp) && finishedProp.GetBoolean();

            var statusStr = MapDelugeStateToString(state, isFinished);

            string? localPath = savePath;
            if (!string.IsNullOrEmpty(savePath))
            {
                try { localPath = await _pathMappingService.TranslatePathAsync(client.Id, savePath); }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger.LogDebug(ex, "Failed to translate path '{Path}'", LogRedaction.SanitizeText(savePath));
                }
            }

            var contentPath = !string.IsNullOrEmpty(savePath) && !string.IsNullOrEmpty(name) ? CombinePath(savePath, name) : savePath;
            var localContentPath = !string.IsNullOrEmpty(contentPath) ? await _pathMappingService.TranslatePathAsync(client.Id, contentPath) : contentPath;
            var addedAt = timeAdded > 0 ? DateTimeOffset.FromUnixTimeSeconds(timeAdded).UtcDateTime : DateTime.UtcNow;

            return new QueueItem
            {
                Id = hash.ToUpperInvariant(),
                Title = name,
                Quality = string.IsNullOrWhiteSpace(label) ? "Unknown" : label,
                Status = statusStr,
                Progress = progress,
                Size = totalSize,
                Downloaded = totalDone,
                DownloadSpeed = downloadRate,
                Eta = eta >= 0 ? eta : null,
                DownloadClient = client.Name ?? client.Id ?? "Deluge",
                DownloadClientId = client.Id ?? string.Empty,
                DownloadClientType = ClientType,
                AddedAt = addedAt,
                Ratio = ratio,
                CanPause = statusStr is "downloading" or "queued",
                CanRemove = true,
                RemotePath = savePath,
                LocalPath = localPath,
                ContentPath = localContentPath
            };
        }

        private async Task<DownloadClientItem> MapTorrentToDownloadClientItemAsync(
            DownloadClientConfiguration client,
            string hash,
            JsonElement torrent,
            bool removeCompletedDownloads,
            CancellationToken ct)
        {
            var name = torrent.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? string.Empty : string.Empty;
            var state = torrent.TryGetProperty("state", out var stateProp) ? stateProp.GetString() ?? string.Empty : string.Empty;
            var progress = torrent.TryGetProperty("progress", out var progressProp) ? progressProp.GetDouble() : 0d;
            var totalSize = torrent.TryGetProperty("total_size", out var sizeProp) ? sizeProp.GetInt64() : 0L;
            var totalDone = torrent.TryGetProperty("total_done", out var doneProp) ? doneProp.GetInt64() : 0L;
            var downloadRate = torrent.TryGetProperty("download_payload_rate", out var rateProp) ? rateProp.GetDouble() : 0d;
            var eta = torrent.TryGetProperty("eta", out var etaProp) ? etaProp.GetInt32() : -1;
            var savePath = torrent.TryGetProperty("save_path", out var savePathProp) ? savePathProp.GetString() ?? string.Empty : string.Empty;
            var label = torrent.TryGetProperty("label", out var labelProp) ? labelProp.GetString() ?? string.Empty : string.Empty;
            var isFinished = torrent.TryGetProperty("is_finished", out var finishedProp) && finishedProp.GetBoolean();

            var status = MapDelugeStateToStatus(state, isFinished);
            var downloadId = hash.ToUpperInvariant();

            var contentPath = !string.IsNullOrEmpty(savePath) && !string.IsNullOrEmpty(name) ? CombinePath(savePath, name) : savePath;
            var localContentPath = !string.IsNullOrEmpty(contentPath) ? await _pathMappingService.TranslatePathAsync(client.Id, contentPath) : contentPath;

            TimeSpan? remainingTime = eta >= 0 ? TimeSpan.FromSeconds(eta) : null;
            var remainingSize = Math.Max(0, totalSize - totalDone);

            var isCompleted = status == DownloadItemStatus.Completed;
            var canBeRemoved = removeCompletedDownloads && isCompleted;

            return new DownloadClientItem
            {
                DownloadId = downloadId,
                Title = name,
                Category = label,
                Status = status,
                TotalSize = totalSize,
                RemainingSize = remainingSize,
                RemainingTime = remainingTime,
                OutputPath = localContentPath,
                Progress = progress,
                DownloadSpeed = downloadRate,
                CanBeRemoved = canBeRemoved,
                CanMoveFiles = canBeRemoved && isCompleted,
                DownloadClientInfo = DownloadClientItemClientInfo.FromClient(
                    clientId: client.Id,
                    clientName: client.Name,
                    clientType: "deluge",
                    protocol: DownloadProtocol.Torrent,
                    removeCompletedDownloads: removeCompletedDownloads,
                    hasPostImportCategory: false
                )
            };
        }

        private static DownloadItemStatus MapDelugeStateToStatus(string state, bool isFinished)
        {
            return state switch
            {
                "Downloading" => DownloadItemStatus.Downloading,
                "Seeding" when isFinished => DownloadItemStatus.Completed,
                "Seeding" => DownloadItemStatus.Downloading,
                "Paused" => DownloadItemStatus.Paused,
                "Error" => DownloadItemStatus.Failed,
                "Checking" => DownloadItemStatus.Checking,
                "Queued" => DownloadItemStatus.Queued,
                _ => DownloadItemStatus.Unknown
            };
        }

        private static string MapDelugeStateToString(string state, bool isFinished)
        {
            return state switch
            {
                "Downloading" => "downloading",
                "Seeding" when isFinished => "completed",
                "Seeding" => "downloading",
                "Paused" => "paused",
                "Error" => "failed",
                "Checking" => "checking",
                "Queued" => "queued",
                _ => "unknown"
            };
        }

        private static string CombinePath(string basePath, string name)
        {
            var normalizedBase = basePath.TrimEnd('/', '\\');
            var normalizedName = name.TrimStart('/', '\\');
            return string.IsNullOrEmpty(normalizedBase) ? normalizedName : $"{normalizedBase}/{normalizedName}";
        }

        private static string? NormalizeTorrentUrl(string? torrentUrl)
        {
            var trimmed = (torrentUrl ?? string.Empty).Trim();
            if (trimmed.Length == 0) return null;

            if (!DownloadClientUriBuilder.TryParseHttpOrHttpsAbsoluteUri(trimmed, out var torrentUri))
                return null;

            return torrentUri!.ToString();
        }
    }
}
