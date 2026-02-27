using System;

namespace Win7POS.Core.Pos
{
    public sealed class PosException : Exception
    {
        public PosErrorCode Code { get; }
        public string ContextValue { get; }

        public PosException(PosErrorCode code, string contextValue = null, string message = null, Exception innerException = null)
            : base(message ?? BuildMessage(code, contextValue), innerException)
        {
            Code = code;
            ContextValue = contextValue;
        }

        private static string BuildMessage(PosErrorCode code, string contextValue)
        {
            switch (code)
            {
                case PosErrorCode.ProductNotFound:
                    return contextValue == null ? "Prodotto non trovato." : $"Prodotto non trovato: {contextValue}";
                case PosErrorCode.EmptyCart:
                    return "Cart is empty.";
                case PosErrorCode.InvalidQuantity:
                    return contextValue == null ? "Invalid quantity." : $"Invalid quantity: {contextValue}";
                case PosErrorCode.InvalidBarcode:
                    return "Invalid barcode.";
                default:
                    return "POS error.";
            }
        }
    }
}
