using System.Threading.Tasks;

namespace Win7POS.Wpf.Printing
{
    public interface IReceiptPrinter
    {
        Task PrintAsync(string receiptText, ReceiptPrintOptions opt);
    }
}
