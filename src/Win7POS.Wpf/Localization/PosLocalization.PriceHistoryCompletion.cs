using System.Collections.Generic;

namespace Win7POS.Wpf.Localization
{
    public sealed partial class PosLocalization
    {
        private static void AddPriceHistoryCompletionTranslations(Dictionary<string, Dictionary<string, string>> catalog)
        {
            catalog["en"]["priceHistory.invalidPrice"] = "Enter a whole CLP amount from 0 to {0}.";
            catalog["es"]["priceHistory.invalidPrice"] = "Introduce un importe entero en CLP entre 0 y {0}.";
            catalog["it"]["priceHistory.invalidPrice"] = "Inserisci un importo intero in CLP tra 0 e {0}.";
            catalog["zh-CN"]["priceHistory.invalidPrice"] = "请输入 0 到 {0} 之间的整数 CLP 金额。";
            catalog["en"]["priceHistory.noPriceChanges"] = "No price changes to apply.";
            catalog["es"]["priceHistory.noPriceChanges"] = "No hay cambios de precio que aplicar.";
            catalog["it"]["priceHistory.noPriceChanges"] = "Nessuna modifica ai prezzi da applicare.";
            catalog["zh-CN"]["priceHistory.noPriceChanges"] = "没有需要应用的价格更改。";
            catalog["en"]["priceHistory.savingPrices"] = "Saving prices…";
            catalog["es"]["priceHistory.savingPrices"] = "Guardando precios…";
            catalog["it"]["priceHistory.savingPrices"] = "Salvataggio prezzi…";
            catalog["zh-CN"]["priceHistory.savingPrices"] = "正在保存价格…";
        }
    }
}
