using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Importer;

namespace GameReady.USD
{
    [NodeMetadata("CreateNavMeshAgentNode", 0)]
    public class CreateNavMeshAgentNode : Node<CreateNavMeshAgentNode.InputPort, CreateNavMeshAgentNode.OutputPort>
    {
        public class InputPort : InputPorts
        {
            public Dictionary<string, GameObject> gameObjects;
            public HashSet<string> npcPrimPaths;
        }

        public class OutputPort : OutputPorts
        {
            public Dictionary<string, GameObject> gameObjects;
        }

        public override void Run()
        {
            Output.gameObjects = Input.gameObjects;

            if (Input.npcPrimPaths == null || Input.gameObjects == null)
                return;

            foreach (var path in Input.npcPrimPaths)
            {
                if (Input.gameObjects.TryGetValue(path, out var go))
                    go.AddComponent<NavMeshAgent>();
            }
        }
    }
}
