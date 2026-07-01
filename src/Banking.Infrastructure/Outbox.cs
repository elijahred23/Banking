using System.Text.Json;
using Banking.Domain;

namespace Banking.Infrastructure;

public static class Outbox
{
    public static OutboxMessage Message<T>(string queue, T message) => new()
    {
        Queue = queue,
        Payload = JsonSerializer.Serialize(message)
    };
}
