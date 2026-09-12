using System.Net;
using TorrentDownloader.Core.Models;
using TorrentDownloader.Core.Services;

namespace TorrentDownloader.Tests;

public class HttpDownloadServiceResumeAfterErrorTests
{
    [Fact]
    public async Task ServerErrorOnResume_DoesNotResetProgress()
    {
        // Reproduces the reported bug: a large in-progress download (simulated here as 1000
        // bytes for speed) hits a connection drop, then the *next* attempt (a Range-header
        // resume request) gets a transient server error like 503. That must not wipe the
        // already-downloaded progress - the next real retry needs to still resume from where
        // it left off, not reopen the file with FileMode.Create and truncate everything.
        var handler = new ScriptedHandler();
        handler.Responses.Enqueue(new ScriptedResponse(BodyBytesThenDrop: 1000));
        handler.Responses.Enqueue(new ScriptedResponse(StatusCode: HttpStatusCode.ServiceUnavailable));

        using var networkMonitor = new NetworkMonitorService();
        using var service = new HttpDownloadService(networkMonitor, httpMessageHandler: handler);
        var saveDir = Path.Combine(Path.GetTempPath(), "TorrentDownloaderTests", Guid.NewGuid().ToString());

        var info = await service.AddAsync("http://fake.test/file.bin", saveDir);

        // Wait for the first attempt to drop after writing 1000 bytes.
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
        Assert.Equal(1000, snapshot!.DownloadedBytes);

        // Manually trigger the resume attempt that will hit the scripted 503.
        await service.ResumeAsync(info.Id);

        deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            snapshot = service.GetSnapshot().Single(d => d.Id == info.Id);
            if (snapshot.State != HttpDownloadState.Connecting && snapshot.State != HttpDownloadState.Downloading)
                break;
            await Task.Delay(50);
        }

        // The critical assertion: progress must survive the 503, not get reset to 0.
        Assert.Equal(1000, snapshot!.DownloadedBytes);

        await service.RemoveAsync(info.Id, deleteFile: true);
    }

    private sealed record ScriptedResponse(int BodyBytesThenDrop = 0, HttpStatusCode StatusCode = HttpStatusCode.OK);

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        public Queue<ScriptedResponse> Responses { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var script = Responses.Count > 0 ? Responses.Dequeue() : new ScriptedResponse(StatusCode: HttpStatusCode.ServiceUnavailable);

            if (script.StatusCode != HttpStatusCode.OK)
            {
                await Task.Yield();
                return new HttpResponseMessage(script.StatusCode) { Content = new ByteArrayContent(Array.Empty<byte>()) };
            }

            // StreamContent hands the consumer this stream directly with no buffering, so a
            // fault partway through actually delivers the earlier bytes first - unlike a plain
            // HttpContent subclass, whose default ReadAsStreamAsync() buffers the *entire* body
            // into a MemoryStream before returning anything, so a mid-stream throw there would
            // deliver nothing at all and not actually exercise the bug being tested.
            var stream = new DropAfterNBytesStream(script.BodyBytesThenDrop);
            var content = new StreamContent(stream);
            content.Headers.ContentLength = script.BodyBytesThenDrop;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }
    }

    /// <summary>Yields N bytes successfully, then throws IOException on the next read -
    /// simulating a connection that drops mid-transfer rather than ending cleanly.</summary>
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
                throw new IOException("Simulated connection drop.");

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
