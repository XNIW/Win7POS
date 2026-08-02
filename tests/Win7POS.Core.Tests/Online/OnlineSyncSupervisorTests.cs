using Microsoft.VisualStudio.TestTools.UnitTesting;
using Win7POS.Core.Online;
using Win7POS.Data.Online;

namespace Win7POS.Core.Tests.Online;

[TestClass]
public sealed class OnlineSyncSupervisorTests
{
    [TestMethod]
    public void StartOfDayTreatsProductImageAuthenticationDenialAsGlobalAuthStop()
    {
        var result = new OnlineSyncStartOfDayResult
        {
            ProductImages = OnlineSyncLaneOutcome.AuthDenied("auth_denied")
        };

        Assert.IsTrue(result.AuthenticationDenied);
    }

    [TestMethod]
    public async Task TwentySameLaneTriggers_CoalesceToOneRunAndOneRerun()
    {
        var firstStarted = NewSignal();
        var releaseFirst = NewSignal();
        var runs = 0;
        var active = 0;
        var maximumActive = 0;
        using var supervisor = CreateSupervisor(async (_, _, _) =>
        {
            var currentRun = Interlocked.Increment(ref runs);
            var currentActive = Interlocked.Increment(ref active);
            UpdateMaximum(ref maximumActive, currentActive);
            try
            {
                if (currentRun == 1)
                {
                    firstStarted.TrySetResult(true);
                    await releaseFirst.Task;
                }
                return TerminalSuccess();
            }
            finally
            {
                Interlocked.Decrement(ref active);
            }
        });

        var first = supervisor.TriggerAsync(
            OnlineSyncLane.SalesOutbox,
            OnlineSyncLaneTrigger.LocalCommit);
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var concurrent = Enumerable.Range(0, 19)
            .Select(_ => supervisor.TriggerAsync(
                OnlineSyncLane.SalesOutbox,
                OnlineSyncLaneTrigger.Manual))
            .ToArray();

        releaseFirst.TrySetResult(true);
        await Task.WhenAll(concurrent.Append(first))
            .WaitAsync(TimeSpan.FromSeconds(5));
        await supervisor.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.AreEqual(2, runs);
        Assert.AreEqual(1, maximumActive);
    }

    [TestMethod]
    public async Task BlockedCatalogLane_DoesNotBlockSalesLane()
    {
        var catalogStarted = NewSignal();
        var releaseCatalog = NewSignal();
        using var supervisor = CreateSupervisor((context, _, cancellationToken) =>
        {
            return context.ExecuteRequestAsync(async _ =>
            {
                if (context.Lane == OnlineSyncLane.CatalogDelta)
                {
                    catalogStarted.TrySetResult(true);
                    await releaseCatalog.Task;
                }
                return TerminalSuccess();
            }, cancellationToken);
        });

        var catalog = supervisor.TriggerAsync(
            OnlineSyncLane.CatalogDelta,
            OnlineSyncLaneTrigger.Manual);
        await catalogStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            var sales = supervisor.TriggerAsync(
                OnlineSyncLane.SalesOutbox,
                OnlineSyncLaneTrigger.LocalCommit);
            var salesOutcome = await sales.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsTrue(salesOutcome.Success);
            Assert.IsFalse(catalog.IsCompleted);
        }
        finally
        {
            releaseCatalog.TrySetResult(true);
        }

        Assert.IsTrue((await catalog.WaitAsync(TimeSpan.FromSeconds(5))).Success);
    }

    [TestMethod]
    public async Task CallerCancellation_DoesNotCancelSharedRunOrOtherWaiter()
    {
        var firstStarted = NewSignal();
        var releaseFirst = NewSignal();
        var runs = 0;
        using var supervisor = CreateSupervisor(async (_, _, _) =>
        {
            if (Interlocked.Increment(ref runs) == 1)
            {
                firstStarted.TrySetResult(true);
                await releaseFirst.Task;
            }
            return TerminalSuccess();
        });

        var first = supervisor.TriggerAsync(
            OnlineSyncLane.CatalogImportOutbox,
            OnlineSyncLaneTrigger.StartOfDay);
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var cancellation = new CancellationTokenSource();
        var cancelledWaiter = supervisor.TriggerAsync(
            OnlineSyncLane.CatalogImportOutbox,
            OnlineSyncLaneTrigger.Foreground,
            cancellation.Token);
        var survivingWaiter = supervisor.TriggerAsync(
            OnlineSyncLane.CatalogImportOutbox,
            OnlineSyncLaneTrigger.Manual);

        cancellation.Cancel();
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(
            async () => await cancelledWaiter);
        releaseFirst.TrySetResult(true);

        Assert.IsTrue((await first.WaitAsync(TimeSpan.FromSeconds(5))).Success);
        Assert.IsTrue((await survivingWaiter.WaitAsync(TimeSpan.FromSeconds(5))).Success);
        Assert.AreEqual(2, runs);
    }

    [TestMethod]
    public async Task AuthenticationDenial_FromEveryLane_CancelsAllLanesAndPersistsOnce()
    {
        foreach (var deniedLane in Enum.GetValues<OnlineSyncLane>())
        {
            var companionLanesStarted = NewSignal();
            var companionCount = 0;
            var networkCalls = 0;
            var stops = 0;
            var runsByLane = new int[Enum.GetValues<OnlineSyncLane>().Length];
            var generation = Generation("generation-auth-" + deniedLane);
            using var supervisor = new OnlineSyncSupervisor(
                generation,
                async (context, _, cancellationToken) =>
                {
                    Interlocked.Increment(ref runsByLane[(int)context.Lane]);
                    if (context.Lane == deniedLane)
                    {
                        await companionLanesStarted.Task;
                        var code = await context.ExecuteCredentialedRequestAsync(
                            (_, _) =>
                            {
                                Interlocked.Increment(ref networkCalls);
                                return Task.FromResult("session_revoked");
                            },
                            responseCode => responseCode,
                            cancellationToken);
                        return OnlineSyncLaneOutcome.AuthDenied(code);
                    }

                    if (Interlocked.Increment(ref companionCount) ==
                        Enum.GetValues<OnlineSyncLane>().Length - 1)
                        companionLanesStarted.TrySetResult(true);
                    return await context.ExecuteRequestAsync(async requestCancellationToken =>
                    {
                        await Task.Delay(
                            Timeout.InfiniteTimeSpan,
                            requestCancellationToken);
                        return TerminalSuccess();
                    }, cancellationToken);
                },
                _ => Task.FromResult(true),
                (_, _) =>
                {
                    Interlocked.Increment(ref stops);
                    return Task.CompletedTask;
                },
                networkConcurrency: Enum.GetValues<OnlineSyncLane>().Length,
                credentialProvider: currentGeneration => Task.FromResult(
                    new OnlineSyncRequestCredentials(
                        currentGeneration,
                        "device-token-test",
                        "session-token-test",
                        "credential-stamp-test")));

            var runs = Enum.GetValues<OnlineSyncLane>()
                .ToDictionary(
                    lane => lane,
                    lane => supervisor.TriggerAsync(
                        lane,
                        OnlineSyncLaneTrigger.Manual));
            await companionLanesStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var outcomes = await Task.WhenAll(runs.Values)
                .WaitAsync(TimeSpan.FromSeconds(5));
            await supervisor.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));

            Assert.IsTrue(runs[deniedLane].Result.AuthenticationDenied, deniedLane.ToString());
            Assert.AreEqual(1, stops, deniedLane.ToString());
            Assert.AreEqual(1, networkCalls, deniedLane.ToString());
            Assert.IsTrue(supervisor.GetSnapshot().AuthenticationStopped);
            CollectionAssert.AreEqual(
                Enumerable.Repeat(
                    1,
                    Enum.GetValues<OnlineSyncLane>().Length).ToArray(),
                runsByLane);
            Assert.IsTrue(outcomes.All(outcome => !outcome.Success));

            var future = await supervisor.TriggerAsync(
                deniedLane == OnlineSyncLane.Heartbeat
                    ? OnlineSyncLane.SalesOutbox
                    : OnlineSyncLane.Heartbeat,
                OnlineSyncLaneTrigger.LocalCommit);
            Assert.IsTrue(future.AuthenticationDenied);
            Assert.AreEqual(
                Enum.GetValues<OnlineSyncLane>().Length,
                runsByLane.Sum());
            Assert.AreEqual(1, networkCalls);
        }
    }

    [TestMethod]
    public async Task SalesBacklog_DoesNotStarveHeartbeat()
    {
        var firstSalesStarted = NewSignal();
        var releaseFirstSales = NewSignal();
        var heartbeatRan = NewSignal();
        var salesRuns = 0;
        using var supervisor = CreateSupervisor(async (context, _, cancellationToken) =>
        {
            if (context.Lane == OnlineSyncLane.Heartbeat)
            {
                return await context.ExecuteRequestAsync(_ =>
                {
                    heartbeatRan.TrySetResult(true);
                    return Task.FromResult(TerminalSuccess());
                }, cancellationToken);
            }

            if (context.Lane != OnlineSyncLane.SalesOutbox)
                return TerminalSuccess();

            var run = Interlocked.Increment(ref salesRuns);
            if (run == 1)
            {
                return await context.ExecuteRequestAsync(async _ =>
                {
                    firstSalesStarted.TrySetResult(true);
                    await releaseFirstSales.Task;
                    return TerminalSuccess();
                }, cancellationToken);
            }
            return TerminalSuccess();
        });

        var firstSales = supervisor.TriggerAsync(
            OnlineSyncLane.SalesOutbox,
            OnlineSyncLaneTrigger.LocalCommit);
        await firstSalesStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var backlog = Enumerable.Range(0, 19)
            .Select(_ => supervisor.TriggerAsync(
                OnlineSyncLane.SalesOutbox,
                OnlineSyncLaneTrigger.LocalCommit))
            .ToArray();
        var heartbeat = supervisor.TriggerAsync(
            OnlineSyncLane.Heartbeat,
            OnlineSyncLaneTrigger.Periodic);

        await heartbeatRan.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsTrue((await heartbeat.WaitAsync(TimeSpan.FromSeconds(5))).Success);
        Assert.IsFalse(firstSales.IsCompleted);

        releaseFirstSales.TrySetResult(true);
        await Task.WhenAll(backlog.Append(firstSales))
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(2, salesRuns);
    }

    [TestMethod]
    public async Task NetworkConcurrencyCap_NeverExceedsTwoAcrossFourLanes()
    {
        var allRunnersEntered = NewSignal();
        var twoRequestsStarted = NewSignal();
        var releaseRequests = NewSignal();
        var runners = 0;
        var requests = 0;
        var active = 0;
        var maximumActive = 0;
        using var supervisor = new OnlineSyncSupervisor(
            Generation("generation-cap"),
            async (context, _, cancellationToken) =>
            {
                if (Interlocked.Increment(ref runners) == 4)
                    allRunnersEntered.TrySetResult(true);
                return await context.ExecuteRequestAsync(async _ =>
                {
                    var currentActive = Interlocked.Increment(ref active);
                    UpdateMaximum(ref maximumActive, currentActive);
                    if (Interlocked.Increment(ref requests) == 2)
                        twoRequestsStarted.TrySetResult(true);
                    try
                    {
                        await releaseRequests.Task;
                        return TerminalSuccess();
                    }
                    finally
                    {
                        Interlocked.Decrement(ref active);
                    }
                }, cancellationToken);
            },
            _ => Task.FromResult(true),
            (_, _) => Task.CompletedTask,
            networkConcurrency: 2);

        var laneRuns = Enum.GetValues<OnlineSyncLane>()
            .Select(lane => supervisor.TriggerAsync(
                lane,
                OnlineSyncLaneTrigger.Manual))
            .ToArray();
        await allRunnersEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await twoRequestsStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.AreEqual(2, Volatile.Read(ref requests));
        Assert.AreEqual(2, Volatile.Read(ref active));
        Assert.AreEqual(2, Volatile.Read(ref maximumActive));

        releaseRequests.TrySetResult(true);
        var outcomes = await Task.WhenAll(laneRuns).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsTrue(outcomes.All(outcome => outcome.Success));
        Assert.AreEqual(Enum.GetValues<OnlineSyncLane>().Length, requests);
        Assert.AreEqual(2, maximumActive);
    }

    [TestMethod]
    public async Task SustainedProductImages_CannotStarveSalesCatalogOrArticleLanes()
    {
        var imageFirstStarted = NewSignal();
        var releaseImageFirst = NewSignal();
        var catalogBlockerStarted = NewSignal();
        var releaseCatalogBlocker = NewSignal();
        var salesQueued = NewSignal();
        var salesStarted = NewSignal();
        var releaseSales = NewSignal();
        var catalogQueued = NewSignal();
        var catalogStarted = NewSignal();
        var articleQueued = NewSignal();
        var articleStarted = NewSignal();
        var imageSecondQueued = NewSignal();
        var imageSecondCompleted = NewSignal();
        var imageThirdStarted = NewSignal();
        var imageThirdCancelled = NewSignal();
        var requestTurns = 0;
        var imageRuns = 0;
        var imageSecondTurn = 0;
        var activeImageRequests = 0;
        var maximumActiveImageRequests = 0;
        var firstTurnByLane = new int[Enum.GetValues<OnlineSyncLane>().Length];
        using var clock = new ManualOnlineSyncClock(
            DateTimeOffset.Parse("2026-08-02T12:00:00Z"));
        using var supervisor = new OnlineSyncSupervisor(
            Generation("generation-product-image-fairness"),
            async (context, _, cancellationToken) =>
            {
                if (context.Lane == OnlineSyncLane.ProductImageOutbox)
                {
                    var run = Interlocked.Increment(ref imageRuns);
                    var request = context.ExecuteRequestAsync(async requestCancellationToken =>
                    {
                        var turn = RecordRequestTurn(
                            context.Lane,
                            ref requestTurns,
                            firstTurnByLane);
                        if (run == 2)
                            Interlocked.Exchange(ref imageSecondTurn, turn);
                        var active = Interlocked.Increment(ref activeImageRequests);
                        UpdateMaximum(ref maximumActiveImageRequests, active);
                        try
                        {
                            if (run == 1)
                            {
                                imageFirstStarted.TrySetResult(true);
                                await releaseImageFirst.Task;
                            }
                            else if (run == 2)
                            {
                                imageSecondCompleted.TrySetResult(true);
                            }
                            else
                            {
                                imageThirdStarted.TrySetResult(true);
                                try
                                {
                                    await WaitForCancellationAsync(
                                        requestCancellationToken);
                                }
                                finally
                                {
                                    imageThirdCancelled.TrySetResult(true);
                                }
                            }

                            return new OnlineSyncLaneOutcome(
                                success: true,
                                hasImmediateMore: true);
                        }
                        finally
                        {
                            Interlocked.Decrement(ref activeImageRequests);
                        }
                    }, cancellationToken);
                    if (run == 2)
                        imageSecondQueued.TrySetResult(true);
                    return await request;
                }

                if (context.Lane == OnlineSyncLane.CatalogImportOutbox)
                {
                    return await context.ExecuteRequestAsync(async _ =>
                    {
                        RecordRequestTurn(
                            context.Lane,
                            ref requestTurns,
                            firstTurnByLane);
                        catalogBlockerStarted.TrySetResult(true);
                        await releaseCatalogBlocker.Task;
                        return TerminalSuccess();
                    }, cancellationToken);
                }

                if (context.Lane == OnlineSyncLane.SalesOutbox ||
                    context.Lane == OnlineSyncLane.CatalogDelta ||
                    context.Lane == OnlineSyncLane.ArticleMutationOutbox)
                {
                    var request = context.ExecuteRequestAsync(async _ =>
                    {
                        RecordRequestTurn(
                            context.Lane,
                            ref requestTurns,
                            firstTurnByLane);
                        if (context.Lane == OnlineSyncLane.SalesOutbox)
                        {
                            salesStarted.TrySetResult(true);
                            await releaseSales.Task;
                        }
                        else if (context.Lane == OnlineSyncLane.CatalogDelta)
                        {
                            catalogStarted.TrySetResult(true);
                        }
                        else
                        {
                            articleStarted.TrySetResult(true);
                        }
                        return TerminalSuccess();
                    }, cancellationToken);
                    if (context.Lane == OnlineSyncLane.SalesOutbox)
                        salesQueued.TrySetResult(true);
                    else if (context.Lane == OnlineSyncLane.CatalogDelta)
                        catalogQueued.TrySetResult(true);
                    else
                        articleQueued.TrySetResult(true);
                    return await request;
                }

                return TerminalSuccess();
            },
            _ => Task.FromResult(true),
            (_, _) => Task.CompletedTask,
            networkConcurrency: 2,
            clock: clock,
            jitter: () => 0.5);

        var firstImage = supervisor.TriggerAsync(
            OnlineSyncLane.ProductImageOutbox,
            OnlineSyncLaneTrigger.LocalCommit);
        await imageFirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var catalogBlocker = supervisor.TriggerAsync(
            OnlineSyncLane.CatalogImportOutbox,
            OnlineSyncLaneTrigger.Manual);
        await catalogBlockerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var sales = supervisor.TriggerAsync(
            OnlineSyncLane.SalesOutbox,
            OnlineSyncLaneTrigger.LocalCommit);
        await salesQueued.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var catalog = supervisor.TriggerAsync(
            OnlineSyncLane.CatalogDelta,
            OnlineSyncLaneTrigger.Periodic);
        await catalogQueued.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var article = supervisor.TriggerAsync(
            OnlineSyncLane.ArticleMutationOutbox,
            OnlineSyncLaneTrigger.LocalCommit);
        await articleQueued.Task.WaitAsync(TimeSpan.FromSeconds(5));

        releaseImageFirst.TrySetResult(true);
        Assert.IsTrue((await firstImage.WaitAsync(TimeSpan.FromSeconds(5))).Success);
        await salesStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await clock.AdvanceNextAsync();
        await imageSecondQueued.Task.WaitAsync(TimeSpan.FromSeconds(5));

        releaseCatalogBlocker.TrySetResult(true);
        await catalogStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await articleStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await imageSecondCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.AreEqual(3, firstTurnByLane[(int)OnlineSyncLane.SalesOutbox]);
        Assert.AreEqual(4, firstTurnByLane[(int)OnlineSyncLane.CatalogDelta]);
        Assert.AreEqual(5, firstTurnByLane[(int)OnlineSyncLane.ArticleMutationOutbox]);
        Assert.AreEqual(6, imageSecondTurn);
        Assert.AreEqual(1, maximumActiveImageRequests);

        releaseSales.TrySetResult(true);
        var nonImageOutcomes = await Task.WhenAll(
                catalogBlocker,
                sales,
                catalog,
                article)
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsTrue(nonImageOutcomes.All(outcome => outcome.Success));
        await clock.AdvanceNextAsync();
        await imageThirdStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var stop = supervisor.StopAsync();
        await imageThirdCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await stop.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.AreEqual(3, imageRuns);
        Assert.AreEqual(7, requestTurns);
        Assert.AreEqual(1, maximumActiveImageRequests);
        Assert.IsTrue(supervisor.GetSnapshot().Lanes.All(
            lane => !lane.InFlight && !lane.Pending));
    }

    [TestMethod]
    public async Task ConcurrentStop_CancelsActiveAndQueuedRequestsAndIsIdempotent()
    {
        var allRunnersEntered = NewSignal();
        var twoRequestsStarted = NewSignal();
        var laneCount = Enum.GetValues<OnlineSyncLane>().Length;
        var runners = 0;
        var requests = 0;
        using var supervisor = new OnlineSyncSupervisor(
            Generation("generation-stop"),
            async (context, _, cancellationToken) =>
            {
                if (Interlocked.Increment(ref runners) == laneCount)
                    allRunnersEntered.TrySetResult(true);
                return await context.ExecuteRequestAsync(async requestCancellationToken =>
                {
                    if (Interlocked.Increment(ref requests) == 2)
                        twoRequestsStarted.TrySetResult(true);
                    await Task.Delay(Timeout.InfiniteTimeSpan, requestCancellationToken);
                    return TerminalSuccess();
                }, cancellationToken);
            },
            _ => Task.FromResult(true),
            (_, _) => Task.CompletedTask,
            networkConcurrency: 2);

        var laneRuns = Enum.GetValues<OnlineSyncLane>()
            .Select(lane => supervisor.TriggerAsync(
                lane,
                OnlineSyncLaneTrigger.Manual))
            .ToArray();
        await allRunnersEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await twoRequestsStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var firstStop = supervisor.StopAsync();
        var secondStop = supervisor.StopAsync();
        Assert.AreSame(firstStop, secondStop);
        await firstStop.WaitAsync(TimeSpan.FromSeconds(5));
        var outcomes = await Task.WhenAll(laneRuns).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.AreEqual(2, requests);
        Assert.IsTrue(outcomes.All(outcome =>
            !outcome.Success &&
            (outcome.Code == "generation_cancelled" ||
             outcome.Code == "stale_generation")));
        Assert.IsTrue(supervisor.GetSnapshot().Lanes.All(
            lane => !lane.InFlight && !lane.Pending));
        var future = await supervisor.TriggerAsync(
            OnlineSyncLane.Heartbeat,
            OnlineSyncLaneTrigger.Periodic);
        Assert.AreEqual("sync_stopped", future.Code);
    }

    [TestMethod]
    public async Task ShutdownRace_AwaitsAuthenticationDenialAlreadyObservedByRequest()
    {
        var freshCredentialReadStarted = NewSignal();
        var releaseFreshCredentialRead = NewSignal();
        var authStopStarted = NewSignal();
        var releaseAuthStop = NewSignal();
        var credentialReads = 0;
        var authStops = 0;
        var generation = Generation("generation-shutdown-auth-race");
        using var supervisor = new OnlineSyncSupervisor(
            generation,
            async (context, _, cancellationToken) =>
            {
                var denialCode = await context.ExecuteCredentialedRequestAsync(
                    (_, _) => Task.FromResult("session_revoked"),
                    responseCode => responseCode,
                    cancellationToken);
                return OnlineSyncLaneOutcome.AuthDenied(denialCode);
            },
            _ => Task.FromResult(true),
            async (_, _) =>
            {
                Interlocked.Increment(ref authStops);
                authStopStarted.TrySetResult(true);
                await releaseAuthStop.Task;
            },
            credentialProvider: async currentGeneration =>
            {
                if (Interlocked.Increment(ref credentialReads) == 2)
                {
                    freshCredentialReadStarted.TrySetResult(true);
                    await releaseFreshCredentialRead.Task;
                }
                return new OnlineSyncRequestCredentials(
                    currentGeneration,
                    "device-token-test",
                    "session-token-test",
                    "credential-stamp-test");
            });

        var deniedRun = supervisor.TriggerAsync(
            OnlineSyncLane.Heartbeat,
            OnlineSyncLaneTrigger.Periodic);
        await freshCredentialReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var stop = supervisor.StopAsync();
        releaseFreshCredentialRead.TrySetResult(true);
        await authStopStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsFalse(stop.IsCompleted);
        Assert.AreEqual(1, authStops);

        releaseAuthStop.TrySetResult(true);
        await stop.WaitAsync(TimeSpan.FromSeconds(5));
        var outcome = await deniedRun.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsTrue(outcome.AuthenticationDenied);
        Assert.AreEqual(1, authStops);
        Assert.IsTrue(supervisor.GetSnapshot().AuthenticationStopped);
    }

    [TestMethod]
    public async Task RelinkDuringInFlightRequest_CancelsOldGenerationAndRunsNewOne()
    {
        var oldStarted = NewSignal();
        var oldCancelled = NewSignal();
        using var oldSupervisor = new OnlineSyncSupervisor(
            Generation("generation-before-relink"),
            (context, _, cancellationToken) =>
            {
                return context.ExecuteRequestAsync(async requestCancellationToken =>
                {
                    oldStarted.TrySetResult(true);
                    try
                    {
                        await Task.Delay(
                            Timeout.InfiniteTimeSpan,
                            requestCancellationToken);
                        return TerminalSuccess();
                    }
                    catch (OperationCanceledException)
                    {
                        oldCancelled.TrySetResult(true);
                        throw;
                    }
                }, cancellationToken);
            },
            _ => Task.FromResult(true),
            (_, _) => Task.CompletedTask);

        var oldRun = oldSupervisor.TriggerAsync(
            OnlineSyncLane.CatalogDelta,
            OnlineSyncLaneTrigger.PartialResume);
        await oldStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var oldStop = oldSupervisor.StopAsync();
        using var newSupervisor = new OnlineSyncSupervisor(
            Generation("generation-after-relink"),
            (_, _, _) => Task.FromResult(TerminalSuccess()),
            _ => Task.FromResult(true),
            (_, _) => Task.CompletedTask);
        var newRun = newSupervisor.TriggerAsync(
            OnlineSyncLane.Heartbeat,
            OnlineSyncLaneTrigger.FirstBootstrap);

        await oldCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await oldStop.WaitAsync(TimeSpan.FromSeconds(5));
        var oldOutcome = await oldRun.WaitAsync(TimeSpan.FromSeconds(5));
        var newOutcome = await newRun.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.AreEqual("generation_cancelled", oldOutcome.Code);
        Assert.IsTrue(newOutcome.Success);
        Assert.AreNotEqual(
            oldSupervisor.Generation.Fingerprint,
            newSupervisor.Generation.Fingerprint);
    }

    [TestMethod]
    public async Task GenerationBecomingStale_DiscardsOutcomeAndSchedule()
    {
        var runnerStarted = NewSignal();
        var releaseRunner = NewSignal();
        var current = 1;
        using var supervisor = new OnlineSyncSupervisor(
            Generation("generation-stale"),
            async (_, _, _) =>
            {
                runnerStarted.TrySetResult(true);
                await releaseRunner.Task;
                return new OnlineSyncLaneOutcome(
                    success: true,
                    nextPollAfterSeconds: 30);
            },
            _ => Task.FromResult(Volatile.Read(ref current) == 1),
            (_, _) => Task.CompletedTask,
            jitter: () => 0.5);

        var run = supervisor.TriggerAsync(
            OnlineSyncLane.Heartbeat,
            OnlineSyncLaneTrigger.Periodic);
        await runnerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Volatile.Write(ref current, 0);
        releaseRunner.TrySetResult(true);

        var outcome = await run.WaitAsync(TimeSpan.FromSeconds(5));
        await supervisor.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var lane = supervisor.GetSnapshot().Lanes.Single(
            item => item.Lane == OnlineSyncLane.Heartbeat);

        Assert.IsFalse(outcome.Success);
        Assert.AreEqual("stale_generation", outcome.Code);
        Assert.IsNull(lane.LastOutcome);
        Assert.IsNull(lane.NextDueAt);
        Assert.AreEqual(0, lane.FailureCount);
    }

    [TestMethod]
    public void SchedulePolicy_RespectsImmediateTerminalAndServerRetryBoundaries()
    {
        var now = DateTimeOffset.Parse("2026-07-19T12:00:00Z");
        var immediate = OnlineSyncLaneSchedulePolicy.Evaluate(
            OnlineSyncLane.SalesOutbox,
            new OnlineSyncLaneOutcome(
                success: false,
                code: "network_error",
                hasImmediateMore: true),
            2,
            now,
            0.5);
        var terminal = OnlineSyncLaneSchedulePolicy.Evaluate(
            OnlineSyncLane.CatalogImportOutbox,
            new OnlineSyncLaneOutcome(
                success: false,
                code: "blocked",
                terminal: true),
            5,
            now,
            0.5);
        var retryAt = now.AddMinutes(2).ToUnixTimeMilliseconds();
        var delayed = OnlineSyncLaneSchedulePolicy.Evaluate(
            OnlineSyncLane.SalesOutbox,
            new OnlineSyncLaneOutcome(
                success: false,
                code: "retry_later",
                nextRetryAt: retryAt),
            0,
            now,
            0);

        Assert.IsTrue(immediate.ShouldSchedule);
        Assert.IsTrue(immediate.Delay >= TimeSpan.FromSeconds(1));
        Assert.IsTrue(immediate.Delay <= TimeSpan.FromSeconds(5));
        Assert.AreEqual(3, immediate.FailureCount);
        Assert.IsFalse(terminal.ShouldSchedule);
        Assert.AreEqual(0, terminal.FailureCount);
        Assert.IsTrue(delayed.ShouldSchedule);
        Assert.IsTrue(delayed.Delay >= TimeSpan.FromMinutes(2));
    }

    [TestMethod]
    public async Task AwaitedCatalogHint_IsOrchestratedByCaller_WhileBackgroundHintFansOut()
    {
        var catalogRan = NewSignal();
        var catalogRuns = 0;
        using var supervisor = CreateSupervisor((context, _, _) =>
        {
            if (context.Lane == OnlineSyncLane.Heartbeat)
            {
                return Task.FromResult(new OnlineSyncLaneOutcome(
                    success: true,
                    requestCatalogNow: true,
                    terminal: true));
            }

            if (context.Lane == OnlineSyncLane.CatalogDelta)
            {
                Interlocked.Increment(ref catalogRuns);
                catalogRan.TrySetResult(true);
            }
            return Task.FromResult(TerminalSuccess());
        });

        var awaited = await supervisor.TriggerAsync(
            OnlineSyncLane.Heartbeat,
            OnlineSyncLaneTrigger.StartOfDay);
        await supervisor.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsTrue(awaited.RequestCatalogNow);
        Assert.AreEqual(0, catalogRuns);

        supervisor.Signal(
            OnlineSyncLane.Heartbeat,
            OnlineSyncLaneTrigger.Periodic);
        await catalogRan.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await supervisor.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(1, catalogRuns);
    }

    [TestMethod]
    public void AmbiguousPaginationFailure_RequiresExplicitOperatorRetry()
    {
        var state = new CatalogSyncState(
            bootstrapCompleted: false,
            failure: CatalogSyncFailure.TerminalPaginationAmbiguous);

        var decision = CatalogSyncPolicy.Evaluate(
            CatalogSyncTrigger.Periodic,
            state);

        Assert.AreEqual(CatalogSyncMode.Blocked, decision.Mode);
        Assert.IsTrue(decision.IsBlocking);
        Assert.AreEqual(
            CatalogPaginationSafetyPolicy.AmbiguousEndCode,
            decision.DiagnosticCode);
    }

    private static OnlineSyncSupervisor CreateSupervisor(OnlineSyncLaneRunner runner)
    {
        return new OnlineSyncSupervisor(
            Generation("generation-test"),
            runner,
            _ => Task.FromResult(true),
            (_, _) => Task.CompletedTask,
            jitter: () => 0.5);
    }

    private static OnlineSyncGeneration Generation(string id)
    {
        return new OnlineSyncGeneration(
            id,
            "session-test",
            "device-test",
            "shop-test",
            "SHOP-TEST");
    }

    private static OnlineSyncLaneOutcome TerminalSuccess()
    {
        return new OnlineSyncLaneOutcome(success: true, terminal: true);
    }

    private static TaskCompletionSource<bool> NewSignal()
    {
        return new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static int RecordRequestTurn(
        OnlineSyncLane lane,
        ref int requestTurns,
        int[] firstTurnByLane)
    {
        var turn = Interlocked.Increment(ref requestTurns);
        Interlocked.CompareExchange(
            ref firstTurnByLane[(int)lane],
            turn,
            0);
        return turn;
    }

    private static async Task WaitForCancellationAsync(
        CancellationToken cancellationToken)
    {
        var cancelled = NewSignal();
        using (cancellationToken.Register(
                   () => cancelled.TrySetResult(true)))
        {
            await cancelled.Task;
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static void UpdateMaximum(ref int target, int candidate)
    {
        var observed = Volatile.Read(ref target);
        while (candidate > observed)
        {
            var previous = Interlocked.CompareExchange(ref target, candidate, observed);
            if (previous == observed)
                return;
            observed = previous;
        }
    }

    private sealed class ManualOnlineSyncClock : IOnlineSyncClock, IDisposable
    {
        private readonly object _gate = new object();
        private readonly Queue<PendingDelay> _pending = new Queue<PendingDelay>();
        private readonly SemaphoreSlim _queued = new SemaphoreSlim(0);
        private DateTimeOffset _utcNow;

        internal ManualOnlineSyncClock(DateTimeOffset utcNow)
        {
            _utcNow = utcNow;
        }

        public DateTimeOffset UtcNow
        {
            get
            {
                lock (_gate) return _utcNow;
            }
        }

        public Task DelayAsync(
            TimeSpan delay,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pending = new PendingDelay(delay);
            pending.Cancellation = cancellationToken.Register(
                () => pending.Completion.TrySetCanceled(cancellationToken));
            lock (_gate) _pending.Enqueue(pending);
            _queued.Release();
            return AwaitDelayAsync(pending);
        }

        internal async Task AdvanceNextAsync()
        {
            while (true)
            {
                Assert.IsTrue(
                    await _queued.WaitAsync(TimeSpan.FromSeconds(5)),
                    "No scheduled supervisor delay was queued.");
                PendingDelay pending;
                lock (_gate)
                {
                    pending = _pending.Dequeue();
                    if (!pending.Completion.Task.IsCompleted)
                        _utcNow = _utcNow.Add(pending.Delay);
                }
                if (pending.Completion.TrySetResult(true)) return;
            }
        }

        public void Dispose()
        {
            _queued.Dispose();
        }

        private static async Task AwaitDelayAsync(PendingDelay pending)
        {
            try
            {
                await pending.Completion.Task;
            }
            finally
            {
                pending.Cancellation.Dispose();
            }
        }

        private sealed class PendingDelay
        {
            internal PendingDelay(TimeSpan delay)
            {
                Delay = delay;
                Completion = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            }

            internal CancellationTokenRegistration Cancellation { get; set; }
            internal TaskCompletionSource<bool> Completion { get; }
            internal TimeSpan Delay { get; }
        }
    }
}
