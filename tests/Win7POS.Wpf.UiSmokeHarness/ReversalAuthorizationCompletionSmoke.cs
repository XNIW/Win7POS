using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Win7POS.Core.Audit;
using Win7POS.Core.Models;
using Win7POS.Core.Online;
using Win7POS.Core.Security;
using Win7POS.Data;
using Win7POS.Data.Repositories;
using Win7POS.Wpf.Infrastructure.Security;
using Win7POS.Wpf.Pos;
using Win7POS.Wpf.Pos.Online;

namespace Win7POS.Wpf.UiSmokeHarness
{
    internal static class ReversalAuthorizationCompletionSmoke
    {
        internal static async Task RunAsync()
        {
            foreach (var isVoid in new[] { false, true })
            foreach (var fault in new[] { "expiry", "revocation", "operator" })
                await VerifyCommitDenialAsync(isVoid, fault);
            await VerifyApprovalBindingAsync();
            await VerifySuccessAsync(isVoid: false, useOverride: false);
            await VerifySuccessAsync(isVoid: false, useOverride: true);
            await VerifySuccessAsync(isVoid: true, useOverride: true);
        }

        private static async Task VerifyCommitDenialAsync(bool isVoid, string fault)
        {
            using (var fixture = await Fixture.CreateAsync())
            {
                var grant = fixture.Approve(isVoid, useOverride: true);
                var before = fixture.ReadState();
                var injected = false;
                var demandCount = 0;
                UserAccount replacement = null;
                fixture.Operator.SetAuthorizationUseTestHookForTesting((checkpoint, demand) =>
                {
                    if (checkpoint == "before_demand") demandCount = demand;
                    if (injected) return;
                    // Revoke before the final gate; expiry/operator change are
                    // injected at its final demand, immediately before COMMIT.
                    if (fault == "revocation" && checkpoint == "before_demand" && demand == 3)
                    {
                        injected = true;
                        PosOnlineSyncRevocationLatch.Revoke(fixture.Generation);
                    }
                    if (fault != "revocation" && checkpoint == "inside_commit_gate")
                    {
                        injected = true;
                        if (fault == "expiry") fixture.Expire();
                        else
                        {
                            replacement = new UserAccount
                            {
                                Id = fixture.Operator.CurrentUser.Id + 100000,
                                Username = "qa-replacement", IsActive = true, RoleCode = "cashier",
                                PermissionCodes = new List<string> { PermissionCodes.PosPay }
                            };
                            fixture.Operator.SetUserForTesting(replacement);
                        }
                    }
                });
                var denied = false;
                try { await fixture.Service.CreateRefundAsync(fixture.Request(isVoid), true, false, fixture.Operator, grant); }
                catch (PosAuthorizationLeaseException ex)
                {
                    denied = fault == "expiry" ? ex.Code == "offline_lease_expired" : fault == "revocation";
                }
                catch (InvalidOperationException) { denied = fault == "operator"; }
                finally { fixture.Operator.SetAuthorizationUseTestHookForTesting(null); }
                Require(injected && demandCount == 3 && denied, "reversal " + fault + " did not reach the expected commit denial");
                Require(before == fixture.ReadState(), "denied reversal changed sale/stock/outbox/audit/void mark: " + fault);
                if (replacement != null)
                    Require(ReferenceEquals(fixture.Operator.CurrentUser, replacement), "old denial removed replacement operator");
                Require((await fixture.Service.BuildRefundPreviewAsync(fixture.SaleId)).Lines.Single().RemainingQty == 1,
                    "authorization denial retained workflow gate or consumed refundable quantity");
            }
        }

        private static async Task VerifyApprovalBindingAsync()
        {
            using (var fixture = await Fixture.CreateAsync())
            {
                var grant = fixture.Operator.CaptureReversalAuthorizationGrant();
                var before = fixture.ReadState();
                var current = fixture.Operator.CurrentUser;
                // Even a same-ID logout/login must invalidate an earlier UI approval.
                fixture.Operator.SetUserForTesting(current);
                var staleApprovalDenied = false;
                try { fixture.Operator.ApproveReversalAuthorization(grant, PermissionCodes.PosRefund, true); }
                catch (PosAuthorizationLeaseException) { staleApprovalDenied = true; }
                Require(staleApprovalDenied, "supervisor approval transferred across authority change");
                await fixture.ReloginAsync();
                grant = fixture.Operator.CaptureReversalAuthorizationGrant();
                grant = fixture.Operator.ApproveReversalAuthorization(grant, PermissionCodes.PosRefund, true);
                var voidDenied = false;
                try
                {
                    using (await fixture.Operator.BeginReversalAuthorizationUseAsync(grant, true, "qa unapproved void")) { }
                }
                catch (InvalidOperationException) { voidDenied = true; }
                Require(voidDenied, "refund override authorized an unapproved void");
                fixture.Operator.CurrentUser.PermissionCodes = new List<string>();
                var ordinaryDenied = false;
                try
                {
                    using (await fixture.Operator.BeginAuthorizationUseAsync(PermissionCodes.PosPay, "qa ordinary permission")) { }
                }
                catch (InvalidOperationException) { ordinaryDenied = true; }
                Require(ordinaryDenied && before == fixture.ReadState(), "reversal override leaked into ordinary permission or wrote data");
                using (var lease = await fixture.Operator.BeginReversalAuthorizationUseAsync(grant, false, "qa exact refund override"))
                    Require(lease.CommitGuard.AuthorizedSaleKind == SaleKind.Refund, "override lost exact economic scope");
                fixture.Operator.SetUserForTesting(fixture.Operator.CurrentUser);
                var staleUseDenied = false;
                try
                {
                    using (await fixture.Operator.BeginReversalAuthorizationUseAsync(grant, false, "qa stale override")) { }
                }
                catch (PosAuthorizationLeaseException) { staleUseDenied = true; }
                Require(staleUseDenied, "approved override transferred across later authority change");
            }
        }

        private static async Task VerifySuccessAsync(bool isVoid, bool useOverride)
        {
            using (var fixture = await Fixture.CreateAsync())
            {
                if (!useOverride)
                    fixture.Operator.CurrentUser.PermissionCodes = new[]
                    {
                        PermissionCodes.PosPay, PermissionCodes.PosRefund, PermissionCodes.PosVoidSale
                    };
                var grant = fixture.Approve(isVoid, useOverride);
                var result = await fixture.Service.CreateRefundAsync(fixture.Request(isVoid), true, false, fixture.Operator, grant);
                using (var connection = fixture.Factory.Open())
                {
                    Require(connection.ExecuteScalar<long>("SELECT stock_qty FROM product_meta WHERE barcode=@barcode", new { barcode = fixture.Barcode }) == 10,
                        "authorized reversal did not restore stock");
                    Require(connection.ExecuteScalar<long>("SELECT COUNT(*) FROM sales WHERE related_sale_id=@id", new { id = fixture.SaleId }) == 1,
                        "authorized reversal created multiple movements");
                    Require(connection.ExecuteScalar<int>("SELECT operator_id FROM sales WHERE id=@id", new { id = result.RefundSaleId }) == fixture.Operator.CurrentUser.Id,
                        "authorized reversal lost operator attribution");
                    Require(result.PrintStatus == RefundPrintStatus.NotRequested, "disabled printing changed accounting status");
                }
            }
        }

        private sealed class Fixture : IDisposable
        {
            private long _clockTicks;
            private long _expiryTicks;
            private PosTrustedDeviceStore _store;
            internal SqliteConnectionFactory Factory;
            internal PosWorkflowService Service;
            internal OperatorSession Operator;
            internal OnlineSyncGeneration Generation;
            internal string Barcode;
            internal long SaleId;
            private long _lineId;
            private string _username;

            internal static async Task<Fixture> CreateAsync()
            {
                var fixture = new Fixture { Factory = new SqliteConnectionFactory(PosDbOptions.Default()) };
                var users = new UserRepository(fixture.Factory);
                var response = AuthorizationLeaseWpfSmoke.BuildResponse(true);
                await users.UpsertRemoteStaffMirrorAsync(new RemoteStaffMirrorInput
                {
                    Credential = "2468", CredentialVersion = response.Staff.CredentialVersion,
                    DisplayName = response.Staff.DisplayName, RemoteRoleKey = response.Staff.RoleKey,
                    RemoteShopId = response.Shop.ShopId, RemoteStaffId = response.Staff.StaffId,
                    ShopCode = response.Shop.ShopCode, StaffCode = response.Staff.StaffCode
                });
                fixture._store = new PosTrustedDeviceStore(() => Interlocked.Read(ref fixture._clockTicks),
                    TimeSpan.TicksPerSecond, "qa-reversal-clock-" + Guid.NewGuid().ToString("N"));
                fixture._store.Clear();
                fixture._store.SaveFirstLogin(response, "qa-reversal-" + Guid.NewGuid().ToString("N"));
                Require(fixture._store.TryRead(out var trusted), "reversal trusted fixture unavailable");
                Require(PosOnlineSyncSupervisorHost.TryCreateGeneration(trusted, out fixture.Generation), "reversal generation fixture invalid");
                var wall = DateTimeOffset.Parse(trusted.LastOkLocalAt, CultureInfo.InvariantCulture);
                fixture._expiryTicks = (DateTimeOffset.Parse(trusted.EffectiveOfflineAuthorizationExpiresAt, CultureInfo.InvariantCulture) -
                    DateTimeOffset.Parse(trusted.LastOkServerAt, CultureInfo.InvariantCulture)).Ticks;
                var leaseGuard = new PosOfflineAuthorizationLeaseGuard(fixture._store, () => wall,
                    () => Interlocked.Read(ref fixture._clockTicks), TimeSpan.TicksPerSecond);
                fixture.Operator = new OperatorSession(users, new SecurityRepository(fixture.Factory), leaseGuard);
                fixture._username = await users.FindTrustedRemoteStaffUsernameAsync(response.Shop.ShopId, response.Shop.ShopCode,
                    response.Staff.StaffId, response.Staff.StaffCode, response.Staff.CredentialVersion);
                await fixture.ReloginAsync();
                fixture.Operator.CurrentUser.PermissionCodes = new[] { PermissionCodes.PosPay };
                await AuthorizationLeaseWpfSmoke.SeedCatalogSaleSafetyAsync(fixture.Factory);
                fixture.Barcode = "REV-AUTH-" + Guid.NewGuid().ToString("N");
                await new ProductRepository(fixture.Factory).UpsertAsync(new Product
                {
                    Barcode = fixture.Barcode, Name = "Authorization TEST", UnitPrice = 100
                }, ProductWriteOrigin.TestFixture);
                using (var connection = fixture.Factory.Open())
                    connection.Execute("INSERT INTO product_meta(barcode,stock_qty) VALUES(@barcode,10)", new { barcode = fixture.Barcode });
                fixture.Service = new PosWorkflowService();
                await fixture.Service.AddByBarcodeAsync(fixture.Barcode);
                var sale = await fixture.Service.CompleteSaleAsync(new PosPaymentInfo { CashAmountMinor = 100 }, fixture.Operator);
                fixture.SaleId = sale.SaleId;
                fixture._lineId = (await fixture.Service.BuildRefundPreviewAsync(sale.SaleId)).Lines.Single().OriginalLineId;
                return fixture;
            }

            internal OperatorSession.ReversalAuthorizationGrant Approve(bool isVoid, bool useOverride)
            {
                var grant = Operator.CaptureReversalAuthorizationGrant();
                grant = Operator.ApproveReversalAuthorization(grant, PermissionCodes.PosRefund, useOverride);
                return isVoid ? Operator.ApproveReversalAuthorization(grant, PermissionCodes.PosVoidSale, useOverride) : grant;
            }

            internal RefundCreateRequest Request(bool isVoid) => new RefundCreateRequest
            {
                OriginalSaleId = SaleId, IsFullVoid = isVoid,
                Lines = new List<RefundLineRequest> { new RefundLineRequest { OriginalLineId = _lineId, QtyToRefund = 1 } },
                Payment = new RefundPaymentInfo { CashMinor = 100 }
            };

            internal void Expire() => Interlocked.Exchange(ref _clockTicks, _expiryTicks);

            internal async Task ReloginAsync() => Require(
                await Operator.LoginAsync(_username, "2468") == LoginResult.Success, "reversal fixture login failed");

            internal string ReadState()
            {
                using (var connection = Factory.Open())
                    return string.Join("|", new[]
                    {
                        connection.ExecuteScalar<long>("SELECT COUNT(*) FROM sales"),
                        connection.ExecuteScalar<long>("SELECT COUNT(*) FROM sales_sync_outbox"),
                        connection.ExecuteScalar<long>("SELECT COUNT(*) FROM local_stock_movements"),
                        connection.ExecuteScalar<long>("SELECT COUNT(*) FROM audit_log WHERE action=@action", new { action = AuditActions.RefundCreate }),
                        connection.ExecuteScalar<long>("SELECT stock_qty FROM product_meta WHERE barcode=@barcode", new { barcode = Barcode }),
                        connection.ExecuteScalar<long>("SELECT COALESCE(voided_by_sale_id,0) FROM sales WHERE id=@id", new { id = SaleId })
                    });
            }

            public void Dispose()
            {
                Operator?.SetAuthorizationUseTestHookForTesting(null);
                _store?.Clear();
            }
        }

        private static void Require(bool value, string message) => FunctionalCompletionSmoke.Require(value, message);
    }
}
