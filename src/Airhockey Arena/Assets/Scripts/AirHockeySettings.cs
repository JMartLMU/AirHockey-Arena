using UnityEngine;

public class AirHockeySettings : MonoBehaviour
{
    [Header("Visuals")]
    public Material blueMaterial;
    public Material redMaterial;

    [Header("Mallet Movement")]
    public float malletMoveForce = 2.5f;
    public float maxMalletSpeed = 16f;
    [Tooltip("Small Rigidbody drag that makes mallet movement settle without slowing the puck.")]
    public float malletTableDrag = 1.2f;
    public float puckStrikeImpulse = 45f;
    public float puckVelocityTransfer = 1.25f;

    [Header("Rewards")]
    [Tooltip("Base reward for the first few puck hits. Later hits in the same rally are reduced to discourage farming.")]
    public float puckHitReward = 0.02f;
    [Tooltip("Largest extra reward available for a strong puck launch.")]
    public float maxPuckHitVelocityBonus = 0.08f;
    [Tooltip("Expected puck speed that earns the full puck-hit velocity bonus.")]
    public float puckHitVelocityForMaxBonus = 20f;

    [Header("Table Bounds")]
    public float sideHalfWidth = 5.5f;
    [Tooltip("Enable this only if the table has no physical side walls. When disabled, wall colliders define the left/right mallet limits.")]
    public bool clampMalletXToSettingsBounds;
    public float centerLinePadding = 0.7f;
    [Range(0.1f, 1f)]
    public float goalDepthPercent = 0.92f;
    public float defaultGoalDistance = 14.8f;

    [Header("Episode Reset")]
    public float resetPositionJitterX = 1.5f;
    public float resetPositionJitterZ = 0.5f;
    public float puckResetJitterX = 1.5f;
    public float puckResetJitterZ = 0.5f;

    [Header("Observation Normalization")]
    public float observationScaleX = 8f;
    public float observationScaleZ = 20f;

    [Header("Puck")]
    public float maxPuckSpeed = 35f;
}
