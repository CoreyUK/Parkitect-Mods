using System;
using System.Collections.Generic;
using Parkitect.UI;
using UnityEngine;
using UnityEngine.EventSystems;

namespace ConstructionLogistics
{
    public sealed class ConstructionLogisticsMod : AbstractMod
    {
        private static GameObject hostObject;

        public override string getName() { return "Realistic Construction Mode (Test)"; }

        public override string getDescription()
        {
            return "New rides become temporary construction sites with safety barriers, dust, progress timers, and delayed opening.";
        }

        public override string getVersionNumber() { return "0.3.1"; }
        public override string getIdentifier() { return "ConstructionLogistics"; }
        public override bool isMultiplayerModeCompatible() { return false; }

        public override void onEnabled()
        {
            if (hostObject != null)
            {
                UnityEngine.Object.Destroy(hostObject);
            }

            hostObject = new GameObject("ConstructionLogistics.Host");
            UnityEngine.Object.DontDestroyOnLoad(hostObject);
            hostObject.AddComponent<ConstructionManager>();
        }

        public override void onDisabled()
        {
            if (hostObject != null)
            {
                UnityEngine.Object.Destroy(hostObject);
                hostObject = null;
            }
        }
    }

    internal sealed class ConstructionManager : MonoBehaviour
    {
        private const float FinishedEditingDelay = 2f;
        private const float MinimumDuration = 60f;
        private const float MaximumDuration = 360f;
        private const float CostSecondsMultiplier = 0.015f;
        private const float FootprintSecondsMultiplier = 4f;

        private readonly HashSet<uint> knownAttractions = new HashSet<uint>();
        private readonly Dictionary<uint, PendingAttraction> pending = new Dictionary<uint, PendingAttraction>();
        private readonly Dictionary<uint, ConstructionProject> projects = new Dictionary<uint, ConstructionProject>();
        private Park activePark;
        private EventManager activeEvents;
        private bool baselineCaptured;
        private GUIStyle progressStyle;
        private GUIStyle remainingTimeStyle;
        private readonly List<RaycastResult> uiRaycastResults = new List<RaycastResult>();

        private void Update()
        {
            Park park = GameController.Instance == null ? null : GameController.Instance.park;
            if (park != activePark)
            {
                SwitchPark(park);
            }

            if (activePark == null || GameController.Instance == null || !GameController.Instance.isPlayingPark)
            {
                return;
            }

            if (!baselineCaptured)
            {
                CaptureExistingAttractions();
                Subscribe();
                baselineCaptured = true;
                return;
            }

            UpdatePendingAttractions();
            UpdateProjects();
        }

        private void SwitchPark(Park park)
        {
            Unsubscribe();
            ClearProjects();
            knownAttractions.Clear();
            pending.Clear();
            activePark = park;
            baselineCaptured = false;
        }

        private void CaptureExistingAttractions()
        {
            IList<Attraction> attractions = activePark.getAttractions();
            for (int i = 0; i < attractions.Count; i++)
            {
                Attraction attraction = attractions[i];
                if (attraction != null)
                {
                    knownAttractions.Add(attraction.objectID);
                }
            }
        }

        private void Subscribe()
        {
            if (activeEvents != null || !EventManager.Exists)
            {
                return;
            }

            activeEvents = EventManager.Instance;
            activeEvents.OnAttractionAdded += OnAttractionAdded;
            activeEvents.OnAttractionRemoved += OnAttractionRemoved;
        }

        private void Unsubscribe()
        {
            if (activeEvents != null)
            {
                activeEvents.OnAttractionAdded -= OnAttractionAdded;
                activeEvents.OnAttractionRemoved -= OnAttractionRemoved;
                activeEvents = null;
            }
        }

        private void OnAttractionAdded(Attraction attraction)
        {
            if (attraction == null || !baselineCaptured || GameController.Instance == null || !GameController.Instance.isPlayingPark)
            {
                return;
            }

            if (knownAttractions.Add(attraction.objectID))
            {
                pending[attraction.objectID] = new PendingAttraction(attraction, FinishedEditingDelay);
            }
        }

        private void OnAttractionRemoved(Attraction attraction)
        {
            if (attraction == null)
            {
                return;
            }

            uint id = attraction.objectID;
            pending.Remove(id);
            ConstructionProject project;
            if (projects.TryGetValue(id, out project))
            {
                project.Dispose();
                projects.Remove(id);
            }

            knownAttractions.Remove(id);
        }

        private void UpdatePendingAttractions()
        {
            if (pending.Count == 0)
            {
                return;
            }

            List<uint> ready = null;
            List<uint> missing = null;
            foreach (KeyValuePair<uint, PendingAttraction> pair in pending)
            {
                PendingAttraction item = pair.Value;
                if (item.Attraction == null)
                {
                    if (missing == null) missing = new List<uint>();
                    missing.Add(pair.Key);
                    continue;
                }

                if (item.Attraction.isBeingEdited)
                {
                    item.Delay = FinishedEditingDelay;
                    continue;
                }

                item.Delay -= Time.deltaTime;
                if (item.Delay <= 0f)
                {
                    if (ready == null) ready = new List<uint>();
                    ready.Add(pair.Key);
                }
            }

            if (missing != null)
            {
                for (int i = 0; i < missing.Count; i++) pending.Remove(missing[i]);
            }

            if (ready != null)
            {
                for (int i = 0; i < ready.Count; i++)
                {
                    uint id = ready[i];
                    PendingAttraction item;
                    if (pending.TryGetValue(id, out item))
                    {
                        pending.Remove(id);
                        StartProject(item.Attraction);
                    }
                }
            }
        }

        private void StartProject(Attraction attraction)
        {
            if (attraction == null || projects.ContainsKey(attraction.objectID))
            {
                return;
            }

            Bounds bounds = CalculateBounds(attraction);
            float price = 0f;
            try
            {
                price = Mathf.Max(0f, attraction.getTotalRealPrice());
            }
            catch
            {
                price = 0f;
            }

            float area = Mathf.Max(1f, bounds.size.x * bounds.size.z);
            float duration = Mathf.Clamp(
                45f + (price * CostSecondsMultiplier) + (Mathf.Sqrt(area) * FootprintSecondsMultiplier),
                MinimumDuration,
                MaximumDuration);

            new AttractionChangeStateCommand(attraction, Attraction.State.CLOSED).run();
            ConstructionProject project = new ConstructionProject(
                attraction,
                bounds,
                duration,
                CreateZoneVisual(attraction, bounds),
                new RideFadeVisual(attraction));
            projects[attraction.objectID] = project;

            int minutes = Mathf.Max(1, Mathf.CeilToInt(duration / 60f));
            Notify("Construction Started", attraction.getCustomizedName() + " is now an active construction site. Estimated duration: " + minutes + " minute" + (minutes == 1 ? "." : "s."));
        }

        private void UpdateProjects()
        {
            if (projects.Count == 0)
            {
                return;
            }

            List<uint> complete = null;
            foreach (KeyValuePair<uint, ConstructionProject> pair in projects)
            {
                ConstructionProject project = pair.Value;
                if (project.Attraction == null)
                {
                    if (complete == null) complete = new List<uint>();
                    complete.Add(pair.Key);
                    continue;
                }

                project.Remaining -= Time.deltaTime;
                project.UpdateVisual();
                if (project.Remaining <= 0f)
                {
                    if (complete == null) complete = new List<uint>();
                    complete.Add(pair.Key);
                }
            }

            if (complete != null)
            {
                for (int i = 0; i < complete.Count; i++)
                {
                    uint id = complete[i];
                    ConstructionProject project;
                    if (projects.TryGetValue(id, out project))
                    {
                        string name = project.Attraction == null ? "The attraction" : project.Attraction.getCustomizedName();
                        project.Dispose();
                        projects.Remove(id);
                        Notify("Construction Complete", name + " is complete and ready for testing or opening.");
                    }
                }
            }
        }

        private Bounds CalculateBounds(Attraction attraction)
        {
            List<TilePoint> occupiedTiles = GetOccupiedTiles(attraction);
            Renderer[] renderers = attraction.GetComponentsInChildren<Renderer>();
            bool hasBounds = false;
            Bounds bounds = new Bounds(attraction.transform.position, new Vector3(4f, 2f, 4f));
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null || !renderer.enabled)
                {
                    continue;
                }

                if (!hasBounds)
                {
                    bounds = renderer.bounds;
                    hasBounds = true;
                }
                else
                {
                    bounds.Encapsulate(renderer.bounds);
                }
            }

            if (!hasBounds && occupiedTiles.Count > 0)
            {
                int minX = occupiedTiles[0].X;
                int maxX = occupiedTiles[0].X;
                int minZ = occupiedTiles[0].Z;
                int maxZ = occupiedTiles[0].Z;
                for (int i = 1; i < occupiedTiles.Count; i++)
                {
                    minX = Math.Min(minX, occupiedTiles[i].X);
                    maxX = Math.Max(maxX, occupiedTiles[i].X);
                    minZ = Math.Min(minZ, occupiedTiles[i].Z);
                    maxZ = Math.Max(maxZ, occupiedTiles[i].Z);
                }

                float minY = bounds.min.y;
                float sizeY = Mathf.Max(2f, bounds.size.y);
                bounds = new Bounds(
                    new Vector3((minX + maxX + 1f) * 0.5f, minY + (sizeY * 0.5f), (minZ + maxZ + 1f) * 0.5f),
                    new Vector3(maxX - minX + 1f, sizeY, maxZ - minZ + 1f));
            }
            else if (hasBounds && occupiedTiles.Count > 0)
            {
                for (int i = 0; i < occupiedTiles.Count; i++)
                {
                    bounds.Encapsulate(new Vector3(occupiedTiles[i].X, bounds.center.y, occupiedTiles[i].Z));
                    bounds.Encapsulate(new Vector3(occupiedTiles[i].X + 1f, bounds.center.y, occupiedTiles[i].Z + 1f));
                }
            }

            bounds.Expand(new Vector3(1f, 0f, 1f));
            return bounds;
        }

        private ZoneVisual CreateZoneVisual(Attraction attraction, Bounds bounds)
        {
            GameObject root = new GameObject("Construction Zone - " + attraction.objectID);
            root.transform.SetParent(transform, false);
            float groundY = attraction.transform.position.y + 0.25f;

            Texture2D hazardTexture = CreateHazardTexture();
            Material hazardMaterial = CreateMaterial("Sprites/Default", Color.white);
            hazardMaterial.mainTexture = hazardTexture;
            Material postMaterial = CreateMaterial("Sprites/Default", new Color(1f, 0.43f, 0.02f, 1f));
            Material crateMaterial = CreateMaterial("Sprites/Default", new Color(0.42f, 0.24f, 0.09f, 1f));
            Material scaffoldMaterial = CreateMaterial("Sprites/Default", new Color(0.92f, 0.74f, 0.18f, 1f));
            Material lampMaterial = CreateMaterial("Sprites/Default", new Color(1f, 0.12f, 0.02f, 1f));

            List<TilePoint> occupiedTiles = GetOccupiedTiles(attraction);
            List<BarrierSegment> segments = TilesDescribeFootprint(occupiedTiles, bounds)
                ? CreateBarrierSegments(occupiedTiles, bounds, groundY)
                : CreateRendererHullSegments(attraction, bounds, groundY);
            segments = CreateAccessGaps(segments, attraction);
            CreateBarrierMesh(root, segments, hazardMaterial);
            CreateBoundaryPosts(root, segments, postMaterial);
            CreateTrafficCones(root, segments, postMaterial);
            GameObject piles = CreateMaterialPiles(root, attraction, bounds, groundY, crateMaterial);
            GameObject scaffolding = CreateScaffolding(root, bounds, groundY, scaffoldMaterial);
            Renderer[] warningLamps = CreateWarningLamps(root, segments, lampMaterial);
            DustVisual dust = CreateDust(root, bounds, groundY);

            return new ZoneVisual(
                root,
                dust.System,
                piles,
                scaffolding,
                warningLamps,
                new[] { hazardMaterial, postMaterial, crateMaterial, scaffoldMaterial, lampMaterial, dust.Material },
                new[] { hazardTexture, dust.Texture });
        }

        private static Material CreateMaterial(string shaderName, Color color)
        {
            Shader shader = Shader.Find(shaderName);
            if (shader == null)
            {
                shader = Shader.Find("Unlit/Color");
            }

            Material material = new Material(shader);
            material.color = color;
            return material;
        }

        private static List<TilePoint> GetOccupiedTiles(Attraction attraction)
        {
            List<TilePoint> result = new List<TilePoint>();
            try
            {
                CrossedTiles crossed = attraction.getCrossedTiles();
                if (crossed != null && crossed.crossedTilesInfo != null)
                {
                    for (int i = 0; i < crossed.crossedTilesInfo.Count; i++)
                    {
                        CrossedTileInfo tile = crossed.crossedTilesInfo[i];
                        if (tile != null && !tile.hidden)
                        {
                            result.Add(new TilePoint(tile.getWorldX(), tile.getWorldZ()));
                        }
                    }
                }
            }
            catch
            {
                result.Clear();
            }

            return result;
        }

        private static List<BarrierSegment> CreateBarrierSegments(List<TilePoint> tiles, Bounds bounds, float groundY)
        {
            List<BarrierSegment> segments = new List<BarrierSegment>();
            if (tiles.Count == 0)
            {
                Vector3 a = new Vector3(bounds.min.x, groundY, bounds.min.z);
                Vector3 b = new Vector3(bounds.max.x, groundY, bounds.min.z);
                Vector3 c = new Vector3(bounds.max.x, groundY, bounds.max.z);
                Vector3 d = new Vector3(bounds.min.x, groundY, bounds.max.z);
                segments.Add(new BarrierSegment(a, b));
                segments.Add(new BarrierSegment(b, c));
                segments.Add(new BarrierSegment(c, d));
                segments.Add(new BarrierSegment(d, a));
                return segments;
            }

            HashSet<long> occupied = new HashSet<long>();
            for (int i = 0; i < tiles.Count; i++) occupied.Add(TileKey(tiles[i].X, tiles[i].Z));
            for (int i = 0; i < tiles.Count; i++)
            {
                int x = tiles[i].X;
                int z = tiles[i].Z;
                if (!occupied.Contains(TileKey(x, z - 1))) segments.Add(new BarrierSegment(new Vector3(x, groundY, z), new Vector3(x + 1f, groundY, z)));
                if (!occupied.Contains(TileKey(x + 1, z))) segments.Add(new BarrierSegment(new Vector3(x + 1f, groundY, z), new Vector3(x + 1f, groundY, z + 1f)));
                if (!occupied.Contains(TileKey(x, z + 1))) segments.Add(new BarrierSegment(new Vector3(x + 1f, groundY, z + 1f), new Vector3(x, groundY, z + 1f)));
                if (!occupied.Contains(TileKey(x - 1, z))) segments.Add(new BarrierSegment(new Vector3(x, groundY, z + 1f), new Vector3(x, groundY, z)));
            }

            return segments;
        }

        private static long TileKey(int x, int z)
        {
            return ((long)x << 32) ^ (uint)z;
        }

        private static bool TilesDescribeFootprint(List<TilePoint> tiles, Bounds bounds)
        {
            if (tiles == null || tiles.Count == 0) return false;
            int minX = tiles[0].X;
            int maxX = tiles[0].X;
            int minZ = tiles[0].Z;
            int maxZ = tiles[0].Z;
            for (int i = 1; i < tiles.Count; i++)
            {
                minX = Math.Min(minX, tiles[i].X);
                maxX = Math.Max(maxX, tiles[i].X);
                minZ = Math.Min(minZ, tiles[i].Z);
                maxZ = Math.Max(maxZ, tiles[i].Z);
            }

            float tileWidth = maxX - minX + 1f;
            float tileDepth = maxZ - minZ + 1f;
            float visualWidth = Mathf.Max(1f, bounds.size.x - 1f);
            float visualDepth = Mathf.Max(1f, bounds.size.z - 1f);
            if (tileWidth < visualWidth * 0.65f || tileDepth < visualDepth * 0.65f) return false;

            float tileArea = tileWidth * tileDepth;
            return tileArea <= 1f || (tiles.Count / tileArea) >= 0.35f;
        }

        private static List<BarrierSegment> CreateRendererHullSegments(Attraction attraction, Bounds fallbackBounds, float groundY)
        {
            List<Vector2> points = new List<Vector2>();
            Renderer[] renderers = attraction.GetComponentsInChildren<Renderer>();
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null || !renderer.enabled) continue;
                Bounds rendererBounds = renderer.bounds;
                if (rendererBounds.size.x > 250f || rendererBounds.size.z > 250f) continue;
                points.Add(new Vector2(rendererBounds.min.x, rendererBounds.min.z));
                points.Add(new Vector2(rendererBounds.max.x, rendererBounds.min.z));
                points.Add(new Vector2(rendererBounds.max.x, rendererBounds.max.z));
                points.Add(new Vector2(rendererBounds.min.x, rendererBounds.max.z));
            }

            List<Vector2> hull = CreateConvexHull(points);
            if (hull.Count < 3)
            {
                return CreateBarrierSegments(new List<TilePoint>(), fallbackBounds, groundY);
            }

            Vector2 center = Vector2.zero;
            for (int i = 0; i < hull.Count; i++) center += hull[i];
            center /= hull.Count;
            const float safetyMargin = 0.65f;
            for (int i = 0; i < hull.Count; i++)
            {
                Vector2 outward = (hull[i] - center).normalized;
                hull[i] += outward * safetyMargin;
            }

            List<BarrierSegment> result = new List<BarrierSegment>(hull.Count);
            for (int i = 0; i < hull.Count; i++)
            {
                Vector2 a = hull[i];
                Vector2 b = hull[(i + 1) % hull.Count];
                result.Add(new BarrierSegment(new Vector3(a.x, groundY, a.y), new Vector3(b.x, groundY, b.y)));
            }
            return result;
        }

        private static List<Vector2> CreateConvexHull(List<Vector2> points)
        {
            points.Sort((left, right) => left.x == right.x ? left.y.CompareTo(right.y) : left.x.CompareTo(right.x));
            List<Vector2> unique = new List<Vector2>(points.Count);
            for (int i = 0; i < points.Count; i++)
            {
                if (unique.Count == 0 || Vector2.SqrMagnitude(points[i] - unique[unique.Count - 1]) > 0.0001f)
                    unique.Add(points[i]);
            }
            if (unique.Count <= 2) return unique;

            List<Vector2> hull = new List<Vector2>();
            for (int i = 0; i < unique.Count; i++)
            {
                while (hull.Count >= 2 && Cross(hull[hull.Count - 2], hull[hull.Count - 1], unique[i]) <= 0f)
                    hull.RemoveAt(hull.Count - 1);
                hull.Add(unique[i]);
            }
            int lowerCount = hull.Count;
            for (int i = unique.Count - 2; i >= 0; i--)
            {
                while (hull.Count > lowerCount && Cross(hull[hull.Count - 2], hull[hull.Count - 1], unique[i]) <= 0f)
                    hull.RemoveAt(hull.Count - 1);
                hull.Add(unique[i]);
            }
            hull.RemoveAt(hull.Count - 1);
            return hull;
        }

        private static float Cross(Vector2 origin, Vector2 a, Vector2 b)
        {
            return ((a.x - origin.x) * (b.y - origin.y)) - ((a.y - origin.y) * (b.x - origin.x));
        }

        private static List<BarrierSegment> CreateAccessGaps(List<BarrierSegment> segments, Attraction attraction)
        {
            List<Vector3> accessPoints = new List<Vector3>();
            if (attraction.entranceGO != null) accessPoints.Add(attraction.entranceGO.transform.position);
            if (attraction.exitGO != null) accessPoints.Add(attraction.exitGO.transform.position);
            List<BarrierSegment> result = segments;
            for (int i = 0; i < accessPoints.Count; i++)
            {
                List<BarrierSegment> next = new List<BarrierSegment>();
                for (int j = 0; j < result.Count; j++) SplitForAccess(result[j], accessPoints[i], 0.9f, next);
                result = next;
            }
            return result;
        }

        private static void SplitForAccess(BarrierSegment segment, Vector3 access, float radius, List<BarrierSegment> output)
        {
            Vector3 direction = segment.B - segment.A;
            float length = direction.magnitude;
            if (length < 0.1f) return;
            Vector3 unit = direction / length;
            Vector3 flatAccess = new Vector3(access.x, segment.A.y, access.z);
            float along = Mathf.Clamp(Vector3.Dot(flatAccess - segment.A, unit), 0f, length);
            Vector3 closest = segment.A + (unit * along);
            float perpendicular = Vector3.Distance(flatAccess, closest);
            if (perpendicular >= radius)
            {
                output.Add(segment);
                return;
            }

            float halfGap = Mathf.Sqrt((radius * radius) - (perpendicular * perpendicular));
            float before = Mathf.Max(0f, along - halfGap);
            float after = Mathf.Min(length, along + halfGap);
            if (before > 0.35f) output.Add(new BarrierSegment(segment.A, segment.A + (unit * before)));
            if (length - after > 0.35f) output.Add(new BarrierSegment(segment.A + (unit * after), segment.B));
        }

        private static void CreateBarrierMesh(GameObject root, List<BarrierSegment> segments, Material material)
        {
            GameObject barrier = new GameObject("Striped Safety Barriers");
            barrier.transform.SetParent(root.transform, false);
            MeshFilter filter = barrier.AddComponent<MeshFilter>();
            MeshRenderer renderer = barrier.AddComponent<MeshRenderer>();
            renderer.material = material;

            List<Vector3> vertices = new List<Vector3>(segments.Count * 4);
            List<Vector2> uv = new List<Vector2>(segments.Count * 4);
            List<int> triangles = new List<int>(segments.Count * 12);
            for (int i = 0; i < segments.Count; i++)
            {
                int start = vertices.Count;
                Vector3 a = segments[i].A + new Vector3(0f, 0.42f, 0f);
                Vector3 b = segments[i].B + new Vector3(0f, 0.42f, 0f);
                Vector3 c = segments[i].B + new Vector3(0f, 0.64f, 0f);
                Vector3 d = segments[i].A + new Vector3(0f, 0.64f, 0f);
                vertices.Add(a); vertices.Add(b); vertices.Add(c); vertices.Add(d);
                uv.Add(new Vector2(0f, 0f)); uv.Add(new Vector2(1f, 0f)); uv.Add(new Vector2(1f, 1f)); uv.Add(new Vector2(0f, 1f));
                triangles.Add(start); triangles.Add(start + 2); triangles.Add(start + 1);
                triangles.Add(start); triangles.Add(start + 3); triangles.Add(start + 2);
                triangles.Add(start); triangles.Add(start + 1); triangles.Add(start + 2);
                triangles.Add(start); triangles.Add(start + 2); triangles.Add(start + 3);
            }

            Mesh mesh = new Mesh();
            mesh.name = "Construction Barrier Mesh";
            mesh.SetVertices(vertices);
            mesh.SetUVs(0, uv);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateBounds();
            filter.mesh = mesh;
        }

        private static void CreateBoundaryPosts(GameObject root, List<BarrierSegment> segments, Material material)
        {
            HashSet<string> used = new HashSet<string>();
            int maximumPosts = 160;
            for (int i = 0; i < segments.Count && used.Count < maximumPosts; i++)
            {
                CreatePost(segments[i].A);
                if (used.Count < maximumPosts) CreatePost(segments[i].B);
            }

            void CreatePost(Vector3 position)
            {
                string key = Mathf.RoundToInt(position.x * 10f) + ":" + Mathf.RoundToInt(position.z * 10f);
                if (!used.Add(key)) return;
                GameObject post = new GameObject("Safety Post");
                post.transform.SetParent(root.transform, false);
                LineRenderer line = post.AddComponent<LineRenderer>();
                line.useWorldSpace = true;
                line.positionCount = 2;
                line.startWidth = 0.09f;
                line.endWidth = 0.09f;
                line.material = material;
                line.SetPosition(0, position);
                line.SetPosition(1, position + new Vector3(0f, 0.78f, 0f));
            }
        }

        private static Texture2D CreateHazardTexture()
        {
            const int width = 64;
            const int height = 16;
            Texture2D texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            texture.name = "Construction Hazard Stripes";
            texture.wrapMode = TextureWrapMode.Repeat;
            texture.filterMode = FilterMode.Point;
            Color32 orange = new Color(1f, 0.48f, 0.02f, 1f);
            Color32 black = new Color(0.035f, 0.035f, 0.035f, 1f);
            Color32[] pixels = new Color32[width * height];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    pixels[(y * width) + x] = (((x + (y * 2)) / 8) % 2) == 0 ? orange : black;
                }
            }

            texture.SetPixels32(pixels);
            texture.Apply(false, true);
            return texture;
        }

        private static GameObject CreateMaterialPiles(GameObject root, Attraction attraction, Bounds bounds, float groundY, Material material)
        {
            GameObject group = new GameObject("Construction Materials");
            group.transform.SetParent(root.transform, false);
            Vector3 basePosition = new Vector3(bounds.min.x + 0.8f, groundY + 0.15f, bounds.min.z + 0.8f);
            if (attraction.entranceGO != null)
            {
                Vector3 entrance = attraction.entranceGO.transform.position;
                Vector3 inward = new Vector3(bounds.center.x - entrance.x, 0f, bounds.center.z - entrance.z).normalized;
                Vector3 sideways = new Vector3(-inward.z, 0f, inward.x);
                basePosition = new Vector3(entrance.x, groundY + 0.15f, entrance.z) + (inward * 1.2f) + (sideways * 1.1f);
            }
            CreatePrimitiveProp(group, PrimitiveType.Cube, basePosition, new Vector3(0.55f, 0.3f, 0.45f), material);
            CreatePrimitiveProp(group, PrimitiveType.Cube, basePosition + new Vector3(0.15f, 0.33f, 0.05f), new Vector3(0.42f, 0.3f, 0.38f), material);
            CreatePrimitiveProp(group, PrimitiveType.Cube, basePosition + new Vector3(0.05f, 0.64f, 0.12f), new Vector3(0.32f, 0.26f, 0.3f), material);
            return group;
        }

        private static GameObject CreateScaffolding(GameObject root, Bounds bounds, float groundY, Material material)
        {
            GameObject group = new GameObject("Temporary Scaffolding");
            group.transform.SetParent(root.transform, false);
            float halfX = Mathf.Min(bounds.extents.x * 0.55f, 4f);
            float halfZ = Mathf.Min(bounds.extents.z * 0.55f, 4f);
            Vector3 center = new Vector3(bounds.center.x, groundY, bounds.center.z);
            Vector3[] corners =
            {
                center + new Vector3(-halfX, 0f, -halfZ), center + new Vector3(halfX, 0f, -halfZ),
                center + new Vector3(halfX, 0f, halfZ), center + new Vector3(-halfX, 0f, halfZ)
            };
            for (int i = 0; i < corners.Length; i++)
            {
                CreateSimpleLine(group, corners[i], corners[i] + new Vector3(0f, 2.5f, 0f), 0.055f, material);
                CreateSimpleLine(group, corners[i] + new Vector3(0f, 1.25f, 0f), corners[(i + 1) % corners.Length] + new Vector3(0f, 1.25f, 0f), 0.045f, material);
                CreateSimpleLine(group, corners[i] + new Vector3(0f, 2.5f, 0f), corners[(i + 1) % corners.Length] + new Vector3(0f, 2.5f, 0f), 0.045f, material);
            }
            group.SetActive(false);
            return group;
        }

        private static Renderer[] CreateWarningLamps(GameObject root, List<BarrierSegment> segments, Material material)
        {
            int count = Mathf.Min(6, segments.Count);
            Renderer[] result = new Renderer[count];
            for (int i = 0; i < count; i++)
            {
                int segmentIndex = Mathf.Min(segments.Count - 1, Mathf.FloorToInt((i / (float)count) * segments.Count));
                Vector3 position = segments[segmentIndex].A + new Vector3(0f, 0.82f, 0f);
                GameObject lamp = CreatePrimitiveProp(root, PrimitiveType.Sphere, position, new Vector3(0.16f, 0.16f, 0.16f), material);
                lamp.name = "Warning Lamp";
                result[i] = lamp.GetComponent<Renderer>();
            }
            return result;
        }

        private static void CreateTrafficCones(GameObject root, List<BarrierSegment> segments, Material material)
        {
            HashSet<string> used = new HashSet<string>();
            const int maximumCones = 80;
            for (int i = 0; i < segments.Count && used.Count < maximumCones; i++)
            {
                float length = Vector3.Distance(segments[i].A, segments[i].B);
                int count = Mathf.Max(1, Mathf.CeilToInt(length / 4.5f));
                for (int step = 0; step <= count && used.Count < maximumCones; step++)
                {
                    Vector3 position = Vector3.Lerp(segments[i].A, segments[i].B, step / (float)count);
                    string key = Mathf.RoundToInt(position.x * 10f) + ":" + Mathf.RoundToInt(position.z * 10f);
                    if (!used.Add(key)) continue;
                    GameObject cone = new GameObject("Safety Cone");
                    cone.transform.SetParent(root.transform, false);
                    cone.transform.position = position;
                    MeshFilter filter = cone.AddComponent<MeshFilter>();
                    MeshRenderer renderer = cone.AddComponent<MeshRenderer>();
                    renderer.material = material;
                    filter.mesh = CreateConeMesh();
                }
            }
        }

        private static Mesh CreateConeMesh()
        {
            const int sides = 10;
            const float radius = 0.17f;
            const float height = 0.52f;
            Vector3[] vertices = new Vector3[sides + 2];
            int[] triangles = new int[sides * 6];
            vertices[0] = new Vector3(0f, height, 0f);
            vertices[sides + 1] = Vector3.zero;
            for (int i = 0; i < sides; i++)
            {
                float angle = (Mathf.PI * 2f * i) / sides;
                vertices[i + 1] = new Vector3(Mathf.Cos(angle) * radius, 0f, Mathf.Sin(angle) * radius);
                int next = ((i + 1) % sides) + 1;
                int index = i * 6;
                triangles[index] = 0;
                triangles[index + 1] = i + 1;
                triangles[index + 2] = next;
                triangles[index + 3] = sides + 1;
                triangles[index + 4] = next;
                triangles[index + 5] = i + 1;
            }

            Mesh mesh = new Mesh();
            mesh.name = "Construction Cone Mesh";
            mesh.vertices = vertices;
            mesh.triangles = triangles;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        private static GameObject CreatePrimitiveProp(GameObject parent, PrimitiveType type, Vector3 position, Vector3 scale, Material material)
        {
            GameObject item = GameObject.CreatePrimitive(type);
            item.transform.SetParent(parent.transform, false);
            item.transform.position = position;
            item.transform.localScale = scale;
            Collider collider = item.GetComponent<Collider>();
            if (collider != null) UnityEngine.Object.Destroy(collider);
            Renderer renderer = item.GetComponent<Renderer>();
            if (renderer != null) renderer.material = material;
            return item;
        }

        private static void CreateSimpleLine(GameObject parent, Vector3 start, Vector3 end, float width, Material material)
        {
            GameObject lineObject = new GameObject("Scaffold Rail");
            lineObject.transform.SetParent(parent.transform, false);
            LineRenderer line = lineObject.AddComponent<LineRenderer>();
            line.useWorldSpace = true;
            line.positionCount = 2;
            line.startWidth = width;
            line.endWidth = width;
            line.material = material;
            line.SetPosition(0, start);
            line.SetPosition(1, end);
        }

        private static DustVisual CreateDust(GameObject root, Bounds bounds, float groundY)
        {
            GameObject dustObject = new GameObject("Construction Dust");
            dustObject.transform.SetParent(root.transform, false);
            dustObject.transform.position = new Vector3(bounds.center.x, groundY + 0.2f, bounds.center.z);
            ParticleSystem dust = dustObject.AddComponent<ParticleSystem>();

            ParticleSystem.MainModule main = dust.main;
            main.loop = true;
            main.startLifetime = new ParticleSystem.MinMaxCurve(1.5f, 3.5f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.2f, 0.8f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.25f, 0.7f);
            main.startColor = new ParticleSystem.MinMaxGradient(
                new Color(0.64f, 0.53f, 0.38f, 0.28f),
                new Color(0.82f, 0.75f, 0.62f, 0.42f));
            main.maxParticles = 120;
            main.simulationSpace = ParticleSystemSimulationSpace.World;

            ParticleSystem.EmissionModule emission = dust.emission;
            emission.rateOverTime = 5f;

            ParticleSystem.ShapeModule shape = dust.shape;
            shape.shapeType = ParticleSystemShapeType.Box;
            shape.scale = new Vector3(Mathf.Min(bounds.size.x, 20f), 0.2f, Mathf.Min(bounds.size.z, 20f));

            ParticleSystemRenderer renderer = dustObject.GetComponent<ParticleSystemRenderer>();
            renderer.renderMode = ParticleSystemRenderMode.Billboard;
            Shader shader = Shader.Find("Legacy Shaders/Particles/Alpha Blended");
            if (shader == null) shader = Shader.Find("Particles/Standard Unlit");
            Material dustMaterial = null;
            Texture2D dustTexture = CreateSoftDustTexture();
            if (shader != null)
            {
                dustMaterial = new Material(shader);
                dustMaterial.SetTexture("_MainTex", dustTexture);
                dustMaterial.color = Color.white;
                renderer.material = dustMaterial;
            }

            dust.Play();
            return new DustVisual(dust, dustMaterial, dustTexture);
        }

        private static Texture2D CreateSoftDustTexture()
        {
            const int size = 32;
            float center = (size - 1) * 0.5f;
            Texture2D texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
            texture.name = "Soft Construction Dust";
            texture.wrapMode = TextureWrapMode.Clamp;
            texture.filterMode = FilterMode.Bilinear;
            Color32[] pixels = new Color32[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = (x - center) / center;
                    float dy = (y - center) / center;
                    float distance = Mathf.Sqrt((dx * dx) + (dy * dy));
                    float alpha = Mathf.Clamp01(1f - distance);
                    alpha *= alpha;
                    pixels[(y * size) + x] = new Color(1f, 1f, 1f, alpha);
                }
            }
            texture.SetPixels32(pixels);
            texture.Apply(false, true);
            return texture;
        }

        private void OnGUI()
        {
            if (projects.Count == 0)
            {
                return;
            }

            GUI.depth = 1000;

            if (progressStyle == null)
            {
                progressStyle = new GUIStyle(GUI.skin.label);
                progressStyle.alignment = TextAnchor.MiddleCenter;
                progressStyle.fontSize = 14;
                progressStyle.fontStyle = FontStyle.Bold;
                progressStyle.normal.textColor = Color.white;

                remainingTimeStyle = new GUIStyle(progressStyle);
                remainingTimeStyle.fontSize = 11;
                remainingTimeStyle.normal.textColor = new Color(1f, 0.74f, 0.2f, 1f);
            }

            Camera camera = Camera.main;
            if (camera == null) camera = UnityEngine.Object.FindObjectOfType<Camera>();
            if (camera == null) return;

            foreach (ConstructionProject project in projects.Values)
            {
                if (project.Attraction == null) continue;
                Vector3 world = new Vector3(project.Bounds.center.x, project.Bounds.max.y + 1f, project.Bounds.center.z);
                Vector3 screen = camera.WorldToScreenPoint(world);
                if (screen.z <= 0f || screen.x < 0f || screen.x > Screen.width || screen.y < 0f || screen.y > Screen.height) continue;
                if (IsCoveredByParkUi(screen)) continue;

                int percent = Mathf.Clamp(Mathf.RoundToInt((1f - (project.Remaining / project.Duration)) * 100f), 0, 100);
                Rect rect = new Rect(screen.x - 34f, Screen.height - screen.y - 34f, 68f, 68f);
                GUI.DrawTexture(rect, project.GetProgressTexture(percent), ScaleMode.ScaleToFit, true);
                GUI.Label(rect, percent + "%", progressStyle);
                int totalSeconds = Mathf.Max(0, Mathf.CeilToInt(project.Remaining));
                string remaining = (totalSeconds / 60).ToString("0") + ":" + (totalSeconds % 60).ToString("00");
                Rect timeRect = new Rect(rect.x - 6f, rect.y + 52f, rect.width + 12f, 20f);
                GUI.Label(timeRect, remaining, remainingTimeStyle);
            }
        }

        private bool IsCoveredByParkUi(Vector3 screenPosition)
        {
            EventSystem eventSystem = EventSystem.current;
            if (eventSystem == null) return false;
            PointerEventData pointer = new PointerEventData(eventSystem);
            pointer.position = new Vector2(screenPosition.x, screenPosition.y);
            uiRaycastResults.Clear();
            eventSystem.RaycastAll(pointer, uiRaycastResults);
            return uiRaycastResults.Count > 0;
        }

        private void ClearProjects()
        {
            foreach (ConstructionProject project in projects.Values)
            {
                project.Dispose();
            }

            projects.Clear();
        }

        private static void Notify(string title, string message)
        {
            if (SystemNotificationManager.Instance != null)
            {
                SystemNotificationManager.Instance.spawnNotification(title, message);
            }

            Debug.Log("[ConstructionLogistics] " + title + ": " + message);
        }

        private void OnDestroy()
        {
            Unsubscribe();
            ClearProjects();
        }

        private sealed class PendingAttraction
        {
            public PendingAttraction(Attraction attraction, float delay)
            {
                Attraction = attraction;
                Delay = delay;
            }

            public Attraction Attraction { get; private set; }
            public float Delay { get; set; }
        }

        private sealed class ConstructionProject
        {
            public ConstructionProject(Attraction attraction, Bounds bounds, float duration, ZoneVisual visual, RideFadeVisual rideFade)
            {
                Attraction = attraction;
                Bounds = bounds;
                Duration = duration;
                Remaining = duration;
                Visual = visual;
                RideFade = rideFade;
                UpdateVisual();
            }

            public Attraction Attraction { get; private set; }
            public Bounds Bounds { get; private set; }
            public float Duration { get; private set; }
            public float Remaining { get; set; }
            private ZoneVisual Visual { get; set; }
            private RideFadeVisual RideFade { get; set; }
            private Texture2D progressTexture;
            private int progressTexturePercent = -1;

            public Texture2D GetProgressTexture(int percent)
            {
                if (progressTexture == null || progressTexturePercent != percent)
                {
                    if (progressTexture != null)
                    {
                        UnityEngine.Object.Destroy(progressTexture);
                    }

                    progressTexture = CreateProgressRing(percent);
                    progressTexturePercent = percent;
                }

                return progressTexture;
            }

            public void UpdateVisual()
            {
                if (Visual != null)
                {
                    Visual.UpdateProgress(Mathf.Clamp01(1f - (Remaining / Duration)));
                }
                if (RideFade != null)
                {
                    RideFade.UpdateProgress(Mathf.Clamp01(1f - (Remaining / Duration)));
                }
            }

            private static Texture2D CreateProgressRing(int percent)
            {
                const int size = 96;
                const float center = (size - 1) * 0.5f;
                const float outerRadius = 44f;
                const float innerRadius = 34f;
                float completedAngle = Mathf.Clamp01(percent / 100f) * Mathf.PI * 2f;
                Texture2D texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
                texture.name = "Construction Progress Ring";
                texture.wrapMode = TextureWrapMode.Clamp;
                texture.filterMode = FilterMode.Bilinear;
                Color32[] pixels = new Color32[size * size];

                for (int y = 0; y < size; y++)
                {
                    for (int x = 0; x < size; x++)
                    {
                        float dx = x - center;
                        float dy = y - center;
                        float radius = Mathf.Sqrt((dx * dx) + (dy * dy));
                        Color color = Color.clear;

                        if (radius < innerRadius - 1f)
                        {
                            float innerAlpha = Mathf.Clamp01((innerRadius - radius) * 0.7f);
                            color = new Color(0.035f, 0.045f, 0.055f, 0.78f * innerAlpha);
                        }
                        else if (radius <= outerRadius + 1f && radius >= innerRadius - 1f)
                        {
                            float edgeAlpha = Mathf.Clamp01(Mathf.Min(outerRadius + 1f - radius, radius - innerRadius + 1f));
                            float angle = Mathf.Atan2(dx, dy);
                            if (angle < 0f) angle += Mathf.PI * 2f;
                            bool completed = percent > 0 && angle <= completedAngle;
                            color = completed
                                ? new Color(1f, 0.48f, 0.03f, edgeAlpha)
                                : new Color(0.16f, 0.19f, 0.22f, 0.88f * edgeAlpha);
                        }

                        pixels[(y * size) + x] = color;
                    }
                }

                texture.SetPixels32(pixels);
                texture.Apply(false, true);
                return texture;
            }

            public void Dispose()
            {
                if (Visual != null)
                {
                    Visual.Dispose();
                    Visual = null;
                }

                if (RideFade != null)
                {
                    RideFade.Dispose();
                    RideFade = null;
                }

                if (progressTexture != null)
                {
                    UnityEngine.Object.Destroy(progressTexture);
                    progressTexture = null;
                }
            }
        }

        private struct TilePoint
        {
            public readonly int X;
            public readonly int Z;

            public TilePoint(int x, int z)
            {
                X = x;
                Z = z;
            }
        }

        private struct BarrierSegment
        {
            public readonly Vector3 A;
            public readonly Vector3 B;

            public BarrierSegment(Vector3 a, Vector3 b)
            {
                A = a;
                B = b;
            }
        }

        private sealed class DustVisual
        {
            public readonly ParticleSystem System;
            public readonly Material Material;
            public readonly Texture2D Texture;

            public DustVisual(ParticleSystem system, Material material, Texture2D texture)
            {
                System = system;
                Material = material;
                Texture = texture;
            }
        }

        private sealed class RideFadeVisual
        {
            private readonly List<RendererMaterialRecord> rendererRecords = new List<RendererMaterialRecord>();
            private readonly List<Material> clonedMaterials = new List<Material>();

            public RideFadeVisual(Attraction attraction)
            {
                Dictionary<Material, Material> clones = new Dictionary<Material, Material>();
                Renderer[] renderers = attraction.GetComponentsInChildren<Renderer>(true);
                for (int i = 0; i < renderers.Length; i++)
                {
                    Renderer renderer = renderers[i];
                    if (renderer == null) continue;
                    Material[] originals = renderer.sharedMaterials;
                    if (originals == null || originals.Length == 0) continue;
                    Material[] faded = new Material[originals.Length];
                    bool changed = false;
                    for (int materialIndex = 0; materialIndex < originals.Length; materialIndex++)
                    {
                        Material original = originals[materialIndex];
                        if (original == null) continue;
                        Material clone;
                        if (!clones.TryGetValue(original, out clone))
                        {
                            clone = CreateTransparentCopy(original);
                            clones.Add(original, clone);
                            clonedMaterials.Add(clone);
                        }
                        faded[materialIndex] = clone;
                        changed = true;
                    }
                    if (!changed) continue;
                    rendererRecords.Add(new RendererMaterialRecord(renderer, originals));
                    renderer.sharedMaterials = faded;
                }
            }

            public void UpdateProgress(float progress)
            {
                float alpha = Mathf.Clamp01(progress);
                for (int i = 0; i < clonedMaterials.Count; i++)
                {
                    Material material = clonedMaterials[i];
                    if (material == null) continue;
                    SetMaterialAlpha(material, "_Color", alpha);
                    SetMaterialAlpha(material, "_BaseColor", alpha);
                }
            }

            private static Material CreateTransparentCopy(Material original)
            {
                Texture mainTexture = original.HasProperty("_MainTex") ? original.GetTexture("_MainTex") : null;
                Vector2 textureScale = original.HasProperty("_MainTex") ? original.GetTextureScale("_MainTex") : Vector2.one;
                Vector2 textureOffset = original.HasProperty("_MainTex") ? original.GetTextureOffset("_MainTex") : Vector2.zero;
                Color originalColor = FindOriginalColor(original);
                Shader transparentShader = Shader.Find("Legacy Shaders/Transparent/Diffuse");
                if (transparentShader == null) transparentShader = Shader.Find("Unlit/Transparent");
                if (transparentShader == null) transparentShader = Shader.Find("Sprites/Default");

                Material material = transparentShader == null ? new Material(original) : new Material(transparentShader);
                material.name = original.name + " (Construction Fade)";
                if (material.HasProperty("_MainTex"))
                {
                    material.SetTexture("_MainTex", mainTexture);
                    material.SetTextureScale("_MainTex", textureScale);
                    material.SetTextureOffset("_MainTex", textureOffset);
                }
                if (material.HasProperty("_Color")) material.SetColor("_Color", originalColor);
                if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", originalColor);
                if (material.HasProperty("_Mode")) material.SetFloat("_Mode", 3f);
                if (material.HasProperty("_Surface")) material.SetFloat("_Surface", 1f);
                if (material.HasProperty("_SrcBlend")) material.SetInt("_SrcBlend", 5);
                if (material.HasProperty("_DstBlend")) material.SetInt("_DstBlend", 10);
                if (material.HasProperty("_ZWrite")) material.SetInt("_ZWrite", 0);
                material.SetOverrideTag("RenderType", "Transparent");
                material.DisableKeyword("_ALPHATEST_ON");
                material.EnableKeyword("_ALPHABLEND_ON");
                material.DisableKeyword("_ALPHAPREMULTIPLY_ON");
                material.renderQueue = 3000;
                return material;
            }

            private static Color FindOriginalColor(Material material)
            {
                string[] properties = { "_Color", "_BaseColor", "_MainColor", "_Color1" };
                for (int i = 0; i < properties.Length; i++)
                {
                    if (material.HasProperty(properties[i])) return material.GetColor(properties[i]);
                }
                return Color.white;
            }

            private static void SetMaterialAlpha(Material material, string property, float alpha)
            {
                if (!material.HasProperty(property)) return;
                Color color = material.GetColor(property);
                color.a = alpha;
                material.SetColor(property, color);
            }

            public void Dispose()
            {
                for (int i = 0; i < rendererRecords.Count; i++)
                {
                    RendererMaterialRecord record = rendererRecords[i];
                    if (record.Renderer != null) record.Renderer.sharedMaterials = record.OriginalMaterials;
                }
                for (int i = 0; i < clonedMaterials.Count; i++)
                {
                    if (clonedMaterials[i] != null) UnityEngine.Object.Destroy(clonedMaterials[i]);
                }
                rendererRecords.Clear();
                clonedMaterials.Clear();
            }
        }

        private sealed class RendererMaterialRecord
        {
            public readonly Renderer Renderer;
            public readonly Material[] OriginalMaterials;

            public RendererMaterialRecord(Renderer renderer, Material[] originalMaterials)
            {
                Renderer = renderer;
                OriginalMaterials = originalMaterials;
            }
        }

        private sealed class ZoneVisual
        {
            private GameObject root;
            private ParticleSystem dust;
            private GameObject piles;
            private GameObject scaffolding;
            private Renderer[] warningLamps;
            private Material[] materials;
            private Texture2D[] textures;
            private int currentStage = -1;

            public ZoneVisual(GameObject root, ParticleSystem dust, GameObject piles, GameObject scaffolding,
                Renderer[] warningLamps, Material[] materials, Texture2D[] textures)
            {
                this.root = root;
                this.dust = dust;
                this.piles = piles;
                this.scaffolding = scaffolding;
                this.warningLamps = warningLamps;
                this.materials = materials;
                this.textures = textures;
            }

            public void UpdateProgress(float progress)
            {
                int stage = progress < 0.25f ? 0 : progress < 0.60f ? 1 : progress < 0.90f ? 2 : 3;
                if (stage != currentStage)
                {
                    currentStage = stage;
                    if (piles != null) piles.SetActive(stage < 2);
                    if (scaffolding != null) scaffolding.SetActive(stage == 1 || stage == 2);
                    if (dust != null)
                    {
                        ParticleSystem.EmissionModule emission = dust.emission;
                        emission.rateOverTime = stage == 0 ? 9f : stage == 1 ? 6f : stage == 2 ? 3f : 1f;
                    }
                }

                bool lampsOn = Mathf.PingPong(Time.time * 2.5f, 1f) > 0.35f;
                if (warningLamps != null)
                {
                    for (int i = 0; i < warningLamps.Length; i++)
                    {
                        if (warningLamps[i] != null) warningLamps[i].enabled = lampsOn;
                    }
                }
            }

            public void Dispose()
            {
                if (root != null)
                {
                    MeshFilter[] filters = root.GetComponentsInChildren<MeshFilter>(true);
                    for (int i = 0; i < filters.Length; i++)
                    {
                        Mesh mesh = filters[i].sharedMesh;
                        if (mesh != null && mesh.name.StartsWith("Construction", StringComparison.Ordinal))
                        {
                            UnityEngine.Object.Destroy(mesh);
                        }
                    }
                    UnityEngine.Object.Destroy(root);
                }
                if (materials != null)
                {
                    for (int i = 0; i < materials.Length; i++)
                    {
                        if (materials[i] != null) UnityEngine.Object.Destroy(materials[i]);
                    }
                }
                if (textures != null)
                {
                    for (int i = 0; i < textures.Length; i++)
                    {
                        if (textures[i] != null) UnityEngine.Object.Destroy(textures[i]);
                    }
                }
                root = null;
                dust = null;
                piles = null;
                scaffolding = null;
                warningLamps = null;
                materials = null;
                textures = null;
            }
        }
    }
}
