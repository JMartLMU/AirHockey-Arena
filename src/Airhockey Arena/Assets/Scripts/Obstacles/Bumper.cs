using UnityEngine;

public class Bumper : MonoBehaviour
{
    const float k_MinDirectionSqrMagnitude = 0.0001f;

    [SerializeField, Min(0f)] float impulse = 20f;
    [SerializeField, Min(0.1f)] float maxPuckSpeed = 40f;

    void OnCollisionEnter(Collision collision)
    {
        var puckRb = collision.rigidbody;
        if (puckRb == null || !puckRb.CompareTag("Puck"))
        {
            return;
        }

        var direction = GetBounceDirection(collision, puckRb);
        puckRb.AddForce(direction * impulse, ForceMode.Impulse);
        ClampPlanarVelocity(puckRb);
    }

    Vector3 GetBounceDirection(Collision collision, Rigidbody puckRb)
    {
        var direction = Planar(puckRb.position - transform.position);
        if (direction.sqrMagnitude < k_MinDirectionSqrMagnitude && collision.contactCount > 0)
        {
            direction = Planar(collision.GetContact(0).point - transform.position);
        }

        if (direction.sqrMagnitude < k_MinDirectionSqrMagnitude)
        {
            direction = Planar(transform.forward);
        }

        return direction.normalized;
    }

    void ClampPlanarVelocity(Rigidbody puckRb)
    {
        var velocity = Planar(puckRb.velocity);
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
