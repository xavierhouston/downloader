using System.Net;
using System.Net.Http.Headers;
using TorrentDownloader.Core.Models;
using TorrentDownloader.Core.Services;

namespace TorrentDownloader.Tests;

public class HttpDownloadServiceUpdateUrlTests
{
    [Fact]
    public async Task UpdateUrlAndResumeAsync_ResumesFromExistingBytes_AgainstTheNewLink()
    {
        // Reproduces the "link expired mid-download, generated a new one" scenario: the first
        // link drops after some bytes (simulating expiry), and the freshly generated second
        // link must be requested with a Range header for the bytes already on disk rather than
        // restarting the whole transfer.
        const int bytesBeforeExpiry = 1000;
        const int remainingBytes = 500;

        var handler = new ExpiringLinkHandler(bytesBeforeExpiry, remainingBytes);
        using var networkMonitor = new NetworkMonitorService();
        using var service = new HttpDownloadService(networkMonitor, httpMessageHandler: handler);
        var saveDir = Path.Combine(Path.GetTempPath(), "TorrentDownloaderTests", Guid.NewGuid().ToString());

        var info = await service.AddAsync("http://fake.test/expired-link", saveDir);

        var deadline = DateTime.UtcNow.AddSeconds(5);
        HttpDownloadInfo? snapshot = null;
        while (DateTime.UtcNow < deadline)
        {
            snapshot = service.GetSnapshot().Single(d => d.Id == info.Id);
            if (snapshot.DownloadedBytes > 0 && snapshot.State != HttpDownloadState.Downloading)
                break;
            await Task.Delay(50);
        }

        Assert.NotNull(snapshot);
        Assert.Equal(bytesBeforeExpiry, snapshot!.DownloadedBytes);

        await service.UpdateUrlAndResumeAsync(info.Id, "http://fake.test/fresh-link");

        deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            snapshot = service.GetSnapshot().Single(d => d.Id == info.Id);
            if (snapshot.State == HttpDownloadState.Completed)
                break;
            await Task.Delay(50);
        }

        Assert.Equal(HttpDownloadState.Completed, snapshot!.State);
        Assert.Equal(bytesBeforeExpiry + remainingBytes, snapshot.DownloadedBytes);
        Assert.Equal("http://fake.test/fresh-link", snapshot.Url);
        Assert.Equal(bytesBeforeExpiry, handler.RangeStartRequestedOnFreshLink);

        await service.RemoveAsync(info.Id, deleteFile: true);
    }

    /// <summary>The first URL always drops after N bytes (as if the link had expired); the
    /// second URL honors the Range header and serves the rest as 206 Partial Content, recording
    /// what offset it was asked to resume from.</summary>
    private sealed class ExpiringLinkHandler(int bytesBeforeExpiry, int remainingBytes) : HttpMessageHandler
    {
        public long? RangeStartRequestedOnFreshLink { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Yield();

            if (request.RequestUri!.AbsoluteUri.EndsWith("expired-link"))
            {
                var stream = new DropAfterNBytesStream(bytesBeforeExpiry);
                var content = new StreamContent(stream);
                content.Headers.ContentLength = bytesBeforeExpiry;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
            }

            var rangeStart = request.Headers.Range?.Ranges.FirstOrDefault()?.From ?? 0;
            RangeStartRequestedOnFreshLink = rangeStart;

            var body = new byte[remainingBytes];
            var partialContent = new ByteArrayContent(body);
            var totalLength = rangeStart + remainingBytes;
            partialContent.Headers.ContentRange = new ContentRangeHeaderValue(rangeStart, totalLength - 1, totalLength);
            return new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = partialContent };
        }
    }

    private sealed class DropAfterNBytesStream(int bytesBeforeDrop) : Stream
    {
        private int _remaining = bytesBeforeDrop;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_remaining <= 0)
                throw new IOException("Simulated link expiry.");

            var n = Math.Min(count, _remaining);
            _remaining -= n;
            return n;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
