using System.Collections.Generic;
using Unity.MLAgents;
using UnityEngine;

public enum ObstacleGenerationMode
{
    Manual,
    RandomSafeSeedEveryReset
}

public class AirHockeyEnvController : MonoBehaviour
{
    const float k_DefaultSideHalfWidth = 5.5f;
    const float k_DefaultCenterLinePadding = 0.7f;
    const float k_DefaultGoalDepthPercent = 0.92f;
    const float k_DefaultGoalDistance = 14.8f;
    const float k_DefaultResetPositionJitterX = 1.5f;
    const float k_DefaultResetPositionJitterZ = 0.5f;
    const float k_DefaultPuckResetJitterX = 1.5f;
    const float k_DefaultPuckResetJitterZ = 0.5f;
    const float k_DefaultMaxPuckSpeed = 35f;

    [System.Serializable]
    public class PlayerInfo
    {
        public AirHockeyAgent Agent;

        [HideInInspector]
        public Vector3 StartingPos;

        [HideInInspector]
        public Quaternion StartingRot;

        [HideInInspector]
        public Rigidbody Rb;
    }

    [Tooltip("Max environment steps before the rally resets.")]
    public int MaxEnvironmentSteps = 25000;

    [Tooltip("Reward applied to active agents when MaxEnvironmentSteps expires during training.")]
    public float timeoutPenalty = -0.05f;

    [Tooltip("Enable this for ML-Agents training. Leave it off for heuristic demo recording or human play.")]
    public bool endAgentEpisodesOnGoal;

    [Tooltip("Allow pressing R during play mode to reset the rally.")]
    public bool enableKeyboardReset = true;

    [Tooltip("Disable this for blue-only heuristic demo recording.")]
    public bool spawnRedMallet = true;

    [Header("Obstacle Generation")]
    [Tooltip("Turn this off for training runs that should use the plain table without generated obstacles.")]
    public bool enableObstacleGeneration = true;

    [Tooltip("Manual keeps the WorldGen seed/layout stable. RandomSafeSeedEveryReset regenerates safe obstacles before each rally reset.")]
    public ObstacleGenerationMode obstacleGenerationMode = ObstacleGenerationMode.Manual;

    [Tooltip("Optional obstacle generator. Auto-discovered from children when empty.")]
    public WorldGen obstacleGenerator;

    [Tooltip("Minimum planar distance between a mallet spawn point and generated obstacles.")]
    [Min(0.1f)]
    public float obstacleSpawnClearRadius = 1.2f;

    public GameObject puck;

    [Tooltip("Optional spawn area for the blue mallet. Auto-discovered from a child named BlueField when empty.")]
    public Collider blueSpawnField;

    [Tooltip("Optional spawn area for the red mallet. Auto-discovered from a child named RedField when empty.")]
    public Collider redSpawnField;

    [Tooltip("Physics material applied to wall colliders so the puck rebounds from rails.")]
    public PhysicMaterial wallBounceMaterial;

    [HideInInspector]
    public Rigidbody puckRb;

    public List<PlayerInfo> AgentsList = new List<PlayerInfo>();

    AirHockeySettings m_Settings;
    SimpleMultiAgentGroup m_BlueAgentGroup;
    SimpleMultiAgentGroup m_RedAgentGroup;
    Vector3 m_PuckStartingPos;
    Quaternion m_PuckStartingRot;
    Vector3 m_TableCenter;
    float m_BlueGoalZ;
    float m_RedGoalZ;
    int m_ResetTimer;
    bool m_AppliedSpawnRedMallet;

    public Vector3 TableCenter => m_TableCenter;

    void Start()
    {
        m_Settings = FindFirstObjectByType<AirHockeySettings>();
        m_BlueAgentGroup = new SimpleMultiAgentGroup();
        m_RedAgentGroup = new SimpleMultiAgentGroup();

        FindPuck();
        FindSpawnFields();
        FindAgents();
        FindGoals();
        FindObstacleGenerator();
        ConfigureWallBounce();
        ConfigurePuck();
        ConfigureAgents();
        m_AppliedSpawnRedMallet = spawnRedMallet;
        ResetScene();
    }

    void Update()
    {
        if (m_AppliedSpawnRedMallet != spawnRedMallet)
        {
            ApplyRuntimeMalletSpawnToggle();
        }

        if (enableKeyboardReset && Input.GetKeyDown(KeyCode.R))
        {
            InterruptTrainingEpisodes();
            ResetScene();
        }
    }

    void ApplyRuntimeMalletSpawnToggle()
    {
        InterruptTrainingEpisodes();
        ConfigureAgents();
        m_AppliedSpawnRedMallet = spawnRedMallet;
        ResetScene();
    }

    void FixedUpdate()
    {
        m_ResetTimer += 1;
        LimitPuckSpeed();

        if (m_ResetTimer >= MaxEnvironmentSteps && MaxEnvironmentSteps > 0)
        {
            ApplyTimeoutPenalty();
            InterruptTrainingEpisodes();
            ResetScene();
        }
    }

    public void GoalTouched(AirHockeyTeam scoredTeam)
    {
        var scoreReward = MaxEnvironmentSteps > 0
            ? Mathf.Clamp01(1f - (float)m_ResetTimer / MaxEnvironmentSteps)
            : 1f;

        if (scoredTeam == AirHockeyTeam.Blue)
        {
            AddTeamReward(AirHockeyTeam.Blue, scoreReward);
            AddTeamReward(AirHockeyTeam.Red, -1f);
        }
        else
        {
            AddTeamReward(AirHockeyTeam.Red, scoreReward);
            AddTeamReward(AirHockeyTeam.Blue, -1f);
        }

        Academy.Instance.StatsRecorder.Add("AirHockey/Goals", 1f);
        Academy.Instance.StatsRecorder.Add("AirHockey/BlueScored", scoredTeam == AirHockeyTeam.Blue ? 1f : 0f);
        Academy.Instance.StatsRecorder.Add("AirHockey/RedScored", scoredTeam == AirHockeyTeam.Red ? 1f : 0f);

        if (endAgentEpisodesOnGoal)
        {
            LazyInitializeActiveAgents();
            m_BlueAgentGroup.EndGroupEpisode();
            m_RedAgentGroup.EndGroupEpisode();
        }

        ResetScene();
    }

    public void ResetScene()
    {
        m_ResetTimer = 0;
        RegenerateObstaclesForReset();

        foreach (var item in AgentsList)
        {
            if (item.Agent == null || item.Rb == null)
            {
                continue;
            }

            if (!item.Agent.gameObject.activeInHierarchy)
            {
                continue;
            }

            item.Rb.position = GetRandomAgentSpawnPosition(item);
            item.Agent.transform.rotation = item.StartingRot;
            item.Agent.ResetRallyRewardState();
            item.Rb.velocity = Vector3.zero;
            item.Rb.angularVelocity = Vector3.zero;
        }

        ResetPuck();
    }

    public void ResetPuck()
    {
        if (puck == null || puckRb == null)
        {
            return;
        }

        var jitter = new Vector3(
            Random.Range(-PuckResetJitterX, PuckResetJitterX),
            0f,
            Random.Range(-PuckResetJitterZ, PuckResetJitterZ));
        puckRb.position = m_PuckStartingPos + jitter;
        puck.transform.rotation = m_PuckStartingRot;
        puckRb.velocity = Vector3.zero;
        puckRb.angularVelocity = Vector3.zero;
    }

    public void ClampAgentToSide(AirHockeyAgent agent)
    {
        var player = AgentsList.Find(item => item.Agent == agent);
        if (player == null || player.Rb == null)
        {
            return;
        }

        var clamped = ClampPositionToSide(agent, player.Rb.position);
        player.Rb.position = clamped;

        var velocity = player.Rb.velocity;
        if (ClampMalletXToSettingsBounds
            && ((Mathf.Approximately(clamped.x, m_TableCenter.x - SideHalfWidth) && velocity.x < 0f)
                || (Mathf.Approximately(clamped.x, m_TableCenter.x + SideHalfWidth) && velocity.x > 0f)))
        {
            velocity.x = 0f;
        }

        var zRange = GetTeamZRange(agent.team);
        if ((Mathf.Approximately(clamped.z, zRange.x) && velocity.z < 0f)
            || (Mathf.Approximately(clamped.z, zRange.y) && velocity.z > 0f))
        {
            velocity.z = 0f;
        }

        player.Rb.velocity = velocity;
    }

    Vector3 ClampPositionToSide(AirHockeyAgent agent, Vector3 position)
    {
        var zRange = GetTeamZRange(agent.team);
        if (ClampMalletXToSettingsBounds)
        {
            position.x = Mathf.Clamp(position.x, m_TableCenter.x - SideHalfWidth, m_TableCenter.x + SideHalfWidth);
        }

        position.z = Mathf.Clamp(position.z, zRange.x, zRange.y);
        return position;
    }

    Vector2 GetTeamZRange(AirHockeyTeam team)
    {
        var ownGoalZ = team == AirHockeyTeam.Blue ? m_BlueGoalZ : m_RedGoalZ;
        var centerEdge = m_TableCenter.z + Mathf.Sign(ownGoalZ - m_TableCenter.z) * CenterLinePadding;
        var goalEdge = Mathf.Lerp(m_TableCenter.z, ownGoalZ, GoalDepthPercent);
        return new Vector2(Mathf.Min(centerEdge, goalEdge), Mathf.Max(centerEdge, goalEdge));
    }

    Vector3 GetRandomAgentSpawnPosition(PlayerInfo item)
    {
        var spawnField = item.Agent.team == AirHockeyTeam.Blue ? blueSpawnField : redSpawnField;
        if (spawnField != null)
        {
            if (enableObstacleGeneration
                && obstacleGenerator != null
                && obstacleGenerator.TryGetSafePointInSpawnField(
                    spawnField,
                    item.StartingPos.y,
                    obstacleSpawnClearRadius,
                    out var safeSpawnPoint))
            {
                return ClampPositionToSide(item.Agent, safeSpawnPoint);
            }

            Debug.LogWarning(
                $"{nameof(AirHockeyEnvController)} could not find an obstacle-clear spawn point for {item.Agent.name}; using the regular spawn fallback.",
                this);
            return ClampPositionToSide(item.Agent, RandomPointInSpawnField(spawnField, item.StartingPos.y));
        }

        var jitter = new Vector3(
            Random.Range(-ResetPositionJitterX, ResetPositionJitterX),
            0f,
            Random.Range(-ResetPositionJitterZ, ResetPositionJitterZ));
        return ClampPositionToSide(item.Agent, item.StartingPos + jitter);
    }

    Vector3 RandomPointInSpawnField(Collider spawnField, float y)
    {
        if (spawnField is BoxCollider boxCollider)
        {
            var halfSize = boxCollider.size * 0.5f;
            var localPoint = boxCollider.center + new Vector3(
                Random.Range(-halfSize.x, halfSize.x),
                0f,
                Random.Range(-halfSize.z, halfSize.z));
            var worldPoint = boxCollider.transform.TransformPoint(localPoint);
            worldPoint.y = y;
            return worldPoint;
        }

        var bounds = spawnField.bounds;
        return new Vector3(
            Random.Range(bounds.min.x, bounds.max.x),
            y,
            Random.Range(bounds.min.z, bounds.max.z));
    }

    void FindPuck()
    {
        if (puck == null)
        {
            foreach (var childTransform in GetComponentsInChildren<Transform>())
            {
                if (childTransform.CompareTag("Puck"))
                {
                    puck = childTransform.gameObject;
                    break;
                }
            }
        }

        if (puck == null)
        {
            Debug.LogError($"{nameof(AirHockeyEnvController)} could not find a child tagged Puck.", this);
            return;
        }

        puckRb = puck.GetComponent<Rigidbody>();
        m_PuckStartingPos = puck.transform.position;
        m_PuckStartingRot = puck.transform.rotation;
        m_TableCenter = m_PuckStartingPos;
    }

    void ConfigureWallBounce()
    {
        if (wallBounceMaterial == null)
        {
            return;
        }

        foreach (var wallCollider in GetComponentsInChildren<Collider>(true))
        {
            if (wallCollider.CompareTag("Wall") || wallCollider.CompareTag("CenterWall"))
            {
                wallCollider.sharedMaterial = wallBounceMaterial;
            }
        }
    }

    void FindSpawnFields()
    {
        foreach (var spawnField in GetComponentsInChildren<Collider>(true))
        {
            if (blueSpawnField == null && spawnField.name == "BlueField")
            {
                blueSpawnField = spawnField;
            }
            else if (redSpawnField == null && spawnField.name == "RedField")
            {
                redSpawnField = spawnField;
            }
        }
    }

    void FindObstacleGenerator()
    {
        if (obstacleGenerator == null)
        {
            obstacleGenerator = GetComponentInChildren<WorldGen>(true);
        }
    }

    void RegenerateObstaclesForReset()
    {
        if (obstacleGenerator == null)
        {
            FindObstacleGenerator();
        }

        if (obstacleGenerator == null)
        {
            return;
        }

        if (!enableObstacleGeneration)
        {
            obstacleGenerator.ClearGeneratedWorld();
            return;
        }

        if (obstacleGenerationMode == ObstacleGenerationMode.RandomSafeSeedEveryReset)
        {
            obstacleGenerator.GenerateWithRandomSafeSeed();
        }
        else
        {
            obstacleGenerator.EnsureGenerated();
        }
    }

    void FindAgents()
    {
        if (AgentsList.Count > 0)
        {
            return;
        }

        foreach (var agent in GetComponentsInChildren<AirHockeyAgent>(true))
        {
            AgentsList.Add(new PlayerInfo { Agent = agent });
        }
    }

    void FindGoals()
    {
        m_BlueGoalZ = m_TableCenter.z - DefaultGoalDistance;
        m_RedGoalZ = m_TableCenter.z + DefaultGoalDistance;

        foreach (var childTransform in GetComponentsInChildren<Transform>())
        {
            if (childTransform.CompareTag("BlueGoal"))
            {
                m_BlueGoalZ = childTransform.position.z;
            }
            else if (childTransform.CompareTag("RedGoal"))
            {
                m_RedGoalZ = childTransform.position.z;
            }
        }
    }

    void ConfigurePuck()
    {
        if (puckRb == null)
        {
            return;
        }

        puckRb.useGravity = false;
        puckRb.drag = 0f;
        puckRb.angularDrag = 0f;
        puckRb.constraints = RigidbodyConstraints.FreezePositionY | RigidbodyConstraints.FreezeRotationX | RigidbodyConstraints.FreezeRotationZ;
        puckRb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;

        var puckController = puck.GetComponent<PuckGoalController>();
        if (puckController != null)
        {
            puckController.envController = this;
        }
    }

    void ConfigureAgents()
    {
        foreach (var item in AgentsList)
        {
            if (item.Agent == null)
            {
                continue;
            }

            item.Agent.RefreshTeamFromBehaviorParameters();
            var shouldSpawnAgent = spawnRedMallet || item.Agent.team != AirHockeyTeam.Red;
            item.Agent.gameObject.SetActive(shouldSpawnAgent);
            if (!shouldSpawnAgent)
            {
                continue;
            }

            item.Agent.LazyInitialize();
            item.StartingPos = item.Agent.transform.position;
            item.StartingRot = item.Agent.transform.rotation;
            item.Rb = item.Agent.GetComponent<Rigidbody>();

            if (item.Agent.team == AirHockeyTeam.Blue)
            {
                m_BlueAgentGroup.RegisterAgent(item.Agent);
            }
            else
            {
                m_RedAgentGroup.RegisterAgent(item.Agent);
            }
        }

        for (var i = 0; i < AgentsList.Count; i++)
        {
            var agent = AgentsList[i].Agent;
            if (agent == null)
            {
                continue;
            }

            if (!agent.gameObject.activeInHierarchy)
            {
                continue;
            }

            Transform opponent = null;
            foreach (var candidate in AgentsList)
            {
                if (candidate.Agent != null && candidate.Agent != agent && candidate.Agent.gameObject.activeInHierarchy)
                {
                    opponent = candidate.Agent.transform;
                    break;
                }
            }

            agent.Configure(this, puck != null ? puck.transform : null, opponent);
        }

        var expectedAgentCount = spawnRedMallet ? 2 : 1;
        var activeAgentCount = 0;
        foreach (var item in AgentsList)
        {
            if (item.Agent != null && item.Agent.gameObject.activeInHierarchy)
            {
                activeAgentCount += 1;
            }
        }

        if (activeAgentCount != expectedAgentCount)
        {
            Debug.LogWarning($"{nameof(AirHockeyEnvController)} expected {expectedAgentCount} active agent(s) but found {activeAgentCount}.", this);
        }
    }

    void LimitPuckSpeed()
    {
        if (puckRb == null)
        {
            return;
        }

        var velocity = puckRb.velocity;
        velocity.y = 0f;
        if (velocity.magnitude > MaxPuckSpeed)
        {
            velocity = velocity.normalized * MaxPuckSpeed;
        }

        puckRb.velocity = velocity;
        puckRb.angularVelocity = Vector3.zero;
    }

    void ApplyTimeoutPenalty()
    {
        if (!endAgentEpisodesOnGoal || Mathf.Approximately(timeoutPenalty, 0f))
        {
            return;
        }

        AddActiveAgentsReward(timeoutPenalty);
        Academy.Instance.StatsRecorder.Add("AirHockey/Timeouts", 1f);
    }

    void AddTeamReward(AirHockeyTeam team, float reward)
    {
        foreach (var item in AgentsList)
        {
            if (item.Agent != null
                && item.Agent.gameObject.activeInHierarchy
                && item.Agent.team == team)
            {
                item.Agent.AddReward(reward);
            }
        }
    }

    void AddActiveAgentsReward(float reward)
    {
        foreach (var item in AgentsList)
        {
            if (item.Agent != null && item.Agent.gameObject.activeInHierarchy)
            {
                item.Agent.AddReward(reward);
            }
        }
    }

    void InterruptTrainingEpisodes()
    {
        if (!endAgentEpisodesOnGoal)
        {
            return;
        }

        LazyInitializeActiveAgents();
        m_BlueAgentGroup.GroupEpisodeInterrupted();
        m_RedAgentGroup.GroupEpisodeInterrupted();
    }

    void LazyInitializeActiveAgents()
    {
        foreach (var item in AgentsList)
        {
            if (item.Agent != null && item.Agent.gameObject.activeInHierarchy)
            {
                item.Agent.LazyInitialize();
            }
        }
    }

    AirHockeySettings Settings
    {
        get
        {
            if (m_Settings == null)
            {
                m_Settings = FindFirstObjectByType<AirHockeySettings>();
            }

            return m_Settings;
        }
    }

    float SideHalfWidth => Settings != null ? Settings.sideHalfWidth : k_DefaultSideHalfWidth;

    bool ClampMalletXToSettingsBounds => Settings != null && Settings.clampMalletXToSettingsBounds;

    float CenterLinePadding => Settings != null ? Settings.centerLinePadding : k_DefaultCenterLinePadding;

    float GoalDepthPercent => Settings != null ? Settings.goalDepthPercent : k_DefaultGoalDepthPercent;

    float DefaultGoalDistance => Settings != null ? Settings.defaultGoalDistance : k_DefaultGoalDistance;

    float ResetPositionJitterX => Settings != null ? Settings.resetPositionJitterX : k_DefaultResetPositionJitterX;

    float ResetPositionJitterZ => Settings != null ? Settings.resetPositionJitterZ : k_DefaultResetPositionJitterZ;

    float PuckResetJitterX => Settings != null ? Settings.puckResetJitterX : k_DefaultPuckResetJitterX;

    float PuckResetJitterZ => Settings != null ? Settings.puckResetJitterZ : k_DefaultPuckResetJitterZ;

    float MaxPuckSpeed => Settings != null ? Settings.maxPuckSpeed : k_DefaultMaxPuckSpeed;
}
