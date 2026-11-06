namespace LMP.Core.Youtube.Exceptions;

/// <summary>
/// Fatal verification error for native quickjs_bridge.dll.
/// Occurs when the application has loaded an outdated/incorrect DLL
/// or the native browser environment does not match the expected ABI/features.
/// </summary>
public sealed class QuickJsBridgeVerificationException : Exception
{
    public QuickJsBridgeVerificationException(string message)
        : base(message)
    {
    }

    public QuickJsBridgeVerificationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}