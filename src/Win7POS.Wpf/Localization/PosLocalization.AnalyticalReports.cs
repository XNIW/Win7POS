using System.Collections.Generic;

namespace Win7POS.Wpf.Localization
{
    public sealed partial class PosLocalization
    {
        private static void AddAnalyticalReportTranslations(Dictionary<string, Dictionary<string, string>> catalog)
        {
            var rows = new[]
            {
                new[] { "historyReport.analyticalTitle", "Sales summary · analytical", "Resumen de ventas · analítico", "Riepilogo vendite · analitico", "销售汇总 · 分析" },
                new[] { "historyReport.origin", "Origin", "Origen", "Origine", "来源" },
                new[] { "historyReport.originAll", "POS + imported", "POS + importadas", "POS + importate", "POS + 导入" },
                new[] { "historyReport.originNative", "POS receipts", "Comprobantes POS", "Scontrini POS", "POS 小票" },
                new[] { "historyReport.originGetnet", "Imported · Getnet", "Importadas · Getnet", "Importato · Getnet", "已导入 · Getnet" },
                new[] { "historyReport.import", "Import history", "Importar historial", "Importa storico", "导入历史" },
                new[] { "historyReport.coverageNotLoaded", "Load an authorized shop report.", "Carga un informe de la tienda autorizada.", "Caricare il prospetto del negozio autorizzato.", "请加载授权门店的报表。" },
                new[] { "historyReport.coverage.PolicyRequired", "Shop time policy required. Select Getnet only to view imported card movements.", "Falta la política horaria de la tienda. Selecciona solo Getnet para ver los movimientos importados.", "Serve la policy oraria del negozio. Selezionare solo Getnet per consultare i movimenti importati.", "需要门店时间规则。可选择仅 Getnet 查看已导入的银行卡流水。" },
                new[] { "historyReport.coverage.PendingOwnershipVerification", "Shop attribution verification pending; totals are unavailable.", "Verificación de pertenencia a la tienda pendiente; totales no disponibles.", "Verifica di attribuzione al negozio in corso; totali non disponibili.", "门店归属验证尚未完成；暂无合计。" },
                new[] { "historyReport.coverage.VerifiedSubset", "Verified attributable portion. Coverage is not certified complete.", "Parte atribuible verificada. No se certifica cobertura completa.", "Parte attribuibile verificata. Copertura completa non certificata.", "已验证可归属部分；未证明覆盖完整。" },
                new[] { "historyReport.coverage.VerifiedForScope", "Verified for the selected shop, origins and period.", "Verificado para la tienda, los orígenes y el período seleccionados.", "Verificato per negozio, origini e periodo selezionati.", "已验证所选门店、来源和期间。" },
                new[] { "historyReport.coverage.VerifiedEmpty", "No movements in the verified selection.", "No hay movimientos en la selección verificada.", "Nessun movimento nella selezione verificata.", "已验证的筛选范围内没有流水。" },
                new[] { "historyReport.coverage.CloudReplicaRequired", "Getnet cloud copy is incomplete. Synchronize history before viewing totals.", "La copia Getnet está incompleta. Sincroniza el historial antes de consultar totales.", "La copia cloud Getnet è incompleta. Sincronizzare lo storico prima di consultare i totali.", "Getnet 云端副本尚未完整同步，请先同步历史记录再查看总额。" },
                new[] { "historyReport.coverage.CloudReplicaCached", "Getnet: last complete synchronized copy. New changes may be pending; offline figures are not live.", "Getnet: última copia sincronizada completa. Puede haber cambios pendientes; los datos sin conexión no son en vivo.", "Getnet: ultima copia sincronizzata completa. Potrebbero esserci modifiche in attesa; i dati offline non sono in tempo reale.", "Getnet：上次完整同步的副本。可能还有待同步的更改；离线数据并非实时数据。" },
                new[] { "historyReport.importedCount", "Imported card movements: {0}", "Movimientos de tarjeta importados: {0}", "Movimenti carta importati: {0}", "已导入银行卡流水：{0}" },
                new[] { "historyReport.cardCoverage", "Historical card payments only; historical cash and item details are unavailable.", "Solo pagos históricos con tarjeta; no se dispone de efectivo histórico ni detalle de artículos.", "Storico dei soli pagamenti carta; contanti storici e dettaglio articoli non disponibili.", "历史记录仅包含银行卡支付；无历史现金和商品明细。" },
                new[] { "historyReport.notAvailable", "N/A", "N/D", "N/D", "不适用" },
                new[] { "historyReport.nativeAverage", "Average POS receipt", "Promedio comprobante POS", "Scontrino medio POS", "POS 平均小票" },
                new[] { "historyReport.snapshotStale", "Data changed. Reload the report.", "Los datos cambiaron. Vuelve a cargar el informe.", "Dati cambiati. Ricaricare il prospetto.", "数据已更改，请重新加载报表。" },
                new[] { "historyReport.shopRequired", "An authoritative shop profile is required for this report.", "Este informe requiere un perfil oficial de tienda.", "Per questo prospetto serve un profilo negozio autorevole.", "此报表需要正式门店资料。" },
                new[] { "historyReport.loadFailed", "Report unavailable. Check shop authorization and time policy.", "Informe no disponible. Revisa la autorización y la política horaria de la tienda.", "Prospetto non disponibile. Verificare autorizzazione e policy oraria del negozio.", "报表不可用。请检查门店授权和时间规则。" },
                new[] { "historyReport.exportPublished", "Report snapshot saved.", "Fotografía del informe guardada.", "Fotografia del prospetto salvata.", "已保存报表快照。" },
                new[] { "historyReport.exportPreparing", "Preparing the report snapshot…", "Preparando la fotografía del informe…", "Preparazione fotografia del prospetto…", "正在准备报表快照…" },
                new[] { "historyReport.exportFinalizing", "Finalizing the authorized publication. Waiting for the destination.", "Finalizando la publicación autorizada. Esperando al destino.", "Finalizzazione della pubblicazione autorizzata. Attendere la destinazione.", "正在完成已授权的发布，请等待保存位置响应。" },
                new[] { "historyReport.exportUnknown", "Publication outcome uncertain. Verify the selected destination before retrying.", "Resultado de publicación incierto. Verifica el destino antes de reintentar.", "Esito pubblicazione incerto. Verificare la destinazione prima di riprovare.", "发布结果不确定。重试前请检查所选位置。" },
                new[] { "historyReport.exportFailed", "Report was not published. Reload and check the destination.", "No se publicó el informe. Vuelve a cargar y revisa el destino.", "Prospetto non pubblicato. Ricaricare e verificare la destinazione.", "报表未发布。请重新加载并检查保存位置。" },
                new[] { "historyReport.itemsUnavailable", "Item details unavailable", "Detalle de artículos no disponible", "Dettaglio articoli non disponibile", "无商品明细" },
                new[] { "historyReport.loadMore", "Load more", "Cargar más", "Carica altri", "加载更多" },
                new[] { "historyReport.registerSummary", "POS receipts: {0} · imported movements: {1} · balance: {2}", "Comprobantes POS: {0} · movimientos importados: {1} · saldo: {2}", "Scontrini POS: {0} · movimenti importati: {1} · saldo: {2}", "POS 小票：{0} · 导入流水：{1} · 余额：{2}" },
                new[] { "historyReport.detail", "{0}\nReference: {1}\nCommercial time: {2}\nCard: {3}\n{4}", "{0}\nReferencia: {1}\nHora comercial: {2}\nTarjeta: {3}\n{4}", "{0}\nRiferimento: {1}\nData commerciale: {2}\nCarta: {3}\n{4}", "{0}\n参考编号：{1}\n营业时间：{2}\n银行卡：{3}\n{4}" }
                ,new[] { "reportTimePolicy.configure", "Shop time rules", "Reglas horarias", "Fuso report", "门店时间规则" }
                ,new[] { "reportTimePolicy.title", "Approved shop time policy", "Política horaria aprobada de la tienda", "Policy oraria approvata del negozio", "已批准的门店时间规则" }
                ,new[] { "reportTimePolicy.instructions", "Select a canonical policy file approved for this shop. Review its source, UTC coverage and hash. Computer time-zone settings are never used as a fallback.", "Selecciona un archivo canónico aprobado para esta tienda. Revisa su procedencia, cobertura UTC y hash. No se utiliza la zona horaria del equipo como alternativa.", "Selezionare un file canonico approvato per il negozio. Verificare provenienza, copertura UTC e hash. Il fuso del computer non viene usato come alternativa.", "选择为此门店批准的规范规则文件。请检查来源、UTC 覆盖范围和哈希值。系统不会使用电脑时区作为后备。" }
                ,new[] { "reportTimePolicy.select", "Select approved JSON…", "Seleccionar JSON aprobado…", "Seleziona JSON approvato…", "选择已批准的 JSON…" }
                ,new[] { "reportTimePolicy.fileFilter", "Approved time policy (*.json)|*.json", "Política horaria aprobada (*.json)|*.json", "Policy oraria approvata (*.json)|*.json", "已批准的时间规则 (*.json)|*.json" }
                ,new[] { "reportTimePolicy.preview", "Policy: {0}\nVersion: {1}\nProvenance: {2}\nUTC coverage: [{3}, {4})\nSegments: {5}\nPolicy SHA-256: {6}\nSource SHA-256: {7}", "Política: {0}\nVersión: {1}\nProcedencia: {2}\nCobertura UTC: [{3}, {4})\nSegmentos: {5}\nSHA-256 de política: {6}\nSHA-256 del archivo: {7}", "Policy: {0}\nVersione: {1}\nProvenienza: {2}\nCopertura UTC: [{3}, {4})\nSegmenti: {5}\nSHA-256 policy: {6}\nSHA-256 sorgente: {7}", "规则：{0}\n版本：{1}\n来源：{2}\nUTC 范围：[{3}, {4})\n分段数：{5}\n规则 SHA-256：{6}\n文件 SHA-256：{7}" }
                ,new[] { "reportTimePolicy.destination", "Authoritative destination shop", "Tienda de destino oficial", "Negozio destinazione autorevole", "正式目标门店" }
                ,new[] { "reportTimePolicy.evidence", "Approval evidence reference", "Referencia de evidencia de aprobación", "Riferimento della prova di approvazione", "批准依据引用" }
                ,new[] { "reportTimePolicy.activationNotice", "Activation creates a verified backup and changes native report date conversion. Imported Getnet commercial dates stay fixed. Activate only after checking this policy and the destination shop.", "La activación crea un respaldo verificado y cambia la conversión de fechas del informe POS. Las fechas comerciales importadas de Getnet permanecen fijas. Activa después de revisar la política y la tienda de destino.", "L'attivazione crea un backup verificato e cambia la conversione delle date dei report POS. Le date commerciali Getnet importate restano fissate. Attivare dopo aver verificato policy e negozio destinazione.", "激活将创建已验证的备份并更改 POS 报表的日期转换。已导入的 Getnet 营业日期保持不变。请核对规则和目标门店后再激活。" }
                ,new[] { "reportTimePolicy.activate", "Activate reviewed policy", "Activar política revisada", "Attiva policy verificata", "激活已核对的规则" }
                ,new[] { "reportTimePolicy.ready", "Review complete. Enter the approval evidence before activation.", "Revisión completa. Introduce la evidencia de aprobación antes de activar.", "Anteprima disponibile. Inserire la prova di approvazione prima di attivare.", "预览已就绪。激活前请输入批准依据。" }
                ,new[] { "reportTimePolicy.shopMissing", "An authoritative shop profile is required for activation. The policy can be reviewed now.", "Se requiere un perfil oficial de tienda para activar. Puedes revisar la política ahora.", "Per attivare serve un profilo negozio autorevole. È possibile verificare ora la policy.", "激活需要正式门店资料；现在仍可核对规则。" }
                ,new[] { "reportTimePolicy.working", "Checking policy or creating the protected activation…", "Verificando la política o preparando la activación protegida…", "Verifica della policy o preparazione dell'attivazione protetta…", "正在核对规则或准备受保护的激活…" }
                ,new[] { "reportTimePolicy.cancelled", "Operation cancelled before activation completed.", "Operación cancelada antes de completar la activación.", "Operazione annullata prima del completamento dell'attivazione.", "操作在激活完成前已取消。" }
                ,new[] { "reportTimePolicy.failed", "Policy not activated. Verify the approved file, hash, shop authorization and backup destination.", "No se activó la política. Revisa el archivo aprobado, hash, autorización de tienda y destino del respaldo.", "Policy non attivata. Verificare file approvato, hash, autorizzazione negozio e destinazione backup.", "规则未激活。请检查批准文件、哈希值、门店授权和备份位置。" }
                ,new[] { "reportTimePolicy.activated", "Approved policy activated: {0}", "Política aprobada activada: {0}", "Policy approvata attivata: {0}", "已激活批准的规则：{0}" }
                ,new[] { "reportTimePolicy.authorizationChanged", "Authorization changed. Review the file and destination again.", "Cambió la autorización. Revisa de nuevo el archivo y el destino.", "Autorizzazione cambiata. Verificare nuovamente file e destinazione.", "授权已更改。请重新核对文件和目标门店。" }
            };
            foreach (var row in rows)
            {
                catalog["en"][row[0]] = row[1]; catalog["es"][row[0]] = row[2]; catalog["it"][row[0]] = row[3]; catalog["zh-CN"][row[0]] = row[4];
            }
        }
    }
}
