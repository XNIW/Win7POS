using System;
using Win7POS.Wpf.Localization;

namespace Win7POS.Wpf.Printing
{
    internal static class PrinterHardwareSafety
    {
        public static void DemandHardwareOutputAllowed()
        {
            if (App.IsSafeStart)
                throw new InvalidOperationException(
                    PosLocalization.T("printer.hardwareDisabledSafeStart"));
        }
    }
}
