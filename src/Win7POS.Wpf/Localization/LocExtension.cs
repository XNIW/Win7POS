using System;
using System.Windows.Data;
using System.Windows.Markup;

namespace Win7POS.Wpf.Localization
{
    [MarkupExtensionReturnType(typeof(object))]
    public sealed class LocExtension : MarkupExtension
    {
        public LocExtension()
        {
        }

        public LocExtension(string key)
        {
            Key = key;
        }

        public string Key { get; set; }

        public override object ProvideValue(IServiceProvider serviceProvider)
        {
            var binding = new Binding("[" + (Key ?? string.Empty) + "]")
            {
                Mode = BindingMode.OneWay,
                Source = PosLocalization.Current
            };

            return binding.ProvideValue(serviceProvider);
        }
    }
}
