using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Listenarr.Api.Services;
using Listenarr.Api.Services.Adapters;
using Listenarr.Domain.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Listenarr.Api.Tests
{
    public class DelugeAdapterTests
    {
        // ── helpers ──────────────────────────────────────────────────────────────

        private sealed class TestHttpClientFactory : IHttpClientFactory
        {
            private readonly HttpClient _client;

            public TestHttpClientFactory(HttpClient client)
            {
                _client = client;
            }

            public HttpClient CreateClient(string name) => _client;
        }

        /// <summary>
        /// Creates a handler that dispatches on the JSON-RPC method field.
        /// auth.login always returns true; <paramref name="mainHandler"/> handles everything else.
        /// </summary>
        private static DelegatingHandlerMock CreateDispatchHandler(
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> mainHandler)
        {
            return new DelegatingHandlerMock(async (req, ct) =>
            {
                var body = await req.Content!.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(body);
                var method = doc.RootElement.GetProperty("method").GetString();

                if (method == "auth.login")
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("""{"result":true,"error":null,"id":1}""")
                    };
                }

                return await mainHandler(req, ct);
            });
        }

        private static DownloadClientConfiguration MakeClient() => new()
        {
            Id = "deluge-1",
            Name = "Deluge",
            Type = "deluge",
            Host = "localhost",
            Port = 8112,
            Password = "localclient"
        };

        // ── TestConnectionAsync ───────────────────────────────────────────────────

        [Fact]
        public async Task TestConnectionAsync_Success_ReturnsTrue()
        {
            var handler = CreateDispatchHandler((req, ct) => Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"result":{},"error":null,"id":2}""")
                }));

            using var httpClient = new HttpClient(handler);
            var adapter = new DelugeAdapter(
                new TestHttpClientFactory(httpClient),
                Mock.Of<IRemotePathMappingService>(),
                Mock.Of<ITorrentFileDownloader>(),
                NullLogger<DelugeAdapter>.Instance);

            var (success, message) = await adapter.TestConnectionAsync(MakeClient());

            Assert.True(success);
            Assert.Contains("connected", message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task TestConnectionAsync_AuthFails_ReturnsFalse()
        {
            // auth.login returns false
            var handler = new DelegatingHandlerMock((req, ct) => Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"result":false,"error":null,"id":1}""")
                }));

            using var httpClient = new HttpClient(handler);
            var adapter = new DelugeAdapter(
                new TestHttpClientFactory(httpClient),
                Mock.Of<IRemotePathMappingService>(),
                Mock.Of<ITorrentFileDownloader>(),
                NullLogger<DelugeAdapter>.Instance);

            var (success, message) = await adapter.TestConnectionAsync(MakeClient());

            Assert.False(success);
            Assert.Contains("authentication", message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task TestConnectionAsync_NetworkError_ReturnsFalse()
        {
            var handler = new DelegatingHandlerMock((req, ct) =>
                throw new HttpRequestException("Connection refused", null, HttpStatusCode.ServiceUnavailable));

            using var httpClient = new HttpClient(handler);
            var adapter = new DelugeAdapter(
                new TestHttpClientFactory(httpClient),
                Mock.Of<IRemotePathMappingService>(),
                Mock.Of<ITorrentFileDownloader>(),
                NullLogger<DelugeAdapter>.Instance);

            var (success, message) = await adapter.TestConnectionAsync(MakeClient());

            Assert.False(success);
            Assert.Contains("network", message, StringComparison.OrdinalIgnoreCase);
        }

        // ── AddAsync ─────────────────────────────────────────────────────────────

        [Fact]
        public async Task AddAsync_WithFileContent_UsesAddTorrentFile()
        {
            string? capturedMethod = null;
            string? capturedBase64 = null;

            var handler = CreateDispatchHandler(async (req, ct) =>
            {
                var body = await req.Content!.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(body);
                capturedMethod = doc.RootElement.GetProperty("method").GetString();
                // params[1] is the base64 filedump
                capturedBase64 = doc.RootElement.GetProperty("params")[1].GetString();

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"result":"aabbccddee112233445566778899aabbccddee11","error":null,"id":2}""")
                };
            });

            using var httpClient = new HttpClient(handler);
            var adapter = new DelugeAdapter(
                new TestHttpClientFactory(httpClient),
                Mock.Of<IRemotePathMappingService>(),
                Mock.Of<ITorrentFileDownloader>(),
                NullLogger<DelugeAdapter>.Instance);

            var fileBytes = new byte[] { 0x64, 0x38, 0x3A };
            var result = new SearchResult
            {
                Title = "Great Audio Book",
                TorrentFileContent = fileBytes
            };

            var hash = await adapter.AddAsync(MakeClient(), result);

            Assert.Equal("AABBCCDDEE112233445566778899AABBCCDDEE11", hash);
            Assert.Equal("core.add_torrent_file", capturedMethod);
            Assert.Equal(Convert.ToBase64String(fileBytes), capturedBase64);
        }

        [Fact]
        public async Task AddAsync_WithMagnetLink_UsesAddTorrentMagnet()
        {
            string? capturedMethod = null;
            string? capturedMagnet = null;

            var handler = CreateDispatchHandler(async (req, ct) =>
            {
                var body = await req.Content!.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(body);
                capturedMethod = doc.RootElement.GetProperty("method").GetString();
                capturedMagnet = doc.RootElement.GetProperty("params")[0].GetString();

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"result":"deadbeefdeadbeefdeadbeefdeadbeefdeadbeef","error":null,"id":2}""")
                };
            });

            using var httpClient = new HttpClient(handler);
            var adapter = new DelugeAdapter(
                new TestHttpClientFactory(httpClient),
                Mock.Of<IRemotePathMappingService>(),
                Mock.Of<ITorrentFileDownloader>(),
                NullLogger<DelugeAdapter>.Instance);

            var searchResult = new SearchResult
            {
                Title = "Audiobook Via Magnet",
                MagnetLink = "magnet:?xt=urn:btih:DEADBEEFDEADBEEFDEADBEEFDEADBEEFDEADBEEF"
            };

            var hash = await adapter.AddAsync(MakeClient(), searchResult);

            Assert.Equal("DEADBEEFDEADBEEFDEADBEEFDEADBEEFDEADBEEF", hash);
            Assert.Equal("core.add_torrent_magnet", capturedMethod);
            Assert.Equal("magnet:?xt=urn:btih:DEADBEEFDEADBEEFDEADBEEFDEADBEEFDEADBEEF", capturedMagnet);
        }

        // ── GetItemsAsync ─────────────────────────────────────────────────────────

        [Fact]
        public async Task GetItemsAsync_DownloadingTorrent_MapsToDownloadingStatus()
        {
            var handler = CreateDispatchHandler((req, ct) => Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                    {
                        "result": {
                            "abc123abc123abc123abc123abc123abc123abc1": {
                                "name": "My Book",
                                "state": "Downloading",
                                "progress": 45.5,
                                "total_size": 500000000,
                                "total_done": 200000000,
                                "download_payload_rate": 1048576,
                                "eta": 300,
                                "save_path": "/downloads",
                                "label": "",
                                "time_added": 1700000000,
                                "ratio": 0.0,
                                "is_finished": false,
                                "seeding_time": 0
                            }
                        },
                        "error": null,
                        "id": 2
                    }
                    """)
                }));

            using var httpClient = new HttpClient(handler);
            var pathMock = new Mock<IRemotePathMappingService>();
            pathMock.Setup(x => x.TranslatePathAsync(It.IsAny<string>(), It.IsAny<string>()))
                    .ReturnsAsync((string _, string p) => p);

            var adapter = new DelugeAdapter(
                new TestHttpClientFactory(httpClient),
                pathMock.Object,
                Mock.Of<ITorrentFileDownloader>(),
                NullLogger<DelugeAdapter>.Instance);

            var items = await adapter.GetItemsAsync(MakeClient());

            Assert.Single(items);
            var item = items[0];
            Assert.Equal("ABC123ABC123ABC123ABC123ABC123ABC123ABC1", item.DownloadId);
            Assert.Equal("My Book", item.Title);
            Assert.Equal(DownloadItemStatus.Downloading, item.Status);
            Assert.Equal(45.5, item.Progress);
        }

        [Fact]
        public async Task GetItemsAsync_SeedingFinishedTorrent_MapsToCompletedStatus()
        {
            var handler = CreateDispatchHandler((req, ct) => Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                    {
                        "result": {
                            "deadbeefdeadbeefdeadbeefdeadbeefdeadbeef": {
                                "name": "Finished Book",
                                "state": "Seeding",
                                "progress": 100.0,
                                "total_size": 300000000,
                                "total_done": 300000000,
                                "download_payload_rate": 0,
                                "eta": 0,
                                "save_path": "/downloads",
                                "label": "audiobooks",
                                "time_added": 1700000000,
                                "ratio": 1.5,
                                "is_finished": true,
                                "seeding_time": 3600
                            }
                        },
                        "error": null,
                        "id": 2
                    }
                    """)
                }));

            using var httpClient = new HttpClient(handler);
            var pathMock = new Mock<IRemotePathMappingService>();
            pathMock.Setup(x => x.TranslatePathAsync(It.IsAny<string>(), It.IsAny<string>()))
                    .ReturnsAsync((string _, string p) => p);

            var adapter = new DelugeAdapter(
                new TestHttpClientFactory(httpClient),
                pathMock.Object,
                Mock.Of<ITorrentFileDownloader>(),
                NullLogger<DelugeAdapter>.Instance);

            var items = await adapter.GetItemsAsync(MakeClient());

            Assert.Single(items);
            var item = items[0];
            Assert.Equal(DownloadItemStatus.Completed, item.Status);
            Assert.Equal("audiobooks", item.Category);
        }

        // ── RemoveAsync ───────────────────────────────────────────────────────────

        [Fact]
        public async Task RemoveAsync_Success_ReturnsTrue()
        {
            var handler = CreateDispatchHandler((req, ct) => Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"result":true,"error":null,"id":2}""")
                }));

            using var httpClient = new HttpClient(handler);
            var adapter = new DelugeAdapter(
                new TestHttpClientFactory(httpClient),
                Mock.Of<IRemotePathMappingService>(),
                Mock.Of<ITorrentFileDownloader>(),
                NullLogger<DelugeAdapter>.Instance);

            var result = await adapter.RemoveAsync(MakeClient(), "DEADBEEFDEADBEEFDEADBEEFDEADBEEFDEADBEEF");

            Assert.True(result);
        }

        [Fact]
        public async Task RemoveAsync_WithDeleteFiles_PassesRemoveDataTrue()
        {
            bool? capturedRemoveData = null;

            var handler = CreateDispatchHandler(async (req, ct) =>
            {
                var body = await req.Content!.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(body);
                capturedRemoveData = doc.RootElement.GetProperty("params")[1].GetBoolean();

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"result":true,"error":null,"id":2}""")
                };
            });

            using var httpClient = new HttpClient(handler);
            var adapter = new DelugeAdapter(
                new TestHttpClientFactory(httpClient),
                Mock.Of<IRemotePathMappingService>(),
                Mock.Of<ITorrentFileDownloader>(),
                NullLogger<DelugeAdapter>.Instance);

            var result = await adapter.RemoveAsync(MakeClient(), "DEADBEEFDEADBEEFDEADBEEFDEADBEEFDEADBEEF", deleteFiles: true);

            Assert.True(result);
            Assert.True(capturedRemoveData);
        }
    }
}
