using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;

namespace GoatTicket.Shared.Ticketing;

/// <summary>
/// Signs and verifies ticket QR JWTs using the dedicated RS256 `TicketQrSigningKey` — a key
/// pair that is distinct from the auth `JwtSigningKey` used by the login/API path
/// (research.md §6/§10). A future gate-scanning capability only ever needs the public key.
/// </summary>
public class TicketQrSigner
{
    public const string OrderIdClaim = "orderId";
    public const string SeatIdClaim = "seatId";

    private readonly RSA _rsa;
    private readonly bool _hasPrivateKey;

    private TicketQrSigner(RSA rsa, bool hasPrivateKey)
    {
        _rsa = rsa;
        _hasPrivateKey = hasPrivateKey;
    }

    /// <summary>Creates a signer capable of both signing and verifying, from an RSA private key PEM.</summary>
    public static TicketQrSigner FromPrivateKeyPem(string privateKeyPem)
    {
        var rsa = RSA.Create();
        rsa.ImportFromPem(privateKeyPem);
        return new TicketQrSigner(rsa, hasPrivateKey: true);
    }

    /// <summary>Creates a verify-only signer from an RSA public key PEM (what a future scanner would hold).</summary>
    public static TicketQrSigner FromPublicKeyPem(string publicKeyPem)
    {
        var rsa = RSA.Create();
        rsa.ImportFromPem(publicKeyPem);
        return new TicketQrSigner(rsa, hasPrivateKey: false);
    }

    /// <summary>Signs a JWT with claims {orderId, seatId} plus a unique jti (FR-013a).</summary>
    public (string Token, string Jti) Sign(Guid orderId, long seatId)
    {
        if (!_hasPrivateKey)
        {
            throw new InvalidOperationException("This signer was constructed with a public key only and cannot sign.");
        }

        var key = new RsaSecurityKey(_rsa);
        var credentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256);
        var jti = Guid.NewGuid().ToString();

        var claims = new[]
        {
            new Claim(OrderIdClaim, orderId.ToString()),
            new Claim(SeatIdClaim, seatId.ToString()),
            new Claim(JwtRegisteredClaimNames.Jti, jti)
        };

        var token = new JwtSecurityToken(
            claims: claims,
            notBefore: DateTime.UtcNow,
            expires: null,
            signingCredentials: credentials);

        return (new JwtSecurityTokenHandler().WriteToken(token), jti);
    }

    /// <summary>
    /// Verifies a QR JWT's signature and returns its claims. Returns false (with a null
    /// principal) for a tampered or invalid payload rather than throwing, so callers can
    /// treat "not a valid ticket" as an ordinary outcome.
    /// </summary>
    public bool TryVerify(string token, out ClaimsPrincipal? principal)
    {
        var key = new RsaSecurityKey(_rsa);
        var handler = new JwtSecurityTokenHandler();
        var validationParameters = new TokenValidationParameters
        {
            ValidateIssuer = false,
            ValidateAudience = false,
            ValidateLifetime = false,
            IssuerSigningKey = key,
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256]
        };

        try
        {
            principal = handler.ValidateToken(token, validationParameters, out _);
            return true;
        }
        catch (Exception)
        {
            // Catches both SecurityTokenException (bad signature) and lower-level parsing
            // exceptions (e.g. ArgumentException from malformed base64/JSON) thrown when a
            // tampered payload no longer decodes cleanly — a QR scan is untrusted external
            // input, so any failure to validate means "not a valid ticket", not a crash.
            principal = null;
            return false;
        }
    }
}
