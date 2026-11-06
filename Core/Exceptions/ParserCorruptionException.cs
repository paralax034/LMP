namespace LMP.Core.Exceptions;

/// <summary>
/// Parser signal indicating structural data corruption (Validation by Consumption).
/// </summary>
public sealed class ParserCorruptionException : Exception
{
    /// <summary>
    /// Absolute byte position in the stream where garbage was detected.
    /// Used by the self-healing layer to calculate the index of the corrupted chunk.
    /// </summary>
    public long AbsoluteBytePosition { get; }

    public ParserCorruptionException(long absoluteBytePosition, string message, Exception? inner = null) 
        : base(message, inner)
    {
        AbsoluteBytePosition = absoluteBytePosition;
    }
}