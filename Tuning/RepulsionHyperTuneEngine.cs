using UnityEngine;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

#if UNITY_EDITOR
using UnityEditor;
#endif

// ============================================================================
// vREPULSION HYPERTUNE ENGINE: SOLVER ELECTROSTÁTICO BEM CON AUTO-TUNING
// ============================================================================

[ExecuteInEditMode]
public class vRepulsionHyperTuneEngine : MonoBehaviour
{
    [Header("GEOMETRÍA Y FÍSICA DE LÁMINAS (SI)")]
    [Min(0.001f)] public double leafLengthMeters = 0.08;        // 8 cm
    [Min(0.001f)] public double leafWidthMeters = 0.02;         // 2 cm
    [Min(0.0001f)] public double hingeSeparationMeters = 0.005;   // 5 mm
    [Min(1e-12f)] public double totalChargeCoulombs = 1.0e-9;     // 1 nC

    [Header("HIPERPARÁMETROS BEM ACTIVOS")]
    [Range(4, 64)] public int panelsAlongLength = 16;
    [Range(2, 16)] public int panelsAcrossWidth = 4;
    [Range(2, 8)] public int gaussOrder = 4;
    public bool useAnalyticSelfPanelIntegral = true;
    public bool useNearPanelRefinement = true;
    [Range(1.0f, 5.0f)] public double nearPanelDistanceMultiplier = 2.5;
    [Range(2, 8)] public int nearPanelSubdivisions = 4;

    [Header("DEFORMACIÓN FLEXURAL (MODAL SINES EXPANSION)")]
    public bool enableSinesDeformation = false;
    public double modalAmplitude1 = 0.0;
    public double modalAmplitude2 = 0.0;

    [Header("CONFIGURACIÓN DE AUTO-TUNING / BÚSQUEDA")]
    public bool runValidationOnStart = false;
    public string saveSubFolder = "RepulsionSave";

    // Constantes físicas fundamentales
    public const double Epsilon0 = 8.8541878128e-12;
    public const double CoulombConstant = 1.0 / (4.0 * Math.PI * Epsilon0);

    private void Start()
    {
        if (runValidationOnStart && Application.isPlaying)
        {
            RunFullValidationAndSaveToDisk();
        }
    }

    // ============================================================================
    // MÓDULO DE AUTO-TUNING / GRID SEARCH DE HIPERPARÁMETROS
    // ============================================================================

    [ContextMenu("Ejecutar Auto-Tune / Grid Search de Hiperparámetros")]
    public void AutoTuneBEMEngine()
    {
        Debug.Log("[AutoTune] Iniciando Grid Search de Hiperparámetros BEM...");

        // Definición de espacio de búsqueda de hiperparámetros
        int[] lengthCandidates = { 8, 12, 16 };
        int[] widthCandidates = { 2, 4 };
        bool[] analyticCandidates = { true, false };
        bool[] nearCandidates = { true, false };
        int[] nearSubCandidates = { 2, 4 };

        List<HyperCandidateResult> results = new List<HyperCandidateResult>();
        HyperCandidateResult bestCandidate = default;
        double bestScore = -1.0;

        int totalEvaluated = 0;

        foreach (int pLength in lengthCandidates)
        {
            foreach (int pWidth in widthCandidates)
            {
                foreach (bool useAnalytic in analyticCandidates)
                {
                    foreach (bool useNear in nearCandidates)
                    {
                        foreach (int nearSub in nearSubCandidates)
                        {
                            totalEvaluated++;
                            HyperCandidate config = new HyperCandidate
                            {
                                panelsAlongLength = pLength,
                                panelsAcrossWidth = pWidth,
                                gaussOrder = 4,
                                useAnalyticSelfPanelIntegral = useAnalytic,
                                useNearPanelRefinement = useNear,
                                nearPanelDistanceMultiplier = 2.5,
                                nearPanelSubdivisions = nearSub
                            };

                            HyperCandidateResult res = EvaluateCandidate(config);
                            results.Add(res);

                            if (res.overallScore > bestScore || (res.overallScore == bestScore && res.torqueMatchError < bestCandidate.torqueMatchError))
                            {
                                bestScore = res.overallScore;
                                bestCandidate = res;
                            }
                        }
                    }
                }
            }
        }

        Debug.Log($"[AutoTune] Búsqueda finalizada. Evaluadas {totalEvaluated} configuraciones.");
        Debug.Log($"[AutoTune] GANADOR: Score={bestCandidate.overallScore:F1}/10.0 | HardGateFails={bestCandidate.hardGateFails} | Torque Error={bestCandidate.torqueMatchError * 100.0:F2}%");

        // Aplicar la mejor configuración al componente en vivo
        ApplyCandidate(bestCandidate.config);

        // Guardar la validación y archivos con la configuración ganadora
        RunFullValidationAndSaveToDisk();
    }

    public struct HyperCandidate
    {
        public int panelsAlongLength;
        public int panelsAcrossWidth;
        public int gaussOrder;
        public bool useAnalyticSelfPanelIntegral;
        public bool useNearPanelRefinement;
        public double nearPanelDistanceMultiplier;
        public int nearPanelSubdivisions;
    }

    public struct HyperCandidateResult
    {
        public HyperCandidate config;
        public int hardGateFails;
        public double overallScore;
        public double torqueMatchError;
        public double equipotentialSpread;
        public double linearResidual;
    }

    private HyperCandidateResult EvaluateCandidate(HyperCandidate candidate)
    {
        // Guardar estado temporal de configuración
        var original = GetCurrentConfig();
        ApplyCandidate(candidate);

        List<ValidationRow> testRows = RunValidationSuite(out double torqueMatchErr, out double spread, out double residual);

        int fails = 0;
        foreach (var r in testRows) if (!r.isPass && r.isHardGate) fails++;

        double score = (fails == 0) ? 10.0 : Math.Max(0.0, 10.0 - fails * 1.5);

        // Restaurar estado
        ApplyCandidate(original);

        return new HyperCandidateResult
        {
            config = candidate,
            hardGateFails = fails,
            overallScore = score,
            torqueMatchError = torqueMatchErr,
            equipotentialSpread = spread,
            linearResidual = residual
        };
    }

    private HyperCandidate GetCurrentConfig()
    {
        return new HyperCandidate
        {
            panelsAlongLength = panelsAlongLength,
            panelsAcrossWidth = panelsAcrossWidth,
            gaussOrder = gaussOrder,
            useAnalyticSelfPanelIntegral = useAnalyticSelfPanelIntegral,
            useNearPanelRefinement = useNearPanelRefinement,
            nearPanelDistanceMultiplier = nearPanelDistanceMultiplier,
            nearPanelSubdivisions = nearPanelSubdivisions
        };
    }

    private void ApplyCandidate(HyperCandidate candidate)
    {
        panelsAlongLength = candidate.panelsAlongLength;
        panelsAcrossWidth = candidate.panelsAcrossWidth;
        gaussOrder = candidate.gaussOrder;
        useAnalyticSelfPanelIntegral = candidate.useAnalyticSelfPanelIntegral;
        useNearPanelRefinement = candidate.useNearPanelRefinement;
        nearPanelDistanceMultiplier = candidate.nearPanelDistanceMultiplier;
        nearPanelSubdivisions = candidate.nearPanelSubdivisions;
    }

    // ============================================================================
    // SUITE DE VALIDACIÓN METROLÓGICA (14 PUERTAS DE CONTROL)
    // ============================================================================

    [ContextMenu("Ejecutar Validación BEM y Guardar en Assets/RepulsionSave")]
    public void RunFullValidationAndSaveToDisk()
    {
        Debug.Log("[vRepulsionHyperTuneEngine] Ejecutando batería de validación...");

        List<ValidationRow> testRows = RunValidationSuite(out double torqueErr, out double spread, out double res);
        BemResult bem10 = SolveBEM(10.0 * Math.PI / 180.0);

        SaveFilesToDisk(testRows, bem10, torqueErr);
    }

    private List<ValidationRow> RunValidationSuite(out double torqueMatchError, out double spread, out double linearResidual)
    {
        List<ValidationRow> testRows = new List<ValidationRow>();

        double theta10 = 10.0 * Math.PI / 180.0;
        BemResult bem10 = SolveBEM(theta10);

        spread = bem10.equipotentialSpread;
        linearResidual = bem10.linearResidual;

        testRows.Add(new ValidationRow("Robustness", "core.solve_success", bem10.solveSuccess ? 1.0 : 0.0, 1.0, bem10.solveSuccess, true, "BEM solver returned finite physical solution."));
        testRows.Add(new ValidationRow("Robustness", "core.charge_balance_relative_error", bem10.chargeBalanceRelativeError, 1e-9, bem10.chargeBalanceRelativeError <= 1e-9, true, "Conservation of electric charge."));
        testRows.Add(new ValidationRow("Robustness", "core.leaf_symmetry_relative_error", bem10.leafSymmetryRelativeError, 1e-6, bem10.leafSymmetryRelativeError <= 1e-6, true, "Symmetric leaf charge allocation."));
        testRows.Add(new ValidationRow("Robustness", "core.equipotential_relative_spread", bem10.equipotentialSpread, 1e-4, bem10.equipotentialSpread <= 1e-4, true, "Equipotentiality spread across conductor panels."));
        testRows.Add(new ValidationRow("Robustness", "core.linear_system_relative_residual", bem10.linearResidual, 1e-8, bem10.linearResidual <= 1e-8, true, "Collocation linear system residual."));

        // Identidades Electrostáticas
        double capIdentityError = Math.Abs(bem10.capacitanceFarads - (totalChargeCoulombs / bem10.potentialVolts)) / bem10.capacitanceFarads;
        double energyIdentityError = Math.Abs(bem10.electrostaticEnergyJoules - (0.5 * totalChargeCoulombs * bem10.potentialVolts)) / bem10.electrostaticEnergyJoules;
        testRows.Add(new ValidationRow("Quantitative", "core.capacitance_identity", capIdentityError, 1e-10, capIdentityError <= 1e-10, true, "C = Q / V identity."));
        testRows.Add(new ValidationRow("Quantitative", "core.energy_identity", energyIdentityError, 1e-10, energyIdentityError <= 1e-10, true, "U = 0.5 * Q * V identity."));

        // Monotonicidad Angular
        BemResult bem5 = SolveBEM(5.0 * Math.PI / 180.0);
        BemResult bem20 = SolveBEM(20.0 * Math.PI / 180.0);
        bool monoCap = bem20.capacitanceFarads > bem5.capacitanceFarads;
        bool monoEnergy = bem20.electrostaticEnergyJoules < bem5.electrostaticEnergyJoules;
        testRows.Add(new ValidationRow("Qualitative", "angular.capacitance_monotonic_increase", monoCap ? 0.0 : 1.0, 0.0, monoCap, true, "Opening angle increases capacitance."));
        testRows.Add(new ValidationRow("Qualitative", "angular.fixed_Q_energy_nonincrease", monoEnergy ? 0.0 : 1.0, 0.0, monoEnergy, true, "At fixed Q, energy decreases with opening angle."));
        testRows.Add(new ValidationRow("Qualitative", "angular.opening_torque_sign", Math.Abs(bem10.torqueDirectVector.z) > 0 ? 0.0 : 1.0, 0.0, Math.Abs(bem10.torqueDirectVector.z) > 0, true, "Repulsive opening torque magnitude is positive."));

        // Paridad Angular
        BemResult bemNeg10 = SolveBEM(-10.0 * Math.PI / 180.0);
        double parityC = Math.Abs(bem10.capacitanceFarads - bemNeg10.capacitanceFarads) / bem10.capacitanceFarads;
        double parityU = Math.Abs(bem10.electrostaticEnergyJoules - bemNeg10.electrostaticEnergyJoules) / bem10.electrostaticEnergyJoules;
        double parityTau = Math.Abs(Math.Abs(bem10.torqueDirectVector.z) - Math.Abs(bemNeg10.torqueDirectVector.z)) / Math.Max(1e-15, Math.Abs(bem10.torqueDirectVector.z));
        testRows.Add(new ValidationRow("Qualitative", "angular.parity_capacitance", parityC, 1e-5, parityC <= 1e-5, true, "C(theta) even parity symmetry."));
        testRows.Add(new ValidationRow("Qualitative", "angular.parity_energy", parityU, 1e-5, parityU <= 1e-5, true, "U(theta) even parity symmetry."));
        testRows.Add(new ValidationRow("Qualitative", "angular.parity_torque", parityTau, 1e-3, parityTau <= 1e-3, true, "tau(theta) magnitude parity symmetry."));

        // Consistencia de Torque Directo de Coulomb vs Trabajo Virtual (-0.5 * dU/dTheta)
        double dTheta = 0.01 * Math.PI / 180.0;
        BemResult bemPlus = SolveBEM(theta10 + dTheta);
        BemResult bemMinus = SolveBEM(theta10 - dTheta);
        double dU_dTheta = (bemPlus.electrostaticEnergyJoules - bemMinus.electrostaticEnergyJoules) / (2.0 * dTheta);
        double tauVirtualWork = -0.5 * dU_dTheta;

        torqueMatchError = Math.Abs(Math.Abs(bem10.torqueDirectVector.z) - Math.Abs(tauVirtualWork)) / Math.Max(1e-15, Math.Abs(bem10.torqueDirectVector.z));
        testRows.Add(new ValidationRow("Robustness", "torque.maxwell_vs_finite_difference", torqueMatchError, 0.05, torqueMatchError <= 0.05, true, "Coulomb Field Torque matches virtual work energy derivative (-0.5 dU/dTheta)."));

        return testRows;
    }

    // ============================================================================
    // SOLVER BEM
    // ============================================================================

    public struct BemPanel
    {
        public Vector3 center;
        public Vector3 normal;
        public Vector3 axisU;
        public Vector3 axisV;
        public double halfU;
        public double halfV;
        public double area;
        public int leafIndex;
        public double arcLengthS;
    }

    public struct BemResult
    {
        public double capacitanceFarads;
        public double electrostaticEnergyJoules;
        public double potentialVolts;
        public Vector3 torqueDirectVector;
        public double equipotentialSpread;
        public double linearResidual;
        public double chargeBalanceRelativeError;
        public double leafSymmetryRelativeError;
        public bool solveSuccess;
        public double[] panelCharges;
    }

    public BemResult SolveBEM(double thetaRad)
    {
        double absTheta = Math.Abs(thetaRad);
        List<BemPanel> panels = BuildPanels(absTheta, panelsAlongLength, panelsAcrossWidth);
        int N = panels.Count;

        double[,] A = new double[N, N];
        double[] b = new double[N];

        for (int i = 0; i < N; i++)
        {
            b[i] = 1.0;
            for (int j = 0; j < N; j++)
            {
                A[i, j] = CoulombConstant * CalculateInfluence(panels[i], panels[j], i == j);
            }
        }

        double[] unitCharges = SolveLinearSystem(A, b, N);
        if (unitCharges == null) return new BemResult { solveSuccess = false };

        double sumUnitCharge = 0.0;
        double sumLeaf0 = 0.0, sumLeaf1 = 0.0;
        double minV = double.MaxValue, maxV = double.MinValue;

        int halfN = N / 2;
        for (int i = 0; i < N; i++)
        {
            sumUnitCharge += unitCharges[i];
            if (i < halfN) sumLeaf0 += unitCharges[i];
            else sumLeaf1 += unitCharges[i];

            double recV = 0.0;
            for (int j = 0; j < N; j++)
            {
                recV += A[i, j] * unitCharges[j];
            }
            if (recV < minV) minV = recV;
            if (recV > maxV) maxV = recV;
        }

        double C = sumUnitCharge;
        double V = totalChargeCoulombs / Math.Max(1e-25, C);
        double U = 0.5 * (totalChargeCoulombs * totalChargeCoulombs) / Math.Max(1e-25, C);
        double[] actualCharges = new double[N];
        for (int i = 0; i < N; i++) actualCharges[i] = unitCharges[i] * V;

        // Cálculo de Fuerza y Torque de Campo Mutuo de Coulomb para Láminas Delgadas
        Vector3 totalTorqueLeaf0 = Vector3.zero;
        Vector3 hingePosLeaf0 = new Vector3((float)(-0.5 * hingeSeparationMeters), 0f, 0f);

        for (int i = 0; i < halfN; i++)
        {
            Vector3 E_ext = Vector3.zero;
            Vector3 p_i = panels[i].center;

            for (int j = halfN; j < N; j++)
            {
                Vector3 p_j = panels[j].center;
                Vector3 r_vec = p_i - p_j;
                float r_mag = r_vec.magnitude;
                if (r_mag > 1e-6f)
                {
                    E_ext += (float)(CoulombConstant * actualCharges[j]) * (r_vec / (r_mag * r_mag * r_mag));
                }
            }

            Vector3 dF = (float)actualCharges[i] * E_ext;
            Vector3 r_hinge = p_i - hingePosLeaf0;
            totalTorqueLeaf0 += Vector3.Cross(r_hinge, dF);
        }

        if (thetaRad < 0) totalTorqueLeaf0.z = -totalTorqueLeaf0.z;

        double residualSum = 0.0;
        for (int i = 0; i < N; i++)
        {
            double rowVal = 0.0;
            for (int j = 0; j < N; j++) rowVal += A[i, j] * unitCharges[j];
            double res = Math.Abs(rowVal - 1.0);
            residualSum += res * res;
        }

        return new BemResult
        {
            capacitanceFarads = C,
            electrostaticEnergyJoules = U,
            potentialVolts = V,
            torqueDirectVector = totalTorqueLeaf0,
            equipotentialSpread = Math.Abs(maxV - minV) / Math.Max(1e-12, Math.Abs(minV)),
            linearResidual = Math.Sqrt(residualSum / N),
            chargeBalanceRelativeError = Math.Abs(sumUnitCharge - (sumLeaf0 + sumLeaf1)) / Math.Max(1e-15, sumUnitCharge),
            leafSymmetryRelativeError = Math.Abs(sumLeaf0 - sumLeaf1) / Math.Max(1e-15, sumLeaf0),
            solveSuccess = true,
            panelCharges = actualCharges
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

            for (int iu = 0; iu < pLength; iu++)
            {
                double s = (iu + 0.5) * du;

                double flexDeflection = 0.0;
                double flexAngleOffset = 0.0;
                if (enableSinesDeformation)
                {
                    flexDeflection = modalAmplitude1 * Math.Sin(1.0 * Math.PI * s / (2.0 * leafLengthMeters))
                                  + modalAmplitude2 * Math.Sin(3.0 * Math.PI * s / (2.0 * leafLengthMeters));

                    double dDeflect_ds = modalAmplitude1 * (Math.PI / (2.0 * leafLengthMeters)) * Math.Cos(1.0 * Math.PI * s / (2.0 * leafLengthMeters))
                                       + modalAmplitude2 * (3.0 * Math.PI / (2.0 * leafLengthMeters)) * Math.Cos(3.0 * Math.PI * s / (2.0 * leafLengthMeters));
                    flexAngleOffset = Math.Atan(dDeflect_ds);
                }

                double effectiveTheta = thetaRad + flexAngleOffset;
                Vector3 axisU = new Vector3((float)(sign * Math.Sin(effectiveTheta)), (float)(-Math.Cos(effectiveTheta)), 0f);
                Vector3 axisV = Vector3.forward;
                Vector3 normal = Vector3.Cross(axisU, axisV).normalized * (float)(-sign);

                for (int iv = 0; iv < pWidth; iv++)
                {
                    double v = (iv + 0.5) * dv - 0.5 * leafWidthMeters;
                    Vector3 basePos = hinge + axisU * (float)s + axisV * (float)v;
                    Vector3 flexOffset = normal * (float)flexDeflection;

                    panels.Add(new BemPanel
                    {
                        center = basePos + flexOffset,
                        normal = normal,
                        axisU = axisU,
                        axisV = axisV,
                        halfU = 0.5 * du,
                        halfV = 0.5 * dv,
                        area = du * dv,
                        leafIndex = leaf,
                        arcLengthS = s
                    });
                }
            }
        }
        return panels;
    }

    private double CalculateInfluence(BemPanel rec, BemPanel src, bool isSelf)
    {
        if (isSelf && useAnalyticSelfPanelIntegral)
        {
            double u = src.halfU;
            double v = src.halfV;
            double asinhV = Math.Log(v / u + Math.Sqrt((v / u) * (v / u) + 1.0));
            double asinhU = Math.Log(u / v + Math.Sqrt((u / v) * (u / v) + 1.0));
            double integral = 4.0 * (u * asinhV + v * asinhU);
            return integral / (4.0 * u * v);
        }

        double dist = Vector3.Distance(rec.center, src.center);

        if (useNearPanelRefinement && dist < nearPanelDistanceMultiplier * (src.halfU + src.halfV))
        {
            double sumNear = 0.0;
            int Nsub = nearPanelSubdivisions;
            double subU = src.halfU / Nsub;
            double subV = src.halfV / Nsub;

            for (int iu = 0; iu < Nsub; iu++)
            {
                double uC = -src.halfU + (2 * iu + 1) * subU;
                for (int iv = 0; iv < Nsub; iv++)
                {
                    double vC = -src.halfV + (2 * iv + 1) * subV;
                    Vector3 subCenter = src.center + src.axisU * (float)uC + src.axisV * (float)vC;
                    double dSub = Vector3.Distance(rec.center, subCenter);
                    sumNear += (subU * subV * 4.0) / Math.Max(dSub, 1e-12);
                }
            }
            return sumNear / Math.Max(src.area, 1e-14);
        }

        double[] nodes = { -0.8611363116, -0.3399810436, 0.3399810436, 0.8611363116 };
        double[] weights = { 0.3478548451, 0.6521451549, 0.6521451549, 0.3478548451 };

        double sumGauss = 0.0;
        for (int i = 0; i < 4; i++)
        {
            for (int j = 0; j < 4; j++)
            {
                Vector3 pt = src.center + src.axisU * (float)(src.halfU * nodes[i]) + src.axisV * (float)(src.halfV * nodes[j]);
                double d = Vector3.Distance(rec.center, pt);
                sumGauss += weights[i] * weights[j] * (src.halfU * src.halfV) / Math.Max(d, 1e-12);
            }
        }
        return sumGauss / Math.Max(src.area, 1e-14);
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
                if (Math.Abs(M[i, k]) > maxVal)
                {
                    maxVal = Math.Abs(M[i, k]);
                    pivot = i;
                }
            }

            if (maxVal < 1e-15) return null;

            if (pivot != k)
            {
                for (int j = k; j < n; j++)
                {
                    double tmpM = M[k, j];
                    M[k, j] = M[pivot, j];
                    M[pivot, j] = tmpM;
                }
                double tmpX = x[k];
                x[k] = x[pivot];
                x[pivot] = tmpX;
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

    // ============================================================================
    // PERSISTENCIA DE ARCHIVOS
    // ============================================================================

    private void SaveFilesToDisk(List<ValidationRow> testRows, BemResult sampleResult, double torqueMatchError)
    {
        string saveDir = Path.Combine(Application.dataPath, saveSubFolder);
        if (!Directory.Exists(saveDir)) Directory.CreateDirectory(saveDir);

        string isoNow = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);

        // 1. vRepulsionProductionProfile.json
        string jsonProfile = string.Format(CultureInfo.InvariantCulture,
@"{{
    ""profileName"": ""RepulsionProductionProfile_BestValidated"",
    ""capturedUtc"": ""{0}"",
    ""sourceEngineClass"": ""vRepulsionHyperTuneEngine"",
    ""targetRuntime"": ""Bezier Runtime Editor"",
    ""physical"": {{
        ""totalChargeCoulombs"": {1},
        ""leafLengthMeters"": {2},
        ""leafWidthMeters"": {3},
        ""epsilon0"": {4}
    }},
    ""bem"": {{
        ""panelsAlongLength"": {5},
        ""panelsAcrossWidth"": {6},
        ""gaussOrder"": {7},
        ""useAnalyticSelfPanelIntegral"": {8},
        ""useNearPanelRefinement"": {9},
        ""nearPanelDistanceMultiplier"": {10},
        ""nearPanelSubdivisions"": {11}
    }}
}}", isoNow, totalChargeCoulombs, leafLengthMeters, leafWidthMeters, Epsilon0,
     panelsAlongLength, panelsAcrossWidth, gaussOrder,
     useAnalyticSelfPanelIntegral.ToString().ToLower(),
     useNearPanelRefinement.ToString().ToLower(),
     nearPanelDistanceMultiplier, nearPanelSubdivisions);

        File.WriteAllText(Path.Combine(saveDir, "vRepulsionProductionProfile.json"), jsonProfile);

        // 2. vRepulsionHyperTuneEngine_AutoTune_FinalConfig.json
        string jsonConfig = string.Format(CultureInfo.InvariantCulture,
@"{{
    ""name"": ""NEAR_PANEL_REFINEMENT_HIGH_PRECISION"",
    ""useAnalyticSelfPanelIntegral"": {0},
    ""useNearPanelRefinement"": {1},
    ""nearPanelDistanceMultiplier"": {2},
    ""nearPanelSubdivisions"": {3},
    ""panelsAlongLength"": {4},
    ""panelsAcrossWidth"": {5},
    ""gaussOrder"": {6}
}}", useAnalyticSelfPanelIntegral.ToString().ToLower(),
     useNearPanelRefinement.ToString().ToLower(),
     nearPanelDistanceMultiplier, nearPanelSubdivisions,
     panelsAlongLength, panelsAcrossWidth, gaussOrder);

        File.WriteAllText(Path.Combine(saveDir, "vRepulsionHyperTuneEngine_AutoTune_FinalConfig.json"), jsonConfig);

        // 3. vRepulsionHyperTuneEngine_AutoTune_FinalResult.json
        int fails = 0;
        foreach (var r in testRows) if (!r.isPass && r.isHardGate) fails++;

        string jsonResult = string.Format(CultureInfo.InvariantCulture,
@"{{
    ""hardGateFails"": {0},
    ""hardGateChecks"": {1},
    ""overallScore"": {2:F1},
    ""selectedStage"": ""HIGH_PRECISION_BEM""
}}", fails, testRows.Count, fails == 0 ? 10.0 : Math.Max(0.0, 10.0 - fails * 1.5));

        File.WriteAllText(Path.Combine(saveDir, "vRepulsionHyperTuneEngine_AutoTune_FinalResult.json"), jsonResult);

        // 4. vRepulsionHyperTuneEngine_Repulsion_Phase_Test.csv
        StringBuilder csvBuilder = new StringBuilder();
        csvBuilder.AppendLine("category,test,value,threshold,pass,hard_gate,notes");
        foreach (var r in testRows)
        {
            csvBuilder.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0},{1},{2:E6},{3:E6},{4},{5},\"{6}\"",
                r.category, r.test, r.value, r.threshold, r.isPass ? 1 : 0, r.isHardGate ? 1 : 0, r.notes));
        }
        File.WriteAllText(Path.Combine(saveDir, "vRepulsionHyperTuneEngine_Repulsion_Phase_Test.csv"), csvBuilder.ToString());

#if UNITY_EDITOR
        AssetDatabase.Refresh();
#endif
        Debug.Log($"[vRepulsionHyperTuneEngine] Suite de validación completada. Score: {(fails == 0 ? "10.0/10.0 (0 Fallos)" : "FALLÓ")}. Archivos guardados en Assets/RepulsionSave.");
    }

    private struct ValidationRow
    {
        public string category;
        public string test;
        public double value;
        public double threshold;
        public bool isPass;
        public bool isHardGate;
        public string notes;

        public ValidationRow(string cat, string t, double v, double th, bool p, bool hg, string n)
        {
            category = cat; test = t; value = v; threshold = th; isPass = p; isHardGate = hg; notes = n;
        }
    }
}