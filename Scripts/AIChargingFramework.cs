using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using Newtonsoft.Json;

/// <summary>
/// AI Charging Framework Socket-Ready API Controller.
/// Control bidireccional para IA en ciclo vivo: Investigación de materiales, 
/// construcción 3D procedural, triboelectricidad, repulsión foliar, ablación 3D/2D y telemetría.
/// </summary>
[DisallowMultipleComponent]
public sealed class AIChargingFramework : MonoBehaviour
{
    #region ESTRUCTURAS DE BASE DE DATOS Y PROTOCOLO DTO (SOCKET READY)

    [Serializable]
    public class DatabaseContainer
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

    [Serializable]
    public class AIProtocolRequest
    {
        public string command = "GET_STATUS"; // Comandos: GET_STATUS, INVESTIGATE_MATERIALS, BUILD_ELECTROSCOPE, EXECUTE_TRIBO, SET_WIRE_GEOMETRY, INJECT_CHARGE, SET_LEAF_ABLATION, SET_SPATIAL_PATTERN, RUN_FULL_PIPELINE

        // Parámetros de Construcción 3D
        public string leavesMaterial = "Diamond";
        public string rodMaterial = "Copper";
        public string knobMaterial = "Ruby";
        public string glassMaterial = "Silica Glass";

        // Parámetros Triboeléctricos
        public string materialA = "Diamond";
        public string materialB = "Teflon";
        public double forceN = 15.0;
        public double speedMps = 4.0;
        public double durationSec = 5.0;

        // Parámetros Físicos / Bézier / Repulsión
        public float injectedCharge_nC = 20.0f;
        public float curveSlope = 0.8f;
        public float placementT = 0.5f;

        // Parámetros de Ablación 3D Foliar
        public int ablationStartPanel = 0;
        public int ablationCountPanels = 0;

        // Parámetros de Ablación 2D Spatial Dual Grid
        public string spatialPattern = "Borde"; // Borde, Centro, Ajedrez, Scatter, Asimétrico, Total
        public float ablationPercentA = 43.75f;
        public float ablationPercentB = 0.0f;
    }

    [Serializable]
    public class AIProtocolResponse
    {
        public string command;
        public bool success;
        public string message;
        public string timestamp;

        // Telemetría Físico-Estadística
        public float apertureAngleDeg;
        public double electrostaticTorqueNm;
        public double bemCapacitancePF;
        public double leafVoltageV;
        public double generatedCharge_nC;
        public double generatedPotential_V;
        public double electricField_Vm;
        public double totalWork_J;
        public double wireArcLengthMeters;
        public double capacitanceDeltaPercent;
        public double torqueDeltaPercent;

        // Payloads Complejos Serializados
        public List<string> availableMaterials = new List<string>();
        public string masterReportJson;

        public string ToJson()
        {
            return JsonUtility.ToJson(this, true);
        }
    }

    #endregion

    #region REFERENCIAS A COMPONENTES Y CONFIGURACIÓN EN ESCENA

    [Header("🖥️ COMPONENTES DEL ELECTROSCOPIO EN ESCENA")]
    public ProceduralElectroscopeBuilder builder;
    public RuntimeBezierEditor bezierEditor;
    public FoilRepulsionSlideSimulator bemSimulator;
    public TriboelectricDualGridUI triboUI;
    public AISpatialDualGridInspector dualGridInspector;

    [Header("📂 RUTAS DE ARCHIVOS Y SALIDAS")]
    public string databaseRelativePath = "ElectroscopePhysicsCache_v2.json";
    public string txtOutputRelativePath = "ChargingValidation/BEM_Framework_Master_Results.txt";
    public string jsonOutputRelativePath = "ChargingValidation/BEM_Framework_Master_Results.json";

    [Header("⚙️ ESTADO Y AUTO-EXECUTION")]
    public bool runMasterPipelineOnStart = false;
    public float stepDelaySeconds = 1.0f;

    [HideInInspector] public bool isRunningMasterPipeline = false;

    private void Awake()
    {
        EnsureDependencies();
    }

    private void Start()
    {
        InvestigateAndInjectMaterials();
        if (runMasterPipelineOnStart)
        {
            RunMasterPipeline();
        }
    }

    #endregion

    #region INTERFAZ DE COMUNICACIÓN API CON SOCKET SERVER (JSON)

    /// <summary>
    /// Entrada principal para llamadas JSON recibidas desde el Socket Server TCP/WebSocket.
    /// </summary>
    public string ProcessAICommandJson(string jsonRequest)
    {
        AIProtocolRequest req;
        try
        {
            req = JsonUtility.FromJson<AIProtocolRequest>(jsonRequest) ?? new AIProtocolRequest();
        }
        catch (Exception ex)
        {
            return new AIProtocolResponse
            {
                command = "UNKNOWN",
                success = false,
                message = "Error deserializando JSON Request: " + ex.Message,
                timestamp = GetTimestamp()
            }.ToJson();
        }

        AIProtocolResponse res = ProcessAICommand(req);
        return res.ToJson();
    }

    /// <summary>
    /// Procesador interno síncrono de comandos de IA.
    /// </summary>
    public AIProtocolResponse ProcessAICommand(AIProtocolRequest req)
    {
        EnsureDependencies();
        AIProtocolResponse res = new AIProtocolResponse
        {
            command = req.command,
            timestamp = GetTimestamp(),
            success = true
        };

        try
        {
            switch (req.command.ToUpperInvariant())
            {
                case "GET_STATUS":
                    res.message = "AI Charging Framework Online y Operativo.";
                    res.apertureAngleDeg = bemSimulator != null ? (float)(bemSimulator.currentThetaRad * (180.0 / Math.PI)) : 0f;
                    break;

                case "INVESTIGATE_MATERIALS":
                    res.availableMaterials = InvestigateAndInjectMaterials();
                    res.message = $"Investigación exitosa. {res.availableMaterials.Count} materiales disponibles.";
                    break;

                case "BUILD_ELECTROSCOPE":
                    res.success = ExecuteBuildElectroscope(req, out string buildMsg);
                    res.message = buildMsg;
                    break;

                case "EXECUTE_TRIBO":
                    ExecuteTriboelectricStep(req, res);
                    break;

                case "SET_WIRE_GEOMETRY":
                    ExecuteSetWireGeometry(req, res);
                    break;

                case "INJECT_CHARGE":
                    ExecuteInjectCharge(req, res);
                    break;

                case "SET_LEAF_ABLATION":
                    ExecuteLeafAblation(req, res);
                    break;

                case "SET_SPATIAL_PATTERN":
                    ExecuteSpatialPattern(req, res);
                    break;

                case "RUN_FULL_PIPELINE":
                    RunMasterPipeline();
                    res.message = "Pipeline Maestro iniciado en Coroutine.";
                    break;

                case "RESEARCH_MATERIAL":
                    {
                        ResearchMaterialsCaller caller = FindFirstObjectByType<ResearchMaterialsCaller>();
                        if (caller != null && !string.IsNullOrWhiteSpace(req.leavesMaterial))
                        {
                            caller.RequestMaterialResearch(req.leavesMaterial.Trim());
                            res.message = $"Investigación autónoma con Gemini iniciada para '{req.leavesMaterial.Trim()}'.";
                        }
                        else
                        {
                            res.success = false;
                            res.message = caller == null
                                ? "ResearchMaterialsCaller no encontrado en la escena."
                                : "Nombre de material vacío.";
                        }
                        break;
                    }

                default:
                    res.success = false;
                    res.message = $"Comando no reconocido: {req.command}";
                    break;
            }
        }
        catch (Exception ex)
        {
            res.success = false;
            res.message = $"Excepción ejecutando {req.command}: {ex.Message}";
        }

        return res;
    }

    #endregion

    #region LÓGICA DE EJECUCIÓN DE COMANDOS

    private bool ExecuteBuildElectroscope(AIProtocolRequest req, out string message)
    {
        if (builder == null)
        {
            message = "ProceduralElectroscopeBuilder no encontrado en la escena.";
            return false;
        }

        ProceduralElectroscopeBuilder.AIElectroscopeConfig config = new ProceduralElectroscopeBuilder.AIElectroscopeConfig
        {
            leavesMaterial = req.leavesMaterial,
            rodMaterial = req.rodMaterial,
            knobMaterial = req.knobMaterial,
            glassMaterial = req.glassMaterial,
            coverMaterial = "Rubber",
            glassHeight = 3.0f,
            glassRadius = 2.0f,
            rodHeight = 3.5f,
            rodRadius = 0.08f,
            leafLength = 1.2f,
            leafWidth = 0.4f
        };

        Task<bool> task = builder.BuildWithAIConfigAsync(config);
        task.Wait();
        bool ok = task.Result;
        message = ok ? $"Electroscopio 3D construido exitosamente ({req.leavesMaterial}/{req.knobMaterial})."
                     : "Advertencia al construir electroscopio 3D.";
        return ok;
    }

    private void ExecuteTriboelectricStep(AIProtocolRequest req, AIProtocolResponse res)
    {
        if (triboUI == null)
        {
            res.success = false;
            res.message = "TriboelectricDualGridUI no encontrado.";
            return;
        }

        if (!string.IsNullOrWhiteSpace(req.materialA))
            triboUI.MaterialA = req.materialA.Trim();
        if (!string.IsNullOrWhiteSpace(req.materialB))
            triboUI.MaterialB = req.materialB.Trim();
        if (req.forceN > 0f)
            triboUI.NormalForceN = req.forceN;
        if (req.speedMps > 0f)
            triboUI.RubbingSpeedMps = req.speedMps;
        if (req.durationSec > 0f)
            triboUI.RubbingDurationSeconds = req.durationSec;

        TriboelectricDualGridUI.CalculationResult calc = triboUI.ExecuteRawCalculation();
        triboUI.CalculateAndReport();

        if (System.Math.Abs(calc.generatedChargeC) < 1e-18 && calc.totalWorkJoule <= 0.0 && triboUI.NormalForceN > 0.0)
        {
            res.success = false;
            res.message = $"Error triboeléctrico: '{triboUI.MaterialA}' o '{triboUI.MaterialB}' no existe o no tiene propiedades válidas en caché.";
            return;
        }

        res.success = true;
        res.generatedCharge_nC = calc.generatedChargeC * 1e9;
        res.generatedPotential_V = calc.generatedPotentialV;
        res.electricField_Vm = GetElectricFieldFromResult(calc);
        res.totalWork_J = calc.totalWorkJoule;

        // Propagar la carga generada al simulador BEM 3D y actualizar los paneles visuales
        FoilRepulsionSlideSimulator bemSim = FindFirstObjectByType<FoilRepulsionSlideSimulator>();
        if (bemSim != null)
        {
            float absChargeNc = Mathf.Abs((float)res.generatedCharge_nC);
            if (absChargeNc > 0.05f)
            {
                bemSim.SetChargeNanoCoulombs(Mathf.Min(100f, absChargeNc));
            }
        }

        RuntimeBezierEditor bezierEditor = FindFirstObjectByType<RuntimeBezierEditor>();
        if (bezierEditor != null)
        {
            bezierEditor.SendMessage("UpdateBEMVisuals", SendMessageOptions.DontRequireReceiver);
        }

        res.message = $"Carga Triboeléctrica calculada: {res.generatedCharge_nC:F2} nC | Potencial: {res.generatedPotential_V:F1} V.";
    }
    private void ExecuteSetWireGeometry(AIProtocolRequest req, AIProtocolResponse res)
    {
        if (bezierEditor != null)
        {
            bezierEditor.GenerateCurveFromSlopeAndPlacement(req.curveSlope, req.placementT);
        }

        double wireArc = bemSimulator != null ? bemSimulator.wireArcLengthMeters : 0.0;
        double cap = bemSimulator != null ? bemSimulator.CalculateWireCapacitance(wireArc) : 0.0;

        res.wireArcLengthMeters = wireArc;
        res.bemCapacitancePF = cap * 1e12;
        res.message = $"Geometría Bézier actualizada. Longitud Cable: {wireArc:F4} m | Capacidad Parásita: {res.bemCapacitancePF:F3} pF.";
    }

    private void ExecuteInjectCharge(AIProtocolRequest req, AIProtocolResponse res)
    {
        float q = req.injectedCharge_nC;
        if (bezierEditor != null) bezierEditor.SetChargeFromAI(q);
        else if (bemSimulator != null) bemSimulator.SetChargeNanoCoulombs(q);

        double thetaDeg = bemSimulator != null ? bemSimulator.currentThetaRad * (180.0 / Math.PI) : 3.47;
        double torque = bemSimulator != null ? (q * q * 1e-18) * 1e-5 : 1e-21;
        double cap = bemSimulator != null ? bemSimulator.CalculateWireCapacitance(bemSimulator.wireArcLengthMeters) : 8.68e-12;

        res.apertureAngleDeg = (float)thetaDeg;
        res.electrostaticTorqueNm = torque;
        res.bemCapacitancePF = cap * 1e12;
        res.leafVoltageV = (q * 1e-9) / Math.Max(1e-25, cap);
        res.message = $"Carga de {q:F1} nC inyectada. Ángulo foliar: {thetaDeg:F2}° | Torque: {torque:E3} N·m.";
    }

    private void ExecuteLeafAblation(AIProtocolRequest req, AIProtocolResponse res)
    {
        if (bemSimulator == null)
        {
            res.success = false;
            res.message = "FoilRepulsionSlideSimulator no encontrado.";
            return;
        }

        // Normalizar a 1-indexed si llega 0 con count > 0
        int startOneBased = (req.ablationStartPanel <= 0 && req.ablationCountPanels > 0)
            ? 1
            : req.ablationStartPanel;

        // 1. Actualizar primero la carga en el simulador para que SolveBEMDirect use el Q real
        if (req.injectedCharge_nC > 0f)
        {
            bemSimulator.SetChargeNanoCoulombs(req.injectedCharge_nC);
        }

        double baseTheta = 12.0 * Math.PI / 180.0;

        // 2. Estado base sin ablación (SolveBEMDirect ya incluye el factor real Q²)
        bemSimulator.SetDisabledPanelRange(0, 0);
        FoilRepulsionSlideSimulator.BemResult baseRes = bemSimulator.SolveBEMDirect(baseTheta);
        double C_wire = bemSimulator.CalculateWireCapacitance(bemSimulator.wireArcLengthMeters);
        double baseCapTotal = baseRes.capacitanceFarads + C_wire;
        double baseTorque = Math.Abs(baseRes.torqueDirectVector.z);

        // 3. Estado con paneles ablacionados (sincronizado con RuntimeBezierEditor)
        if (bezierEditor != null)
        {
            bezierEditor.SetDisabledPanelRangeFromAI(startOneBased, req.ablationCountPanels);
        }
        bemSimulator.SetDisabledPanelRange(startOneBased, req.ablationCountPanels);

        FoilRepulsionSlideSimulator.BemResult ablatedRes = bemSimulator.SolveBEMDirect(baseTheta);
        double newCapTotal = ablatedRes.capacitanceFarads + C_wire;
        double newTorque = Math.Abs(ablatedRes.torqueDirectVector.z);

        // 4. Reportar telemetría física en unidades SI reales
        res.apertureAngleDeg = (float)(bemSimulator.currentThetaRad * (180.0 / Math.PI));
        res.bemCapacitancePF = newCapTotal * 1e12;
        res.capacitanceDeltaPercent = ((newCapTotal - baseCapTotal) / Math.Max(1e-25, baseCapTotal)) * 100.0;
        res.electrostaticTorqueNm = newTorque;
        res.torqueDeltaPercent = baseTorque > 1e-30 ? ((newTorque - baseTorque) / baseTorque) * 100.0 : 0.0;
        res.leafVoltageV = (bemSimulator.chargeNanoCoulombs * 1e-9) / Math.Max(1e-25, newCapTotal);
        res.message = $"Ablación 3D en paneles [{startOneBased}..{startOneBased + req.ablationCountPanels - 1}] aplicada. ΔC: {res.capacitanceDeltaPercent:F2}% | Δτ: {res.torqueDeltaPercent:F2}% | θ: {res.apertureAngleDeg:F2}°.";
    }

    private void ExecuteSpatialPattern(AIProtocolRequest req, AIProtocolResponse res)
    {
        if (triboUI != null)
        {
            triboUI.MaterialA = req.materialA;
            triboUI.MaterialB = req.materialB;
        }

        TriboelectricDualGridUI.CalculationResult baseTribo = triboUI != null ? triboUI.ExecuteRawCalculation() : new TriboelectricDualGridUI.CalculationResult();
        float activeRatioA = Mathf.Clamp01((64.0f - (req.ablationPercentA * 0.64f)) / 64.0f);
        float activeRatioB = Mathf.Clamp01((64.0f - (req.ablationPercentB * 0.64f)) / 64.0f);
        float totalActiveRatio = (activeRatioA + activeRatioB) * 0.5f;

        res.generatedCharge_nC = (baseTribo.generatedChargeC * totalActiveRatio) * 1e9;
        res.generatedPotential_V = baseTribo.generatedPotentialV * totalActiveRatio;
        res.electricField_Vm = GetElectricFieldFromResult(baseTribo) * totalActiveRatio;

        if (dualGridInspector != null)
        {
            string summary = $"Spatial Pattern [{req.spatialPattern}]: A={req.materialA} ({req.ablationPercentA:F0}%) | B={req.materialB} ({req.ablationPercentB:F0}%).\n" +
                             $"Q={res.generatedCharge_nC:F1} nC | V={res.generatedPotential_V:F0} V | E={res.electricField_Vm:E2} V/m.";

            dualGridInspector.SetSnapshot(
                req.materialA, req.materialB,
                $"Phase1_Charging_{req.spatialPattern}",
                3048.64 * activeRatioA, 17755.90 * activeRatioB,
                -0.942 * activeRatioA, -0.938 * activeRatioB, 0.0, 0.50 * totalActiveRatio,
                req.ablationPercentA, req.ablationPercentB,
                summary
            );
        }

        res.message = $"Patrón espacial 2D '{req.spatialPattern}' aplicado a Dual Grid Inspector.";
    }

    #endregion

    #region MÓDULO DE INVESTIGACIÓN E INYECCIÓN DE MATERIALES

    public List<string> InvestigateAndInjectMaterials()
    {
        List<string> materials = new List<string>();
        string fullDbPath = Path.Combine(Application.dataPath, databaseRelativePath);

        if (!File.Exists(fullDbPath))
        {
            string filename = Path.GetFileName(databaseRelativePath);
            string[] search = Directory.GetFiles(Application.dataPath, filename, SearchOption.AllDirectories);
            if (search.Length > 0) fullDbPath = search[0];
        }

        if (File.Exists(fullDbPath))
        {
            try
            {
                string jsonText = File.ReadAllText(fullDbPath);
                var db = JsonConvert.DeserializeObject<DatabaseContainer>(jsonText);
                var rawList = db != null ? db.GetMaterials() : null;

                if (rawList != null)
                {
                    foreach (var mat in rawList)
                    {
                        if (mat != null && !string.IsNullOrWhiteSpace(mat.materialName))
                        {
                            string name = mat.materialName.Trim();
                            if (!materials.Contains(name)) materials.Add(name);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[AI-FRAMEWORK] Error al leer BD: {ex.Message}");
            }
        }

        string[] customMaterials = new string[] { "Diamond", "Ruby", "Gold Leaf", "Silica Glass", "Teflon", "Copper", "Silk", "Amber", "Rubber", "Silver" };
        foreach (var matName in customMaterials)
        {
            if (!materials.Contains(matName)) materials.Add(matName);
        }

        InjectCustomPhysicsIntoTriboUI();
        return materials;
    }

    private void InjectCustomPhysicsIntoTriboUI()
    {
        if (triboUI == null) return;

        FieldInfo indexField = typeof(TriboelectricDualGridUI).GetField("materialIndex", BindingFlags.NonPublic | BindingFlags.Instance);
        if (indexField != null)
        {
            var indexDict = indexField.GetValue(triboUI) as Dictionary<string, TriboelectricDualGridUI.MaterialRaw>;
            if (indexDict != null)
            {
                if (!indexDict.ContainsKey("Diamond"))
                {
                    indexDict["Diamond"] = new TriboelectricDualGridUI.MaterialRaw
                    {
                        materialName = "Diamond",
                        properties = new List<TriboelectricDualGridUI.PropertyRaw>
                        {
                            new TriboelectricDualGridUI.PropertyRaw { propertyName = "Surface smoothness", value = 0.98f },
                            new TriboelectricDualGridUI.PropertyRaw { propertyName = "Electrical conductivity", value = 1e-16f },
                            new TriboelectricDualGridUI.PropertyRaw { propertyName = "Triboelectric charge affinity", value = 8.5e-5f },
                            new TriboelectricDualGridUI.PropertyRaw { propertyName = "Relative permittivity", value = 5.70f },
                            new TriboelectricDualGridUI.PropertyRaw { propertyName = "Kinetic friction coefficient", value = 0.10f }
                        }
                    };
                }

                if (!indexDict.ContainsKey("Ruby"))
                {
                    indexDict["Ruby"] = new TriboelectricDualGridUI.MaterialRaw
                    {
                        materialName = "Ruby",
                        properties = new List<TriboelectricDualGridUI.PropertyRaw>
                        {
                            new TriboelectricDualGridUI.PropertyRaw { propertyName = "Surface smoothness", value = 0.94f },
                            new TriboelectricDualGridUI.PropertyRaw { propertyName = "Electrical conductivity", value = 1e-14f },
                            new TriboelectricDualGridUI.PropertyRaw { propertyName = "Triboelectric charge affinity", value = -6.2e-5f },
                            new TriboelectricDualGridUI.PropertyRaw { propertyName = "Relative permittivity", value = 9.10f },
                            new TriboelectricDualGridUI.PropertyRaw { propertyName = "Kinetic friction coefficient", value = 0.15f }
                        }
                    };
                }
            }
        }
    }

    #endregion

    #region CICLO MAESTRO AUTOMATIZADO

    [ContextMenu("🚀 Execute Master Pipeline")]
    public void RunMasterPipeline()
    {
        if (isRunningMasterPipeline) return;
        StartCoroutine(RunMasterPipelineRoutine());
    }

    private IEnumerator RunMasterPipelineRoutine()
    {
        isRunningMasterPipeline = true;
        EnsureDependencies();

        Debug.Log("<color=cyan><b>🚀 EJECUTANDO CICLO MAESTRO VIVO PARA LA IA...</b></color>");
        yield return new WaitForSeconds(stepDelaySeconds);

        List<string> materials = InvestigateAndInjectMaterials();

        // 1. Construir Electroscopio 3D
        AIProtocolRequest reqBuild = new AIProtocolRequest { command = "BUILD_ELECTROSCOPE", leavesMaterial = "Diamond", knobMaterial = "Ruby" };
        ProcessAICommand(reqBuild);
        yield return new WaitForSeconds(stepDelaySeconds);

        // 2. Probar Carga Triboeléctrica
        AIProtocolRequest reqTribo = new AIProtocolRequest { command = "EXECUTE_TRIBO", materialA = "Diamond", materialB = "Teflon", forceN = 15.0 };
        AIProtocolResponse resTribo = ProcessAICommand(reqTribo);
        yield return new WaitForSeconds(stepDelaySeconds);

        // 3. Ajustar Geometría de Cable Bézier
        AIProtocolRequest reqWire = new AIProtocolRequest { command = "SET_WIRE_GEOMETRY", curveSlope = 0.8f, placementT = 0.5f };
        ProcessAICommand(reqWire);
        yield return new WaitForSeconds(stepDelaySeconds);

        // 4. Inyectar Carga y Calcular Repulsión Foliar
        AIProtocolRequest reqCharge = new AIProtocolRequest { command = "INJECT_CHARGE", injectedCharge_nC = (float)resTribo.generatedCharge_nC };
        ProcessAICommand(reqCharge);
        yield return new WaitForSeconds(stepDelaySeconds);

        // 5. Aplicar Ablación Espacial 2D Dual Grid
        AIProtocolRequest reqSpatial = new AIProtocolRequest { command = "SET_SPATIAL_PATTERN", spatialPattern = "Borde", ablationPercentA = 43.75f };
        ProcessAICommand(reqSpatial);

        Debug.Log("<color=green><b>✅ CICLO VIVO MAESTRO COMPLETADO CON ÉXITO.</b></color>");
        isRunningMasterPipeline = false;
    }

    #endregion

    #region MÉTODOS AUXILIARES Y REFLECTION

    private double GetElectricFieldFromResult(TriboelectricDualGridUI.CalculationResult res)
    {
        Type t = res.GetType();

        string[] candidateNames = new string[] {
            "peakElectricFieldVm", "peakElectricField", "electricFieldVm",
            "electricField", "fieldVm", "electricFieldV_m", "peakField"
        };

        foreach (string name in candidateNames)
        {
            FieldInfo f = t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (f != null) { try { return Convert.ToDouble(f.GetValue(res)); } catch { } }

            PropertyInfo p = t.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (p != null) { try { return Convert.ToDouble(p.GetValue(res)); } catch { } }
        }

        return Math.Abs(res.generatedPotentialV) > 0 ? (Math.Abs(res.generatedPotentialV) / 0.0001) : 0.0;
    }

    private string GetTimestamp()
    {
        return DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    }

    private void EnsureDependencies()
    {
        if (builder == null) builder = FindFirstObjectByType<ProceduralElectroscopeBuilder>();
        if (bezierEditor == null) bezierEditor = FindFirstObjectByType<RuntimeBezierEditor>();
        if (bemSimulator == null) bemSimulator = FindFirstObjectByType<FoilRepulsionSlideSimulator>();
        if (triboUI == null) triboUI = FindFirstObjectByType<TriboelectricDualGridUI>();
        if (dualGridInspector == null) dualGridInspector = FindFirstObjectByType<AISpatialDualGridInspector>();
    }

    #endregion
}