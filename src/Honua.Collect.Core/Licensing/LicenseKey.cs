using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Collect.Core.Editions;

namespace Honua.Collect.Core.Licensing;

/// <summary>
/// Encodes and verifies the offline, signed Honua Collect license key. The token
/// is a compact, JWS-like triple — <c>HLIC1.&lt;base64url(claims-json)&gt;.&lt;base64url(ed25519-sig)&gt;</c>
/// — signed by the licensing authority's private key (held offline, never in this
/// repo) and verified on-device against the embedded authority public key. The
/// signature is the tamper-evidence: any change to the claims invalidates it.
/// </summary>
public static class LicenseKey
{
    private const string Header = "HLIC1";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed record Payload(
        [property: JsonPropertyName("edition")] CollectEdition Edition,
        [property: JsonPropertyName("features")] string[]? Features,
        [property: JsonPropertyName("customer")] string? Customer,
        [property: JsonPropertyName("lid")] string? LicenseId,
        [property: JsonPropertyName("iat")] DateTimeOffset IssuedAtUtc,
        [property: JsonPropertyName("exp")] DateTimeOffset ExpiresAtUtc,
        [property: JsonPropertyName("trial")] bool IsTrial);

    /// <summary>
    /// Issues a signed license token (licensing-authority / issuer tooling — needs
    /// the private key). The app only ever verifies.
    /// </summary>
    /// <param name="claims">The license to encode.</param>
    /// <param name="privateKey">The authority's raw 32-byte Ed25519 private key.</param>
    /// <returns>The signed token string.</returns>
    public static string Issue(LicenseClaims claims, byte[] privateKey)
    {
        ArgumentNullException.ThrowIfNull(claims);
        ArgumentNullException.ThrowIfNull(privateKey);

        var payload = new Payload(
            claims.Edition,
            claims.Features.Count == 0 ? null : claims.Features.Select(f => f.ToString()).ToArray(),
            claims.Customer,
            claims.LicenseId,
            claims.IssuedAtUtc,
            claims.ExpiresAtUtc,
            claims.IsTrial);

        var payloadJson = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        var signingInput = $"{Header}.{Base64Url.Encode(payloadJson)}";
        var signature = Ed25519Signing.Sign(Encoding.ASCII.GetBytes(signingInput), privateKey);
        return $"{signingInput}.{Base64Url.Encode(signature)}";
    }

    /// <summary>
    /// Verifies a license token against the authority public key and decodes its
    /// claims. The signature is checked before the claims are trusted; expiry and
    /// activation are evaluated against <paramref name="nowUtc"/>.
    /// </summary>
    /// <param name="token">The token string.</param>
    /// <param name="publicKey">The authority's raw 32-byte Ed25519 public key.</param>
    /// <param name="nowUtc">The current time (injected for testability).</param>
    /// <returns>The verification result; claims are populated whenever the signature verified.</returns>
    public static LicenseVerificationResult Verify(string? token, byte[] publicKey, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(publicKey);

        if (string.IsNullOrWhiteSpace(token))
        {
            return LicenseVerificationResult.Malformed;
        }

        var parts = token.Split('.');
        if (parts.Length != 3 || parts[0] != Header)
        {
            return LicenseVerificationResult.Malformed;
        }

        if (!Base64Url.TryDecode(parts[2], out var signature) || !Base64Url.TryDecode(parts[1], out var payloadBytes))
        {
            return LicenseVerificationResult.Malformed;
        }

        // Verify the signature over the exact signing input before trusting any claim.
        var signingInput = Encoding.ASCII.GetBytes($"{Header}.{parts[1]}");
        if (!Ed25519Signing.Verify(signingInput, signature, publicKey))
        {
            return LicenseVerificationResult.Untrusted;
        }

        Payload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<Payload>(payloadBytes, JsonOptions);
        }
        catch (JsonException)
        {
            return LicenseVerificationResult.Malformed;
        }

        if (payload is null || string.IsNullOrEmpty(payload.Customer) || string.IsNullOrEmpty(payload.LicenseId))
        {
            return LicenseVerificationResult.Malformed;
        }

        if (!TryParseFeatures(payload.Features, out var features))
        {
            return LicenseVerificationResult.Malformed;
        }

        var claims = new LicenseClaims
        {
            Edition = payload.Edition,
            Features = features,
            Customer = payload.Customer,
            LicenseId = payload.LicenseId,
            IssuedAtUtc = payload.IssuedAtUtc,
            ExpiresAtUtc = payload.ExpiresAtUtc,
            IsTrial = payload.IsTrial,
        };

        if (!claims.HasCoherentWindow || !Enum.IsDefined(claims.Edition))
        {
            return new LicenseVerificationResult(LicenseStatus.Malformed, null);
        }

        if (nowUtc < claims.IssuedAtUtc)
        {
            return new LicenseVerificationResult(LicenseStatus.NotYetValid, claims);
        }

        if (claims.IsExpired(nowUtc))
        {
            return new LicenseVerificationResult(LicenseStatus.Expired, claims);
        }

        return new LicenseVerificationResult(LicenseStatus.Valid, claims);
    }

    private static bool TryParseFeatures(string[]? names, out IReadOnlySet<CollectFeature> features)
    {
        var set = new HashSet<CollectFeature>();
        if (names is not null)
        {
            foreach (var name in names)
            {
                if (!Enum.TryParse<CollectFeature>(name, ignoreCase: false, out var feature) || !Enum.IsDefined(feature))
                {
                    features = set;
                    return false;
                }

                set.Add(feature);
            }
        }

        features = set;
        return true;
    }

    /// <summary>Base64url (RFC 4648 §5, unpadded) — the token's segment encoding.</summary>
    private static class Base64Url
    {
        public static string Encode(byte[] bytes)
            => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        public static bool TryDecode(string value, out byte[] bytes)
        {
            var padded = value.Replace('-', '+').Replace('_', '/');
            switch (padded.Length % 4)
            {
                case 2: padded += "=="; break;
                case 3: padded += "="; break;
                case 1: bytes = []; return false; // never a valid base64 length
            }

            try
            {
                bytes = Convert.FromBase64String(padded);
                return true;
            }
            catch (FormatException)
            {
                bytes = [];
                return false;
            }
        }
    }
}
