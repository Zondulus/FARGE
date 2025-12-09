using FerramAerospaceResearch;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

[KSPAddon(KSPAddon.Startup.Flight, once: false)]
public class GroundEffectFAR : MonoBehaviour
{
    // --- Configurable via GroundEffectFAR.cfg ---
    private static float maxDragReduction = 0.5f;
    private static float wingspanScale = 1.0f;
    private static float defaultWingspan = 10.0f;
    private static bool debugMode = true;
    // ---------------------------------------------

    private bool isGroundEffectActive = false;
    private float currentReductionFraction = 0f;
    private Vessel lastActiveVessel = null;

    // Cached values
    private float currentWingspan = 10.0f;
    private float effectStartAlt = 10.0f;
    private float effectMaxAlt = 5.0f;

    // Timers
    private float dimensionCheckTimer = 0f;
    private const float DIMENSION_CHECK_INTERVAL = 2.0f;

    private float debugLogTimer = 0f;
    private const float DEBUG_LOG_INTERVAL = 1.0f;

    public void Awake()
    {
        LoadConfiguration();

        if (!IsFARInstalled())
        {
            Debug.LogError("[GroundEffectFAR] FAR not found. Mod disabled.");
            Destroy(this);
            return;
        }

        GameEvents.onVesselWasModified.Add(OnVesselModified);
        GameEvents.onStageActivate.Add(OnStageActivate);

        ScreenMessages.PostScreenMessage("[GroundEffectFAR] Loaded.", 5.0f, ScreenMessageStyle.UPPER_CENTER);
        if (debugMode) Debug.Log("[GroundEffectFAR] Mod initialized.");
    }

    public void OnDestroy()
    {
        GameEvents.onVesselWasModified.Remove(OnVesselModified);
        GameEvents.onStageActivate.Remove(OnStageActivate);

        if (lastActiveVessel != null)
        {
            RemoveGroundEffect(lastActiveVessel);
        }
    }

    public void FixedUpdate()
    {
        if (!FlightGlobals.ready || FlightGlobals.ActiveVessel == null) return;

        Vessel activeVessel = FlightGlobals.ActiveVessel;

        // 1. Vessel Switching Logic
        if (activeVessel != lastActiveVessel)
        {
            if (lastActiveVessel != null) RemoveGroundEffect(lastActiveVessel);
            lastActiveVessel = activeVessel;
            isGroundEffectActive = false;
            RecalculateVesselWingspan(activeVessel);
        }

        // 2. Periodic Wingspan Check
        dimensionCheckTimer += Time.fixedDeltaTime;
        if (dimensionCheckTimer >= DIMENSION_CHECK_INTERVAL)
        {
            RecalculateVesselWingspan(activeVessel);
            dimensionCheckTimer = 0f;
        }

        // 3. Ground Effect Logic
        float agl = (float)activeVessel.radarAltitude;
        float asl = (float)activeVessel.altitude;

        bool conditionsMet = activeVessel.mainBody.atmosphere &&
                             activeVessel.srfSpeed > 2.5 &&
                             agl >= 0 &&
                             agl <= effectStartAlt &&
                             asl > -5.0f;

        bool shouldBeActive = false;
        if (conditionsMet)
        {
            currentReductionFraction = CalculateGroundEffectStrength(agl);
            shouldBeActive = currentReductionFraction > 0.001f;
        }
        else
        {
            currentReductionFraction = 0f;
        }

        UpdateEffectState(activeVessel, shouldBeActive);

        // 4. Debug Logging (Throttled)
        if (debugMode && isGroundEffectActive)
        {
            debugLogTimer += Time.fixedDeltaTime;
            if (debugLogTimer >= DEBUG_LOG_INTERVAL)
            {
                Debug.Log($"[GroundEffectFAR] Active. AGL: {agl:F1}m / Span: {currentWingspan:F1}m / DragRed: {currentReductionFraction:P1}");
                debugLogTimer = 0f;
            }
        }
    }

    // --- Events ---
    private void OnVesselModified(Vessel v) { if (v == FlightGlobals.ActiveVessel) RecalculateVesselWingspan(v); }
    private void OnStageActivate(int stage) { if (FlightGlobals.ActiveVessel != null) RecalculateVesselWingspan(FlightGlobals.ActiveVessel); }

    // --- Core Logic ---

    private void RecalculateVesselWingspan(Vessel v)
    {
        if (v == null) return;

        float minX = float.MaxValue;
        float maxX = float.MinValue;
        bool foundAny = false;

        Transform refTransform = v.transform;

        foreach (Part p in v.parts)
        {
            if (p == null) continue;

            // Reverted to FindModelComponents to avoid grabbing FX/Particle meshes with infinite bounds
            List<MeshRenderer> renderers = p.FindModelComponents<MeshRenderer>();

            // Loop through the list (List<T> is indexable, using for-loop to avoid enumerator garbage)
            for (int i = 0; i < renderers.Count; i++)
            {
                MeshRenderer mr = renderers[i];
                if (!mr.enabled) continue;

                Bounds b = mr.bounds;
                Vector3 min = b.min;
                Vector3 max = b.max;

                // Optimization: Check bounds corners directly without allocating new Vector3[] arrays
                CheckPoint(ref minX, ref maxX, refTransform, min.x, min.y, min.z);
                CheckPoint(ref minX, ref maxX, refTransform, min.x, min.y, max.z);
                CheckPoint(ref minX, ref maxX, refTransform, min.x, max.y, min.z);
                CheckPoint(ref minX, ref maxX, refTransform, min.x, max.y, max.z);
                CheckPoint(ref minX, ref maxX, refTransform, max.x, min.y, min.z);
                CheckPoint(ref minX, ref maxX, refTransform, max.x, min.y, max.z);
                CheckPoint(ref minX, ref maxX, refTransform, max.x, max.y, min.z);
                CheckPoint(ref minX, ref maxX, refTransform, max.x, max.y, max.z);

                foundAny = true;
            }
        }

        if (foundAny)
        {
            currentWingspan = Mathf.Abs(maxX - minX);
            // Sanity clamp: If wingspan is > 2km, something is wrong with a mesh, default to 10m
            if (currentWingspan > 2000f)
            {
                if (debugMode) Debug.LogWarning($"[GroundEffectFAR] Calculated wingspan {currentWingspan}m is unrealistically large. Reverting to default.");
                currentWingspan = defaultWingspan;
            }
        }
        else
        {
            currentWingspan = defaultWingspan;
        }

        float effectiveSpan = currentWingspan * wingspanScale;
        effectStartAlt = effectiveSpan;
        effectMaxAlt = effectiveSpan * 0.5f;
    }

    private void CheckPoint(ref float minX, ref float maxX, Transform refTransform, float x, float y, float z)
    {
        // Converts World Point to Local Point relative to the vessel
        Vector3 localPt = refTransform.InverseTransformPoint(x, y, z);
        if (localPt.x < minX) minX = localPt.x;
        if (localPt.x > maxX) maxX = localPt.x;
    }

    private void UpdateEffectState(Vessel v, bool shouldBeActive)
    {
        if (isGroundEffectActive == shouldBeActive) return;

        isGroundEffectActive = shouldBeActive;

        if (isGroundEffectActive)
        {
            if (debugMode) Debug.Log($"[GroundEffectFAR] Entering Ground Effect. (AGL: {v.radarAltitude:F1}m)");
            ApplyGroundEffect(v);
        }
        else
        {
            if (debugMode && currentReductionFraction > 0) Debug.Log("[GroundEffectFAR] Exiting Ground Effect.");
            RemoveGroundEffect(v);
        }
    }

    private void ApplyGroundEffect(Vessel v)
    {
        for (int i = 0; i < v.parts.Count; i++)
        {
            FARAPI.SetPartAeroForceModifier(v.parts[i], GroundEffectModifier);
        }
    }

    private void RemoveGroundEffect(Vessel v)
    {
        if (v == null) return;
        for (int i = 0; i < v.parts.Count; i++)
        {
            FARAPI.SetPartAeroForceModifier(v.parts[i], null);
        }
    }

    private Vector3 GroundEffectModifier(Part part, Vector3 aeroForce)
    {
        if (currentReductionFraction <= 0f) return aeroForce;

        Vector3 velocity = part.vessel.srf_velocity;
        if (velocity.sqrMagnitude < 1.0f) return aeroForce;

        Vector3 velocityDirection = velocity.normalized;
        Vector3 dragComponent = Vector3.Project(aeroForce, -velocityDirection);
        Vector3 liftSideComponent = aeroForce - dragComponent;

        dragComponent *= (1.0f - currentReductionFraction);

        return liftSideComponent + dragComponent;
    }

    private float CalculateGroundEffectStrength(float agl)
    {
        if (agl <= effectMaxAlt) return maxDragReduction;

        float range = effectStartAlt - effectMaxAlt;
        if (range <= 0.001f) return maxDragReduction;

        float t = Mathf.Clamp01((effectStartAlt - agl) / range);
        return maxDragReduction * t;
    }

    // --- Helpers ---

    private bool IsFARInstalled()
    {
        return AssemblyLoader.loadedAssemblies.Any(a => a.name == "FerramAerospaceResearch");
    }

    private void LoadConfiguration()
    {
        ConfigNode[] nodes = GameDatabase.Instance.GetConfigNodes("GROUNDEFFECTFAR");
        if (nodes != null && nodes.Length > 0)
        {
            ConfigNode node = nodes[0];
            if (node != null)
            {
                if (node.HasValue("maxDragReduction")) float.TryParse(node.GetValue("maxDragReduction"), out maxDragReduction);
                if (node.HasValue("wingspanScale")) float.TryParse(node.GetValue("wingspanScale"), out wingspanScale);
                if (node.HasValue("defaultWingspan")) float.TryParse(node.GetValue("defaultWingspan"), out defaultWingspan);
                if (node.HasValue("debugMode")) bool.TryParse(node.GetValue("debugMode"), out debugMode);

                if (debugMode) Debug.Log("[GroundEffectFAR] Configuration loaded successfully.");
            }
        }
        else
        {
            Debug.LogWarning("[GroundEffectFAR] No settings config found. Using defaults.");
        }
    }
}