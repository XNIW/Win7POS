using System.Collections.Generic;

namespace Win7POS.Wpf.Localization
{
    public sealed partial class PosLocalization
    {
        private static void AddHardwareTranslations(Dictionary<string, Dictionary<string, string>> catalog)
        {
            var entries = new[]
            {
                new TranslationEntry("hardware.receiptProfile", "Receipt paper profile", "Perfil de papel del recibo", "Profilo carta ricevuta", "收据纸张配置"),
                new TranslationEntry("hardware.receipt58", "58 mm · 32 columns", "58 mm · 32 columnas", "58 mm · 32 colonne", "58毫米 · 32列"),
                new TranslationEntry("hardware.receipt80", "80 mm · 42 columns", "80 mm · 42 columnas", "80 mm · 42 colonne", "80毫米 · 42列"),
                new TranslationEntry("hardware.enter", "Enter", "Enter", "Invio", "回车"),
                new TranslationEntry("hardware.tab", "Tab", "Tab", "Tab", "Tab"),
                new TranslationEntry("hardware.enterOrTab", "Enter or Tab", "Enter o Tab", "Invio o Tab", "回车或Tab"),
                new TranslationEntry("hardware.pin2", "ESC/POS · pin 2", "ESC/POS · pin 2", "ESC/POS · pin 2", "ESC/POS · 引脚2"),
                new TranslationEntry("hardware.pin5", "ESC/POS · pin 5", "ESC/POS · pin 5", "ESC/POS · pin 5", "ESC/POS · 引脚5"),
                new TranslationEntry("hardware.custom", "Validated custom pulse", "Pulso personalizado validado", "Impulso personalizzato validato", "已验证的自定义脉冲"),
                new TranslationEntry("hardware.scanner", "Scanner input and isolated test", "Entrada de escaner y prueba aislada", "Input scanner e test isolato", "扫描输入与独立测试"),
                new TranslationEntry("hardware.scannerHelp", "Keyboard input only. Arm the test before scanning. The test never changes the cart or catalog; its barcode stays on this screen only.", "Solo entrada de teclado. Arme la prueba antes de escanear. La prueba no cambia el carrito ni el catalogo; el codigo queda solo en esta pantalla.", "Solo input da tastiera. Arma il test prima di scansionare. Il test non modifica carrello o catalogo; il barcode resta solo in questa schermata.", "仅接收键盘输入。扫描前请启用测试。测试不会更改购物车或目录，条码仅显示在此屏幕。"),
                new TranslationEntry("hardware.terminator", "Submit key", "Tecla de envio", "Tasto invio input", "提交键"),
                new TranslationEntry("hardware.prefix", "Exact prefix (0–16 ASCII characters)", "Prefijo exacto (0–16 caracteres ASCII)", "Prefisso esatto (0–16 caratteri ASCII)", "精确前缀（0至16个ASCII字符）"),
                new TranslationEntry("hardware.suffix", "Exact suffix (0–16 ASCII characters)", "Sufijo exacto (0–16 caracteres ASCII)", "Suffisso esatto (0–16 caratteri ASCII)", "精确后缀（0至16个ASCII字符）"),
                new TranslationEntry("hardware.trim", "Trim outer whitespace", "Quitar espacios exteriores", "Rimuovi spazi esterni", "移除首尾空白"),
                new TranslationEntry("hardware.minLength", "Minimum length (1–128)", "Longitud minima (1–128)", "Lunghezza minima (1–128)", "最小长度（1至128）"),
                new TranslationEntry("hardware.maxLength", "Maximum length (1–256)", "Longitud maxima (1–256)", "Lunghezza massima (1–256)", "最大长度（1至256）"),
                new TranslationEntry("hardware.armScannerTest", "Arm one scanner test", "Armar una prueba de escaner", "Arma un test scanner", "启用一次扫描测试"),
                new TranslationEntry("hardware.scannerResult", "Accepted {0}; input time {1} ms", "Aceptado {0}; tiempo de entrada {1} ms", "Accettato {0}; tempo input {1} ms", "已接收{0}；输入耗时{1}毫秒"),
                new TranslationEntry("hardware.testPassedAt", "Test passed at {0:g}", "Prueba superada el {0:g}", "Test superato il {0:g}", "测试通过时间：{0:g}"),
                new TranslationEntry("hardware.unknownNeedsTest", "Unknown · needs test", "Desconocido · necesita prueba", "Sconosciuto · da testare", "未知 · 需要测试"),
                new TranslationEntry("hardware.configuredNeedsTest", "Configured · needs physical test", "Configurado · necesita prueba fisica", "Configurato · da testare fisicamente", "已配置 · 需要实机测试"),
                new TranslationEntry("hardware.disabled", "Disabled", "Desactivado", "Disabilitato", "已禁用"),
                new TranslationEntry("hardware.testFailed", "Test failed · needs review", "Prueba fallida · necesita revision", "Test fallito · da verificare", "测试失败 · 需要检查"),
                new TranslationEntry("hardware.warningQueue", "Warning · queue missing, offline or interactive", "Advertencia · cola ausente, desconectada o interactiva", "Warning · coda assente, offline o interattiva", "警告 · 队列缺失、离线或需要交互"),
                new TranslationEntry("hardware.drawerOpened", "Confirm that the drawer opened", "Confirmar que el cajon se abrio", "Conferma che il cassetto si è aperto", "确认钱箱已打开"),
                new TranslationEntry("hardware.queueDiagnostics", "Read queue diagnostics", "Leer diagnostico de cola", "Leggi diagnostica coda", "读取打印队列诊断"),
                new TranslationEntry("hardware.queueDetails", "Queue: {0}\nDriver: {1}\nPort: {2}\nStatus: 0x{3}\nJobs: {4} ({5})\nQueue metadata does not prove physical output.", "Cola: {0}\nControlador: {1}\nPuerto: {2}\nEstado: 0x{3}\nTrabajos: {4} ({5})\nLos metadatos no prueban la salida fisica.", "Coda: {0}\nDriver: {1}\nPorta: {2}\nStato: 0x{3}\nJob: {4} ({5})\nI metadati non provano l'output fisico.", "队列：{0}\n驱动程序：{1}\n端口：{2}\n状态：0x{3}\n作业：{4}（{5}）\n队列信息不能证明实机输出。"),
                new TranslationEntry("hardware.jobsRead", "job enumeration available", "lista de trabajos disponible", "elenco job disponibile", "可读取作业列表"),
                new TranslationEntry("hardware.jobsUnknown", "job enumeration unavailable", "lista de trabajos no disponible", "elenco job non disponibile", "无法读取作业列表"),
                new TranslationEntry("hardware.diagnostic.timeout", "Queue read timed out. The driver is still running; further requests reuse that worker.", "Tiempo de espera agotado. El controlador sigue activo; las solicitudes reutilizan el mismo proceso.", "Attesa scaduta. Il driver è ancora attivo; le nuove richieste riusano lo stesso worker.", "队列读取超时。驱动调用仍在执行，后续请求将复用该任务。"),
                new TranslationEntry("hardware.diagnostic.busy", "Another queue is still being read. Try again when it finishes.", "Otra cola sigue en lectura. Reintente cuando termine.", "Un'altra coda è ancora in lettura. Riprova al termine.", "另一队列仍在读取，请等待完成后重试。"),
                new TranslationEntry("hardware.diagnostic.unavailable", "Queue unavailable or renamed; health is unknown.", "Cola no disponible o renombrada; estado desconocido.", "Coda non disponibile o rinominata; salute sconosciuta.", "队列不可用或已重命名，健康状态未知。"),
                new TranslationEntry("hardware.invalidSettings", "Invalid hardware settings.", "Configuracion de hardware invalida.", "Impostazioni hardware non valide.", "硬件设置无效。"),
                new TranslationEntry("scanner.invalidSettings", "Scanner settings are invalid.", "Configuracion de escaner invalida.", "Impostazioni scanner non valide.", "扫描设置无效。"),
                new TranslationEntry("scanner.invalidLength", "Scanner input is outside the configured length limits.", "La entrada supera los limites de longitud.", "Input fuori dai limiti di lunghezza configurati.", "扫描输入超出配置的长度限制。"),
                new TranslationEntry("scanner.affixMismatch", "Scanner prefix or suffix does not match.", "El prefijo o sufijo no coincide.", "Prefisso o suffisso scanner non corrispondente.", "扫描前缀或后缀不匹配。"),
                new TranslationEntry("scanner.invalidCharacters", "Scanner input contains unsupported control characters.", "La entrada contiene caracteres de control no admitidos.", "Input con caratteri di controllo non supportati.", "扫描输入包含不支持的控制字符。")
            };
            foreach (var entry in entries)
            {
                catalog["en"][entry.Key] = entry.En; catalog["es"][entry.Key] = entry.Es;
                catalog["it"][entry.Key] = entry.It; catalog["zh-CN"][entry.Key] = entry.ZhCn;
            }
        }
    }
}
