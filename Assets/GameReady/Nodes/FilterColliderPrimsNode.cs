using System.Collections.Generic;
using pxr;
using UnityEngine.Importer;

namespace GameReady.USD
{
    [NodeMetadata("FilterColliderPrimsNode", 0)]
    public class FilterColliderPrimsNode : Node<FilterColliderPrimsNode.InputPort, FilterColliderPrimsNode.OutputPort>
    {
        public class InputPort : InputPorts
        {
            public UsdStage stage;
        }

        public class OutputPort : OutputPorts
        {
            public Dictionary<string, string> colliderPrimPaths = new();
        }

        public override void Run()
        {
            var colliderToken = new TfToken("gameReady:hasCollider");
            var prims = Input.stage.Traverse(Usd.UsdTraverseInstanceProxies());
            foreach (var prim in prims)
            {
                var attr = prim.GetAttribute(colliderToken);
                if (!attr.IsValid() || !attr.HasValue())
                    continue;

                var value = Vt.VtValueToTfToken(attr.Get(UsdTimeCode.EarliestTime())).GetString();
                if (string.IsNullOrEmpty(value))
                    continue;

                Output.colliderPrimPaths[prim.GetPath().ToString()] = value;
            }
        }
    }
}
