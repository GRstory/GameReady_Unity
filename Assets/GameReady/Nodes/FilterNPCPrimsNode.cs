using System.Collections.Generic;
using pxr;
using UnityEngine.Importer;

namespace GameReady.USD
{
    [NodeMetadata("FilterNPCPrimsNode", 0)]
    public class FilterNPCPrimsNode : Node<FilterNPCPrimsNode.InputPort, FilterNPCPrimsNode.OutputPort>
    {
        public class InputPort : InputPorts
        {
            public UsdStage stage;
        }

        public class OutputPort : OutputPorts
        {
            public HashSet<string> npcPrimPaths = new();
        }

        public override void Run()
        {
            var npcToken = new TfToken("gameReady:isNPC");
            var prims = Input.stage.Traverse(Usd.UsdTraverseInstanceProxies());
            foreach (var prim in prims)
            {
                var attr = prim.GetAttribute(npcToken);
                if (attr.IsValid() && attr.HasValue())
                    Output.npcPrimPaths.Add(prim.GetPath().ToString());
            }
        }
    }
}
