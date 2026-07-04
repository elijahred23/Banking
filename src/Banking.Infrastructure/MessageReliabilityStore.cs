using System.Security.Cryptography;
using System.Text.Json;
using Banking.Domain;
using Microsoft.EntityFrameworkCore;

namespace Banking.Infrastructure;

public sealed record InboxAttempt(Guid InboxId, bool AlreadyCompleted, int Attempts);

public sealed class MessageReliabilityStore(IDbContextFactory<BankingDbContext> dbFactory)
{
    public static string PayloadHash(ReadOnlySpan<byte> body) =>
        Convert.ToHexString(SHA256.HashData(body));

    public async Task<InboxAttempt> BeginAttemptAsync(string queue, string messageId,
        byte[] body, CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var inbox = await db.ConsumerInboxMessages.SingleOrDefaultAsync(
            x => x.Queue == queue && x.MessageId == messageId, token);
        if (inbox is null)
        {
            inbox = new ConsumerInboxMessage
            {
                Queue = queue,
                MessageId = messageId,
                PayloadHash = PayloadHash(body),
                BankId = await ResolveBankIdAsync(db, body, token)
            };
            db.ConsumerInboxMessages.Add(inbox);
        }
        if (inbox.CompletedDate is not null)
            return new InboxAttempt(inbox.Id, true, inbox.Attempts);

        inbox.Attempts++;
        inbox.LastAttemptDate = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(token);
        return new InboxAttempt(inbox.Id, false, inbox.Attempts);
    }

    public async Task CompleteAsync(Guid inboxId, CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var inbox = await db.ConsumerInboxMessages.SingleAsync(x => x.Id == inboxId, token);
        inbox.CompletedDate ??= DateTimeOffset.UtcNow;
        inbox.LastError = null;
        await db.SaveChangesAsync(token);
    }

    public async Task<bool> FailAsync(Guid inboxId, string payload, Exception exception,
        int maximumAttempts, CancellationToken token)
    {
        await using var db = await dbFactory.CreateDbContextAsync(token);
        var inbox = await db.ConsumerInboxMessages.SingleAsync(x => x.Id == inboxId, token);
        inbox.LastError = Truncate(exception.Message);
        if (inbox.Attempts < maximumAttempts)
        {
            await db.SaveChangesAsync(token);
            return false;
        }

        var exists = await db.DeadLetterMessages.AnyAsync(x =>
            x.Queue == inbox.Queue && x.MessageId == inbox.MessageId && x.ReplayedDate == null, token);
        if (!exists)
            db.DeadLetterMessages.Add(new DeadLetterMessage
            {
                ConsumerInboxMessageId = inbox.Id,
                BankId = inbox.BankId,
                Queue = inbox.Queue,
                MessageId = inbox.MessageId,
                Payload = payload,
                Error = inbox.LastError,
                Attempts = inbox.Attempts
            });
        await db.SaveChangesAsync(token);
        return true;
    }

    private static string Truncate(string value) => value.Length <= 1000 ? value : value[..1000];

    private static async Task<Guid?> ResolveBankIdAsync(BankingDbContext db, byte[] body,
        CancellationToken token)
    {
        JsonElement root;
        try { root = JsonSerializer.Deserialize<JsonElement>(body); }
        catch (JsonException) { return null; }

        if (FindGuid(root, "BankId") is { } direct) return direct;
        if (FindGuid(root, "OriginatingBankId") is { } origin) return origin;
        if (FindGuid(root, "DepositoryBankId") is { } depository) return depository;
        if (FindGuid(root, "SenderBankId") is { } sender) return sender;
        if (FindGuid(root, "WireId") is { } wireId)
            return await db.WireTransfers.Where(x => x.Id == wireId)
                .Select(x => (Guid?)x.BankId).SingleOrDefaultAsync(token);
        if (FindGuid(root, "EntryId") is { } entryId)
            return await db.AchEntries.Where(x => x.Id == entryId)
                .Select(x => (Guid?)x.OriginatingBankId).SingleOrDefaultAsync(token);
        if (FindGuid(root, "FileId") is { } fileId)
            return await db.AchFiles.Where(x => x.Id == fileId)
                .Select(x => (Guid?)x.OriginatingBankId).SingleOrDefaultAsync(token);
        if (FindGuid(root, "CheckDepositId") is { } checkId)
            return await db.CheckDeposits.Where(x => x.Id == checkId)
                .Select(x => (Guid?)x.DepositoryBankId).SingleOrDefaultAsync(token);
        if (FindGuid(root, "CashLetterId") is { } letterId)
            return await db.CheckCashLetters.Where(x => x.Id == letterId)
                .Select(x => (Guid?)x.DepositoryBankId).SingleOrDefaultAsync(token);
        return null;
    }

    private static Guid? FindGuid(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object)
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                    && property.Value.ValueKind == JsonValueKind.String
                    && Guid.TryParse(property.Value.GetString(), out var value)) return value;
                if (FindGuid(property.Value, name) is { } nested) return nested;
            }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray())
                if (FindGuid(item, name) is { } nested) return nested;
        return null;
    }
}
