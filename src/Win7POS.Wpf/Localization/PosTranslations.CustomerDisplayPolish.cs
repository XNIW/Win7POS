using System.Collections.Generic;

namespace Win7POS.Wpf.Localization
{
    public sealed partial class PosLocalization
    {
        private static void AddCustomerDisplayPolishTranslations(Dictionary<string, Dictionary<string, string>> catalog)
        {
            var entries = new[]
            {
                new TranslationEntry("customerDisplay.polish.logo", "Customer logo (PNG / JPEG / BMP)", "Logo de cliente (PNG / JPEG / BMP)", "Logo cliente (PNG / JPEG / BMP)", "客户标志（PNG / JPEG / BMP）"),
                new TranslationEntry("customerDisplay.polish.importLogo", "Choose logo…", "Elegir logo…", "Scegli logo…", "选择标志…"),
                new TranslationEntry("customerDisplay.polish.removeLogo", "Remove logo", "Quitar logo", "Rimuovi logo", "移除标志"),
                new TranslationEntry("customerDisplay.polish.logoError", "Choose a valid local image up to 2 MiB and 4 million pixels. Locked or changed files cannot be used.", "Elija una imagen local válida de hasta 2 MiB y 4 millones de píxeles. No se pueden usar archivos bloqueados o modificados.", "Scegli un'immagine locale valida fino a 2 MiB e 4 milioni di pixel. I file bloccati o modificati non sono utilizzabili.", "请选择不超过 2 MiB、400 万像素的有效本地图像。无法使用被锁定或更改的文件。"),
                new TranslationEntry("customerDisplay.polish.logoPosition.Left", "Logo on the left", "Logo a la izquierda", "Logo a sinistra", "标志居左"),
                new TranslationEntry("customerDisplay.polish.logoPosition.Center", "Centered logo", "Logo centrado", "Logo centrato", "标志居中"),
                new TranslationEntry("customerDisplay.polish.idleMode", "Idle screen", "Pantalla en reposo", "Schermata inattiva", "空闲画面"),
                new TranslationEntry("customerDisplay.polish.idleMode.Welcome", "Welcome", "Bienvenida", "Benvenuto", "欢迎"),
                new TranslationEntry("customerDisplay.polish.idleMode.CustomMessage", "Custom message (120 characters)", "Mensaje propio (120 caracteres)", "Messaggio personalizzato (120 caratteri)", "自定义消息（120 字符）"),
                new TranslationEntry("customerDisplay.polish.idleMode.Clock", "Clock", "Reloj", "Orologio", "时钟"),
                new TranslationEntry("customerDisplay.polish.barcodeMode", "Barcode privacy", "Privacidad de códigos", "Privacy barcode", "条码隐私"),
                new TranslationEntry("customerDisplay.polish.barcodeMode.Hidden", "Hidden", "Oculto", "Nascosto", "隐藏"),
                new TranslationEntry("customerDisplay.polish.barcodeMode.Last4", "Last four characters", "Últimos cuatro caracteres", "Ultimi quattro caratteri", "最后四个字符"),
                new TranslationEntry("customerDisplay.polish.barcodeMode.Full", "Full barcode", "Código completo", "Barcode completo", "完整条码"),
                new TranslationEntry("customerDisplay.polish.showPaid", "Show paid amount", "Mostrar importe pagado", "Mostra importo pagato", "显示已付金额"),
                new TranslationEntry("customerDisplay.polish.showChange", "Show cash change", "Mostrar vuelto", "Mostra resto", "显示找零"),
                new TranslationEntry("customerDisplay.polish.testPattern", "Display test — no sale data", "Prueba de pantalla — sin venta", "Test display — nessun dato vendita", "显示测试 — 不含销售数据"),
                new TranslationEntry("customerDisplay.polish.stopPreview", "End preview / test", "Finalizar vista / prueba", "Termina anteprima / test", "结束预览 / 测试"),
                new TranslationEntry("customerDisplay.polish.duration", "Preview / test duration (5–60 seconds)", "Duración de vista / prueba (5–60 segundos)", "Durata anteprima / test (5–60 secondi)", "预览 / 测试时长（5–60 秒）"),
                new TranslationEntry("customerDisplay.polish.copyDiagnostics", "Copy monitor diagnostics", "Copiar diagnóstico de monitores", "Copia diagnostica monitor", "复制显示器诊断"),
                new TranslationEntry("customerDisplay.error.idle_message", "Message: up to 120 characters, without control characters or markup.", "Mensaje: hasta 120 caracteres, sin controles ni marcado.", "Messaggio: fino a 120 caratteri, senza controlli o markup.", "消息最多 120 个字符，不能包含控制字符或标记。"),
                new TranslationEntry("customerDisplay.error.test_pattern_duration", "Choose a duration from 5 to 60 seconds.", "Elija entre 5 y 60 segundos.", "Scegli una durata da 5 a 60 secondi.", "请选择 5 至 60 秒的时长。"),
                new TranslationEntry("customerDisplay.error.logo_file", "Choose the logo again; its managed reference is invalid.", "Elija el logo de nuevo; su referencia no es válida.", "Scegli di nuovo il logo; il riferimento gestito non è valido.", "请重新选择标志，其托管引用无效。")
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
