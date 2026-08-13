using Godot;

namespace DatViewer;

/// <summary>
/// A modern dark theme with an FFXI-style blue/gold accent, built in code so the viewer stays a
/// self-contained tool (no .tres to keep in sync). Applied once to the root Control; Godot cascades
/// it to every child. Colour constants are also exposed for the BBCode we emit into RichTextLabels.
/// </summary>
public static class UiTheme
{
    // Palette — dark slate with a cool blue accent and a warm gold nod to Altana Viewer.
    public static readonly Color Bg0     = C("14161b"); // window backdrop
    public static readonly Color Bg1     = C("1b1e25"); // panels / tree background
    public static readonly Color Bg2     = C("232732"); // inputs, tab bar, headers
    public static readonly Color Bg3     = C("2b3040"); // hover
    public static readonly Color Sel     = C("34506e"); // selection (blue)
    public static readonly Color Border  = C("333a49");
    public static readonly Color Accent  = C("4aa3ff"); // primary blue
    public static readonly Color Gold    = C("e8c877"); // section headers / highlights
    public static readonly Color Text    = C("dfe3ea");
    public static readonly Color Muted   = C("8b93a3");

    private static Color C(string hex) => new("#" + hex);

    public static Theme Build()
    {
        var t = new Theme();

        // ---- shared styleboxes -------------------------------------------------------------
        StyleBoxFlat Flat(Color bg, Color? border = null, int radius = 6, int padX = 10, int padY = 6)
        {
            var s = new StyleBoxFlat { BgColor = bg };
            s.SetCornerRadiusAll(radius);
            s.SetContentMarginAll(padY);
            s.ContentMarginLeft = s.ContentMarginRight = padX;
            if (border is { } b) { s.BorderColor = b; s.SetBorderWidthAll(1); }
            return s;
        }
        StyleBoxFlat Panel(Color bg, int radius = 8) => Flat(bg, Border, radius, 6, 6);

        var btnNormal  = Flat(Bg2, Border);
        var btnHover   = Flat(Bg3, Accent);
        var btnPressed = Flat(Sel, Accent);
        var btnFocus   = Flat(Bg2, Accent);

        // ---- Button + OptionButton ---------------------------------------------------------
        foreach (var type in new[] { "Button", "OptionButton" })
        {
            t.SetStylebox("normal",  type, btnNormal);
            t.SetStylebox("hover",   type, btnHover);
            t.SetStylebox("pressed", type, btnPressed);
            t.SetStylebox("focus",   type, btnFocus);
            t.SetStylebox("disabled", type, Flat(C("1a1c22"), Border));
            t.SetColor("font_color",          type, Text);
            t.SetColor("font_hover_color",    type, C("ffffff"));
            t.SetColor("font_pressed_color",  type, C("ffffff"));
            t.SetColor("font_disabled_color", type, Muted);
            t.SetFontSize("font_size", type, 13);
        }

        // ---- LineEdit ----------------------------------------------------------------------
        t.SetStylebox("normal", "LineEdit", Flat(C("121419"), Border, 6, 10, 6));
        t.SetStylebox("focus",  "LineEdit", Flat(C("121419"), Accent, 6, 10, 6));
        t.SetColor("font_color",             "LineEdit", Text);
        t.SetColor("font_placeholder_color", "LineEdit", Muted);
        t.SetColor("caret_color",            "LineEdit", Accent);
        t.SetColor("selection_color",        "LineEdit", Sel);
        t.SetFontSize("font_size", "LineEdit", 13);

        // ---- Tree / ItemList (browsers) ----------------------------------------------------
        foreach (var type in new[] { "Tree", "ItemList" })
        {
            t.SetStylebox("panel",          type, Panel(Bg1));
            t.SetStylebox("focus",          type, Flat(Bg1, Accent, 8));
            t.SetStylebox("selected",       type, Flat(Sel, null, 4, 6, 3));
            t.SetStylebox("selected_focus", type, Flat(Sel, null, 4, 6, 3));
            t.SetStylebox("hovered",        type, Flat(Bg3, null, 4, 6, 3));
            t.SetStylebox("cursor",         type, Flat(new Color(0, 0, 0, 0)));
            t.SetStylebox("cursor_unfocused", type, Flat(new Color(0, 0, 0, 0)));
            t.SetColor("font_color",          type, Text);
            t.SetColor("font_selected_color", type, C("ffffff"));
            t.SetColor("font_hovered_color",  type, C("ffffff"));
            t.SetColor("guide_color",         type, new Color(1, 1, 1, 0.04f));
            t.SetConstant("v_separation", type, 6);
            t.SetFontSize("font_size", type, 13);
        }

        // ---- TabContainer ------------------------------------------------------------------
        t.SetStylebox("panel",            "TabContainer", Panel(Bg1));
        t.SetStylebox("tabbar_background","TabContainer", Flat(new Color(0, 0, 0, 0)));
        t.SetStylebox("tab_selected",     "TabContainer", TopTab(Bg1, Accent));
        t.SetStylebox("tab_unselected",   "TabContainer", TopTab(C("15171d"), null));
        t.SetStylebox("tab_hovered",      "TabContainer", TopTab(Bg3, null));
        t.SetColor("font_selected_color",   "TabContainer", C("ffffff"));
        t.SetColor("font_unselected_color", "TabContainer", Muted);
        t.SetColor("font_hovered_color",    "TabContainer", Text);
        t.SetFontSize("font_size", "TabContainer", 13);

        // ---- PopupMenu (OptionButton dropdowns) --------------------------------------------
        t.SetStylebox("panel",           "PopupMenu", Panel(Bg2));
        t.SetStylebox("hover",           "PopupMenu", Flat(Sel, null, 4, 8, 4));
        t.SetColor("font_color",         "PopupMenu", Text);
        t.SetColor("font_hover_color",   "PopupMenu", C("ffffff"));
        t.SetColor("font_separator_color","PopupMenu", Gold);
        t.SetFontSize("font_size", "PopupMenu", 13);

        // ---- Panels / scrolling ------------------------------------------------------------
        t.SetStylebox("panel", "PanelContainer", Panel(Bg1));
        t.SetStylebox("panel", "Panel", Panel(Bg1));
        var grabber = Flat(C("3a4152"), null, 4);
        t.SetStylebox("grabber",         "VScrollBar", grabber);
        t.SetStylebox("grabber_highlight","VScrollBar", Flat(Accent, null, 4));
        t.SetStylebox("grabber",         "HScrollBar", grabber);
        t.SetStylebox("grabber_highlight","HScrollBar", Flat(Accent, null, 4));

        // ---- Labels ------------------------------------------------------------------------
        t.SetColor("font_color", "Label", Text);
        t.SetFontSize("font_size", "Label", 13);
        t.SetColor("default_color", "RichTextLabel", Text);
        t.SetFontSize("normal_font_size", "RichTextLabel", 13);

        t.SetColor("separator", "HSeparator", Border);
        t.SetStylebox("separator", "HSeparator", Flat(Border, null, 0, 0, 1));

        return t;
    }

    // Tab stylebox: rounded top corners only, an accent underline when selected.
    private static StyleBoxFlat TopTab(Color bg, Color? accent)
    {
        var s = new StyleBoxFlat { BgColor = bg };
        s.CornerRadiusTopLeft = s.CornerRadiusTopRight = 6;
        s.ContentMarginLeft = s.ContentMarginRight = 14;
        s.ContentMarginTop = s.ContentMarginBottom = 7;
        if (accent is { } a) { s.BorderColor = a; s.BorderWidthBottom = 2; }
        return s;
    }
}
