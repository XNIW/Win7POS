using System;
using System.IO;

namespace Win7POS.Core
{
    /// <summary>Root directory per dati applicazione. Permette override per test (es. WIN7POS_DATA_DIR).</summary>
    public static class PosPaths
    {
        /// <summary>Directory root dati. Se la variabile d'ambiente WIN7POS_DATA_DIR è impostata, la usa; altrimenti usa la cartella standard (es. C:\ProgramData\Win7POS).</summary>
        /// <summary>Restituisce sempre un path assoluto, così lo stesso DB è usato a ogni avvio.</summary>
        public static string GetDataRoot()
        {
            var overridePath = Environment.GetEnvironmentVariable("WIN7POS_DATA_DIR");
            if (!string.IsNullOrWhiteSpace(overridePath))
                return Path.GetFullPath(overridePath.Trim());

            var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            if (!string.IsNullOrWhiteSpace(programData))
                return Path.GetFullPath(Path.Combine(programData, "Win7POS"));

            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrWhiteSpace(localAppData))
                return Path.GetFullPath(Path.Combine(localAppData, "Win7POS"));

            return Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Win7POS"));
        }
    }
}
