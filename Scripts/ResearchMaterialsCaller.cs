using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using Newtonsoft.Json;

/// <summary>
/// Módulo de investigación de materiales integrado al Framework de Descubrimiento Autónomo.
/// Conecta la API de Gemini con la caché física en disco y notifica al BEM Framework 
/// cuando un nuevo material es investigado y sintetizado en tiempo real.
/// </summary>
[ExecuteAlways]
public class ResearchMaterialsCaller : MonoBehaviour
{
    [Header("🤖 Configuración de Gemini & AI Framework")]
    [Tooltip("Referencia al GeminiManager. Si está vacío, se buscará automáticamente en la escena.")]
    public GeminiManager geminiManager;

    [Header("📁 Caché y Base de Datos")]
    public string cacheFileName = "ElectroscopePhysicsCache_v2.json";

    [Header("Expansión de Base de Datos Manual")]
    [Tooltip("Lista de nuevos materiales específicos que deseas investigar manualmente.")]
    public string[] materialsToResearch = new string[]
    {
        "Gold",
        "Silver",
        "PVC",
        "Teflon",
        "Silk",
        "Wool"
    };

    [Tooltip("Si está activo, investigará las propiedades faltantes para TODOS los materiales que existan en la caché.")]
    public bool updateAllExistingMaterials = true;

    [Header("Propiedades Adicionales Opcionales")]
    [Tooltip("Las 11 propiedades base de física SIEMPRE se investigan automáticamente.")]
    [TextArea(2, 4)]
    public string extraPropertiesToInvestigate = "";

    [Tooltip("Cantidad de materiales a investigar simultáneamente antes de pausar.")]
    [Range(1, 10)]
    public int chunkSize = 4;

    // ==========================================
    // LAS 11 PROPIEDADES FISICAS BASE OBLIGATORIAS
    // ==========================================
    private readonly string[] core11Properties = new string[]
    {
        "Hex color code",
        "Density",
        "Surface smoothness",
        "Transparency",
        "Electrical conductivity",
        "Triboelectric charge affinity",
        "Work function",
        "Relative permittivity",
        "Water absorption rate",
        "Kinetic friction coefficient",
        "Dielectric strength"
    };

    private ResearchMaterials researcher;
    private bool isBusy = false;

    private void Awake()
    {
        EnsureDependencies();
    }

    private void EnsureDependencies()
    {
        if (geminiManager == null)
        {
            geminiManager = FindObjectOfType<GeminiManager>();
        }
        if (researcher == null)
        {
            researcher = new ResearchMaterials();
        }
    }

    // ============================================================================
    // MÉTODOS PÚBLICOS DE CONEXIÓN CON EL FRAMEWORK (CALLABLE BY SOCKET / AI)
    // ============================================================================

    /// <summary>
    /// Invócalo vía SendMessage("RequestMaterialResearch", "Graphene") desde BEMSocketServer.
    /// Investiga un material si no está en la caché y notifica a la simulación para actualizarse.
    /// </summary>
    public async void RequestMaterialResearch(string materialName)
    {
        if (string.IsNullOrWhiteSpace(materialName)) return;
        await ResearchAndIntegrateMaterialAsync(materialName);
    }

    /// <summary>
    /// Método asíncrono principal para investigar un material específico y forzar
    /// la actualización dinámica en el Builder y el Simulador BEM.
    /// </summary>
    public async Task<PhysicsProfileEntry> ResearchAndIntegrateMaterialAsync(string materialName)
    {
        if (isBusy)
        {
            Debug.LogWarning($"[ResearchCaller] ⚠️ El módulo de investigación está ocupado. Solicitud para '{materialName}' en espera...");
        }

        isBusy = true;
        EnsureDependencies();

        if (geminiManager == null)
        {
            Debug.LogError("[ResearchCaller] ❌ No se encontró GeminiManager en la escena. Imposible investigar vía API.");
            isBusy = false;
            return null;
        }

        string cleanName = materialName.Trim();
        Debug.Log($"<color=cyan><b>[RESEARCH FRAMEWORK]</b> 🔍 Solicitando investigación para material: '{cleanName}'</color>");

        // 1. Construir lista de 11 propiedades base + extra
        string propsQuery = GetCombinedPropertiesQuery();

        // 2. Ejecutar investigación asíncrona con Gemini
        await researcher.EnsureMaterialsAsync(new string[] { cleanName }, propsQuery, geminiManager, chunkSize: 1);

        // 3. Obtener el perfil resultante cargado desde la caché
        PhysicsProfileEntry profile = researcher.GetMaterial(cleanName);

        // 4. Notificar a los componentes de la escena para reconstruir visuals y re-calcular BEM
        NotifyFrameworkOfMaterialUpdate(cleanName);

        isBusy = false;
        return profile;
    }

    /// <summary>
    /// Investiga un lote completo de materiales enviados por el Swarm en Python.
    /// </summary>
    public async Task ResearchMaterialsBatchAsync(string[] materials)
    {
        if (materials == null || materials.Length == 0) return;

        EnsureDependencies();
        if (geminiManager == null) return;

        string propsQuery = GetCombinedPropertiesQuery();
        var validList = materials.Where(m => !string.IsNullOrWhiteSpace(m)).Select(m => m.Trim()).Distinct().ToArray();

        Debug.Log($"<color=cyan><b>[RESEARCH FRAMEWORK]</b> 📦 Investigando lote de {validList.Length} materiales...</color>");
        await researcher.EnsureMaterialsAsync(validList, propsQuery, geminiManager, chunkSize);

        if (validList.Length > 0)
        {
            NotifyFrameworkOfMaterialUpdate(validList[0]);
        }
    }

    // ============================================================================
    // MÉTODOS AUXILIARES Y ACTUALIZACIÓN EN CADENA DE LA SIMULACIÓN
    // ============================================================================

    private void NotifyFrameworkOfMaterialUpdate(string updatedMaterialName)
    {
        Debug.Log($"<color=green><b>[RESEARCH FRAMEWORK SUCCESS]</b> Material '{updatedMaterialName}' integrado.</color>");

        ProceduralElectroscopeBuilder builder = FindFirstObjectByType<ProceduralElectroscopeBuilder>();
        if (builder != null)
        {
            builder.SetLeavesMaterial(updatedMaterialName);
        }

        FoilRepulsionSlideSimulator bemSim = FindFirstObjectByType<FoilRepulsionSlideSimulator>();
        RuntimeBezierEditor bezierEditor = FindFirstObjectByType<RuntimeBezierEditor>();

        if (bezierEditor != null)
        {
            bezierEditor.SetLeavesMaterialFromAI(updatedMaterialName);
            bezierEditor.SendMessage("RebuildBEMVisualPanels", SendMessageOptions.DontRequireReceiver);
            bezierEditor.SendMessage("UpdateBEMVisuals", SendMessageOptions.DontRequireReceiver);
        }

        if (bemSim != null)
        {
            bemSim.SendMessage("RebindLeafReferences", SendMessageOptions.DontRequireReceiver);
            bemSim.SetChargeNanoCoulombs((float)bemSim.chargeNanoCoulombs);
        }

        TriboelectricDualGridUI triboUI = FindFirstObjectByType<TriboelectricDualGridUI>();
        if (triboUI != null)
        {
            triboUI.LoadAllData();
        }

        AIChargingFramework aiFramework = FindFirstObjectByType<AIChargingFramework>();
        if (aiFramework != null)
        {
            aiFramework.InvestigateAndInjectMaterials();
        }
    }

    private string GetCombinedPropertiesQuery()
    {
        var extraProps = string.IsNullOrEmpty(extraPropertiesToInvestigate)
            ? new List<string>()
            : extraPropertiesToInvestigate.Split(',')
                .Select(p => p.Trim())
                .Where(p => !string.IsNullOrEmpty(p))
                .ToList();

        List<string> allRequiredProps = core11Properties
            .Union(extraProps, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return string.Join(", ", allRequiredProps);
    }

    // ============================================================================
    // MENÚ DE CONTEXTO / INSPECTOR ORIGINAL
    // ============================================================================

    [ContextMenu("🧪 Iniciar Investigación y Expandir BD")]
    public async void ExpandDatabase()
    {
        EnsureDependencies();

        if (geminiManager == null)
        {
            Debug.LogError("[ResearchCaller] ❌ No se encontró GeminiManager en la escena.");
            return;
        }

        List<string> combinedMaterials = new List<string>();
        if (materialsToResearch != null)
        {
            combinedMaterials.AddRange(materialsToResearch);
        }

        if (updateAllExistingMaterials)
        {
            string cachePath = Path.Combine(Application.dataPath, cacheFileName);
            if (File.Exists(cachePath))
            {
                try
                {
                    string json = File.ReadAllText(cachePath);
                    PhysicsDatabase db = JsonConvert.DeserializeObject<PhysicsDatabase>(json);
                    if (db != null && db.savedPhysics != null)
                    {
                        foreach (var mat in db.savedPhysics)
                        {
                            if (!string.IsNullOrWhiteSpace(mat.materialName))
                            {
                                combinedMaterials.Add(mat.materialName);
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[ResearchCaller] ⚠️ Error leyendo base de datos existente: {e.Message}");
                }
            }
        }

        var validMaterials = combinedMaterials
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .Select(m => m.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (validMaterials.Length == 0)
        {
            Debug.LogWarning("[ResearchCaller] ⚠️ La lista de materiales está vacía.");
            return;
        }

        string propsQuery = GetCombinedPropertiesQuery();

        Debug.Log("==========================================");
        Debug.Log($" INICIANDO EXPANSIÓN/ACTUALIZACIÓN DE BASE DE DATOS ({validMaterials.Length} materiales)");
        Debug.Log("==========================================");

        try
        {
            await researcher.EnsureMaterialsAsync(validMaterials, propsQuery, geminiManager, chunkSize);
            if (validMaterials.Length > 0)
            {
                NotifyFrameworkOfMaterialUpdate(validMaterials[0]);
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"[ResearchCaller] ❌ Error en expansión de base de datos: {ex.Message}");
        }
    }
}