using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Win7POS.Core;
using Win7POS.Core.Online;
using Win7POS.Data;
using Win7POS.Data.Online;
using Win7POS.Data.Repositories;
using Win7POS.Wpf.Pos.Online;

namespace Win7POS.Wpf.UiSmokeHarness
{
    internal static class AuthoritativeCatalogDrainLoopbackHarness
    {
        private const int CategoryCount = 71;
        private const int SupplierCount = 102;
        private const int ProductCount = 19763;
        private const int PriceCount = 41228;
        private const int ExpectedPages = 676;
        private const string ShopCode = "LOOPBACK";
        private const string ShopId = "loopback-shop";
        private const string MarkerProductId = "preexisting-live-marker";

        internal static async Task<string> RunAsync(string outputDirectory)
        {
            Directory.CreateDirectory(outputDirectory);
            var result = new LoopbackReport
            {
                StartedAtUtc = DateTimeOffset.UtcNow.ToString(
                    "O",
                    CultureInfo.InvariantCulture)
            };

            try
            {
                var success = await RunScenarioAsync(
                    outputDirectory,
                    "success",
                    LoopbackFailureMode.None).ConfigureAwait(false);
                var pageFailure = await RunScenarioAsync(
                    outputDirectory,
                    "page-600-failure",
                    LoopbackFailureMode.RepeatCursorAtPage600).ConfigureAwait(false);
                var terminalFailure = await RunScenarioAsync(
                    outputDirectory,
                    "terminal-failure",
                    LoopbackFailureMode.ChangeSummaryAtTerminal).ConfigureAwait(false);

                result.Pages = success.Outcome.PagesProcessed;
                result.Requests = success.Requests;
                result.ProductsApplied = success.Outcome.ProductsApplied;
                result.PricesApplied = success.Outcome.PricesApplied;
                result.PendingPrices = success.Audit.PendingRemotePrices;
                result.LocalActiveCategories = success.Audit.ActiveRemoteCategories;
                result.LocalActiveProducts = success.Audit.ActiveRemoteProducts;
                result.LocalActiveSuppliers = success.Audit.ActiveRemoteSuppliers;
                result.LocalPriceRows = success.Audit.RemotePriceHistoryRows;
                result.Exactness = success.Exactness.Status.ToString();
                result.RepairRequired = success.Exactness.RepairRequired;
                result.SaleSafe = success.Outcome.CatalogSaleSafe;
                result.TerminalHasMore = success.Outcome.HasMore;
                result.RowsSkipped = 0;
                result.AutomaticRetries = success.Requests - success.Outcome.PagesProcessed;
                result.ProductListPopulated = success.ProductListPopulated;
                result.Page600FailureCode = pageFailure.Outcome.StatusCode;
                result.Page600LiveCatalogPreserved = pageFailure.MarkerPreserved;
                result.TerminalFailureCode = terminalFailure.Outcome.StatusCode;
                result.TerminalFailureLiveCatalogPreserved =
                    terminalFailure.MarkerPreserved;

                Require(success.Outcome.Completed, "success outcome was incomplete");
                Require(success.Requests == ExpectedPages, "success request count mismatch");
                Require(success.Outcome.PagesProcessed == ExpectedPages, "success page count mismatch");
                Require(success.Outcome.ProductsApplied == ProductCount, "product apply count mismatch");
                Require(success.Outcome.PricesApplied == PriceCount, "price apply count mismatch");
                Require(success.Outcome.PricesQueued == 0, "prices were queued");
                Require(success.Outcome.PendingPricesApplied == 0, "pending prices were applied");
                Require(!success.Outcome.HasMore, "terminal hasMore was true");
                Require(success.Outcome.CatalogSaleSafe, "catalog was not sale-safe");
                Require(
                    success.Exactness.Status == CatalogCompletenessStatus.Verified &&
                    !success.Exactness.RepairRequired,
                    "exactness was not verified");
                Require(success.Audit.ActiveRemoteCategories == CategoryCount, "category count mismatch");
                Require(success.Audit.ActiveRemoteSuppliers == SupplierCount, "supplier count mismatch");
                Require(success.Audit.ActiveRemoteProducts == ProductCount, "local product count mismatch");
                Require(success.Audit.RemotePriceHistoryRows == PriceCount, "local price count mismatch");
                Require(success.Audit.PendingRemotePrices == 0, "pending prices were not drained");
                Require(success.ProductListPopulated, "product list was empty");
                Require(success.Requests - success.Outcome.PagesProcessed == 0, "unexpected retry detected");
                Require(
                    string.Equals(
                        pageFailure.Outcome.StatusCode,
                        CatalogAuthoritativeDrainBudgetPolicy.CursorRepeatedCode,
                        StringComparison.Ordinal),
                    "page 600 failure did not reject repeated cursor");
                Require(pageFailure.Requests == 600, "page 600 request count mismatch");
                Require(pageFailure.MarkerPreserved, "page 600 changed live catalog");
                Require(
                    string.Equals(
                        terminalFailure.Outcome.StatusCode,
                        "catalog_summary_changed_mid_pull",
                        StringComparison.Ordinal),
                    "terminal failure did not reject changed summary");
                Require(terminalFailure.Requests == ExpectedPages, "terminal request count mismatch");
                Require(terminalFailure.MarkerPreserved, "terminal failure changed live catalog");

                result.Passed = true;
                result.Code = "success";
                return "PASS authoritative catalog drain loopback";
            }
            catch (Exception ex)
            {
                result.Passed = false;
                result.Code = SafeCode(ex.Message);
                result.ExceptionType = ex.GetType().Name;
                return "FAIL authoritative catalog drain loopback: " +
                    result.Code;
            }
            finally
            {
                result.CompletedAtUtc = DateTimeOffset.UtcNow.ToString(
                    "O",
                    CultureInfo.InvariantCulture);
                WriteJson(
                    Path.Combine(
                        outputDirectory,
                        "authoritative-catalog-drain-loopback-result.json"),
                    result);
            }
        }

        private static async Task<ScenarioResult> RunScenarioAsync(
            string outputDirectory,
            string scenarioName,
            LoopbackFailureMode failureMode)
        {
            var dbPath = Path.Combine(outputDirectory, scenarioName + ".sqlite");
            var options = PosDbOptions.ForPath(dbPath);
            DbInitializer.EnsureCreated(options);
            var factory = new SqliteConnectionFactory(options);
            await SeedShopAndMarkerAsync(factory).ConfigureAwait(false);

            using (var server = new SequentialCatalogServer(failureMode))
            using (var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10)))
            {
                var session = new PosTrustedDeviceSession
                {
                    DeviceToken = "synthetic-device-token",
                    PosSessionId = "synthetic-pos-session",
                    SessionToken = "synthetic-session-token",
                    ShopCode = ShopCode,
                    ShopDeviceId = "synthetic-shop-device",
                    ShopId = ShopId,
                    ShopName = "Loopback Shop"
                };
                var outcome = await new PosCatalogPullService(factory)
                    .TryPullCatalogForSupervisorAsync(
                        new PosAdminWebOptions(new Uri(server.BaseUrl)),
                        session,
                        generation: null,
                        executionContext: null,
                        forceFullRepair: true,
                        bootstrapRun: true,
                        cancellationToken: timeout.Token)
                    .ConfigureAwait(false);
                var exactness = await new CatalogShopStateRepository(factory)
                    .LoadExactnessAsync().ConfigureAwait(false);
                var audit = await new CatalogFullRefreshReconciler(factory)
                    .AuditCurrentAsync().ConfigureAwait(false);
                var markerPreserved = await HasActiveRemoteProductAsync(
                    factory,
                    MarkerProductId).ConfigureAwait(false);
                var productList = await new ProductRepository(factory)
                    .SearchAsync(string.Empty, 1).ConfigureAwait(false);

                return new ScenarioResult
                {
                    Audit = audit,
                    Exactness = exactness,
                    MarkerPreserved = markerPreserved,
                    Outcome = outcome,
                    ProductListPopulated = productList != null &&
                        productList.Count > 0,
                    Requests = server.RequestCount
                };
            }
        }

        private static async Task SeedShopAndMarkerAsync(
            SqliteConnectionFactory factory)
        {
            await new ShopOfficialSnapshotRepository(factory).SaveAsync(
                new OfficialShopSnapshot
                {
                    ShopCode = ShopCode,
                    ShopId = ShopId,
                    ShopName = "Loopback Shop",
                    ShopStatus = "active",
                    Source = "qa_loopback"
                }).ConfigureAwait(false);
            await new CatalogShopStateRepository(factory)
                .EnsureAndLoadCursorAsync(ShopId, ShopCode)
                .ConfigureAwait(false);
            using (var connection = factory.Open())
            {
                await connection.ExecuteAsync(@"
INSERT INTO products(
  barcode, name, unitPrice, remote_product_id, is_active)
VALUES(
  'LOOPBACK-MARKER', 'Preexisting live marker', 1, @productId, 1);
INSERT INTO product_meta(barcode, stock_qty)
VALUES('LOOPBACK-MARKER', 1);",
                    new { productId = MarkerProductId }).ConfigureAwait(false);
            }
        }

        private static async Task<bool> HasActiveRemoteProductAsync(
            SqliteConnectionFactory factory,
            string productId)
        {
            using (var connection = factory.Open())
            {
                return await connection.ExecuteScalarAsync<long>(@"
SELECT COUNT(1)
FROM products
WHERE remote_product_id = @productId
  AND is_active = 1;",
                    new { productId }).ConfigureAwait(false) == 1L;
            }
        }

        private static void Require(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }

        private static string SafeCode(string value)
        {
            var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
            var builder = new StringBuilder();
            foreach (var character in normalized)
            {
                if ((character >= 'a' && character <= 'z') ||
                    (character >= '0' && character <= '9') ||
                    character == '_' || character == '-')
                {
                    builder.Append(character);
                }
                else if (builder.Length > 0 && builder[builder.Length - 1] != '_')
                {
                    builder.Append('_');
                }
                if (builder.Length >= 120)
                {
                    break;
                }
            }
            return builder.Length == 0 ? "loopback_failure" : builder.ToString();
        }

        private static void WriteJson<T>(string path, T value)
        {
            var serializer = new DataContractJsonSerializer(typeof(T));
            using (var stream = new FileStream(
                path,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None))
            {
                serializer.WriteObject(stream, value);
            }
        }

        private sealed class SequentialCatalogServer : IDisposable
        {
            private readonly CancellationTokenSource _cancellation =
                new CancellationTokenSource();
            private readonly LoopbackFailureMode _failureMode;
            private readonly TcpListener _listener;
            private readonly Task _serverTask;
            private int _requestCount;

            internal SequentialCatalogServer(LoopbackFailureMode failureMode)
            {
                _failureMode = failureMode;
                _listener = new TcpListener(IPAddress.Loopback, 0);
                _listener.Start();
                var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
                BaseUrl = "http://127.0.0.1:" +
                    port.ToString(CultureInfo.InvariantCulture) + "/";
                _serverTask = Task.Run(() => RunAsync(_cancellation.Token));
            }

            internal string BaseUrl { get; }
            internal int RequestCount => Volatile.Read(ref _requestCount);

            public void Dispose()
            {
                _cancellation.Cancel();
                try
                {
                    _listener.Stop();
                }
                catch
                {
                }
                try
                {
                    _serverTask.Wait(TimeSpan.FromSeconds(5));
                }
                catch
                {
                }
                _cancellation.Dispose();
            }

            private async Task RunAsync(CancellationToken cancellationToken)
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    TcpClient client = null;
                    try
                    {
                        client = await _listener.AcceptTcpClientAsync()
                            .ConfigureAwait(false);
                        await HandleClientAsync(client, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (ObjectDisposedException)
                    {
                        client?.Dispose();
                    }
                    catch (SocketException)
                    {
                        client?.Dispose();
                        if (!cancellationToken.IsCancellationRequested)
                        {
                            throw;
                        }
                    }
                }
            }

            private async Task HandleClientAsync(
                TcpClient client,
                CancellationToken cancellationToken)
            {
                using (client)
                using (var stream = client.GetStream())
                {
                    var request = await ReadRequestAsync(
                        stream,
                        cancellationToken).ConfigureAwait(false);
                    if (!request.StartsWith(
                        "POST /api/pos/catalog/pull ",
                        StringComparison.OrdinalIgnoreCase))
                    {
                        await WriteResponseAsync(
                            stream,
                            404,
                            Encoding.UTF8.GetBytes(
                                "{\"ok\":false,\"code\":\"not_found\"}"),
                            cancellationToken).ConfigureAwait(false);
                        return;
                    }

                    var page = Interlocked.Increment(ref _requestCount);
                    var response = BuildResponse(page, _failureMode);
                    await WriteResponseAsync(
                        stream,
                        200,
                        Serialize(response),
                        cancellationToken).ConfigureAwait(false);
                }
            }
        }

        private static PosCatalogPullResponse BuildResponse(
            int page,
            LoopbackFailureMode failureMode)
        {
            if (page <= 0 || page > ExpectedPages)
            {
                throw new InvalidOperationException("loopback page out of range");
            }

            var payload = new PosCatalogPayload
            {
                Categories = Array.Empty<PosCatalogCategoryResponse>(),
                Suppliers = Array.Empty<PosCatalogSupplierResponse>(),
                Products = Array.Empty<PosCatalogProductResponse>(),
                Prices = Array.Empty<PosCatalogPriceResponse>(),
                Tombstones = new PosCatalogTombstonesResponse
                {
                    Categories = Array.Empty<PosCatalogCategoryTombstoneResponse>(),
                    Suppliers = Array.Empty<PosCatalogSupplierTombstoneResponse>(),
                    Products = Array.Empty<PosCatalogProductTombstoneResponse>()
                }
            };

            if (page == 1)
            {
                payload.Categories = Enumerable.Range(1, CategoryCount)
                    .Select(Category)
                    .ToArray();
            }
            else if (page == 2)
            {
                payload.Suppliers = Enumerable.Range(1, SupplierCount)
                    .Select(Supplier)
                    .ToArray();
            }
            else if (page <= 332)
            {
                var start = ((page - 3) * 60) + 1;
                var count = Math.Min(60, ProductCount - start + 1);
                payload.Products = Enumerable.Range(start, count)
                    .Select(Product)
                    .ToArray();
            }
            else
            {
                var start = ((page - 333) * 120) + 1;
                var count = Math.Min(120, PriceCount - start + 1);
                payload.Prices = Enumerable.Range(start, count)
                    .Select(Price)
                    .ToArray();
            }

            var summaryPrices = PriceCount;
            if (failureMode == LoopbackFailureMode.ChangeSummaryAtTerminal &&
                page == ExpectedPages)
            {
                summaryPrices++;
            }

            var cursorPage =
                failureMode == LoopbackFailureMode.RepeatCursorAtPage600 &&
                page == 600
                    ? page - 1
                    : page;
            var now = "2026-07-27T12:00:00.0000000Z";
            return new PosCatalogPullResponse
            {
                Catalog = payload,
                CatalogSummary = new PosCatalogSummaryResponse
                {
                    ActiveProducts = ProductCount,
                    Categories = CategoryCount,
                    Prices = summaryPrices,
                    Products = ProductCount,
                    Suppliers = SupplierCount
                },
                CatalogVersion = "loopback-catalog-v2",
                Code = "success",
                GeneratedAt = now,
                HasMore = page < ExpectedPages,
                Ok = true,
                Policy = ValidPolicy(),
                SchemaVersion = PosOnlineContract.CatalogPullSchemaVersion,
                ServerTime = now,
                Shop = new PosShopResponse
                {
                    ShopCode = ShopCode,
                    ShopId = ShopId,
                    ShopName = "Loopback Shop",
                    ShopStatus = "active",
                    Source = "qa_loopback",
                    UpdatedAt = now
                },
                SyncCursor = "loopback-cursor-" +
                    cursorPage.ToString("D4", CultureInfo.InvariantCulture),
                SyncMode = "full_refresh"
            };
        }

        private static PosPolicyResponse ValidPolicy()
        {
            return new PosPolicyResponse
            {
                Capabilities = new PosPolicyCapabilitiesResponse
                {
                    CatalogPull = PosOnlineContract.CatalogCapabilityVersion,
                    OfflineSales = true,
                    SalesSync = PosOnlineContract.SalesSchemaVersion
                },
                ContractVersion = PosOnlineContract.PolicyContractVersion,
                PaymentPolicy = new PosPaymentPolicyResponse
                {
                    Currency = "CLP",
                    SupportedMethods = new[]
                    {
                        PosOnlineContract.PaymentCash,
                        PosOnlineContract.PaymentCard
                    }
                }
            };
        }

        private static PosCatalogCategoryResponse Category(int index)
        {
            return new PosCatalogCategoryResponse
            {
                CategoryId = "loopback-category-" +
                    index.ToString("D3", CultureInfo.InvariantCulture),
                Name = "Loopback Category " +
                    index.ToString(CultureInfo.InvariantCulture),
                UpdatedAt = "2026-07-27T12:00:00.0000000Z"
            };
        }

        private static PosCatalogSupplierResponse Supplier(int index)
        {
            return new PosCatalogSupplierResponse
            {
                SupplierId = "loopback-supplier-" +
                    index.ToString("D3", CultureInfo.InvariantCulture),
                Name = "Loopback Supplier " +
                    index.ToString(CultureInfo.InvariantCulture),
                UpdatedAt = "2026-07-27T12:00:00.0000000Z"
            };
        }

        private static PosCatalogProductResponse Product(int index)
        {
            return new PosCatalogProductResponse
            {
                Barcode = "LB" + index.ToString("D12", CultureInfo.InvariantCulture),
                CategoryId = "loopback-category-" +
                    (((index - 1) % CategoryCount) + 1)
                        .ToString("D3", CultureInfo.InvariantCulture),
                ItemNumber = "ITEM-" +
                    index.ToString("D6", CultureInfo.InvariantCulture),
                ProductId = ProductId(index),
                ProductName = "Loopback Product " +
                    index.ToString(CultureInfo.InvariantCulture),
                PurchasePrice = 500,
                RetailPrice = 1000 + (index % 100),
                SecondProductName = string.Empty,
                StockQuantity = 10,
                SupplierId = "loopback-supplier-" +
                    (((index - 1) % SupplierCount) + 1)
                        .ToString("D3", CultureInfo.InvariantCulture),
                UpdatedAt = "2026-07-27T12:00:00.0000000Z"
            };
        }

        private static PosCatalogPriceResponse Price(int index)
        {
            var productIndex = ((index - 1) % ProductCount) + 1;
            return new PosCatalogPriceResponse
            {
                EffectiveAt = DateTimeOffset
                    .Parse(
                        "2026-07-27T12:00:00Z",
                        CultureInfo.InvariantCulture)
                    .AddSeconds(index)
                    .ToString("O", CultureInfo.InvariantCulture),
                Price = 1000 + (productIndex % 100),
                PriceId = "loopback-price-" +
                    index.ToString("D6", CultureInfo.InvariantCulture),
                ProductId = ProductId(productIndex),
                Source = "catalog_pull",
                Type = "retail"
            };
        }

        private static string ProductId(int index)
        {
            return "loopback-product-" +
                index.ToString("D6", CultureInfo.InvariantCulture);
        }

        private static byte[] Serialize(PosCatalogPullResponse response)
        {
            var serializer = new DataContractJsonSerializer(
                typeof(PosCatalogPullResponse));
            using (var stream = new MemoryStream())
            {
                serializer.WriteObject(stream, response);
                return stream.ToArray();
            }
        }

        private static async Task<string> ReadRequestAsync(
            NetworkStream stream,
            CancellationToken cancellationToken)
        {
            var buffer = new byte[8192];
            var bytes = new List<byte>();
            var headerEnd = -1;
            while (headerEnd < 0)
            {
                var read = await stream.ReadAsync(
                    buffer,
                    0,
                    buffer.Length,
                    cancellationToken).ConfigureAwait(false);
                if (read <= 0)
                {
                    break;
                }
                bytes.AddRange(buffer.Take(read));
                headerEnd = IndexOfHeaderEnd(bytes);
            }
            if (headerEnd < 0)
            {
                return string.Empty;
            }

            var headers = Encoding.ASCII.GetString(
                bytes.Take(headerEnd).ToArray());
            var contentLength = 0;
            foreach (var line in headers.Split(
                new[] { "\r\n" },
                StringSplitOptions.None))
            {
                var separator = line.IndexOf(':');
                if (separator > 0 &&
                    string.Equals(
                        line.Substring(0, separator).Trim(),
                        "Content-Length",
                        StringComparison.OrdinalIgnoreCase))
                {
                    int.TryParse(
                        line.Substring(separator + 1).Trim(),
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out contentLength);
                }
            }

            var bodyStart = headerEnd + 4;
            while (bytes.Count - bodyStart < contentLength)
            {
                var read = await stream.ReadAsync(
                    buffer,
                    0,
                    buffer.Length,
                    cancellationToken).ConfigureAwait(false);
                if (read <= 0)
                {
                    break;
                }
                bytes.AddRange(buffer.Take(read));
            }

            return headers.Split(
                new[] { "\r\n" },
                StringSplitOptions.None)[0];
        }

        private static int IndexOfHeaderEnd(List<byte> bytes)
        {
            for (var index = 3; index < bytes.Count; index++)
            {
                if (bytes[index - 3] == 13 &&
                    bytes[index - 2] == 10 &&
                    bytes[index - 1] == 13 &&
                    bytes[index] == 10)
                {
                    return index - 3;
                }
            }
            return -1;
        }

        private static async Task WriteResponseAsync(
            NetworkStream stream,
            int statusCode,
            byte[] body,
            CancellationToken cancellationToken)
        {
            var reason = statusCode == 200 ? "OK" : "Not Found";
            var header = Encoding.ASCII.GetBytes(
                "HTTP/1.1 " +
                statusCode.ToString(CultureInfo.InvariantCulture) +
                " " + reason + "\r\n" +
                "Content-Type: application/json; charset=utf-8\r\n" +
                "Content-Length: " +
                body.Length.ToString(CultureInfo.InvariantCulture) +
                "\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(
                header,
                0,
                header.Length,
                cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(
                body,
                0,
                body.Length,
                cancellationToken).ConfigureAwait(false);
        }

        private enum LoopbackFailureMode
        {
            None = 0,
            RepeatCursorAtPage600 = 1,
            ChangeSummaryAtTerminal = 2
        }

        private sealed class ScenarioResult
        {
            internal CatalogFullRefreshResult Audit { get; set; }
            internal CatalogExactnessState Exactness { get; set; }
            internal bool MarkerPreserved { get; set; }
            internal PosCatalogPullOutcome Outcome { get; set; }
            internal bool ProductListPopulated { get; set; }
            internal int Requests { get; set; }
        }

        [DataContract]
        private sealed class LoopbackReport
        {
            [DataMember(Name = "automaticRetries")]
            internal long AutomaticRetries { get; set; }

            [DataMember(Name = "code")]
            internal string Code { get; set; }

            [DataMember(Name = "completedAtUtc")]
            internal string CompletedAtUtc { get; set; }

            [DataMember(Name = "exactness")]
            internal string Exactness { get; set; }

            [DataMember(Name = "exceptionType")]
            internal string ExceptionType { get; set; }

            [DataMember(Name = "localActiveCategories")]
            internal long LocalActiveCategories { get; set; }

            [DataMember(Name = "localActiveProducts")]
            internal long LocalActiveProducts { get; set; }

            [DataMember(Name = "localActiveSuppliers")]
            internal long LocalActiveSuppliers { get; set; }

            [DataMember(Name = "localPriceRows")]
            internal long LocalPriceRows { get; set; }

            [DataMember(Name = "page600FailureCode")]
            internal string Page600FailureCode { get; set; }

            [DataMember(Name = "page600LiveCatalogPreserved")]
            internal bool Page600LiveCatalogPreserved { get; set; }

            [DataMember(Name = "pages")]
            internal long Pages { get; set; }

            [DataMember(Name = "passed")]
            internal bool Passed { get; set; }

            [DataMember(Name = "pendingPrices")]
            internal long PendingPrices { get; set; }

            [DataMember(Name = "pricesApplied")]
            internal int PricesApplied { get; set; }

            [DataMember(Name = "productListPopulated")]
            internal bool ProductListPopulated { get; set; }

            [DataMember(Name = "productsApplied")]
            internal int ProductsApplied { get; set; }

            [DataMember(Name = "repairRequired")]
            internal bool RepairRequired { get; set; }

            [DataMember(Name = "requests")]
            internal int Requests { get; set; }

            [DataMember(Name = "rowsSkipped")]
            internal int RowsSkipped { get; set; }

            [DataMember(Name = "saleSafe")]
            internal bool SaleSafe { get; set; }

            [DataMember(Name = "startedAtUtc")]
            internal string StartedAtUtc { get; set; }

            [DataMember(Name = "terminalFailureCode")]
            internal string TerminalFailureCode { get; set; }

            [DataMember(Name = "terminalFailureLiveCatalogPreserved")]
            internal bool TerminalFailureLiveCatalogPreserved { get; set; }

            [DataMember(Name = "terminalHasMore")]
            internal bool TerminalHasMore { get; set; }
        }
    }
}
