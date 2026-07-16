# Solution Steps

1. Update the revenue report logic to match month-end ledger attribution rules.

2. Interpret the requested `month=yyyy-MM` as a local month per customer (use `BillingTimeZoneOffsetMinutes`). Select invoices where `(IssuedAtUtc + offset)` falls within the local month range.

3. Compute each customer’s `InvoicedAmount` by summing invoice line amounts for the selected invoices.

4. Compute `PaidAmount` by summing *all* payments linked to the selected invoices (do not filter by `PaidAtUtc`). This fixes split payments and payments crossing month boundaries.

5. Compute `CreditedAmount` by summing *all* credit notes linked to the selected invoices (do not filter by `PostedAtUtc`). This fixes credits posted after the reporting month.

6. Ensure customers are included as long as they have at least one invoice in the selected local month; payments/credits may be zero.

7. Keep the JSON contract unchanged: return `RevenueReportResponse` with `Customers` ordered by customer name, and `Totals` values matching the sums (including `CustomerCount = Customers.Count`).

8. Keep logs safe for financial data: log only non-sensitive identifiers like the requested report month/path; avoid logging monetary values.

