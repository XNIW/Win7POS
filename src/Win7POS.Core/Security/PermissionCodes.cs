namespace Win7POS.Core.Security
{
    /// <summary>Codici permessi granulari per azione (non per schermata).</summary>
    public static class PermissionCodes
    {
        public const string PosSell = "pos.sell";
        public const string PosPay = "pos.pay";
        public const string PosSuspendCart = "pos.suspend_cart";
        public const string PosRecoverCart = "pos.recover_cart";
        public const string PosDiscount = "pos.discount";
        public const string PosDiscountOverLimit = "pos.discount_over_limit";
        public const string PosRefund = "pos.refund";
        public const string PosVoidSale = "pos.void_sale";
        public const string PosReprintReceipt = "pos.reprint_receipt";

        public const string CatalogView = "catalog.view";
        public const string CatalogEdit = "catalog.edit";
        public const string CatalogImport = "catalog.import";
        public const string CatalogPriceEdit = "catalog.price_edit";

        public const string RegisterView = "register.view";
        public const string RegisterViewAll = "register.view_all";

        public const string DailyCloseView = "daily_close.view";
        public const string DailyCloseRun = "daily_close.run";
        public const string DailyClosePrint = "daily_close.print";

        public const string SettingsShop = "settings.shop";
        public const string SettingsPrinter = "settings.printer";

        public const string DbBackup = "db.backup";
        public const string DbRestore = "db.restore";
        public const string DbMaintenance = "db.maintenance";

        public const string UsersManage = "users.manage";
        public const string RolesManage = "roles.manage";
        public const string SecurityOverride = "security.override";
    }
}
