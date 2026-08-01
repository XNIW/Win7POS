using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Win7POS.Core.Images;
using Win7POS.Core.Models;
using Win7POS.Wpf.Localization;
using Win7POS.Wpf.Products;
using Win7POS.Wpf.Products.Images;

namespace Win7POS.Wpf.UiSmokeHarness
{
    internal static class ProductImageUiWpfSmoke
    {
        internal static async Task<string> RunAsync(string outputDirectory)
        {
            Directory.CreateDirectory(outputDirectory);
            var product = new ProductDetailsRow
            {
                Id = 1,
                Barcode = "QA-IMAGE-UI-1",
                Name = "Synthetic product image UI",
                UnitPrice = 100,
                StockQty = 1
            };
            var listModel = new ProductsViewModel();
            listModel.Items.Add(product);
            var listWindow = new Window
            {
                Content = new ProductsView { DataContext = listModel },
                Height = 768,
                ShowInTaskbar = false,
                Title = "Product image UI smoke",
                Width = 1024,
                WindowStartupLocation = WindowStartupLocation.CenterScreen
            };
            ProductEditDialog editor = null;
            try
            {
                listWindow.Show();
                listWindow.UpdateLayout();
                await Task.Delay(250).ConfigureAwait(true);
                var grid = Descendants<DataGrid>(listWindow).FirstOrDefault();
                var presenter = Descendants<ProductImagePresenter>(listWindow)
                    .FirstOrDefault();
                var display = presenter?.DataContext as ProductImageDisplayViewModel;
                if (grid == null || !grid.EnableRowVirtualization ||
                    !grid.EnableColumnVirtualization || grid.RowHeight != 60 ||
                    display == null ||
                    display.State != ProductImageDisplayState.NoImage)
                {
                    return "FAIL product_image_list_contract";
                }
                Capture(
                    listWindow,
                    Path.Combine(
                        outputDirectory,
                        "product-image-list-1024x768.png"));

                var editorModel = new ProductEditViewModel(
                    ProductEditMode.Edit,
                    product,
                    ProductsWorkflowService.CreateDefault());
                editor = new ProductEditDialog(editorModel)
                {
                    Owner = listWindow
                };
                editor.Show();
                editor.UpdateLayout();
                await Task.Delay(250).ConfigureAwait(true);
                editor.UpdateLayout();
                var buttons = Descendants<Button>(editor).ToArray();
                var imageCommands = new Dictionary<object, string>
                {
                    { editorModel.ChooseImageCommand, "choose" },
                    { editorModel.RemoveImageCommand, "remove" },
                    { editorModel.RetryImageCommand, "retry" }
                };
                foreach (var command in imageCommands)
                {
                    var button = buttons.FirstOrDefault(item =>
                        ReferenceEquals(item.Command, command.Key));
                    if (button == null || string.IsNullOrWhiteSpace(
                        AutomationProperties.GetName(button)))
                    {
                        return "FAIL product_image_automation_" + command.Value;
                    }
                }
                if (editor.ActualWidth > 1024 || editor.ActualHeight > 768 ||
                    editor.ActualWidth < 560 || editor.ActualHeight < 480)
                {
                    return "FAIL product_image_editor_1024x768";
                }
                Capture(
                    editor,
                    Path.Combine(
                        outputDirectory,
                        "product-image-editor-1024x768.png"));

                var keys = new[]
                {
                    "productImage.choose",
                    "productImage.replace",
                    "productImage.remove",
                    "productImage.removeConfirmation",
                    "productImage.queued",
                    "productImage.uploading",
                    "productImage.finalizing",
                    "productImage.retrying",
                    "productImage.unavailable",
                    "productImage.offline",
                    "productImage.corrupt",
                    "productImage.conflict",
                    "productImage.completed",
                    "productImage.cleanupPending"
                };
                foreach (var language in new[] { "en", "es", "it", "zh-CN" })
                {
                    foreach (var key in keys)
                    {
                        var value = PosLocalization.Current.TextForLanguage(
                            language,
                            key);
                        if (string.IsNullOrWhiteSpace(value) ||
                            string.Equals(value, key, StringComparison.Ordinal))
                        {
                            return "FAIL product_image_translation_" + language;
                        }
                    }
                }

                return "PASS list_virtualization=true row_height=60 " +
                    "list_no_image=true editor_commands=3 accessibility=true " +
                    "languages=it,en,es,zh-CN viewport=1024x768";
            }
            finally
            {
                try { editor?.Close(); } catch { }
                try { listWindow.Close(); } catch { }
            }
        }

        private static IEnumerable<T> Descendants<T>(DependencyObject root)
            where T : DependencyObject
        {
            if (root == null) yield break;
            if (root is T typed) yield return typed;
            var count = VisualTreeHelper.GetChildrenCount(root);
            for (var index = 0; index < count; index++)
            {
                foreach (var child in Descendants<T>(
                    VisualTreeHelper.GetChild(root, index)))
                {
                    yield return child;
                }
            }
        }

        private static void Capture(Window window, string path)
        {
            var width = Math.Max(1, (int)Math.Ceiling(window.ActualWidth));
            var height = Math.Max(1, (int)Math.Ceiling(window.ActualHeight));
            var bitmap = new RenderTargetBitmap(
                width,
                height,
                96,
                96,
                PixelFormats.Pbgra32);
            bitmap.Render(window);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = new FileStream(
                path,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None))
            {
                encoder.Save(stream);
            }
        }
    }
}
