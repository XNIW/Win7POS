using System;
namespace Win7POS.Core.Import
{
    /// <summary>Read/export capability issued by trusted operator composition, never by a manifest.</summary>
    public sealed class HistoricalCloudExportGuard
    {
        private readonly Action _demand;
        internal HistoricalCloudExportGuard(string shopId, string deviceId, string datasetId,
            long dataRevision, long restoreEpoch, Action demand)
        {
            if (!Guid.TryParse(shopId, out _) || !Guid.TryParse(deviceId, out _) ||
                string.IsNullOrWhiteSpace(datasetId) || datasetId.Length > 256 || dataRevision < 0 || restoreEpoch < 0)
                throw new ArgumentException("history_cloud_export_binding_invalid");
            ShopId = Guid.Parse(shopId).ToString("D"); DeviceId = Guid.Parse(deviceId).ToString("D"); DatasetId = datasetId;
            DataRevision = dataRevision; RestoreEpoch = restoreEpoch;
            _demand = demand ?? throw new ArgumentNullException(nameof(demand));
        }
        public string ShopId { get; }
        public string DeviceId { get; }
        public string DatasetId { get; }
        public long DataRevision { get; }
        public long RestoreEpoch { get; }
        public void DemandStillValid() => _demand();
    }
}
