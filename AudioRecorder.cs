using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Lame;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace MeetRecorder;

internal sealed class AudioRecorder : IDisposable
{
    private WasapiLoopbackCapture? _speakerCapture;
    private WasapiCapture? _microphoneCapture;
    private TimedWaveFile? _speakerFile;
    private TimedWaveFile? _microphoneFile;
    private string? _speakerTempPath;
    private string? _microphoneTempPath;
    private Stopwatch? _recordingClock;
    private bool _disposed;
    private CaptureStopTracker? _speakerStop;
    private CaptureStopTracker? _microphoneStop;

    public string? CaptureWarning { get; private set; }

    public bool IsRecording { get; private set; }

    public void Start(MMDevice speaker, MMDevice? microphone, string workingDirectory)
    {
        if (IsRecording) throw new InvalidOperationException("이미 녹음 중입니다.");
        CaptureWarning = null;

        Directory.CreateDirectory(workingDirectory);
        var token = Guid.NewGuid().ToString("N");
        _speakerTempPath = Path.Combine(workingDirectory, $".{token}.speaker.wav");
        _microphoneTempPath = microphone is null ? null : Path.Combine(workingDirectory, $".{token}.microphone.wav");

        try
        {
            _speakerCapture = new WasapiLoopbackCapture(speaker);
            _recordingClock = Stopwatch.StartNew();
            _speakerFile = new TimedWaveFile(_speakerTempPath, _speakerCapture.WaveFormat, _recordingClock);
            if (microphone is not null && _microphoneTempPath is not null)
            {
                _microphoneCapture = new WasapiCapture(microphone, true, 100);
                _microphoneFile = new TimedWaveFile(_microphoneTempPath, _microphoneCapture.WaveFormat, _recordingClock);
            }

            _speakerCapture.DataAvailable += SpeakerDataAvailable;
            if (_microphoneCapture is not null) _microphoneCapture.DataAvailable += MicrophoneDataAvailable;
            _speakerStop = new CaptureStopTracker(_speakerCapture);
            if (_microphoneCapture is not null) _microphoneStop = new CaptureStopTracker(_microphoneCapture);
            _speakerCapture.StartRecording();
            _microphoneCapture?.StartRecording();
            IsRecording = true;
        }
        catch
        {
            CleanupCaptures();
            DeleteTemporaryFiles();
            throw;
        }
    }

    public async Task StopAndSaveAsync(string outputPath, IProgress<int>? progress = null)
    {
        if (!IsRecording) throw new InvalidOperationException("녹음 중이 아닙니다.");

        IsRecording = false;
        var elapsed = _recordingClock?.Elapsed ?? TimeSpan.Zero;
        try
        {
            await StopCapturesAsync();
            _speakerFile?.Complete(elapsed);
            _microphoneFile?.Complete(elapsed);
            _speakerFile?.Dispose();
            _microphoneFile?.Dispose();
            _speakerFile = null;
            _microphoneFile = null;
            _recordingClock?.Stop();
            await Task.Run(() => MixToMp3(outputPath, progress));
            DeleteTemporaryFiles();
        }
        catch (Exception exception)
        {
            throw new IOException($"{exception.Message}\n\n저장하지 못한 녹음의 임시 WAV 파일을 보존했습니다.\n{_speakerTempPath}\n{_microphoneTempPath}", exception);
        }
        finally
        {
            CleanupCaptures();
        }
    }

    public async Task CancelAsync()
    {
        if (IsRecording)
        {
            IsRecording = false;
            await StopCapturesAsync();
        }

        CleanupCaptures();
        DeleteTemporaryFiles();
    }

    private void MixToMp3(string outputPath, IProgress<int>? progress)
    {
        if (_speakerTempPath is null)
            throw new InvalidOperationException("임시 스피커 녹음 파일을 찾을 수 없습니다.");

        using var speakerReader = new AudioFileReader(_speakerTempPath);
        using var microphoneReader = _microphoneTempPath is not null && File.Exists(_microphoneTempPath)
            ? new AudioFileReader(_microphoneTempPath)
            : null;
        var inputs = new List<ISampleProvider> { Normalize(speakerReader) };
        if (microphoneReader is not null) inputs.Add(Normalize(microphoneReader));
        var mixer = new MixingSampleProvider(inputs) { ReadFully = false };
        var pcm = mixer.ToWaveProvider16();

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var partialPath = outputPath + ".partial";
        try
        {
            using var writer = new LameMP3FileWriter(partialPath, pcm.WaveFormat, 128);
            var buffer = new byte[pcm.WaveFormat.AverageBytesPerSecond];
            var durationSeconds = Math.Max(speakerReader.TotalTime.TotalSeconds, microphoneReader?.TotalTime.TotalSeconds ?? 0);
            var expectedBytes = Math.Max(1L, (long)(durationSeconds
                * pcm.WaveFormat.AverageBytesPerSecond));
            long written = 0;
            int read;
            while ((read = pcm.Read(buffer, 0, buffer.Length)) > 0)
            {
                writer.Write(buffer, 0, read);
                written += read;
                progress?.Report(Math.Min(99, (int)(written * 100 / expectedBytes)));
            }
            writer.Flush();
            // Windows requires the output handle to be closed before renaming.
            writer.Dispose();

            File.Move(partialPath, outputPath, true);
            progress?.Report(100);
        }
        catch
        {
            TryDelete(partialPath);
            throw;
        }
    }

    private static ISampleProvider Normalize(ISampleProvider source)
    {
        ISampleProvider channels = source.WaveFormat.Channels switch
        {
            1 => new MonoToStereoSampleProvider(source),
            2 => source,
            _ => throw new NotSupportedException($"{source.WaveFormat.Channels}채널 오디오는 아직 지원하지 않습니다."),
        };

        return channels.WaveFormat.SampleRate == 44_100
            ? channels
            : new WdlResamplingSampleProvider(channels, 44_100);
    }

    private async Task StopCapturesAsync()
    {
        var waits = new List<Task<Exception?>>();
        if (_speakerStop is not null) waits.Add(_speakerStop.StopAsync());
        if (_microphoneStop is not null) waits.Add(_microphoneStop.StopAsync());
        var errors = await Task.WhenAll(waits);
        CaptureWarning = errors.Any(error => error is not null)
            ? "오디오 장치 오류로 일부 녹음이 중단되었습니다. 중단된 구간은 무음으로 저장됩니다."
            : null;
    }

    private void SpeakerDataAvailable(object? sender, WaveInEventArgs e) => _speakerFile?.Write(e.Buffer, e.BytesRecorded);
    private void MicrophoneDataAvailable(object? sender, WaveInEventArgs e) => _microphoneFile?.Write(e.Buffer, e.BytesRecorded);

    private void CleanupCaptures()
    {
        if (_speakerCapture is not null) _speakerCapture.DataAvailable -= SpeakerDataAvailable;
        if (_microphoneCapture is not null) _microphoneCapture.DataAvailable -= MicrophoneDataAvailable;
        _speakerCapture?.Dispose();
        _microphoneCapture?.Dispose();
        _speakerCapture = null;
        _microphoneCapture = null;
        _speakerStop?.Dispose();
        _microphoneStop?.Dispose();
        _speakerStop = null;
        _microphoneStop = null;
        _speakerFile?.Dispose();
        _microphoneFile?.Dispose();
        _speakerFile = null;
        _microphoneFile = null;
        _recordingClock?.Stop();
        _recordingClock = null;
    }

    private void DeleteTemporaryFiles()
    {
        TryDelete(_speakerTempPath);
        TryDelete(_microphoneTempPath);
        _speakerTempPath = null;
        _microphoneTempPath = null;
    }

    private static void TryDelete(string? path)
    {
        if (path is null) return;
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* 앱 종료 시 운영체제가 정리할 수 있도록 남겨 둔다. */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CleanupCaptures();
    }

    private sealed class TimedWaveFile : IDisposable
    {
        private readonly object _sync = new();
        private readonly WaveFileWriter _writer;
        private readonly Stopwatch _clock;
        private bool _disposed;

        public TimedWaveFile(string path, WaveFormat format, Stopwatch clock)
        {
            _writer = new WaveFileWriter(path, format);
            _clock = clock;
        }

        public void Write(byte[] buffer, int count)
        {
            lock (_sync)
            {
                if (_disposed) return;
                FillSilenceTo(_clock.Elapsed);
                _writer.Write(buffer, 0, count);
            }
        }

        public void Complete(TimeSpan duration)
        {
            lock (_sync)
            {
                if (_disposed) return;
                FillSilenceTo(duration);
            }
        }

        private void FillSilenceTo(TimeSpan duration)
        {
            var expected = (long)(duration.TotalSeconds * _writer.WaveFormat.AverageBytesPerSecond);
            var missing = expected - _writer.Length;
            if (missing <= _writer.WaveFormat.BlockAlign) return;
            missing -= missing % _writer.WaveFormat.BlockAlign;
            var silence = new byte[Math.Min(_writer.WaveFormat.AverageBytesPerSecond, 64 * 1024)];
            while (missing > 0)
            {
                var write = (int)Math.Min(missing, silence.Length);
                write -= write % _writer.WaveFormat.BlockAlign;
                if (write <= 0) break;
                _writer.Write(silence, 0, write);
                missing -= write;
            }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed) return;
                _disposed = true;
                _writer.Dispose();
            }
        }
    }
}
