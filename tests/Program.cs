using System.Reflection;
using MeetRecorder;
using NAudio.Wave;

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

var deviceError = new IOException("Device disconnected");
var early = new FakeCapture();
using (var tracker = new CaptureStopTracker(early))
{
    early.End(deviceError);
    Check(await tracker.StopAsync().WaitAsync(TimeSpan.FromSeconds(2)) == deviceError,
        "An earlier device failure must complete the stop and preserve its warning.");
    Check(early.StopCalls == 0, "An already stopped device must not be stopped again.");
}
Check(early.Subscribers == 0, "The event subscription must be released.");

var normal = new FakeCapture();
using (var tracker = new CaptureStopTracker(normal))
{
    Check(await tracker.StopAsync().WaitAsync(TimeSpan.FromSeconds(2)) is null,
        "Normal stop must succeed, including a synchronous event.");
}

var pending = new FakeCapture { StopImmediately = false };
using (var tracker = new CaptureStopTracker(pending))
{
    var stop = tracker.StopAsync();
    Check(!stop.IsCompleted, "Saving must wait for the final audio callback.");
    pending.End(deviceError);
    Check(await stop.WaitAsync(TimeSpan.FromSeconds(2)) == deviceError,
        "Device failure during stop must not discard the recording.");
}

var directory = Path.Combine(Path.GetTempPath(), "RecorderChecks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
try
{
    var wav = Path.Combine(directory, "input.wav");
    using (var writer = new WaveFileWriter(wav, new WaveFormat(44100, 16, 2)))
        writer.Write(new byte[176400], 0, 176400);

    using var recorder = new AudioRecorder();
    typeof(AudioRecorder).GetField("_speakerTempPath", BindingFlags.NonPublic | BindingFlags.Instance)!
        .SetValue(recorder, wav);
    var disconnected = new FakeCapture();
    var stopped = new CaptureStopTracker(disconnected);
    disconnected.End(deviceError);
    typeof(AudioRecorder).GetField("_speakerStop", BindingFlags.NonPublic | BindingFlags.Instance)!
        .SetValue(recorder, stopped);
    typeof(AudioRecorder).GetProperty("IsRecording")!.SetValue(recorder, true);
    var output = Path.Combine(directory, "saved.mp3");
    await recorder.StopAndSaveAsync(output).WaitAsync(TimeSpan.FromSeconds(10));
    Check(recorder.CaptureWarning is not null, "A recovered recording must report device interruption.");
    Check(!File.Exists(wav), "Temporary WAV should be removed after successful save.");
    Check(File.Exists(output) && new FileInfo(output).Length > 0, "MP3 must be finalized and moved.");
    Check(!File.Exists(output + ".partial"), "No partial file should remain after success.");
    using var reader = new Mp3FileReader(output);
    Check(reader.TotalTime.TotalSeconds > 0, "The saved MP3 must be readable.");

    using (var writer = new WaveFileWriter(wav, new WaveFormat(44100, 16, 2)))
        writer.Write(new byte[176400], 0, 176400);
    typeof(AudioRecorder).GetField("_speakerTempPath", BindingFlags.NonPublic | BindingFlags.Instance)!
        .SetValue(recorder, wav);
    typeof(AudioRecorder).GetProperty("IsRecording")!.SetValue(recorder, true);
    var invalidFolder = Path.Combine(directory, "not-a-directory");
    File.WriteAllText(invalidFolder, "block directory creation");
    try
    {
        await recorder.StopAndSaveAsync(Path.Combine(invalidFolder, "failed.mp3"));
        throw new Exception("Expected a save error.");
    }
    catch (IOException exception)
    {
        Check(File.Exists(wav) && exception.Message.Contains(wav), "Save failure must preserve and identify the WAV.");
    }
}
finally
{
    Directory.Delete(directory, true);
}
Console.WriteLine("PASS: early device stop, normal stop, failure during stop, event cleanup, recovered MP3/readback, WAV preservation on failure.");

sealed class FakeCapture : IWaveIn
{
    private EventHandler<StoppedEventArgs>? _stopped;
    public int Subscribers { get; private set; }
    public int StopCalls { get; private set; }
    public bool StopImmediately { get; init; } = true;
    public WaveFormat WaveFormat { get; set; } = new(44100, 16, 2);
    public event EventHandler<WaveInEventArgs>? DataAvailable { add { } remove { } }
    public event EventHandler<StoppedEventArgs>? RecordingStopped
    {
        add { _stopped += value; Subscribers++; }
        remove { _stopped -= value; Subscribers--; }
    }
    public void StartRecording() { }
    public void StopRecording()
    {
        StopCalls++;
        if (StopImmediately) End(null);
    }
    public void End(Exception? error) => _stopped?.Invoke(this, new StoppedEventArgs(error));
    public void Dispose() { }
}
