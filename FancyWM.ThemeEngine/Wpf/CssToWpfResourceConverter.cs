using AngleSharp;
using AngleSharp.Css.Dom;
using AngleSharp.Css.Parser;

using FancyWM.ThemeEngine.Extensions;

namespace FancyWM.ThemeEngine.Wpf
{
    public partial class CssToWpfResourceConverter
    {
        private readonly IConfiguration m_config;

        public CssToWpfResourceConverter()
        {
            var options = new CssParserOptions
            {
                IsIncludingUnknownDeclarations = true
            };
            m_config = Configuration.Default
                .WithCss(options)
                .WithCustomDeclarationFactory();
        }

        public IReadOnlyDictionary<string, CssValue> Convert(string htmlTemplate, string cssText)
        {
            return ConvertAsync(htmlTemplate, cssText).Result;
        }

        public async Task<IReadOnlyDictionary<string, CssValue>> ConvertAsync(string htmlTemplate, string cssText)
        {
            var styledDoc = await StyledDocument.CreateAsync(m_config);
            styledDoc.Parse(htmlTemplate, cssText);
            var stylesDict = styledDoc.GetComputedStyles();
            return new CssDictionary(stylesDict);
        }
    }
}
