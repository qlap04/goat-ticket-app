using System.Security.Cryptography;
using GoatTicket.Shared.Ticketing;
using Xunit;

namespace GoatTicket.UnitTests;

/// <summary>T027: a validly signed payload round-trips and verifies; a tampered payload fails verification (research.md §7).</summary>
public class TicketQrSignerTests
{
    private static (string PrivatePem, string PublicPem) GenerateKeyPair()
    {
        using var rsa = RSA.Create(2048);
        return (rsa.ExportRSAPrivateKeyPem(), rsa.ExportRSAPublicKeyPem());
    }

    [Fact]
    public void Sign_ThenVerify_RoundTripsSuccessfully()
    {
        var (privatePem, publicPem) = GenerateKeyPair();
        var signer = TicketQrSigner.FromPrivateKeyPem(privatePem);
        var orderId = Guid.NewGuid();
        const long seatId = 42;

        var (token, jti) = signer.Sign(orderId, seatId);

        var verifier = TicketQrSigner.FromPublicKeyPem(publicPem);
        var verified = verifier.TryVerify(token, out var principal);

        Assert.True(verified);
        Assert.NotNull(principal);
        Assert.Equal(orderId.ToString(), principal!.FindFirst(TicketQrSigner.OrderIdClaim)!.Value);
        Assert.Equal(seatId.ToString(), principal.FindFirst(TicketQrSigner.SeatIdClaim)!.Value);
        Assert.False(string.IsNullOrEmpty(jti));
    }

    [Fact]
    public void TryVerify_TamperedPayload_FailsVerification()
    {
        var (privatePem, publicPem) = GenerateKeyPair();
        var signer = TicketQrSigner.FromPrivateKeyPem(privatePem);
        var (token, _) = signer.Sign(Guid.NewGuid(), seatId: 42);

        // Flip a character in the payload segment to simulate tampering.
        var parts = token.Split('.');
        var tamperedPayload = parts[1][..^1] + (parts[1][^1] == 'A' ? 'B' : 'A');
        var tamperedToken = $"{parts[0]}.{tamperedPayload}.{parts[2]}";

        var verifier = TicketQrSigner.FromPublicKeyPem(publicPem);
        var verified = verifier.TryVerify(tamperedToken, out var principal);

        Assert.False(verified);
        Assert.Null(principal);
    }

    [Fact]
    public void TryVerify_WrongKey_FailsVerification()
    {
        var (privatePem, _) = GenerateKeyPair();
        var (_, otherPublicPem) = GenerateKeyPair();
        var signer = TicketQrSigner.FromPrivateKeyPem(privatePem);
        var (token, _) = signer.Sign(Guid.NewGuid(), seatId: 7);

        var verifier = TicketQrSigner.FromPublicKeyPem(otherPublicPem);
        var verified = verifier.TryVerify(token, out var principal);

        Assert.False(verified);
        Assert.Null(principal);
    }
}
