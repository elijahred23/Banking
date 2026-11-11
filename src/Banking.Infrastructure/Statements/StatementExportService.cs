using System.Globalization;
using System.Text;
using Banking.Domain;

namespace Banking.Infrastructure;

/// <summary>Deterministic exports of the stored snapshot; never queries live payment status.</summary>
public sealed class StatementExportService
{
    public byte[] Csv(AccountStatement statement)
    {
        static string Cell(string value)
        {
            if (value.TrimStart().FirstOrDefault() is '=' or '+' or '-' or '@') value = "'" + value;
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }
        string Money(decimal value) => value.ToString("0.0000", CultureInfo.InvariantCulture);
        var csv = new StringBuilder();
        void Row(params string[] cells) => csv.AppendLine(string.Join(',', cells.Select(Cell)));
        Row("Statement number", statement.StatementNumber); Row("Issuer", statement.IssuerName);
        Row("Respondent", statement.RecipientName); Row("Account", statement.AccountNumber);
        Row("Message type", StatementPolicy.MessageType(statement.Kind)); Row("Version", statement.Version.ToString(CultureInfo.InvariantCulture));
        Row("Period start", statement.PeriodStart.ToString("O")); Row("Period end", statement.PeriodEnd.ToString("O"));
        Row("Corrects", statement.PreviousStatementId?.ToString() ?? "");
        if (statement.Kind != StatementKind.Notification) Row("Opening balance USD", Money(statement.OpeningBalance));
        Row("Total debits USD", Money(statement.TotalDebits)); Row("Total credits USD", Money(statement.TotalCredits));
        if (statement.Kind != StatementKind.Notification) Row("Closing balance USD", Money(statement.ClosingBalance));
        Row("Booking timestamp", "Value date", "Posting reference", "Payment correlation", "Description", "Debit USD", "Credit USD");
        foreach (var entry in statement.Entries.OrderBy(x => x.BookingAt).ThenBy(x => x.PostingId))
            Row(entry.BookingAt.ToString("O"), entry.ValueDate.ToString("yyyy-MM-dd"), entry.PostingId.ToString(), entry.CorrelationId.ToString(), entry.Reference,
                entry.IsCredit ? "" : Money(entry.Amount), entry.IsCredit ? Money(entry.Amount) : "");
        return Encoding.UTF8.GetBytes(csv.ToString());
    }
    public byte[] Pdf(AccountStatement statement)
    {
        var pages = new List<StringBuilder>();
        StringBuilder page = null!;
        double y = 0;
        static string Plain(string value) => new(value.Normalize(NormalizationForm.FormD)
            .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            .Select(c => c is >= ' ' and <= '~' ? c : ' ').ToArray());
        static string Escape(string value) => value.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");
        void Text(string value, double x, double atY, double size = 9, string font = "F1", string color = "0.12 0.17 0.23") =>
            page.Append(FormattableString.Invariant($"{color} rg BT /{font} {size} Tf {x} {atY} Td ({Escape(Plain(value))}) Tj ET\n"));
        void Rule(double atY) => page.Append(FormattableString.Invariant($"0.82 0.86 0.90 RG 0.5 w 40 {atY} m 572 {atY} l S\n"));
        string Money(decimal value) => value.ToString("N4", CultureInfo.InvariantCulture);
        void Right(string value, double right, double atY) => Text(value, right - Plain(value).Length * 9 * .6, atY);
        void NewPage()
        {
            page = new StringBuilder(); pages.Add(page);
            page.Append("0.07 0.15 0.25 rg 0 698 612 94 re f\n");
            Text("CORRESPONDENT ACCOUNT REPORTING", 40, 763, 10, "F2", "1 1 1");
            Text(statement.IssuerName[..Math.Min(60, statement.IssuerName.Length)], 40, 738, 16, "F2", "1 1 1");
            Text(StatementPolicy.MessageType(statement.Kind) + " | USD | LEARNING SIMULATOR", 40, 716, 9, "F1", "0.8 0.9 1");
            Text(statement.RecipientName[..Math.Min(70, statement.RecipientName.Length)], 40, 676, 12, "F2");
            Text("Account " + statement.AccountNumber + " | Version " + statement.Version, 40, 658);
            Text(statement.StatementNumber, 40, 641, 7);
            Text($"Period: {statement.PeriodStart:yyyy-MM-dd HH:mm zzz} to {statement.PeriodEnd:yyyy-MM-dd HH:mm zzz}", 40, 625, 8);
            if (statement.PreviousStatementId is Guid previous) Text("Corrects statement " + previous, 40, 610, 8);
            Text(statement.Kind == StatementKind.Notification ? "ENTRIES" : "OPENING", 40, 586, 8, "F2"); Text("DEBITS", 177, 586, 8, "F2");
            Text("CREDITS", 314, 586, 8, "F2"); Text(statement.Kind == StatementKind.Notification ? "NET ENTRY" : "CLOSING", 451, 586, 8, "F2");
            Text(statement.Kind == StatementKind.Notification ? statement.Entries.Count.ToString(CultureInfo.InvariantCulture) : Money(statement.OpeningBalance), 40, 568, 9); Text(Money(statement.TotalDebits), 177, 568, 9);
            Text(Money(statement.TotalCredits), 314, 568, 9); Text(Money(statement.Kind == StatementKind.Notification ? statement.TotalCredits - statement.TotalDebits : statement.ClosingBalance), 451, 568, 9);
            Text(statement.GenerationDifference == 0 ? "Booked snapshot - simulated funds" : "HELD - balance validation exception", 40, 545, 9, "F2");
            Rule(534); Text("BOOKING / VALUE", 40, 519, 8, "F2"); Text("REFERENCE", 153, 519, 8, "F2");
            Text("DEBIT USD", 402, 519, 8, "F2"); Text("CREDIT USD", 510, 519, 8, "F2"); Rule(509);
            y = 491;
        }
        NewPage();
        foreach (var entry in statement.Entries.OrderBy(x => x.BookingAt).ThenBy(x => x.PostingId))
        {
            var reference = Plain(entry.Reference);
            var chunks = Enumerable.Range(0, Math.Max(1, (reference.Length + 37) / 38))
                .Select(i => reference.Substring(i * 38, Math.Min(38, reference.Length - i * 38))).ToList();
            var height = Math.Max(36, chunks.Count * 12 + 10);
            if (y - height < 78) NewPage();
            Text(entry.BookingAt.ToString("yyyy-MM-dd"), 40, y, 8);
            Text(entry.ValueDate.ToString("yyyy-MM-dd"), 40, y - 12, 8);
            for (var i = 0; i < chunks.Count; i++) Text(chunks[i], 153, y - i * 12, 8);
            if (entry.IsCredit) Right(Money(entry.Amount), 572, y); else Right(Money(entry.Amount), 464, y);
            y -= height; Rule(y + 14);
        }
        if (statement.Entries.Count == 0) Text("No booked transactions in this interval.", 40, y, 10);
        for (var i = 0; i < pages.Count; i++)
        {
            page = pages[i]; Rule(59);
            Text(statement.Kind == StatementKind.Notification ? "Single-entry booked notification. No full-period balance asserted." : "Opening + credits - debits = closing. Not a certified bank statement.", 40, 44, 7);
            Text($"Page {i + 1} of {pages.Count}", 500, 30, 8);
        }
        var objects = new List<string> {
            "<< /Type /Catalog /Pages 2 0 R >>", "",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Courier >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold >>" };
        var kids = new List<string>();
        foreach (var content in pages)
        {
            var id = objects.Count + 1; kids.Add($"{id} 0 R");
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 3 0 R /F2 4 0 R >> >> /Contents {id + 1} 0 R >>");
            var stream = content.ToString();
            objects.Add($"<< /Length {Encoding.ASCII.GetByteCount(stream)} >>\nstream\n{stream}endstream");
        }
        objects[1] = $"<< /Type /Pages /Count {pages.Count} /Kids [{string.Join(' ', kids)}] >>";
        using var output = new MemoryStream();
        void Write(string value) { var bytes = Encoding.ASCII.GetBytes(value); output.Write(bytes); }
        Write("%PDF-1.4\n");
        var offsets = new List<long> { 0 };
        for (var i = 0; i < objects.Count; i++) { offsets.Add(output.Position); Write($"{i + 1} 0 obj\n{objects[i]}\nendobj\n"); }
        var xref = output.Position;
        Write($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets.Skip(1)) Write($"{offset:D10} 00000 n \n");
        Write($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return output.ToArray();
    }
}
