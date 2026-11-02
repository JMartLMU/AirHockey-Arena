using UnityEngine;

public class PuckGoalController : MonoBehaviour
{
    public AirHockeyEnvController envController;
    public string redGoalTag = "RedGoal";
    public string blueGoalTag = "BlueGoal";

    void Start()
    {
        if (envController == null)
        {
            envController = GetComponentInParent<AirHockeyEnvController>();
        }
    }

    void OnTriggerEnter(Collider other)
    {
        if (envController == null)
        {
            return;
        }

        if (other.CompareTag(redGoalTag))
        {
            envController.GoalTouched(AirHockeyTeam.Blue);
        }
        else if (other.CompareTag(blueGoalTag))
        {
            envController.GoalTouched(AirHockeyTeam.Red);
        }
    }
}
