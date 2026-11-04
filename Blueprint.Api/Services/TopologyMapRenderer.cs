// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Blueprint.Api.Services
{
    /// <summary>What a MSEL's network map shows besides the topology. None of it is Caster's.</summary>
    public class TopologyMapSettings
    {
        /// <summary>Drawn across the top. None when empty.</summary>
        public string Title { get; set; }

        /// <summary>Caster node ids of the machines and networks the map leaves out.</summary>
        public List<string> HiddenNodeIds { get; set; } = new();

        /// <summary>A key under the drawing naming each network's colour.</summary>
        public bool ShowLegend { get; set; }

        /// <summary>
        /// Give each click point the machine's name as its label. Player draws a label as black text over
        /// the image, so with the names already in the picture it prints each one twice; off by default.
        /// </summary>
        public bool ClickPointLabels { get; set; }
    }

    /// <summary>One place on the map Player opens a machine's console from.</summary>
    public class TopologyMapClickPoint
    {
        /// <summary>The Caster node id of the machine.</summary>
        public string NodeId { get; set; }

        /// <summary>Across the image, 0 to 100.</summary>
        public double XPosition { get; set; }

        /// <summary>Down the image, 0 to 100.</summary>
        public double YPosition { get; set; }

        /// <summary>On the same 0 to 100 scale.</summary>
        public double Radius { get; set; }

        public string Label { get; set; }

        /// <summary>The machine's Player name: Player opens views/{viewId}/vms/{name}/console.</summary>
        public string[] Urls { get; set; }
    }

    public class TopologyMapDrawing
    {
        public string Svg { get; set; }

        /// <summary>The SVG as a data URL, which Player draws as a map's image.</summary>
        public string ImageUrl => "data:image/svg+xml;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(Svg ?? ""));

        /// <summary>The drawing is square, this many units a side, so 0 to 100 means the same both ways.</summary>
        public double Size { get; set; }

        public List<TopologyMapClickPoint> ClickPoints { get; set; } = new();
        public int MachineCount { get; set; }
        public int NetworkCount { get; set; }

        /// <summary>Machines drawn without a click point: they have no Player record, so no console to open.</summary>
        public List<string> MachinesWithoutConsole { get; set; } = new();
    }

    /// <summary>
    /// Draws a Caster topology as an SVG, labelled with the machines' Player names, and works out where on
    /// it Player's click points go. Nodes sit where Caster's topology view saved them (its tree layout,
    /// else its force layout); when nobody laid it out there, it is drawn as a network diagram: networks as stacked
    /// bars, each machine under the topmost network it is on. The same topology and settings always give the same bytes.
    /// </summary>
    public static class TopologyMapRenderer
    {
        private const double MachineHeight = 56;
        private const double MachineMinWidth = 96;
        private const double MachineMaxWidth = 240;
        private const double NetworkHeight = 30;
        private const double NetworkMinWidth = 110;
        private const double NetworkMaxWidth = 300;
        private const double Gap = 24;
        private const double RowGap = 28;
        private const double Margin = 32;
        private const double TitleBand = 44;
        private const double LegendRowHeight = 22;
        private const int MaxLabelLength = 30;
        private const int GridColumns = 3;

        // Player draws a map in <svg viewBox="0 8 100 100">, so the top 8% of the image is never seen
        private const double PlayerTopCrop = 0.09;

        private static readonly string[] _palette =
            ["#2563eb", "#059669", "#d97706", "#7c3aed", "#db2777", "#0891b2", "#65a30d", "#dc2626"];

        private static readonly Dictionary<string, string> _roleCaptions = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Server"] = "Server",
            ["Workstation"] = "Workstation",
            ["DomainController"] = "Domain controller",
            ["Firewall"] = "Firewall",
            ["Router"] = "Router",
            ["WebServer"] = "Web server",
            ["Database"] = "Database",
            ["MailServer"] = "Mail server",
            ["Linux"] = "Linux",
            ["Windows"] = "Windows",
            ["Attacker"] = "Attacker",
        };

        private sealed class Box
        {
            public CasterTopologyNode Node;
            public string Label;
            public string Role;
            public double W;
            public double H;
            public double X;
            public double Y;
            public bool Placed;
            public string Color;
            // a network drawn as a bar across the map, which its machines join straight up or down
            public bool IsBar;
            // in a stacked band: the row it is in, and the width of its column, for routing its links between columns
            public int Row;
            public double SlotWidth;
            public double Left => X - W / 2;
            public double Right => X + W / 2;
            public double Top => Y - H / 2;
            public double Bottom => Y + H / 2;
        }

        public static TopologyMapDrawing Render(CasterTopology topology, TopologyMapSettings settings = null)
        {
            settings ??= new TopologyMapSettings();
            topology ??= new CasterTopology();
            var hidden = new HashSet<string>(settings.HiddenNodeIds ?? new List<string>(), StringComparer.Ordinal);
            var layout = topology.Layout ?? new Dictionary<string, CasterTopologyNodeLayout>();

            // what will exist: not what the plan deletes, and not what the planner hid
            var nodes = (topology.Nodes ?? new List<CasterTopologyNode>())
                .Where(n => n != null && !string.IsNullOrEmpty(n.Id) && (n.IsMachine || n.IsNetwork))
                .Where(n => !string.Equals(n.Action, "delete", StringComparison.OrdinalIgnoreCase))
                .Where(n => !hidden.Contains(n.Id))
                .GroupBy(n => n.Id, StringComparer.Ordinal).Select(g => g.First())
                .ToList();

            var networks = nodes.Where(n => n.IsNetwork)
                .OrderBy(n => n.Label ?? n.Id, NaturalComparer.Instance).ThenBy(n => n.Id, StringComparer.Ordinal)
                .Select(n => NetworkBox(n))
                .ToList();
            var machines = nodes.Where(n => n.IsMachine)
                .OrderBy(n => n.PlayerName ?? n.Label ?? n.Id, NaturalComparer.Instance).ThenBy(n => n.Id, StringComparer.Ordinal)
                .Select(n => MachineBox(n, layout))
                .ToList();
            for (var i = 0; i < networks.Count; i++)
                networks[i].Color = _palette[i % _palette.Length];

            var byId = networks.Concat(machines).ToDictionary(b => b.Node.Id, StringComparer.Ordinal);
            var edges = (topology.Edges ?? new List<CasterTopologyEdge>())
                .Where(e => e.From != null && e.To != null && byId.ContainsKey(e.From) && byId.ContainsKey(e.To))
                .Where(e => byId[e.From].Node.IsMachine && byId[e.To].Node.IsNetwork)
                .GroupBy(e => (e.From, e.To)).Select(g => g.OrderBy(e => e.Nic).First())
                .OrderBy(e => e.From, StringComparer.Ordinal).ThenBy(e => e.Nic).ThenBy(e => e.To, StringComparer.Ordinal)
                .ToList();

            // each machine hangs under the network its lowest NIC is on
            var primary = new Dictionary<string, Box>(StringComparer.Ordinal);
            foreach (var machine in machines)
            {
                var first = edges.Where(e => e.From == machine.Node.Id).OrderBy(e => e.Nic).ThenBy(e => e.To, StringComparer.Ordinal).FirstOrDefault();
                if (first != null)
                {
                    primary[machine.Node.Id] = byId[first.To];
                    machine.Color = byId[first.To].Color;
                }
                else
                {
                    machine.Color = "#6b7280";
                }
            }

            if (!HasSavedPositions(networks.Concat(machines), layout))
                Stack(networks, machines, edges, byId);
            else
                Place(networks, machines, primary, layout);

            return Draw(networks, machines, edges, byId, settings);
        }

        private static Box NetworkBox(CasterTopologyNode node)
        {
            var label = Shorten(string.IsNullOrWhiteSpace(node.Label) ? node.Id : node.Label.Trim());
            return new Box
            {
                Node = node,
                Label = label,
                W = Math.Clamp(TextWidth(label, 12) + 28, NetworkMinWidth, NetworkMaxWidth),
                H = NetworkHeight
            };
        }

        private static Box MachineBox(CasterTopologyNode node, Dictionary<string, CasterTopologyNodeLayout> layout)
        {
            var label = Shorten(node.PlayerName ?? (string.IsNullOrWhiteSpace(node.Label) ? node.Id : node.Label.Trim()));
            layout.TryGetValue(node.Id, out var saved);
            var role = saved?.Role != null && _roleCaptions.TryGetValue(saved.Role, out var caption) ? caption : null;
            return new Box
            {
                Node = node,
                Label = label,
                Role = role,
                W = Math.Clamp(TextWidth(label, 13) + 28, MachineMinWidth, MachineMaxWidth),
                H = MachineHeight
            };
        }

        private static int IndexIn(IReadOnlyList<Box> list, Box box)
        {
            for (var i = 0; i < list.Count; i++)
                if (list[i] == box)
                    return i;
            return -1;
        }

        // every order of the list, the given order first, so the first best order found is the natural one
        private static IEnumerable<List<Box>> Permutations(List<Box> items)
        {
            if (items.Count <= 1)
            {
                yield return items.ToList();
                yield break;
            }
            for (var i = 0; i < items.Count; i++)
            {
                var rest = items.Where((_, k) => k != i).ToList();
                foreach (var tail in Permutations(rest))
                {
                    tail.Insert(0, items[i]);
                    yield return tail;
                }
            }
        }

        private static bool HasSavedPositions(IEnumerable<Box> boxes, Dictionary<string, CasterTopologyNodeLayout> layout) =>
            boxes.Any(b => layout.TryGetValue(b.Node.Id, out var l)
                && ((l.TreeX != null && l.TreeY != null) || (l.X != null && l.Y != null)));

        /// <summary>
        /// A network diagram for a topology nobody laid out in Caster: each network a bar across the map, stacked so that
        /// networks sharing machines sit next to each other, and each machine in the band under the topmost network it is on,
        /// so a machine on two networks sits between their bars and joins both with short straight lines. The columns are
        /// chosen to make the drawing about as tall as it is wide, since Player shows the map square.
        /// </summary>
        private static void Stack(List<Box> networks, List<Box> machines, List<CasterTopologyEdge> edges, Dictionary<string, Box> byId)
        {
            var nets = edges.GroupBy(e => e.From).ToDictionary(g => g.Key, g => g.Select(e => byId[e.To]).Distinct().ToList(), StringComparer.Ordinal);
            List<Box> NetsOf(Box m) => nets.TryGetValue(m.Node.Id, out var list) ? list : new List<Box>();

            // the order of the bars: the one where machines on more than one network span the fewest bars, tried in full for up
            // to seven networks; past that, the busiest network first and then each time the one sharing most with the last
            int Size(Box n) => machines.Count(m => NetsOf(m).Contains(n));
            var multi = machines.Select(NetsOf).Where(list => list.Count > 1).ToList();
            int Span(IReadOnlyList<Box> arrangement) =>
                multi.Sum(list => list.Max(n => IndexIn(arrangement, n)) - list.Min(n => IndexIn(arrangement, n)));
            var start = networks.OrderByDescending(Size).ThenBy(n => networks.IndexOf(n)).ToList();
            List<Box> order;
            if (networks.Count <= 7)
                order = Permutations(start).OrderBy(Span).First();
            else
            {
                order = new List<Box>();
                var left = start.ToList();
                int Shared(Box a, Box b) => machines.Count(m => NetsOf(m).Contains(a) && NetsOf(m).Contains(b));
                while (left.Count > 0)
                {
                    var next = order.Count == 0 ? left[0] : left.OrderByDescending(n => Shared(order[^1], n)).ThenByDescending(Size).First();
                    order.Add(next);
                    left.Remove(next);
                }
            }
            networks.Sort((a, b) => order.IndexOf(a).CompareTo(order.IndexOf(b)));

            // each machine in the band under the topmost of its networks; a machine on none goes in a band of its own at the end
            var bands = order.Select(n => machines.Where(m => NetsOf(m).Any() && NetsOf(m).OrderBy(n => order.IndexOf(n)).First() == n).ToList()).ToList();
            var loose = machines.Where(m => !NetsOf(m).Any()).ToList();
            // within a band, machines that also reach a lower network go to the right, nearest that network last
            foreach (var band in bands)
                band.Sort((a, b) =>
                {
                    int Reach(Box m) => NetsOf(m).Select(n => order.IndexOf(n)).DefaultIfEmpty(0).Max();
                    var byReach = Reach(a).CompareTo(Reach(b));
                    return byReach != 0 ? byReach : machines.IndexOf(a).CompareTo(machines.IndexOf(b));
                });
            if (loose.Any())
                bands.Add(loose);

            var slotWidth = (machines.Count == 0 ? MachineMinWidth : machines.Max(m => m.W)) + Gap;
            var slotHeight = MachineHeight + RowGap;
            double Height(int columns) =>
                order.Count * (NetworkHeight + RowGap) + bands.Sum(b => Math.Ceiling(b.Count / (double)columns)) * slotHeight;
            var widest = Math.Max(1, bands.Select(b => b.Count).DefaultIfEmpty(1).Max());
            var columnsWanted = Enumerable.Range(1, Math.Max(1, Math.Min(widest, 8)))
                .OrderBy(c => Math.Abs(Math.Log(Math.Max(c * slotWidth, NetworkMinWidth) / Math.Max(Height(c), 1))))
                .First();
            var width = Math.Max(columnsWanted * slotWidth, networks.Select(n => n.W).DefaultIfEmpty(NetworkMinWidth).Max() + Gap);

            double y = 0;
            for (var i = 0; i < bands.Count; i++)
            {
                if (i < order.Count)
                {
                    var bar = order[i];
                    bar.IsBar = true;
                    bar.W = width;
                    bar.X = width / 2;
                    bar.Y = y + NetworkHeight / 2;
                    bar.Placed = true;
                    y += NetworkHeight + RowGap;
                }
                var band = bands[i];
                var columns = Math.Min(columnsWanted, Math.Max(band.Count, 1));
                // the band's columns on one grid, centred as a block, so the gaps between them line up for the links to run in
                var offset = (width - columns * slotWidth) / 2;
                for (var k = 0; k < band.Count; k++)
                {
                    band[k].X = offset + slotWidth * (k % columns + 0.5);
                    band[k].Y = y + MachineHeight / 2 + (k / columns) * slotHeight;
                    band[k].Row = k / columns;
                    band[k].SlotWidth = slotWidth;
                    band[k].Placed = true;
                }
                y += Math.Ceiling(band.Count / (double)columns) * slotHeight;
            }
        }

        /// <summary>
        /// Caster's saved positions first, from one of its layouts only, since the two do not share
        /// coordinates: the tree layout, Caster's default, when any node has a tree position, else the
        /// force layout. Then what nobody placed.
        /// </summary>
        private static void Place(List<Box> networks, List<Box> machines, Dictionary<string, Box> primary, Dictionary<string, CasterTopologyNodeLayout> layout)
        {
            var all = networks.Concat(machines).ToList();
            CasterTopologyNodeLayout Saved(Box b) => layout.TryGetValue(b.Node.Id, out var l) ? l : null;
            var useTree = all.Any(b => Saved(b)?.TreeX != null && Saved(b)?.TreeY != null);
            foreach (var box in all)
            {
                var saved = Saved(box);
                var x = useTree ? saved?.TreeX : saved?.X;
                var y = useTree ? saved?.TreeY : saved?.Y;
                if (x != null && y != null && double.IsFinite(x.Value) && double.IsFinite(y.Value))
                {
                    box.X = x.Value;
                    box.Y = y.Value;
                    box.Placed = true;
                }
            }

            var slotWidth = (machines.Count == 0 ? MachineMinWidth : machines.Max(m => m.W)) + Gap;
            var slotHeight = MachineHeight + RowGap;
            var placedAny = all.Any(b => b.Placed);
            double rowY = placedAny ? all.Where(b => b.Placed).Min(b => b.Y) : 0;
            double nextX = placedAny ? all.Where(b => b.Placed).Max(b => b.Right) + Gap * 3 : 0;

            // networks nobody placed, in a row, each as wide as the grid of its machines
            foreach (var network in networks.Where(n => !n.Placed))
            {
                var count = machines.Count(m => !m.Placed && primary.TryGetValue(m.Node.Id, out var p) && p == network);
                var width = Math.Max(network.W, Math.Min(Math.Max(count, 1), GridColumns) * slotWidth);
                network.X = nextX + width / 2;
                network.Y = rowY;
                network.Placed = true;
                nextX += width + Gap * 2;
            }

            // machines nobody placed, in the first free slots of a grid below their network
            var anchorless = new Box { X = nextX + slotWidth * GridColumns / 2, Y = rowY, H = NetworkHeight };
            foreach (var group in machines.Where(m => !m.Placed)
                .GroupBy(m => primary.TryGetValue(m.Node.Id, out var p) ? p : anchorless))
            {
                var anchor = group.Key;
                var columns = Math.Min(group.Count(), GridColumns);
                var slot = 0;
                foreach (var machine in group)
                {
                    while (true)
                    {
                        var column = slot % columns;
                        var row = slot / columns;
                        machine.X = anchor.X + (column - (columns - 1) / 2.0) * slotWidth;
                        machine.Y = anchor.Y + anchor.H / 2 + RowGap + MachineHeight / 2 + row * slotHeight;
                        slot++;
                        if (!all.Any(b => b != machine && b.Placed && Overlaps(b, machine)) || slot > 10000)
                            break;
                    }
                    machine.Placed = true;
                }
            }
        }

        private static bool Overlaps(Box a, Box b) =>
            a.Left - 8 < b.Right && b.Left - 8 < a.Right && a.Top - 8 < b.Bottom && b.Top - 8 < a.Bottom;

        private static TopologyMapDrawing Draw(List<Box> networks, List<Box> machines, List<CasterTopologyEdge> edges, Dictionary<string, Box> byId, TopologyMapSettings settings)
        {
            var all = networks.Concat(machines).ToList();
            var title = string.IsNullOrWhiteSpace(settings.Title) ? null : settings.Title.Trim();
            var legend = settings.ShowLegend && networks.Count > 0;

            double minX = all.Count == 0 ? 0 : all.Min(b => b.Left);
            double maxX = all.Count == 0 ? 0 : all.Max(b => b.Right);
            double minY = all.Count == 0 ? 0 : all.Min(b => b.Top);
            double maxY = all.Count == 0 ? 0 : all.Max(b => b.Bottom);
            var titleHeight = title == null ? 0 : TitleBand;
            var legendWidth = legend ? networks.Max(n => TextWidth(n.Label, 12)) + 40 : 0;
            var legendColumns = legend ? Math.Max(1, (int)Math.Floor(Math.Max(maxX - minX, 240) / legendWidth)) : 0;
            var legendHeight = legend ? Gap + LegendRowHeight * Math.Ceiling(networks.Count / (double)legendColumns) : 0;
            var titleWidth = title == null ? 0 : TextWidth(title, 20);

            var contentWidth = Math.Max(Math.Max(maxX - minX, titleWidth), legend ? legendWidth : 0) + Margin * 2;
            var contentHeight = (maxY - minY) + titleHeight + legendHeight + Margin * 2;
            var size = Math.Ceiling(Math.Max(Math.Max(contentWidth, contentHeight / (1 - PlayerTopCrop)), 240));

            // centred across, and down within the part of the square Player shows
            var left = (size - contentWidth) / 2 + Margin;
            var top = size * PlayerTopCrop + (size * (1 - PlayerTopCrop) - contentHeight) / 2 + Margin;
            var dx = left + ((contentWidth - Margin * 2) - (maxX - minX)) / 2 - minX;
            var dy = top + titleHeight - minY;
            foreach (var box in all)
            {
                box.X += dx;
                box.Y += dy;
            }

            var svg = new StringBuilder();
            svg.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{N(size)}\" height=\"{N(size)}\" viewBox=\"0 0 {N(size)} {N(size)}\" font-family=\"Helvetica, Arial, sans-serif\">\n");
            svg.Append($"<rect x=\"0\" y=\"0\" width=\"{N(size)}\" height=\"{N(size)}\" fill=\"#ffffff\"/>\n");
            if (title != null)
                svg.Append($"<text x=\"{N(size / 2)}\" y=\"{N(top + 24)}\" font-size=\"20\" font-weight=\"bold\" fill=\"#111827\" text-anchor=\"middle\">{Escape(title)}</text>\n");

            foreach (var edge in edges)
            {
                var from = byId[edge.From];
                var to = byId[edge.To];
                if (to.IsBar)
                {
                    var above = to.Y < from.Y;
                    var clear = above ? from.Row == 0 : !machines.Any(m => m != from && m.X == from.X && m.Y > from.Y && m.Y < to.Y);
                    if (clear)
                    {
                        // straight up to the bar above the machine, or down to the one below with nothing in between
                        var y1 = above ? from.Top : from.Bottom;
                        var y2 = above ? to.Bottom : to.Top;
                        svg.Append($"<line x1=\"{N(from.X)}\" y1=\"{N(y1)}\" x2=\"{N(from.X)}\" y2=\"{N(y2)}\" stroke=\"{to.Color}\" stroke-width=\"3\"/>\n");
                    }
                    else
                    {
                        // out of the side into the gap between columns, then along it to the bar, so the line crosses no machine;
                        // up on the left side, down on the right
                        var side = above ? from.Left : from.Right;
                        var gap = above ? from.X - from.SlotWidth / 2 : from.X + from.SlotWidth / 2;
                        var end = above ? to.Bottom : to.Top;
                        svg.Append($"<polyline points=\"{N(side)},{N(from.Y)} {N(gap)},{N(from.Y)} {N(gap)},{N(end)}\" fill=\"none\" stroke=\"{to.Color}\" stroke-width=\"3\"/>\n");
                    }
                }
                else
                    svg.Append($"<line x1=\"{N(from.X)}\" y1=\"{N(from.Y)}\" x2=\"{N(to.X)}\" y2=\"{N(to.Y)}\" stroke=\"{to.Color}\" stroke-width=\"2\"/>\n");
            }

            foreach (var network in networks)
            {
                svg.Append($"<rect x=\"{N(network.Left)}\" y=\"{N(network.Top)}\" width=\"{N(network.W)}\" height=\"{N(network.H)}\" rx=\"{N(network.IsBar ? 6 : network.H / 2)}\" fill=\"{network.Color}\"/>\n");
                if (network.IsBar)
                    svg.Append($"<text x=\"{N(network.Left + 14)}\" y=\"{N(network.Y + 4)}\" font-size=\"12\" font-weight=\"bold\" fill=\"#ffffff\">{Escape(network.Label)}</text>\n");
                else
                    svg.Append($"<text x=\"{N(network.X)}\" y=\"{N(network.Y + 4)}\" font-size=\"12\" font-weight=\"bold\" fill=\"#ffffff\" text-anchor=\"middle\">{Escape(network.Label)}</text>\n");
            }

            var drawing = new TopologyMapDrawing { Size = size, NetworkCount = networks.Count, MachineCount = machines.Count };
            foreach (var machine in machines)
            {
                var hasConsole = machine.Node.PlayerName != null;
                var dash = machine.Node.NotCreated || !hasConsole ? " stroke-dasharray=\"5 3\"" : "";
                var fill = hasConsole ? "#ffffff" : "#f3f4f6";
                svg.Append($"<rect x=\"{N(machine.Left)}\" y=\"{N(machine.Top)}\" width=\"{N(machine.W)}\" height=\"{N(machine.H)}\" rx=\"8\" fill=\"{fill}\" stroke=\"{machine.Color}\" stroke-width=\"2\"{dash}/>\n");
                if (machine.Role != null)
                    svg.Append($"<text x=\"{N(machine.X)}\" y=\"{N(machine.Top + 15)}\" font-size=\"10\" fill=\"#6b7280\" text-anchor=\"middle\">{Escape(machine.Role)}</text>\n");
                svg.Append($"<text x=\"{N(machine.X)}\" y=\"{N(machine.Role != null ? machine.Y + 12 : machine.Y + 5)}\" font-size=\"13\" fill=\"{(hasConsole ? "#111827" : "#6b7280")}\" text-anchor=\"middle\">{Escape(machine.Label)}</text>\n");

                if (hasConsole)
                {
                    drawing.ClickPoints.Add(new TopologyMapClickPoint
                    {
                        NodeId = machine.Node.Id,
                        XPosition = Percent(machine.X, size),
                        YPosition = Percent(machine.Y, size),
                        Radius = Percent(Math.Min(machine.H * 0.6, machine.W / 2), size),
                        Label = settings.ClickPointLabels ? machine.Node.PlayerName : "",
                        Urls = [machine.Node.PlayerName]
                    });
                }
                else
                {
                    drawing.MachinesWithoutConsole.Add(machine.Label);
                }
            }

            if (legend)
            {
                var legendTop = all.Max(b => b.Bottom) + Gap;
                var legendLeft = size / 2 - legendColumns * legendWidth / 2;
                for (var i = 0; i < networks.Count; i++)
                {
                    var x = legendLeft + (i % legendColumns) * legendWidth;
                    var y = legendTop + (i / legendColumns) * LegendRowHeight;
                    svg.Append($"<rect x=\"{N(x)}\" y=\"{N(y)}\" width=\"14\" height=\"14\" rx=\"3\" fill=\"{networks[i].Color}\"/>\n");
                    svg.Append($"<text x=\"{N(x + 20)}\" y=\"{N(y + 11)}\" font-size=\"12\" fill=\"#111827\">{Escape(networks[i].Label)}</text>\n");
                }
            }

            svg.Append("</svg>\n");
            drawing.Svg = svg.ToString();
            return drawing;
        }

        private static double Percent(double value, double size) => Math.Round(value / size * 100, 2);

        // an average glyph is a little over half the font size wide in a sans-serif face
        private static double TextWidth(string text, double fontSize) => (text?.Length ?? 0) * fontSize * 0.6;

        private static string Shorten(string label) =>
            label.Length <= MaxLabelLength ? label : label[..(MaxLabelLength - 1)] + "…";

        private static string N(double value) => Math.Round(value, 2).ToString("0.##", CultureInfo.InvariantCulture);

        private static string Escape(string text) => (text ?? "")
            .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

        /// <summary>Orders "vm2" before "vm10", as Caster's view does.</summary>
        private sealed class NaturalComparer : IComparer<string>
        {
            public static readonly NaturalComparer Instance = new();

            public int Compare(string a, string b)
            {
                a ??= "";
                b ??= "";
                int i = 0, j = 0;
                while (i < a.Length && j < b.Length)
                {
                    if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
                    {
                        var si = i;
                        var sj = j;
                        while (i < a.Length && char.IsDigit(a[i])) i++;
                        while (j < b.Length && char.IsDigit(b[j])) j++;
                        var na = a[si..i].TrimStart('0');
                        var nb = b[sj..j].TrimStart('0');
                        var byLength = na.Length.CompareTo(nb.Length);
                        if (byLength != 0) return byLength;
                        var byDigits = string.CompareOrdinal(na, nb);
                        if (byDigits != 0) return byDigits;
                    }
                    else
                    {
                        var byChar = char.ToLowerInvariant(a[i]).CompareTo(char.ToLowerInvariant(b[j]));
                        if (byChar != 0) return byChar;
                        i++;
                        j++;
                    }
                }
                return (a.Length - i).CompareTo(b.Length - j);
            }
        }
    }
}
