using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace GameReady.CoACDMeshCutter
{
    /// <summary>
    /// Clips a triangle mesh against convex MeshCollider volumes. All calculations are
    /// performed in the source MeshFilter's local space.
    /// </summary>
    public static class ConvexMeshClipper
    {
        public readonly struct Settings
        {
            public readonly bool CapSections;
            public readonly float PlaneTolerance;
            public readonly float WeldTolerance;
            public readonly float MinimumTriangleArea;
            public readonly float CapUvScale;

            public Settings(
                bool capSections,
                float planeTolerance,
                float weldTolerance,
                float minimumTriangleArea,
                float capUvScale)
            {
                CapSections = capSections;
                PlaneTolerance = Mathf.Max(0.0000001f, planeTolerance);
                WeldTolerance = Mathf.Max(0.0000001f, weldTolerance);
                MinimumTriangleArea = Mathf.Max(0f, minimumTriangleArea);
                CapUvScale = capUvScale;
            }
        }

        private struct Vertex
        {
            public Vector3 Position;
            public Vector3 Normal;
            public Vector4 Tangent;
            public Vector2 Uv;
            public Color32 Color;

            public static Vertex Lerp(in Vertex a, in Vertex b, float t)
            {
                return new Vertex
                {
                    Position = Vector3.LerpUnclamped(a.Position, b.Position, t),
                    Normal = Vector3.LerpUnclamped(a.Normal, b.Normal, t).normalized,
                    Tangent = Vector4.LerpUnclamped(a.Tangent, b.Tangent, t),
                    Uv = Vector2.LerpUnclamped(a.Uv, b.Uv, t),
                    Color = Color32.Lerp(a.Color, b.Color, t)
                };
            }
        }

        private readonly struct Triangle
        {
            public readonly Vertex A;
            public readonly Vertex B;
            public readonly Vertex C;
            public readonly int SubMesh;

            public Triangle(in Vertex a, in Vertex b, in Vertex c, int subMesh)
            {
                A = a;
                B = b;
                C = c;
                SubMesh = subMesh;
            }
        }

        private readonly struct ClipPlane
        {
            public readonly Vector3 Normal;
            public readonly float Distance;

            public ClipPlane(Vector3 normal, Vector3 point)
            {
                Normal = normal.normalized;
                Distance = -Vector3.Dot(Normal, point);
            }

            public float GetDistance(Vector3 point) => Vector3.Dot(Normal, point) + Distance;
        }

        private readonly struct Segment
        {
            public readonly Vector3 A;
            public readonly Vector3 B;

            public Segment(Vector3 a, Vector3 b)
            {
                A = a;
                B = b;
            }
        }

        private readonly struct GridKey : IEquatable<GridKey>
        {
            public readonly int X;
            public readonly int Y;
            public readonly int Z;

            public GridKey(int x, int y, int z)
            {
                X = x;
                Y = y;
                Z = z;
            }

            public bool Equals(GridKey other) => X == other.X && Y == other.Y && Z == other.Z;
            public override bool Equals(object obj) => obj is GridKey other && Equals(other);
            public override int GetHashCode()
            {
                unchecked
                {
                    var hash = X;
                    hash = hash * 397 ^ Y;
                    return hash * 397 ^ Z;
                }
            }
        }

        /// <summary>
        /// Creates one render mesh for the portion of <paramref name="sourceMesh"/> inside
        /// <paramref name="collider"/>. Returns null when the intersection is empty.
        /// </summary>
        public static Mesh Cut(
            Mesh sourceMesh,
            Transform sourceTransform,
            MeshCollider collider,
            in Settings settings,
            out string error)
        {
            error = null;
            if (sourceMesh == null || sourceTransform == null)
            {
                error = "A source mesh and source transform are required.";
                return null;
            }

            if (collider == null || collider.sharedMesh == null)
            {
                error = "The collider does not have a mesh.";
                return null;
            }

            if (!collider.convex)
            {
                error = $"Collider '{collider.name}' must be convex.";
                return null;
            }

            try
            {
                var triangles = ReadSourceTriangles(sourceMesh);
                var planes = BuildColliderPlanes(sourceTransform, collider, settings.PlaneTolerance);
                if (planes.Count < 4)
                {
                    error = $"Collider '{collider.name}' does not contain a valid convex volume.";
                    return null;
                }

                var capSubMesh = sourceMesh.subMeshCount;
                foreach (var plane in planes)
                {
                    triangles = ClipAgainstPlane(triangles, plane, capSubMesh, settings);
                    if (triangles.Count == 0)
                        return null;
                }

                if (settings.CapSections)
                    SealOpenCutBoundaries(triangles, planes, capSubMesh, settings);

                return BuildMesh(sourceMesh, triangles, settings.CapSections);
            }
            catch (Exception exception)
            {
                error = $"Could not cut with collider '{collider.name}': {exception.Message}";
                return null;
            }
        }

        private static List<Triangle> ReadSourceTriangles(Mesh mesh)
        {
            var positions = mesh.vertices;
            var normals = mesh.normals;
            var tangents = mesh.tangents;
            var uv = mesh.uv;
            var colors = mesh.colors32;
            var hasNormals = normals != null && normals.Length == positions.Length;
            var hasTangents = tangents != null && tangents.Length == positions.Length;
            var hasUv = uv != null && uv.Length == positions.Length;
            var hasColors = colors != null && colors.Length == positions.Length;
            var vertices = new Vertex[positions.Length];

            for (var i = 0; i < positions.Length; i++)
            {
                vertices[i] = new Vertex
                {
                    Position = positions[i],
                    Normal = hasNormals ? normals[i] : Vector3.zero,
                    Tangent = hasTangents ? tangents[i] : new Vector4(1f, 0f, 0f, 1f),
                    Uv = hasUv ? uv[i] : Vector2.zero,
                    Color = hasColors ? colors[i] : new Color32(255, 255, 255, 255)
                };
            }

            var result = new List<Triangle>(mesh.triangles.Length / 3);
            for (var subMesh = 0; subMesh < mesh.subMeshCount; subMesh++)
            {
                if (mesh.GetTopology(subMesh) != MeshTopology.Triangles)
                    continue;

                var indices = mesh.GetTriangles(subMesh);
                for (var i = 0; i + 2 < indices.Length; i += 3)
                    result.Add(new Triangle(vertices[indices[i]], vertices[indices[i + 1]], vertices[indices[i + 2]], subMesh));
            }

            return result;
        }

        private static List<ClipPlane> BuildColliderPlanes(
            Transform sourceTransform,
            MeshCollider collider,
            float tolerance)
        {
            var colliderMesh = collider.sharedMesh;
            var localToSource = sourceTransform.worldToLocalMatrix * collider.transform.localToWorldMatrix;
            var sourceVertices = colliderMesh.vertices;
            var transformed = new Vector3[sourceVertices.Length];
            var center = Vector3.zero;
            for (var i = 0; i < sourceVertices.Length; i++)
            {
                transformed[i] = localToSource.MultiplyPoint3x4(sourceVertices[i]);
                center += transformed[i];
            }

            if (transformed.Length == 0)
                return new List<ClipPlane>();
            center /= transformed.Length;

            var indices = colliderMesh.triangles;
            var planes = new List<ClipPlane>();
            for (var i = 0; i + 2 < indices.Length; i += 3)
            {
                var a = transformed[indices[i]];
                var b = transformed[indices[i + 1]];
                var c = transformed[indices[i + 2]];
                var normal = Vector3.Cross(b - a, c - a);
                if (normal.sqrMagnitude < 0.000000000001f)
                    continue;
                normal.Normalize();

                // A convex hull's center must be behind every outward-facing plane.
                if (Vector3.Dot(normal, center - a) > 0f)
                    normal = -normal;

                var candidate = new ClipPlane(normal, a);
                var duplicate = false;
                foreach (var plane in planes)
                {
                    if (Vector3.Dot(plane.Normal, candidate.Normal) > 0.99999f &&
                        Mathf.Abs(plane.Distance - candidate.Distance) <= tolerance)
                    {
                        duplicate = true;
                        break;
                    }
                }

                if (!duplicate)
                    planes.Add(candidate);
            }

            return planes;
        }

        private static List<Triangle> ClipAgainstPlane(
            List<Triangle> input,
            in ClipPlane plane,
            int capSubMesh,
            in Settings settings)
        {
            var output = new List<Triangle>(input.Count + 16);
            var intersections = settings.CapSections ? new List<Segment>() : null;
            var polygon = new List<Vertex>(4);

            foreach (var triangle in input)
            {
                polygon.Clear();
                var hadInsideVertex = false;
                var hadOutsideVertex = false;
                var source = new[] { triangle.A, triangle.B, triangle.C };
                for (var i = 0; i < 3; i++)
                {
                    var current = source[i];
                    var next = source[(i + 1) % 3];
                    var currentDistance = plane.GetDistance(current.Position);
                    var nextDistance = plane.GetDistance(next.Position);
                    var currentInside = currentDistance <= settings.PlaneTolerance;
                    var nextInside = nextDistance <= settings.PlaneTolerance;
                    hadInsideVertex |= currentInside;
                    hadOutsideVertex |= !currentInside;

                    if (currentInside)
                        polygon.Add(current);

                    if (currentInside == nextInside)
                        continue;

                    var denominator = currentDistance - nextDistance;
                    var t = Mathf.Abs(denominator) > Mathf.Epsilon ? currentDistance / denominator : 0f;
                    var intersection = Vertex.Lerp(current, next, Mathf.Clamp01(t));
                    // Snap onto the plane to make contour welding deterministic.
                    intersection.Position -= plane.Normal * plane.GetDistance(intersection.Position);
                    polygon.Add(intersection);
                }

                // Read the actual boundary edge from the clipped polygon. Using only the two
                // calculated crossing points misses cases where the plane passes through an
                // existing source vertex or edge, which is common for CoACD hull planes.
                if (intersections != null && hadInsideVertex && hadOutsideVertex && polygon.Count >= 2)
                {
                    var planeEdgeTolerance = settings.PlaneTolerance * 2f;
                    var minimumEdgeLengthSquared = settings.WeldTolerance * settings.WeldTolerance;
                    for (var i = 0; i < polygon.Count; i++)
                    {
                        var next = (i + 1) % polygon.Count;
                        var a = polygon[i].Position;
                        var b = polygon[next].Position;
                        if (Mathf.Abs(plane.GetDistance(a)) > planeEdgeTolerance ||
                            Mathf.Abs(plane.GetDistance(b)) > planeEdgeTolerance ||
                            (a - b).sqrMagnitude <= minimumEdgeLengthSquared)
                        {
                            continue;
                        }

                        a -= plane.Normal * plane.GetDistance(a);
                        b -= plane.Normal * plane.GetDistance(b);
                        intersections.Add(new Segment(a, b));
                    }
                }

                if (polygon.Count < 3)
                    continue;

                for (var i = 1; i < polygon.Count - 1; i++)
                    AddTriangleIfValid(output, polygon[0], polygon[i], polygon[i + 1], triangle.SubMesh, settings.MinimumTriangleArea);
            }

            if (settings.CapSections && intersections != null && intersections.Count > 2)
                AddCaps(output, intersections, plane, capSubMesh, settings);

            return output;
        }

        private static void AddCaps(
            List<Triangle> triangles,
            List<Segment> segments,
            in ClipPlane plane,
            int capSubMesh,
            in Settings settings)
        {
            var loops = BuildLoops(segments, settings.WeldTolerance);
            var axis = Mathf.Abs(plane.Normal.y) < 0.9f ? Vector3.up : Vector3.right;
            var tangent = Vector3.Cross(axis, plane.Normal).normalized;
            var bitangent = Vector3.Cross(plane.Normal, tangent).normalized;

            foreach (var loop in loops)
            {
                SimplifyLoop(loop, settings.WeldTolerance);
                if (loop.Count < 3)
                    continue;

                var points2D = new List<Vector2>(loop.Count);
                foreach (var point in loop)
                    points2D.Add(new Vector2(Vector3.Dot(point, tangent), Vector3.Dot(point, bitangent)));

                if (SignedArea(points2D) < 0f)
                {
                    loop.Reverse();
                    points2D.Reverse();
                }

                var capIndices = Triangulate(points2D, settings.WeldTolerance * settings.WeldTolerance);
                for (var i = 0; i + 2 < capIndices.Count; i += 3)
                {
                    var a = MakeCapVertex(loop[capIndices[i]], plane.Normal, tangent, bitangent, settings.CapUvScale);
                    var b = MakeCapVertex(loop[capIndices[i + 1]], plane.Normal, tangent, bitangent, settings.CapUvScale);
                    var c = MakeCapVertex(loop[capIndices[i + 2]], plane.Normal, tangent, bitangent, settings.CapUvScale);
                    AddTriangleIfValid(triangles, a, b, c, capSubMesh, settings.MinimumTriangleArea);
                }
            }
        }

        private static void SealOpenCutBoundaries(
            List<Triangle> triangles,
            IReadOnlyList<ClipPlane> planes,
            int capSubMesh,
            in Settings settings)
        {
            // A cap can remain partially open when several contours touch at one source
            // vertex or when a plane runs along an existing source edge. Inspect the final
            // topology and fill only boundary loops that lie on a collider cutting plane.
            // Two passes also close a corner shared by two cutting planes.
            for (var pass = 0; pass < 2; pass++)
            {
                var nodes = new List<Vector3>();
                var nodeSamples = new List<int>();
                var spatialBuckets = new Dictionary<GridKey, List<int>>();
                var edgeUseCount = new Dictionary<ulong, int>();

                foreach (var triangle in triangles)
                {
                    var a = FindOrAddNode(nodes, nodeSamples, spatialBuckets, triangle.A.Position, settings.WeldTolerance);
                    var b = FindOrAddNode(nodes, nodeSamples, spatialBuckets, triangle.B.Position, settings.WeldTolerance);
                    var c = FindOrAddNode(nodes, nodeSamples, spatialBuckets, triangle.C.Position, settings.WeldTolerance);
                    CountEdge(edgeUseCount, a, b);
                    CountEdge(edgeUseCount, b, c);
                    CountEdge(edgeUseCount, c, a);
                }

                var planeSegments = new List<Segment>[planes.Count];
                for (var i = 0; i < planeSegments.Length; i++)
                    planeSegments[i] = new List<Segment>();

                var planeTolerance = Mathf.Max(settings.PlaneTolerance * 4f, settings.WeldTolerance * 2f);
                foreach (var pair in edgeUseCount)
                {
                    if (pair.Value != 1)
                        continue;
                    DecodeEdgeKey(pair.Key, out var a, out var b);
                    var pointA = nodes[a];
                    var pointB = nodes[b];
                    for (var planeIndex = 0; planeIndex < planes.Count; planeIndex++)
                    {
                        var plane = planes[planeIndex];
                        if (Mathf.Abs(plane.GetDistance(pointA)) > planeTolerance ||
                            Mathf.Abs(plane.GetDistance(pointB)) > planeTolerance)
                        {
                            continue;
                        }

                        var projectedA = pointA - plane.Normal * plane.GetDistance(pointA);
                        var projectedB = pointB - plane.Normal * plane.GetDistance(pointB);
                        planeSegments[planeIndex].Add(new Segment(projectedA, projectedB));
                    }
                }

                var triangleCountBefore = triangles.Count;
                for (var planeIndex = 0; planeIndex < planes.Count; planeIndex++)
                {
                    if (planeSegments[planeIndex].Count > 2)
                        AddCaps(triangles, planeSegments[planeIndex], planes[planeIndex], capSubMesh, settings);
                }

                if (triangles.Count == triangleCountBefore)
                    break;
            }
        }

        private static void CountEdge(Dictionary<ulong, int> edgeUseCount, int a, int b)
        {
            if (a == b)
                return;
            var key = MakeEdgeKey(a, b);
            edgeUseCount.TryGetValue(key, out var count);
            edgeUseCount[key] = count + 1;
        }

        private static Vertex MakeCapVertex(Vector3 position, Vector3 normal, Vector3 tangent, Vector3 bitangent, float uvScale)
        {
            return new Vertex
            {
                Position = position,
                Normal = normal,
                Tangent = new Vector4(tangent.x, tangent.y, tangent.z, 1f),
                Uv = new Vector2(Vector3.Dot(position, tangent), Vector3.Dot(position, bitangent)) * uvScale,
                Color = new Color32(255, 255, 255, 255)
            };
        }

        private static List<List<Vector3>> BuildLoops(List<Segment> source, float tolerance)
        {
            // Build a welded edge graph first. Walking the raw segments in input order is
            // unreliable: several source triangles can produce numerically different copies
            // of the same contour vertex, or four edges can meet at an existing mesh vertex.
            var nodes = new List<Vector3>();
            var nodeSamples = new List<int>();
            var spatialBuckets = new Dictionary<GridKey, List<int>>();
            var edges = new HashSet<ulong>();

            foreach (var segment in source)
            {
                var a = FindOrAddNode(nodes, nodeSamples, spatialBuckets, segment.A, tolerance);
                var b = FindOrAddNode(nodes, nodeSamples, spatialBuckets, segment.B, tolerance);
                if (a == b)
                    continue;
                edges.Add(MakeEdgeKey(a, b));
            }

            var adjacency = new List<List<int>>(nodes.Count);
            for (var i = 0; i < nodes.Count; i++)
                adjacency.Add(new List<int>(2));
            foreach (var edge in edges)
            {
                DecodeEdgeKey(edge, out var a, out var b);
                adjacency[a].Add(b);
                adjacency[b].Add(a);
            }

            var unusedEdges = new HashSet<ulong>(edges);
            var loops = new List<List<Vector3>>();
            while (unusedEdges.Count > 0)
            {
                ulong firstEdge = 0;
                foreach (var edge in unusedEdges)
                {
                    firstEdge = edge;
                    break;
                }

                DecodeEdgeKey(firstEdge, out var start, out var current);
                var previous = start;
                var loopIndices = new List<int> { start, current };
                unusedEdges.Remove(firstEdge);
                var closed = false;
                var guard = unusedEdges.Count + 2;

                while (guard-- > 0)
                {
                    var next = ChooseNextNode(previous, current, start, nodes, adjacency, unusedEdges);
                    if (next < 0)
                        break;

                    unusedEdges.Remove(MakeEdgeKey(current, next));
                    if (next == start)
                    {
                        closed = true;
                        break;
                    }

                    loopIndices.Add(next);
                    previous = current;
                    current = next;
                }

                if (!closed || loopIndices.Count < 3)
                    continue;

                var loop = new List<Vector3>(loopIndices.Count);
                foreach (var index in loopIndices)
                    loop.Add(nodes[index]);
                loops.Add(loop);
            }

            return loops;
        }

        private static int FindOrAddNode(
            List<Vector3> nodes,
            List<int> nodeSamples,
            Dictionary<GridKey, List<int>> spatialBuckets,
            Vector3 position,
            float tolerance)
        {
            var toleranceSquared = tolerance * tolerance;
            var key = PositionToGrid(position, tolerance);
            for (var x = -1; x <= 1; x++)
            {
                for (var y = -1; y <= 1; y++)
                {
                    for (var z = -1; z <= 1; z++)
                    {
                        var nearbyKey = new GridKey(key.X + x, key.Y + y, key.Z + z);
                        if (!spatialBuckets.TryGetValue(nearbyKey, out var nearbyNodes))
                            continue;
                        foreach (var index in nearbyNodes)
                        {
                            if ((nodes[index] - position).sqrMagnitude > toleranceSquared)
                                continue;

                            var sampleCount = nodeSamples[index];
                            nodes[index] = (nodes[index] * sampleCount + position) / (sampleCount + 1);
                            nodeSamples[index] = sampleCount + 1;
                            return index;
                        }
                    }
                }
            }

            nodes.Add(position);
            nodeSamples.Add(1);
            var newIndex = nodes.Count - 1;
            if (!spatialBuckets.TryGetValue(key, out var bucket))
            {
                bucket = new List<int>();
                spatialBuckets.Add(key, bucket);
            }
            bucket.Add(newIndex);
            return newIndex;
        }

        private static GridKey PositionToGrid(Vector3 position, float cellSize)
        {
            return new GridKey(
                Mathf.FloorToInt(position.x / cellSize),
                Mathf.FloorToInt(position.y / cellSize),
                Mathf.FloorToInt(position.z / cellSize));
        }

        private static int ChooseNextNode(
            int previous,
            int current,
            int start,
            IReadOnlyList<Vector3> nodes,
            IReadOnlyList<List<int>> adjacency,
            HashSet<ulong> unusedEdges)
        {
            var best = -1;
            var bestScore = float.NegativeInfinity;
            var incoming = (nodes[current] - nodes[previous]).normalized;
            foreach (var candidate in adjacency[current])
            {
                if (!unusedEdges.Contains(MakeEdgeKey(current, candidate)))
                    continue;

                // Prefer the straightest continuation at branch vertices. This keeps two
                // contours that merely touch at a source vertex from being stitched together.
                var outgoing = (nodes[candidate] - nodes[current]).normalized;
                var score = Vector3.Dot(incoming, outgoing);
                if (candidate == start)
                    score += 0.0001f;
                if (score <= bestScore)
                    continue;
                bestScore = score;
                best = candidate;
            }
            return best;
        }

        private static ulong MakeEdgeKey(int a, int b)
        {
            var min = (uint)Mathf.Min(a, b);
            var max = (uint)Mathf.Max(a, b);
            return ((ulong)min << 32) | max;
        }

        private static void DecodeEdgeKey(ulong key, out int a, out int b)
        {
            a = (int)(key >> 32);
            b = (int)(key & uint.MaxValue);
        }

        private static void SimplifyLoop(List<Vector3> loop, float tolerance)
        {
            var toleranceSquared = tolerance * tolerance;
            var changed = true;
            var guard = loop.Count;
            while (changed && loop.Count >= 3 && guard-- > 0)
            {
                changed = false;
                for (var i = loop.Count - 1; i >= 0 && loop.Count >= 3; i--)
                {
                    var previous = loop[(i - 1 + loop.Count) % loop.Count];
                    var current = loop[i];
                    var next = loop[(i + 1) % loop.Count];
                    var previousToCurrent = current - previous;
                    var currentToNext = next - current;
                    var previousToNext = next - previous;
                    var duplicate = previousToCurrent.sqrMagnitude <= toleranceSquared;
                    var collinear = false;
                    if (!duplicate && previousToNext.sqrMagnitude > toleranceSquared &&
                        Vector3.Dot(previousToCurrent, currentToNext) >= 0f)
                    {
                        var perpendicularDistance =
                            Vector3.Cross(previousToCurrent, previousToNext).magnitude /
                            previousToNext.magnitude;
                        collinear = perpendicularDistance <= tolerance;
                    }

                    if (!duplicate && !collinear)
                        continue;
                    loop.RemoveAt(i);
                    changed = true;
                }
            }
        }

        private static float SignedArea(IReadOnlyList<Vector2> points)
        {
            var area = 0f;
            for (var i = 0; i < points.Count; i++)
            {
                var next = (i + 1) % points.Count;
                area += points[i].x * points[next].y - points[next].x * points[i].y;
            }
            return area * 0.5f;
        }

        private static List<int> Triangulate(IReadOnlyList<Vector2> points, float epsilon)
        {
            var result = new List<int>((points.Count - 2) * 3);
            var polygon = new List<int>(points.Count);
            for (var i = 0; i < points.Count; i++)
                polygon.Add(i);

            var guard = points.Count * points.Count;
            while (polygon.Count > 3 && guard-- > 0)
            {
                var earFound = false;
                for (var i = 0; i < polygon.Count; i++)
                {
                    var previous = polygon[(i - 1 + polygon.Count) % polygon.Count];
                    var current = polygon[i];
                    var next = polygon[(i + 1) % polygon.Count];
                    if (Cross(points[previous], points[current], points[next]) <= epsilon)
                        continue;

                    var containsPoint = false;
                    for (var j = 0; j < polygon.Count; j++)
                    {
                        var candidate = polygon[j];
                        if (candidate == previous || candidate == current || candidate == next)
                            continue;
                        if (PointInTriangle(points[candidate], points[previous], points[current], points[next], epsilon))
                        {
                            containsPoint = true;
                            break;
                        }
                    }

                    if (containsPoint)
                        continue;
                    result.Add(previous);
                    result.Add(current);
                    result.Add(next);
                    polygon.RemoveAt(i);
                    earFound = true;
                    break;
                }

                if (!earFound)
                    break;
            }

            if (polygon.Count == 3)
            {
                result.Add(polygon[0]);
                result.Add(polygon[1]);
                result.Add(polygon[2]);
            }
            return result;
        }

        private static float Cross(Vector2 a, Vector2 b, Vector2 c)
            => (b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x);

        private static bool PointInTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c, float epsilon)
        {
            var ab = Cross(a, b, p);
            var bc = Cross(b, c, p);
            var ca = Cross(c, a, p);
            // Points on an ear edge do not invalidate the ear. Treating boundary points as
            // interior can make triangulation stop halfway on contours containing collinear
            // samples, leaving a visibly missing section of the cap.
            return ab > epsilon && bc > epsilon && ca > epsilon;
        }

        private static void AddTriangleIfValid(
            List<Triangle> destination,
            in Vertex a,
            in Vertex b,
            in Vertex c,
            int subMesh,
            float minimumArea)
        {
            var twiceArea = Vector3.Cross(b.Position - a.Position, c.Position - a.Position).magnitude;
            if (twiceArea * 0.5f > minimumArea)
                destination.Add(new Triangle(a, b, c, subMesh));
        }

        private static Mesh BuildMesh(Mesh source, List<Triangle> triangles, bool hasCapSubMesh)
        {
            var vertexCount = triangles.Count * 3;
            var mesh = new Mesh
            {
                name = source.name + " Cut",
                indexFormat = vertexCount > ushort.MaxValue ? IndexFormat.UInt32 : IndexFormat.UInt16
            };

            var positions = new List<Vector3>(vertexCount);
            var normals = new List<Vector3>(vertexCount);
            var tangents = new List<Vector4>(vertexCount);
            var uv = new List<Vector2>(vertexCount);
            var colors = new List<Color32>(vertexCount);
            var subMeshCount = source.subMeshCount + (hasCapSubMesh ? 1 : 0);
            var indices = new List<int>[subMeshCount];
            for (var i = 0; i < subMeshCount; i++)
                indices[i] = new List<int>();

            foreach (var triangle in triangles)
            {
                AddVertex(triangle.A, positions, normals, tangents, uv, colors);
                AddVertex(triangle.B, positions, normals, tangents, uv, colors);
                AddVertex(triangle.C, positions, normals, tangents, uv, colors);
                var start = positions.Count - 3;
                indices[triangle.SubMesh].Add(start);
                indices[triangle.SubMesh].Add(start + 1);
                indices[triangle.SubMesh].Add(start + 2);
            }

            mesh.SetVertices(positions);
            mesh.SetNormals(normals);
            mesh.SetTangents(tangents);
            mesh.SetUVs(0, uv);
            mesh.SetColors(colors);
            mesh.subMeshCount = subMeshCount;
            for (var i = 0; i < subMeshCount; i++)
                mesh.SetTriangles(indices[i], i, false);

            if (!source.HasVertexAttribute(VertexAttribute.Normal))
                mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        private static void AddVertex(
            in Vertex vertex,
            List<Vector3> positions,
            List<Vector3> normals,
            List<Vector4> tangents,
            List<Vector2> uv,
            List<Color32> colors)
        {
            positions.Add(vertex.Position);
            normals.Add(vertex.Normal);
            tangents.Add(vertex.Tangent);
            uv.Add(vertex.Uv);
            colors.Add(vertex.Color);
        }
    }
}
