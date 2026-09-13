using System.Windows;

using System.Windows.Threading;
using AngleSharp.Css.Dom;

using FancyWM.ThemeEngine.Wpf.Converters;

namespace FancyWM.ThemeEngine.Wpf
{
    public class CssValue
    {
        private readonly ICssValue? m_value;
        private readonly Dictionary<Type, object?> m_cache = [];

        internal CssValue(ICssValue? value)
        {
            m_value = value;
        }

        public TOut As<TOut>()
        {
            return (TOut)As(typeof(TOut))!;
        }

        public object? As(Type outType)
        {
            lock (m_cache) return ConvertCore(outType);
        }

        private object? ConvertCore(Type outType)
        {
            if (m_cache.TryGetValue(outType, out var converted)
                && (converted is not DispatcherObject dispatcherObject || dispatcherObject.CheckAccess()))
            {
                return converted;
            }
            var newConverted = ConverterRegistry.Instance.Convert(m_value, outType);
            if (newConverted is Freezable freezable)
            {
                freezable.FreezeIfPossible();
            }
            // Frozen values can be shared. An asynchronously loading image may
            // still belong to its creating Dispatcher; replace the bounded slot
            // with an owner-local conversion instead of returning that object.
            m_cache[outType] = newConverted;
            return newConverted;
        }
    }
}
