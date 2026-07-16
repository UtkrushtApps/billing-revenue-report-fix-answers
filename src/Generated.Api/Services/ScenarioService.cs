using Generated.Api.Data;
using Generated.Api.Dtos;
using Microsoft.EntityFrameworkCore;
using System.Globalization;

namespace Generated.Api.Services;

public sealed class RevenueReportService
{
    private readonly AppDbContext _db;
    private readonly ILogger<RevenueReportService> _logger;

    public RevenueReportService(AppDbContext db, ILogger<RevenueReportService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<RevenueReportResponse> GetRevenueReportAsync(string month, CancellationToken cancellationToken)
    {
        if (!DateTime.TryParseExact(month, "yyyy-MM", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var reportMonth))
        {
            throw new ArgumentException("Month must be formatted as yyyy-MM.", nameof(month));
        }

        // Interpret report month in each customer's *local* timezone.
        var monthStartLocal = new DateTime(reportMonth.Year, reportMonth.Month, 1, 0, 0, 0, DateTimeKind.Unspecified);
        var monthEndLocal = monthStartLocal.AddMonths(1);

        _logger.LogInformation("Building revenue report for {ReportMonth}", month);

        // Business rules (as required to match the finance ledger):
        // - Invoiced / "belongs to month" is determined by invoice issue timestamp converted to the customer's local timezone.
        // - Paid/Credited totals are attributed to the invoices in that month (payment/credit dates do NOT filter).
        //   This is critical for split payments and for credits/payments posted in a different month.

        var selectedInvoices =
            from inv in _db.Invoices
            join cust in _db.Customers on inv.CustomerId equals cust.Id
            where inv.IssuedAtUtc.AddMinutes(cust.BillingTimeZoneOffsetMinutes) >= monthStartLocal
               && inv.IssuedAtUtc.AddMinutes(cust.BillingTimeZoneOffsetMinutes) < monthEndLocal
            select new
            {
                InvoiceId = inv.Id,
                CustomerId = cust.Id,
                AccountNumber = cust.AccountNumber,
                CustomerName = cust.Name,
                Region = cust.Region
            };

        var customerKeys = await selectedInvoices
            .GroupBy(x => new { x.CustomerId, x.AccountNumber, x.CustomerName, x.Region })
            .Select(g => g.Key)
            .OrderBy(x => x.CustomerName)
            .ToListAsync(cancellationToken);

        if (customerKeys.Count == 0)
        {
            var emptyTotals = new RevenueTotalsDto(0m, 0m, 0m, 0m, 0);
            return new RevenueReportResponse(month, DateTime.UtcNow, Array.Empty<CustomerRevenueDto>(), emptyTotals);
        }

        var invoicedRows = await (from si in selectedInvoices
                                   join line in _db.InvoiceLines on si.InvoiceId equals line.InvoiceId
                                   group line.Amount by si.CustomerId into g
                                   select new { CustomerId = g.Key, InvoicedAmount = g.Sum() })
            .ToListAsync(cancellationToken);

        var paidRows = await (from si in selectedInvoices
                              join p in _db.Payments on si.InvoiceId equals p.InvoiceId
                              group p.Amount by si.CustomerId into g
                              select new { CustomerId = g.Key, PaidAmount = g.Sum() })
            .ToListAsync(cancellationToken);

        var creditedRows = await (from si in selectedInvoices
                                 join c in _db.CreditNotes on si.InvoiceId equals c.InvoiceId
                                 group c.Amount by si.CustomerId into g
                                 select new { CustomerId = g.Key, CreditedAmount = g.Sum() })
            .ToListAsync(cancellationToken);

        var invoicedByCustomer = invoicedRows.ToDictionary(x => x.CustomerId, x => x.InvoicedAmount);
        var paidByCustomer = paidRows.ToDictionary(x => x.CustomerId, x => x.PaidAmount);
        var creditedByCustomer = creditedRows.ToDictionary(x => x.CustomerId, x => x.CreditedAmount);

        var rows = new List<CustomerRevenueDto>(customerKeys.Count);
        foreach (var c in customerKeys)
        {
            var invoicedAmount = invoicedByCustomer.TryGetValue(c.CustomerId, out var invAmt) ? invAmt : 0m;
            var paidAmount = paidByCustomer.TryGetValue(c.CustomerId, out var paidAmt) ? paidAmt : 0m;
            var creditedAmount = creditedByCustomer.TryGetValue(c.CustomerId, out var credAmt) ? credAmt : 0m;
            var netRevenue = invoicedAmount - creditedAmount;

            rows.Add(new CustomerRevenueDto(
                c.CustomerId,
                c.AccountNumber,
                c.CustomerName,
                c.Region,
                invoicedAmount,
                paidAmount,
                creditedAmount,
                netRevenue));
        }

        var totals = new RevenueTotalsDto(
            rows.Sum(x => x.InvoicedAmount),
            rows.Sum(x => x.PaidAmount),
            rows.Sum(x => x.CreditedAmount),
            rows.Sum(x => x.NetRevenue),
            rows.Count);

        // Preserve expected JSON ordering.
        // (customerKeys are already ordered by CustomerName.)
        return new RevenueReportResponse(month, DateTime.UtcNow, rows, totals);
    }
}
