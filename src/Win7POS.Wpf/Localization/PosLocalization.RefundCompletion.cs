using System.Collections.Generic;

namespace Win7POS.Wpf.Localization
{
    public sealed partial class PosLocalization
    {
        private static void AddRefundCompletionTranslations(Dictionary<string, Dictionary<string, string>> catalog)
        {
            var entries = new[]
            {
                new TranslationEntry("refund.saved", "Return saved: {0}", "Devolución guardada: {0}", "Reso salvato: {0}", "退货已保存：{0}"),
                new TranslationEntry("refund.savedPrintAccepted", "Return saved: {0}. Print request accepted by the channel.", "Devolución guardada: {0}. Solicitud aceptada por el canal de impresión.", "Reso salvato: {0}. Richiesta accettata dal canale di stampa.", "退货已保存：{0}。打印通道已接受请求。"),
                new TranslationEntry("refund.savedPrintFailed", "Return saved: {0}. Printing failed: {1}. Reprint the saved return.", "Devolución guardada: {0}. Falló la impresión: {1}. Reimprima la devolución guardada.", "Reso salvato: {0}, stampa non riuscita: {1}. Ristampare il reso salvato.", "退货已保存：{0}。打印失败：{1}。请重新打印已保存的退货单。"),
                new TranslationEntry("refund.reprintSaved", "Reprint return {0}", "Reimprimir devolución {0}", "Ristampa reso {0}", "重新打印退货单 {0}"),
                new TranslationEntry("refund.reprintAccepted", "Saved return: print request accepted by the channel.", "Devolución guardada: solicitud aceptada por el canal de impresión.", "Reso salvato: richiesta accettata dal canale di stampa.", "已保存的退货单：打印通道已接受请求。")
            };
            foreach (var entry in entries)
            {
                catalog["en"][entry.Key] = entry.En;
                catalog["es"][entry.Key] = entry.Es;
                catalog["it"][entry.Key] = entry.It;
                catalog["zh-CN"][entry.Key] = entry.ZhCn;
            }
        }
    }
}
