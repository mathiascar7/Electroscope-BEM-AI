using System;
using System.IO;
using System.Text;
using System.Collections.Generic;
using UnityEngine;
using Unity.Collections;
using Unity.Jobs;
using Unity.Burst;
using Unity.Mathematics;
using Newtonsoft.Json;

namespace ElectroscopePhysics
{
    #region Contenedores JSON
    [Serializable]
    public class PhysicsDatabaseContainer
    {
        [JsonProperty("savedPhysics")]
        public List<MaterialPhysicsRaw> savedPhysicsMain;

        [JsonProperty("saved Physics")]
        public List<MaterialPhysicsRaw> savedPhysicsAlt;

        public List<MaterialPhysicsRaw> GetMaterials()
        {
            if (savedPhysicsMain != null && savedPhysicsMain.Count > 0) return savedPhysicsMain;
            if (savedPhysicsAlt != null && savedPhysicsAlt.Count > 0) return savedPhysicsAlt;
            return new List<MaterialPhysicsRaw>();
        }
    }

    [Serializable]
    public class MaterialPhysicsRaw
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
    #endregion

    #region Estructuras Físicas y Configuración del Ganador
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    public struct MaterialPhysicsData
    {
        public float density;
        public float surfaceSmoothness;
        public float transparency;
        public float electricalConductivity;
        public float chargeAffinity;
        public float workFunction;
        public float relativePermittivity;
        public float waterAbsorptionRate;
        public float kineticFriction;
        public float dielectricStrength;
        public float meltingPointKelvin;
        public uint rgbColor24Bit;
    }

    [Serializable]
    public class BEMWinnerConfig
    {
        public string timestamp;
        public string winnerRodMaterial;
        public string winnerElectroscopeMaterial;
        public MaterialPhysicsData rodPhysics;
        public MaterialPhysicsData electroscopePhysics;

        // Escenario Ganador
        public float normalForceN;
        public float rubbingSpeedMps;
        public float durationSeconds;
        public float frictionForceN;
        public float totalWorkJoule;

        // Métricas de Rendimiento BEM Ganadoras
        public float generatedChargeC;
        public float generatedPotentialV;
        public float maxChargeDensityC_m2;
        public float peakElectricFieldV_m;
        public bool isConductor;
        public int bemIterationsUsed;
        public float bemSolverTolerance;
    }
    #endregion

    #region Jobs de Burst Compiler
    [BurstCompile(FloatMode = FloatMode.Fast, FloatPrecision = FloatPrecision.Standard, CompileSynchronously = true)]
    public struct BuildGreenMatrixJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float3> centroids;
        [ReadOnly] public NativeArray<float> areas;
        [ReadOnly] public NativeArray<float> relativePermittivities;
        public NativeArray<float> greenMatrix;
        public int N;

        public void Execute(int index)
        {
            int i = index / N;
            int j = index % N;

            const float eps0 = 8.854187817e-12f;

            if (i == j)
            {
                float r_eq = math.sqrt(areas[i] / math.PI);
                float perm = math.max(relativePermittivities[i], 1.0f);
                greenMatrix[index] = r_eq / (math.PI * eps0 * perm);
            }
            else
            {
                float3 diff = centroids[i] - centroids[j];
                float dist = math.max(math.length(diff), 1e-4f);
                float perm = math.max(relativePermittivities[j], 1.0f);
                greenMatrix[index] = areas[j] / (4.0f * math.PI * eps0 * perm * dist);
            }
        }
    }

    [BurstCompile(CompileSynchronously = true)]
    public struct BiCGSTABSolverJob : IJob
    {
        [ReadOnly] public NativeArray<float> A;
        [ReadOnly] public NativeArray<float> b;
        public NativeArray<float> x;
        public int N;
        public int maxIterations;
        public float tolerance;

        public void Execute()
        {
            NativeArray<float> r = new NativeArray<float>(N, Allocator.Temp);
            NativeArray<float> r_hat = new NativeArray<float>(N, Allocator.Temp);
            NativeArray<float> p = new NativeArray<float>(N, Allocator.Temp);
            NativeArray<float> v = new NativeArray<float>(N, Allocator.Temp);
            NativeArray<float> s = new NativeArray<float>(N, Allocator.Temp);
            NativeArray<float> t = new NativeArray<float>(N, Allocator.Temp);

            for (int i = 0; i < N; i++)
            {
                float Ax = 0.0f;
                for (int j = 0; j < N; j++) Ax += A[i * N + j] * x[j];
                r[i] = b[i] - Ax;
                r_hat[i] = r[i];
                p[i] = r[i];
            }

            float rho_old = 1.0f, alpha = 1.0f, omega = 1.0f;

            for (int iter = 0; iter < maxIterations; iter++)
            {
                float rho_new = 0.0f;
                for (int i = 0; i < N; i++) rho_new += r_hat[i] * r[i];

                if (math.abs(rho_new) < 1e-12f) break;

                if (iter > 0)
                {
                    float beta = (rho_new / rho_old) * (alpha / omega);
                    for (int i = 0; i < N; i++)
                        p[i] = r[i] + beta * (p[i] - omega * v[i]);
                }

                for (int i = 0; i < N; i++)
                {
                    v[i] = 0.0f;
                    for (int j = 0; j < N; j++) v[i] += A[i * N + j] * p[j];
                }

                float r_hat_v = 0.0f;
                for (int i = 0; i < N; i++) r_hat_v += r_hat[i] * v[i];
                alpha = rho_new / (r_hat_v + 1e-12f);

                float s_norm = 0.0f;
                for (int i = 0; i < N; i++)
                {
                    s[i] = r[i] - alpha * v[i];
                    s_norm += s[i] * s[i];
                }

                if (math.sqrt(s_norm) < tolerance)
                {
                    for (int i = 0; i < N; i++) x[i] += alpha * p[i];
                    break;
                }

                for (int i = 0; i < N; i++)
                {
                    t[i] = 0.0f;
                    for (int j = 0; j < N; j++) t[i] += A[i * N + j] * s[j];
                }

                float t_t = 0.0f, t_s = 0.0f;
                for (int i = 0; i < N; i++)
                {
                    t_t += t[i] * t[i];
                    t_s += t[i] * s[i];
                }

                omega = t_s / (t_t + 1e-12f);

                for (int i = 0; i < N; i++)
                {
                    x[i] += alpha * p[i] + omega * s[i];
                    r[i] = s[i] - omega * t[i];
                }

                rho_old = rho_new;

                float r_norm = 0.0f;
                for (int i = 0; i < N; i++) r_norm += r[i] * r[i];
                if (math.sqrt(r_norm) < tolerance) break;
            }

            r.Dispose(); r_hat.Dispose(); p.Dispose();
            v.Dispose(); s.Dispose(); t.Dispose();
        }
    }
    #endregion

    /// <summary>
    /// Engine Autónomo de Hipertuning BEM.
    /// Evalúa la matriz de escenarios, registra CSV con formato numérico invariante y exporta BEM_Winner_Config.json con el escenario ganador.
    /// </summary>
    [RequireComponent(typeof(MeshFilter))]
    public class vChargingHyperTuneEngine : MonoBehaviour
    {
        [Header("⚙️ Matriz de Escenarios (Auto-Hypertune)")]
        public float[] testNormalForcesN = new float[] { 1.0f, 5.0f, 10.0f };
        public float[] testRubbingSpeedsMps = new float[] { 0.5f, 1.5f, 3.0f };
        public float[] testDurationsSeconds = new float[] { 1.0f, 3.0f, 5.0f };

        [Header("🧪 Prueba Manual Rápida")]
        public string testRodMaterial = "Teflon";
        public string testElectroscopeMaterial = "Aluminum Foil";

        private PhysicsDatabaseContainer dbCache;

        // Buffers Nativos
        private NativeArray<float3> centroids;
        private NativeArray<float3> normals;
        private NativeArray<float> areas;
        private NativeArray<float> relativePermittivities;
        private NativeArray<float> surfaceCharges;
        private NativeArray<float> boundaryPotentials;
        private int N = 0;

        private void OnDestroy()
        {
            DisposeBuffers();
        }

        private bool EnsureGeometryInitialized()
        {
            if (N > 0 && centroids.IsCreated && surfaceCharges.IsCreated && boundaryPotentials.IsCreated)
                return true;

            MeshFilter mf = GetComponent<MeshFilter>();
            Mesh mesh = (mf != null) ? mf.sharedMesh : null;

            if (mesh == null || mesh.triangles.Length == 0)
            {
                N = 1;
                DisposeBuffers();
                AllocateBuffers(N);
                centroids[0] = new float3(0, 0, 0);
                normals[0] = new float3(0, 1, 0);
                areas[0] = 0.001f;
                return true;
            }

            Vector3[] verts = mesh.vertices;
            int[] tris = mesh.triangles;
            N = tris.Length / 3;

            DisposeBuffers();
            AllocateBuffers(N);

            for (int i = 0; i < N; i++)
            {
                Vector3 v0 = transform.TransformPoint(verts[tris[i * 3]]);
                Vector3 v1 = transform.TransformPoint(verts[tris[i * 3 + 1]]);
                Vector3 v2 = transform.TransformPoint(verts[tris[i * 3 + 2]]);

                Vector3 cross = Vector3.Cross(v1 - v0, v2 - v0);
                centroids[i] = (v0 + v1 + v2) / 3.0f;
                normals[i] = cross.normalized;
                areas[i] = math.max(cross.magnitude * 0.5f, 1e-7f);
            }

            return true;
        }

        private void AllocateBuffers(int count)
        {
            centroids = new NativeArray<float3>(count, Allocator.Persistent);
            normals = new NativeArray<float3>(count, Allocator.Persistent);
            areas = new NativeArray<float>(count, Allocator.Persistent);
            relativePermittivities = new NativeArray<float>(count, Allocator.Persistent);
            surfaceCharges = new NativeArray<float>(count, Allocator.Persistent);
            boundaryPotentials = new NativeArray<float>(count, Allocator.Persistent);
        }

        private string FindJSONFilePath()
        {
            string directPath = Path.Combine(Application.dataPath, "ElectroscopePhysicsCache_v2.json");
            if (File.Exists(directPath)) return directPath;

            string[] searchResults = Directory.GetFiles(Application.dataPath, "ElectroscopePhysicsCache_v2.json", SearchOption.AllDirectories);
            if (searchResults.Length > 0) return searchResults[0];

            string[] fallbackResults = Directory.GetFiles(Application.dataPath, "*.json", SearchOption.AllDirectories);
            foreach (var file in fallbackResults)
            {
                if (file.Contains("Electroscope") || file.Contains("PhysicsCache")) return file;
            }

            return directPath;
        }

        public void LoadJSONCache()
        {
            string cachePath = FindJSONFilePath();
            if (!File.Exists(cachePath))
            {
                Debug.LogError($"[vChargingHyperTuneEngine] ❌ No se encontró el archivo JSON en: {cachePath}");
                return;
            }

            try
            {
                string jsonText = File.ReadAllText(cachePath);
                dbCache = JsonConvert.DeserializeObject<PhysicsDatabaseContainer>(jsonText);

                var materials = dbCache != null ? dbCache.GetMaterials() : null;
                if (materials != null && materials.Count > 0)
                {
                    Debug.Log($"[vChargingHyperTuneEngine] 🟢 Base de datos cargada. Materiales encontrados: {materials.Count}");
                }
                else
                {
                    var directList = JsonConvert.DeserializeObject<List<MaterialPhysicsRaw>>(jsonText);
                    if (directList != null && directList.Count > 0)
                    {
                        dbCache = new PhysicsDatabaseContainer { savedPhysicsMain = directList };
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[vChargingHyperTuneEngine] ❌ Error al parsear JSON: {ex.Message}");
            }
        }

        /// <summary>
        /// ContextMenu para ejecutar la suite completa, formatear el CSV en InvariantCulture y guardar BEM_Winner_Config.json.
        /// </summary>
        [ContextMenu("🚀 Run Full Automated HyperTune Suite & Save Winner Config")]
        public void RunFullAutomatedHyperTuneSuite()
        {
            if (!EnsureGeometryInitialized()) return;
            if (dbCache == null || dbCache.GetMaterials().Count == 0) LoadJSONCache();

            List<MaterialPhysicsRaw> materials = dbCache != null ? dbCache.GetMaterials() : new List<MaterialPhysicsRaw>();
            if (materials.Count == 0)
            {
                Debug.LogError("[vChargingHyperTuneEngine] ❌ La base de datos sigue sin cargar materiales.");
                return;
            }

            string validationFolder = Path.Combine(Application.dataPath, "ChargingValidation");
            if (!Directory.Exists(validationFolder)) Directory.CreateDirectory(validationFolder);

            string csvPath = Path.Combine(validationFolder, "Charging_Hypertune_Validation.csv");
            bool csvExists = File.Exists(csvPath);

            int totalRuns = 0;
            BEMWinnerConfig winnerConfig = null;
            float maxObservedCharge = -1.0f;

            Debug.Log($"[vChargingHyperTuneEngine] 🏁 Iniciando Suite de Hipertuning sobre {materials.Count} materiales...");

            using (StreamWriter writer = new StreamWriter(csvPath, append: true, Encoding.UTF8))
            {
                if (!csvExists)
                {
                    writer.WriteLine("Timestamp,RodMaterial,ElectroscopeMaterial,NormalForce_N,Speed_m_s,Duration_s,FrictionForce_N,TotalWork_J,GeneratedCharge_C,GeneratedPotential_V,MaxChargeDensity_C_m2,PeakElectricField_V_m,IsConductor");
                }

                foreach (var rod in materials)
                {
                    foreach (var elec in materials)
                    {
                        var rodData = MapRawToStruct(rod);
                        var elecData = MapRawToStruct(elec);

                        foreach (float force in testNormalForcesN)
                        {
                            foreach (float speed in testRubbingSpeedsMps)
                            {
                                foreach (float duration in testDurationsSeconds)
                                {
                                    var result = SimulateScenario(rod.materialName, elec.materialName, rodData, elecData, force, speed, duration);
                                    if (result == null) continue;

                                    // Formato numérico en InvariantCulture para garantizar puntos decimales en el CSV
                                    string line = string.Format(
                                        System.Globalization.CultureInfo.InvariantCulture,
                                        "{0},{1},{2},{3:F2},{4:F2},{5:F2},{6:E4},{7:E4},{8:E4},{9:F2},{10:E4},{11:E4},{12}",
                                        result.timestamp,
                                        result.rodMaterial,
                                        result.electroscopeMaterial,
                                        result.normalForceN,
                                        result.rubbingSpeedMps,
                                        result.durationSeconds,
                                        result.frictionForceN,
                                        result.totalWorkJoule,
                                        result.generatedChargeC,
                                        result.generatedPotentialV,
                                        result.maxChargeDensityC_m2,
                                        result.peakElectricFieldV_m,
                                        result.isConductor
                                    );
                                    writer.WriteLine(line);

                                    // Rastrear el escenario ganador según la mayor magnitud de carga triboeléctrica generada |Q|
                                    float absCharge = math.abs(result.generatedChargeC);
                                    if (absCharge > maxObservedCharge)
                                    {
                                        maxObservedCharge = absCharge;
                                        winnerConfig = new BEMWinnerConfig
                                        {
                                            timestamp = result.timestamp,
                                            winnerRodMaterial = rod.materialName,
                                            winnerElectroscopeMaterial = elec.materialName,
                                            rodPhysics = rodData,
                                            electroscopePhysics = elecData,
                                            normalForceN = force,
                                            rubbingSpeedMps = speed,
                                            durationSeconds = duration,
                                            frictionForceN = result.frictionForceN,
                                            totalWorkJoule = result.totalWorkJoule,
                                            generatedChargeC = result.generatedChargeC,
                                            generatedPotentialV = result.generatedPotentialV,
                                            maxChargeDensityC_m2 = result.maxChargeDensityC_m2,
                                            peakElectricFieldV_m = result.peakElectricFieldV_m,
                                            isConductor = result.isConductor,
                                            bemIterationsUsed = result.bemIterationsUsed,
                                            bemSolverTolerance = 1e-4f
                                        };
                                    }

                                    totalRuns++;
                                }
                            }
                        }
                    }
                }
            }

            // Exportar la configuración JSON del Ganador BEM
            if (winnerConfig != null)
            {
                string winnerJsonPath = Path.Combine(validationFolder, "BEM_Winner_Config.json");
                string jsonOutput = JsonConvert.SerializeObject(winnerConfig, Formatting.Indented);
                File.WriteAllText(winnerJsonPath, jsonOutput);

                Debug.Log($"[vChargingHyperTuneEngine] 🏆 ESCENARIO GANADOR BEM: Barra '{winnerConfig.winnerRodMaterial}' vs Electroscopio '{winnerConfig.winnerElectroscopeMaterial}' | Carga Máxima: {winnerConfig.generatedChargeC:E4} C | Potencial: {winnerConfig.generatedPotentialV:F2} V");
                Debug.Log($"[vChargingHyperTuneEngine] 📄 Configuración del Ganador guardada en: {winnerJsonPath}");
            }

            Debug.Log($"[vChargingHyperTuneEngine] ✅ ¡Suite Completada! {totalRuns} simulaciones BEM procesadas. CSV de validación actualizado.");
        }

        private ScenarioRunResult SimulateScenario(string rodName, string elecName, MaterialPhysicsData rod, MaterialPhysicsData elec, float forceN, float speedMps, float durationS)
        {
            if (!EnsureGeometryInitialized()) return null;

            const float eps0 = 8.854187817e-12f;

            ScenarioRunResult res = new ScenarioRunResult();
            res.timestamp = DateTime.UtcNow.ToString("o");
            res.rodMaterial = rodName;
            res.electroscopeMaterial = elecName;
            res.normalForceN = forceN;
            res.rubbingSpeedMps = speedMps;
            res.durationSeconds = durationS;
            res.isConductor = elec.electricalConductivity >= 1.0f;

            // Fricción y Trabajo Mecánico
            float effectiveFrictionCoeff = (rod.kineticFriction + elec.kineticFriction) * 0.5f;
            res.frictionForceN = effectiveFrictionCoeff * forceN;
            res.totalWorkJoule = res.frictionForceN * speedMps * durationS;

            // Carga Triboeléctrica
            float effectiveArea = 1e-4f * rod.surfaceSmoothness * elec.surfaceSmoothness;
            float deltaAffinity = rod.chargeAffinity - elec.chargeAffinity;
            res.generatedChargeC = deltaAffinity * effectiveArea * forceN * speedMps * durationS;

            float perm = math.max(elec.relativePermittivity, 1.0f);
            res.generatedPotentialV = res.generatedChargeC / (eps0 * perm);

            for (int i = 0; i < N; i++)
            {
                boundaryPotentials[i] = res.generatedPotentialV;
                relativePermittivities[i] = res.isConductor ? 1.0f : perm;
                surfaceCharges[i] = 0.0f;
            }

            NativeArray<float> greenMatrix = new NativeArray<float>(N * N, Allocator.TempJob);

            try
            {
                BuildGreenMatrixJob buildJob = new BuildGreenMatrixJob
                {
                    centroids = centroids,
                    areas = areas,
                    relativePermittivities = relativePermittivities,
                    greenMatrix = greenMatrix,
                    N = N
                };
                JobHandle buildHandle = buildJob.Schedule(N * N, 64);

                int iterations = res.isConductor ? 20 : 35;
                BiCGSTABSolverJob solveJob = new BiCGSTABSolverJob
                {
                    A = greenMatrix,
                    b = boundaryPotentials,
                    x = surfaceCharges,
                    N = N,
                    maxIterations = iterations,
                    tolerance = 1e-4f
                };
                JobHandle solveHandle = solveJob.Schedule(buildHandle);
                solveHandle.Complete();

                res.bemIterationsUsed = iterations;
            }
            finally
            {
                if (greenMatrix.IsCreated) greenMatrix.Dispose();
            }

            float totalQ = 0.0f;
            float maxQDensity = 0.0f;
            for (int i = 0; i < N; i++)
            {
                float q = surfaceCharges[i];
                totalQ += q * areas[i];
                maxQDensity = math.max(maxQDensity, math.abs(q));
            }

            res.maxChargeDensityC_m2 = maxQDensity;
            res.peakElectricFieldV_m = maxQDensity / eps0;

            return res;
        }

        private MaterialPhysicsData MapRawToStruct(MaterialPhysicsRaw raw)
        {
            MaterialPhysicsData data = new MaterialPhysicsData();
            data.density = GetProp(raw, "Density");
            data.surfaceSmoothness = GetProp(raw, "Surface smoothness");
            data.transparency = GetProp(raw, "Transparency");
            data.electricalConductivity = GetProp(raw, "Electrical conductivity");
            data.chargeAffinity = GetProp(raw, "Triboelectric charge affinity");
            data.workFunction = GetProp(raw, "Work function");
            data.relativePermittivity = GetProp(raw, "Relative permittivity");
            data.waterAbsorptionRate = GetProp(raw, "Water absorption rate");
            data.kineticFriction = GetProp(raw, "Kinetic friction coefficient");
            data.dielectricStrength = GetProp(raw, "Dielectric strength");
            data.meltingPointKelvin = GetProp(raw, "Melting Point");
            data.rgbColor24Bit = (uint)GetProp(raw, "Hex color code");
            return data;
        }

        private float GetProp(MaterialPhysicsRaw raw, string name)
        {
            if (raw.properties == null) return 0.0f;
            var prop = raw.properties.Find(p => string.Equals(p.propertyName, name, StringComparison.OrdinalIgnoreCase));
            return prop != null ? prop.value : 0.0f;
        }

        private void DisposeBuffers()
        {
            if (centroids.IsCreated) centroids.Dispose();
            if (normals.IsCreated) normals.Dispose();
            if (areas.IsCreated) areas.Dispose();
            if (relativePermittivities.IsCreated) relativePermittivities.Dispose();
            if (surfaceCharges.IsCreated) surfaceCharges.Dispose();
            if (boundaryPotentials.IsCreated) boundaryPotentials.Dispose();
            N = 0;
        }
    }

    public class ScenarioRunResult
    {
        public string timestamp;
        public string rodMaterial;
        public string electroscopeMaterial;
        public float normalForceN;
        public float rubbingSpeedMps;
        public float durationSeconds;
        public float frictionForceN;
        public float totalWorkJoule;
        public float generatedChargeC;
        public float generatedPotentialV;
        public float maxChargeDensityC_m2;
        public float peakElectricFieldV_m;
        public bool isConductor;
        public int bemIterationsUsed;
    }
}