// SpikeWindow.cs
//
// The single AltiumSpike window -- replaces the seven menu items.
//
// Built ENTIRELY IN CODE, no .xaml file, on purpose: XAML needs the WPF
// SDK's markup compiler, which only runs under `UseWPF` with a
// net8.0-windows target. Deploy.ps1 builds that way, but the sandbox build
// references Altium's own WPF assemblies against plain net8.0 and has no
// XAML task. Code-only WPF compiles identically under both.
//
// Shown modeless and kept in a static field so it is not collected and so a
// second invocation re-activates the existing window instead of stacking
// duplicates.

using DXP;
using PCB;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace AltiumSpike
{
    public class SpikeWindow : Window
    {
        // ---- palette (Windows 11 dark, sitting next to Altium) ----
        private static readonly Brush Chrome      = Hex("#1F1F1F");
        private static readonly Brush Surface     = Hex("#272727");
        private static readonly Brush Card        = Hex("#2F2F2F");
        private static readonly Brush CardBorder  = Hex("#3C3C3C");
        private static readonly Brush Field       = Hex("#232323");
        private static readonly Brush FieldBorder = Hex("#454545");
        private static readonly Brush Btn         = Hex("#3A3A3A");
        private static readonly Brush BtnBorder   = Hex("#4A4A4A");
        private static readonly Brush BtnStrong   = Hex("#464646");
        private static readonly Brush Accent      = Hex("#2F6FBE");
        private static readonly Brush AccentEdge  = Hex("#3F83D6");
        private static readonly Brush TextPrimary = Hex("#ECECEC");
        private static readonly Brush TextDim     = Hex("#A8A8A8");
        private static readonly Brush TextFaint   = Hex("#8A8A8A");
        private static readonly Brush Green       = Hex("#5FD19A");
        private static readonly Brush Amber       = Hex("#E8A33D");
        private static readonly Brush Red         = Hex("#E86C6C");
        private static readonly Brush Divider     = Hex("#3A3A3A");

        private static Brush Hex(string s)
        {
            SolidColorBrush b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(s));
            b.Freeze();
            return b;
        }

        private static readonly FontFamily UiFont = new FontFamily("Segoe UI");
        private static readonly FontFamily MonoFont = new FontFamily("Consolas");

        // Wide enough for "Copper & current", the longest section name.
        private const double SidebarWidth = 152;

        // ---- showing the window ----
        //
        // Modeless (Show), so Altium stays usable with the window open.
        //
        // A note for anyone who sees the window stop responding: during remote
        // testing, synthetic clicks injected into this window stopped landing
        // after the first couple of actions, and ShowDialog did not help --
        // but a person clicking the same buttons had no trouble. The symptom
        // was almost certainly the test harness failing to give a second
        // top-level window inside Altium's process foreground activation,
        // rather than a defect here. If a REAL user ever sees buttons stop
        // firing while the window still repaints, that changes the diagnosis:
        // it would mean Altium's Delphi message loop is not pumping the WPF
        // Dispatcher for this window, and the fix is a dedicated STA thread
        // running Dispatcher.Run with every SDK call marshalled back to
        // Altium's own thread.

        private static SpikeWindow current;

        public static void ShowSingleton(IClient client)
        {
            try
            {
                if (current != null && current.IsLoaded)
                {
                    if (current.WindowState == WindowState.Minimized)
                        current.WindowState = WindowState.Normal;
                    current.Activate();
                    current.RefreshBoard();
                    Log.Write("SpikeWindow re-activated");
                    return;
                }
            }
            catch (Exception ex)
            {
                Log.Exception("SpikeWindow.ShowSingleton/reactivate", ex);
                current = null;
            }

            SpikeWindow w = new SpikeWindow(client);
            current = w;
            w.Closed += delegate { current = null; Log.Write("SpikeWindow closed"); };
            w.Show();
            w.Activate();
            Log.Write("SpikeWindow shown");
        }

        // ---- state ----
        private readonly IClient client;

        private TextBlock docName, docStats, statusText;
        private Ellipse statusDot;
        private TextBox outputFolderBox;
        private Border resultsCard;
        private StackPanel resultsBody;
        private TextBlock resultsTitle;

        private string objectsCsv, poursCsv, regionsCsv;
        private TextBlock objectsPath, poursPath, regionsPath;
        private Button objectsPlace, poursPlace, regionsPlace;

        // via fence parameters
        private TextBox fencePitch, fenceOffset, fenceDia, fenceHole, fenceNet;
        private CheckBox fenceLeft, fenceRight;

        // stackup table
        private ComboBox stackLayer;
        private TextBox stackX, stackY, stackTextH;
        private CheckBox stackImperial, stackReplace;

        // assembly notes
        private ComboBox notesLayer;
        private TextBox notesX, notesY, notesFinish, notesIpc;
        private CheckBox notesReplace;

        // release packager
        private TextBox relProject, relRev, relOutJob, relOutFolder, relDest;
        private CheckBox relGenerate;

        // schematic placement
        private TextBox spComps, spNets, spLib, spStub, spPitch, spCols;

        // via tools
        private TextBox rvNet, rvDist;
        private CheckBox rvOnlySel, rvCsv;
        private TextBox tentOpen, tentMinHole, tentMaxHole;
        private CheckBox tentOnlySel;
        private TextBox brMinHole, brRelief;
        private CheckBox brOnlySel;

        // copper & current
        private TextBox ccTemp, ccTarget;
        private CheckBox ccOnlySel;

        // cleanup
        private TextBox clTol, dcTol, dupTol, lrNet;
        private CheckBox clPours, lrVias, lrSel;

        // dfm
        private TextBox pgMin, pgCoverage, pgDiv;
        private CheckBox pgSel, sopSelect;

        // placement
        private TextBox obMargin, colClear, rotAngle, snapGrid, rnRow;
        private CheckBox obSelect, colSelect, snapSel, snapLocked, rnSel, rnTopDown;

        // polygons
        private CheckBox rpStale, rpSel;

        // connectivity
        private CheckBox ucSelect;

        // testpoints
        private TextBox tpClass, pcTol;
        private CheckBox tpPads, tpAssembly, tpUncovered;

        // silkscreen
        private TextBox skHeight, skStroke;
        private CheckBox skSelOnly, skComments;

        // geometry
        private TextBox geoRadius, geoScale;
        private CheckBox geoClamp;

        // layers
        private ComboBox lyTarget;
        private TextBox lyViaDia, lyViaHole;
        private CheckBox lyVias;

        private SpikeWindow(IClient client)
        {
            this.client = client;

            Title = "AltiumSpike";
            Width = 1060;
            Height = 760;
            MinWidth = 880;
            MinHeight = 540;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.CanResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = Surface;
            Foreground = TextPrimary;
            FontFamily = UiFont;
            BorderBrush = Hex("#0F0F0F");
            BorderThickness = new Thickness(1);

            Grid root = new Grid();
            root.RowDefinitions.Add(Row(GridLength.Auto));   // title bar
            root.RowDefinitions.Add(Row(new GridLength(1, GridUnitType.Star)));
            root.RowDefinitions.Add(Row(GridLength.Auto));   // status bar

            root.Children.Add(At(BuildTitleBar(), 0));
            root.Children.Add(At(BuildContent(), 1));
            root.Children.Add(At(BuildStatusBar(), 2));

            Content = root;

            Loaded += delegate { RefreshBoard(); };
        }

        private static RowDefinition Row(GridLength h)
        {
            RowDefinition r = new RowDefinition();
            r.Height = h;
            return r;
        }

        private static UIElement At(UIElement e, int row)
        {
            Grid.SetRow(e, row);
            return e;
        }

        // =============================================================
        // title bar
        // =============================================================
        private UIElement BuildTitleBar()
        {
            Border bar = new Border();
            bar.Background = Chrome;
            bar.Height = 36;

            DockPanel dp = new DockPanel();
            dp.LastChildFill = true;

            StackPanel buttons = new StackPanel();
            buttons.Orientation = Orientation.Horizontal;
            DockPanel.SetDock(buttons, Dock.Right);

            buttons.Children.Add(ChromeButton("", delegate { WindowState = WindowState.Minimized; }, "Minimize"));
            buttons.Children.Add(ChromeButton("", delegate
            {
                WindowState = (WindowState == WindowState.Maximized) ? WindowState.Normal : WindowState.Maximized;
            }, "Maximize"));
            buttons.Children.Add(ChromeButton("", delegate { Close(); }, "Close"));

            StackPanel left = new StackPanel();
            left.Orientation = Orientation.Horizontal;
            left.VerticalAlignment = VerticalAlignment.Center;
            left.Margin = new Thickness(14, 0, 0, 0);

            Border mark = new Border();
            mark.Width = 13; mark.Height = 13;
            mark.BorderBrush = Amber;
            mark.BorderThickness = new Thickness(2);
            mark.CornerRadius = new CornerRadius(3);
            mark.VerticalAlignment = VerticalAlignment.Center;
            left.Children.Add(mark);

            left.Children.Add(Text("AltiumSpike", 12, TextPrimary, FontWeights.SemiBold, new Thickness(9, 0, 0, 0)));
            left.Children.Add(Text("1.0", 11, TextFaint, FontWeights.Normal, new Thickness(8, 0, 0, 0)));

            dp.Children.Add(buttons);
            dp.Children.Add(left);
            bar.Child = dp;

            bar.MouseLeftButtonDown += delegate (object s, MouseButtonEventArgs e)
            {
                try
                {
                    if (e.ClickCount == 2)
                        WindowState = (WindowState == WindowState.Maximized) ? WindowState.Normal : WindowState.Maximized;
                    else
                        DragMove();
                }
                catch { /* DragMove throws if the button was already released */ }
            };

            return bar;
        }

        private Button ChromeButton(string glyph, Action onClick, string name)
        {
            Button b = new Button();
            b.Width = 46;
            b.Height = 36;
            b.Content = glyph;
            b.FontFamily = new FontFamily("Segoe MDL2 Assets");
            b.FontSize = 10;
            b.Foreground = Hex("#C4C4C4");
            b.Background = Brushes.Transparent;
            b.BorderThickness = new Thickness(0);
            b.Cursor = Cursors.Arrow;
            b.Focusable = false;
            AutomationName(b, name);
            b.Click += delegate { onClick(); };
            return b;
        }

        private static void AutomationName(DependencyObject o, string name)
        {
            System.Windows.Automation.AutomationProperties.SetName(o, name);
        }

        // =============================================================
        // content
        // =============================================================
        private UIElement BuildContent()
        {
            Grid g = new Grid();
            g.Margin = new Thickness(20, 18, 20, 18);
            g.RowDefinitions.Add(Row(GridLength.Auto));                          // board header
            g.RowDefinitions.Add(Row(GridLength.Auto));                          // results
            g.RowDefinitions.Add(Row(new GridLength(1, GridUnitType.Star)));     // sidebar + section

            g.Children.Add(At(BuildBoardHeader(), 0));
            g.Children.Add(At(BuildResultsCard(), 1));
            g.Children.Add(At(BuildNavAndBody(), 2));
            return g;
        }
        // ---------------- navigation ----------------
        //
        // A LEFT SIDEBAR, not a tab strip.
        //
        // The window started with four tabs across the top and that was fine.
        // At fourteen sections it is not: the labels alone come to roughly
        // 1900px of tab row in a 900px window, and the usual fixes make it
        // worse. A second row means the row a section sits on shifts as the
        // window resizes, so you can never learn where anything is. A
        // scrolling row hides whatever is off-screen, which is exactly wrong
        // when most sections are things you reach for occasionally.
        //
        // A vertical list scales past fourteen without any of that, keeps every
        // name fully readable, and -- the real gain -- lets sections carry
        // group headers, so the list reads as five short lists rather than one
        // long one.
        //
        // Hand-built for the same reason the tabs were: restyling a WPF
        // TreeView or ListBox for a dark window means replacing its
        // ControlTemplate, and a ControlTemplate written in C# is a page of
        // FrameworkElementFactory calls nobody can read afterwards.
        //
        // The board header and the results strip stay ACROSS THE TOP, above
        // both the sidebar and the content. Which board is open, and what the
        // last action did, are true whichever section you are in -- and
        // putting the result inside a section would hide the outcome of a
        // button the moment you navigated away from it.

        private delegate UIElement SectionBuilder();

        private sealed class Section
        {
            public string Group;    // null when it continues the previous group
            public string Name;
            public SectionBuilder Build;

            public Section(string group, string name, SectionBuilder build)
            {
                Group = group; Name = name; Build = build;
            }
        }

        private readonly List<Button> navButtons = new List<Button>();
        private readonly List<UIElement> navPanels = new List<UIElement>();
        private readonly List<Border> navMarkers = new List<Border>();
        private int activeSection = -1;

        private Section[] Sections()
        {
            return new Section[]
            {
                new Section("DATA",       "Import / Export",  BuildImportExportSection),
                new Section(null,         "Reports",          BuildReportsSection),

                new Section("HIGH-SPEED", "Via fence",        BuildViaFenceSection),
                new Section(null,         "Via tools",        BuildViaToolsSection),
                new Section(null,         "Copper & current", BuildCopperSection),

                new Section("DESIGN",     "Design rules",     BuildDesignRulesSection),
                new Section(null,         "Testpoints",       BuildTestpointsSection),
                new Section(null,         "Cleanup",          BuildCleanupSection),

                new Section("EDIT",       "Silkscreen",       BuildSilkscreenSection),
                new Section(null,         "Placement",        BuildPlacementSection),
                new Section(null,         "Geometry",         BuildGeometrySection),
                new Section(null,         "Layers",           BuildLayersSection),
                new Section(null,         "Polygons",         BuildPolygonsSection),

                new Section("OUTPUT",     "Fabrication",      BuildFabricationSection),
                new Section(null,         "Variants",         BuildVariantsSection),
                new Section(null,         "Release",          BuildReleaseSection),

                // Last, so the remembered section index of every tab above is
                // unchanged for anyone upgrading.
                new Section("SCHEMATIC",  "Place from CSV",   BuildSchPlacementSection),
            };
        }

        // A minimal Button template: a Border wrapping the content, with our
        // own hover and press colours.
        //
        // Needed because WPF's stock Button template hardcodes a pale blue
        // MouseOver fill in its own trigger, which a plain Background setter
        // cannot override -- on a dark sidebar that reads as a rendering
        // fault. Built with FrameworkElementFactory rather than
        // XamlReader.Parse so nothing parses markup at runtime inside Altium's
        // process.
        private static ControlTemplate FlatButtonTemplate(Brush hover, Brush pressed)
        {
            FrameworkElementFactory border = new FrameworkElementFactory(typeof(Border), "bd");
            border.SetValue(Border.BackgroundProperty,
                            new TemplateBindingExtension(Control.BackgroundProperty));
            border.SetValue(Border.PaddingProperty,
                            new TemplateBindingExtension(Control.PaddingProperty));

            FrameworkElementFactory content = new FrameworkElementFactory(typeof(ContentPresenter));
            content.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Left);
            content.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(content);

            ControlTemplate t = new ControlTemplate(typeof(Button));
            t.VisualTree = border;

            Trigger over = new Trigger();
            over.Property = UIElement.IsMouseOverProperty;
            over.Value = true;
            over.Setters.Add(new Setter(Border.BackgroundProperty, hover, "bd"));
            t.Triggers.Add(over);

            Trigger press = new Trigger();
            press.Property = System.Windows.Controls.Primitives.ButtonBase.IsPressedProperty;
            press.Value = true;
            press.Setters.Add(new Setter(Border.BackgroundProperty, pressed, "bd"));
            t.Triggers.Add(press);

            t.Seal();
            return t;
        }

        private static readonly ControlTemplate NavTemplate =
            FlatButtonTemplate(Hex("#333333"), Hex("#3A3A3A"));

        private UIElement BuildNavAndBody()
        {
            Grid split = new Grid();
            split.ColumnDefinitions.Add(ColFixed(SidebarWidth));
            split.ColumnDefinitions.Add(ColFixed(18));
            split.ColumnDefinitions.Add(Col());

            Section[] sections = Sections();

            // --- sidebar ---
            StackPanel list = new StackPanel();

            for (int i = 0; i < sections.Length; i++)
            {
                int index = i;      // captured per iteration, not shared

                if (sections[i].Group != null)
                {
                    TextBlock hdr = Text(sections[i].Group, 10, TextFaint, FontWeights.Bold,
                                         new Thickness(10, i == 0 ? 0 : 14, 0, 5));
                    list.Children.Add(hdr);
                }

                // A 2px accent bar down the left edge marks the active
                // section, rather than a filled row, so the sidebar stays
                // quiet next to the cards.
                Grid row = new Grid();
                row.ColumnDefinitions.Add(ColFixed(2));
                row.ColumnDefinitions.Add(Col());

                Border marker = new Border();
                marker.Background = Accent;
                marker.Visibility = Visibility.Hidden;
                Grid.SetColumn(marker, 0);
                row.Children.Add(marker);

                Button b = new Button();
                b.Content = sections[i].Name;
                b.FontSize = 12.5;
                b.FontFamily = UiFont;
                b.Foreground = TextDim;
                b.Background = Brushes.Transparent;
                b.BorderThickness = new Thickness(0);
                b.Padding = new Thickness(10, 7, 8, 7);
                b.HorizontalContentAlignment = HorizontalAlignment.Left;
                b.Cursor = Cursors.Hand;
                b.Template = NavTemplate;
                b.Click += delegate { SelectSection(index); };
                AutomationName(b, sections[i].Name + " section");
                Grid.SetColumn(b, 1);
                row.Children.Add(b);

                navButtons.Add(b);
                navMarkers.Add(marker);
                list.Children.Add(row);
            }

            ScrollViewer nav = new ScrollViewer();
            nav.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            nav.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            nav.Content = list;

            Border navWrap = new Border();
            navWrap.BorderBrush = Divider;
            navWrap.BorderThickness = new Thickness(0, 0, 1, 0);
            navWrap.Padding = new Thickness(0, 0, 10, 0);
            navWrap.Child = nav;
            Grid.SetColumn(navWrap, 0);
            split.Children.Add(navWrap);

            // --- content ---
            //
            // Every panel is built once and kept alive. Rebuilding on each
            // switch would clear whatever the user had just typed, and these
            // fields are remembered settings that should survive a look at
            // another section.
            Grid host = new Grid();
            for (int i = 0; i < sections.Length; i++)
            {
                ScrollViewer sv = new ScrollViewer();
                sv.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
                sv.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
                sv.Padding = new Thickness(0, 0, 6, 0);
                sv.Visibility = Visibility.Collapsed;

                UIElement built;
                try
                {
                    built = sections[i].Build();
                }
                catch (Exception ex)
                {
                    // One section that fails to build must not take the whole
                    // window down with it -- the rest still work, and the
                    // placeholder says which one broke.
                    Log.Exception("SpikeWindow: building section '" + sections[i].Name + "'", ex);
                    built = Wrap("This section failed to build: " + ex.GetType().Name + " — " + ex.Message,
                                 12, Red, new Thickness(0));
                }
                sv.Content = built;

                navPanels.Add(sv);
                host.Children.Add(sv);
            }
            Grid.SetColumn(host, 2);
            split.Children.Add(host);

            int remembered;
            if (!int.TryParse(Settings.GetValue("ActiveSection", "0"), out remembered)) remembered = 0;
            if (remembered < 0 || remembered >= navPanels.Count) remembered = 0;
            SelectSection(remembered);

            return split;
        }

        private void SelectSection(int index)
        {
            if (index < 0 || index >= navPanels.Count) return;
            if (index == activeSection) return;

            for (int i = 0; i < navPanels.Count; i++)
            {
                bool on = (i == index);
                navPanels[i].Visibility = on ? Visibility.Visible : Visibility.Collapsed;
                navButtons[i].Foreground = on ? TextPrimary : TextDim;
                navButtons[i].FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
                navMarkers[i].Visibility = on ? Visibility.Visible : Visibility.Hidden;
            }

            activeSection = index;
            Settings.SetValue("ActiveSection", index.ToString(CultureInfo.InvariantCulture));
        }

        // A section is just a stack of cards. Which cards go where is decided
        // here and nowhere else, so moving one between sections is a one-line
        // change.
        private static StackPanel Stack(params UIElement[] children)
        {
            StackPanel sp = new StackPanel();
            for (int i = 0; i < children.Length; i++)
            {
                if (children[i] == null) continue;
                FrameworkElement fe = children[i] as FrameworkElement;
                if (fe != null && i > 0) fe.Margin = new Thickness(0, 12, 0, 0);
                sp.Children.Add(children[i]);
            }
            return sp;
        }

        // Two cards side by side, for sections whose tools pair naturally.
        private UIElement Pair(UIElement left, UIElement right)
        {
            Grid g = new Grid();
            g.ColumnDefinitions.Add(Col());
            g.ColumnDefinitions.Add(ColFixed(16));
            g.ColumnDefinitions.Add(Col());

            Grid.SetColumn(left, 0);
            g.Children.Add(left);

            if (right != null)
            {
                Grid.SetColumn(right, 2);
                g.Children.Add(right);
            }
            return g;
        }

        // ---------------- sections ----------------

        private UIElement BuildImportExportSection()
        {
            StackPanel sp = new StackPanel();

            FrameworkElement cols = BuildColumns() as FrameworkElement;
            if (cols != null) cols.MinHeight = 340;   // keeps the two columns readable
            sp.Children.Add(cols);

            // Net lengths sits here rather than with the via tools: it writes
            // CSVs and changes nothing on the board, which is what everything
            // else in this section does.
            Grid row = new Grid();
            row.Margin = new Thickness(0, 15, 0, 0);
            row.ColumnDefinitions.Add(ColFixed(250));
            row.ColumnDefinitions.Add(Col());

            UIElement lengths = BuildNetLengthsCard();
            Grid.SetColumn(lengths, 0);
            row.Children.Add(lengths);

            sp.Children.Add(row);
            return sp;
        }

        private UIElement BuildViaFenceSection()
        {
            return Stack(BuildViaFenceCard());
        }

        private UIElement BuildFabricationSection()
        {
            return Stack(Pair(BuildStackupCard(), BuildNotesCard()), BuildPasteGridCard());
        }

        private UIElement BuildPasteGridCard()
        {
            UIElement f0 = LabeledField("Min pad mm", Settings.GetValue("PgMin", "3.000"),
                                        "Only pads at least this large in both axes", out pgMin);
            UIElement f1 = LabeledField("Coverage %", Settings.GetValue("PgCoverage", "60"),
                                        "Paste area as a share of the pad area", out pgCoverage);
            UIElement f2 = LabeledField("Divisions", Settings.GetValue("PgDiv", "3"),
                                        "Grid is N x N apertures", out pgDiv);

            pgSel = SideCheck("Selection only", Settings.GetValue("PgSel", "0") != "0");

            return ToolCard("Paste grid", "MODIFIES BOARD", Hex("#3D3527"), Hex("#5F5130"), Hex("#F0C477"),
                "Breaks the paste aperture of large pads — thermal tabs, shield grounds, QFN centre pads — " +
                "into a window-pane grid. One solid aperture that size puts down more paste than the joint " +
                "can absorb and the part floats off its pads. The pad's own paste expansion is set hard " +
                "negative and flagged manual first, or the rule puts the solid aperture straight back.",
                FieldRow(f0, f1, f2),
                ActionRow(Go("Build paste grid", DoPasteGrid), 168, pgSel));
        }

        private UIElement BuildReleaseSection()
        {
            UIElement card = BuildReleaseCard();
            FrameworkElement fe = card as FrameworkElement;
            if (fe != null) fe.Margin = new Thickness(0);
            return Stack(card);
        }



        private UIElement BuildBoardHeader()
        {
            Border card = CardBorderEl(new Thickness(16, 13, 16, 13));
            card.Margin = new Thickness(0, 0, 0, 15);

            DockPanel dp = new DockPanel();
            dp.LastChildFill = true;

            Button refresh = SecondaryButton("Refresh", 30, 12);
            refresh.Click += delegate { RefreshBoard(); };
            DockPanel.SetDock(refresh, Dock.Right);
            dp.Children.Add(refresh);

            Border icon = new Border();
            icon.Width = 38; icon.Height = 38;
            icon.CornerRadius = new CornerRadius(6);
            icon.Background = Hex("#22382C");
            icon.Margin = new Thickness(0, 0, 14, 0);
            icon.VerticalAlignment = VerticalAlignment.Center;
            Border inner = new Border();
            inner.Width = 16; inner.Height = 16;
            inner.BorderBrush = Green;
            inner.BorderThickness = new Thickness(2);
            inner.CornerRadius = new CornerRadius(2);
            icon.Child = inner;
            DockPanel.SetDock(icon, Dock.Left);
            dp.Children.Add(icon);

            StackPanel sp = new StackPanel();
            sp.VerticalAlignment = VerticalAlignment.Center;
            docName = Text("No PCB document", 14, TextPrimary, FontWeights.SemiBold, new Thickness(0));
            docName.TextTrimming = TextTrimming.CharacterEllipsis;
            docStats = Text("Open a .PcbDoc and press Refresh", 12, TextDim, FontWeights.Normal, new Thickness(0, 3, 0, 0));
            sp.Children.Add(docName);
            sp.Children.Add(docStats);
            dp.Children.Add(sp);

            card.Child = dp;
            return card;
        }

        private UIElement BuildResultsCard()
        {
            resultsCard = CardBorderEl(new Thickness(15, 12, 15, 12));
            resultsCard.Margin = new Thickness(0, 0, 0, 15);
            resultsCard.Visibility = Visibility.Collapsed;

            StackPanel sp = new StackPanel();

            DockPanel head = new DockPanel();
            head.LastChildFill = true;
            Button dismiss = new Button();
            dismiss.Content = "Dismiss";
            dismiss.FontSize = 11;
            dismiss.Foreground = TextDim;
            dismiss.Background = Brushes.Transparent;
            dismiss.BorderThickness = new Thickness(0);
            dismiss.Cursor = Cursors.Hand;
            dismiss.Click += delegate { resultsCard.Visibility = Visibility.Collapsed; };
            DockPanel.SetDock(dismiss, Dock.Right);
            head.Children.Add(dismiss);

            resultsTitle = Text("", 12, TextPrimary, FontWeights.Bold, new Thickness(0));
            head.Children.Add(resultsTitle);
            sp.Children.Add(head);

            // A long exclusion list must not push the columns off the bottom of
            // the window -- it did on the first run, clipping the output folder
            // row. Cap the height and let this one area scroll.
            resultsBody = new StackPanel();
            ScrollViewer sv = new ScrollViewer();
            sv.MaxHeight = 110;
            sv.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            sv.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            sv.Margin = new Thickness(0, 8, 0, 0);
            sv.Content = resultsBody;
            sp.Children.Add(sv);

            resultsCard.Child = sp;
            return resultsCard;
        }

        private UIElement BuildColumns()
        {
            Grid g = new Grid();
            g.ColumnDefinitions.Add(Col());
            g.ColumnDefinitions.Add(ColFixed(16));
            g.ColumnDefinitions.Add(Col());

            UIElement left = BuildExportColumn();
            Grid.SetColumn(left, 0);
            g.Children.Add(left);

            UIElement right = BuildImportColumn();
            Grid.SetColumn(right, 2);
            g.Children.Add(right);
            return g;
        }

        private static ColumnDefinition Col()
        {
            ColumnDefinition c = new ColumnDefinition();
            c.Width = new GridLength(1, GridUnitType.Star);
            return c;
        }

        private static ColumnDefinition ColFixed(double w)
        {
            ColumnDefinition c = new ColumnDefinition();
            c.Width = new GridLength(w);
            return c;
        }

        // ---------------- cards ----------------
        //
        // Each card is a self-contained module. Which tab a card appears on is
        // decided by the BuildXxxTab methods above, so moving one between tabs
        // is a one-line change here.

        private UIElement BuildViaFenceCard()
        {
            Border card = CardBorderEl(new Thickness(15, 13, 15, 13));
            StackPanel sp = new StackPanel();

            StackPanel titleRow = new StackPanel();
            titleRow.Orientation = Orientation.Horizontal;
            titleRow.Children.Add(Text("Via fence", 13, TextPrimary, FontWeights.SemiBold, new Thickness(0)));

            Border tag = new Border();
            tag.Background = Hex("#2A3340");
            tag.BorderBrush = Hex("#3C4A5C");
            tag.BorderThickness = new Thickness(1);
            tag.CornerRadius = new CornerRadius(3);
            tag.Padding = new Thickness(6, 2, 6, 2);
            tag.Margin = new Thickness(8, 0, 0, 0);
            tag.VerticalAlignment = VerticalAlignment.Center;
            tag.Child = Text("USES SELECTION", 10, Hex("#8FB4DC"), FontWeights.Bold, new Thickness(0));
            titleRow.Children.Add(tag);
            sp.Children.Add(titleRow);

            sp.Children.Add(Wrap("Select trace segments in the PCB editor first. No clearance check is performed — run Design › Rule Check afterwards.",
                                 12, TextDim, new Thickness(0, 6, 0, 0)));

            // five parameters across one row
            Grid fields = new Grid();
            fields.Margin = new Thickness(0, 11, 0, 0);
            for (int i = 0; i < 9; i++)
                fields.ColumnDefinitions.Add(i % 2 == 0 ? Col() : ColFixed(8));

            UIElement f0 = LabeledField("Pitch mm", Settings.GetValue("FencePitch", "1.000"), "Fence pitch in millimetres", out fencePitch);
            UIElement f1 = LabeledField("Offset mm", Settings.GetValue("FenceOffset", "0.500"), "Fence offset from trace centreline in millimetres", out fenceOffset);
            UIElement f2 = LabeledField("Via Ø mm", Settings.GetValue("FenceDia", "0.600"), "Via pad diameter in millimetres", out fenceDia);
            UIElement f3 = LabeledField("Hole mm", Settings.GetValue("FenceHole", "0.300"), "Via hole size in millimetres", out fenceHole);
            UIElement f4 = LabeledField("Net", Settings.GetValue("FenceNet", "GND"), "Net to tie the fence vias to", out fenceNet);

            Grid.SetColumn(f0, 0); fields.Children.Add(f0);
            Grid.SetColumn(f1, 2); fields.Children.Add(f1);
            Grid.SetColumn(f2, 4); fields.Children.Add(f2);
            Grid.SetColumn(f3, 6); fields.Children.Add(f3);
            Grid.SetColumn(f4, 8); fields.Children.Add(f4);
            sp.Children.Add(fields);

            // sides on the left, action on the right
            DockPanel actions = new DockPanel();
            actions.LastChildFill = false;
            actions.Margin = new Thickness(0, 11, 0, 0);

            Button go = PrimaryButton("Fence selected traces", 32);
            go.MinWidth = 168;
            go.Click += delegate { DoViaFence(); };
            DockPanel.SetDock(go, Dock.Right);
            actions.Children.Add(go);

            StackPanel sides = new StackPanel();
            sides.Orientation = Orientation.Horizontal;
            sides.VerticalAlignment = VerticalAlignment.Center;
            fenceLeft = SideCheck("Left", Settings.GetValue("FenceLeft", "1") != "0");
            fenceRight = SideCheck("Right", Settings.GetValue("FenceRight", "1") != "0");
            fenceRight.Margin = new Thickness(14, 0, 0, 0);
            sides.Children.Add(fenceLeft);
            sides.Children.Add(fenceRight);
            DockPanel.SetDock(sides, Dock.Left);
            actions.Children.Add(sides);

            sp.Children.Add(actions);
            card.Child = sp;
            return card;
        }

        private UIElement BuildNetLengthsCard()
        {
            Border card = CardBorderEl(new Thickness(15, 13, 15, 13));
            StackPanel sp = new StackPanel();

            sp.Children.Add(Text("Net lengths", 13, TextPrimary, FontWeights.SemiBold, new Thickness(0)));
            sp.Children.Add(Wrap("Routed length and delay per net and per pin pair, straight from Altium's own calculator.",
                                 12, TextDim, new Thickness(0, 6, 0, 0)));

            Button go = PrimaryButton("Export net lengths", 32);
            go.Margin = new Thickness(0, 11, 0, 0);
            go.Click += delegate { DoExportNetLengths(); };
            sp.Children.Add(go);

            card.Child = sp;
            return card;
        }

        private UIElement LabeledField(string label, string initial, string automation, out TextBox box)
        {
            StackPanel sp = new StackPanel();
            sp.Children.Add(Text(label, 11, TextDim, FontWeights.Normal, new Thickness(0, 0, 0, 5)));

            box = new TextBox();
            box.Text = initial;
            box.Height = 30;
            box.FontFamily = MonoFont;
            box.FontSize = 12;
            box.Foreground = Hex("#DCDCDC");
            box.Background = Field;
            box.BorderBrush = FieldBorder;
            box.BorderThickness = new Thickness(1);
            box.Padding = new Thickness(7, 0, 7, 0);
            box.VerticalContentAlignment = VerticalAlignment.Center;
            box.CaretBrush = TextPrimary;
            AutomationName(box, automation);

            sp.Children.Add(box);
            return sp;
        }

        private CheckBox SideCheck(string label, bool initial)
        {
            CheckBox cb = new CheckBox();
            cb.Content = label;
            cb.IsChecked = initial;
            cb.Foreground = TextDim;
            cb.FontFamily = UiFont;
            cb.FontSize = 12;
            cb.VerticalContentAlignment = VerticalAlignment.Center;
            cb.Cursor = Cursors.Hand;
            AutomationName(cb, "Fence the " + label.ToLowerInvariant() + " side");
            return cb;
        }

        // Both of these end in an artefact a fabricator reads, drawn onto the
        // board itself, which is why they share the Fabrication tab. The
        // release archive gets its own tab instead: it is the only thing in
        // the window that writes outside Altium, and it deserves the room for
        // its four paths.
        private UIElement BuildStackupCard()
        {
            Border card = CardBorderEl(new Thickness(15, 13, 15, 13));
            StackPanel sp = new StackPanel();

            sp.Children.Add(Text("Layer stackup table", 13, TextPrimary, FontWeights.SemiBold, new Thickness(0)));
            sp.Children.Add(Wrap("Draws the physical stack — material, thickness, copper weight and Er — as a ruled table.",
                                 12, TextDim, new Thickness(0, 6, 0, 0)));

            Grid f = new Grid();
            f.Margin = new Thickness(0, 11, 0, 0);
            for (int i = 0; i < 7; i++) f.ColumnDefinitions.Add(i % 2 == 0 ? Col() : ColFixed(8));

            UIElement c0 = LabeledCombo("Layer", PcbDraw.DrawableLayers,
                                        Settings.GetValue("StackLayer", "Drill Drawing"),
                                        "Layer to draw the stackup table on", out stackLayer);
            UIElement c1 = LabeledField("Origin X", Settings.GetValue("StackX", "10.0"),
                                        "Stackup table origin X in millimetres", out stackX);
            UIElement c2 = LabeledField("Origin Y", Settings.GetValue("StackY", "10.0"),
                                        "Stackup table origin Y in millimetres", out stackY);
            UIElement c3 = LabeledField("Text mm", Settings.GetValue("StackTextH", "1.2"),
                                        "Stackup table text height in millimetres", out stackTextH);

            Grid.SetColumn(c0, 0); f.Children.Add(c0);
            Grid.SetColumn(c1, 2); f.Children.Add(c1);
            Grid.SetColumn(c2, 4); f.Children.Add(c2);
            Grid.SetColumn(c3, 6); f.Children.Add(c3);
            sp.Children.Add(f);

            DockPanel actions = new DockPanel();
            actions.LastChildFill = false;
            actions.Margin = new Thickness(0, 11, 0, 0);

            Button go = PrimaryButton("Generate stackup table", 32);
            go.MinWidth = 170;
            go.Click += delegate { DoStackupTable(); };
            DockPanel.SetDock(go, Dock.Right);
            actions.Children.Add(go);

            StackPanel opts = new StackPanel();
            opts.Orientation = Orientation.Horizontal;
            opts.VerticalAlignment = VerticalAlignment.Center;
            stackImperial = SideCheck("mil too", Settings.GetValue("StackImperial", "1") != "0");
            stackReplace = SideCheck("Replace", Settings.GetValue("StackReplace", "1") != "0");
            stackReplace.Margin = new Thickness(14, 0, 0, 0);
            opts.Children.Add(stackImperial);
            opts.Children.Add(stackReplace);
            DockPanel.SetDock(opts, Dock.Left);
            actions.Children.Add(opts);

            sp.Children.Add(actions);
            card.Child = sp;
            return card;
        }

        private UIElement BuildNotesCard()
        {
            Border card = CardBorderEl(new Thickness(15, 13, 15, 13));
            StackPanel sp = new StackPanel();

            sp.Children.Add(Text("Assembly notes", 13, TextPrimary, FontWeights.SemiBold, new Thickness(0)));
            sp.Children.Add(Wrap("Board size, layer count, measured minimum track and hole, rule clearance — as numbered fab notes.",
                                 12, TextDim, new Thickness(0, 6, 0, 0)));

            Grid f = new Grid();
            f.Margin = new Thickness(0, 11, 0, 0);
            for (int i = 0; i < 7; i++) f.ColumnDefinitions.Add(i % 2 == 0 ? Col() : ColFixed(8));

            UIElement c0 = LabeledCombo("Layer", PcbDraw.DrawableLayers,
                                        Settings.GetValue("NotesLayer", "Mechanical 1"),
                                        "Layer to write the fabrication notes on", out notesLayer);
            UIElement c1 = LabeledField("Origin X", Settings.GetValue("NotesX", "10.0"),
                                        "Notes origin X in millimetres", out notesX);
            UIElement c2 = LabeledField("Origin Y", Settings.GetValue("NotesY", "10.0"),
                                        "Notes origin Y in millimetres", out notesY);
            UIElement c3 = LabeledField("Finish", Settings.GetValue("NotesFinish", "ENIG"),
                                        "Surface finish named in the notes", out notesFinish);

            Grid.SetColumn(c0, 0); f.Children.Add(c0);
            Grid.SetColumn(c1, 2); f.Children.Add(c1);
            Grid.SetColumn(c2, 4); f.Children.Add(c2);
            Grid.SetColumn(c3, 6); f.Children.Add(c3);
            sp.Children.Add(f);

            DockPanel actions = new DockPanel();
            actions.LastChildFill = false;
            actions.Margin = new Thickness(0, 11, 0, 0);

            Button go = PrimaryButton("Generate notes", 32);
            go.MinWidth = 170;
            go.Click += delegate { DoAssemblyNotes(); };
            DockPanel.SetDock(go, Dock.Right);
            actions.Children.Add(go);

            StackPanel opts = new StackPanel();
            opts.Orientation = Orientation.Horizontal;
            opts.VerticalAlignment = VerticalAlignment.Center;
            opts.Children.Add(Text("IPC class", 12, TextDim, FontWeights.Normal, new Thickness(0, 0, 7, 0)));

            notesIpc = new TextBox();
            notesIpc.Text = Settings.GetValue("NotesIpc", "2");
            notesIpc.Width = 36;
            notesIpc.Height = 30;
            notesIpc.FontFamily = MonoFont;
            notesIpc.FontSize = 12;
            notesIpc.Foreground = Hex("#DCDCDC");
            notesIpc.Background = Field;
            notesIpc.BorderBrush = FieldBorder;
            notesIpc.BorderThickness = new Thickness(1);
            notesIpc.VerticalContentAlignment = VerticalAlignment.Center;
            notesIpc.HorizontalContentAlignment = HorizontalAlignment.Center;
            notesIpc.CaretBrush = TextPrimary;
            AutomationName(notesIpc, "IPC class");
            opts.Children.Add(notesIpc);

            notesReplace = SideCheck("Replace", Settings.GetValue("NotesReplace", "1") != "0");
            notesReplace.Margin = new Thickness(14, 0, 0, 0);
            opts.Children.Add(notesReplace);

            DockPanel.SetDock(opts, Dock.Left);
            actions.Children.Add(opts);

            sp.Children.Add(actions);
            card.Child = sp;
            return card;
        }

        private UIElement BuildReleaseCard()
        {
            Border card = CardBorderEl(new Thickness(15, 13, 15, 13));
            card.Margin = new Thickness(0, 10, 0, 0);
            StackPanel sp = new StackPanel();

            StackPanel titleRow = new StackPanel();
            titleRow.Orientation = Orientation.Horizontal;
            titleRow.Children.Add(Text("Release candidate package", 13, TextPrimary, FontWeights.SemiBold, new Thickness(0)));

            Border tag = new Border();
            tag.Background = Hex("#3D3527");
            tag.BorderBrush = Hex("#5F5130");
            tag.BorderThickness = new Thickness(1);
            tag.CornerRadius = new CornerRadius(3);
            tag.Padding = new Thickness(6, 2, 6, 2);
            tag.Margin = new Thickness(8, 0, 0, 0);
            tag.VerticalAlignment = VerticalAlignment.Center;
            tag.Child = Text("WRITES TO DISK", 10, Hex("#F0C477"), FontWeights.Bold, new Thickness(0));
            titleRow.Children.Add(tag);
            sp.Children.Add(titleRow);

            sp.Children.Add(Wrap("Collects the Output Job's files, renames to Project_Rev_YYYYMMDD, zips, verifies the archive reopens, and delivers it. Never overwrites an existing release.",
                                 12, TextDim, new Thickness(0, 6, 0, 0)));

            // project / revision
            Grid ids = new Grid();
            ids.Margin = new Thickness(0, 11, 0, 0);
            ids.ColumnDefinitions.Add(Col());
            ids.ColumnDefinitions.Add(ColFixed(8));
            ids.ColumnDefinitions.Add(ColFixed(150));

            UIElement p0 = LabeledField("Project name", Settings.GetValue("RelProject", ""),
                                        "Project name used in the archive file name", out relProject);
            UIElement p1 = LabeledField("Revision", Settings.GetValue("RelRev", "RevA"),
                                        "Revision used in the archive file name", out relRev);
            Grid.SetColumn(p0, 0); ids.Children.Add(p0);
            Grid.SetColumn(p1, 2); ids.Children.Add(p1);
            sp.Children.Add(ids);

            sp.Children.Add(PathRow("Output Job", Settings.GetValue("RelOutJob", ""),
                                    "Path to the .OutJob to run", true, out relOutJob));
            sp.Children.Add(PathRow("Outputs folder", Settings.GetValue("RelOutFolder", ""),
                                    "Folder the Output Job writes into", false, out relOutFolder));
            sp.Children.Add(PathRow("Deliver to", Settings.GetValue("RelDest", ""),
                                    "Destination folder or network share for the release archive", false, out relDest));

            DockPanel actions = new DockPanel();
            actions.LastChildFill = false;
            actions.Margin = new Thickness(0, 12, 0, 0);

            Button go = PrimaryButton("Package release", 32);
            go.MinWidth = 150;
            go.Click += delegate { DoReleasePackage(false); };
            DockPanel.SetDock(go, Dock.Right);
            actions.Children.Add(go);

            Button dry = SecondaryButton("Dry run", 32, 12);
            dry.MinWidth = 90;
            dry.Margin = new Thickness(0, 0, 8, 0);
            dry.Click += delegate { DoReleasePackage(true); };
            DockPanel.SetDock(dry, Dock.Right);
            actions.Children.Add(dry);

            StackPanel opts = new StackPanel();
            opts.Orientation = Orientation.Horizontal;
            opts.VerticalAlignment = VerticalAlignment.Center;

            // Defaults to OFF. The launch is the one call in this project that
            // could not be confirmed from SDK metadata, so it is opt-in until
            // it has been seen to work here.
            relGenerate = SideCheck("Run the Output Job first", Settings.GetValue("RelGenerate", "0") != "0");
            opts.Children.Add(relGenerate);
            DockPanel.SetDock(opts, Dock.Left);
            actions.Children.Add(opts);

            sp.Children.Add(actions);
            card.Child = sp;
            return card;
        }

        // label + path box + Browse, as one row
        private UIElement PathRow(string label, string initial, string automation,
                                  bool pickFile, out TextBox box)
        {
            return PathRow(label, initial, automation,
                           pickFile ? "Choose the Output Job" : null,
                           "Output Job (*.OutJob)|*.OutJob|All files (*.*)|*.*", out box);
        }

        // pickTitle null -> Browse picks a folder; otherwise a file matching pickFilter.
        private UIElement PathRow(string label, string initial, string automation,
                                  string pickTitle, string pickFilter, out TextBox box)
        {
            StackPanel sp = new StackPanel();
            sp.Margin = new Thickness(0, 9, 0, 0);
            sp.Children.Add(Text(label, 11, TextDim, FontWeights.Normal, new Thickness(0, 0, 0, 5)));

            DockPanel row = new DockPanel();
            row.LastChildFill = true;

            TextBox tb = new TextBox();
            tb.Text = initial;
            tb.Height = 30;
            tb.FontFamily = MonoFont;
            tb.FontSize = 12;
            tb.Foreground = Hex("#DCDCDC");
            tb.Background = Field;
            tb.BorderBrush = FieldBorder;
            tb.BorderThickness = new Thickness(1);
            tb.Padding = new Thickness(7, 0, 7, 0);
            tb.VerticalContentAlignment = VerticalAlignment.Center;
            tb.CaretBrush = TextPrimary;
            AutomationName(tb, automation);

            TextBox captured = tb;
            string title = pickTitle, filter = pickFilter;
            Button browse = SecondaryButton("Browse", 30, 12);
            browse.Margin = new Thickness(7, 0, 0, 0);
            browse.Click += delegate
            {
                string chosen = title != null
                    ? Settings.PickFile(title, filter)
                    : Settings.PickOutputFolder("Choose a folder");
                if (chosen != null) captured.Text = chosen;
            };
            DockPanel.SetDock(browse, Dock.Right);
            row.Children.Add(browse);
            row.Children.Add(tb);

            sp.Children.Add(row);
            box = tb;
            return sp;
        }

        private UIElement LabeledCombo(string label, string[] items, string selected,
                                       string automation, out ComboBox combo)
        {
            StackPanel sp = new StackPanel();
            sp.Children.Add(Text(label, 11, TextDim, FontWeights.Normal, new Thickness(0, 0, 0, 5)));

            ComboBox cb = new ComboBox();
            cb.Height = 30;
            cb.FontFamily = MonoFont;
            cb.FontSize = 12;
            cb.Foreground = Hex("#DCDCDC");
            cb.Background = Field;
            cb.BorderBrush = FieldBorder;
            cb.BorderThickness = new Thickness(1);
            cb.VerticalContentAlignment = VerticalAlignment.Center;

            int pick = 0;
            for (int i = 0; i < items.Length; i++)
            {
                cb.Items.Add(items[i]);
                if (string.Equals(items[i], selected, StringComparison.OrdinalIgnoreCase)) pick = i;
            }
            cb.SelectedIndex = pick;
            AutomationName(cb, automation);

            sp.Children.Add(cb);
            combo = cb;
            return sp;
        }

        // ---------------- card scaffolding ----------------
        //
        // Every tool card is the same shape: a title, a sentence saying what
        // it does and what it will not do, a row of parameters, and an action
        // row with options on the left and the button on the right. Building
        // that once means a new tool is its fields and its handler, not
        // another forty lines of layout.

        private Border ToolCard(string title, string tag, Brush tagFill, Brush tagEdge, Brush tagText,
                                string description, UIElement fields, UIElement actions)
        {
            Border card = CardBorderEl(new Thickness(15, 13, 15, 13));
            StackPanel sp = new StackPanel();

            if (tag == null)
            {
                sp.Children.Add(Text(title, 13, TextPrimary, FontWeights.SemiBold, new Thickness(0)));
            }
            else
            {
                StackPanel row = new StackPanel();
                row.Orientation = Orientation.Horizontal;
                row.Children.Add(Text(title, 13, TextPrimary, FontWeights.SemiBold, new Thickness(0)));

                Border b = new Border();
                b.Background = tagFill;
                b.BorderBrush = tagEdge;
                b.BorderThickness = new Thickness(1);
                b.CornerRadius = new CornerRadius(3);
                b.Padding = new Thickness(6, 2, 6, 2);
                b.Margin = new Thickness(8, 0, 0, 0);
                b.VerticalAlignment = VerticalAlignment.Center;
                b.Child = Text(tag, 10, tagText, FontWeights.Bold, new Thickness(0));
                row.Children.Add(b);
                sp.Children.Add(row);
            }

            if (description != null)
                sp.Children.Add(Wrap(description, 12, TextDim, new Thickness(0, 6, 0, 0)));

            if (fields != null)
            {
                FrameworkElement fe = fields as FrameworkElement;
                if (fe != null) fe.Margin = new Thickness(0, 11, 0, 0);
                sp.Children.Add(fields);
            }

            if (actions != null)
            {
                FrameworkElement fe = actions as FrameworkElement;
                if (fe != null) fe.Margin = new Thickness(0, 11, 0, 0);
                sp.Children.Add(actions);
            }

            card.Child = sp;
            return card;
        }

        // Evenly spaced parameter fields across one row.
        private Grid FieldRow(params UIElement[] fields)
        {
            Grid g = new Grid();
            for (int i = 0; i < fields.Length; i++)
            {
                if (i > 0) g.ColumnDefinitions.Add(ColFixed(8));
                g.ColumnDefinitions.Add(Col());
            }
            for (int i = 0; i < fields.Length; i++)
            {
                Grid.SetColumn(fields[i], i * 2);
                g.Children.Add(fields[i]);
            }
            return g;
        }

        // Options on the left, the button on the right.
        private DockPanel ActionRow(Button go, double minWidth, params UIElement[] options)
        {
            DockPanel dp = new DockPanel();
            dp.LastChildFill = false;

            go.MinWidth = minWidth;
            DockPanel.SetDock(go, Dock.Right);
            dp.Children.Add(go);

            if (options != null && options.Length > 0)
            {
                StackPanel sp = new StackPanel();
                sp.Orientation = Orientation.Horizontal;
                sp.VerticalAlignment = VerticalAlignment.Center;
                for (int i = 0; i < options.Length; i++)
                {
                    if (options[i] == null) continue;
                    FrameworkElement fe = options[i] as FrameworkElement;
                    if (fe != null && sp.Children.Count > 0) fe.Margin = new Thickness(14, 0, 0, 0);
                    sp.Children.Add(options[i]);
                }
                DockPanel.SetDock(sp, Dock.Left);
                dp.Children.Add(sp);
            }
            return dp;
        }

        private Button Go(string label, Action onClick)
        {
            Button b = PrimaryButton(label, 32);
            b.Click += delegate { onClick(); };
            return b;
        }

        // ---------------- silkscreen ----------------

        private UIElement BuildSilkscreenSection()
        {
            skHeight = null; skStroke = null;

            UIElement f0 = LabeledField("Text height mm", Settings.GetValue("SkHeight", "1.000"),
                                        "Designator text height", out skHeight);
            UIElement f1 = LabeledField("Stroke mm", Settings.GetValue("SkStroke", "0.150"),
                                        "Designator stroke width", out skStroke);

            skSelOnly = SideCheck("Selection only", Settings.GetValue("SkSelOnly", "0") != "0");
            skComments = SideCheck("Comments too", Settings.GetValue("SkComments", "0") != "0");

            DockPanel showHide = new DockPanel();
            showHide.LastChildFill = false;
            Button hide = PrimaryButton("Hide", 32);
            hide.MinWidth = 100;
            hide.Click += delegate { DoShowHide(false); };
            DockPanel.SetDock(hide, Dock.Right);
            showHide.Children.Add(hide);
            Button show = SecondaryButton("Show", 32, 12);
            show.MinWidth = 100;
            show.Margin = new Thickness(0, 0, 8, 0);
            show.Click += delegate { DoShowHide(true); };
            DockPanel.SetDock(show, Dock.Right);
            showHide.Children.Add(show);

            return Stack(
                ToolCard("Centre designators", "MODIFIES BOARD", Hex("#3D3527"), Hex("#5F5130"), Hex("#F0C477"),
                    "Moves each designator onto the middle of its component body — not onto the component's " +
                    "X/Y, which is the anchor and on many footprints is nowhere near the middle. Designators " +
                    "are switched to manual positioning first, or autoposition pulls them straight back.",
                    null, ActionRow(Go("Centre designators", DoCentreDesignators), 168, skSelOnly, skComments)),

                ToolCard("Autoposition", null, null, null, null,
                    "Hands designators back to Altium's autoposition, placing them above the component body. " +
                    "The opposite of centring — useful for undoing a manual mess across a whole board.",
                    null, ActionRow(Go("Autoposition designators", DoAutoPosition), 190)),

                ToolCard("Show / hide", null, null, null, null,
                    "Turns designators — and comments, if ticked — on or off in bulk. Faster than a selection " +
                    "filter when you want a clean assembly drawing or a readable screen.",
                    null, showHide),

                ToolCard("Normalise text", null, null, null, null,
                    "One height and stroke across every designator. A stroke wider than about a quarter of " +
                    "the height fills the letterforms in once it is screened onto a board, and you will be " +
                    "warned if the values cross that.",
                    FieldRow(f0, f1),
                    ActionRow(Go("Normalise text", DoNormaliseText), 168)),

                BuildSilkOverPadsCard());
        }

        private UIElement BuildSilkOverPadsCard()
        {
            sopSelect = SideCheck("Select offenders", Settings.GetValue("SopSelect", "1") != "0");

            return ToolCard("Silkscreen over pads", "FINDS FAB ERRORS", Hex("#3D3527"), Hex("#5F5130"), Hex("#F0C477"),
                "Overlay primitives sitting on exposed copper. The fab house clips silkscreen back off the " +
                "mask openings, so the ink you drew across a pad is simply not printed — a designator loses " +
                "half its characters and nobody sees it until the boards arrive. Through-hole pads are " +
                "checked against both overlays, because they are exposed on both sides.",
                null, ActionRow(Go("Check silkscreen", DoSilkOverPads), 168, sopSelect));
        }

        // ---------------- placement ----------------

        private UIElement BuildPlacementSection()
        {
            return Stack(BuildOffBoardCard(), BuildCollisionCard(), BuildRotationCard(),
                         BuildSnapCard(), BuildRenumberCard());
        }

        private UIElement BuildOffBoardCard()
        {
            UIElement f0 = LabeledField("Margin mm", Settings.GetValue("ObMargin", "0.050"),
                                        "A corner this close to the edge still counts as on the board",
                                        out obMargin);

            obSelect = SideCheck("Select offenders", Settings.GetValue("ObSelect", "1") != "0");

            return ToolCard("Off-board components", "FINDS FAB ERRORS", Hex("#3D3527"), Hex("#5F5130"), Hex("#F0C477"),
                "Component bodies crossing or clearing the board outline. Altium's board-outline clearance " +
                "rule watches copper, not bodies, so a connector hanging over the edge passes DRC and is " +
                "found by the assembler. Designators are ignored — ink past the edge is cosmetic, a body " +
                "past the edge is not.",
                FieldRow(f0),
                ActionRow(Go("Check board edge", DoOffBoard), 168, obSelect));
        }

        private UIElement BuildCollisionCard()
        {
            UIElement f0 = LabeledField("Clearance mm", Settings.GetValue("ColClear", "0.000"),
                                        "Report pairs closer than this, not only overlapping ones",
                                        out colClear);

            colSelect = SideCheck("Select offenders", Settings.GetValue("ColSelect", "1") != "0");

            return ToolCard("Component collisions", null, null, null, null,
                "Component bodies sitting on top of each other on the same side — two parts pasted in the " +
                "same place, a part dropped onto a neighbour, a footprint whose body is far bigger than " +
                "anyone expected. Bodies are compared as axis-aligned rectangles, so a rotated part reads " +
                "larger than it is: every hit is worth a look, not every hit is a fault.",
                FieldRow(f0),
                ActionRow(Go("Check collisions", DoCollisions), 168, colSelect));
        }

        private UIElement BuildRotationCard()
        {
            UIElement f0 = LabeledField("Rotation °", Settings.GetValue("RotAngle", "0"),
                                        "Absolute angle, counter-clockwise", out rotAngle);

            return ToolCard("Align rotation", "MODIFIES BOARD", Hex("#3D3527"), Hex("#5F5130"), Hex("#F0C477"),
                "Sets every selected component to one rotation. The angle is absolute, not added, so " +
                "running it twice does the same as running it once — which is what you want when a row of " +
                "parts came in at four different angles.",
                FieldRow(f0),
                ActionRow(Go("Align selected", DoAlignRotation), 168));
        }

        private UIElement BuildSnapCard()
        {
            UIElement f0 = LabeledField("Grid mm", Settings.GetValue("SnapGrid", "0.100"),
                                        "Placement grid the origins are pulled onto", out snapGrid);

            snapSel = SideCheck("Selection only", Settings.GetValue("SnapSel", "1") != "0");
            snapLocked = SideCheck("Skip locked", Settings.GetValue("SnapLocked", "1") != "0");

            return ToolCard("Snap to grid", "MODIFIES BOARD", Hex("#3D3527"), Hex("#5F5130"), Hex("#F0C477"),
                "Pulls component origins onto a placement grid. Parts that arrived from a library import, " +
                "a paste at an odd snap setting or a drag with the grid off end up on coordinates no " +
                "assembly file should carry. This moves the origin, which on many footprints is not the " +
                "middle of the body.",
                FieldRow(f0),
                ActionRow(Go("Snap to grid", DoSnapToGrid), 168, snapSel, snapLocked));
        }

        private UIElement BuildRenumberCard()
        {
            UIElement f0 = LabeledField("Row band mm", Settings.GetValue("RnRow", "5.000"),
                                        "Parts within this band of each other count as one row",
                                        out rnRow);

            rnSel = SideCheck("Selection only", Settings.GetValue("RnSel", "0") != "0");
            rnTopDown = SideCheck("Top row first", Settings.GetValue("RnTopDown", "1") != "0");

            DockPanel actions = new DockPanel();
            actions.LastChildFill = false;

            Button apply = PrimaryButton("Apply renumber", 32);
            apply.MinWidth = 150;
            apply.Click += delegate { DoRenumber(true); };
            DockPanel.SetDock(apply, Dock.Right);
            actions.Children.Add(apply);

            Button propose = SecondaryButton("Propose only", 32, 12);
            propose.MinWidth = 130;
            propose.Margin = new Thickness(0, 0, 8, 0);
            propose.Click += delegate { DoRenumber(false); };
            DockPanel.SetDock(propose, Dock.Right);
            actions.Children.Add(propose);

            StackPanel opts = new StackPanel();
            opts.Orientation = Orientation.Horizontal;
            opts.VerticalAlignment = VerticalAlignment.Center;
            opts.Children.Add(rnSel);
            rnTopDown.Margin = new Thickness(14, 0, 0, 0);
            opts.Children.Add(rnTopDown);
            DockPanel.SetDock(opts, Dock.Left);
            actions.Children.Add(opts);

            return ToolCard("Renumber designators", "DESYNCS THE SCHEMATIC", Hex("#3D2A2A"), Hex("#5F3535"), Hex("#E89797"),
                "Renumbers by position — rows from the top, left to right inside a row, counting per " +
                "prefix. Propose first: it writes renumber_proposal.csv and changes nothing. Apply renames " +
                "on the PCB only, through a scratch name so R3→R1 cannot collide with the R1 that still " +
                "exists, and leaves the schematic out of step until you run Design > Update Schematics.",
                FieldRow(f0), actions);
        }

        // ---------------- polygons ----------------

        private UIElement BuildPolygonsSection()
        {
            rpStale = SideCheck("Stale only", Settings.GetValue("RpStale", "1") != "0");
            rpSel = SideCheck("Selection only", Settings.GetValue("RpSel", "0") != "0");

            return Stack(
                ToolCard("Polygon report", "FINDS STALE COPPER", Hex("#3D3527"), Hex("#5F5130"), Hex("#F0C477"),
                    "Every polygon with its pour settings and, more to the point, whether its copper is " +
                    "current. A polygon not repoured since the last edit shows the copper it had then — on " +
                    "screen, in DRC and in Gerber — so a ground pour with a hole under a part you moved an " +
                    "hour ago looks right everywhere and arrives wrong. Polygons set to ignore violations " +
                    "are flagged too: their DRC results mean nothing.",
                    null, ActionRow(Go("Export polygons", DoPolygonReport), 178)),

                ToolCard("Repour", "MODIFIES BOARD", Hex("#3D3527"), Hex("#5F5130"), Hex("#F0C477"),
                    "Rebuilds polygons whose copper is out of date. Pour order is left alone, so overlapping " +
                    "polygons keep the precedence they already had — reordering them silently would move " +
                    "copper on a board someone has signed off.",
                    null, ActionRow(Go("Repour polygons", DoRepour), 178, rpStale, rpSel)));
        }

        // ---------------- geometry ----------------

        private UIElement BuildGeometrySection()
        {
            UIElement f0 = LabeledField("Radius mm", Settings.GetValue("GeoRadius", "0.500"),
                                        "Fillet radius", out geoRadius);
            UIElement f1 = LabeledField("Scale factor", Settings.GetValue("GeoScale", "1.000"),
                                        "Multiplier, about the centre of the selection", out geoScale);

            geoClamp = SideCheck("Shrink radius to fit", Settings.GetValue("GeoClamp", "1") != "0");

            DockPanel dist = new DockPanel();
            dist.LastChildFill = false;
            Button vert = PrimaryButton("Vertically", 32);
            vert.MinWidth = 120;
            vert.Click += delegate { DoDistribute(false); };
            DockPanel.SetDock(vert, Dock.Right);
            dist.Children.Add(vert);
            Button horz = SecondaryButton("Horizontally", 32, 12);
            horz.MinWidth = 120;
            horz.Margin = new Thickness(0, 0, 8, 0);
            horz.Click += delegate { DoDistribute(true); };
            DockPanel.SetDock(horz, Dock.Right);
            dist.Children.Add(horz);

            return Stack(
                ToolCard("Fillet corners", "MODIFIES BOARD", Hex("#3D3527"), Hex("#5F5130"), Hex("#F0C477"),
                    "Rounds the corner between every pair of selected tracks that share an endpoint. Each " +
                    "corner is either filleted completely or left exactly as it was — a track shortened " +
                    "without its arc is a broken connection.",
                    FieldRow(f0),
                    ActionRow(Go("Fillet selected corners", DoFillet), 178, geoClamp)),

                ToolCard("Distribute", null, null, null, null,
                    "Spaces three or more selected objects evenly, keeping the outermost two where they are. " +
                    "Objects are sorted by position first, so nothing shuffles past anything else.",
                    null, dist),

                ToolCard("Flip components", "MODIFIES BOARD", Hex("#3D3527"), Hex("#5F5130"), Hex("#F0C477"),
                    "Flips the selected components to the other side. Uses Altium's own FlipComponent, which " +
                    "moves the layer, mirrors the footprint and carries the designator with it — doing it by " +
                    "hand gets the pads right and the silkscreen backwards.",
                    null, ActionRow(Go("Flip selected", DoFlip), 168)),

                ToolCard("Scale selection", null, null, null, null,
                    "Scales selected geometry about the centre of its own extent. Track widths, hole sizes " +
                    "and pad sizes are deliberately left alone — scaling those turns a manufacturable board " +
                    "into one that misses its design rules everywhere at once.",
                    FieldRow(f1),
                    ActionRow(Go("Scale selection", DoScale), 168)));
        }

        // ---------------- layers ----------------

        private UIElement BuildLayersSection()
        {
            UIElement f1 = LabeledField("Via Ø mm", Settings.GetValue("LyViaDia", "0.600"),
                                        "Diameter of vias added where a connection would break", out lyViaDia);
            UIElement f2 = LabeledField("Hole mm", Settings.GetValue("LyViaHole", "0.300"),
                                        "Hole size of those vias", out lyViaHole);

            // Populated on first use rather than at build time, because the
            // board may not be open when the window is constructed.
            lyTarget = new ComboBox();
            lyTarget.Height = 30;
            lyTarget.FontFamily = MonoFont;
            lyTarget.FontSize = 12;
            lyTarget.Foreground = Hex("#DCDCDC");
            lyTarget.Background = Field;
            lyTarget.BorderBrush = FieldBorder;
            lyTarget.BorderThickness = new Thickness(1);
            lyTarget.VerticalContentAlignment = VerticalAlignment.Center;
            lyTarget.IsEditable = true;
            lyTarget.Text = Settings.GetValue("LyTarget", "Bottom Layer");
            AutomationName(lyTarget, "Target signal layer");

            StackPanel targetCell = new StackPanel();
            targetCell.Children.Add(Text("Target layer", 11, TextDim, FontWeights.Normal, new Thickness(0, 0, 0, 5)));
            targetCell.Children.Add(lyTarget);

            lyVias = SideCheck("Add vias", Settings.GetValue("LyVias", "1") != "0");

            DockPanel vis = new DockPanel();
            vis.LastChildFill = false;
            Button hide = PrimaryButton("Hide all signal", 32);
            hide.MinWidth = 140;
            hide.Click += delegate { DoVisibility(false); };
            DockPanel.SetDock(hide, Dock.Right);
            vis.Children.Add(hide);
            Button show = SecondaryButton("Show all signal", 32, 12);
            show.MinWidth = 140;
            show.Margin = new Thickness(0, 0, 8, 0);
            show.Click += delegate { DoVisibility(true); };
            DockPanel.SetDock(show, Dock.Right);
            vis.Children.Add(show);

            return Stack(
                ToolCard("Move to layer", "MODIFIES BOARD", Hex("#3D3527"), Hex("#5F5130"), Hex("#F0C477"),
                    "Moves selected tracks and arcs to another signal layer and places a via wherever that " +
                    "would break a connection. Without the vias the net is broken silently — the track still " +
                    "looks connected. Internal planes are refused: a track on a plane is a void, not a wire.",
                    FieldRow(targetCell, f1, f2),
                    ActionRow(Go("Move selected copper", DoMoveToLayer), 178, lyVias)),

                ToolCard("Export stack", null, null, null, null,
                    "The physical stack as a CSV: every layer with material, thickness in mm and mil, " +
                    "dielectric constant and loss tangent, plus the finished total.",
                    null, ActionRow(Go("Export layer stack", DoExportStack), 178)),

                ToolCard("Layer visibility", null, null, null, null,
                    "Shows or hides every signal layer at once.",
                    null, vis),

                ToolCard("Mechanical layer map", null, null, null, null,
                    "Every mechanical layer with its name, whether it is enabled, visible and paired, and " +
                    "how many primitives are actually on it. Mechanical layers carry no fixed meaning — one " +
                    "board's assembly drawing is another's courtyard — so before handing files over this is " +
                    "the sheet that says which layer is which, and which ones are empty and can be dropped.",
                    null, ActionRow(Go("Export layer map", DoMechLayerNames), 178)));
        }

        // ---------------- variants ----------------

        private UIElement BuildVariantsSection()
        {
            return Stack(
                ToolCard("Variant report", null, null, null, null,
                    "Every variant with the components it populates, and how many of those are actually " +
                    "ordered. Parts marked NoBOM, Mechanical or Graphical are on the board but must not be " +
                    "purchased — counting them is how a build ends up over on parts.",
                    null, ActionRow(Go("Export variant report", DoVariantReport), 190)));
        }

        // ---------------- design rules ----------------

        private UIElement BuildDesignRulesSection()
        {
            return Stack(
                ToolCard("Export rules", null, null, null, null,
                    "Every rule as a CSV: kind, priority, scope, enabled state and — for the kinds whose " +
                    "constraint member is confirmed in the SDK — its value. Other kinds export with a blank " +
                    "value rather than a guess.",
                    null, ActionRow(Go("Export design rules", DoExportRules), 168)),

                ToolCard("Audit rules", "FINDS INERT RULES", Hex("#3D3527"), Hex("#5F5130"), Hex("#F0C477"),
                    "Rules that exist but are not doing their job: disabled ones, ones with an empty scope " +
                    "that match nothing, and two rules of a kind sharing a priority so which wins depends on " +
                    "ordering. None of these is something Altium will ever report — DRC simply passes.",
                    null, ActionRow(Go("Audit rules", DoAuditRules), 168)));
        }

        // ---------------- testpoints ----------------

        private UIElement BuildTestpointsSection()
        {
            return Stack(BuildTpCoverageCard(), BuildTpAssignCard(), BuildPadCentreCard());
        }

        private UIElement BuildTpCoverageCard()
        {
            return ToolCard("Testpoint coverage", null, null, null, null,
                "Which nets have a testpoint and which do not. Fabrication and assembly testpoints are " +
                "separate flags and are reported separately — a net can have one without the other, and " +
                "conflating them says a net is covered when the test house cannot reach it.",
                null, ActionRow(Go("Export coverage", DoTpCoverage), 168));
        }

        private UIElement BuildTpAssignCard()
        {
            UIElement f0 = LabeledField("Net class", Settings.GetValue("TpClass", ""),
                                        "Net class to assign within; blank for every net", out tpClass);

            tpPads = SideCheck("Through-hole pads too", Settings.GetValue("TpPads", "0") != "0");
            tpAssembly = SideCheck("Assembly flags", Settings.GetValue("TpAssembly", "0") != "0");
            tpUncovered = SideCheck("Skip covered nets", Settings.GetValue("TpUncovered", "1") != "0");

            return ToolCard("Assign testpoints", "MODIFIES BOARD", Hex("#3D3527"), Hex("#5F5130"), Hex("#F0C477"),
                "Marks vias — and optionally through-hole pads — as testpoints. Surface-mount pads are never " +
                "marked: a bed of nails probing an SMD pad damages the joint it is meant to test. With " +
                "\"skip covered\" on, one testpoint per net is assigned, which is what a test house needs.",
                FieldRow(f0),
                ActionRow(Go("Assign testpoints", DoTpAssign), 168, tpPads, tpAssembly, tpUncovered));
        }

        private UIElement BuildPadCentreCard()
        {
            UIElement f0 = LabeledField("Tolerance mm", Settings.GetValue("PcTol", "0.010"),
                                        "How close a track end must be to the pad centre", out pcTol);

            return ToolCard("Pad centres", null, null, null, null,
                "Pads with no track end at their centre. Altium's connectivity is centre-to-centre, so a " +
                "track touching only a pad edge behaves as connected while a probe on the centre may not be. " +
                "A pad fed only by a pour will appear here — that is expected, not a fault.",
                FieldRow(f0),
                ActionRow(Go("Check pad centres", DoPadCentres), 168));
        }

        // ---------------- cleanup ----------------

        private UIElement BuildCleanupSection()
        {
            return Stack(BuildUnnettedCard(), BuildInvalidCard(), BuildAntennaCard(), BuildDanglingCard(),
                         BuildDuplicateCard(), BuildLockRoutingCard());
        }

        private UIElement BuildUnnettedCard()
        {
            ucSelect = SideCheck("Select it", Settings.GetValue("UcSelect", "1") != "0");

            return ToolCard("Unnetted copper", "BREAKS OTHER CHECKS", Hex("#3D2A2A"), Hex("#5F3535"), Hex("#E89797"),
                "Copper that belongs to no net. It looks completely normal on screen, and it is invisible " +
                "to every rule scoped by net or class, contributes nothing to routed length, and connects " +
                "nothing as far as Altium is concerned. It comes from imports and from copy-paste between " +
                "documents. Run this first when current capacity, dangling copper or net lengths come back " +
                "empty — on a board whose copper has no net, all three have nothing to work with. The fix " +
                "is Design > Netlist > Update Free Primitives From Component Pads.",
                null, ActionRow(Go("Find unnetted copper", DoUnnetted), 178, ucSelect));
        }

        private UIElement BuildDuplicateCard()
        {
            UIElement f0 = LabeledField("Tolerance mm", Settings.GetValue("DupTol", "0.001"),
                                        "How close two endpoints must be to count as the same", out dupTol);

            DockPanel actions = new DockPanel();
            actions.LastChildFill = false;

            Button del = PrimaryButton("Delete duplicates", 32);
            del.MinWidth = 150;
            del.Click += delegate { DoDuplicates(true); };
            DockPanel.SetDock(del, Dock.Right);
            actions.Children.Add(del);

            Button find = SecondaryButton("Find only", 32, 12);
            find.MinWidth = 110;
            find.Margin = new Thickness(0, 0, 8, 0);
            find.Click += delegate { DoDuplicates(false); };
            DockPanel.SetDock(find, Dock.Right);
            actions.Children.Add(find);

            return ToolCard("Duplicate tracks", null, null, null, null,
                "Track segments lying exactly on top of each other — from re-routing over an existing path, " +
                "or paste-in-place. They look and behave like one track but emit two identical draws into " +
                "Gerber. Find only selects the duplicates and leaves one copy of each unselected, so deleting " +
                "the selection keeps the routing intact.",
                FieldRow(f0), actions);
        }

        private UIElement BuildLockRoutingCard()
        {
            UIElement f0 = LabeledField("Net filter", Settings.GetValue("LrNet", ""),
                                        "Exact net name, or a prefix ending in *; blank for every net",
                                        out lrNet);

            lrVias = SideCheck("Vias too", Settings.GetValue("LrVias", "1") != "0");
            lrSel = SideCheck("Selection only", Settings.GetValue("LrSel", "0") != "0");

            DockPanel actions = new DockPanel();
            actions.LastChildFill = false;

            Button lockb = PrimaryButton("Lock routing", 32);
            lockb.MinWidth = 140;
            lockb.Click += delegate { DoLockRouting(true); };
            DockPanel.SetDock(lockb, Dock.Right);
            actions.Children.Add(lockb);

            Button unlock = SecondaryButton("Unlock", 32, 12);
            unlock.MinWidth = 110;
            unlock.Margin = new Thickness(0, 0, 8, 0);
            unlock.Click += delegate { DoLockRouting(false); };
            DockPanel.SetDock(unlock, Dock.Right);
            actions.Children.Add(unlock);

            StackPanel opts = new StackPanel();
            opts.Orientation = Orientation.Horizontal;
            opts.VerticalAlignment = VerticalAlignment.Center;
            opts.Children.Add(lrVias);
            lrSel.Margin = new Thickness(14, 0, 0, 0);
            opts.Children.Add(lrSel);
            DockPanel.SetDock(opts, Dock.Left);
            actions.Children.Add(opts);

            return ToolCard("Lock net routing", "MODIFIES BOARD", Hex("#3D3527"), Hex("#5F5130"), Hex("#F0C477"),
                "Locks every track, arc and via on the matching nets, so finished routing survives a stray " +
                "drag. Altium locks components readily; locking a net's copper otherwise means selecting it " +
                "all first. \"DDR*\" matches every net starting DDR.",
                FieldRow(f0), actions);
        }

        private UIElement BuildInvalidCard()
        {
            DockPanel actions = new DockPanel();
            actions.LastChildFill = false;

            Button del = PrimaryButton("Delete them", 32);
            del.MinWidth = 130;
            del.Click += delegate { DoInvalidObjects(true); };
            DockPanel.SetDock(del, Dock.Right);
            actions.Children.Add(del);

            Button find = SecondaryButton("Find only", 32, 12);
            find.MinWidth = 110;
            find.Margin = new Thickness(0, 0, 8, 0);
            find.Click += delegate { DoInvalidObjects(false); };
            DockPanel.SetDock(find, Dock.Right);
            actions.Children.Add(find);

            return ToolCard("Invalid polygons and regions", null, null, null, null,
                "A polygon with fewer than three vertices cannot enclose anything, but Altium keeps it — it " +
                "survives save and reload and turns up later as a Gerber artefact. Find only selects them so " +
                "you can look before anything is removed.",
                null, actions);
        }

        private UIElement BuildAntennaCard()
        {
            UIElement f0 = LabeledField("Tolerance mm", Settings.GetValue("ClTol", "0.010"),
                                        "How close a track end must be to count as touching", out clTol);
            clPours = SideCheck("Ignore vias in pours", Settings.GetValue("ClPours", "1") != "0");

            return ToolCard("Via antennas", null, null, null, null,
                "Vias with copper on only one layer — they connect nothing and each is an unterminated stub. " +
                "Leave the pour option ticked: copper poured over a via connects it in a way a coincident-end " +
                "test cannot see, so every stitching via would otherwise be reported.",
                FieldRow(f0),
                ActionRow(Go("Find via antennas", DoViaAntennas), 168, clPours));
        }

        private UIElement BuildDanglingCard()
        {
            UIElement f0 = LabeledField("Tolerance mm", Settings.GetValue("DcTol", "0.010"),
                                        "How close counts as connected", out dcTol);

            return ToolCard("Dangling copper", null, null, null, null,
                "Track and arc ends that coincide with nothing else on their net — a stub left by an edit, or " +
                "a connection that only looks made. A track ending part-way along another track is a real " +
                "connection in Altium and will not be found here; raise the tolerance if that matters.",
                FieldRow(f0),
                ActionRow(Go("Find dangling ends", DoDangling), 168));
        }

        // ---------------- reports ----------------

        private UIElement BuildReportsSection()
        {
            DockPanel selfTest = new DockPanel();
            selfTest.LastChildFill = false;

            Button full = PrimaryButton("Run full self-test", 32);
            full.MinWidth = 168;
            full.Click += delegate { DoSelfTest(true); };
            DockPanel.SetDock(full, Dock.Right);
            selfTest.Children.Add(full);

            Button ro = SecondaryButton("Read-only self-test", 32, 12);
            ro.MinWidth = 168;
            ro.Margin = new Thickness(0, 0, 8, 0);
            ro.Click += delegate { DoSelfTest(false); };
            DockPanel.SetDock(ro, Dock.Right);
            selfTest.Children.Add(ro);

            return Stack(
                ToolCard("Self-test", "VERIFIES EVERYTHING", Hex("#22382C"), Hex("#2F5540"), Hex("#7FD6A6"),
                    "Runs every function against this board and writes spike_selftest.md with a verdict per " +
                    "check — comparing counts, coordinates and file contents, not just checking nothing threw. " +
                    "The full run also exercises the board-modifying tools: they build their own geometry in a " +
                    "clear area well off the board, read it back, and leave it there as evidence.",
                    null, selfTest),
                ToolCard("Single-pin nets", "FINDS DESIGN ERRORS", Hex("#3D3527"), Hex("#5F5130"), Hex("#F0C477"),
                    "Nets that reach exactly one pin — a wire that goes nowhere: a net label that never found " +
                    "its partner, a pin renamed on one side of a hierarchy, a power net whose only other " +
                    "connection was deleted. DRC does not flag it because there is no violation, the net is " +
                    "simply lonely. Nets reaching no pin at all are reported separately.",
                    null, ActionRow(Go("Find single-pin nets", DoSinglePinNets), 178)),

                ToolCard("Board census", null, null, null, null,
                    "What the board is made of: every object type counted, and copper primitives per layer, " +
                    "plus net, component, class and rule totals.",
                    null, ActionRow(Go("Export census", DoBoardCensus), 168)),

                ToolCard("Drill table", null, null, null, null,
                    "Hole sizes with counts, plated and non-plated separated because a fab house prices them " +
                    "separately. Altium will draw this on a fabrication drawing; here it is data you can diff " +
                    "between revisions or check a quote against.",
                    null, ActionRow(Go("Export drill table", DoDrillTable), 168)),

                ToolCard("Unrouted connections", null, null, null, null,
                    "Pin pairs that still have unrouted length. Net-level state is no use here — a net counts " +
                    "as routed as soon as any copper is on it, so a fly-by net missing one hop looks finished.",
                    null, ActionRow(Go("Export unrouted", DoUnrouted), 168)),

                ToolCard("Measured net lengths", null, null, null, null,
                    "Routed length per net summed from the copper itself — track lengths by Pythagoras, " +
                    "arc lengths by radius and sweep — next to the length Altium reports. It needs no " +
                    "connectivity analysis, so it still works when the reported figure comes back as zero. " +
                    "A net where the two disagree is telling you its copper and its connectivity model " +
                    "have come apart.",
                    null, ActionRow(Go("Measure net lengths", DoMeasureNets), 190)),

                ToolCard("Net class report", null, null, null, null,
                    "Every net class with its members, and — separately — the nets belonging to no class at " +
                    "all. Design rules are scoped by class, so a net that fell out of one is a net running " +
                    "with default clearance and width while the report says the rules are in place.",
                    null, ActionRow(Go("Export net classes", DoNetClassReport), 178)));
        }

        // ---------------- via tools ----------------

        private UIElement BuildViaToolsSection()
        {
            return Stack(BuildReturnViaCard(), BuildTentingCard(), BuildBarrelReliefCard());
        }

        private UIElement BuildReturnViaCard()
        {
            UIElement f0 = LabeledField("Reference net", Settings.GetValue("RvNet", "GND"),
                                        "Net the return vias belong to", out rvNet);
            UIElement f1 = LabeledField("Max distance mm", Settings.GetValue("RvDist", "2.0"),
                                        "Largest acceptable distance to the nearest return via", out rvDist);

            rvOnlySel = SideCheck("Selection only", Settings.GetValue("RvOnlySel", "0") != "0");
            rvCsv = SideCheck("Write CSV", Settings.GetValue("RvCsv", "1") != "0");

            return ToolCard("Return via check", "EMC", Hex("#2A3340"), Hex("#3C4A5C"), Hex("#8FB4DC"),
                "Finds signal vias with no return via nearby. A layer change whose return current has no " +
                "stitching via close by routes its return the long way round, and that loop is radiated " +
                "emission. Offenders are selected on the board.",
                FieldRow(f0, f1),
                ActionRow(Go("Check return vias", DoReturnViaCheck), 168, rvOnlySel, rvCsv));
        }

        private UIElement BuildTentingCard()
        {
            UIElement f0 = LabeledField("Open by mm", Settings.GetValue("TentOpen", "0.050"),
                                        "Mask expansion when opening", out tentOpen);
            UIElement f1 = LabeledField("Min hole mm", Settings.GetValue("TentMinHole", "0"),
                                        "Only vias with at least this hole size, 0 for no limit", out tentMinHole);
            UIElement f2 = LabeledField("Max hole mm", Settings.GetValue("TentMaxHole", "0"),
                                        "Only vias up to this hole size, 0 for no limit", out tentMaxHole);

            tentOnlySel = SideCheck("Selection only", Settings.GetValue("TentOnlySel", "0") != "0");

            DockPanel actions = new DockPanel();
            actions.LastChildFill = false;

            Button open = PrimaryButton("Open mask", 32);
            open.MinWidth = 120;
            open.Click += delegate { DoTenting(false); };
            DockPanel.SetDock(open, Dock.Right);
            actions.Children.Add(open);

            Button tent = SecondaryButton("Tent", 32, 12);
            tent.MinWidth = 100;
            tent.Margin = new Thickness(0, 0, 8, 0);
            tent.Click += delegate { DoTenting(true); };
            DockPanel.SetDock(tent, Dock.Right);
            actions.Children.Add(tent);

            StackPanel opts = new StackPanel();
            opts.Orientation = Orientation.Horizontal;
            opts.VerticalAlignment = VerticalAlignment.Center;
            opts.Children.Add(tentOnlySel);
            DockPanel.SetDock(opts, Dock.Left);
            actions.Children.Add(opts);

            return ToolCard("Via tenting", null, null, null, null,
                "Tenting is not a flag in the SDK — it is a mask opening small enough to vanish, so Tent " +
                "sets a negative expansion past the pad radius. Both buttons set the expansion manually, " +
                "which overrides the solder mask rule for those vias.",
                FieldRow(f0, f1, f2), actions);
        }

        private UIElement BuildBarrelReliefCard()
        {
            UIElement f0 = LabeledField("Min hole mm", Settings.GetValue("BrMinHole", "0.500"),
                                        "Only vias with at least this hole size", out brMinHole);
            UIElement f1 = LabeledField("Relief mm", Settings.GetValue("BrRelief", "0.050"),
                                        "Mask opening measured from the hole edge", out brRelief);

            brOnlySel = SideCheck("Selection only", Settings.GetValue("BrOnlySel", "0") != "0");

            return ToolCard("Solder mask barrel relief", null, null, null, null,
                "A large plated hole under solder mask is a mask-cracking risk — the mask bridges the barrel " +
                "with nothing under it. This opens the mask a fixed distance from the HOLE edge, so the " +
                "annular opening is consistent whatever the pad size.",
                FieldRow(f0, f1),
                ActionRow(Go("Apply relief", DoBarrelRelief), 150, brOnlySel));
        }

        // ---------------- copper & current ----------------

        private UIElement BuildCopperSection()
        {
            return Stack(BuildCurrentCard(), BuildAreasCard());
        }

        private UIElement BuildCurrentCard()
        {
            UIElement f0 = LabeledField("Temp rise C", Settings.GetValue("CcTemp", "10"),
                                        "Allowed temperature rise above ambient", out ccTemp);
            UIElement f1 = LabeledField("Target A", Settings.GetValue("CcTarget", "0"),
                                        "Current each net must carry; 0 to report only", out ccTarget);

            ccOnlySel = SideCheck("Selection only", Settings.GetValue("CcOnlySel", "0") != "0");

            return ToolCard("Current capacity", "IPC-2221", Hex("#2A3340"), Hex("#3C4A5C"), Hex("#8FB4DC"),
                "Rates every net by its NARROWEST track, using that layer's real copper weight and whether " +
                "it is an outer or inner layer — a buried track carries half what the same track carries " +
                "outside. Nets below the target are selected at their narrowest point.",
                FieldRow(f0, f1),
                ActionRow(Go("Rate nets", DoCurrentCapacity), 150, ccOnlySel));
        }

        private UIElement BuildAreasCard()
        {
            return ToolCard("Copper areas", null, null, null, null,
                "Polygon and region area per layer and per net. Polygon area is the real filled area after " +
                "thermal reliefs and island removal; regions have no area member in the SDK and are reported " +
                "by bounding box, labelled as such.",
                null,
                ActionRow(Go("Export copper areas", DoCopperAreas), 168));
        }

        private UIElement BuildExportColumn()
        {
            DockPanel dp = new DockPanel();
            dp.LastChildFill = false;

            StackPanel top = new StackPanel();
            DockPanel.SetDock(top, Dock.Top);

            top.Children.Add(SectionHeader("EXPORT"));

            // board data
            Border c1 = CardBorderEl(new Thickness(15, 14, 15, 14));
            c1.Margin = new Thickness(0, 0, 0, 10);
            StackPanel s1 = new StackPanel();
            s1.Children.Add(Text("Board data", 13, TextPrimary, FontWeights.SemiBold, new Thickness(0)));
            s1.Children.Add(Wrap("footprint_sizes · pad_nets · board_geometry", 12, TextDim, new Thickness(0, 6, 0, 0)));
            Button bExport = PrimaryButton("Export board data", 34);
            bExport.Margin = new Thickness(0, 12, 0, 0);
            bExport.Click += delegate { DoExportBoardData(); };
            s1.Children.Add(bExport);
            c1.Child = s1;
            top.Children.Add(c1);

            // jlcpcb
            Border c2 = CardBorderEl(new Thickness(15, 14, 15, 14));
            StackPanel s2 = new StackPanel();
            StackPanel titleRow = new StackPanel();
            titleRow.Orientation = Orientation.Horizontal;
            titleRow.Children.Add(Text("JLCPCB assembly", 13, TextPrimary, FontWeights.SemiBold, new Thickness(0)));
            Border tag = new Border();
            tag.Background = Hex("#3D3527");
            tag.BorderBrush = Hex("#5F5130");
            tag.BorderThickness = new Thickness(1);
            tag.CornerRadius = new CornerRadius(3);
            tag.Padding = new Thickness(6, 2, 6, 2);
            tag.Margin = new Thickness(8, 0, 0, 0);
            tag.VerticalAlignment = VerticalAlignment.Center;
            tag.Child = Text("BOM + CPL", 10, Hex("#F0C477"), FontWeights.Bold, new Thickness(0));
            titleRow.Children.Add(tag);
            s2.Children.Add(titleRow);
            s2.Children.Add(Wrap("BOM, pick-and-place and a private missing-parts list", 12, TextDim, new Thickness(0, 6, 0, 0)));
            Button bJlc = PrimaryButton("Export for JLCPCB", 34);
            bJlc.Margin = new Thickness(0, 12, 0, 0);
            bJlc.Click += delegate { DoExportJlc(); };
            s2.Children.Add(bJlc);
            c2.Child = s2;
            top.Children.Add(c2);

            dp.Children.Add(top);

            // output folder pinned to the bottom
            StackPanel bottom = new StackPanel();
            DockPanel.SetDock(bottom, Dock.Bottom);
            bottom.Children.Add(Text("Output folder", 12, TextDim, FontWeights.Normal, new Thickness(0, 0, 0, 6)));

            DockPanel row = new DockPanel();
            row.LastChildFill = true;
            Button browse = SecondaryButton("Browse", 32, 12);
            browse.Margin = new Thickness(7, 0, 0, 0);
            browse.Click += delegate { DoBrowseOutput(); };
            DockPanel.SetDock(browse, Dock.Right);
            row.Children.Add(browse);

            outputFolderBox = new TextBox();
            outputFolderBox.Height = 32;
            outputFolderBox.FontFamily = MonoFont;
            outputFolderBox.FontSize = 12;
            outputFolderBox.Foreground = Hex("#DCDCDC");
            outputFolderBox.Background = Field;
            outputFolderBox.BorderBrush = FieldBorder;
            outputFolderBox.BorderThickness = new Thickness(1);
            outputFolderBox.Padding = new Thickness(8, 0, 8, 0);
            outputFolderBox.VerticalContentAlignment = VerticalAlignment.Center;
            outputFolderBox.CaretBrush = TextPrimary;
            AutomationName(outputFolderBox, "Output folder");
            row.Children.Add(outputFolderBox);

            bottom.Children.Add(row);
            dp.Children.Add(bottom);

            return dp;
        }

        // ---------------- import ----------------
        private UIElement BuildImportColumn()
        {
            StackPanel sp = new StackPanel();

            sp.Children.Add(SectionHeader("IMPORT"));

            Border card = CardBorderEl(new Thickness(5, 5, 5, 5));
            StackPanel rows = new StackPanel();

            rows.Children.Add(ImportRow("Objects", "objects.csv",
                delegate (string p) { objectsCsv = p; }, out objectsPath, out objectsPlace,
                delegate { DoPlace("Place Objects", objectsCsv, BoardImport.PlaceObjects, "object(s)"); }));
            rows.Children.Add(RowDivider());
            rows.Children.Add(ImportRow("Polygon pours", "pours.csv",
                delegate (string p) { poursCsv = p; }, out poursPath, out poursPlace,
                delegate { DoPlace("Place Polygon Pours", poursCsv, BoardImport.PlacePours, "pour(s)"); }));
            rows.Children.Add(RowDivider());
            rows.Children.Add(ImportRow("Regions", "regions.csv",
                delegate (string p) { regionsCsv = p; }, out regionsPath, out regionsPlace,
                delegate { DoPlace("Place Regions", regionsCsv, BoardImport.PlaceRegions, "region(s)"); }));

            card.Child = rows;
            sp.Children.Add(card);

            sp.Children.Add(SectionHeader("COMPONENT LOCK"));

            Border lockCard = CardBorderEl(new Thickness(15, 13, 15, 13));
            StackPanel ls = new StackPanel();
            ls.Children.Add(Wrap("From a CSV of designators, one per line.", 12, TextDim, new Thickness(0, 0, 0, 10)));
            Grid lg = new Grid();
            lg.ColumnDefinitions.Add(Col());
            lg.ColumnDefinitions.Add(ColFixed(8));
            lg.ColumnDefinitions.Add(Col());

            Button bLock = SecondaryButton("Lock…", 32, 12);
            bLock.Click += delegate { DoLock(true); };
            Grid.SetColumn(bLock, 0);
            lg.Children.Add(bLock);

            Button bUnlock = SecondaryButton("Unlock…", 32, 12);
            bUnlock.Click += delegate { DoLock(false); };
            Grid.SetColumn(bUnlock, 2);
            lg.Children.Add(bUnlock);

            ls.Children.Add(lg);
            lockCard.Child = ls;
            sp.Children.Add(lockCard);

            return sp;
        }

        private UIElement RowDivider()
        {
            Border d = new Border();
            d.Height = 1;
            d.Background = CardBorder;
            d.Margin = new Thickness(10, 0, 10, 0);
            return d;
        }

        private UIElement ImportRow(string label, string suggested, Action<string> setPath,
                                    out TextBlock pathText, out Button placeButton, Action onPlace)
        {
            DockPanel dp = new DockPanel();
            dp.LastChildFill = true;
            dp.Margin = new Thickness(10, 10, 10, 10);

            Button place = new Button();
            place.Content = "Place";
            place.Height = 30;
            place.MinWidth = 66;
            place.FontSize = 12;
            place.FontWeight = FontWeights.SemiBold;
            place.Foreground = Hex("#6E6E6E");
            place.Background = Hex("#333333");
            place.BorderBrush = Hex("#414141");
            place.BorderThickness = new Thickness(1);
            place.IsEnabled = false;
            place.Margin = new Thickness(9, 0, 0, 0);
            place.Click += delegate { onPlace(); };
            DockPanel.SetDock(place, Dock.Right);
            dp.Children.Add(place);
            placeButton = place;

            TextBlock pt = Text("no file selected", 11, Hex("#7A7A7A"), FontWeights.Normal, new Thickness(0, 3, 0, 0));
            pt.FontFamily = MonoFont;
            pt.TextTrimming = TextTrimming.CharacterEllipsis;
            pathText = pt;

            Button pick = SecondaryButton("…", 30, 12);
            pick.MinWidth = 32;
            pick.Margin = new Thickness(9, 0, 0, 0);
            AutomationName(pick, "Choose " + label + " file");
            Button placeRef = place;
            TextBlock ptRef = pt;
            pick.Click += delegate
            {
                string chosen = CsvIo.PickCsv("Choose " + label + " CSV", suggested);
                if (chosen == null) return;
                setPath(chosen);
                ptRef.Text = System.IO.Path.GetFileName(chosen);
                ptRef.Foreground = Hex("#6CB6FF");
                ptRef.ToolTip = chosen;
                placeRef.IsEnabled = true;
                placeRef.Foreground = TextPrimary;
                placeRef.Background = BtnStrong;
                placeRef.BorderBrush = Hex("#585858");
            };
            DockPanel.SetDock(pick, Dock.Right);
            dp.Children.Add(pick);

            StackPanel sp = new StackPanel();
            sp.VerticalAlignment = VerticalAlignment.Center;
            sp.Children.Add(Text(label, 13, TextPrimary, FontWeights.SemiBold, new Thickness(0)));
            sp.Children.Add(pt);
            dp.Children.Add(sp);

            return dp;
        }

        // =============================================================
        // status bar
        // =============================================================
        private UIElement BuildStatusBar()
        {
            Border bar = new Border();
            bar.Background = Chrome;
            bar.BorderBrush = Hex("#101010");
            bar.BorderThickness = new Thickness(0, 1, 0, 0);
            bar.Height = 30;

            DockPanel dp = new DockPanel();
            dp.LastChildFill = true;
            dp.Margin = new Thickness(14, 0, 14, 0);

            statusDot = new Ellipse();
            statusDot.Width = 7; statusDot.Height = 7;
            statusDot.Fill = Green;
            statusDot.VerticalAlignment = VerticalAlignment.Center;
            statusDot.Margin = new Thickness(0, 0, 9, 0);
            DockPanel.SetDock(statusDot, Dock.Left);
            dp.Children.Add(statusDot);

            statusText = Text("Ready", 11, TextDim, FontWeights.Normal, new Thickness(0));
            statusText.VerticalAlignment = VerticalAlignment.Center;
            statusText.TextTrimming = TextTrimming.CharacterEllipsis;
            dp.Children.Add(statusText);

            bar.Child = dp;
            return bar;
        }

        // =============================================================
        // shared element builders
        // =============================================================
        private static TextBlock Text(string s, double size, Brush fg, FontWeight weight, Thickness margin)
        {
            TextBlock t = new TextBlock();
            t.Text = s;
            t.FontSize = size;
            t.Foreground = fg;
            t.FontWeight = weight;
            t.Margin = margin;
            t.FontFamily = UiFont;
            return t;
        }

        private static TextBlock Wrap(string s, double size, Brush fg, Thickness margin)
        {
            TextBlock t = Text(s, size, fg, FontWeights.Normal, margin);
            t.TextWrapping = TextWrapping.Wrap;
            t.LineHeight = size * 1.5;
            return t;
        }

        private static Border CardBorderEl(Thickness padding)
        {
            Border b = new Border();
            b.Background = Card;
            b.BorderBrush = CardBorder;
            b.BorderThickness = new Thickness(1);
            b.CornerRadius = new CornerRadius(6);
            b.Padding = padding;
            return b;
        }

        private UIElement SectionHeader(string label)
        {
            DockPanel dp = new DockPanel();
            dp.LastChildFill = true;
            dp.Margin = new Thickness(0, 0, 0, 10);

            TextBlock t = Text(label, 11, Hex("#9E9E9E"), FontWeights.Bold, new Thickness(0, 0, 9, 0));
            t.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(t, Dock.Left);
            dp.Children.Add(t);

            Border line = new Border();
            line.Height = 1;
            line.Background = Divider;
            line.VerticalAlignment = VerticalAlignment.Center;
            dp.Children.Add(line);

            return dp;
        }

        private static Button PrimaryButton(string label, double height)
        {
            Button b = new Button();
            b.Content = label;
            b.Height = height;
            b.FontSize = 12;
            b.FontWeight = FontWeights.SemiBold;
            b.Foreground = Brushes.White;
            b.Background = Accent;
            b.BorderBrush = AccentEdge;
            b.BorderThickness = new Thickness(1);
            b.Cursor = Cursors.Hand;
            return b;
        }

        private static Button SecondaryButton(string label, double height, double size)
        {
            Button b = new Button();
            b.Content = label;
            b.Height = height;
            b.MinWidth = 64;
            b.Padding = new Thickness(13, 0, 13, 0);
            b.FontSize = size;
            b.Foreground = Hex("#DCDCDC");
            b.Background = Btn;
            b.BorderBrush = BtnBorder;
            b.BorderThickness = new Thickness(1);
            b.Cursor = Cursors.Hand;
            return b;
        }

        // =============================================================
        // board access + actions
        // =============================================================
        private bool TryGetBoard(out IPCB_ServerInterface pcbServer, out IPCB_Board board)
        {
            pcbServer = null;
            board = null;
            try
            {
                client.StartServer("PCB");
                pcbServer = client.GetServerModuleByName("PCB") as IPCB_ServerInterface;
            }
            catch (Exception ex)
            {
                Log.Exception("SpikeWindow/PCBServer", ex);
                return false;
            }
            if (pcbServer == null) return false;
            board = pcbServer.GetCurrentPCBBoard();
            return board != null;
        }

        public void RefreshBoard()
        {
            try
            {
                string folder = Settings.GetOutputFolder();
                if (folder != null && outputFolderBox != null) outputFolderBox.Text = folder;

                IPCB_ServerInterface pcbServer;
                IPCB_Board board;
                if (!TryGetBoard(out pcbServer, out board))
                {
                    docName.Text = "No PCB document";
                    docStats.Text = "Open a .PcbDoc and press Refresh";
                    SetStatus("No active PCB document", Amber);
                    return;
                }

                string file = "PCB document";
                try { file = System.IO.Path.GetFileName(board.GetState_FileName() ?? "") ; } catch { }
                if (string.IsNullOrEmpty(file)) file = "PCB document";
                docName.Text = file;

                int total = 0, locked = 0;
                IPCB_BoardIterator it = board.BoardIterator_Create();
                try
                {
                    it.AddFilter_ObjectSet(new TObjectSet(TObjectId.eComponentObject));
                    it.AddFilter_AllLayers();
                    it.AddFilter_Method(TIterationMethod.eProcessAll);
                    IPCB_Component c = it.FirstPCBObject() as IPCB_Component;
                    while (c != null)
                    {
                        total++;
                        try { if (!c.GetState_Moveable()) locked++; } catch { }
                        c = it.NextPCBObject() as IPCB_Component;
                    }
                }
                finally { board.BoardIterator_Destroy(ref it); }

                string size = "";
                try
                {
                    IPCB_BoardOutline o = board.GetState_BoardOutline();
                    int n = o.GetState_PointCount();
                    if (n > 0)
                    {
                        double minx = double.MaxValue, maxx = double.MinValue, miny = double.MaxValue, maxy = double.MinValue;
                        for (int i = 0; i < n; i++)
                        {
                            PolySegment s = o.GetState_Segments(i);
                            double x = EDP.Utils.CoordToMMs(s.Vx), y = EDP.Utils.CoordToMMs(s.Vy);
                            if (x < minx) minx = x; if (x > maxx) maxx = x;
                            if (y < miny) miny = y; if (y > maxy) maxy = y;
                        }
                        size = " · " + (maxx - minx).ToString("0.#") + " × " + (maxy - miny).ToString("0.#") + " mm";
                    }
                }
                catch { }

                string layers = "";
                try { layers = " · " + board.GetState_LayerStack_V7().SignalLayerCount() + " layers"; } catch { }

                docStats.Text = total + " components" + size + layers + " · " + locked + " locked";
                SetStatus("Ready", Green);
            }
            catch (Exception ex)
            {
                Log.Exception("SpikeWindow.RefreshBoard", ex);
                SetStatus("Could not read the board: " + ex.Message, Red);
            }
        }

        private void SetStatus(string message, Brush colour)
        {
            if (statusText == null) return;
            statusText.Text = message;
            statusDot.Fill = colour;
            Log.Write("SpikeWindow status: " + message);
        }

        // ---------------- high-speed handlers ----------------

        private void DoExportNetLengths()
        {
            try
            {
                IPCB_ServerInterface pcbServer;
                IPCB_Board board;
                if (!TryGetBoard(out pcbServer, out board))
                {
                    SetStatus("No active PCB document", Amber);
                    return;
                }
                string folder = EnsureOutputFolder();
                if (folder == null) { SetStatus("Cancelled", TextDim); return; }

                NetLengths.Result res = NetLengths.Export(board, folder);

                List<string> lines = new List<string>();
                lines.Add("net_lengths.csv — " + res.NetRows + " net(s)");
                lines.Add("pin_pair_lengths.csv — " + res.PinPairRows + " pin pair(s)");
                foreach (string n in res.Notes) lines.Add(n);
                lines.Add(folder);

                ShowResult("Net lengths exported", Green, lines.ToArray());
                SetStatus("2 files written to " + folder, Green);
            }
            catch (Exception ex)
            {
                Log.Exception("SpikeWindow.DoExportNetLengths", ex);
                ShowResult("Net length export failed", Red, ex.GetType().Name + " — " + ex.Message);
                SetStatus("Net length export failed", Red);
            }
        }

        private void DoViaFence()
        {
            try
            {
                IPCB_ServerInterface pcbServer;
                IPCB_Board board;
                if (!TryGetBoard(out pcbServer, out board))
                {
                    SetStatus("No active PCB document", Amber);
                    return;
                }

                FenceOptions opt = new FenceOptions();
                string problem;
                if (!ReadFenceOptions(opt, out problem))
                {
                    ShowResult("Check the fence settings", Amber, problem);
                    SetStatus(problem, Amber);
                    return;
                }

                RememberFenceOptions(opt);

                ViaFence.Result r = ViaFence.Fence(pcbServer, board, opt);

                if (r.Placed == 0)
                {
                    List<string> lines = new List<string>();
                    if (r.Errors.Count > 0) lines.AddRange(r.Errors);
                    else lines.Add("Nothing was placed.");
                    ShowResult("No vias placed", Amber, lines.ToArray());
                    SetStatus("No vias placed", Amber);
                    return;
                }

                List<string> ok = new List<string>();
                ok.Add(r.Placed + " via(s) on net " + opt.NetName + " along " + r.SegmentsUsed + " segment(s)");
                ok.Add("pitch " + opt.PitchMM.ToString("0.###", CultureInfo.InvariantCulture) +
                       " mm · offset " + opt.OffsetMM.ToString("0.###", CultureInfo.InvariantCulture) +
                       " mm · " + opt.ViaDiameterMM.ToString("0.###", CultureInfo.InvariantCulture) +
                       "/" + opt.HoleSizeMM.ToString("0.###", CultureInfo.InvariantCulture) + " mm");
                if (r.SkippedDuplicate > 0)
                    ok.Add(r.SkippedDuplicate + " candidate(s) merged at segment junctions");
                if (r.SkippedInnerArc > 0)
                    ok.Add("inner wall skipped on " + r.SkippedInnerArc + " arc(s) (offset ≥ radius)");
                if (r.SkippedNonTrace > 0)
                    ok.Add(r.SkippedNonTrace + " selected object(s) were not tracks or arcs");
                if (r.HitCap)
                    ok.Add("STOPPED at the via limit — the pitch may be too small");
                foreach (string e in r.Errors) ok.Add(e);
                ok.Add("No clearance check was performed — run Design › Rule Check.");

                ShowResult("Via fence placed", Green, ok.ToArray());
                SetStatus(r.Placed + " vias placed — run DRC", Green);
            }
            catch (Exception ex)
            {
                Log.Exception("SpikeWindow.DoViaFence", ex);
                ShowResult("Via fence failed", Red, ex.GetType().Name + " — " + ex.Message);
                SetStatus("Via fence failed", Red);
            }
        }

        // This machine's Windows locale is fr-FR, where the decimal separator
        // is a comma. Parsing invariant-only would reject "0,5" typed by
        // someone using their own keyboard habits; parsing current-culture
        // only would reject "0.5" pasted from a datasheet. Accept both by
        // normalising the separator, which is unambiguous here because none
        // of these fields is ever a thousands-grouped number.
        private static bool TryMM(string raw, out double value)
        {
            value = 0;
            if (raw == null) return false;
            string s = raw.Trim().Replace(',', '.');
            if (s.Length == 0) return false;
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        private bool ReadFenceOptions(FenceOptions opt, out string problem)
        {
            problem = null;

            if (!TryMM(fencePitch.Text, out opt.PitchMM)) { problem = "Pitch is not a number."; return false; }
            if (!TryMM(fenceOffset.Text, out opt.OffsetMM)) { problem = "Offset is not a number."; return false; }
            if (!TryMM(fenceDia.Text, out opt.ViaDiameterMM)) { problem = "Via diameter is not a number."; return false; }
            if (!TryMM(fenceHole.Text, out opt.HoleSizeMM)) { problem = "Hole size is not a number."; return false; }

            opt.NetName = (fenceNet.Text ?? "").Trim();
            if (opt.NetName.Length == 0) { problem = "Enter the net the fence vias belong to."; return false; }

            opt.LeftSide = fenceLeft.IsChecked == true;
            opt.RightSide = fenceRight.IsChecked == true;
            if (!opt.LeftSide && !opt.RightSide) { problem = "Tick at least one side."; return false; }

            // The remaining range checks live in ViaFence.Fence so the same
            // rules apply however it is called; this only catches what would
            // otherwise be a confusing empty result.
            return true;
        }

        private void RememberFenceOptions(FenceOptions opt)
        {
            Settings.SetValue("FencePitch", fencePitch.Text.Trim());
            Settings.SetValue("FenceOffset", fenceOffset.Text.Trim());
            Settings.SetValue("FenceDia", fenceDia.Text.Trim());
            Settings.SetValue("FenceHole", fenceHole.Text.Trim());
            Settings.SetValue("FenceNet", opt.NetName);
            Settings.SetValue("FenceLeft", opt.LeftSide ? "1" : "0");
            Settings.SetValue("FenceRight", opt.RightSide ? "1" : "0");
        }

        // ---------------- silkscreen handlers ----------------

        private void Report(string okTitle, string failTitle, int changed, List<string> lines, bool good)
        {
            ShowResult(good ? okTitle : failTitle, good ? Green : Amber, lines.ToArray());
            SetStatus(good ? changed + " changed" : failTitle, good ? Green : Amber);
        }

        private bool Board(out IPCB_ServerInterface pcbServer, out IPCB_Board board)
        {
            if (TryGetBoard(out pcbServer, out board)) return true;
            SetStatus("No active PCB document", Amber);
            return false;
        }

        private void DoCentreDesignators()
        {
            try
            {
                IPCB_ServerInterface s; IPCB_Board b;
                if (!Board(out s, out b)) return;

                Settings.SetValue("SkSelOnly", skSelOnly.IsChecked == true ? "1" : "0");
                Settings.SetValue("SkComments", skComments.IsChecked == true ? "1" : "0");

                Silkscreen.Result r = Silkscreen.CentreDesignators(s, b,
                    skSelOnly.IsChecked == true, skComments.IsChecked == true);

                List<string> lines = new List<string>();
                lines.Add(r.Changed + " designator(s) centred of " + r.Considered + " component(s)");
                foreach (string n in r.Notes) lines.Add(n);
                foreach (string e in r.Errors) lines.Add(e);
                Report("Designators centred", "Nothing centred", r.Changed, lines, r.Changed > 0);
            }
            catch (Exception ex) { Fail("Centre designators", ex); }
        }

        private void DoAutoPosition()
        {
            try
            {
                IPCB_ServerInterface s; IPCB_Board b;
                if (!Board(out s, out b)) return;

                Silkscreen.Result r = Silkscreen.AutoPosition(s, b,
                    skSelOnly.IsChecked == true,
                    TTextAutoposition.eAutoPos_TopCenter,
                    skComments.IsChecked == true);

                List<string> lines = new List<string>();
                lines.Add(r.Changed + " designator(s) set to autoposition above the body");
                foreach (string e in r.Errors) lines.Add(e);
                Report("Autoposition applied", "Nothing changed", r.Changed, lines, r.Changed > 0);
            }
            catch (Exception ex) { Fail("Autoposition", ex); }
        }

        private void DoShowHide(bool show)
        {
            try
            {
                IPCB_ServerInterface s; IPCB_Board b;
                if (!Board(out s, out b)) return;

                Silkscreen.Result r = Silkscreen.ShowHide(s, b,
                    skSelOnly.IsChecked == true, show, true, skComments.IsChecked == true);

                List<string> lines = new List<string>();
                lines.Add(r.Changed + " component(s) — designators " + (show ? "shown" : "hidden") +
                          (skComments.IsChecked == true ? ", comments too" : ""));
                foreach (string e in r.Errors) lines.Add(e);
                Report(show ? "Designators shown" : "Designators hidden", "Nothing changed",
                       r.Changed, lines, r.Changed > 0);
            }
            catch (Exception ex) { Fail("Show/hide", ex); }
        }

        private void DoNormaliseText()
        {
            try
            {
                IPCB_ServerInterface s; IPCB_Board b;
                if (!Board(out s, out b)) return;

                double h, w;
                if (!TryMM(skHeight.Text, out h) || h <= 0.0) { Complain("Text height must be greater than 0."); return; }
                if (!TryMM(skStroke.Text, out w) || w <= 0.0) { Complain("Stroke must be greater than 0."); return; }

                Settings.SetValue("SkHeight", skHeight.Text.Trim());
                Settings.SetValue("SkStroke", skStroke.Text.Trim());

                Silkscreen.Result r = Silkscreen.Normalise(s, b,
                    skSelOnly.IsChecked == true, h, w, skComments.IsChecked == true);

                List<string> lines = new List<string>();
                lines.Add(r.Changed + " designator(s) resized");
                foreach (string n in r.Notes) lines.Add(n);
                foreach (string e in r.Errors) lines.Add(e);
                Report("Text normalised", "Nothing changed", r.Changed, lines, r.Changed > 0);
            }
            catch (Exception ex) { Fail("Normalise text", ex); }
        }

        // ---------------- geometry handlers ----------------

        private void DoFillet()
        {
            try
            {
                IPCB_ServerInterface s; IPCB_Board b;
                if (!Board(out s, out b)) return;

                double r;
                if (!TryMM(geoRadius.Text, out r) || r <= 0.0) { Complain("Radius must be greater than 0."); return; }
                Settings.SetValue("GeoRadius", geoRadius.Text.Trim());
                Settings.SetValue("GeoClamp", geoClamp.IsChecked == true ? "1" : "0");

                Geometry.Result res = Geometry.FilletCorners(s, b, r, geoClamp.IsChecked == true);

                if (res.Applied == 0)
                {
                    List<string> why = new List<string>();
                    if (res.Errors.Count > 0) why.AddRange(res.Errors);
                    if (res.Considered > 0)
                        why.Add(res.Considered + " corner(s) found, none could be filleted");
                    foreach (string x in res.Reasons) why.Add(x);
                    if (res.LargestFittingRadius > 0)
                        why.Add("largest radius that fits here is " +
                                res.LargestFittingRadius.ToString("0.###", CultureInfo.InvariantCulture) + " mm");
                    ShowResult("No corners filleted", Amber, why.ToArray());
                    SetStatus("No corners filleted", Amber);
                    return;
                }

                List<string> lines = new List<string>();
                lines.Add(res.Applied + " corner(s) rounded of " + res.Considered + " found");
                if (res.Skipped > 0) lines.Add(res.Skipped + " skipped");
                foreach (string x in res.Reasons) lines.Add(x);
                foreach (string e in res.Errors) lines.Add(e);

                ShowResult("Corners filleted", Green, lines.ToArray());
                SetStatus(res.Applied + " corners rounded", Green);
            }
            catch (Exception ex) { Fail("Fillet", ex); }
        }

        private void DoDistribute(bool horizontal)
        {
            try
            {
                IPCB_ServerInterface s; IPCB_Board b;
                if (!Board(out s, out b)) return;

                Geometry.Result r = Geometry.Distribute(s, b, horizontal);

                if (r.Errors.Count > 0 && r.Applied == 0)
                {
                    ShowResult("Nothing distributed", Amber, r.Errors.ToArray());
                    SetStatus("Nothing distributed", Amber);
                    return;
                }

                List<string> lines = new List<string>();
                lines.Add(r.Applied + " object(s) moved, spaced evenly " + (horizontal ? "left to right" : "bottom to top"));
                if (r.Skipped > 0) lines.Add(r.Skipped + " object(s) had no movable position");
                foreach (string e in r.Errors) lines.Add(e);

                ShowResult("Distributed", Green, lines.ToArray());
                SetStatus(r.Applied + " objects distributed", Green);
            }
            catch (Exception ex) { Fail("Distribute", ex); }
        }

        private void DoScale()
        {
            try
            {
                IPCB_ServerInterface s; IPCB_Board b;
                if (!Board(out s, out b)) return;

                double f;
                if (!TryMM(geoScale.Text, out f)) { Complain("Scale factor is not a number."); return; }
                Settings.SetValue("GeoScale", geoScale.Text.Trim());

                Geometry.Result r = Geometry.ScaleSelection(s, b, f);

                if (r.Errors.Count > 0 && r.Applied == 0)
                {
                    ShowResult("Nothing scaled", Amber, r.Errors.ToArray());
                    SetStatus("Nothing scaled", Amber);
                    return;
                }

                List<string> lines = new List<string>();
                lines.Add(r.Applied + " object(s) scaled by " + f.ToString("0.###", CultureInfo.InvariantCulture) +
                          " about the centre of the selection");
                lines.Add("track widths, holes and pad sizes were left alone");
                if (r.Skipped > 0) lines.Add(r.Skipped + " object(s) had no movable position");
                foreach (string e in r.Errors) lines.Add(e);

                ShowResult("Selection scaled", Green, lines.ToArray());
                SetStatus(r.Applied + " objects scaled", Green);
            }
            catch (Exception ex) { Fail("Scale", ex); }
        }

        // ---------------- layer handlers ----------------

        private void DoMoveToLayer()
        {
            try
            {
                IPCB_ServerInterface s; IPCB_Board b;
                if (!Board(out s, out b)) return;

                Layers.MoveOptions opt = new Layers.MoveOptions();
                opt.TargetLayer = (lyTarget.Text ?? "").Trim();
                if (opt.TargetLayer.Length == 0) { Complain("Enter or choose a target layer."); return; }
                if (!TryMM(lyViaDia.Text, out opt.ViaDiameterMM) || opt.ViaDiameterMM <= 0.0)
                { Complain("Via diameter must be greater than 0."); return; }
                if (!TryMM(lyViaHole.Text, out opt.ViaHoleMM) || opt.ViaHoleMM <= 0.0)
                { Complain("Via hole must be greater than 0."); return; }
                if (opt.ViaHoleMM >= opt.ViaDiameterMM)
                { Complain("Via hole must be smaller than the via diameter."); return; }
                opt.PlaceVias = lyVias.IsChecked == true;

                Settings.SetValue("LyTarget", opt.TargetLayer);
                Settings.SetValue("LyViaDia", lyViaDia.Text.Trim());
                Settings.SetValue("LyViaHole", lyViaHole.Text.Trim());
                Settings.SetValue("LyVias", opt.PlaceVias ? "1" : "0");

                Layers.Result r = Layers.MoveToLayer(s, b, opt);

                if (r.Moved == 0)
                {
                    ShowResult("Nothing moved", Amber,
                        r.Errors.Count > 0 ? r.Errors.ToArray() : new string[] { "Nothing suitable is selected." });
                    SetStatus("Nothing moved", Amber);
                    return;
                }

                List<string> lines = new List<string>();
                lines.Add(r.Moved + " primitive(s) moved to " + opt.TargetLayer);
                lines.Add(r.ViasPlaced + " via(s) placed to keep connections intact");
                foreach (string n in r.Notes) lines.Add(n);
                foreach (string e in r.Errors) lines.Add(e);

                ShowResult("Moved to layer", Green, lines.ToArray());
                SetStatus(r.Moved + " moved, " + r.ViasPlaced + " vias", Green);
            }
            catch (Exception ex) { Fail("Move to layer", ex); }
        }

        private void DoExportStack()
        {
            try
            {
                IPCB_ServerInterface s; IPCB_Board b;
                if (!Board(out s, out b)) return;

                string folder = EnsureOutputFolder();
                if (folder == null) { SetStatus("Cancelled", TextDim); return; }

                Layers.Result r = Layers.ExportStack(s, b, folder);

                List<string> lines = new List<string>();
                foreach (string n in r.Notes) lines.Add(n);
                if (r.CsvPath.Length > 0) lines.Add(r.CsvPath);
                foreach (string e in r.Errors) lines.Add(e);

                ShowResult("Layer stack exported", r.Errors.Count > 0 ? Amber : Green, lines.ToArray());
                SetStatus("layer_stack.csv written", r.Errors.Count > 0 ? Amber : Green);
            }
            catch (Exception ex) { Fail("Export stack", ex); }
        }

        private void DoVisibility(bool show)
        {
            try
            {
                IPCB_ServerInterface s; IPCB_Board b;
                if (!Board(out s, out b)) return;

                Layers.Result r = Layers.Visibility(s, b, show, true);

                List<string> lines = new List<string>();
                lines.Add(r.Considered + " signal layer(s) " + (show ? "shown" : "hidden"));
                foreach (string e in r.Errors) lines.Add(e);

                ShowResult(show ? "Layers shown" : "Layers hidden", Green, lines.ToArray());
                SetStatus(r.Considered + " layers " + (show ? "shown" : "hidden"), Green);
            }
            catch (Exception ex) { Fail("Layer visibility", ex); }
        }

        // ---------------- variant handler ----------------

        private void DoVariantReport()
        {
            try
            {
                IPCB_ServerInterface s; IPCB_Board b;
                if (!Board(out s, out b)) return;

                string folder = EnsureOutputFolder();
                if (folder == null) { SetStatus("Cancelled", TextDim); return; }

                Variants.Result r = Variants.Report(b, folder);

                List<string> lines = new List<string>();
                if (r.VariantCount == 0) lines.Add("No variants found on this board.");
                else lines.Add(r.VariantCount + " variant(s), " + r.Rows + " component row(s)");
                foreach (string x in r.Summary) lines.Add(x);
                foreach (string n in r.Notes) lines.Add(n);
                if (r.CsvPath.Length > 0) lines.Add(r.CsvPath);
                foreach (string e in r.Errors) lines.Add(e);

                bool good = r.Errors.Count == 0;
                ShowResult(r.VariantCount > 0 ? "Variant report exported" : "No variants",
                           good ? Green : Amber, lines.ToArray());
                SetStatus(r.VariantCount + " variants", good ? Green : Amber);
            }
            catch (Exception ex) { Fail("Variant report", ex); }
        }

        private void Fail(string what, Exception ex)
        {
            Log.Exception("SpikeWindow." + what, ex);
            ShowResult(what + " failed", Red, ex.GetType().Name + " — " + ex.Message);
            SetStatus(what + " failed", Red);
        }

        // ---------------- design rule handlers ----------------

        private void DoExportRules()
        {
            try
            {
                IPCB_ServerInterface pcbServer;
                IPCB_Board board;
                if (!TryGetBoard(out pcbServer, out board))
                { SetStatus("No active PCB document", Amber); return; }

                string folder = EnsureOutputFolder();
                if (folder == null) { SetStatus("Cancelled", TextDim); return; }

                DesignRules.Result r = DesignRules.Export(board, folder);

                List<string> lines = new List<string>();
                lines.Add(r.Rules + " rule(s) exported");
                if (r.Disabled > 0) lines.Add(r.Disabled + " of them are disabled");
                if (r.CsvPath.Length > 0) lines.Add(r.CsvPath);
                foreach (string e in r.Errors) lines.Add(e);

                ShowResult("Design rules exported", Green, lines.ToArray());
                SetStatus(r.Rules + " rules written", Green);
            }
            catch (Exception ex)
            {
                Log.Exception("SpikeWindow.DoExportRules", ex);
                ShowResult("Rule export failed", Red, ex.GetType().Name + " — " + ex.Message);
                SetStatus("Rule export failed", Red);
            }
        }

        private void DoAuditRules()
        {
            try
            {
                IPCB_ServerInterface pcbServer;
                IPCB_Board board;
                if (!TryGetBoard(out pcbServer, out board))
                { SetStatus("No active PCB document", Amber); return; }

                DesignRules.Result r = DesignRules.Audit(board);

                List<string> lines = new List<string>(r.Findings);
                foreach (string e in r.Errors) lines.Add(e);

                bool clean = r.Disabled == 0 && r.Unscoped == 0;
                ShowResult(clean ? "Rules look healthy" : "Rule audit findings",
                           clean ? Green : Amber, lines.ToArray());
                SetStatus(clean ? r.Rules + " rules, nothing inert"
                                : r.Disabled + " disabled, " + r.Unscoped + " unscoped",
                          clean ? Green : Amber);
            }
            catch (Exception ex)
            {
                Log.Exception("SpikeWindow.DoAuditRules", ex);
                ShowResult("Rule audit failed", Red, ex.GetType().Name + " — " + ex.Message);
                SetStatus("Rule audit failed", Red);
            }
        }

        // ---------------- testpoint handlers ----------------

        private void DoTpCoverage()
        {
            try
            {
                IPCB_ServerInterface pcbServer;
                IPCB_Board board;
                if (!TryGetBoard(out pcbServer, out board))
                { SetStatus("No active PCB document", Amber); return; }

                string folder = EnsureOutputFolder();
                if (folder == null) { SetStatus("Cancelled", TextDim); return; }

                Testpoints.Result r = Testpoints.Coverage(board, folder);

                List<string> lines = new List<string>();
                lines.Add(r.Covered + " of " + r.Nets + " net(s) have a fabrication testpoint");
                if (r.Uncovered > 0) lines.Add(r.Uncovered + " net(s) have none");
                foreach (string n in r.Notes) lines.Add(n);
                if (r.CsvPath.Length > 0) lines.Add(r.CsvPath);
                foreach (string e in r.Errors) lines.Add(e);

                ShowResult("Testpoint coverage exported", r.Uncovered > 0 ? Amber : Green, lines.ToArray());
                SetStatus(r.Covered + "/" + r.Nets + " nets covered", r.Uncovered > 0 ? Amber : Green);
            }
            catch (Exception ex)
            {
                Log.Exception("SpikeWindow.DoTpCoverage", ex);
                ShowResult("Coverage report failed", Red, ex.GetType().Name + " — " + ex.Message);
                SetStatus("Coverage report failed", Red);
            }
        }

        private void DoTpAssign()
        {
            try
            {
                IPCB_ServerInterface pcbServer;
                IPCB_Board board;
                if (!TryGetBoard(out pcbServer, out board))
                { SetStatus("No active PCB document", Amber); return; }

                Testpoints.AssignOptions opt = new Testpoints.AssignOptions();
                opt.NetClass = (tpClass.Text ?? "").Trim();
                opt.IncludePads = tpPads.IsChecked == true;
                opt.Assembly = tpAssembly.IsChecked == true;
                opt.OnlyUncovered = tpUncovered.IsChecked == true;

                Settings.SetValue("TpClass", opt.NetClass);
                Settings.SetValue("TpPads", opt.IncludePads ? "1" : "0");
                Settings.SetValue("TpAssembly", opt.Assembly ? "1" : "0");
                Settings.SetValue("TpUncovered", opt.OnlyUncovered ? "1" : "0");

                Testpoints.Result r = Testpoints.Assign(pcbServer, board, opt);

                if (r.Errors.Count > 0 && r.Changed == 0)
                {
                    ShowResult("Nothing assigned", Amber, r.Errors.ToArray());
                    SetStatus("Nothing assigned", Amber);
                    return;
                }

                List<string> lines = new List<string>();
                lines.Add(r.Changed + " " + (opt.Assembly ? "assembly" : "fabrication") +
                          " testpoint(s) assigned from " + r.Scanned + " candidate(s)");
                if (opt.OnlyUncovered) lines.Add("one per net; nets that already had one were skipped");
                foreach (string e in r.Errors) lines.Add(e);

                ShowResult(r.Changed > 0 ? "Testpoints assigned" : "Nothing to assign",
                           r.Changed > 0 ? Green : Amber, lines.ToArray());
                SetStatus(r.Changed + " testpoints assigned", r.Changed > 0 ? Green : Amber);
            }
            catch (Exception ex)
            {
                Log.Exception("SpikeWindow.DoTpAssign", ex);
                ShowResult("Assign failed", Red, ex.GetType().Name + " — " + ex.Message);
                SetStatus("Assign failed", Red);
            }
        }

        private void DoPadCentres()
        {
            try
            {
                IPCB_ServerInterface pcbServer;
                IPCB_Board board;
                if (!TryGetBoard(out pcbServer, out board))
                { SetStatus("No active PCB document", Amber); return; }

                double tol;
                if (!TryMM(pcTol.Text, out tol) || tol <= 0.0)
                { Complain("Tolerance must be a number greater than 0."); return; }
                Settings.SetValue("PcTol", pcTol.Text.Trim());

                Testpoints.Result r = Testpoints.PadCentres(pcbServer, board, tol);

                List<string> lines = new List<string>();
                lines.Add(r.Found + " pad(s) of " + r.Scanned + " have nothing at the centre");
                if (r.Found > 0) lines.Add("selected on the board");
                foreach (string n in r.Notes) lines.Add(n);
                foreach (string e in r.Errors) lines.Add(e);

                ShowResult(r.Found == 0 ? "All pad centres fed" : "Pad centres to check",
                           r.Found == 0 ? Green : Amber, lines.ToArray());
                SetStatus(r.Found == 0 ? "All pad centres fed" : r.Found + " pads to check",
                          r.Found == 0 ? Green : Amber);
            }
            catch (Exception ex)
            {
                Log.Exception("SpikeWindow.DoPadCentres", ex);
                ShowResult("Pad centre check failed", Red, ex.GetType().Name + " — " + ex.Message);
                SetStatus("Pad centre check failed", Red);
            }
        }

        // ---------------- cleanup handlers ----------------

        private void DoInvalidObjects(bool delete)
        {
            try
            {
                IPCB_ServerInterface pcbServer;
                IPCB_Board board;
                if (!TryGetBoard(out pcbServer, out board))
                { SetStatus("No active PCB document", Amber); return; }

                Cleanup.Result r = Cleanup.InvalidObjects(pcbServer, board, delete);

                if (r.Found == 0)
                {
                    ShowResult("Nothing invalid", Green,
                        r.Scanned + " polygon(s) and region(s) checked — all valid.");
                    SetStatus("No invalid objects", Green);
                    return;
                }

                List<string> lines = new List<string>();
                lines.Add(r.Found + " invalid object(s) of " + r.Scanned + " checked");
                if (delete) lines.Add(r.Removed + " removed");
                else lines.Add("selected on the board — nothing was deleted");
                foreach (string e in r.Errors) lines.Add(e);

                ShowResult(delete ? "Invalid objects removed" : "Invalid objects found",
                           delete ? Green : Amber, lines.ToArray());
                SetStatus(delete ? r.Removed + " removed" : r.Found + " found", delete ? Green : Amber);
            }
            catch (Exception ex)
            {
                Log.Exception("SpikeWindow.DoInvalidObjects", ex);
                ShowResult("Invalid object check failed", Red, ex.GetType().Name + " — " + ex.Message);
                SetStatus("Invalid object check failed", Red);
            }
        }

        private void DoViaAntennas()
        {
            try
            {
                IPCB_ServerInterface pcbServer;
                IPCB_Board board;
                if (!TryGetBoard(out pcbServer, out board))
                { SetStatus("No active PCB document", Amber); return; }

                Cleanup.AntennaOptions opt = new Cleanup.AntennaOptions();
                if (!TryMM(clTol.Text, out opt.ToleranceMM) || opt.ToleranceMM <= 0.0)
                { Complain("Tolerance must be a number greater than 0."); return; }
                opt.IgnoreInPours = clPours.IsChecked == true;

                Settings.SetValue("ClTol", clTol.Text.Trim());
                Settings.SetValue("ClPours", opt.IgnoreInPours ? "1" : "0");

                Cleanup.Result r = Cleanup.ViaAntennas(pcbServer, board, opt);

                List<string> lines = new List<string>();
                lines.Add(r.Found + " antenna via(s) of " + r.Scanned + " checked");
                if (r.Found > 0) lines.Add("selected on the board");
                foreach (string n in r.Notes) lines.Add(n);
                foreach (string e in r.Errors) lines.Add(e);

                ShowResult(r.Found == 0 ? "No via antennas" : "Via antennas found",
                           r.Found == 0 ? Green : Amber, lines.ToArray());
                SetStatus(r.Found == 0 ? "No via antennas" : r.Found + " antenna vias", r.Found == 0 ? Green : Amber);
            }
            catch (Exception ex)
            {
                Log.Exception("SpikeWindow.DoViaAntennas", ex);
                ShowResult("Antenna check failed", Red, ex.GetType().Name + " — " + ex.Message);
                SetStatus("Antenna check failed", Red);
            }
        }

        private void DoDangling()
        {
            try
            {
                IPCB_ServerInterface pcbServer;
                IPCB_Board board;
                if (!TryGetBoard(out pcbServer, out board))
                { SetStatus("No active PCB document", Amber); return; }

                double tol;
                if (!TryMM(dcTol.Text, out tol) || tol <= 0.0)
                { Complain("Tolerance must be a number greater than 0."); return; }
                Settings.SetValue("DcTol", dcTol.Text.Trim());

                Cleanup.Result r = Cleanup.DanglingCopper(pcbServer, board, tol);

                List<string> lines = new List<string>();
                lines.Add(r.Found + " primitive(s) with a free end, from " + r.Scanned + " endpoint(s)");
                if (r.Found > 0) lines.Add("selected on the board");
                lines.Add("a track ending part-way along another is a real connection and is not reported");
                foreach (string e in r.Errors) lines.Add(e);

                ShowResult(r.Found == 0 ? "No dangling copper" : "Dangling copper found",
                           r.Found == 0 ? Green : Amber, lines.ToArray());
                SetStatus(r.Found == 0 ? "No dangling ends" : r.Found + " dangling", r.Found == 0 ? Green : Amber);
            }
            catch (Exception ex)
            {
                Log.Exception("SpikeWindow.DoDangling", ex);
                ShowResult("Dangling check failed", Red, ex.GetType().Name + " — " + ex.Message);
                SetStatus("Dangling check failed", Red);
            }
        }

        private void DoSinglePinNets()
        {
            try
            {
                IPCB_ServerInterface s; IPCB_Board b;
                if (!Board(out s, out b)) return;

                string folder = EnsureOutputFolder();
                if (folder == null) { SetStatus("Cancelled", TextDim); return; }

                NetTools.Result r = NetTools.SinglePinNets(s, b, folder, true);

                List<string> lines = new List<string>();
                lines.Add(r.Scanned + " net(s) scanned");
                foreach (string n in r.Notes) lines.Add(n);
                if (r.CsvPath.Length > 0) lines.Add(r.CsvPath);
                foreach (string e in r.Errors) lines.Add(e);

                ShowResult(r.Found == 0 ? "Every net reaches two pins" : "Single-pin nets found",
                           r.Found == 0 ? Green : Amber, lines.ToArray());
                SetStatus(r.Found == 0 ? "No single-pin nets" : r.Found + " single-pin nets",
                          r.Found == 0 ? Green : Amber);
            }
            catch (Exception ex) { Fail("Single-pin nets", ex); }
        }

        private void DoDuplicates(bool delete)
        {
            try
            {
                IPCB_ServerInterface s; IPCB_Board b;
                if (!Board(out s, out b)) return;

                double tol;
                if (!TryMM(dupTol.Text, out tol) || tol <= 0.0)
                { Complain("Tolerance must be a number greater than 0."); return; }
                Settings.SetValue("DupTol", dupTol.Text.Trim());

                NetTools.Result r = NetTools.DuplicateTracks(s, b, tol, delete);

                List<string> lines = new List<string>();
                lines.Add(r.Scanned + " track(s) scanned, " + r.Found + " duplicate(s)");
                foreach (string n in r.Notes) lines.Add(n);
                foreach (string e in r.Errors) lines.Add(e);

                ShowResult(r.Found == 0 ? "No duplicate tracks"
                                        : (delete ? "Duplicates removed" : "Duplicates found"),
                           r.Found == 0 ? Green : (delete ? Green : Amber), lines.ToArray());
                SetStatus(r.Found == 0 ? "No duplicates"
                                       : (delete ? r.Removed + " removed" : r.Found + " found"),
                          r.Found == 0 ? Green : Amber);
            }
            catch (Exception ex) { Fail("Duplicate tracks", ex); }
        }

        private void DoLockRouting(bool lockIt)
        {
            try
            {
                IPCB_ServerInterface s; IPCB_Board b;
                if (!Board(out s, out b)) return;

                NetTools.LockOptions o = new NetTools.LockOptions();
                o.NetFilter = (lrNet.Text ?? "").Trim();
                o.Lock = lockIt;
                o.IncludeVias = lrVias.IsChecked == true;
                o.OnlySelection = lrSel.IsChecked == true;

                Settings.SetValue("LrNet", o.NetFilter);
                Settings.SetValue("LrVias", o.IncludeVias ? "1" : "0");
                Settings.SetValue("LrSel", o.OnlySelection ? "1" : "0");

                NetTools.Result r = NetTools.LockRouting(s, b, o);

                List<string> lines = new List<string>();
                foreach (string n in r.Notes) lines.Add(n);
                foreach (string e in r.Errors) lines.Add(e);
                if (r.Changed > 0) lines.Add("Moveable is inverted in Altium — locked means Moveable=false.");

                ShowResult(r.Changed > 0 ? (lockIt ? "Routing locked" : "Routing unlocked") : "Nothing matched",
                           r.Changed > 0 ? Green : Amber, lines.ToArray());
                SetStatus(r.Changed + " primitives " + (lockIt ? "locked" : "unlocked"),
                          r.Changed > 0 ? Green : Amber);
            }
            catch (Exception ex) { Fail("Lock routing", ex); }
        }

        private void DoFlip()
        {
            try
            {
                IPCB_ServerInterface s; IPCB_Board b;
                if (!Board(out s, out b)) return;

                Geometry.Result r = Geometry.FlipComponents(s, b);

                if (r.Applied == 0)
                {
                    ShowResult("Nothing flipped", Amber,
                        r.Errors.Count > 0 ? r.Errors.ToArray() : new string[] { "No components selected." });
                    SetStatus("Nothing flipped", Amber);
                    return;
                }

                List<string> lines = new List<string>();
                lines.Add(r.Applied + " component(s) flipped of " + r.Considered + " selected");
                if (r.Skipped > 0) lines.Add(r.Skipped + " skipped");
                foreach (string e in r.Errors) lines.Add(e);

                ShowResult("Components flipped", Green, lines.ToArray());
                SetStatus(r.Applied + " components flipped", Green);
            }
            catch (Exception ex) { Fail("Flip components", ex); }
        }

        private void DoOffBoard()
        {
            try
            {
                IPCB_ServerInterface s; IPCB_Board b;
                if (!Board(out s, out b)) return;

                string folder = EnsureOutputFolder();
                if (folder == null) { SetStatus("Cancelled", TextDim); return; }

                double margin;
                if (!TryMM(obMargin.Text, out margin) || margin < 0.0)
                { Complain("Margin must be a number of 0 or more."); return; }

                bool sel = obSelect.IsChecked == true;
                Settings.SetValue("ObMargin", obMargin.Text.Trim());
                Settings.SetValue("ObSelect", sel ? "1" : "0");

                Placement.Result r = Placement.OffBoard(s, b, margin, folder, sel);

                List<string> lines = new List<string>();
                lines.Add(r.Scanned + " component(s) checked");
                foreach (string n in r.Notes) lines.Add(n);
                if (r.CsvPath.Length > 0) lines.Add(r.CsvPath);
                foreach (string e in r.Errors) lines.Add(e);

                ShowResult(r.Found == 0 ? "Everything is on the board" : "Components past the edge",
                           r.Found == 0 ? Green : Amber, lines.ToArray());
                SetStatus(r.Found == 0 ? "All inside the outline" : r.Found + " off the board",
                          r.Found == 0 ? Green : Amber);
            }
            catch (Exception ex) { Fail("Off-board components", ex); }
        }

        private void DoCollisions()
        {
            try
            {
                IPCB_ServerInterface s; IPCB_Board b;
                if (!Board(out s, out b)) return;

                string folder = EnsureOutputFolder();
                if (folder == null) { SetStatus("Cancelled", TextDim); return; }

                double clear;
                if (!TryMM(colClear.Text, out clear) || clear < 0.0)
                { Complain("Clearance must be a number of 0 or more."); return; }

                bool sel = colSelect.IsChecked == true;
                Settings.SetValue("ColClear", colClear.Text.Trim());
                Settings.SetValue("ColSelect", sel ? "1" : "0");

                Placement.Result r = Placement.Collisions(s, b, clear, folder, sel);

                List<string> lines = new List<string>();
                lines.Add(r.Scanned + " component(s) checked");
                foreach (string n in r.Notes) lines.Add(n);
                if (r.CsvPath.Length > 0) lines.Add(r.CsvPath);
                foreach (string e in r.Errors) lines.Add(e);

                ShowResult(r.Found == 0 ? "No bodies overlap" : "Overlapping bodies",
                           r.Found == 0 ? Green : Amber, lines.ToArray());
                SetStatus(r.Found == 0 ? "No collisions" : r.Found + " pairs",
                          r.Found == 0 ? Green : Amber);
            }
            catch (Exception ex) { Fail("Component collisions", ex); }
        }

        private void DoAlignRotation()
        {
            try
            {
                IPCB_ServerInterface s; IPCB_Board b;
                if (!Board(out s, out b)) return;

                double deg;
                if (!TryMM(rotAngle.Text, out deg))
                { Complain("Rotation must be a number of degrees."); return; }
                Settings.SetValue("RotAngle", rotAngle.Text.Trim());

                Placement.Result r = Placement.AlignRotation(s, b, deg);

                List<string> lines = new List<string>();
                foreach (string n in r.Notes) lines.Add(n);
                foreach (string e in r.Errors) lines.Add(e);

                ShowResult(r.Changed > 0 ? "Rotation aligned" : "Nothing changed",
                           r.Changed > 0 ? Green : Amber, lines.ToArray());
                SetStatus(r.Changed + " components rotated", r.Changed > 0 ? Green : Amber);
            }
            catch (Exception ex) { Fail("Align rotation", ex); }
        }

        private void DoSnapToGrid()
        {
            try
            {
                IPCB_ServerInterface s; IPCB_Board b;
                if (!Board(out s, out b)) return;

                double grid;
                if (!TryMM(snapGrid.Text, out grid) || grid <= 0.0)
                { Complain("Grid must be a number greater than 0."); return; }

                bool sel = snapSel.IsChecked == true;
                bool skipLocked = snapLocked.IsChecked == true;
                Settings.SetValue("SnapGrid", snapGrid.Text.Trim());
                Settings.SetValue("SnapSel", sel ? "1" : "0");
                Settings.SetValue("SnapLocked", skipLocked ? "1" : "0");

                Placement.Result r = Placement.SnapToGrid(s, b, grid, sel, skipLocked);

                List<string> lines = new List<string>();
                foreach (string n in r.Notes) lines.Add(n);
                foreach (string e in r.Errors) lines.Add(e);

                ShowResult(r.Changed > 0 ? "Snapped to grid" : "Nothing moved",
                           r.Changed > 0 ? Green : Amber, lines.ToArray());
                SetStatus(r.Changed + " components snapped", r.Changed > 0 ? Green : Amber);
            }
            catch (Exception ex) { Fail("Snap to grid", ex); }
        }

        private void DoRenumber(bool apply)
        {
            try
            {
                IPCB_ServerInterface s; IPCB_Board b;
                if (!Board(out s, out b)) return;

                string folder = EnsureOutputFolder();
                if (folder == null) { SetStatus("Cancelled", TextDim); return; }

                Placement.RenumberOptions o = new Placement.RenumberOptions();

                double band;
                if (!TryMM(rnRow.Text, out band) || band <= 0.0)
                { Complain("Row band must be a number greater than 0."); return; }
                o.RowHeightMM = band;
                o.OnlySelection = rnSel.IsChecked == true;
                o.TopToBottom = rnTopDown.IsChecked == true;
                o.Apply = apply;

                Settings.SetValue("RnRow", rnRow.Text.Trim());
                Settings.SetValue("RnSel", o.OnlySelection ? "1" : "0");
                Settings.SetValue("RnTopDown", o.TopToBottom ? "1" : "0");

                Placement.Result r = Placement.Renumber(s, b, o, folder);

                List<string> lines = new List<string>();
                foreach (string n in r.Notes) lines.Add(n);
                if (r.CsvPath.Length > 0) lines.Add(r.CsvPath);
                foreach (string e in r.Errors) lines.Add(e);

                if (apply)
                {
                    ShowResult(r.Changed > 0 ? "Designators renumbered" : "Nothing renamed",
                               r.Changed > 0 ? Amber : Green, lines.ToArray());
                    SetStatus(r.Changed + " renamed — schematic now out of step",
                              r.Changed > 0 ? Amber : Green);
                }
                else
                {
                    ShowResult("Proposal written — nothing changed", Green, lines.ToArray());
                    SetStatus(r.Found + " would change", Green);
                }
            }
            catch (Exception ex) { Fail("Renumber designators", ex); }
        }

        private void DoPolygonReport()
        {
            try
            {
                IPCB_ServerInterface s; IPCB_Board b;
                if (!Board(out s, out b)) return;

                string folder = EnsureOutputFolder();
                if (folder == null) { SetStatus("Cancelled", TextDim); return; }

                Polygons.Result r = Polygons.Report(s, b, folder);

                List<string> lines = new List<string>();
                foreach (string n in r.Notes) lines.Add(n);
                if (r.CsvPath.Length > 0) lines.Add(r.CsvPath);
                foreach (string e in r.Errors) lines.Add(e);

                ShowResult(r.Stale > 0 ? "Stale copper found" : "Polygon report written",
                           r.Stale > 0 ? Amber : Green, lines.ToArray());
                SetStatus(r.Stale > 0 ? r.Stale + " need repouring" : r.Found + " polygons",
                          r.Stale > 0 ? Amber : Green);
            }
            catch (Exception ex) { Fail("Polygon report", ex); }
        }

        private void DoRepour()
        {
            try
            {
                IPCB_ServerInterface s; IPCB_Board b;
                if (!Board(out s, out b)) return;

                bool stale = rpStale.IsChecked == true;
                bool sel = rpSel.IsChecked == true;
                Settings.SetValue("RpStale", stale ? "1" : "0");
                Settings.SetValue("RpSel", sel ? "1" : "0");

                Polygons.Result r = Polygons.Repour(s, b, stale, sel);

                List<string> lines = new List<string>();
                foreach (string n in r.Notes) lines.Add(n);
                foreach (string e in r.Errors) lines.Add(e);

                ShowResult(r.Changed > 0 ? "Polygons repoured" : "Nothing to repour",
                           r.Changed > 0 ? Green : Amber, lines.ToArray());
                SetStatus(r.Changed + " repoured", r.Changed > 0 ? Green : Amber);
            }
            catch (Exception ex) { Fail("Repour polygons", ex); }
        }

        private void DoUnnetted()
        {
            try
            {
                IPCB_ServerInterface s; IPCB_Board b;
                if (!Board(out s, out b)) return;

                string folder = EnsureOutputFolder();
                if (folder == null) { SetStatus("Cancelled", TextDim); return; }

                bool sel = ucSelect.IsChecked == true;
                Settings.SetValue("UcSelect", sel ? "1" : "0");

                Connectivity.CacheCopperLayers(s, b);
                Connectivity.Result r = Connectivity.UnnettedCopper(s, b, folder, sel);

                List<string> lines = new List<string>();
                foreach (string n in r.Notes) lines.Add(n);
                if (r.CsvPath.Length > 0) lines.Add(r.CsvPath);
                foreach (string e in r.Errors) lines.Add(e);

                ShowResult(r.Found == 0 ? "All copper is on a net" : "Unnetted copper found",
                           r.Found == 0 ? Green : Amber, lines.ToArray());
                SetStatus(r.Found == 0 ? "All copper netted" : r.Found + " unnetted primitives",
                          r.Found == 0 ? Green : Amber);
            }
            catch (Exception ex) { Fail("Unnetted copper", ex); }
        }

        private void DoMeasureNets()
        {
            try
            {
                IPCB_ServerInterface s; IPCB_Board b;
                if (!Board(out s, out b)) return;

                string folder = EnsureOutputFolder();
                if (folder == null) { SetStatus("Cancelled", TextDim); return; }

                Connectivity.CacheCopperLayers(s, b);
                Connectivity.Result r = Connectivity.MeasureNets(s, b, folder);

                List<string> lines = new List<string>();
                foreach (string n in r.Notes) lines.Add(n);
                if (r.CsvPath.Length > 0) lines.Add(r.CsvPath);
                foreach (string e in r.Errors) lines.Add(e);

                ShowResult("Measured net lengths written", Green, lines.ToArray());
                SetStatus(r.Found + " nets measured", Green);
            }
            catch (Exception ex) { Fail("Measure net lengths", ex); }
        }

        private void DoSilkOverPads()
        {
            try
            {
                IPCB_ServerInterface s; IPCB_Board b;
                if (!Board(out s, out b)) return;

                string folder = EnsureOutputFolder();
                if (folder == null) { SetStatus("Cancelled", TextDim); return; }

                bool sel = sopSelect.IsChecked == true;
                Settings.SetValue("SopSelect", sel ? "1" : "0");

                DfmTools.Result r = DfmTools.SilkOverPads(s, b, folder, sel);

                List<string> lines = new List<string>();
                lines.Add(r.Scanned + " overlay primitive(s) checked");
                foreach (string n in r.Notes) lines.Add(n);
                if (r.CsvPath.Length > 0) lines.Add(r.CsvPath);
                foreach (string e in r.Errors) lines.Add(e);

                ShowResult(r.Found == 0 ? "No silkscreen on pads" : "Silkscreen clashes found",
                           r.Found == 0 ? Green : Amber, lines.ToArray());
                SetStatus(r.Found == 0 ? "Silkscreen clear" : r.Found + " clashes",
                          r.Found == 0 ? Green : Amber);
            }
            catch (Exception ex) { Fail("Silkscreen over pads", ex); }
        }

        private void DoPasteGrid()
        {
            try
            {
                IPCB_ServerInterface s; IPCB_Board b;
                if (!Board(out s, out b)) return;

                DfmTools.PasteOptions o = new DfmTools.PasteOptions();

                double min;
                if (!TryMM(pgMin.Text, out min) || min <= 0.0)
                { Complain("Minimum pad size must be a number greater than 0."); return; }
                o.MinPadMM = min;

                double cov;
                if (!TryMM(pgCoverage.Text, out cov) || cov <= 0.0 || cov >= 100.0)
                { Complain("Coverage must be between 0 and 100 percent."); return; }
                o.CoveragePercent = cov;

                double div;
                if (!TryMM(pgDiv.Text, out div) || div < 2.0 || div > 10.0 || div != Math.Floor(div))
                { Complain("Divisions must be a whole number between 2 and 10."); return; }
                o.Divisions = (int)div;

                o.OnlySelection = pgSel.IsChecked == true;

                Settings.SetValue("PgMin", pgMin.Text.Trim());
                Settings.SetValue("PgCoverage", pgCoverage.Text.Trim());
                Settings.SetValue("PgDiv", pgDiv.Text.Trim());
                Settings.SetValue("PgSel", o.OnlySelection ? "1" : "0");

                DfmTools.Result r = DfmTools.PasteGrid(s, b, o);

                List<string> lines = new List<string>();
                lines.Add(r.Scanned + " pad(s) scanned, " + r.Found + " large enough");
                foreach (string n in r.Notes) lines.Add(n);
                foreach (string e in r.Errors) lines.Add(e);
                if (r.Changed > 0)
                    lines.Add("Paste expansion was flagged manual on each pad, so the paste rule will not " +
                              "overwrite it.");

                ShowResult(r.Changed > 0 ? "Paste grids built" : "Nothing changed",
                           r.Changed > 0 ? Green : Amber, lines.ToArray());
                SetStatus(r.Changed + " pads gridded", r.Changed > 0 ? Green : Amber);
            }
            catch (Exception ex) { Fail("Paste grid", ex); }
        }

        private void DoNetClassReport()
        {
            try
            {
                IPCB_ServerInterface s; IPCB_Board b;
                if (!Board(out s, out b)) return;

                string folder = EnsureOutputFolder();
                if (folder == null) { SetStatus("Cancelled", TextDim); return; }

                DfmTools.Result r = DfmTools.NetClassReport(b, folder);

                List<string> lines = new List<string>();
                foreach (string n in r.Notes) lines.Add(n);
                if (r.CsvPath.Length > 0) lines.Add(r.CsvPath);
                foreach (string e in r.Errors) lines.Add(e);

                ShowResult("Net class report written", r.Errors.Count == 0 ? Green : Amber, lines.ToArray());
                SetStatus(r.Found + " classes", Green);
            }
            catch (Exception ex) { Fail("Net class report", ex); }
        }

        private void DoMechLayerNames()
        {
            try
            {
                IPCB_ServerInterface s; IPCB_Board b;
                if (!Board(out s, out b)) return;

                string folder = EnsureOutputFolder();
                if (folder == null) { SetStatus("Cancelled", TextDim); return; }

                DfmTools.Result r = DfmTools.MechLayerNames(s, b, folder);

                List<string> lines = new List<string>();
                foreach (string n in r.Notes) lines.Add(n);
                if (r.CsvPath.Length > 0) lines.Add(r.CsvPath);
                foreach (string e in r.Errors) lines.Add(e);

                ShowResult("Mechanical layer map written", r.Errors.Count == 0 ? Green : Amber, lines.ToArray());
                SetStatus(r.Found + " layers in use", Green);
            }
            catch (Exception ex) { Fail("Mechanical layer map", ex); }
        }

        private void DoSelfTest(bool includeModifying)
        {
            try
            {
                IPCB_ServerInterface s; IPCB_Board b;
                if (!Board(out s, out b)) return;

                string folder = EnsureOutputFolder();
                if (folder == null) { SetStatus("Cancelled", TextDim); return; }

                SetStatus("Running self-test…", Amber);
                SelfTest.Report rep = SelfTest.Run(client, s, b, folder, includeModifying);

                List<string> lines = new List<string>();
                lines.Add(rep.Headline());
                foreach (SelfTest.Check c in rep.Checks)
                    if (c.Verdict == SelfTest.Verdict.Fail)
                        lines.Add("FAILED — " + c.Name + ": " + c.Actual);
                if (rep.Path.Length > 0) lines.Add(rep.Path);

                ShowResult(rep.Failed == 0 ? "Self-test passed" : "Self-test found failures",
                           rep.Failed == 0 ? Green : Red, lines.ToArray());
                SetStatus(rep.Headline(), rep.Failed == 0 ? Green : Red);
            }
            catch (Exception ex) { Fail("Self-test", ex); }
        }

        // ---------------- schematic placement ----------------

        private UIElement BuildSchPlacementSection()
        {
            StackPanel fields = new StackPanel();
            fields.Children.Add(PathRow("Components CSV", Settings.GetValue("SpComps", ""),
                                        "Components CSV: Designator, LCSC or LibRef, optional Library, X, Y, Rotation, Mirror",
                                        "Choose the components CSV", "CSV files (*.csv)|*.csv|All files (*.*)|*.*", out spComps));
            fields.Children.Add(PathRow("Nets CSV (optional)", Settings.GetValue("SpNets", ""),
                                        "Nets CSV: Net, Designator, Pin -- or Net, Node such as R1.2",
                                        "Choose the nets CSV", "CSV files (*.csv)|*.csv|All files (*.*)|*.*", out spNets));
            fields.Children.Add(PathRow("Symbol library", Settings.GetValue("SpLib", SchPlacement.DefaultYouEdaLibrary()),
                                        "Default schematic library, used for rows with no Library column",
                                        "Choose the symbol library", "Schematic libraries (*.SchLib;*.IntLib)|*.SchLib;*.IntLib|All files (*.*)|*.*",
                                        out spLib));

            UIElement f0 = LabeledField("Stub mil", Settings.GetValue("SpStub", "300"),
                                        "Length of the wire stub drawn from each connected pin, in mil", out spStub);
            UIElement f1 = LabeledField("Auto pitch mil", Settings.GetValue("SpPitch", "1500"),
                                        "Grid spacing for components with no X and Y, in mil", out spPitch);
            UIElement f2 = LabeledField("Auto columns", Settings.GetValue("SpCols", "8"),
                                        "Components per row when laying out automatically", out spCols);
            Grid row = FieldRow(f0, f1, f2);
            row.Margin = new Thickness(0, 11, 0, 0);
            fields.Children.Add(row);

            DockPanel actions = new DockPanel();
            actions.LastChildFill = false;

            Button go = PrimaryButton("Place on schematic", 32);
            go.MinWidth = 160;
            go.Click += delegate { DoSchPlacement(false); };
            DockPanel.SetDock(go, Dock.Right);
            actions.Children.Add(go);

            Button dry = SecondaryButton("Dry run", 32, 12);
            dry.MinWidth = 90;
            dry.Margin = new Thickness(0, 0, 8, 0);
            dry.Click += delegate { DoSchPlacement(true); };
            DockPanel.SetDock(dry, Dock.Right);
            actions.Children.Add(dry);

            Button test = SecondaryButton("Schematic self-test", 32, 12);
            test.MinWidth = 150;
            test.Click += delegate { DoSchSelfTest(); };
            DockPanel.SetDock(test, Dock.Left);
            actions.Children.Add(test);

            return Stack(ToolCard("Place components and nets", "MODIFIES SCHEMATIC", Hex("#3D3527"), Hex("#5F5130"), Hex("#F0C477"),
                "Places the components in a CSV onto the focused schematic sheet, then connects them from a nets " +
                "CSV with a short wire stub and a net label on each pin. Symbols come from YouEDA's youeda.SchLib " +
                "(LCSC numbers are mapped through its family-classification files) or from a Library column. " +
                "Both CSVs are checked in full before anything is placed; components already on the sheet are " +
                "skipped. X and Y are in mil unless the header says mm. Nothing is saved.",
                fields, actions));
        }

        private void DoSchPlacement(bool dryRun)
        {
            try
            {
                SchPlacement.Options o = new SchPlacement.Options();
                o.ComponentsCsv = (spComps.Text ?? "").Trim();
                o.NetsCsv = (spNets.Text ?? "").Trim();
                o.DefaultLibrary = (spLib.Text ?? "").Trim();

                if (o.ComponentsCsv.Length == 0 || !System.IO.File.Exists(o.ComponentsCsv))
                { Complain("Choose the components CSV."); return; }
                if (o.NetsCsv.Length > 0 && !System.IO.File.Exists(o.NetsCsv))
                { Complain("The nets CSV does not exist: " + o.NetsCsv); return; }

                double stub, pitch, cols;
                if (!TryMM(spStub.Text, out stub) || stub <= 0.0)
                { Complain("Stub must be a length in mil greater than 0."); return; }
                if (!TryMM(spPitch.Text, out pitch) || pitch <= 0.0)
                { Complain("Auto pitch must be a length in mil greater than 0."); return; }
                if (!TryMM(spCols.Text, out cols) || cols < 1.0 || cols != Math.Floor(cols))
                { Complain("Auto columns must be a whole number of at least 1."); return; }
                o.StubMil = stub;
                o.AutoPitchMil = pitch;
                o.AutoColumns = (int)cols;
                o.DryRun = dryRun;

                Settings.SetValue("SpComps", o.ComponentsCsv);
                Settings.SetValue("SpNets", o.NetsCsv);
                Settings.SetValue("SpLib", o.DefaultLibrary);
                Settings.SetValue("SpStub", spStub.Text.Trim());
                Settings.SetValue("SpPitch", spPitch.Text.Trim());
                Settings.SetValue("SpCols", spCols.Text.Trim());

                string folder = EnsureOutputFolder();
                if (folder == null) { SetStatus("Cancelled", TextDim); return; }
                o.OutputFolder = folder;

                SetStatus(dryRun ? "Checking the CSVs…" : "Placing on the schematic…", Amber);
                SchPlacement.Result r = SchPlacement.Run(client, o);

                List<string> lines = new List<string>();
                foreach (string n in r.Notes) lines.Add(n);
                int shown = 0, cap = 30;
                foreach (string e in r.Errors) { if (shown++ < cap) lines.Add("ERROR — " + e); }
                foreach (string w in r.Warnings) { if (shown++ < cap) lines.Add("warning — " + w); }
                if (shown > cap) lines.Add("… and " + (shown - cap) + " more; see the report CSVs");
                if (r.PartsCsvPath.Length > 0) lines.Add(r.PartsCsvPath);
                if (r.LabelsCsvPath.Length > 0) lines.Add(r.LabelsCsvPath);

                if (r.Refused)
                {
                    ShowResult("Nothing placed", Red, lines.ToArray());
                    SetStatus(r.Errors.Count + " problem(s) to fix before placing", Red);
                }
                else if (dryRun)
                {
                    ShowResult("Dry run", r.Warnings.Count > 0 ? Amber : Green, lines.ToArray());
                    SetStatus("Dry run — the sheet was not changed", Amber);
                }
                else
                {
                    bool clean = r.Errors.Count == 0;
                    ShowResult(clean ? "Placed on the schematic" : "Placed, with problems", clean ? Green : Amber, lines.ToArray());
                    SetStatus(r.PartsPlaced + " components placed, " + r.Labelled + " pins labelled" +
                              (clean ? "" : ", " + r.Errors.Count + " problem(s)"), clean ? Green : Amber);
                }
            }
            catch (Exception ex) { Fail("Schematic placement", ex); }
        }

        private void DoSchSelfTest()
        {
            try
            {
                string lib = (spLib.Text ?? "").Trim();
                if (lib.Length == 0 || !System.IO.File.Exists(lib))
                { Complain("Set the symbol library first -- the self-test places one of its symbols."); return; }
                Settings.SetValue("SpLib", lib);

                string folder = EnsureOutputFolder();
                if (folder == null) { SetStatus("Cancelled", TextDim); return; }

                SetStatus("Running the schematic self-test…", Amber);
                SelfTest.Report rep = SelfTest.RunSchematic(client, folder, lib);

                List<string> lines = new List<string>();
                lines.Add(rep.Headline());
                foreach (SelfTest.Check c in rep.Checks)
                    if (c.Verdict == SelfTest.Verdict.Fail || c.Verdict == SelfTest.Verdict.Info)
                        lines.Add((c.Verdict == SelfTest.Verdict.Fail ? "FAILED — " : "note — ") + c.Name + ": " + c.Actual);
                if (rep.Path.Length > 0) lines.Add(rep.Path);

                ShowResult(rep.Failed == 0 ? "Schematic self-test passed" : "Schematic self-test found failures",
                           rep.Failed == 0 ? Green : Red, lines.ToArray());
                SetStatus(rep.Headline(), rep.Failed == 0 ? Green : Red);
            }
            catch (Exception ex) { Fail("Schematic self-test", ex); }
        }

        // ---------------- report handlers ----------------

        private void RunReport(string title, Func<IPCB_ServerInterface, IPCB_Board, string, Reports.Result> run)
        {
            try
            {
                IPCB_ServerInterface pcbServer;
                IPCB_Board board;
                if (!TryGetBoard(out pcbServer, out board))
                { SetStatus("No active PCB document", Amber); return; }

                string folder = EnsureOutputFolder();
                if (folder == null) { SetStatus("Cancelled", TextDim); return; }

                Reports.Result r = run(pcbServer, board, folder);

                List<string> lines = new List<string>();
                foreach (string h in r.Headline) lines.Add(h);
                foreach (string f in r.Files) lines.Add(f);
                foreach (string e in r.Errors) lines.Add(e);

                ShowResult(title, r.Errors.Count > 0 ? Amber : Green, lines.ToArray());
                SetStatus(title, r.Errors.Count > 0 ? Amber : Green);
            }
            catch (Exception ex)
            {
                Log.Exception("SpikeWindow." + title, ex);
                ShowResult(title + " failed", Red, ex.GetType().Name + " — " + ex.Message);
                SetStatus(title + " failed", Red);
            }
        }

        private void DoBoardCensus()
        {
            RunReport("Board census exported",
                delegate (IPCB_ServerInterface s, IPCB_Board b, string f) { return Reports.BoardCensus(s, b, f); });
        }

        private void DoDrillTable()
        {
            RunReport("Drill table exported",
                delegate (IPCB_ServerInterface s, IPCB_Board b, string f) { return Reports.DrillTable(b, f); });
        }

        private void DoUnrouted()
        {
            RunReport("Unrouted report exported",
                delegate (IPCB_ServerInterface s, IPCB_Board b, string f) { return Reports.Unrouted(b, f); });
        }

        // ---------------- via tool handlers ----------------

        private void DoReturnViaCheck()
        {
            try
            {
                IPCB_ServerInterface pcbServer;
                IPCB_Board board;
                if (!TryGetBoard(out pcbServer, out board))
                { SetStatus("No active PCB document", Amber); return; }

                ViaTools.ReturnViaOptions opt = new ViaTools.ReturnViaOptions();
                opt.ReferenceNet = (rvNet.Text ?? "").Trim();
                if (opt.ReferenceNet.Length == 0) { Complain("Enter the reference net, usually GND."); return; }
                if (!TryMM(rvDist.Text, out opt.MaxDistanceMM) || opt.MaxDistanceMM <= 0.0)
                { Complain("Max distance must be a number greater than 0."); return; }

                opt.OnlySelection = rvOnlySel.IsChecked == true;
                opt.SelectOffenders = true;

                if (rvCsv.IsChecked == true)
                {
                    opt.WriteCsvTo = EnsureOutputFolder();
                    if (opt.WriteCsvTo == null) { SetStatus("Cancelled", TextDim); return; }
                }

                Settings.SetValue("RvNet", opt.ReferenceNet);
                Settings.SetValue("RvDist", rvDist.Text.Trim());
                Settings.SetValue("RvOnlySel", opt.OnlySelection ? "1" : "0");
                Settings.SetValue("RvCsv", rvCsv.IsChecked == true ? "1" : "0");

                ViaTools.ReturnViaResult r = ViaTools.ReturnViaCheck(pcbServer, board, opt);

                if (r.ReferenceVias == 0)
                {
                    ShowResult("Nothing to check against", Amber,
                        "No vias found on net " + opt.ReferenceNet + ".",
                        "Check the net name — it is case-insensitive but must match exactly otherwise.");
                    SetStatus("No " + opt.ReferenceNet + " vias found", Amber);
                    return;
                }

                List<string> lines = new List<string>();
                lines.Add(r.SignalViasChecked + " signal via(s) checked against " +
                          r.ReferenceVias + " on " + opt.ReferenceNet);
                if (r.Offenders == 0)
                    lines.Add("every one has a return via within " +
                              opt.MaxDistanceMM.ToString("0.###", CultureInfo.InvariantCulture) + " mm");
                else
                {
                    lines.Add(r.Offenders + " via(s) with no return via in range — selected on the board");
                    lines.Add("worst " + r.WorstDistanceMM.ToString("0.000", CultureInfo.InvariantCulture) +
                              " mm on net " + r.WorstNet);
                }
                if (r.CsvPath.Length > 0) lines.Add(r.CsvPath);
                foreach (string e in r.Errors) lines.Add(e);

                if (r.Offenders == 0)
                {
                    ShowResult("Return vias look good", Green, lines.ToArray());
                    SetStatus("No return-via problems found", Green);
                }
                else
                {
                    ShowResult("Return vias missing", Amber, lines.ToArray());
                    SetStatus(r.Offenders + " vias without a nearby return", Amber);
                }
            }
            catch (Exception ex)
            {
                Log.Exception("SpikeWindow.DoReturnViaCheck", ex);
                ShowResult("Return via check failed", Red, ex.GetType().Name + " — " + ex.Message);
                SetStatus("Return via check failed", Red);
            }
        }

        private void DoTenting(bool tent)
        {
            try
            {
                IPCB_ServerInterface pcbServer;
                IPCB_Board board;
                if (!TryGetBoard(out pcbServer, out board))
                { SetStatus("No active PCB document", Amber); return; }

                ViaTools.TentOptions opt = new ViaTools.TentOptions();
                opt.Tent = tent;
                if (!TryMM(tentOpen.Text, out opt.OpenExpansionMM)) { Complain("Open-by is not a number."); return; }
                if (!TryMM(tentMinHole.Text, out opt.MinHoleMM)) { Complain("Min hole is not a number."); return; }
                if (!TryMM(tentMaxHole.Text, out opt.MaxHoleMM)) { Complain("Max hole is not a number."); return; }
                opt.OnlySelection = tentOnlySel.IsChecked == true;

                if (opt.MinHoleMM > 0.0 && opt.MaxHoleMM > 0.0 && opt.MaxHoleMM < opt.MinHoleMM)
                { Complain("Max hole is smaller than min hole, so nothing can match."); return; }

                Settings.SetValue("TentOpen", tentOpen.Text.Trim());
                Settings.SetValue("TentMinHole", tentMinHole.Text.Trim());
                Settings.SetValue("TentMaxHole", tentMaxHole.Text.Trim());
                Settings.SetValue("TentOnlySel", opt.OnlySelection ? "1" : "0");

                ViaTools.TentResult r = ViaTools.SetTenting(pcbServer, board, opt);

                List<string> lines = new List<string>();
                lines.Add((tent ? "tented " : "opened mask over ") + r.Changed +
                          " via(s) of " + r.Considered + " matching");
                if (r.Changed > 0)
                    lines.Add("expansion set manually, overriding the solder mask rule for these vias");
                foreach (string e in r.Errors) lines.Add(e);

                if (r.Changed == 0)
                {
                    ShowResult("No vias changed", Amber,
                        r.Considered == 0 ? "No vias matched the hole-size filter." : lines[0]);
                    SetStatus("No vias changed", Amber);
                }
                else
                {
                    ShowResult(tent ? "Vias tented" : "Mask opened", Green, lines.ToArray());
                    SetStatus(r.Changed + " vias changed", Green);
                }
            }
            catch (Exception ex)
            {
                Log.Exception("SpikeWindow.DoTenting", ex);
                ShowResult("Tenting failed", Red, ex.GetType().Name + " — " + ex.Message);
                SetStatus("Tenting failed", Red);
            }
        }

        private void DoBarrelRelief()
        {
            try
            {
                IPCB_ServerInterface pcbServer;
                IPCB_Board board;
                if (!TryGetBoard(out pcbServer, out board))
                { SetStatus("No active PCB document", Amber); return; }

                ViaTools.BarrelReliefOptions opt = new ViaTools.BarrelReliefOptions();
                if (!TryMM(brMinHole.Text, out opt.MinHoleMM) || opt.MinHoleMM <= 0.0)
                { Complain("Min hole must be a number greater than 0."); return; }
                if (!TryMM(brRelief.Text, out opt.ReliefMM)) { Complain("Relief is not a number."); return; }
                opt.OnlySelection = brOnlySel.IsChecked == true;

                Settings.SetValue("BrMinHole", brMinHole.Text.Trim());
                Settings.SetValue("BrRelief", brRelief.Text.Trim());
                Settings.SetValue("BrOnlySel", opt.OnlySelection ? "1" : "0");

                ViaTools.BarrelResult r = ViaTools.BarrelRelief(pcbServer, board, opt);

                List<string> lines = new List<string>();
                lines.Add(r.Changed + " via(s) relieved of " + r.Considered + " with a hole ≥ " +
                          opt.MinHoleMM.ToString("0.###", CultureInfo.InvariantCulture) + " mm");
                if (r.Changed > 0)
                    lines.Add("mask opens " + opt.ReliefMM.ToString("0.###", CultureInfo.InvariantCulture) +
                              " mm from the hole edge");
                foreach (string e in r.Errors) lines.Add(e);

                if (r.Changed == 0)
                {
                    ShowResult("No vias changed", Amber, "No vias have a hole that large.");
                    SetStatus("No vias changed", Amber);
                }
                else
                {
                    ShowResult("Barrel relief applied", Green, lines.ToArray());
                    SetStatus(r.Changed + " vias relieved", Green);
                }
            }
            catch (Exception ex)
            {
                Log.Exception("SpikeWindow.DoBarrelRelief", ex);
                ShowResult("Barrel relief failed", Red, ex.GetType().Name + " — " + ex.Message);
                SetStatus("Barrel relief failed", Red);
            }
        }

        // ---------------- copper & current handlers ----------------

        private void DoCurrentCapacity()
        {
            try
            {
                IPCB_ServerInterface pcbServer;
                IPCB_Board board;
                if (!TryGetBoard(out pcbServer, out board))
                { SetStatus("No active PCB document", Amber); return; }

                CopperCurrent.CurrentOptions opt = new CopperCurrent.CurrentOptions();
                if (!TryMM(ccTemp.Text, out opt.TempRiseC) || opt.TempRiseC <= 0.0)
                { Complain("Temperature rise must be a number greater than 0."); return; }
                if (!TryMM(ccTarget.Text, out opt.TargetAmps)) { Complain("Target current is not a number."); return; }
                opt.OnlySelection = ccOnlySel.IsChecked == true;

                opt.OutputFolder = EnsureOutputFolder();
                if (opt.OutputFolder == null) { SetStatus("Cancelled", TextDim); return; }

                Settings.SetValue("CcTemp", ccTemp.Text.Trim());
                Settings.SetValue("CcTarget", ccTarget.Text.Trim());
                Settings.SetValue("CcOnlySel", opt.OnlySelection ? "1" : "0");

                CopperCurrent.CurrentResult r = CopperCurrent.CurrentCapacity(pcbServer, board, opt);

                CultureInfo inv = CultureInfo.InvariantCulture;
                List<string> lines = new List<string>();
                lines.Add(r.NetsReported + " net(s) from " + r.TracksScanned +
                          " copper segment(s) at +" + opt.TempRiseC.ToString("0.#", inv) + " C");
                if (r.WorstNet.Length > 0)
                    lines.Add("lowest capacity " + r.WorstAmps.ToString("0.000", inv) + " A on " + r.WorstNet);
                if (opt.TargetAmps > 0.0)
                    lines.Add(r.Failures + " net(s) below " + opt.TargetAmps.ToString("0.###", inv) +
                              " A" + (r.Failures > 0 ? " — narrowest tracks selected" : ""));
                if (r.UsedDefaultThickness)
                    lines.Add("some layers reported no copper thickness; 1 oz assumed for those");
                if (r.CsvPath.Length > 0) lines.Add(r.CsvPath);
                foreach (string e in r.Errors) lines.Add(e);

                if (r.NetsReported == 0)
                {
                    ShowResult("Nothing to rate", Amber, "No copper tracks found on this board.");
                    SetStatus("Nothing to rate", Amber);
                }
                else if (r.Failures > 0)
                {
                    ShowResult("Nets below target", Amber, lines.ToArray());
                    SetStatus(r.Failures + " nets below " + opt.TargetAmps.ToString("0.###", inv) + " A", Amber);
                }
                else
                {
                    ShowResult("Current capacity rated", Green, lines.ToArray());
                    SetStatus(r.NetsReported + " nets rated", Green);
                }
            }
            catch (Exception ex)
            {
                Log.Exception("SpikeWindow.DoCurrentCapacity", ex);
                ShowResult("Current rating failed", Red, ex.GetType().Name + " — " + ex.Message);
                SetStatus("Current rating failed", Red);
            }
        }

        private void DoCopperAreas()
        {
            try
            {
                IPCB_ServerInterface pcbServer;
                IPCB_Board board;
                if (!TryGetBoard(out pcbServer, out board))
                { SetStatus("No active PCB document", Amber); return; }

                string folder = EnsureOutputFolder();
                if (folder == null) { SetStatus("Cancelled", TextDim); return; }

                CopperCurrent.AreaResult r = CopperCurrent.CopperAreas(pcbServer, board, folder);

                List<string> lines = new List<string>();
                lines.Add(r.Polygons + " polygon(s), " + r.Regions + " region(s)");
                lines.Add(r.TotalAreaMM2.ToString("0.00", CultureInfo.InvariantCulture) + " mm² of pour in total");
                if (r.CsvPath.Length > 0) lines.Add(r.CsvPath);
                foreach (string e in r.Errors) lines.Add(e);

                ShowResult("Copper areas exported", Green, lines.ToArray());
                SetStatus("copper_areas.csv written", Green);
            }
            catch (Exception ex)
            {
                Log.Exception("SpikeWindow.DoCopperAreas", ex);
                ShowResult("Copper area export failed", Red, ex.GetType().Name + " — " + ex.Message);
                SetStatus("Copper area export failed", Red);
            }
        }

        // ---------------- fabrication handlers ----------------

        private void DoStackupTable()
        {
            try
            {
                IPCB_ServerInterface pcbServer;
                IPCB_Board board;
                if (!TryGetBoard(out pcbServer, out board))
                { SetStatus("No active PCB document", Amber); return; }

                StackupTable.Options opt = new StackupTable.Options();
                opt.LayerName = stackLayer.SelectedItem as string;

                if (!TryMM(stackX.Text, out opt.OriginXMM)) { Complain("Origin X is not a number."); return; }
                if (!TryMM(stackY.Text, out opt.OriginYMM)) { Complain("Origin Y is not a number."); return; }
                if (!TryMM(stackTextH.Text, out opt.TextHeightMM) || opt.TextHeightMM <= 0.0)
                { Complain("Text height must be a number greater than 0."); return; }

                opt.ImperialToo = stackImperial.IsChecked == true;
                opt.ReplaceExisting = stackReplace.IsChecked == true;

                Settings.SetValue("StackLayer", opt.LayerName);
                Settings.SetValue("StackX", stackX.Text.Trim());
                Settings.SetValue("StackY", stackY.Text.Trim());
                Settings.SetValue("StackTextH", stackTextH.Text.Trim());
                Settings.SetValue("StackImperial", opt.ImperialToo ? "1" : "0");
                Settings.SetValue("StackReplace", opt.ReplaceExisting ? "1" : "0");

                StackupTable.Result r = StackupTable.Generate(pcbServer, board, opt);

                if (r.Rows == 0)
                {
                    ShowResult("No stackup table drawn", Amber,
                               r.Errors.Count > 0 ? r.Errors.ToArray() : new string[] { "The physical stack is empty." });
                    SetStatus("No stackup table drawn", Amber);
                    return;
                }

                List<string> lines = new List<string>();
                lines.Add(r.Rows + " layer(s) on " + opt.LayerName + ", " + r.PrimitivesDrawn + " primitives");
                lines.Add("finished thickness " + r.BoardThicknessMM.ToString("0.000", CultureInfo.InvariantCulture) +
                          " mm (" + (r.BoardThicknessMM / 0.0254).ToString("0.0", CultureInfo.InvariantCulture) + " mil)");
                lines.Add("table is " + r.WidthMM.ToString("0.0", CultureInfo.InvariantCulture) + " x " +
                          r.HeightMM.ToString("0.0", CultureInfo.InvariantCulture) + " mm from (" +
                          opt.OriginXMM.ToString("0.#", CultureInfo.InvariantCulture) + ", " +
                          opt.OriginYMM.ToString("0.#", CultureInfo.InvariantCulture) + ")");
                if (r.PrimitivesRemoved > 0)
                    lines.Add("replaced a previous table (" + r.PrimitivesRemoved + " primitives removed)");
                foreach (string e in r.Errors) lines.Add(e);

                ShowResult("Stackup table drawn", Green, lines.ToArray());
                SetStatus(r.Rows + " stack layers drawn on " + opt.LayerName, Green);
            }
            catch (Exception ex)
            {
                Log.Exception("SpikeWindow.DoStackupTable", ex);
                ShowResult("Stackup table failed", Red, ex.GetType().Name + " — " + ex.Message);
                SetStatus("Stackup table failed", Red);
            }
        }

        private void DoAssemblyNotes()
        {
            try
            {
                IPCB_ServerInterface pcbServer;
                IPCB_Board board;
                if (!TryGetBoard(out pcbServer, out board))
                { SetStatus("No active PCB document", Amber); return; }

                AssemblyNotes.Options opt = new AssemblyNotes.Options();
                opt.LayerName = notesLayer.SelectedItem as string;

                if (!TryMM(notesX.Text, out opt.OriginXMM)) { Complain("Origin X is not a number."); return; }
                if (!TryMM(notesY.Text, out opt.OriginYMM)) { Complain("Origin Y is not a number."); return; }

                string finish = (notesFinish.Text ?? "").Trim();
                if (finish.Length > 0) opt.SurfaceFinish = finish;

                string ipc = (notesIpc.Text ?? "").Trim();
                if (ipc.Length > 0) opt.IpcClass = ipc;

                opt.ReplaceExisting = notesReplace.IsChecked == true;

                Settings.SetValue("NotesLayer", opt.LayerName);
                Settings.SetValue("NotesX", notesX.Text.Trim());
                Settings.SetValue("NotesY", notesY.Text.Trim());
                Settings.SetValue("NotesFinish", opt.SurfaceFinish);
                Settings.SetValue("NotesIpc", opt.IpcClass);
                Settings.SetValue("NotesReplace", opt.ReplaceExisting ? "1" : "0");

                AssemblyNotes.Result r = AssemblyNotes.Generate(pcbServer, board, opt);

                if (r.NotesWritten == 0)
                {
                    ShowResult("No notes written", Amber,
                               r.Errors.Count > 0 ? r.Errors.ToArray() : new string[] { "Nothing to write." });
                    SetStatus("No notes written", Amber);
                    return;
                }

                CultureInfo inv = CultureInfo.InvariantCulture;
                List<string> lines = new List<string>();
                lines.Add(r.NotesWritten + " note(s) on " + opt.LayerName);
                lines.Add("board " + r.Stats.WidthMM.ToString("0.00", inv) + " x " +
                          r.Stats.HeightMM.ToString("0.00", inv) + " mm, " +
                          r.Stats.SignalLayers + " copper layer(s)");

                // Measured and specified are reported separately on purpose --
                // they answer different questions and a fab quote depends on
                // knowing which is which.
                lines.Add(double.IsNaN(r.Stats.MinTrackMM)
                    ? "narrowest track: none found"
                    : "narrowest track measured " + r.Stats.MinTrackMM.ToString("0.000", inv) + " mm");
                lines.Add(double.IsNaN(r.Stats.MinClearanceMM)
                    ? "min clearance: no enabled rule"
                    : "min clearance per rules " + r.Stats.MinClearanceMM.ToString("0.000", inv) + " mm");
                lines.Add(double.IsNaN(r.Stats.MinHoleMM)
                    ? "smallest hole: none found"
                    : "smallest hole measured " + r.Stats.MinHoleMM.ToString("0.000", inv) + " mm");

                if (r.PrimitivesRemoved > 0)
                    lines.Add("replaced previous notes (" + r.PrimitivesRemoved + " primitives removed)");
                foreach (string e in r.Errors) lines.Add(e);

                ShowResult("Assembly notes written", Green, lines.ToArray());
                SetStatus(r.NotesWritten + " notes on " + opt.LayerName, Green);
            }
            catch (Exception ex)
            {
                Log.Exception("SpikeWindow.DoAssemblyNotes", ex);
                ShowResult("Assembly notes failed", Red, ex.GetType().Name + " — " + ex.Message);
                SetStatus("Assembly notes failed", Red);
            }
        }

        private void DoReleasePackage(bool dryRun)
        {
            try
            {
                ReleaseBundle.Options opt = new ReleaseBundle.Options();
                opt.ProjectName = (relProject.Text ?? "").Trim();
                opt.Revision = (relRev.Text ?? "").Trim();
                opt.OutJobPath = (relOutJob.Text ?? "").Trim();
                opt.OutputFolder = (relOutFolder.Text ?? "").Trim();
                opt.DestinationFolder = (relDest.Text ?? "").Trim();
                opt.GenerateOutputs = relGenerate.IsChecked == true;
                opt.DryRun = dryRun;
                opt.Stamp = DateTime.Now;

                if (opt.ProjectName.Length == 0)
                {
                    // Falling back to the document name beats refusing, and
                    // beats silently shipping an archive called Project_*.zip.
                    opt.ProjectName = CurrentDocumentStem();
                    if (opt.ProjectName.Length == 0) { Complain("Enter a project name."); return; }
                    relProject.Text = opt.ProjectName;
                }

                Settings.SetValue("RelProject", opt.ProjectName);
                Settings.SetValue("RelRev", opt.Revision);
                Settings.SetValue("RelOutJob", opt.OutJobPath);
                Settings.SetValue("RelOutFolder", opt.OutputFolder);
                Settings.SetValue("RelDest", opt.DestinationFolder);
                Settings.SetValue("RelGenerate", opt.GenerateOutputs ? "1" : "0");

                ReleaseBundle.Result r = ReleasePackager.Package(client, opt);

                foreach (string d in r.Diagnostics) Log.Write("ReleasePackager: " + d);

                List<string> lines = new List<string>();
                lines.Add(ReleaseBundle.ArchiveName(opt));
                lines.Add(r.FilesCollected + " file(s), " + ReleaseBundle.Human(r.BytesCollected) +
                          (r.ArchiveBytes > 0 ? " → " + ReleaseBundle.Human(r.ArchiveBytes) + " zipped" : ""));
                if (r.EntriesVerified > 0)
                    lines.Add("archive reopened and verified — " + r.EntriesVerified + " entries");
                if (r.Generated) lines.Add("Output Job was launched before packaging");
                if (r.Skipped.Count > 0)
                    lines.Add(r.Skipped.Count + " file(s) skipped by the extension filter");
                if (r.Ok && !r.WasDryRun) lines.Add("delivered to " + r.ArchivePath);
                foreach (string e in r.Errors) lines.Add(e);

                if (r.WasDryRun)
                {
                    lines.Insert(0, "Nothing was written.");
                    ShowResult("Dry run", Amber, lines.ToArray());
                    SetStatus("Dry run — " + r.FilesCollected + " files would be packaged", Amber);
                }
                else if (r.Ok)
                {
                    ShowResult("Release packaged", Green, lines.ToArray());
                    SetStatus("Delivered " + r.ArchiveName, Green);
                }
                else
                {
                    ShowResult("Release failed", Red, lines.ToArray());
                    SetStatus("Release failed", Red);
                }
            }
            catch (Exception ex)
            {
                Log.Exception("SpikeWindow.DoReleasePackage", ex);
                ShowResult("Release failed", Red, ex.GetType().Name + " — " + ex.Message);
                SetStatus("Release failed", Red);
            }
        }

        // The PCB document's own file name, minus extension, as a project
        // name default.
        private string CurrentDocumentStem()
        {
            try
            {
                IPCB_ServerInterface pcbServer;
                IPCB_Board board;
                if (!TryGetBoard(out pcbServer, out board)) return "";
                string name = board.GetState_FileName();
                if (string.IsNullOrEmpty(name)) return "";
                return System.IO.Path.GetFileNameWithoutExtension(name);
            }
            catch { return ""; }
        }

        private void Complain(string message)
        {
            ShowResult("Check the settings", Amber, message);
            SetStatus(message, Amber);
        }

        private void ShowResult(string title, Brush accent, params string[] lines)
        {
            resultsTitle.Text = title;
            resultsTitle.Foreground = accent;
            resultsBody.Children.Clear();
            foreach (string line in lines)
            {
                if (line == null) continue;
                resultsBody.Children.Add(Wrap(line, 12, TextDim, new Thickness(0, 0, 0, 4)));
            }
            resultsCard.Visibility = Visibility.Visible;
        }

        private void DoBrowseOutput()
        {
            string chosen = Settings.PickOutputFolder("Choose a folder for the exported files");
            if (chosen == null) return;
            Settings.SetOutputFolder(chosen);
            outputFolderBox.Text = chosen;
            SetStatus("Output folder set to " + chosen, Green);
        }

        private string EnsureOutputFolder()
        {
            string folder = (outputFolderBox.Text ?? "").Trim();
            if (folder.Length > 0 && System.IO.Directory.Exists(folder))
            {
                Settings.SetOutputFolder(folder);
                return folder;
            }
            folder = Settings.ResolveOutputFolder();
            if (folder != null) outputFolderBox.Text = folder;
            return folder;
        }

        private void DoExportBoardData()
        {
            try
            {
                IPCB_ServerInterface pcbServer;
                IPCB_Board board;
                if (!TryGetBoard(out pcbServer, out board))
                {
                    SetStatus("No active PCB document", Amber);
                    return;
                }
                string folder = EnsureOutputFolder();
                if (folder == null) { SetStatus("Cancelled", TextDim); return; }

                int f = BoardExport.FootprintSizes(board, folder);
                int p = BoardExport.PadNets(board, folder);
                int g = BoardExport.BoardGeometry(board, folder);

                ShowResult("Board data exported", Green,
                    "footprint_sizes.csv — " + f + " components",
                    "pad_nets.csv — " + p + " pads",
                    "board_geometry.csv — " + g + " rows",
                    folder);
                SetStatus("3 files written to " + folder, Green);
            }
            catch (Exception ex)
            {
                Log.Exception("SpikeWindow.DoExportBoardData", ex);
                ShowResult("Export failed", Red, ex.GetType().Name + " — " + ex.Message);
                SetStatus("Export failed", Red);
            }
        }

        private void DoExportJlc()
        {
            try
            {
                IPCB_ServerInterface pcbServer;
                IPCB_Board board;
                if (!TryGetBoard(out pcbServer, out board))
                {
                    SetStatus("No active PCB document", Amber);
                    return;
                }
                string folder = EnsureOutputFolder();
                if (folder == null) { SetStatus("Cancelled", TextDim); return; }

                JlcExport.JlcResult r = JlcExport.Export(pcbServer, board, folder);

                string excluded = r.MissingLcsc.Count == 0
                    ? "Nothing excluded — every component has an LCSC number."
                    : r.MissingLcsc.Count + " excluded (no LCSC number): " + string.Join(", ", r.MissingLcsc.ToArray());

                ShowResult("JLCPCB export complete", Green,
                    "bom_jlcpcb.csv — " + r.Parts + " distinct parts",
                    "cpl_jlcpcb.csv — " + r.Placements + " placements",
                    excluded,
                    r.MissingLcsc.Count > 0 ? "Listed in bom_missing_lcsc.csv — they are in neither deliverable." : null);
                SetStatus("Written to " + folder, Green);
            }
            catch (Exception ex)
            {
                Log.Exception("SpikeWindow.DoExportJlc", ex);
                ShowResult("JLCPCB export failed", Red, ex.GetType().Name + " — " + ex.Message);
                SetStatus("Export failed", Red);
            }
        }

        private void DoPlace(string what, string csv,
                             Func<IPCB_ServerInterface, IPCB_Board, string, BoardImport.Result> action,
                             string noun)
        {
            try
            {
                if (string.IsNullOrEmpty(csv)) { SetStatus("Choose a CSV first", Amber); return; }

                IPCB_ServerInterface pcbServer;
                IPCB_Board board;
                if (!TryGetBoard(out pcbServer, out board))
                {
                    SetStatus("No active PCB document", Amber);
                    return;
                }

                BoardImport.Result r = action(pcbServer, board, csv);

                Brush tone = (r.Errors.Count > 0 || r.Missing.Count > 0) ? Amber : Green;
                ShowResult(what + " — " + r.Placed + " " + noun, tone,
                    r.Missing.Count > 0 ? "Not found (" + r.Missing.Count + "): " + string.Join(", ", r.Missing.ToArray()) : null,
                    r.Errors.Count > 0 ? "Row errors (" + r.Errors.Count + "): " + string.Join("; ", r.Errors.ToArray()) : null,
                    (r.Missing.Count == 0 && r.Errors.Count == 0) ? "No errors." : null);
                SetStatus(what + ": " + r.Placed + " " + noun, tone);
                RefreshBoard();
            }
            catch (Exception ex)
            {
                Log.Exception("SpikeWindow." + what, ex);
                ShowResult(what + " failed", Red, ex.GetType().Name + " — " + ex.Message);
                SetStatus(what + " failed", Red);
            }
        }

        private void DoLock(bool locked)
        {
            try
            {
                IPCB_ServerInterface pcbServer;
                IPCB_Board board;
                if (!TryGetBoard(out pcbServer, out board))
                {
                    SetStatus("No active PCB document", Amber);
                    return;
                }

                string csv = CsvIo.PickCsv(locked ? "Lock components" : "Unlock components", "locked_components.csv");
                if (csv == null) { SetStatus("Cancelled", TextDim); return; }

                BoardImport.Result r = BoardImport.SetLock(pcbServer, board, csv, locked);
                string verb = locked ? "locked" : "unlocked";

                ShowResult(r.Placed + " components " + verb, r.Missing.Count > 0 ? Amber : Green,
                    r.Missing.Count > 0 ? "Not found (" + r.Missing.Count + "): " + string.Join(", ", r.Missing.ToArray()) : "No errors.");
                SetStatus(r.Placed + " components " + verb, Green);
                RefreshBoard();
            }
            catch (Exception ex)
            {
                Log.Exception("SpikeWindow.DoLock", ex);
                ShowResult("Lock failed", Red, ex.GetType().Name + " — " + ex.Message);
                SetStatus("Lock failed", Red);
            }
        }
    }
}
