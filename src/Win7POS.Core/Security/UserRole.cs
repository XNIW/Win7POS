namespace Win7POS.Core.Security
{
    public sealed class UserRole
    {
        public int Id { get; set; }
        public string Code { get; set; } = "";
        public string Name { get; set; } = "";
        public bool IsSystem { get; set; }
    }
}
