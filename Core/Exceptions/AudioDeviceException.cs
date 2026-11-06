namespace LMP.Core.Exceptions;

/// <summary>
/// Thrown when the audio output device is unavailable.
/// </summary>
public sealed class AudioDeviceException : Exception
{
    public AudioDeviceException(string message) : base(message) { }
    public AudioDeviceException(string message, Exception? inner) : base(message, inner) { }
}