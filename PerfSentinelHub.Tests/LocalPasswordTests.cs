using PerfSentinelHub.Configuration;

namespace PerfSentinelHub.Tests;

/// <summary>
///     The PHC string a local account's password is stored as: PBKDF2-HMAC-SHA256 at exactly
///     600000 iterations, a 16-byte salt and a 32-byte key, both unpadded base64.
/// </summary>
public sealed class LocalPasswordTests
{
    private const string Password = "correct horse battery staple";

    // From Python's hashlib.pbkdf2_hmac, with salt bytes 00 01 ... 0F.
    private const string Vector = "$pbkdf2-sha256$i=600000$AAECAwQFBgcICQoLDA0ODw$7xdxRO7JQgy8EJPSqLNEqSvFBtDU7JwCjdGfgyTYweY";

    [Fact]
    public void The_known_vector_parses_and_verifies()
    {
        Assert.True(LocalPassword.TryParse(Vector, out var salt, out var key));

        Assert.Equal(Enumerable.Range(0, 16).Select(value => (byte)value), salt);
        Assert.Equal(32, key.Length);
        Assert.True(LocalPassword.Verify(Password, salt, key));
    }

    [Fact]
    public void A_fresh_hash_verifies_its_password_and_no_other()
    {
        var hash = LocalPassword.Hash(Password);

        Assert.StartsWith("$pbkdf2-sha256$i=600000$", hash, StringComparison.Ordinal);
        Assert.NotEqual(Vector, hash);
        Assert.True(LocalPassword.TryParse(hash, out var salt, out var key));
        Assert.True(LocalPassword.Verify(Password, salt, key));
        Assert.False(LocalPassword.Verify(Password + " ", salt, key));
    }

    // A Secret value often ends with a newline.
    [Fact]
    public void A_trailing_newline_is_trimmed()
    {
        Assert.True(LocalPassword.TryParse(Vector + "\n", out _, out _));
        Assert.Equal(Vector, new LocalAccountOptions { PasswordHash = Vector + "\r\n" }.PasswordHash);
    }

    [Theory]
    [InlineData(15, true)]
    [InlineData(1024, true)]
    [InlineData(14, false)]
    [InlineData(1025, false)]
    public void A_password_holds_15_to_1024_characters(int length, bool acceptable)
    {
        Assert.Equal(acceptable, LocalPassword.IsAcceptable(new string('p', length)));
    }

    [Theory]
    [InlineData("$pbkdf2-sha256$i=600001$AAECAwQFBgcICQoLDA0ODw$7xdxRO7JQgy8EJPSqLNEqSvFBtDU7JwCjdGfgyTYweY")]
    [InlineData("$pbkdf2-sha1$i=600000$AAECAwQFBgcICQoLDA0ODw$7xdxRO7JQgy8EJPSqLNEqSvFBtDU7JwCjdGfgyTYweY")]
    [InlineData("$pbkdf2-sha256$i=600000$AAECAwQFBgcICQoLDA0O$7xdxRO7JQgy8EJPSqLNEqSvFBtDU7JwCjdGfgyTYweY")]
    [InlineData("$pbkdf2-sha256$i=600000$AAECAwQFBgcICQoLDA0ODw==$7xdxRO7JQgy8EJPSqLNEqSvFBtDU7JwCjdGfgyTYweY=")]
    [InlineData("$pbkdf2-sha256$i=600000$AAECAwQFBgcICQoLDA0ODw$7xdxRO7JQgy8EJPSqLNEqSvFBtDU7JwCjdGfgyTYweY$")]
    [InlineData("$pbkdf2-sha256$i=600000$AAECAwQFBgcICQoLDA0OD!$7xdxRO7JQgy8EJPSqLNEqSvFBtDU7JwCjdGfgyTYweY")]
    [InlineData("")]
    public void A_hash_in_any_other_shape_is_refused(string text)
    {
        Assert.False(LocalPassword.TryParse(text, out _, out _));
    }
}
