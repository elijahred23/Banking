using System.Text.Json;
using Banking.Domain;

namespace Banking.Infrastructure;

public static class Outbox
{
    public static OutboxMessage Message<T>(string queue, T message, Guid? bankId = null) => new()
    {
        Queue = queue,
        BankId = bankId,
        Payload = JsonSerializer.Serialize(message)
    };
}
