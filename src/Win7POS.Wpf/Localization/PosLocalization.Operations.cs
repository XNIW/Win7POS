using System.Collections.Generic;

namespace Win7POS.Wpf.Localization
{
    public sealed partial class PosLocalization
    {
        private static void AddOperationsTranslations(Dictionary<string, Dictionary<string, string>> catalog)
        {
            var entries = new[]
            {
                new TranslationEntry("settingsProfile.title", "Portable settings", "Configuración portátil", "Impostazioni portabili", "可移植设置"),
                new TranslationEntry("settingsProfile.help", "Profiles contain safe preferences only. Hardware activation and local targets require local configuration. SHA-256 detects corruption; it does not authenticate the file.", "Los perfiles contienen solo preferencias permitidas. Active los dispositivos y configure los destinos localmente. SHA-256 detecta alteraciones; no autentica el archivo.", "I profili contengono solo preferenze consentite. Attivazione hardware e destinazioni richiedono configurazione locale. SHA-256 verifica l'integrità, non l'autenticità.", "配置文件仅包含允许的首选项。硬件启用和本地目标需在本机配置。SHA-256用于检测文件损坏，不能验证来源。"),
                new TranslationEntry("settingsProfile.export", "Export profile", "Exportar perfil", "Esporta profilo", "导出配置"),
                new TranslationEntry("settingsProfile.preview", "Preview import", "Vista previa de importación", "Anteprima importazione", "预览导入"),
                new TranslationEntry("settingsProfile.apply", "Apply profile", "Aplicar perfil", "Applica profilo", "应用配置"),
                new TranslationEntry("settingsProfile.audit", "Settings audit", "Auditoría de configuración", "Audit impostazioni", "设置审计"),
                new TranslationEntry("settingsProfile.reset", "Restore defaults", "Restablecer valores", "Ripristina predefiniti", "恢复默认设置"),
                new TranslationEntry("settingsProfile.filter", "Win7POS settings (*.win7pos-settings.json)|*.win7pos-settings.json", "Configuración Win7POS (*.win7pos-settings.json)|*.win7pos-settings.json", "Impostazioni Win7POS (*.win7pos-settings.json)|*.win7pos-settings.json", "Win7POS配置 (*.win7pos-settings.json)|*.win7pos-settings.json"),
                new TranslationEntry("settingsProfile.exported", "Profile exported.", "Perfil exportado.", "Profilo esportato.", "配置已导出。"),
                new TranslationEntry("settingsProfile.failed", "Settings operation failed or permission changed. No partial settings were saved.", "La operación falló o cambió el permiso. No se guardaron cambios parciales.", "Operazione fallita o permesso cambiato. Nessuna impostazione salvata parzialmente.", "设置操作失败或权限已更改，未保存部分设置。"),
                new TranslationEntry("settingsProfile.invalid", "Invalid profile: check size, schema, checksum and values.", "Perfil inválido: revise tamaño, esquema, suma y valores.", "Profilo non valido: verificare dimensione, schema, checksum e valori.", "配置无效，请检查大小、结构、校验和及设置值。"),
                new TranslationEntry("settingsProfile.diffCount", "Changes in preview: {0}", "Cambios previstos: {0}", "Modifiche in anteprima: {0}", "预览更改：{0}"),
                new TranslationEntry("settingsProfile.confirmImport", "Apply the displayed changes? Catalog, sales and session data are preserved.", "¿Aplicar los cambios mostrados? Se conservan catálogo, ventas y sesión.", "Applicare le modifiche visualizzate? Catalogo, vendite e sessione sono preservati.", "应用所显示的更改？目录、销售和会话数据将保留。"),
                new TranslationEntry("settingsProfile.applied", "Profile applied atomically.", "Perfil aplicado de forma atómica.", "Profilo applicato atomicamente.", "配置已完整应用。"),
                new TranslationEntry("settingsProfile.applyFailed", "Profile not applied. Settings or permission may have changed; preview again.", "Perfil no aplicado. La configuración o el permiso pudo cambiar; genere otra vista previa.", "Profilo non applicato. Impostazioni o permessi possono essere cambiati: ripetere l'anteprima.", "配置未应用。设置或权限可能已更改，请重新预览。"),
                new TranslationEntry("settingsProfile.confirmReset", "Restore defaults for the selected scope? Hardware will be disabled and backup scheduling stopped in their respective scopes. Catalog, sales and session data are preserved.", "¿Restablecer el ámbito seleccionado? Los dispositivos y la programación de copias se desactivan en sus ámbitos. Se conservan catálogo, ventas y sesión.", "Ripristinare l'ambito selezionato? Hardware e backup programmati saranno disattivati nei rispettivi ambiti. Catalogo, vendite e sessione sono preservati.", "恢复所选范围的默认设置？相应范围内硬件和定时备份将停用。目录、销售和会话数据将保留。"),
                new TranslationEntry("settingsProfile.resetDone", "Defaults restored atomically.", "Valores predeterminados restaurados de forma atómica.", "Predefiniti ripristinati atomicamente.", "默认设置已完整恢复。"),
                new TranslationEntry("settingsProfile.safety.hardware_activation_local", "Automatic print and Windows default targeting remain off. Local hardware activation is required.", "La impresión automática y el destino predeterminado de Windows permanecen apagados. Se requiere activación local.", "Stampa automatica e destinazione predefinita Windows restano disattivate. L'attivazione hardware è locale.", "自动打印和Windows默认打印目标保持关闭，需在本机启用硬件。"),
                new TranslationEntry("settingsProfile.safety.backup_target_invalid", "Backup schedule kept disabled because the local destination is unavailable or invalid.", "La programación sigue desactivada porque el destino local no está disponible o es inválido.", "Backup programmato disattivato: destinazione locale non disponibile o non valida.", "本地目标不可用或无效，定时备份保持关闭。"),
                new TranslationEntry("settingsProfile.scope.hardware", "Scanner / printer / drawer", "Escáner / impresora / caja", "Scanner / stampante / cassetto", "扫描器／打印机／钱箱"),
                new TranslationEntry("settingsProfile.scope.backup", "Backup schedule", "Programación de copias", "Backup programmato", "定时备份"),
                new TranslationEntry("settingsProfile.scope.display", "Customer display", "Pantalla cliente", "Display cliente", "客户显示屏"),
                new TranslationEntry("settingsProfile.scope.language", "Language", "Idioma", "Lingua", "语言"),
                new TranslationEntry("settingsProfile.scope.all", "All portable preferences", "Todas las preferencias portátiles", "Tutte le preferenze portabili", "全部可移植设置")
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
