namespace Win7POS.Core.Models
{
    public sealed class Product
    {
        public long Id { get; set; }
        public string Barcode { get; set; }   // UNIQUE
        public string Name { get; set; }
        public int UnitPrice { get; set; }    // intero (centesimi / pesos)
    }
}
