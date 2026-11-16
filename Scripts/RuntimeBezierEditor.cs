using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using UIImage = UnityEngine.UI.Image;
using UIButton = UnityEngine.UI.Button;
using UIText = UnityEngine.UI.Text;
using UnityEngine.UI;

// ============================================================================
// 0. ESTRUCTURA DE CONFIGURACIÓN UNIFICADA PARA LA IA (CON CANCELACIÓN BEM)
// ============================================================================

[Serializable]
public class AICurveChargeConfig
{
    public float charge_nC = 20.0f;
    public float curve_slope_dy_dx = 0.5f;
    public float leaf_placement_t = 0.5f;
    public string leaves_material = "Gold";
    public bool auto_start_simulation = true;

    // Rango de paneles BEM a cancelar (1-indexed). Ej: start=1, count=16 deshabilita [1..16]
    public int cancel_panel_start = 0;
    public int cancel_panel_count = 0;
}

// ============================================================================
// 1. ESTRUCTURAS SERIALIZABLES PARA ElectroscopePhysicsCache_v2.json
// ============================================================================

[Serializable]
public class SavedPhysicsRoot
{
    public List<MaterialPhysicsData> savedPhysics = new List<MaterialPhysicsData>();
    public List<MaterialPhysicsData> materials = new List<MaterialPhysicsData>();
}

[Serializable]
public class MaterialPhysicsData
{
    public string materialName;
    public List<MaterialProperty> properties = new List<MaterialProperty>();
}

[Serializable]
public class MaterialProperty
{
    public string propertyName;
    public string meaning;
    public double value;
    public string unit;
    public string status;
    public ScaleContainer scales;
}

[Serializable]
public class ScaleContainer
{
    public ScaleData scale1_charging;
    public ScaleData scale2_transfer;
    public ScaleData scale3_repulsion;
}

[Serializable]
public class ScaleData
{
    public string status;
    public string dependent_quantity;
    public string formula;
    public string output_unit;
}

// ============================================================================
// 2. PARSER Y EVALUADOR DINÁMICO DE ARCHIVOS Y MATERIALES JSON
// ============================================================================

public static class DynamicJsonMaterialReader
{
    public struct ResolvedMaterial
    {
        public string materialName;
        public double density;
        public double thicknessMeters;
        public double massKg;
        public Color materialColor;
        public double electricalConductivity;
        public bool isParsedFromJSON;
    }

    public static ResolvedMaterial ResolveMaterial(string jsonContent, string targetMaterialName, double leafAreaM2, double defaultThicknessMeters)
    {
        string cleanTarget = targetMaterialName.ToLower().Trim();
        double defaultDensity = 2700.0;
        double thickness = defaultThicknessMeters;

        if (cleanTarget.Contains("gold") || cleanTarget.Contains("oro"))
        {
            defaultDensity = 19300.0;
            thickness = 0.0000015;
        }
        else if (cleanTarget.Contains("brass") || cleanTarget.Contains("laton"))
        {
            defaultDensity = 8500.0;
            thickness = 0.000008;
        }
        else if (cleanTarget.Contains("copper") || cleanTarget.Contains("cobre"))
        {
            defaultDensity = 8960.0;
            thickness = 0.000008;
        }
        else if (cleanTarget.Contains("amber") || cleanTarget.Contains("ambar"))
        {
            defaultDensity = 1080.0;
            thickness = 0.000012;
        }

        double leafVolumeM3 = leafAreaM2 * thickness;

        ResolvedMaterial result = new ResolvedMaterial
        {
            materialName = targetMaterialName,
            density = defaultDensity,
            thicknessMeters = thickness,
            massKg = Math.Max(1e-7, defaultDensity * leafVolumeM3),
            materialColor = new Color(0.85f, 0.85f, 0.85f, 1f),
            electricalConductivity = 3.77e7,
            isParsedFromJSON = false
        };

        if (string.IsNullOrEmpty(jsonContent)) return result;

        try
        {
            SavedPhysicsRoot root = JsonUtility.FromJson<SavedPhysicsRoot>(jsonContent);
            List<MaterialPhysicsData> matList = (root != null && root.savedPhysics != null && root.savedPhysics.Count > 0)
                ? root.savedPhysics
                : (root != null ? root.materials : null);

            if (matList == null || matList.Count == 0) return result;

            MaterialPhysicsData matchedMat = matList.Find(
                m => m.materialName.Equals(targetMaterialName, StringComparison.OrdinalIgnoreCase) ||
                     targetMaterialName.IndexOf(m.materialName, StringComparison.OrdinalIgnoreCase) >= 0 ||
                     m.materialName.IndexOf(targetMaterialName, StringComparison.OrdinalIgnoreCase) >= 0
            );

            if (matchedMat == null) return result;

            result.materialName = matchedMat.materialName;
            result.isParsedFromJSON = true;

            foreach (var prop in matchedMat.properties)
            {
                if (prop.propertyName.Equals("Hex color code", StringComparison.OrdinalIgnoreCase))
                {
                    int rgbInt = Convert.ToInt32(prop.value);
                    float r = ((rgbInt >> 16) & 0xFF) / 255f;
                    float g = ((rgbInt >> 8) & 0xFF) / 255f;
                    float b = (rgbInt & 0xFF) / 255f;
                    result.materialColor = new Color(r, g, b, 1f);
                }

                if (prop.propertyName.Equals("Density", StringComparison.OrdinalIgnoreCase))
                {
                    result.density = prop.value;

                    if (prop.scales != null && prop.scales.scale3_repulsion != null &&
                        prop.scales.scale3_repulsion.status == "supported_quantitative")
                    {
                        string formula = prop.scales.scale3_repulsion.formula;
                        result.massKg = Math.Max(1e-7, EvaluateFormula(formula, prop.value, leafVolumeM3));
                    }
                    else
                    {
                        result.massKg = Math.Max(1e-7, prop.value * leafVolumeM3);
                    }
                }

                if (prop.propertyName.Equals("Electrical conductivity", StringComparison.OrdinalIgnoreCase))
                {
                    result.electricalConductivity = prop.value;
                }
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[DynamicJsonMaterialReader] Error al parsear JSON: " + ex.Message);
        }

        return result;
    }

    private static double EvaluateFormula(string formulaStr, double defaultDensity, double xVolume)
    {
        if (string.IsNullOrEmpty(formulaStr)) return defaultDensity * xVolume;

        string expr = formulaStr.Replace("x", xVolume.ToString(CultureInfo.InvariantCulture)).Trim();
        if (expr.Contains("*"))
        {
            string[] parts = expr.Split('*');
            if (parts.Length == 2 &&
                double.TryParse(parts[0].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out double p1) &&
                double.TryParse(parts[1].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out double p2))
            {
                return p1 * p2;
            }
        }
        return defaultDensity * xVolume;
    }
}

// ============================================================================
// 3. RUNTIME BEZIER EDITOR (ORIENTACIÓN VERTICAL FIJA EN LA BASE Y CONTROL IA)
// ============================================================================

public class RuntimeBezierEditor : MonoBehaviour
{
    [Header("CONFIGURACIÓN DE CACHÉ DE MATERIALES")]
    public string physicsCacheFileName = "ElectroscopePhysicsCache_v2.json";

    [Header("MATERIAL AND CURVE CONFIGURATION")]
    public string fallbackMaterialName = "Gold";
    public float lineThickness = 4f;
    public Color curveColor = Color.green;
    public int curveResolution = 80;
    public float wire3DThickness = 0.04f;

    [Header("3D SPACE MAPPING")]
    public float worldWidth = 1.5f;
    public float worldHeight = 2.0f;

    [Header("CHARGE CONTROL & SIMULATION")]
    public float initialChargeNanoCoulombs = 20.0f;
    public float maximumChargeNanoCoulombs = 100.0f;

    [Header("AI DYNAMIC PARAMETERS")]
    public float currentSlopeDyDx = 0.5f;
    public float currentLeafPlacementT = 0.5f;
    public int currentCancelStart = 0;
    public int currentCancelCount = 0;

    private RectTransform windowRect;
    private UIBezierGraphic bezierGraphic;
    private FoilRepulsionSlideSimulator foilSimulator;
    private Slider chargeSlider;
    private UIText chargeValueText;
    private UIText simulationButtonText;

    private MathCurve generatedCurve;
    private Transform generatedLeavesAnchor;
    private Transform generatedLeftHinge;
    private Transform generatedRightHinge;
    private RectTransform selectionMarker;

    private int callSequenceCounter = 0;

    private void OnEnable()
    {
        ProceduralElectroscopeBuilder.OnElectroscopeBuilt += HandleElectroscopeBuilt;
    }

    private void OnDisable()
    {
        ProceduralElectroscopeBuilder.OnElectroscopeBuilt -= HandleElectroscopeBuilt;
    }

    private void HandleElectroscopeBuilt()
    {
        Debug.Log("<color=green>[EVENT-NOTIFY]</color> Electroscope 3D listo en la escena. Sincronizando curva 3D...");
        GenerateNormalized3DCurve();
    }

    private void Start()
    {
        Debug.Log("<color=cyan>[TRACE-INIT]</color> RuntimeBezierEditor.Start() ejecutado.");
        EnsureUIBuilt();
        EnsureEventSystem();

        Invoke(nameof(AutoInitialize3DScene), 0.1f);
    }

    private void EnsureUIBuilt()
    {
        if (windowRect == null || bezierGraphic == null || bezierGraphic.points == null || bezierGraphic.points.Count == 0)
        {
            CleanupExistingCanvas();
            BuildProceduralUI();
        }
    }

    private void AutoInitialize3DScene()
    {
        Debug.Log("<color=cyan>[TRACE-INIT]</color> AutoInitialize3DScene() iniciando.");
        UpdateCurveAndPhysicsInstant(initialChargeNanoCoulombs, currentSlopeDyDx, currentLeafPlacementT);
    }

    public string LoadPhysicsCacheJsonFile()
    {
        string primaryPath = Path.Combine(Application.dataPath, physicsCacheFileName);

        if (File.Exists(primaryPath))
        {
            try { return File.ReadAllText(primaryPath); } catch { }
        }

        string streamingPath = Path.Combine(Application.streamingAssetsPath, physicsCacheFileName);
        if (File.Exists(streamingPath))
        {
            try { return File.ReadAllText(streamingPath); } catch { }
        }

        return "{\"savedPhysics\":[]}";
    }

    // ============================================================================
    // MÉTODOS DE PRUEBA DESDE EL INSPECTOR (CONTEXT MENU API)
    // ============================================================================

    [ContextMenu("Framework Test: Cancel BEM Range (1..16) & Log Recalculation")]
    public void TestCancelBEMRangeAndLog()
    {
        Debug.Log("<color=cyan>[INSPECTOR-TEST]</color> Ejecutando prueba de cancelación BEM [1..16]...");
        if (foilSimulator == null)
        {
            foilSimulator = UnityEngine.Object.FindFirstObjectByType<FoilRepulsionSlideSimulator>();
        }

        if (foilSimulator != null)
        {
            foilSimulator.TestCancelPanelsAndLogRecalculation(1, 16);
        }
        else
        {
            Debug.LogWarning("[INSPECTOR-TEST] FoilRepulsionSlideSimulator no está instanciado aún en la escena.");
        }
    }

    [ContextMenu("Framework Test: Restore All BEM Panels")]
    public void TestRestoreAllBEMPanels()
    {
        Debug.Log("<color=cyan>[INSPECTOR-TEST]</color> Restaurando todos los paneles BEM...");
        if (foilSimulator == null)
        {
            foilSimulator = UnityEngine.Object.FindFirstObjectByType<FoilRepulsionSlideSimulator>();
        }

        if (foilSimulator != null)
        {
            foilSimulator.TestCancelPanelsAndLogRecalculation(0, 0);
        }
    }

    // ============================================================================
    // MÉTODOS PÚBLICOS DE ENTRADA (MAPPING API & IA FRAMEWORK)
    // ============================================================================

    public void ApplyAIConfiguration(AICurveChargeConfig config)
    {
        callSequenceCounter++;
        Debug.Log($"<color=yellow>[LOG-IA #{callSequenceCounter}]</color> ApplyAIConfiguration() | Q={config?.charge_nC}, Slope={config?.curve_slope_dy_dx}, PlacementT={config?.leaf_placement_t}, CancelRange=({config?.cancel_panel_start}, {config?.cancel_panel_count})");

        if (config == null) return;

        if (!string.IsNullOrEmpty(config.leaves_material))
        {
            fallbackMaterialName = config.leaves_material;
        }

        currentCancelStart = config.cancel_panel_start;
        currentCancelCount = config.cancel_panel_count;

        UpdateCurveAndPhysicsInstant(config.charge_nC, config.curve_slope_dy_dx, config.leaf_placement_t);
    }

    public void SetDisabledPanelRangeFromAI(int startOneBased, int count)
    {
        callSequenceCounter++;
        Debug.Log($"<color=yellow>[LOG-IA #{callSequenceCounter}]</color> SetDisabledPanelRangeFromAI(start={startOneBased}, count={count})");
        currentCancelStart = startOneBased;
        currentCancelCount = count;

        if (foilSimulator != null)
        {
            foilSimulator.SetDisabledPanelRange(startOneBased, count);
        }
    }

    public void ClearDisabledPanelsFromAI()
    {
        SetDisabledPanelRangeFromAI(0, 0);
    }

    public void SetChargeFromAI(float charge_nC)
    {
        callSequenceCounter++;
        Debug.Log($"<color=yellow>[LOG-IA #{callSequenceCounter}]</color> SetChargeFromAI({charge_nC} nC)");
        UpdateCurveAndPhysicsInstant(charge_nC, currentSlopeDyDx, currentLeafPlacementT);
    }

    public void GenerateCurveFromSlopeAndPlacement(float slopeDyDx, float placementT)
    {
        callSequenceCounter++;
        Debug.Log($"<color=yellow>[LOG-IA #{callSequenceCounter}]</color> GenerateCurveFromSlopeAndPlacement(slope={slopeDyDx}, placementT={placementT})");
        UpdateCurveAndPhysicsInstant(initialChargeNanoCoulombs, slopeDyDx, placementT);
    }

    public void SetLeavesMaterialFromAI(string materialName)
    {
        callSequenceCounter++;
        Debug.Log($"<color=yellow>[LOG-IA #{callSequenceCounter}]</color> SetLeavesMaterialFromAI('{materialName}')");
        if (string.IsNullOrEmpty(materialName)) return;
        fallbackMaterialName = materialName;
        UpdateCurveAndPhysicsInstant(initialChargeNanoCoulombs, currentSlopeDyDx, currentLeafPlacementT);
    }

    public void UpdateCurveAndPhysicsInstant(float charge_nC, float slopeDyDx, float placementT)
    {
        callSequenceCounter++;
        Debug.Log($"<color=orange>[TRACE-EXEC #{callSequenceCounter}]</color> UpdateCurveAndPhysicsInstant(Q={charge_nC}, Slope={slopeDyDx}, T={placementT})");

        EnsureUIBuilt();

        this.initialChargeNanoCoulombs = Mathf.Clamp(charge_nC, 0.01f, maximumChargeNanoCoulombs);
        this.currentSlopeDyDx = slopeDyDx;
        this.currentLeafPlacementT = Mathf.Clamp01(placementT);

        if (chargeSlider != null)
        {
            chargeSlider.value = initialChargeNanoCoulombs;
        }
        UpdateChargeText(initialChargeNanoCoulombs);

        if (bezierGraphic != null && bezierGraphic.points != null && bezierGraphic.points.Count >= 4)
        {
            float w = windowRect != null ? windowRect.rect.width : 460f;
            float h = windowRect != null ? windowRect.rect.height : 530f;

            Vector2 p0 = new Vector2(-w * 0.3f, -h * 0.1f);
            Vector2 p3 = new Vector2(w * 0.35f, h * 0.2f);

            float handleLength = 120f;
            Vector2 tangentDir = new Vector2(1f, slopeDyDx).normalized * handleLength;

            bezierGraphic.points[0].anchoredPosition = p0;
            bezierGraphic.points[1].anchoredPosition = p0 + tangentDir;
            bezierGraphic.points[2].anchoredPosition = p3 - tangentDir;
            bezierGraphic.points[3].anchoredPosition = p3;

            bezierGraphic.SetVerticesDirty();
        }

        GenerateNormalized3DCurve();

        if (foilSimulator != null)
        {
            foilSimulator.SetDisabledPanelRange(currentCancelStart, currentCancelCount);
            foilSimulator.SetChargeNanoCoulombs(initialChargeNanoCoulombs);
            foilSimulator.isSimulating = true;
        }
    }

    // ============================================================================
    // CALLBACKS DE INTERACCIÓN MANUAL (USER UI INPUTS)
    // ============================================================================

    public void OnManualSliderChanged(float nanoCoulombs)
    {
        callSequenceCounter++;
        Debug.Log($"<color=green>[LOG-MANUAL #{callSequenceCounter}]</color> Usuario movió Slider -> {nanoCoulombs:F2} nC");
        OnChargeChanged(nanoCoulombs);
    }

    public void OnManualGenerateButtonClick()
    {
        callSequenceCounter++;
        Debug.Log($"<color=green>[LOG-MANUAL #{callSequenceCounter}]</color> Clic en 'Generate Parametric Curve'");
        GenerateNormalized3DCurve();
    }

    public void OnManualCurveClicked(float parameter, Vector2 curveLocalPoint)
    {
        callSequenceCounter++;
        Debug.Log($"<color=green>[LOG-MANUAL #{callSequenceCounter}]</color> Clic en Curva 2D -> t = {parameter:F3}");
        SelectCurvePoint(parameter, curveLocalPoint);
    }

    public void OnManualPointDragged(int pointIndex, Vector2 newAnchoredPosition)
    {
        callSequenceCounter++;
        Debug.Log($"<color=green>[LOG-MANUAL #{callSequenceCounter}]</color> Arrastró Asa Bézier #{pointIndex} -> {newAnchoredPosition}");
        if (bezierGraphic != null) bezierGraphic.SetVerticesDirty();
    }

    // ============================================================================
    // BÚSQUEDA PROFUNDA Y CONSTRUCCIÓN CON ORIENTACIÓN VERTICAL CONSTANTE
    // ============================================================================

    private GameObject FindSceneObject(params string[] possibleNames)
    {
        foreach (string candidate in possibleNames)
        {
            if (string.IsNullOrEmpty(candidate)) continue;
            GameObject exact = GameObject.Find(candidate);
            if (exact != null) return exact;
        }

        Transform[] allTransforms = Resources.FindObjectsOfTypeAll<Transform>();
        foreach (string candidate in possibleNames)
        {
            if (string.IsNullOrEmpty(candidate)) continue;
            foreach (Transform item in allTransforms)
            {
                if (item == null || !item.gameObject.scene.IsValid() || !item.gameObject.scene.isLoaded) continue;
                if (item.hideFlags == HideFlags.NotEditable || item.hideFlags == HideFlags.HideAndDontSave) continue;
                if (string.Equals(item.name, candidate, StringComparison.OrdinalIgnoreCase))
                    return item.gameObject;
            }
        }
        return null;
    }

    public void GenerateNormalized3DCurve()
    {
        StopCoroutine(nameof(GenerateNormalized3DCurveRoutine));
        StartCoroutine(nameof(GenerateNormalized3DCurveRoutine));
    }

    private IEnumerator GenerateNormalized3DCurveRoutine()
    {
        Debug.Log($"<color=magenta>[TRACE-METHOD]</color> Generando curva independiente y acoplando a 3D...");

        EnsureUIBuilt();

        GameObject attachmentPlate = FindSceneObject("Curve_Attachment_Plate", "Plate", "Rod_Top", "Attachment_Plate");
        GameObject leavesAnchor = FindSceneObject("Leaves_Anchor", "Foil_Anchor", "Anchor");

        while (attachmentPlate == null || leavesAnchor == null)
        {
            yield return new WaitForSeconds(0.1f);
            attachmentPlate = FindSceneObject("Curve_Attachment_Plate", "Plate", "Rod_Top", "Attachment_Plate");
            leavesAnchor = FindSceneObject("Leaves_Anchor", "Foil_Anchor", "Anchor");
        }

        attachmentPlate.SetActive(true);
        leavesAnchor.SetActive(true);

        Transform leftHinge = FindChildRecursive(leavesAnchor.transform, "Left_Leaf_Hinge");
        Transform rightHinge = FindChildRecursive(leavesAnchor.transform, "Right_Leaf_Hinge");
        if (leftHinge == null) leftHinge = FindChildRecursive(leavesAnchor.transform, "Leaf_1");
        if (rightHinge == null) rightHinge = FindChildRecursive(leavesAnchor.transform, "Leaf_2");

        if (leftHinge == null || rightHinge == null || leftHinge == rightHinge)
        {
            Debug.LogError("[TRACE-ERROR] No se encontraron dos bisagras originales distintas.");
            yield break;
        }

        leftHinge.gameObject.SetActive(true);
        rightHinge.gameObject.SetActive(true);

        if (bezierGraphic == null || bezierGraphic.points == null || windowRect == null)
        {
            Debug.LogWarning("[TRACE-WARN] La UI gráfica no estaba lista. Reconstruyendo...");
            EnsureUIBuilt();
            if (bezierGraphic == null || bezierGraphic.points == null || windowRect == null)
            {
                Debug.LogError("[TRACE-ERROR] No se pudo acceder a bezierGraphic o windowRect.");
                yield break;
            }
        }

        Vector3 targetPos = attachmentPlate.transform.position;
        int count = bezierGraphic.points.Count;
        List<Vector3> unconstrainedPoints = new List<Vector3>();

        float winWidth = windowRect.rect.width > 0 ? windowRect.rect.width : 460f;
        float winHeight = windowRect.rect.height > 0 ? windowRect.rect.height : 530f;

        for (int i = 0; i < count; i++)
        {
            if (bezierGraphic.points[i] != null)
            {
                Vector2 uiPos = bezierGraphic.points[i].anchoredPosition;
                float percentX = uiPos.x / winWidth;
                float percentY = uiPos.y / winHeight;

                float localX = percentX * worldWidth;
                float localY = percentY * worldHeight;
                unconstrainedPoints.Add(new Vector3(localX, localY, 0f));
            }
        }

        if (unconstrainedPoints.Count == 0) yield break;

        MathCurve rawCurve = new MathCurve(unconstrainedPoints.ToArray());

        Vector3 endPointLocal = rawCurve.Evaluate(1.0f);
        Vector3 snapOffset = targetPos - endPointLocal;

        List<Vector3> snappedPoints = new List<Vector3>();
        for (int i = 0; i < unconstrainedPoints.Count; i++)
        {
            snappedPoints.Add(unconstrainedPoints[i] + snapOffset);
        }

        MathCurve curve = new MathCurve(snappedPoints.ToArray());

        float leafT = Mathf.Clamp01(currentLeafPlacementT);
        Vector3 anchorPosition3D = curve.Evaluate(leafT);

        Quaternion baseVerticalRotation = Quaternion.identity;

        generatedCurve = curve;
        generatedLeavesAnchor = leavesAnchor.transform;
        generatedLeftHinge = leftHinge;
        generatedRightHinge = rightHinge;
        if (selectionMarker != null) selectionMarker.gameObject.SetActive(false);

        leavesAnchor.transform.rotation = baseVerticalRotation;
        leavesAnchor.transform.position = anchorPosition3D;

        float existingHingeDistance = Vector3.Distance(leftHinge.position, rightHinge.position);
        if (existingHingeDistance < 1e-6f) existingHingeDistance = 0.005f;

        leftHinge.position = anchorPosition3D - 0.5f * existingHingeDistance * Vector3.right;
        rightHinge.position = anchorPosition3D + 0.5f * existingHingeDistance * Vector3.right;
        leftHinge.rotation = baseVerticalRotation;
        rightHinge.rotation = baseVerticalRotation;

        float wireArcLength = curve.CalculateArcLength(100);

        if (foilSimulator == null)
        {
            FoilRepulsionSlideSimulator[] existingSimulators =
                FindObjectsByType<FoilRepulsionSlideSimulator>(FindObjectsSortMode.None);

            if (existingSimulators != null && existingSimulators.Length > 0)
            {
                foilSimulator = existingSimulators[0];
                for (int i = 1; i < existingSimulators.Length; i++)
                {
                    if (existingSimulators[i] != null && existingSimulators[i] != foilSimulator)
                        Destroy(existingSimulators[i].gameObject);
                }

                foilSimulator.RefreshFoilPosition(curve, leftHinge, rightHinge);
            }
            else
            {
                string physicsJsonData = LoadPhysicsCacheJsonFile();
                string activeLeavesMaterial = DetectActiveMaterialFromScene();
                GameObject simulatorGO = new GameObject("Foil_Repulsion_Simulator");
                foilSimulator = simulatorGO.AddComponent<FoilRepulsionSlideSimulator>();
                foilSimulator.Initialize(curve, leftHinge, rightHinge, leafT, wireArcLength, physicsJsonData, activeLeavesMaterial);
            }
        }
        else
        {
            foilSimulator.RefreshFoilPosition(curve, leftHinge, rightHinge);
        }

        foilSimulator.SetDisabledPanelRange(currentCancelStart, currentCancelCount);
        foilSimulator.SetChargeNanoCoulombs(chargeSlider != null ? chargeSlider.value : initialChargeNanoCoulombs);
        foilSimulator.isSimulating = true;

        Create3DWire(curve, attachmentPlate);
        Debug.Log("<color=green>[TRACE-SUCCESS]</color> Curva generada con láminas en posición base vertical.");
    }

    public void SelectCurvePoint(float parameter, Vector2 curveLocalPoint)
    {
        Debug.Log($"<color=magenta>[TRACE-METHOD]</color> SelectCurvePoint(parameter={parameter:F3})");
        if (generatedCurve == null || generatedLeavesAnchor == null ||
            generatedLeftHinge == null || generatedRightHinge == null)
        {
            return;
        }

        parameter = Mathf.Clamp01(parameter);
        currentLeafPlacementT = parameter;

        if (selectionMarker != null && curveLocalPoint != Vector2.zero)
        {
            selectionMarker.anchoredPosition = curveLocalPoint;
            selectionMarker.gameObject.SetActive(true);
            selectionMarker.SetAsLastSibling();
        }

        Vector3 selectedPosition = generatedCurve.Evaluate(parameter);
        Vector3 derivativeTangent = generatedCurve.GetDerivative(parameter);
        float dy_dx = derivativeTangent.x != 0 ? derivativeTangent.y / derivativeTangent.x : derivativeTangent.y;
        currentSlopeDyDx = dy_dx;

        float hingeDistance = Vector3.Distance(generatedLeftHinge.position, generatedRightHinge.position);
        if (hingeDistance < 1e-6f) hingeDistance = 0.005f;

        Quaternion baseVerticalRotation = Quaternion.identity;
        generatedLeavesAnchor.rotation = baseVerticalRotation;
        generatedLeavesAnchor.position = selectedPosition;

        generatedLeftHinge.position = selectedPosition - 0.5f * hingeDistance * Vector3.right;
        generatedRightHinge.position = selectedPosition + 0.5f * hingeDistance * Vector3.right;
        generatedLeftHinge.rotation = baseVerticalRotation;
        generatedRightHinge.rotation = baseVerticalRotation;

        if (foilSimulator != null)
        {
            foilSimulator.RefreshFoilPosition(generatedCurve, generatedLeftHinge, generatedRightHinge);
            foilSimulator.isSimulating = true;
        }
    }

    private void Create3DWire(MathCurve curve, GameObject attachmentPlate)
    {
        GameObject oldAssembly = GameObject.Find("3D_Wire_Assembly");
        if (oldAssembly != null) Destroy(oldAssembly);

        GameObject assembly = new GameObject("3D_Wire_Assembly");
        if (attachmentPlate.transform.parent != null) assembly.transform.SetParent(attachmentPlate.transform.parent);

        Vector3[] finalCurvePoints = new Vector3[curveResolution];
        for (int i = 0; i < curveResolution; i++)
            finalCurvePoints[i] = curve.Evaluate(i / (float)(curveResolution - 1));

        GameObject lineObj = new GameObject("Wire_Line");
        lineObj.transform.SetParent(assembly.transform);

        LineRenderer line = lineObj.AddComponent<LineRenderer>();
        line.useWorldSpace = true;
        line.startWidth = wire3DThickness;
        line.endWidth = wire3DThickness;
        line.positionCount = curveResolution;
        line.SetPositions(finalCurvePoints);

        Shader shader = Shader.Find("Standard");
        if (shader != null)
        {
            Material wireMaterial = new Material(shader);
            wireMaterial.color = new Color(0.70f, 0.40f, 0.20f);
            line.material = wireMaterial;
        }
    }

    // ============================================================================
    // CONSTRUCCIÓN DE UI Y DETECCIÓN DE MATERIALES
    // ============================================================================

    private void CleanupExistingCanvas()
    {
        GameObject oldCanvas = GameObject.Find("Procedural_Curve_Canvas");
        if (oldCanvas != null) Destroy(oldCanvas);
    }

    private T GetOrAddComponent<T>(GameObject go) where T : Component
    {
        T comp = go.GetComponent<T>();
        return comp != null ? comp : go.AddComponent<T>();
    }

    private Font GetBuiltinFont()
    {
        Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        if (font == null) font = Resources.GetBuiltinResource<Font>("Arial.ttf");
        if (font == null) font = Font.CreateDynamicFontFromOSFont("Arial", 14);
        return font;
    }

    private void EnsureEventSystem()
    {
        EventSystem eventSystem = FindFirstObjectByType<EventSystem>();
        if (eventSystem == null)
        {
            GameObject eventSystemGO = new GameObject("EventSystem");
            eventSystem = eventSystemGO.AddComponent<EventSystem>();
        }

        InputSystemUIInputModule inputModule = eventSystem.GetComponent<InputSystemUIInputModule>();
        if (inputModule == null)
        {
            eventSystem.gameObject.AddComponent<InputSystemUIInputModule>();
        }
    }

    public string DetectActiveMaterialFromScene()
    {
        MonoBehaviour[] sceneScripts = UnityEngine.Object.FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None);

        foreach (MonoBehaviour script in sceneScripts)
        {
            if (script == null) continue;
            Type t = script.GetType();

            if (t.Name.Equals("ProceduralElectroscopeBuilder", StringComparison.OrdinalIgnoreCase) ||
                t.Name.Contains("ElectroscopeBuilder"))
            {
                string[] possibleFields = new string[] {
                    "leavesMaterial", "LeavesMaterial", "leavesMaterialName",
                    "leafMaterial", "LeafMaterial", "leavesMat"
                };

                foreach (string fieldName in possibleFields)
                {
                    FieldInfo f = t.GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.IgnoreCase);
                    if (f != null)
                    {
                        object val = f.GetValue(script);
                        if (val != null && !string.IsNullOrEmpty(val.ToString()))
                            return val.ToString();
                    }

                    PropertyInfo p = t.GetProperty(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.IgnoreCase);
                    if (p != null)
                    {
                        object val = p.GetValue(script);
                        if (val != null && !string.IsNullOrEmpty(val.ToString()))
                            return val.ToString();
                    }
                }
            }
        }

        return fallbackMaterialName;
    }

    private void BuildProceduralUI()
    {
        GameObject canvasGO = new GameObject("Procedural_Curve_Canvas");

        Canvas canvas = GetOrAddComponent<Canvas>(canvasGO);
        canvas.enabled = true;
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;

        CanvasScaler scaler = GetOrAddComponent<CanvasScaler>(canvasGO);
        scaler.enabled = true;
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);

        GetOrAddComponent<GraphicRaycaster>(canvasGO);

        GameObject windowGO = new GameObject("Draggable_Table_Window");
        windowGO.transform.SetParent(canvasGO.transform, false);

        UIImage windowImage = GetOrAddComponent<UIImage>(windowGO);
        windowImage.color = new Color(0.12f, 0.12f, 0.12f, 0.95f);

        windowRect = windowGO.GetComponent<RectTransform>();
        windowRect.anchorMin = new Vector2(0.5f, 0f);
        windowRect.anchorMax = new Vector2(0.5f, 0f);
        windowRect.pivot = new Vector2(0.5f, 0f);
        windowRect.sizeDelta = new Vector2(460f, 530f);
        windowRect.anchoredPosition = new Vector2(0f, 30f);

        GetOrAddComponent<UIDraggable>(windowGO).isWindow = true;
        UIWindowController windowController = GetOrAddComponent<UIWindowController>(windowGO);

        UIText title = CreateText("Title", windowGO.transform, "Mathematical Curve Editor (BEM Precision)", 16, Color.white);
        RectTransform titleRect = title.GetComponent<RectTransform>();
        titleRect.anchorMin = new Vector2(0f, 1f);
        titleRect.anchorMax = new Vector2(1f, 1f);
        titleRect.pivot = new Vector2(0.5f, 1f);
        titleRect.sizeDelta = new Vector2(0f, 35f);

        List<GameObject> collapsibleElements = new List<GameObject>();

        GameObject curveGO = new GameObject("Bezier_Curve");
        curveGO.transform.SetParent(windowGO.transform, false);
        RectTransform curveRect = curveGO.AddComponent<RectTransform>();
        curveRect.anchorMin = Vector2.zero;
        curveRect.anchorMax = Vector2.one;

        GetOrAddComponent<CanvasRenderer>(curveGO);
        bezierGraphic = GetOrAddComponent<UIBezierGraphic>(curveGO);
        bezierGraphic.color = curveColor;
        bezierGraphic.thickness = lineThickness;
        bezierGraphic.resolution = curveResolution;
        bezierGraphic.editor = this;
        collapsibleElements.Add(curveGO);

        Vector2[] initialPositions = {
            new Vector2(-130f, 40f),
            new Vector2(-40f, 180f),
            new Vector2(40f, 180f),
            new Vector2(180f, 40f)
        };
        Color[] pointColors = { Color.yellow, Color.cyan, Color.cyan, Color.red };

        bezierGraphic.points = new List<RectTransform>();
        for (int i = 0; i < 4; i++)
        {
            GameObject pointGO = new GameObject("Point_" + i);
            pointGO.transform.SetParent(windowGO.transform, false);
            GetOrAddComponent<UIImage>(pointGO).color = pointColors[i];

            RectTransform pointRect = pointGO.GetComponent<RectTransform>();
            pointRect.sizeDelta = new Vector2(18f, 18f);
            pointRect.anchoredPosition = initialPositions[i];

            UIDraggable drag = GetOrAddComponent<UIDraggable>(pointGO);
            drag.pointIndex = i;
            drag.editor = this;

            bezierGraphic.points.Add(pointRect);
            collapsibleElements.Add(pointGO);
        }

        GameObject markerGO = new GameObject("Curve_Selection_Marker");
        markerGO.transform.SetParent(windowGO.transform, false);
        UIImage markerImage = GetOrAddComponent<UIImage>(markerGO);
        markerImage.color = new Color(0.55f, 0.55f, 0.55f, 1f);
        markerImage.raycastTarget = false;
        selectionMarker = markerGO.GetComponent<RectTransform>();
        selectionMarker.anchorMin = new Vector2(0.5f, 0.5f);
        selectionMarker.anchorMax = new Vector2(0.5f, 0.5f);
        selectionMarker.pivot = new Vector2(0.5f, 0.5f);
        selectionMarker.sizeDelta = new Vector2(14f, 14f);
        selectionMarker.anchoredPosition = Vector2.zero;
        markerGO.SetActive(false);

        GameObject generateButtonGO = new GameObject("Generate_3D_Button");
        generateButtonGO.transform.SetParent(windowGO.transform, false);
        GetOrAddComponent<UIImage>(generateButtonGO).color = new Color(0.20f, 0.60f, 0.30f);

        UIButton generateButton = GetOrAddComponent<UIButton>(generateButtonGO);
        RectTransform generateRect = generateButtonGO.GetComponent<RectTransform>();
        generateRect.anchorMin = new Vector2(0.5f, 0f);
        generateRect.anchorMax = new Vector2(0.5f, 0f);
        generateRect.pivot = new Vector2(0.5f, 0f);
        generateRect.sizeDelta = new Vector2(240f, 34f);
        generateRect.anchoredPosition = new Vector2(0f, 160f);

        CreateText("Text", generateButtonGO.transform, "Generate Parametric Curve", 14, Color.white);
        generateButton.onClick.AddListener(OnManualGenerateButtonClick);

        collapsibleElements.Add(generateButtonGO);

        BuildChargeControlUIElements(windowGO.transform, collapsibleElements);

        windowController.collapsibleElements = collapsibleElements.ToArray();
    }

    private void BuildChargeControlUIElements(Transform parentWindow, List<GameObject> collapsibleList)
    {
        UIText sectionHeader = CreateText("Charge_Control_Header", parentWindow, "── Electrostatic Charge Control ──", 13, new Color(0.9f, 0.9f, 0.9f));
        RectTransform headerRect = sectionHeader.GetComponent<RectTransform>();
        headerRect.anchorMin = new Vector2(0.5f, 0f);
        headerRect.anchorMax = new Vector2(0.5f, 0f);
        headerRect.pivot = new Vector2(0.5f, 0f);
        headerRect.sizeDelta = new Vector2(400f, 20f);
        headerRect.anchoredPosition = new Vector2(0f, 125f);
        collapsibleList.Add(sectionHeader.gameObject);

        UIText chargeLabel = CreateText("Charge_Label", parentWindow, "Injected Charge:", 13, Color.white);
        RectTransform chargeLabelRect = chargeLabel.GetComponent<RectTransform>();
        chargeLabelRect.anchorMin = new Vector2(0f, 0f);
        chargeLabelRect.anchorMax = new Vector2(0f, 0f);
        chargeLabelRect.pivot = new Vector2(0f, 0f);
        chargeLabelRect.sizeDelta = new Vector2(140f, 22f);
        chargeLabelRect.anchoredPosition = new Vector2(25f, 95f);
        collapsibleList.Add(chargeLabel.gameObject);

        chargeValueText = CreateText("Charge_Value", parentWindow, initialChargeNanoCoulombs.ToString("F2") + " nC", 13, Color.yellow);
        chargeValueText.alignment = TextAnchor.MiddleRight;
        RectTransform chargeValueRect = chargeValueText.GetComponent<RectTransform>();
        chargeValueRect.anchorMin = new Vector2(1f, 0f);
        chargeValueRect.anchorMax = new Vector2(1f, 0f);
        chargeValueRect.pivot = new Vector2(1f, 0f);
        chargeValueRect.sizeDelta = new Vector2(100f, 22f);
        chargeValueRect.anchoredPosition = new Vector2(-25f, 95f);
        collapsibleList.Add(chargeValueText.gameObject);

        GameObject sliderGO = new GameObject("Charge_Slider", typeof(RectTransform), typeof(Slider));
        sliderGO.transform.SetParent(parentWindow, false);

        RectTransform sliderRect = sliderGO.GetComponent<RectTransform>();
        sliderRect.anchorMin = new Vector2(0.5f, 0f);
        sliderRect.anchorMax = new Vector2(0.5f, 0f);
        sliderRect.pivot = new Vector2(0.5f, 0f);
        sliderRect.sizeDelta = new Vector2(400f, 20f);
        sliderRect.anchoredPosition = new Vector2(0f, 65f);

        GameObject bgGO = new GameObject("Background", typeof(RectTransform), typeof(UIImage));
        bgGO.transform.SetParent(sliderGO.transform, false);
        RectTransform bgRect = bgGO.GetComponent<RectTransform>();
        bgRect.anchorMin = new Vector2(0f, 0.25f);
        bgRect.anchorMax = new Vector2(1f, 0.75f);
        bgRect.sizeDelta = Vector2.zero;
        bgGO.GetComponent<UIImage>().color = new Color(0.2f, 0.2f, 0.2f, 0.9f);

        GameObject fillAreaGO = new GameObject("Fill Area", typeof(RectTransform));
        fillAreaGO.transform.SetParent(sliderGO.transform, false);
        RectTransform fillAreaRect = fillAreaGO.GetComponent<RectTransform>();
        fillAreaRect.anchorMin = new Vector2(0f, 0.25f);
        fillAreaRect.anchorMax = new Vector2(1f, 0.75f);
        fillAreaRect.sizeDelta = new Vector2(-20f, 0f);
        fillAreaRect.anchoredPosition = Vector2.zero;

        GameObject fillGO = new GameObject("Fill", typeof(RectTransform), typeof(UIImage));
        fillGO.transform.SetParent(fillAreaGO.transform, false);
        RectTransform fillRect = fillGO.GetComponent<RectTransform>();
        fillRect.anchorMin = Vector2.zero;
        fillRect.anchorMax = Vector2.one;
        fillRect.sizeDelta = Vector2.zero;
        fillGO.GetComponent<UIImage>().color = new Color(0.15f, 0.65f, 1.0f);

        GameObject handleAreaGO = new GameObject("Handle Slide Area", typeof(RectTransform));
        handleAreaGO.transform.SetParent(sliderGO.transform, false);
        RectTransform handleAreaRect = handleAreaGO.GetComponent<RectTransform>();
        handleAreaRect.anchorMin = Vector2.zero;
        handleAreaRect.anchorMax = Vector2.one;
        handleAreaRect.sizeDelta = new Vector2(-20f, 0f);

        GameObject handleGO = new GameObject("Handle", typeof(RectTransform), typeof(UIImage));
        handleGO.transform.SetParent(handleAreaGO.transform, false);
        RectTransform handleRect = handleGO.GetComponent<RectTransform>();
        handleRect.sizeDelta = new Vector2(18f, 26f);
        handleGO.GetComponent<UIImage>().color = Color.white;

        chargeSlider = sliderGO.GetComponent<Slider>();
        chargeSlider.fillRect = fillRect;
        chargeSlider.handleRect = handleRect;
        chargeSlider.targetGraphic = handleGO.GetComponent<UIImage>();
        chargeSlider.direction = Slider.Direction.LeftToRight;
        chargeSlider.minValue = 0.1f;
        chargeSlider.maxValue = maximumChargeNanoCoulombs;
        chargeSlider.value = initialChargeNanoCoulombs;
        chargeSlider.onValueChanged.AddListener(OnManualSliderChanged);
        collapsibleList.Add(sliderGO);

        GameObject simulationButtonGO = new GameObject("Simulate_Charges_Button");
        simulationButtonGO.transform.SetParent(parentWindow, false);
        GetOrAddComponent<UIImage>(simulationButtonGO).color = new Color(0.75f, 0.25f, 0.15f);

        UIButton simulationButton = GetOrAddComponent<UIButton>(simulationButtonGO);
        RectTransform simulationRect = simulationButtonGO.GetComponent<RectTransform>();
        simulationRect.anchorMin = new Vector2(0.5f, 0f);
        simulationRect.anchorMax = new Vector2(0.5f, 0f);
        simulationRect.pivot = new Vector2(0.5f, 0f);
        simulationRect.sizeDelta = new Vector2(240f, 34f);
        simulationRect.anchoredPosition = new Vector2(0f, 15f);

        simulationButtonText = CreateText("Text", simulationButtonGO.transform, "Simulate Charges", 14, Color.white);
        simulationButton.onClick.AddListener(ToggleSimulation);
        collapsibleList.Add(simulationButtonGO);

        UpdateChargeText(initialChargeNanoCoulombs);
    }

    private void OnChargeChanged(float nanoCoulombs)
    {
        UpdateChargeText(nanoCoulombs);
        if (foilSimulator != null)
        {
            foilSimulator.SetChargeNanoCoulombs(nanoCoulombs);
            foilSimulator.isSimulating = true;
        }
    }

    private void UpdateChargeText(float nanoCoulombs)
    {
        if (chargeValueText != null)
            chargeValueText.text = nanoCoulombs.ToString("F2", CultureInfo.InvariantCulture) + " nC";
    }

    private void ToggleSimulation()
    {
        if (foilSimulator == null) return;
        foilSimulator.ToggleSimulation();
        if (simulationButtonText != null)
            simulationButtonText.text = foilSimulator.IsSimulating ? "Stop Simulation" : "Simulate Charges";
    }

    private Transform FindChildRecursive(Transform parent, string childName)
    {
        foreach (Transform child in parent)
        {
            if (child.name == childName) return child;
            Transform result = FindChildRecursive(child, childName);
            if (result != null) return result;
        }
        return null;
    }

    private UIText CreateText(string objectName, Transform parent, string text, int fontSize, Color color)
    {
        GameObject go = new GameObject(objectName);
        go.transform.SetParent(parent, false);
        UIText uiText = go.AddComponent<UIText>();
        uiText.text = text;
        uiText.font = GetBuiltinFont();
        uiText.fontSize = fontSize;
        uiText.color = color;
        uiText.alignment = TextAnchor.MiddleCenter;
        uiText.raycastTarget = false;
        return uiText;
    }
}

// ============================================================================
// 4. SIMULADOR FÍSICO BEM (SOLVER MATRICIAL CON CANCELACIÓN Y PRUEBAS INSPECTOR)
// ============================================================================

public class FoilRepulsionSlideSimulator : MonoBehaviour
{
    [Header("BEM MESH CONFIGURATION")]
    public int bemLengthPanels = 16;
    public int bemWidthPanels = 4;

    [Header("BEM VISUAL GRID SETTINGS")]
    public bool showBEMGridGizmos = true;
    public Color bemGridColor = Color.cyan;
    public Color bemDisabledColor = new Color(0.3f, 0.05f, 0.05f, 1f);

    [Header("PROPERTIES DEDUCED FROM BUILDER")]
    public string detectedMaterialName = "";
    public double leafLengthMeters = 0.08;
    public double leafWidthMeters = 0.02;
    public double leafThicknessMeters = 0.00001;
    public double leafMassKg = 0.0000432;
    public double hingeSeparationMeters = 0.005;
    public double chargeNanoCoulombs = 20.0;
    public double gravityMps2 = 9.80665;
    public double wireArcLengthMeters = 0.5;
    public float leafPlacementT = 0.5f;

    [Header("CANCELATION RANGE (1-INDEXED)")]
    public int cancelPanelStart = 0;
    public int cancelPanelCount = 0;

    [Header("AERODYNAMICS & DAMPING")]
    public double airDensityKgPerM3 = 1.225;
    public double dragCoefficientCd = 1.28;
    public double hingeFrictionCoeff = 0.00002;

    [Header("DYNAMIC & STATIC STATE")]
    public double currentThetaRad = 0.0;
    public double currentOmegaRadS = 0.0;
    public bool isSimulating = false;

    public const double Epsilon0 = 8.8541878128e-12;
    public const double CoulombConstant = 1.0 / (4.0 * Math.PI * Epsilon0);

    private const int LUT_STEPS = 85;
    private double[] unitTorqueLUT = new double[LUT_STEPS + 1];
    private double[] unitCapacitanceLUT = new double[LUT_STEPS + 1];
    private bool isLUTCached = false;

    private Transform foil1;
    private Transform foil2;
    private GameObject signalPanel;
    private UIText physicsSignalText;
    private Material lineMaterial;
    private BEMCanvasGridGraphic bemCanvasGraphic;
    private UIText bemComputationText;
    private double[] lastPanelCharges = new double[0];
    private double[] lastPanelChargeWeights = new double[0];
    private double[] lastPanelTorqueWeights = new double[0];
    private double lastBEMResidual = 0.0;
    private double lastBEMVoltage = 0.0;
    private double lastBEMUnitChargeSum = 0.0;
    private double lastBEMTotalAbsCharge = 0.0;
    private double lastBEMMaxMatrixCoefficient = 0.0;
    private double lastBEMPanelArea = 0.0;
    private float nextBEMVisualizationTime = 0f;

    public bool IsSimulating => isSimulating;

    public void Initialize(MathCurve curve, Transform newFoil1, Transform newFoil2, float placementT, double wireArcLength, string jsonContent, string targetMaterial)
    {
        Debug.Log($"<color=lightblue>[BEM-INIT]</color> FoilRepulsionSlideSimulator.Initialize() | Material={targetMaterial}, WireLength={wireArcLength:F3}m");

        foil1 = newFoil1;
        foil2 = newFoil2;
        leafPlacementT = placementT;
        wireArcLengthMeters = Math.Max(0.05, wireArcLength);

        if (foil1 != null) foil1.gameObject.SetActive(true);
        if (foil2 != null) foil2.gameObject.SetActive(true);

        leafLengthMeters = 0.08;
        leafWidthMeters = 0.02;
        double leafAreaM2 = leafLengthMeters * leafWidthMeters;

        DynamicJsonMaterialReader.ResolvedMaterial matProfile =
            DynamicJsonMaterialReader.ResolveMaterial(jsonContent, targetMaterial, leafAreaM2, leafThicknessMeters);

        detectedMaterialName = matProfile.materialName;
        leafThicknessMeters = Math.Max(1e-7, matProfile.thicknessMeters);
        leafMassKg = Math.Max(1e-7, matProfile.massKg);

        BuildBEMLookupTable();

        isSimulating = true;
        CreateOrRefreshSignalUI();
        ApplyState();
    }

    // ============================================================================
    // MÉTODOS DE PRUEBA INSPECTOR (CONTEXT MENU API)
    // ============================================================================

    [ContextMenu("BEM Test: Cancel Range (1..16) & Log Recalculation")]
    public void TestCancelRange1To16()
    {
        TestCancelPanelsAndLogRecalculation(1, 16);
    }

    [ContextMenu("BEM Test: Restore All Panels")]
    public void TestRestoreAllPanels()
    {
        TestCancelPanelsAndLogRecalculation(0, 0);
    }

    public void TestCancelPanelsAndLogRecalculation(int startOneBased, int count)
    {
        double oldTheta = currentThetaRad > 0 ? currentThetaRad : (12.02 * Math.PI / 180.0);

        // 1. Resolver estado base
        SetDisabledPanelRange(0, 0);
        BemResult baseResult = SolveBEMDirect(oldTheta);
        double baseCap = baseResult.capacitanceFarads;
        double baseTorque = Math.Abs(baseResult.torqueDirectVector.z);

        // 2. Aplicar cancelación y resolver nuevamente
        SetDisabledPanelRange(startOneBased, count);
        BemResult newResult = SolveBEMDirect(oldTheta);
        double newCap = newResult.capacitanceFarads;
        double newTorque = Math.Abs(newResult.torqueDirectVector.z);

        // 3. Imprimir reporte de inspección de matriz y pesos
        System.Text.StringBuilder sb = new System.Text.StringBuilder();
        sb.AppendLine($"<color=yellow>═══ RECALCULO BEM [Rango {startOneBased}..{startOneBased + count - 1}] ═══</color>");
        sb.AppendLine($"• Estado: {(count > 0 ? $"{count} Paneles Desactivados (0 nC)" : "Todos Activos")}");
        sb.AppendLine($"• Capacitancia BEM: Base = {baseCap:E4} F ➔ Recalculada = {newCap:E4} F (Δ = {((newCap - baseCap) / baseCap * 100.0):F2}%)");
        sb.AppendLine($"• Torque Directo: Base = {baseTorque:E4} N·m ➔ Recalculado = {newTorque:E4} N·m (Δ = {((newTorque - baseTorque) / baseTorque * 100.0):F2}%)");
        sb.AppendLine($"• Voltaje del Sistema V: {lastBEMVoltage:F2} V | Residuo Máximo Matriz: {lastBEMResidual:E3}");
        sb.AppendLine($"── MUESTRA DE PESOS RECONSTRUIDOS DE CADA PANEL (Weight Q %) ──");

        int totalPanels = bemLengthPanels * bemWidthPanels * 2;
        for (int i = 0; i < Math.Min(totalPanels, 32); i++)
        {
            bool disabled = IsPanelDisabled(i);
            double qW = i < lastPanelChargeWeights.Length ? lastPanelChargeWeights[i] * 100.0 : 0.0;
            double tW = i < lastPanelTorqueWeights.Length ? lastPanelTorqueWeights[i] * 100.0 : 0.0;
            string status = disabled ? "<color=red>[CANCELADO 0.0%]</color>" : "<color=green>[ACTIVO]</color>";
            sb.AppendLine($"  Panel #{i + 1:D2} {status} ➔ Peso Carga = {qW:F2}% | Peso Torque = {tW:F2}%");
        }

        Debug.Log(sb.ToString());
    }

    public void SetDisabledPanelRange(int startOneBased, int count)
    {
        cancelPanelStart = Mathf.Max(0, startOneBased);
        cancelPanelCount = Mathf.Max(0, count);
        isLUTCached = false;
        nextBEMVisualizationTime = 0f; // Fuerza el repintado inmediato de las celdas "X" en UI
        Debug.Log($"<color=lightblue>[BEM-CANCEL]</color> Paneles desactivados: Rango [{cancelPanelStart} .. {cancelPanelStart + cancelPanelCount - 1}] (Total: {cancelPanelCount})");
        UpdateSignalUI();
    }

    public bool IsPanelDisabled(int panelZeroBasedIndex)
    {
        if (cancelPanelStart <= 0 || cancelPanelCount <= 0) return false;
        int panelNumber = panelZeroBasedIndex + 1;
        return panelNumber >= cancelPanelStart && panelNumber < cancelPanelStart + cancelPanelCount;
    }

    public void SetChargeNanoCoulombs(float nC)
    {
        chargeNanoCoulombs = Math.Max(0.01, nC);
        Debug.Log($"<color=lightblue>[BEM-PHYSICS]</color> Carga seteada a {chargeNanoCoulombs:F2} nC");
        UpdateSignalUI();
    }

    public void RefreshFoilPosition(MathCurve activeCurve = null, Transform newFoil1 = null, Transform newFoil2 = null)
    {
        if (newFoil1 != null) foil1 = newFoil1;
        if (newFoil2 != null) foil2 = newFoil2;

        if (foil1 != null) foil1.gameObject.SetActive(true);
        if (foil2 != null) foil2.gameObject.SetActive(true);

        if (activeCurve != null)
        {
            wireArcLengthMeters = Math.Max(0.05, activeCurve.CalculateArcLength(100));
        }
        ApplyState();
        UpdateSignalUI();
    }

    public void ToggleSimulation()
    {
        isSimulating = !isSimulating;
        if (!isSimulating)
        {
            currentThetaRad = 0.0;
            currentOmegaRadS = 0.0;
            ApplyState();
            UpdateSignalUI();
        }
    }

    private void Update()
    {
        if (signalPanel == null && GameObject.Find("Procedural_Curve_Canvas") != null)
        {
            CreateOrRefreshSignalUI();
        }

        if (isSimulating)
        {
            StepPhysicsAerodynamic(Time.deltaTime);
            ApplyState();
            UpdateSignalUI();
        }
    }

    // ============================================================================
    // DIBUJADO DE GRILLAS BEM DIRECTO EN GAME VIEW (IDENTIFICANDO DESACTIVADOS)
    // ============================================================================
    private void CreateLineMaterial()
    {
        if (!lineMaterial)
        {
            Shader shader = Shader.Find("Hidden/Internal-Colored");
            lineMaterial = new Material(shader);
            lineMaterial.hideFlags = HideFlags.HideAndDontSave;
            lineMaterial.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            lineMaterial.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            lineMaterial.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
            lineMaterial.SetInt("_ZWrite", 0);
        }
    }

    private void OnRenderObject()
    {
        if (!showBEMGridGizmos || !Application.isPlaying) return;

        CreateLineMaterial();
        lineMaterial.SetPass(0);

        GL.PushMatrix();
        GL.Begin(GL.LINES);

        List<BemPanel> panels = BuildPanels(currentThetaRad, bemLengthPanels, bemWidthPanels);

        for (int i = 0; i < panels.Count; i++)
        {
            var p = panels[i];
            bool disabled = IsPanelDisabled(i);

            Vector3 corner1 = p.center - p.axisU * (float)p.halfU - p.axisV * (float)p.halfV;
            Vector3 corner2 = p.center + p.axisU * (float)p.halfU - p.axisV * (float)p.halfV;
            Vector3 corner3 = p.center + p.axisU * (float)p.halfU + p.axisV * (float)p.halfV;
            Vector3 corner4 = p.center - p.axisU * (float)p.halfU + p.axisV * (float)p.halfV;

            GL.Color(disabled ? bemDisabledColor : bemGridColor);
            GL.Vertex(corner1); GL.Vertex(corner2);
            GL.Vertex(corner2); GL.Vertex(corner3);
            GL.Vertex(corner3); GL.Vertex(corner4);
            GL.Vertex(corner4); GL.Vertex(corner1);

            if (!disabled)
            {
                GL.Color(Color.yellow);
                GL.Vertex(p.center);
                GL.Vertex(p.center + p.normal * 0.003f);
            }
        }

        GL.End();
        GL.PopMatrix();
    }

    private void OnDrawGizmos()
    {
        if (!showBEMGridGizmos || !Application.isPlaying) return;

        List<BemPanel> panels = BuildPanels(currentThetaRad, bemLengthPanels, bemWidthPanels);

        for (int i = 0; i < panels.Count; i++)
        {
            var p = panels[i];
            bool disabled = IsPanelDisabled(i);

            Vector3 corner1 = p.center - p.axisU * (float)p.halfU - p.axisV * (float)p.halfV;
            Vector3 corner2 = p.center + p.axisU * (float)p.halfU - p.axisV * (float)p.halfV;
            Vector3 corner3 = p.center + p.axisU * (float)p.halfU + p.axisV * (float)p.halfV;
            Vector3 corner4 = p.center - p.axisU * (float)p.halfU + p.axisV * (float)p.halfV;

            Gizmos.color = disabled ? bemDisabledColor : bemGridColor;
            Gizmos.DrawLine(corner1, corner2);
            Gizmos.DrawLine(corner2, corner3);
            Gizmos.DrawLine(corner3, corner4);
            Gizmos.DrawLine(corner4, corner1);

            if (!disabled)
            {
                Gizmos.color = Color.yellow;
                Gizmos.DrawRay(p.center, p.normal * 0.003f);
            }
        }
    }

    public double CalculateWireCapacitance(double lengthMeters, double radiusMeters = 0.0005)
    {
        if (lengthMeters <= 0 || radiusMeters <= 0) return 0.0;
        double logTerm = Math.Log((2.0 * lengthMeters) / radiusMeters) - 1.0;
        return (2.0 * Math.PI * Epsilon0 * lengthMeters) / Math.Max(0.1, logTerm);
    }

    private void BuildBEMLookupTable()
    {
        double oldCharge = chargeNanoCoulombs;
        chargeNanoCoulombs = 1.0;

        for (int i = 1; i <= LUT_STEPS; i++)
        {
            double theta = i * (Math.PI / 180.0);
            BemResult bem = SolveBEMDirect(theta);
            unitTorqueLUT[i] = Math.Abs(bem.torqueDirectVector.z);
            unitCapacitanceLUT[i] = bem.capacitanceFarads;
        }
        unitTorqueLUT[0] = unitTorqueLUT[1];
        unitCapacitanceLUT[0] = unitCapacitanceLUT[1];

        chargeNanoCoulombs = oldCharge;
        isLUTCached = true;
    }

    private double GetInterpolatedUnitTorque(double thetaRad)
    {
        if (!isLUTCached) BuildBEMLookupTable();
        double deg = Math.Clamp(thetaRad * (180.0 / Math.PI), 0.1, 85.0);
        int idx = (int)Math.Floor(deg);
        double frac = deg - idx;
        if (idx >= LUT_STEPS) return unitTorqueLUT[LUT_STEPS];
        return (1.0 - frac) * unitTorqueLUT[idx] + frac * unitTorqueLUT[idx + 1];
    }

    private double GetInterpolatedCapacitance(double thetaRad)
    {
        if (!isLUTCached) BuildBEMLookupTable();
        double deg = Math.Clamp(thetaRad * (180.0 / Math.PI), 0.1, 85.0);
        int idx = (int)Math.Floor(deg);
        double frac = deg - idx;
        if (idx >= LUT_STEPS) return unitCapacitanceLUT[LUT_STEPS];
        return (1.0 - frac) * unitCapacitanceLUT[idx] + frac * unitCapacitanceLUT[idx + 1];
    }

    private void StepPhysicsAerodynamic(float dt)
    {
        if (dt <= 0) return;
        int substeps = 20;
        double subDt = Math.Min(dt, 0.033) / substeps;

        double safeMass = Math.Max(1e-7, leafMassKg);
        double safeLength = Math.Max(0.01, leafLengthMeters);
        double I_inertia = Math.Max(1e-12, (1.0 / 3.0) * safeMass * (safeLength * safeLength));
        double k_drag = 0.125 * airDensityKgPerM3 * dragCoefficientCd * leafWidthMeters * Math.Pow(safeLength, 4);

        for (int step = 0; step < substeps; step++)
        {
            double evalTheta = Math.Clamp(currentThetaRad, 0.0017, 85.0 * Math.PI / 180.0);

            double C_bem = GetInterpolatedCapacitance(evalTheta);
            double C_wire = CalculateWireCapacitance(wireArcLengthMeters, 0.0005);
            double C_total = C_bem + C_wire;

            double Q_total = chargeNanoCoulombs * 1e-9;
            double Q_leaves = Q_total * (C_bem / Math.Max(1e-25, C_total));
            double nC_leaves = Q_leaves * 1e9;

            double tauCoulomb = (nC_leaves * nC_leaves) * GetInterpolatedUnitTorque(evalTheta);
            double tauGravity = 0.5 * safeMass * gravityMps2 * safeLength * Math.Sin(currentThetaRad);
            double tauAirDrag = k_drag * currentOmegaRadS * Math.Abs(currentOmegaRadS);
            double tauHingeFriction = hingeFrictionCoeff * currentOmegaRadS;

            double tauNet = tauCoulomb - tauGravity - tauAirDrag - tauHingeFriction;
            double alpha = tauNet / I_inertia;

            currentOmegaRadS += alpha * subDt;
            currentThetaRad += currentOmegaRadS * subDt;
        }

        if (double.IsNaN(currentThetaRad) || double.IsInfinity(currentThetaRad)) currentThetaRad = 0.0;
        if (double.IsNaN(currentOmegaRadS) || double.IsInfinity(currentOmegaRadS)) currentOmegaRadS = 0.0;

        if (currentThetaRad < 0.0)
        {
            currentThetaRad = 0.0;
            currentOmegaRadS = -currentOmegaRadS * 0.15;
        }
    }

    private void ApplyState()
    {
        if (foil1 == null || foil2 == null) return;

        if (!foil1.gameObject.activeSelf) foil1.gameObject.SetActive(true);
        if (!foil2.gameObject.activeSelf) foil2.gameObject.SetActive(true);

        float angleDeg = Mathf.Clamp((float)(currentThetaRad * 180.0 / Math.PI), 0f, 85f);
        foil1.localRotation = Quaternion.Euler(0f, 0f, -angleDeg);
        foil2.localRotation = Quaternion.Euler(0f, 0f, angleDeg);
    }

    public struct BemPanel
    {
        public Vector3 center;
        public Vector3 normal;
        public Vector3 axisU;
        public Vector3 axisV;
        public double halfU;
        public double halfV;
        public double area;
    }

    public struct BemResult
    {
        public double capacitanceFarads;
        public Vector3 torqueDirectVector;
    }

    public BemResult SolveBEMDirect(double thetaRad)
    {
        List<BemPanel> panels = BuildPanels(thetaRad, bemLengthPanels, bemWidthPanels);
        int N = panels.Count;
        double[,] A = new double[N, N];
        double[] b = new double[N];

        lastBEMMaxMatrixCoefficient = 0.0;

        for (int i = 0; i < N; i++)
        {
            if (IsPanelDisabled(i))
            {
                b[i] = 0.0;
                for (int j = 0; j < N; j++)
                {
                    A[i, j] = (i == j) ? 1.0 : 0.0;
                }
            }
            else
            {
                b[i] = 1.0;
                for (int j = 0; j < N; j++)
                {
                    if (IsPanelDisabled(j))
                    {
                        A[i, j] = 0.0;
                    }
                    else
                    {
                        A[i, j] = CoulombConstant * CalculateInfluence(panels[i], panels[j], i == j);
                    }
                    lastBEMMaxMatrixCoefficient = Math.Max(lastBEMMaxMatrixCoefficient, Math.Abs(A[i, j]));
                }
            }
        }

        double[] unitCharges = SolveLinearSystem(A, b, N);
        double sumUnitCharge = 0.0;
        for (int i = 0; i < N; i++) sumUnitCharge += unitCharges[i];
        lastBEMUnitChargeSum = sumUnitCharge;
        lastBEMPanelArea = N > 0 ? panels[0].area : 0.0;

        lastBEMResidual = 0.0;
        for (int i = 0; i < N; i++)
        {
            double rowValue = 0.0;
            for (int j = 0; j < N; j++) rowValue += A[i, j] * unitCharges[j];
            lastBEMResidual = Math.Max(lastBEMResidual, Math.Abs(rowValue - b[i]));
        }

        double Q = chargeNanoCoulombs * 1e-9;
        double C = sumUnitCharge;
        double V = Q / Math.Max(1e-25, C);
        lastBEMVoltage = V;

        double[] actualCharges = new double[N];
        double totalAbsCharge = 0.0;
        for (int i = 0; i < N; i++)
        {
            actualCharges[i] = IsPanelDisabled(i) ? 0.0 : unitCharges[i] * V;
            totalAbsCharge += Math.Abs(actualCharges[i]);
        }
        lastPanelCharges = actualCharges;
        lastBEMTotalAbsCharge = totalAbsCharge;

        double[] chargeWeights = new double[N];
        for (int i = 0; i < N; i++)
            chargeWeights[i] = Math.Abs(actualCharges[i]) / Math.Max(1e-30, totalAbsCharge);
        lastPanelChargeWeights = chargeWeights;

        Vector3 totalTorqueLeaf0 = Vector3.zero;
        Vector3 hingePosLeaf0 = new Vector3((float)(-0.5 * hingeSeparationMeters), 0f, 0f);
        int halfN = N / 2;
        double[] rawTorqueWeights = new double[N];

        for (int i = 0; i < halfN; i++)
        {
            if (IsPanelDisabled(i)) continue;
            Vector3 p_i = panels[i].center;

            for (int j = halfN; j < N; j++)
            {
                if (IsPanelDisabled(j)) continue;
                Vector3 p_j = panels[j].center;
                Vector3 r_vec = p_i - p_j;
                float r_mag = r_vec.magnitude;
                if (r_mag <= 1e-6f) continue;

                Vector3 pairField = (float)(CoulombConstant * actualCharges[j]) *
                    (r_vec / (r_mag * r_mag * r_mag));
                Vector3 pairForce = (float)actualCharges[i] * pairField;
                Vector3 pairTorque = Vector3.Cross(p_i - hingePosLeaf0, pairForce);
                totalTorqueLeaf0 += pairTorque;

                double pairMagnitude = Math.Abs(pairTorque.z);
                rawTorqueWeights[i] += pairMagnitude * 0.5;
                rawTorqueWeights[j] += pairMagnitude * 0.5;
            }
        }

        double totalRawTorqueWeight = 0.0;
        for (int i = 0; i < N; i++) totalRawTorqueWeight += rawTorqueWeights[i];
        double[] torqueWeights = new double[N];
        for (int i = 0; i < N; i++)
            torqueWeights[i] = rawTorqueWeights[i] / Math.Max(1e-30, totalRawTorqueWeight);
        lastPanelTorqueWeights = torqueWeights;

        return new BemResult
        {
            capacitanceFarads = C,
            torqueDirectVector = totalTorqueLeaf0
        };
    }

    private List<BemPanel> BuildPanels(double thetaRad, int pLength, int pWidth)
    {
        List<BemPanel> panels = new List<BemPanel>();
        double du = leafLengthMeters / pLength;
        double dv = leafWidthMeters / pWidth;

        for (int leaf = 0; leaf < 2; leaf++)
        {
            double sign = (leaf == 0) ? -1.0 : 1.0;
            Vector3 hinge = new Vector3((float)(sign * 0.5 * hingeSeparationMeters), 0f, 0f);
            Vector3 axisU = new Vector3((float)(sign * Math.Sin(thetaRad)), (float)(-Math.Cos(thetaRad)), 0f);
            Vector3 axisV = Vector3.forward;
            Vector3 normal = Vector3.Cross(axisU, axisV).normalized * (float)(-sign);

            for (int iu = 0; iu < pLength; iu++)
            {
                double s = (iu + 0.5) * du;
                for (int iv = 0; iv < pWidth; iv++)
                {
                    double v = (iv + 0.5) * dv - 0.5 * leafWidthMeters;
                    panels.Add(new BemPanel
                    {
                        center = hinge + axisU * (float)s + axisV * (float)v,
                        normal = normal,
                        axisU = axisU,
                        axisV = axisV,
                        halfU = 0.5 * du,
                        halfV = 0.5 * dv,
                        area = du * dv
                    });
                }
            }
        }
        return panels;
    }

    private double CalculateInfluence(BemPanel rec, BemPanel src, bool isSelf)
    {
        if (isSelf)
        {
            double u = src.halfU, v = src.halfV;
            double asinhV = Math.Log(v / u + Math.Sqrt((v / u) * (v / u) + 1.0));
            double asinhU = Math.Log(u / v + Math.Sqrt((u / v) * (u / v) + 1.0));
            return (4.0 * (u * asinhV + v * asinhU)) / (4.0 * u * v);
        }

        double dist = Vector3.Distance(rec.center, src.center);
        if (dist < 2.5 * (src.halfU + src.halfV))
        {
            double sumNear = 0.0;
            int Nsub = 2;
            double subU = src.halfU / Nsub, subV = src.halfV / Nsub;

            for (int iu = 0; iu < Nsub; iu++)
            {
                double uC = -src.halfU + (2 * iu + 1) * subU;
                for (int iv = 0; iv < Nsub; iv++)
                {
                    double vC = -src.halfV + (2 * iv + 1) * subV;
                    Vector3 subCenter = src.center + src.axisU * (float)uC + src.axisV * (float)vC;
                    sumNear += (subU * subV * 4.0) / Math.Max(Vector3.Distance(rec.center, subCenter), 1e-12);
                }
            }
            return sumNear / Math.Max(src.area, 1e-14);
        }

        return src.area / Math.Max(dist, 1e-12);
    }

    private double[] SolveLinearSystem(double[,] A, double[] b, int n)
    {
        double[,] M = (double[,])A.Clone();
        double[] x = (double[])b.Clone();

        for (int k = 0; k < n; k++)
        {
            int pivot = k;
            double maxVal = Math.Abs(M[k, k]);
            for (int i = k + 1; i < n; i++)
            {
                if (Math.Abs(M[i, k]) > maxVal) { maxVal = Math.Abs(M[i, k]); pivot = i; }
            }
            if (maxVal < 1e-15) return new double[n];

            if (pivot != k)
            {
                for (int j = k; j < n; j++) { double tmpM = M[k, j]; M[k, j] = M[pivot, j]; M[pivot, j] = tmpM; }
                double tmpX = x[k]; x[k] = x[pivot]; x[pivot] = tmpX;
            }

            for (int i = k + 1; i < n; i++)
            {
                double factor = M[i, k] / M[k, k];
                for (int j = k + 1; j < n; j++) M[i, j] -= factor * M[k, j];
                x[i] -= factor * x[k];
            }
        }

        for (int i = n - 1; i >= 0; i--)
        {
            double sum = x[i];
            for (int j = i + 1; j < n; j++) sum -= M[i, j] * x[j];
            x[i] = sum / M[i, i];
        }
        return x;
    }

    public void CreateOrRefreshSignalUI()
    {
        GameObject canvasGO = GameObject.Find("Procedural_Curve_Canvas");
        if (canvasGO == null) return;

        Transform oldPanel = canvasGO.transform.Find("Repulsion_Signal_Panel");
        if (oldPanel != null)
        {
            if (Application.isPlaying) Destroy(oldPanel.gameObject);
            else DestroyImmediate(oldPanel.gameObject);
        }

        signalPanel = new GameObject("Repulsion_Signal_Panel", typeof(RectTransform), typeof(CanvasRenderer), typeof(UIImage));
        signalPanel.transform.SetParent(canvasGO.transform, false);
        signalPanel.GetComponent<UIImage>().color = new Color(0.025f, 0.035f, 0.055f, 0.985f);

        RectTransform panelRect = signalPanel.GetComponent<RectTransform>();
        panelRect.anchorMin = new Vector2(1f, 0f);
        panelRect.anchorMax = new Vector2(1f, 0f);
        panelRect.pivot = new Vector2(1f, 0f);
        panelRect.sizeDelta = new Vector2(820f, 610f);
        panelRect.anchoredPosition = new Vector2(-20f, 20f);

        UIText titleText = CreateUIText("Title", signalPanel.transform,
            "ELECTROSCOPE | LEAF PHYSICS + BOUNDARY ELEMENT METHOD", 15, Color.white);
        titleText.fontStyle = FontStyle.Bold;
        SetTopTextRect(titleText, -8f, 25f);

        GameObject gridGO = new GameObject("BEM_Canvas_Grid", typeof(RectTransform), typeof(CanvasRenderer));
        gridGO.transform.SetParent(signalPanel.transform, false);
        RectTransform gridRect = gridGO.GetComponent<RectTransform>();
        gridRect.anchorMin = new Vector2(0.5f, 1f);
        gridRect.anchorMax = new Vector2(0.5f, 1f);
        gridRect.pivot = new Vector2(0.5f, 1f);
        gridRect.sizeDelta = new Vector2(780f, 190f);
        gridRect.anchoredPosition = new Vector2(0f, -42f);
        bemCanvasGraphic = gridGO.AddComponent<BEMCanvasGridGraphic>();
        bemCanvasGraphic.raycastTarget = false;
        bemCanvasGraphic.simulator = this;
        bemCanvasGraphic.SetGrid(bemLengthPanels, bemWidthPanels,
            lastPanelCharges, lastPanelChargeWeights, lastPanelTorqueWeights);

        UIText gridCaption = CreateUIText("Grid_Caption", signalPanel.transform,
            "PANEL MAP  |  TEXT = CHARGE WEIGHT (%)  |  CELL INTENSITY = TORQUE WEIGHT (%)  |  LEFT / RIGHT LEAF",
            9, new Color(0.55f, 0.9f, 1f));
        SetTopTextRect(gridCaption, -237f, 18f);

        physicsSignalText = CreateUIText("Leaf_Physics", signalPanel.transform, "", 10, Color.white);
        physicsSignalText.alignment = TextAnchor.UpperLeft;
        SetTopTextRect(physicsSignalText, -260f, 160f);

        bemComputationText = CreateUIText("BEM_Computation", signalPanel.transform, "", 10,
            new Color(0.65f, 0.92f, 0.76f));
        bemComputationText.alignment = TextAnchor.UpperLeft;
        SetTopTextRect(bemComputationText, -425f, 165f);

        UIDraggable draggable = signalPanel.GetComponent<UIDraggable>();
        if (draggable == null) draggable = signalPanel.AddComponent<UIDraggable>();
        draggable.isWindow = true;

        UpdateSignalUI();
    }

    private void SetTopTextRect(UIText text, float y, float height)
    {
        RectTransform rect = text.GetComponent<RectTransform>();
        rect.anchorMin = new Vector2(0f, 1f);
        rect.anchorMax = new Vector2(1f, 1f);
        rect.pivot = new Vector2(0.5f, 1f);
        rect.sizeDelta = new Vector2(-20f, height);
        rect.anchoredPosition = new Vector2(0f, y);
    }

    private UIText CreateUIText(string objectName, Transform parent, string text, int fontSize, Color color)
    {
        GameObject go = new GameObject(objectName);
        go.transform.SetParent(parent, false);
        UIText textComponent = go.AddComponent<UIText>();
        textComponent.text = text;
        textComponent.font = GetBuiltinFont();
        textComponent.fontSize = fontSize;
        textComponent.color = color;
        textComponent.alignment = TextAnchor.MiddleCenter;
        textComponent.raycastTarget = false;
        return textComponent;
    }

    private Font GetBuiltinFont()
    {
        Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        if (font == null) font = Resources.GetBuiltinResource<Font>("Arial.ttf");
        if (font == null) font = Font.CreateDynamicFontFromOSFont("Arial", 14);
        return font;
    }

    private void UpdateSignalUI()
    {
        if (physicsSignalText == null) return;

        double thetaDeg = currentThetaRad * 180.0 / Math.PI;
        double evalTheta = Math.Max(0.0017, currentThetaRad);
        double C_bem = GetInterpolatedCapacitance(evalTheta);
        double C_wire = CalculateWireCapacitance(wireArcLengthMeters, 0.0005);
        double C_total = C_bem + C_wire;
        double Q_total = chargeNanoCoulombs * 1e-9;
        double Q_leaves = Q_total * (C_bem / Math.Max(1e-25, C_total));
        double nC_leaves = Q_leaves * 1e9;
        double V = Q_total / Math.Max(1e-25, C_total);
        double tauCoulomb = (nC_leaves * nC_leaves) * GetInterpolatedUnitTorque(evalTheta);
        double leafVolume = Math.Max(1e-30, leafLengthMeters * leafWidthMeters * leafThicknessMeters);
        double density = leafMassKg / leafVolume;
        double leafArea = leafLengthMeters * leafWidthMeters;
        double panelArea = leafArea / Math.Max(1, bemLengthPanels * bemWidthPanels);
        double inertia = (1.0 / 3.0) * leafMassKg * leafLengthMeters * leafLengthMeters;
        double gravityTorque = 0.5 * leafMassKg * gravityMps2 * leafLengthMeters * Math.Sin(currentThetaRad);
        double dragTorque = 0.125 * airDensityKgPerM3 * dragCoefficientCd * leafWidthMeters *
            Math.Pow(leafLengthMeters, 4) * currentOmegaRadS * Math.Abs(currentOmegaRadS);
        double frictionTorque = hingeFrictionCoeff * currentOmegaRadS;

        string cancelStatus = cancelPanelCount > 0
            ? $"DISABLED RANGE: [{cancelPanelStart} .. {cancelPanelStart + cancelPanelCount - 1}] ({cancelPanelCount} panels)"
            : "ALL PANELS ACTIVE";

        physicsSignalText.text = FormattableString.Invariant($"LEAF PROPERTIES (each leaf) | {cancelStatus}\nMaterial: {(string.IsNullOrEmpty(detectedMaterialName) ? "Unknown" : detectedMaterialName)} | Length: {leafLengthMeters * 1000.0:F2} mm | Width: {leafWidthMeters * 1000.0:F2} mm | Thickness: {leafThicknessMeters * 1e6:F2} µm\nMass: {leafMassKg * 1e6:F3} mg | Derived density: {density:F1} kg/m³ | Surface: {leafArea * 1e6:F2} mm² | Volume: {leafVolume:E3} m³\nHinge gap: {hingeSeparationMeters * 1000.0:F2} mm | Leaf placement t: {leafPlacementT:F3} | θ: {thetaDeg:F2}° each | Total aperture: {2.0 * thetaDeg:F2}°\nCHARGE / CIRCUIT: Q input: {chargeNanoCoulombs:F3} nC | Q assigned to leaves: {nC_leaves:F3} nC | V: {V:E3} V\nC leaf-pair BEM: {C_bem:E3} F | C wire: {C_wire:E3} F | C equivalent total: {C_total:E3} F\nMECHANICS: I per leaf: {inertia:E3} kg·m² | τ electrostatic: {tauCoulomb:E3} N·m | τ gravity: {gravityTorque:E3} N·m\nτ air: {dragTorque:E3} N·m | τ hinge friction: {frictionTorque:E3} N·m | ω: {currentOmegaRadS:F4} rad/s | Simulation: {(isSimulating ? "RUNNING" : "PAUSED")}\nENVIRONMENT: g: {gravityMps2:F3} m/s² | air density: {airDensityKgPerM3:F3} kg/m³ | Cd: {dragCoefficientCd:F3} | hinge friction coeff: {hingeFrictionCoeff:E3} | wire arc: {wireArcLengthMeters:F3} m");

        if (Time.unscaledTime >= nextBEMVisualizationTime)
        {
            nextBEMVisualizationTime = Time.unscaledTime + 0.35f;
            BemResult liveResult = SolveBEMDirect(evalTheta);
            if (bemCanvasGraphic != null)
                bemCanvasGraphic.SetGrid(bemLengthPanels, bemWidthPanels,
                    lastPanelCharges, lastPanelChargeWeights, lastPanelTorqueWeights);

            if (bemComputationText != null)
            {
                int panelsPerLeaf = Mathf.Max(1, bemLengthPanels) * Mathf.Max(1, bemWidthPanels);
                int panelCount = panelsPerLeaf * 2;
                double maxChargeWeight = 0.0;
                double maxTorqueWeight = 0.0;
                for (int i = 0; i < lastPanelChargeWeights.Length; i++)
                    maxChargeWeight = Math.Max(maxChargeWeight, lastPanelChargeWeights[i]);
                for (int i = 0; i < lastPanelTorqueWeights.Length; i++)
                    maxTorqueWeight = Math.Max(maxTorqueWeight, lastPanelTorqueWeights[i]);

                bemComputationText.text = FormattableString.Invariant($"BEM DISCRETIZATION: {bemLengthPanels} × {bemWidthPanels} panels per leaf | {panelCount} panels total | ΔA per panel: {panelArea:E3} m²\nLINEAR SYSTEM: A q(unit) = 1 | A dimensions: {panelCount} × {panelCount} | max |Aᵢⱼ|: {lastBEMMaxMatrixCoefficient:E3} | max residual: {lastBEMResidual:E3}\nSOLUTION: Σ q(unit): {lastBEMUnitChargeSum:E3} | solved C: {liveResult.capacitanceFarads:E3} F | solved V: {lastBEMVoltage:E3} V | Σ |qᵢ|: {lastBEMTotalAbsCharge:E3} C\nFORCES: τ BEM direct: {liveResult.torqueDirectVector.z:E3} N·m | max panel charge weight: {maxChargeWeight * 100.0:F2}% | max panel torque weight: {maxTorqueWeight * 100.0:F2}%\nWEIGHT DEFINITION: Q weightᵢ = |qᵢ| / Σ|q|; torque weightᵢ = normalized absolute pair-torque share. Canceled boxes forced to qᵢ = 0.");
            }
        }
    }
}

// ============================================================================
// BEM CANVAS VISUALIZER (CON MARCADOR DE CELDAS ANULADAS EN LA INTERFAZ UI)
// ============================================================================
public class BEMCanvasGridGraphic : UnityEngine.UI.Graphic
{
    public FoilRepulsionSlideSimulator simulator;
    private int lengthPanels = 1;
    private int widthPanels = 1;
    private double[] panelCharges = new double[0];
    private double[] chargeWeights = new double[0];
    private double[] torqueWeights = new double[0];
    private readonly List<UIText> weightLabels = new List<UIText>();

    public void SetGrid(int length, int width, double[] charges,
        double[] perPanelChargeWeights, double[] perPanelTorqueWeights)
    {
        lengthPanels = Mathf.Clamp(length, 1, 64);
        widthPanels = Mathf.Clamp(width, 1, 32);
        panelCharges = charges != null ? (double[])charges.Clone() : new double[0];
        chargeWeights = perPanelChargeWeights != null ? (double[])perPanelChargeWeights.Clone() : new double[0];
        torqueWeights = perPanelTorqueWeights != null ? (double[])perPanelTorqueWeights.Clone() : new double[0];
        EnsureWeightLabels();
        UpdateWeightLabels();
        SetVerticesDirty();
    }

    private void EnsureWeightLabels()
    {
        int required = 2 * lengthPanels * widthPanels;
        while (weightLabels.Count < required)
        {
            GameObject labelGO = new GameObject("Panel_Weight_" + weightLabels.Count,
                typeof(RectTransform), typeof(CanvasRenderer));
            labelGO.transform.SetParent(transform, false);
            UIText label = labelGO.AddComponent<UIText>();
            label.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (label.font == null) label.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            label.fontSize = 7;
            label.alignment = TextAnchor.MiddleCenter;
            label.color = Color.white;
            label.raycastTarget = false;
            label.horizontalOverflow = HorizontalWrapMode.Overflow;
            label.verticalOverflow = VerticalWrapMode.Overflow;
            weightLabels.Add(label);
        }
        for (int i = 0; i < weightLabels.Count; i++)
            weightLabels[i].gameObject.SetActive(i < required);
    }

    private void UpdateWeightLabels()
    {
        Rect rect = GetPixelAdjustedRect();
        int perLeaf = lengthPanels * widthPanels;
        float outerGap = 12f;
        float leafGap = 24f;
        float leafWidth = (rect.width - outerGap * 2f - leafGap) * 0.5f;
        float leafHeight = rect.height - outerGap * 2f;
        float cellW = leafWidth / widthPanels;
        float cellH = leafHeight / lengthPanels;

        for (int leaf = 0; leaf < 2; leaf++)
        {
            float leafX = rect.xMin + outerGap + leaf * (leafWidth + leafGap);
            for (int iu = 0; iu < lengthPanels; iu++)
            {
                for (int iv = 0; iv < widthPanels; iv++)
                {
                    int index = leaf * perLeaf + iu * widthPanels + iv;
                    UIText label = weightLabels[index];
                    RectTransform rt = label.rectTransform;
                    rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
                    rt.pivot = new Vector2(0.5f, 0.5f);
                    rt.sizeDelta = new Vector2(cellW, cellH);
                    float x = leafX + (iv + 0.5f) * cellW;
                    float y = rect.yMax - outerGap - (iu + 0.5f) * cellH;
                    rt.anchoredPosition = new Vector2(x - rect.center.x, y - rect.center.y);

                    bool disabled = (simulator != null) && simulator.IsPanelDisabled(index);
                    if (disabled)
                    {
                        label.text = "X";
                        label.color = Color.red;
                    }
                    else
                    {
                        double qWeight = index < chargeWeights.Length ? chargeWeights[index] : 0.0;
                        label.text = (qWeight * 100.0).ToString("F1", CultureInfo.InvariantCulture);
                        label.color = Color.white;
                    }
                    label.fontSize = Mathf.Clamp(Mathf.RoundToInt(cellH * 0.62f), 6, 9);
                }
            }
        }
    }

    protected override void OnPopulateMesh(VertexHelper vh)
    {
        vh.Clear();
        Rect rect = GetPixelAdjustedRect();
        int perLeaf = lengthPanels * widthPanels;
        float outerGap = 12f;
        float leafGap = 24f;
        float leafWidth = (rect.width - outerGap * 2f - leafGap) * 0.5f;
        float leafHeight = rect.height - outerGap * 2f;
        float cellW = leafWidth / widthPanels;
        float cellH = leafHeight / lengthPanels;

        for (int leaf = 0; leaf < 2; leaf++)
        {
            float leafX = rect.xMin + outerGap + leaf * (leafWidth + leafGap);
            for (int iu = 0; iu < lengthPanels; iu++)
            {
                for (int iv = 0; iv < widthPanels; iv++)
                {
                    int index = leaf * perLeaf + iu * widthPanels + iv;
                    bool disabled = (simulator != null) && simulator.IsPanelDisabled(index);

                    Color cellColor;
                    if (disabled)
                    {
                        cellColor = new Color(0.15f, 0.05f, 0.05f, 1f);
                    }
                    else
                    {
                        double q = index < panelCharges.Length ? panelCharges[index] : 0.0;
                        double torqueWeight = index < torqueWeights.Length ? torqueWeights[index] : 0.0;
                        float torqueIntensity = Mathf.Clamp01((float)(torqueWeight * perLeaf * 2.0));
                        Color low = q >= 0.0
                            ? new Color(0.035f, 0.12f, 0.22f, 1f)
                            : new Color(0.22f, 0.07f, 0.10f, 1f);
                        Color high = q >= 0.0
                            ? new Color(0.05f, 0.85f, 1f, 1f)
                            : new Color(1f, 0.35f, 0.28f, 1f);
                        cellColor = Color.Lerp(low, high, torqueIntensity);
                    }

                    float x0 = leafX + iv * cellW;
                    float x1 = x0 + cellW - 1f;
                    float y1 = rect.yMax - outerGap - iu * cellH;
                    float y0 = y1 - cellH + 1f;
                    AddQuad(vh, new Vector2(x0, y0), new Vector2(x0, y1),
                        new Vector2(x1, y1), new Vector2(x1, y0), cellColor);
                }
            }
        }
    }

    private void AddQuad(VertexHelper vh, Vector2 a, Vector2 b, Vector2 c, Vector2 d, Color color)
    {
        int start = vh.currentVertCount;
        UIVertex v = UIVertex.simpleVert;
        v.color = color;
        v.position = a; vh.AddVert(v);
        v.position = b; vh.AddVert(v);
        v.position = c; vh.AddVert(v);
        v.position = d; vh.AddVert(v);
        vh.AddTriangle(start, start + 1, start + 2);
        vh.AddTriangle(start, start + 2, start + 3);
    }
}

// ============================================================================
// 5. CLASES AUXILIARES MATEMÁTICAS Y DE INTERFAZ
// ============================================================================

public class MathCurve
{
    private Vector3[] points;

    public MathCurve(Vector3[] controlPoints) { points = controlPoints; }

    public Vector3 Evaluate(float t)
    {
        t = Mathf.Clamp01(t);
        int n = points.Length;
        if (n == 0) return Vector3.zero;
        if (n == 1) return points[0];
        Vector3[] p = new Vector3[n];
        Array.Copy(points, p, n);
        for (int k = 1; k < n; k++)
            for (int i = 0; i < n - k; i++)
                p[i] = (1f - t) * p[i] + t * p[i + 1];
        return p[0];
    }

    public float CalculateArcLength(int samples = 100)
    {
        float totalLength = 0f;
        Vector3 prev = Evaluate(0f);
        for (int i = 1; i <= samples; i++)
        {
            Vector3 curr = Evaluate(i / (float)samples);
            totalLength += Vector3.Distance(curr, prev);
            prev = curr;
        }
        return totalLength;
    }

    public Vector3 GetDerivative(float t)
    {
        t = Mathf.Clamp01(t);
        int n = points.Length;
        if (n <= 1) return Vector3.zero;
        int count = n - 1;
        Vector3[] derivativePoints = new Vector3[count];
        for (int i = 0; i < count; i++) derivativePoints[i] = (points[i + 1] - points[i]) * count;

        Vector3[] p = new Vector3[count];
        Array.Copy(derivativePoints, p, count);
        for (int k = 1; k < count; k++)
            for (int i = 0; i < count - k; i++)
                p[i] = (1f - t) * p[i] + t * p[i + 1];
        return p[0];
    }
}

public class UIBezierGraphic : UnityEngine.UI.Graphic, IPointerDownHandler
{
    public List<RectTransform> points = new List<RectTransform>();
    public float thickness = 4f;
    public int resolution = 80;
    public RuntimeBezierEditor editor;

    private const float SelectionTolerancePixels = 25f;

    private void Update() => SetVerticesDirty();

    private List<Vector2> GetLocalPoints()
    {
        List<Vector2> result = new List<Vector2>();
        if (points == null) return result;
        foreach (RectTransform rt in points)
        {
            if (rt != null && rt.gameObject != null) result.Add(rt.anchoredPosition);
        }
        return result;
    }

    protected override void OnPopulateMesh(VertexHelper vh)
    {
        vh.Clear();
        List<Vector2> localPts = GetLocalPoints();
        if (localPts.Count < 2) return;

        Vector2[] curvePoints = new Vector2[resolution];
        for (int i = 0; i < resolution; i++)
            curvePoints[i] = EvaluateBezier(i / (float)(resolution - 1), localPts);

        for (int i = 0; i < resolution - 1; i++)
        {
            Vector2 diff = curvePoints[i + 1] - curvePoints[i];
            if (diff.sqrMagnitude < 0.000001f) continue;

            Vector2 normal = new Vector2(-diff.normalized.y, diff.normalized.x) * (thickness / 2f);
            UIVertex[] verts = new UIVertex[4];
            verts[0] = CreateVertex(curvePoints[i] + normal);
            verts[1] = CreateVertex(curvePoints[i] - normal);
            verts[2] = CreateVertex(curvePoints[i + 1] - normal);
            verts[3] = CreateVertex(curvePoints[i + 1] + normal);

            int startIndex = vh.currentVertCount;
            vh.AddVert(verts[0]); vh.AddVert(verts[1]);
            vh.AddVert(verts[2]); vh.AddVert(verts[3]);
            vh.AddTriangle(startIndex, startIndex + 1, startIndex + 2);
            vh.AddTriangle(startIndex + 2, startIndex + 3, startIndex);
        }
    }

    public static Vector2 EvaluateBezier(float t, List<Vector2> pts)
    {
        t = Mathf.Clamp01(t);
        int n = pts.Count;
        if (n == 0) return Vector2.zero;
        if (n == 1) return pts[0];
        Vector2[] p = new Vector2[n];
        pts.CopyTo(p);
        for (int k = 1; k < n; k++)
            for (int i = 0; i < n - k; i++)
                p[i] = (1f - t) * p[i] + t * p[i + 1];
        return p[0];
    }

    public void OnPointerDown(PointerEventData eventData)
    {
        if (editor == null || points == null || points.Count < 2) return;

        RectTransform rect = transform as RectTransform;
        if (rect == null) return;

        if (RectTransformUtility.ScreenPointToLocalPointInRectangle(rect, eventData.position, eventData.pressEventCamera, out Vector2 localClick))
        {
            if (TryGetNearestCurvePoint(localClick, out float parameter, out Vector2 nearestPoint))
            {
                editor.OnManualCurveClicked(parameter, nearestPoint);
            }
        }
    }

    private bool TryGetNearestCurvePoint(Vector2 click, out float bestParameter, out Vector2 bestPoint)
    {
        bestParameter = 0f;
        bestPoint = Vector2.zero;

        List<Vector2> localPts = GetLocalPoints();
        if (localPts.Count < 2 || resolution < 2) return false;

        float bestDistanceSquared = float.PositiveInfinity;
        int samples = Mathf.Max(200, resolution * 3);

        for (int i = 0; i <= samples; i++)
        {
            float t = i / (float)samples;
            Vector2 point = EvaluateBezier(t, localPts);
            float distanceSquared = (point - click).sqrMagnitude;

            if (distanceSquared < bestDistanceSquared)
            {
                bestDistanceSquared = distanceSquared;
                bestParameter = t;
                bestPoint = point;
            }
        }

        return bestDistanceSquared <= SelectionTolerancePixels * SelectionTolerancePixels;
    }

    private UIVertex CreateVertex(Vector2 position)
    {
        UIVertex vertex = UIVertex.simpleVert;
        vertex.position = position;
        vertex.color = color;
        return vertex;
    }
}

public class UIWindowController : MonoBehaviour
{
    private RectTransform rectTransform;
    private bool isMinimized = false;
    public GameObject[] collapsibleElements;

    private void Awake() => rectTransform = GetComponent<RectTransform>();

    public void ToggleMinimize()
    {
        if (rectTransform == null) return;
        isMinimized = !isMinimized;
        rectTransform.sizeDelta = new Vector2(rectTransform.sizeDelta.x, isMinimized ? 35f : 530f);
        if (collapsibleElements != null)
        {
            foreach (GameObject element in collapsibleElements)
            {
                if (element != null) element.SetActive(!isMinimized);
            }
        }
    }
}

public class UIDraggable : MonoBehaviour, IDragHandler, IPointerDownHandler
{
    private RectTransform rectTransform;
    private Canvas canvas;
    public bool isWindow = false;
    public int pointIndex = -1;
    public RuntimeBezierEditor editor;

    private void Awake()
    {
        rectTransform = GetComponent<RectTransform>();
        canvas = GetComponentInParent<Canvas>();
    }

    public void OnPointerDown(PointerEventData eventData)
    {
        if (rectTransform == null) rectTransform = GetComponent<RectTransform>();
        if (rectTransform != null) rectTransform.SetAsLastSibling();
    }

    public void OnDrag(PointerEventData eventData)
    {
        if (rectTransform == null) rectTransform = GetComponent<RectTransform>();
        if (canvas != null && rectTransform != null)
        {
            rectTransform.anchoredPosition += eventData.delta / canvas.scaleFactor;
            if (!isWindow && editor != null && pointIndex >= 0)
            {
                editor.OnManualPointDragged(pointIndex, rectTransform.anchoredPosition);
            }
        }
    }
}