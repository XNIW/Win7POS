using System.Collections.Generic;

namespace Win7POS.Wpf.Localization
{
    public sealed partial class PosLocalization
    {
        private static void AddStockQuantityTranslations(Dictionary<string, Dictionary<string, string>> catalog)
        {
            catalog["en"]["products.invalidStockQuantity"] = "Enter a nonnegative stock quantity with at most three decimal places.";
            catalog["es"]["products.invalidStockQuantity"] = "Introduzca una cantidad de existencias no negativa con un máximo de tres decimales.";
            catalog["it"]["products.invalidStockQuantity"] = "Inserire uno stock non negativo con al massimo tre cifre decimali.";
            catalog["zh-CN"]["products.invalidStockQuantity"] = "请输入非负库存数量，最多保留三位小数。";
        }
    }
}
