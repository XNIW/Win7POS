using System;
using System.Collections.Generic;
using Win7POS.Core.Import;

namespace Win7POS.Data.Import
{
    internal sealed class GetnetModernProfileAdapter : GetnetProfileAdapter
    {
        internal override HistoricalSalesProfile Profile => HistoricalSalesProfile.GetnetModern;
        internal override void ValidateHeaders(GetnetHeaderMap header)
        {
            header.Require("FECHA DE VENTA", "ID FINANCIERO DEL MOVIMIENTO", "MONTO BRUTO", "ESTADO TRX", "TIPO MOV.", "MONEDA", "ZONA HORARIA (UTC)");
            if (!header.Has("VALOR TRANSACCION") && !header.Has("VALOR VENTA")) throw new GetnetImportException("getnet_header_missing", "VALOR TRANSACCION / VALOR VENTA", header.RowNumber);
            if (!header.Has("LOCAL") && !header.Has("VENDEDOR") && !header.Has("SELLER ID") && !header.Has("RUT") && !header.Has("NUM LOCAL"))
                throw new GetnetImportException("getnet_header_missing", "MERCHANT", header.RowNumber);
        }

        internal override HistoricalSalesTransaction Read(GetnetRowContext row, HistoricalSalesImportOptions options, List<HistoricalSalesRejectedRow> rejected)
        {
            var status = row.Text("ESTADO TRX"); var normalizedStatus = HistoricalSalesValueRules.NormalizeHeader(status);
            if (normalizedStatus != "RECHAZADA" && normalizedStatus != "CONFIRMADA") throw row.Error("getnet_transaction_state_unknown", "ESTADO TRX");
            // Requested amounts of declined payments are CLP diagnostics only
            // when their currency is proven too; they never require a financial ID.
            if (HistoricalSalesValueRules.NormalizeHeader(row.Text("MONEDA")) != "CLP") throw row.Error("getnet_currency_unsupported", "MONEDA");
            // A rejected authorization has no accepted financial movement ID. Classify before accepted-row requirements.
            if (normalizedStatus == "RECHAZADA")
            {
                long? requested = null;
                try { requested = row.Has("VALOR TRANSACCION") ? row.Money("VALOR TRANSACCION") : row.Money("VALOR VENTA"); }
                catch (GetnetImportException) { /* Coverage remains explicitly unknown; no revenue is inferred. */ }
                rejected.Add(new HistoricalSalesRejectedRow(row.Source, status, requested));
                return null;
            }
            var rawMovement = HistoricalSalesValueRules.NormalizeHeader(row.Text("TIPO MOV."));
            var kind = rawMovement == "VENTA" ? HistoricalSalesMovementKind.Sale : rawMovement == "ANULACION" ?
                HistoricalSalesMovementKind.Cancellation : throw row.Error("getnet_movement_unknown", "TIPO MOV.");
            var gross = row.Money("MONTO BRUTO", true).Value;
            if ((kind == HistoricalSalesMovementKind.Sale && gross <= 0) || (kind == HistoricalSalesMovementKind.Cancellation && gross >= 0))
                throw row.Error("getnet_movement_sign_invalid", "MONTO BRUTO");
            foreach (var alias in new[] { "VALOR TRANSACCION", "VALOR VENTA" })
                if (row.Has(alias) && row.Money(alias, true).Value != gross) throw row.Error("getnet_amount_alias_mismatch", alias);
            row.RequireZeroIfProvided("PROPINA", "VUELTO", "COMISION PROPINA");
            var settlement = row.Text("ESTADO DEL ABONO");
            var settled = HistoricalSalesValueRules.NormalizeHeader(settlement) == "ABONADO";
            var commission = row.Money("COMISION", settled);
            var vat = row.Money("IVA COMISION", settled);
            var net = row.Money("VALOR NETO", settled);
            if ((commission.HasValue && ((gross > 0 && commission.Value > 0) || (gross < 0 && commission.Value < 0))) ||
                (vat.HasValue && ((gross > 0 && vat.Value > 0) || (gross < 0 && vat.Value < 0))))
                throw row.Error("getnet_fee_sign_invalid", "COMISION");
            if (commission.HasValue && vat.HasValue && net.HasValue && checked(checked(gross + commission.Value) + vat.Value) != net.Value)
                throw row.Error("getnet_financial_reconciliation_mismatch", "VALOR NETO");
            return new HistoricalSalesTransaction(row.Source, Profile, "getnet.financial.movement", row.Identifier("ID FINANCIERO DEL MOVIMIENTO", true),
                row.Identifier("PAYMENT ID"), row.Identifier("COD.AUT"), row.Merchant(),
                HistoricalSalesTimeRules.ParseModern(row.Text("FECHA DE VENTA", 128), row.Text("ZONA HORARIA (UTC)", 16), options),
                kind, status, settlement, gross, commission, vat, net,
                HistoricalSalesValueRules.CardSubtype(row.Text("TIPO"), row.Text("PREPAGO")), row.Text("TIPO"), row.Text("PREPAGO"),
                row.Identifier("TERMINAL"), row.Text("FECHA DE ABONO", 128));
        }
    }
}
