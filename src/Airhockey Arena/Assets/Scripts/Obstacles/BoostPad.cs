using UnityEngine;

// This script works for both the BoostPad and the SlowZone
public class BoostPad : MonoBehaviour
{
    [SerializeField, Min(0.1f)] float velocityMultiplier = 1.35f;
    [SerializeField, Min(0.1f)] float maxPuckSpeed = 35f;

    void OnTriggerEnter(Collider other)
    {
        var puckRb = other.attachedRigidbody;
        if (puckRb == null || !puckRb.CompareTag("Puck"))
        {
            return;
        }

        BoostPuck(puckRb);
    }

    void BoostPuck(Rigidbody puckRb)
    {
        var velocity = Planar(puckRb.velocity) * velocityMultiplier;
        if (velocity.magnitude > maxPuckSpeed)
        {
            velocity = velocity.normalized * maxPuckSpeed;
        }

        puckRb.velocity = velocity;
        puckRb.angularVelocity = Vector3.zero;
    }

    static Vector3 Planar(Vector3 vector)
    {
        vector.y = 0f;
        return vector;
    }
}
