using System.Collections.Generic;
using System.Linq;
using GameReady.USD;
using Unity.Importer.USD;
using UnityEditor;
using UnityEngine;
using UnityEngine.Importer;

namespace GameReady.USD.Editor
{
    public static class GameReadyGraphBuilder
    {
        private const string k_SourceGraphPath =
            "Packages/com.unity.importer.usd/Unity.Importer.USD.Editor/ImportGraph/usdImporter.asset";

        private const string k_OutputGraphPath =
            "Assets/GameReady/gameReadyUsdImporter.asset";

        [MenuItem("GameReady/Build USD Importer Graph")]
        public static void BuildGraph()
        {
            var sourceGraph = AssetDatabase.LoadAssetAtPath<ImporterGraph>(k_SourceGraphPath);
            if (sourceGraph == null)
            {
                Debug.LogError($"[GameReady] 기본 그래프를 찾을 수 없습니다: {k_SourceGraphPath}");
                return;
            }

            // 기존 그래프를 복사해서 커스텀 그래프 생성
            var graph = Object.Instantiate(sourceGraph);

            // 새 노드 생성
            var filterNPCNode      = new FilterNPCPrimsNode();
            var createAgentNode    = new CreateNavMeshAgentNode();
            var filterColliderNode = new FilterColliderPrimsNode();
            var createColliderNode = new CreateColliderNode();

            // aggregator_final → BuildHierarchyNode.gameObjects 엣지 찾기
            var buildHierarchyNode = graph.Nodes.OfType<BuildHierarchyNode>().FirstOrDefault();
            if (buildHierarchyNode == null)
            {
                Debug.LogError("[GameReady] BuildHierarchyNode를 그래프에서 찾을 수 없습니다.");
                return;
            }

            // BuildHierarchyNode에 들어오는 엣지 중 aggregator에서 오는 것 (stage 엣지가 아닌 것)
            var hierarchyEdge = graph.Edges.FirstOrDefault(e =>
                e.Destination.Node == (INode<InputPorts, OutputPorts>)buildHierarchyNode &&
                e.Origin.Node is IDictionaryAggregatorNode<Dictionary<string, GameObject>>);

            if (hierarchyEdge.Origin.Node == null)
            {
                Debug.LogError("[GameReady] aggregator → BuildHierarchyNode 엣지를 찾을 수 없습니다.");
                return;
            }

            var finalAggregator = hierarchyEdge.Origin.Node;

            // UsdStageOpenNode 찾기
            var stageOpenNode = graph.Nodes.OfType<UsdStageOpenNode>().FirstOrDefault();
            if (stageOpenNode == null)
            {
                Debug.LogError("[GameReady] UsdStageOpenNode를 그래프에서 찾을 수 없습니다.");
                return;
            }

            // 기존 aggregator → buildHierarchyNode 엣지 제거
            graph.RemoveEdge(hierarchyEdge);

            // 새 노드 등록
            graph.AddNode(filterNPCNode);
            graph.AddNode(createAgentNode);
            graph.AddNode(filterColliderNode);
            graph.AddNode(createColliderNode);

            // UsdStageOpenNode.stage → FilterNPCPrimsNode.stage
            graph.AddEdge(new Edge(
                stageOpenNode,  nameof(UsdStageOpenNode.Output.stage),
                filterNPCNode,  nameof(FilterNPCPrimsNode.Input.stage)));

            // finalAggregator.output → CreateNavMeshAgentNode.gameObjects
            graph.AddEdge(new Edge(
                finalAggregator,  nameof(IDictionaryAggregatorNode<Dictionary<string, GameObject>>.Output.output),
                createAgentNode,  nameof(CreateNavMeshAgentNode.Input.gameObjects)));

            // FilterNPCPrimsNode.npcPrimPaths → CreateNavMeshAgentNode.npcPrimPaths
            graph.AddEdge(new Edge(
                filterNPCNode,  nameof(FilterNPCPrimsNode.Output.npcPrimPaths),
                createAgentNode, nameof(CreateNavMeshAgentNode.Input.npcPrimPaths)));

            // CreateNavMeshAgentNode.gameObjects → CreateColliderNode.gameObjects
            graph.AddEdge(new Edge(
                createAgentNode,    nameof(CreateNavMeshAgentNode.Output.gameObjects),
                createColliderNode, nameof(CreateColliderNode.Input.gameObjects)));

            // UsdStageOpenNode.stage → FilterColliderPrimsNode.stage
            graph.AddEdge(new Edge(
                stageOpenNode,      nameof(UsdStageOpenNode.Output.stage),
                filterColliderNode, nameof(FilterColliderPrimsNode.Input.stage)));

            // FilterColliderPrimsNode.colliderPrimPaths → CreateColliderNode.colliderPrimPaths
            graph.AddEdge(new Edge(
                filterColliderNode, nameof(FilterColliderPrimsNode.Output.colliderPrimPaths),
                createColliderNode, nameof(CreateColliderNode.Input.colliderPrimPaths)));

            // CreateColliderNode.gameObjects → BuildHierarchyNode.gameObjects
            graph.AddEdge(new Edge(
                createColliderNode, nameof(CreateColliderNode.Output.gameObjects),
                buildHierarchyNode, nameof(BuildHierarchyNode.Input.gameObjects)));

            // 에셋 저장
            System.IO.Directory.CreateDirectory("Assets/GameReady");
            AssetDatabase.CreateAsset(graph, k_OutputGraphPath);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"[GameReady] 커스텀 임포터 그래프 저장 완료: {k_OutputGraphPath}");
        }
    }
}
