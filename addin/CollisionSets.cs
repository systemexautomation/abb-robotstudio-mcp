using System;
using System.Linq;
using ABB.Robotics.RobotStudio;
using ABB.Robotics.RobotStudio.Stations;
using Newtonsoft.Json.Linq;

namespace ClaudeBridge
{
    // Creation of RobotStudio collision sets (Simulation > Collision sets).
    public partial class ClaudeBridgeAddIn
    {
        // ── POST /collision/set ───────────────────────────────────────────────
        // Body: {"name":"Robot_vs_cellule","first":["IRB460_110_240__01"],"second":["Cloture_1","Convoyeur"],
        //        "color":[255,0,0],"nearMiss":0,"replace":true}
        // first / second: names of existing graphic objects (Part, Group, Mechanism...). The set is created
        // active, with highlighting of the colliding objects. nearMiss in mm (0 = collisions only).
        // replace=true removes an existing set of the same name first. One undo step.
        private object CreateCollisionSet(string body)
        {
            var station = GetActiveStation();
            var req = JObject.Parse(body ?? "{}");
            var name = (string)req["name"];
            var first = (req["first"] as JArray)?.Select(t => (string)t).ToList();
            var second = (req["second"] as JArray)?.Select(t => (string)t).ToList();
            if (string.IsNullOrWhiteSpace(name) || first == null || second == null || first.Count == 0 || second.Count == 0)
                throw new Exception("Required: name, first[] and second[] (names of graphic objects)");
            bool replace = req["replace"] != null && req["replace"].Type != JTokenType.Null && (bool)req["replace"];

            GraphicComponent Find(string n)
            {
                GraphicComponent found = null;
                void Walk(ProjectObject parent, int depth)
                {
                    foreach (var child in ChildrenOf(parent))
                    {
                        if (found != null) return;
                        if (child is GraphicComponent g && (g.Name ?? "").Equals(n, StringComparison.OrdinalIgnoreCase)) { found = g; return; }
                        if (depth < MaxMaxDepth) Walk(child, depth + 1);
                    }
                }
                Walk(station, 0);
                if (found == null) throw new Exception("No graphic object named '" + n + "'");
                return found;
            }
            var groupA = first.Select(Find).ToList();
            var groupB = second.Select(Find).ToList();

            var color = System.Drawing.Color.Red;
            var c = req["color"] as JArray;
            if (c != null)
            {
                if (c.Count != 3) throw new Exception("color must be [r,g,b] (0-255)");
                color = System.Drawing.Color.FromArgb((int)c[0], (int)c[1], (int)c[2]);
            }
            double nearMissMm = req["nearMiss"] != null && req["nearMiss"].Type != JTokenType.Null ? (double)req["nearMiss"] : 0;

            var undo = Project.UndoContext;
            undo?.BeginUndoStep("ClaudeBridge: collision set " + name);
            try
            {
                var existing = station.CollisionSets.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
                if (existing != null)
                {
                    if (!replace) throw new Exception("A collision set named '" + name + "' already exists (pass replace=true to replace it)");
                    station.CollisionSets.Remove(existing);
                }
                var set = new CollisionSet { Name = name };
                foreach (var g in groupA) set.FirstGroup.Add(g);
                foreach (var g in groupB) set.SecondGroup.Add(g);
                set.Highlight = true;
                set.Active = true;
                set.CollisionColor = color;
                set.NearMissDistance = nearMissMm * 0.001;
                station.CollisionSets.Add(set);
            }
            finally
            {
                undo?.EndUndoStep();
            }
            return new
            {
                success = true,
                name,
                first,
                second,
                nearMissMm,
                note = "Set created, active, highlighting on. Ctrl+Z undoes it. The station is not saved."
            };
        }
    }
}
