using Banking.Domain;
using Microsoft.EntityFrameworkCore;

namespace Banking.Infrastructure;

public sealed class CorrespondentPostingService(IDbContextFactory<BankingDbContext> factory)
{
    public Task<Guid> OpenAsync(Guid issuerBankId, Guid respondentBankId, string accountNumber, decimal opening, CancellationToken ct) =>
        StatementTransactions.Run(factory, async db => {
            if (issuerBankId == respondentBankId || opening < 0 || decimal.Round(opening, 4) != opening
                || string.IsNullOrWhiteSpace(accountNumber) || accountNumber.Trim().Length > 34)
                throw new ArgumentException("Use distinct banks, a valid account number, and a nonnegative opening position.");
            if (await db.Banks.CountAsync(x => (x.Id == issuerBankId || x.Id == respondentBankId) && x.CountryCode == "US", ct) != 2)
                throw new ArgumentException("Select two eligible US lab banks.");
            if (await db.CorrespondentAccounts.AnyAsync(x => x.IssuerBankId == issuerBankId
                && (x.RespondentBankId == respondentBankId || x.AccountNumber == accountNumber.Trim()), ct))
                throw new ArgumentException("This account number or respondent relationship already exists at your bank.");
            var account = new CorrespondentAccount { IssuerBankId = issuerBankId, RespondentBankId = respondentBankId,
                AccountNumber = accountNumber.Trim(), OpeningBalance = opening, IssuerBalance = opening,
                RespondentBalance = opening, CreatedAt = await StatementTransactions.Now(db, ct) };
            db.CorrespondentAccounts.Add(account);
            return account.Id;
        }, ct);

    public Task<Guid> RequestWireAsync(Guid bankId, Guid accountId, Guid receiverBankId, decimal amount, string actor, CancellationToken ct) =>
        StatementTransactions.Run(factory, async db => {
            var account = await StatementTransactions.Account(db, bankId, accountId, false, ct);
            if (account.RespondentBankId != bankId) throw new ArgumentException("Only the respondent maker can request its account transfer.");
            if (amount <= 0 || decimal.Round(amount, 4) != amount || account.IssuerBalance - account.HeldBalance < amount)
                throw new ArgumentException("Insufficient available correspondent balance or invalid amount.");
            var sender = await db.Banks.SingleAsync(x => x.Id == account.IssuerBankId, ct);
            var receiver = await db.Banks.SingleOrDefaultAsync(x => x.Id == receiverBankId && x.Id != sender.Id && x.CountryCode == "US", ct)
                ?? throw new ArgumentException("Select a different domestic receiving bank.");
            var now = await StatementTransactions.Now(db, ct);
            var wire = new WireTransfer { BankId = sender.Id, SenderBankId = sender.Id, ReceiverBankId = receiver.Id,
                Status = WireStatus.PendingApproval, Rail = PaymentRail.Fedwire, Direction = WireDirection.Outgoing,
                TransferType = WireTransferType.FinancialInstitutionCreditTransfer, Amount = amount,
                SenderName = sender.Name, ReceiverName = receiver.Name, BeneficiaryAccountNumber = receiver.FedParticipantId,
                CreatedBy = actor, CreatedDate = now, CustomerReference = $"COR-{Guid.NewGuid():N}"[..35],
                Events = [new WireEvent { EventType = "Created", Description = "Respondent requested a correspondent-funded institution transfer. Issuer approval required; no account debit posted." }] };
            db.WireTransfers.Add(wire);
            db.CorrespondentPaymentLinks.Add(new CorrespondentPaymentLink { AccountId = account.Id, WireTransferId = wire.Id,
                ValueDate = DateOnly.FromDateTime(now.UtcDateTime), CreatedAt = now });
            account.HeldBalance += amount;
            return wire.Id;
        }, ct);

    public Task AssignIncomingAsync(Guid bankId, Guid accountId, Guid incomingWireId, CancellationToken ct) =>
        StatementTransactions.Run(factory, async db => {
            var account = await StatementTransactions.Account(db, bankId, accountId, true, ct);
            var wire = await db.WireTransfers.SingleOrDefaultAsync(x => x.Id == incomingWireId && x.BankId == bankId
                && x.Direction == WireDirection.Incoming && x.Rail == PaymentRail.Fedwire
                && x.TransferType == WireTransferType.FinancialInstitutionCreditTransfer, ct)
                ?? throw new ArgumentException("Select an incoming institution transfer owned by the issuer.");
            if (await db.CorrespondentPaymentLinks.AnyAsync(x => x.WireTransferId == wire.Id, ct))
                throw new ArgumentException("This payment already has a correspondent account assignment.");
            var now = await StatementTransactions.Now(db, ct);
            db.CorrespondentPaymentLinks.Add(new CorrespondentPaymentLink { AccountId = account.Id, WireTransferId = wire.Id,
                IsCredit = true, ValueDate = DateOnly.FromDateTime(wire.CreatedDate.UtcDateTime), CreatedAt = now });
            return true;
        }, ct);

    public Task AssessFeeAsync(Guid bankId, Guid accountId, decimal amount, string reference, CancellationToken ct) =>
        StatementTransactions.Run(factory, async db => {
            var account = await StatementTransactions.Account(db, bankId, accountId, true, ct);
            if (amount <= 0 || decimal.Round(amount, 4) != amount || account.IssuerBalance - account.HeldBalance < amount
                || string.IsNullOrWhiteSpace(reference) || reference.Length > 180)
                throw new ArgumentException("Use an affordable positive fee and a short reference.");
            var postingId = Guid.NewGuid();
            var now = await StatementTransactions.Now(db, ct);
            Post(db, account, account.IssuerBankId, postingId, postingId, -amount, now, DateOnly.FromDateTime(now.UtcDateTime), null, "Fee: " + reference, "FEE_INCOME");
            account.IssuerBalance -= amount;
            db.OutboxMessages.Add(Outbox.Message(StatementQueues.Posting, new CorrespondentPostingNotice(account.Id, postingId), account.RespondentBankId));
            return true;
        }, ct);

    public async Task ProcessPaymentsAsync(CancellationToken ct)
    {
        await using var probe = await factory.CreateDbContextAsync(ct);
        var ids = await probe.CorrespondentPaymentLinks.Where(x => x.Status == CorrespondentPaymentStatus.Pending).Select(x => x.Id).ToListAsync(ct);
        foreach (var id in ids)
            await StatementTransactions.Run(factory, async db => {
                var link = await db.CorrespondentPaymentLinks.Include(x => x.Account).SingleAsync(x => x.Id == id, ct);
                if (link.Status != CorrespondentPaymentStatus.Pending) return false;
                var wire = await db.WireTransfers.SingleAsync(x => x.Id == link.WireTransferId, ct);
                if (wire.Status == WireStatus.Rejected)
                {
                    if (!link.IsCredit) link.Account.HeldBalance -= wire.Amount;
                    link.Status = CorrespondentPaymentStatus.Rejected;
                    return true;
                }
                // Extract a finalized reserve ledger posting, not a wire's status-derived face amount.
                var lines = await db.LedgerEntries.Where(x => x.WireTransferId == wire.Id
                    && x.WireTransfer.BankId == link.Account.IssuerBankId).ToListAsync(ct);
                var cash = lines.Where(x => x.AccountCode == $"FEDMASTER:{link.Account.IssuerBankId:N}").ToList();
                if (cash.Count == 0) return false;
                if (cash.Count != 1 || !JournalControl.IsBalanced(lines.Where(x => x.JournalId == cash[0].JournalId).Select(x => new JournalLine(x.Debit, x.Credit))))
                    throw new InvalidOperationException("The source cash journal is ambiguous or unbalanced.");
                if (!await db.FedSettlements.AnyAsync(x => x.CorrelationId == wire.CorrelationId && x.StatusCode == "ACSC", ct)) return false;
                var delta = cash[0].Debit - cash[0].Credit;
                if (delta == 0 || link.IsCredit != (delta > 0) || Math.Abs(delta) != wire.Amount)
                    throw new InvalidOperationException("Posted cash does not match the assigned instruction.");
                if (!link.IsCredit) link.Account.HeldBalance -= -delta;
                link.Account.IssuerBalance += delta;
                var now = await StatementTransactions.Now(db, ct);
                Post(db, link.Account, link.Account.IssuerBankId, link.Id, wire.CorrelationId, delta, now, link.ValueDate,
                    wire.Id, $"{(delta > 0 ? "Incoming" : "Outgoing")} Fedwire {wire.CorrelationId}", $"FI_CLEARING:{(link.IsCredit ? wire.SenderBankId : wire.ReceiverBankId):N}");
                link.Status = CorrespondentPaymentStatus.Posted;
                db.OutboxMessages.Add(Outbox.Message(StatementQueues.Posting, new CorrespondentPostingNotice(link.AccountId, link.Id), link.Account.RespondentBankId));
                return true;
            }, ct);
    }

    public Task MirrorAsync(CorrespondentPostingNotice notice, CancellationToken ct) =>
        StatementTransactions.Run(factory, async db => {
            var account = await db.CorrespondentAccounts.SingleAsync(x => x.Id == notice.AccountId, ct);
            if (await db.CorrespondentLedgerEntries.AnyAsync(x => x.AccountId == account.Id && x.BankId == account.RespondentBankId && x.PostingId == notice.PostingId, ct)) return false;
            var source = await db.CorrespondentLedgerEntries.SingleAsync(x => x.AccountId == account.Id
                && x.BankId == account.IssuerBankId && x.PostingId == notice.PostingId && x.IsAccountLine, ct);
            var delta = source.Credit - source.Debit;
            var now = await StatementTransactions.Now(db, ct);
            Post(db, account, account.RespondentBankId, source.PostingId, source.CorrelationId, delta, now,
                source.ValueDate, source.WireTransferId, source.Reference, source.WireTransferId is null ? "FEE_EXPENSE" : "PAYMENT_CLEARING");
            account.RespondentBalance += delta;
            return true;
        }, ct);

    private static void Post(BankingDbContext db, CorrespondentAccount account, Guid bankId, Guid postingId, Guid correlation,
        decimal delta, DateTimeOffset now, DateOnly valueDate, Guid? wireId, string reference, string offset)
    {
        var issuer = bankId == account.IssuerBankId;
        var debit = issuer ? delta < 0 : delta > 0;
        var amount = Math.Abs(delta);
        var journal = Guid.NewGuid();
        var lines = new[] {
            new CorrespondentLedgerEntry { AccountId = account.Id, BankId = bankId, PostingId = postingId, JournalId = journal,
                IsAccountLine = true, AccountCode = $"{(issuer ? "DUE_TO" : "DUE_FROM")}:{account.Id:N}",
                Debit = debit ? amount : 0, Credit = debit ? 0 : amount, BookingAt = now, ValueDate = valueDate,
                WireTransferId = wireId, CorrelationId = correlation, Reference = reference },
            new CorrespondentLedgerEntry { AccountId = account.Id, BankId = bankId, PostingId = postingId, JournalId = journal,
                AccountCode = offset, Debit = debit ? 0 : amount, Credit = debit ? amount : 0, BookingAt = now,
                ValueDate = valueDate, WireTransferId = wireId, CorrelationId = correlation, Reference = reference } };
        JournalControl.RequireBalanced(lines.Select(x => new JournalLine(x.Debit, x.Credit)));
        db.CorrespondentLedgerEntries.AddRange(lines);
    }
}
