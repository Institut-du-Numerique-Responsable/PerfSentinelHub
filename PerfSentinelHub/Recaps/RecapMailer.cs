using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Options;
using PerfSentinelHub.Configuration;

namespace PerfSentinelHub.Recaps;

/// <summary>The only class that touches <see cref="SmtpClient" />: it hands one message to the configured relay.</summary>
public sealed class RecapMailer(IOptions<HubOptions> options)
{
    // A relay can take its time over an attachment, and Hub:HttpTimeout is sized for a daemon.
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    /// <summary>250 when sent, the relay's reply code when it refused, null when nothing answered.</summary>
    public async Task<int?> SendAsync(MailMessage message, CancellationToken cancellationToken)
    {
        var smtp = options.Value.Recaps.Smtp;
        using var client = new SmtpClient(smtp.Host, smtp.Port)
        {
            EnableSsl = smtp.StartTls,
            DeliveryMethod = SmtpDeliveryMethod.Network,
            // Held for "login" alone: a plain credential is offered to GSSAPI and NTLM first, and the image,
            // which has no libgssapi, fails on a relay that advertises them instead of falling back to LOGIN.
            Credentials = smtp.UserName is null
                ? null
                : new CredentialCache
                {
                    { smtp.Host!, smtp.Port, "login", new NetworkCredential(smtp.UserName, smtp.Password) }
                }
        };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        try
        {
            await client.SendMailAsync(message, timeout.Token);
            return 250;
        }
        catch (SmtpException exception) when (exception.StatusCode != SmtpStatusCode.GeneralFailure)
        {
            return (int)exception.StatusCode;
        }
        catch (Exception exception) when (exception is SmtpException or IOException
                                              || (exception is OperationCanceledException
                                                  && !cancellationToken.IsCancellationRequested))
        {
            return null;
        }
    }
}
