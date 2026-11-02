using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Policies;
using Unity.MLAgents.Sensors;
using UnityEngine;

public enum AirHockeyTeam
{
    Blue = 0,
    Red = 1
}

public class AirHockeyAgent : Agent
{
    const float k_DefaultMalletMoveForce = 2.5f;
    const float k_DefaultMaxMalletSpeed = 16f;
    const float k_DefaultMalletTableDrag = 1.2f;
    const float k_DefaultPuckStrikeImpulse = 45f;
    const float k_DefaultPuckVelocityTransfer = 1.25f;
    const float k_DefaultPuckHitReward = 0.02f;
    const float k_DefaultMaxPuckHitVelocityBonus = 0.08f;
    const float k_DefaultPuckHitVelocityForMaxBonus = 20f;
    const float k_DefaultMaxPuckSpeed = 35f;
    const float k_DefaultObservationScaleX = 8f;
    const float k_DefaultObservationScaleZ = 20f;
    static readonly float[] k_PuckHitRewardMultipliers = { 1f, 0.6f, 0.35f, 0.2f, 0.1f };

    [HideInInspector]
    public AirHockeyTeam team;

    [HideInInspector]
    public Rigidbody agentRb;

    AirHockeySettings m_Settings;
    AirHockeyEnvController m_EnvController;
    BehaviorParameters m_BehaviorParameters;
    Rigidbody m_PuckRb;
    Rigidbody m_OpponentRb;
    Transform m_Puck;
    Transform m_Opponent;
    int m_PuckHitsThisEpisode;

    public override void Initialize()
    {
        agentRb = GetComponent<Rigidbody>();
        m_Settings = FindFirstObjectByType<AirHockeySettings>();
        m_EnvController = GetComponentInParent<AirHockeyEnvController>();
        m_BehaviorParameters = GetComponent<BehaviorParameters>();

        RefreshTeamFromBehaviorParameters();
        ConfigureRigidbody();
    }

    public void Configure(AirHockeyEnvController envController, Transform puck, Transform opponent)
    {
        m_EnvController = envController;
        m_Puck = puck;
        m_Opponent = opponent;
        m_PuckRb = puck != null ? puck.GetComponent<Rigidbody>() : null;
        m_OpponentRb = opponent != null ? opponent.GetComponent<Rigidbody>() : null;
    }

    public override void OnEpisodeBegin()
    {
        ResetRallyRewardState();
    }

    public void ResetRallyRewardState()
    {
        m_PuckHitsThisEpisode = 0;
    }

    public void RefreshTeamFromBehaviorParameters()
    {
        if (m_BehaviorParameters == null)
        {
            m_BehaviorParameters = GetComponent<BehaviorParameters>();
        }

        team = m_BehaviorParameters != null && m_BehaviorParameters.TeamId == (int)AirHockeyTeam.Red
            ? AirHockeyTeam.Red
            : AirHockeyTeam.Blue;
    }

    public override void CollectObservations(VectorSensor sensor)
    {
        if (sensor == null)
        {
            return;
        }

        if (m_EnvController == null)
        {
            m_EnvController = GetComponentInParent<AirHockeyEnvController>();
        }

        AddVectorObservation(sensor, transform.position - CenterPosition);
        AddVectorObservation(sensor, agentRb != null ? agentRb.velocity : Vector3.zero);
        AddVectorObservation(sensor, m_Puck != null ? m_Puck.position - transform.position : Vector3.zero);
        AddVectorObservation(sensor, m_PuckRb != null ? m_PuckRb.velocity : Vector3.zero);
        AddVectorObservation(sensor, m_Opponent != null ? m_Opponent.position - transform.position : Vector3.zero);
        AddVectorObservation(sensor, m_OpponentRb != null ? m_OpponentRb.velocity : Vector3.zero);
    }

    public override void OnActionReceived(ActionBuffers actionBuffers)
    {
        MoveAgent(actionBuffers.DiscreteActions);
    }

    public override void Heuristic(in ActionBuffers actionsOut)
    {
        var discreteActionsOut = actionsOut.DiscreteActions;
        discreteActionsOut[0] = 0;
        discreteActionsOut[1] = 0;

        if (Input.GetKey(KeyCode.W))
        {
            discreteActionsOut[0] = 1;
        }
        else if (Input.GetKey(KeyCode.S))
        {
            discreteActionsOut[0] = 2;
        }

        if (Input.GetKey(KeyCode.D))
        {
            discreteActionsOut[1] = 1;
        }
        else if (Input.GetKey(KeyCode.A))
        {
            discreteActionsOut[1] = 2;
        }
    }

    void FixedUpdate()
    {
        KeepMalletOnTable();
    }

    void MoveAgent(ActionSegment<int> act)
    {
        if (agentRb == null)
        {
            return;
        }

        var move = Vector3.zero;
        var forwardAxis = act[0];
        var rightAxis = act[1];

        if (forwardAxis == 1)
        {
            move += TeamForward;
        }
        else if (forwardAxis == 2)
        {
            move -= TeamForward;
        }

        if (rightAxis == 1)
        {
            move += TeamRight;
        }
        else if (rightAxis == 2)
        {
            move -= TeamRight;
        }

        if (move.sqrMagnitude > 0f)
        {
            agentRb.AddForce(move.normalized * MalletMoveForce, ForceMode.VelocityChange);
        }

        KeepMalletOnTable();
    }

    void OnCollisionEnter(Collision collision)
    {
        if (!collision.gameObject.CompareTag("Puck"))
        {
            return;
        }

        var puckRb = collision.rigidbody != null ? collision.rigidbody : collision.gameObject.GetComponent<Rigidbody>();
        if (puckRb == null || collision.contactCount == 0)
        {
            return;
        }

        var preHitPuckVelocity = puckRb.velocity;
        preHitPuckVelocity.y = 0f;

        var strikeDirection = collision.GetContact(0).point - transform.position;
        strikeDirection.y = 0f;

        if (strikeDirection.sqrMagnitude < 0.0001f)
        {
            strikeDirection = TeamForward;
        }

        var malletVelocity = agentRb != null ? agentRb.velocity : Vector3.zero;
        malletVelocity.y = 0f;
        var strike = strikeDirection.normalized * PuckStrikeImpulse
                     + malletVelocity * PuckVelocityTransfer;

        puckRb.AddForce(strike, ForceMode.Impulse);
        RewardPuckHit(puckRb, preHitPuckVelocity, strike);
    }

    void RewardPuckHit(Rigidbody puckRb, Vector3 preHitPuckVelocity, Vector3 strikeImpulse)
    {
        Academy.Instance.StatsRecorder.Add("AirHockey/PuckHits", 1f);

        var rewardMultiplier = GetPuckHitRewardMultiplier();
        m_PuckHitsThisEpisode += 1;
        if (rewardMultiplier <= 0f)
        {
            return;
        }

        var expectedLaunchVelocity = preHitPuckVelocity;
        if (puckRb != null)
        {
            expectedLaunchVelocity += strikeImpulse / Mathf.Max(puckRb.mass, 0.0001f);
        }

        expectedLaunchVelocity.y = 0f;
        var launchSpeed = Mathf.Min(expectedLaunchVelocity.magnitude, MaxPuckSpeed);
        var velocityBonus = Mathf.Clamp01(launchSpeed / PuckHitVelocityForMaxBonus) * MaxPuckHitVelocityBonus;
        var hitReward = (PuckHitReward + velocityBonus) * rewardMultiplier;

        AddReward(hitReward);
        Academy.Instance.StatsRecorder.Add("AirHockey/PuckHitReward", hitReward);
        Academy.Instance.StatsRecorder.Add("AirHockey/PuckHitSpeed", launchSpeed);
    }

    float GetPuckHitRewardMultiplier()
    {
        if (m_PuckHitsThisEpisode >= k_PuckHitRewardMultipliers.Length)
        {
            return 0f;
        }

        return k_PuckHitRewardMultipliers[m_PuckHitsThisEpisode];
    }

    void ConfigureRigidbody()
    {
        if (agentRb == null)
        {
            return;
        }

        agentRb.useGravity = false;
        agentRb.drag = MalletTableDrag;
        agentRb.constraints = RigidbodyConstraints.FreezePositionY | RigidbodyConstraints.FreezeRotation;
        agentRb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        agentRb.maxAngularVelocity = 0f;
    }

    void KeepMalletOnTable()
    {
        if (agentRb == null)
        {
            return;
        }

        var velocity = agentRb.velocity;
        velocity.y = 0f;
        if (velocity.magnitude > MaxMalletSpeed)
        {
            velocity = velocity.normalized * MaxMalletSpeed;
        }

        agentRb.velocity = velocity;
        agentRb.angularVelocity = Vector3.zero;

        if (m_EnvController != null)
        {
            m_EnvController.ClampAgentToSide(this);
        }
    }

    void AddVectorObservation(VectorSensor sensor, Vector3 worldVector)
    {
        if (sensor == null)
        {
            return;
        }

        var teamVector = ToTeamSpace(worldVector);
        sensor.AddObservation(teamVector.x / ObservationScaleX);
        sensor.AddObservation(teamVector.y / ObservationScaleZ);
    }

    Vector2 ToTeamSpace(Vector3 worldVector)
    {
        var sign = TeamSign;
        return new Vector2(worldVector.x * sign, worldVector.z * sign);
    }

    Vector3 CenterPosition => m_EnvController != null ? m_EnvController.TableCenter : transform.position;

    Vector3 TeamForward => new Vector3(0f, 0f, TeamSign);

    Vector3 TeamRight => new Vector3(TeamSign, 0f, 0f);

    float TeamSign => team == AirHockeyTeam.Blue ? 1f : -1f;

    float MalletMoveForce => Settings != null ? Settings.malletMoveForce : k_DefaultMalletMoveForce;

    float MaxMalletSpeed => Settings != null ? Settings.maxMalletSpeed : k_DefaultMaxMalletSpeed;

    float MalletTableDrag => Settings != null ? Settings.malletTableDrag : k_DefaultMalletTableDrag;

    float PuckStrikeImpulse => Settings != null ? Settings.puckStrikeImpulse : k_DefaultPuckStrikeImpulse;

    float PuckVelocityTransfer => Settings != null ? Settings.puckVelocityTransfer : k_DefaultPuckVelocityTransfer;

    float PuckHitReward => Settings != null ? Settings.puckHitReward : k_DefaultPuckHitReward;

    float MaxPuckHitVelocityBonus => Settings != null ? Settings.maxPuckHitVelocityBonus : k_DefaultMaxPuckHitVelocityBonus;

    float PuckHitVelocityForMaxBonus => Settings != null && Settings.puckHitVelocityForMaxBonus > 0f
        ? Settings.puckHitVelocityForMaxBonus
        : k_DefaultPuckHitVelocityForMaxBonus;

    float MaxPuckSpeed => Settings != null ? Settings.maxPuckSpeed : k_DefaultMaxPuckSpeed;

    float ObservationScaleX => Settings != null && Settings.observationScaleX > 0f
        ? Settings.observationScaleX
        : k_DefaultObservationScaleX;

    float ObservationScaleZ => Settings != null && Settings.observationScaleZ > 0f
        ? Settings.observationScaleZ
        : k_DefaultObservationScaleZ;

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
}
