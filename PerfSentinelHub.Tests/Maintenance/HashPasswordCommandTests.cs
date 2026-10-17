using PerfSentinelHub.Configuration;
using PerfSentinelHub.Maintenance;

namespace PerfSentinelHub.Tests.Maintenance;

/// <summary>
///     PerfSentinelHub hash-password: the password comes from stdin or the terminal, never
///     argv, and stdout carries the PHC string alone, ready for a Secret.
/// </summary>
public sealed class HashPasswordCommandTests
{
    private const string Password = "correct horse battery staple";

    [Fact]
    public void An_argument_is_a_usage_error_that_never_echoes_it()
    {
        var (exit, output, error) = Run(["hash-password", Password], Password + "\n");

        Assert.Equal(2, exit);
        Assert.Empty(output);
        Assert.DoesNotContain(Password, error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("fourteen chars\n")]
    [InlineData("\n")]
    [InlineData("")]
    [InlineData("a password with a\ttab\n")]
    public void A_password_out_of_bounds_or_missing_is_refused_with_nothing_on_stdout(string input)
    {
        var (exit, output, error) = Run(["hash-password"], input);

        Assert.Equal(1, exit);
        Assert.Empty(output);
        Assert.Single(error.Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public void A_password_over_1024_characters_is_refused_with_nothing_on_stdout()
    {
        var (exit, output, _) = Run(["hash-password"], new string('p', LocalPassword.MaxLength + 1) + "\n");

        Assert.Equal(1, exit);
        Assert.Empty(output);
    }

    [Fact]
    public void Redirected_input_gives_the_hash_alone_on_stdout()
    {
        var (exit, output, error) = Run(["hash-password"], Password + "\nignored second line\n");

        Assert.Equal(0, exit);
        Assert.Empty(error);
        Assert.EndsWith("\n", output, StringComparison.Ordinal);
        Assert.True(LocalPassword.TryParse(output, out var salt, out var key));
        Assert.Equal(output.TrimEnd('\n'), output.Trim());
        Assert.True(LocalPassword.Verify(Password, salt, key));
    }

    [Fact]
    public void A_terminal_entry_is_read_twice_with_backspace_honoured()
    {
        var keys = Keys(Password + "x\b\r" + Password + "\r");

        var (exit, output, error) = Run(["hash-password"], "", keys);

        Assert.Equal(0, exit);
        Assert.Equal("Password: \nRepeat: \n", error);
        Assert.True(LocalPassword.TryParse(output, out var salt, out var key));
        Assert.True(LocalPassword.Verify(Password, salt, key));
    }

    [Fact]
    public void Two_different_terminal_entries_are_refused()
    {
        var keys = Keys(Password + "\r" + Password + "!\r");

        var (exit, output, error) = Run(["hash-password"], "", keys);

        Assert.Equal(1, exit);
        Assert.Empty(output);
        Assert.Contains("The two entries differ.", error, StringComparison.Ordinal);
    }

    private static (int Exit, string Output, string Error) Run(string[] args, string input,
        Func<ConsoleKeyInfo>? readKey = null)
    {
        using var output = new StringWriter();
        output.NewLine = "\n";
        using var error = new StringWriter();
        error.NewLine = "\n";
        var exit = HashPasswordCommand.Run(args, new StringReader(input), readKey, output, error);
        return (exit, output.ToString(), error.ToString());
    }

    private static Func<ConsoleKeyInfo> Keys(string typed)
    {
        var queue = new Queue<ConsoleKeyInfo>(typed.Select(character => character switch
        {
            '\r' => new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false),
            '\b' => new ConsoleKeyInfo('\b', ConsoleKey.Backspace, false, false, false),
            _ => new ConsoleKeyInfo(character, ConsoleKey.A, false, false, false)
        }));
        return queue.Dequeue;
    }
}
