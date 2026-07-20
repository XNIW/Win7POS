namespace Win7POS.Core.Online
{
    public sealed class PosTrustedDeviceSession
    {
        public string DeviceToken { get; set; }
        public string GenerationId { get; set; }
        public string LastOkLocalAt { get; set; }
        public string LastOkServerAt { get; set; }
        public string PosSessionId { get; set; }
        public string SessionExpiresAt { get; set; }
        public string SessionToken { get; set; }
        public string ShopCode { get; set; }
        public string ShopId { get; set; }
        public string ShopName { get; set; }
        public string ShopDeviceId { get; set; }
        public string StaffCode { get; set; }
        public int StaffCredentialVersion { get; set; }
        public string StaffDisplayName { get; set; }
        public string StaffId { get; set; }
        public string StaffRoleKey { get; set; }
    }
}
