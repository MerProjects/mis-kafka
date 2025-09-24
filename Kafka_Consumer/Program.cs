// Kafka_Consumer/Program.cs
using System;
using System.Data;
using System.Text;
using System.Text.Json;
using System.Threading;
using Confluent.Kafka;
using Dapper;
using Microsoft.Data.SqlClient;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace Kafka_Consumer
{
    class Program
    {
        static void Main()
        {
            var cs = "Server=HO-LP-MNGALO;Database=MIS;Trusted_Connection=True;TrustServerCertificate=True;";
            var bootstrap = "10.0.18.216:9092";
            var topic = "orders";
            var group = "orders-worker";

            var smtpCfg = LoadSmtp(cs);
            var emailSender = new SmtpEmailSender(smtpCfg);

            var ccfg = new ConsumerConfig
            {
                BootstrapServers = bootstrap,
                GroupId = group,
                AutoOffsetReset = AutoOffsetReset.Earliest,
                EnableAutoCommit = false,
                EnablePartitionEof = true
            };

            using var c = new ConsumerBuilder<string, string>(ccfg).Build();
            c.Subscribe(topic);

            var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

            try
            {
                while (!cts.IsCancellationRequested)
                {
                    var cr = c.Consume(cts.Token);
                    if (cr == null || cr.IsPartitionEOF) continue;

                    if (!cr.Message.Headers.TryGetLastBytes("outbox-id", out var hdr) || hdr is null)
                    {
                        Console.Error.WriteLine($"Missing outbox-id header for {cr.TopicPartitionOffset}. Skipping.");
                        continue;
                    }
                    var outboxId = Guid.Parse(Encoding.UTF8.GetString(hdr));

                    using var con = new SqlConnection(cs);
                    con.Open();

                    // 1) Reserve (idempotent)
                    using (var tx = con.BeginTransaction())
                    {
                        con.Execute(@"
IF NOT EXISTS (SELECT 1 FROM dbo.EmailDispatch WHERE OutboxId=@id)
    INSERT dbo.EmailDispatch(OutboxId, Status) VALUES (@id,'Reserved');",
                            new { id = outboxId }, tx);

                        var status = con.ExecuteScalar<string>(
                            "SELECT Status FROM dbo.EmailDispatch WHERE OutboxId=@id",
                            new { id = outboxId }, tx);

                        if (string.Equals(status, "Sent", StringComparison.OrdinalIgnoreCase))
                        {
                            con.Execute(@"
UPDATE dbo.Outbox
SET Status='Confirmed', ConfirmedAt=SYSUTCDATETIME()
WHERE Id=@id AND Status IN ('Produced','Confirmed');",
                                new { id = outboxId }, tx);

                            tx.Commit();
                            c.Commit(cr);
                            Console.WriteLine($"Dedup confirm {cr.TopicPartitionOffset} id={outboxId}");
                            continue;
                        }

                        tx.Commit();
                    }

                    // 2) Side effect: send MS365 email
                    var providerMsgId = SendEmail(emailSender, outboxId, cr.Message.Value);

                    // 3) Mark Sent + Confirm, then commit offset
                    using (var tx = con.BeginTransaction())
                    {
                        con.Execute(@"
UPDATE dbo.EmailDispatch
SET Status='Sent', SentAt=SYSUTCDATETIME(), ProviderMessageId=@pmid
WHERE OutboxId=@id;",
                            new { id = outboxId, pmid = providerMsgId }, tx);

                        con.Execute(@"
UPDATE dbo.Outbox
SET Status='Confirmed', ConfirmedAt=SYSUTCDATETIME()
WHERE Id=@id AND Status IN ('Produced','Confirmed');",
                            new { id = outboxId }, tx);

                        tx.Commit();
                    }

                    c.Commit(cr);
                    Console.WriteLine($"Processed {cr.TopicPartitionOffset} id={outboxId}");
                }
            }
            catch (OperationCanceledException) { }
            finally { c.Close(); }
        }

        // payload JSON → { "to": "...", "subject": "...", "body": "..." }
        static string SendEmail(SmtpEmailSender sender, Guid outboxId, string payloadJson)
        {
            var payload = JsonSerializer.Deserialize<EmailPayload>(
                payloadJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

            if (string.IsNullOrWhiteSpace(payload.To)) return "invalid-to";
            var safeSubject = string.IsNullOrWhiteSpace(payload.Subject) ? "(no subject)" : payload.Subject;
            var safeBody = payload.Body ?? string.Empty;

            return sender.Send(outboxId, payload with { Subject = safeSubject, Body = safeBody });
        }


        static SmtpSettings LoadSmtp(string cs)
        {
            const string sql = @"
SELECT TOP 1 host, port, username, password, fromAddress, fromName, useStartTls
FROM dbo.SmtpConfig ORDER BY 1";
            using var con = new SqlConnection(cs);
            var row = con.QuerySingle(sql);
            return new SmtpSettings(
                (string)row.host, (int)row.port, (string)row.username, (string)row.password,
                (string)row.fromAddress, (string)row.fromName, (bool)row.useStartTls);
        }
    }

    public sealed record EmailPayload(string To, string Subject, string Body);

    public sealed record SmtpSettings(
        string Host, int Port, string Username, string Password,
        string FromAddress, string FromName, bool UseStartTls);

    public sealed class SmtpEmailSender
    {
        private readonly SmtpSettings _cfg;
        public SmtpEmailSender(SmtpSettings cfg) => _cfg = cfg;

        // Sync variant to keep Main() non-async
        public string Send(Guid outboxId, EmailPayload payload)
        {
            var msg = new MimeMessage();
            msg.MessageId = $"<{outboxId}@outbox>";                 // idempotency marker
            msg.Headers.Add("X-Outbox-Id", outboxId.ToString());    // extra dedupe signal
            msg.From.Add(new MailboxAddress(_cfg.FromName, _cfg.FromAddress));
            msg.To.Add(MailboxAddress.Parse(payload.To));
            msg.Subject = payload.Subject;
            msg.Body = new TextPart("plain") { Text = payload.Body };

            using var smtp = new SmtpClient();
            var ssl = _cfg.UseStartTls ? SecureSocketOptions.StartTls : SecureSocketOptions.Auto;
            smtp.Connect(_cfg.Host, _cfg.Port, ssl);                // MS365: smtp.office365.com:587 + StartTLS
            smtp.Authenticate(_cfg.Username, _cfg.Password);        // SMTP AUTH must be enabled on the mailbox
            smtp.Send(msg);
            smtp.Disconnect(true);

            return msg.MessageId ?? outboxId.ToString();
        }
    }
}
