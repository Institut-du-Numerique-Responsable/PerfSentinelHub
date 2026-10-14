using System.Text;
using PerfSentinelHub.Configuration;

namespace PerfSentinelHub.Maintenance;

/// <summary>
///     <c>PerfSentinelHub hash-password</c>: prints the PHC string a local account's
///     PasswordHash takes, and nothing else on stdout, so it can go straight into a Secret.
///     The password is one line of redirected input, or typed twice at a prompt that does not
///     echo it; never an argument, which would stay in the shell history and the process list.
/// </summary>
public static class HashPasswordCommand
{
    public static int Run(
        IReadOnlyList<string> args,
        TextReader input,
        Func<ConsoleKeyInfo>? readKey,
        TextWriter output,
        TextWriter error)
    {
        if (args is not ["hash-password"])
        {
            // The arguments are not echoed: one of them may be the password.
            error.WriteLine("Usage: PerfSentinelHub hash-password, with the password on stdin or typed at the prompt.");
            return 2;
        }

        string? password;
        if (readKey is null)
        {
            password = input.ReadLine();
        }
        else
        {
            password = Prompt("Password: ", readKey, error);
            if (password != Prompt("Repeat: ", readKey, error))
            {
                error.WriteLine("The two entries differ.");
                return 1;
            }
        }

        if (password is null)
        {
            error.WriteLine("No password was given.");
            return 1;
        }

        if (!LocalPassword.IsAcceptable(password))
        {
            error.WriteLine($"The password must be {LocalPassword.MinLength} to {LocalPassword.MaxLength} "
                            + "characters, with no control character.");
            return 1;
        }

        output.WriteLine(LocalPassword.Hash(password));
        return 0;
    }

    // A delegate rather than Console.ReadKey, so a test can type.
    private static string Prompt(string label, Func<ConsoleKeyInfo> readKey, TextWriter error)
    {
        error.Write(label);
        var typed = new StringBuilder();
        for (var key = readKey(); key.Key != ConsoleKey.Enter; key = readKey())
            if (key.Key != ConsoleKey.Backspace)
                typed.Append(key.KeyChar);
            else if (typed.Length > 0)
                typed.Length--;
        error.WriteLine();
        return typed.ToString();
    }
}
