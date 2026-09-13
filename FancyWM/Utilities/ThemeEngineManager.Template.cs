using System;
using System.Windows.Media;

namespace FancyWM.Utilities
{
    internal sealed partial class ThemeEngineManager
    {
        internal const string HtmlTemplate = @"
<panel>
    <panel-bar>
        <panel-bar-header>
            <panel-bar-handle></panel-bar-handle>
            <panel-bar-button></panel-bar-button>
        </panel-bar-header>
        <panel-bar-tab></panel-bar-tab>
    </panel-bar>
    <window>
        <window-actions></window-actions>
    </window>
    <window class=""preview""></window>
</panel>
<panel class=""preview""></panel>
";
        internal static string GetDefaultCss(Func<string, object> R, bool isDarkTheme, bool isRounded)
        {
            var accent = (Color)R("SystemAccentColor");
            var accentLight1 = (Color)R("SystemAccentColorLight1");
            var accentLight2 = (Color)R("SystemAccentColorLight2");
            var accentLight3 = (Color)R("SystemAccentColorLight3");
            var accentDark1 = (Color)R("SystemAccentColorDark1");
            var accentDark2 = (Color)R("SystemAccentColorDark2");
            var accentDark3 = (Color)R("SystemAccentColorDark3");

            var previewBorderColor = accentLight2;
            var previewRectangleFill = accentLight1.WithOpacity(0.1);
            var panelBarBackground = isDarkTheme ? Color.FromRgb(0x1F, 0x1F, 0x1F) : Color.FromRgb(0xCD, 0xCD, 0xCD);
            var panelBarButtonBackground = isDarkTheme ? accentLight2 : accentDark1;
            var panelBarBorder = isDarkTheme ? Color.FromArgb(0x19, 0, 0, 0) : Color.FromArgb(0x19, 255, 255, 255);
            var panelBarButtonText = isDarkTheme ? Colors.Black : Colors.White;
            var panelBarTabText = isDarkTheme ? Colors.White : Colors.Black;
            var controlBorderRadius = isRounded ? 4 : 0;
            var overlayBorderRadius = isRounded ? 8 : 0;
            var panelBarButtonBackgroundHover = accentLight1;
            var panelBarButtonBackgroundActive = accentDark1;

            var css = @$"
/* Generated file _default.css - ANY CHANGES WILL NOT BE SAVED */
window:focus, window.preview, panel.preview {{
    border-color: {previewBorderColor.ToCss()};
    border-width: 2px;
    border-radius: {overlayBorderRadius}px;
}}
window.preview, panel.preview {{
    background-color: {previewRectangleFill.ToCss()};
}}
panel-bar {{
    border-color: {panelBarBorder.ToCss()};
    border-width: 0.5px;
    border-radius: {controlBorderRadius}px; 
    background-color: {panelBarBackground.ToCss()};
    filter: drop-shadow(0px 2px 2px rgba(0,0,0,0.2));
}}
panel-bar-tab {{
    color: {panelBarTabText.ToCss()};
}}
panel-bar-handle, panel-bar-button {{
    color: {panelBarButtonText.ToCss()};
    background-color: {panelBarButtonBackground.ToCss()};
    border-radius: {controlBorderRadius}px;
}}
panel-bar-button:hover {{
    background-color: {panelBarButtonBackgroundHover.ToCss()};
}}
panel-bar-button:active {{
    background-color: {panelBarButtonBackgroundActive.ToCss()};
}}
";
            return css.Trim();
        }
    }
}
