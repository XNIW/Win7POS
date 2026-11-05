using System.Collections.Generic;
using Win7POS.Core.Import;

namespace Win7POS.Data.Import
{
    internal sealed class GetnetLegacyProfileAdapter : GetnetProfileAdapter
    {
        internal override HistoricalSalesProfile Profile => HistoricalSalesProfile.GetnetLegacy;
        internal override void ValidateHeaders(GetnetHeaderMap header) => header.Require(
            "FECHA VENTA", "ID TRANSACCION", "VALOR TRANSACCION", "ESTADO", "LOCAL", "COMISION", "MONTO ABONO");

        internal override HistoricalSalesTransaction Read(GetnetRowContext row, HistoricalSalesImportOptions options, List<HistoricalSalesRejectedRow> rejected)
        {
            var status = row.Text("ESTADO");
            if (HistoricalSalesValueRules.NormalizeHeader(status) != "ABONADO") throw row.Error("getnet_transaction_state_unknown", "ESTADO");
            var gross = row.Money("VALOR TRANSACCION", true).Value;
            var commission = row.Money("COMISION", true).Value;
            var net = row.Money("MONTO ABONO", true).Value;
            if (gross <= 0) throw row.Error("getnet_movement_sign_invalid", "VALOR TRANSACCION");
            if (commission < 0) throw row.Error("getnet_fee_sign_invalid", "COMISION");
            if (checked(gross - commission) != net) throw row.Error("getnet_financial_reconciliation_mismatch", "MONTO ABONO");
            row.RequireZeroIfProvided("PROPINA", "VUELTO", "COMISION PROPINA");
            var rawTimestamp = row.Text("FECHA VENTA", 128);
            return new HistoricalSalesTransaction(row.Source, Profile, "getnet.legacy.transaction", row.Identifier("ID TRANSACCION", true),
                string.Empty, row.Identifier("COD.AUT"), row.Merchant(),
                HistoricalSalesTimeRules.ParseLegacy(rawTimestamp, options, row.LegacyTimestampText()),
                HistoricalSalesMovementKind.Sale, status, status, gross, commission, null, net,
                HistoricalSalesValueRules.CardSubtype(row.Text("TIPO"), string.Empty), row.Text("TIPO"), string.Empty, string.Empty, string.Empty);
        }
    }
}
