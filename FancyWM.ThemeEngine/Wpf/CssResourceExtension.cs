using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using System.Windows.Markup;
using System.Windows.Threading;


namespace FancyWM.ThemeEngine.Wpf
{
    [MarkupExtensionReturnType(typeof(object))]
    public class CssResourceExtension : MarkupExtension, INotifyPropertyChanged
    {
        [ConstructorArgument("path")]
        public string Path { get; set; }

        public Type? As
        {
            get => m_as;
            set
            {
                lock (m_valueLock)
                {
                    m_as = value;
                    m_previousValue = null;
                    m_previousResult = null;
                }
            }
        }

        public object? Value => GetValue();

        private Type? m_as;
        private object? m_previousValue;
        private object? m_previousResult;

        public event PropertyChangedEventHandler? PropertyChanged;

        public CssResourceExtension()
        {
        }

        public CssResourceExtension(string path)
        {
            Path = path;
        }

        public override object? ProvideValue(IServiceProvider serviceProvider)
        {
            if (Path is null)
            {
                throw new InvalidOperationException($"{nameof(CssResourceExtension)} requires a non-null Key.");
            }

            var target = serviceProvider.GetService(typeof(IProvideValueTarget)) as IProvideValueTarget;

            if (target?.TargetObject is Setter)
            {
                var binding = new Binding(nameof(Value))
                {
                    Source = this,
                    Mode = BindingMode.OneWay,
                };
                CssBindingRegistry.Register(binding, this);
                return binding.ProvideValue(serviceProvider);
            }

            if (target?.TargetObject != null &&
                target.TargetObject.GetType().FullName == "System.Windows.SharedDp")
            {
                return this;
            }

            if (target?.TargetObject is not DependencyObject obj)
            {
                throw new InvalidOperationException(
                    $"{nameof(CssResourceExtension)} can only be used on a DependencyObject (Key={Path}).");
            }

            if (DesignerProperties.GetIsInDesignMode(obj))
            {
                return DependencyProperty.UnsetValue;
            }

            if (target.TargetProperty is DependencyProperty dp)
            {
                As ??= dp.PropertyType;
                CssBindingRegistry.Register(obj, dp, this);
            }

            if (As is null)
            {
                throw new ArgumentException("`As` type cannot be deduced, so it must be provided explicitly", nameof(As));
            }

            return GetValue();
        }

        public object? GetValue()
        {
            lock (m_valueLock) return GetValueCore();
        }

        private readonly object m_valueLock = new();

        private object? GetValueCore()
        {
            var baseValue = CssManager.Resolve(Path);
            if (baseValue is null)
            {
                return DependencyProperty.UnsetValue;
            }

            if (Equals(m_previousValue, baseValue)
                && (m_previousResult is not DispatcherObject dispatcherObject || dispatcherObject.CheckAccess()))
            {
                return m_previousResult;
            }

            if (As is null)
            {
                throw new ArgumentException("`As` type cannot be deduced, so it must be provided explicitly", nameof(As));
            }

            var computedValue = baseValue.As(As);
            m_previousValue = baseValue;
            m_previousResult = computedValue;
            return computedValue;
        }

        internal void NotifyPropertyChanged()
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value)));
        }
    }
}
