namespace LMP.Core.Exceptions;

/// <summary>
/// Audio source error.
/// </summary>
public class AudioSourceException : AudioException
{
    public AudioSourceException(string message) : base(message) { }
    public AudioSourceException(string message, Exception inner) : base(message, inner) { }
}