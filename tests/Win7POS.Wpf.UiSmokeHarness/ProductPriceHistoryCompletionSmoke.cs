using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Dapper;
using Win7POS.Core.Models;
using Win7POS.Data;
using Win7POS.Data.Repositories;
using Win7POS.Wpf.Products;

namespace Win7POS.Wpf.UiSmokeHarness
{
    internal static class ProductPriceHistoryCompletionSmoke
    {
        internal static async Task RunAsync(List<string> lines)
        {
            var factory = new SqliteConnectionFactory(PosDbOptions.Default());
            var products = new ProductRepository(factory);
            await products.UpsertProductAndMetaInTransactionAsync(
                new Product { Barcode = "HISTORY64-UI", Name = "History Int64", UnitPrice = long.MaxValue },
                "", "", 100, null, "", null, "", 1, ProductWriteOrigin.SupplierImportApply);
            var product = await products.GetByBarcodeAsync("HISTORY64-UI");
            using (var connection = factory.Open())
                connection.Execute("INSERT INTO product_price_history(barcode,timestamp,type,old_price,new_price,source) VALUES('HISTORY64-UI','2026-10-08 10:00:00','retail',@old,@price,'IMPORT')",
                    new { old = 4294967296L, price = long.MaxValue });

            var service = new ProductsWorkflowService();
            var vm = new ProductPriceHistoryViewModel(product.Id, product.Barcode, product.Name,
                product.UnitPrice, 100, service, true);
            var exact = long.MaxValue.ToString(CultureInfo.InvariantCulture);
            Require(vm.CurrentRetailPrice == exact, "initial history price was narrowed");
            await vm.LoadAsync();
            Require(vm.CurrentRetailPrice == exact && vm.RetailHistory.Count == 1 &&
                vm.RetailHistory[0].OldPrice == 4294967296L && vm.RetailHistory[0].NewPrice == long.MaxValue,
                "history UI reader lost Int64 values");

            // The existing manual/API ingress has a narrower range. A purchase-only
            // edit must retain the real retail value and fail explicitly, never wrap.
            vm.NewPurchaseText = "101";
            vm.ApplyNewPricesCommand.Execute(null);
            for (var attempt = 0; attempt < 100 && (vm.IsBusy || vm.NewPurchaseText.Length == 0 ||
                vm.StatusMessage.IndexOf("Article price or stock is invalid.", StringComparison.Ordinal) < 0); attempt++)
                await Task.Delay(10);
            Require(vm.NewPurchaseText == "101" && vm.StatusMessage.IndexOf("Article price or stock is invalid.", StringComparison.Ordinal) >= 0,
                "manual history edit did not retain draft and report the existing API range");
            var unchanged = await products.GetDetailsByIdAsync(product.Id);
            Require(unchanged.UnitPrice == long.MaxValue && unchanged.PurchasePrice == 100,
                "rejected history edit changed product prices");
            using (var connection = factory.Open())
                Require(connection.ExecuteScalar<long>("SELECT count(*) FROM product_price_history WHERE barcode='HISTORY64-UI'") == 1,
                    "rejected history edit wrote another history row");
            lines.Add("history_ui_int64=PASS initial+refresh+DTO+purchase-only-rejection+draft+no-writes");
        }

        private static void Require(bool value, string message)
        {
            if (!value) throw new InvalidOperationException("price_history_completion: " + message);
        }
    }
}
