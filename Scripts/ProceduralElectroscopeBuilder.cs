using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;

#if UNITY_EDITOR
using UnityEditor;
#endif

/// <summary>
/// Constructor Procedural de Electroscopios integrado para control autónomo por IA.
/// Permite reconfigurar materiales, dimensiones y geometrías dinámicamente desde el AI Framework.
/// </summary>
[ExecuteAlways]
public class ProceduralElectroscopeBuilder : MonoBehaviour
{
    // ==========================================
    // EVENTO DE NOTIFICACIÓN PARA RUNTIME BEZIER EDITOR
    // ==========================================
    public static event System.Action OnElectroscopeBuilt;

    // ==========================================
    // STRUCTS PARA CONFIGURACIÓN DE IA
    // ==========================================
    [Serializable]
    public class AIElectroscopeConfig
    {
        public string glassMaterial = "Silica Glass";
        public string coverMaterial = "Rubber";
        public string rodMaterial = "Copper";
        public string knobMaterial = "Brass";
        public string leavesMaterial = "Aluminum Foil";

        // Dimensiones físicas personalizables (en metros)
        public float glassHeight = 3.0f;
        public float glassRadius = 2.0f;
        public float rodHeight = 3.5f;
        public float rodRadius = 0.08f;
        public float knobRadius = 0.5f;
        public float leafLength = 1.2f;
        public float leafWidth = 0.4f;
        public float leafThickness = 0.02f;
    }

    // ==========================================
    // GENERATION CONFIGURATION
    // ==========================================
    [Header("Generation Configuration")]
    public string rootObjectName = "Electroscope";
    public float spawnDistance = 5f;

    [Header("Gemini Manager")]
    [Tooltip("Optional. If empty, the script will search for GeminiManager automatically.")]
    public GeminiManager geminiManager;

    // ==========================================
    // MATERIAL NAMES (Configurables por String / IA)
    // ==========================================
    [Header("Material Names")]
    public string glassContainerMaterial = "Silica Glass";
    public string topCoverMaterial = "Rubber";
    public string centralRodMaterial = "Copper";
    public string topKnobMaterial = "Brass";
    public string leavesMaterial = "Aluminum Foil";

    // ==========================================
    // DIMENSIONES VARIABLES (Configurables por IA)
    // ==========================================
    [Header("Procedural Dimensions (AI Dynamic)")]
    public float alturaVidrio = 3.0f;
    public float radioVidrio = 2.0f;
    public float alturaBarra = 3.5f;
    public float radioBarra = 0.08f;
    public float radioPerilla = 0.5f;
    public float largoLamina = 1.2f;
    public float anchoLamina = 0.4f;
    public float grosorLamina = 0.02f;

    [Header("Propiedades Adicionales Opcionales")]
    [Tooltip("Las 11 propiedades base de física SIEMPRE se investigan automáticamente.")]
    [TextArea(2, 4)]
    public string extraPropertiesToInvestigate = "";

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

    // ==========================================
    // VISUAL MATERIAL OVERRIDES
    // ==========================================
    [Header("Visual Material Overrides")]
    public Material glassMaterialOverride;
    public Material metalMaterialOverride;
    public Material foilMaterialOverride;

    [Header("Compatibilidad con Scripts Externos")]
    public Material leavesMaterialReference;

    private ResearchMaterials researcher;
    private bool isBuilding = false;

    async void Start()
    {
        if (!Application.isPlaying) return;
        BuildElectroscope();
    }

    // ==========================================
    // INTERFAZ Y MÉTODOS PÚBLICOS PARA LA IA
    // ==========================================

    /// <summary>
    /// Método directo invocado por el AIChargingFramework o BEMSocketServer para reconfigurar todo el electroscopio.
    /// </summary>
    public async Task<bool> BuildWithAIConfigAsync(AIElectroscopeConfig config)
    {
        if (config == null) return false;

        // Actualizar nombres de materiales
        if (!string.IsNullOrEmpty(config.glassMaterial)) glassContainerMaterial = config.glassMaterial;
        if (!string.IsNullOrEmpty(config.coverMaterial)) topCoverMaterial = config.coverMaterial;
        if (!string.IsNullOrEmpty(config.rodMaterial)) centralRodMaterial = config.rodMaterial;
        if (!string.IsNullOrEmpty(config.knobMaterial)) topKnobMaterial = config.knobMaterial;
        if (!string.IsNullOrEmpty(config.leavesMaterial)) leavesMaterial = config.leavesMaterial;

        // Actualizar dimensiones físicas si son válidas
        if (config.glassHeight > 0) alturaVidrio = config.glassHeight;
        if (config.glassRadius > 0) radioVidrio = config.glassRadius;
        if (config.rodHeight > 0) alturaBarra = config.rodHeight;
        if (config.rodRadius > 0) radioBarra = config.rodRadius;
        if (config.knobRadius > 0) radioPerilla = config.knobRadius;
        if (config.leafLength > 0) largoLamina = config.leafLength;
        if (config.leafWidth > 0) anchoLamina = config.leafWidth;
        if (config.leafThickness > 0) grosorLamina = config.leafThickness;

        return await BuildElectroscopeInternalAsync();
    }

    /// <summary>
    /// Permite a la IA cambiar solo el material de las láminas (Ej: "Gold", "Aluminum Foil", "Graphene").
    /// </summary>
    public async void SetLeavesMaterial(string newMaterialName)
    {
        if (string.IsNullOrWhiteSpace(newMaterialName)) return;
        leavesMaterial = newMaterialName;
        await BuildElectroscopeInternalAsync();
    }

    /// <summary>
    /// Permite a la IA actualizar todos los materiales por string.
    /// </summary>
    public async void SetMaterials(string glass, string cover, string rod, string knob, string leaves)
    {
        if (!string.IsNullOrEmpty(glass)) glassContainerMaterial = glass;
        if (!string.IsNullOrEmpty(cover)) topCoverMaterial = cover;
        if (!string.IsNullOrEmpty(rod)) centralRodMaterial = rod;
        if (!string.IsNullOrEmpty(knob)) topKnobMaterial = knob;
        if (!string.IsNullOrEmpty(leaves)) leavesMaterial = leaves;

        await BuildElectroscopeInternalAsync();
    }

    /// <summary>
    /// Invocación pública por defecto para reconstruir.
    /// </summary>
    [ContextMenu("Build and Paint Electroscope Procedurally")]
    public async void BuildElectroscope()
    {
        await BuildElectroscopeInternalAsync();
    }

    // ==========================================
    // LÓGICA DE CONSTRUCCIÓN INTERNA
    // ==========================================

    private async Task<bool> BuildElectroscopeInternalAsync()
    {
        if (isBuilding)
        {
            Debug.LogWarning("[ProceduralBuilder] ⚠️ Reconstrucción en progreso. Solicitud omitida.");
            return false;
        }

        isBuilding = true;

        if (researcher == null) researcher = new ResearchMaterials();
        EnsureGeminiManager();

        Debug.Log("==========================================");
        Debug.Log(" STARTING PROCEDURAL ELECTROSCOPE BUILD (AI)");
        Debug.Log("==========================================");

        PurgeExistingElectroscope();

        // 1. Agrupar todos los materiales configurados
        string[] allMaterials = new string[]
        {
            glassContainerMaterial,
            topCoverMaterial,
            centralRodMaterial,
            topKnobMaterial,
            leavesMaterial
        }.Where(m => !string.IsNullOrWhiteSpace(m)).Distinct().ToArray();

        // 2. Construir lista de propiedades
        var extraProps = string.IsNullOrEmpty(extraPropertiesToInvestigate)
            ? new List<string>()
            : extraPropertiesToInvestigate.Split(',')
                .Select(p => p.Trim())
                .Where(p => !string.IsNullOrEmpty(p))
                .ToList();

        List<string> allRequiredProps = core11Properties
            .Union(extraProps, StringComparer.OrdinalIgnoreCase)
            .ToList();

        string propsQuery = string.Join(", ", allRequiredProps);
        List<string> materialsToResearch = new List<string>();

        // 3. Pre-Revisión Estricta en Caché
        foreach (string mat in allMaterials)
        {
            var profile = researcher.GetMaterial(mat);
            bool needsResearch = false;

            if (profile == null || profile.properties == null || profile.properties.Count == 0)
            {
                needsResearch = true;
            }
            else
            {
                foreach (var reqProp in allRequiredProps)
                {
                    if (!profile.properties.Any(p => string.Equals(p.propertyName, reqProp, StringComparison.OrdinalIgnoreCase)))
                    {
                        needsResearch = true;
                        break;
                    }
                }
            }

            if (needsResearch) materialsToResearch.Add(mat);
        }

        // 4. Investigar materiales si faltan
        if (materialsToResearch.Count > 0)
        {
            Debug.Log($"[ProceduralBuilder] 🔍 Investigando {materialsToResearch.Count} materiales para la IA...");
            if (geminiManager != null)
            {
                await researcher.EnsureMaterialsAsync(materialsToResearch.ToArray(), propsQuery, geminiManager, chunkSize: 4);
            }
        }

        // 5. Obtener los perfiles actualizados
        PhysicsProfileEntry glassProfile = researcher.GetMaterial(glassContainerMaterial);
        PhysicsProfileEntry coverProfile = researcher.GetMaterial(topCoverMaterial);
        PhysicsProfileEntry rodProfile = researcher.GetMaterial(centralRodMaterial);
        PhysicsProfileEntry knobProfile = researcher.GetMaterial(topKnobMaterial);
        PhysicsProfileEntry leavesProfile = researcher.GetMaterial(leavesMaterial);

        // 6. Generar geometría
        GenerateGeometry(glassProfile, coverProfile, rodProfile, knobProfile, leavesProfile);

        Debug.Log("==========================================");
        Debug.Log(" ELECTROSCOPE BUILD COMPLETE (AI READY)");
        Debug.Log("==========================================");

        isBuilding = false;
        return true;
    }

    private void EnsureGeminiManager()
    {
        if (geminiManager != null) return;
        geminiManager = GetComponent<GeminiManager>();
        if (geminiManager != null) return;
        geminiManager = FindObjectOfType<GeminiManager>();
    }

    private void PurgeExistingElectroscope()
    {
        GameObject existingRoot = GameObject.Find(rootObjectName);
        if (existingRoot != null)
        {
            if (Application.isPlaying) Destroy(existingRoot);
            else DestroyImmediate(existingRoot);
        }

        GameObject orphanSimulator = GameObject.Find("Foil_Repulsion_Simulator");
        if (orphanSimulator != null)
        {
            if (Application.isPlaying) Destroy(orphanSimulator);
            else DestroyImmediate(orphanSimulator);
        }

        GameObject orphanAssembly = GameObject.Find("3D_Wire_Assembly");
        if (orphanAssembly != null)
        {
            if (Application.isPlaying) Destroy(orphanAssembly);
            else DestroyImmediate(orphanAssembly);
        }
    }

    private Color GetMaterialColor(PhysicsProfileEntry profile, string defaultHex)
    {
        Color defaultColor = Color.gray;
        if (ColorUtility.TryParseHtmlString(defaultHex, out Color parsedDefault)) defaultColor = parsedDefault;
        if (profile == null || profile.properties == null) return defaultColor;

        var prop = profile.properties.Find(p => p.propertyName != null && p.propertyName.Equals("Hex color code", StringComparison.OrdinalIgnoreCase));
        if (prop == null) return defaultColor;

        if (prop.value > 0)
        {
            int rgb = Convert.ToInt32(prop.value);
            float r = ((rgb >> 16) & 0xFF) / 255f;
            float g = ((rgb >> 8) & 0xFF) / 255f;
            float b = (rgb & 0xFF) / 255f;
            return new Color(r, g, b, 1.0f);
        }

        if (!string.IsNullOrEmpty(prop.value_range_or_uncertainty) && ColorUtility.TryParseHtmlString(prop.value_range_or_uncertainty, out Color parsedHex))
        {
            return parsedHex;
        }

        return defaultColor;
    }

    private float GetSmoothness(PhysicsProfileEntry profile)
    {
        if (profile == null || profile.properties == null) return 0.5f;
        var prop = profile.properties.Find(p => p.propertyName != null && p.propertyName.Equals("Surface smoothness", StringComparison.OrdinalIgnoreCase));
        return prop != null ? (float)prop.value : 0.5f;
    }

    private void ApplyPhysics(GameObject obj, PhysicsProfileEntry profile)
    {
        if (obj == null || profile == null || profile.properties == null) return;
        var densityProp = profile.properties.Find(p => p.propertyName != null && p.propertyName.Equals("Density", StringComparison.OrdinalIgnoreCase));
        if (densityProp != null)
        {
            Rigidbody rb = obj.GetComponent<Rigidbody>();
            if (rb == null) rb = obj.AddComponent<Rigidbody>();

            Collider col = obj.GetComponent<Collider>();
            float vol = 0.0001f;
            if (col != null) vol = col.bounds.size.x * col.bounds.size.y * col.bounds.size.z;

            rb.mass = Mathf.Max(0.001f, (float)densityProp.value * vol);
            rb.isKinematic = true;
        }
    }

    private void GenerateGeometry(
        PhysicsProfileEntry glass,
        PhysicsProfileEntry cover,
        PhysicsProfileEntry rod,
        PhysicsProfileEntry knob,
        PhysicsProfileEntry leaves
    )
    {
        Vector3 spawnPosition = transform.position + transform.forward * spawnDistance;
        Camera mainCam = Camera.main;
        if (mainCam != null)
        {
            spawnPosition = mainCam.transform.position + mainCam.transform.forward * spawnDistance;
        }

        GameObject root = new GameObject(rootObjectName);
        root.transform.position = spawnPosition;
        root.transform.rotation = Quaternion.identity;

        Shader targetShader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard") ?? Shader.Find("Diffuse");
        if (targetShader == null) return;

        Material glassMat = CreateDynamicMaterialFromColor(targetShader, GetMaterialColor(glass, "#8FD3FF"), GetSmoothness(glass), true);
        Material coverMat = CreateDynamicMaterialFromColor(targetShader, GetMaterialColor(cover, "#3B3B3B"), GetSmoothness(cover), false);
        Material rodMat = CreateDynamicMaterialFromColor(targetShader, GetMaterialColor(rod, "#B87333"), GetSmoothness(rod), false);
        Material knobMat = CreateDynamicMaterialFromColor(targetShader, GetMaterialColor(knob, "#D4AF37"), GetSmoothness(knob), false);
        Material leavesMat = CreateDynamicMaterialFromColor(targetShader, GetMaterialColor(leaves, "#CDCDCD"), GetSmoothness(leaves), false);

        leavesMaterialReference = leavesMat;

        // Vidrio
        GameObject vidrio = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        vidrio.name = "Glass_Jar";
        vidrio.transform.SetParent(root.transform);
        vidrio.transform.localPosition = new Vector3(0, alturaVidrio / 2f, 0);
        vidrio.transform.localScale = new Vector3(radioVidrio * 2f, alturaVidrio / 2f, radioVidrio * 2f);
        SetMaterial(vidrio, glassMaterialOverride != null ? glassMaterialOverride : glassMat);
        ApplyPhysics(vidrio, glass);

        // Tapa
        GameObject bolsa = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        bolsa.name = "Top_Shield_Bag";
        bolsa.transform.SetParent(root.transform);
        bolsa.transform.localPosition = new Vector3(0, alturaVidrio + 0.1f, 0);
        bolsa.transform.localScale = new Vector3(radioVidrio * 2.1f, 0.2f, radioVidrio * 2.1f);
        SetMaterial(bolsa, coverMat);
        ApplyPhysics(bolsa, cover);

        // Barra
        GameObject barra = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        barra.name = "Conductive_Rod";
        barra.transform.SetParent(root.transform);
        barra.transform.localPosition = new Vector3(0, alturaVidrio - (alturaBarra / 2f) + 0.5f, 0);
        barra.transform.localScale = new Vector3(radioBarra * 2f, alturaBarra / 2f, radioBarra * 2f);
        SetMaterial(barra, metalMaterialOverride != null ? metalMaterialOverride : rodMat);
        ApplyPhysics(barra, rod);

        // Perilla
        GameObject perilla = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        perilla.name = "Metal_Knob";
        perilla.transform.SetParent(root.transform);
        float cupulaBarra = barra.transform.localPosition.y + (alturaBarra / 2f);
        perilla.transform.localPosition = new Vector3(0, cupulaBarra + (radioPerilla / 2f), 0);
        perilla.transform.localScale = Vector3.one * (radioPerilla * 2f);
        SetMaterial(perilla, metalMaterialOverride != null ? metalMaterialOverride : knobMat);
        ApplyPhysics(perilla, knob);

        // Anclaje de Láminas (Curve_Attachment_Plate)
        float baseBarraY = barra.transform.localPosition.y - (alturaBarra / 2f);
        Vector3 puntoAnclajeLaminas = new Vector3(0, baseBarraY, 0);
        float plateThickness = 0.02f;
        float plateRadius = radioBarra * 1.5f;

        GameObject curveAttachment = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        curveAttachment.name = "Curve_Attachment_Plate";
        curveAttachment.transform.SetParent(root.transform);
        curveAttachment.transform.localPosition = puntoAnclajeLaminas - new Vector3(0, plateThickness, 0);
        curveAttachment.transform.localScale = new Vector3(plateRadius * 2f, plateThickness, plateRadius * 2f);
        SetMaterial(curveAttachment, metalMaterialOverride != null ? metalMaterialOverride : rodMat);

        GameObject leavesAnchor = new GameObject("Leaves_Anchor");
        leavesAnchor.transform.SetParent(root.transform);
        leavesAnchor.transform.localPosition = curveAttachment.transform.localPosition - new Vector3(0, plateThickness, 0);

        // Lámina Izquierda
        GameObject bisagraIzq = new GameObject("Left_Leaf_Hinge");
        bisagraIzq.transform.SetParent(leavesAnchor.transform);
        bisagraIzq.transform.localPosition = new Vector3(-grosorLamina, 0, 0);
        GameObject cuboIzq = GameObject.CreatePrimitive(PrimitiveType.Cube);
        cuboIzq.name = "Repelling_Leaf_Left";
        cuboIzq.transform.SetParent(bisagraIzq.transform);
        cuboIzq.transform.localPosition = new Vector3(0, -largoLamina / 2f, 0);
        cuboIzq.transform.localScale = new Vector3(grosorLamina, largoLamina, anchoLamina);
        SetMaterial(cuboIzq, foilMaterialOverride != null ? foilMaterialOverride : leavesMat);
        ApplyPhysics(cuboIzq, leaves);

        // Lámina Derecha
        GameObject bisagraDer = new GameObject("Right_Leaf_Hinge");
        bisagraDer.transform.SetParent(leavesAnchor.transform);
        bisagraDer.transform.localPosition = new Vector3(grosorLamina, 0, 0);
        GameObject cuboDer = GameObject.CreatePrimitive(PrimitiveType.Cube);
        cuboDer.name = "Repelling_Leaf_Right";
        cuboDer.transform.SetParent(bisagraDer.transform);
        cuboDer.transform.localPosition = new Vector3(0, -largoLamina / 2f, 0);
        cuboDer.transform.localScale = new Vector3(grosorLamina, largoLamina, anchoLamina);
        SetMaterial(cuboDer, foilMaterialOverride != null ? foilMaterialOverride : leavesMat);
        ApplyPhysics(cuboDer, leaves);

        // NOTIFICAR A RUNTIME BEZIER EDITOR QUE LA GEOMETRÍA YA EXISTE EN LA ESCENA
        OnElectroscopeBuilt?.Invoke();
    }

    private Material CreateDynamicMaterialFromColor(Shader shader, Color baseColor, float metallic, bool isTransparent)
    {
        Material mat = new Material(shader);
        metallic = Mathf.Clamp01(metallic);

        if (isTransparent)
        {
            baseColor.a = 0.30f;
            mat.color = baseColor;

            if (mat.shader.name.Contains("Universal Render Pipeline"))
            {
                mat.SetFloat("_Surface", 1.0f);
                mat.SetFloat("_Blend", 0.0f);
                mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                mat.SetInt("_ZWrite", 0);
                mat.renderQueue = 3000;
                mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            }
            else
            {
                mat.SetFloat("_Mode", 3);
                mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                mat.SetInt("_ZWrite", 0);
                mat.renderQueue = 3000;
                mat.DisableKeyword("_ALPHATEST_ON");
                mat.EnableKeyword("_ALPHABLEND_ON");
                mat.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            }
        }
        else
        {
            mat.color = baseColor;
            if (mat.HasProperty("_Metallic")) mat.SetFloat("_Metallic", metallic);
            if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", metallic);
            if (mat.HasProperty("_Glossiness")) mat.SetFloat("_Glossiness", metallic);
        }

        return mat;
    }

    private void SetMaterial(GameObject obj, Material mat)
    {
        if (obj == null || mat == null) return;
        Renderer renderer = obj.GetComponent<Renderer>();
        if (renderer == null) return;

        if (Application.isPlaying) renderer.material = mat;
        else renderer.sharedMaterial = mat;
    }
}