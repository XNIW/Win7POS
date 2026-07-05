using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Markup;
using Win7POS.Data.Repositories;
using Win7POS.Wpf.Infrastructure;

namespace Win7POS.Wpf.Localization
{
    public sealed partial class PosLocalization : INotifyPropertyChanged
    {
        public const string DefaultLanguage = "en";

        private static readonly Lazy<Dictionary<string, Dictionary<string, string>>> TranslationCatalog =
            new Lazy<Dictionary<string, Dictionary<string, string>>>(CreateTranslations);

        private static Dictionary<string, Dictionary<string, string>> Translations
        {
            get { return TranslationCatalog.Value; }
        }

        private static readonly HashSet<string> SupportedLanguageCodes =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "en", "es", "it", "zh-CN" };

        private static readonly Dictionary<string, string> LanguageAliases =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "english", "en" },
                { "spanish", "es" },
                { "espanol", "es" },
                { "español", "es" },
                { "italian", "it" },
                { "italiano", "it" },
                { "chinese", "zh-CN" },
                { "zh", "zh-CN" },
                { "zh-cn", "zh-CN" },
                { "zh-hans", "zh-CN" },
                { "中文", "zh-CN" }
            };

        private static bool _frameworkLanguageMetadataApplied;
        private string _currentLanguage = DefaultLanguage;

        private PosLocalization()
        {
            SupportedLanguages = new List<SupportedLanguageOption>
            {
                new SupportedLanguageOption("en", "English"),
                new SupportedLanguageOption("es", "Español"),
                new SupportedLanguageOption("it", "Italiano"),
                new SupportedLanguageOption("zh-CN", "简体中文")
            }.AsReadOnly();

            SetLanguage(DefaultLanguage);
        }

        public static PosLocalization Current { get; } = new PosLocalization();

        public static IReadOnlyList<SupportedLanguageOption> SupportedLanguages { get; private set; }

        public string CurrentLanguage
        {
            get { return _currentLanguage; }
            private set
            {
                if (string.Equals(_currentLanguage, value, StringComparison.Ordinal))
                {
                    return;
                }

                _currentLanguage = value;
                OnPropertyChanged(nameof(CurrentLanguage));
                OnPropertyChanged("Item[]");
                var handler = LanguageChanged;
                if (handler != null)
                {
                    handler(this, EventArgs.Empty);
                }
            }
        }

        public string this[string key]
        {
            get { return Text(key); }
        }

        public static string T(string key)
        {
            return Current.Text(key);
        }

        public static string F(string key, params object[] args)
        {
            return Current.Format(key, args);
        }

        public event EventHandler LanguageChanged;
        public event PropertyChangedEventHandler PropertyChanged;

        public async Task LoadAsync(SettingsRepository settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            var stored = await settings.GetStringAsync(AppSettingKeys.UiLanguage).ConfigureAwait(false);
            SetLanguage(stored);
        }

        public async Task SetLanguageAsync(SettingsRepository settings, string languageCode)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            var normalized = SetLanguage(languageCode);
            await settings.SetStringAsync(AppSettingKeys.UiLanguage, normalized).ConfigureAwait(false);
        }

        public string SetLanguage(string languageCode)
        {
            var normalized = NormalizeLanguage(languageCode);
            ApplyCulture(normalized);
            CurrentLanguage = normalized;
            return normalized;
        }

        public static string NormalizeLanguage(string languageCode)
        {
            var normalized = (languageCode ?? string.Empty).Trim();

            if (normalized.Length == 0)
            {
                return DefaultLanguage;
            }

            if (SupportedLanguageCodes.Contains(normalized))
            {
                return string.Equals(normalized, "zh-CN", StringComparison.OrdinalIgnoreCase)
                    ? "zh-CN"
                    : normalized.ToLowerInvariant();
            }

            string alias;
            if (LanguageAliases.TryGetValue(normalized, out alias))
            {
                return alias;
            }

            var dashIndex = normalized.IndexOf('-');
            if (dashIndex > 0 && LanguageAliases.TryGetValue(normalized.Substring(0, dashIndex), out alias))
            {
                return alias;
            }

            return DefaultLanguage;
        }

        public string Text(string key)
        {
            var normalizedKey = (key ?? string.Empty).Trim();
            if (normalizedKey.Length == 0)
            {
                return string.Empty;
            }

            string value;
            Dictionary<string, string> active;
            if (Translations.TryGetValue(CurrentLanguage, out active) &&
                active.TryGetValue(normalizedKey, out value))
            {
                return value;
            }

            Dictionary<string, string> english;
            if (Translations.TryGetValue(DefaultLanguage, out english) &&
                english.TryGetValue(normalizedKey, out value))
            {
                return value;
            }

            if (Debugger.IsAttached)
            {
                Debug.WriteLine("[i18n] Missing POS key: " + normalizedKey);
            }

            return "[missing:" + normalizedKey + "]";
        }

        public string Format(string key, params object[] args)
        {
            var template = Text(key);
            try
            {
                return string.Format(CultureInfo.CurrentCulture, template, args ?? new object[0]);
            }
            catch (FormatException)
            {
                return template;
            }
        }

        private static void ApplyCulture(string languageCode)
        {
            var cultureName = CultureNameForLanguage(languageCode);
            var culture = CultureInfo.GetCultureInfo(cultureName);
            var xmlLanguage = XmlLanguage.GetLanguage(culture.IetfLanguageTag);
            CultureInfo.DefaultThreadCurrentCulture = culture;
            CultureInfo.DefaultThreadCurrentUICulture = culture;
            Thread.CurrentThread.CurrentCulture = culture;
            Thread.CurrentThread.CurrentUICulture = culture;

            ApplyDefaultFrameworkLanguage(xmlLanguage);
            ApplyLanguageToOpenWindows(xmlLanguage);
        }

        private static void ApplyDefaultFrameworkLanguage(XmlLanguage language)
        {
            if (_frameworkLanguageMetadataApplied)
            {
                return;
            }

            try
            {
                FrameworkElement.LanguageProperty.OverrideMetadata(
                    typeof(FrameworkElement),
                    new FrameworkPropertyMetadata(language));
                _frameworkLanguageMetadataApplied = true;
            }
            catch (InvalidOperationException)
            {
                _frameworkLanguageMetadataApplied = true;
            }
        }

        private static void ApplyLanguageToOpenWindows(XmlLanguage language)
        {
            var application = Application.Current;
            if (application == null)
            {
                return;
            }

            Action apply = () =>
            {
                foreach (Window window in application.Windows)
                {
                    if (window != null)
                    {
                        window.Language = language;
                    }
                }

                if (application.MainWindow != null)
                {
                    application.MainWindow.Language = language;
                }
            };

            var dispatcher = application.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
            {
                dispatcher.BeginInvoke(apply);
                return;
            }

            apply();
        }

        private static string CultureNameForLanguage(string languageCode)
        {
            switch (NormalizeLanguage(languageCode))
            {
                case "es":
                    return "es-CL";
                case "it":
                    return "it-IT";
                case "zh-CN":
                    return "zh-CN";
                default:
                    return "en-US";
            }
        }

        private static Dictionary<string, Dictionary<string, string>> CreateTranslations()
        {
            var catalog = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
            catalog["en"] = new Dictionary<string, string>(StringComparer.Ordinal);
            catalog["es"] = new Dictionary<string, string>(StringComparer.Ordinal);
            catalog["it"] = new Dictionary<string, string>(StringComparer.Ordinal);
            catalog["zh-CN"] = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var entry in Entries)
            {
                catalog["en"][entry.Key] = entry.En;
                catalog["es"][entry.Key] = entry.Es;
                catalog["it"][entry.Key] = entry.It;
                catalog["zh-CN"][entry.Key] = entry.ZhCn;
            }

            foreach (var entry in SecondaryEntries())
            {
                catalog["en"][entry.Key] = entry.En;
                catalog["es"][entry.Key] = entry.Es;
                catalog["it"][entry.Key] = entry.It;
                catalog["zh-CN"][entry.Key] = entry.ZhCn;
            }

            foreach (var entry in ReachableLegacyEntries())
            {
                catalog["en"][entry.Key] = entry.En;
                catalog["es"][entry.Key] = entry.Es;
                catalog["it"][entry.Key] = entry.It;
                catalog["zh-CN"][entry.Key] = entry.ZhCn;
            }

            return catalog;
        }

        private void OnPropertyChanged(string propertyName)
        {
            var handler = PropertyChanged;
            if (handler != null)
            {
                handler(this, new PropertyChangedEventArgs(propertyName));
            }
        }

        private static readonly TranslationEntry[] Entries =
        {
            new TranslationEntry("common.add", "Add", "Agregar", "Aggiungi", "添加"),
            new TranslationEntry("common.backspace", "Del", "Borrar", "Canc", "删除"),
            new TranslationEntry("common.cancel", "Cancel", "Cancelar", "Annulla", "取消"),
            new TranslationEntry("common.ok", "OK", "Aceptar", "OK", "确定"),
            new TranslationEntry("common.card", "Card", "Tarjeta", "Carta", "银行卡"),
            new TranslationEntry("common.cash", "Cash", "Efectivo", "Contanti", "现金"),
            new TranslationEntry("common.close", "Close", "Cerrar", "Chiudi", "关闭"),
            new TranslationEntry("common.confirm", "Confirm", "Confirmar", "Conferma", "确认"),
            new TranslationEntry("common.document", "Document", "Documento", "Documento", "凭证"),
            new TranslationEntry("common.loading", "Processing...", "Procesando...", "Elaborazione in corso...", "处理中..."),
            new TranslationEntry("common.login", "Sign in", "Acceder", "Accedi", "登录"),
            new TranslationEntry("common.open", "Open", "Abrir", "Apri", "打开"),
            new TranslationEntry("common.pay", "Pay", "Pagar", "Paga", "付款"),
            new TranslationEntry("common.price", "Price", "Precio", "Prezzo", "价格"),
            new TranslationEntry("common.product", "Product", "Producto", "Prodotto", "商品"),
            new TranslationEntry("common.quantity", "Qty", "Cant.", "Q.ta", "数量"),
            new TranslationEntry("common.sale", "Sale:", "Venta:", "Vendita:", "销售："),
            new TranslationEntry("common.time", "Date/time:", "Fecha/hora:", "Data/Ora:", "日期/时间："),
            new TranslationEntry("common.total", "Total", "Total", "Totale", "总计"),
            new TranslationEntry("common.userPermissionDenied", "Permission denied", "Permiso denegado", "Permesso negato", "权限被拒绝"),

            new TranslationEntry("shell.aboutSupport", "About / Support", "Acerca de / Soporte", "About / Support", "关于 / 支持"),
            new TranslationEntry("shell.changeLock", "Change / Lock", "Cambiar / Bloquear", "Cambia / Blocca", "切换 / 锁定"),
            new TranslationEntry("shell.changeLockTooltip", "Change operator or lock session", "Cambiar operador o bloquear sesion", "Cambia operatore o blocca sessione", "切换操作员或锁定会话"),
            new TranslationEntry("shell.dailyClose", "Daily close", "Cierre de caja", "Chiusura cassa", "日结"),
            new TranslationEntry("shell.management", "Management", "Gestion", "Gestione", "管理"),
            new TranslationEntry("shell.menu", "Menu", "Menu", "Menu", "菜单"),
            new TranslationEntry("shell.navigation", "Navigation", "Navegacion", "Navigazione", "导航"),
            new TranslationEntry("shell.openCashDrawer", "Open cash drawer", "Abrir caja", "Apri cassa", "打开钱箱"),
            new TranslationEntry("shell.openCashDrawerTooltip", "Opens the cash drawer when configured", "Abre la caja si esta configurada", "Apre il cassetto portamonete se configurato", "配置后打开钱箱"),
            new TranslationEntry("shell.operator", "Operator:", "Operador:", "Operatore:", "操作员："),
            new TranslationEntry("shell.officialShopData", "Official shop data", "Datos oficiales del local", "Dati negozio ufficiali", "官方店铺数据"),
            new TranslationEntry("shell.printerSettings", "Printer settings", "Configuracion de impresora", "Impostazioni stampante", "打印机设置"),
            new TranslationEntry("shell.products", "Products", "Productos", "Prodotti", "商品"),
            new TranslationEntry("shell.salesRegister", "Sales register", "Registro de ventas", "Registro vendite", "销售登记"),
            new TranslationEntry("shell.settings", "Settings", "Configuracion", "Impostazioni", "设置"),
            new TranslationEntry("shell.support", "Support", "Soporte", "Supporto", "支持"),
            new TranslationEntry("shell.syncUnavailable", "Sync: status unavailable", "Sync: estado no disponible", "Sync: stato non disponibile", "同步：状态不可用"),
            new TranslationEntry("shell.usersRoles", "Users and roles", "Usuarios y roles", "Utenti e ruoli", "用户和角色"),

            new TranslationEntry("sync.attention", "attention", "atencion", "attenzione", "注意"),
            new TranslationEntry("sync.blocked", "Blocked", "Bloqueadas", "Bloccate", "已阻塞"),
            new TranslationEntry("sync.blockedAttention", "Blocked/attention", "Bloqueadas/atencion", "Bloccate/attenzione", "已阻塞/需注意"),
            new TranslationEntry("sync.blockedSales", "Blocked sales", "Ventas bloqueadas", "Vendite bloccate", "已阻塞销售"),
            new TranslationEntry("sync.callSupport", "Call manager/support.", "Llamar a gerente/soporte.", "Chiamare manager/assistenza.", "请联系经理/支持。"),
            new TranslationEntry("sync.catalog", "Catalog", "Catalogo", "Catalogo", "目录"),
            new TranslationEntry("sync.catalogBootstrap", "Catalog bootstrap", "Bootstrap catalogo", "Bootstrap catalogo", "目录初始化"),
            new TranslationEntry("sync.catalogBootstrapFailed", "Catalog bootstrap failed", "Bootstrap catalogo fallido", "Bootstrap catalogo fallito", "目录初始化失败"),
            new TranslationEntry("sync.catalogInterruptedResume", "Catalog download interrupted, resume available", "Descarga catalogo interrumpida, reanudacion disponible", "Download interrotto, ripresa disponibile", "目录下载中断，可继续"),
            new TranslationEntry("sync.catalogNeverDownloaded", "Catalog never downloaded", "Catalogo nunca descargado", "Catalogo mai scaricato", "目录从未下载"),
            new TranslationEntry("sync.catalogPartial", "Catalog partial", "Catalogo parcial", "Catalogo parziale", "目录部分同步"),
            new TranslationEntry("sync.catalogPreparing", "Preparing catalog", "Preparando catalogo", "Preparazione catalogo", "正在准备目录"),
            new TranslationEntry("sync.catalogReady", "Catalog ready", "Catalogo listo", "Catalogo pronto", "目录已就绪"),
            new TranslationEntry("sync.catalogUpdating", "Catalog updating", "Actualizando catalogo", "Catalogo in aggiornamento", "正在更新目录"),
            new TranslationEntry("sync.catalogSaleSafe", "Catalog sale-safe", "Catalogo seguro para venta", "Catalogo sicuro per vendita", "目录可安全销售"),
            new TranslationEntry("sync.device", "Device", "Dispositivo", "Dispositivo", "设备"),
            new TranslationEntry("sync.inProgress", "Sync in progress", "Sync en curso", "Sync in corso", "同步中"),
            new TranslationEntry("sync.lastCatalog", "Last catalog", "Ultimo catalogo", "Ultimo catalogo", "最近目录"),
            new TranslationEntry("sync.lastCatalogError", "Last catalog error", "Ultimo error catalogo", "Ultimo errore catalogo", "最近目录错误"),
            new TranslationEntry("sync.lastRestore", "Last restore", "Ultimo restore", "Ultimo restore", "最近恢复"),
            new TranslationEntry("sync.lastSaleSent", "Last sale sent", "Ultima venta enviada", "Ultima vendita inviata", "最近发送销售"),
            new TranslationEntry("sync.lastSalesError", "Last sales error", "Ultimo error ventas", "Ultimo errore vendite", "最近销售错误"),
            new TranslationEntry("sync.localSaleDoNotDelete", "Sync attention: sale saved locally; do not delete data.", "Atencion sync: venta guardada localmente; no borrar datos.", "Attenzione sync: vendita salvata localmente; non cancellare dati.", "同步注意：销售已保存在本地；不要删除数据。"),
            new TranslationEntry("sync.never", "never", "nunca", "mai", "从未"),
            new TranslationEntry("sync.noBlockedSales", "Sync attention: no blocked sales.", "Atencion sync: no hay ventas bloqueadas.", "Attenzione sync: nessuna vendita bloccata.", "同步注意：没有已阻塞销售。"),
            new TranslationEntry("sync.none", "none", "ninguno", "nessuno", "无"),
            new TranslationEntry("sync.notConnected", "Not connected", "No conectado", "Non collegato", "未连接"),
            new TranslationEntry("sync.offlineStaff", "offline staff", "staff offline", "staff offline", "离线员工"),
            new TranslationEntry("sync.online", "Online", "Online", "Online", "在线"),
            new TranslationEntry("sync.payments", "payments", "pagos", "pagamenti", "付款"),
            new TranslationEntry("sync.pendingCatalogImports", "Catalog imports", "Importaciones catalogo", "Import catalogo", "目录导入"),
            new TranslationEntry("sync.pendingSales", "Queued sales", "Ventas en cola", "Vendite in coda", "排队销售"),
            new TranslationEntry("sync.policyUnavailable", "POS policy: unavailable from server.", "Policy POS: no disponible desde servidor.", "Policy POS: non disponibile dal server.", "POS 策略：服务器不可用。"),
            new TranslationEntry("sync.reconnectSession", "Reconnect session", "Reconectar sesion", "Sessione da ricollegare", "重新连接会话"),
            new TranslationEntry("sync.requiresAttention", "Requires attention", "Requiere atencion", "Richiede attenzione", "需要注意"),
            new TranslationEntry("sync.restoreNeedsReview", "DB restore: verify synchronization status.", "Restore DB: verificar estado de sincronizacion.", "Restore DB: verificare stato sincronizzazione.", "数据库恢复：请验证同步状态。"),
            new TranslationEntry("sync.restoreNoReview", "DB restore: no sync review required.", "Restore DB: no requiere revision sync.", "Restore DB: nessuna revisione sync richiesta.", "数据库恢复：无需同步审核。"),
            new TranslationEntry("sync.restoreVerifyBeforeClose", "After restore, verify sync status before closing the intervention.", "Despues de restore, verificar estado sync antes de cerrar la intervencion.", "Dopo restore verificare stato sincronizzazione prima di chiudere intervento.", "恢复后，在结束处理前请验证同步状态。"),
            new TranslationEntry("sync.safeStart", "Safe start: online sync disabled for this launch", "Inicio seguro: sync online deshabilitada en este inicio", "Safe start: online sync disabled for this launch", "安全启动：本次启动禁用在线同步"),
            new TranslationEntry("sync.safeStartTooltip", "Safe start: heartbeat, sales sync, catalog pull and trusted-session refresh are disabled for this launch.", "Inicio seguro: heartbeat, sync ventas, catalog pull y refresh de sesion confiable deshabilitados en este inicio.", "Safe start: heartbeat, sales sync, catalog pull and trusted-session refresh are disabled for this launch.", "安全启动：本次启动禁用心跳、销售同步、目录拉取和可信会话刷新。"),
            new TranslationEntry("sync.sessionVerified", "Session verified", "Sesion verificada", "Sessione verificata", "会话已验证"),
            new TranslationEntry("sync.statusPrefix", "Sync: {0}", "Sync: {0}", "Sync: {0}", "同步：{0}"),
            new TranslationEntry("sync.shop", "Shop", "Negocio", "Negozio", "店铺"),
            new TranslationEntry("sync.staffOnline", "Online staff", "Staff online", "Staff online", "在线员工"),
            new TranslationEntry("sync.toRetry", "To retry", "A reintentar", "Da ritentare", "待重试"),
            new TranslationEntry("sync.unavailable", "unavailable", "no disponible", "non disponibile", "不可用"),
            new TranslationEntry("sync.versionUnavailable", "version unavailable", "version no disponible", "versione non disponibile", "版本不可用"),

            new TranslationEntry("settings.language", "Language", "Idioma", "Lingua", "语言"),
            new TranslationEntry("settings.languageSaved", "Language saved.", "Idioma guardado.", "Lingua salvata.", "语言已保存。"),
            new TranslationEntry("settings.languageSaveError", "Language could not be saved.", "No se pudo guardar el idioma.", "Impossibile salvare la lingua.", "无法保存语言。"),

            new TranslationEntry("operator.login.title", "Operator sign-in", "Acceso operador", "Accesso operatore", "操作员登录"),
            new TranslationEntry("operator.login.operator", "Operator:", "Operador:", "Operatore:", "操作员："),
            new TranslationEntry("operator.login.helper", "Select the displayed operator. The username is shown in parentheses.", "Selecciona el operador mostrado. El usuario se muestra entre parentesis.", "Seleziona l'operatore visualizzato. Lo username e mostrato tra parentesi.", "选择显示的操作员，用户名显示在括号中。"),
            new TranslationEntry("operator.login.pin", "PIN:", "PIN:", "PIN:", "PIN："),
            new TranslationEntry("operator.login.onlineConnect", "Connect POS online", "Conectar POS online", "Collega POS online", "连接 POS 在线"),
            new TranslationEntry("operator.login.noOperators", "No operators are configured. Initial setup will start.", "No hay operadores configurados. Se iniciara la configuracion inicial.", "Non esistono operatori configurati. Verra avviata la configurazione iniziale.", "未配置操作员，将启动初始设置。"),
            new TranslationEntry("operator.login.selectOperator", "Select an operator from the list.", "Selecciona un operador de la lista.", "Seleziona un operatore dalla lista.", "请从列表中选择操作员。"),
            new TranslationEntry("operator.login.sessionMissing", "Session was not initialized.", "La sesion no fue inicializada.", "Sessione non inizializzata.", "会话未初始化。"),
            new TranslationEntry("operator.login.locked", "Account temporarily locked. Try again in a few minutes.", "Cuenta temporalmente bloqueada. Intenta nuevamente en unos minutos.", "Account temporaneamente bloccato. Riprova tra qualche minuto.", "账号暂时锁定，请几分钟后重试。"),
            new TranslationEntry("operator.login.invalid", "Operator or PIN is invalid.", "Operador o PIN no validos.", "Operatore o PIN non validi.", "操作员或 PIN 无效。"),
            new TranslationEntry("operator.login.pinChangeRequired", "PIN change is required before access.", "Debes cambiar el PIN antes de acceder.", "E obbligatorio cambiare il PIN per accedere.", "必须先更改 PIN 才能访问。"),
            new TranslationEntry("operator.login.onlineLinked", "Device connected. The online session will be verified at startup.", "Dispositivo conectado. La sesion online se verificara al iniciar.", "Dispositivo collegato. La sessione online verra verificata all'avvio.", "设备已连接。在线会话将在启动时验证。"),

            new TranslationEntry("pos.cart.productHeader", "Product", "Producto", "Prodotto", "商品"),
            new TranslationEntry("pos.cart.quantityHeader", "Qty", "Cant.", "Q.ta", "数量"),
            new TranslationEntry("pos.cart.priceHeader", "Price", "Precio", "Prezzo", "价格"),
            new TranslationEntry("pos.cart.totalHeader", "Total", "Total", "Totale", "总计"),
            new TranslationEntry("pos.cart.editPrice", "Edit price", "Editar precio", "Modifica prezzo", "编辑价格"),
            new TranslationEntry("pos.cart.editQuantity", "Edit quantity", "Editar cantidad", "Modifica quantita", "编辑数量"),
            new TranslationEntry("pos.cart.scannerTitle", "Entry / Scanner", "Ingreso / Scanner", "Inserimento / Scanner", "输入 / 扫描"),
            new TranslationEntry("pos.cart.barcode", "Barcode", "Codigo de barras", "Codice a barre", "条码"),
            new TranslationEntry("pos.cart.discount", "Discount /", "Descuento /", "Sconto /", "折扣 /"),
            new TranslationEntry("pos.cart.changeQuantity", "Change quantity +", "Cambiar cantidad +", "Modifica quantita +", "更改数量 +"),
            new TranslationEntry("pos.cart.printLast", "Print last", "Imprimir ultima", "Stampa ultima", "打印上一张"),
            new TranslationEntry("pos.cart.refundScanReceipt", "Return (scan receipt)", "Devolucion (scan boleta)", "Reso (scan scontrino)", "退货（扫码小票）"),
            new TranslationEntry("pos.cart.refundTooltip", "Scan the receipt code: if one sale is found, return opens directly", "Escanea el codigo de boleta: si encuentra una venta, abre devolucion directamente", "Scansiona il codice scontrino: se trova 1 vendita apre il reso direttamente", "扫描小票码：若找到一笔销售，将直接打开退货"),
            new TranslationEntry("pos.cart.suspend", "Suspend", "Suspender", "Sospendi", "挂起"),
            new TranslationEntry("pos.cart.recover", "Recover", "Recuperar", "Recupera", "恢复"),
            new TranslationEntry("pos.cart.clear", "Clear", "Vaciar", "Svuota", "清空"),
            new TranslationEntry("pos.cart.itemsCountPrefix", "Items:", "Articulos:", "Articoli:", "件数："),
            new TranslationEntry("pos.cart.totalSpend", "Total spend", "Total compra", "Totale spesa", "消费总额"),
            new TranslationEntry("pos.cart.discountLabel", "Discount", "Descuento", "Sconto", "折扣"),
            new TranslationEntry("pos.cart.quantityReady", "Quantity ready: {0}", "Cantidad lista: {0}", "Quantita pronta: {0}", "数量已准备：{0}"),
            new TranslationEntry("pos.status.ready", "POS ready.", "POS listo.", "POS pronto.", "POS 已就绪。"),
            new TranslationEntry("pos.status.initialized", "POS initialized.", "POS inicializado.", "POS inizializzato.", "POS 已初始化。"),
            new TranslationEntry("pos.status.added", "Added: {0} x {1}", "Agregado: {0} x {1}", "Aggiunto: {0} x {1}", "已添加：{0} x {1}"),
            new TranslationEntry("pos.status.cartEmpty", "Cart is empty.", "Carrito vacio.", "Carrello vuoto.", "购物车为空。"),
            new TranslationEntry("pos.status.paymentCancelled", "Payment cancelled.", "Pago cancelado.", "Pagamento annullato.", "付款已取消。"),
            new TranslationEntry("pos.status.paymentOk", "Payment OK: {0}", "Pago OK: {0}", "Pagamento OK: {0}", "付款成功：{0}"),
            new TranslationEntry("pos.status.paymentOkCardOnly", "Payment OK: {0} (boleta not printed: card-only payment)", "Pago OK: {0} (boleta no impresa: pago solo tarjeta)", "Pagamento OK: {0} (boleta non stampata: pagamento solo carta)", "付款成功：{0}（未打印 boleta：仅银行卡付款）"),
            new TranslationEntry("pos.status.paymentOkDrawerFailed", "Payment OK, but cash drawer did not open.", "Pago OK, pero no se pudo abrir la caja.", "Pagamento OK, ma apertura cassetto non riuscita.", "付款成功，但钱箱未打开。"),
            new TranslationEntry("pos.status.returnCompleted", "Return completed: {0}", "Devolucion completada: {0}", "Reso completato: {0}", "退货完成：{0}"),
            new TranslationEntry("pos.status.maxReturned", "Maximum quantity already returned.", "Cantidad maxima ya devuelta.", "Quantita massima gia resa.", "已达到最大退货数量。"),
            new TranslationEntry("pos.status.savings", "Savings {0}", "Ahorro {0}", "Risparmio {0}", "节省 {0}"),
            new TranslationEntry("pos.status.stock", "Stock: {0}", "Stock: {0}", "Stock: {0}", "库存：{0}"),

            new TranslationEntry("payment.printReceipt", "Print receipt", "Imprimir recibo", "Stampa scontrino", "打印小票"),
            new TranslationEntry("payment.nextPdfNumber", "Next boleta PDF: {0}", "Proxima boleta PDF: {0}", "Prossima boleta PDF: {0}", "下一张 boleta PDF：{0}"),
            new TranslationEntry("payment.autoBoletaCashOnly", "Auto-print boleta only with cash", "Impresion automatica de boleta solo con efectivo", "Stampa automatica boleta solo con contanti", "仅现金时自动打印 boleta"),
            new TranslationEntry("payment.localPdf", "Local boleta/PDF", "Boleta/PDF local", "Boleta/PDF locale", "本地 boleta/PDF"),
            new TranslationEntry("payment.pending", "Pending", "Pendiente", "In attesa", "待处理"),
            new TranslationEntry("payment.quick", "Quick", "Rapidos", "Rapidi", "快捷"),
            new TranslationEntry("payment.amountClp", "Amount (CLP)", "Monto (CLP)", "Somma (CLP)", "金额 (CLP)"),
            new TranslationEntry("payment.amount", "Amount", "Monto", "Importo", "金额"),
            new TranslationEntry("payment.round", "Round", "Redondear", "Arrotonda", "取整"),
            new TranslationEntry("payment.exactAmount", "Exact amount", "Monto exacto", "Importo esatto", "精确金额"),
            new TranslationEntry("payment.allCard", "All on card (+)", "Todo en tarjeta (+)", "Tutto su carta (+)", "全部刷卡 (+)"),
            new TranslationEntry("payment.totalDue", "Total due", "Total a pagar", "Totale da pagare", "应付总额"),
            new TranslationEntry("payment.paid", "Paid", "Pagado", "Pagato", "已付"),
            new TranslationEntry("payment.changeCashOnly", "Change (cash only): {0}", "Vuelto (solo efectivo): {0}", "Resto (solo contanti): {0}", "找零（仅现金）：{0}"),
            new TranslationEntry("payment.missing", "Missing: {0}", "Falta: {0}", "Manca: {0}", "还差：{0}"),
            new TranslationEntry("payment.cardOverBalance", "Card cannot exceed the balance due. Reduce card or move the extra to cash.", "La tarjeta no puede superar el saldo a pagar. Reduce tarjeta o mueve el excedente a efectivo.", "La carta non può superare il saldo da pagare. Riduci carta o sposta l'eccedenza su contanti.", "银行卡金额不能超过应付余额。请减少刷卡金额或把超出部分转为现金。"),
            new TranslationEntry("payment.openDrawer", "Open cash drawer", "Abrir caja", "Apri cassa", "打开钱箱"),
            new TranslationEntry("payment.cashOnly", "cash only", "solo efectivo", "solo per contanti", "仅现金"),
            new TranslationEntry("payment.suspend", "Suspend", "Suspender", "Sospendi", "挂起"),
            new TranslationEntry("payment.confirm", "Confirm payment", "Confirmar pago", "Conferma pagamento", "确认付款"),

            new TranslationEntry("discount.title", "Discount", "Descuento", "Sconto", "折扣"),
            new TranslationEntry("discount.percent", "Percentage %", "Porcentaje %", "Percentuale %", "百分比 %"),
            new TranslationEntry("discount.amount", "Amount $", "Monto $", "Importo $", "金额 $"),
            new TranslationEntry("discount.percentHelp", "0 = remove discount · Maximum percentage: 100", "0 = quitar descuento · Porcentaje maximo: 100", "0 = rimuovi sconto · Percentuale massima: 100", "0 = 移除折扣 · 最大百分比：100"),
            new TranslationEntry("discount.amountHelp", "Enter desired final unit price", "Ingresa el precio unitario final deseado", "Inserisci prezzo finale unitario desiderato", "输入期望的最终单价"),
            new TranslationEntry("discount.wholeCart", "Whole cart", "Carrito completo", "Intero carrello", "整车"),
            new TranslationEntry("discount.scopeCart", "Application: whole cart", "Aplicacion: carrito completo", "Applicazione: intero carrello", "应用：整车"),
            new TranslationEntry("discount.scopeProduct", "Application: selected product", "Aplicacion: producto seleccionado", "Applicazione: prodotto selezionato", "应用：所选商品"),
            new TranslationEntry("discount.valuePercent", "Discount (%)", "Descuento (%)", "Sconto (%)", "折扣 (%)"),
            new TranslationEntry("discount.valueAmount", "Discount ($)", "Descuento ($)", "Sconto ($)", "折扣 ($)"),

            new TranslationEntry("refund.title", "Return / Void", "Devolucion / Anulacion", "Reso / Storno", "退货 / 作废"),
            new TranslationEntry("refund.remaining", "Refundable remaining:", "Rembolsable restante:", "Rimanente rimborsabile:", "剩余可退："),
            new TranslationEntry("refund.originalTotal", "Original total:", "Total original:", "Totale originale:", "原始总计："),
            new TranslationEntry("refund.alreadyRefunded", "Already refunded:", "Ya devuelto:", "Gia rimborsato:", "已退款："),
            new TranslationEntry("refund.operationType", "Operation type", "Tipo de operacion", "Tipo operazione", "操作类型"),
            new TranslationEntry("refund.fullVoid", "Full void", "Anulacion total", "Storno totale", "全额作废"),
            new TranslationEntry("refund.fullVoidHelp", "Completely cancel the sale", "Anula completamente la venta", "Annulla completamente la vendita", "完全取消该销售"),
            new TranslationEntry("refund.partialReturn", "Partial return", "Devolucion parcial", "Reso parziale", "部分退货"),
            new TranslationEntry("refund.partialReturnHelp", "Return selected items only", "Devuelve solo algunos articulos", "Restituisci solo alcuni articoli", "只退回部分商品"),
            new TranslationEntry("refund.scanBarcode", "Scan barcode:", "Escanear codigo:", "Scansiona barcode:", "扫描条码："),
            new TranslationEntry("refund.scanTooltip", "Enter barcode and press Enter to add 1 to the return quantity", "Ingresa codigo y presiona Enter para sumar 1 a la cantidad a devolver", "Inserisci barcode e premi Invio per aggiungere 1 alla quantita da rendere", "输入条码并按 Enter，将退货数量加 1"),
            new TranslationEntry("refund.name", "Name", "Nombre", "Nome", "名称"),
            new TranslationEntry("refund.sold", "Sold", "Vendidos", "Venduti", "已售"),
            new TranslationEntry("refund.alreadyReturned", "Returned", "Ya devueltos", "Gia resi", "已退"),
            new TranslationEntry("refund.available", "Avail.", "Disp.", "Disp.", "可用"),
            new TranslationEntry("refund.unitPrice", "Unit price", "Precio unit.", "Prezzo unit.", "单价"),
            new TranslationEntry("refund.toReturn", "To return", "A devolver", "Da rendere", "待退"),
            new TranslationEntry("refund.returnTotal", "Return total", "Total devolucion", "Totale reso", "退货总计"),
            new TranslationEntry("refund.selectedRefund", "Selected refund:", "Reembolso seleccionado:", "Rimborso selezionato:", "已选退款："),
            new TranslationEntry("refund.reason", "Reason:", "Motivo:", "Motivo:", "原因："),
            new TranslationEntry("refund.allCash", "All cash", "Todo efectivo", "Tutto contanti", "全额现金"),
            new TranslationEntry("refund.allCard", "All card", "Todo tarjeta", "Tutto carta", "全额银行卡"),
            new TranslationEntry("refund.splitHalf", "Split 50/50", "Dividir 50/50", "Dividi 50/50", "50/50 拆分"),
            new TranslationEntry("refund.splitTooltip", "Half cash, half card", "Mitad efectivo, mitad tarjeta", "Meta contanti, meta carta", "一半现金，一半银行卡"),
            new TranslationEntry("refund.zero", "Zero", "Cero", "Azzera", "清零"),

            new TranslationEntry("settings.shopTitle", "Official shop data", "Datos oficiales del local", "Dati negozio ufficiali", "官方店铺数据"),
            new TranslationEntry("settings.shopReadOnly", "Read-only. Profile and fiscal identity changes are made in Master Console; Win7POS uses this cache offline too.", "Solo lectura. Los cambios de perfil e identidad fiscal se hacen en Master Console; Win7POS usa esta cache tambien offline.", "Sola lettura. Le modifiche a profilo e identita fiscale si fanno in Master Console; Win7POS usa questa cache anche offline.", "只读。资料和税务身份在 Master Console 修改；Win7POS 离线时也使用此缓存。"),
            new TranslationEntry("settings.shopName", "Shop name:", "Nombre local:", "Nome negozio:", "店铺名称："),
            new TranslationEntry("settings.shopCode", "Shop code:", "Codigo shop:", "Codice shop:", "店铺代码："),
            new TranslationEntry("settings.address", "Address:", "Direccion:", "Indirizzo:", "地址："),
            new TranslationEntry("settings.city", "City:", "Ciudad:", "Citta:", "城市："),
            new TranslationEntry("settings.companyRut", "Company RUT:", "RUT empresa:", "RUT azienda:", "公司 RUT："),
            new TranslationEntry("settings.businessGiro", "Business giro:", "Giro:", "Giro:", "经营范围："),
            new TranslationEntry("settings.phone", "Phone:", "Telefono:", "Telefono:", "电话："),
            new TranslationEntry("settings.receiptFooter", "Receipt footer:", "Footer recibo:", "Footer ricevuta:", "小票页脚："),
            new TranslationEntry("settings.lastBoleta", "Last printed boleta:", "Ultima boleta impresa:", "Ultima boleta stampata:", "最后打印的 boleta：")
        };

        private sealed class TranslationEntry
        {
            public TranslationEntry(string key, string en, string es, string it, string zhCn)
            {
                Key = key;
                En = en;
                Es = es;
                It = it;
                ZhCn = zhCn;
            }

            public string Key { get; private set; }
            public string En { get; private set; }
            public string Es { get; private set; }
            public string It { get; private set; }
            public string ZhCn { get; private set; }
        }
    }

    public sealed class SupportedLanguageOption
    {
        public SupportedLanguageOption(string code, string displayName)
        {
            Code = code;
            DisplayName = displayName;
        }

        public string Code { get; private set; }
        public string DisplayName { get; private set; }
    }
}
