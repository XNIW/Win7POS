namespace Win7POS.Wpf.Printing
{
    public enum PrinterOutputKind
    {
        Unknown = 0,
        Physical = 1,
        Virtual = 2
    }

    public sealed class InstalledPrinterInfo
    {
        private PrinterOutputKind _outputKind;

        public string Name { get; set; } = string.Empty;
        public bool IsDefault { get; set; }
        public PrinterOutputKind OutputKind
        {
            get => _outputKind;
            set => _outputKind = value;
        }
        public bool IsVirtual
        {
            get => OutputKind == PrinterOutputKind.Virtual;
            set
            {
                if (value)
                    OutputKind = PrinterOutputKind.Virtual;
                else if (OutputKind == PrinterOutputKind.Virtual)
                    OutputKind = PrinterOutputKind.Unknown;
            }
        }
        public bool IsPhysical => OutputKind == PrinterOutputKind.Physical;
        public bool IsInventoryFresh { get; set; }
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
                if (OutputKind == PrinterOutputKind.Unknown) flags += " [output unknown]";
                if (!IsInventoryFresh) flags += " [stale inventory]";
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

        public InstalledPrinterInfo CloneWithInventoryFreshness(bool isInventoryFresh)
        {
            return new InstalledPrinterInfo
            {
                Name = Name,
                IsDefault = IsDefault,
                OutputKind = OutputKind,
                IsInventoryFresh = isInventoryFresh,
                IsAvailable = IsAvailable,
                IsOffline = IsOffline,
                IsPaused = IsPaused,
                StatusText = StatusText,
                DriverName = DriverName,
                PortName = PortName,
                Notes = Notes
            };
        }
    }
}
