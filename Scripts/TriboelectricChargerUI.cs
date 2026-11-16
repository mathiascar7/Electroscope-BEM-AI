using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.UI;
using Newtonsoft.Json;

/// <summary>
/// Sistema Unificado Triboeléctrico y Réplica Exacta de la UI Visual "AI SPATIAL DUAL-GRID INSPECTOR".
/// Ajuste dinámico de Canvas mediante ContentSizeFitter e InputFields no modificables para evitar desbordamientos.
/// </summary>
[DisallowMultipleComponent]
public sealed class TriboelectricDualGridUI : MonoBehaviour
{
    [Header("📂 Rutas de Archivos (Assets)")]
    [SerializeField] private string databaseRelativePath = "ElectroscopePhysicsCache_v2.json";
    [SerializeField] private string winnerConfigRelativePath = "ChargingValidation/BEM_Winner_Config.json";

    [Header("⚙️ Parámetros Físicos y de Contacto (SI)")]
    [SerializeField] private double normalForceN = 10.0;
    [SerializeField] private double rubbingSpeedMps = 3.0;
    [SerializeField] private double rubbingDurationSeconds = 5.0;

    [Header("🧪 Par de Materiales Seleccionado")]
    [SerializeField] private string materialAName = "Silica Glass";
    [SerializeField] private string materialBName = "Teflon";

    [Header("🖥️ Configuración de Interfaces")]
    [SerializeField] private bool drawControlOnGUI = true;
    [SerializeField] private bool showDualGridInspector = true;

    [Header("📊 Diagnóstico y Reporte")]
    [SerializeField, TextArea(6, 12)]
    private string lastReport = "Calculation not performed yet.";
    [SerializeField] private bool lastValidationPassed;

    public event Action OnParametersChanged;

    #region ESTRUCTURAS DE DATOS Y PAYLOADS PARA IA (DTOs)

    [Serializable]
    public class AITriboRequest
    {
        public string materialA = "Silica Glass";
        public string materialB = "Teflon";
        public double normalForceN = 10.0;
        public double rubbingSpeedMps = 3.0;
        public double rubbingDurationSeconds = 5.0;
        public bool injectChargeIntoSimulation = true;
    }

    [Serializable]
    public class AITriboResponse
    {
        public string materialA;
        public string materialB;
        public double generatedCharge_nC;
        public double generatedPotential_V;
        public double frictionForce_N;
        public double totalWork_J;
        public double peakElectricField_Vm;
        public bool isConductor;
        public bool success;
        public string report;
    }

    #endregion

    #region PROPIEDADES PÚBLICAS

    public string MaterialA
    {
        get => materialAName;
        set { if (materialAName != value) { materialAName = value; OnParametersChanged?.Invoke(); } }
    }

    public string MaterialB
    {
        get => materialBName;
        set { if (materialBName != value) { materialBName = value; OnParametersChanged?.Invoke(); } }
    }

    public double NormalForceN
    {
        get => normalForceN;
        set { if (Math.Abs(normalForceN - value) > 1e-6) { normalForceN = value; OnParametersChanged?.Invoke(); } }
    }

    public double RubbingSpeedMps
    {
        get => rubbingSpeedMps;
        set { if (Math.Abs(rubbingSpeedMps - value) > 1e-6) { rubbingSpeedMps = value; OnParametersChanged?.Invoke(); } }
    }

    public double RubbingDurationSeconds
    {
        get => rubbingDurationSeconds;
        set { if (Math.Abs(rubbingDurationSeconds - value) > 1e-6) { rubbingDurationSeconds = value; OnParametersChanged?.Invoke(); } }
    }

    public string LastReport => lastReport;
    public bool LastValidationPassed => lastValidationPassed;

    #endregion

    #region Estructuras JSON
    [Serializable]
    private class DatabaseContainer
    {
        [JsonProperty("savedPhysics")]
        public List<MaterialRaw> savedPhysicsMain;

        [JsonProperty("saved Physics")]
        public List<MaterialRaw> savedPhysicsAlt;

        public List<MaterialRaw> GetMaterials()
        {
            if (savedPhysicsMain != null && savedPhysicsMain.Count > 0) return savedPhysicsMain;
            if (savedPhysicsAlt != null && savedPhysicsAlt.Count > 0) return savedPhysicsAlt;
            return new List<MaterialRaw>();
        }
    }

    [Serializable]
    public class MaterialRaw
    {
        public string materialName;
        public List<PropertyRaw> properties;
    }

    [Serializable]
    public class PropertyRaw
    {
        public string propertyName;
        public float value;
    }

    public class MaterialPhysicsData
    {
        public float smoothness = 0.9f;
        public float conductivity = 1e-12f;
        public float affinity = 0.0f;
        public float permittivity = 2.0f;
        public float friction = 0.2f;
    }

    [Serializable]
    private class BEMWinnerConfigData
    {
        public string winnerRodMaterial;
        public string winnerElectroscopeMaterial;
        public double normalForceN;
        public double rubbingSpeedMps;
        public double durationSeconds;
        public double generatedChargeC;
        public double generatedPotentialV;
        public double frictionForceN;
        public double totalWorkJoule;
    }

    public struct CalculationResult
    {
        public double generatedChargeC;
        public double generatedPotentialV;
        public double frictionForceN;
        public double totalWorkJoule;
        public double peakElectricField;
        public bool isConductor;
        public double deltaAffinity;
    }
    #endregion

    private Dictionary<string, MaterialRaw> materialIndex = new Dictionary<string, MaterialRaw>(StringComparer.OrdinalIgnoreCase);
    private BEMWinnerConfigData winnerConfig;
    private string fullDbPath;
    private string fullWinnerPath;

    private AISpatialDualGridInspector inspectorVisualizer;

    private void Awake()
    {
        LoadAllData();
    }

    private void Start()
    {
        if (showDualGridInspector)
        {
            EnsureInspectorVisualizer();
        }
        CalculateAndReport();
    }

    private void OnEnable()
    {
        if (materialIndex.Count == 0) LoadAllData();
        if (showDualGridInspector) EnsureInspectorVisualizer();
    }

    private void EnsureInspectorVisualizer()
    {
        if (inspectorVisualizer == null)
        {
            inspectorVisualizer = GetComponent<AISpatialDualGridInspector>();
            if (inspectorVisualizer == null)
            {
                inspectorVisualizer = gameObject.AddComponent<AISpatialDualGridInspector>();
            }
        }
        inspectorVisualizer.CreateUI();
    }

    private void SyncInspectorUI(CalculationResult res)
    {
        if (!showDualGridInspector || inspectorVisualizer == null) EnsureInspectorVisualizer();
        if (inspectorVisualizer == null) return;

        double sigmaA = 3048.64;
        double sigmaB = 17755.90;
        double pearsonRad = -0.942;
        double pearsonAA = -0.938;
        double pearsonAB = 0.000;
        double superpositionRatio = 0.50;

        float ablationA = 25f;
        float ablationB = 0f;

        string payloadSummary = $"Step 2 [{materialAName} ➔ {materialBName}] Phase=Phase1_IsolationA.\n" +
                                $"A: ΔQ_A={ablationA:F0} %, σ_A={sigmaA:F1} | B: ΔQ_B={ablationB:F0} %, σ_B={sigmaB:F1}.\n" +
                                $"Pearson: p_rad={pearsonRad:F3}, p_hist(AA)={pearsonAA:F3}, p_hist(AB)={pearsonAB:F3}, η={superpositionRatio:F2}.";

        inspectorVisualizer.SetSnapshot(
            materialAName, materialBName,
            "Phase1_IsolationA",
            sigmaA, sigmaB,
            pearsonRad, pearsonAA, pearsonAB,
            superpositionRatio, ablationA, ablationB,
            payloadSummary
        );
    }

    #region INTEGRACIÓN CON AI FRAMEWORK Y CÁLCULO
    public string ExecuteAITriboChargingJson(string jsonRequest)
    {
        try
        {
            AITriboRequest req = JsonUtility.FromJson<AITriboRequest>(jsonRequest) ?? new AITriboRequest();
            AITriboResponse resp = ExecuteAITriboStep(req);
            return JsonUtility.ToJson(resp);
        }
        catch (Exception ex)
        {
            Debug.LogError($"[TriboelectricDualGridUI] Error en llamada AI JSON: {ex.Message}");
            return $"{{\"error\": \"{ex.Message}\"}}";
        }
    }

    public AITriboResponse ExecuteAITriboStep(AITriboRequest req)
    {
        if (req == null) req = new AITriboRequest();

        if (!string.IsNullOrEmpty(req.materialA)) MaterialA = req.materialA;
        if (!string.IsNullOrEmpty(req.materialB)) MaterialB = req.materialB;
        if (req.normalForceN > 0) NormalForceN = req.normalForceN;
        if (req.rubbingSpeedMps >= 0) RubbingSpeedMps = req.rubbingSpeedMps;
        if (req.rubbingDurationSeconds >= 0) RubbingDurationSeconds = req.rubbingDurationSeconds;

        CalculationResult res = ExecuteRawCalculation();
        CalculateAndReport();

        double chargeNanoCoulombs = res.generatedChargeC * 1e9;

        if (req.injectChargeIntoSimulation)
        {
            RuntimeBezierEditor bezierEditor = FindFirstObjectByType<RuntimeBezierEditor>();
            if (bezierEditor != null)
            {
                bezierEditor.SetChargeFromAI((float)chargeNanoCoulombs);
            }
        }

        return new AITriboResponse
        {
            materialA = materialAName,
            materialB = materialBName,
            generatedCharge_nC = chargeNanoCoulombs,
            generatedPotential_V = res.generatedPotentialV,
            frictionForce_N = res.frictionForceN,
            totalWork_J = res.totalWorkJoule,
            peakElectricField_Vm = res.peakElectricField,
            isConductor = res.isConductor,
            success = true,
            report = lastReport
        };
    }

    public void LoadAllData()
    {
        materialIndex.Clear();
        fullDbPath = FindAssetFilePath(databaseRelativePath);
        fullWinnerPath = FindAssetFilePath(winnerConfigRelativePath);

        if (!string.IsNullOrEmpty(fullDbPath) && File.Exists(fullDbPath))
        {
            try
            {
                string jsonText = File.ReadAllText(fullDbPath);
                var db = JsonConvert.DeserializeObject<DatabaseContainer>(jsonText);
                var materials = db != null ? db.GetMaterials() : null;

                if (materials == null || materials.Count == 0)
                    materials = JsonConvert.DeserializeObject<List<MaterialRaw>>(jsonText);

                if (materials != null)
                {
                    foreach (var mat in materials)
                    {
                        if (mat != null && !string.IsNullOrWhiteSpace(mat.materialName))
                        {
                            string key = mat.materialName.Trim();
                            if (!materialIndex.ContainsKey(key))
                                materialIndex.Add(key, mat);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[TriboelectricDualGridUI] Error al cargar materiales: {ex.Message}");
            }
        }

        if (!string.IsNullOrEmpty(fullWinnerPath) && File.Exists(fullWinnerPath))
        {
            try
            {
                string winnerJson = File.ReadAllText(fullWinnerPath);
                winnerConfig = JsonConvert.DeserializeObject<BEMWinnerConfigData>(winnerJson);
                if (winnerConfig != null)
                {
                    if (!string.IsNullOrEmpty(winnerConfig.winnerRodMaterial)) materialAName = winnerConfig.winnerRodMaterial;
                    if (!string.IsNullOrEmpty(winnerConfig.winnerElectroscopeMaterial)) materialBName = winnerConfig.winnerElectroscopeMaterial;
                    if (winnerConfig.normalForceN > 0) normalForceN = winnerConfig.normalForceN;
                    if (winnerConfig.rubbingSpeedMps > 0) rubbingSpeedMps = winnerConfig.rubbingSpeedMps;
                    if (winnerConfig.durationSeconds > 0) rubbingDurationSeconds = winnerConfig.durationSeconds;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[TriboelectricDualGridUI] Error leyendo ganador BEM: {ex.Message}");
            }
        }
    }

    private string FindAssetFilePath(string relativePath)
    {
        string direct = Path.Combine(Application.dataPath, relativePath);
        if (File.Exists(direct)) return direct;
        string filename = Path.GetFileName(relativePath);
        string[] search = Directory.GetFiles(Application.dataPath, filename, SearchOption.AllDirectories);
        return search.Length > 0 ? search[0] : direct;
    }

    private void OnGUI()
    {
        if (!drawControlOnGUI) return;

        GUILayout.BeginArea(new Rect(12, 12, Mathf.Min(Screen.width - 24, 420), Screen.height - 24), GUI.skin.box);
        GUILayout.Label("⚡ TRIBOELECTRIC CHARGER (BEM Control Panel)");
        GUILayout.Label("DB: " + (!string.IsNullOrEmpty(fullDbPath) && File.Exists(fullDbPath) ? "Cargada (" + materialIndex.Count + " mat.)" : "NOT FOUND"));
        GUILayout.Label("BEM Winner Config: " + (winnerConfig != null ? winnerConfig.winnerRodMaterial + " vs " + winnerConfig.winnerElectroscopeMaterial : "NOT LOADED"));
        GUILayout.Space(6);

        string newMatA = GUILayout.TextField(materialAName);
        if (newMatA != materialAName) MaterialA = newMatA;

        string newMatB = GUILayout.TextField(materialBName);
        if (newMatB != materialBName) MaterialB = newMatB;

        double newForce = ParseGuiDouble(GUILayout.TextField(normalForceN.ToString("G", CultureInfo.InvariantCulture)), normalForceN);
        if (Math.Abs(newForce - normalForceN) > 1e-6) NormalForceN = newForce;

        double newSpeed = ParseGuiDouble(GUILayout.TextField(rubbingSpeedMps.ToString("G", CultureInfo.InvariantCulture)), rubbingSpeedMps);
        if (Math.Abs(newSpeed - rubbingSpeedMps) > 1e-6) RubbingSpeedMps = newSpeed;

        double newDuration = ParseGuiDouble(GUILayout.TextField(rubbingDurationSeconds.ToString("G", CultureInfo.InvariantCulture)), rubbingDurationSeconds);
        if (Math.Abs(newDuration - rubbingDurationSeconds) > 1e-6) RubbingDurationSeconds = newDuration;

        GUILayout.Space(6);
        if (GUILayout.Button("⚡ Calculate Charge & Update Dual Grid"))
        {
            CalculateAndReport();
        }

        GUILayout.Space(6);
        GUILayout.TextArea(lastReport, GUILayout.MinHeight(180), GUILayout.ExpandHeight(true));

        GUILayout.EndArea();
    }

    private static double ParseGuiDouble(string s, double fallback)
    {
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && !double.IsNaN(v) && !double.IsInfinity(v))
            return v;
        return fallback;
    }

    public CalculationResult ExecuteRawCalculation()
    {
        string keyA = string.IsNullOrWhiteSpace(materialAName) ? "" : materialAName.Trim();
        string keyB = string.IsNullOrWhiteSpace(materialBName) ? "" : materialBName.Trim();

        // 1. Recargar caché desde disco si algún material (ej. recién investigado con Gemini) no está en memoria
        if (materialIndex.Count == 0 || !materialIndex.ContainsKey(keyA) || !materialIndex.ContainsKey(keyB))
        {
            LoadAllData();
            AIChargingFramework fw = FindFirstObjectByType<AIChargingFramework>();
            if (fw != null) fw.InvestigateAndInjectMaterials();
        }

        CalculationResult res = new CalculationResult();
        if (!materialIndex.TryGetValue(keyA, out MaterialRaw rawA) ||
            !materialIndex.TryGetValue(keyB, out MaterialRaw rawB))
            return res;

        MaterialPhysicsData dataA = ExtractPhysicsData(rawA);
        MaterialPhysicsData dataB = ExtractPhysicsData(rawB);

        const double eps0 = 8.854187817e-12;
        const double airBreakdownFieldVm = 3.0e6; // Límite de descarga corona en aire (~3 kV/mm)

        // 2. Mecánica de fricción y trabajo
        double frictionCoeff = (dataA.friction + dataB.friction) * 0.5;
        res.frictionForceN = frictionCoeff * normalForceN;
        res.totalWorkJoule = res.frictionForceN * rubbingSpeedMps * rubbingDurationSeconds;

        // 3. Saturación triboeléctrica exponencial + límite de ruptura dieléctrica del aire
        double effectiveContactArea = 1e-4 * Math.Max(0.05, dataA.smoothness * dataB.smoothness);
        double sweptTransferArea = 25.0 * effectiveContactArea; // Área efectiva barrida (~25 cm²)

        res.deltaAffinity = dataA.affinity - dataB.affinity;
        double saturationFactor = 1.0 - Math.Exp(-0.015 * Math.Max(0.0, normalForceN * rubbingSpeedMps * rubbingDurationSeconds));
        double maxSigmaAir = eps0 * airBreakdownFieldVm; // ~26.56 µC/m²
        double surfaceChargeDensity = Math.Clamp(res.deltaAffinity * saturationFactor, -maxSigmaAir, maxSigmaAir);

        res.generatedChargeC = surfaceChargeDensity * sweptTransferArea;

        // 4. Potencial electrostático dimensionado en Voltios (V = Q / C_eff)
        double perm = Math.Max(dataB.permittivity, 1.0f);
        double effectiveRodCapacitanceF = 10.0e-12 * (1.0 + 0.1 * Math.Log(perm)); // ~10–12 pF
        res.generatedPotentialV = res.generatedChargeC / effectiveRodCapacitanceF;

        res.peakElectricField = Math.Abs(surfaceChargeDensity) / eps0;
        res.isConductor = dataB.conductivity >= 1.0f;

        return res;
    }

    public void CalculateAndReport()
    {
        CalculationResult res = ExecuteRawCalculation();
        if (!materialIndex.TryGetValue(materialAName.Trim(), out MaterialRaw rawA) ||
            !materialIndex.TryGetValue(materialBName.Trim(), out MaterialRaw rawB))
        {
            lastReport = $"ERROR: Material A '{materialAName}' o B '{materialBName}' no encontrado.";
            return;
        }

        List<string> lines = new List<string>
        {
            $"Material A: {rawA.materialName}",
            $"Material B: {rawB.materialName}",
            $"Conditions: Force={normalForceN:F2} N | Speed={rubbingSpeedMps:F2} m/s | Time={rubbingDurationSeconds:F2} s",
            "--------------------------------------------------",
            "🟢 BEM PHYSICAL CALCULATION:",
            $"Relative Affinity (Δσ): {res.deltaAffinity:E4} C/m²",
            $"Generated Charge (Q): {res.generatedChargeC:E4} C ({res.generatedChargeC * 1e9:F2} nC)",
            $"Electric Potential (V): {res.generatedPotentialV:N2} V",
            $"Peak Electric Field (E): {res.peakElectricField:E4} V/m",
            $"Friction Force (F_f): {res.frictionForceN:F2} N",
            $"Total Mechanical Work (W): {res.totalWorkJoule:F2} J",
            $"Classification: {(res.isConductor ? "Electronic Conductor" : "Dielectric / Insulator")}"
        };

        lastReport = string.Join("\n", lines.ToArray());
        SyncInspectorUI(res);
    }

    private MaterialPhysicsData ExtractPhysicsData(MaterialRaw raw)
    {
        MaterialPhysicsData data = new MaterialPhysicsData();
        if (raw.properties == null) return data;

        foreach (var p in raw.properties)
        {
            if (p == null || string.IsNullOrEmpty(p.propertyName)) continue;
            string name = p.propertyName.Trim();
            if (name.Equals("Surface smoothness", StringComparison.OrdinalIgnoreCase)) data.smoothness = p.value;
            else if (name.Equals("Electrical conductivity", StringComparison.OrdinalIgnoreCase)) data.conductivity = p.value;
            else if (name.Equals("Triboelectric charge affinity", StringComparison.OrdinalIgnoreCase)) data.affinity = p.value;
            else if (name.Equals("Relative permittivity", StringComparison.OrdinalIgnoreCase)) data.permittivity = p.value;
            else if (name.Equals("Kinetic friction coefficient", StringComparison.OrdinalIgnoreCase)) data.friction = p.value;
        }

        return data;
    }
    #endregion
}

// ============================================================================
// COMPONENTE VISUAL CON AUTO-FIT Y RENDERIZADO ESPACIAL DINÁMICO DE PATRONES
// ============================================================================

[DisallowMultipleComponent]
public sealed class AISpatialDualGridInspector : MonoBehaviour
{
    private Vector2 referenceResolution = new Vector2(1920f, 1080f);
    private Vector2 screenMargin = new Vector2(25f, 25f);

    private Canvas canvas;
    private Text textMaterialA;
    private Text textMaterialB;
    private Text textGridATitle;
    private Text textGridBTitle;
    private Text textProtocolPhase;
    private Text textInstability;
    private Text textPearsonRad;
    private Text textPearsonAA;
    private Text textPearsonAB;
    private Text textSuperposition;
    private InputField inputPayloadSummary;

    private Image[] gridAImages;
    private Image[] gridBImages;

    private const int GridColumns = 18;
    private const int GridRows = 5;

    private static readonly Color ColorPanelBg = new Color(0.10f, 0.13f, 0.17f, 0.96f);
    private static readonly Color ColorTitleCyan = new Color(0.15f, 0.85f, 1.0f, 1f);
    private static readonly Color ColorHeaderCyan = new Color(0.0f, 0.9f, 1.0f, 1f);
    private static readonly Color ColorOrangePhase = new Color(1.0f, 0.72f, 0.2f, 1f);
    private static readonly Color ColorGreenPearson = new Color(0.3f, 0.85f, 0.4f, 1f);
    private static readonly Color ColorMutedGray = new Color(0.70f, 0.75f, 0.82f, 1f);
    private static readonly Color ColorPayloadBg = new Color(0.06f, 0.08f, 0.11f, 0.98f);

    private static readonly Color ColorPurpleOuter = new Color(0.25f, 0.18f, 0.62f, 1f);
    private static readonly Color ColorPurpleInner = new Color(0.35f, 0.26f, 0.78f, 1f);
    private static readonly Color ColorYellowCore = new Color(0.95f, 0.82f, 0.15f, 1f);
    private static readonly Color ColorRedAblation = new Color(0.85f, 0.15f, 0.15f, 1f);

    public void CreateUI()
    {
        if (canvas != null) return;

        GameObject canvasGO = GameObject.Find("AI_Spatial_DualGrid_Inspector_Canvas");
        if (canvasGO == null)
        {
            canvasGO = new GameObject("AI_Spatial_DualGrid_Inspector_Canvas",
                typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        }

        canvas = canvasGO.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 350;

        CanvasScaler scaler = canvasGO.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = referenceResolution;
        scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
        scaler.matchWidthOrHeight = 0.5f;

        Transform existingPanel = canvasGO.transform.Find("DualGridInspectorPanel");
        GameObject panel = existingPanel != null ? existingPanel.gameObject : CreateUIObject("DualGridInspectorPanel", canvasGO.transform, typeof(Image));

        RectTransform panelRect = panel.GetComponent<RectTransform>();
        panelRect.anchorMin = new Vector2(1f, 1f);
        panelRect.anchorMax = new Vector2(1f, 1f);
        panelRect.pivot = new Vector2(1f, 1f);
        panelRect.anchoredPosition = new Vector2(-screenMargin.x, -screenMargin.y);
        panelRect.sizeDelta = new Vector2(440f, 0f);
        panel.GetComponent<Image>().color = ColorPanelBg;

        VerticalLayoutGroup layout = panel.GetComponent<VerticalLayoutGroup>();
        if (layout == null) layout = panel.AddComponent<VerticalLayoutGroup>();
        layout.padding = new RectOffset(14, 14, 14, 14);
        layout.spacing = 4f;
        layout.childAlignment = TextAnchor.UpperLeft;
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = true;
        layout.childForceExpandHeight = false;

        ContentSizeFitter csf = panel.GetComponent<ContentSizeFitter>();
        if (csf == null) csf = panel.AddComponent<ContentSizeFitter>();
        csf.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
        csf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        AddLabel(panel.transform, "Title", "🔍 AI SPATIAL DUAL-GRID INSPECTOR (2D Blind)", 14, ColorTitleCyan, FontStyle.Bold, 22f);
        AddLabel(panel.transform, "PairHeader", "Active Charging Pair:", 13, ColorHeaderCyan, FontStyle.Bold, 18f);
        textMaterialA = AddLabel(panel.transform, "MatA", "⚡ A (Charging Rod): Silica Glass", 12, Color.white, FontStyle.Bold, 18f);
        textMaterialB = AddLabel(panel.transform, "MatB", "🥊 B (Charged Body): Teflon", 12, new Color(1.0f, 0.4f, 0.35f), FontStyle.Bold, 20f);

        textGridATitle = AddLabel(panel.transform, "GridATitle", "Grid A [Charging Rod] (Ablation ΔQ_A = 25 %):", 11, Color.white, FontStyle.Bold, 18f);
        gridAImages = CreateGrid(panel.transform, "GridA", 80f);

        textGridBTitle = AddLabel(panel.transform, "GridBTitle", "Grid B [Charged Body] (Ablation ΔQ_B = 0 %):", 11, Color.white, FontStyle.Bold, 18f);
        gridBImages = CreateGrid(panel.transform, "GridB", 80f);

        textProtocolPhase = AddLabel(panel.transform, "ProtocolPhase", "📊 Protocol Phase: Phase1_IsolationA", 12, ColorOrangePhase, FontStyle.Bold, 20f);
        textInstability = AddLabel(panel.transform, "Instability", "• Grid A Instability (σ_A): 3048,64 | Grid B (σ_B): 17755,90", 10, ColorMutedGray, FontStyle.Normal, 18f);

        AddLabel(panel.transform, "PearsonHeader", "☑ Segmented Pearson Matrix (ρ):", 12, Color.white, FontStyle.Bold, 18f);
        textPearsonRad = AddLabel(panel.transform, "PearsonRad", "• p_rad(Radiative Repulsion): -0,942", 11, ColorGreenPearson, FontStyle.Normal, 16f);
        textPearsonAA = AddLabel(panel.transform, "PearsonAA", "• p_hist(Autocorrelation A ➔ A): -0,938", 11, ColorGreenPearson, FontStyle.Normal, 16f);
        textPearsonAB = AddLabel(panel.transform, "PearsonAB", "• p_hist(Cross Interference A ➔ B): 0,000", 11, ColorMutedGray, FontStyle.Normal, 16f);
        textSuperposition = AddLabel(panel.transform, "Superposition", "• Superposition Ratio (η): 0,50", 11, ColorMutedGray, FontStyle.Normal, 18f);

        AddLabel(panel.transform, "PayloadHeader", "📋 Agent Payload Summary:", 12, Color.white, FontStyle.Bold, 18f);
        inputPayloadSummary = AddReadOnlyInputField(panel.transform, "SummaryBox", "Initializing payload...", 10, Color.white, 80f);
    }

    private Image[] CreateGrid(Transform parent, string objectName, float height)
    {
        Transform existing = parent.Find(objectName);
        GameObject gridGO = existing != null ? existing.gameObject : CreateUIObject(objectName, parent, typeof(Image));

        LayoutElement gridLe = gridGO.GetComponent<LayoutElement>();
        if (gridLe == null) gridLe = gridGO.AddComponent<LayoutElement>();
        gridLe.preferredHeight = height;
        gridLe.minHeight = height;
        gridLe.flexibleWidth = 1f;

        gridGO.GetComponent<Image>().color = new Color(0.08f, 0.10f, 0.14f, 1f);

        GridLayoutGroup grid = gridGO.GetComponent<GridLayoutGroup>();
        if (grid == null) grid = gridGO.AddComponent<GridLayoutGroup>();
        grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
        grid.constraintCount = GridColumns;
        grid.cellSize = new Vector2((440f - 28f - 4f - (GridColumns - 1) * 1.5f) / GridColumns, (height - 4f - (GridRows - 1) * 1.5f) / GridRows);
        grid.spacing = new Vector2(1.5f, 1.5f);
        grid.padding = new RectOffset(2, 2, 2, 2);

        Image[] images = new Image[GridColumns * GridRows];
        for (int row = 0; row < GridRows; row++)
        {
            for (int col = 0; col < GridColumns; col++)
            {
                int index = row * GridColumns + col;
                string cellName = "Panel_" + row + "_" + col;
                Transform existingCell = gridGO.transform.Find(cellName);
                GameObject cell = existingCell != null ? existingCell.gameObject : CreateUIObject(cellName, gridGO.transform, typeof(Image));
                images[index] = cell.GetComponent<Image>();
            }
        }
        return images;
    }

    private InputField AddReadOnlyInputField(Transform parent, string objectName, string value, int fontSize, Color textColor, float preferredHeight)
    {
        Transform existing = parent.Find(objectName);
        GameObject fieldGO = existing != null ? existing.gameObject : CreateUIObject(objectName, parent, typeof(Image));
        fieldGO.GetComponent<Image>().color = ColorPayloadBg;

        LayoutElement le = fieldGO.GetComponent<LayoutElement>();
        if (le == null) le = fieldGO.AddComponent<LayoutElement>();
        le.preferredHeight = preferredHeight;
        le.minHeight = preferredHeight;
        le.flexibleWidth = 1f;

        Transform existingText = fieldGO.transform.Find("Text");
        GameObject textGO = existingText != null ? existingText.gameObject : CreateUIObject("Text", fieldGO.transform, typeof(Text));
        Text text = textGO.GetComponent<Text>();
        text.font = GetFont();
        text.fontSize = fontSize;
        text.color = textColor;
        text.text = value;
        text.alignment = TextAnchor.UpperLeft;
        text.horizontalOverflow = HorizontalWrapMode.Wrap;
        text.verticalOverflow = VerticalWrapMode.Overflow;

        RectTransform textRt = text.rectTransform;
        textRt.anchorMin = Vector2.zero;
        textRt.anchorMax = Vector2.one;
        textRt.offsetMin = new Vector2(8f, 6f);
        textRt.offsetMax = new Vector2(-8f, -6f);

        InputField inputField = fieldGO.GetComponent<InputField>();
        if (inputField == null) inputField = fieldGO.AddComponent<InputField>();
        inputField.textComponent = text;
        inputField.text = value;
        inputField.readOnly = true;
        inputField.interactable = false;

        return inputField;
    }

    public void SetSnapshot(
        string materialNameA, string materialNameB, string phase,
        double sigmaA, double sigmaB,
        double pearsonRad, double pearsonAA, double pearsonAB,
        double superpositionRatio, float ablationA, float ablationB,
        string agentSummary)
    {
        if (canvas == null) CreateUI();

        if (textMaterialA != null) textMaterialA.text = $"⚡ A (Charging Rod): {materialNameA}";
        if (textMaterialB != null) textMaterialB.text = $"🥊 B (Charged Body): {materialNameB}";
        if (textGridATitle != null) textGridATitle.text = $"Grid A [Charging Rod] (Ablation ΔQ_A = {ablationA:F0} %):";
        if (textGridBTitle != null) textGridBTitle.text = $"Grid B [Charged Body] (Ablation ΔQ_B = {ablationB:F0} %):";
        if (textProtocolPhase != null) textProtocolPhase.text = $"📊 Protocol Phase: {phase}";
        if (textInstability != null) textInstability.text = $"• Grid A Instability (σ_A): {sigmaA:F2} | Grid B (σ_B): {sigmaB:F2}";

        if (textPearsonRad != null) textPearsonRad.text = $"• p_rad(Radiative Repulsion): {pearsonRad:F3}";
        if (textPearsonAA != null) textPearsonAA.text = $"• p_hist(Autocorrelation A ➔ A): {pearsonAA:F3}";
        if (textPearsonAB != null) textPearsonAB.text = $"• p_hist(Cross Interference A ➔ B): {pearsonAB:F3}";
        if (textSuperposition != null) textSuperposition.text = $"• Superposition Ratio (η): {superpositionRatio:F2}";

        if (inputPayloadSummary != null)
        {
            inputPayloadSummary.text = agentSummary;
            if (inputPayloadSummary.textComponent != null)
                inputPayloadSummary.textComponent.text = agentSummary;
        }

        // Renderizado dinámico según el patrón espacial y la grilla objetivo
        RenderGridCells(gridAImages, ablationA, phase, true);
        RenderGridCells(gridBImages, ablationB, phase, false);
    }

    private void RenderGridCells(Image[] images, float ablationPercent, string phase, bool isGridA)
    {
        if (images == null) return;

        string phaseLower = phase != null ? phase.ToLowerInvariant() : "";
        string pattern = "default";

        if (phaseLower.Contains("asimétric") || phaseLower.Contains("asimetric"))
        {
            pattern = isGridA ? "borde" : "centro";
        }
        else if (phaseLower.Contains("borde"))
        {
            pattern = "borde";
        }
        else if (phaseLower.Contains("centro") || phaseLower.Contains("núcleo") || phaseLower.Contains("nucleo"))
        {
            pattern = "centro";
        }
        else if (phaseLower.Contains("ajedrez"))
        {
            pattern = "ajedrez";
        }
        else if (phaseLower.Contains("aleatorio") || phaseLower.Contains("scatter"))
        {
            pattern = "aleatorio";
        }
        else if (phaseLower.Contains("total") || ablationPercent >= 99f)
        {
            pattern = "total";
        }

        for (int i = 0; i < images.Length; i++)
        {
            int row = i / GridColumns;
            int col = i % GridColumns;

            bool isYellowArea = (row >= 1 && row <= 3 && col >= 4 && col <= 13);
            bool isInnerCore = (row >= 1 && row <= 3 && col >= 6 && col <= 11);
            bool isBorderArea = isYellowArea && !isInnerCore;

            bool isAblated = false;

            if (ablationPercent > 0)
            {
                switch (pattern)
                {
                    case "borde":
                        isAblated = isBorderArea;
                        break;
                    case "centro":
                        isAblated = isInnerCore;
                        break;
                    case "ajedrez":
                        isAblated = isYellowArea && ((row + col) % 2 == 0);
                        break;
                    case "aleatorio":
                        // Algoritmo pseudo-aleatorio determinista disperso por celda
                        int hash = (i * 37 + (isGridA ? 17 : 53)) % 100;
                        isAblated = isYellowArea && (hash < ablationPercent);
                        break;
                    case "total":
                        isAblated = isYellowArea;
                        break;
                    default:
                        isAblated = isInnerCore;
                        break;
                }
            }

            if (isAblated)
            {
                images[i].color = ColorRedAblation;
            }
            else if (isYellowArea)
            {
                images[i].color = ColorYellowCore;
            }
            else
            {
                images[i].color = ((row + col) % 2 == 0) ? ColorPurpleOuter : ColorPurpleInner;
            }
        }
    }

    private Text AddLabel(Transform parent, string objectName, string value, int fontSize, Color color, FontStyle style, float preferredHeight)
    {
        Transform existing = parent.Find(objectName);
        GameObject go = existing != null ? existing.gameObject : CreateUIObject(objectName, parent, typeof(Text));
        Text text = go.GetComponent<Text>();
        text.font = GetFont();
        text.fontSize = fontSize;
        text.fontStyle = style;
        text.color = color;
        text.text = value;
        text.alignment = TextAnchor.MiddleLeft;
        text.horizontalOverflow = HorizontalWrapMode.Wrap;
        text.verticalOverflow = VerticalWrapMode.Truncate;

        LayoutElement le = go.GetComponent<LayoutElement>();
        if (le == null) le = go.AddComponent<LayoutElement>();
        le.preferredHeight = preferredHeight;
        le.minHeight = preferredHeight;
        le.flexibleWidth = 1f;
        return text;
    }

    private GameObject CreateUIObject(string objectName, Transform parent, Type componentType)
    {
        GameObject go = new GameObject(objectName, typeof(RectTransform), typeof(CanvasRenderer), componentType);
        go.transform.SetParent(parent, false);
        return go;
    }

    private Font GetFont()
    {
        Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        if (font == null) font = Resources.GetBuiltinResource<Font>("Arial.ttf");
        return font;
    }
}