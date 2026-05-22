using NAudio.Wave;

namespace Soundpad.Audio;

/// <summary>
/// Abstraction over a microphone capture device. The implementation owns the
/// underlying capture object (e.g. <c>WasapiCapture</c>) and exposes the live
/// audio as an <see cref="ISampleProvider"/> at a caller-chosen working format
/// (typically 48 kHz stereo float). Resampling is the implementation's job.
/// </summary>
/// <remarks>
/// Designed so the engine can mix mic samples into the same pipeline as
/// playback samples. When the capture is stopped or no device is configured,
/// the exposed sample provider returns silence so the mixer is never starved.
/// </remarks>
public interface IMicCapture : IDisposable
{
    /// <summary>
    /// The working format the exposed sample provider produces. Set on Start.
    /// </summary>
    WaveFormat OutputFormat { get; }

    /// <summary>
    /// Sample provider that streams the captured mic audio (resampled to
    /// <see cref="OutputFormat"/>). Always non-null after <see cref="Start"/>;
    /// returns silence when the device is stopped or fails.
    /// </summary>
    ISampleProvider Samples { get; }

    /// <summary>
    /// True once <see cref="Start"/> has succeeded and capture is active.
    /// </summary>
    bool IsRunning { get; }

    /// <summary>
    /// Begin capturing from the named device. If <paramref name="deviceFriendlyName"/>
    /// is null, uses the default capture device. The output is resampled to
    /// <paramref name="workingFormat"/>. Safe to call multiple times — stops
    /// the previous capture first.
    /// </summary>
    void Start(string? deviceFriendlyName, WaveFormat workingFormat);

    /// <summary>
    /// Stop capture. The <see cref="Samples"/> provider stays valid and emits
    /// silence. Safe to call when not running.
    /// </summary>
    void Stop();

    /// <summary>
    /// Raised on the capture thread when the underlying device errors (e.g.
    /// the mic is unplugged at runtime). The engine logs it; the sample
    /// provider keeps returning silence.
    /// </summary>
    event Action<Exception>? CaptureError;
}
