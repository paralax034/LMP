namespace LMP.Core.Youtube.Bridge.Common;

/// <summary>
/// Lightweight player manifest containing a signature timestamp (STS).
/// </summary>
internal sealed class CipherManifest(string signatureTimestamp)
{
    /// <summary>Signature timestamp (sts/signatureTimestamp) required to bypass restrictions.</summary>
    public string SignatureTimestamp { get; } = signatureTimestamp;
}