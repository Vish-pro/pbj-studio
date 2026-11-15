using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using iText.Commons.Bouncycastle.Crypto;
using iText.Kernel.Crypto;
using iText.Signatures;

namespace KcsPdfSigner.Core;

/// <summary>
/// Feeds iText the raw signature bytes, produced by the private key that lives on the DSC token.
/// </summary>
/// <remarks>
/// The key handle is opened once in the constructor and reused for every signature in the run.
/// That is deliberate: a document with eight signature spots means eight signing operations, and
/// re-opening the key each time makes most token drivers re-prompt for the PIN every time.
/// Keeping one handle alive gives one prompt for the whole run - assuming the driver's own
/// "single logon" / PIN-caching setting allows it.
/// </remarks>
public sealed class TokenExternalSignature : IExternalSignature, IDisposable
{
    private readonly RSA? _rsa;
    private readonly ECDsa? _ecdsa;
    private readonly object _gate = new();
    private bool _disposed;

    public TokenExternalSignature(X509Certificate2 certificate)
    {
        _rsa = certificate.GetRSAPrivateKey();
        if (_rsa is null)
        {
            _ecdsa = certificate.GetECDsaPrivateKey();
        }

        if (_rsa is null && _ecdsa is null)
        {
            throw new InvalidOperationException(
                "The selected certificate has no usable RSA or ECDSA private key. " +
                "If this is a USB token, check that it is plugged in and its driver is installed.");
        }
    }

    public string GetDigestAlgorithmName() => DigestAlgorithms.SHA256;

    public string GetSignatureAlgorithmName() => _rsa is not null ? "RSA" : "ECDSA";

    public ISignatureMechanismParams? GetSignatureMechanismParameters() => null;

    public byte[] Sign(byte[] message)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Token sessions are not thread-safe; serialise every operation through this key.
        lock (_gate)
        {
            return _rsa is not null
                ? _rsa.SignData(message, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
                : _ecdsa!.SignData(message, HashAlgorithmName.SHA256);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _rsa?.Dispose();
        _ecdsa?.Dispose();
    }
}
