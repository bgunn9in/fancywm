using System.Drawing.Printing;
using System.Text;

using AngleSharp;
using AngleSharp.Css.Dom;
using AngleSharp.Css.Parser;
using AngleSharp.Dom;

namespace FancyWM.ThemeEngine
{
    public class StyledDocument
    {
        private record StyleVariant
        {
            public required string? PseudoClassName { get; init; }

            public string? GeneratedClassName => string.IsNullOrEmpty(PseudoClassName) ? PseudoClassName : "pseudo--" + PseudoClassName;
            public string? CssSelector => string.IsNullOrEmpty(PseudoClassName) ? PseudoClassName : ":" + PseudoClassName;
            public string? CssReplacementSelector => string.IsNullOrEmpty(PseudoClassName) ? PseudoClassName : "." + GeneratedClassName!;
            public string HtmlTagName => string.IsNullOrEmpty(PseudoClassName) ? "x-root" : "x-root-" + PseudoClassName;
            public string HtmlOpenTag => $"<{HtmlTagName}>";
            public string HtmlCloseTag => $"</{HtmlTagName}>";
        }

        private static readonly StyleVariant[] s_variants =
        [
            new() { PseudoClassName = null },
            new() { PseudoClassName = "hover" },
            new() { PseudoClassName = "active" },
            new() { PseudoClassName = "focus" },
        ];

        private readonly IConfiguration m_configuration;
        private List<(StyleVariant, IDocument)> m_documents = [];

        public static async Task<StyledDocument> CreateAsync(IConfiguration configuration)
        {
            var container = new StyledDocument(configuration);
            await container.InitializeAsync();
            return container;
        }

        public void Parse(string htmlTemplate, string cssText)
        {
            // Each conversion owns its parsed rules. Variants temporarily rewrite
            // selectors and restore them before the next document uses the sheet.
            var stylesheet = new CssParser().ParseStyleSheet(cssText);
            foreach (var (variant, document) in m_documents)
            {
                List<string> variantSelectors = [];

                string transformedCssText = RewriteRules(variant, stylesheet, cssText, variantSelectors);
                document.Head!.InnerHtml = $"<style>{transformedCssText}</style>";

                string transformedHtmlTemplate = RewriteTemplate(variant, htmlTemplate);
                document.Body!.InnerHtml = transformedHtmlTemplate;

                if (variantSelectors.Count > 0)
                {
                    var combinedVariantSelector = string.Join(", ", variantSelectors);
                    var variantDependentElements = document.QuerySelectorAll(combinedVariantSelector);
                    foreach (var element in variantDependentElements)
                    {
                        element.ClassList.Add(variant.GeneratedClassName!);
                    }
                }
            }
        }

        public IReadOnlyDictionary<string, ICssStyleDeclaration> GetComputedStyles()
        {
            Dictionary<string, ICssStyleDeclaration> elementDict = [];
            foreach (var (variant, document) in m_documents)
            {
                var elements = document.QuerySelectorAll($"html > body > {variant.HtmlTagName} *");
                foreach (var element in elements.Cast<IElement>())
                {
                    var tagName = element.TagName.ToLowerInvariant();
                    var className = GetClassSelector(variant, element);
                    var key = BuildElementKey(tagName, className, variant.PseudoClassName);
                    var styles = document.DefaultView.GetComputedStyle(element);
                    elementDict.Add(key, styles);
                }
            }
            return elementDict;
        }

        private StyledDocument(IConfiguration configuration)
        {
            m_configuration = configuration;
        }

        private async Task InitializeAsync()
        {
            m_documents.Clear();
            foreach (var variant in s_variants)
            {
                string documentHtml = $"<!DOCTYPE html><html><head></head><body></body>";

                var context = BrowsingContext.New(m_configuration);
                var document = await context.OpenAsync(req => req.Content(documentHtml));

                m_documents.Add((variant, document));
            }
        }

        private static string RewriteRules(StyleVariant variant, ICssStyleSheet stylesheet, string cssText, List<string> variantSelectors)
        {
            if (string.IsNullOrEmpty(variant.CssSelector) || !cssText.Contains(variant.CssSelector))
            {
                return FlattenRules(stylesheet);
            }

            List<(ICssStyleRule Rule, string Selector)> rewritten = [];
            try
            {
                foreach (var rule in stylesheet.Rules.OfType<ICssStyleRule>())
                {
                    if (rule.SelectorText?.Contains(variant.CssSelector) != true)
                    {
                        continue;
                    }
                    var selector = rule.SelectorText;
                    rewritten.Add((rule, selector));
                    variantSelectors.Add(selector.Replace(variant.CssSelector, ""));
                    rule.SelectorText = selector.Replace(variant.CssSelector, variant.CssReplacementSelector!);
                }
                return FlattenRules(stylesheet);
            }
            finally
            {
                foreach (var (rule, selector) in rewritten)
                    rule.SelectorText = selector;
            }
        }

        private static string FlattenRules(ICssStyleSheet styleSheet)
        {
            StringBuilder sb = new();
            foreach (var anyRule in styleSheet.Rules)
            {
                if (anyRule is ICssStyleRule rule)
                {
                    if (rule.SelectorText?.Contains(',') == true)
                    {
                        foreach (var selector in rule.SelectorText.Split(','))
                        {
                            sb.Append(selector);
                            sb.Append("\n{");
                            sb.Append(rule.Style.CssText);
                            sb.Append("\n}\n");
                        }
                        continue;
                    }
                }
                sb.Append(anyRule.CssText);
            }
            return sb.ToString();
        }

        private static string RewriteTemplate(StyleVariant variant, string htmlTemplate)
        {
            StringBuilder sb = new();
            sb.Append(variant.HtmlOpenTag);
            sb.Append(htmlTemplate);
            sb.AppendLine(variant.HtmlCloseTag);
            return sb.ToString();
        }

        private static string? GetClassSelector(StyleVariant variant, IElement element)
        {
            var classAttr = element.GetAttribute("class");
            if (string.IsNullOrWhiteSpace(classAttr))
                return null;

            var classes = classAttr.Split([' '], StringSplitOptions.RemoveEmptyEntries).Except([variant.GeneratedClassName]);
            var sb = new StringBuilder();
            foreach (var cls in classes)
            {
                sb.Append('.');
                sb.Append(cls);
            }
            if (sb.Length == 0)
                return null;
            return sb.ToString();
        }

        private static string BuildElementKey(string tagName, string? classSelector, string? state)
        {
            StringBuilder sb = new();
            sb.Append(tagName);
            if (!string.IsNullOrEmpty(classSelector))
            {
                sb.Append(classSelector);
            }

            if (!string.IsNullOrEmpty(state))
            {
                sb.Append(':');
                sb.Append(state);
            }

            return sb.ToString();
        }
    }
}
