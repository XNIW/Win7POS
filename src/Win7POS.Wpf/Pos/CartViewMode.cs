using System;

namespace Win7POS.Wpf.Pos
{
    public enum CartViewMode
    {
        Rows,
        Grid
    }

    public static class CartViewModeSetting
    {
        public const string RowsValue = "rows";
        public const string GridValue = "grid";

        public static CartViewMode Parse(string value)
        {
            return string.Equals(
                (value ?? string.Empty).Trim(),
                GridValue,
                StringComparison.OrdinalIgnoreCase)
                ? CartViewMode.Grid
                : CartViewMode.Rows;
        }

        public static string Serialize(CartViewMode mode)
        {
            return mode == CartViewMode.Grid ? GridValue : RowsValue;
        }
    }
}
