namespace Win7POS.Wpf.Infrastructure.Security
{
    /// <summary>Riferimento condiviso alla sessione operatore (per binding header MainWindow).</summary>
    public static class OperatorSessionHolder
    {
        public static IOperatorSession Current { get; set; }
    }
}
