using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Threading;
using Win7POS.Wpf.Pos;
using Win7POS.Wpf.Pos.Dialogs;

namespace Win7POS.Wpf.UiSmokeHarness
{
    internal static class HeldCartsViewModelSmoke
    {
        internal static async Task RunAsync()
        {
            var fake = new Fake();
            var vm = new HeldCartsViewModel(fake, _ => { });
            var a = new HeldCartsViewModel.HoldRow { HoldId = "A" };
            var b = new HeldCartsViewModel.HoldRow { HoldId = "B" };
            vm.Items.Add(a); vm.Items.Add(b);
            vm.SelectedHold = a;
            vm.SelectedHold = b;
            fake.B.SetResult(new[] { new HoldLineDisplay { Barcode = "B", Name = "B", Qty = 1, UnitPrice = 20 } });
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            fake.A.SetResult(new[] { new HoldLineDisplay { Barcode = "A", Name = "A", Qty = 1, UnitPrice = 10 } });
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            FunctionalCompletionSmoke.Require(vm.SelectedLines.Count == 1 && vm.SelectedLines[0].Barcode == "B", "stale A replaced B preview");
            vm.SelectedHold = a;
            vm.DeleteCommand.Execute(null);
            vm.SelectedHold = b;
            vm.DeleteCommand.Execute(null);
            FunctionalCompletionSmoke.Require(fake.Deletes == 1 && fake.DeletedId == "A", "duplicate or retargeted deletion");
            fake.Delete.SetResult(true);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            FunctionalCompletionSmoke.Require(vm.Items.Count == 1 && ReferenceEquals(vm.Items[0], b) && ReferenceEquals(vm.SelectedHold, b), "deletion removed changed selection");
            var closedFake = new Fake();
            var closed = new HeldCartsViewModel(closedFake, _ => throw new Exception("updated after close"));
            closed.SelectedHold = a;
            var changes = 0;
            closed.PropertyChanged += (_, __) => changes++;
            closed.SelectedLines.CollectionChanged += (_, __) => changes++;
            FunctionalCompletionSmoke.Require(closed.TryClose(), "preview cannot close");
            closedFake.A.SetException(new InvalidOperationException("late preview failure"));
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            FunctionalCompletionSmoke.Require(changes == 0, "closed view model updated");
        }

        private sealed class Fake : IHeldCartWorkflow
        {
            internal readonly TaskCompletionSource<IReadOnlyList<HoldLineDisplay>> A = new TaskCompletionSource<IReadOnlyList<HoldLineDisplay>>();
            internal readonly TaskCompletionSource<IReadOnlyList<HoldLineDisplay>> B = new TaskCompletionSource<IReadOnlyList<HoldLineDisplay>>();
            internal readonly TaskCompletionSource<bool> Delete = new TaskCompletionSource<bool>();
            internal int Deletes;
            internal string DeletedId;
            public Task<IReadOnlyList<HeldCartItem>> GetHeldCartsAsync() => Task.FromResult<IReadOnlyList<HeldCartItem>>(Array.Empty<HeldCartItem>());
            public Task<IReadOnlyList<HoldLineDisplay>> PeekHeldCartLinesAsync(string id) => id == "A" ? A.Task : B.Task;
            public Task DeleteHeldCartAsync(string id) { Deletes++; DeletedId = id; return Delete.Task; }
            public Task<PosWorkflowSnapshot> RecoverHeldCartAsync(string id) => Task.FromResult(new PosWorkflowSnapshot());
        }
    }
}
