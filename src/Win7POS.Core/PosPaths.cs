using System;
using System.IO;

namespace Win7POS.Core
{
    /// <summary>Root directory per dati applicazione. Permette override per test (es. WIN7POS_DATA_DIR).</summary>
    public static class PosPaths
    {
        /// <summary>Directory root dati. Se la variabile d'ambiente WIN7POS_DATA_DIR è impostata, la usa; altrimenti usa la cartella standard (es. C:\ProgramData\Win7POS).</summary>
        public static string GetDataRoot()
        {
            var overridePath = Environment.GetEnvironmentVariable("WIN7POS_DATA_DIR");
            if (!string.IsNullOrWhiteSpace(overridePath))
                return overridePath.Trim();

            var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            if (!string.IsNullOrWhiteSpace(programData))
                return Path.Combine(programData, "Win7POS");

            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrWhiteSpace(localAppData))
                return Path.Combine(localAppData, "Win7POS");

            return Path.Combine(Path.GetTempPath(), "Win7POS");
        }
    }
}
