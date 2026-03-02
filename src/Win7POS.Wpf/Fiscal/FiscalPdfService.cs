using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Win7POS.Core;

namespace Win7POS.Wpf.Fiscal
{
    public sealed class FiscalPdfService
    {
        public async Task<string> GenerateFiscalHtmlAsync(string fiscalText, string saleCode)
        {
            var dir = AppPaths.ExportsDirectory;
            Directory.CreateDirectory(dir);
            var safeCode = string.IsNullOrWhiteSpace(saleCode) ? "sale" : saleCode.Replace("/", "-").Replace("\\", "-");
            var fileName = $"Boleta_{safeCode}_{DateTime.Now:yyyyMMdd_HHmmss}.html";
            var path = Path.Combine(dir, fileName);

            var html = new StringBuilder();
            html.Append("<!DOCTYPE html><html><head><meta charset=\"utf-8\"/>");
            html.Append("<title>Boleta ").Append(System.Net.WebUtility.HtmlEncode(saleCode ?? "")).Append("</title>");
            html.Append("<style>body{font-family:Consolas,'Courier New',monospace;font-size:12px;margin:24px;white-space:pre-wrap;}</style>");
            html.Append("</head><body>");
            html.Append(System.Net.WebUtility.HtmlEncode(fiscalText ?? ""));
            html.Append("</body></html>");

            File.WriteAllText(path, html.ToString(), Encoding.UTF8);
            return await Task.FromResult(path).ConfigureAwait(false);
        }
    }
}
