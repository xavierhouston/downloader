using System.Net;
using TorrentDownloader.Core.Models;
using TorrentDownloader.Core.Services;

namespace TorrentDownloader.Tests;

public class HttpDownloadServiceDiskMismatchTests
{
    [Fact]
    public async Task ResumeAfterFileShrinksOnDisk_ClampsToActualLengthInsteadOfSeekingPastEndOfFile()
    {
        // If the in-memory progress (1000 bytes) ever disagrees with what's actually on disk
        // (here, simulating the file having shrunk to 500 bytes - e.g. a disk error, antivirus
        // quarantine, or anything else that touched the file externally), resuming must trust
        // the disk and request byte 500 onward - not seek to byte 1000 in a 500-byte file, which
        // on Windows/NTFS silently succeeds and leaves a zero-filled gap instead of erroring,
        // corrupting the final file with no visible failure at all.
        var handler = new RangeCapturingHandler();
        handler.Responses.Enqueue(new ScriptedResponse(BodyBytesThenDrop: 1000));
        handler.Responses.Enqueue(new ScriptedResponse(StatusCode: HttpStatusCode.PartialContent, BodyBytesThenDrop: 0, CompleteImmediately: true));

        using var networkMonitor = new NetworkMonitorService();
        using var service = new HttpDownloadService(networkMonitor, httpMessageHandler: handler);
        var saveDir = Path.Combine(Path.GetTempPath(), "TorrentDownloaderTests", Guid.NewGuid().ToString());

        var info = await service.AddAsync("http://fake.test/file.bin", saveDir);

        var deadline = DateTime.UtcNow.AddSeconds(5);
        HttpDownloadInfo? snapshot = null;
        while (DateTime.UtcNow < deadline)
        {
            snapshot = service.GetSnapshot().Single(d => d.Id == info.Id);
            if (snapshot.DownloadedBytes > 0 && snapshot.State != HttpDownloadState.Downloading)
                break;
            await Task.Delay(50);
        }

        Assert.Equal(1000, snapshot!.DownloadedBytes);

        // Simulate the file having shrunk on disk without the in-memory tracker knowing.
        var filePath = Path.Combine(saveDir, "file.bin");
        using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Write))
            fs.SetLength(500);

        await service.ResumeAsync(info.Id);

        deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (handler.LastRangeFrom.HasValue)
                break;
            await Task.Delay(50);
        }

        Assert.Equal(500, handler.LastRangeFrom);

        await service.RemoveAsync(info.Id, deleteFile: true);
    }

    private sealed record ScriptedResponse(int BodyBytesThenDrop = 0, HttpStatusCode StatusCode = HttpStatusCode.OK, bool CompleteImmediately = false);

    private sealed class RangeCapturingHandler : HttpMessageHandler
    {
        public Queue<ScriptedResponse> Responses { get; } = new();
        public long? LastRangeFrom { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRangeFrom = request.Headers.Range?.Ranges.FirstOrDefault()?.From;

            var script = Responses.Count > 0 ? Responses.Dequeue() : new ScriptedResponse(StatusCode: HttpStatusCode.ServiceUnavailable);

            if (script.StatusCode != HttpStatusCode.OK && script.StatusCode != HttpStatusCode.PartialContent)
            {
                await Task.Yield();
                return new HttpResponseMessage(script.StatusCode) { Content = new ByteArrayContent(Array.Empty<byte>()) };
            }

            if (script.CompleteImmediately)
            {
                await Task.Yield();
                return new HttpResponseMessage(script.StatusCode) { Content = new ByteArrayContent(Array.Empty<byte>()) };
            }

            var stream = new DropAfterNBytesStream(script.BodyBytesThenDrop);
            var content = new StreamContent(stream);
            content.Headers.ContentLength = script.BodyBytesThenDrop;
            return new HttpResponseMessage(script.StatusCode) { Content = content };
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
