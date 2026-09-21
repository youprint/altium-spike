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
        private CheckBox relGenerate, relDryRun;

        private SpikeWindow(IClient client)
        {
            this.client = client;

            Title = "AltiumSpike";
            Width = 900;
            Height = 880;
            MinWidth = 760;
            MinHeight = 560;
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
            g.RowDefinitions.Add(Row(new GridLength(1, GridUnitType.Star)));     // scrolling body

            g.Children.Add(At(BuildBoardHeader(), 0));
            g.Children.Add(At(BuildResultsCard(), 1));

            // The body scrolls. With three tool groups stacked, a window tall
            // enough to show everything at once would not fit on a laptop
            // screen beside Altium, which is where this actually gets used.
            // The board header and the results strip stay pinned above it,
            // because those are what you look at after pressing a button.
            StackPanel body = new StackPanel();

            FrameworkElement cols = BuildColumns() as FrameworkElement;
            if (cols != null) cols.MinHeight = 340;   // keeps the two columns readable
            body.Children.Add(cols);
            body.Children.Add(BuildHighSpeedSection());
            body.Children.Add(BuildFabricationSection());

            ScrollViewer sv = new ScrollViewer();
            sv.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            sv.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            sv.Padding = new Thickness(0, 0, 6, 0);
            sv.Content = body;

            g.Children.Add(At(sv, 2));
            return g;
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

        // ---------------- export ----------------
        // ---------------- high-speed tools ----------------
        //
        // These two live in their own full-width strip rather than inside the
        // Export column, because neither is a plain "write a CSV of the
        // board" action: the fence MUTATES the board and depends on what is
        // selected in the editor, and the length export is the only output
        // whose numbers come from Altium's own calculators rather than from
        // geometry this plugin measures. Grouping them keeps that distinction
        // visible instead of burying a board-modifying button between two
        // read-only exports.
        private UIElement BuildHighSpeedSection()
        {
            StackPanel outer = new StackPanel();
            outer.Margin = new Thickness(0, 15, 0, 0);
            outer.Children.Add(SectionHeader("HIGH-SPEED"));

            Grid g = new Grid();
            g.ColumnDefinitions.Add(Col());
            g.ColumnDefinitions.Add(ColFixed(16));
            g.ColumnDefinitions.Add(ColFixed(250));

            UIElement fence = BuildViaFenceCard();
            Grid.SetColumn(fence, 0);
            g.Children.Add(fence);

            UIElement lengths = BuildNetLengthsCard();
            Grid.SetColumn(lengths, 2);
            g.Children.Add(lengths);

            outer.Children.Add(g);
            return outer;
        }

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

        // ---------------- fabrication documentation & release ----------------
        //
        // Three tools that all end in an artefact a fabricator reads: a
        // stackup table and a note block drawn onto the board, and the
        // release archive itself. They share a section because they share a
        // moment -- the one just before a board goes out.
        private UIElement BuildFabricationSection()
        {
            StackPanel outer = new StackPanel();
            outer.Margin = new Thickness(0, 15, 0, 0);
            outer.Children.Add(SectionHeader("FABRICATION"));

            Grid top = new Grid();
            top.ColumnDefinitions.Add(Col());
            top.ColumnDefinitions.Add(ColFixed(16));
            top.ColumnDefinitions.Add(Col());

            UIElement stackCard = BuildStackupCard();
            Grid.SetColumn(stackCard, 0);
            top.Children.Add(stackCard);

            UIElement notesCard = BuildNotesCard();
            Grid.SetColumn(notesCard, 2);
            top.Children.Add(notesCard);

            outer.Children.Add(top);
            outer.Children.Add(BuildReleaseCard());
            return outer;
        }

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
            bool wantFile = pickFile;
            Button browse = SecondaryButton("Browse", 30, 12);
            browse.Margin = new Thickness(7, 0, 0, 0);
            browse.Click += delegate
            {
                string chosen = wantFile
                    ? Settings.PickFile("Choose the Output Job", "Output Job (*.OutJob)|*.OutJob|All files (*.*)|*.*")
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
