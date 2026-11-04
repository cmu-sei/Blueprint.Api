// Copyright 2026 Carnegie Mellon University. All Rights Reserved.
// Released under a MIT (SEI)-style license. See LICENSE.md in the project root for license information.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Blueprint.Api.Services;
using Xunit;

namespace Blueprint.Api.Tests.Services;

/// <summary>
/// <c>TopologyMapRenderer</c> and <c>CasterTopologyReader.Parse</c>: the network map a MSEL's Caster topology becomes, and the click points
/// Player is given. No database and no host.
/// </summary>
public class TopologyMapRendererTests
{
    private const string Lan = "proxmox_sdn_vnet.lan";
    private const string Dmz = "proxmox_sdn_vnet.dmz";

    // ---------------------------------------------------------------------------------------------
    // Parse
    // ---------------------------------------------------------------------------------------------

    /// <summary>The JSON Caster's GET workspaces/{id}/topology answers: camel case, enums by name, the
    /// layout keyed by node id.</summary>
    [Fact]
    public void Parse_ReadsCastersTopologyAnswer()
    {
        var topology = CasterTopologyReader.Parse("""
            {
              "workspaceId": "6f1f4d1e-8a59-4f0e-9a51-0d2f43f2d5aa",
              "source": "State",
              "runId": null,
              "nodes": [
                { "id": "module.host[\"dc\"].proxmox_virtual_environment_vm.vm", "kind": "Machine", "type": "proxmox_virtual_environment_vm",
                  "label": "dc-01", "action": null, "external": false, "notCreated": false, "properties": { "cpus": "2" },
                  "console": { "teamIds": ["0b9b1a5c-5c4e-4d7e-9c3e-111111111111"], "teamsUnknown": false, "match": "Id",
                               "vmId": "4f9d...", "address": null, "action": null, "name": "Domain Controller", "url": null, "teams": [] } },
                { "id": "proxmox_sdn_vnet.lan", "kind": "Network", "label": "lan", "console": null }
              ],
              "edges": [ { "from": "module.host[\"dc\"].proxmox_virtual_environment_vm.vm", "to": "proxmox_sdn_vnet.lan", "nic": 0 } ],
              "layout": { "proxmox_sdn_vnet.lan": { "x": null, "y": null, "treeX": 120, "treeY": -40, "role": null },
                          "module.host[\"dc\"].proxmox_virtual_environment_vm.vm": { "treeX": 120, "treeY": 60, "role": "DomainController" } }
            }
            """);

        var machine = topology.Nodes[0];
        Assert.True(machine.IsMachine);
        Assert.True(topology.Nodes[1].IsNetwork);
        Assert.Equal("Domain Controller", machine.PlayerName);
        Assert.Equal("dc-01", machine.Label);
        Assert.Equal(["0b9b1a5c-5c4e-4d7e-9c3e-111111111111"], machine.Console.TeamIds);
        Assert.Equal("2", machine.Properties["cpus"]);
        Assert.Equal("State", topology.Source);
        Assert.Equal((machine.Id, Lan, 0), (topology.Edges[0].From, topology.Edges[0].To, topology.Edges[0].Nic));
        Assert.Equal(120, topology.Layout[Lan].TreeX);
        Assert.Equal(-40, topology.Layout[Lan].TreeY);
        Assert.Equal("DomainController", topology.Layout[machine.Id].Role);
    }

    [Fact]
    public void Parse_OfAnEmptyGraph_HasNoNodes()
    {
        var topology = CasterTopologyReader.Parse("""{"workspaceId":"6f1f4d1e-8a59-4f0e-9a51-0d2f43f2d5aa","nodes":[],"edges":[],"layout":{}}""");

        Assert.Empty(topology.Nodes);
        Assert.Empty(TopologyMapRenderer.Render(topology).ClickPoints);
    }

    // ---------------------------------------------------------------------------------------------
    // Names
    // ---------------------------------------------------------------------------------------------

    /// <summary>A machine is drawn and opened by its Player name, not its infrastructure name.</summary>
    [Fact]
    public void Render_LabelsAMachineWithItsPlayerName()
    {
        var drawing = TopologyMapRenderer.Render(Topology(Machine("vm.dc", "dc-01", player: "Domain Controller", on: Lan), Network(Lan, "lan")));

        Assert.Contains("Domain Controller", Texts(drawing));
        Assert.DoesNotContain("dc-01", Texts(drawing));
        var point = Assert.Single(drawing.ClickPoints);
        Assert.Equal(["Domain Controller"], point.Urls);
        Assert.Equal("vm.dc", point.NodeId);
    }

    /// <summary>A machine with no Player record keeps its Caster label and gets no click point.</summary>
    [Fact]
    public void Render_OfAMachineWithNoPlayerRecord_DrawsItsLabelWithoutAClickPoint()
    {
        var drawing = TopologyMapRenderer.Render(Topology(
            Machine("vm.router", "router", player: null, on: Lan),
            Machine("vm.ws", "ws-01", player: "Workstation 1", on: Lan),
            Network(Lan, "lan")));

        Assert.Contains("router", Texts(drawing));
        Assert.Equal(["Workstation 1"], drawing.ClickPoints.Select(p => p.Urls.Single()));
        Assert.Equal(["router"], drawing.MachinesWithoutConsole);
        Assert.Equal(2, drawing.MachineCount);
    }

    /// <summary>Player prints a click point's label over the image, so by default it is left empty.</summary>
    [Fact]
    public void Render_LeavesClickPointLabelsEmptyUnlessAskedFor()
    {
        var topology = Topology(Machine("vm.a", "a", player: "Alpha", on: Lan), Network(Lan, "lan"));

        Assert.Equal("", Assert.Single(TopologyMapRenderer.Render(topology).ClickPoints).Label);
        Assert.Equal("Alpha", Assert.Single(TopologyMapRenderer.Render(topology, new TopologyMapSettings { ClickPointLabels = true }).ClickPoints).Label);
    }

    [Fact]
    public void Render_EscapesNamesForXml()
    {
        var drawing = TopologyMapRenderer.Render(Topology(Machine("vm.a", "a", player: "R&D <lab> \"one\"", on: Lan), Network(Lan, "lan")));

        Assert.Contains("R&D <lab> \"one\"", Texts(drawing));
        Assert.Equal(["R&D <lab> \"one\""], drawing.ClickPoints.Single().Urls);
    }

    // ---------------------------------------------------------------------------------------------
    // Layout
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// With no saved layout, the map is a network diagram: each network a bar across it, one above another, and each machine
    /// in the band under its network's bar.
    /// </summary>
    [Fact]
    public void Render_WithNoSavedLayout_StacksTheNetworksAsBarsWithTheirMachinesUnderThem()
    {
        var drawing = TopologyMapRenderer.Render(Topology(
            Network(Lan, "lan"), Network(Dmz, "dmz"),
            Machine("vm.web", "web", player: "Web", on: Dmz),
            Machine("vm.ws1", "ws1", player: "WS1", on: Lan),
            Machine("vm.ws2", "ws2", player: "WS2", on: Lan)));

        var dmz = Rects(drawing).Single(r => r.Label == "dmz");
        var lan = Rects(drawing).Single(r => r.Label == "lan");
        Assert.Equal((dmz.X, dmz.Width), (lan.X, lan.Width));
        Assert.NotEqual(dmz.CenterY, lan.CenterY);
        var (upper, lower) = dmz.CenterY < lan.CenterY ? (dmz, lan) : (lan, dmz);
        foreach (var (name, bar) in new[] { ("Web", dmz), ("WS1", lan), ("WS2", lan) })
        {
            var y = Pixels(drawing, name).Y;
            Assert.True(y > bar.CenterY, $"{name} is under its network");
            if (bar == upper)
                Assert.True(y < lower.CenterY, $"{name} is above the next network");
        }
        Assert.NotEqual(Pixels(drawing, "WS1").X, Pixels(drawing, "WS2").X);
    }

    /// <summary>A machine on two networks sits between their bars, with a link to each.</summary>
    [Fact]
    public void Render_PutsAMachineOnTwoNetworksBetweenTheirBars()
    {
        var firewall = Machine("vm.fw", "fw", player: "Firewall", on: Dmz);
        var topology = Topology(Network(Lan, "lan"), Network(Dmz, "dmz"), firewall);
        topology.Edges.Add(new CasterTopologyEdge { From = "vm.fw", To = Lan, Nic = 1 });

        var drawing = TopologyMapRenderer.Render(topology);

        Assert.Equal(2, Elements(drawing, "line").Count() + Elements(drawing, "polyline").Count());
        var bars = Rects(drawing).Where(r => r.Label is "dmz" or "lan").OrderBy(r => r.CenterY).ToList();
        var y = Pixels(drawing, "Firewall").Y;
        Assert.True(y > bars[0].CenterY && y < bars[1].CenterY);
    }

    /// <summary>
    /// The bars are ordered so machines on two networks span as few as they can: a LAN that both the DMZ's firewall and the
    /// plant's historian reach sits between the DMZ and the plant network.
    /// </summary>
    [Fact]
    public void Render_PutsAHubNetworkBetweenTheNetworksItShares()
    {
        const string Ot = "proxmox_sdn_vnet.ot";
        var topology = Topology(Network(Dmz, "dmz"), Network(Ot, "ot"), Network(Lan, "lan"),
            Machine("vm.fw", "fw", player: "Firewall", on: Dmz), Machine("vm.hist", "hist", player: "Historian", on: Lan),
            Machine("vm.dc", "dc", player: "DC", on: Lan), Machine("vm.hmi", "hmi", player: "HMI", on: Ot));
        topology.Edges.Add(new CasterTopologyEdge { From = "vm.fw", To = Lan, Nic = 1 });
        topology.Edges.Add(new CasterTopologyEdge { From = "vm.hist", To = Ot, Nic = 1 });

        var drawing = TopologyMapRenderer.Render(topology);

        var order = Rects(drawing).Where(r => r.Label is "dmz" or "lan" or "ot").OrderBy(r => r.CenterY).Select(r => r.Label).ToList();
        Assert.Equal("lan", order[1]);
    }

    /// <summary>
    /// A machine in a band's second row reaches its bar through the gap between columns, so its link crosses no machine
    /// above it.
    /// </summary>
    [Fact]
    public void Render_RunsALinkFromASecondRowBetweenTheColumns()
    {
        var machines = Enumerable.Range(1, 6).Select(i => Machine($"vm.ws{i}", $"ws{i}", player: $"Workstation {i}", on: Lan)).ToArray();
        var drawing = TopologyMapRenderer.Render(Topology([Network(Lan, "lan"), .. machines]));

        var boxes = Rects(drawing).Where(r => r.Rx == 8 && r.Label?.StartsWith("Workstation") == true).ToList();
        Assert.True(boxes.Select(b => b.CenterY).Distinct().Count() > 1, "the band wraps onto a second row");
        foreach (var polyline in Elements(drawing, "polyline"))
        {
            var points = polyline.Attribute("points")!.Value.Split(' ')
                .Select(p => p.Split(',').Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray()).ToList();
            // the run up to the bar is vertical, at an x outside every machine
            var x = points[^1][0];
            Assert.All(boxes, b => Assert.False(x > b.X && x < b.X + b.Width, $"the link at x={x} crosses {b.Label}"));
        }
    }

    /// <summary>Caster's saved tree positions are used as they are: the drawing keeps their proportions.</summary>
    [Fact]
    public void Render_PlacesNodesWhereCastersTreeLayoutSavedThem()
    {
        var topology = Topology(
            Network(Lan, "lan"),
            Machine("vm.a", "a", player: "A", on: Lan),
            Machine("vm.b", "b", player: "B", on: Lan));
        topology.Layout[Lan] = new CasterTopologyNodeLayout { TreeX = 0, TreeY = 0, X = 999, Y = 999 };
        topology.Layout["vm.a"] = new CasterTopologyNodeLayout { TreeX = -300, TreeY = 200 };
        topology.Layout["vm.b"] = new CasterTopologyNodeLayout { TreeX = 300, TreeY = 400 };

        var drawing = TopologyMapRenderer.Render(topology);

        var a = Point(drawing, "A");
        var b = Point(drawing, "B");
        var lan = Rects(drawing).Single(r => r.Rx > 8 && r.Label == "lan");
        var scale = drawing.Size / 100;
        // 600 apart across and 200 down, in Caster's units
        Assert.Equal(600, (b.X - a.X) * scale, 0);
        Assert.Equal(200, (b.Y - a.Y) * scale, 0);
        Assert.Equal(lan.CenterX, (a.X + b.X) / 2 * scale, 0);
    }

    /// <summary>The force layout's positions are used when nothing has a tree position.</summary>
    [Fact]
    public void Render_UsesTheForceLayoutWhenNoNodeHasATreePosition()
    {
        var topology = Topology(Machine("vm.a", "a", player: "A", on: null), Machine("vm.b", "b", player: "B", on: null));
        topology.Layout["vm.a"] = new CasterTopologyNodeLayout { X = 0, Y = 500 };
        topology.Layout["vm.b"] = new CasterTopologyNodeLayout { X = 0, Y = 0 };

        var drawing = TopologyMapRenderer.Render(topology);

        Assert.True(Point(drawing, "A").Y > Point(drawing, "B").Y);
        Assert.Equal(Point(drawing, "A").X, Point(drawing, "B").X);
    }

    /// <summary>A machine nobody placed, among ones that were, goes under its network clear of the others.</summary>
    [Fact]
    public void Render_PlacesAnUnplacedMachineClearOfThePlacedOnes()
    {
        var topology = Topology(
            Network(Lan, "lan"),
            Machine("vm.a", "a", player: "A", on: Lan),
            Machine("vm.new", "new", player: "New", on: Lan));
        topology.Layout[Lan] = new CasterTopologyNodeLayout { TreeX = 0, TreeY = 0 };
        topology.Layout["vm.a"] = new CasterTopologyNodeLayout { TreeX = 0, TreeY = 114 };

        var drawing = TopologyMapRenderer.Render(topology);

        var boxes = Rects(drawing).Where(r => r.Rx == 8).ToList();
        Assert.Equal(2, boxes.Count);
        Assert.False(boxes[0].Intersects(boxes[1]));
        Assert.True(Point(drawing, "New").Y > Rects(drawing).Single(r => r.Rx > 8 && r.Label == "lan").CenterY / (drawing.Size / 100));
    }

    // ---------------------------------------------------------------------------------------------
    // Click points
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The drawing is square, so x and y share Player's 0 to 100 scale, and a click point is the centre
    /// of its machine's box in it.
    /// </summary>
    [Fact]
    public void Render_PutsEachClickPointAtTheCentreOfItsMachineOnPlayersScale()
    {
        var drawing = TopologyMapRenderer.Render(Topology(
            Network(Lan, "lan"),
            Machine("vm.a", "a", player: "A", on: Lan),
            Machine("vm.b", "b", player: "B", on: Lan),
            Machine("vm.c", "c", player: "C", on: Lan)));

        var svg = XDocument.Parse(drawing.Svg).Root;
        Assert.Equal(svg.Attribute("width").Value, svg.Attribute("height").Value);
        Assert.Equal($"0 0 {svg.Attribute("width").Value} {svg.Attribute("width").Value}", svg.Attribute("viewBox").Value);

        foreach (var point in drawing.ClickPoints)
        {
            var box = Rects(drawing).Single(r => r.Rx == 8 && r.Label == point.Urls[0]);
            Assert.Equal(Math.Round(box.CenterX / drawing.Size * 100, 2), point.XPosition);
            Assert.Equal(Math.Round(box.CenterY / drawing.Size * 100, 2), point.YPosition);
            Assert.Equal(Math.Round(Math.Min(box.Height * 0.6, box.Width / 2) / drawing.Size * 100, 2), point.Radius);
            Assert.InRange(point.XPosition, 0, 100);
        }
    }

    /// <summary>Player shows a map in viewBox "0 8 100 100", hiding the top 8%, so nothing is drawn there.</summary>
    [Fact]
    public void Render_KeepsEverythingOutOfTheTopStripPlayerCrops()
    {
        var drawing = TopologyMapRenderer.Render(Topology(Network(Lan, "lan"), Machine("vm.a", "a", player: "A", on: Lan)),
            new TopologyMapSettings { Title = "Range" });

        var top = Rects(drawing).Skip(1).Min(r => r.Y);
        Assert.True(top / drawing.Size > 0.08);
        Assert.All(drawing.ClickPoints, p => Assert.True(p.YPosition - p.Radius > 8));
    }

    // ---------------------------------------------------------------------------------------------
    // Settings and plan actions
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Render_LeavesOutHiddenNodesAndWhatThePlanDeletes()
    {
        var deleted = Machine("vm.old", "old", player: "Old", on: Lan);
        deleted.Action = "delete";
        var drawing = TopologyMapRenderer.Render(
            Topology(Network(Lan, "lan"), Network(Dmz, "dmz"), deleted,
                Machine("vm.secret", "secret", player: "Secret", on: Lan),
                Machine("vm.web", "web", player: "Web", on: Dmz)),
            new TopologyMapSettings { HiddenNodeIds = ["vm.secret", Lan] });

        Assert.Equal(["Web"], drawing.ClickPoints.Select(p => p.Urls[0]));
        Assert.DoesNotContain("lan", Texts(drawing));
        Assert.Single(Elements(drawing, "line"));
        Assert.Equal(1, drawing.NetworkCount);
    }

    [Fact]
    public void Render_DrawsTheTitleAndALegendOfNetworks()
    {
        var drawing = TopologyMapRenderer.Render(
            Topology(Network(Lan, "lan"), Network(Dmz, "dmz"), Machine("vm.a", "a", player: "A", on: Lan)),
            new TopologyMapSettings { Title = "Blue range", ShowLegend = true });

        var texts = Texts(drawing);
        Assert.Contains("Blue range", texts);
        // each network named twice: on its bar and in the legend
        Assert.Equal(2, texts.Count(t => t == "lan"));
        Assert.Equal(2, texts.Count(t => t == "dmz"));
    }

    [Fact]
    public void Render_CaptionsAMachineWithItsRole()
    {
        var topology = Topology(Network(Lan, "lan"), Machine("vm.dc", "dc", player: "DC", on: Lan));
        topology.Layout["vm.dc"] = new CasterTopologyNodeLayout { Role = "DomainController" };

        Assert.Contains("Domain controller", Texts(TopologyMapRenderer.Render(topology)));
    }

    /// <summary>The same topology gives the same bytes, whatever order Caster lists it in.</summary>
    [Fact]
    public void Render_IsDeterministic()
    {
        CasterTopology Build(bool reversed)
        {
            var nodes = new[]
            {
                Network(Lan, "lan"), Network(Dmz, "dmz"),
                Machine("vm.ws10", "ws10", player: "ws10", on: Lan),
                Machine("vm.ws2", "ws2", player: "ws2", on: Lan),
                Machine("vm.web", "web", player: "web", on: Dmz)
            };
            var topology = Topology(reversed ? nodes.Reverse().ToArray() : nodes);
            if (reversed)
                topology.Edges.Reverse();
            return topology;
        }

        var first = TopologyMapRenderer.Render(Build(false));
        var second = TopologyMapRenderer.Render(Build(true));

        Assert.Equal(first.Svg, second.Svg);
        Assert.Equal(first.ClickPoints.Select(p => (p.XPosition, p.YPosition, p.Radius)), second.ClickPoints.Select(p => (p.XPosition, p.YPosition, p.Radius)));
        // ws2 before ws10, as Caster orders them
        Assert.True(Point(first, "ws2").X < Point(first, "ws10").X);
    }

    [Fact]
    public void ImageUrl_IsTheSvgAsABase64DataUrl()
    {
        var drawing = TopologyMapRenderer.Render(Topology(Network(Lan, "lan")));

        Assert.StartsWith("data:image/svg+xml;base64,", drawing.ImageUrl);
        Assert.Equal(drawing.Svg, Encoding.UTF8.GetString(Convert.FromBase64String(drawing.ImageUrl["data:image/svg+xml;base64,".Length..])));
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------

    private static CasterTopology Topology(params CasterTopologyNode[] nodes)
    {
        var topology = new CasterTopology { WorkspaceId = Guid.NewGuid(), Source = "State" };
        foreach (var node in nodes)
        {
            topology.Nodes.Add(node);
            if (node.IsMachine && node.Properties.TryGetValue("on", out var network))
                topology.Edges.Add(new CasterTopologyEdge { From = node.Id, To = network, Nic = 0 });
        }
        return topology;
    }

    private static CasterTopologyNode Network(string id, string label) =>
        new() { Id = id, Kind = "Network", Label = label };

    private static CasterTopologyNode Machine(string id, string label, string player, string on)
    {
        var node = new CasterTopologyNode
        {
            Id = id,
            Kind = "Machine",
            Label = label,
            Console = player == null ? null : new CasterTopologyConsole { Name = player }
        };
        if (on != null)
            node.Properties["on"] = on;
        return node;
    }

    private static IEnumerable<XElement> Elements(TopologyMapDrawing drawing, string name) =>
        XDocument.Parse(drawing.Svg).Root.Elements(XName.Get(name, "http://www.w3.org/2000/svg"));

    private static List<string> Texts(TopologyMapDrawing drawing) =>
        Elements(drawing, "text").Select(t => t.Value).ToList();

    private static (double X, double Y) Point(TopologyMapDrawing drawing, string name)
    {
        var point = drawing.ClickPoints.Single(p => p.Urls[0] == name);
        return (point.XPosition, point.YPosition);
    }

    private static (double X, double Y) Pixels(TopologyMapDrawing drawing, string name)
    {
        var (x, y) = Point(drawing, name);
        return (x * drawing.Size / 100, y * drawing.Size / 100);
    }

    private sealed record Rect(double X, double Y, double Width, double Height, double Rx, string Label)
    {
        public double CenterX => X + Width / 2;
        public double CenterY => Y + Height / 2;
        public bool Intersects(Rect other) =>
            X < other.X + other.Width && other.X < X + Width && Y < other.Y + other.Height && other.Y < Y + Height;
    }

    /// <summary>Every rect, with the text drawn in it, the background first.</summary>
    private static List<Rect> Rects(TopologyMapDrawing drawing)
    {
        static double D(XElement e, string name) =>
            e.Attribute(name) is { } a ? double.Parse(a.Value, CultureInfo.InvariantCulture) : 0;
        var texts = Elements(drawing, "text").Select(t => (X: D(t, "x"), Y: D(t, "y"), t.Value)).ToList();
        return Elements(drawing, "rect").Select(r =>
        {
            var (x, y, w, h) = (D(r, "x"), D(r, "y"), D(r, "width"), D(r, "height"));
            var label = texts.Where(t => t.X > x && t.X < x + w && t.Y > y && t.Y < y + h).Select(t => t.Value).LastOrDefault();
            return new Rect(x, y, w, h, D(r, "rx"), label);
        }).ToList();
    }
}
