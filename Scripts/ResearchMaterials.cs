using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using Newtonsoft.Json;

#if UNITY_EDITOR
using UnityEditor;
#endif

/// <summary>
/// Clase C# instanciable y componente MonoBehaviour. Gestiona la base de datos física completa y las peticiones asíncronas en chunks con lógica Upsert (fusión sin pérdida).
/// </summary>
public class ResearchMaterials : MonoBehaviour
{
    private string CacheFilePath => Path.Combine(Application.dataPath, "ElectroscopePhysicsCache_v2.json");
    private PhysicsDatabase database;
    private readonly object dbLock = new object();

    public ResearchMaterials()
    {
        LoadDatabase();
    }

    public PhysicsProfileEntry GetMaterial(string materialName)
    {
        if (string.IsNullOrWhiteSpace(materialName)) return null;
        string key = materialName.Trim().ToLowerInvariant();
        return database.savedPhysics.FirstOrDefault(m => string.Equals(m.materialName.Trim(), key, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Investiga una lista de materiales en lotes (chunks). Identifica qué propiedades faltan y solo solicita esas, fusionándolas al finalizar.
    /// </summary>
    public async Task EnsureMaterialsAsync(string[] materialNames, string propertiesToInvestigate, GeminiManager manager, int chunkSize = 4)
    {
        if (manager == null)
        {
            Debug.LogError("[MaterialResearcher] GeminiManager es nulo. No se pueden hacer peticiones.");
            return;
        }

        var distinctMaterials = materialNames
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .Select(m => m.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var requestedProps = propertiesToInvestigate.Split(',')
            .Select(p => p.Trim())
            .Where(p => !string.IsNullOrEmpty(p))
            .ToList();

        bool cacheUpdated = false;

        for (int i = 0; i < distinctMaterials.Count; i += chunkSize)
        {
            var chunk = distinctMaterials.Skip(i).Take(chunkSize).ToList();
            Debug.Log($"[MaterialResearcher] ⏳ Procesando Chunk {i / chunkSize + 1} de {Mathf.CeilToInt((float)distinctMaterials.Count / chunkSize)} ({chunk.Count} materiales)...");

            var tasks = new List<Task>();

            foreach (string matName in chunk)
            {
                var existing = GetMaterial(matName);
                List<string> missingProps = new List<string>();

                if (existing != null && existing.properties != null)
                {
                    foreach (var reqProp in requestedProps)
                    {
                        if (!existing.properties.Any(p => string.Equals(p.propertyName, reqProp, StringComparison.OrdinalIgnoreCase)))
                        {
                            missingProps.Add(reqProp);
                        }
                    }
                }
                else
                {
                    missingProps = requestedProps;
                }

                if (missingProps.Count == 0)
                {
                    Debug.Log($"[MaterialResearcher] 🗄️ Material '{matName}' ya tiene TODAS las propiedades solicitadas. Saltando...");
                    continue;
                }

                string propertiesToAsk = string.Join(", ", missingProps);
                Debug.Log($"[MaterialResearcher] 🔎 Añadiendo '{matName}' a la cola para propiedades: [{propertiesToAsk}]...");

                tasks.Add(FetchAndUpsertAsync(matName, propertiesToAsk, manager));
            }

            if (tasks.Count > 0)
            {
                await Task.WhenAll(tasks);
                cacheUpdated = true;

                if (i + chunkSize < distinctMaterials.Count)
                {
                    Debug.Log("[MaterialResearcher] 🛑 Chunk terminado. Esperando 3 segundos...");
                    await Task.Delay(3000);
                }
            }
        }

        if (cacheUpdated)
        {
            SaveDatabase();
        }
    }

    private async Task FetchAndUpsertAsync(string materialName, string properties, GeminiManager manager)
    {
        int maxAttempts = 2;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                string jsonResponse = await ConsultMaterialTaskAsync(manager, materialName, properties);

                if (!string.IsNullOrEmpty(jsonResponse))
                {
                    MaterialApiResponse apiResponse = JsonConvert.DeserializeObject<MaterialApiResponse>(jsonResponse);
                    if (apiResponse != null && !string.IsNullOrEmpty(apiResponse.materialName))
                    {
                        UpsertCompleteMaterial(apiResponse);
                        return;
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[MaterialResearcher] ❌ Error en '{materialName}' (Intento {attempt}/{maxAttempts}): {e.Message}");
                if (attempt == maxAttempts) return;
                await Task.Delay(2000);
            }
        }
    }

    private Task<string> ConsultMaterialTaskAsync(GeminiManager manager, string material, string properties)
    {
        var tcs = new TaskCompletionSource<string>();

        manager.ConsultMaterial(material, properties,
            onSuccess: (json) => tcs.TrySetResult(json),
            onError: (err) => tcs.TrySetException(new Exception(err))
        );

        return tcs.Task;
    }

    private void UpsertCompleteMaterial(MaterialApiResponse apiData)
    {
        lock (dbLock)
        {
            string key = apiData.materialName.Trim();
            var entry = database.savedPhysics.FirstOrDefault(m => string.Equals(m.materialName.Trim(), key, StringComparison.OrdinalIgnoreCase));

            if (entry != null)
            {
                if (entry.properties == null) entry.properties = new List<PropertyData>();
                if (apiData.properties != null)
                {
                    foreach (var newProp in apiData.properties)
                    {
                        var existingProp = entry.properties.FirstOrDefault(p => string.Equals(p.propertyName, newProp.propertyName, StringComparison.OrdinalIgnoreCase));
                        if (existingProp != null)
                        {
                            int index = entry.properties.IndexOf(existingProp);
                            entry.properties[index] = newProp;
                        }
                        else
                        {
                            entry.properties.Add(newProp);
                        }
                    }
                }

                if (entry.sources == null) entry.sources = new List<SourceData>();
                if (apiData.sources != null)
                {
                    foreach (var newSource in apiData.sources)
                    {
                        if (!entry.sources.Any(s => string.Equals(s.url, newSource.url, StringComparison.OrdinalIgnoreCase)))
                        {
                            entry.sources.Add(newSource);
                        }
                    }
                }

                entry.lastResearchUtc = DateTime.UtcNow.ToString("o");
                Debug.Log($"[MaterialResearcher] 🔄 Material existente actualizado (Fusión inteligente de propiedades): {key}");
            }
            else
            {
                entry = new PhysicsProfileEntry
                {
                    materialName = key,
                    properties = apiData.properties ?? new List<PropertyData>(),
                    sources = apiData.sources ?? new List<SourceData>(),
                    researchStatus = "Verified",
                    lastResearchUtc = DateTime.UtcNow.ToString("o")
                };
                database.savedPhysics.Add(entry);
                Debug.Log($"[MaterialResearcher] 🆕 Nuevo material creado con árbol completo en BD: {key}");
            }
        }
    }

    private void LoadDatabase()
    {
        if (File.Exists(CacheFilePath))
        {
            try
            {
                string json = File.ReadAllText(CacheFilePath);
                database = JsonConvert.DeserializeObject<PhysicsDatabase>(json) ?? new PhysicsDatabase();
            }
            catch (Exception e) { Debug.LogError($"[MaterialResearcher] Error cargando caché:\n{e.Message}"); }
        }
        else
        {
            database = new PhysicsDatabase();
        }
    }

    private void SaveDatabase()
    {
        lock (dbLock)
        {
            try
            {
                database.savedPhysics = database.savedPhysics.OrderBy(e => e.materialName).ToList();
                string json = JsonConvert.SerializeObject(database, Formatting.Indented);
#if UNITY_EDITOR
                AssetDatabase.StartAssetEditing();
#endif
                File.WriteAllText(CacheFilePath, json);
#if UNITY_EDITOR
                AssetDatabase.StopAssetEditing();
                AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
#endif
            }
            catch (Exception e) { Debug.LogError($"[MaterialResearcher] Error guardando caché:\n{e.Message}"); }
        }
    }
}

[Serializable]
public class PhysicsDatabase
{
    public List<PhysicsProfileEntry> savedPhysics = new List<PhysicsProfileEntry>();
}

[Serializable]
public class PhysicsProfileEntry
{
    public string materialName;
    public List<PropertyData> properties;
    public List<SourceData> sources;
    public string researchStatus;
    public string lastResearchUtc;
}

[Serializable]
public class MaterialApiResponse
{
    public string materialName;
    public List<PropertyData> properties;
    public List<SourceData> sources;
}

[Serializable]
public class PropertyData
{
    public string propertyName;
    public string meaning;
    public float value;
    public string value_range_or_uncertainty;
    public string unit;
    public string status;
    public ScalesData scales;
    public List<SourceData> sources;
}

[Serializable]
public class ScalesData
{
    public ScaleDetail scale1_charging;
    public ScaleDetail scale2_transfer;
    public ScaleDetail scale3_repulsion;
}

[Serializable]
public class ScaleDetail
{
    public string status;
    public string dependent_quantity;
    public List<string> independent_variables;
    public string formula;
    public Dictionary<string, string> variables_meaning;
    public string output_unit;
    public string effect_type;
    public string relationship_type;
    public string assumptions_and_validity;
    public bool ready_for_quantitative_prediction;
    public string confidence_and_limitations;
}

[Serializable]
public class SourceData
{
    public string url;
    public string supports;
}