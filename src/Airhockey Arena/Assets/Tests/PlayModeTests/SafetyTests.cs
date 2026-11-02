using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

public class SafetyTests
{
    const int WorldColumns = 8;
    const int WorldRows = 12;
    const int SampleColumns = 28;
    const int SampleRows = 40;
    const float MalletRadius = 0.6f;
    const float VisibilityRadius = 0.2f;
    const float MinimumReachableRatio = 0.8f;
    const int SafetySeedCount = 3;
    const string SafetySeedEnvironmentVariable = "AIR_HOCKEY_SAFETY_TEST_SEED";

    [UnityTest]
    public IEnumerator GoalPathwayTest()
    {
        var arena = CreateTestArena();

        try
        {
            foreach (var seed in GetSafetySeeds())
            {
                arena.GenerateWithSeed(seed);
                yield return null;
                Physics.SyncTransforms();

                var blockers = GetBlockingColliders(arena.Root);
                var grid = BuildGrid(arena.GenerationField, blockers, SampleColumns, SampleRows, MalletRadius);
                var center = GetNearestOpenCell(grid.Open, new Vector2Int(SampleColumns / 2, SampleRows / 2));

                var topGoalPath = FindPath(grid.Open, center, cell => cell.y == SampleRows - 1);
                var bottomGoalPath = FindPath(grid.Open, center, cell => cell.y == 0);

                Assert.IsNotNull(topGoalPath, $"Seed {seed} blocked the puck path from center to the top goal.");
                Assert.IsNotNull(bottomGoalPath, $"Seed {seed} blocked the puck path from center to the bottom goal.");

                AssertPathVisible(topGoalPath, grid.Points, blockers, $"Seed {seed} top goal path");
                AssertPathVisible(bottomGoalPath, grid.Points, blockers, $"Seed {seed} bottom goal path");
            }
        }
        finally
        {
            arena.Cleanup();
        }
    }

    [UnityTest]
    public IEnumerator MalletFreedom()
    {
        var arena = CreateTestArena();

        try
        {
            foreach (var seed in GetSafetySeeds())
            {
                arena.GenerateWithSeed(seed);
                yield return null;
                Physics.SyncTransforms();

                var blockers = GetBlockingColliders(arena.Root);
                var grid = BuildGrid(arena.GenerationField, blockers, SampleColumns, SampleRows, MalletRadius);
                var bottomStart = GetNearestOpenCell(grid.Open, new Vector2Int(SampleColumns / 2, 1));
                var topStart = GetNearestOpenCell(grid.Open, new Vector2Int(SampleColumns / 2, SampleRows - 2));

                var bottomReachable = FloodFill(grid.Open, bottomStart);
                var topReachable = FloodFill(grid.Open, topStart);

                AssertReachableRatio(grid.Open, bottomReachable, 0, SampleRows / 2, seed, "bottom mallet half");
                AssertReachableRatio(grid.Open, topReachable, SampleRows / 2, SampleRows, seed, "top mallet half");
                AssertReachableRatio(grid.Open, Union(bottomReachable, topReachable), 0, SampleRows, seed, "combined mallet space");
            }
        }
        finally
        {
            arena.Cleanup();
        }
    }

    static int[] GetSafetySeeds()
    {
        var baseSeedText = Environment.GetEnvironmentVariable(SafetySeedEnvironmentVariable);
        var hasBaseSeed = int.TryParse(baseSeedText, out var baseSeed);

        if (!hasBaseSeed)
        {
            baseSeed = unchecked(Environment.TickCount ^ Guid.NewGuid().GetHashCode());
        }

        var random = new System.Random(baseSeed);
        var seeds = new int[SafetySeedCount];

        for (var i = 0; i < seeds.Length; i++)
        {
            seeds[i] = random.Next(1, int.MaxValue);
        }

        Debug.Log($"SafetyTests base seed {baseSeed}; generated world seeds: {string.Join(", ", seeds)}");
        return seeds;
    }

    static TestArena CreateTestArena()
    {
        var root = new GameObject("SafetyTestWorldGen");
        var fieldObject = new GameObject("GenerationField");
        fieldObject.transform.SetParent(root.transform, false);

        var field = fieldObject.AddComponent<BoxCollider>();
        field.isTrigger = true;
        field.size = new Vector3(12f, 1f, 18f);

        var generator = root.AddComponent(GetRequiredType("WorldGen"));
        var templates = new GameObject("SafetyTestObstacleTemplates");
        templates.transform.position = new Vector3(1000f, 1000f, 1000f);

        SetPrivateField(generator, "generationField", field);
        SetPrivateField(generator, "columns", WorldColumns);
        SetPrivateField(generator, "rows", WorldRows);
        SetPrivateField(generator, "emptyThreshold", 0.35f);
        SetPrivateField(generator, "goalClearRows", 1);
        SetPrivateField(generator, "puckClearRadius", 1.1f);
        SetPrivateField(generator, "cellJitter", 0.18f);
        SetPrivateField(generator, "perlinScale", 3f);
        SetPrivateField(generator, "perlinOctaves", 4);
        SetPrivateField(generator, "perlinPersistence", 0.6f);
        SetPrivateField(generator, "perlinLacunarity", 2f);
        SetPrivateField(generator, "blockingMalletClearance", 1.8f);
        SetPrivateField(generator, "reservedSpawnFields", new List<Collider>());

        SetPrivateField(generator, "obstaclePrefabsList", CreateObstaclePrefabEntries(templates.transform));

        return new TestArena(root, templates, generator, field);
    }

    static Type GetRequiredType(string typeName)
    {
        var type = Type.GetType($"{typeName}, Assembly-CSharp");
        Assert.IsNotNull(type, $"Could not find runtime type {typeName}.");
        return type;
    }

    static object CreateObstaclePrefabEntries(Transform templateParent)
    {
        var entryType = GetRequiredType("ObstaclePrefabEntry");
        var obstacleType = GetRequiredType("ObstacleType");
        var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(entryType));

        list.Add(CreateObstaclePrefabEntry(entryType, obstacleType, "Bumper", CreateTemplate(templateParent, "BumperPrefab", PrimitiveType.Capsule, new Vector3(0.9f, 1f, 0.9f), false)));
        list.Add(CreateObstaclePrefabEntry(entryType, obstacleType, "BoostPad", CreateTemplate(templateParent, "BoostPadPrefab", PrimitiveType.Cube, new Vector3(1.1f, 0.1f, 1.1f), true)));
        list.Add(CreateObstaclePrefabEntry(entryType, obstacleType, "SlowZone", CreateTemplate(templateParent, "SlowZonePrefab", PrimitiveType.Cube, new Vector3(1.1f, 0.1f, 1.1f), true)));
        list.Add(CreateObstaclePrefabEntry(entryType, obstacleType, "GenWall", CreateTemplate(templateParent, "GenWallPrefab", PrimitiveType.Cube, new Vector3(1.4f, 1f, 0.35f), false)));

        return list;
    }

    static object CreateObstaclePrefabEntry(Type entryType, Type obstacleType, string typeName, GameObject prefab)
    {
        var entry = Activator.CreateInstance(entryType);
        entryType.GetField("obstacleType").SetValue(entry, Enum.Parse(obstacleType, typeName));
        entryType.GetField("prefab").SetValue(entry, prefab);
        return entry;
    }

    static GameObject CreateTemplate(Transform parent, string name, PrimitiveType primitiveType, Vector3 scale, bool isTrigger)
    {
        var template = GameObject.CreatePrimitive(primitiveType);
        template.name = name;
        template.transform.SetParent(parent, false);
        template.transform.localScale = scale;

        foreach (var collider in template.GetComponentsInChildren<Collider>())
        {
            collider.isTrigger = isTrigger;
        }

        return template;
    }

    static List<Collider> GetBlockingColliders(GameObject root)
    {
        var blockers = new List<Collider>();
        foreach (var collider in root.GetComponentsInChildren<Collider>())
        {
            if (!collider.isTrigger)
            {
                blockers.Add(collider);
            }
        }

        return blockers;
    }

    static GridSample BuildGrid(BoxCollider field, List<Collider> blockers, int columns, int rows, float radius)
    {
        var open = new bool[columns, rows];
        var points = new Vector3[columns, rows];

        for (var x = 0; x < columns; x++)
        {
            for (var y = 0; y < rows; y++)
            {
                var point = GetFieldPoint(field, x, y, columns, rows);
                points[x, y] = point;
                open[x, y] = IsPointOpen(point, blockers, radius);
            }
        }

        return new GridSample(open, points);
    }

    static Vector3 GetFieldPoint(BoxCollider field, int x, int y, int columns, int rows)
    {
        var localX = Mathf.Lerp(-field.size.x * 0.5f, field.size.x * 0.5f, (x + 0.5f) / columns);
        var localZ = Mathf.Lerp(-field.size.z * 0.5f, field.size.z * 0.5f, (y + 0.5f) / rows);
        return field.transform.TransformPoint(field.center + new Vector3(localX, 0f, localZ));
    }

    static bool IsPointOpen(Vector3 point, List<Collider> blockers, float radius)
    {
        var radiusSquared = radius * radius;
        foreach (var blocker in blockers)
        {
            var closest = blocker.ClosestPoint(point);
            var delta = new Vector2(closest.x - point.x, closest.z - point.z);
            if (delta.sqrMagnitude <= radiusSquared)
            {
                return false;
            }
        }

        return true;
    }

    static Vector2Int GetNearestOpenCell(bool[,] open, Vector2Int desired)
    {
        var best = new Vector2Int(-1, -1);
        var bestDistance = int.MaxValue;

        for (var x = 0; x < open.GetLength(0); x++)
        {
            for (var y = 0; y < open.GetLength(1); y++)
            {
                if (!open[x, y])
                {
                    continue;
                }

                var distance = Mathf.Abs(x - desired.x) + Mathf.Abs(y - desired.y);
                if (distance < bestDistance)
                {
                    best = new Vector2Int(x, y);
                    bestDistance = distance;
                }
            }
        }

        Assert.AreNotEqual(new Vector2Int(-1, -1), best, "No open grid cell was available.");
        return best;
    }

    static List<Vector2Int> FindPath(bool[,] open, Vector2Int start, System.Predicate<Vector2Int> isGoal)
    {
        var columns = open.GetLength(0);
        var rows = open.GetLength(1);
        var queue = new Queue<Vector2Int>();
        var visited = new bool[columns, rows];
        var previous = new Vector2Int[columns, rows];

        for (var x = 0; x < columns; x++)
        {
            for (var y = 0; y < rows; y++)
            {
                previous[x, y] = new Vector2Int(-1, -1);
            }
        }

        queue.Enqueue(start);
        visited[start.x, start.y] = true;

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (isGoal(current))
            {
                return ReconstructPath(previous, current);
            }

            foreach (var neighbor in GetNeighbors(current, columns, rows))
            {
                if (visited[neighbor.x, neighbor.y] || !open[neighbor.x, neighbor.y])
                {
                    continue;
                }

                visited[neighbor.x, neighbor.y] = true;
                previous[neighbor.x, neighbor.y] = current;
                queue.Enqueue(neighbor);
            }
        }

        return null;
    }

    static bool[,] FloodFill(bool[,] open, Vector2Int start)
    {
        var columns = open.GetLength(0);
        var rows = open.GetLength(1);
        var reachable = new bool[columns, rows];
        var queue = new Queue<Vector2Int>();

        queue.Enqueue(start);
        reachable[start.x, start.y] = true;

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var neighbor in GetNeighbors(current, columns, rows))
            {
                if (reachable[neighbor.x, neighbor.y] || !open[neighbor.x, neighbor.y])
                {
                    continue;
                }

                reachable[neighbor.x, neighbor.y] = true;
                queue.Enqueue(neighbor);
            }
        }

        return reachable;
    }

    static IEnumerable<Vector2Int> GetNeighbors(Vector2Int cell, int columns, int rows)
    {
        if (cell.x > 0) yield return new Vector2Int(cell.x - 1, cell.y);
        if (cell.x < columns - 1) yield return new Vector2Int(cell.x + 1, cell.y);
        if (cell.y > 0) yield return new Vector2Int(cell.x, cell.y - 1);
        if (cell.y < rows - 1) yield return new Vector2Int(cell.x, cell.y + 1);
    }

    static List<Vector2Int> ReconstructPath(Vector2Int[,] previous, Vector2Int end)
    {
        var path = new List<Vector2Int>();
        var current = end;

        while (current.x >= 0)
        {
            path.Add(current);
            current = previous[current.x, current.y];
        }

        path.Reverse();
        return path;
    }

    static void AssertPathVisible(List<Vector2Int> path, Vector3[,] points, List<Collider> blockers, string label)
    {
        for (var i = 0; i < path.Count - 1; i++)
        {
            var start = points[path[i].x, path[i].y];
            var end = points[path[i + 1].x, path[i + 1].y];
            Assert.IsTrue(IsSegmentOpen(start, end, blockers, VisibilityRadius), $"{label} has a blocked visible segment.");
        }
    }

    static bool IsSegmentOpen(Vector3 start, Vector3 end, List<Collider> blockers, float radius)
    {
        var distance = Vector3.Distance(start, end);
        var steps = Mathf.Max(1, Mathf.CeilToInt(distance / radius));

        for (var i = 0; i <= steps; i++)
        {
            var point = Vector3.Lerp(start, end, i / (float)steps);
            if (!IsPointOpen(point, blockers, radius))
            {
                return false;
            }
        }

        return true;
    }

    static bool[,] Union(bool[,] first, bool[,] second)
    {
        var columns = first.GetLength(0);
        var rows = first.GetLength(1);
        var result = new bool[columns, rows];

        for (var x = 0; x < columns; x++)
        {
            for (var y = 0; y < rows; y++)
            {
                result[x, y] = first[x, y] || second[x, y];
            }
        }

        return result;
    }

    static void AssertReachableRatio(bool[,] open, bool[,] reachable, int minRow, int maxRow, int seed, string areaName)
    {
        var openCount = 0;
        var reachableCount = 0;

        for (var x = 0; x < open.GetLength(0); x++)
        {
            for (var y = minRow; y < maxRow; y++)
            {
                if (!open[x, y])
                {
                    continue;
                }

                openCount++;
                if (reachable[x, y])
                {
                    reachableCount++;
                }
            }
        }

        Assert.Greater(openCount, 0, $"Seed {seed} had no open cells in the {areaName}.");
        var reachableRatio = reachableCount / (float)openCount;
        Assert.GreaterOrEqual(
            reachableRatio,
            MinimumReachableRatio,
            $"Seed {seed} only allowed the {areaName} to reach {reachableRatio:P0} of open space."
        );
    }

    static void SetPrivateField<T>(object target, string fieldName, T value)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(field, $"Could not find private field {fieldName}.");
        field.SetValue(target, value);
    }

    class GridSample
    {
        public bool[,] Open { get; }
        public Vector3[,] Points { get; }

        public GridSample(bool[,] open, Vector3[,] points)
        {
            Open = open;
            Points = points;
        }
    }

    class TestArena
    {
        public GameObject Root { get; }
        public Component Generator { get; }
        public BoxCollider GenerationField { get; }

        readonly GameObject templates;
        readonly MethodInfo generateWithSeedMethod;

        public TestArena(GameObject root, GameObject templates, Component generator, BoxCollider generationField)
        {
            Root = root;
            this.templates = templates;
            Generator = generator;
            GenerationField = generationField;
            generateWithSeedMethod = generator.GetType().GetMethod("GenerateWithSeed", BindingFlags.Instance | BindingFlags.Public);
            Assert.IsNotNull(generateWithSeedMethod, "WorldGen.GenerateWithSeed was not found.");
        }

        public void GenerateWithSeed(int seed)
        {
            generateWithSeedMethod.Invoke(Generator, new object[] { seed });
        }

        public void Cleanup()
        {
            UnityEngine.Object.Destroy(Root);
            UnityEngine.Object.Destroy(templates);
        }
    }
}
