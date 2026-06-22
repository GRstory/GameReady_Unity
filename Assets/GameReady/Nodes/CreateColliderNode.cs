using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Importer;

namespace GameReady.USD
{
    [NodeMetadata("CreateColliderNode", 0)]
    public class CreateColliderNode : Node<CreateColliderNode.InputPort, CreateColliderNode.OutputPort>
    {
        public class InputPort : InputPorts
        {
            public Dictionary<string, GameObject> gameObjects;
            public Dictionary<string, string> colliderPrimPaths;
        }

        public class OutputPort : OutputPorts
        {
            public Dictionary<string, GameObject> gameObjects;
        }

        public override void Run()
        {
            Output.gameObjects = Input.gameObjects;

            if (Input.colliderPrimPaths == null || Input.gameObjects == null)
                return;

            foreach (var kvp in Input.colliderPrimPaths)
            {
                if (!Input.gameObjects.TryGetValue(kvp.Key, out var go))
                    continue;

                switch (kvp.Value)
                {
                    case "Box":     go.AddComponent<BoxCollider>();     break;
                    case "Sphere":  go.AddComponent<SphereCollider>();  break;
                    case "Capsule": go.AddComponent<CapsuleCollider>(); break;
                    case "Mesh":    go.AddComponent<MeshCollider>();    break;
                    default:
                        Debug.LogWarning($"[GameReady] 알 수 없는 Collider 타입 '{kvp.Value}' (prim: {kvp.Key})");
                        break;
                }
            }
        }
    }
}
