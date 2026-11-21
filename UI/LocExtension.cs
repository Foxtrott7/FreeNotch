using System;
using System.Windows.Data;
using System.Windows.Markup;
using WindowsNotch.Services;

namespace WindowsNotch.UI
{
    /// <summary>
    /// XAML MarkupExtension für lokalisierte Strings mit automatischer Live-Umschaltung:
    /// Text="{ui:Loc settings.general.title}"
    /// </summary>
    [MarkupExtensionReturnType(typeof(object))]
    public sealed class LocExtension : MarkupExtension
    {
        public string Key { get; set; }

        public LocExtension(string key) => Key = key;

        public override object ProvideValue(IServiceProvider serviceProvider)
        {
            var binding = new Binding($"[{Key}]")
            {
                Source = LocalizationService.Instance,
                Mode = BindingMode.OneWay
            };
            return binding.ProvideValue(serviceProvider);
        }
    }
}
