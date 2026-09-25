using System;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;
using Win7POS.Core.Models;
using Win7POS.Core.Security;
using Win7POS.Data;
using Win7POS.Data.Repositories;
using Win7POS.Wpf.Infrastructure.Security;
using Win7POS.Wpf.Pos;
using Win7POS.Wpf.Pos.Online;

namespace Win7POS.Wpf.UiSmokeHarness
{
    internal static class FunctionalSaleSmoke
    {
        internal static async Task RunAsync()
        {
            var factory = new SqliteConnectionFactory(PosDbOptions.Default());
            var response = AuthorizationLeaseWpfSmoke.BuildResponse(true);
            var users = new UserRepository(factory);
            await users.UpsertRemoteStaffMirrorAsync(new RemoteStaffMirrorInput
            {
                Credential = "2468", CredentialVersion = response.Staff.CredentialVersion,
                DisplayName = response.Staff.DisplayName, RemoteRoleKey = response.Staff.RoleKey,
                RemoteShopId = response.Shop.ShopId, RemoteStaffId = response.Staff.StaffId,
                ShopCode = response.Shop.ShopCode, StaffCode = response.Staff.StaffCode
            });
            var store = new PosTrustedDeviceStore();
            store.SaveFirstLogin(response, "qa-functional-" + Guid.NewGuid().ToString("N"));
            await AuthorizationLeaseWpfSmoke.SeedCatalogSaleSafetyAsync(factory);
            var username = await users.FindTrustedRemoteStaffUsernameAsync(response.Shop.ShopId, response.Shop.ShopCode,
                response.Staff.StaffId, response.Staff.StaffCode, response.Staff.CredentialVersion);
            var op = new OperatorSession(users, new SecurityRepository(factory), new PosOfflineAuthorizationLeaseGuard(store, () => DateTimeOffset.UtcNow));
            FunctionalCompletionSmoke.Require(await op.LoginAsync(username, "2468") == LoginResult.Success, "offline login failed");
            await new ProductRepository(factory).UpsertAsync(new Product { Barcode = "SALE", Name = "Synthetic sale item", UnitPrice = 1000 }, ProductWriteOrigin.TestFixture);
            using (var conn = factory.Open()) conn.Execute("INSERT INTO product_meta(barcode,stock_qty) VALUES('SALE',10)");
            var service = new PosWorkflowService();
            await service.AddByBarcodeAsync("SALE");
            await service.ApplyLineDiscountByFinalPriceAsync("SALE", 650);
            var suspended = await service.SuspendCartAsync();
            await service.RecoverHeldCartAsync(suspended.HoldId);
            using (var conn = factory.Open()) conn.Execute("CREATE TRIGGER held_sale_fail BEFORE DELETE ON held_carts BEGIN SELECT RAISE(ABORT,'synthetic sale retry'); END");
            var failed = false;
            try { await service.CompleteSaleAsync(new PosPaymentInfo { CashAmountMinor = 650 }, op); }
            catch (SqliteException) { failed = true; }
            FunctionalCompletionSmoke.Require(failed, "sale fault not injected");
            using (var conn = factory.Open())
            {
                FunctionalCompletionSmoke.Require(conn.ExecuteScalar<long>("SELECT COUNT(*) FROM sales") == 0, "failed sale persisted");
                conn.Execute("DROP TRIGGER held_sale_fail");
            }
            var completed = await service.CompleteSaleAsync(new PosPaymentInfo { CashAmountMinor = 650 }, op);
            FunctionalCompletionSmoke.Require(completed.TotalMinor == 650 && completed.Receipt42.Contains("650"), "sale/receipt amount mismatch");
            var restart = new PosWorkflowService();
            FunctionalCompletionSmoke.Require((await restart.GetHeldCartsAsync()).Count == 0, "restart re-exposed consumed hold");
            var csv = await restart.GetDailyCsvContentAsync(DateTime.Today);
            FunctionalCompletionSmoke.Require(csv.Contains(completed.SaleCode), "register/export omitted sale");
            using var verify = factory.Open();
            FunctionalCompletionSmoke.Require(verify.ExecuteScalar<long>("SELECT COUNT(*) FROM sales") == 1, "retry duplicated sale");
            FunctionalCompletionSmoke.Require(verify.ExecuteScalar<long>("SELECT COUNT(*) FROM sales_sync_outbox") == 1, "outbox count mismatch");
            FunctionalCompletionSmoke.Require(verify.ExecuteScalar<long>("SELECT stock_qty FROM product_meta WHERE barcode='SALE'") == 9, "stock decremented incorrectly");
            FunctionalCompletionSmoke.Require(verify.ExecuteScalar<long>("SELECT SUM(lineTotal) FROM sale_lines") == completed.TotalMinor, "persisted line totals differ");
            verify.Close();
            var ambiguous = new PosWorkflowService();
            await ambiguous.AddManualPriceAsync(100);
            op.SetAuthorizationUseTestHookForTesting((checkpoint, _) =>
            {
                if (checkpoint == "after_commit_before_return") throw new InvalidOperationException("synthetic lost commit response");
            });
            try { await ambiguous.CompleteSaleAsync(new PosPaymentInfo { CashAmountMinor = 100 }, op); }
            catch (InvalidOperationException ex) when (ex.Message == "synthetic lost commit response") { }
            finally { op.SetAuthorizationUseTestHookForTesting(null); }
            var changed = await ambiguous.AddManualPriceAsync(50);
            FunctionalCompletionSmoke.Require(changed.Total == 50, "already committed items survived into a changed cart after lost response");
            await ambiguous.CompleteSaleAsync(new PosPaymentInfo { CashAmountMinor = 50 }, op);
            using var final = factory.Open();
            FunctionalCompletionSmoke.Require(final.ExecuteScalar<long>("SELECT SUM(total) FROM sales") == 800, "ambiguous edit duplicated economic effects");
        }
    }
}
