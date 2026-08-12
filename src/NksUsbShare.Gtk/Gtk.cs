using System.Runtime.InteropServices;

namespace NksUsbShare.Gtk;

/// <summary>
/// GTK 3 through plain P/Invoke. GTK is part of every Linux desktop, so using it adds nothing to
/// what the machine already has; there is no binding library in between.
/// </summary>
internal static partial class Gtk
{
    private const string Lib = "libgtk-3.so.0";
    private const string GObject = "libgobject-2.0.so.0";
    private const string GLib = "libglib-2.0.so.0";

    internal const int OrientationHorizontal = 0;
    internal const int OrientationVertical = 1;
    internal const int WindowToplevel = 0;
    internal const int AlignStart = 1;
    internal const int PolicyAutomatic = 1;

    [LibraryImport(Lib, EntryPoint = "gtk_init_check")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool InitCheck(ref int argc, ref IntPtr argv);

    [LibraryImport(Lib, EntryPoint = "gtk_main")]
    internal static partial void MainLoop();

    [LibraryImport(Lib, EntryPoint = "gtk_main_quit")]
    internal static partial void MainQuit();

    [LibraryImport(Lib, EntryPoint = "gtk_window_new")]
    internal static partial IntPtr WindowNew(int type);

    [LibraryImport(Lib, EntryPoint = "gtk_window_set_title", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial void WindowSetTitle(IntPtr window, string title);

    [LibraryImport(Lib, EntryPoint = "gtk_window_set_default_size")]
    internal static partial void WindowSetDefaultSize(IntPtr window, int width, int height);

    [LibraryImport(Lib, EntryPoint = "gtk_window_set_position")]
    internal static partial void WindowSetPosition(IntPtr window, int position);

    [LibraryImport(Lib, EntryPoint = "gtk_box_new")]
    internal static partial IntPtr BoxNew(int orientation, int spacing);

    [LibraryImport(Lib, EntryPoint = "gtk_box_pack_start")]
    internal static partial void BoxPackStart(IntPtr box, IntPtr child,
        [MarshalAs(UnmanagedType.Bool)] bool expand, [MarshalAs(UnmanagedType.Bool)] bool fill, uint padding);

    [LibraryImport(Lib, EntryPoint = "gtk_container_add")]
    internal static partial void ContainerAdd(IntPtr container, IntPtr widget);

    [LibraryImport(Lib, EntryPoint = "gtk_container_set_border_width")]
    internal static partial void ContainerSetBorderWidth(IntPtr container, uint width);

    [LibraryImport(Lib, EntryPoint = "gtk_container_get_children")]
    internal static partial IntPtr ContainerGetChildren(IntPtr container);

    [LibraryImport(Lib, EntryPoint = "gtk_container_remove")]
    internal static partial void ContainerRemove(IntPtr container, IntPtr widget);

    [LibraryImport(Lib, EntryPoint = "gtk_label_new", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial IntPtr LabelNew(string? text);

    [LibraryImport(Lib, EntryPoint = "gtk_label_set_markup", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial void LabelSetMarkup(IntPtr label, string markup);

    [LibraryImport(Lib, EntryPoint = "gtk_label_set_text", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial void LabelSetText(IntPtr label, string text);

    [LibraryImport(Lib, EntryPoint = "gtk_label_set_selectable")]
    internal static partial void LabelSetSelectable(IntPtr label, [MarshalAs(UnmanagedType.Bool)] bool selectable);

    [LibraryImport(Lib, EntryPoint = "gtk_widget_set_halign")]
    internal static partial void WidgetSetHalign(IntPtr widget, int align);

    [LibraryImport(Lib, EntryPoint = "gtk_button_new_with_label", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial IntPtr ButtonNew(string label);

    [LibraryImport(Lib, EntryPoint = "gtk_widget_show_all")]
    internal static partial void WidgetShowAll(IntPtr widget);

    [LibraryImport(Lib, EntryPoint = "gtk_widget_set_sensitive")]
    internal static partial void WidgetSetSensitive(IntPtr widget, [MarshalAs(UnmanagedType.Bool)] bool sensitive);

    [LibraryImport(Lib, EntryPoint = "gtk_scrolled_window_new")]
    internal static partial IntPtr ScrolledWindowNew(IntPtr horizontal, IntPtr vertical);

    [LibraryImport(Lib, EntryPoint = "gtk_scrolled_window_set_policy")]
    internal static partial void ScrolledWindowSetPolicy(IntPtr window, int horizontal, int vertical);

    [LibraryImport(Lib, EntryPoint = "gtk_separator_new")]
    internal static partial IntPtr SeparatorNew(int orientation);

    [LibraryImport(Lib, EntryPoint = "gtk_grid_new")]
    internal static partial IntPtr GridNew();

    [LibraryImport(Lib, EntryPoint = "gtk_grid_attach")]
    internal static partial void GridAttach(IntPtr grid, IntPtr child, int left, int top, int width, int height);

    [LibraryImport(Lib, EntryPoint = "gtk_grid_set_column_spacing")]
    internal static partial void GridSetColumnSpacing(IntPtr grid, uint spacing);

    [LibraryImport(Lib, EntryPoint = "gtk_grid_set_row_spacing")]
    internal static partial void GridSetRowSpacing(IntPtr grid, uint spacing);

    [LibraryImport(Lib, EntryPoint = "gtk_widget_set_hexpand")]
    internal static partial void WidgetSetHexpand(IntPtr widget, [MarshalAs(UnmanagedType.Bool)] bool expand);

    [LibraryImport(Lib, EntryPoint = "gtk_widget_set_margin_top")]
    internal static partial void WidgetSetMarginTop(IntPtr widget, int margin);

    [LibraryImport(Lib, EntryPoint = "gtk_widget_set_margin_bottom")]
    internal static partial void WidgetSetMarginBottom(IntPtr widget, int margin);

    [LibraryImport(Lib, EntryPoint = "gtk_label_set_xalign")]
    internal static partial void LabelSetXalign(IntPtr label, float xalign);

    [LibraryImport(Lib, EntryPoint = "gtk_label_set_ellipsize")]
    internal static partial void LabelSetEllipsize(IntPtr label, int mode);

    [LibraryImport(Lib, EntryPoint = "gtk_widget_set_tooltip_text", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial void WidgetSetTooltip(IntPtr widget, string text);

    [LibraryImport(Lib, EntryPoint = "gtk_widget_set_valign")]
    internal static partial void WidgetSetValign(IntPtr widget, int align);

    [LibraryImport(GObject, EntryPoint = "g_signal_connect_data", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial ulong SignalConnect(
        IntPtr instance, string detailedSignal, IntPtr handler, IntPtr data, IntPtr destroyData, int flags);

    [LibraryImport(GLib, EntryPoint = "g_list_free")]
    internal static partial void ListFree(IntPtr list);

    /// <summary>Walks a GList of widgets, which is what gtk_container_get_children returns.</summary>
    internal static List<IntPtr> Children(IntPtr container)
    {
        var result = new List<IntPtr>();
        var list = ContainerGetChildren(container);
        var head = list;

        while (list != IntPtr.Zero)
        {
            // struct GList { gpointer data; GList *next; GList *prev; }
            result.Add(Marshal.ReadIntPtr(list));
            list = Marshal.ReadIntPtr(list, IntPtr.Size);
        }

        if (head != IntPtr.Zero) ListFree(head);
        return result;
    }

    /// <summary>Escapes text for a Pango markup string.</summary>
    internal static string Escape(string text)
        => text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
