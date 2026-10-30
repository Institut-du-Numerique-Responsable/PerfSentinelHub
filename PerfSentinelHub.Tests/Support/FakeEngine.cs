using System.Diagnostics;

namespace PerfSentinelHub.Tests.Support;

/// <summary>
///     A stand-in perf-sentinel binary: a <c>/bin/sh</c> script on Unix, a batch file on
///     Windows. Both only dispatch on the subcommand. What they print lives in files beside
///     them, so no payload goes through either shell's quoting.
/// </summary>
internal sealed record FakeEngine
{
    /// <summary>The bytes of the <c>card.svg</c> a <c>--format svg</c> render writes.</summary>
    public const string CardSvg = """<?xml version="1.0" encoding="UTF-8"?><svg xmlns="http://www.w3.org/2000/svg"/>""";

    private const string Letters = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";

    // What the engine prints when its --out-dir does not exist: it never creates it.
    private const string Unwritable = """{"result":"input_error","reason":"output_unwritable"}""";

    // The default key less its last character, which is the call's letter.
    internal static readonly string Fingerprint = new('A', 21);
    internal static readonly string PublicKey = new('A', 42);

    /// <summary>Printed for <c>--version</c>. Null runs <c>--version</c> as a query.</summary>
    public string? Version { get; init; }

    public int VersionExitCode { get; init; }

    /// <summary>Printed for <c>report --help</c>, which the probe reads for <c>--daemon-url</c> and <c>--hub-relay</c>.</summary>
    public string Help { get; init; } = "";

    public int HelpExitCode { get; init; }

    /// <summary>What the query subcommand prints: anything but <c>--version</c> and <c>report</c>.</summary>
    public string Output { get; init; } = "";

    public string Error { get; init; } = "";

    public int ExitCode { get; init; }

    public int SleepSeconds { get; init; }

    /// <summary>A <c>report</c> argument that makes the render exit 75, as a failed scrape does.</summary>
    public string? RenderFailsOn { get; init; }

    /// <summary>Whether <c>disclose-pdf</c> exists: its <c>--help</c> exits 0, or 2 as clap does for an unknown one.</summary>
    public bool DisclosePdf { get; init; }

    public int DisclosePdfExitCode { get; init; }

    /// <summary>The reason line <c>disclose-pdf</c> prints on stdout.</summary>
    public string DisclosePdfOutput { get; init; } = "";

    /// <summary>Exits 0 without writing the PDF, which breaks the contract.</summary>
    public bool DisclosePdfWritesNoPdf { get; init; }

    /// <summary>Whether <c>card</c> exists: <c>card &lt;sub&gt; --help</c> exits 0, or 2 as clap does for an unknown one.</summary>
    public bool Card { get; init; }

    /// <summary>
    ///     The line <c>card keygen</c> prints. Null prints a valid key whose fingerprint and
    ///     public key end with the call's letter (A, then B, ...), so each call makes another key.
    /// </summary>
    public string? CardKeygenOutput { get; init; }

    public int CardKeygenExitCode { get; init; }

    /// <summary>The line <c>card render</c> prints; the default is a rendered card signed by the first key.</summary>
    public string CardRenderOutput { get; init; } =
        """{"result":"rendered","files":["card.png"],"fingerprint":"ps1:AAAAAAAAAAAAAAAAAAAAAA","compact_url":"https://perf-sentinel.dev/verifier#c=e30.AA"}""";

    /// <summary>Exit 0 writes <c>card.png</c>, <c>card.svg</c> or <c>card.pdf</c> in <c>--out-dir</c>; any other writes nothing.</summary>
    public int CardRenderExitCode { get; init; }

    /// <summary>The verdict line <c>verify-card</c> prints, whatever its exit code.</summary>
    public string VerifyCardOutput { get; init; } =
        """{"integrity":"ok","level":2,"domain":"example.com","key":"current","reason":null}""";

    public int VerifyCardExitCode { get; init; }

    /// <summary>
    ///     Writes the engine at <paramref name="path" /> and returns what to launch, which on
    ///     Windows carries a <c>.cmd</c> extension. <c>report</c> appends its arguments, one
    ///     render a line, to <c>render-args.txt</c> in its working directory, writes the
    ///     <c>pg_stat</c> auth header it was handed to <c>render-env.txt</c>, and writes an
    ///     HTML file wherever <c>--output</c> points, the two-step shape the real binary imposes.
    ///     <c>card keygen</c> appends its arguments to <c>card-keygen-args.txt</c> there and writes
    ///     <c>key.pk8</c> in its <c>--out-dir</c>. <c>card render</c> appends its arguments to
    ///     <c>card-args.txt</c>, copies its <c>--input</c> to <c>card-input.json</c> and its
    ///     <c>--signing-key</c> to <c>card-key-seen.pk8</c> there, and refuses a missing <c>--out-dir</c>
    ///     as the engine does. <c>verify-card</c> appends its arguments to <c>verify-args.txt</c>.
    /// </summary>
    public string WriteTo(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText($"{path}.version", Version ?? "");
        File.WriteAllText($"{path}.help", Help);
        File.WriteAllText($"{path}.out", Output);
        File.WriteAllText($"{path}.err", Error);
        File.WriteAllText($"{path}.html", "<html>report</html>");
        File.WriteAllText($"{path}.disclose", DisclosePdfOutput);
        File.WriteAllText($"{path}.pdf", "%PDF-1.7 fake");
        File.WriteAllText($"{path}.pk8", "fake pkcs8");
        File.WriteAllText($"{path}.keygen", CardKeygenOutput ?? "");
        File.WriteAllBytes($"{path}.png", [0x89, .. "PNG fake"u8]);
        File.WriteAllText($"{path}.svg", CardSvg);
        File.WriteAllText($"{path}.cardrender", CardRenderOutput);
        File.WriteAllText($"{path}.verify", VerifyCardOutput);

        if (OperatingSystem.IsWindows())
        {
            // CRLF, because cmd.exe can miss a label in a file with bare LF line endings.
            File.WriteAllText($"{path}.cmd", BatchScript(path).ReplaceLineEndings("\r\n"));
            return $"{path}.cmd";
        }

        // LF, because a CRLF checkout would otherwise hand sh an interpreter named "/bin/sh\r".
        File.WriteAllText($"{path}.sh", ShellScript(path).ReplaceLineEndings("\n"));
        // A child copies it into place: a test that forks while this process holds the file
        // open for writing would keep it busy, and Linux refuses to exec it with ETXTBSY.
        using (var copy = Process.Start("cp", [$"{path}.sh", path]))
        {
            copy.WaitForExit();
        }

        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    private string ShellScript(string path)
    {
        var version = Version is null
            ? ""
            : $"""if [ "$1" = "--version" ]; then cat "$P.version"; exit {VersionExitCode}; fi""";
        var keygen = CardKeygenOutput is null
            ? $$"""echo '{"fingerprint":"ps1:{{Fingerprint}}'"$L"'","public_key":"{{PublicKey}}'"$L"'"}'"""
            : """cat "$P.keygen" """;
        return $"""
                #!/bin/sh
                P={Quote(path)}
                {version}
                if [ "$1" = "report" ] && [ "$2" = "--help" ]; then cat "$P.help"; exit {HelpExitCode}; fi
                if [ "$1" = "card" ] && [ "$3" = "--help" ]; then exit {(Card ? 0 : 2)}; fi
                if [ "$1" = "card" ] && [ "$2" = "keygen" ]; then
                  echo "$@" >> card-keygen-args.txt
                  L=$(echo {Letters} | cut -c"$(wc -l < card-keygen-args.txt | tr -d ' ')")
                  while [ $# -gt 0 ]; do
                    if [ "$1" = "--out-dir" ]; then shift; cat "$P.pk8" > "$1/key.pk8"; fi
                    shift
                  done
                  {keygen}
                  exit {CardKeygenExitCode}
                fi
                if [ "$1" = "card" ] && [ "$2" = "render" ]; then
                  echo "$@" >> card-args.txt
                  sleep {SleepSeconds}
                  F=png
                  O=
                  while [ $# -gt 0 ]; do
                    case "$1" in
                      --input) shift; cat "$1" > card-input.json ;;
                      --signing-key) shift; cat "$1" > card-key-seen.pk8 ;;
                      --out-dir) shift; O="$1" ;;
                      --format) shift; F="$1" ;;
                    esac
                    shift
                  done
                  if [ ! -d "$O" ]; then echo '{Unwritable}'; exit 3; fi
                  if [ {CardRenderExitCode} = 0 ]; then cat "$P.$F" > "$O/card.$F"; fi
                  cat "$P.cardrender"
                  exit {CardRenderExitCode}
                fi
                if [ "$1" = "verify-card" ]; then
                  echo "$@" >> verify-args.txt
                  cat "$P.verify"
                  exit {VerifyCardExitCode}
                fi
                if [ "$1" = "disclose-pdf" ]; then
                  if [ "$2" = "--help" ]; then exit {(DisclosePdf ? 0 : 2)}; fi
                  echo "$@" > disclose-args.txt
                  sleep {SleepSeconds}
                  cat "$P.disclose"
                  if [ {DisclosePdfExitCode} = 0 ] && [ {(DisclosePdfWritesNoPdf ? 1 : 0)} = 0 ]; then
                    while [ $# -gt 0 ]; do
                      if [ "$1" = "--output" ]; then shift; cat "$P.pdf" > "$1"; fi
                      shift
                    done
                  fi
                  exit {DisclosePdfExitCode}
                fi
                if [ "$1" = "report" ]; then
                  echo "$@" >> render-args.txt
                  printf '%s' "$PERF_SENTINEL_PGSTAT_AUTH_HEADER" > render-env.txt
                  {(RenderFailsOn is null ? "" : $"case \" $* \" in *\" {RenderFailsOn} \"*) exit 75;; esac")}
                  while [ $# -gt 0 ]; do
                    if [ "$1" = "--output" ]; then shift; cat "$P.html" > "$1"; fi
                    shift
                  done
                  exit 0
                fi
                sleep {SleepSeconds}
                cat "$P.err" >&2
                cat "$P.out"
                exit {ExitCode}

                """;
    }

    private string BatchScript(string path)
    {
        var version = Version is null
            ? ""
            : $"""if "%~1"=="--version" (type "%P%.version" & exit /b {VersionExitCode})""";
        var keygen = CardKeygenOutput is null
            ? $$"""echo {"fingerprint":"ps1:{{Fingerprint}}%L%","public_key":"{{PublicKey}}%L%"}"""
            : """type "%P%.keygen" """;
        // ping stands in for sleep: timeout refuses to run once stdin is redirected.
        var sleep = SleepSeconds > 0 ? $"ping -n {SleepSeconds + 1} 127.0.0.1 >nul" : "";
        return $"""
                @echo off
                set "P={path}"
                {version}
                if "%~1"=="report" if "%~2"=="--help" (type "%P%.help" & exit /b {HelpExitCode})
                if "%~1"=="card" if "%~3"=="--help" exit /b {(Card ? 0 : 2)}
                if "%~1"=="card" if "%~2"=="keygen" goto keygen
                if "%~1"=="card" if "%~2"=="render" goto render
                if "%~1"=="verify-card" goto verify
                if "%~1"=="disclose-pdf" goto disclose
                if not "%~1"=="report" goto query
                >>render-args.txt echo %*
                >render-env.txt echo(%PERF_SENTINEL_PGSTAT_AUTH_HEADER%
                {(RenderFailsOn is null ? "" : $"echo %* | find \"{RenderFailsOn}\" >nul && exit /b 75")}
                :next
                if "%~1"=="" exit /b 0
                if "%~1"=="--output" copy /y "%P%.html" "%~2" >nul
                shift
                goto next
                :disclose
                if "%~2"=="--help" exit /b {(DisclosePdf ? 0 : 2)}
                >disclose-args.txt echo %*
                {sleep}
                type "%P%.disclose"
                if not "{DisclosePdfExitCode}{(DisclosePdfWritesNoPdf ? 1 : 0)}"=="00" exit /b {DisclosePdfExitCode}
                :dnext
                if "%~1"=="" exit /b 0
                if "%~1"=="--output" copy /y "%P%.pdf" "%~2" >nul
                shift
                goto dnext
                :keygen
                >>card-keygen-args.txt echo %*
                for /f %%n in ('find /c /v "" ^< card-keygen-args.txt') do set /a I=%%n-1
                set "LETTERS={Letters}"
                call set "L=%%LETTERS:~%I%,1%%"
                :knext
                if "%~1"=="" goto kprint
                if "%~1"=="--out-dir" copy /y "%P%.pk8" "%~2\key.pk8" >nul
                shift
                goto knext
                :kprint
                {keygen}
                exit /b {CardKeygenExitCode}
                :render
                >>card-args.txt echo %*
                {sleep}
                set "F=png"
                set "O="
                :rnext
                if "%~1"=="" goto rdone
                if "%~1"=="--input" copy /y "%~2" card-input.json >nul
                if "%~1"=="--signing-key" copy /y "%~2" card-key-seen.pk8 >nul
                if "%~1"=="--out-dir" set "O=%~2"
                if "%~1"=="--format" set "F=%~2"
                shift
                goto rnext
                :rdone
                if not exist "%O%\" (echo {Unwritable}& exit /b 3)
                if "{CardRenderExitCode}"=="0" copy /y "%P%.%F%" "%O%\card.%F%" >nul
                type "%P%.cardrender"
                exit /b {CardRenderExitCode}
                :verify
                >>verify-args.txt echo %*
                type "%P%.verify"
                exit /b {VerifyCardExitCode}
                :query
                {sleep}
                type "%P%.err" 1>&2
                type "%P%.out"
                exit /b {ExitCode}

                """;
    }

    private static string Quote(string value)
    {
        return $"'{value.Replace("'", "'\\''", StringComparison.Ordinal)}'";
    }
}
