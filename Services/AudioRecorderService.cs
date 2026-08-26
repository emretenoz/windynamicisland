using System.IO;
using NAudio.Wave;

namespace WinDynamicIsland.Services;

public sealed class AudioRecorderService : IDisposable
{
    private WaveInEvent? _waveIn;
    private WaveFileWriter? _writer;
    private MemoryStream? _recordingStream;
    private bool _isRecording;

    public bool IsRecording => _isRecording;

    public void StartRecording()
    {
        if (_isRecording)
        {
            return;
        }

        _recordingStream?.Dispose();
        _recordingStream = new MemoryStream();
        _waveIn = new WaveInEvent
        {
            WaveFormat = new WaveFormat(16000, 16, 1),
            BufferMilliseconds = 50
        };

        _writer = new WaveFileWriter(new IgnoreDisposeStream(_recordingStream), _waveIn.WaveFormat);
        _waveIn.DataAvailable += OnDataAvailable;
        _waveIn.RecordingStopped += OnRecordingStopped;
        _waveIn.StartRecording();
        _isRecording = true;
    }

    public async Task<byte[]> StopRecordingAsync()
    {
        if (!_isRecording || _waveIn is null)
        {
            return Array.Empty<byte>();
        }

        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(object? sender, StoppedEventArgs args)
        {
            stopped.TrySetResult();
        }

        var waveIn = _waveIn;
        waveIn.RecordingStopped += Handler;
        waveIn.StopRecording();
        await stopped.Task.ConfigureAwait(false);
        waveIn.RecordingStopped -= Handler;

        _writer?.Flush();
        _recordingStream!.Position = 0;
        return _recordingStream.ToArray();
    }

    public async Task PlayAudioAsync(byte[] audioBytes, string? contentType = null)
    {
        if (audioBytes.Length == 0)
        {
            return;
        }

        await Task.Run(async () =>
        {
            await using var stream = new MemoryStream(audioBytes);
            using var reader = CreateWaveStream(stream, contentType);
            using var output = new WaveOutEvent();
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            output.Init(reader);
            output.PlaybackStopped += (_, _) => done.TrySetResult();
            output.Play();
            await done.Task.ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    private static WaveStream CreateWaveStream(Stream stream, string? contentType)
    {
        if (contentType?.Contains("mpeg", StringComparison.OrdinalIgnoreCase) == true ||
            contentType?.Contains("mp3", StringComparison.OrdinalIgnoreCase) == true ||
            LooksLikeMp3(stream))
        {
            stream.Position = 0;
            return new Mp3FileReader(stream);
        }

        stream.Position = 0;
        return new WaveFileReader(stream);
    }

    private static bool LooksLikeMp3(Stream stream)
    {
        if (!stream.CanSeek || stream.Length < 3)
        {
            return false;
        }

        var originalPosition = stream.Position;
        Span<byte> header = stackalloc byte[3];
        stream.Position = 0;
        var bytesRead = stream.Read(header);
        stream.Position = originalPosition;

        return bytesRead == 3 &&
               ((header[0] == (byte)'I' && header[1] == (byte)'D' && header[2] == (byte)'3') ||
                (header[0] == 0xFF && (header[1] & 0xE0) == 0xE0));
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs args)
    {
        _writer?.Write(args.Buffer, 0, args.BytesRecorded);
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs args)
    {
        _isRecording = false;
        _writer?.Dispose();
        _writer = null;

        if (_waveIn is not null)
        {
            _waveIn.DataAvailable -= OnDataAvailable;
            _waveIn.RecordingStopped -= OnRecordingStopped;
            _waveIn.Dispose();
            _waveIn = null;
        }
    }

    public void Dispose()
    {
        if (_isRecording)
        {
            _waveIn?.StopRecording();
        }

        _writer?.Dispose();
        _waveIn?.Dispose();
        _recordingStream?.Dispose();
    }

    private sealed class IgnoreDisposeStream(Stream inner) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        protected override void Dispose(bool disposing) { }
    }
}
