using System;

namespace Win7POS.Wpf.Pos.Online
{
    /// <summary>
    /// The version declared to the POS backend. Keeping it in the production WPF
    /// assembly prevents test executables from identifying themselves as clients.
    /// </summary>
    public static class PosApplicationVersion
    {
        public static string GetCurrent()
        {
            try
            {
                return typeof(PosOnlineBootstrapService).Assembly.GetName().Version?.ToString() ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}
