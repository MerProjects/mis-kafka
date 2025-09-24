// Kafka_Producer/Program.cs
using System;
using System.Data;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Confluent.Kafka;
using Dapper;
using Microsoft.Data.SqlClient;

namespace Kafka_Producer
{
    class Program
    {
        static async Task Main()
        {
            var cs = "Server=HO-LP-MNGALO;Database=MIS;Trusted_Connection=True;TrustServerCertificate=True;";
            var bootstrap = "10.0.18.216:9092";
            var batch = 100;

            var kcfg = new ProducerConfig
            {
                BootstrapServers = bootstrap,
                EnableIdempotence = true,
                Acks = Acks.All,
                LingerMs = 5,
                CompressionType = CompressionType.Lz4
            };
            using var producer = new ProducerBuilder<string, string>(kcfg).Build();

            var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

            while (!cts.IsCancellationRequested)
            {
                await using var con = new SqlConnection(cs);
                await con.OpenAsync(cts.Token);

                // A1: claim a batch safely
                var toProduce = await con.QueryAsync<OutboxRow>(
@";WITH cte AS (
    SELECT TOP (@batch) *
    FROM dbo.Outbox WITH (READPAST, UPDLOCK, ROWLOCK)
    WHERE Status IN ('Pending','Error') AND NextAttemptAt <= SYSUTCDATETIME()
    ORDER BY CreatedAt
)
UPDATE cte
SET Status='Producing',
    AttemptCount = AttemptCount + 1,
    LastError = NULL
OUTPUT inserted.*;",
                    new { batch });

                foreach (var m in toProduce)
                {
                    if (cts.IsCancellationRequested) break;

                    try
                    {
                        // B1: carry OutboxId for consumer idempotency
                        var msg = new Message<string, string>
                        {
                            Key = m.KafkaKey,
                            Value = m.Payload,
                            Headers = new Headers {
                                new Header("outbox-id", Encoding.UTF8.GetBytes(m.Id.ToString()))
                            }
                        };

                        // B2: respect per-row topic
                        var dr = await producer.ProduceAsync(m.Topic, msg, cts.Token);

                        await con.ExecuteAsync(
@"UPDATE dbo.Outbox
  SET Status='Produced',
      ProducedAt=SYSUTCDATETIME(),
      KafkaPartition=@p,
      KafkaOffset=@o,
      LastError=NULL
  WHERE Id=@id;",
                            new { id = m.Id, p = dr.Partition.Value, o = dr.Offset.Value });

                        Console.WriteLine($"Produced {dr.TopicPartitionOffset} id={m.Id}");
                    }
                    catch (ProduceException<string, string> ex)
                    {
                        // exponential backoff
                        await con.ExecuteAsync(
@"UPDATE dbo.Outbox
  SET Status='Error',
      LastError=@err,
      NextAttemptAt = DATEADD(SECOND, POWER(2, IIF(AttemptCount+1>6,6,AttemptCount+1)), SYSUTCDATETIME())
  WHERE Id=@id;",
                            new { id = m.Id, err = ex.Error.Reason });

                        Console.Error.WriteLine($"Produce failed id={m.Id}: {ex.Error.Reason}");
                        break; // likely broker issue → backoff to next loop
                    }
                }

                await Task.Delay(TimeSpan.FromSeconds(3), cts.Token);
            }

            producer.Flush(TimeSpan.FromSeconds(10));
        }

        private sealed record OutboxRow(
            Guid Id, string Topic, string KafkaKey, string Payload, string Status,
            int AttemptCount, string? LastError, DateTime CreatedAt, DateTime? ProducedAt,
            DateTime? ConfirmedAt, int? KafkaPartition, long? KafkaOffset, DateTime NextAttemptAt);
    }
}
