namespace Win7POS.Wpf.Infrastructure.Security
{
    public interface IPermissionService
    {
        bool Has(string permissionCode);
        void Demand(string permissionCode, string operationText);
        bool CanOverride(string permissionCode);
    }
}
