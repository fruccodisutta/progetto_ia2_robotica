using UnityEngine;

public static class SimulationUnits
{
    public const float DefaultUnitsToKmFactor = 0.001f;
    private static float unitsToKmFactor = DefaultUnitsToKmFactor;

    public static float UnitsToKmFactor => unitsToKmFactor;

    public static void SetUnitsToKmFactor(float value)
    {
        if (value <= 0f) return;
        if (!Mathf.Approximately(unitsToKmFactor, value))
        {
            unitsToKmFactor = value;
        }
    }
}