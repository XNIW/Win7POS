using Microsoft.Win32;
using System;
using System.IO;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Win7POS.Core.Online;

namespace Win7POS.Wpf.Pos.Online
{
    // HKCU and DPAPI CurrentUser bind this witness to the Windows user/machine.
    // Volatile keys are not saved by RegSaveKey and disappear on hive unload.
    // Missing/corrupt witnesses are never recreated by an offline read.
    internal sealed class PosOfflineRestartWitnessStore
    {
        private const string RegistryRoot = @"Software\Win7POS\OfflineAuthorization";
        private const int MaximumBytes = 32 * 1024;
        private readonly string _scope;

        internal PosOfflineRestartWitnessStore(string scope) { _scope = scope; }

        internal void Write(PosOfflineRestartReceipt receipt)
        {
            using (var mutex = Acquire()) WriteLocked(receipt);
        }

        internal bool TryAdvance(
            string stateSha, string receiptId, string generation,
            string domain, long frequency, long timestamp,
            DateTimeOffset now, out PosOfflineRestartReceipt advanced, out string code)
        {
            advanced = null;
            code = "offline_attestation_required";
            try
            {
                using (var mutex = Acquire())
                using (var key = Registry.CurrentUser.OpenSubKey(RegistryRoot + @"\" + _scope))
                {
                    var bytes = key?.GetValue("Witness", null,
                        RegistryValueOptions.DoNotExpandEnvironmentNames) as byte[];
                    if (bytes == null || bytes.Length == 0 || bytes.Length > MaximumBytes)
                        return false;
                    var plain = ProtectedData.Unprotect(bytes, Entropy(), DataProtectionScope.CurrentUser);
                    try
                    {
                        if (plain.Length > MaximumBytes) return false;
                        PosOfflineRestartReceipt receipt;
                        using (var stream = new MemoryStream(plain))
                            receipt = (PosOfflineRestartReceipt)new DataContractJsonSerializer(
                                typeof(PosOfflineRestartReceipt)).ReadObject(stream);
                        if (!PosOfflineRestartClockPolicy.TryAdvance(receipt, _scope,
                                stateSha, receiptId, generation, domain, frequency,
                                timestamp, now, out advanced, out code)) return false;
                        WriteLocked(advanced);
                        return true;
                    }
                    finally { Array.Clear(plain, 0, plain.Length); }
                }
            }
            catch { advanced = null; return false; }
        }

        internal void Invalidate()
        {
            // A tombstone replaces the witness before the profile file is removed.
            // Restoring an old profile cannot restore this independent authority.
            using (var mutex = Acquire()) WriteLocked(null);
        }

        private void WriteLocked(PosOfflineRestartReceipt receipt)
        {
            byte[] plain;
            using (var stream = new MemoryStream())
            {
                new DataContractJsonSerializer(typeof(PosOfflineRestartReceipt)).WriteObject(stream, receipt);
                plain = stream.ToArray();
            }
            try
            {
                if (plain.Length > MaximumBytes) throw new InvalidDataException("offline_witness_size");
                var protectedValue = ProtectedData.Protect(plain, Entropy(), DataProtectionScope.CurrentUser);
                if (protectedValue.Length > MaximumBytes) throw new InvalidDataException("offline_witness_size");
                using (var parent = Registry.CurrentUser.CreateSubKey(RegistryRoot))
                using (var key = parent.CreateSubKey(_scope, RegistryKeyPermissionCheck.ReadWriteSubTree,
                           RegistryOptions.Volatile))
                {
                    key.SetValue("Witness", protectedValue, RegistryValueKind.Binary);
                }
            }
            finally { Array.Clear(plain, 0, plain.Length); }
        }

        private byte[] Entropy() => Encoding.UTF8.GetBytes("Win7POS.offline-restart.v1:" + _scope);

        private MutexLease Acquire()
        {
            var mutex = new Mutex(false, @"Local\Win7POS.OfflineAuthorization." + _scope);
            try
            {
                try
                {
                    if (!mutex.WaitOne(TimeSpan.FromSeconds(5)))
                        throw new IOException("offline_witness_busy");
                }
                catch (AbandonedMutexException)
                {
                    // An interrupted writer could have changed only one side.
                    // Leave the data untouched and require fresh online authority.
                    mutex.ReleaseMutex();
                    throw new IOException("offline_witness_interrupted");
                }
                return new MutexLease(mutex);
            }
            catch { mutex.Dispose(); throw; }
        }

        private sealed class MutexLease : IDisposable
        {
            private readonly Mutex _mutex;
            internal MutexLease(Mutex mutex) { _mutex = mutex; }
            public void Dispose() { _mutex.ReleaseMutex(); _mutex.Dispose(); }
        }
    }
}
