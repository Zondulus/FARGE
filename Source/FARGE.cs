using System;
using System.IO;
using System.Linq;
using UnityEngine;
using FerramAerospaceResearch;

[KSPAddon(KSPAddon.Startup.Flight, once: false)]
public class GroundEffectFAR : MonoBehaviour
{
    // --- Configurable via GroundEffectFAR.cfg ---
    private static float maxDragReduction = 0.5f;
    private static float startAltitude = 20.0f;
    private static float endAltitude = 2.0f;
    private static bool debugMode = false;
    // ---------------------------------------------

    private bool isGroundEffectActive = false;
    private float currentReductionFraction = 0f;
    private Vessel lastActiveVessel = null;

    public void Awake()
    {
        LoadConfiguration();

        // Disable if FAR is not installed.
        bool farInstalled = AssemblyLoader.loadedAssemblies.Any(a => a.name == "FerramAerospaceResearch");
        if (!farInstalled)
        {
            Debug.Log("[GroundEffectFAR] Ferram Aerospace Research not detected. Mod will be disabled.");
            Destroy(this);
            return;
        }

        Debug.Log("[GroundEffectFAR] Mod loaded successfully.");
    }

    public void OnDestroy()
    {
        // Ensure the effect is removed if the scene is destroyed or the mod is disabled.
        if (lastActiveVessel != null)
        {
            RemoveGroundEffect(lastActiveVessel);
        }
    }

    public void FixedUpdate()
    {
        // --- Guard Clauses: Cheap checks to exit early ---
        if (!FlightGlobals.ready) return;

        Vessel activeVessel = FlightGlobals.ActiveVessel;

        if (activeVessel == null)
        {
            if (lastActiveVessel != null)
            {
                RemoveGroundEffect(lastActiveVessel);
                isGroundEffectActive = false;
                lastActiveVessel = null;
            }
            return;
        }

        // --- Vessel Change Detection ---
        if (activeVessel != lastActiveVessel)
        {
            // Clean up the effect on the old vessel before switching to the new one.
            if (lastActiveVessel != null)
            {
                RemoveGroundEffect(lastActiveVessel);
            }
            isGroundEffectActive = false; // Reset state for the new vessel.
            lastActiveVessel = activeVessel;
        }

        // --- Determine if the effect *should* be active based on all conditions ---
        float agl = (float)activeVessel.radarAltitude;

        // All of these conditions must be true for the ground effect to be a candidate for activation.
        // This check correctly represents the "mod is active" state you wanted for logging.
        bool conditionsMet = activeVessel.mainBody.atmosphere &&
                             activeVessel.srfSpeed >= 2.0 &&
                             agl >= 0 && agl <= startAltitude;

        bool shouldBeActive = false;

        if (conditionsMet)
        {
            // If conditions are met, calculate the strength.
            currentReductionFraction = CalculateGroundEffectStrength(agl);
            // The effect is only truly active if the calculated reduction is greater than zero.
            shouldBeActive = currentReductionFraction > 0f;
        }

        if (!shouldBeActive)
        {
            // Ensure the reduction fraction is zero if the effect is not active.
            currentReductionFraction = 0f;
        }

        // --- Update State ---
        // This single call now manages applying/removing the effect based on the final 'shouldBeActive' state.
        UpdateEffectState(activeVessel, shouldBeActive);
    }

    /// <summary>
    /// Manages the state of the effect, only applying or removing it when the state changes.
    /// This is the gatekeeper for logging and the expensive part-looping logic.
    /// </summary>
    private void UpdateEffectState(Vessel v, bool shouldBeActive)
    {
        if (isGroundEffectActive == shouldBeActive) return;

        isGroundEffectActive = shouldBeActive;

        if (isGroundEffectActive)
        {
            if (debugMode) Debug.Log("[GroundEffectFAR] Vessel has entered ground effect.");
            ApplyGroundEffect(v);
        }
        else
        {
            if (debugMode) Debug.Log("[GroundEffectFAR] Vessel has exited ground effect.");
            RemoveGroundEffect(v);
        }
    }

    private void ApplyGroundEffect(Vessel v)
    {
        if (v == null) return;
        foreach (Part part in v.parts)
        {
            // This delegate will be called by FAR for each part during its physics calculations.
            FARAPI.SetPartAeroForceModifier(part, GroundEffectModifier);
        }
    }

    private void RemoveGroundEffect(Vessel v)
    {
        if (v == null) return;
        foreach (Part part in v.parts)
        {
            // Setting the modifier to null removes it.
            FARAPI.SetPartAeroForceModifier(part, null);
        }
    }

    /// <summary>
    /// The 'hot path' delegate called by FAR. It contains only fast vector math and no allocations
    /// to ensure maximum performance during physics calculations.
    /// </summary>
    private Vector3 GroundEffectModifier(Part part, Vector3 aeroForce)
    {
        // This modifier is only active when currentReductionFraction > 0.
        // We get the vessel's velocity, not the part's, for a more stable direction vector.
        Vector3 velocity = part.vessel.srf_velocity;
        if (velocity.sqrMagnitude < 0.01f)
        {
            return aeroForce;
        }

        // Deconstruct the force into a component opposing velocity (drag) and everything else.
        Vector3 velocityDirection = velocity.normalized;
        Vector3 dragComponent = Vector3.Project(aeroForce, -velocityDirection);
        Vector3 otherComponents = aeroForce - dragComponent;

        // Reduce only the drag component.
        dragComponent *= (1.0f - currentReductionFraction);

        // Reconstruct the force and return it.
        return otherComponents + dragComponent;
    }

    /// <summary>
    /// Calculates the strength of the ground effect based on altitude.
    /// Returns a value from 0 to maxDragReduction.
    /// </summary>
    private float CalculateGroundEffectStrength(float agl)
    {
        // Linearly interpolate between the start and end altitudes.
        // 't' will be 1.0 at or below endAltitude, and 0.0 at or above startAltitude.
        float t = Mathf.Clamp01((startAltitude - agl) / (startAltitude - endAltitude));
        return maxDragReduction * t;
    }

    private void LoadConfiguration()
    {
        try
        {
            string configFilePath = Path.Combine(KSPUtil.ApplicationRootPath, "GameData", "GroundEffectFAR", "GroundEffectFAR.cfg");

            if (!File.Exists(configFilePath))
            {
                Debug.LogWarning("[GroundEffectFAR] Config file not found at " + configFilePath + ". Using default values.");
                return;
            }

            ConfigNode settings = ConfigNode.Load(configFilePath);
            if (settings == null || !settings.HasNode("GroundEffectFAR"))
            {
                Debug.LogWarning("[GroundEffectFAR] Config file is invalid or missing 'GroundEffectFAR' node. Using default values.");
                return;
            }

            ConfigNode node = settings.GetNode("GroundEffectFAR");
            node.TryGetValue("maxDragReduction", ref maxDragReduction);
            node.TryGetValue("startAltitude", ref startAltitude);
            node.TryGetValue("endAltitude", ref endAltitude);
            node.TryGetValue("debugMode", ref debugMode);

            if (debugMode)
            {
                Debug.Log($"[GroundEffectFAR] Config loaded: maxDragReduction={maxDragReduction}, startAltitude={startAltitude}, endAltitude={endAltitude}, debugMode={debugMode}");
            }
        }
        catch (Exception e)
        {
            Debug.LogError("[GroundEffectFAR] Error loading configuration: " + e.Message);
        }
    }
}
