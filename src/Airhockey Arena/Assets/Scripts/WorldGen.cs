using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Serialization;

public class WorldGen : MonoBehaviour
{
    const string k_GenerationFieldName = "GenerationField";
    const string k_BlueSpawnFieldName = "BlueField";
    const string k_RedSpawnFieldName = "RedField";
    const float k_BlockingPathCost = 20f;
    const int k_SpawnPointAttempts = 30;

    static readonly Vector2Int[] s_PathDirections =
    {
        new Vector2Int(1, 0),
        new Vector2Int(-1, 0),
        new Vector2Int(0, 1),
        new Vector2Int(0, -1)
    };

    [SerializeField] private int seed = -1;

    [Header("Bounds")]
    [SerializeField] private BoxCollider generationField;
    [SerializeField] private List<Collider> reservedSpawnFields = new List<Collider>();

    [Header("Grid")]
    [FormerlySerializedAs("width")]
    [SerializeField, Min(2)] private int columns = 4;
    [FormerlySerializedAs("height")]
    [SerializeField, Min(2)] private int rows = 8;
    [FormerlySerializedAs("obstacleY")]
    [SerializeField] private float surfaceYOffset;
    [SerializeField, Range(0f, 1f)] private float emptyThreshold = 0.68f;
    [SerializeField, Range(0f, 0.45f)] private float cellJitter = 0.2f;
    [SerializeField, Min(0)] private int goalClearRows = 1;
    [SerializeField, Min(0f)] private float puckClearRadius = 2f;

    [Header("Obstacle Preference")]
    [Tooltip("When set, occupied cells are more likely to become this obstacle type.")]
    [SerializeField] private ObstacleType preferredObstacleType = ObstacleType.None;
    [Tooltip("Chance that an occupied cell uses the preferred obstacle. The remaining chance is split between the other obstacle types.")]
    [SerializeField, Range(0f, 1f)] private float preferredObstacleChance = 0.5f;

    [Header("Perlin Settings")]
    [SerializeField, Min(0.01f)] private float perlinScale = 30f;
    [SerializeField, Min(1)] private int perlinOctaves = 3;
    [SerializeField, Range(0f, 1f)] private float perlinPersistence = 0.5f;
    [SerializeField, Min(1f)] private float perlinLacunarity = 2f;
    [SerializeField] private Vector2 perlinOffset = Vector2.zero;
    [SerializeField] private Vector2 obstacleTypeOffset = new Vector2(10000f, 10000f);
    [SerializeField] private Vector2 rotationOffset = new Vector2(20000f, 20000f);

    [Header("Rotation")]
    [SerializeField, Range(0f, 360f)] private float yRotationRange = 360f;

    [Header("Blocking Clearance")]
    [SerializeField, Min(0f)] private float blockingMalletClearance = 2.4f;

    [Header("Prefabs")]
    [FormerlySerializedAs("tilePrefabsList")]
    [SerializeField] private List<ObstaclePrefabEntry> obstaclePrefabsList = new List<ObstaclePrefabEntry>();

    private readonly Dictionary<ObstacleType, GameObject> obstaclePrefabs = new Dictionary<ObstacleType, GameObject>();
    private readonly List<ObstacleType> availableObstacleTypes = new List<ObstacleType>();
    private readonly List<GameObject> spawnedObstacles = new List<GameObject>();
    private readonly List<Collider> spawnedObstacleColliders = new List<Collider>();
    private ObstacleType[,] obstacleGrid;
    private Vector2[,] jitterGrid;
    private float[,] yRotationGrid;
    private GameObject[,] spawnedObstacleGrid;
    private bool hasGenerated;

    public bool HasGenerated => hasGenerated;

    void Awake()
    {
        GenerateCurrentSeed();
    }

    public void EnsureGenerated()
    {
        if (!hasGenerated)
        {
            GenerateCurrentSeed();
        }
    }

    public void GenerateCurrentSeed()
    {
        GenerateWithSeed(seed);
    }

    public void GenerateWithRandomSafeSeed()
    {
        GenerateWithSeed(System.Environment.TickCount ^ Random.Range(0, int.MaxValue));
    }

    public void GenerateWithSeed(int newSeed)
    {
        seed = newSeed;
        hasGenerated = false;

        BuildObstaclePrefabCache();
        ResolveGenerationField();
        ResolveReservedSpawnFields();
        ClearSpawnedObstacles();

        obstacleGrid = new ObstacleType[columns, rows];
        jitterGrid = new Vector2[columns, rows];
        yRotationGrid = new float[columns, rows];
        spawnedObstacleGrid = new GameObject[columns, rows];

        if (generationField == null || availableObstacleTypes.Count == 0)
        {
            return;
        }

        GenerateMirroredLayout();
        ValidatePuckPaths();
        SpawnLayout();
        hasGenerated = true;
    }

    public void ClearGeneratedWorld()
    {
        ClearSpawnedObstacles();
        obstacleGrid = null;
        jitterGrid = null;
        yRotationGrid = null;
        spawnedObstacleGrid = null;
        hasGenerated = false;
    }

    public bool TryGetSafePointInSpawnField(Collider spawnField, float y, float radius, out Vector3 point)
    {
        EnsureGenerated();

        for (int attempt = 0; attempt < k_SpawnPointAttempts; attempt++)
        {
            point = RandomPointInCollider(spawnField, y);
            if (IsPointClearOfObstacles(point, radius))
            {
                return true;
            }
        }

        return TryGetSafeGridPointInSpawnField(spawnField, y, radius, out point);
    }

    void BuildObstaclePrefabCache()
    {
        obstaclePrefabs.Clear();
        availableObstacleTypes.Clear();

        for (int i = 0; i < obstaclePrefabsList.Count; i++)
        {
            var entry = obstaclePrefabsList[i];
            if (entry == null || entry.obstacleType == ObstacleType.None || entry.prefab == null)
            {
                continue;
            }

            if (!obstaclePrefabs.ContainsKey(entry.obstacleType))
            {
                availableObstacleTypes.Add(entry.obstacleType);
            }

            obstaclePrefabs[entry.obstacleType] = entry.prefab;
        }
    }

    void ResolveGenerationField()
    {
        if (generationField != null)
        {
            return;
        }

        foreach (var boxCollider in GetComponentsInChildren<BoxCollider>(true))
        {
            if (boxCollider.name == k_GenerationFieldName)
            {
                generationField = boxCollider;
                return;
            }
        }

        Debug.LogWarning($"{nameof(WorldGen)} could not find a child BoxCollider named {k_GenerationFieldName}.", this);
    }

    void ResolveReservedSpawnFields()
    {
        reservedSpawnFields.RemoveAll(spawnField => spawnField == null);
    }

    void GenerateMirroredLayout()
    {
        float[,] occupancyMap = GeneratePerlinNoiseMap(perlinOffset);
        float[,] obstacleTypeMap = GeneratePerlinNoiseMap(perlinOffset + obstacleTypeOffset);
        float[,] rotationMap = GeneratePerlinNoiseMap(perlinOffset + rotationOffset);
        var layoutRandom = new System.Random(seed == -1 ? 0 : seed);
        int halfRows = rows / 2;

        for (int y = 0; y < halfRows; y++)
        {
            int mirrorY = rows - 1 - y;
            for (int x = 0; x < columns; x++)
            {
                Vector2 jitter = GetCellJitter(layoutRandom);
                jitterGrid[x, y] = jitter;
                jitterGrid[x, mirrorY] = new Vector2(jitter.x, -jitter.y);

                float yRotation = GetYRotation(rotationMap[x, y]);
                yRotationGrid[x, y] = yRotation;
                yRotationGrid[x, mirrorY] = GetMirroredYRotation(yRotation);

                ObstacleType type = GetObstacleType(occupancyMap[x, y], obstacleTypeMap[x, y]);
                obstacleGrid[x, y] = IsReservedCell(x, y) ? ObstacleType.None : type;
                obstacleGrid[x, mirrorY] = IsReservedCell(x, mirrorY) ? ObstacleType.None : type;
            }
        }

        if (rows % 2 != 0)
        {
            int centerY = rows / 2;
            for (int x = 0; x < columns; x++)
            {
                jitterGrid[x, centerY] = GetCellJitter(layoutRandom);
                yRotationGrid[x, centerY] = GetYRotation(rotationMap[x, centerY]);
                obstacleGrid[x, centerY] = ObstacleType.None;
            }
        }
    }

    float[,] GeneratePerlinNoiseMap(Vector2 mapOffset)
    {
        float[,] noiseMap = new float[columns, rows];
        float maxNoiseHeight = float.MinValue;
        float minNoiseHeight = float.MaxValue;
        float scale = Mathf.Max(0.01f, perlinScale);
        int octaveCount = Mathf.Max(1, perlinOctaves);
        Vector2[] octaveOffsets = GenerateOctaveOffsets(octaveCount, mapOffset);
        float halfWidth = columns * 0.5f;
        float halfHeight = rows * 0.5f;

        for (int y = 0; y < rows; y++)
        {
            for (int x = 0; x < columns; x++)
            {
                float amplitude = 1f;
                float frequency = 1f;
                float noiseHeight = 0f;

                for (int octave = 0; octave < octaveCount; octave++)
                {
                    float sampleX = (x - halfWidth + octaveOffsets[octave].x) / scale * frequency;
                    float sampleY = (y - halfHeight + octaveOffsets[octave].y) / scale * frequency;

                    noiseHeight += (Mathf.PerlinNoise(sampleX, sampleY) * 2f - 1f) * amplitude;
                    amplitude *= perlinPersistence;
                    frequency *= perlinLacunarity;
                }

                if (noiseHeight > maxNoiseHeight)
                {
                    maxNoiseHeight = noiseHeight;
                }

                if (noiseHeight < minNoiseHeight)
                {
                    minNoiseHeight = noiseHeight;
                }

                noiseMap[x, y] = noiseHeight;
            }
        }

        if (Mathf.Approximately(minNoiseHeight, maxNoiseHeight))
        {
            for (int x = 0; x < columns; x++)
            {
                for (int y = 0; y < rows; y++)
                {
                    noiseMap[x, y] = 0.5f;
                }
            }

            return noiseMap;
        }

        for (int x = 0; x < columns; x++)
        {
            for (int y = 0; y < rows; y++)
            {
                noiseMap[x, y] = Mathf.InverseLerp(minNoiseHeight, maxNoiseHeight, noiseMap[x, y]);
            }
        }

        return noiseMap;
    }

    Vector2[] GenerateOctaveOffsets(int octaveCount, Vector2 mapOffset)
    {
        Vector2[] octaveOffsets = new Vector2[octaveCount];
        int noiseSeed = seed == -1 ? 0 : seed;
        System.Random random = new System.Random(noiseSeed);

        for (int octave = 0; octave < octaveCount; octave++)
        {
            float offsetX = random.Next(-100000, 100000) + mapOffset.x;
            float offsetY = random.Next(-100000, 100000) + mapOffset.y;
            octaveOffsets[octave] = new Vector2(offsetX, offsetY);
        }

        return octaveOffsets;
    }

    ObstacleType GetObstacleType(float occupancyValue, float typeValue)
    {
        if (occupancyValue < emptyThreshold || availableObstacleTypes.Count == 0)
        {
            return ObstacleType.None;
        }

        if (CanUsePreferredObstacleType())
        {
            if (typeValue < preferredObstacleChance || Mathf.Approximately(preferredObstacleChance, 1f))
            {
                return preferredObstacleType;
            }

            var fallbackValue = Mathf.InverseLerp(preferredObstacleChance, 1f, typeValue);
            return GetAvailableObstacleType(fallbackValue, preferredObstacleType);
        }

        return GetAvailableObstacleType(typeValue, ObstacleType.None);
    }

    bool CanUsePreferredObstacleType()
    {
        return preferredObstacleType != ObstacleType.None
            && preferredObstacleChance > 0f
            && availableObstacleTypes.Contains(preferredObstacleType);
    }

    ObstacleType GetAvailableObstacleType(float typeValue, ObstacleType excludedType)
    {
        int selectableCount = GetAvailableObstacleTypeCount(excludedType);
        if (selectableCount == 0)
        {
            return availableObstacleTypes[0];
        }

        int targetIndex = Mathf.Min((int)(Mathf.Clamp01(typeValue) * selectableCount), selectableCount - 1);
        int currentIndex = 0;
        for (int i = 0; i < availableObstacleTypes.Count; i++)
        {
            var obstacleType = availableObstacleTypes[i];
            if (obstacleType == excludedType)
            {
                continue;
            }

            if (currentIndex == targetIndex)
            {
                return obstacleType;
            }

            currentIndex++;
        }

        return availableObstacleTypes[availableObstacleTypes.Count - 1];
    }

    int GetAvailableObstacleTypeCount(ObstacleType excludedType)
    {
        int count = 0;
        for (int i = 0; i < availableObstacleTypes.Count; i++)
        {
            if (availableObstacleTypes[i] != excludedType)
            {
                count++;
            }
        }

        return count;
    }

    float GetYRotation(float noiseValue)
    {
        return noiseValue * yRotationRange;
    }

    float GetMirroredYRotation(float yRotation)
    {
        return Mathf.Repeat(180f - yRotation, 360f);
    }

    Vector2 GetCellJitter(System.Random random)
    {
        if (cellJitter <= 0f)
        {
            return Vector2.zero;
        }

        float cellWidth = generationField.size.x / columns;
        float cellDepth = generationField.size.z / rows;
        float offsetX = ((float)random.NextDouble() * 2f - 1f) * cellWidth * cellJitter;
        float offsetZ = ((float)random.NextDouble() * 2f - 1f) * cellDepth * cellJitter;
        return new Vector2(offsetX, offsetZ);
    }

    bool IsReservedCell(int x, int y)
    {
        if (goalClearRows > 0 && (y < goalClearRows || y >= rows - goalClearRows))
        {
            return true;
        }

        Vector3 cellCenter = GetCellWorldPosition(x, y, false);
        if (puckClearRadius > 0f && PlanarDistanceSqr(cellCenter, GetGenerationCenterWorld()) <= puckClearRadius * puckClearRadius)
        {
            return true;
        }

        for (int i = 0; i < reservedSpawnFields.Count; i++)
        {
            if (ShouldReserveCollider(reservedSpawnFields[i]) && IsPointInsideColliderXZ(reservedSpawnFields[i], cellCenter))
            {
                return true;
            }
        }

        return false;
    }

    void ValidatePuckPaths()
    {
        int centerX = Mathf.Clamp(columns / 2, 0, columns - 1);
        int centerY = Mathf.Clamp(rows / 2, 0, rows - 1);

        obstacleGrid[centerX, centerY] = ObstacleType.None;
        if (rows % 2 == 0 && centerY > 0)
        {
            obstacleGrid[centerX, centerY - 1] = ObstacleType.None;
        }

        CarveLeastBlockingPath(centerX, centerY, 0);
        CarveLeastBlockingPath(centerX, centerY, rows - 1);
    }

    void CarveLeastBlockingPath(int startX, int startY, int targetY)
    {
        int cellCount = columns * rows;
        float[] costs = new float[cellCount];
        int[] previous = new int[cellCount];
        bool[] visited = new bool[cellCount];

        for (int i = 0; i < cellCount; i++)
        {
            costs[i] = float.PositiveInfinity;
            previous[i] = -1;
        }

        int startIndex = GetCellIndex(startX, startY);
        costs[startIndex] = 0f;
        int targetIndex = -1;

        for (int step = 0; step < cellCount; step++)
        {
            int currentIndex = GetCheapestUnvisitedIndex(costs, visited);
            if (currentIndex == -1)
            {
                break;
            }

            visited[currentIndex] = true;
            int currentX = currentIndex % columns;
            int currentY = currentIndex / columns;

            if (currentY == targetY)
            {
                targetIndex = currentIndex;
                break;
            }

            for (int i = 0; i < s_PathDirections.Length; i++)
            {
                int nextX = currentX + s_PathDirections[i].x;
                int nextY = currentY + s_PathDirections[i].y;
                if (!IsInGrid(nextX, nextY))
                {
                    continue;
                }

                int nextIndex = GetCellIndex(nextX, nextY);
                if (visited[nextIndex])
                {
                    continue;
                }

                float nextCost = costs[currentIndex] + GetPathCost(obstacleGrid[nextX, nextY]);
                if (nextCost < costs[nextIndex])
                {
                    costs[nextIndex] = nextCost;
                    previous[nextIndex] = currentIndex;
                }
            }
        }

        if (targetIndex == -1)
        {
            ClearStraightPath(startX, startY, targetY);
            return;
        }

        int pathIndex = targetIndex;
        while (pathIndex != -1)
        {
            int x = pathIndex % columns;
            int y = pathIndex / columns;
            if (IsBlockingObstacle(obstacleGrid[x, y]))
            {
                obstacleGrid[x, y] = ObstacleType.None;
            }

            pathIndex = previous[pathIndex];
        }
    }

    int GetCheapestUnvisitedIndex(float[] costs, bool[] visited)
    {
        int cheapestIndex = -1;
        float cheapestCost = float.PositiveInfinity;

        for (int i = 0; i < costs.Length; i++)
        {
            if (!visited[i] && costs[i] < cheapestCost)
            {
                cheapestCost = costs[i];
                cheapestIndex = i;
            }
        }

        return cheapestIndex;
    }

    float GetPathCost(ObstacleType type)
    {
        return IsBlockingObstacle(type) ? k_BlockingPathCost : 1f;
    }

    void ClearStraightPath(int x, int startY, int targetY)
    {
        int step = targetY >= startY ? 1 : -1;
        for (int y = startY; y != targetY + step; y += step)
        {
            if (IsInGrid(x, y) && IsBlockingObstacle(obstacleGrid[x, y]))
            {
                obstacleGrid[x, y] = ObstacleType.None;
            }
        }
    }

    void SpawnLayout()
    {
        for (int x = 0; x < columns; x++)
        {
            for (int y = 0; y < rows; y++)
            {
                SpawnObstacle(x, y, obstacleGrid[x, y]);
            }
        }

        EnforceBlockingMalletClearance();
    }

    void SpawnObstacle(int x, int y, ObstacleType type)
    {
        if (type == ObstacleType.None || !obstaclePrefabs.TryGetValue(type, out GameObject prefab) || prefab == null)
        {
            return;
        }

        Vector3 obstaclePosition = GetCellWorldPosition(x, y, true);
        Quaternion obstacleRotation = Quaternion.Euler(0f, yRotationGrid[x, y], 0f) * prefab.transform.rotation;
        GameObject obstacleObj = Instantiate(prefab, obstaclePosition, obstacleRotation, transform);
        obstacleObj.name = $"Obstacle_{x}_{y}_{type}";
        spawnedObstacleGrid[x, y] = obstacleObj;
        spawnedObstacles.Add(obstacleObj);
        obstacleObj.GetComponentsInChildren<Collider>(spawnedObstacleColliders);
    }

    void EnforceBlockingMalletClearance()
    {
        if (blockingMalletClearance <= 0f || spawnedObstacleGrid == null)
        {
            return;
        }

        bool removedBlockingObstacle;
        int remainingPasses = columns * rows;
        do
        {
            removedBlockingObstacle = false;

            for (int firstIndex = 0; firstIndex < columns * rows && !removedBlockingObstacle; firstIndex++)
            {
                int firstX = firstIndex % columns;
                int firstY = firstIndex / columns;
                if (!IsSpawnedBlockingObstacle(firstX, firstY))
                {
                    continue;
                }

                for (int secondIndex = firstIndex + 1; secondIndex < columns * rows; secondIndex++)
                {
                    int secondX = secondIndex % columns;
                    int secondY = secondIndex / columns;
                    if (!IsSpawnedBlockingObstacle(secondX, secondY))
                    {
                        continue;
                    }

                    if (GetPlanarGapBetweenObstacles(
                        spawnedObstacleGrid[firstX, firstY],
                        spawnedObstacleGrid[secondX, secondY]) < blockingMalletClearance)
                    {
                        RemoveSpawnedObstacleAndMirror(secondX, secondY);
                        removedBlockingObstacle = true;
                        break;
                    }
                }
            }

            remainingPasses--;
        }
        while (removedBlockingObstacle && remainingPasses > 0);
    }

    bool IsSpawnedBlockingObstacle(int x, int y)
    {
        return IsInGrid(x, y)
            && IsBlockingObstacle(obstacleGrid[x, y])
            && spawnedObstacleGrid[x, y] != null;
    }

    void RemoveSpawnedObstacleAndMirror(int x, int y)
    {
        RemoveSpawnedObstacleAt(x, y);

        int mirrorY = rows - 1 - y;
        if (mirrorY != y)
        {
            RemoveSpawnedObstacleAt(x, mirrorY);
        }
    }

    void RemoveSpawnedObstacleAt(int x, int y)
    {
        if (!IsInGrid(x, y))
        {
            return;
        }

        GameObject obstacleObj = spawnedObstacleGrid[x, y];
        obstacleGrid[x, y] = ObstacleType.None;
        spawnedObstacleGrid[x, y] = null;

        if (obstacleObj == null)
        {
            return;
        }

        RemoveCachedColliders(obstacleObj);
        spawnedObstacles.Remove(obstacleObj);
        obstacleObj.SetActive(false);

        if (Application.isPlaying)
        {
            Destroy(obstacleObj);
        }
        else
        {
            DestroyImmediate(obstacleObj);
        }
    }

    void RemoveCachedColliders(GameObject obstacleObj)
    {
        Collider[] colliders = obstacleObj.GetComponentsInChildren<Collider>();
        for (int i = 0; i < colliders.Length; i++)
        {
            spawnedObstacleColliders.Remove(colliders[i]);
        }
    }

    float GetPlanarGapBetweenObstacles(GameObject first, GameObject second)
    {
        if (first == null || second == null)
        {
            return float.PositiveInfinity;
        }

        Collider[] firstColliders = first.GetComponentsInChildren<Collider>();
        Collider[] secondColliders = second.GetComponentsInChildren<Collider>();
        float minGap = float.PositiveInfinity;

        for (int i = 0; i < firstColliders.Length; i++)
        {
            Collider firstCollider = firstColliders[i];
            if (firstCollider == null || !firstCollider.enabled)
            {
                continue;
            }

            for (int j = 0; j < secondColliders.Length; j++)
            {
                Collider secondCollider = secondColliders[j];
                if (secondCollider == null || !secondCollider.enabled)
                {
                    continue;
                }

                minGap = Mathf.Min(minGap, GetPlanarGapBetweenColliders(firstCollider, secondCollider));
            }
        }

        return minGap;
    }

    float GetPlanarGapBetweenColliders(Collider first, Collider second)
    {
        Vector3 secondCenter = second.bounds.center;
        Vector3 firstPoint = first.ClosestPoint(secondCenter);
        Vector3 secondPoint = second.ClosestPoint(firstPoint);
        firstPoint = first.ClosestPoint(secondPoint);
        return Mathf.Sqrt(PlanarDistanceSqr(firstPoint, secondPoint));
    }

    void ClearSpawnedObstacles()
    {
        for (int i = spawnedObstacles.Count - 1; i >= 0; i--)
        {
            if (spawnedObstacles[i] == null)
            {
                continue;
            }

            spawnedObstacles[i].SetActive(false);
            if (Application.isPlaying)
            {
                Destroy(spawnedObstacles[i]);
            }
            else
            {
                DestroyImmediate(spawnedObstacles[i]);
            }
        }

        spawnedObstacles.Clear();
        spawnedObstacleColliders.Clear();
        spawnedObstacleGrid = null;
    }

    bool TryGetSafeGridPointInSpawnField(Collider spawnField, float y, float radius, out Vector3 point)
    {
        point = default;
        if (obstacleGrid == null)
        {
            return false;
        }

        bool found = false;
        int safeCellCount = 0;

        for (int x = 0; x < columns; x++)
        {
            for (int row = 0; row < rows; row++)
            {
                if (obstacleGrid[x, row] != ObstacleType.None)
                {
                    continue;
                }

                Vector3 candidate = GetCellWorldPosition(x, row, false);
                candidate.y = y;
                if (!IsPointInsideColliderXZ(spawnField, candidate) || !IsPointClearOfObstacles(candidate, radius))
                {
                    continue;
                }

                safeCellCount++;
                if (Random.Range(0, safeCellCount) == 0)
                {
                    point = candidate;
                    found = true;
                }
            }
        }

        return found;
    }

    Vector3 RandomPointInCollider(Collider targetCollider, float y)
    {
        if (targetCollider is BoxCollider boxCollider)
        {
            Vector3 halfSize = boxCollider.size * 0.5f;
            Vector3 localPoint = boxCollider.center + new Vector3(
                Random.Range(-halfSize.x, halfSize.x),
                0f,
                Random.Range(-halfSize.z, halfSize.z));
            Vector3 worldPoint = boxCollider.transform.TransformPoint(localPoint);
            worldPoint.y = y;
            return worldPoint;
        }

        Bounds bounds = targetCollider.bounds;
        return new Vector3(
            Random.Range(bounds.min.x, bounds.max.x),
            y,
            Random.Range(bounds.min.z, bounds.max.z));
    }

    bool IsPointClearOfObstacles(Vector3 point, float radius)
    {
        float radiusSqr = radius * radius;

        for (int i = 0; i < spawnedObstacleColliders.Count; i++)
        {
            Collider obstacleCollider = spawnedObstacleColliders[i];
            if (obstacleCollider == null || !obstacleCollider.enabled)
            {
                continue;
            }

            Vector3 closestPoint = obstacleCollider.ClosestPoint(point);
            if (PlanarDistanceSqr(point, closestPoint) <= radiusSqr)
            {
                return false;
            }
        }

        return true;
    }

    Vector3 GetCellWorldPosition(int x, int y, bool includeJitter)
    {
        float cellWidth = generationField.size.x / columns;
        float cellDepth = generationField.size.z / rows;
        Vector2 jitter = includeJitter ? jitterGrid[x, y] : Vector2.zero;
        Vector3 localPoint = generationField.center + new Vector3(
            -generationField.size.x * 0.5f + cellWidth * (x + 0.5f) + jitter.x,
            surfaceYOffset,
            -generationField.size.z * 0.5f + cellDepth * (y + 0.5f) + jitter.y);

        return generationField.transform.TransformPoint(localPoint);
    }

    Vector3 GetGenerationCenterWorld()
    {
        return generationField.transform.TransformPoint(generationField.center + new Vector3(0f, surfaceYOffset, 0f));
    }

    bool IsPointInsideColliderXZ(Collider targetCollider, Vector3 point)
    {
        if (targetCollider == null)
        {
            return false;
        }

        if (targetCollider is BoxCollider boxCollider)
        {
            Vector3 localPoint = boxCollider.transform.InverseTransformPoint(point) - boxCollider.center;
            Vector3 halfSize = boxCollider.size * 0.5f;
            return Mathf.Abs(localPoint.x) <= halfSize.x && Mathf.Abs(localPoint.z) <= halfSize.z;
        }

        Bounds bounds = targetCollider.bounds;
        return point.x >= bounds.min.x
            && point.x <= bounds.max.x
            && point.z >= bounds.min.z
            && point.z <= bounds.max.z;
    }

    bool ShouldReserveCollider(Collider targetCollider)
    {
        if (targetCollider == null)
        {
            return false;
        }

        return targetCollider.name != k_BlueSpawnFieldName && targetCollider.name != k_RedSpawnFieldName;
    }

    bool IsInGrid(int x, int y)
    {
        return x >= 0 && x < columns && y >= 0 && y < rows;
    }

    int GetCellIndex(int x, int y)
    {
        return y * columns + x;
    }

    static bool IsBlockingObstacle(ObstacleType type)
    {
        return type == ObstacleType.Bumper || type == ObstacleType.GenWall;
    }

    static float PlanarDistanceSqr(Vector3 a, Vector3 b)
    {
        float dx = a.x - b.x;
        float dz = a.z - b.z;
        return dx * dx + dz * dz;
    }
}

public enum ObstacleType
{
    None,
    Bumper,
    BoostPad,
    SlowZone,
    GenWall
}

[System.Serializable]
public class ObstaclePrefabEntry
{
    public ObstacleType obstacleType;
    public GameObject prefab;
}
