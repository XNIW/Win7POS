namespace Win7POS.Wpf.Printing
{
    public sealed class InstalledPrinterInfo
    {
        public string Name { get; set; } = string.Empty;
        public bool IsDefault { get; set; }
        public bool IsVirtual { get; set; }
        public bool IsPhysical => !IsVirtual;
        public bool IsAvailable { get; set; }
        public bool IsOffline { get; set; }
        public bool IsPaused { get; set; }
        public string StatusText { get; set; } = string.Empty;
        public string DriverName { get; set; } = string.Empty;
        public string PortName { get; set; } = string.Empty;
        public string Notes { get; set; } = string.Empty;

        public string Summary
        {
            get
            {
                var flags = string.Empty;
                if (IsDefault) flags += " [default]";
                if (IsVirtual) flags += " [virtual/PDF]";
                if (IsPaused) flags += " [paused]";
                if (IsOffline) flags += " [offline]";
                if (!IsAvailable) flags += " [not available]";
                return (Name ?? string.Empty) + flags;
            }
        }

        public override string ToString()
        {
            return Summary;
        }
    }
}
