using System.Threading.Tasks;

namespace Win7POS.Wpf.Printing
{
    public interface IReceiptPrinter
    {
        Task PrintAsync(string receiptText, ReceiptPrintOptions opt);

        /// <summary>Apre il cassetto portamonete (ESC/POS kick drawer). Opzionale: se la stampante non lo supporta può restare no-op.</summary>
        Task OpenCashDrawerAsync(ReceiptPrintOptions opt);
    }
}
