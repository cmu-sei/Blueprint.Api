// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Blueprint.Api.Data.Enumerations;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;

namespace Blueprint.Api.Automation.Handlers
{
    /// <summary>send_email, through the SMTP server Automation:Email names. Ported from Steamfitter's EmailService, without attachments.</summary>
    public class EmailActionHandler : IAutomationActionHandler
    {
        private readonly IOptionsMonitor<AutomationOptions> _options;

        public EmailActionHandler(IOptionsMonitor<AutomationOptions> options)
        {
            _options = options;
        }

        public IReadOnlyCollection<AutomationAction> Actions { get; } = new[] { AutomationAction.send_email };

        public async Task<ActionOutcome> ExecuteAsync(AutomationActionContext context, CancellationToken ct)
        {
            var options = _options.CurrentValue.Email;
            if (string.IsNullOrEmpty(options.SmtpHost))
                throw new AutomationConfigurationException("Email tasks are off: Automation:Email:SmtpHost is not set.");
            var from = !string.IsNullOrEmpty(context.Get("EmailFrom")) ? context.Get("EmailFrom") : options.DefaultFromAddress;
            if (string.IsNullOrEmpty(from))
                throw new AutomationConfigurationException("The sender is required: set the task's From, or Automation:Email:DefaultFromAddress.");

            var message = new MimeMessage();
            message.From.Add(MailboxAddress.Parse(from));
            foreach (var to in Split(context.Get("EmailTo")))
                message.To.Add(MailboxAddress.Parse(to));
            foreach (var cc in Split(context.Get("EmailCC")))
                message.Cc.Add(MailboxAddress.Parse(cc));
            if (message.To.Count == 0)
                throw new AutomationConfigurationException("The email has no recipient.");
            message.Subject = context.Get("Subject") ?? string.Empty;
            var body = new BodyBuilder();
            if (string.Equals(context.Get("Mime"), "html", StringComparison.OrdinalIgnoreCase))
                body.HtmlBody = context.Get("Message");
            else
                body.TextBody = context.Get("Message");
            message.Body = body.ToMessageBody();

            using var client = new SmtpClient();
            if (options.AcceptAllCertificates)
                client.ServerCertificateValidationCallback = (_, _, _, _) => true;
            await client.ConnectAsync(options.SmtpHost, options.SmtpPort,
                options.UseStartTls ? SecureSocketOptions.StartTlsWhenAvailable : SecureSocketOptions.Auto, ct);
            if (!string.IsNullOrEmpty(options.SmtpUsername))
                await client.AuthenticateAsync(options.SmtpUsername, options.SmtpPassword, ct);
            await client.SendAsync(message, ct);
            await client.DisconnectAsync(true, ct);
            return new ActionOutcome($"Sent to {string.Join(", ", message.To.Mailboxes.Select(m => m.Address))}.", true);
        }

        private static IEnumerable<string> Split(string s) => string.IsNullOrEmpty(s)
            ? Enumerable.Empty<string>()
            : s.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
