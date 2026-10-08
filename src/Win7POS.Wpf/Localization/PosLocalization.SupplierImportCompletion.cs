using System.Collections.Generic;

namespace Win7POS.Wpf.Localization
{
    public sealed partial class PosLocalization
    {
        private static void AddSupplierImportCompletionTranslations(Dictionary<string, Dictionary<string, string>> catalog)
        {
            catalog["en"]["supplierExcelImport.statusOperationCancelling"] = "Cancelling import operation...";
            catalog["es"]["supplierExcelImport.statusOperationCancelling"] = "Cancelando la operación de importación...";
            catalog["it"]["supplierExcelImport.statusOperationCancelling"] = "Annullamento importazione in corso...";
            catalog["zh-CN"]["supplierExcelImport.statusOperationCancelling"] = "正在取消导入操作...";
            catalog["en"]["supplierExcelImport.statusOperationCancelled"] = "Import operation cancelled. Your draft is preserved; you can review it and retry.";
            catalog["es"]["supplierExcelImport.statusOperationCancelled"] = "Operación de importación cancelada. Se conserva el borrador; puedes revisarlo y volver a intentarlo.";
            catalog["it"]["supplierExcelImport.statusOperationCancelled"] = "Importazione annullata. La bozza è conservata: puoi controllarla e riprovare.";
            catalog["zh-CN"]["supplierExcelImport.statusOperationCancelled"] = "导入操作已取消。草稿已保留，可检查后重试。";
            catalog["en"]["supplierExcelImport.issueInvalidPrice"] = "Invalid price in {0}. Enter a finite value from 0 to {1} with supported decimal precision.";
            catalog["es"]["supplierExcelImport.issueInvalidPrice"] = "Precio no válido en {0}. Introduce un valor finito entre 0 y {1} con precisión decimal admitida.";
            catalog["it"]["supplierExcelImport.issueInvalidPrice"] = "Prezzo non valido in {0}. Inserisci un valore finito da 0 a {1} con precisione decimale supportata.";
            catalog["zh-CN"]["supplierExcelImport.issueInvalidPrice"] = "{0} 中的价格无效。请输入 0 到 {1} 之间的有限值，并使用支持的小数精度。";
            catalog["en"]["supplierExcelImport.issueInvalidText"] = "Invalid text in {0}. Use at most {1} characters and remove control characters.";
            catalog["es"]["supplierExcelImport.issueInvalidText"] = "Texto no válido en {0}. Usa un máximo de {1} caracteres y elimina los caracteres de control.";
            catalog["it"]["supplierExcelImport.issueInvalidText"] = "Testo non valido in {0}. Usa al massimo {1} caratteri e rimuovi i caratteri di controllo.";
            catalog["zh-CN"]["supplierExcelImport.issueInvalidText"] = "{0} 中的文本无效。最多使用 {1} 个字符，并移除控制字符。";
        }
    }
}
