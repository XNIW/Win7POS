using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Win7POS.Wpf.Pos;
using Win7POS.Wpf.Pos.Dialogs;

namespace Win7POS.Wpf.UiSmokeHarness
{
    // Render only the registered, realized synthetic view region. This is never
    // a desktop/window screenshot and never accepts arbitrary text or paths.
    internal sealed class SyntheticQaVisualWriter
    {
        internal const string SummaryOracle = "Rows 1; sales 1; cancellations 0; rejected 0. Signed gross 1000 CLP; known net 1000 CLP (1/1 movements). Coverage 2000-01-02 08:00:00 → 2000-01-02 08:00:00.";
        private readonly string _dataRoot, _output;
        private readonly Envelope _envelope;
        internal SyntheticQaVisualWriter(HistoricalSalesQaContext context, string dataRoot, string output)
        {
            _dataRoot = Path.GetFullPath(dataRoot).TrimEnd(Path.DirectorySeparatorChar); _output = output;
            _envelope = new Envelope { RunId = context.RunId, RunNonce = context.RunNonce, SourceFingerprintSha256 = context.SourceFingerprintSha256,
                HarnessSha256 = context.HarnessSha256, FixtureSha256 = context.FixtureSha256, Captures = new[] {
                    New("hourly_mixed", "HIST07_REPORTS_EXPORT", "hourly_mixed_realized", "no_text"),
                    New("hourly_negative", "HIST07_REPORTS_EXPORT", "hourly_negative_realized", "no_text"),
                    New("import_summary", "HIST08_UI_LIFECYCLE", "import_summary_analyzed", "one_row_1000_v1") } };
            Flush();
        }
        private static CaptureRecord New(string id, string scenario, string point, string textRegistry) => new CaptureRecord {
            Id = id, ScenarioId = scenario, CapturePoint = point, TextRegistryId = textRegistry, Status = "NOT_RUN", Code = "unavailable" };

        internal void Capture(string id, FrameworkElement region, HistoricalSalesFoundationRunner.Fixture fixture)
        {
            var record = _envelope.Captures.Single(item => item.Id == id);
            try
            {
                var fixtureRoot = Path.GetFullPath(fixture.Root);
                if (!fixtureRoot.StartsWith(_dataRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                    fixture.ShopId != "qa-auth-shop" || fixture.ShopCode != "QA-AUTH" || fixture.Owner.FixtureToken == null ||
                    !string.Equals(fixture.Factory.DbPath, fixture.Owner.DbPath, StringComparison.OrdinalIgnoreCase) ||
                    File.ReadAllText(fixture.Owner.DbPath + ".synthetic-owner") != fixture.Owner.FixtureToken)
                    throw new InvalidOperationException("qa_visual_fixture_invalid");
                fixture.Owner.AccessContext.DemandPath(fixture.Factory.DbPath);
                if (region == null || !region.Dispatcher.CheckAccess() || !region.IsVisible || PresentationSource.FromVisual(region) == null ||
                    region.ActualWidth < 8 || region.ActualHeight < 8 || region.ActualWidth > 2048 || region.ActualHeight > 1024)
                    throw new InvalidOperationException("qa_visual_view_unavailable");
                var descendants = Descendants(region).Prepend(region).ToArray();
                if (descendants.Any(item => item is Image || item is MediaElement || item is PasswordBox || item is WebBrowser || item is TextBox))
                    throw new InvalidOperationException("qa_visual_content_withheld");
                var texts = descendants.OfType<TextBlock>().Where(item => item.IsVisible && !string.IsNullOrEmpty(item.Text)).Select(item => item.Text).ToArray();
                if (id == "import_summary")
                {
                    if (!(region is TextBlock) || !(Window.GetWindow(region) is HistoricalSalesImportDialog) || texts.Length != 1 || texts[0] != SummaryOracle)
                        throw new InvalidOperationException("qa_visual_text_withheld");
                }
                else if (region.Name != "HourlyPlot" || texts.Length != 0 || !Ancestors(region).Any(item => item is DailyReportView) ||
                    !descendants.OfType<Border>().Any(item => (item.Name == "PositiveBar" || item.Name == "NegativeBar") && item.ActualHeight > 0))
                    throw new InvalidOperationException("qa_visual_content_withheld");
                var width = (int)Math.Ceiling(region.ActualWidth); var height = (int)Math.Ceiling(region.ActualHeight);
                // Sample the validated, realized region in local coordinates. A
                // child TextBlock's layout offset can otherwise place its ink
                // outside a bitmap sized to only that child's actual bounds.
                var localVisual = new DrawingVisual();
                using (var drawing = localVisual.RenderOpen())
                {
                    // Child visuals omit their ancestor's surface. Composite
                    // against that actual opaque surface so the registered
                    // text remains readable in a standalone PNG.
                    drawing.DrawRectangle(FindOpaqueBackground(region), null, new Rect(0, 0, region.ActualWidth, region.ActualHeight));
                    drawing.DrawRectangle(new VisualBrush(region), null, new Rect(0, 0, region.ActualWidth, region.ActualHeight));
                }
                var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(localVisual);
                var pixels = new byte[width * height * 4]; bitmap.CopyPixels(pixels, width * 4, 0);
                var first = BitConverter.ToInt32(pixels, 0); var varied = false;
                for (var offset = 4; offset < pixels.Length; offset += 4) if (BitConverter.ToInt32(pixels, offset) != first) { varied = true; break; }
                if (!varied) throw new InvalidOperationException("qa_visual_blank_withheld");
                var relative = "qa-visual-" + id + ".png"; var path = Path.Combine(_output, relative);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { encoder.Save(stream); stream.Flush(true); }
                record.Status = "PASS"; record.Code = "none"; record.RelativePath = relative; record.PngSha256 = Hash(File.ReadAllBytes(path));
                record.Width = width; record.Height = height; record.TextSha256 = Hash(Encoding.UTF8.GetBytes(id == "import_summary" ? SummaryOracle : ""));
                record.FixtureRoot = fixtureRoot; record.FixtureToken = fixture.Owner.FixtureToken; record.ShopId = fixture.ShopId;
                record.NativeFixture = true;
            }
            catch (Exception error)
            {
                record.Status = "NOT_RUN"; record.Code = "unavailable";
                // Retain the primary reason privately without exporting arbitrary text.
                try { File.AppendAllText(Path.Combine(_output, "qa-visuals.private-failures.txt"), id + Environment.NewLine + error + Environment.NewLine); } catch { }
            }
            Flush();
        }
        private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
        { for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) { var child = VisualTreeHelper.GetChild(root, i); yield return child; foreach (var nested in Descendants(child)) yield return nested; } }
        private static IEnumerable<DependencyObject> Ancestors(DependencyObject item)
        { while ((item = VisualTreeHelper.GetParent(item)) != null) yield return item; }
        private static Brush FindOpaqueBackground(FrameworkElement region)
        {
            foreach (var item in new[] { (DependencyObject)region }.Concat(Ancestors(region)))
            {
                Brush brush = (item as TextBlock)?.Background ?? (item as Border)?.Background ??
                    (item as Panel)?.Background ?? (item as Control)?.Background;
                if (brush == null) continue;
                if (brush is SolidColorBrush transparent && (transparent.Color.A == 0 || transparent.Opacity == 0)) continue;
                if (brush is SolidColorBrush solid && solid.Color.A == 255 && solid.Opacity == 1) return solid;
                throw new InvalidOperationException("qa_visual_surface_withheld");
            }
            throw new InvalidOperationException("qa_visual_surface_unavailable");
        }
        private void Flush() => HistoricalSalesQaReceipt.WriteJson(Path.Combine(_output, "qa-visuals.json"), _envelope);
        private static string Hash(byte[] data) { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "").ToLowerInvariant(); }
        [DataContract] private sealed class Envelope
        {
            [DataMember(Name = "schemaVersion")] public string SchemaVersion = "win7pos-qa-visuals-v1";
            [DataMember(Name = "registryVersion")] public string RegistryVersion = "synthetic-history-visuals-v1";
            [DataMember(Name = "runId")] public string RunId;
            [DataMember(Name = "runNonce")] public string RunNonce;
            [DataMember(Name = "sourceFingerprintSha256")] public string SourceFingerprintSha256;
            [DataMember(Name = "harnessSha256")] public string HarnessSha256;
            [DataMember(Name = "fixtureSha256")] public string FixtureSha256;
            [DataMember(Name = "captures")] public CaptureRecord[] Captures;
        }
        [DataContract] private sealed class CaptureRecord
        {
            [DataMember(Name = "id")] public string Id;
            [DataMember(Name = "scenarioId")] public string ScenarioId;
            [DataMember(Name = "capturePoint")] public string CapturePoint;
            [DataMember(Name = "textRegistryId")] public string TextRegistryId;
            [DataMember(Name = "status")] public string Status;
            [DataMember(Name = "code")] public string Code;
            [DataMember(Name = "relativePath", EmitDefaultValue = false)] public string RelativePath;
            [DataMember(Name = "pngSha256", EmitDefaultValue = false)] public string PngSha256;
            [DataMember(Name = "textSha256", EmitDefaultValue = false)] public string TextSha256;
            [DataMember(Name = "width")] public int Width;
            [DataMember(Name = "height")] public int Height;
            [DataMember(Name = "nativeFixture")] public bool NativeFixture;
            [DataMember(Name = "fixtureRoot", EmitDefaultValue = false)] public string FixtureRoot;
            [DataMember(Name = "fixtureToken", EmitDefaultValue = false)] public string FixtureToken;
            [DataMember(Name = "shopId", EmitDefaultValue = false)] public string ShopId;
        }
    }
}
