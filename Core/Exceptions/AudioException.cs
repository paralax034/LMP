namespace LMP.Core.Exceptions;

/// <summary>
/// Base exception for audio operations.
/// </summary>
public class AudioException : Exception
{
    public AudioException(string message) : base(message) { }
    public AudioException(string message, Exception? inner) : base(message, inner) { }
}