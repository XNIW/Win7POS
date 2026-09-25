using System.Collections.Generic;
using System.Threading.Tasks;

namespace Win7POS.Wpf.Pos
{
    public interface IHeldCartWorkflow
    {
        Task<IReadOnlyList<HeldCartItem>> GetHeldCartsAsync();
        Task<IReadOnlyList<HoldLineDisplay>> PeekHeldCartLinesAsync(string holdId);
        Task DeleteHeldCartAsync(string holdId);
        Task<PosWorkflowSnapshot> RecoverHeldCartAsync(string holdId);
    }
}
