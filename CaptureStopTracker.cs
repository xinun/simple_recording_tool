using NAudio.Wave;

namespace MeetRecorder;

// Subscribe before StartRecording: a disconnected device can stop long before
// the user asks to save. StopRecording does not raise another event in that case.
internal sealed class CaptureStopTracker : IDisposable
{
    private readonly IWaveIn _capture;
    private readonly TaskCompletionSource<Exception?> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public CaptureStopTracker(IWaveIn capture)
    {
        _capture = capture;
        _capture.RecordingStopped += OnStopped;
    }

    public Task<Exception?> StopAsync()
    {
        if (!_completion.Task.IsCompleted) _capture.StopRecording();
        return _completion.Task;
    }

    private void OnStopped(object? sender, StoppedEventArgs args) =>
        _completion.TrySetResult(args.Exception);

    public void Dispose() => _capture.RecordingStopped -= OnStopped;
}
